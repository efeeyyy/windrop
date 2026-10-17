using System.Security.Cryptography;
using System.Threading.Channels;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Core.Transfer;

namespace WinDrop.Core.Tests.Support;

/// <summary>Geçici dizin + iki cihaz anahtarı + bellek içi Noise kanalı kurulumu.</summary>
public sealed class Rig : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "windrop-tests", Guid.NewGuid().ToString("N"));
    public NoiseKeyPair PhoneKey { get; } = NoiseKeyPair.Generate();     // iPhone: başlatan
    public NoiseKeyPair PcKey { get; } = NoiseKeyPair.Generate();        // PC: yanıtlayan
    public TransferId TransferId { get; } = TransferId.Create();

    public string PhoneId => DeviceId.HexFromPublicKey(PhoneKey.PublicKey);
    public string PcId => DeviceId.HexFromPublicKey(PcKey.PublicKey);

    public Rig() => Directory.CreateDirectory(Root);

    public string Dir(string name)
    {
        var p = Path.Combine(Root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public Prefix TcpPrefix => Prefix.Create(TransportKind.Tcp, TransferId);

    /// <summary>iPhone (başlatan) ve PC (yanıtlayan) IK ile bağlanır.</summary>
    public async Task<Link> ConnectAsync(bool droppable = false, int maxChunk = int.MaxValue)
    {
        var (a, b) = ChannelStream.CreatePair(maxChunk);
        IMessageTransport phoneT = new TcpMessageTransport(a);
        DroppingTransport? dropper = null;
        if (droppable) phoneT = dropper = new DroppingTransport(phoneT, a.CompleteWriting);
        var pcT = new TcpMessageTransport(b);

        var phone = SecureChannel.ConnectIkAsync(phoneT, TcpPrefix, PhoneKey, PcKey.PublicKey);
        var pc = SecureChannel.AcceptIkAsync(pcT, PcKey, k => k.Span.SequenceEqual(PhoneKey.PublicKey));
        await Task.WhenAll(phone, pc);
        return new Link(await phone, await pc, a, b) { Dropper = dropper };
    }

    public FileSystemPartialStore NewStore(string name, Func<long>? free = null, IFileCommitHook? hook = null) =>
        new(Dir(name + "-partial"), Dir(name + "-final"), hook, free ?? (() => long.MaxValue / 2));

    public void Dispose()
    {
        PhoneKey.Dispose();
        PcKey.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    public sealed record Link(SecureChannel Phone, SecureChannel Pc, ChannelStream PhoneStream, ChannelStream PcStream) : IDisposable
    {
        public DroppingTransport? Dropper { get; init; }

        public void Dispose()
        {
            Phone.Dispose();
            Pc.Dispose();
        }
    }
}

/// <summary>Bellek içinde verilen bayt dizisini kaynak olarak sunar (ad serbest: tehlikeli adlar için).</summary>
public sealed class MemoryFileSource(string name, byte[] data, long mtimeMs = 1_700_000_000_000) : IFileSource
{
    public string Name { get; } = name;
    public long Size => data.Length;
    public long MtimeMs { get; } = mtimeMs;
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false));
}

/// <summary>Eşzamanlı çağrılan IProgress (SynchronizationContext'e bağlı değil).</summary>
public sealed class SyncProgress(Action<TransferProgress> onReport) : IProgress<TransferProgress>
{
    public List<TransferProgress> Events { get; } = [];
    public void Report(TransferProgress value)
    {
        lock (Events) Events.Add(value);
        onReport(value);
    }
}

/// <summary>Belirli sayıda mesajdan sonra bağlantıyı koparan taşıma sarmalayıcısı.</summary>
public sealed class DroppingTransport(IMessageTransport inner, Action drop) : IMessageTransport
{
    private int _sent;
    public int FailAfter { get; set; } = int.MaxValue;

    /// <summary>Şu andan itibaren <paramref name="messages"/> mesaj sonra kopar.</summary>
    public void Arm(int messages) => FailAfter = _sent + messages;

    public TransportKind Kind => inner.Kind;
    public int MaxMessageSize => inner.MaxMessageSize;
    public ValueTask SendRawAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => inner.SendRawAsync(data, ct);
    public ValueTask<byte[]> ReceiveRawAsync(int length, CancellationToken ct) => inner.ReceiveRawAsync(length, ct);
    public ValueTask<byte[]?> ReceiveMessageAsync(bool handshakePhase, CancellationToken ct) => inner.ReceiveMessageAsync(handshakePhase, ct);

    public ValueTask SendMessageAsync(ReadOnlyMemory<byte> noiseMessage, CancellationToken ct)
    {
        if (++_sent > FailAfter)
        {
            drop();
            throw new IOException("bağlantı koptu (test)");
        }
        return inner.SendMessageAsync(noiseMessage, ct);
    }
}

/// <summary>Bellek içi BLE bağlantısı çifti (GATT yükü kuyruğu).</summary>
public sealed class MemoryBleLink : IBleLink
{
    private readonly Channel<byte[]> _in;
    private readonly Channel<byte[]> _out;

    private MemoryBleLink(int mtu, Channel<byte[]> input, Channel<byte[]> output)
    {
        Mtu = mtu;
        _in = input;
        _out = output;
    }

    public static (MemoryBleLink Phone, MemoryBleLink Pc) CreatePair(int mtu)
    {
        var c2p = Channel.CreateUnbounded<byte[]>();
        var p2c = Channel.CreateUnbounded<byte[]>();
        return (new MemoryBleLink(mtu, p2c, c2p), new MemoryBleLink(mtu, c2p, p2c));
    }

    public int Mtu { get; }
    public List<int> SentSizes { get; } = [];

    public ValueTask SendAsync(ReadOnlyMemory<byte> gattPayload, CancellationToken cancellationToken)
    {
        if (gattPayload.Length > Mtu - 3) throw new InvalidOperationException("GATT yükü MTU − 3'ü aşıyor.");
        SentSizes.Add(gattPayload.Length);
        return _out.Writer.WriteAsync(gattPayload.ToArray(), cancellationToken);
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (!await _in.Reader.WaitToReadAsync(cancellationToken)) return null;
        return _in.Reader.TryRead(out var x) ? x : null;
    }

    public void Close() => _out.Writer.TryComplete();
}

public static class TestData
{
    public static byte[] Random(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
