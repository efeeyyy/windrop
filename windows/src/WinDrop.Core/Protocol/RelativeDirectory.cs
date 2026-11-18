namespace WinDrop.Core.Protocol;

/// <summary>
/// PROTOCOL §6.7 <c>dir</c> alanı: gönderilen kökten göreli klasör yolu, ayırıcı <c>/</c>, en çok 16 seviye, toplam ≤ 1024 karakter.
/// Alıcı her bileşeni ayrı ayrı §6.6 ad kurallarıyla temizler; <c>.</c>, <c>..</c>, boş bileşen, <c>\</c> ve mutlak yol
/// (başta <c>/</c> veya sürücü harfi) → <see cref="ProtocolException"/>.
/// </summary>
public static class RelativeDirectory
{
    public const int MaxDepth = 16;
    public const int MaxLength = 1024;
    public const char Separator = '/';

    /// <summary>Alıcı: <paramref name="dir"/> null ise null (kök). Aksi halde temizlenmiş bileşenler; geçersizse <see cref="ProtocolException"/>.</summary>
    public static IReadOnlyList<string>? Parse(string? dir)
    {
        if (dir is null) return null;
        if (dir.Length > MaxLength) throw new ProtocolException($"dir {MaxLength} karakteri aşıyor ({dir.Length}).");
        var parts = dir.Split(Separator);
        if (parts.Length > MaxDepth) throw new ProtocolException($"dir {MaxDepth} seviyeden derin ({parts.Length}).");
        var result = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 0) throw new ProtocolException("dir boş bileşen içeriyor.");
            if (p is "." or "..") throw new ProtocolException("dir '.' veya '..' içeremez.");
            if (p.Contains('\\')) throw new ProtocolException("dir ters bölü içeremez.");
            if (p.Length == 2 && char.IsAsciiLetter(p[0]) && p[1] == ':') throw new ProtocolException("dir mutlak yol olamaz.");
            result[i] = FileNameSanitizer.Sanitize(p);
        }
        return result;
    }

    /// <summary>Gönderen: ham bileşenlerden <c>dir</c> dizesi üretir; sınırlar aşılırsa false.</summary>
    public static bool TryJoin(IReadOnlyList<string> components, out string dir)
    {
        dir = string.Join(Separator, components);
        return components.Count is >= 1 and <= MaxDepth && dir.Length <= MaxLength;
    }
}
