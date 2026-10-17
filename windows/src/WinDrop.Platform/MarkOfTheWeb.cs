using System.Text;

namespace WinDrop.Platform;

/// <summary>Alınan dosyaya <c>Zone.Identifier</c> NTFS akışı (ZoneId=3, İnternet bölgesi) yazar (PROTOCOL §5).</summary>
public static class MarkOfTheWeb
{
    public const string StreamName = "Zone.Identifier";
    public const string Content = "[ZoneTransfer]\r\nZoneId=3\r\n";

    /// <summary>İşaretler; NTFS olmayan birimlerde (ADS yok) veya yetki yoksa false döner, atmaz.</summary>
    public static bool TryMark(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return false;
            // .NET Core, "yol:akış" biçimini NTFS'te doğrudan açar.
            File.WriteAllText(filePath + ":" + StreamName, Content, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Akışın içeriğini okur (yoksa null); test ve tanılama için.</summary>
    public static string? ReadMark(string filePath)
    {
        try { return File.ReadAllText(filePath + ":" + StreamName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }
}
