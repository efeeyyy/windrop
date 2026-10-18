using WinDrop.Platform;

namespace WinDrop.App.Infrastructure;

/// <summary>Basit dosya günlüğü: <c>%LOCALAPPDATA%\WinDrop\logs\app.log</c> (5 MB'ta <c>app.1.log</c>'a döner). Hata fırlatmaz.</summary>
public static class AppLog
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDrop", "logs", "app.log");

    private static readonly RollingFileLog Log = new(FilePath);

    public static void Write(string message) =>
        Log.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}");
}
