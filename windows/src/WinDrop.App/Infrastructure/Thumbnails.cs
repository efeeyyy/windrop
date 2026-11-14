using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace WinDrop.App.Infrastructure;

/// <summary>Aktarım satırı önizlemesi: Shell küçük resmi, olmazsa dosya türü/klasör simgesi. Arka planda üretilir, yol başına önbelleğe alınır.</summary>
public static class Thumbnails
{
    private const uint ThumbSize = 96;   // 48 dip @ 200% DPI'ya yeter
    private const int MaxEntries = 200;   // 96x96 BGRA ≈ 37 KB: en çok ~7 MB
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> Order = new();

    /// <summary>Başarısız/boş sonuç (dosya henüz yazılmadı, geçici hata) önbellekte kalmaz; sonraki istek yeniden dener.</summary>
    public static async Task<ImageSource?> GetAsync(string path)
    {
        var task = Cache.GetOrAdd(path, p =>
        {
            Order.Enqueue(p);
            while (Cache.Count > MaxEntries && Order.TryDequeue(out var old)) Cache.TryRemove(old, out _);
            return Task.Run(() => Load(p));
        });
        ImageSource? result = null;
        try { result = await task; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Write($"küçük resim hatası ({ex.GetType().Name}): {path}"); }
        if (result is null) Cache.TryRemove(new KeyValuePair<string, Task<ImageSource?>>(path, task));
        return result;
    }

    private static async Task<ImageSource?> Load(string path)
    {
        try
        {
            bool isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) return null;
            try
            {
                IStorageItemProperties item = isDir
                    ? await StorageFolder.GetFolderFromPathAsync(path)
                    : await StorageFile.GetFileFromPathAsync(path);
                using var thumb = await item.GetThumbnailAsync(ThumbnailMode.SingleItem, ThumbSize);
                if (thumb is { Size: > 0 })
                {
                    using var stream = thumb.AsStreamForRead();
                    var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    ms.Position = 0;
                    var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    frame.Freeze();
                    return frame;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write($"küçük resim alınamadı ({ex.GetType().Name}): {path}");
            }
            return ShellIcon(path, isDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        {
            return null;   // COMException da ExternalException'dır
        }
    }

    private static ImageSource? ShellIcon(string path, bool isDir)
    {
        var info = new ShFileInfo();
        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0, SHGFI_USEFILEATTRIBUTES = 0x10;
        uint attrs = isDir ? 0x10u : 0x80u;
        if (SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES) == 0
            || info.hIcon == 0) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DestroyIcon(info.hIcon); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attrs, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);
}
