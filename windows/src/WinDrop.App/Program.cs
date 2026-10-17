using WinDrop.App.Infrastructure;

namespace WinDrop.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
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
        return code;
    }
}
