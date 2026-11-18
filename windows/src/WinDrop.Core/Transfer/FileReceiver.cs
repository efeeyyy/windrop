using System.Security.Cryptography;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;

namespace WinDrop.Core.Transfer;

/// <summary>Alıcı ayarları (PROTOCOL §6.5 sınırları). <see cref="PeerId"/> = <see cref="DeviceId.HexFromPublicKey"/>.</summary>
public sealed record ReceiverOptions(string PeerId, TransferId TransferId, OfferInfo Offer)
{
    /// <summary>Kabulden önce <c>total ≤ boş alan − pay</c>.</summary>
    public long SpaceMarginBytes { get; init; } = TransferLimits.DefaultSpaceMarginBytes;
    public int AckIntervalBytes { get; init; } = TransferLimits.DefaultAckIntervalBytes;
    public int MaxFiles { get; init; } = TransferLimits.MaxFiles;
    public int MaxNameLength { get; init; } = TransferLimits.MaxNameLength;
}

/// <summary>
/// PROTOCOL §6 alıcı. MANIFEST'i sınırlara karşı doğrular; her dosya için kısmi depodan devam ofsetini bulur
/// (boyut/mtime değiştiyse baştan), RESUME yollar, DATA'yı yazar, ACK'ten önce <c>flush(true)</c> yapar,
/// FILE_END'de 0. bayttan SHA-256'yı denetler; uyuşmazsa dosyayı siler ve <c>DONE.failed</c>'e koyar.
/// <para>İptal (CANCEL, yerel veya uzak) ve politika kesmeleri bu aktarımın kısmi dosyalarını siler;
/// bağlantı kopması saklar (devam için).</para>
/// Tek kullanımlıktır.
/// </summary>
public sealed class FileReceiver
{
    private readonly IFrameChannel _channel;
    private readonly IPartialStore _store;
    private readonly ReceiverOptions _options;
    private readonly ProgressTracker _tracker;
    private readonly List<ReceivedFile> _received = [];
    private readonly List<int> _failed = [];
    private readonly Dictionary<int, long> _resumeOffsets = [];
    private int _used;

    public FileReceiver(IFrameChannel channel, IPartialStore store, ReceiverOptions options, IProgress<TransferProgress>? progress = null, TimeSpan? progressInterval = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _channel = channel;
        _store = store;
        _options = options;
        _tracker = new ProgressTracker(progress, progressInterval);
    }

    private sealed class AbortException(string reason) : Exception(reason);
    private sealed class RemoteCancelException(string? reason) : Exception("remote cancel")
    {
        public string? Reason { get; } = reason;
    }

    public async Task<TransferResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0) throw new InvalidOperationException("FileReceiver tek kullanımlıktır.");
        try
        {
            return await ReceiveAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AbortException a)
        {
            await TransferCommon.TrySendCancelAsync(_channel, a.Message).ConfigureAwait(false);
            await _store.DeleteTransferAsync(_options.PeerId, _options.TransferId, CancellationToken.None).ConfigureAwait(false);
            return new TransferResult(TransferOutcome.Aborted, _failed.ToArray()) { Error = a.Message, ResumeOffsets = _resumeOffsets };
        }
        catch (RemoteCancelException r)
        {
            await _store.DeleteTransferAsync(_options.PeerId, _options.TransferId, CancellationToken.None).ConfigureAwait(false);
            return new TransferResult(TransferOutcome.Canceled, _failed.ToArray())
                { CanceledByRemote = true, Error = r.Reason, ResumeOffsets = _resumeOffsets };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TransferCommon.TrySendCancelAsync(_channel, null).ConfigureAwait(false);
            await _store.DeleteTransferAsync(_options.PeerId, _options.TransferId, CancellationToken.None).ConfigureAwait(false);
            return new TransferResult(TransferOutcome.Canceled, _failed.ToArray()) { ResumeOffsets = _resumeOffsets };
        }
        catch (ProtocolException)
        {
            await TransferCommon.TrySendCancelAsync(_channel, "protocol error").ConfigureAwait(false);
            throw;
        }
    }

    private async Task<TransferResult> ReceiveAllAsync(CancellationToken ct)
    {
        var first = await ReceiveAsync(ct).ConfigureAwait(false);
        if (first.Type != FrameType.Manifest) throw new ProtocolException($"İlk çerçeve MANIFEST olmalı ({first.Type} geldi).");
        var page = FrameCodec.DecodeManifest(first.Body.Span);
        var collected = new List<ManifestFile>(page.Files);
        if (collected.Count > _options.MaxFiles)
            throw new AbortException($"too many files ({collected.Count} > {_options.MaxFiles})");
        while (page.More)
        {
            var next = await ReceiveAsync(ct).ConfigureAwait(false);
            if (next.Type != FrameType.Manifest) throw new ProtocolException($"more=true sonrası MANIFEST beklendi ({next.Type} geldi).");
            page = FrameCodec.DecodeManifest(next.Body.Span);
            collected.AddRange(page.Files);
            if (collected.Count > _options.MaxFiles)
                throw new AbortException($"too many files ({collected.Count} > {_options.MaxFiles})");
        }
        var manifest = new ManifestBody(collected);
        ValidateManifest(manifest);

        var files = manifest.Files;
        var total = files.Sum(f => f.Size);
        long completedBytes = 0;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            await ReceiveFileAsync(file, index, files.Count, completedBytes, total, ct).ConfigureAwait(false);
            completedBytes += file.Size;
        }

        var done = new DoneBody(_failed.Count == 0, _failed.ToArray());
        await _channel.SendFrameAsync(FrameCodec.Encode(done), ct).ConfigureAwait(false);
        return new TransferResult(_failed.Count == 0 ? TransferOutcome.Completed : TransferOutcome.CompletedWithFailures, _failed.ToArray())
        {
            ResumeOffsets = _resumeOffsets,
            ReceivedFiles = _received,
        };
    }

    private void ValidateManifest(ManifestBody manifest)
    {
        var files = manifest.Files;
        if (files.Count > _options.MaxFiles)
            throw new AbortException($"too many files ({files.Count} > {_options.MaxFiles})");
        if (files.Count != _options.Offer.Count)
            throw new AbortException($"manifest count {files.Count} does not match offer {_options.Offer.Count}");

        var ids = new HashSet<int>();
        long total = 0;
        foreach (var f in files)
        {
            if (!ids.Add(f.Id)) throw new AbortException($"duplicate file id {f.Id}");
            if (f.Size < 0) throw new AbortException($"negative size for file {f.Id}");
            if (f.Name.Length > _options.MaxNameLength)
                throw new AbortException($"file name longer than {_options.MaxNameLength} characters (id {f.Id})");
            RelativeDirectory.Parse(f.Dir);   // §6.7: geçersiz dir → ProtocolException (hiçbir dosya yazılmadan)
            if (long.MaxValue - total < f.Size) throw new AbortException("manifest total overflows");
            total += f.Size;
        }
        if (total != _options.Offer.Total)
            throw new AbortException($"manifest total {total} does not match offer {_options.Offer.Total}");

        var free = _store.GetFreeSpaceBytes();
        if (total > free - _options.SpaceMarginBytes)
            throw new AbortException($"insufficient disk space (need {total}, free {free}, margin {_options.SpaceMarginBytes})");
    }

    private async Task ReceiveFileAsync(ManifestFile file, int index, int count, long completedBytes, long total, CancellationToken ct)
    {
        var key = new PartialKey(_options.PeerId, _options.TransferId, file.Id);

        // Önceki oturumda tamamlanmış ve teslim edilmiş dosya: baştan alma, yalnız SHA'yı doğrula.
        var completed = await _store.GetCompletedAsync(key, ct).ConfigureAwait(false);
        if (completed is not null && completed.Size == file.Size && completed.MtimeMs == file.Mtime)
        {
            await ReceiveAlreadyCompletedAsync(file, completed, index, count, completedBytes, total, ct).ConfigureAwait(false);
            return;
        }

        // Devam: boyut ve mtime aynıysa son ACK'lenen ofsetten, değiştiyse baştan.
        var partial = await _store.OpenExistingAsync(key, ct).ConfigureAwait(false);
        if (partial is not null && (partial.Size != file.Size || partial.MtimeMs != file.Mtime))
        {
            await partial.DeleteAsync(ct).ConfigureAwait(false);
            await partial.DisposeAsync().ConfigureAwait(false);
            partial = null;
        }
        partial ??= await _store.CreateAsync(key, file.Size, file.Mtime, ct).ConfigureAwait(false);

        await using (partial.ConfigureAwait(false))
        {
            var offset = Math.Min(partial.AckedOffset, file.Size);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await partial.ResumeAtAsync(offset, bytes => hash.AppendData(bytes.Span), ct).ConfigureAwait(false);
            _resumeOffsets[file.Id] = offset;

            await _channel.SendFrameAsync(FrameCodec.Encode(new ResumeBody(file.Id, offset)), ct).ConfigureAwait(false);

            var start = await ReceiveAsync(ct).ConfigureAwait(false);
            if (start.Type != FrameType.FileStart) throw new ProtocolException($"FILE_START beklenirken {start.Type} geldi.");
            var startBody = FrameCodec.DecodeFileStart(start.Body.Span);
            if (startBody.Id != file.Id || startBody.Offset != offset)
                throw new ProtocolException($"FILE_START ({startBody.Id}, {startBody.Offset}) RESUME ({file.Id}, {offset}) ile uyuşmuyor.");

            long written = offset;
            long sinceAck = 0;
            _tracker.Report(file.Id, file.Name, index, count, written, file.Size, completedBytes + written, total, force: true);

            FileEndBody end;
            while (true)
            {
                var frame = await ReceiveAsync(ct).ConfigureAwait(false);
                if (frame.Type == FrameType.Data)
                {
                    var data = frame.Body;
                    if (written + data.Length > file.Size)
                        throw new AbortException($"file {file.Id} exceeds declared size {file.Size}");
                    await partial.AppendAsync(data, ct).ConfigureAwait(false);
                    hash.AppendData(data.Span);
                    written += data.Length;
                    sinceAck += data.Length;
                    _tracker.AddTransferred(data.Length);
                    if (sinceAck >= _options.AckIntervalBytes)
                    {
                        // ACK yalnız veri diske (fsync) ve üst veri kalıcılaştıktan sonra.
                        await partial.SyncAckAsync(written, ct).ConfigureAwait(false);
                        await _channel.SendFrameAsync(FrameCodec.Encode(new AckBody(file.Id, written)), ct).ConfigureAwait(false);
                        sinceAck = 0;
                    }
                    _tracker.Report(file.Id, file.Name, index, count, written, file.Size, completedBytes + written, total, force: false);
                }
                else if (frame.Type == FrameType.FileEnd)
                {
                    end = FrameCodec.DecodeFileEnd(frame.Body.Span);
                    if (end.Id != file.Id) throw new ProtocolException($"FILE_END id {end.Id}, beklenen {file.Id}.");
                    break;
                }
                else
                {
                    throw new ProtocolException($"Dosya sürerken beklenmeyen çerçeve: {frame.Type}.");
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (written == file.Size && string.Equals(actual, end.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                var safeName = FileNameSanitizer.Sanitize(file.Name);
                var path = await partial.CommitAsync(safeName, RelativeDirectory.Parse(file.Dir), actual, ct).ConfigureAwait(false);
                _received.Add(new ReceivedFile(file.Id, file.Name, path));
            }
            else
            {
                await partial.DeleteAsync(ct).ConfigureAwait(false);   // bütünlük hatası: sil, DONE.failed'e gir
                _failed.Add(file.Id);
            }
            _tracker.Report(file.Id, file.Name, index, count, file.Size, file.Size, completedBytes + file.Size, total, force: true);
        }
    }

    /// <summary>
    /// Dosya önceki oturumda tamamen alınmıştı: RESUME ofseti = boyut. Gönderen FILE_START(boyut) ve FILE_END yollar
    /// (kaynağı baştan hash'ler); DATA gelmez. Kayıtlı SHA ile eşleşirse dosya teslim edilmiş sayılır.
    /// </summary>
    private async Task ReceiveAlreadyCompletedAsync(ManifestFile file, CompletedFile completed, int index, int count,
        long completedBytes, long total, CancellationToken ct)
    {
        _resumeOffsets[file.Id] = file.Size;
        await _channel.SendFrameAsync(FrameCodec.Encode(new ResumeBody(file.Id, file.Size)), ct).ConfigureAwait(false);

        var start = await ReceiveAsync(ct).ConfigureAwait(false);
        if (start.Type != FrameType.FileStart) throw new ProtocolException($"FILE_START beklenirken {start.Type} geldi.");
        var startBody = FrameCodec.DecodeFileStart(start.Body.Span);
        if (startBody.Id != file.Id || startBody.Offset != file.Size)
            throw new ProtocolException($"FILE_START ({startBody.Id}, {startBody.Offset}) RESUME ({file.Id}, {file.Size}) ile uyuşmuyor.");

        var endFrame = await ReceiveAsync(ct).ConfigureAwait(false);
        if (endFrame.Type != FrameType.FileEnd) throw new ProtocolException($"FILE_END beklenirken {endFrame.Type} geldi.");
        var end = FrameCodec.DecodeFileEnd(endFrame.Body.Span);
        if (end.Id != file.Id) throw new ProtocolException($"FILE_END id {end.Id}, beklenen {file.Id}.");

        if (string.Equals(completed.Sha256, end.Sha256, StringComparison.OrdinalIgnoreCase))
            _received.Add(new ReceivedFile(file.Id, file.Name, completed.StoredPath));
        else
            _failed.Add(file.Id);   // aynı boyut/mtime ama içerik farklı: teslim edilen kopya korunur, başarısız bildirilir
        _tracker.Report(file.Id, file.Name, index, count, file.Size, file.Size, completedBytes + file.Size, total, force: true);
    }

    /// <summary>Sıradaki çerçeve. EOF → <see cref="ConnectionLostException"/>; CANCEL → <see cref="RemoteCancelException"/>.</summary>
    private async Task<Frame> ReceiveAsync(CancellationToken ct)
    {
        var frame = await _channel.ReceiveFrameAsync(ct).ConfigureAwait(false) ?? throw new ConnectionLostException();
        if (frame.Type == FrameType.Cancel)
            throw new RemoteCancelException(TransferCommon.DecodeCancelReason(frame.Body.Span));
        return frame;
    }
}
