using System.Security.Cryptography;

namespace WinDrop.Platform;

/// <summary>Kök dizin ve DPAPI + atomik yazma yardımcıları (IdentityStore, TrustedDevices, HotspotConfigStore ortak).</summary>
public static class Storage
{
    /// <summary>Varsayılan kök: <c>%LOCALAPPDATA%\WinDrop</c>.</summary>
    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDrop");

    /// <summary>DPAPI CurrentUser ile korur; <paramref name="purpose"/> entropy olarak kullanılır (dosyalar birbirinin yerine geçmesin).</summary>
    public static byte[] Protect(byte[] plain, string purpose) =>
        ProtectedData.Protect(plain, Entropy(purpose), DataProtectionScope.CurrentUser);

    /// <exception cref="CryptographicException">Veri bozuk veya başka kullanıcıya ait.</exception>
    public static byte[] Unprotect(byte[] protectedData, string purpose) =>
        ProtectedData.Unprotect(protectedData, Entropy(purpose), DataProtectionScope.CurrentUser);

    private static byte[] Entropy(string purpose) => System.Text.Encoding.UTF8.GetBytes("WinDrop/" + purpose);

    /// <summary>Geçici dosyaya yazıp yerine koyar (yarım dosya kalmaz).</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null);
            else
                File.Move(tmp, path);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }
}
