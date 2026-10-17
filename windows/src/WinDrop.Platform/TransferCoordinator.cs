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
    string? Error)
{
    public bool IsTerminal => Phase >= TransferPhase.Completed;
}

/// <summary>Kullanıcı onayı bekleyen gelen teklif; gönderen adı eşleştirme kaydındandır (PROTOCOL §0).</summary>
/// <param name="VerificationCode">XX oturumunda 4 haneli doğrulama kodu (PROTOCOL §2b); tanıdık/eşli (IK) cihazda null.</param>
public sealed record OfferRequest(TransferId Id, string PeerId, string PeerName, int FileCount, long TotalBytes,
    IReadOnlyList<string> Preview, string? VerificationCode = null, bool IsKnownPeer = false);

/// <summary>PC'nin BLE'de gördüğü yakındaki iPhone (iPhone'un kurduğu bağlantı + hello).</summary>
public sealed record NearbyPeer(string PeerId, string Name, bool IsKnown, bool IsPaired);

/// <summary>Anlaşılır (Türkçe) aktarım hatası; ileti kullanıcıya gösterilebilir.</summary>
public sealed class TransferException(string message) : InvalidOperationException(message);

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

    public Func<long>? FreeSpaceProvider { get; init; }
    public IFileCommitHook? CommitHook { get; init; }
    public TransferTimeouts Timeouts { get; init; } = new();
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// PROTOCOL §4/§6/§7 akışı: BLE sinyal oturumlarını (<see cref="HandleSessionAsync"/>) dinler; PC'den gönderme
/// (offer → accept → addr/hotspot → veri kanalı → DONE) ve PC'ye gelen (offer → onay → accept + addr/hotspot) yollarını yürütür.
/// Aynı anda birden çok aktarım desteklenir; her biri kendi iptal belirtecine sahiptir.
/// <para>TODO (henüz yok): aktarma (relay) yolu §6.3 ve küçük dosyalar için BLE yolu §6.4 — doğrudan yol/etkin nokta
/// başarısız olursa "yol yok" hatası verilir.</para>
/// </summary>
public sealed class TransferCoordinator : IAsyncDisposable
{
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
    private bool _disposed;

    public TransferCoordinator(TransferCoordinatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _o = options;
    }

    /// <summary>Her durum değişiminde (ekleme dahil). Herhangi bir iş parçacığında gelebilir.</summary>
    public event Action<TransferSnapshot>? TransferUpdated;

    /// <summary>Otomatik kabul dışı gelen teklif; yanıt için <see cref="RespondToOffer"/>.</summary>
    public event Action<OfferRequest>? OfferReceived;

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
                new HelloMessage(Prefix.CurrentVersion, _o.LocalName, ["tcp", "hotspot"], null, false), cts.Token).ConfigureAwait(false);
            while (true)
            {
                var msg = await session.Channel.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (msg is null) break;
                if (msg.Frame is not null)
                {
                    PlatformLog.Write("BLE oturumunda veri çerçevesi geldi (BLE veri yolu henüz yok); oturum kapatılıyor.");
                    break;
                }
                Dispatch(session, msg.Signal!);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ProtocolException or NoiseException
                                       or ObjectDisposedException or InvalidOperationException)
        {
            PlatformLog.Write($"BLE oturumu sonlandı: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(session.PeerId, out var cur) && ReferenceEquals(cur, session))
                    _sessions.Remove(session.PeerId);
            }
            FailNegotiating(session.PeerId, "Bluetooth bağlantısı koptu.");
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
                break;
            case StatusMessage st:
                if (st.Wifi is not null) session.Wifi = st.Wifi;
                session.BtAudio = st.BtAudio;
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
        if (p.Result is PathResult.TcpFail or PathResult.HotspotFail or PathResult.RelayFail && !s.ConnectionActive)
            s.PathFailed.TrySetResult(p.Result);
    }

    private void FailNegotiating(string peerId, string message)
    {
        State[] list;
        lock (_lock) list = [.. _states.Values.Where(s => s.PeerId == peerId)];
        foreach (var s in list)
        {
            // Veri kanalı bağımsızdır (TCP); yalnız pazarlık aşamasındakiler BLE'ye muhtaçtır.
            if (s.Phase is TransferPhase.Negotiating or TransferPhase.AwaitingApproval)
                s.Fail(message);
        }
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
        if (session is null) throw new TransferException("Cihaz yakında değil (Bluetooth bağlantısı yok).");

        var files = new List<IFileSource>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) throw new TransferException($"Klasör gönderme henüz desteklenmiyor: {Path.GetFileName(path)}");
            try { files.Add(new FileSystemFileSource(path)); }
            catch (FileNotFoundException) { throw new TransferException($"Dosya bulunamadı: {Path.GetFileName(path)}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new TransferException($"Dosya okunamadı: {Path.GetFileName(path)}");
            }
        }
        if (files.Count == 0) throw new TransferException("Gönderilecek dosya yok.");
        if (files.Count > TransferLimits.MaxFiles) throw new TransferException($"En çok {TransferLimits.MaxFiles} dosya gönderilebilir.");

        var state = new State(TransferId.Create(), TransferRole.Sender, session.PeerId, session.Name,
            (byte[])session.PublicKey.Clone(), files.Count, files.Sum(f => f.Size)) { Files = files, KnownAtStart = IsKnownPeer(session) };
        Register(state);
        _ = Task.Run(() => RunGuardedAsync(state, () => RunSenderAsync(state, session)));
        return Snapshot(state);
    }

    private async Task RunSenderAsync(State s, PeerSession session)
    {
        var ct = s.Cts.Token;
        s.Registration = _o.Pending.Register(s.Id, s.PeerKey, ch => OnDataConnectionAsync(s, ch), session.Channel.Kind);
        Update(s, TransferPhase.Negotiating);
        var preview = s.Files!.Take(SignalMessages.MaxPreview).Select(f => f.Name).ToArray();
        await session.Channel.SendSignalAsync(new OfferMessage(s.Id, s.Count, s.Total, preview), ct).ConfigureAwait(false);

        bool accepted;
        try { accepted = await s.Decision.Task.WaitAsync(_o.Timeouts.Accept, ct).ConfigureAwait(false); }
        catch (TimeoutException) { throw new TransferException("Karşı cihaz teklife yanıt vermedi."); }
        if (!accepted)
        {
            Finish(s, TransferPhase.Rejected, s.RejectReason is { Length: > 0 } r ? $"Karşı cihaz reddetti ({r})." : "Karşı cihaz reddetti.");
            return;
        }

        await ProvidePathAsync(s, session, ct).ConfigureAwait(false);
        await WaitForDataAsync(s).ConfigureAwait(false);
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
        await ProvidePathAsync(s, session, ct).ConfigureAwait(false);
        await WaitForDataAsync(s).ConfigureAwait(false);
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
                throw new TransferException("iPhone etkin nokta yolunu desteklemiyor ve bir Wi-Fi ağına bağlı değil. Yol yok.");
            if (_o.Hotspot is null || _o.HotspotConfig is null)
                throw new TransferException("iPhone bir Wi-Fi ağına bağlı değil ve etkin nokta yolu bu sürümde kullanılamıyor. Yol yok.");

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
                throw new TransferException("Windows Mobil etkin noktası açılamadı: " + ex.Message);
            }
            if (!started)
                throw new TransferException(
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
            if (ips.Count == 0) throw new TransferException("Bu bilgisayarda kullanılabilir ağ adresi yok (ağa bağlı değil).");
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

    private async Task OnDataConnectionAsync(State s, SecureChannel channel)
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
                throw new TransferException(
                    "Doğrudan bağlantı kurulamadı (iPhone bildirdi: " + s.PathFailed.Task.Result + ") ve aktarma yolu henüz yok. Yol yok.");   // TODO §6.3 relay

            if (!s.ConnectionActive)
            {
                var limit = s.EverConnected ? _o.Timeouts.Reconnect : s.ConnectTimeout;
                if (_o.Time.GetUtcNow() - s.LastActivity > limit)
                    throw new TransferException(s.EverConnected
                        ? "Bağlantı koptu ve iPhone yeniden bağlanmadı."
                        : "iPhone veri bağlantısını kuramadı (zaman aşımı). Aktarma yolu henüz yok. Yol yok.");   // TODO §6.3 relay
            }
        }
    }

    private void FinishFromResult(State s, TransferResult r)
    {
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
        return r.ReceivedFiles.Count == 1 ? r.ReceivedFiles[0].StoredPath : _o.Settings.DownloadFolder;
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
            return new TransferSnapshot(s.Id, s.Role, s.PeerId, s.PeerName, s.Count, s.Total, s.Phase, s.Progress, s.Speed, s.SavedPath, s.Error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        State[] list;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
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

        public List<IFileSource>? Files;
        public IReadOnlyList<string> Preview = [];
        public FileSystemPartialStore? Store;
        public IDisposable? Registration;
        public string? RejectReason;
        public bool HotspotRequested;
        public volatile bool HotspotSent;
        /// <summary>Teklif/gönderim anında eş eşli veya tanıdık mıydı (onay sonrası "tanıdık" yapılması bunu değiştirmez).</summary>
        public bool KnownAtStart;
        public int Generation;
        public TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

        public volatile bool ConnectionActive;
        public volatile bool EverConnected;
        public volatile bool RemoteCanceled;
        public volatile string? FailureMessage;

        private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;

        public TransferPhase Phase = TransferPhase.Negotiating;
        public double Progress;
        public double Speed;
        public string? SavedPath;
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
