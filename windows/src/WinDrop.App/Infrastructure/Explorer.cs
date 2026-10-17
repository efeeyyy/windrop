using System.Diagnostics;

namespace WinDrop.App.Infrastructure;

public static class Explorer
{
    /// <summary>Dosyayı seçili olarak, yoksa klasörü açar.</summary>
    public static void Reveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path } });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path } });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"explorer açılamadı: {ex.Message}");
        }
    }
}
