using System.Net.WebSockets;
using WinDrop.Core.Protocol;

namespace WinDrop.Core.Session;

/// <summary>
/// Aktarma sunucusu (PROTOCOL §6.3) WebSocket'i üstünde Noise taşıması: <b>her ikili WebSocket mesajı bir Noise mesajıdır</b>
/// (1..65535 bayt, uzunluk öneki yok; sunucu mesaj sınırlarını korur). 20 baytlık ön ek ve sürüm uyuşmazlığı yanıtı da ayrı
/// birer ikili mesajdır. Metin mesajı veya kapanış çerçevesi hata sayılır: metin <see cref="ProtocolException"/>,
/// kapanış/kopma <see cref="IOException"/> (aktarım katmanı bunu "bağlantı koptu, devam edilebilir" olarak ele alır).
/// Aynı yönde eşzamanlı çağrı yapılmaz (<see cref="SecureChannel"/> sıralar); bir gönderme ile bir alma eşzamanlı olabilir.
/// </summary>
public sealed class WebSocketMessageTransport : IMessageTransport, IDisposable
{
    public const int MaxNoiseMessage = TcpFraming.MaxNoiseMessage;

    private readonly WebSocket _socket;
    private readonly bool _ownsSocket;

    public WebSocketMessageTransport(WebSocket socket, bool ownsSocket = true)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
        _ownsSocket = ownsSocket;
    }

    public TransportKind Kind => TransportKind.Relay;

    public int MaxMessageSize => MaxNoiseMessage;

    public ValueTask SendRawAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        SendBinaryAsync(data, cancellationToken);

    public async ValueTask<byte[]> ReceiveRawAsync(int length, CancellationToken cancellationToken)
    {
        var message = await ReceiveBinaryAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolException("Aktarma bağlantısı ön ek gelmeden kapandı.");
        if (message.Length != length)
            throw new ProtocolException($"Ham yük {length} bayt olmalı ({message.Length} geldi).");
        return message;
    }

    public ValueTask SendMessageAsync(ReadOnlyMemory<byte> noiseMessage, CancellationToken cancellationToken)
    {
        if (noiseMessage.Length == 0) throw new ProtocolException("Boş Noise mesajı gönderilemez.");
        if (noiseMessage.Length > MaxNoiseMessage)
            throw new ProtocolException($"Noise mesajı {MaxNoiseMessage} baytı aşıyor ({noiseMessage.Length}).");
        return SendBinaryAsync(noiseMessage, cancellationToken);
    }

    public async ValueTask<byte[]?> ReceiveMessageAsync(bool handshakePhase, CancellationToken cancellationToken)
    {
        var message = await ReceiveBinaryAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("Aktarma sunucusu bağlantıyı kapattı.");
        if (message.Length == 0) throw new ProtocolException("Boş WebSocket mesajı.");
        // Sürüm uyuşmazlığı yanıtı: tam 2 bayt, ilk bayt 0xFF (gerçek bir Noise mesajı bundan uzundur).
        if (handshakePhase && message.Length == 2 && message[0] == Prefix.VersionMismatchMarker)
            throw new VersionMismatchException(0, message[1]);
        return message;
    }

    private async ValueTask SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            await _socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            throw new IOException("Aktarma sunucusuna yazılamadı: " + ex.Message, ex);
        }
    }

    /// <summary>Tek ikili mesajı okur (≤ 65535 bayt). Kapanış çerçevesi → null; metin/aşırı büyük → <see cref="ProtocolException"/>.</summary>
    private async ValueTask<byte[]?> ReceiveBinaryAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxNoiseMessage + 1];
        var filled = 0;
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                switch (result.MessageType)
                {
                    case WebSocketMessageType.Close:
                        await TryCloseOutputAsync().ConfigureAwait(false);
                        return null;
                    case WebSocketMessageType.Text:
                        Abort();
                        throw new ProtocolException("Aktarma bağlantısında metin mesajı geldi (yalnız ikili beklenir).");
                }
                filled += result.Count;
                if (filled > MaxNoiseMessage)
                {
                    Abort();
                    throw new ProtocolException($"WebSocket mesajı {MaxNoiseMessage} baytı aşıyor.");
                }
                if (result.EndOfMessage) return buffer.AsSpan(0, filled).ToArray();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            throw new IOException("Aktarma bağlantısı koptu: " + ex.Message, ex);
        }
    }

    private async Task TryCloseOutputAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.CloseReceived)
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
    }

    private void Abort()
    {
        try { _socket.Abort(); } catch (Exception ex) when (ex is ObjectDisposedException or WebSocketException) { }
    }

    /// <summary>Temiz kapanış (en iyi çaba, en çok 2 sn): yazılan baytlar karşıya ulaştıktan sonra bağlantı kapanır.</summary>
    public async Task CloseAsync()
    {
        try
        {
            if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException) { }
    }

    /// <summary>Soketi sahiplenmişse sert kapatır (önce <see cref="CloseAsync"/> çağrılmalıdır).</summary>
    public void Dispose()
    {
        if (!_ownsSocket) return;
        Abort();
        _socket.Dispose();
    }
}
