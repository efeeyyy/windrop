using System.Net.WebSockets;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Core.Tests.Support;
using Xunit;

namespace WinDrop.Core.Tests.Session;

/// <summary>WebSocketMessageTransport yerel Kestrel WebSocket sunucusuna karşı (gerçek soket; Kestrel/HttpListener değil mock).</summary>
public class WebSocketMessageTransportTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private const string Room = "0123456789abcdef0123456789abcdef";

    private static async Task<WebSocketMessageTransport> ConnectAsync(FakeRelayServer relay, string role, string room = Room)
    {
        var uri = new Uri($"{relay.Url}/v1/room/{room}?role={role}");
        return new WebSocketMessageTransport(await RelayRoom.ConnectAsync(uri, CancellationToken.None).WaitAsync(Wait));
    }

    [Fact]
    public async Task EachBinaryMessage_IsOneNoiseMessage_BoundariesPreserved_UpTo65535()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var pc = await ConnectAsync(relay, "pc");
        using var phone = await ConnectAsync(relay, "phone");
        Assert.Equal(TransportKind.Relay, pc.Kind);
        Assert.Equal(65535, pc.MaxMessageSize);

        var prefix = Prefix.Create(TransportKind.Relay, TransferId.Create().Span).Bytes;
        await phone.SendRawAsync(prefix, CancellationToken.None);
        Assert.Equal(prefix, await pc.ReceiveRawAsync(20, CancellationToken.None).AsTask().WaitAsync(Wait));

        byte[][] sent = [[1], new byte[100], Random(1500, 1), Random(65535, 2), [9, 9]];
        foreach (var m in sent) await phone.SendMessageAsync(m, CancellationToken.None);
        foreach (var m in sent)
            Assert.Equal(m, await pc.ReceiveMessageAsync(false, CancellationToken.None).AsTask().WaitAsync(Wait));

        await pc.SendMessageAsync(sent[3], CancellationToken.None);   // diğer yön
        Assert.Equal(sent[3], await phone.ReceiveMessageAsync(false, CancellationToken.None).AsTask().WaitAsync(Wait));
    }

    [Fact]
    public async Task Send_RejectsEmptyAndOversized()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var pc = await ConnectAsync(relay, "pc");
        await Assert.ThrowsAsync<ProtocolException>(async () => await pc.SendMessageAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ProtocolException>(async () => await pc.SendMessageAsync(new byte[65536], CancellationToken.None));
    }

    [Fact]
    public async Task ReceiveRaw_WrongLength_IsProtocolError()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var pc = await ConnectAsync(relay, "pc");
        using var phone = await ConnectAsync(relay, "phone");
        await phone.SendRawAsync(new byte[19], CancellationToken.None);
        await Assert.ThrowsAsync<ProtocolException>(async () => await pc.ReceiveRawAsync(20, CancellationToken.None).AsTask().WaitAsync(Wait));
    }

    [Fact]
    public async Task VersionMismatchReply_IsRecognizedInHandshakePhaseOnly()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var pc = await ConnectAsync(relay, "pc");
        using var phone = await ConnectAsync(relay, "phone");
        await pc.SendRawAsync(Prefix.EncodeVersionMismatch(2), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<VersionMismatchException>(async () =>
            await phone.ReceiveMessageAsync(true, CancellationToken.None).AsTask().WaitAsync(Wait));
        Assert.Equal(2, ex.PeerVersion);
    }

    [Fact]
    public async Task PeerClosing_IsAnIoError_NotACleanEof()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var pc = await ConnectAsync(relay, "pc");
        using var phone = await ConnectAsync(relay, "phone");
        await phone.CloseAsync();
        await Assert.ThrowsAsync<IOException>(async () => await pc.ReceiveMessageAsync(false, CancellationToken.None).AsTask().WaitAsync(Wait));
    }

    [Fact]
    public async Task AbruptDrop_IsAnIoError()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var pc = await ConnectAsync(relay, "pc");
        using var phone = await ConnectAsync(relay, "phone");
        await phone.SendMessageAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        await pc.ReceiveMessageAsync(false, CancellationToken.None).AsTask().WaitAsync(Wait);
        relay.DropAll();
        await Assert.ThrowsAsync<IOException>(async () => await pc.ReceiveMessageAsync(false, CancellationToken.None).AsTask().WaitAsync(Wait));
    }

    [Fact]
    public async Task TextMessage_IsAProtocolError()
    {
        await using var server = await LocalWsServer.StartAsync(async ctx =>
        {
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await socket.SendAsync("merhaba"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
            await Task.Delay(500);
        });
        using var client = new WebSocketMessageTransport(await RelayRoom.ConnectAsync(new Uri(server.Url + "/x"), CancellationToken.None));
        await Assert.ThrowsAsync<ProtocolException>(async () => await client.ReceiveMessageAsync(false, CancellationToken.None).AsTask().WaitAsync(Wait));
    }

    [Fact]
    public async Task OversizedServerMessage_IsAProtocolError()
    {
        await using var server = await LocalWsServer.StartAsync(async ctx =>
        {
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await socket.SendAsync(new byte[65536], WebSocketMessageType.Binary, true, CancellationToken.None);
            await Task.Delay(500);
        });
        using var client = new WebSocketMessageTransport(await RelayRoom.ConnectAsync(new Uri(server.Url + "/x"), CancellationToken.None));
        await Assert.ThrowsAsync<ProtocolException>(async () => await client.ReceiveMessageAsync(false, CancellationToken.None).AsTask().WaitAsync(Wait));
    }

    [Fact]
    public async Task Noise_Ik_HandshakeAndFrames_WorkOverTheRelay()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        using var pcKey = NoiseKeyPair.Generate();
        using var phoneKey = NoiseKeyPair.Generate();
        var id = TransferId.Create();
        using var pcT = await ConnectAsync(relay, "pc");
        using var phoneT = await ConnectAsync(relay, "phone");

        var pcTask = SecureChannel.AcceptAutoAsync(pcT, pcKey, k => k.Span.SequenceEqual(phoneKey.PublicKey), null);
        using var phone = await SecureChannel.ConnectIkAsync(phoneT, Prefix.Create(TransportKind.Relay, id.Span), phoneKey, pcKey.PublicKey)
            .WaitAsync(Wait);
        using var pc = await pcTask.WaitAsync(Wait);
        await pc.EnsureEstablishedAsync().AsTask().WaitAsync(Wait);

        Assert.Equal(TransportKind.Relay, pc.Transport);
        Assert.Equal(HandshakeKind.IK, pc.Kind);
        Assert.Equal(id.ToArray(), pc.Prefix.TransferId);
        Assert.Equal(phone.HandshakeHash, pc.HandshakeHash);

        var data = Random(pc.MaxFrameBodySize, 3);
        await phone.SendFrameAsync(FrameCodec.EncodeData(data));
        var frame = await pc.ReceiveFrameAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(FrameType.Data, frame!.Value.Type);
        Assert.Equal(data, frame.Value.Body.ToArray());
    }

    [Fact]
    public async Task Connect_MapsHttpStatus_ToRelayConnectException()
    {
        await using var relay = await FakeRelayServer.StartAsync();
        relay.RejectStatus = 429;
        var ex = await Assert.ThrowsAsync<RelayConnectException>(async () =>
            await RelayRoom.ConnectAsync(new Uri($"{relay.Url}/v1/room/{Room}?role=pc"), CancellationToken.None).WaitAsync(Wait));
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, ex.Status);

        relay.RejectStatus = null;
        using var first = await ConnectAsync(relay, "pc");
        var dup = await Assert.ThrowsAsync<RelayConnectException>(async () => await ConnectAsync(relay, "pc"));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, dup.Status);
    }

    private static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}
