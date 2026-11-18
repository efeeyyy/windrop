using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
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

    /// <summary>Dil değişikliği için "Şimdi yeniden başlat": çıkıştan sonra <see cref="Program"/> yeni süreci başlatır.</summary>
    public bool RestartRequested { get; private set; }

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
            // Komut sessizce başarısız olmasın: kalıcı bantta göster (günlük açılabilir).
            _mainVm?.ShowNotice(Strings.Get("Main.UnexpectedError"));
        };
        SessionEnding += (_, _) => { if (_main is not null) _main.AllowClose = true; };   // oturum kapanırken "kapanmayı engelliyor" çıkmasın
        ApplyThemeColors();

        _settings = Program.Settings ?? AppSettings.Load(onProblem: AppLog.Write);
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
        _transfers.Transfers.CollectionChanged += OnTransfersChanged;

        _mainVm = new MainViewModel(_devices, _transfers, this);
        _main = new MainWindow(_mainVm);
        _main.HiddenToTray += (_, _) =>
        {
            if (_balloonShown || _settings.TrayHintShown) return;
            _balloonShown = true;
            _tray?.ShowBalloon(Strings.Get("Tray.Background"));
            _settings.TrayHintShown = true;   // yalnız ilk seferde; kalıcı
            try { _settings.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Write($"ayar kaydedilemedi: {ex.Message}"); }
        };

        _tray = new TrayIcon(ShowMain, PickAndStage, () => ExitApp(confirm: true));

        _instance.StartServer(request => Dispatcher.BeginInvoke(() => HandleRequest(request)));

        if (Program.SettingsProblem is not null) _mainVm.ShowNotice(Strings.Get("Main.SettingsProblem"));

        if (_options.SendPaths.Count > 0) HandleRequest(_options);
        else if (!_options.Hidden) ShowMain();

        if (fakeTransfers is not null && Environment.GetEnvironmentVariable("WINDROP_FAKE_INCOMING") == "1")
            fakeTransfers.SimulateIncomingOffer();
    }

    /// <summary>Hata rengi temaya göre: açık temada koyu kırmızı (AA kontrast), koyu temada açık kırmızı.</summary>
    private void ApplyThemeColors()
    {
        bool light = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("AppsUseLightTheme") is not 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        Resources["ErrorBrush"] = new SolidColorBrush(light ? Color.FromRgb(0xC4, 0x2B, 0x1C) : Color.FromRgb(0xFF, 0x99, 0xA4));
    }

    /// <summary>Platform servisleri (BLE, TCP, etkin nokta) başlayamazsa kullanıcıya kalıcı bantla bildirir.</summary>
    private async Task StartRuntimeAsync(WinDropRuntime runtime)
    {
        try { await runtime.StartAsync(); }
        catch (Exception ex)
        {
            AppLog.Write($"platform servisleri başlatılamadı: {ex}");
            _ = Dispatcher.BeginInvoke(() =>
                _mainVm?.ShowNotice(Strings.Format("Main.ServiceFailed", ex.Message), canRestart: true));
        }
    }

    private void HandleRequest(AppOptions request)
    {
        if (request.Quit)
        {
            AppLog.Write("kapatma isteği alındı (--quit).");
            ExitApp(confirm: false);   // kurulum/kaldırma betiği: onay sorulmaz
            return;
        }
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

    public void RestartApp()
    {
        if (!ConfirmQuitWithActiveTransfers()) return;
        RestartRequested = true;
        ExitApp(confirm: false);
    }

    /// <summary>Devam eden aktarım varsa çıkmadan önce (uygulama dilinde) sorar.</summary>
    private bool ConfirmQuitWithActiveTransfers()
    {
        if (!_transfers.Transfers.Any(t => t.Status is TransferStatus.Waiting or TransferStatus.Active)) return true;
        return ConfirmDialog.Ask(_main, Strings.Get("Tray.ExitConfirm"), Strings.Get("Dialog.Yes"), Strings.Get("Dialog.No"));
    }

    private void ExitApp(bool confirm)
    {
        if (confirm && !ConfirmQuitWithActiveTransfers()) return;
        _main.AllowClose = true;
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        // Platform kapanırken (en çok 8 sn) pencereler donuk kalmasın: önce gizle.
        foreach (Window w in Windows.OfType<Window>().ToArray()) w.Hide();
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
        if (_pairing != null) { Restore(_pairing); return; }
        var window = new PairingWindow(_devices);
        if (_main.IsVisible) window.Owner = _main;
        window.Closed += (_, _) => _pairing = null;
        _pairing = window;
        window.Show();
    }

    public void OpenSettings()
    {
        if (_settingsWindow != null) { Restore(_settingsWindow); return; }
        // Ayrı deneme örneği (WINDROP_INSTANCE) gerçek SendTo kısayoluna / Run anahtarına dokunmaz.
        ShellIntegration? shell = Environment.GetEnvironmentVariable("WINDROP_INSTANCE") is { Length: > 0 }
            ? null
            : new ShellIntegration(Environment.ProcessPath ?? throw new InvalidOperationException("Exe yolu yok."));
        var window = new SettingsWindow(new SettingsViewModel(_settings, shell, _devices, RestartApp));
        if (_main.IsVisible) window.Owner = _main;
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show();
    }

    private static void Restore(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void OpenLog() => Explorer.Reveal(AppLog.FilePath);

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

    // ---- Alınan dosya bildirimi ----

    private void OnTransfersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null) return;
        foreach (TransferItem item in e.NewItems)
        {
            if (item.Direction != TransferDirection.Receive) continue;
            bool notified = false;
            item.PropertyChanged += (_, args) =>
            {
                if (notified || args.PropertyName != nameof(TransferItem.Status) || item.Status != TransferStatus.Completed) return;
                notified = true;
                // Pencere görünür/öndeyken satır zaten görünür; gizliyken (tepside) bildirim ver.
                if (_main.IsVisible && _main.IsActive) return;
                var text = Strings.Plural("Tray.Received", item.FileCount, item.DeviceName, item.FileCount);
                var path = item.SavedPath;
                _tray?.ShowBalloon(text, () => { if (!string.IsNullOrEmpty(path)) Explorer.Reveal(path); else ShowMain(); });
            };
        }
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
        // Otomatik kabul kararı TransferCoordinator'da verilir (yalnız QR ile eşli cihaz, PROTOCOL §8/§3.1);
        // bu olay yalnız kullanıcı onayı gerektiğinde gelir — burada kabul etmek onayı atlardı.
        var window = new IncomingOfferWindow(offer, accept => _transfers.RespondToOffer(offer, accept));
        window.Show();
    }
}
