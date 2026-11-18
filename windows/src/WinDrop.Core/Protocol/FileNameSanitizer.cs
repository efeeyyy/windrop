using System.Text;

namespace WinDrop.Core.Protocol;

/// <summary>Hedef klasörde dosya adı var mı? Dosya sistemi erişimini soyutlar (üretimde klasöre bağlı uygulama, testte sahte).</summary>
public interface IFileNameExistence
{
    /// <param name="fileName">Yalnız dosya adı (yol değil); karşılaştırma dosya sisteminin kuralına (NTFS: büyük/küçük harf duyarsız) bağlıdır.</param>
    bool Exists(string fileName);
}

/// <summary>
/// PROTOCOL §6.6 "Dosya adı (alıcı)" kuralları: yalnız son yol bileşeni; <c>..</c>, <c>/</c>, <c>\</c>,
/// <c>:</c> (NTFS akışı), denetim karakterleri ve Windows ayrılmış adları reddedilmez, <c>_</c> ile
/// değiştirilir/öne <c>_</c> eklenir; sondaki nokta/boşluk silinir; üzerine yazılmaz ("ad (2).ext").
/// <para>PROTOCOL §6.6: Spesifikasyon "reddedilir veya değiştirilir" der; burada her zaman değiştirilir (adı bozuk
/// dosya yine kaydedilebilsin). Sonuç hiçbir girdide boş olmaz (en kötü ihtimalle <see cref="FallbackName"/>).</para>
/// <para>PROTOCOL §6.6: Spesifikasyonda olmayan ek sertleştirmeler: Windows'ta geçersiz <c>&lt; &gt; " | ? *</c>, C1 denetim
/// karakterleri, çift yönlü metin denetimleri (U+202A–E, U+2066–9, LRM/RLM/ALM; ".exe" sahteciliği) ve eşleşmemiş
/// vekil (surrogate) karakterleri <c>_</c> olur; COM¹/²/³, CONIN$, CONOUT$ da ayrılmış sayılır; uzunluk sınırı
/// 255 UTF-16 kod birimidir (NTFS), uzantı (≤ 20 karakter ise) korunur.</para>
/// </summary>
public static class FileNameSanitizer
{
    public const int MaxLength = 255;
    public const string FallbackName = "file";
    private const int MaxPreservedExtension = 20;
    private const int MaxUniqueAttempts = 10_000;

    public static string Sanitize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return FallbackName;

        // 1) Yalnız son yol bileşeni: önce sondaki ayırıcıları at, sonra son ayırıcıdan sonrasını al.
        var trimmed = name.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        var component = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;

        // 2) Yasak/tehlikeli karakterleri '_' yap.
        var sb = new StringBuilder(component.Length);
        for (var i = 0; i < component.Length; i++)
        {
            var c = component[i];
            if (char.IsHighSurrogate(c) && i + 1 < component.Length && char.IsLowSurrogate(component[i + 1]))
            {
                sb.Append(c).Append(component[++i]);
                continue;
            }
            sb.Append(IsForbidden(c) ? '_' : c);
        }

        // 3) Sondaki nokta/boşluk; 4) uzunluk; 5) ayrılmış ad.
        var result = TrimTrailing(sb.ToString());
        if (result.Length == 0) return FallbackName;

        result = Truncate(result);
        if (result.Length == 0) return FallbackName;

        if (IsReserved(result))
        {
            result = Truncate("_" + result);
        }
        return result;
    }

    /// <summary>Var olan bir dosyanın üzerine yazmamak için "ad (2).ext", "ad (3).ext"… üretir.</summary>
    /// <param name="sanitizedName"><see cref="Sanitize"/> çıktısı.</param>
    public static string MakeUnique(string sanitizedName, IFileNameExistence fileSystem)
    {
        ArgumentException.ThrowIfNullOrEmpty(sanitizedName);
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (!fileSystem.Exists(sanitizedName)) return sanitizedName;

        var (stem, ext) = SplitExtension(sanitizedName);
        for (var n = 2; n <= MaxUniqueAttempts; n++)
        {
            var suffix = $" ({n})";
            var room = MaxLength - suffix.Length - ext.Length;
            var candidateStem = TrimSurrogate(stem.Length > room ? stem[..room] : stem);
            var candidate = candidateStem + suffix + ext;
            if (!fileSystem.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException($"'{sanitizedName}' için benzersiz ad bulunamadı.");
    }

    /// <summary>Sanitize + MakeUnique.</summary>
    public static string SanitizeAndMakeUnique(string? name, IFileNameExistence fileSystem) =>
        MakeUnique(Sanitize(name), fileSystem);

    private static bool IsForbidden(char c) =>
        c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F)            // denetim (C0, DEL, C1)
        || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '/' or '\\'
        || c is '‎' or '‏' or '؜'                        // LRM, RLM, ALM
        || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩')  // çift yönlü gömme/üzerine yazma/izolasyon
        || char.IsSurrogate(c);                                         // eşleşmemiş vekil (eşleşenler yukarıda geçirildi)

    private static string TrimTrailing(string s) => s.TrimEnd('.', ' ');

    private static (string Stem, string Ext) SplitExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? (name[..dot], name[dot..]) : (name, "");
    }

    private static string Truncate(string name)
    {
        if (name.Length <= MaxLength) return name;
        var (stem, ext) = SplitExtension(name);
        if (ext.Length > MaxPreservedExtension) { stem = name; ext = ""; }
        var room = MaxLength - ext.Length;
        var cut = TrimSurrogate(stem[..room]);
        return TrimTrailing(cut) + ext;
    }

    private static string TrimSurrogate(string s) =>
        s.Length > 0 && char.IsHighSurrogate(s[^1]) ? s[..^1] : s;

    private static bool IsReserved(string name)
    {
        var dot = name.IndexOf('.');
        var stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ');
        if (stem.Length is < 3 or > 7) return false;   // en uzun: "CONOUT$" (7)
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;
        if (stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            var d = stem[3];
            return d is >= '1' and <= '9' or '¹' or '²' or '³';   // 1–9 ve ¹ ² ³
        }
        return false;
    }
}
