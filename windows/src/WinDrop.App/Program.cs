using WinDrop.App.Infrastructure;
using WinDrop.Platform;

namespace WinDrop.App;

public static class Program
{
    /// <summary>Başlangıçta okunan ayarlar (App aynı nesneyi kullanır; dosya iki kez okunup bozuk yedeği kaçırılmasın).</summary>
    public static AppSettings? Settings { get; private set; }

    /// <summary>Ayar dosyası bozuk/okunamadıysa kullanıcıya gösterilecek günlük metni; yoksa null.</summary>
    public static string? SettingsProblem { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Arayüz iş parçacığı dışındaki hatalar da günlüğe düşsün (başlangıç hatası sessiz çökme olmasın).
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write($"yakalanmamış hata (süreç): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write($"gözlenmeyen görev hatası: {e.Exception}");
            e.SetObserved();
        };
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            AppLog.Write($"başlangıç hatası: {ex}");
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        var options = AppOptions.Parse(args);
        Settings = AppSettings.Load(onProblem: msg => { SettingsProblem = msg; AppLog.Write(msg); });
        AppLanguage.Apply(Settings.Language);
        AppLanguage.ApplyToWpf();
        using var instance = new SingleInstance();
        if (!instance.IsFirst)
        {
            AppLog.Write($"ikinci örnek: hidden={options.Hidden}, quit={options.Quit}, --send {options.SendPaths.Count} yol");
            return SingleInstance.Forward(options) ? 0 : 1;
        }
        if (options.Quit) return 0;   // çalışan örnek yok: yapılacak bir şey yok

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
