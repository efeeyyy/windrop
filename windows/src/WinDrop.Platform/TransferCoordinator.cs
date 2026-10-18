using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Core.Transfer;

namespace WinDrop.Platform;

public enum TransferRole { Sender, Receiver }

public enum TransferPhase
{
    /// <summary>Teklif gönderildi / karşı tarafın yanıtı bekleniyor.</summary>
    Negotiating,
    /// <summary>Gelen teklif PC'de kullanıcı onayı bekliyor.</summary>
    AwaitingApproval,
    /// <summary>Kabul edildi; adres/etkin nokta verildi, iPhone'un veri bağlantısı bekleniyor.</summary>
    Connecting,
    Active,
    Completed,
    Failed,
    Canceled,
    Rejected,
}

/// <summary>Aktarım durumuna eşlik eden bilgi notu (arayüz metni Strings.resx'te).</summary>
public enum TransferNote
{
    None,
    /// <summary>PROTOCOL §6.5: telefona push bildirimi gönderildi, telefonun bağlanması bekleniyor.</summary>
    PushSent,
}

/// <summary>Aktarımın o anki durumu (değişmez kopya). <see cref="Progress"/> 0..1.</summary>
public sealed record TransferSnapshot(
    TransferId Id,
    TransferRole Role,
    string PeerId,
    string PeerName,
    int FileCount,
    long TotalBytes,
    TransferPhase Phase,
    double Progress,
    double BytesPerSecond,
    string? SavedPath,
    string? Error,
    TransferNote Note = TransferNote.None,
    string? FirstFilePath = null)
{
    public bool IsTerminal => Phase >= TransferPhase.Completed;
}

/// <summary>Kullanıcı onayı bekleyen gelen teklif; gönderen adı eşleştirme kaydındandır (PROTOCOL §0).</summary>
/// <param name="VerificationCode">XX oturumunda 4 haneli doğrulama kodu (PROTOCOL §2b); tanıdık/eşli (IK) cihazda null.</param>
public sealed record OfferRequest(TransferId Id, string PeerId, string PeerName, int FileCount, long TotalBytes,
    IReadOnlyList<string> Preview, string? VerificationCode = null, bool IsKnownPeer = false);

/// <summary>
/// PROTOCOL §6.3: doğrudan yol/etkin nokta/BLE yok; aktarma sunucusu (internet/hücresel veri) kullanılacak — kullanıcı onayı bekler
/// (<see cref="TransferCoordinator.RespondToRelay"/>). Onaylanmazsa aktarma sunucusuna hiç bağlanılmaz.
/// </summary>
public sealed record RelayApprovalRequest(TransferId Id, string PeerId, string PeerName, int FileCount, long TotalBytes);

/// <summary>PC'nin BLE'de gördüğü yakındaki iPhone (iPhone'un kurduğu bağlantı + hello).</summary>
public sealed record NearbyPeer(string PeerId, string Name, bool IsKnown, bool IsPaired);

/// <summary>Anlaşılır (Türkçe) aktarım hatası; ileti kullanıcıya gösterilebilir.</summary>
public class TransferException(string message) : InvalidOperationException(message);

public sealed class TransferTimeouts
{
    /// <summary>Gönderen: karşı tarafın accept/reject yanıtı için (kullanıcı iPhone'da onaylıyor olabilir).</summary>
    public TimeSpan Accept { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Alıcı: PC'deki kullanıcı onayı için.</summary>
    public TimeSpan Approval { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>addr sonrası iPhone'un veri bağlantısı için.</summary>
    public TimeSpan Connect { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>hotspot sonrası (iPhone önce ağa katılır) veri bağlantısı için.</summary>
    public TimeSpan HotspotConnect { get; set; } = TimeSpan.FromSeconds(90);
    /// <summary>Aktarım ortasında kopunca aynı transferId ile yeniden bağlanma süresi.</summary>
    public TimeSpan Reconnect { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Aktarma: odaya girişten kurulmuş Noise oturumuna kadar (iPhone'un bağlanmasını bekleme dahil; sunucu eşi 60 sn bekler).</summary>
    public TimeSpan RelayPeer { get; set; } = TimeSpan.FromSeconds(75);
    /// <summary>Aktarma yolu için PC'deki kullanıcı onayı.</summary>
    public TimeSpan RelayApproval { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>PROTOCOL §6.5: push bildirimi gönderildikten sonra telefonun BLE ile bağlanıp <c>hello</c> göndermesi için (5 dk).</summary>
    public TimeSpan PushWait { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Bekleme döngüsü aralığı.</summary>
    public TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>Mobil etkin nokta denetimi (<see cref="HotspotManager"/>; testte sahtesi verilir).</summary>
public interface IHotspotController
{
    /// <returns>true: WinDrop etkin noktası açık; false: kullanıcı etkin noktayı zaten kullanıyor, dokunulmadı.</returns>
    /// <exception cref="HotspotException">Açılamadı.</exception>
    Task<bool> StartAsync(HotspotConfig config, CancellationToken ct = default);

    void TransferStarted();
    void TransferFinished();
}

public sealed class TransferCoordinatorOptions
{
    public required AppSettings Settings { get; init; }
    public required PendingTransferTable Pending { get; init; }
    public required TrustedDevices Trusted { get; init; }

    /// <summary>Kısmi dosya kökü (<c>%LOCALAPPDATA%\WinDrop\partial</c>).</summary>
    public required string PartialRoot { get; init; }

    /// <summary>Veri kanalı portu (<see cref="DataListener.Port"/>).</summary>
    public required Func<int> DataPort { get; init; }

    public string LocalName { get; init; } = Environment.MachineName;
    public IHotspotController? Hotspot { get; init; }
    public Func<HotspotConfig>? HotspotConfig { get; init; }

    /// <summary>
    /// PROTOCOL §6.2: eşli/tanıdık olmayan bir eşe <c>hotspot</c> (SSID + parola) gönderilen aktarım bitince (iptal/hata dahil)
    /// etkin nokta parolasını yeniler (<see cref="HotspotConfigStore.RotatePassphrase"/>).
    /// </summary>
    public Action? RotateHotspotPassphrase { get; init; }

    /// <summary>addr adayları (IP dizeleri). Varsayılan: <see cref="NetworkInfo.GetCandidateAddresses"/>.</summary>
    public Func<IReadOnlyList<string>>? CandidateAddresses { get; init; }

    /// <summary>Etkin nokta açıldıktan sonra PC'nin etkin nokta arayüzündeki IP'si. Varsayılan: 192.168.137.1 (ölçüm).</summary>
    public Func<string>? HotspotAddress { get; init; }

    /// <summary>Varsayılan ses çıkışı Bluetooth mu / ses çalıyor mu (<c>btAudio</c>, PROTOCOL §4). null: Bluetooth ses yok sayılır.</summary>
    public IBluetoothAudioMonitor? BluetoothAudio { get; init; }

    /// <summary>BLE veri yolunun (PROTOCOL §6.4) gönderim hız üst sınırı, bayt/sn; ≤ 0: sınırsız (yalnız test).</summary>
    public int BleBytesPerSecond { get; init; } = TransferCoordinator.BleDefaultBytesPerSecond;

    /// <summary>PC'nin statik Noise anahtarı (aktarma yolunda yanıtlayan olarak el sıkışmak için). null: aktarma yolu kapalı.</summary>
    public NoiseKeyPair? LocalKey { get; init; }

    /// <summary>Push isteği (<see cref="PushNotifier"/>) için HTTP işleyicisi; null: sistem varsayılanı (testte sahte).</summary>
    public HttpMessageHandler? PushHandler { get; init; }

    public Func<long>? FreeSpaceProvider { get; init; }
    public IFileCommitHook? CommitHook { get; init; }
    public TransferTimeouts Timeouts { get; init; } = new();
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// PROTOCOL §4/§6/§7 akışı: BLE sinyal oturumlarını (<see cref="HandleSessionAsync"/>) dinler; PC'den gönderme
/// (offer → accept → addr/hotspot → veri kanalı → DONE) ve PC'ye gelen (offer → onay → accept + addr/hotspot) yollarını yürütür.
/// Aynı anda birden çok aktarım desteklenir; her biri kendi iptal belirtecine sahiptir.
/// <para>§6.4 BLE yolu: doğrudan yol ve etkin nokta mümkün değilse/başarısızsa, toplam ≤ 64 KB ve iki tarafta
/// <c>btAudio=false</c> ise §5 çerçeveleri BLE oturumundan hız sınırlı gider.</para>
/// <para>§6.3 aktarma (relay) yolu: BLE de uygun değilse ve <see cref="AppSettings.RelayUrl"/> doluysa, kullanıcı onayından
/// (<see cref="RelayApprovalRequested"/>) sonra PC token üretir, <c>relay</c> yollar, odaya bağlanıp yanıtlayan olarak el sıkışır
/// (en çok 1 yeniden deneme, RESUME ile devam). Adres boşsa "yol yok" hatası verilir.</para>
/// </summary>
public sealed class TransferCoordinator : IAsyncDisposable
{
    /// <summary>§6.4: BLE veri yolu toplam üst sınırı (64 KB).</summary>
    public const long BleMaxTotalBytes = 64 * 1024;

    /// <summary>BLE veri hızı üst sınırı (ölçüm: BLE verisi Bluetooth sesini kesiyor; btAudio=false olsa da kibar ol).</summary>
    public const int BleDefaultBytesPerSecond = 20 * 1024;

    private const int PairedOfferRateLimit = 5;     // §5: cihaz başına 5/dk
    private const int UnpairedOfferRateLimit = 3;   // §2b: tanıdık (eşli olmayan) cihazdan 3/dk
    private const int UnknownOfferRateLimit = 3;    // §2b: bilinmeyen (XX) cihazlardan TOPLAM 3/dk (anahtar değiştirerek aşılamaz)
    private const int MaxTrackedSilenced = 64;      // sessizlik tablosu üst sınırı
    private static readonly TimeSpan OfferWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SilenceAfterDecline = TimeSpan.FromMinutes(2);
    private const int MaxRememberedTransfers = 200;

    private readonly TransferCoordinatorOptions _o;
    private readonly object _lock = new();
    private readonly Dictionary<TransferId, State> _states = [];
    private readonly Dictionary<string, PeerSession> _sessions = [];
    private readonly Dictionary<string, Queue<DateTimeOffset>> _offerTimes = [];
    private readonly Queue<DateTimeOffset> _unknownOfferTimes = [];
    private readonly Dictionary<string, DateTimeOffset> _silencedUntil = [];
    private readonly Dictionary<string, State> _bleActive = [];   // eş başına tek BLE veri aktarımı (§6.4)
    private readonly Dictionary<string, List<State>> _pushWaiters = [];   // §6.5: push gönderilmiş, telefonun hello'sunu bekleyen aktarımlar
    private readonly PushNotifier _push;
    private int _pushDisabledLogged;
    private bool _disposed;

    public TransferCoordinator(TransferCoordinatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _o = options;
        _push = new PushNotifier(options.PushHandler);
        if (_o.BluetoothAudio is { } monitor) monitor.Changed += OnLocalBtAudioChanged;
    }

    private bool LocalBtAudio => _o.BluetoothAudio?.Current.BtAudio ?? false;

    /// <summary>Yerel <c>btAudio</c> değişti: tüm eşlere <c>status</c> yolla; ses başladıysa süren BLE aktarımlarını kes.</summary>
    private void OnLocalBtAudioChanged(BluetoothAudioState state)
    {
        PeerSession[] sessions;
        lock (_lock) sessions = [.. _sessions.Values];
        foreach (var session in sessions)
            _ = SendSignalQuietly(session, new StatusMessage(null, state.BtAudio));
        if (state.BtAudio) FailBleTransfers(null, "Bu bilgisayarda Bluetooth ses başladı; BLE ile aktarım durduruldu.");
    }

    /// <summary>Süren BLE veri aktarımını (eşe göre veya hepsini) hata ile bitirir.</summary>
    private void FailBleTransfers(string? peerId, string message)
    {
        State[] list;
        lock (_lock) list = [.. _bleActive.Where(kv => peerId is null || kv.Key == peerId).Select(kv => kv.Value)];
        foreach (var s in list) s.Fail(message);
    }

    /// <summary>Her durum değişiminde (ekleme dahil). Herhangi bir iş parçacığında gelebilir.</summary>
    public event Action<TransferSnapshot>? TransferUpdated;

    /// <summary>Otomatik kabul dışı gelen teklif; yanıt için <see cref="RespondToOffer"/>.</summary>
    public event Action<OfferRequest>? OfferReceived;

    /// <summary>Aktarma sunucusu yolu için kullanıcı onayı gerekiyor; yanıt için <see cref="RespondToRelay"/>.</summary>
    public event Action<RelayApprovalRequest>? RelayApprovalRequested;

    /// <summary>Yakındaki (BLE oturumu açık) cihaz kümesi değişti.</summary>
    public event Action? PeersChanged;

    // ---------------------------------------------------------------- eşler

    /// <summary>Eşin BLE oturumu açık ve <c>hello</c> geldi (PROTOCOL §2b: PC kendisi taramaz, iPhone bağlanır).</summary>
    public bool IsNearby(string peerId)
    {
        lock (_lock) return _sessions.TryGetValue(peerId, out var s) && s.HelloReceived;
    }

    /// <summary>Yakındaki tüm iPhone'lar (eşli, tanıdık ve bilinmeyen).</summary>
    public IReadOnlyList<NearbyPeer> NearbyPeers
    {
        get
        {
            lock (_lock)
                return _sessions.Values.Where(s => s.HelloReceived)
                    .Select(s => new NearbyPeer(s.PeerId, s.Name, s.IsKnown, s.IsPaired)).ToArray();
        }
    }

    /// <summary>Eşin BLE oturumunu kapatır (eşleştirme kaldırıldı).</summary>
    public void DropPeer(string peerId)
    {
        PeerSession? s;
        lock (_lock) _sessions.TryGetValue(peerId, out s);
        s?.Close();
    }

    // ---------------------------------------------------------------- BLE oturumu

    /// <summary>Bir eşin sinyal oturumunu bitene kadar yürütür (<see cref="SignalSessionAcceptor.SessionHandler"/>).</summary>
    public async Task HandleSessionAsync(PeerSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        PeerSession? replaced;
        lock (_lock)
        {
            _sessions.TryGetValue(session.PeerId, out replaced);
            _sessions[session.PeerId] = session;
        }
        replaced?.Close();
        PeersChanged?.Invoke();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Closed);
        try
        {
            await session.Channel.SendSignalAsync(
                new HelloMessage(Prefix.CurrentVersion, _o.LocalName, ["tcp", "hotspot"], null, LocalBtAudio), cts.Token).ConfigureAwait(false);
            while (true)
            {
                var msg = await session.Channel.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (msg is null) break;
                if (msg.Frame is { } frame)
                {
                    // §9a: düz metnin ilk baytı 1–8 → §5 çerçevesi; yalnız izin verilen BLE aktarımına ait olabilir (§6.4).
                    if (RouteBleFrame(session, frame)) continue;
                    PlatformLog.Write("BLE oturumunda beklenmeyen veri çerçevesi geldi; oturum kapatılıyor.");
                    session.Faulted = true;
                    break;
                }
                Dispatch(session, msg.Signal!);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ProtocolException or NoiseException
                                       or ObjectDisposedException or InvalidOperationException)
        {
            PlatformLog.Write($"BLE oturumu sonlandı: {ex.GetType().Name}: {ex.Message}");
            if (ex is ProtocolException or NoiseException) session.Faulted = true;
        }
        finally
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(session.PeerId, out var cur) && ReferenceEquals(cur, session))
                    _sessions.Remove(session.PeerId);
            }
            FailNegotiating(session.PeerId, "Bluetooth bağlantısı koptu.");
            CompleteBle(session);
            PeersChanged?.Invoke();
        }
    }

    private void Dispatch(PeerSession session, SignalMessage signal)
    {
        switch (signal)
        {
            case HelloMessage h:
                session.SetHelloName(PairingService.SanitizeName(h.Name));
                session.Wifi = h.Wifi;
                session.BtAudio = h.BtAudio;
                session.Caps = h.Caps;
                PlatformLog.Write($"BLE hello: wifi={h.Wifi ?? "?"} caps=[{string.Join(",", h.Caps)}]");
                if (!session.HelloReceived)
                {
                    session.HelloReceived = true;
                    PeersChanged?.Invoke();
                }
                NotifyPushWaiters(session);
                break;
            case PushIdMessage push:
                OnPushId(session, push);
                break;
            case StatusMessage st:
                if (st.Wifi is not null) session.Wifi = st.Wifi;
                session.BtAudio = st.BtAudio;
                if (st.BtAudio) FailBleTransfers(session.PeerId, "iPhone'da Bluetooth ses başladı; BLE ile aktarım durduruldu.");
                break;
            case OfferMessage offer:
                OnOffer(session, offer);
                break;
            case AcceptMessage a:
                if (Owned(a.TransferId, session) is { Role: TransferRole.Sender } sa) sa.Decision.TrySetResult(true);
                break;
            case RejectMessage r:
                if (Owned(r.TransferId, session) is { Role: TransferRole.Sender } sr)
                {
                    sr.RejectReason = r.Reason;
                    sr.Decision.TrySetResult(false);
                }
                break;
            case PathMessage p:
                OnPath(session, p);
                break;
            case CancelSignalMessage c:
                if (Owned(c.TransferId, session) is { } sc)
                {
                    sc.RemoteCanceled = true;
                    sc.Cts.Cancel();
                }
                break;
            default:
                PlatformLog.Write($"BLE: beklenmeyen sinyal ({signal.GetType().Name}), yok sayıldı.");
                break;
        }
    }

    private State? Owned(TransferId id, PeerSession session)
    {
        lock (_lock)
        {
            return _states.TryGetValue(id, out var s) && s.PeerId == session.PeerId ? s : null;
        }
    }

    private void OnPath(PeerSession session, PathMessage p)
    {
        if (Owned(p.TransferId, session) is not { } s) return;
        PlatformLog.Write($"path {s.Id}: {p.Result}");
        s.Touch(_o.Time);
        if (s.ConnectionActive) return;
        if (p.Result is PathResult.TcpFail or PathResult.HotspotFail)
            s.PathFailed.TrySetResult(p.Result);
        else if (p.Result == PathResult.RelayFail)
            s.ReportRelayFail();
    }

    private void FailNegotiating(string peerId, string message)
    {
        State[] list;
        lock (_lock) list = [.. _states.Values.Where(s => s.PeerId == peerId)];
        foreach (var s in list)
        {
            // Veri kanalı bağımsızdır (TCP); yalnız pazarlık aşamasındakiler BLE'ye muhtaçtır.
            // Push bekleyen aktarımın henüz BLE oturumu yoktur: eski oturumun kapanması onu düşürmez (§6.5).
            if (s.PushWaiting) continue;
            if (s.Phase is TransferPhase.Negotiating or TransferPhase.AwaitingApproval)
                s.Fail(message);
        }
    }

    // ---------------------------------------------------------------- BLE küçük dosya yolu (§6.4)

    /// <summary>BLE yolu kullanılamıyorsa nedeni (kullanıcıya gösterilir), kullanılabiliyorsa null.</summary>
    private string? BleIneligibleReason(long total, PeerSession session)
    {
        if (total > BleMaxTotalBytes) return $"toplam boyut {BleMaxTotalBytes / 1024} KB'ı aşıyor";
        if (LocalBtAudio) return "bu bilgisayarda Bluetooth ses çıkışı kullanılıyor";
        if (session.BtAudio) return "iPhone'da Bluetooth ses çıkışı kullanılıyor";
        return null;
    }

    /// <summary>Aktarımı eşin BLE oturumuna bağlar (eş başına tek aktarım); çerçeveler artık bu aktarıma yönlenir.</summary>
    private bool TryActivateBle(State s, PeerSession session)
    {
        lock (_lock)
        {
            if (s.Ble is not null) return true;
            if (_bleActive.ContainsKey(s.PeerId)) return false;
            s.Ble = new BleDataChannel(session.Channel, _o.BleBytesPerSecond, _o.Time);
            _bleActive[s.PeerId] = s;
        }
        return true;
    }

    private void ReleaseBle(State s)
    {
        lock (_lock)
        {
            if (_bleActive.TryGetValue(s.PeerId, out var cur) && ReferenceEquals(cur, s)) _bleActive.Remove(s.PeerId);
        }
        s.Ble?.Dispose();
    }

    /// <summary>BLE oturumu koptu: o oturumdaki BLE aktarımının okuyucusu "bağlantı koptu" alır.</summary>
    private void CompleteBle(PeerSession session)
    {
        State? s;
        lock (_lock) _bleActive.TryGetValue(session.PeerId, out s);
        if (s?.Ble is { } ble && ReferenceEquals(ble.Session, session.Channel)) ble.Complete();
    }

    /// <summary>
    /// BLE oturumundan gelen §5 çerçevesini sürmekte olan BLE aktarımına verir. false: çerçeve beklenmiyor (oturum düşürülür).
    /// </summary>
    private bool RouteBleFrame(PeerSession session, Frame frame)
    {
        // §6.4a: yol PC tarafından seçilir; çerçeve ancak PC `bleData` gönderdikten sonra (etkin BLE aktarımı) beklenir.
        State? s;
        lock (_lock) _bleActive.TryGetValue(session.PeerId, out s);
        return s?.Ble is { } ble && ReferenceEquals(ble.Session, session.Channel) && ble.Post(frame);
    }

    /// <summary>Aktarımı BLE kanalı üstünde yürütür (FileSender/FileReceiver aynı, kanal uyarlayıcı).</summary>
    private async Task RunBleTransferAsync(State s, CancellationToken ct)
    {
        var ble = s.Ble!;
        // TCP kaydı kapanır: geç gelen bir TCP bağlantısı ikinci bir oturum açmasın.
        s.Registration?.Dispose();
        s.Registration = null;
        PlatformLog.Write($"BLE veri yolu {s.Id}: {s.Total} B, en çok {(_o.BleBytesPerSecond > 0 ? _o.BleBytesPerSecond : 0)} B/sn");
        // Eş Connect süresi içinde hiç çerçeve göndermezse (bleData sonrası) aktarım düşer.
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchdog = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_o.Timeouts.Connect, _o.Time, watchdogCts.Token).ConfigureAwait(false);
                if (!ble.FrameSeen) s.Fail("iPhone Bluetooth üzerinden yanıt vermedi (zaman aşımı). Yol yok.");
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
        try
        {
            await OnDataConnectionAsync(s, ble).ConfigureAwait(false);
        }
        finally
        {
            watchdogCts.Cancel();
            await watchdog.ConfigureAwait(false);
            ReleaseBle(s);
        }
        if (s.FailureMessage is { } failure) throw new TransferException(failure);
        if (s.DataDone.Task.IsCompletedSuccessfully)
        {
            FinishFromResult(s, s.DataDone.Task.Result);
            return;
        }
        ct.ThrowIfCancellationRequested();
        throw new TransferException("Bluetooth üzerinden aktarım kesildi.");
    }

    // ---------------------------------------------------------------- PC'den gönder

    /// <summary>
    /// Dosyaları eşe teklif eder ve arka planda yürütür. Hemen döner (ilk durum); sonuç için <see cref="TransferUpdated"/> /
    /// <see cref="WaitAsync"/>. Cihaz yakında değilse veya dosya yoksa <see cref="TransferException"/>.
    /// </summary>
    public TransferSnapshot Send(string peerId, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        PeerSession? session;
        lock (_lock) _sessions.TryGetValue(peerId, out session);
        if (session is null)
        {
            // §6.5: yakında değil ama pushId (ve aktarma sunucusu adresi) varsa telefon bildirimle uyandırılır.
            var device = FindTrustedByPeerId(peerId);
            if (device?.PushId is not null && !string.IsNullOrWhiteSpace(_o.Settings.RelayUrl))
                return SendViaPush(device, paths);
            throw new TransferException("Cihaz yakında değil (Bluetooth bağlantısı yok).");
        }

        var files = CollectFiles(paths);
        var state = new State(TransferId.Create(), TransferRole.Sender, session.PeerId, session.Name,
            (byte[])session.PublicKey.Clone(), files.Count, files.Sum(f => f.Size)) { Files = files, KnownAtStart = IsKnownPeer(session) };
        Register(state);
        _ = Task.Run(() => RunGuardedAsync(state, () => RunSenderAsync(state, session)));
        return Snapshot(state);
    }

    private static IReadOnlyList<IFileSource> CollectFiles(IReadOnlyList<string> paths)
    {
        IReadOnlyList<IFileSource> files;
        try { files = FolderScanner.Collect(paths, PlatformLog.Write); }   // §5a: klasörler özyinelemeli açılır
        catch (FolderScanException ex) { throw new TransferException(ex.Message); }
        catch (FileNotFoundException ex) { throw new TransferException($"Dosya bulunamadı: {Path.GetFileName(ex.FileName)}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TransferException("Dosya veya klasör okunamadı: " + ex.Message);
        }
        if (files.Count == 0) throw new TransferException("Gönderilecek dosya yok.");
        if (files.Count > TransferLimits.MaxFiles) throw new TransferException($"En çok {TransferLimits.MaxFiles} dosya gönderilebilir.");
        return files;
    }

    // ---------------------------------------------------------------- push bildirimi (§6.5)

    private TrustedDevice? FindTrustedByPeerId(string peerId) =>
        _o.Trusted.List().FirstOrDefault(d => DeviceId.ToHex(d.DeviceId) == peerId);

    /// <summary>Cihazın bildirimle ulaşılabilir olduğu (pushId kayıtlı ve aktarma sunucusu adresi dolu) mu?</summary>
    public bool CanPush(string peerId) =>
        !string.IsNullOrWhiteSpace(_o.Settings.RelayUrl) && FindTrustedByPeerId(peerId)?.PushId is not null;

    /// <summary>
    /// <c>pushId</c> (iPhone→PC): yalnız eşli/tanıdık (güvenilen listede kayıtlı) cihazdan kabul edilir; geçersiz biçim yok sayılır
    /// (oturum düşmez). Değer loglanmaz.
    /// </summary>
    private void OnPushId(PeerSession session, PushIdMessage m)
    {
        if (!session.IsKnown)
        {
            PlatformLog.Write("pushId tanıdık olmayan cihazdan geldi, yok sayıldı.");
            return;
        }
        if (!PushIds.IsValid(m.PushId))
        {
            PlatformLog.Write("Geçersiz biçimli pushId yok sayıldı.");
            return;
        }
        if (_o.Trusted.SetPushId(session.PublicKey, m.PushId))
        {
            PlatformLog.Write("pushId kaydedildi.");
            PeersChanged?.Invoke();   // arayüzde "bildirimle ulaşılabilir" rozeti
        }
        else
        {
            PlatformLog.Write("pushId: cihaz güvenilen listede yok, yok sayıldı.");
        }
    }

    private TransferSnapshot SendViaPush(TrustedDevice device, IReadOnlyList<string> paths)
    {
        var files = CollectFiles(paths);
        var state = new State(TransferId.Create(), TransferRole.Sender, DeviceId.HexFromPublicKey(device.PublicKey), device.Name,
            (byte[])device.PublicKey.Clone(), files.Count, files.Sum(f => f.Size)) { Files = files, KnownAtStart = true };
        Register(state);
        _ = Task.Run(() => RunGuardedAsync(state, () => RunPushSenderAsync(state, device)));
        return Snapshot(state);
    }

    /// <summary>
    /// §6.5: push gönder → telefonun BLE <c>hello</c>'sunu en çok <see cref="TransferTimeouts.PushWait"/> bekle → bekleyen teklifi
    /// normal akışla (offer…) yeniden gönder.
    /// </summary>
    private async Task RunPushSenderAsync(State s, TrustedDevice device)
    {
        var ct = s.Cts.Token;
        var pushId = device.PushId!;
        PeerSession? arrived;
        s.PushWaiting = true;
        lock (_lock)
        {
            if (!_pushWaiters.TryGetValue(s.PeerId, out var list)) _pushWaiters[s.PeerId] = list = [];
            list.Add(s);
            // Kayıt ile birlikte kontrol: arada telefon zaten bağlandıysa beklemeye gerek yok.
            arrived = _sessions.TryGetValue(s.PeerId, out var cur) && cur.HelloReceived ? cur : null;
        }
        try
        {
            if (arrived is null)
            {
                var result = await _push.SendAsync(_o.Settings.RelayUrl, pushId, _o.LocalName, ct).ConfigureAwait(false);
                if (result.Outcome != PushOutcome.Sent && !s.PhoneArrived.Task.IsCompletedSuccessfully)
                    throw PushFailure(result, device);
                PlatformLog.Write($"push gönderildi {s.Id}, telefon bekleniyor.");
                s.Note = TransferNote.PushSent;
                Raise(s);
                try { arrived = await s.PhoneArrived.Task.WaitAsync(_o.Timeouts.PushWait, _o.Time, ct).ConfigureAwait(false); }
                catch (TimeoutException) { throw new TransferException("Telefon yanıt vermedi."); }
            }
        }
        finally
        {
            s.PushWaiting = false;
            lock (_lock)
            {
                if (_pushWaiters.TryGetValue(s.PeerId, out var list) && list.Remove(s) && list.Count == 0)
                    _pushWaiters.Remove(s.PeerId);
            }
        }
        s.Note = TransferNote.None;
        s.Touch(_o.Time);
        PlatformLog.Write($"telefon bağlandı {s.Id}; bekleyen teklif yeniden gönderiliyor.");
        await RunSenderAsync(s, arrived).ConfigureAwait(false);
    }

    private TransferException PushFailure(PushResult result, TrustedDevice device)
    {
        switch (result.Outcome)
        {
            case PushOutcome.Disabled:
                if (Interlocked.Exchange(ref _pushDisabledLogged, 1) == 0)
                    PlatformLog.Write("Aktarma sunucusunda push kapalı (501); bildirim gönderilmeyecek.");
                return new TransferException("Push bu sunucuda kapalı.");
            case PushOutcome.Unknown:
                _o.Trusted.ClearPushId(device.PublicKey);   // sunucu kaydı silmiş: telefon yeniden bağlanınca yenisini bildirir
                PeersChanged?.Invoke();
                return new TransferException("Telefonun bildirim kaydı sunucuda yok; telefonda uygulamayı açıp Bluetooth ile bağlanmasını bekleyin.");
            case PushOutcome.RateLimited:
                return new TransferException("Telefona çok fazla bildirim gönderildi; biraz sonra yeniden deneyin.");
            case PushOutcome.ServerError:
                return new TransferException($"Bildirim sunucusu hata verdi (HTTP {result.Status}).");
            default:
                return new TransferException("Bildirim sunucusuna ulaşılamadı.");
        }
    }

    /// <summary>Telefon BLE ile bağlanıp hello gönderdi: bekleyen aktarımlar devam eder.</summary>
    private void NotifyPushWaiters(PeerSession session)
    {
        State[] waiting;
        lock (_lock) waiting = _pushWaiters.TryGetValue(session.PeerId, out var list) ? [.. list] : [];
        foreach (var s in waiting) s.PhoneArrived.TrySetResult(session);
    }

    /// <summary>offer.preview: klasör içindeki dosyalar yerine en üst klasör adı, sonda "/" ile (§5a); aynı klasör bir kez.</summary>
    internal static string[] BuildPreview(IReadOnlyList<IFileSource> files)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preview = new List<string>();
        foreach (var f in files)
        {
            var entry = f.Dir is { Length: > 0 } d ? d.Split(RelativeDirectory.Separator)[0] + "/" : f.Name;
            if (seen.Add(entry)) preview.Add(entry);
            if (preview.Count >= SignalMessages.MaxPreview) break;
        }
        return preview.ToArray();
    }

    private async Task RunSenderAsync(State s, PeerSession session)
    {
        var ct = s.Cts.Token;
        s.Registration = _o.Pending.Register(s.Id, s.PeerKey, ch => OnDataConnectionAsync(s, ch), session.Channel.Kind);
        Update(s, TransferPhase.Negotiating);
        var preview = BuildPreview(s.Files!);
        await session.Channel.SendSignalAsync(new OfferMessage(s.Id, s.Count, s.Total, preview), ct).ConfigureAwait(false);

        bool accepted;
        try { accepted = await s.Decision.Task.WaitAsync(_o.Timeouts.Accept, ct).ConfigureAwait(false); }
        catch (TimeoutException) { throw new TransferException("Karşı cihaz teklife yanıt vermedi."); }
        if (!accepted)
        {
            Finish(s, TransferPhase.Rejected, s.RejectReason is { Length: > 0 } r ? $"Karşı cihaz reddetti ({r})." : "Karşı cihaz reddetti.");
            return;
        }

        await RunPathAsync(s, session, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- PC'ye gelen

    private void OnOffer(PeerSession session, OfferMessage offer)
    {
        lock (_lock)
        {
            if (_states.ContainsKey(offer.TransferId))
            {
                PlatformLog.Write("Yinelenen offer yok sayıldı.");
                return;
            }
        }
        var known = IsKnownPeer(session);
        // §2b: eşli olmayan (tanıdık/bilinmeyen) cihazdan reddedilen teklifin ardından 2 dk sessizlik.
        if (!session.IsPaired && IsSilenced(session.PeerId))
        {
            PlatformLog.Write("offer sessizlik süresinde, reddedildi.");
            _ = SendSignalQuietly(session, new RejectMessage(offer.TransferId, "silenced"));
            return;
        }
        if (offer.Count < 1 || offer.Count > TransferLimits.MaxFiles)
        {
            _ = SendSignalQuietly(session, new RejectMessage(offer.TransferId, "bad_count"));
            return;
        }

        var state = new State(offer.TransferId, TransferRole.Receiver, session.PeerId, session.Name,
            (byte[])session.PublicKey.Clone(), offer.Count, offer.Total) { Preview = offer.Preview, KnownAtStart = known };
        string? rejectReason = null;
        lock (_lock)
        {
            // Hız sınırı + kayıt tek kilit altında: eşzamanlı iki bilinmeyen teklif aynı anda geçemez.
            if (!known)
            {
                // §2b: bilinmeyen (XX) cihazlar için PeerId'ye değil GENEL sınır (yeni anahtarla aşılamaz):
                // aynı anda en çok 1 onay bekleyen bilinmeyen teklif ve toplam 3/dk.
                if (_states.Values.Any(x => x.Role == TransferRole.Receiver && !x.KnownAtStart && !x.IsTerminal
                                            && x.Phase is TransferPhase.Negotiating or TransferPhase.AwaitingApproval))
                    rejectReason = "busy";
                else if (!AllowUnknownOfferLocked())
                    rejectReason = "rate_limited";
                else
                    AllowOfferLocked(session.PeerId, UnpairedOfferRateLimit);   // onaylanırsa tanıdık olur: bu teklif eş başına pencereye de sayılır
                                                                               // (kayıt sayısı genel 3/dk ile sınırlı, budanır)
            }
            else if (!AllowOfferLocked(session.PeerId, session.IsPaired ? PairedOfferRateLimit : UnpairedOfferRateLimit))
                rejectReason = "rate_limited";
            if (rejectReason is null) AddLocked(state);
        }
        if (rejectReason is not null)
        {
            PlatformLog.Write($"offer reddedildi ({rejectReason}).");
            _ = SendSignalQuietly(session, new RejectMessage(offer.TransferId, rejectReason));
            return;
        }
        state.Touch(_o.Time);
        Raise(state);
        _ = Task.Run(() => RunGuardedAsync(state, () => RunReceiverAsync(state, session)));
    }

    /// <summary>Eş şu an eşli veya tanıdık mı (oturum bayrağı ya da <see cref="TrustedDevices"/>)?</summary>
    private bool IsKnownPeer(PeerSession session) =>
        session.IsKnown || _o.Trusted.FindByPublicKey(session.PublicKey) is not null;

    private bool IsSilenced(string peerId)
    {
        lock (_lock)
        {
            if (!_silencedUntil.TryGetValue(peerId, out var until)) return false;
            if (_o.Time.GetUtcNow() < until) return true;
            _silencedUntil.Remove(peerId);
            return false;
        }
    }

    private void Silence(string peerId)
    {
        var now = _o.Time.GetUtcNow();
        lock (_lock)
        {
            foreach (var key in _silencedUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToArray())
                _silencedUntil.Remove(key);   // eski kayıtları buda
            while (_silencedUntil.Count >= MaxTrackedSilenced && !_silencedUntil.ContainsKey(peerId))
                _silencedUntil.Remove(_silencedUntil.MinBy(kv => kv.Value).Key);   // sözlük sınırsız büyümesin
            _silencedUntil[peerId] = now + SilenceAfterDecline;
        }
    }

    private bool AllowOfferLocked(string peerId, int limit)
    {
        var now = _o.Time.GetUtcNow();
        foreach (var key in _offerTimes.Where(kv => kv.Value.Count == 0 || now - kv.Value.Peek() > OfferWindow)
                     .Select(kv => kv.Key).ToArray())
            _offerTimes.Remove(key);   // süresi dolan eş kayıtlarını buda
        if (!_offerTimes.TryGetValue(peerId, out var q)) _offerTimes[peerId] = q = new Queue<DateTimeOffset>();
        if (q.Count >= limit) return false;
        q.Enqueue(now);
        return true;
    }

    private bool AllowUnknownOfferLocked()
    {
        var now = _o.Time.GetUtcNow();
        while (_unknownOfferTimes.Count > 0 && now - _unknownOfferTimes.Peek() > OfferWindow) _unknownOfferTimes.Dequeue();
        if (_unknownOfferTimes.Count >= UnknownOfferRateLimit) return false;
        _unknownOfferTimes.Enqueue(now);
        return true;
    }

    private async Task RunReceiverAsync(State s, PeerSession session)
    {
        var ct = s.Cts.Token;
        Update(s, TransferPhase.AwaitingApproval);

        var settings = _o.Settings;
        // §7 + §2b: otomatik kabul yalnız QR ile eşli cihazlara; tanıdık/bilinmeyen her zaman alıcı onayı ister.
        var auto = session.IsPaired && settings.AutoAccept && s.Total <= settings.AutoAcceptMaxBytes;
        bool accepted = true;
        if (!auto)
        {
            OfferReceived?.Invoke(new OfferRequest(s.Id, s.PeerId, s.PeerName, s.Count, s.Total, s.Preview, session.VerificationCode, session.IsKnown));
            try { accepted = await s.UiDecision.Task.WaitAsync(_o.Timeouts.Approval, ct).ConfigureAwait(false); }
            catch (TimeoutException) { accepted = false; }
        }
        if (!accepted)
        {
            if (!session.IsPaired) Silence(session.PeerId);
            await SendSignalQuietly(session, new RejectMessage(s.Id, "declined")).ConfigureAwait(false);
            Finish(s, TransferPhase.Rejected, null);
            return;
        }

        if (!auto && !session.IsKnown)
        {
            // §2b: onaylanan karşı taraf "tanıdık cihaz" olarak hatırlanır (sonraki bağlantılar IK); reddedilen saklanmaz.
            _o.Trusted.Add(session.PublicKey, session.Name, familiar: true);
            session.IsKnown = true;
            PlatformLog.Write($"Tanıdık cihaz kaydedildi: '{session.Name}'");
            PeersChanged?.Invoke();
        }

        var store = new FileSystemPartialStore(_o.PartialRoot, settings.DownloadFolder, _o.CommitHook, _o.FreeSpaceProvider);
        s.Store = store;
        if (store.GetFreeSpaceBytes() - TransferLimits.DefaultSpaceMarginBytes < s.Total)   // §5: kabulden önce
        {
            await SendSignalQuietly(session, new RejectMessage(s.Id, "no_space")).ConfigureAwait(false);
            throw new TransferException("İndirme klasörünün bulunduğu sürücüde yeterli boş alan yok.");
        }

        s.Registration = _o.Pending.Register(s.Id, s.PeerKey, ch => OnDataConnectionAsync(s, ch), session.Channel.Kind);
        Update(s, TransferPhase.Connecting);
        await session.Channel.SendSignalAsync(new AcceptMessage(s.Id), ct).ConfigureAwait(false);
        await RunPathAsync(s, session, ct).ConfigureAwait(false);
    }

    /// <summary>Teklifi kabul/ret eder (<see cref="OfferReceived"/> yanıtı).</summary>
    public void RespondToOffer(TransferId id, bool accept)
    {
        State? s;
        lock (_lock) _states.TryGetValue(id, out s);
        s?.UiDecision.TrySetResult(accept);
    }

    // ---------------------------------------------------------------- yol seçimi (§6)

    /// <summary>
    /// §6: iPhone <c>wifi=connected</c> (veya bilinmiyor) → <c>addr</c>; <c>wifi=none</c> → etkin noktayı aç + <c>hotspot</c>.
    /// iPhone bir Wi-Fi'a bağlıyken etkin nokta asla denenmez.
    /// </summary>
    private async Task ProvidePathAsync(State s, PeerSession session, CancellationToken ct)
    {
        var port = _o.DataPort();
        s.Touch(_o.Time);
        if (session.Wifi == WifiState.None)
        {
            if (session.Caps.Count > 0 && !session.Caps.Contains("hotspot"))
                throw new PathUnavailableException("iPhone etkin nokta yolunu desteklemiyor ve bir Wi-Fi ağına bağlı değil. Yol yok.");
            if (_o.Hotspot is null || _o.HotspotConfig is null)
                throw new PathUnavailableException("iPhone bir Wi-Fi ağına bağlı değil ve etkin nokta yolu bu sürümde kullanılamıyor. Yol yok.");

            s.HotspotRequested = true;
            _o.Hotspot.TransferStarted();   // boşta zamanlayıcısı bu aktarım bitene kadar durur
            bool started;
            var cfg = _o.HotspotConfig();
            try
            {
                started = await _o.Hotspot.StartAsync(cfg, ct).ConfigureAwait(false);
            }
            catch (HotspotException ex)
            {
                throw new PathUnavailableException("Windows Mobil etkin noktası açılamadı: " + ex.Message);
            }
            if (!started)
                throw new PathUnavailableException(
                    "Windows Mobil etkin noktası zaten sizin tarafınızdan kullanılıyor; WinDrop kendi etkin noktasını açamıyor. " +
                    "Etkin noktayı kapatın ya da iPhone'u bir Wi-Fi ağına bağlayıp yeniden deneyin.");

            var ip = _o.HotspotAddress?.Invoke() ?? "192.168.137.1";
            s.ConnectTimeout = _o.Timeouts.HotspotConnect;
            s.Touch(_o.Time);
            // §6.2: hotspot yalnız kullanıcının onayladığı aktarımda gider (alıcıda onay/otomatik kabul, gönderende kullanıcının seçimi).
            s.HotspotSent = true;
            await session.Channel.SendSignalAsync(new HotspotMessage(s.Id, ip, port, cfg.Ssid, cfg.Passphrase), ct).ConfigureAwait(false);
            PlatformLog.Write($"hotspot gönderildi {s.Id}: {ip}:{port}");
        }
        else
        {
            var ips = (_o.CandidateAddresses ?? DefaultCandidates)();
            if (ips.Count == 0) throw new PathUnavailableException("Bu bilgisayarda kullanılabilir ağ adresi yok (ağa bağlı değil).");
            s.ConnectTimeout = _o.Timeouts.Connect;
            s.Touch(_o.Time);
            var cands = ips.Select(ip => new AddrCandidate(ip, port)).ToArray();
            await session.Channel.SendSignalAsync(new AddrMessage(s.Id, cands), ct).ConfigureAwait(false);
            PlatformLog.Write($"addr gönderildi {s.Id}: {cands.Length} aday, port {port}");
        }
    }

    private static IReadOnlyList<string> DefaultCandidates() =>
        NetworkInfo.GetCandidateAddresses().Select(c => c.Address.ToString()).ToArray();

    // ---------------------------------------------------------------- veri kanalı

    private async Task OnDataConnectionAsync(State s, IFrameChannel channel)
    {
        var gen = Interlocked.Increment(ref s.Generation);
        s.ConnectionActive = true;
        s.EverConnected = true;
        s.Touch(_o.Time);
        Update(s, TransferPhase.Active);
        var progress = new SyncProgress(p => OnProgress(s, p));
        try
        {
            TransferResult result;
            if (s.Role == TransferRole.Sender)
            {
                result = await new FileSender(channel, s.Files!, progress).RunAsync(s.Cts.Token).ConfigureAwait(false);
            }
            else
            {
                var options = new ReceiverOptions(DeviceId.ToHex(DeviceId.FromPublicKey(s.PeerKey)), s.Id, new OfferInfo(s.Count, s.Total));
                result = await new FileReceiver(channel, s.Store!, options, progress).RunAsync(s.Cts.Token).ConfigureAwait(false);
            }
            if (gen == s.Generation) s.DataDone.TrySetResult(result);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ProtocolException or NoiseException)
        {
            // Bağlantı kopması (ConnectionLostException dahil), akışın ortasında kesilmesi veya yeni bağlantının eskiyi kapatması:
            // kısmi dosyalar saklanır, aynı transferId ile devam edilebilir (Reconnect süresi içinde).
            if (gen != s.Generation) return;
            PlatformLog.Write($"veri bağlantısı koptu {s.Id}: {ex.Message}");
            s.ConnectionActive = false;
            s.Touch(_o.Time);
            Update(s, TransferPhase.Connecting);
        }
        catch (OperationCanceledException)
        {
            if (gen == s.Generation) s.DataDone.TrySetResult(new TransferResult(TransferOutcome.Canceled, []));
        }
    }

    private void OnProgress(State s, TransferProgress p)
    {
        s.Touch(_o.Time);
        lock (s.Gate)
        {
            s.Progress = p.TotalBytes > 0 ? Math.Clamp(p.BytesDone / (double)p.TotalBytes, 0, 1) : 0;
            s.Speed = p.BytesPerSecond;
        }
        Raise(s);
    }

    /// <summary>
    /// Yol sağlama (addr/hotspot) + veri bağlantısı bekleme; doğrudan yol/etkin nokta mümkün değilse veya başarısızsa
    /// §6.4/§6.4a BLE küçük dosya yoluna düşer (toplam ≤ 64 KB, iki tarafta btAudio=false): PC `bleData` yollar, olmazsa "yol yok" hatası.
    /// </summary>
    private async Task RunPathAsync(State s, PeerSession session, CancellationToken ct)
    {
        string reason;
        try
        {
            await ProvidePathAsync(s, session, ct).ConfigureAwait(false);
            await WaitForDataAsync(s).ConfigureAwait(false);   // TCP/etkin nokta ile bitti
            return;
        }
        catch (PathUnavailableException ex)
        {
            reason = ex.Message;
        }

        // §6: doğrudan yol → etkin nokta → BLE (küçük dosya) → aktarma sunucusu.
        var why = BleIneligibleReason(s.Total, session);
        if (why is null)
        {
            if (TryActivateBle(s, session))
            {
                // Önce kanal etkin (gelen çerçeveler kabul edilir), sonra `bleData`: gönderen taraf MANIFEST ile başlar.
                await session.Channel.SendSignalAsync(new BleDataMessage(s.Id), ct).ConfigureAwait(false);
                PlatformLog.Write($"bleData gönderildi {s.Id}");
                await RunBleTransferAsync(s, ct).ConfigureAwait(false);
                return;
            }
        }

        var bleNote = why is not null
            ? $"{reason} BLE (küçük dosya) yolu da kullanılamıyor: {why}."
            : $"{reason} BLE yolu bu eşle başka bir aktarım için kullanılıyor.";
        if (string.IsNullOrWhiteSpace(_o.Settings.RelayUrl) || _o.LocalKey is null)
            throw new TransferException(bleNote);   // aktarma kapalı (RelayUrl boş): "yol yok"
        await RunRelayAsync(s, session, bleNote, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- aktarma sunucusu (§6.3)

    /// <summary>Aktarma onayını yanıtlar (<see cref="RelayApprovalRequested"/>).</summary>
    public void RespondToRelay(TransferId id, bool allow)
    {
        State? s;
        lock (_lock) _states.TryGetValue(id, out s);
        s?.RelayDecision.TrySetResult(allow);
    }

    /// <summary>
    /// §6.3: kullanıcı onayı → token + <c>relay</c> → odaya bağlan, yanıtlayan olarak el sıkış → §5 çerçeveleri.
    /// iPhone kopar/oda kapanırsa yeni token ile en çok <b>1</b> yeniden deneme (RESUME ile kaldığı yerden).
    /// </summary>
    private async Task RunRelayAsync(State s, PeerSession session, string whyNoOtherPath, CancellationToken ct)
    {
        if (RelayRoom.BuildRoomUri(_o.Settings.RelayUrl, new string('0', RelayRoom.RoomIdLength), "pc") is null)
            throw new TransferException($"{whyNoOtherPath} Aktarma sunucusu adresi geçersiz (Ayarlar'da wss://… biçiminde girin).");

        // PC her iki rolde de kendi kullanıcısına sorar; onaylanmadan aktarma sunucusuna hiç bağlanılmaz.
        s.Touch(_o.Time);
        Update(s, TransferPhase.Connecting);
        RelayApprovalRequested?.Invoke(new RelayApprovalRequest(s.Id, s.PeerId, s.PeerName, s.Count, s.Total));
        bool allowed;
        try { allowed = await s.RelayDecision.Task.WaitAsync(_o.Timeouts.RelayApproval, ct).ConfigureAwait(false); }
        catch (TimeoutException) { allowed = false; }
        if (!allowed)
        {
            PlatformLog.Write($"aktarma yolu onaylanmadı {s.Id}");
            await SendSignalQuietly(session, new CancelSignalMessage(s.Id)).ConfigureAwait(false);
            Finish(s, TransferPhase.Canceled, "Aktarma sunucusu üzerinden gönderim onaylanmadı.");
            return;
        }

        // TCP kaydı kapanır: geç gelen bir TCP bağlantısı ikinci bir oturum açmasın.
        s.Registration?.Dispose();
        s.Registration = null;

        var lastFailure = "";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await TryRelayAttemptAsync(s, session, ct).ConfigureAwait(false)) return;
                lastFailure = "Aktarma bağlantısı koptu.";
            }
            catch (RelayConnectException ex)
            {
                if (ex.Status == System.Net.HttpStatusCode.TooManyRequests) throw new TransferException($"{ex.Message} Yol yok.");
                lastFailure = ex.Message;
            }
            catch (TimeoutException ex)
            {
                lastFailure = ex.Message;
            }
            catch (RelayFailedException)
            {
                lastFailure = "iPhone aktarma sunucusuna bağlanamadı (relay_fail).";
            }
            catch (Exception ex) when (ex is IOException or ProtocolException or NoiseException or ObjectDisposedException or PeerNotTrustedException)
            {
                lastFailure = "Aktarma el sıkışması başarısız: " + ex.Message;
            }
            PlatformLog.Write($"aktarma denemesi {attempt + 1}/2 başarısız {s.Id}: {lastFailure}");
            s.Touch(_o.Time);
        }
        throw new TransferException($"{lastFailure} {whyNoOtherPath} Aktarma yolu da başarısız. Yol yok.");
    }

    /// <summary>iPhone <c>relay_fail</c> bildirdi (oda denemesi iptal).</summary>
    private sealed class RelayFailedException : Exception;

    /// <returns>true: aktarım bitti (sonuç işlendi); false: bağlantı koptu, yeniden denenebilir.</returns>
    private async Task<bool> TryRelayAttemptAsync(State s, PeerSession session, CancellationToken ct)
    {
        var token = RelayRoom.NewToken();
        var uri = RelayRoom.BuildRoomUri(_o.Settings.RelayUrl, RelayRoom.ComputeRoomId(token), "pc")
            ?? throw new TransferException("Aktarma sunucusu adresi geçersiz.");

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        s.BeginRelayAttempt(attemptCts);
        RelayPcSession? relay = null;
        try
        {
            try
            {
                relay = await RelayPcClient.AcceptAsync(uri, s.Id, _o.LocalKey!, s.PeerKey, session.Channel.Kind,
                    // Önce odaya gir, sonra eşe bildir: iPhone geldiğinde oda hazır olsun.
                    sendCt => session.Channel.SendSignalAsync(new RelayMessage(s.Id, RelayRoom.EncodeToken(token)), sendCt).AsTask(),
                    _o.Timeouts.RelayPeer, attemptCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RelayFailedException();
            }
            PlatformLog.Write($"aktarma odası kuruldu {s.Id}");
            await OnDataConnectionAsync(s, relay.Channel).ConfigureAwait(false);
            await relay.CloseAsync().ConfigureAwait(false);
            if (s.FailureMessage is { } failure) throw new TransferException(failure);
            if (s.DataDone.Task.IsCompletedSuccessfully)
            {
                FinishFromResult(s, s.DataDone.Task.Result);
                return true;
            }
            ct.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            s.EndRelayAttempt();
            relay?.Dispose();
        }
    }

    /// <summary>Yol kurulamadı (BLE yoluna düşülebilir); ileti kullanıcıya "yol yok" nedeni olarak gösterilir.</summary>
    private sealed class PathUnavailableException(string message) : TransferException(message);

    private async Task WaitForDataAsync(State s)
    {
        var ct = s.Cts.Token;
        while (true)
        {
            var delay = Task.Delay(_o.Timeouts.Poll, ct);
            await Task.WhenAny(s.DataDone.Task, s.PathFailed.Task, delay).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (s.DataDone.Task.IsCompleted)
            {
                var result = await s.DataDone.Task.ConfigureAwait(false);   // TransferException olabilir
                FinishFromResult(s, result);
                return;
            }
            if (s.PathFailed.Task.IsCompleted && !s.ConnectionActive)
                throw new PathUnavailableException(
                    "Doğrudan bağlantı kurulamadı (iPhone bildirdi: " + s.PathFailed.Task.Result + "). Yol yok.");

            if (!s.ConnectionActive)
            {
                var limit = s.EverConnected ? _o.Timeouts.Reconnect : s.ConnectTimeout;
                if (_o.Time.GetUtcNow() - s.LastActivity > limit)
                {
                    if (!s.EverConnected)
                        throw new PathUnavailableException("iPhone veri bağlantısını kuramadı (zaman aşımı). Yol yok.");
                    throw new TransferException("Bağlantı koptu ve iPhone yeniden bağlanmadı.");
                }
            }
        }
    }

    private void FinishFromResult(State s, TransferResult r)
    {
        if (s.Role == TransferRole.Receiver && r.ReceivedFiles.Count > 0) s.FirstFilePath = r.ReceivedFiles[0].StoredPath;
        switch (r.Outcome)
        {
            case TransferOutcome.Completed:
                Finish(s, TransferPhase.Completed, null, SavedPathOf(s, r));
                break;
            case TransferOutcome.CompletedWithFailures:
                Finish(s, TransferPhase.Failed, $"{r.FailedFileIds.Count} dosya doğrulanamadı (SHA-256 uyuşmadı).", SavedPathOf(s, r));
                break;
            case TransferOutcome.Canceled:
                Finish(s, TransferPhase.Canceled, r.CanceledByRemote || s.RemoteCanceled ? "Karşı taraf iptal etti." : null);
                break;
            default:
                Finish(s, TransferPhase.Failed, r.Error is { Length: > 0 } e ? $"Aktarım kesildi: {e}" : "Aktarım kesildi.");
                break;
        }
    }

    private string? SavedPathOf(State s, TransferResult r)
    {
        if (s.Role != TransferRole.Receiver || r.ReceivedFiles.Count == 0) return null;
        return SavedRootOf(r.ReceivedFiles, _o.Settings.DownloadFolder);
    }

    /// <summary>Tek dosya → dosyanın yolu; hepsi indirme klasöründeki tek bir alt klasörün içindeyse (§5a klasör aktarımı) o klasör; aksi halde indirme klasörü.</summary>
    internal static string SavedRootOf(IReadOnlyList<ReceivedFile> received, string downloadFolder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(downloadFolder)) + Path.DirectorySeparatorChar;
        string? top = null;
        foreach (var f in received)
        {
            var full = Path.GetFullPath(f.StoredPath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return downloadFolder;
            var rel = full[root.Length..];
            var sep = rel.IndexOf(Path.DirectorySeparatorChar);
            if (sep < 0) return received.Count == 1 ? f.StoredPath : downloadFolder;
            var first = rel[..sep];
            if (top is not null && !string.Equals(top, first, StringComparison.OrdinalIgnoreCase)) return downloadFolder;
            top = first;
        }
        return top is null ? downloadFolder : Path.Combine(root, top);
    }

    // ---------------------------------------------------------------- iptal / bekleme

    /// <summary>Aktarımı iptal eder (BLE oturumu açıksa karşıya <c>cancel</c> da gider).</summary>
    public void Cancel(TransferId id)
    {
        State? s;
        PeerSession? session = null;
        lock (_lock)
        {
            if (!_states.TryGetValue(id, out s)) return;
            _sessions.TryGetValue(s.PeerId, out session);
        }
        if (s.IsTerminal) return;
        s.Cts.Cancel();
        if (session is not null) _ = SendSignalQuietly(session, new CancelSignalMessage(id));
    }

    /// <summary>Aktarım bitene kadar bekler (test ve CLI için).</summary>
    public Task<TransferSnapshot> WaitAsync(TransferId id)
    {
        lock (_lock)
        {
            return _states.TryGetValue(id, out var s) ? s.Completion.Task : Task.FromException<TransferSnapshot>(new KeyNotFoundException());
        }
    }

    public IReadOnlyList<TransferSnapshot> Transfers
    {
        get
        {
            State[] list;
            lock (_lock) list = [.. _states.Values];
            return list.Select(Snapshot).ToArray();
        }
    }

    // ---------------------------------------------------------------- yardımcılar

    private async Task RunGuardedAsync(State s, Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);
            if (!s.IsTerminal) Finish(s, TransferPhase.Failed, "Aktarım beklenmedik şekilde sona erdi.");
        }
        catch (OperationCanceledException)
        {
            if (s.FailureMessage is { } msg) Finish(s, TransferPhase.Failed, msg);
            else Finish(s, TransferPhase.Canceled, s.RemoteCanceled ? "Karşı taraf iptal etti." : null);
        }
        catch (TransferException ex)
        {
            PlatformLog.Write($"aktarım hatası {s.Id}: {ex.Message}");
            Finish(s, TransferPhase.Failed, ex.Message);
            await NotifyPeerCanceledAsync(s).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ProtocolException or NoiseException or ObjectDisposedException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            PlatformLog.Write($"aktarım hatası {s.Id}: {ex.GetType().Name}: {ex.Message}");
            Finish(s, TransferPhase.Failed, "Aktarım başarısız: " + ex.Message);
            await NotifyPeerCanceledAsync(s).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PlatformLog.Write($"aktarım beklenmeyen hata {s.Id}: {ex}");
            Finish(s, TransferPhase.Failed, "Aktarım başarısız (beklenmeyen hata).");
        }
        finally
        {
            s.Registration?.Dispose();
            if (s.HotspotRequested) _o.Hotspot?.TransferFinished();
            if (s.HotspotSent && !s.KnownAtStart)
            {
                // §6.2: eşli/tanıdık olmayan eş SSID + parolayı öğrendi → aktarım bitti/iptal; kalıcı parola yenilenir.
                try { _o.RotateHotspotPassphrase?.Invoke(); PlatformLog.Write("Etkin nokta parolası yenilendi (bilinmeyen eş)."); }
                catch (Exception ex) { PlatformLog.Write($"Etkin nokta parolası yenilenemedi: {ex.Message}"); }
            }
            ReleaseBle(s);
        }
    }

    private async Task NotifyPeerCanceledAsync(State s)
    {
        PeerSession? session;
        lock (_lock) _sessions.TryGetValue(s.PeerId, out session);
        if (session is not null) await SendSignalQuietly(session, new CancelSignalMessage(s.Id)).ConfigureAwait(false);
    }

    private static async Task SendSignalQuietly(PeerSession session, SignalMessage message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await session.Channel.SendSignalAsync(message, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ProtocolException or InvalidOperationException
                                       or ObjectDisposedException or NoiseException)
        {
            // en iyi çaba
        }
    }

    private void Register(State s)
    {
        lock (_lock) AddLocked(s);
        s.Touch(_o.Time);
        Raise(s);
    }

    private void AddLocked(State s)
    {
        _states[s.Id] = s;
        if (_states.Count > MaxRememberedTransfers)
        {
            foreach (var old in _states.Values.Where(x => x.IsTerminal).OrderBy(x => x.LastActivity).Take(_states.Count - MaxRememberedTransfers).ToArray())
                _states.Remove(old.Id);
        }
    }

    private void Update(State s, TransferPhase phase)
    {
        lock (s.Gate)
        {
            if (s.IsTerminal) return;
            s.Phase = phase;
        }
        Raise(s);
    }

    private void Finish(State s, TransferPhase phase, string? error, string? savedPath = null)
    {
        lock (s.Gate)
        {
            if (s.IsTerminal) return;
            s.Phase = phase;
            s.Error = error;
            s.SavedPath = savedPath;
            if (phase == TransferPhase.Completed) s.Progress = 1;
            s.Speed = 0;
        }
        PlatformLog.Write($"aktarım bitti {s.Id}: {phase}{(error is null ? "" : " — " + error)}");
        var snap = Snapshot(s);
        try { TransferUpdated?.Invoke(snap); }
        catch (Exception ex) { PlatformLog.Write($"TransferUpdated işleyicisi hata verdi: {ex.Message}"); }
        s.Completion.TrySetResult(snap);
    }

    private void Raise(State s)
    {
        try { TransferUpdated?.Invoke(Snapshot(s)); }
        catch (Exception ex) { PlatformLog.Write($"TransferUpdated işleyicisi hata verdi: {ex.Message}"); }
    }

    private static TransferSnapshot Snapshot(State s)
    {
        lock (s.Gate)
        {
            return new TransferSnapshot(s.Id, s.Role, s.PeerId, s.PeerName, s.Count, s.Total, s.Phase, s.Progress, s.Speed, s.SavedPath, s.Error, s.Note, s.FirstFilePath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        State[] list;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_o.BluetoothAudio is { } monitor) monitor.Changed -= OnLocalBtAudioChanged;
            list = [.. _states.Values];
        }
        foreach (var s in list.Where(x => !x.IsTerminal))
        {
            try { s.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        foreach (var s in list)
        {
            try { await s.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { }
        }
        _push.Dispose();
    }

    private sealed class SyncProgress(Action<TransferProgress> onReport) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => onReport(value);
    }

    private sealed class State(TransferId id, TransferRole role, string peerId, string peerName, byte[] peerKey, int count, long total)
    {
        public TransferId Id { get; } = id;
        public TransferRole Role { get; } = role;
        public string PeerId { get; } = peerId;
        public string PeerName { get; } = peerName;
        public byte[] PeerKey { get; } = peerKey;
        public int Count { get; } = count;
        public long Total { get; } = total;

        public object Gate { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public TaskCompletionSource<bool> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> UiDecision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TransferResult> DataDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> PathFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TransferSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<IFileSource>? Files;
        public IReadOnlyList<string> Preview = [];
        public FileSystemPartialStore? Store;
        public IDisposable? Registration;
        public string? RejectReason;
        public bool HotspotRequested;
        public volatile bool HotspotSent;
        /// <summary>Teklif/gönderim anında eş eşli veya tanıdık mıydı (onay sonrası "tanıdık" yapılması bunu değiştirmez).</summary>
        public bool KnownAtStart;
        public int Generation;
        /// <summary>§6.4: bu aktarımın BLE veri kanalı (etkinleştirildiyse).</summary>
        public volatile BleDataChannel? Ble;
        public TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

        public TaskCompletionSource<bool> RelayDecision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _relayAttempt;

        /// <summary>Yeni aktarma denemesi başlar: iPhone'un <c>relay_fail</c>'i bu belirteci iptal eder.</summary>
        public void BeginRelayAttempt(CancellationTokenSource attempt)
        {
            lock (Gate) _relayAttempt = attempt;
        }

        public void EndRelayAttempt()
        {
            lock (Gate) _relayAttempt = null;
        }

        public void ReportRelayFail()
        {
            CancellationTokenSource? attempt;
            lock (Gate) attempt = _relayAttempt;
            try { attempt?.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>§6.5: push bildirimi gönderildi/gönderiliyor, telefonun BLE oturumu bekleniyor.</summary>
        public volatile bool PushWaiting;
        public TaskCompletionSource<PeerSession> PhoneArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile TransferNote Note;

        public volatile bool ConnectionActive;
        public volatile bool EverConnected;
        public volatile bool RemoteCanceled;
        public volatile string? FailureMessage;

        private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;

        public TransferPhase Phase = TransferPhase.Negotiating;
        public double Progress;
        public double Speed;
        public string? SavedPath;
        public string? FirstFilePath;
        public string? Error;

        public bool IsTerminal
        {
            get { lock (Gate) return Phase >= TransferPhase.Completed; }
        }

        public DateTimeOffset LastActivity
        {
            get { lock (Gate) return _lastActivity; }
        }

        public void Touch(TimeProvider time)
        {
            lock (Gate) _lastActivity = time.GetUtcNow();
        }

        public void Fail(string message)
        {
            FailureMessage ??= message;
            try { Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
