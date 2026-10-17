using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Core.Transfer;
using Xunit;

namespace WinDrop.Platform.Tests;

/// <summary>Bellek içi BLE bağlantısı çifti (GATT yükü kuyruğu).</summary>
internal sealed class MemoryBleLink : IBleLink
{
    private readonly Channel<byte[]> _in;
    private readonly Channel<byte[]> _out;

    private MemoryBleLink(int mtu, Channel<byte[]> input, Channel<byte[]> output)
    {
        Mtu = mtu;
        _in = input;
        _out = output;
    }

    public static (MemoryBleLink Phone, MemoryBleLink Pc) CreatePair(int mtu = 247)
    {
        var c2p = Channel.CreateUnbounded<byte[]>();
        var p2c = Channel.CreateUnbounded<byte[]>();
        return (new MemoryBleLink(mtu, p2c, c2p), new MemoryBleLink(mtu, c2p, p2c));
    }

    public int Mtu { get; }

    public ValueTask SendAsync(ReadOnlyMemory<byte> gattPayload, CancellationToken cancellationToken)
    {
        if (gattPayload.Length > Mtu - 3) throw new InvalidOperationException("GATT yükü MTU − 3'ü aşıyor.");
        return _out.Writer.WriteAsync(gattPayload.ToArray(), cancellationToken);
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (!await _in.Reader.WaitToReadAsync(cancellationToken)) return null;
        return _in.Reader.TryRead(out var x) ? x : null;
    }

    public void Close() => _out.Writer.TryComplete();
}

internal sealed class MemoryFileSource(string name, byte[] data, long mtimeMs = 1_700_000_000_000) : IFileSource
{
    public string Name { get; } = name;
    public long Size => data.Length;
    public long MtimeMs { get; } = mtimeMs;
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false));
}

internal static class T
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    public static byte[] Random(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static FileSystemPartialStore Store(string root, string name) =>
        new(Path.Combine(root, name + "-partial"), Path.Combine(root, name + "-final"), null, () => long.MaxValue / 2);

    /// <summary>iPhone rolü: TCP veri kanalına bağlanır ve IK başlatır.</summary>
    public static async Task<(SecureChannel Channel, TcpClient Client)> ConnectDataAsync(
        int port, TransferId id, NoiseKeyPair phoneKey, byte[] pcPublicKey, IPAddress? address = null, bool xx = false)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(address ?? IPAddress.Loopback, port).WaitAsync(Wait);
            client.NoDelay = true;
            var transport = new TcpMessageTransport(client.GetStream());
            var prefix = Prefix.Create(TransportKind.Tcp, id);
            var channel = await (xx
                    ? SecureChannel.ConnectXxAsync(transport, prefix, phoneKey)
                    : SecureChannel.ConnectIkAsync(transport, prefix, phoneKey, pcPublicKey))
                .WaitAsync(Wait);
            return (channel, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

/// <summary>Bluetooth ses durumu sahtesi (gerçek Core Audio'ya dokunmaz).</summary>
internal sealed class FakeBluetoothAudioMonitor : IBluetoothAudioMonitor
{
    private BluetoothAudioState _current = BluetoothAudioState.None;

    public BluetoothAudioState Current => _current;

    public event Action<BluetoothAudioState>? Changed;

    public void Set(bool btAudio) => Set(new BluetoothAudioState(btAudio, btAudio, btAudio ? "Fake BT" : null));

    public void Set(BluetoothAudioState state)
    {
        var changed = _current.BtAudio != state.BtAudio;
        _current = state;
        if (changed) Changed?.Invoke(state);
    }
}

/// <summary>Etkin nokta sahtesi (gerçek etkin nokta asla açılmaz).</summary>
internal sealed class FakeHotspot : IHotspotController
{
    public bool Result { get; set; } = true;
    public Exception? Throw { get; set; }
    public int StartCalls;
    public int Started;
    public int Finished;

    public Task<bool> StartAsync(HotspotConfig config, CancellationToken ct = default)
    {
        Interlocked.Increment(ref StartCalls);
        if (Throw is not null) throw Throw;
        return Task.FromResult(Result);
    }

    public void TransferStarted() => Interlocked.Increment(ref Started);
    public void TransferFinished() => Interlocked.Increment(ref Finished);
}

/// <summary>PC tarafı bileşenlerinin tamamı (gerçek DPAPI depoları geçici dizinde, gerçek localhost TCP, bellek içi BLE).</summary>
internal sealed class Harness : IAsyncDisposable
{
    public TempDir Dir { get; } = new();
    public AppSettings Settings { get; }
    public IdentityStore Identity { get; }
    public TrustedDevices Trusted { get; }
    public HotspotConfigStore HotspotCfg { get; }
    public PendingTransferTable Pending { get; } = new();
    public PairingService Pairing { get; }
    public SignalSessionAcceptor Acceptor { get; }
    public DataListener Listener { get; }
    public TransferCoordinator Coord { get; }
    public FakeHotspot Hotspot { get; } = new();
    public NoiseKeyPair PhoneKey { get; } = NoiseKeyPair.Generate();
    public string PhoneId { get; }
    public List<Task> LinkTasks { get; } = [];
    public string DownloadDir => Settings.DownloadFolder;

    public FakeBluetoothAudioMonitor BtAudio { get; } = new();

    /// <param name="blePace">BLE veri yolu hız sınırı (bayt/sn); testlerde varsayılan sınırsız.</param>
    public Harness(bool autoAccept = true, bool trustPhone = true, TransferTimeouts? timeouts = null, int blePace = 0,
        string relayUrl = "", HttpMessageHandler? pushHandler = null, TimeProvider? time = null)
    {
        Settings = new AppSettings { DownloadFolder = Path.Combine(Dir.Path, "downloads"), AutoAccept = autoAccept, RelayUrl = relayUrl };
        var root = Path.Combine(Dir.Path, "pc");
        Identity = new IdentityStore(root);
        Trusted = new TrustedDevices(root);
        HotspotCfg = new HotspotConfigStore(root);
        Pairing = new PairingService(Identity, Trusted, HotspotCfg, "TestPC", TimeSpan.FromMinutes(2));
        Acceptor = new SignalSessionAcceptor(Identity, Trusted, Pairing);
        Listener = new DataListener(Identity.LoadOrCreate(), Pending, 0);
        Listener.Start();
        Coord = new TransferCoordinator(new TransferCoordinatorOptions
        {
            Settings = Settings,
            Pending = Pending,
            Trusted = Trusted,
            PartialRoot = Path.Combine(root, "partial"),
            DataPort = () => Listener.Port,
            LocalName = "TestPC",
            Hotspot = Hotspot,
            HotspotConfig = HotspotCfg.GetOrCreate,
            RotateHotspotPassphrase = () => HotspotCfg.RotatePassphrase(),
            CandidateAddresses = () => ["127.0.0.1"],
            HotspotAddress = () => "127.0.0.1",
            CommitHook = new MotwCommitHook(),
            BluetoothAudio = BtAudio,
            BleBytesPerSecond = blePace,
            LocalKey = Identity.LoadOrCreate(),
            PushHandler = pushHandler,
            Time = time ?? TimeProvider.System,
            Timeouts = timeouts ?? new TransferTimeouts
            {
                Accept = TimeSpan.FromSeconds(10),
                Approval = TimeSpan.FromSeconds(10),
                Connect = TimeSpan.FromSeconds(3),
                HotspotConnect = TimeSpan.FromSeconds(3),
                Reconnect = TimeSpan.FromSeconds(3),
                RelayPeer = TimeSpan.FromSeconds(8),
                RelayApproval = TimeSpan.FromSeconds(10),
                Poll = TimeSpan.FromMilliseconds(20),
            },
        });
        Acceptor.SessionHandler = Coord.HandleSessionAsync;
        PhoneId = DeviceId.HexFromPublicKey(PhoneKey.PublicKey);
        if (trustPhone) Trusted.Add(PhoneKey.PublicKey, "Test iPhone");
    }

    public byte[] PcPublicKey => Identity.PublicKey;
    public string PcId => DeviceId.HexFromPublicKey(PcPublicKey);

    /// <summary>iPhone IK ile BLE'ye bağlanır, PC hello'sunu okur, kendi hello'sunu yollar.</summary>
    public async Task<Phone> ConnectPhoneAsync(string? wifi, NoiseKeyPair? key = null, bool xx = false, bool btAudio = false)
    {
        var (phoneLink, pcLink) = MemoryBleLink.CreatePair();
        LinkTasks.Add(Acceptor.RunLinkAsync(pcLink, CancellationToken.None));
        var transport = new BleMessageTransport(phoneLink);
        var channel = await (xx
                ? SecureChannel.ConnectXxAsync(transport, Prefix.CreateBle(), key ?? PhoneKey, k => k.Span.SequenceEqual(PcPublicKey))
                : SecureChannel.ConnectIkAsync(transport, Prefix.CreateBle(), key ?? PhoneKey, PcPublicKey)).WaitAsync(T.Wait);
        var phone = new Phone(this, channel, phoneLink);
        var hello = Assert.IsType<HelloMessage>(await phone.ReceiveAsync());
        Assert.Equal("TestPC", hello.Name);
        phone.PcHello = hello;
        await phone.SendAsync(new HelloMessage(1, "iPhone", ["tcp", "hotspot"], wifi, btAudio));
        var deadline = DateTime.UtcNow + T.Wait;
        while (!Coord.IsNearby(DeviceId.HexFromPublicKey((key ?? PhoneKey).PublicKey)))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("PC hello'yu işlemedi.");
            await Task.Delay(10);
        }
        return phone;
    }

    public async ValueTask DisposeAsync()
    {
        await Coord.DisposeAsync();
        await Listener.DisposeAsync();
        PhoneKey.Dispose();
        Dir.Dispose();
    }
}

internal sealed class Phone(Harness h, SecureChannel channel, MemoryBleLink link) : IDisposable
{
    public SecureChannel Channel { get; } = channel;
    public string Sas => WinDrop.Platform.Sas.Compute(Channel.HandshakeHash);
    public Harness Harness { get; } = h;

    /// <summary>PC'nin <c>hello</c> mesajı.</summary>
    public HelloMessage? PcHello { get; set; }

    public Task SendAsync(SignalMessage m) => Channel.SendSignalAsync(m).AsTask().WaitAsync(T.Wait);

    public async Task<SignalMessage> ReceiveAsync() =>
        await Channel.ReceiveSignalAsync().AsTask().WaitAsync(T.Wait) ?? throw new InvalidOperationException("BLE oturumu kapandı");

    public void Dispose()
    {
        link.Close();
        Channel.Dispose();
    }
}
