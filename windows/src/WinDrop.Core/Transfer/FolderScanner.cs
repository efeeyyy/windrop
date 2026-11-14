using WinDrop.Core.Protocol;

namespace WinDrop.Core.Transfer;

/// <summary>Gönderilecek klasör taranırken anlaşılır (kullanıcıya gösterilebilir) hata: sınır aşımı, okunamayan yol.</summary>
public enum FolderScanError { Other, TooManyFiles, TooDeep, PathTooLong }

/// <param name="Kind">Arayüzün kendi dilinde metin üretebilmesi için hata türü.</param>
public sealed class FolderScanException(string message, FolderScanError kind = FolderScanError.Other, int limit = 0, string? component = null)
    : Exception(message)
{
    public FolderScanError Kind { get; } = kind;
    public int Limit { get; } = limit;
    public string? Component { get; } = component;
}

/// <summary>
/// PROTOCOL §5a gönderen: verilen yollar arasındaki klasörleri özyinelemeli tarar. Symlink/junction (reparse point) izlenmez
/// (içerideki bağlantılar atlanır ve loglanır; kullanıcının açıkça seçtiği kök klasör izlenir). Gizli/sistem dosyaları gönderilir;
/// erişilemeyen dosya/klasör atlanır ve loglanır. <c>dir</c> = seçilen klasörün adı + göreli alt yol ('/' ayırıcı).
/// Sınırlar (≤ 16 seviye, ≤ 1024 karakter, ≤ <see cref="TransferLimits.MaxFiles"/> dosya) gönderimden önce <see cref="FolderScanException"/> olur.
/// </summary>
public static class FolderScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,            // gizli ve sistem dosyaları da gelsin
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Düz dosyalar (dir=null) ve klasörler karışık olabilir; sıra: verilen yol sırası, klasör içinde ada göre.</summary>
    public static IReadOnlyList<IFileSource> Collect(IEnumerable<string> paths, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = new List<IFileSource>();
        var usedTopNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var top = UniqueTopName(TopName(path), usedTopNames);
                Walk(new DirectoryInfo(path), [top], result, log);
            }
            else
            {
                result.Add(new FileSystemFileSource(path));
            }
            if (result.Count > TransferLimits.MaxFiles)
                throw new FolderScanException($"En çok {TransferLimits.MaxFiles} dosya gönderilebilir.", FolderScanError.TooManyFiles, TransferLimits.MaxFiles);
        }
        return result;
    }

    private static void Walk(DirectoryInfo dir, List<string> components, List<IFileSource> result, Action<string>? log)
    {
        if (!RelativeDirectory.TryJoin(components, out var dirString))
            throw components.Count > RelativeDirectory.MaxDepth
                ? new FolderScanException($"Klasör en çok {RelativeDirectory.MaxDepth} seviye derinlikte olabilir: {components[0]}",
                    FolderScanError.TooDeep, RelativeDirectory.MaxDepth, components[0])
                : new FolderScanException($"Klasör yolu en çok {RelativeDirectory.MaxLength} karakter olabilir: {components[0]}",
                    FolderScanError.PathTooLong, RelativeDirectory.MaxLength, components[0]);

        List<FileSystemInfo> entries;
        try
        {
            entries = dir.EnumerateFileSystemInfos("*", Options).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"klasör okunamadı, atlandı: {dir.FullName} ({ex.GetType().Name})");
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.LinkTarget is not null)   // yalnız symlink/junction; OneDrive yer tutucuları (bulut reparse) atlanmaz
            {
                log?.Invoke($"bağlantı (symlink/junction) izlenmedi: {entry.FullName}");
                continue;
            }
            if (entry is DirectoryInfo sub)
            {
                components.Add(sub.Name);
                Walk(sub, components, result, log);
                components.RemoveAt(components.Count - 1);
            }
            else
            {
                try
                {
                    using (File.Open(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }   // okunabilir mi?
                    result.Add(new FileSystemFileSource(entry.FullName, dirString));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"dosya okunamadı, atlandı: {entry.FullName} ({ex.GetType().Name})");
                }
            }
            if (result.Count > TransferLimits.MaxFiles)
                throw new FolderScanException($"En çok {TransferLimits.MaxFiles} dosya gönderilebilir.", FolderScanError.TooManyFiles, TransferLimits.MaxFiles);
        }
    }

    private static string TopName(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var name = Path.GetFileName(full);
        if (name.Length > 0) return name;
        // Sürücü kökü ("C:\"): ad olarak sürücü harfi.
        var letters = new string(full.Where(char.IsLetterOrDigit).ToArray());
        return letters.Length > 0 ? "Drive_" + letters : "Folder";
    }

    /// <summary>İki farklı klasör aynı adı taşıyorsa ("Fotoğraflar" iki ayrı yerden) alıcıda karışmasın: "ad (2)".</summary>
    private static string UniqueTopName(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++) candidate = $"{name} ({n})";
        return candidate;
    }
}
