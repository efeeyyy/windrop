using WinDrop.App.Infrastructure;
using WinDrop.Platform;

namespace WinDrop.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        AppLanguage.Apply(AppSettings.Load().Language);
        AppLanguage.ApplyToWpf();
        using var instance = new SingleInstance();
        if (!instance.IsFirst)
        {
            AppLog.Write($"ikinci örnek: --send {options.SendPaths.Count} yol");
            return SingleInstance.Forward(options) ? 0 : 1;
        }

        AppLog.Write($"başlatıldı (hidden={options.Hidden}, send={options.SendPaths.Count})");
        var app = new App(options, instance);
        app.InitializeComponent();
        int code = app.Run();
        AppLog.Write("çıkıldı");
        if (app.RestartRequested) RestartProcess(instance);
        return code;
    }

    /// <summary>Tek örnek kilidini bırakır, sonra aynı exe'yi (argümansız) yeniden başlatır.</summary>
    private static void RestartProcess(SingleInstance instance)
    {
        instance.Dispose();
        try
        {
            if (Environment.ProcessPath is { } exe)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"yeniden başlatılamadı: {ex.Message}");
        }
    }
}
