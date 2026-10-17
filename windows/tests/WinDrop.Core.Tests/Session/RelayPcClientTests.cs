using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Core.Tests.Support;
using Xunit;

namespace WinDrop.Core.Tests.Session;

/// <summary>RelayPcClient (PC = yanıtlayan) denetimleri: eş anahtarı, transferId, desen, bekleme zaman aşımı.</summary>
public class RelayPcClientTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private sealed class Rig : IDisposable
    {
        public NoiseKeyPair PcKey { get; } = NoiseKeyPair.Generate();
        public NoiseKeyPair PhoneKey { get; } = NoiseKeyPair.Generate();
        public TransferId Id { get; } = TransferId.Create();
        public byte[] Token { get; } = RelayRoom.NewToken();
        public Uri PcUri(FakeRelayServer relay) => RelayRoom.BuildRoomUri(relay.Url, RelayRoom.ComputeRoomId(Token), "pc")!;
        public Uri PhoneUri(FakeRelayServer relay) => RelayRoom.BuildRoomUri(relay.Url, RelayRoom.ComputeRoomId(Token), "phone")!;

        public void Dispose()
        {
            PcKey.Dispose();
            PhoneKey.Dispose();
        }
    }

    private static async Task<SecureChannel> PhoneConnectAsync(Rig rig, FakeRelayServer relay, TransferId id, NoiseKeyPair key, bool xx = false)
    {
        var transport = new WebSocketMessageTransport(await RelayRoom.ConnectAsync(rig.PhoneUri(relay), CancellationToken.None));
        var prefix = Prefix.Create(TransportKind.Relay, id.Span);
        return await (xx
            ? SecureChannel.ConnectXxAsync(transport, prefix, key)
            : SecureChannel.ConnectIkAsync(transport, prefix, key, rig.PcKey.PublicKey)).WaitAsync(Wait);
    }

    [Fact]
    public async Task Accepts_Ik_AndCallsOnConnected_BeforePhoneArrives()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var rig = new Rig();
        var connected = new TaskCompletionSource();
        var pcTask = RelayPcClient.AcceptAsync(rig.PcUri(relay), rig.Id, rig.PcKey, rig.PhoneKey.PublicKey, HandshakeKind.IK,
            _ => { connected.SetResult(); return Task.CompletedTask; }, TimeSpan.FromSeconds(10), CancellationToken.None);
        await connected.Task.WaitAsync(Wait);   // PC odada, iPhone henüz yok
        Assert.False(pcTask.IsCompleted);

        using var phone = await PhoneConnectAsync(rig, relay, rig.Id, rig.PhoneKey);
        using var session = await pcTask.WaitAsync(Wait);
        Assert.Equal(HandshakeKind.IK, session.Channel.Kind);
        Assert.True(session.Channel.IsEstablished);
        Assert.Equal(TransportKind.Relay, session.Channel.Transport);
    }

    [Fact]
    public async Task Accepts_Xx_WhenBleSessionWasXx()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var rig = new Rig();
        var pcTask = RelayPcClient.AcceptAsync(rig.PcUri(relay), rig.Id, rig.PcKey, rig.PhoneKey.PublicKey, HandshakeKind.XX,
            null, TimeSpan.FromSeconds(10), CancellationToken.None);
        using var phone = await PhoneConnectAsync(rig, relay, rig.Id, rig.PhoneKey, xx: true);
        using var session = await pcTask.WaitAsync(Wait);
        Assert.Equal(HandshakeKind.XX, session.Channel.Kind);
    }

    [Fact]
    public async Task PatternMismatch_IsRejected()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var rig = new Rig();
        var pcTask = RelayPcClient.AcceptAsync(rig.PcUri(relay), rig.Id, rig.PcKey, rig.PhoneKey.PublicKey, HandshakeKind.IK,
            null, TimeSpan.FromSeconds(10), CancellationToken.None);
        _ = PhoneConnectAsync(rig, relay, rig.Id, rig.PhoneKey, xx: true).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        await Assert.ThrowsAsync<ProtocolException>(async () => await pcTask.WaitAsync(Wait));
    }

    [Fact]
    public async Task WrongTransferIdInPrefix_IsRejected()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var rig = new Rig();
        var pcTask = RelayPcClient.AcceptAsync(rig.PcUri(relay), rig.Id, rig.PcKey, rig.PhoneKey.PublicKey, null,
            null, TimeSpan.FromSeconds(10), CancellationToken.None);
        _ = PhoneConnectAsync(rig, relay, TransferId.Create(), rig.PhoneKey).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        await Assert.ThrowsAsync<ProtocolException>(async () => await pcTask.WaitAsync(Wait));
    }

    [Fact]
    public async Task ForeignKey_IsRejected_BeforeReply()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var rig = new Rig();
        using var stranger = NoiseKeyPair.Generate();
        var pcTask = RelayPcClient.AcceptAsync(rig.PcUri(relay), rig.Id, rig.PcKey, rig.PhoneKey.PublicKey, null,
            null, TimeSpan.FromSeconds(10), CancellationToken.None);
        _ = PhoneConnectAsync(rig, relay, rig.Id, stranger).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        await Assert.ThrowsAsync<PeerNotTrustedException>(async () => await pcTask.WaitAsync(Wait));
    }

    [Fact]
    public async Task PhoneNeverArrives_TimesOut()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var rig = new Rig();
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await RelayPcClient.AcceptAsync(rig.PcUri(relay), rig.Id, rig.PcKey, rig.PhoneKey.PublicKey, null,
                null, TimeSpan.FromMilliseconds(300), CancellationToken.None).WaitAsync(Wait));
    }

    [Fact]
    public async Task CallerCancellation_IsOperationCanceled_NotTimeout()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var rig = new Rig();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await RelayPcClient.AcceptAsync(rig.PcUri(relay), rig.Id, rig.PcKey, rig.PhoneKey.PublicKey, null,
                null, TimeSpan.FromSeconds(30), cts.Token).WaitAsync(Wait));
    }
}
