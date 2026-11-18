using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WinDrop.Core.Protocol;

namespace WinDrop.Platform;

public enum PushOutcome
{
    /// <summary>Sunucu 200 döndü (bildirim APNs'e verildi).</summary>
    Sent,
    /// <summary>501: sunucuda push yapılandırılmamış.</summary>
    Disabled,
    /// <summary>404/410: sunucu bu pushId'yi tanımıyor (kayıt silinmiş).</summary>
    Unknown,
    /// <summary>429: pushId veya IP sınırı.</summary>
    RateLimited,
    /// <summary>Diğer 4xx/5xx.</summary>
    ServerError,
    /// <summary>Ağ hatası, zaman aşımı veya geçersiz adres.</summary>
    Unreachable,
}

public sealed record PushResult(PushOutcome Outcome, int? Status = null);

/// <summary>
/// PROTOCOL §9 tetik: <c>POST &lt;relayUrl&gt;/v1/push/&lt;pushId&gt;</c> <c>{"from": "&lt;PC adı, ≤ 40&gt;"}</c>.
/// pushId asla loglanmaz. Sunucu cevabı yalnız durum koduyla yorumlanır.
/// </summary>
public sealed class PushNotifier : IDisposable
{
    public const int MaxFromChars = 40;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;

    /// <param name="handler">Testte sahte işleyici; null: sistem varsayılanı.</param>
    public PushNotifier(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = RequestTimeout;
    }

    /// <summary>
    /// <c>https://&lt;host&gt;/v1/push/&lt;pushId&gt;</c>. <paramref name="relayUrl"/>: <c>host</c>, <c>wss://host</c>, <c>https://host</c> (şemasız → https).
    /// Şifresiz <c>ws://</c>/<c>http://</c> yalnız yerel döngü adresi için kabul edilir; geçersizse null.
    /// </summary>
    public static Uri? BuildPushUri(string? relayUrl, string pushId)
    {
        if (!PushIds.IsValid(pushId)) return null;
        var text = relayUrl?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return null;
        string scheme;
        switch (u.Scheme.ToLowerInvariant())
        {
            case "wss" or "https": scheme = "https"; break;
            case "ws" or "http" when u.IsLoopback: scheme = "http"; break;
            default: return null;
        }
        return new UriBuilder(scheme, u.Host, u.IsDefaultPort ? -1 : u.Port, "/v1/push/" + pushId).Uri;
    }

    /// <summary>PC adını sunucunun beklediği biçime getirir: denetim karakterleri atılır, en çok 40 karakter (kod noktası). Boşsa "WinDrop".</summary>
    public static string SanitizeFrom(string? name)
    {
        var sb = new StringBuilder();
        var count = 0;
        foreach (var rune in (name ?? "").EnumerateRunes())
        {
            var cat = System.Text.Rune.GetUnicodeCategory(rune);
            if (cat is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
                continue;
            if (count >= MaxFromChars) break;
            sb.Append(rune.ToString());
            count++;
        }
        var result = sb.ToString().Trim();
        return result.Length == 0 ? "WinDrop" : result;
    }

    public async Task<PushResult> SendAsync(string? relayUrl, string pushId, string pcName, CancellationToken ct)
    {
        var uri = BuildPushUri(relayUrl, pushId);
        if (uri is null) return new PushResult(PushOutcome.Unreachable);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["from"] = SanitizeFrom(pcName) }),
                    new UTF8Encoding(false), new MediaTypeHeaderValue("application/json")),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var outcome = response.StatusCode switch
            {
                HttpStatusCode.OK => PushOutcome.Sent,
                HttpStatusCode.NotImplemented => PushOutcome.Disabled,
                HttpStatusCode.NotFound or HttpStatusCode.Gone => PushOutcome.Unknown,
                HttpStatusCode.TooManyRequests => PushOutcome.RateLimited,
                _ => PushOutcome.ServerError,
            };
            return new PushResult(outcome, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested();   // kullanıcı iptali ≠ ağ hatası
            return new PushResult(PushOutcome.Unreachable);
        }
    }

    public void Dispose() => _http.Dispose();
}
