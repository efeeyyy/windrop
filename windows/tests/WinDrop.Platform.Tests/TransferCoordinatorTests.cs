using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Core.Transfer;
using Xunit;

namespace WinDrop.Platform.Tests;

public class TransferCoordinatorTests
{
    private static string WriteFile(Harness h, string name, byte[] data)
    {
        var dir = Path.Combine(h.Dir.Path, "src");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, data);
        return p;
    }

    [Fact]
    public async Task PcSends_Offer_Accept_Addr_Tcp_Done()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        Assert.True(h.Coord.IsNearby(h.PhoneId));

        var data = T.Random(400_000, 3);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "photo.jpg", data)]);

        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        Assert.Equal(snap.Id, offer.TransferId);
        Assert.Equal(1, offer.Count);
        Assert.Equal(data.Length, offer.Total);
        Assert.Equal(["photo.jpg"], offer.Preview);

        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        var addr = Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        Assert.Equal(offer.TransferId, addr.TransferId);
        var cand = Assert.Single(addr.Candidates);
        Assert.Equal("127.0.0.1", cand.Ip);
        Assert.Equal(h.Listener.Port, cand.Port);

        var (channel, client) = await T.ConnectDataAsync(cand.Port, offer.TransferId, h.PhoneKey, h.PcPublicKey);
        using (client)
        using (channel)
        {
            await phone.SendAsync(new PathMessage(offer.TransferId, PathResult.TcpOk));
            var opts = new ReceiverOptions(h.PcId, offer.TransferId, new OfferInfo(offer.Count, offer.Total));
            var store = T.Store(h.Dir.Path, "phone");
            var rx = await new FileReceiver(channel, store, opts).RunAsync().WaitAsync(T.Wait);
            Assert.Equal(TransferOutcome.Completed, rx.Outcome);
            Assert.Equal(T.Sha(data), T.Sha(await File.ReadAllBytesAsync(Assert.Single(rx.ReceivedFiles).StoredPath)));
        }

        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Completed, final.Phase);
        Assert.Equal(TransferRole.Sender, final.Role);
        Assert.Equal("Test iPhone", final.PeerName);   // eşleştirme kaydındaki ad
        Assert.Equal(1.0, final.Progress);
        Assert.Equal(0, h.Hotspot.StartCalls);          // iPhone Wi-Fi'da: etkin nokta asla
    }

    [Fact]
    public async Task PcReceives_AutoAccept_Addr_Tcp_Done_WithMotw()
    {
        await using var h = new Harness(autoAccept: true);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var updates = new List<TransferSnapshot>();
        h.Coord.TransferUpdated += s => { lock (updates) updates.Add(s); };

        var data = T.Random(300_000, 5);
        var id = TransferId.Create();
        await phone.SendAsync(new OfferMessage(id, 1, data.Length, ["note.txt"]));
        Assert.IsType<AcceptMessage>(await phone.ReceiveAsync());
        var addr = Assert.IsType<AddrMessage>(await phone.ReceiveAsync());

        var (channel, client) = await T.ConnectDataAsync(addr.Candidates[0].Port, id, h.PhoneKey, h.PcPublicKey);
        using (client)
        using (channel)
        {
            var sent = await new FileSender(channel, [new MemoryFileSource("note.txt", data)]).RunAsync().WaitAsync(T.Wait);
            Assert.Equal(TransferOutcome.Completed, sent.Outcome);   // DONE alındı
        }

        var final = await h.Coord.WaitAsync(id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Completed, final.Phase);
        Assert.Equal(TransferRole.Receiver, final.Role);
        Assert.NotNull(final.SavedPath);
        Assert.StartsWith(h.DownloadDir, final.SavedPath);
        Assert.Equal(T.Sha(data), T.Sha(await File.ReadAllBytesAsync(final.SavedPath!)));
        Assert.Contains("ZoneId=3", MarkOfTheWeb.ReadMark(final.SavedPath!));
        lock (updates) Assert.Contains(updates, u => u.Phase == TransferPhase.Active);
    }

    [Fact]
    public async Task PcReceives_NeedsApproval_Accept()
    {
        await using var h = new Harness(autoAccept: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var offered = new TaskCompletionSource<OfferRequest>();
        h.Coord.OfferReceived += o => offered.TrySetResult(o);

        var data = T.Random(50_000, 6);
        var id = TransferId.Create();
        await phone.SendAsync(new OfferMessage(id, 1, data.Length, ["x.bin"]));
        var req = await offered.Task.WaitAsync(T.Wait);
        Assert.Equal("Test iPhone", req.PeerName);
        Assert.Equal(id, req.Id);
        h.Coord.RespondToOffer(req.Id, true);

        Assert.IsType<AcceptMessage>(await phone.ReceiveAsync());
        var addr = Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        var (channel, client) = await T.ConnectDataAsync(addr.Candidates[0].Port, id, h.PhoneKey, h.PcPublicKey);
        using (client)
        using (channel)
            Assert.Equal(TransferOutcome.Completed, (await new FileSender(channel, [new MemoryFileSource("x.bin", data)]).RunAsync().WaitAsync(T.Wait)).Outcome);
        Assert.Equal(TransferPhase.Completed, (await h.Coord.WaitAsync(id).WaitAsync(T.Wait)).Phase);
    }

    [Fact]
    public async Task PcReceives_NeedsApproval_Reject()
    {
        await using var h = new Harness(autoAccept: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var offered = new TaskCompletionSource<OfferRequest>();
        h.Coord.OfferReceived += o => offered.TrySetResult(o);
        var id = TransferId.Create();
        await phone.SendAsync(new OfferMessage(id, 1, 10, ["x"]));
        h.Coord.RespondToOffer((await offered.Task.WaitAsync(T.Wait)).Id, false);

        Assert.IsType<RejectMessage>(await phone.ReceiveAsync());
        Assert.Equal(TransferPhase.Rejected, (await h.Coord.WaitAsync(id).WaitAsync(T.Wait)).Phase);
    }

    [Fact]
    public async Task PcReceives_AutoAcceptLimit_AboveLimit_AsksUser()
    {
        await using var h = new Harness(autoAccept: true);
        h.Settings.AutoAcceptMaxBytes = 1000;
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var offered = new TaskCompletionSource<OfferRequest>();
        h.Coord.OfferReceived += o => offered.TrySetResult(o);
        await phone.SendAsync(new OfferMessage(TransferId.Create(), 1, 5000, ["big"]));
        Assert.Equal(5000, (await offered.Task.WaitAsync(T.Wait)).TotalBytes);
    }

    [Fact]
    public async Task PcReceives_OfferRateLimit_RejectsSixthWithin_Minute()
    {
        await using var h = new Harness(autoAccept: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        h.Coord.OfferReceived += o => h.Coord.RespondToOffer(o.Id, false);
        for (var i = 0; i < 5; i++)
        {
            await phone.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["x"]));
            Assert.Equal("declined", Assert.IsType<RejectMessage>(await phone.ReceiveAsync()).Reason);
        }
        await phone.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["x"]));
        Assert.Equal("rate_limited", Assert.IsType<RejectMessage>(await phone.ReceiveAsync()).Reason);
    }

    [Fact]
    public async Task PcSends_PeerRejects()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(100))]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new RejectMessage(offer.TransferId, "no"));
        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Rejected, final.Phase);
    }

    [Fact]
    public async Task WifiNone_StartsHotspot_SendsHotspotMessage_Transfers()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.None);
        var data = T.Random(120_000, 11);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "h.bin", data)]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));

        var hs = Assert.IsType<HotspotMessage>(await phone.ReceiveAsync());
        Assert.Equal(offer.TransferId, hs.TransferId);
        Assert.Equal("127.0.0.1", hs.Ip);
        Assert.Equal(h.Listener.Port, hs.Port);
        var cfg = h.HotspotCfg.GetOrCreate();   // eşleştirmesiz modda iPhone başka yoldan bilmez: ssid + pass her zaman gelir
        Assert.Equal(cfg.Ssid, hs.Ssid);
        Assert.Equal(cfg.Passphrase, hs.Pass);
        Assert.Equal(1, h.Hotspot.StartCalls);

        await phone.SendAsync(new PathMessage(offer.TransferId, PathResult.HotspotOk));
        var (channel, client) = await T.ConnectDataAsync(hs.Port, offer.TransferId, h.PhoneKey, h.PcPublicKey);
        using (client)
        using (channel)
        {
            var opts = new ReceiverOptions(h.PcId, offer.TransferId, new OfferInfo(1, data.Length));
            Assert.Equal(TransferOutcome.Completed, (await new FileReceiver(channel, T.Store(h.Dir.Path, "phone"), opts).RunAsync().WaitAsync(T.Wait)).Outcome);
        }
        Assert.Equal(TransferPhase.Completed, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
        Assert.Equal(1, h.Hotspot.Started);
        Assert.Equal(1, h.Hotspot.Finished);   // boşta zamanlayıcısı için bildirildi
    }

    [Fact]
    public async Task WifiNone_UserHotspotBusy_FailsWithClearMessage_AndCancelsPeer()
    {
        await using var h = new Harness();
        h.Hotspot.Result = false;   // kullanıcının etkin noktası açık
        using var phone = await h.ConnectPhoneAsync(WifiState.None);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(100))]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));

        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, final.Phase);
        Assert.Contains("etkin nokta", final.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zaten", final.Error);
        Assert.IsType<CancelSignalMessage>(await phone.ReceiveAsync());
        Assert.Equal(1, h.Hotspot.Finished);
    }

    [Fact]
    public async Task WifiNone_HotspotStartFails_ShowsHotspotError()
    {
        await using var h = new Harness();
        h.Hotspot.Throw = new HotspotException("Bu bilgisayarda etkin nokta desteklenmiyor.");
        using var phone = await h.ConnectPhoneAsync(WifiState.None);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(100))]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, final.Phase);
        Assert.Contains("desteklenmiyor", final.Error);
    }

    [Fact]
    public async Task TcpFail_NoRelayYet_ReportsNoPath()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(100))]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new PathMessage(offer.TransferId, PathResult.TcpFail));

        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, final.Phase);
        Assert.Contains("Yol yok", final.Error);
        Assert.Equal(0, h.Hotspot.StartCalls);   // Wi-Fi'daki iPhone için etkin nokta denenmez
        Assert.Equal(0, h.Pending.Count);        // kayıt temizlendi
    }

    [Fact]
    public async Task NoDataConnection_TimesOut()
    {
        await using var h = new Harness(timeouts: new TransferTimeouts
        {
            Accept = TimeSpan.FromSeconds(5), Connect = TimeSpan.FromMilliseconds(300), Poll = TimeSpan.FromMilliseconds(20),
        });
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(100))]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, final.Phase);
        Assert.Contains("Yol yok", final.Error);
    }

    [Fact]
    public async Task UserCancel_SendsCancelSignal_AndEndsCanceled()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(100))]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        Assert.IsType<AddrMessage>(await phone.ReceiveAsync());

        h.Coord.Cancel(snap.Id);
        Assert.IsType<CancelSignalMessage>(await phone.ReceiveAsync());
        Assert.Equal(TransferPhase.Canceled, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
    }

    [Fact]
    public async Task RemoteCancel_Signal_EndsCanceled()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(100))]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new CancelSignalMessage(offer.TransferId));
        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Canceled, final.Phase);
    }

    [Fact]
    public async Task ConcurrentTransfers_BothComplete()
    {
        await using var h = new Harness(autoAccept: true);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var a = T.Random(150_000, 21);
        var b = T.Random(250_000, 22);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "out.bin", a)]);           // PC → iPhone
        var inId = TransferId.Create();
        await phone.SendAsync(new OfferMessage(inId, 1, b.Length, ["in.bin"]));      // iPhone → PC

        var seenOffer = default(OfferMessage);
        var seenAddr = new Dictionary<TransferId, AddrMessage>();
        var accepted = false;
        while (seenOffer is null || seenAddr.Count < 2 || !accepted)
        {
            switch (await phone.ReceiveAsync())
            {
                case OfferMessage o: seenOffer = o; await phone.SendAsync(new AcceptMessage(o.TransferId)); break;
                case AcceptMessage: accepted = true; break;
                case AddrMessage ad: seenAddr[ad.TransferId] = ad; break;
            }
        }

        async Task RunOutAsync()
        {
            var (ch, cl) = await T.ConnectDataAsync(seenAddr[snap.Id].Candidates[0].Port, snap.Id, h.PhoneKey, h.PcPublicKey);
            using (cl) using (ch)
            {
                var opts = new ReceiverOptions(h.PcId, snap.Id, new OfferInfo(1, a.Length));
                Assert.Equal(TransferOutcome.Completed, (await new FileReceiver(ch, T.Store(h.Dir.Path, "ph"), opts).RunAsync()).Outcome);
            }
        }
        async Task RunInAsync()
        {
            var (ch, cl) = await T.ConnectDataAsync(seenAddr[inId].Candidates[0].Port, inId, h.PhoneKey, h.PcPublicKey);
            using (cl) using (ch)
                Assert.Equal(TransferOutcome.Completed, (await new FileSender(ch, [new MemoryFileSource("in.bin", b)]).RunAsync()).Outcome);
        }
        await Task.WhenAll(RunOutAsync(), RunInAsync()).WaitAsync(T.Wait);

        Assert.Equal(TransferPhase.Completed, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
        Assert.Equal(TransferPhase.Completed, (await h.Coord.WaitAsync(inId).WaitAsync(T.Wait)).Phase);
    }

    [Fact]
    public async Task Send_ToDeviceNotNearby_ThrowsClearError()
    {
        await using var h = new Harness();
        var ex = Assert.Throws<TransferException>(() => h.Coord.Send(h.PhoneId, ["x"]));
        Assert.Contains("yakında değil", ex.Message);
    }

    [Fact]
    public async Task Send_MissingFile_Throws()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        Assert.Throws<TransferException>(() => h.Coord.Send(h.PhoneId, [Path.Combine(h.Dir.Path, "yok.txt")]));
    }

    [Fact]
    public async Task UntrustedPhone_CannotOpenBleSession()
    {
        await using var h = new Harness(trustPhone: false);
        var (phoneLink, pcLink) = MemoryBleLink.CreatePair();
        var run = h.Acceptor.RunLinkAsync(pcLink, CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            SecureChannel.ConnectIkAsync(new BleMessageTransport(phoneLink), Prefix.CreateBle(), h.PhoneKey, h.PcPublicKey).WaitAsync(T.Wait));
        pcLink.Close();
        await run.WaitAsync(T.Wait);
        Assert.False(h.Coord.IsNearby(h.PhoneId));
    }

    [Fact]
    public async Task SessionDrop_FailsNegotiatingTransfers()
    {
        await using var h = new Harness();
        var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "a.txt", T.Random(100))]);
        Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        phone.Dispose();   // BLE koptu
        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, final.Phase);
        Assert.Contains("Bluetooth", final.Error);
        Assert.False(h.Coord.IsNearby(h.PhoneId));
    }
}
