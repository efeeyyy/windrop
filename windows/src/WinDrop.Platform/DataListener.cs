using System.Net;
using System.Net.Sockets;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;

namespace WinDrop.Platform;

/// <summary>Kurulmuş (IK + ilk taşıma mesajı doğrulanmış) veri oturumunu işleyen aktarım koşucusu.</summary>
public delegate Task DataSessionHandler(SecureChannel channel);

/// <summary>
/// Veri kanalında beklenen aktarımlar (PROTOCOL §5): <c>transferId → (eşin açık anahtarı, işleyici)</c>.
/// Kayıt <see cref="IDisposable"/> ile kaldırılır; kaldırılınca etkin bağlantı da kapanır.
/// </summary>
public sealed class PendingTransferTable
{
    private readonly object _lock = new();
    private readonly Dictionary<TransferId, Entry> _entries = [];

    public IDisposable Register(TransferId id, byte[] expectedPeerPublicKey, DataSessionHandler handler, HandshakeKind? requiredKind = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(expectedPeerPublicKey);
        ArgumentNullException.ThrowIfNull(handler);
        var entry = new Entry(this, id, (byte[])expectedPeerPublicKey.Clone(), handler, requiredKind);
        lock (_lock)
        {
            if (!_entries.TryAdd(id, entry)) throw new InvalidOperationException("transferId zaten kayıtlı.");
        }
        return entry;
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    internal bool TryGet(TransferId id, out Entry entry)
    {
        lock (_lock) return _entries.TryGetValue(id, out entry!);
    }

    private void Remove(Entry entry)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(entry.Id, out var cur) && ReferenceEquals(cur, entry)) _entries.Remove(entry.Id);
        }
    }

    internal sealed class Entry(PendingTransferTable owner, TransferId id, byte[] peerKey, DataSessionHandler handler, HandshakeKind? requiredKind) : IDisposable
    {
        private readonly object _gate = new();
        private IDisposable? _active;
        private bool _disposed;

        public TransferId Id { get; } = id;
        public byte[] PeerKey { get; } = peerKey;
        public DataSessionHandler Handler { get; } = handler;

        /// <summary>BLE oturumunun deseni: IK ise TCP'de de IK, XX ise XX (null: denetleme).</summary>
        public HandshakeKind? RequiredKind { get; } = requiredKind;

        /// <summary>Yeni (kimliği doğrulanmış) bağlantı eskisini kapatır. Kayıt kaldırılmışsa false.</summary>
        public bool TrySetActive(IDisposable connection)
        {
            IDisposable? old;
            lock (_gate)
            {
                if (_disposed) return false;
                old = _active;
                _active = connection;
            }
            old?.Dispose();
            return true;
        }

        public void ClearActive(IDisposable connection)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, connection)) _active = null;
            }
        }

        public void Dispose()
        {
            IDisposable? active;
            lock (_gate)
            {
                _disposed = true;
                active = _active;
                _active = null;
            }
            owner.Remove(this);
            active?.Dispose();
        }
    }
}

/// <summary>
/// TCP veri kanalı dinleyicisi (PROTOCOL §5; port 53600, IPv4+IPv6). Ön ek → <c>transferId</c> bekleyen aktarımlarda yoksa kapat →
/// IK yanıtlayan; karşı anahtar o aktarımın eşi değilse kapat → kayıtlı işleyiciye devret.
/// Aynı <c>transferId</c>'ye ikinci (doğrulanmış) bağlantı eskisini kapatır; kimliği doğrulanmamış bağlantı etkin oturuma dokunamaz.
/// </summary>
public sealed class DataListener : IAsyncDisposable
{
    public const int DefaultPort = 53600;

    private readonly NoiseKeyPair _localKey;
    private readonly PendingTransferTable _pending;
    private readonly int _requestedPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<IDisposable> _connections = [];
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public DataListener(NoiseKeyPair localKey, PendingTransferTable pending, int port = DefaultPort)
    {
        ArgumentNullException.ThrowIfNull(localKey);
        ArgumentNullException.ThrowIfNull(pending);
        _localKey = localKey;
        _pending = pending;
        _requestedPort = port;
    }

    /// <summary>El sıkışma için üst süre (ön ek + IK + ilk taşıma mesajı).</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Dinlenen gerçek port (<see cref="Start"/> sonrası).</summary>
    public int Port { get; private set; }

    public void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("DataListener zaten başlatıldı.");
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Any, _requestedPort);
            listener.Server.DualMode = true;   // IPv4 + IPv6
            listener.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressFamilyNotSupported)
        {
            listener = new TcpListener(IPAddress.Any, _requestedPort);
            listener.Start();
            PlatformLog.Write("DataListener: IPv6 yok, yalnız IPv4 dinleniyor.");
        }
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        PlatformLog.Write($"DataListener {Port} dinliyor");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener));
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                PlatformLog.Write($"DataListener kabul hatası: {ex.SocketErrorCode}");
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        var remote = SafeEndpoint(client);
        var holder = new ConnectionHolder(client);
        lock (_gate) _connections.Add(holder);
        PendingTransferTable.Entry? entry = null;
        try
        {
            client.NoDelay = true;
            var transport = new TcpMessageTransport(client.GetStream());
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            hs.CancelAfter(HandshakeTimeout);

            var raw = await transport.ReceiveRawAsync(Prefix.Length, hs.Token).ConfigureAwait(false);
            var prefix = Prefix.Decode(raw);
            if (prefix.Transport != TransportKind.Tcp)
            {
                PlatformLog.Write($"DataListener {remote}: TCP olmayan taşıma türü ({prefix.Transport}), kapatıldı.");
                return;
            }
            // Sürüm uyuşmazlığı: AcceptIk 0xFF||sürüm yanıtını kendisi yollar. Aksi halde transferId bilinmeli.
            if (prefix.IsVersionSupported(Prefix.CurrentVersion))
            {
                if (!_pending.TryGet(new TransferId(prefix.TransferId), out entry))
                {
                    PlatformLog.Write($"DataListener {remote}: tanınmayan transferId, kapatıldı.");
                    return;
                }
            }

            var expected = entry;
            // IK (tanıdık/eşli) veya XX (bilinmeyen, §2b): her ikisinde karşı anahtar, aktarımın BLE oturumundaki eşle aynı olmalı.
            Func<ReadOnlyMemory<byte>, bool> samePeer = key => expected is not null && key.Span.SequenceEqual(expected.PeerKey);
            var channel = await SecureChannel.AcceptAutoAsync(
                new PrefixReplayTransport(transport, raw), _localKey, samePeer, samePeer, null, hs.Token).ConfigureAwait(false);
            using (channel)
            {
                if (entry!.RequiredKind is { } required && channel.Kind != required)
                {
                    PlatformLog.Write($"DataListener {remote}: el sıkışma deseni {channel.Kind}, BLE oturumu {required}; kapatıldı.");
                    return;
                }
                await channel.EnsureEstablishedAsync(hs.Token).ConfigureAwait(false);   // PROTOCOL §1: 0-RTT yok
                if (!entry!.TrySetActive(holder))
                {
                    PlatformLog.Write($"DataListener {remote}: aktarım kaydı artık yok, kapatıldı.");
                    return;
                }
                PlatformLog.Write($"DataListener {remote}: veri oturumu kuruldu ({new TransferId(prefix.TransferId)})");
                try
                {
                    await entry.Handler(channel).ConfigureAwait(false);
                }
                finally
                {
                    entry.ClearActive(holder);
                }
            }
        }
        catch (PeerNotTrustedException)
        {
            PlatformLog.Write($"DataListener {remote}: güvenilmeyen/yanlış eş anahtarı, kapatıldı.");
        }
        catch (VersionMismatchException ex)
        {
            PlatformLog.Write($"DataListener {remote}: sürüm uyuşmazlığı (istemci {ex.PeerVersion}).");
        }
        catch (Exception ex) when (ex is ProtocolException or NoiseException or IOException or SocketException
                                       or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            PlatformLog.Write($"DataListener {remote}: oturum sona erdi ({ex.GetType().Name}: {ex.Message})");
        }
        catch (Exception ex)
        {
            PlatformLog.Write($"DataListener {remote}: beklenmeyen hata: {ex}");
        }
        finally
        {
            lock (_gate) _connections.Remove(holder);
            holder.Dispose();
        }
    }

    private static string SafeEndpoint(TcpClient client)
    {
        try { return client.Client.RemoteEndPoint?.ToString() ?? "?"; }
        catch (Exception) { return "?"; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        try { _listener?.Stop(); } catch (Exception) { }
        IDisposable[] open;
        lock (_gate) open = [.. _connections];
        foreach (var c in open) c.Dispose();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (Exception) { }
        }
        _stop.Dispose();
    }

    private sealed class ConnectionHolder(TcpClient client) : IDisposable
    {
        public void Dispose()
        {
            try { client.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>Ön ek dinleyici tarafından zaten okundu; <see cref="SecureChannel.AcceptIkAsync"/> aynı 20 baytı yeniden "okur".</summary>
    private sealed class PrefixReplayTransport(IMessageTransport inner, byte[] prefix) : IMessageTransport
    {
        private bool _replayed;

        public TransportKind Kind => inner.Kind;
        public int MaxMessageSize => inner.MaxMessageSize;

        public ValueTask SendRawAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            inner.SendRawAsync(data, cancellationToken);

        public ValueTask<byte[]> ReceiveRawAsync(int length, CancellationToken cancellationToken)
        {
            if (_replayed || length != prefix.Length) return inner.ReceiveRawAsync(length, cancellationToken);
            _replayed = true;
            return ValueTask.FromResult((byte[])prefix.Clone());
        }

        public ValueTask SendMessageAsync(ReadOnlyMemory<byte> noiseMessage, CancellationToken cancellationToken) =>
            inner.SendMessageAsync(noiseMessage, cancellationToken);

        public ValueTask<byte[]?> ReceiveMessageAsync(bool handshakePhase, CancellationToken cancellationToken) =>
            inner.ReceiveMessageAsync(handshakePhase, cancellationToken);
    }
}
