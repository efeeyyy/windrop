using WinDrop.Core.Protocol;

namespace WinDrop.Core.Session;

/// <summary>
/// Noise mesajı taşıyan soyut taşıma (TCP/aktarma akışı veya BLE). İki ucun ortak sözleşmesi:
/// 20 baytlık ön ek ve sürüm uyuşmazlığı yanıtı <i>ham</i>, diğer her şey tek bir "Noise mesajı"dır.
/// Aynı yönde eşzamanlı çağrı yapılmaz (üst katman <see cref="SecureChannel"/> sıralar).
/// </summary>
public interface IMessageTransport
{
    /// <summary>Bu taşımanın ön ekte beklenen türü (TCP/Relay/Ble).</summary>
    TransportKind Kind { get; }

    /// <summary>Tek Noise mesajının üst sınırı: TCP 65535, BLE 8192.</summary>
    int MaxMessageSize { get; }

    /// <summary>Ön ek veya sürüm uyuşmazlığı yanıtı gibi çerçevesiz baytları yollar.</summary>
    ValueTask SendRawAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Tam <paramref name="length"/> ham bayt alır (ön ek). Eksik/yanlış uzunlukta <see cref="ProtocolException"/>.</summary>
    ValueTask<byte[]> ReceiveRawAsync(int length, CancellationToken cancellationToken);

    ValueTask SendMessageAsync(ReadOnlyMemory<byte> noiseMessage, CancellationToken cancellationToken);

    /// <summary>Sıradaki Noise mesajı; karşı taraf temiz kapattıysa null.
    /// <paramref name="handshakePhase"/> true ise ilk baytların sürüm uyuşmazlığı yanıtı olup olmadığı denetlenir.</summary>
    ValueTask<byte[]?> ReceiveMessageAsync(bool handshakePhase, CancellationToken cancellationToken);
}

/// <summary>TCP (doğrudan, etkin nokta) veya aktarma (<see cref="TransportKind.Relay"/>) akışı üstünde §6.1 çerçevelemesi.</summary>
public sealed class TcpMessageTransport : IMessageTransport
{
    private readonly Stream _stream;

    public TcpMessageTransport(Stream stream, TransportKind kind = TransportKind.Tcp)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (kind == TransportKind.Ble) throw new ArgumentException("TCP taşıması BLE türünde olamaz.", nameof(kind));
        _stream = stream;
        Kind = kind;
    }

    public TransportKind Kind { get; }

    public int MaxMessageSize => TcpFraming.MaxNoiseMessage;

    public async ValueTask SendRawAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<byte[]> ReceiveRawAsync(int length, CancellationToken cancellationToken) =>
        TcpFraming.ReadExactAsync(_stream, length, cancellationToken);

    public ValueTask SendMessageAsync(ReadOnlyMemory<byte> noiseMessage, CancellationToken cancellationToken) =>
        TcpFraming.WriteMessageAsync(_stream, noiseMessage, cancellationToken);

    public ValueTask<byte[]?> ReceiveMessageAsync(bool handshakePhase, CancellationToken cancellationToken) =>
        handshakePhase
            ? TcpFraming.ReadHandshakeMessageAsync(_stream, cancellationToken)
            : TcpFraming.ReadMessageAsync(_stream, cancellationToken);
}

/// <summary>GATT üzerinden ham yük alışverişi yapan bağlantı (gerçek BLE yığını bu arayüzün arkasında kalır).</summary>
public interface IBleLink
{
    /// <summary>Anlaşılan ATT MTU (en az 23). Tek GATT yükü en çok <c>Mtu − 3</c> bayttır.</summary>
    int Mtu { get; }

    /// <summary>BLE bağlantı kimliği (aynı telefondan gelen tüm oturumlarda aynı); bilinmiyorsa null.</summary>
    string? LinkId => null;

    /// <summary>Tek GATT yazma/bildirim yükü gönderir (ön ek dahil).</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> gattPayload, CancellationToken cancellationToken);

    /// <summary>Sıradaki GATT yükü; bağlantı koptuysa null.</summary>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// PROTOCOL §4.3: oturum protokol hatasıyla bittiğinde istemciye tek bildirim olarak <see cref="BleResync.Marker"/> gönderir.
    /// Gerçek bağlantı, abone yoksa sessizce atlar; varsayılan uygulama düz <see cref="SendAsync"/>'tir.
    /// </summary>
    ValueTask SendResetAsync(CancellationToken cancellationToken) => SendAsync(BleResync.Marker, cancellationToken);

    /// <summary>
    /// DECISIONS 2: IK ilk mesajındaki statik anahtar güvenilen listede yoksa tek bildirim olarak <see cref="BleResync.UnknownMarker"/>
    /// (<c>WDU</c>) gönderir; iPhone kaydını yalnız bunu alınca siler. Varsayılan uygulama düz <see cref="SendAsync"/>'tir.
    /// </summary>
    ValueTask SendUnknownAsync(CancellationToken cancellationToken) => SendAsync(BleResync.UnknownMarker, cancellationToken);
}

/// <summary>PROTOCOL §4.3: BLE oturum sıfırlama işareti.</summary>
public static class BleResync
{
    /// <summary>ASCII <c>WDR</c> (3 bayt): PC → iPhone, parçalama başlığı yok.</summary>
    public static readonly byte[] Marker = "WDR"u8.ToArray();

    /// <summary>ASCII <c>WDU</c> (3 bayt): PC → iPhone, "IK anahtarı tanınmıyor" (unknown); parçalama başlığı yok.</summary>
    public static readonly byte[] UnknownMarker = "WDU"u8.ToArray();
}

/// <summary>Oturum ortasında yeni BLE ön eki alındı; ön ek <see cref="BleMessageTransport.RestartPrefix"/> içindedir.</summary>
public sealed class BleSessionRestartException() : ProtocolException("BLE istemcisi oturumu yeniden başlattı (yeni ön ek).");

/// <summary>BLE oturumu: Noise mesajı <see cref="BleFragmenter"/> ile parçalanır, §4.2 gereği sıra no mesaj başınadır.</summary>
public sealed class BleMessageTransport : IMessageTransport
{
    private readonly IBleLink _link;
    private readonly BleReassembler _reassembler = new();
    private int _sendSequence;
    private bool _midMessage;

    public BleMessageTransport(IBleLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        _link = link;
        BleFragmenter.MaxFragmentData(link.Mtu);   // MTU < 23 ise erken hata
    }

    public TransportKind Kind => TransportKind.Ble;

    /// <summary>Oturum ortasında gelen yeni BLE ön eki (istemci oturumu yeniden başlattı); yoksa null.</summary>
    public byte[]? RestartPrefix { get; private set; }

    public int MaxMessageSize => BleFragmenter.MaxMessageSize;

    public ValueTask SendRawAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.Length > _link.Mtu - BleFragmenter.AttOverhead)
            throw new ProtocolException("Ham yük tek GATT yazmasına sığmıyor.");
        return _link.SendAsync(data, cancellationToken);
    }

    public async ValueTask<byte[]> ReceiveRawAsync(int length, CancellationToken cancellationToken)
    {
        var payload = await _link.ReceiveAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolException("BLE bağlantısı koptu.");
        if (payload.Length != length)
            throw new ProtocolException($"Ham yük {length} bayt olmalı ({payload.Length} geldi).");
        return payload;
    }

    public async ValueTask SendMessageAsync(ReadOnlyMemory<byte> noiseMessage, CancellationToken cancellationToken)
    {
        var fragments = BleFragmenter.Fragment(noiseMessage.Span, _link.Mtu, _sendSequence);
        _sendSequence = (_sendSequence + 1) % BleFragmenter.SequenceModulo;
        foreach (var fragment in fragments)
            await _link.SendAsync(fragment, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<byte[]?> ReceiveMessageAsync(bool handshakePhase, CancellationToken cancellationToken)
    {
        while (true)
        {
            var payload = await _link.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                if (_midMessage) throw new ProtocolException("BLE bağlantısı mesaj ortasında koptu.");
                return null;
            }
            // PROTOCOL §4.3: oturum ortasında tam 20 bayt geçerli BLE ön eki = istemci oturumu yeniden başlatıyor.
            if (payload.Length == Prefix.Length && Prefix.TryDecode(payload, out var restart) && restart.Transport == TransportKind.Ble)
            {
                RestartPrefix = payload;
                throw new BleSessionRestartException();
            }
            // Sürüm uyuşmazlığı yanıtı: tam 2 bayt, ilk bayt 0xFF. Geçerli bir ilk parça başlığı (sıra 0) 0x80/0x00 olur.
            if (handshakePhase && !_midMessage && payload.Length == 2 && payload[0] == Prefix.VersionMismatchMarker)
                throw new VersionMismatchException(0, payload[1]);

            var message = _reassembler.Push(payload);
            if (message is not null)
            {
                _midMessage = false;
                return message;
            }
            _midMessage = true;
        }
    }
}
