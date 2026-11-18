using System.Diagnostics;
using WinDrop.Core.Protocol;
using WinDrop.Core.Transfer;
using Xunit;

namespace WinDrop.Platform.Tests;

/// <summary>PROTOCOL §7.3: küçük dosyalar BLE oturumundan (TCP/etkin nokta mümkün değilken), btAudio ve boyut kapılarıyla.</summary>
public class BleSmallFileTests
{
    private static string WriteFile(Harness h, string name, byte[] data)
    {
        var dir = Path.Combine(h.Dir.Path, "src");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, data);
        return p;
    }

    /// <summary>PC gönderir; iPhone kabul eder, addr alır ve TCP başarısız bildirir.</summary>
    private static async Task<(TransferSnapshot Snap, OfferMessage Offer)> SendTcpFailAsync(Harness h, Phone phone, byte[] data, string name = "a.bin")
    {
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, name, data)]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new PathMessage(offer.TransferId, PathResult.TcpFail));
        return (snap, offer);
    }

    /// <summary>§7.3: PC önce `bleData` yollar, sonra MANIFEST ile başlar.</summary>
    private static async Task ExpectBleDataAsync(Phone phone, TransferId id)
    {
        var bd = Assert.IsType<BleDataMessage>(await phone.ReceiveAsync());
        Assert.Equal(id, bd.TransferId);
    }

    private static async Task<TransferResult> PhoneReceiveAsync(Harness h, Phone phone, OfferMessage offer)
    {
        await ExpectBleDataAsync(phone, offer.TransferId);
        return await new FileReceiver(phone.Channel, T.Store(h.Dir.Path, "phone"),
            new ReceiverOptions(h.PcId, offer.TransferId, new OfferInfo(offer.Count, offer.Total))).RunAsync().WaitAsync(T.Wait);
    }

    // ---------------------------------------------------------------- uygun: gerçek aktarım

    [Fact]
    public async Task PcSends_TcpFail_SmallFile_GoesOverBle_ShaMatches()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var data = T.Random(50_000, 11);
        var (snap, offer) = await SendTcpFailAsync(h, phone, data);

        var rx = await PhoneReceiveAsync(h, phone, offer);
        Assert.Equal(TransferOutcome.Completed, rx.Outcome);
        Assert.Equal(T.Sha(data), T.Sha(await File.ReadAllBytesAsync(Assert.Single(rx.ReceivedFiles).StoredPath)));

        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Completed, final.Phase);
        Assert.Equal(1.0, final.Progress);
        Assert.Equal(0, h.Hotspot.StartCalls);
        Assert.Equal(0, h.Pending.Count);
    }

    [Fact]
    public async Task PcSends_Exactly64KB_IsAllowed()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var data = T.Random(64 * 1024, 12);
        var (snap, offer) = await SendTcpFailAsync(h, phone, data);
        var rx = await PhoneReceiveAsync(h, phone, offer);
        Assert.Equal(TransferOutcome.Completed, rx.Outcome);
        Assert.Equal(T.Sha(data), T.Sha(await File.ReadAllBytesAsync(Assert.Single(rx.ReceivedFiles).StoredPath)));
        Assert.Equal(TransferPhase.Completed, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
    }

    [Fact]
    public async Task PcSends_NoWifi_UserHotspotBusy_FallsBackToBle_WithoutAddrOrHotspot()
    {
        await using var h = new Harness();
        h.Hotspot.Result = false;   // kullanıcının etkin noktası açık → etkin nokta yolu yok
        using var phone = await h.ConnectPhoneAsync(WifiState.None);
        var data = T.Random(20_000, 13);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "n.bin", data)]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));

        // addr/hotspot gelmez; ilk mesaj doğrudan MANIFEST çerçevesidir
        var rx = await PhoneReceiveAsync(h, phone, offer);
        Assert.Equal(TransferOutcome.Completed, rx.Outcome);
        Assert.Equal(T.Sha(data), T.Sha(await File.ReadAllBytesAsync(Assert.Single(rx.ReceivedFiles).StoredPath)));
        Assert.Equal(TransferPhase.Completed, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
    }

    [Fact]
    public async Task PcReceives_TcpFail_SmallFile_GoesOverBle_ShaMatches()
    {
        await using var h = new Harness(autoAccept: true);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var data = T.Random(40_000, 14);
        var id = TransferId.Create();
        await phone.SendAsync(new OfferMessage(id, 1, data.Length, ["note.bin"]));
        Assert.IsType<AcceptMessage>(await phone.ReceiveAsync());
        Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new PathMessage(id, PathResult.TcpFail));

        await ExpectBleDataAsync(phone, id);
        var sent = await new FileSender(phone.Channel, [new MemoryFileSource("note.bin", data)]).RunAsync().WaitAsync(T.Wait);
        Assert.Equal(TransferOutcome.Completed, sent.Outcome);

        var final = await h.Coord.WaitAsync(id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Completed, final.Phase);
        Assert.Equal(T.Sha(data), T.Sha(await File.ReadAllBytesAsync(final.SavedPath!)));
    }

    [Fact]
    public async Task PcReceives_NoWifiNoHotspot_PcChoosesBle_Completes()
    {
        await using var h = new Harness(autoAccept: true);
        h.Hotspot.Result = false;
        using var phone = await h.ConnectPhoneAsync(WifiState.None);
        var data = T.Random(30_000, 15);
        var id = TransferId.Create();
        await phone.SendAsync(new OfferMessage(id, 1, data.Length, ["n.bin"]));
        Assert.IsType<AcceptMessage>(await phone.ReceiveAsync());
        await ExpectBleDataAsync(phone, id);   // addr/hotspot yok: PC doğrudan bleData seçer

        var sent = await new FileSender(phone.Channel, [new MemoryFileSource("n.bin", data)]).RunAsync().WaitAsync(T.Wait);
        Assert.Equal(TransferOutcome.Completed, sent.Outcome);
        var final = await h.Coord.WaitAsync(id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Completed, final.Phase);
        Assert.Equal(T.Sha(data), T.Sha(await File.ReadAllBytesAsync(final.SavedPath!)));
    }

    // ---------------------------------------------------------------- yol seçimi tablosu: reddedilenler

    [Theory]
    [InlineData("localBt", 1_000, "Bluetooth ses")]
    [InlineData("phoneBt", 1_000, "Bluetooth ses")]
    [InlineData("none", 64 * 1024 + 1, "64 KB")]
    [InlineData("none", 10_000_000, "64 KB")]
    public async Task PcSends_BleRejected_FailsWithNoPath_AndCancelsPeer(string audio, int size, string reasonText)
    {
        await using var h = new Harness();
        if (audio == "localBt") h.BtAudio.Set(true);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected, btAudio: audio == "phoneBt");
        if (audio == "localBt") Assert.True(phone.PcHello!.BtAudio);   // hello gerçek değeri taşır
        var (snap, _) = await SendTcpFailAsync(h, phone, T.Random(size, 16));

        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, final.Phase);
        Assert.Contains("Yol yok", final.Error);
        Assert.Contains(reasonText, final.Error);
        Assert.IsType<CancelSignalMessage>(await phone.ReceiveAsync());   // BLE'de veri çerçevesi yok, doğrudan cancel
    }

    [Fact]
    public async Task PcSends_NoWifi_HotspotUnavailable_LargeFile_StillReportsHotspotError()
    {
        await using var h = new Harness();
        h.Hotspot.Result = false;
        using var phone = await h.ConnectPhoneAsync(WifiState.None);
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "big.bin", T.Random(100_000))]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, final.Phase);
        Assert.Contains("zaten", final.Error);        // özgün etkin nokta nedeni korunur
        Assert.Contains("64 KB", final.Error);        // BLE neden olmadığı eklenir
    }

    [Fact]
    public async Task PcReceives_BleFrameOutsideAnyTransfer_DropsSession()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        Assert.True(h.Coord.IsNearby(h.PhoneId));
        await phone.Channel.SendFrameAsync(FrameCodec.Encode(new CancelBody(null)));
        var deadline = DateTime.UtcNow + T.Wait;
        while (h.Coord.IsNearby(h.PhoneId))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Oturum düşmedi.");
            await Task.Delay(10);
        }
    }

    private sealed class SpyChannel(WinDrop.Core.Session.SecureChannel inner) : WinDrop.Core.Session.IFrameChannel
    {
        public int MaxData;
        public int MaxFrameBodySize => inner.MaxFrameBodySize;
        public ValueTask EnsureEstablishedAsync(CancellationToken ct = default) => inner.EnsureEstablishedAsync(ct);
        public ValueTask SendFrameAsync(ReadOnlyMemory<byte> f, CancellationToken ct = default) => inner.SendFrameAsync(f, ct);

        public async ValueTask<Frame?> ReceiveFrameAsync(CancellationToken ct = default)
        {
            var f = await inner.ReceiveFrameAsync(ct);
            if (f is { Type: FrameType.Data } d) MaxData = Math.Max(MaxData, d.Body.Length);
            return f;
        }
    }

    [Fact]
    public async Task BleData_ChunksAreAtMost2048()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var data = T.Random(20_000, 19);
        var (snap, offer) = await SendTcpFailAsync(h, phone, data);
        await ExpectBleDataAsync(phone, offer.TransferId);
        var spy = new SpyChannel(phone.Channel);
        var rx = await new FileReceiver(spy, T.Store(h.Dir.Path, "phone"),
            new ReceiverOptions(h.PcId, offer.TransferId, new OfferInfo(offer.Count, offer.Total))).RunAsync().WaitAsync(T.Wait);
        Assert.Equal(TransferOutcome.Completed, rx.Outcome);
        Assert.Equal(2048, spy.MaxData);   // §7.3: ≤ 2048 B (20 000 B → tam dolu parçalar)
        Assert.Equal(TransferPhase.Completed, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
    }

    // ---------------------------------------------------------------- durum bildirimi

    [Fact]
    public async Task BtAudioChange_SendsStatusToPhone()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        Assert.False(phone.PcHello!.BtAudio);

        h.BtAudio.Set(true);
        var on = Assert.IsType<StatusMessage>(await phone.ReceiveAsync());
        Assert.True(on.BtAudio);
        h.BtAudio.Set(false);
        var off = Assert.IsType<StatusMessage>(await phone.ReceiveAsync());
        Assert.False(off.BtAudio);
    }

    // ---------------------------------------------------------------- hız sınırı ve ses başlayınca kesme

    [Fact]
    public async Task BleData_IsPaced()
    {
        await using var h = new Harness(blePace: 20_000);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var data = T.Random(30_000, 17);
        var (snap, offer) = await SendTcpFailAsync(h, phone, data);

        var sw = Stopwatch.StartNew();
        var rx = await PhoneReceiveAsync(h, phone, offer);
        sw.Stop();
        Assert.Equal(TransferOutcome.Completed, rx.Outcome);
        // ~30 KB @ 20 KB/s: ilk çerçeve hemen, sonrakiler aralıklı → en az ~1,2 sn
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1.2), $"çok hızlı: {sw.Elapsed}");
        Assert.Equal(TransferPhase.Completed, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
    }

    [Fact]
    public async Task BtAudioStartsDuringBleTransfer_AbortsTransfer()
    {
        await using var h = new Harness(blePace: 10_000);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var data = T.Random(60_000, 18);
        var (snap, offer) = await SendTcpFailAsync(h, phone, data);
        var rx = PhoneReceiveAsync(h, phone, offer);

        var deadline = DateTime.UtcNow + T.Wait;
        while (h.Coord.Transfers.FirstOrDefault(t => t.Id == snap.Id)?.Phase != TransferPhase.Active)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("BLE aktarımı başlamadı.");
            await Task.Delay(10);
        }
        h.BtAudio.Set(true);   // ses başladı

        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, final.Phase);
        Assert.Contains("Bluetooth ses", final.Error);
        try { await rx; } catch (Exception ex) when (ex is not TimeoutException) { /* iPhone tarafı iptal/koptu görür */ }
    }
}
