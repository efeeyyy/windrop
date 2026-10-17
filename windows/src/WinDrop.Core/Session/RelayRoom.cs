using System.Buffers.Text;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;

namespace WinDrop.Core.Session;

/// <summary>
/// PROTOCOL §6.3 aktarma sunucusu odası: token, oda kimliği ve adres. Token JSON'da (<c>relay</c> sinyali) 32 baytın
/// base64url'üdür (dolgusuz, 43 karakter; <see cref="TransferId"/> ile aynı kodlama); oda kimliği <b>ham 32 bayttan</b> türetilir.
/// </summary>
public static class RelayRoom
{
    public const int TokenLength = 32;
    public const int RoomIdLength = 32;

    private static readonly byte[] DomainTag = "WinDrop-relay"u8.ToArray();

    public static byte[] NewToken() => RandomNumberGenerator.GetBytes(TokenLength);

    public static string EncodeToken(ReadOnlySpan<byte> token) => Base64Url.EncodeToString(token);

    public static bool TryDecodeToken(string? text, out byte[] token)
    {
        token = [];
        if (text is null || text.Length != 43) return false;   // 32 bayt → 43 base64url karakter
        try
        {
            var bytes = Base64Url.DecodeFromChars(text);
            if (bytes.Length != TokenLength) return false;
            token = bytes;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary><c>roomId = hex(SHA-256("WinDrop-relay" || token))[0..32]</c> (32 küçük hex karakter).</summary>
    public static string ComputeRoomId(ReadOnlySpan<byte> token)
    {
        if (token.Length != TokenLength) throw new ArgumentException("token 32 bayt olmalı.", nameof(token));
        var input = new byte[DomainTag.Length + token.Length];
        DomainTag.CopyTo(input, 0);
        token.CopyTo(input.AsSpan(DomainTag.Length));
        return Convert.ToHexStringLower(SHA256.HashData(input))[..RoomIdLength];
    }

    /// <summary>
    /// <c>wss://&lt;host&gt;/v1/room/&lt;roomId&gt;?role=pc|phone</c>. <paramref name="relayUrl"/>: <c>host</c>, <c>wss://host</c> veya
    /// <c>https://host</c> (şemasız → wss). Şifresiz <c>ws://</c>/<c>http://</c> yalnız yerel döngü adresi için kabul edilir
    /// (geliştirme: <c>wrangler dev</c>); aksi halde ve geçersiz adreste null.
    /// </summary>
    public static Uri? BuildRoomUri(string? relayUrl, string roomId, string role)
    {
        var text = relayUrl?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "wss://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return null;
        string scheme;
        switch (u.Scheme.ToLowerInvariant())
        {
            case "wss" or "https": scheme = "wss"; break;
            case "ws" or "http" when u.IsLoopback: scheme = "ws"; break;
            default: return null;
        }
        return new UriBuilder(scheme, u.Host, u.IsDefaultPort ? -1 : u.Port, "/v1/room/" + roomId, "?role=" + role).Uri;
    }

    /// <summary>Odaya WebSocket olarak bağlanır. HTTP reddi (409/410/429...) <see cref="RelayConnectException"/> olur.</summary>
    public static async Task<ClientWebSocket> ConnectAsync(Uri roomUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roomUri);
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;   // HttpStatusCode (409/410/429) ancak böyle doldurulur
        try
        {
            await socket.ConnectAsync(roomUri, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch (WebSocketException ex)
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            throw new RelayConnectException(status, ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>Aktarma sunucusuna bağlanılamadı; <see cref="Status"/> varsa HTTP yanıtı (429: IP başına oda sınırı).</summary>
public sealed class RelayConnectException : IOException
{
    public HttpStatusCode Status { get; }

    public RelayConnectException(HttpStatusCode status, Exception inner)
        : base(Describe(status, inner), inner) => Status = status;

    private static string Describe(HttpStatusCode status, Exception inner) => status switch
    {
        HttpStatusCode.TooManyRequests => "Aktarma sunucusu çok sık oda açıldığı için reddetti (dakikada en çok 10). Biraz sonra deneyin.",
        HttpStatusCode.Conflict => "Aktarma odası dolu (aynı rol zaten bağlı).",
        HttpStatusCode.Gone => "Aktarma odası kapanmış.",
        0 => "Aktarma sunucusuna bağlanılamadı: " + (inner.InnerException?.Message ?? inner.Message),
        _ => $"Aktarma sunucusu bağlantıyı reddetti (HTTP {(int)status}).",
    };
}

/// <summary>Aktarma odasında kurulmuş PC (yanıtlayan) oturumu; yok edilince WebSocket kapanır.</summary>
public sealed class RelayPcSession(SecureChannel channel, WebSocketMessageTransport transport) : IDisposable
{
    public SecureChannel Channel { get; } = channel;

    /// <summary>Temiz kapanış (en iyi çaba) — yazılanlar karşıya ulaşsın diye <see cref="Dispose"/>'dan önce çağrılır.</summary>
    public Task CloseAsync() => transport.CloseAsync();

    public void Dispose()
    {
        Channel.Dispose();
        transport.Dispose();
    }
}

/// <summary>
/// PC tarafı (PROTOCOL §6.3 "Sırası"): odaya <c>role=pc</c> olarak bağlanır; iPhone (başlatan) bağlanınca ön ek + el sıkışma
/// ilk mesajını yollar, PC <b>yanıtlar</b>. Eş anahtarı BLE oturumundakiyle aynı olmalı; ön ekteki transferId ve (verilmişse)
/// el sıkışma deseni (IK/XX) denetlenir. TCP veri kanalı (<c>DataListener</c>) ile aynı kurallar.
/// </summary>
public static class RelayPcClient
{
    /// <param name="onConnected">WebSocket açıldıktan sonra (ör. <c>relay</c> sinyalini yollamak için) çağrılır.</param>
    /// <param name="handshakeTimeout">Odaya girişten kurulmuş oturuma kadar toplam süre (iPhone'u bekleme dahil).</param>
    /// <exception cref="TimeoutException">iPhone <paramref name="handshakeTimeout"/> içinde gelmedi.</exception>
    public static async Task<RelayPcSession> AcceptAsync(
        Uri roomUri, TransferId transferId, NoiseKeyPair localKey, ReadOnlyMemory<byte> peerPublicKey, HandshakeKind? requiredKind,
        Func<CancellationToken, Task>? onConnected, TimeSpan handshakeTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roomUri);
        ArgumentNullException.ThrowIfNull(transferId);
        ArgumentNullException.ThrowIfNull(localKey);
        var peerKey = peerPublicKey.ToArray();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(handshakeTimeout);
        WebSocketMessageTransport? transport = null;
        SecureChannel? channel = null;
        try
        {
            var socket = await RelayRoom.ConnectAsync(roomUri, timeout.Token).ConfigureAwait(false);
            transport = new WebSocketMessageTransport(socket);
            if (onConnected is not null) await onConnected(timeout.Token).ConfigureAwait(false);

            Func<ReadOnlyMemory<byte>, bool> samePeer = key => key.Span.SequenceEqual(peerKey);
            channel = await SecureChannel.AcceptAutoAsync(transport, localKey, samePeer, samePeer, null, timeout.Token).ConfigureAwait(false);
            if (!channel.Prefix.TransferId.AsSpan().SequenceEqual(transferId.Span))
                throw new ProtocolException("Aktarma ön ekindeki transferId beklenenle uyuşmuyor.");
            if (requiredKind is { } kind && channel.Kind != kind)
                throw new ProtocolException($"El sıkışma deseni {channel.Kind}, BLE oturumu {kind}.");
            await channel.EnsureEstablishedAsync(timeout.Token).ConfigureAwait(false);   // PROTOCOL §1: 0-RTT yok
            return new RelayPcSession(channel, transport);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            channel?.Dispose();
            transport?.Dispose();
            throw new TimeoutException("iPhone aktarma odasına zamanında bağlanmadı.");
        }
        catch
        {
            channel?.Dispose();
            transport?.Dispose();
            throw;
        }
    }
}
