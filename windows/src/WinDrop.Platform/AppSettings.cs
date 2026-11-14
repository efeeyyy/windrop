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

    /// <summary>
    /// Okur. UTF-8 BOM'a (Not Defteri, PowerShell 5.1) toleranslıdır. Dosya bozuksa <c>.corrupt</c> olarak yedeklenir
    /// (üzerine yazılmaz) ve varsayılanlar döner; <paramref name="onProblem"/> ile bildirilir.
    /// </summary>
    public static AppSettings Load(string? path = null, Action<string>? onProblem = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var bytes = File.ReadAllBytes(path);
            var span = bytes.AsSpan();
            if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) span = span[3..];
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(span, JsonOptions);
                if (s != null) return s.Sanitized();
                BackupCorrupt(path, "boş (null)", onProblem);
            }
            catch (JsonException ex)
            {
                BackupCorrupt(path, ex.Message, onProblem);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onProblem?.Invoke($"ayar dosyası okunamadı: {ex.Message}");
        }
        return new AppSettings();
    }

    private static void BackupCorrupt(string path, string reason, Action<string>? onProblem)
    {
        try
        {
            var backup = path + ".corrupt";
            File.Move(path, backup, overwrite: true);
            onProblem?.Invoke($"ayar dosyası bozuk ({reason}); '{backup}' olarak yedeklendi, varsayılanlar kullanılıyor.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onProblem?.Invoke($"ayar dosyası bozuk ({reason}) ve yedeklenemedi: {ex.Message}");
        }
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
