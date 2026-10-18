using System.Text.Json;

namespace WinDrop.Platform;

/// <summary>Kullanıcı ayarları: <c>%LOCALAPPDATA%\WinDrop\settings.json</c>. Eksik/bozuk dosya → varsayılanlar.</summary>
public sealed class AppSettings
{
    public const long DefaultAutoAcceptMaxBytes = 2L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // WINDROP_INSTANCE (yalnız deneme için ayrı örnek) ayrı ayar dosyası kullanır; gerçek ayarlara dokunmaz.
    public static string DefaultPath { get; } = Path.Combine(Storage.DefaultRoot,
        Environment.GetEnvironmentVariable("WINDROP_INSTANCE") is { Length: > 0 } instance ? $"settings.{instance}.json" : "settings.json");

    public static string DefaultDownloadFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "WinDrop");

    public string DownloadFolder { get; set; } = DefaultDownloadFolder();

    /// <summary>Eşli cihazdan gelen teklifleri sormadan kabul et (<see cref="AutoAcceptMaxBytes"/> altında).</summary>
    public bool AutoAccept { get; set; }

    public long AutoAcceptMaxBytes { get; set; } = DefaultAutoAcceptMaxBytes;

    /// <summary>"Gönder" menüsüne WinDrop kısayolu ekle (istenen durum; <see cref="ShellIntegration"/> ile uygulanır).</summary>
    public bool SendToEnabled { get; set; } = true;

    /// <summary>Windows ile otomatik başlat (<c>--hidden</c>).</summary>
    public bool AutoStartEnabled { get; set; }

    /// <summary>
    /// Aktarma sunucusu adresi (PROTOCOL §6.3; kendi Cloudflare sunucunuz, <c>relay\README.md</c>): <c>host</c> veya <c>wss://host</c>.
    /// Boş = aktarma yolu kapalı ("yol yok"). Uygulamada ayarlıdır, eşten gelen mesajla değişmez.
    /// </summary>
    public string RelayUrl { get; set; } = "";

    /// <summary>"Arka planda çalışıyor" bildirimi bir kez gösterildi (bir daha gösterilmez).</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>Arayüz dili: <c>system</c> (varsayılan), <c>tr</c> ya da <c>en</c>. Yeniden başlatınca uygulanır.</summary>
    public string Language { get; set; } = "system";

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(path), JsonOptions);
                if (s != null) return s.Sanitized();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save(string? path = null) =>
        Storage.WriteAtomic(path ?? DefaultPath, JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));

    private AppSettings Sanitized()
    {
        if (string.IsNullOrWhiteSpace(DownloadFolder)) DownloadFolder = DefaultDownloadFolder();
        RelayUrl = (RelayUrl ?? "").Trim();
        Language = Language?.Trim().ToLowerInvariant() is "tr" or "en" ? Language.Trim().ToLowerInvariant() : "system";
        if (AutoAcceptMaxBytes < 0) AutoAcceptMaxBytes = DefaultAutoAcceptMaxBytes;
        return this;
    }
}
