using System.Diagnostics;

namespace WinDrop.Core.Transfer;

/// <summary>PROTOCOL §5 sabitleri.</summary>
public static class TransferLimits
{
    public const int MaxFiles = 10_000;
    public const int MaxNameLength = 255;
    /// <summary>Uygulamaların kullandığı DATA gövdesi boyutu (spesifikasyon üst sınırı 65518).</summary>
    public const int DefaultChunkSize = 64_000;
    /// <summary>ACK aralığı: ~4 MB.</summary>
    public const int DefaultAckIntervalBytes = 4 * 1024 * 1024;
    /// <summary>Kabulden önce boş alandan düşülen pay.</summary>
    public const long DefaultSpaceMarginBytes = 64L * 1024 * 1024;
}

/// <summary>offer mesajındaki <c>count</c>/<c>total</c>; MANIFEST ile uyuşmazsa alıcı keser.</summary>
public sealed record OfferInfo(int Count, long Total);

public enum TransferOutcome
{
    /// <summary>Tüm dosyalar doğrulandı.</summary>
    Completed,
    /// <summary>Bitti ama bazı dosyalar SHA-256 uyuşmazlığıyla başarısız (<see cref="TransferResult.FailedFileIds"/>).</summary>
    CompletedWithFailures,
    /// <summary>İptal (yerel veya uzak, <see cref="TransferResult.CanceledByRemote"/>).</summary>
    Canceled,
    /// <summary>Alıcı bir sınır/politika ihlali nedeniyle kesti (<see cref="TransferResult.Error"/>).</summary>
    Aborted,
}

/// <summary>Alıcıda diske inen dosya.</summary>
public sealed record ReceivedFile(int Id, string OriginalName, string StoredPath);

public sealed record TransferResult(TransferOutcome Outcome, IReadOnlyList<int> FailedFileIds)
{
    /// <summary>İptali karşı taraf başlattı.</summary>
    public bool CanceledByRemote { get; init; }

    /// <summary>Kesme/iptal nedeni (karşıdan gelen <c>reason</c> dahil).</summary>
    public string? Error { get; init; }

    /// <summary>Dosya başına RESUME ofseti (baştan başlayanlar 0).</summary>
    public IReadOnlyDictionary<int, long> ResumeOffsets { get; init; } = new Dictionary<int, long>();

    /// <summary>Alıcıda diske inen dosyalar.</summary>
    public IReadOnlyList<ReceivedFile> ReceivedFiles { get; init; } = [];
}

/// <summary>İlerleme olayı. Bayt sayıları devam edilen ofseti de içerir (ilerleme çubuğu doğru kalsın);
/// <see cref="BytesPerSecond"/> yalnız bu oturumda aktarılanlardan hesaplanır.</summary>
public sealed record TransferProgress(
    int FileId,
    string FileName,
    int FileIndex,
    int FileCount,
    long FileBytesDone,
    long FileSize,
    long BytesDone,
    long TotalBytes,
    double BytesPerSecond);

/// <summary>Aktarım ortasında bağlantı koptu (EOF). Kısmi dosyalar saklanır; yeni oturumla devam edilebilir.</summary>
public sealed class ConnectionLostException : IOException
{
    public ConnectionLostException() : base("Bağlantı aktarım ortasında kapandı.") { }
}

/// <summary>Hız ve olay sıklığı (en çok ~10/sn + her dosya sonu) denetimi.</summary>
internal sealed class ProgressTracker
{
    private readonly IProgress<TransferProgress>? _progress;
    private readonly Func<long> _clock;
    private readonly long _minInterval;
    private long _lastReport;
    private long _lastSampleTime;
    private long _lastSampleBytes;
    private long _sessionBytes;
    private double _ema;

    public ProgressTracker(IProgress<TransferProgress>? progress, TimeSpan? interval = null, Func<long>? clock = null)
    {
        _progress = progress;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _minInterval = (long)((interval ?? TimeSpan.FromMilliseconds(100)).TotalSeconds * Stopwatch.Frequency);
        _lastReport = _lastSampleTime = _clock();
        _lastReport -= _minInterval;   // ilk olay hemen gider
    }

    /// <summary>Bu oturumda gerçekten aktarılan baytları ekler.</summary>
    public void AddTransferred(long bytes) => _sessionBytes += bytes;

    public void Report(int fileId, string name, int index, int count, long fileDone, long fileSize, long done, long total, bool force)
    {
        if (_progress is null) return;
        var now = _clock();
        if (!force && now - _lastReport < _minInterval) return;

        var dt = now - _lastSampleTime;
        // İlk ölçüm erken (20 ms) yapılır ki kısa aktarımlarda da hız görünsün; sonrakiler ≥ 200 ms pencereyle yumuşatılır.
        if (dt >= (_ema == 0 ? Stopwatch.Frequency / 50 : Stopwatch.Frequency / 5))
        {
            var instant = (_sessionBytes - _lastSampleBytes) * (double)Stopwatch.Frequency / dt;
            _ema = _ema == 0 ? instant : 0.7 * _ema + 0.3 * instant;
            _lastSampleTime = now;
            _lastSampleBytes = _sessionBytes;
        }
        _lastReport = now;
        _progress.Report(new TransferProgress(fileId, name, index, count, fileDone, fileSize, done, total, _ema));
    }
}
