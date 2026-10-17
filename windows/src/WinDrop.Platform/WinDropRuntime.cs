using WinDrop.Core.Noise;
using WinDrop.Core.Session;
using WinDrop.Core.Transfer;

namespace WinDrop.Platform;

/// <summary>
/// Platform servislerinin bileşim kökü: kimlik, güvenilen cihazlar, eşleştirme, TCP dinleyici, BLE sunucusu, aktarım koordinatörü.
/// Uygulama (WPF) bunu bir kez kurar; arayüz servisleri <see cref="Coordinator"/> ve <see cref="Pairing"/> üstünden çalışır.
/// </summary>
public sealed class WinDropRuntime : IAsyncDisposable
{
    /// <summary>Devam edilmeyen kısmi aktarım dizinleri bu yaştan sonra silinir (açılışta ve her <see cref="PurgeInterval"/>).</summary>
    public static readonly TimeSpan PartialMaxAge = TimeSpan.FromHours(24);

    private readonly bool _enableBle;
    private readonly int _dataPort;
    private readonly string _partialRoot;
    private readonly CancellationTokenSource _purgeCts = new();
    private Task? _purgeLoop;

    public WinDropRuntime(AppSettings settings, string? rootDirectory = null, int dataPort = DataListener.DefaultPort, bool enableBle = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = rootDirectory ?? Storage.DefaultRoot;
        _enableBle = enableBle;
        _dataPort = dataPort;
        _partialRoot = Path.Combine(root, "partial");
        Settings = settings;
        Identity = new IdentityStore(root);
        Trusted = new TrustedDevices(root);
        HotspotConfig = new HotspotConfigStore(root);
        Hotspot = new HotspotManager(root);
        BluetoothAudio = new BluetoothAudioMonitor();
        Pending = new PendingTransferTable();
        LocalName = Environment.MachineName;
        Pairing = new PairingService(Identity, Trusted, HotspotConfig, LocalName);
        Acceptor = new SignalSessionAcceptor(Identity, Trusted, Pairing);
        Listener = new DataListener(Identity.LoadOrCreate(), Pending, dataPort);
        Coordinator = new TransferCoordinator(new TransferCoordinatorOptions
        {
            Settings = settings,
            Pending = Pending,
            Trusted = Trusted,
            PartialRoot = _partialRoot,
            DataPort = () => Listener.Port,
            LocalName = LocalName,
            Hotspot = Hotspot,
            HotspotConfig = HotspotConfig.GetOrCreate,
            RotateHotspotPassphrase = () => HotspotConfig.RotatePassphrase(),
            BluetoothAudio = BluetoothAudio,
            CommitHook = new MotwCommitHook(),
        });
        Acceptor.SessionHandler = Coordinator.HandleSessionAsync;
    }

    public AppSettings Settings { get; }
    public string LocalName { get; }
    public IdentityStore Identity { get; }
    public TrustedDevices Trusted { get; }
    public HotspotConfigStore HotspotConfig { get; }
    public HotspotManager Hotspot { get; }
    /// <summary>Varsayılan ses çıkışı Bluetooth mu / ses çalıyor mu (<c>btAudio</c>).</summary>
    public BluetoothAudioMonitor BluetoothAudio { get; }
    public PendingTransferTable Pending { get; }
    public PairingService Pairing { get; }
    public SignalSessionAcceptor Acceptor { get; }
    public DataListener Listener { get; }
    public TransferCoordinator Coordinator { get; }
    public BleSignalServer? Ble { get; private set; }

    /// <summary>Eski kısmi dosya temizliği aralığı (varsayılan 6 saat).</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>24 saatten eski kısmi aktarım dizinlerini siler (<see cref="FileSystemPartialStore.PurgeStaleTransfers"/>). Hata atmaz.</summary>
    /// <returns>Silinen aktarım dizini sayısı.</returns>
    public int PurgeStalePartials()
    {
        try
        {
            if (!Directory.Exists(_partialRoot)) return 0;
            var removed = new FileSystemPartialStore(_partialRoot, Settings.DownloadFolder).PurgeStaleTransfers(PartialMaxAge);
            if (removed > 0) PlatformLog.Write($"Eski kısmi aktarım dizini temizlendi: {removed}");
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            PlatformLog.Write($"Kısmi dosya temizliği atlandı: {ex.Message}");
            return 0;
        }
    }

    private async Task PurgeLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PurgeInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) PurgeStalePartials();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>TCP dinleyiciyi ve BLE sunucusunu başlatır. BLE kurulamazsa uygulama yine çalışır (günlüğe yazılır).</summary>
    public async Task StartAsync()
    {
        try { await Hotspot.RecoverAsync(); }   // yedek yoksa hiçbir şey yapmaz
        catch (Exception ex) { PlatformLog.Write($"Etkin nokta kurtarma atlandı: {ex.Message}"); }

        PurgeStalePartials();                                       // açılışta bir kez, sonra her 6 saatte
        _purgeLoop = Task.Run(() => PurgeLoopAsync(_purgeCts.Token));

        try { Listener.Start(); }
        catch (Exception ex) { PlatformLog.Write($"DataListener başlatılamadı (port {_dataPort}): {ex.Message}"); }

        if (_enableBle)
        {
            Ble = new BleSignalServer(Acceptor, LocalName);
            await Ble.StartAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _purgeCts.Cancel();
        if (_purgeLoop is not null) await _purgeLoop.ConfigureAwait(false);
        _purgeCts.Dispose();
        if (Ble is not null) await Ble.DisposeAsync();
        await Coordinator.DisposeAsync();
        BluetoothAudio.Dispose();
        await Listener.DisposeAsync();
        await Hotspot.DisposeAsync();
    }
}
