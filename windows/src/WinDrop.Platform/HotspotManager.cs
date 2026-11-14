using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace WinDrop.Platform;

/// <summary>Etkin nokta başlatılamadı/yapılandırılamadı; <see cref="Exception.Message"/> kullanıcıya gösterilebilir (Türkçe).</summary>
public sealed class HotspotException : Exception
{
    public HotspotException(string message, Exception? inner = null) : base(message, inner) => Error = UserError.Raw(message);

    public HotspotException(UserError error, Exception? inner = null) : base(error.ToString(), inner) => Error = error;

    /// <summary>Arayüz dilinde gösterilecek yapılandırılmış neden.</summary>
    public UserError Error { get; }
}

/// <summary>Etkin nokta ayarı (sistem genelindeki kullanıcı ayarının anlık görüntüsü). Parola yazılmaz.</summary>
public sealed record HotspotSnapshot(string Ssid, string Passphrase, TetheringWiFiBand Band)
{
    public override string ToString() => $"Ssid = {Ssid}, Band = {Band}, Passphrase = ***";
}

/// <summary>
/// Mobil etkin nokta (PROTOCOL §6.2). <see cref="StartAsync"/> kullanıcının mevcut ayarını kaydeder, WinDrop SSID/parolasını
/// (2,4 GHz tercih) yazar ve başlatır; <see cref="StopAsync"/> durdurur ve eski ayarı geri yükler. Kullanıcı etkin noktayı
/// zaten açmışsa hiçbir şeye dokunulmaz (<c>false</c>). Yedek <c>hotspot-backup.bin</c> (DPAPI) çökme sonrası
/// <see cref="RecoverAsync"/> ile geri yüklemeyi sağlar.
/// </summary>
public sealed class HotspotManager : IAsyncDisposable, IHotspotController
{
    private const string Purpose = "hotspot-backup.v1";
    private static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(30);

    private readonly string _backupPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _timerLock = new();
    private Timer? _idleTimer;
    private int _activeTransfers;
    private bool _owned;
    private HotspotConfig? _activeConfig;   // WinDrop'un açtığı etkin noktanın ayarı (parola yenilenince yeniden başlatmak için)
    private bool _disposed;

    public HotspotManager(string? rootDirectory = null, TimeSpan? idleTimeout = null)
    {
        _backupPath = Path.Combine(rootDirectory ?? Storage.DefaultRoot, "hotspot-backup.bin");
        IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>Son aktarımdan sonra etkin noktanın kapanacağı süre (varsayılan 2 dk).</summary>
    public TimeSpan IdleTimeout { get; }

    /// <summary>WinDrop etkin noktayı açtı ve henüz kapatmadı.</summary>
    public bool IsOwned => _owned;

    /// <summary>Gerçekte geçerli WinDrop etkin nokta ayarı (açık değilse null); <c>hotspot</c> iletisi bunu taşımalı.</summary>
    public HotspotConfig? CurrentConfig => _owned ? _activeConfig : null;

    /// <summary>
    /// Bilinmeyen eşe verilen parola yenilendi: başka aktarım yoksa etkin nokta hemen kapatılır (eski parolayla boşta açık kalmasın).
    /// </summary>
    public void PassphraseRotated()
    {
        bool idle;
        lock (_timerLock) idle = _activeTransfers == 0 && _owned;
        if (!idle) return;
        _ = Task.Run(async () =>
        {
            try { await StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { PlatformLog.Write($"Etkin nokta kapatılamadı: {ex.Message}"); }
        });
    }

    /// <summary>Etkin nokta boşta süresi dolduğu için kapandığında (başarılı/başarısız).</summary>
    public event Action<Exception?>? IdleStopped;

    // ---- Salt-okur ----

    /// <summary>Kullanıcının mevcut etkin nokta ayarı; profil/yetenek yoksa <see cref="HotspotException"/>.</summary>
    public HotspotSnapshot ReadCurrentConfiguration() => Snapshot(GetManager().GetCurrentAccessPointConfiguration());

    public TetheringOperationalState GetOperationalState() => GetManager().TetheringOperationalState;

    public uint GetClientCount() => GetManager().ClientCount;

    /// <summary>Bu PC'de (ilk bağlantı profiline göre) 2,4 GHz bandı destekleniyor mu.</summary>
    public bool IsTwoPointFourSupported() =>
        GetManager().GetCurrentAccessPointConfiguration().IsBandSupported(TetheringWiFiBand.TwoPointFourGigahertz);

    // ---- Başlat / durdur ----

    /// <returns>true: WinDrop etkin noktası açık (bu çağrıyla veya zaten bizimle); false: kullanıcı etkin noktayı zaten
    /// kullanıyor, dokunulmadı.</returns>
    /// <exception cref="HotspotException">Profil yok, yetenek kapalı, başlatma hatası (eski ayar geri yüklenmiş olarak).</exception>
    public async Task<bool> StartAsync(HotspotConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var mgr = GetManager();
            var state = mgr.TetheringOperationalState;
            if (state == TetheringOperationalState.On)
            {
                if (!_owned) return false; // kullanıcının kendi etkin noktası: dokunma
                if (_activeConfig is null || _activeConfig == config) { ArmIdleTimer(); return true; }
                // Başka bir aktarım (çağıran kendisi sayaçta: >1) eski parolayla etkin noktayı kullanıyor: yeniden başlatma onu koparır.
                // Geçerli ayar CurrentConfig ile döner; yeni parola yalnız etkin nokta boşaldıktan sonra uygulanır.
                bool othersActive;
                lock (_timerLock) othersActive = _activeTransfers > 1;
                if (othersActive) { ArmIdleTimer(); return true; }
                // Parola yenilendi (PROTOCOL §6.2): açık kalan etkin nokta eski parolayla çalışıyor; yenisiyle yeniden başlat.
                try
                {
                    await TryStopAsync(mgr).ConfigureAwait(false);
                    await ConfigureAsync(mgr, new HotspotSnapshot(config.Ssid, config.Passphrase,
                        ChooseBand(mgr.GetCurrentAccessPointConfiguration()))).ConfigureAwait(false);
                    var again = await mgr.StartTetheringAsync().AsTask(ct).WaitAsync(OpTimeout, ct).ConfigureAwait(false);
                    if (again.Status != TetheringOperationStatus.Success)
                        throw new HotspotException(DescribeFailure(again.Status, again.AdditionalErrorMessage));
                }
                catch (Exception ex)
                {
                    try { await StopCoreAsync().ConfigureAwait(false); } catch (Exception) { }
                    if (ex is HotspotException or OperationCanceledException) throw;
                    throw new HotspotException(UserError.Of(UserErrorCode.HotspotRestartFailed, ex.Message), ex);
                }
                _activeConfig = config;
                ArmIdleTimer();
                return true;
            }
            if (state == TetheringOperationalState.InTransition)
                throw new HotspotException(UserError.Of(UserErrorCode.HotspotInTransition));

            // _owned iken etkin nokta dışarıdan kapatılmış olabilir: o anki ayar WinDrop'unkidir, kullanıcının özgün yedeği ezilmez.
            var original = _owned ? LoadBackup() ?? Snapshot(mgr.GetCurrentAccessPointConfiguration())
                                  : Snapshot(mgr.GetCurrentAccessPointConfiguration());
            if (!_owned) SaveBackup(original);
            _owned = true;
            try
            {
                await ConfigureAsync(mgr, new HotspotSnapshot(config.Ssid, config.Passphrase,
                    ChooseBand(mgr.GetCurrentAccessPointConfiguration()))).ConfigureAwait(false);
                var res = await mgr.StartTetheringAsync().AsTask(ct).WaitAsync(OpTimeout, ct).ConfigureAwait(false);
                if (res.Status != TetheringOperationStatus.Success)
                    throw new HotspotException(DescribeFailure(res.Status, res.AdditionalErrorMessage));
            }
            catch (Exception ex)
            {
                // Başarısız: eski ayarı geri yükle (kapalıyken) ve yedeği temizle.
                _owned = false;
                try { await TryStopAsync(mgr).ConfigureAwait(false); } catch (Exception) { }
                try { await RestoreAsync(mgr, original).ConfigureAwait(false); DeleteBackup(); }
                catch (Exception rex)
                {
                    throw new HotspotException(UserError.Of(UserErrorCode.HotspotStartFailed, "eski ayar geri yüklenemedi (yedek dosyada): " + rex.Message),
                        new AggregateException(ex, rex));
                }
                if (ex is HotspotException) throw;
                if (ex is OperationCanceledException) throw;
                throw new HotspotException(UserError.Of(UserErrorCode.HotspotStartFailed, ex.Message), ex);
            }
            _activeConfig = config;
            ArmIdleTimer();
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>WinDrop açtıysa durdurur ve eski ayarı geri yükler. Kullanıcının etkin noktasına dokunmaz.</summary>
    /// <returns>true: durduruldu ve ayar geri yüklendi.</returns>
    public async Task<bool> StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<bool> StopCoreAsync()
    {
        CancelIdleTimer();
        if (!_owned) return false;
        var mgr = GetManager();
        var original = LoadBackup();
        try
        {
            await TryStopAsync(mgr).ConfigureAwait(false);
        }
        finally
        {
            _owned = false;
            _activeConfig = null;
            if (original != null)
            {
                await RestoreAsync(mgr, original).ConfigureAwait(false);
                DeleteBackup();
            }
        }
        return true;
    }

    /// <summary>Çökme sonrası: yedek varsa ve etkin nokta WinDrop ayarıyla çalışmıyorsa eski ayarı geri yükler.</summary>
    public async Task<bool> RecoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_owned) return false;
            var backup = LoadBackup();
            if (backup == null) return false;
            var mgr = GetManager();
            await TryStopAsync(mgr).ConfigureAwait(false);
            await RestoreAsync(mgr, backup).ConfigureAwait(false);
            DeleteBackup();
            return true;
        }
        finally { _gate.Release(); }
    }

    // ---- Boşta zamanlayıcısı ----

    /// <summary>Bir aktarım başladı: bitene kadar boşta zamanlayıcısı durur.</summary>
    public void TransferStarted()
    {
        lock (_timerLock) { _activeTransfers++; _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite); }
    }

    /// <summary>Aktarım bitti: başka aktarım yoksa <see cref="IdleTimeout"/> sayımı yeniden başlar.</summary>
    public void TransferFinished()
    {
        lock (_timerLock)
        {
            if (_activeTransfers > 0) _activeTransfers--;
            if (_activeTransfers == 0 && _owned) _idleTimer?.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void ArmIdleTimer()
    {
        lock (_timerLock)
        {
            var due = _activeTransfers > 0 ? Timeout.InfiniteTimeSpan : IdleTimeout;
            if (_idleTimer == null) _idleTimer = new Timer(_ => _ = OnIdleAsync(), null, due, Timeout.InfiniteTimeSpan);
            else _idleTimer.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    private void CancelIdleTimer()
    {
        lock (_timerLock) _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private async Task OnIdleAsync()
    {
        Exception? error = null;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || _activeTransfers > 0) return;
                await StopCoreAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex) { error = ex; }
        IdleStopped?.Invoke(error);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await StopAsync().ConfigureAwait(false); } catch (Exception) { }
        _disposed = true;
        lock (_timerLock) { _idleTimer?.Dispose(); _idleTimer = null; }
    }

    // ---- Yardımcılar ----

    private static NetworkOperatorTetheringManager GetManager()
    {
        ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile == null)
        {
            // İnternet yok: yerel erişimi olan herhangi bir profil etkin noktayı yine de yapılandırabilir/açabilir.
            profile = NetworkInformation.GetConnectionProfiles()
                .FirstOrDefault(p => p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None);
        }
        if (profile == null)
            throw new HotspotException(UserError.Of(UserErrorCode.HotspotNeedNetwork));

        var cap = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
        if (cap != TetheringCapability.Enabled)
            throw new HotspotException(UserError.Of(UserErrorCode.HotspotUnavailableHere).Then(DescribeCapability(cap)));
        return NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
    }

    private static HotspotSnapshot Snapshot(NetworkOperatorTetheringAccessPointConfiguration c) =>
        new(c.Ssid, c.Passphrase, c.Band);

    private static TetheringWiFiBand ChooseBand(NetworkOperatorTetheringAccessPointConfiguration c) =>
        c.IsBandSupported(TetheringWiFiBand.TwoPointFourGigahertz) ? TetheringWiFiBand.TwoPointFourGigahertz : TetheringWiFiBand.Auto;

    private static async Task ConfigureAsync(NetworkOperatorTetheringManager mgr, HotspotSnapshot s)
    {
        var cfg = new NetworkOperatorTetheringAccessPointConfiguration { Ssid = s.Ssid, Passphrase = s.Passphrase };
        if (cfg.IsBandSupported(s.Band)) cfg.Band = s.Band;
        await mgr.ConfigureAccessPointAsync(cfg).AsTask().WaitAsync(OpTimeout).ConfigureAwait(false);
    }

    private static Task RestoreAsync(NetworkOperatorTetheringManager mgr, HotspotSnapshot original) => ConfigureAsync(mgr, original);

    private static async Task TryStopAsync(NetworkOperatorTetheringManager mgr)
    {
        if (mgr.TetheringOperationalState == TetheringOperationalState.Off) return;
        await mgr.StopTetheringAsync().AsTask().WaitAsync(OpTimeout).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + OpTimeout;
        while (mgr.TetheringOperationalState != TetheringOperationalState.Off && DateTime.UtcNow < deadline)
            await Task.Delay(200).ConfigureAwait(false);
    }

    private static UserError DescribeFailure(TetheringOperationStatus status, string? extra)
    {
        var msg = status switch
        {
            TetheringOperationStatus.WiFiDeviceOff => UserError.Of(UserErrorCode.HsWifiOff),
            TetheringOperationStatus.EntitlementCheckTimeout => UserError.Of(UserErrorCode.HsEntitlement),
            TetheringOperationStatus.OperationInProgress => UserError.Of(UserErrorCode.HsOpInProgress),
            TetheringOperationStatus.BluetoothDeviceOff => UserError.Of(UserErrorCode.HsBtOff),
            TetheringOperationStatus.NetworkLimitedConnectivity => UserError.Of(UserErrorCode.HsLimited),
            _ => UserError.Of(UserErrorCode.HsStartStatus, status.ToString()),
        };
        return string.IsNullOrWhiteSpace(extra) ? msg : msg.Then(UserErrorCode.Raw, extra);
    }

    private static UserErrorPart DescribeCapability(TetheringCapability cap) => new(cap switch
    {
        TetheringCapability.DisabledByGroupPolicy => UserErrorCode.CapGroupPolicy,
        TetheringCapability.DisabledByHardwareLimitation => UserErrorCode.CapHardware,
        TetheringCapability.DisabledByOperator => UserErrorCode.CapOperator,
        TetheringCapability.DisabledBySku => UserErrorCode.CapSku,
        TetheringCapability.DisabledByRequiredAppNotInstalled => UserErrorCode.CapAppMissing,
        TetheringCapability.DisabledBySystemCapability => UserErrorCode.CapSystem,
        _ => UserErrorCode.Raw,
    }, cap.ToString());

    // ---- Yedek (DPAPI) ----

    private sealed record BackupDto(string Ssid, string Passphrase, int Band);

    private void SaveBackup(HotspotSnapshot s)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new BackupDto(s.Ssid, s.Passphrase, (int)s.Band));
        Storage.WriteAtomic(_backupPath, Storage.Protect(json, Purpose));
        CryptographicOperations.ZeroMemory(json);
    }

    private HotspotSnapshot? LoadBackup()
    {
        if (!File.Exists(_backupPath)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<BackupDto>(Storage.Unprotect(File.ReadAllBytes(_backupPath), Purpose));
            return dto == null ? null : new HotspotSnapshot(dto.Ssid, dto.Passphrase, (TetheringWiFiBand)dto.Band);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return null; }
    }

    private void DeleteBackup()
    {
        try { File.Delete(_backupPath); } catch (IOException) { }
    }
}
