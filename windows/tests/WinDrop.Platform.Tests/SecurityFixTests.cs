using System.Buffers.Text;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using Xunit;

namespace WinDrop.Platform.Tests;

internal static class Poll
{
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + T.Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(10);
        }
    }
}

/// <summary>PROTOCOL §6.2: etkin nokta parolası eşli/tanıdık olmayan eş için aktarım sonunda yenilenir.</summary>
public class HotspotRotationTests
{
    private static string WriteSendFile(Harness h)
    {
        var path = Path.Combine(h.Dir.Path, "send-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, T.Random(2000, 5));
        return path;
    }

    private static async Task<HotspotMessage> SenderFlowUntilHotspotFailAsync(Harness h, Phone phone)
    {
        var snap = h.Coord.Send(h.PhoneId, [WriteSendFile(h)]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        var hs = Assert.IsType<HotspotMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new PathMessage(offer.TransferId, PathResult.HotspotFail));
        Assert.Equal(TransferPhase.Failed, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
        return hs;
    }

    [Fact]
    public void RotatePassphrase_KeepsSsid_ChangesPassphrase_AndPersists()
    {
        using var dir = new TempDir();
        var store = new HotspotConfigStore(dir.Path);
        var a = store.GetOrCreate();
        var b = store.RotatePassphrase();
        Assert.Equal(a.Ssid, b.Ssid);
        Assert.NotEqual(a.Passphrase, b.Passphrase);
        Assert.True(HotspotConfigStore.IsValid(b.Ssid, b.Passphrase));
        Assert.Equal(b, store.GetOrCreate());
        Assert.Equal(b, new HotspotConfigStore(dir.Path).GetOrCreate());   // diskte kalıcı
    }

    [Fact]
    public async Task UnknownPeer_Receiver_PassphraseRotatedAfterTransfer_EvenAfterApproval()
    {
        await using var h = new Harness(trustPhone: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.None, xx: true);
        h.Coord.OfferReceived += o => h.Coord.RespondToOffer(o.Id, true);   // kullanıcı onaylar (cihaz tanıdık olur)
        var before = h.HotspotCfg.GetOrCreate();

        var id = TransferId.Create();
        await phone.SendAsync(new OfferMessage(id, 1, 10, ["x"]));
        Assert.IsType<AcceptMessage>(await phone.ReceiveAsync());
        var hs = Assert.IsType<HotspotMessage>(await phone.ReceiveAsync());
        Assert.Equal(before.Passphrase, hs.Pass);                           // bu aktarım için mevcut parola gider
        await phone.SendAsync(new PathMessage(id, PathResult.HotspotFail));
        Assert.Equal(TransferPhase.Failed, (await h.Coord.WaitAsync(id).WaitAsync(T.Wait)).Phase);

        await Poll.UntilAsync(() => h.HotspotCfg.GetOrCreate().Passphrase != before.Passphrase, "parola yenilenmedi");
        var after = h.HotspotCfg.GetOrCreate();
        Assert.Equal(before.Ssid, after.Ssid);
        Assert.True(HotspotConfigStore.IsValid(after.Ssid, after.Passphrase));
        Assert.True(h.Trusted.List().Single().IsFamiliar);                   // onay sonrası tanıdık, ama başlangıçta bilinmiyordu
    }

    [Fact]
    public async Task UnknownPeer_Sender_PassphraseRotatedAfterTransfer()
    {
        await using var h = new Harness(trustPhone: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.None, xx: true);
        var before = h.HotspotCfg.GetOrCreate();

        var hs = await SenderFlowUntilHotspotFailAsync(h, phone);
        Assert.Equal(before.Passphrase, hs.Pass);
        await Poll.UntilAsync(() => h.HotspotCfg.GetOrCreate().Passphrase != before.Passphrase, "parola yenilenmedi");
        Assert.Equal(before.Ssid, h.HotspotCfg.GetOrCreate().Ssid);
    }

    [Fact]
    public async Task UnknownPeer_Canceled_PassphraseAlsoRotated()
    {
        await using var h = new Harness(trustPhone: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.None, xx: true);
        var before = h.HotspotCfg.GetOrCreate();

        var snap = h.Coord.Send(h.PhoneId, [WriteSendFile(h)]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        Assert.IsType<HotspotMessage>(await phone.ReceiveAsync());
        h.Coord.Cancel(snap.Id);
        Assert.Equal(TransferPhase.Canceled, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
        await Poll.UntilAsync(() => h.HotspotCfg.GetOrCreate().Passphrase != before.Passphrase, "parola yenilenmedi");
    }

    [Fact]
    public async Task UnknownPeer_NoHotspotSent_NoRotation()
    {
        await using var h = new Harness(trustPhone: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected, xx: true);   // Wi-Fi'da: addr yolu, etkin nokta yok
        var before = h.HotspotCfg.GetOrCreate();
        var snap = h.Coord.Send(h.PhoneId, [WriteSendFile(h)]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        h.Coord.Cancel(snap.Id);
        await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        await Task.Delay(150);
        Assert.Equal(before, h.HotspotCfg.GetOrCreate());
    }

    [Theory]
    [InlineData(false)]   // QR ile eşli
    [InlineData(true)]    // tanıdık
    public async Task PairedOrFamiliarPeer_PassphraseStaysStable(bool familiar)
    {
        await using var h = new Harness(trustPhone: false);
        h.Trusted.Add(h.PhoneKey.PublicKey, "iPhone", familiar: familiar);
        using var phone = await h.ConnectPhoneAsync(WifiState.None);
        var before = h.HotspotCfg.GetOrCreate();

        var hs = await SenderFlowUntilHotspotFailAsync(h, phone);
        Assert.Equal(before.Passphrase, hs.Pass);
        await Poll.UntilAsync(() => h.Hotspot.Finished == 1, "etkin nokta bitişi bildirilmedi");
        await Task.Delay(150);
        Assert.Equal(before, h.HotspotCfg.GetOrCreate());
    }
}

/// <summary>Bilinmeyen (XX) teklifler için PeerId'ye bağlı olmayan genel sınır.</summary>
public class UnknownOfferGlobalLimitTests
{
    private static async Task WaitAllTerminalAsync(Harness h) =>
        await Poll.UntilAsync(() => h.Coord.Transfers.All(t => t.IsTerminal), "aktarımlar bitmedi");

    [Fact]
    public async Task OnlyOnePendingUnknownOffer_AtATime_EvenWithNewKeys()
    {
        await using var h = new Harness(trustPhone: false);
        using var key2 = NoiseKeyPair.Generate();
        using var phone1 = await h.ConnectPhoneAsync(WifiState.Connected, xx: true);
        using var phone2 = await h.ConnectPhoneAsync(WifiState.Connected, key2, xx: true);
        var offered = new TaskCompletionSource<OfferRequest>();
        h.Coord.OfferReceived += o => offered.TrySetResult(o);

        await phone1.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["x"]));
        var first = await offered.Task.WaitAsync(T.Wait);          // onay bekliyor

        await phone2.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["y"]));
        Assert.Equal("busy", Assert.IsType<RejectMessage>(await phone2.ReceiveAsync()).Reason);
        Assert.Single(h.Coord.Transfers);                           // ikinci teklif kaydedilmedi

        h.Coord.RespondToOffer(first.Id, false);
        Assert.Equal("declined", Assert.IsType<RejectMessage>(await phone1.ReceiveAsync()).Reason);
        await WaitAllTerminalAsync(h);

        var again = new TaskCompletionSource<OfferRequest>();
        h.Coord.OfferReceived += o => again.TrySetResult(o);
        await phone2.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["y"]));
        await again.Task.WaitAsync(T.Wait);                         // bekleyen kalmayınca yeni anahtar da sıraya girer
    }

    [Fact]
    public async Task UnknownOffers_AreLimitedToThreePerMinuteInTotal_AcrossKeys()
    {
        await using var h = new Harness(trustPhone: false);
        h.Coord.OfferReceived += o => h.Coord.RespondToOffer(o.Id, false);
        var keys = new List<NoiseKeyPair>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var key = NoiseKeyPair.Generate();
                keys.Add(key);
                using var phone = await h.ConnectPhoneAsync(WifiState.Connected, key, xx: true);   // her teklif yeni anahtar/PeerId
                await phone.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["x"]));
                var reject = Assert.IsType<RejectMessage>(await phone.ReceiveAsync());
                Assert.Equal(i < 3 ? "declined" : "rate_limited", reject.Reason);
                await WaitAllTerminalAsync(h);
            }
        }
        finally
        {
            foreach (var k in keys) k.Dispose();
        }
    }

    [Fact]
    public async Task FamiliarPeer_KeepsPerPeerLimit_NotAffectedByUnknownPending()
    {
        await using var h = new Harness(trustPhone: false);
        using var key2 = NoiseKeyPair.Generate();
        h.Trusted.Add(key2.PublicKey, "Tanıdık", familiar: true);
        using var unknown = await h.ConnectPhoneAsync(WifiState.Connected, xx: true);
        using var familiar = await h.ConnectPhoneAsync(WifiState.Connected, key2);
        var offers = new List<OfferRequest>();
        h.Coord.OfferReceived += o => { lock (offers) offers.Add(o); };

        await unknown.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["x"]));
        await Poll.UntilAsync(() => { lock (offers) return offers.Count == 1; }, "bilinmeyen teklif gelmedi");
        await familiar.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["y"]));   // bekleyen bilinmeyen tanıdığı engellemez
        await Poll.UntilAsync(() => { lock (offers) return offers.Count == 2; }, "tanıdık teklif gelmedi");
    }
}

/// <summary>QR sırrı atomik tüketilir; onay ekranı el sıkışma özetinden 4 haneli kod alır.</summary>
public class PairingSecretConsumeTests
{
    private static byte[] PskOf(string qr)
    {
        var q = new Uri(qr).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        return Base64Url.DecodeFromChars(q["s"]);
    }

    [Fact]
    public async Task TryConsume_SucceedsOnce_AndFailsForSupersededOrCanceledMode()
    {
        await using var h = new Harness(trustPhone: false);
        var first = h.Pairing.Start();
        var second = h.Pairing.Start();                 // ilkini geçersiz kılar
        Assert.False(h.Pairing.TryConsume(first));
        Assert.True(h.Pairing.TryConsume(second));
        Assert.False(h.Pairing.TryConsume(second));     // tek kullanımlık
        Assert.False(h.Pairing.IsActive);

        var third = h.Pairing.Start();
        third.Cancel();
        Assert.False(h.Pairing.TryConsume(third));
    }

    [Fact]
    public async Task TryConsume_Concurrent_ExactlyOneWinner()
    {
        await using var h = new Harness(trustPhone: false);
        var mode = h.Pairing.Start();
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => h.Pairing.TryConsume(mode))));
        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task TwoConnectionsWithSameQrSecret_OnlyFirstReachesApproval()
    {
        await using var h = new Harness(trustPhone: false);
        var mode = h.Pairing.Start();
        var psk = PskOf(mode.QrPayload);
        var requests = new List<PairingRequestInfo>();
        h.Pairing.PairingRequested += r => { lock (requests) requests.Add(r); };
        using var keyB = NoiseKeyPair.Generate();

        // İki bağlantı da QR açıkken kabul edilir (mod ikisinde de yakalanır); yarışı TryConsume çözer.
        var (phoneA, pcA) = MemoryBleLink.CreatePair();
        var (phoneB, pcB) = MemoryBleLink.CreatePair();
        var runA = h.Acceptor.RunLinkAsync(pcA, CancellationToken.None);
        var runB = h.Acceptor.RunLinkAsync(pcB, CancellationToken.None);

        using var chA = await SecureChannel.ConnectPairingAsync(new BleMessageTransport(phoneA), Prefix.CreateBle(), h.PhoneKey, psk,
            k => k.Span.SequenceEqual(h.PcPublicKey)).WaitAsync(T.Wait);
        await chA.SendSignalAsync(new HelloMessage(1, "A", [], null, false));
        await Poll.UntilAsync(() => { lock (requests) return requests.Count == 1; }, "A'nın isteği gelmedi");

        using var chB = await SecureChannel.ConnectPairingAsync(new BleMessageTransport(phoneB), Prefix.CreateBle(), keyB, psk,
            k => k.Span.SequenceEqual(h.PcPublicKey)).WaitAsync(T.Wait);
        try { await chB.SendSignalAsync(new HelloMessage(1, "B", [], null, false)); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ProtocolException or NoiseException) { }
        await runB.WaitAsync(T.Wait);                    // B'nin bağlantısı onaya varmadan kapatıldı

        PairingRequestInfo req;
        lock (requests) req = Assert.Single(requests);
        Assert.Equal("A", req.DeviceName);
        Assert.Equal(Sas.Compute(chA.HandshakeHash), req.VerificationCode);   // onay ekranındaki kod = el sıkışma özeti
        Assert.Matches("^[0-9]{4}$", req.VerificationCode!);

        h.Pairing.Respond(req.RequestId, true);
        Assert.IsType<HelloMessage>(await chA.ReceiveSignalAsync().AsTask().WaitAsync(T.Wait));
        Assert.IsType<PairInfoMessage>(await chA.ReceiveSignalAsync().AsTask().WaitAsync(T.Wait));
        phoneA.Close();
        await runA.WaitAsync(T.Wait);

        var device = Assert.Single(h.Trusted.List());
        Assert.Equal(h.PhoneKey.PublicKey, device.PublicKey);
    }
}

/// <summary>Açılışta ve periyodik eski kısmi dizin temizliği.</summary>
public class RuntimePurgeTests
{
    private static string MakeStale(string root, string peer, int ageDays)
    {
        var dir = Path.Combine(root, "partial", peer, TransferId.Create().ToString());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "1.done");
        File.WriteAllText(file, "{}");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-ageDays));
        return dir;
    }

    private static WinDropRuntime NewRuntime(TempDir dir) =>
        new(new AppSettings { DownloadFolder = Path.Combine(dir.Path, "dl") }, dir.Path, dataPort: 0, enableBle: false);

    [Fact]
    public async Task StartAsync_PurgesPartialsOlderThan24Hours_KeepsFresh()
    {
        using var dir = new TempDir();
        var stale = MakeStale(dir.Path, "aa11", 2);
        var fresh = MakeStale(dir.Path, "bb22", 0);
        await using var rt = NewRuntime(dir);
        await rt.StartAsync();
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "partial", "aa11")));   // boşalan eş dizini de silinir
    }

    [Fact]
    public async Task PeriodicLoop_PurgesAgain()
    {
        using var dir = new TempDir();
        await using var rt = NewRuntime(dir);
        rt.PurgeInterval = TimeSpan.FromMilliseconds(50);
        await rt.StartAsync();
        var stale = MakeStale(dir.Path, "cc33", 3);   // başlangıçtan sonra oluşur
        await Poll.UntilAsync(() => !Directory.Exists(stale), "periyodik temizlik çalışmadı");
    }
}

/// <summary>MOTW taşımadan önce .part dosyasına yazılır; akış aynı sürücüde taşımayla gelir.</summary>
public class MotwBeforeMoveTests
{
    [Fact]
    public async Task BeforeCommitHook_MarksPart_AndMarkSurvivesMove()
    {
        using var dir = new TempDir();
        var part = Path.Combine(dir.Path, "1.part");
        var dest = Path.Combine(dir.Path, "a.bin");
        File.WriteAllText(part, "x");

        await new MotwCommitHook().OnBeforeCommitAsync(part, default);
        Assert.Contains("ZoneId=3", MarkOfTheWeb.ReadMark(part));    // taşımadan önce işaretli

        File.Move(part, dest);
        Assert.Contains("ZoneId=3", MarkOfTheWeb.ReadMark(dest));    // aynı sürücüde akış korunur (işaretsiz pencere yok)
    }
}
