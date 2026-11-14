using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Core.Transfer;
using Xunit;

namespace WinDrop.Platform.Tests;

/// <summary>docs/review/windows-core.md düzeltmeleri ve DECISIONS.md (C# tarafı) için hedefli testler.</summary>
public class CoreFixTests
{
    private static string WriteFile(Harness h, byte[] data)
    {
        var p = Path.Combine(h.Dir.Path, "send-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(p, data);
        return p;
    }

    private static TransferSnapshot Single(Harness h) => h.Coord.Transfers.Single();

    /// <summary>iPhone'un bağlantısı: yalnız aldığı GATT yüklerini kaydeder.</summary>
    private sealed class RecordingLink(IBleLink inner) : IBleLink
    {
        public List<byte[]> Received { get; } = [];
        public int Mtu => inner.Mtu;
        public ValueTask SendAsync(ReadOnlyMemory<byte> gattPayload, CancellationToken cancellationToken) => inner.SendAsync(gattPayload, cancellationToken);

        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var p = await inner.ReceiveAsync(cancellationToken);
            if (p is not null) lock (Received) Received.Add(p);
            return p;
        }
    }

    // ---------------------------------------------------------------- DECISIONS 2: WDU

    [Fact]
    public async Task Ik_WithUnknownKey_SendsWdu_NotWdr()
    {
        await using var h = new Harness(trustPhone: false);
        var (phoneLink, pcLink) = MemoryBleLink.CreatePair();
        var run = h.Acceptor.RunLinkWithResyncAsync(pcLink, CancellationToken.None);
        var rec = new RecordingLink(phoneLink);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var channel = await SecureChannel.ConnectIkAsync(new BleMessageTransport(rec), Prefix.CreateBle(), h.PhoneKey, h.PcPublicKey).WaitAsync(TimeSpan.FromSeconds(5));
        });
        await Poll.UntilAsync(() => { lock (rec.Received) return rec.Received.Count > 0; }, "WDU gelmedi");
        lock (rec.Received)
        {
            Assert.Equal(BleResync.UnknownMarker, rec.Received[0]);
            Assert.DoesNotContain(rec.Received, x => x.SequenceEqual(BleResync.Marker));
        }
        phoneLink.Close();
        await run.WaitAsync(T.Wait);
    }

    // ---------------------------------------------------------------- bulgu 1 / DECISIONS 6: boşta kalma

    [Fact]
    public async Task ActiveDataSession_WithoutProgress_FailsWithIdleTimeout()
    {
        var timeouts = new TransferTimeouts
        {
            Accept = TimeSpan.FromSeconds(10), Approval = TimeSpan.FromSeconds(10), Connect = TimeSpan.FromSeconds(5),
            HotspotConnect = TimeSpan.FromSeconds(5), Reconnect = TimeSpan.FromSeconds(5), DataIdle = TimeSpan.FromSeconds(1),
            Poll = TimeSpan.FromMilliseconds(20),
        };
        await using var h = new Harness(timeouts: timeouts);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var id = TransferId.Create();
        await phone.SendAsync(new OfferMessage(id, 1, 10, ["x"]));
        Assert.IsType<AcceptMessage>(await phone.ReceiveAsync());
        var addr = Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        var (channel, client) = await T.ConnectDataAsync(addr.Candidates[0].Port, id, h.PhoneKey, h.PcPublicKey);
        using (client) using (channel)
        {
            var snap = await h.Coord.WaitAsync(id).WaitAsync(T.Wait);   // iPhone hiçbir şey göndermiyor (yarı açık bağlantı)
            Assert.Equal(TransferPhase.Failed, snap.Phase);
            Assert.Equal(UserErrorCode.TransferCutWith, snap.ErrorInfo!.Parts[0].Code);
        }
    }

    // ---------------------------------------------------------------- bulgu 5: beklenmeyen istisna

    private sealed class ThrowingCommitHook : IFileCommitHook
    {
        public ValueTask OnBeforeCommitAsync(string partPath, CancellationToken cancellationToken) =>
            throw new UnauthorizedAccessException("Denetimli Klasör Erişimi");

        public ValueTask OnFileCommittedAsync(string finalPath, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CommitUnauthorizedAccess_FailsTransfer_InsteadOfHangingActive()
    {
        await using var h = new Harness(commitHook: new ThrowingCommitHook());
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var data = T.Random(3000, 9);
        var id = TransferId.Create();
        await phone.SendAsync(new OfferMessage(id, 1, data.Length, ["x"]));
        Assert.IsType<AcceptMessage>(await phone.ReceiveAsync());
        var addr = Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        var (channel, client) = await T.ConnectDataAsync(addr.Candidates[0].Port, id, h.PhoneKey, h.PcPublicKey);
        using (client) using (channel)
        {
            try { await new FileSender(channel, [new MemoryFileSource("a.bin", data)]).RunAsync().WaitAsync(T.Wait); }
            catch (Exception) { /* PC bağlantıyı kapatır */ }
            var snap = await h.Coord.WaitAsync(id).WaitAsync(T.Wait);
            Assert.Equal(TransferPhase.Failed, snap.Phase);
        }
        await Poll.UntilAsync(() => h.Pending.Count == 0, "veri kaydı kapanmadı");
    }

    // ---------------------------------------------------------------- bulgu 3 / DECISIONS 4: yol başına başarısızlık

    [Fact]
    public async Task DualPath_TcpFail_KeepsWaitingForHotspot_ThenCompletes()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.None);
        var data = T.Random(5000, 3);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, data)]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        var hs = Assert.IsType<HotspotMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new StatusMessage(WifiState.Connected, false));   // Wi-Fi'a bağlandı → ek addr yolu
        Assert.IsType<AddrMessage>(await phone.ReceiveAsync());

        await phone.SendAsync(new PathMessage(offer.TransferId, PathResult.TcpFail));
        await Task.Delay(500);
        Assert.False(Single(h).IsTerminal);   // etkin nokta yolu hâlâ açık: PC beklemeye devam eder

        var (channel, client) = await T.ConnectDataAsync(hs.Port, offer.TransferId, h.PhoneKey, h.PcPublicKey);
        using (client) using (channel)
        {
            var res = await new FileReceiver(channel, T.Store(h.Dir.Path, "phone"),
                new ReceiverOptions(h.PcId, offer.TransferId, new OfferInfo(1, data.Length))).RunAsync().WaitAsync(T.Wait);
            Assert.Equal(TransferOutcome.Completed, res.Outcome);
        }
        Assert.Equal(TransferPhase.Completed, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
    }

    [Fact]
    public async Task DualPath_BothFail_FallsBack()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.None);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, T.Random(100_000, 4))]);   // BLE sınırını (64 KB) aşar → yol yok
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        Assert.IsType<HotspotMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new StatusMessage(WifiState.Connected, false));
        Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new PathMessage(offer.TransferId, PathResult.TcpFail));
        await phone.SendAsync(new PathMessage(offer.TransferId, PathResult.HotspotFail));
        var done = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, done.Phase);
    }

    // ---------------------------------------------------------------- bulgu 4 / DECISIONS 5, 8: geç kalan etkin nokta

    [Fact]
    public async Task CanceledWhileHotspotStarting_DoesNotSendHotspot_AndRegistrationClosed()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new Harness(trustPhone: false);
        h.Hotspot.StartGate = gate.Task;
        using var phone = await h.ConnectPhoneAsync(WifiState.None, xx: true);
        h.Coord.OfferReceived += o => h.Coord.RespondToOffer(o.Id, true);
        var before = h.HotspotCfg.GetOrCreate();
        var id = TransferId.Create();
        await phone.SendAsync(new OfferMessage(id, 1, 10, ["x"]));
        Assert.IsType<AcceptMessage>(await phone.ReceiveAsync());
        await Poll.UntilAsync(() => h.Hotspot.StartCalls == 1, "etkin nokta başlatılmadı");

        await phone.SendAsync(new CancelSignalMessage(id));   // etkin nokta açılırken karşı taraf vazgeçti
        Assert.Equal(TransferPhase.Canceled, (await h.Coord.WaitAsync(id).WaitAsync(T.Wait)).Phase);
        gate.SetResult();   // StartAsync şimdi döner

        using var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await phone.Channel.ReceiveSignalAsync(shortWait.Token));   // hotspot gitmedi
        Assert.Equal(0, h.Pending.Count);
        await Poll.UntilAsync(() => h.Hotspot.Started == h.Hotspot.Finished, "TransferStarted/Finished dengesiz");
        Assert.Equal(before.Passphrase, h.HotspotCfg.GetOrCreate().Passphrase);   // parola hiç gönderilmedi → yenilemeye gerek yok
    }

    // ---------------------------------------------------------------- bulgu 10: eşleştirme kaldırma

    [Fact]
    public async Task DropPeer_FailsActiveTransfer()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var data = T.Random(2000, 6);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, data)]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        var addr = Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        var (channel, client) = await T.ConnectDataAsync(addr.Candidates[0].Port, offer.TransferId, h.PhoneKey, h.PcPublicKey);
        using (client) using (channel)
        {
            await Poll.UntilAsync(() => Single(h).Phase == TransferPhase.Active, "aktarım Active olmadı");
            h.Coord.DropPeer(h.PhoneId);
            var done = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
            Assert.Equal(TransferPhase.Failed, done.Phase);
            Assert.Equal(UserErrorCode.PairingRemoved, done.ErrorInfo!.Parts[0].Code);
        }
    }

    // ---------------------------------------------------------------- DECISIONS 3: süreler

    [Fact]
    public void DefaultTimeouts_FollowDecisions()
    {
        var t = new TransferTimeouts();
        Assert.Equal(TimeSpan.FromSeconds(60), t.Approval);
        Assert.Equal(TimeSpan.FromSeconds(80), t.Accept);
        Assert.Equal(TimeSpan.FromSeconds(120), t.HotspotConnect);
        Assert.Equal(TimeSpan.FromSeconds(30), t.DataIdle);
        Assert.Equal(TimeSpan.FromSeconds(30), t.BleIdle);
    }

    // ---------------------------------------------------------------- DECISIONS 1: bilinmeyen t oturumu düşürmez

    [Fact]
    public async Task UnknownSignalType_AndBrokenJson_AreDropped_SessionSurvives()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        await phone.Channel.SendPlaintextAsync("{\"t\":\"future-thing\",\"x\":1}"u8.ToArray(), CancellationToken.None).AsTask().WaitAsync(T.Wait);
        await phone.Channel.SendPlaintextAsync("{\"t\":\"offer\"}"u8.ToArray(), CancellationToken.None).AsTask().WaitAsync(T.Wait);   // zorunlu alanlar eksik
        var id = TransferId.Create();
        await phone.SendAsync(new OfferMessage(id, 1, 10, ["x"]));
        Assert.IsType<AcceptMessage>(await phone.ReceiveAsync());   // oturum hâlâ canlı
        Assert.True(h.Coord.IsNearby(h.PhoneId));
    }
}
