using System.Threading.Channels;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;

namespace WinDrop.Platform;

/// <summary>
/// PROTOCOL §7.3: küçük dosyalar için BLE sinyal oturumu üstünde §6 çerçeveleri. Oturumun tek okuyucusu
/// <see cref="TransferCoordinator.HandleSessionAsync"/> döngüsüdür; gelen çerçeveleri <see cref="Post"/> ile buraya verir.
/// Gönderme hız sınırlıdır (Bluetooth sesi kesmesin; ARCHITECTURE "Ölçüm bulguları").
/// </summary>
internal sealed class BleDataChannel : IFrameChannel, IDisposable
{
    /// <summary>Alıcı tarafta bekleyen çerçeve üst sınırı (aşılırsa oturum düşer; ≤ 64 KB aktarım için çok bol).</summary>
    private const int InboxCapacity = 512;

    private readonly SecureChannel _session;
    private readonly int _bytesPerSecond;
    private readonly TimeProvider _time;
    private readonly Channel<Frame> _inbox = Channel.CreateBounded<Frame>(new BoundedChannelOptions(InboxCapacity)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly object _paceLock = new();
    private long _nextSendTimestamp;
    private int _frameSeen;
    private int _frameSinceCheck;

    /// <param name="session">Kurulmuş BLE oturumu (PeerSession.Channel).</param>
    /// <param name="bytesPerSecond">Gönderim hız üst sınırı; ≤ 0: sınırsız (yalnız test).</param>
    public BleDataChannel(SecureChannel session, int bytesPerSecond, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(time);
        _session = session;
        _bytesPerSecond = bytesPerSecond;
        _time = time;
    }

    public SecureChannel Session => _session;

    /// <summary>PROTOCOL §7.3: BLE DATA parçası ≤ 2048 B (iOS seçimi); MANIFEST sayfaları da bu sınıra göre bölünür.</summary>
    public const int MaxDataChunk = 2048;

    public int MaxFrameBodySize => Math.Min(_session.MaxFrameBodySize, MaxDataChunk);

    public ValueTask EnsureEstablishedAsync(CancellationToken cancellationToken = default) =>
        _session.IsEstablished
            ? ValueTask.CompletedTask
            : throw new InvalidOperationException("BLE oturumu kurulmamış.");

    /// <summary>Oturum döngüsünden gelen çerçeve. false: kuyruk dolu veya kanal kapalı (çağıran oturumu düşürür).</summary>
    public bool Post(Frame frame)
    {
        if (!_inbox.Writer.TryWrite(frame)) return false;
        Volatile.Write(ref _frameSeen, 1);
        Volatile.Write(ref _frameSinceCheck, 1);
        return true;
    }

    /// <summary>Eşten en az bir çerçeve geldi mi (eş BLE'den hiç yanıt vermiyorsa zaman aşımı için).</summary>
    public bool FrameSeen => Volatile.Read(ref _frameSeen) != 0;

    /// <summary>Son çağrıdan beri çerçeve geldi mi (sessizlik bekçisi; çağrı sayacı sıfırlar).</summary>
    public bool TakeFrameSeenSinceLastCheck() => Interlocked.Exchange(ref _frameSinceCheck, 0) != 0;

    /// <summary>Oturum koptu: bekleyen okuyucu null (bağlantı koptu) alır.</summary>
    public void Complete() => _inbox.Writer.TryComplete();

    public async ValueTask SendFrameAsync(ReadOnlyMemory<byte> encodedFrame, CancellationToken cancellationToken = default)
    {
        await PaceAsync(encodedFrame.Length, cancellationToken).ConfigureAwait(false);
        await _session.SendFrameAsync(encodedFrame, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Frame?> ReceiveFrameAsync(CancellationToken cancellationToken = default)
    {
        while (await _inbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_inbox.Reader.TryRead(out var frame)) return frame;
        }
        return null;
    }

    /// <summary>Çerçeve başlangıçları arasında bayt/hız kadar boşluk bırakır (ilk çerçeve hemen gider).</summary>
    private async ValueTask PaceAsync(int bytes, CancellationToken cancellationToken)
    {
        if (_bytesPerSecond <= 0) return;
        TimeSpan wait;
        lock (_paceLock)
        {
            var now = _time.GetTimestamp();
            var start = Math.Max(now, _nextSendTimestamp);
            wait = _time.GetElapsedTime(now, start);
            _nextSendTimestamp = start + (long)(bytes * (double)_time.TimestampFrequency / _bytesPerSecond);
        }
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => Complete();
}
