using System.Security.AccessControl;
using System.Text;

namespace WinDrop.Platform;

/// <summary>
/// Dayanıklı dosya günlüğü: her satırda aç-yaz-kapat (<see cref="FileShare.ReadWrite"/>|<see cref="FileShare.Delete"/>),
/// geçici kilit/hata sonrası kısa yeniden deneme, hata hiçbir zaman günlüğü kalıcı kapatmaz.
/// <see cref="MaxBytes"/> aşılınca <c>name.1.ext</c>'e döndürülür. Hata fırlatmaz.
/// </summary>
public sealed class RollingFileLog
{
    private readonly object _gate = new();
    private readonly string _path;

    public RollingFileLog(string path, long maxBytes = 5 * 1024 * 1024)
    {
        _path = path;
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }

    public string RotatedPath =>
        Path.Combine(Path.GetDirectoryName(_path)!, Path.GetFileNameWithoutExtension(_path) + ".1" + Path.GetExtension(_path));

    public void Write(string line)
    {
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine);
            lock (_gate)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                        Rotate();
                        using var fs = new FileInfo(_path).Create(FileMode.OpenOrCreate, FileSystemRights.AppendData,
                            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None, null);
                        fs.Write(bytes);
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Thread.Sleep(20 * (attempt + 1));
                    }
                }
            }
        }
        catch (Exception) { /* günlük asla akışı bozmaz */ }
    }

    private void Rotate()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes) return;
        try { File.Move(_path, RotatedPath, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* başka yazar döndürdü/kilitli: sonraki satırda yeniden dene */ }
    }
}
