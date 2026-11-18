using System.Net;
using System.Net.Sockets;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Core.Transfer;
using Xunit;

namespace WinDrop.Platform.Tests;

public class DataListenerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly NoiseKeyPair _pcKey = NoiseKeyPair.Generate();
    private readonly NoiseKeyPair _phoneKey = NoiseKeyPair.Generate();
    private readonly PendingTransferTable _pending = new();
    private readonly DataListener _listener;

    public DataListenerTests()
    {
        _listener = new DataListener(_pcKey, _pending, 0);
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.DisposeAsync().AsTask().Wait();
        _pcKey.Dispose();
        _phoneKey.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task Listener_BindsEphemeralPort()
    {
        Assert.True(_listener.Port > 0);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task IkSender_ToListenerReceiver_TransfersFile()
    {
        var id = TransferId.Create();
        var data = T.Random(700_000, 7);
        var store = T.Store(_dir.Path, "rx");
        var done = new TaskCompletionSource<TransferResult>();
        using var reg = _pending.Register(id, _phoneKey.PublicKey, async ch =>
        {
            var opts = new ReceiverOptions(DeviceId.HexFromPublicKey(_phoneKey.PublicKey), id, new OfferInfo(1, data.Length));
            done.TrySetResult(await new FileReceiver(ch, store, opts).RunAsync());
        });

        var (channel, client) = await T.ConnectDataAsync(_listener.Port, id, _phoneKey, _pcKey.PublicKey);
        using (client)
        using (channel)
        {
            var sent = await new FileSender(channel, [new MemoryFileSource("a.bin", data)]).RunAsync().WaitAsync(T.Wait);
            Assert.Equal(TransferOutcome.Completed, sent.Outcome);
        }

        var received = await done.Task.WaitAsync(T.Wait);
        Assert.Equal(TransferOutcome.Completed, received.Outcome);
        var stored = Assert.Single(received.ReceivedFiles).StoredPath;
        Assert.Equal(T.Sha(data), T.Sha(await File.ReadAllBytesAsync(stored)));
    }

    [Fact]
    public async Task PcAsSender_IphoneAsReceiver_TransfersFile()
    {
        // Yön veri akışından bağımsız (PROTOCOL §6.1): iPhone bağlanır ama alıcıdır.
        var id = TransferId.Create();
        var data = T.Random(200_000, 9);
        var done = new TaskCompletionSource<TransferResult>();
        using var reg = _pending.Register(id, _phoneKey.PublicKey,
            async ch => done.TrySetResult(await new FileSender(ch, [new MemoryFileSource("b.bin", data)]).RunAsync()));

        var (channel, client) = await T.ConnectDataAsync(_listener.Port, id, _phoneKey, _pcKey.PublicKey);
        using (client)
        using (channel)
        {
            var opts = new ReceiverOptions(DeviceId.HexFromPublicKey(_pcKey.PublicKey), id, new OfferInfo(1, data.Length));
            var rx = await new FileReceiver(channel, T.Store(_dir.Path, "ph"), opts).RunAsync().WaitAsync(T.Wait);
            Assert.Equal(TransferOutcome.Completed, rx.Outcome);
        }
        Assert.Equal(TransferOutcome.Completed, (await done.Task.WaitAsync(T.Wait)).Outcome);
    }

    [Fact]
    public async Task UnknownTransferId_IsClosed_HandlerNeverRuns()
    {
        var called = 0;
        using var reg = _pending.Register(TransferId.Create(), _phoneKey.PublicKey, _ => { Interlocked.Increment(ref called); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<Exception>(() =>
            T.ConnectDataAsync(_listener.Port, TransferId.Create(), _phoneKey, _pcKey.PublicKey));
        Assert.Equal(0, called);
    }

    [Fact]
    public async Task UntrustedKey_ForKnownTransferId_IsClosed()
    {
        var id = TransferId.Create();
        var called = 0;
        using var reg = _pending.Register(id, _phoneKey.PublicKey, _ => { Interlocked.Increment(ref called); return Task.CompletedTask; });
        using var stranger = NoiseKeyPair.Generate();
        await Assert.ThrowsAnyAsync<Exception>(() => T.ConnectDataAsync(_listener.Port, id, stranger, _pcKey.PublicKey));
        Assert.Equal(0, called);
    }

    [Fact]
    public async Task UnregisteredTransfer_IsRejectedAfterDispose()
    {
        var id = TransferId.Create();
        var reg = _pending.Register(id, _phoneKey.PublicKey, _ => Task.CompletedTask);
        reg.Dispose();
        Assert.Equal(0, _pending.Count);
        await Assert.ThrowsAnyAsync<Exception>(() => T.ConnectDataAsync(_listener.Port, id, _phoneKey, _pcKey.PublicKey));
    }

    [Fact]
    public async Task SecondConnection_ForSameTransferId_ClosesFirst()
    {
        var id = TransferId.Create();
        var started = new SemaphoreSlim(0);
        using var reg = _pending.Register(id, _phoneKey.PublicKey, async ch =>
        {
            started.Release();
            try { while (await ch.ReceiveAsync() is not null) { } }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or ProtocolException or NoiseException) { }
        });

        var (ch1, c1) = await T.ConnectDataAsync(_listener.Port, id, _phoneKey, _pcKey.PublicKey);
        using var _1 = c1;
        using var _2 = ch1;
        Assert.True(await started.WaitAsync(T.Wait));

        var (ch2, c2) = await T.ConnectDataAsync(_listener.Port, id, _phoneKey, _pcKey.PublicKey);
        using var _3 = c2;
        using var _4 = ch2;
        Assert.True(await started.WaitAsync(T.Wait));

        // Birinci bağlantı PC tarafından kapatıldı: okuma EOF/hata verir.
        var read1 = Task.Run(async () =>
        {
            try { return await ch1.ReceiveAsync(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or ProtocolException or NoiseException) { return null; }
        });
        Assert.Null(await read1.WaitAsync(T.Wait));
    }

    [Fact]
    public async Task UnauthenticatedConnection_CannotKillActiveSession()
    {
        var id = TransferId.Create();
        var started = new SemaphoreSlim(0);
        using var reg = _pending.Register(id, _phoneKey.PublicKey, async ch =>
        {
            started.Release();
            try { while (await ch.ReceiveAsync() is not null) { } }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or ProtocolException or NoiseException) { }
        });
        var (ch1, c1) = await T.ConnectDataAsync(_listener.Port, id, _phoneKey, _pcKey.PublicKey);
        using var _1 = c1;
        using var _2 = ch1;
        Assert.True(await started.WaitAsync(T.Wait));

        using var stranger = NoiseKeyPair.Generate();
        await Assert.ThrowsAnyAsync<Exception>(() => T.ConnectDataAsync(_listener.Port, id, stranger, _pcKey.PublicKey));

        // Etkin oturum hâlâ yazabiliyor ve kapanmadı.
        await ch1.SendSignalAsync(new StatusMessage(null, false));
        Assert.Equal(0, started.CurrentCount);   // yabancı bağlantı işleyiciyi tetiklemedi
    }

    [Fact]
    public async Task VersionMismatch_GetsUpdateReply()
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, _listener.Port);
        var stream = client.GetStream();
        var prefix = new Prefix(2, TransportKind.Tcp, TransferId.Create().Span).Bytes;
        await stream.WriteAsync(prefix);
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply).AsTask().WaitAsync(T.Wait);
        Assert.Equal(new byte[] { 0xFF, Prefix.CurrentVersion }, reply);
    }

    [Fact]
    public async Task Ipv6Loopback_Works_WhenSupported()
    {
        if (!Socket.OSSupportsIPv6) return;
        var id = TransferId.Create();
        var started = new SemaphoreSlim(0);
        using var reg = _pending.Register(id, _phoneKey.PublicKey, ch => { started.Release(); return Task.CompletedTask; });
        using var client = new TcpClient(AddressFamily.InterNetworkV6);
        await client.ConnectAsync(IPAddress.IPv6Loopback, _listener.Port);
        var transport = new TcpMessageTransport(client.GetStream());
        using var channel = await SecureChannel.ConnectIkAsync(transport, Prefix.Create(TransportKind.Tcp, id), _phoneKey, _pcKey.PublicKey);
        Assert.True(await started.WaitAsync(T.Wait));
    }
}
