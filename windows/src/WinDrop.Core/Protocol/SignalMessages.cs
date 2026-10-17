using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinDrop.Core.Protocol;

/// <summary>
/// PROTOCOL §4 sinyal mesajları. Gövde UTF-8 JSON <c>{"t": "&lt;tür&gt;", ...}</c>, alanlar camelCase.
/// Bilinmeyen alanlar yok sayılır; bilinmeyen/eksik <c>t</c> ve eksik zorunlu alan <see cref="ProtocolException"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(StatusMessage), "status")]
[JsonDerivedType(typeof(OfferMessage), "offer")]
[JsonDerivedType(typeof(AcceptMessage), "accept")]
[JsonDerivedType(typeof(RejectMessage), "reject")]
[JsonDerivedType(typeof(AddrMessage), "addr")]
[JsonDerivedType(typeof(HotspotMessage), "hotspot")]
[JsonDerivedType(typeof(RelayMessage), "relay")]
[JsonDerivedType(typeof(PathMessage), "path")]
[JsonDerivedType(typeof(CancelSignalMessage), "cancel")]
[JsonDerivedType(typeof(BleDataMessage), "bleData")]
[JsonDerivedType(typeof(PairInfoMessage), "pairInfo")]
public abstract record SignalMessage;

/// <summary>hello (iki yön). <c>wifi</c> yalnız iPhone'dan gelir (<see cref="WifiState"/>), PC'de yok olabilir.</summary>
public sealed record HelloMessage(
    [property: JsonRequired] int V,
    [property: JsonRequired] string Name,
    [property: JsonRequired] IReadOnlyList<string> Caps,
    string? Wifi,
    [property: JsonRequired] bool BtAudio) : SignalMessage;

/// <summary>status (iki yön): değiştiğinde yeniden gönderilir.</summary>
public sealed record StatusMessage(string? Wifi, [property: JsonRequired] bool BtAudio) : SignalMessage;

/// <summary>offer: <c>count</c> dosya, <c>total</c> bayt; <c>preview</c> en çok 5 ad.</summary>
public sealed record OfferMessage(
    [property: JsonRequired] TransferId TransferId,
    [property: JsonRequired] int Count,
    [property: JsonRequired] long Total,
    [property: JsonRequired] IReadOnlyList<string> Preview) : SignalMessage;

public sealed record AcceptMessage([property: JsonRequired] TransferId TransferId, string? Reason = null) : SignalMessage;

public sealed record RejectMessage([property: JsonRequired] TransferId TransferId, string? Reason = null) : SignalMessage;

/// <summary>addr (PC→iPhone): bağlantı adayları.</summary>
public sealed record AddrCandidate([property: JsonRequired] string Ip, [property: JsonRequired] int Port);

public sealed record AddrMessage(
    [property: JsonRequired] TransferId TransferId,
    [property: JsonRequired] IReadOnlyList<AddrCandidate> Candidates) : SignalMessage;

/// <summary>hotspot (PC→iPhone): SSID/parola yalnız değiştiyse.</summary>
public sealed record HotspotMessage(
    [property: JsonRequired] TransferId TransferId,
    [property: JsonRequired] string Ip,
    [property: JsonRequired] int Port,
    string? Ssid = null,
    string? Pass = null) : SignalMessage;

/// <summary>pairInfo (PC→iPhone, yalnız eşleştirme kanalında, PC onayından sonra): etkin nokta SSID/parolası (§2.7, §6.2).</summary>
public sealed record PairInfoMessage(
    [property: JsonRequired] string Ssid,
    [property: JsonRequired] string Pass) : SignalMessage
{
    public override string ToString() => $"PairInfoMessage {{ Ssid = {Ssid}, Pass = *** }}";
}

/// <summary>relay: tek kullanımlık token (URL gönderilmez).</summary>
public sealed record RelayMessage(
    [property: JsonRequired] TransferId TransferId,
    [property: JsonRequired] string Token) : SignalMessage;

/// <summary>path (iPhone→PC): <see cref="PathResult"/> sabitlerinden biri.</summary>
public sealed record PathMessage(
    [property: JsonRequired] TransferId TransferId,
    [property: JsonRequired] string Result) : SignalMessage;

/// <summary>cancel (iki yön). Veri kanalındaki CANCEL çerçevesinden ayrıdır.</summary>
public sealed record CancelSignalMessage([property: JsonRequired] TransferId TransferId) : SignalMessage;

/// <summary>bleData (PC→iPhone, PROTOCOL §6.4a): aktarım BLE oturumu üstünden §5 çerçeveleriyle yapılacak; gönderen taraf MANIFEST ile başlar.</summary>
public sealed record BleDataMessage([property: JsonRequired] TransferId TransferId) : SignalMessage;

public static class WifiState
{
    public const string None = "none";
    public const string Connected = "connected";
}

public static class PathResult
{
    public const string TcpOk = "tcp_ok";
    public const string TcpFail = "tcp_fail";
    public const string HotspotOk = "hotspot_ok";
    public const string HotspotFail = "hotspot_fail";
    public const string RelayOk = "relay_ok";
    public const string RelayFail = "relay_fail";

    internal static readonly string[] All = [TcpOk, TcpFail, HotspotOk, HotspotFail, RelayOk, RelayFail];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true)]   // "t" nesnenin ilk alanı olmak zorunda değil
[JsonSerializable(typeof(SignalMessage))]
internal sealed partial class SignalJsonContext : JsonSerializerContext;

/// <summary>§4 sinyal mesajı kodlama/çözme. Düz metin üst sınırı: 8192 − 16 (etiket) = 8176 bayt (BLE Noise mesajı ≤ 8192).</summary>
public static class SignalMessages
{
    public const int MaxPlaintext = 8176;
    public const int MaxPreview = 5;

    public static byte[] Encode(SignalMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Validate(message);
        var json = JsonSerializer.SerializeToUtf8Bytes(message, SignalJsonContext.Default.SignalMessage);
        if (json.Length > MaxPlaintext)
            throw new ProtocolException($"Sinyal mesajı {MaxPlaintext} baytı aşıyor ({json.Length}).");
        return json;
    }

    public static SignalMessage Decode(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length == 0) throw new ProtocolException("Boş sinyal mesajı.");
        if (plaintext.Length > MaxPlaintext)
            throw new ProtocolException($"Sinyal mesajı {MaxPlaintext} baytı aşıyor ({plaintext.Length}).");
        SignalMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(plaintext, SignalJsonContext.Default.SignalMessage);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // NotSupportedException: bilinmeyen/eksik "t" ayırıcısı.
            throw new ProtocolException($"Geçersiz sinyal mesajı: {ex.Message}");
        }
        if (message is null) throw new ProtocolException("Sinyal mesajı null.");
        Validate(message);
        return message;
    }

    /// <summary>JSON gövdesi mi? (İlk bayt '{'; çerçeve tür baytları 1–8, ayrışırlar.)</summary>
    public static bool LooksLikeSignal(ReadOnlySpan<byte> plaintext) => plaintext.Length > 0 && plaintext[0] == (byte)'{';

    private static void Validate(SignalMessage m)
    {
        switch (m)
        {
            case HelloMessage h:
                CheckWifi(h.Wifi);
                break;
            case StatusMessage s:
                CheckWifi(s.Wifi);
                break;
            case OfferMessage o:
                if (o.Count < 0 || o.Total < 0) throw new ProtocolException("offer: negatif count/total.");
                if (o.Preview.Count > MaxPreview) throw new ProtocolException($"offer: preview en çok {MaxPreview} ad olabilir.");
                break;
            case AddrMessage a:
                foreach (var c in a.Candidates) CheckPort(c.Port);
                break;
            case HotspotMessage h:
                CheckPort(h.Port);
                break;
            case PathMessage p:
                if (Array.IndexOf(PathResult.All, p.Result) < 0)
                    throw new ProtocolException($"path: bilinmeyen sonuç '{p.Result}'.");
                break;
        }
    }

    private static void CheckWifi(string? wifi)
    {
        if (wifi is not null && wifi != WifiState.None && wifi != WifiState.Connected)
            throw new ProtocolException($"wifi: bilinmeyen değer '{wifi}'.");
    }

    private static void CheckPort(int port)
    {
        if (port is < 1 or > 65535) throw new ProtocolException($"Geçersiz port: {port}.");
    }
}
