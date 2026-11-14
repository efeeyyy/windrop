using System.Security.Cryptography;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;

namespace WinDrop.Core.Session;

/// <summary>Cihaz kimliği = açık anahtarın SHA-256'sının ilk 16 baytı (PROTOCOL §0).</summary>
public static class DeviceId
{
    public const int Length = 16;

    public static byte[] FromPublicKey(ReadOnlySpan<byte> publicKey) => SHA256.HashData(publicKey)[..Length];

    /// <summary>Küçük harf onaltılık (32 karakter); dosya yollarında ve eş anahtarında kullanılır.</summary>
    public static string ToHex(ReadOnlySpan<byte> deviceId) => Convert.ToHexStringLower(deviceId);

    public static string HexFromPublicKey(ReadOnlySpan<byte> publicKey) => ToHex(FromPublicKey(publicKey));
}

/// <summary>Güvenilen listede olmayan bir başlatan (IK) — bağlantı kesilir (PROTOCOL §1).</summary>
public sealed class PeerNotTrustedException(byte[] remotePublicKey)
    : Exception("Karşı cihaz güvenilen listede değil.")
{
    public byte[] RemotePublicKey { get; } = remotePublicKey;
}

/// <summary>Kanalın kurulduğu Noise deseni (PROTOCOL §1, §2, §2b).</summary>
public enum HandshakeKind { IK, XX, XXpsk3 }

/// <summary>Kanaldan gelen tek mesaj: ya §5 çerçevesi ya §4 sinyali.</summary>
public sealed record SecureMessage(Frame? Frame, SignalMessage? Signal);

/// <summary>
/// Kurulmuş Noise oturumu üstünde çerçeve/sinyal alışverişi. Ön ek + el sıkışma statik fabrikalarla yapılır
/// (<see cref="ConnectIkAsync"/>, <see cref="AcceptIkAsync"/>, eşleştirme için XXpsk3 karşılıkları).
/// <para><b>Kurulmuş sayma kuralı (PROTOCOL §1):</b> IK'de el sıkışmayı yanıtlayan bitirir (msg2), yani başlatanın
/// ilk mesajı yeniden oynatılabilir. Bu yüzden yanıtlayan, başlatandan ilk <i>geçerli taşıma mesajı</i> gelene kadar
/// <see cref="IsEstablished"/> = false kalır ve veri gönderemez (<see cref="SendFrameAsync"/> istisna atar);
/// <see cref="EnsureEstablishedAsync"/> bu mesajı bekler. Başlatan, IK'de el sıkışmadan hemen sonra otomatik
/// olarak <b>boş düz metinli</b> bir taşıma mesajı (yalnız etiket) yollar; kanal bunu yutar, çerçeve olarak göstermez.
/// XXpsk3'te el sıkışmayı başlatan bitirir (msg3 psk ile kimlik doğrulamalı, taze), yanıtlayan hemen kurulmuş sayılır.</para>
/// <para>Eşzamanlılık: bir okuyucu + bir/birden çok yazıcı güvenlidir (yazmalar sıralanır, nonce sırası = tel sırası).</para>
/// </summary>
public sealed class SecureChannel : IFrameChannel, IDisposable
{
    private const int TagSize = CipherState.TagSize;

    private readonly IMessageTransport _transport;
    private readonly NoiseTransport _noise;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private byte[]? _pending;   // kurulmuş sayılmayı tetikleyen ve boş olmayan ilk mesaj
    private bool _established;

    private SecureChannel(IMessageTransport transport, NoiseTransport noise, Prefix prefix, bool initiator,
        byte[] remoteStatic, byte[] handshakeHash, bool established, HandshakeKind kind)
    {
        _transport = transport;
        _noise = noise;
        Prefix = prefix;
        IsInitiator = initiator;
        RemoteStaticPublicKey = remoteStatic;
        RemoteDeviceId = DeviceId.FromPublicKey(remoteStatic);
        HandshakeHash = handshakeHash;
        _established = established;
        Kind = kind;
    }

    public Prefix Prefix { get; }
    public bool IsInitiator { get; }
    public bool IsEstablished => _established;
    public byte[] RemoteStaticPublicKey { get; }
    public byte[] RemoteDeviceId { get; }
    public byte[] HandshakeHash { get; }

    /// <summary>Kurulumda kullanılan el sıkışma deseni (PROTOCOL §1, §2b).</summary>
    public HandshakeKind Kind { get; }
    public TransportKind Transport => Prefix.Transport;

    /// <summary>Tek şifreli mesajın düz metin üst sınırı (taşıma sınırı − 16).</summary>
    public int MaxPlaintextSize => Math.Min(_transport.MaxMessageSize - TagSize, FrameCodec.MaxPlaintext);

    /// <summary>DATA gövdesi üst sınırı (tür baytı düşülmüş).</summary>
    public int MaxFrameBodySize => MaxPlaintextSize - 1;

    // ---------------------------------------------------------------- gönderme / alma

    /// <summary>Hazır kodlanmış çerçeveyi (<see cref="FrameCodec"/>) şifreleyip yollar.</summary>
    public ValueTask SendFrameAsync(ReadOnlyMemory<byte> encodedFrame, CancellationToken cancellationToken = default)
    {
        if (encodedFrame.Length == 0) throw new ArgumentException("Boş çerçeve.", nameof(encodedFrame));
        return SendPlaintextAsync(encodedFrame, cancellationToken);
    }

    public ValueTask SendSignalAsync(SignalMessage message, CancellationToken cancellationToken = default) =>
        SendPlaintextAsync(SignalMessages.Encode(message), cancellationToken);

    internal async ValueTask SendPlaintextAsync(ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        if (!_established)
            throw new InvalidOperationException("Oturum kurulmadı: önce EnsureEstablishedAsync (PROTOCOL §1, 0-RTT yok).");
        await SendUncheckedAsync(plaintext, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendUncheckedAsync(ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        if (plaintext.Length > MaxPlaintextSize)
            throw new ProtocolException($"Düz metin {MaxPlaintextSize} baytı aşıyor ({plaintext.Length}).");
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cipher = _noise.Send.Encrypt(plaintext.Span);
            // İptal belirteci bilerek aktarılmaz: yarım yazılmış bir çerçeve akışı bozar ve nonce sırasını kaydırır
            // (ardından gelen CANCEL çözülemez). Takılan bir yazmayı akışı kapatarak kesin.
            await _transport.SendMessageAsync(cipher, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Yanıtlayanda ilk geçerli taşıma mesajı gelene kadar bekler (gelen veri sonraki okumaya saklanır).</summary>
    public async ValueTask EnsureEstablishedAsync(CancellationToken cancellationToken = default)
    {
        if (_established) return;
        while (true)
        {
            var plaintext = await ReceivePlaintextAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ProtocolException("Bağlantı, oturum kurulmadan kapandı.");
            if (plaintext.Length == 0) return;           // onay mesajı
            _pending = plaintext;                          // gerçek veri; kurulmuş + sakla
            return;
        }
    }

    /// <summary>Sıradaki çerçeve veya sinyal; karşı taraf temiz kapattıysa null. Tek okuyucu varsayılır.</summary>
    public async ValueTask<SecureMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            byte[]? plaintext;
            if (_pending is not null)
            {
                plaintext = _pending;
                _pending = null;
            }
            else
            {
                plaintext = await ReceivePlaintextAsync(cancellationToken).ConfigureAwait(false);
                if (plaintext is null) return null;
            }
            if (plaintext.Length == 0) continue;           // onay mesajı: yut

            if (SignalMessages.LooksLikeSignal(plaintext))
                return new SecureMessage(null, SignalMessages.Decode(plaintext));
            return new SecureMessage(FrameCodec.Decode(plaintext), null);
        }
    }

    /// <summary>Yalnız §5 çerçevesi bekler; sinyal gelirse <see cref="ProtocolException"/>.</summary>
    public async ValueTask<Frame?> ReceiveFrameAsync(CancellationToken cancellationToken = default)
    {
        var m = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (m is null) return null;
        return m.Frame ?? throw new ProtocolException("Çerçeve beklenirken sinyal mesajı geldi.");
    }

    /// <summary>Yalnız §4 sinyali bekler; çerçeve gelirse <see cref="ProtocolException"/>.</summary>
    public async ValueTask<SignalMessage?> ReceiveSignalAsync(CancellationToken cancellationToken = default)
    {
        var m = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (m is null) return null;
        return m.Signal ?? throw new ProtocolException("Sinyal beklenirken çerçeve geldi.");
    }

    private async ValueTask<byte[]?> ReceivePlaintextAsync(CancellationToken cancellationToken)
    {
        var cipher = await _transport.ReceiveMessageAsync(false, cancellationToken).ConfigureAwait(false);
        if (cipher is null) return null;
        var plaintext = _noise.Receive.Decrypt(cipher);   // etiket hatası → NoiseException (oturum düşer)
        _established = true;                               // ilk geçerli taşıma mesajı
        return plaintext;
    }

    public void Dispose()
    {
        _noise.Dispose();
        _sendLock.Dispose();
    }

    // ---------------------------------------------------------------- el sıkışma: başlatan

    /// <summary>Başlatan (iPhone): ön ek + IK. <paramref name="remoteStatic"/> eşleştirmede saklanan PC açık anahtarıdır.</summary>
    public static async Task<SecureChannel> ConnectIkAsync(
        IMessageTransport transport, Prefix prefix, NoiseKeyPair localStatic, ReadOnlyMemory<byte> remoteStatic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(localStatic);
        CheckPrefixKind(transport, prefix);
        await transport.SendRawAsync(prefix.Bytes, cancellationToken).ConfigureAwait(false);

        using var hs = new HandshakeState(NoisePattern.IK, true, prefix.Bytes, localStatic, remoteStatic.Span);
        await transport.SendMessageAsync(hs.WriteMessage([]), cancellationToken).ConfigureAwait(false);   // 0-RTT yok: boş yük
        var reply = await ReceiveHandshakeAsync(transport, prefix, cancellationToken).ConfigureAwait(false);
        hs.ReadMessage(reply);

        var channel = Finish(transport, prefix, hs, initiator: true, established: true);
        // IK'de msg2 yanıtlayandan gelir: yanıtlayanın oturumu kurulmuş saymasını sağlayan ilk taşıma mesajı.
        await channel.SendUncheckedAsync(ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        return channel;
    }

    /// <summary>
    /// Başlatan (iPhone), eşleştirme: ön ek + XXpsk3. <paramref name="isRemoteKeyAcceptable"/> PC'nin statik anahtarını
    /// (QR'daki <c>k</c> ile karşılaştırma) <b>3. mesaj gönderilmeden önce</b> doğrular; false → <see cref="PeerNotTrustedException"/>.
    /// </summary>
    public static async Task<SecureChannel> ConnectPairingAsync(
        IMessageTransport transport, Prefix prefix, NoiseKeyPair localStatic, ReadOnlyMemory<byte> psk,
        Func<ReadOnlyMemory<byte>, bool> isRemoteKeyAcceptable, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(localStatic);
        ArgumentNullException.ThrowIfNull(isRemoteKeyAcceptable);
        CheckPrefixKind(transport, prefix);
        await transport.SendRawAsync(prefix.Bytes, cancellationToken).ConfigureAwait(false);

        using var hs = new HandshakeState(NoisePattern.XXpsk3, true, prefix.Bytes, localStatic, default, [psk.ToArray()]);
        await transport.SendMessageAsync(hs.WriteMessage([]), cancellationToken).ConfigureAwait(false);
        var reply = await ReceiveHandshakeAsync(transport, prefix, cancellationToken).ConfigureAwait(false);
        hs.ReadMessage(reply);
        var remote = hs.RemoteStaticPublicKey!;
        if (!isRemoteKeyAcceptable(remote)) throw new PeerNotTrustedException(remote);
        await transport.SendMessageAsync(hs.WriteMessage([]), cancellationToken).ConfigureAwait(false);
        return Finish(transport, prefix, hs, initiator: true, established: true, kind: HandshakeKind.XXpsk3);
    }

    // ---------------------------------------------------------------- el sıkışma: yanıtlayan

    /// <summary>
    /// Yanıtlayan (PC): ön eki okur, sürümü denetler (uyuşmazsa <c>0xFF || sürüm</c> yollar), IK msg1'i işler,
    /// başlatanın statik anahtarı için <paramref name="isTrusted"/> sorar (false → msg2 <b>gönderilmeden</b>
    /// <see cref="PeerNotTrustedException"/>). Dönen kanal henüz <see cref="IsEstablished"/> değildir.
    /// </summary>
    public static async Task<SecureChannel> AcceptIkAsync(
        IMessageTransport transport, NoiseKeyPair localStatic, Func<ReadOnlyMemory<byte>, bool> isTrusted,
        CancellationToken cancellationToken = default, byte supportedVersion = Prefix.CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(isTrusted);
        var prefix = await ReceivePrefixAsync(transport, supportedVersion, cancellationToken).ConfigureAwait(false);

        using var hs = new HandshakeState(NoisePattern.IK, false, prefix.Bytes, localStatic);
        var msg1 = await transport.ReceiveMessageAsync(true, cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolException("Bağlantı el sıkışma sırasında kapandı.");
        var payload = hs.ReadMessage(msg1);
        if (payload.Length != 0) throw new ProtocolException("IK ilk mesajı boş yük taşımalı (0-RTT yok).");

        var remote = hs.RemoteStaticPublicKey!;
        if (!isTrusted(remote)) throw new PeerNotTrustedException(remote);

        await transport.SendMessageAsync(hs.WriteMessage([]), cancellationToken).ConfigureAwait(false);
        return Finish(transport, prefix, hs, initiator: false, established: false);
    }

    /// <summary>Yanıtlayan (PC), eşleştirme: XXpsk3. Başlatanın anahtarı <see cref="RemoteStaticPublicKey"/>'dedir; PC onayı (§2.4) çağırandadır.</summary>
    public static async Task<SecureChannel> AcceptPairingAsync(
        IMessageTransport transport, NoiseKeyPair localStatic, ReadOnlyMemory<byte> psk,
        CancellationToken cancellationToken = default, byte supportedVersion = Prefix.CurrentVersion)
    {
        var prefix = await ReceivePrefixAsync(transport, supportedVersion, cancellationToken).ConfigureAwait(false);

        using var hs = new HandshakeState(NoisePattern.XXpsk3, false, prefix.Bytes, localStatic, default, [psk.ToArray()]);
        var msg1 = await transport.ReceiveMessageAsync(true, cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolException("Bağlantı el sıkışma sırasında kapandı.");
        hs.ReadMessage(msg1);
        await transport.SendMessageAsync(hs.WriteMessage([]), cancellationToken).ConfigureAwait(false);
        var msg3 = await transport.ReceiveMessageAsync(false, cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolException("Bağlantı el sıkışma sırasında kapandı.");
        hs.ReadMessage(msg3);                                   // psk yanlışsa burada AuthenticationFailed
        return Finish(transport, prefix, hs, initiator: false, established: true, kind: HandshakeKind.XXpsk3);
    }

    // ---------------------------------------------------------------- PROTOCOL §2b: yakındaki herkes (XX) ve otomatik yanıtlayan

    /// <summary>Noise XX ilk mesajı (yalnız <c>e</c>, boş yük): 32 bayt.</summary>
    public const int XxFirstMessageSize = 32;

    /// <summary>XXpsk3 ilk mesajı: <c>e</c> + boş yükün 16 baytlık etiketi = 48 bayt (psk kipinde <c>e</c> anahtarı karıştırır). IK ilk mesajı 96 bayttır.</summary>
    public const int XxPsk3FirstMessageSize = 48;

    /// <summary>
    /// Başlatan (iPhone), eşleştirmesiz ilk temas: ön ek + <c>Noise_XX</c> (psk yok). <paramref name="isRemoteKeyAcceptable"/>
    /// (varsa) karşının statik anahtarını 3. mesaj gönderilmeden doğrular (ör. bilinen PC anahtarı).
    /// </summary>
    public static async Task<SecureChannel> ConnectXxAsync(
        IMessageTransport transport, Prefix prefix, NoiseKeyPair localStatic,
        Func<ReadOnlyMemory<byte>, bool>? isRemoteKeyAcceptable = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(localStatic);
        CheckPrefixKind(transport, prefix);
        await transport.SendRawAsync(prefix.Bytes, cancellationToken).ConfigureAwait(false);

        using var hs = new HandshakeState(NoisePattern.XX, true, prefix.Bytes, localStatic);
        await transport.SendMessageAsync(hs.WriteMessage([]), cancellationToken).ConfigureAwait(false);
        var reply = await ReceiveHandshakeAsync(transport, prefix, cancellationToken).ConfigureAwait(false);
        hs.ReadMessage(reply);
        var remote = hs.RemoteStaticPublicKey!;
        if (isRemoteKeyAcceptable is not null && !isRemoteKeyAcceptable(remote)) throw new PeerNotTrustedException(remote);
        await transport.SendMessageAsync(hs.WriteMessage([]), cancellationToken).ConfigureAwait(false);
        return Finish(transport, prefix, hs, initiator: true, established: true, kind: HandshakeKind.XX);
    }

    /// <summary>
    /// Yanıtlayan (PC): tel üzerinde desen işareti yoktur; ilk el sıkışma mesajının uzunluğundan ayrılır —
    /// 32 bayt = XX, 48 bayt = XXpsk3 (<paramref name="pairingPsk"/> yoksa reddedilir), 96 bayt = IK.
    /// IK: <paramref name="isTrustedIk"/> msg2 gönderilmeden sorulur. XX: <paramref name="isAcceptableXx"/> (null ise XX kabul edilmez)
    /// 3. mesajdan sonra karşı anahtar için sorulur. XXpsk3'te anahtar denetimi çağırandadır. Dönen kanalın
    /// <see cref="Kind"/>'i kullanılan deseni söyler; IK kanalı henüz kurulmuş değildir (<see cref="EnsureEstablishedAsync"/>).
    /// </summary>
    public static async Task<SecureChannel> AcceptAutoAsync(
        IMessageTransport transport, NoiseKeyPair localStatic,
        Func<ReadOnlyMemory<byte>, bool> isTrustedIk, Func<ReadOnlyMemory<byte>, bool>? isAcceptableXx,
        ReadOnlyMemory<byte>? pairingPsk = null, CancellationToken cancellationToken = default,
        byte supportedVersion = Prefix.CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(isTrustedIk);
        var prefix = await ReceivePrefixAsync(transport, supportedVersion, cancellationToken).ConfigureAwait(false);
        var msg1 = await transport.ReceiveMessageAsync(true, cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolException("Bağlantı el sıkışma sırasında kapandı.");

        if (msg1.Length != XxFirstMessageSize && msg1.Length != XxPsk3FirstMessageSize)
        {
            using var ik = new HandshakeState(NoisePattern.IK, false, prefix.Bytes, localStatic);
            var payload = ik.ReadMessage(msg1);
            if (payload.Length != 0) throw new ProtocolException("IK ilk mesajı boş yük taşımalı (0-RTT yok).");
            var remote = ik.RemoteStaticPublicKey!;
            if (!isTrustedIk(remote)) throw new PeerNotTrustedException(remote);
            await transport.SendMessageAsync(ik.WriteMessage([]), cancellationToken).ConfigureAwait(false);
            return Finish(transport, prefix, ik, initiator: false, established: false);
        }

        var wantsPsk = msg1.Length == XxPsk3FirstMessageSize;
        var psk = wantsPsk ? (pairingPsk is { Length: > 0 } p ? p.ToArray() : throw new ProtocolException("Eşleştirme modu kapalı (XXpsk3 reddedildi).")) : null;
        if (!wantsPsk && isAcceptableXx is null) throw new ProtocolException("Bu bağlantıda XX el sıkışması kabul edilmiyor.");
        using var hs = psk is null
            ? new HandshakeState(NoisePattern.XX, false, prefix.Bytes, localStatic)
            : new HandshakeState(NoisePattern.XXpsk3, false, prefix.Bytes, localStatic, default, [psk]);
        hs.ReadMessage(msg1);
        await transport.SendMessageAsync(hs.WriteMessage([]), cancellationToken).ConfigureAwait(false);
        var msg3 = await transport.ReceiveMessageAsync(false, cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolException("Bağlantı el sıkışma sırasında kapandı.");
        hs.ReadMessage(msg3);   // psk yanlışsa burada AuthenticationFailed
        var remoteKey = hs.RemoteStaticPublicKey!;
        if (psk is null && !isAcceptableXx!(remoteKey)) throw new PeerNotTrustedException(remoteKey);
        return Finish(transport, prefix, hs, initiator: false, established: true,
            kind: psk is null ? HandshakeKind.XX : HandshakeKind.XXpsk3);
    }

    // ---------------------------------------------------------------- yardımcılar

    private static SecureChannel Finish(IMessageTransport transport, Prefix prefix, HandshakeState hs, bool initiator, bool established,
        HandshakeKind kind = HandshakeKind.IK)
    {
        var remote = hs.RemoteStaticPublicKey ?? throw new NoiseException(NoiseErrorKind.InvalidState, "Karşı statik anahtar yok.");
        return new SecureChannel(transport, hs.Split(), prefix, initiator, remote, hs.HandshakeHash, established, kind);
    }

    private static void CheckPrefixKind(IMessageTransport transport, Prefix prefix)
    {
        if (prefix.Transport != transport.Kind)
            throw new ArgumentException($"Ön ek taşıması ({prefix.Transport}) bu taşımayla ({transport.Kind}) uyuşmuyor.", nameof(prefix));
    }

    private static async Task<byte[]> ReceiveHandshakeAsync(IMessageTransport transport, Prefix prefix, CancellationToken cancellationToken)
    {
        try
        {
            return await transport.ReceiveMessageAsync(true, cancellationToken).ConfigureAwait(false)
                ?? throw new ProtocolException("Bağlantı el sıkışma sırasında kapandı.");
        }
        catch (VersionMismatchException ex) when (ex.LocalVersion == 0)
        {
            throw new VersionMismatchException(prefix.Version, ex.PeerVersion);
        }
    }

    private static async Task<Prefix> ReceivePrefixAsync(IMessageTransport transport, byte supportedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var raw = await transport.ReceiveRawAsync(Prefix.Length, cancellationToken).ConfigureAwait(false);
        var prefix = Prefix.Decode(raw);
        if (prefix.Transport != transport.Kind)
            throw new ProtocolException($"Ön ek taşıma türü ({prefix.Transport}) bu bağlantıyla ({transport.Kind}) uyuşmuyor.");
        if (!prefix.IsVersionSupported(supportedVersion))
        {
            await transport.SendRawAsync(Prefix.EncodeVersionMismatch(supportedVersion), cancellationToken).ConfigureAwait(false);
            throw new VersionMismatchException(supportedVersion, prefix.Version);
        }
        return prefix;
    }
}
