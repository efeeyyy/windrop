using System.Security.Cryptography;
using NSec.Cryptography;

namespace WinDrop.Core.Noise;

/// <summary>Split sonucu: iki yönlü taşıma şifreleri (başlatan/yanıtlayan rolüne göre çözülmüş).</summary>
public sealed class NoiseTransport : IDisposable
{
    /// <summary>Bu tarafın gönderdiği mesajları şifreler.</summary>
    public CipherState Send { get; }
    /// <summary>Karşı tarafın mesajlarını çözer.</summary>
    public CipherState Receive { get; }

    internal NoiseTransport(CipherState send, CipherState receive)
    {
        Send = send;
        Receive = receive;
    }

    public void Dispose()
    {
        Send.Dispose();
        Receive.Dispose();
    }
}

/// <summary>
/// Genel Noise el sıkışma motoru (rev 34, §5.3): <see cref="NoisePattern"/> belirteç listesini yürütür.
/// Kullanım: sırayla <see cref="WriteMessage"/>/<see cref="ReadMessage"/> (0. mesaj başlatandan),
/// <see cref="IsComplete"/> olunca <see cref="Split"/>. İlk hata el sıkışmayı kalıcı olarak geçersiz kılar.
/// </summary>
public sealed class HandshakeState : IDisposable
{
    public const int MaxMessageSize = CipherState.MaxMessageSize;
    private const int DhLen = 32;

    private readonly NoisePattern _pattern;
    private readonly bool _initiator;
    private readonly SymmetricState _sym;
    private readonly NoiseKeyPair? _s;
    private readonly byte[][] _psks;
    private readonly NoiseKeyPair? _injectedEphemeral;
    private NoiseKeyPair? _e;
    private PublicKey? _rs;
    private byte[]? _rsBytes;
    private PublicKey? _re;
    private int _messageIndex;
    private int _pskIndex;
    private bool _failed;
    private byte[]? _handshakeHash;

    /// <summary>Üretim kurucusu: ephemeral her seferinde rastgele üretilir.</summary>
    /// <param name="pattern">Mesaj deseni.</param>
    /// <param name="initiator">true = başlatan.</param>
    /// <param name="prologue">Her iki tarafın aynı vermesi gereken ön ek (PROTOCOL §1: 20 baytlık açık ön ek).</param>
    /// <param name="localStatic">Kendi statik anahtarım (desen gerektiriyorsa). Sahipliği çağıranda kalır.</param>
    /// <param name="remoteStatic">Karşının statik açık anahtarı (desen ön mesajda bildirmişse; ör. IK'da başlatan için).</param>
    /// <param name="psks">pskN modifikatörü sayısı kadar 32 baytlık psk, kullanım sırasıyla.</param>
    public HandshakeState(
        NoisePattern pattern,
        bool initiator,
        ReadOnlySpan<byte> prologue,
        NoiseKeyPair? localStatic = null,
        ReadOnlySpan<byte> remoteStatic = default,
        IReadOnlyList<byte[]>? psks = null)
        : this(pattern, initiator, prologue, localStatic, remoteStatic, psks, injectedEphemeral: null)
    {
    }

    /// <summary>
    /// Test kurucusu (internal): sabit bir ephemeral anahtar enjekte eder (cacophony vektörleri için).
    /// Üretimde ephemeral sabitlemek güvenliği kırar; bu yüzden public API'de yoktur.
    /// Enjekte edilen anahtarın sahipliği bu nesneye geçer (Dispose'ta atılır).
    /// </summary>
    internal HandshakeState(
        NoisePattern pattern,
        bool initiator,
        ReadOnlySpan<byte> prologue,
        NoiseKeyPair? localStatic,
        ReadOnlySpan<byte> remoteStatic,
        IReadOnlyList<byte[]>? psks,
        NoiseKeyPair? injectedEphemeral)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        _pattern = pattern;
        _initiator = initiator;
        _s = localStatic;
        _injectedEphemeral = injectedEphemeral;

        psks ??= [];
        if (psks.Count != pattern.PskCount)
            throw new ArgumentException($"{pattern.Name} {pattern.PskCount} psk ister, {psks.Count} verildi.", nameof(psks));
        foreach (var p in psks)
            if (p is null || p.Length != 32) throw new ArgumentException("psk 32 bayt olmalı.", nameof(psks));
        _psks = psks.Select(p => (byte[])p.Clone()).ToArray();

        if (!remoteStatic.IsEmpty)
        {
            _rsBytes = remoteStatic.ToArray();
            _rs = ImportPublicKey(_rsBytes);
        }

        _sym = new SymmetricState(pattern.ProtocolName);
        _sym.MixHash(prologue);

        // Ön mesajlar: önce başlatan, sonra yanıtlayan (§5.3).
        if (pattern.InitiatorHasPreMessageStatic) _sym.MixHash(PreMessageKey(initiatorSide: true));
        if (pattern.ResponderHasPreMessageStatic) _sym.MixHash(PreMessageKey(initiatorSide: false));

        // Yerel statik anahtar gereksinimi (kullanım anında da denetlenir, ama erken hata daha iyi).
        if (_s is null && NeedsLocalStatic())
            throw new ArgumentException($"{pattern.Name} bu rol için yerel statik anahtar ister.", nameof(localStatic));
    }

    /// <summary>Sıra benim mi? (0. mesaj başlatanın.)</summary>
    public bool IsMyTurnToWrite => !_failed && !IsComplete && (_messageIndex % 2 == 0) == _initiator;

    public bool IsComplete => _messageIndex >= _pattern.Messages.Length;

    /// <summary>Karşı tarafın statik açık anahtarı (ön mesajdan verildiyse veya el sıkışmada öğrenildiyse), yoksa null.</summary>
    public byte[]? RemoteStaticPublicKey => _rsBytes is null ? null : (byte[])_rsBytes.Clone();

    /// <summary>El sıkışma özeti h; yalnız tamamlandıktan sonra (kanal bağlama için).</summary>
    public byte[] HandshakeHash =>
        _handshakeHash is null
            ? throw new NoiseException(NoiseErrorKind.InvalidState, "El sıkışma henüz tamamlanmadı.")
            : (byte[])_handshakeHash.Clone();

    /// <summary>Sıradaki el sıkışma mesajını üretir (<paramref name="payload"/> şifreli/şifresiz desene göre eklenir).</summary>
    public byte[] WriteMessage(ReadOnlySpan<byte> payload)
    {
        if (_failed) throw new NoiseException(NoiseErrorKind.InvalidState, "El sıkışma daha önce başarısız oldu.");
        if (IsComplete) throw new NoiseException(NoiseErrorKind.InvalidState, "El sıkışma tamamlandı; Split çağırın.");
        if (!IsMyTurnToWrite) throw new NoiseException(NoiseErrorKind.InvalidState, "Yazma sırası karşı tarafta.");

        var tokens = _pattern.Messages[_messageIndex];
        // Durumu değiştirmeden uzunluk ön denetimi: 65535 aşımı yan etkisiz reddedilir.
        if (ComputeWriteLength(tokens, payload.Length) > MaxMessageSize)
            throw new NoiseException(NoiseErrorKind.MessageTooLarge, "El sıkışma mesajı 65535 baytı aşar.");

        try
        {
            var buffer = new List<byte>(DhLen * 4 + payload.Length + CipherState.TagSize);
            foreach (var token in tokens)
            {
                switch (token)
                {
                    case Token.E:
                        _e?.Dispose();
                        _e = _injectedEphemeral ?? NoiseKeyPair.Generate();
                        buffer.AddRange(_e.PublicKey);
                        _sym.MixHash(_e.PublicKey);
                        if (_pattern.PskCount > 0) _sym.MixKey(_e.PublicKey);
                        break;
                    case Token.S:
                        if (_s is null) throw new NoiseException(NoiseErrorKind.InvalidState, "Yerel statik anahtar yok.");
                        buffer.AddRange(_sym.EncryptAndHash(_s.PublicKey));
                        break;
                    case Token.Psk:
                        _sym.MixKeyAndHash(_psks[_pskIndex++]);
                        break;
                    default:
                        MixDh(token);
                        break;
                }
            }
            buffer.AddRange(_sym.EncryptAndHash(payload));
            Advance();
            return buffer.ToArray();
        }
        catch (NoiseException)
        {
            _failed = true;
            throw;
        }
    }

    /// <summary>Karşıdan gelen el sıkışma mesajını işler, çözülmüş yükü döndürür.</summary>
    public byte[] ReadMessage(ReadOnlySpan<byte> message)
    {
        if (_failed) throw new NoiseException(NoiseErrorKind.InvalidState, "El sıkışma daha önce başarısız oldu.");
        if (IsComplete) throw new NoiseException(NoiseErrorKind.InvalidState, "El sıkışma tamamlandı; Split çağırın.");
        if (IsMyTurnToWrite) throw new NoiseException(NoiseErrorKind.InvalidState, "Okuma sırası karşı tarafta değil.");

        try
        {
            if (message.Length > MaxMessageSize)
                throw new NoiseException(NoiseErrorKind.MessageTooLarge, "El sıkışma mesajı 65535 baytı aşar.");

            var tokens = _pattern.Messages[_messageIndex];
            foreach (var token in tokens)
            {
                switch (token)
                {
                    case Token.E:
                        if (message.Length < DhLen)
                            throw new NoiseException(NoiseErrorKind.InvalidMessageLength, "Mesaj ephemeral anahtar için kısa.");
                        var reBytes = message[..DhLen].ToArray();
                        message = message[DhLen..];
                        _re = ImportPublicKey(reBytes);
                        _sym.MixHash(reBytes);
                        if (_pattern.PskCount > 0) _sym.MixKey(reBytes);
                        break;
                    case Token.S:
                        var len = _sym.HasKey ? DhLen + CipherState.TagSize : DhLen;
                        if (message.Length < len)
                            throw new NoiseException(NoiseErrorKind.InvalidMessageLength, "Mesaj statik anahtar için kısa.");
                        var rsBytes = _sym.DecryptAndHash(message[..len]);
                        message = message[len..];
                        _rs = ImportPublicKey(rsBytes);
                        _rsBytes = rsBytes;
                        break;
                    case Token.Psk:
                        _sym.MixKeyAndHash(_psks[_pskIndex++]);
                        break;
                    default:
                        MixDh(token);
                        break;
                }
            }
            var payload = _sym.DecryptAndHash(message);
            Advance();
            return payload;
        }
        catch (NoiseException)
        {
            _failed = true;
            throw;
        }
    }

    /// <summary>El sıkışma bittikten sonra taşıma şifrelerini üretir. Bir kez çağrılır; sonra nesne atılabilir.</summary>
    public NoiseTransport Split()
    {
        if (!IsComplete || _failed)
            throw new NoiseException(NoiseErrorKind.InvalidState, "El sıkışma tamamlanmadan Split çağrılamaz.");
        var (c1, c2) = _sym.Split();
        return _initiator ? new NoiseTransport(c1, c2) : new NoiseTransport(c2, c1);
    }

    public void Dispose()
    {
        _sym.Dispose();
        _e?.Dispose();
        if (!ReferenceEquals(_injectedEphemeral, _e)) _injectedEphemeral?.Dispose();
        foreach (var p in _psks) CryptographicOperations.ZeroMemory(p);
    }

    private void Advance()
    {
        _messageIndex++;
        if (IsComplete) _handshakeHash = _sym.GetHandshakeHash();
    }

    private byte[] PreMessageKey(bool initiatorSide)
    {
        // Ön mesajdaki açık anahtar: kendi tarafınsa yerel statik, değilse bildirilen uzak statik.
        if (initiatorSide == _initiator)
            return _s?.PublicKey ?? throw new ArgumentException("Desen ön mesajda yerel statik anahtar ister.", "localStatic");
        return _rsBytes ?? throw new ArgumentException("Desen ön mesajda karşının statik anahtarını ister.", "remoteStatic");
    }

    private bool NeedsLocalStatic()
    {
        foreach (var message in _pattern.Messages.Where((_, i) => (i % 2 == 0) == _initiator))
            if (message.Contains(Token.S)) return true;
        foreach (var message in _pattern.Messages)
            foreach (var t in message)
                if (t == Token.SS || (t == Token.ES && !_initiator) || (t == Token.SE && _initiator)) return true;
        return false;
    }

    private void MixDh(Token token)
    {
        // Başlatan ve yanıtlayan için belirteç → (yerel anahtar, uzak açık anahtar) eşlemesi (§5.3).
        (NoiseKeyPair? local, PublicKey? remote) = token switch
        {
            Token.EE => (_e, _re),
            Token.ES => _initiator ? (_e, _rs) : (_s, _re),
            Token.SE => _initiator ? (_s, _re) : (_e, _rs),
            Token.SS => (_s, _rs),
            _ => throw new InvalidOperationException("DH belirteci değil."),
        };
        if (local is null || remote is null)
            throw new NoiseException(NoiseErrorKind.InvalidState, $"{token} için gereken anahtar eksik.");

        // Agree, düşük dereceli noktada (tümüyle sıfır çıktı) null döndürür.
        using var shared = KeyAgreementAlgorithm.X25519.Agree(local.Key, remote)
            ?? throw new NoiseException(NoiseErrorKind.InvalidKey, "X25519 çıktısı geçersiz (düşük dereceli nokta).");
        _sym.MixKey(shared);
    }

    private static PublicKey ImportPublicKey(ReadOnlySpan<byte> bytes)
    {
        if (!PublicKey.TryImport(KeyAgreementAlgorithm.X25519, bytes, KeyBlobFormat.RawPublicKey, out var key) || key is null)
            throw new NoiseException(NoiseErrorKind.InvalidKey, "Geçersiz X25519 açık anahtarı.");
        return key;
    }

    /// <summary>Üretilecek mesajın tam uzunluğu (yan etkisiz benzetim; "anahtar var mı" durumunu izler).</summary>
    private int ComputeWriteLength(Token[] tokens, int payloadLength)
    {
        var hasKey = _sym.HasKey;
        var length = 0;
        foreach (var t in tokens)
        {
            switch (t)
            {
                case Token.E:
                    length += DhLen;
                    if (_pattern.PskCount > 0) hasKey = true;   // MixKey(e.public)
                    break;
                case Token.S:
                    length += DhLen + (hasKey ? CipherState.TagSize : 0);
                    break;
                default:
                    hasKey = true;                               // ee/es/se/ss: MixKey; psk: MixKeyAndHash
                    break;
            }
        }
        return length + payloadLength + (hasKey ? CipherState.TagSize : 0);
    }
}
