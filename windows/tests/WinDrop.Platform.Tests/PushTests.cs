using System.Net;
using System.Text;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using Xunit;

namespace WinDrop.Platform.Tests;

/// <summary>Sahte push sunucusu: istekleri kaydeder, verilen durum kodunu döndürür.</summary>
internal sealed class FakePushHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, Uri Uri, string? ContentType, string Body);

    private readonly List<Call> _calls = [];
    public HttpStatusCode Status { get; set; } = status;
    public bool Throw { get; set; }

    public IReadOnlyList<Call> Calls
    {
        get { lock (_calls) return _calls.ToArray(); }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_calls) _calls.Add(new Call(request.Method, request.RequestUri!, request.Content?.Headers.ContentType?.MediaType, body));
        if (Throw) throw new HttpRequestException("ağ yok");
        return new HttpResponseMessage(Status) { Content = new StringContent("", Encoding.UTF8) };
    }
}

/// <summary>Elle ilerletilen saat (<see cref="Task.WaitAsync(TimeSpan, TimeProvider, CancellationToken)"/> zamanlayıcıları dahil).</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        t.Change(dueTime, period);
        return t;
    }

    /// <summary>Verilen kalan süreye sahip etkin bir zamanlayıcı var mı?</summary>
    public bool HasTimerDue(TimeSpan remaining)
    {
        lock (_gate) return _timers.Any(t => t.Due is { } d && d - _now == remaining);
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> fire;
        lock (_gate)
        {
            _now += by;
            fire = _timers.Where(t => t.Due is { } d && d <= _now).ToList();
        }
        foreach (var t in fire) t.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    Due = null;
                    return true;
                }
                Due = owner._now + dueTime;
                owner._timers.Add(this);
                return true;
            }
        }

        public void Fire()
        {
            lock (owner._gate)
            {
                if (Due is null) return;
                if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
                {
                    Due = null;
                    owner._timers.Remove(this);
                }
                else
                {
                    Due = owner._now + _period;
                }
            }
            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>PROTOCOL §6.5: pushId kaydı ve bildirimle uyandırma (uzaktaki tanıdık/eşli cihaza gönderim).</summary>
public class PushTests
{
    private const string PushId = "0123456789abcdef0123456789abcdef";
    private const string OtherPushId = "fedcba9876543210fedcba9876543210";

    private static string WriteFile(Harness h, string name, byte[] data)
    {
        var dir = Path.Combine(h.Dir.Path, "src");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, data);
        return p;
    }

    /// <summary>Koşulu sağlayan ilk anlık görüntüyü bekler (olay + geçmiş).</summary>
    private static async Task<TransferSnapshot> WaitSnapshotAsync(Harness h, TransferId id, Func<TransferSnapshot, bool> predicate)
    {
        var tcs = new TaskCompletionSource<TransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(TransferSnapshot s)
        {
            if (s.Id == id && predicate(s)) tcs.TrySetResult(s);
        }
        h.Coord.TransferUpdated += On;
        foreach (var s in h.Coord.Transfers) On(s);
        return await tcs.Task.WaitAsync(T.Wait);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + T.Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Koşul sağlanmadı.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Send_ToAbsentDevice_PostsPush_ThenResendsOfferWhenPhoneConnects()
    {
        var fake = new FakePushHandler();
        await using var h = new Harness(relayUrl: "relay.example.com", pushHandler: fake);
        Assert.True(h.Trusted.SetPushId(h.PhoneKey.PublicKey, PushId));
        Assert.True(h.Coord.CanPush(h.PhoneId));
        Assert.False(h.Coord.IsNearby(h.PhoneId));

        var data = T.Random(50_000, 9);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "photo.jpg", data)]);
        Assert.Equal(TransferRole.Sender, snap.Role);
        Assert.Equal("Test iPhone", snap.PeerName);

        var waiting = await WaitSnapshotAsync(h, snap.Id, s => s.Note == TransferNote.PushSent);
        Assert.Equal(TransferPhase.Negotiating, waiting.Phase);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal($"https://relay.example.com/v1/push/{PushId}", call.Uri.ToString());
        Assert.Equal("application/json", call.ContentType);
        Assert.Equal("""{"from":"TestPC"}""", call.Body);

        // Telefon bildirime dokunup BLE ile bağlanır: PC bekleyen teklifi aynı aktarımla yeniden gönderir.
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        Assert.Equal(snap.Id, offer.TransferId);
        Assert.Equal(["photo.jpg"], offer.Preview);
        var after = await WaitSnapshotAsync(h, snap.Id, s => s.Note == TransferNote.None && s.Phase == TransferPhase.Negotiating);
        Assert.Equal(TransferNote.None, after.Note);
        Assert.Single(fake.Calls);   // ikinci push yok

        h.Coord.Cancel(snap.Id);
        Assert.Equal(TransferPhase.Canceled, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
    }

    [Fact]
    public void PushNotifier_SanitizesName_AndBuildsUri()
    {
        Assert.Equal(40, PushNotifier.SanitizeFrom(new string('x', 100)).Length);
        Assert.Equal("WinDrop", PushNotifier.SanitizeFrom("\n\t "));
        Assert.Equal("ab", PushNotifier.SanitizeFrom("a\u0000b"));
        Assert.Equal("https://relay.example.com/v1/push/" + PushId, PushNotifier.BuildPushUri("wss://relay.example.com", PushId)!.ToString());
        Assert.Null(PushNotifier.BuildPushUri("http://relay.example.com", PushId));          // şifresiz yalnız yerel döngü
        Assert.NotNull(PushNotifier.BuildPushUri("http://127.0.0.1:8787", PushId));
        Assert.Null(PushNotifier.BuildPushUri("relay.example.com", "../x"));
    }

    [Fact]
    public async Task Server501_FailsWithPushDisabledMessage()
    {
        var fake = new FakePushHandler(HttpStatusCode.NotImplemented);
        await using var h = new Harness(relayUrl: "relay.example.com", pushHandler: fake);
        h.Trusted.SetPushId(h.PhoneKey.PublicKey, PushId);

        for (var i = 0; i < 2; i++)   // ikincide de aynı ileti (günlük satırı yalnız bir kez yazılır)
        {
            var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, $"a{i}.txt", T.Random(100, i))]);
            var done = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
            Assert.Equal(TransferPhase.Failed, done.Phase);
            Assert.Equal("Push bu sunucuda kapalı.", done.Error);
            Assert.Equal(TransferNote.None, done.Note);
        }
        Assert.Equal(2, fake.Calls.Count);
        Assert.True(h.Coord.CanPush(h.PhoneId));   // 501 pushId'yi silmez
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "çok fazla")]
    [InlineData(HttpStatusCode.BadGateway, "HTTP 502")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task Server429Or5xx_FailsWithErrorText(HttpStatusCode status, string expectedPart)
    {
        var fake = new FakePushHandler(status);
        await using var h = new Harness(relayUrl: "relay.example.com", pushHandler: fake);
        h.Trusted.SetPushId(h.PhoneKey.PublicKey, PushId);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(100))]);
        var done = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, done.Phase);
        Assert.Contains(expectedPart, done.Error);
        Assert.True(h.Coord.CanPush(h.PhoneId));
    }

    [Fact]
    public async Task Server404_ClearsStoredPushId()
    {
        var fake = new FakePushHandler(HttpStatusCode.NotFound);
        await using var h = new Harness(relayUrl: "relay.example.com", pushHandler: fake);
        h.Trusted.SetPushId(h.PhoneKey.PublicKey, PushId);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(100))]);
        Assert.Equal(TransferPhase.Failed, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
        Assert.Null(h.Trusted.FindByPublicKey(h.PhoneKey.PublicKey)!.PushId);
        Assert.False(h.Coord.CanPush(h.PhoneId));
    }

    [Fact]
    public async Task NetworkError_FailsWithUnreachableMessage()
    {
        var fake = new FakePushHandler { Throw = true };
        await using var h = new Harness(relayUrl: "relay.example.com", pushHandler: fake);
        h.Trusted.SetPushId(h.PhoneKey.PublicKey, PushId);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(100))]);
        var done = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, done.Phase);
        Assert.Contains("ulaşılamadı", done.Error);
    }

    [Fact]
    public async Task Send_ToAbsentDevice_WithoutPushId_OrWithoutRelayUrl_KeepsNotNearbyError()
    {
        var fake = new FakePushHandler();
        await using (var h = new Harness(relayUrl: "relay.example.com", pushHandler: fake))
        {
            var ex = Assert.Throws<TransferException>(() => h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(10))]));
            Assert.Contains("yakında değil", ex.Message);
            Assert.False(h.Coord.CanPush(h.PhoneId));
        }
        await using (var h = new Harness(relayUrl: "", pushHandler: fake))
        {
            h.Trusted.SetPushId(h.PhoneKey.PublicKey, PushId);
            Assert.False(h.Coord.CanPush(h.PhoneId));
            var ex = Assert.Throws<TransferException>(() => h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(10))]));
            Assert.Contains("yakında değil", ex.Message);
        }
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task PushId_FromKnownDevice_IsStored_AndSurvivesReload()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        await phone.SendAsync(new PushIdMessage(PushId));
        await WaitUntilAsync(() => h.Trusted.FindByPublicKey(h.PhoneKey.PublicKey)!.PushId == PushId);
        Assert.Equal(PushId, new TrustedDevices(Path.Combine(h.Dir.Path, "pc")).FindByPublicKey(h.PhoneKey.PublicKey)!.PushId);

        await phone.SendAsync(new PushIdMessage(OtherPushId));   // yeniden kayıt eskisini değiştirir
        await WaitUntilAsync(() => h.Trusted.FindByPublicKey(h.PhoneKey.PublicKey)!.PushId == OtherPushId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]    // büyük harf
    [InlineData("0123456789abcdef0123456789abcde")]     // 31
    [InlineData("0123456789abcdef0123456789abcdef0")]   // 33
    [InlineData("../../x/0123456789abcdef01234567")]    // yol karakterleri
    public async Task PushId_InvalidFormat_IsIgnored_SessionStaysOpen(string bad)
    {
        await using var h = new Harness(autoAccept: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        h.Coord.OfferReceived += o => h.Coord.RespondToOffer(o.Id, false);
        await phone.SendAsync(new PushIdMessage(bad));
        // Mesajlar sırayla işlenir: teklifin yanıtı geldiyse pushId de işlenmiştir ve oturum düşmemiştir.
        await phone.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["x"]));
        Assert.IsType<RejectMessage>(await phone.ReceiveAsync());
        Assert.Null(h.Trusted.FindByPublicKey(h.PhoneKey.PublicKey)!.PushId);
    }

    [Fact]
    public async Task PushId_FromUnknownDevice_IsRejected()
    {
        await using var h = new Harness(trustPhone: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected, xx: true);
        h.Coord.OfferReceived += o => h.Coord.RespondToOffer(o.Id, false);
        await phone.SendAsync(new PushIdMessage(PushId));
        await phone.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["x"]));
        Assert.IsType<RejectMessage>(await phone.ReceiveAsync());   // pushId işlendikten sonra
        Assert.Empty(h.Trusted.List());                              // kayıt oluşmadı (reddedilen tanıdık da olmaz)
        Assert.False(h.Coord.CanPush(h.PhoneId));
    }

    [Fact]
    public async Task PushId_Preserved_WhenDeviceReAdded()
    {
        await using var h = new Harness();
        h.Trusted.SetPushId(h.PhoneKey.PublicKey, PushId);
        h.Trusted.Add(h.PhoneKey.PublicKey, "Yeni ad");
        Assert.Equal(PushId, h.Trusted.FindByPublicKey(h.PhoneKey.PublicKey)!.PushId);
        Assert.False(h.Trusted.SetPushId(h.PhoneKey.PublicKey, "zz"));       // geçersiz biçim
        Assert.False(h.Trusted.SetPushId(new byte[32], PushId));             // kayıtsız cihaz
    }

    [Fact]
    public async Task WaitingForPhone_TimesOutAfterFiveMinutes_WithInjectedClock()
    {
        var clock = new ManualTimeProvider();
        var fake = new FakePushHandler();
        await using var h = new Harness(relayUrl: "relay.example.com", pushHandler: fake, time: clock);
        h.Trusted.SetPushId(h.PhoneKey.PublicKey, PushId);

        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(100))]);
        await WaitSnapshotAsync(h, snap.Id, s => s.Note == TransferNote.PushSent);
        await WaitUntilAsync(() => clock.HasTimerDue(TimeSpan.FromMinutes(5)));   // bekleme zamanlayıcısı kuruldu

        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        await Task.Delay(100);
        Assert.False(h.Coord.Transfers.Single(t => t.Id == snap.Id).IsTerminal);

        clock.Advance(TimeSpan.FromSeconds(2));
        var done = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, done.Phase);
        Assert.Equal("Telefon yanıt vermedi.", done.Error);
        Assert.Single(fake.Calls);
    }
}
