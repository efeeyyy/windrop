using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Core.Tests.Support;
using WinDrop.Core.Transfer;
using Xunit;

namespace WinDrop.Platform.Tests;

/// <summary>PROTOCOL §7.4: doğrudan yol başarısız + BLE uygun değil + RelayUrl dolu → kullanıcı onayı → aktarma sunucusu (sahte relay).</summary>
public class RelayPathTests
{
    private static string WriteFile(Harness h, string name, byte[] data)
    {
        var dir = Path.Combine(h.Dir.Path, "src");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, data);
        return p;
    }

    /// <summary>Onay isteklerini toplar ve (isteniyorsa) hemen yanıtlar.</summary>
    private sealed class Approvals
    {
        public List<RelayApprovalRequest> Requests { get; } = [];
        public bool? Answer { get; set; }

        public Approvals(Harness h)
        {
            h.Coord.RelayApprovalRequested += r =>
            {
                lock (Requests) Requests.Add(r);
                if (Answer is { } a) h.Coord.RespondToRelay(r.Id, a);
            };
        }

        public int Count
        {
            get { lock (Requests) return Requests.Count; }
        }
    }

    /// <summary>iPhone: <c>relay</c> iletisindeki token'dan odaya girer ve IK başlatır (ön ek taşıma=3).</summary>
    private static async Task<(SecureChannel Channel, WebSocketMessageTransport Transport)> PhoneJoinAsync(
        Harness h, FakeRelayServer relay, RelayMessage msg)
    {
        Assert.True(RelayRoom.TryDecodeToken(msg.Token, out var token));
        var uri = RelayRoom.BuildRoomUri(relay.Url, RelayRoom.ComputeRoomId(token), "phone")!;
        var transport = new WebSocketMessageTransport(await RelayRoom.ConnectAsync(uri, CancellationToken.None).WaitAsync(T.Wait));
        var channel = await SecureChannel.ConnectIkAsync(transport, Prefix.Create(TransportKind.Relay, msg.TransferId.Span),
            h.PhoneKey, h.PcPublicKey).WaitAsync(T.Wait);
        return (channel, transport);
    }

    /// <summary>PC gönderir; iPhone kabul eder, addr alır ve TCP başarısız bildirir. Dönüş: teklif.</summary>
    private static async Task<(TransferSnapshot Snap, OfferMessage Offer)> SendTcpFailAsync(Harness h, Phone phone, byte[] data)
    {
        var snap = h.Coord.Send(h.PhoneId, [WriteFile(h, "big.bin", data)]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new PathMessage(offer.TransferId, PathResult.TcpFail));
        return (snap, offer);
    }

    private static async Task<string> PhoneReceiveAndVerifyAsync(Harness h, SecureChannel channel, OfferMessage offer, byte[] data)
    {
        var rx = await new FileReceiver(channel, T.Store(h.Dir.Path, "phone"),
            new ReceiverOptions(h.PcId, offer.TransferId, new OfferInfo(offer.Count, offer.Total))).RunAsync().WaitAsync(T.Wait);
        Assert.Equal(TransferOutcome.Completed, rx.Outcome);
        var path = Assert.Single(rx.ReceivedFiles).StoredPath;
        Assert.Equal(T.Sha(data), T.Sha(await File.ReadAllBytesAsync(path)));
        return path;
    }

    // ---------------------------------------------------------------- kapalı / geçersiz / ret

    [Fact]
    public async Task RelayUrlEmpty_NoPath_FailsWithYolYok_AndNeverAsks()
    {
        await using var h = new Harness();
        var approvals = new Approvals(h) { Answer = true };
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var (snap, _) = await SendTcpFailAsync(h, phone, T.Random(200_000, 41));

        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, final.Phase);
        Assert.Contains("Yol yok", final.Error);
        Assert.Equal(0, approvals.Count);
    }

    [Fact]
    public async Task RelayUrlInvalid_FailsWithoutAsking()
    {
        await using var h = new Harness();
        h.Settings.RelayUrl = "ftp://relay.example.com";
        var approvals = new Approvals(h) { Answer = true };
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var (snap, _) = await SendTcpFailAsync(h, phone, T.Random(200_000, 42));

        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, final.Phase);
        Assert.Contains("adresi geçersiz", final.Error);
        Assert.Equal(0, approvals.Count);
    }

    [Fact]
    public async Task UserDeclines_TransferCanceled_NeverConnectsToRelay_PeerToldToCancel()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        await using var h = new Harness();
        h.Settings.RelayUrl = relay.Url;
        var approvals = new Approvals(h) { Answer = false };
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var data = T.Random(200_000, 43);
        var (snap, offer) = await SendTcpFailAsync(h, phone, data);

        var cancel = Assert.IsType<CancelSignalMessage>(await phone.ReceiveAsync());   // `relay` hiç gönderilmedi
        Assert.Equal(offer.TransferId, cancel.TransferId);
        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Canceled, final.Phase);
        Assert.Contains("onaylanmadı", final.Error);

        var req = Assert.Single(approvals.Requests);
        Assert.Equal(snap.Id, req.Id);
        Assert.Equal(data.Length, req.TotalBytes);
        Assert.Equal("Test iPhone", req.PeerName);
        Assert.Empty(relay.RoomIds);
        Assert.Equal(0, relay.RejectedCount);
    }

    [Fact]
    public async Task SmallFile_PrefersBle_OverRelay()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        await using var h = new Harness();
        h.Settings.RelayUrl = relay.Url;
        var approvals = new Approvals(h) { Answer = true };
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var (snap, offer) = await SendTcpFailAsync(h, phone, T.Random(20_000, 44));

        var bd = Assert.IsType<BleDataMessage>(await phone.ReceiveAsync());
        Assert.Equal(offer.TransferId, bd.TransferId);
        h.Coord.Cancel(snap.Id);
        await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(0, approvals.Count);
        Assert.Empty(relay.RoomIds);
    }

    // ---------------------------------------------------------------- onay → gerçek aktarım

    [Fact]
    public async Task PcSends_Approved_GoesThroughRelay_ShaMatches_RoomIdFromToken()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        await using var h = new Harness();
        h.Settings.RelayUrl = relay.Url;
        var approvals = new Approvals(h) { Answer = true };
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var data = T.Random(300_000, 45);
        var (snap, offer) = await SendTcpFailAsync(h, phone, data);

        var msg = Assert.IsType<RelayMessage>(await phone.ReceiveAsync());
        Assert.Equal(offer.TransferId, msg.TransferId);
        Assert.True(RelayRoom.TryDecodeToken(msg.Token, out var token));
        Assert.Equal(32, token.Length);

        var (channel, transport) = await PhoneJoinAsync(h, relay, msg);
        using (transport)
        using (channel)
        {
            await phone.SendAsync(new PathMessage(offer.TransferId, PathResult.RelayOk));
            await PhoneReceiveAndVerifyAsync(h, channel, offer, data);
        }

        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Completed, final.Phase);
        Assert.Equal(1.0, final.Progress);
        Assert.Equal(RelayRoom.ComputeRoomId(token), Assert.Single(relay.RoomIds));
        Assert.True(relay.ForwardedBytes > data.Length);   // şifreli baytlar sunucudan geçti
        Assert.Equal(1, approvals.Count);
        Assert.Equal(0, h.Pending.Count);
    }

    [Fact]
    public async Task PcReceives_Approved_GoesThroughRelay()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        await using var h = new Harness(autoAccept: true);
        h.Settings.RelayUrl = relay.Url;
        _ = new Approvals(h) { Answer = true };
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);

        var data = T.Random(250_000, 46);
        var id = TransferId.Create();
        await phone.SendAsync(new OfferMessage(id, 1, data.Length, ["note.bin"]));
        Assert.IsType<AcceptMessage>(await phone.ReceiveAsync());
        Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new PathMessage(id, PathResult.TcpFail));

        var msg = Assert.IsType<RelayMessage>(await phone.ReceiveAsync());
        Assert.Equal(id, msg.TransferId);
        var (channel, transport) = await PhoneJoinAsync(h, relay, msg);
        using (transport)
        using (channel)
        {
            var sent = await new FileSender(channel, [new MemoryFileSource("note.bin", data)]).RunAsync().WaitAsync(T.Wait);
            Assert.Equal(TransferOutcome.Completed, sent.Outcome);
        }

        var final = await h.Coord.WaitAsync(id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Completed, final.Phase);
        Assert.Equal(TransferRole.Receiver, final.Role);
        Assert.Equal(T.Sha(data), T.Sha(await File.ReadAllBytesAsync(final.SavedPath!)));
    }

    // ---------------------------------------------------------------- yeniden deneme / relay_fail / hatalar

    [Fact]
    public async Task PhoneDrops_RetriesOnceWithNewToken_AndCompletes()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        await using var h = new Harness();
        h.Settings.RelayUrl = relay.Url;
        _ = new Approvals(h) { Answer = true };
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var data = T.Random(300_000, 47);
        var (snap, offer) = await SendTcpFailAsync(h, phone, data);

        var first = Assert.IsType<RelayMessage>(await phone.ReceiveAsync());
        var (ch1, tr1) = await PhoneJoinAsync(h, relay, first);
        using (tr1)
        using (ch1)
        {
            await Task.Delay(300);
            relay.DropAll();   // oda kapandı / iPhone koptu
        }

        var second = Assert.IsType<RelayMessage>(await phone.ReceiveAsync());
        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(offer.TransferId, second.TransferId);
        var (ch2, tr2) = await PhoneJoinAsync(h, relay, second);
        using (tr2)
        using (ch2)
            await PhoneReceiveAndVerifyAsync(h, ch2, offer, data);

        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Completed, final.Phase);
        Assert.Equal(2, relay.RoomIds.Count);
        Assert.NotEqual(relay.RoomIds[0], relay.RoomIds[1]);
    }

    [Fact]
    public async Task PhoneReportsRelayFail_PcRetriesWithNewToken()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        await using var h = new Harness();
        h.Settings.RelayUrl = relay.Url;
        _ = new Approvals(h) { Answer = true };
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var data = T.Random(220_000, 48);
        var (snap, offer) = await SendTcpFailAsync(h, phone, data);

        var first = Assert.IsType<RelayMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new PathMessage(offer.TransferId, PathResult.RelayFail));   // iPhone odaya bağlanamadı

        var second = Assert.IsType<RelayMessage>(await phone.ReceiveAsync());
        Assert.NotEqual(first.Token, second.Token);
        var (channel, transport) = await PhoneJoinAsync(h, relay, second);
        using (transport)
        using (channel)
            await PhoneReceiveAndVerifyAsync(h, channel, offer, data);

        Assert.Equal(TransferPhase.Completed, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
    }

    [Fact]
    public async Task PhoneNeverJoins_TwoAttempts_ThenYolYok()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        var timeouts = new TransferTimeouts
        {
            Accept = TimeSpan.FromSeconds(10), Approval = TimeSpan.FromSeconds(10), Connect = TimeSpan.FromSeconds(3),
            HotspotConnect = TimeSpan.FromSeconds(3), Reconnect = TimeSpan.FromSeconds(3), Poll = TimeSpan.FromMilliseconds(20),
            RelayPeer = TimeSpan.FromSeconds(1), RelayApproval = TimeSpan.FromSeconds(10),
        };
        await using var h = new Harness(timeouts: timeouts);
        h.Settings.RelayUrl = relay.Url;
        _ = new Approvals(h) { Answer = true };
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var (snap, _) = await SendTcpFailAsync(h, phone, T.Random(200_000, 49));

        var first = Assert.IsType<RelayMessage>(await phone.ReceiveAsync());
        var second = Assert.IsType<RelayMessage>(await phone.ReceiveAsync());
        Assert.NotEqual(first.Token, second.Token);

        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, final.Phase);
        Assert.Contains("Yol yok", final.Error);
        Assert.Equal(2, relay.RoomIds.Count);   // en çok 1 yeniden deneme
    }

    [Fact]
    public async Task ServerRateLimit429_FailsWithoutRetry()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        relay.RejectStatus = 429;
        await using var h = new Harness();
        h.Settings.RelayUrl = relay.Url;
        _ = new Approvals(h) { Answer = true };
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var (snap, _) = await SendTcpFailAsync(h, phone, T.Random(200_000, 50));

        var final = await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait);
        Assert.Equal(TransferPhase.Failed, final.Phase);
        Assert.Contains("çok sık", final.Error);
        Assert.Contains("Yol yok", final.Error);
        Assert.Equal(1, relay.RejectedCount);
    }

    [Fact]
    public async Task WrongPhoneKey_InRoom_IsRejected()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        var timeouts = new TransferTimeouts
        {
            Accept = TimeSpan.FromSeconds(10), Approval = TimeSpan.FromSeconds(10), Connect = TimeSpan.FromSeconds(3),
            HotspotConnect = TimeSpan.FromSeconds(3), Reconnect = TimeSpan.FromSeconds(3), Poll = TimeSpan.FromMilliseconds(20),
            RelayPeer = TimeSpan.FromSeconds(3), RelayApproval = TimeSpan.FromSeconds(10),
        };
        await using var h = new Harness(timeouts: timeouts);
        h.Settings.RelayUrl = relay.Url;
        _ = new Approvals(h) { Answer = true };
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var (snap, offer) = await SendTcpFailAsync(h, phone, T.Random(200_000, 51));

        var msg = Assert.IsType<RelayMessage>(await phone.ReceiveAsync());
        Assert.True(RelayRoom.TryDecodeToken(msg.Token, out var token));
        using var stranger = WinDrop.Core.Noise.NoiseKeyPair.Generate();
        var uri = RelayRoom.BuildRoomUri(relay.Url, RelayRoom.ComputeRoomId(token), "phone")!;
        using var transport = new WebSocketMessageTransport(await RelayRoom.ConnectAsync(uri, CancellationToken.None));
        // Yabancı anahtar: PC msg2 göndermeden reddeder → el sıkışma tamamlanmaz.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await SecureChannel.ConnectIkAsync(transport, Prefix.Create(TransportKind.Relay, offer.TransferId.Span), stranger, h.PcPublicKey)
                .WaitAsync(T.Wait));

        Assert.NotEqual(TransferPhase.Completed, (await h.Coord.WaitAsync(snap.Id).WaitAsync(TimeSpan.FromSeconds(30))).Phase);
    }
}
