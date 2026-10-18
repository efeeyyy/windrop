using System.Windows;
using Microsoft.Win32;
using WinDrop.App.Infrastructure;
using WinDrop.App.Services;
using WinDrop.App.ViewModels;
using WinDrop.App.Views;
using WinDrop.Platform;

namespace WinDrop.App;

public partial class App : Application, IAppShell
{
    private readonly AppOptions _options;
    private readonly SingleInstance _instance;
    private AppSettings _settings = new();
    private IDeviceService _devices = null!;
    private ITransferService _transfers = null!;
    private WinDropRuntime? _runtime;
    private MainViewModel _mainVm = null!;
    private MainWindow _main = null!;
    private TrayIcon? _tray;
    private PairingWindow? _pairing;
    private SettingsWindow? _settingsWindow;
    private bool _balloonShown;

    public App(AppOptions options, SingleInstance instance)
    {
        _options = options;
        _instance = instance;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write($"yakalanmamış hata: {args.Exception}");
            args.Handled = true;
        };

        _settings = AppSettings.Load();
        PlatformLog.Sink = AppLog.Write;
        FakeTransferService? fakeTransfers = null;
        if (Environment.GetEnvironmentVariable("WINDROP_FAKE") == "1")
        {
            // Yalnız tasarım/deneme: ağ ve BLE yok.
            var fakeDevices = new FakeDeviceService();
            fakeTransfers = new FakeTransferService(fakeDevices, () => _settings.DownloadFolder);
            _devices = fakeDevices;
            _transfers = fakeTransfers;
            AppLog.Write("WINDROP_FAKE=1: sahte servisler kullanılıyor.");
        }
        else
        {
            _runtime = new WinDropRuntime(_settings);
            _devices = new DeviceService(_runtime);
            _transfers = new TransferService(_runtime);
            _ = StartRuntimeAsync(_runtime);
        }
        _transfers.OfferReceived += OnOfferReceived;
        _transfers.RelayApprovalRequested += OnRelayApprovalRequested;

        _mainVm = new MainViewModel(_devices, _transfers, this);
        _main = new MainWindow(_mainVm);
        _main.HiddenToTray += (_, _) =>
        {
            if (_balloonShown) return;
            _balloonShown = true;
            _tray?.ShowBalloon(Strings.Get("Tray.Background"));
        };

        _tray = new TrayIcon(ShowMain, PickAndStage, ExitApp);

        _instance.StartServer(request => Dispatcher.BeginInvoke(() => HandleRequest(request)));

        if (_options.SendPaths.Count > 0) HandleRequest(_options);
        else if (!_options.Hidden) ShowMain();

        if (fakeTransfers is not null && Environment.GetEnvironmentVariable("WINDROP_FAKE_INCOMING") == "1")
            fakeTransfers.SimulateIncomingOffer();
    }

    private static async Task StartRuntimeAsync(WinDropRuntime runtime)
    {
        try { await runtime.StartAsync(); }
        catch (Exception ex) { AppLog.Write($"platform servisleri başlatılamadı: {ex}"); }
    }

    private void HandleRequest(AppOptions request)
    {
        if (request.SendPaths.Count > 0)
        {
            AppLog.Write($"gönderim isteği işleniyor: {request.SendPaths.Count} yol ({string.Join("; ", request.SendPaths)})");
            _mainVm.StageFiles(request.SendPaths);
        }
        ShowMain();
    }

    public void ShowMain()
    {
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void PickAndStage()
    {
        ShowMain();
        _mainVm.StageFiles(PickFiles());
    }

    private void ExitApp()
    {
        _main.AllowClose = true;
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_runtime is not null)
        {
            try { Task.Run(() => _runtime.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(8)); }
            catch (Exception ex) { AppLog.Write($"kapanış hatası: {ex.Message}"); }
        }
        base.OnExit(e);
    }

    // ---- IAppShell ----

    public void OpenPairing()
    {
        if (_pairing != null) { _pairing.Activate(); return; }
        var window = new PairingWindow(_devices);
        if (_main.IsVisible) window.Owner = _main;
        window.Closed += (_, _) => _pairing = null;
        _pairing = window;
        window.Show();
    }

    public void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        var shell = new ShellIntegration(Environment.ProcessPath ?? throw new InvalidOperationException("Exe yolu yok."));
        var window = new SettingsWindow(new SettingsViewModel(_settings, shell, _devices));
        if (_main.IsVisible) window.Owner = _main;
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show();
    }

    public IReadOnlyList<string> PickFiles()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = Strings.Get("Dialog.PickFiles") };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public IReadOnlyList<string> PickFolder()
    {
        var dialog = new OpenFolderDialog { Multiselect = true, Title = Strings.Get("Dialog.PickSendFolder") };
        return dialog.ShowDialog() == true ? dialog.FolderNames : [];
    }

    // ---- Gelen teklif ----

    private void OnRelayApprovalRequested(object? sender, RelayApproval approval)
    {
        AppLog.Write($"aktarma sunucusu onayı isteniyor: {approval.DeviceName}, {approval.TotalBytes} bayt");
        var window = new RelayApprovalWindow(approval, allow => _transfers.RespondToRelay(approval, allow));
        window.Show();
    }

    private void OnOfferReceived(object? sender, IncomingOffer offer)
    {
        AppLog.Write($"gelen teklif: {offer.SenderName}, {offer.FileCount} dosya, {offer.TotalBytes} bayt");
        // Otomatik kabul kararı TransferCoordinator'da verilir (yalnız QR ile eşli cihaz, PROTOCOL §7/§2b);
        // bu olay yalnız kullanıcı onayı gerektiğinde gelir — burada kabul etmek onayı atlardı.
        var window = new IncomingOfferWindow(offer, accept => _transfers.RespondToOffer(offer, accept));
        window.Show();
    }
}
