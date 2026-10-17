using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;

namespace WinDrop.Core.Transfer;

/// <summary>
/// PROTOCOL §5 gönderen: MANIFEST → (alıcıdan RESUME → FILE_START → DATA… → FILE_END) her dosya için → DONE bekler.
/// Devamda ilk <c>offset</c> baytı yeniden okunup hash'e katılır (SHA-256 her zaman 0. bayttan). Arka planda bir okuyucu
/// ACK/RESUME/CANCEL/DONE çerçevelerini alır; böylece uzak iptal gönderim sürerken fark edilir.
/// Tek kullanımlıktır (bir oturum, bir <see cref="RunAsync"/>).
/// </summary>
public sealed class FileSender
{
    private readonly SecureChannel _channel;
    private readonly IReadOnlyList<IFileSource> _files;
    private readonly ProgressTracker _tracker;
    private int _used;

    public FileSender(SecureChannel channel, IReadOnlyList<IFileSource> files, IProgress<TransferProgress>? progress = null, TimeSpan? progressInterval = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) throw new ArgumentException("Gönderilecek dosya yok.", nameof(files));
        if (files.Count > TransferLimits.MaxFiles)
            throw new ArgumentException($"En çok {TransferLimits.MaxFiles} dosya gönderilebilir.", nameof(files));
        _channel = channel;
        _files = files;
        _tracker = new ProgressTracker(progress, progressInterval);
    }

    /// <summary>Yerel iptal (<paramref name="cancellationToken"/>) CANCEL gönderip <see cref="TransferOutcome.Canceled"/> döndürür.
    /// Bağlantı kopması <see cref="ConnectionLostException"/>/<see cref="IOException"/>, protokol ihlali <see cref="ProtocolException"/> fırlatır.</summary>
    public async Task<TransferResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0) throw new InvalidOperationException("FileSender tek kullanımlıktır.");

        await _channel.EnsureEstablishedAsync(cancellationToken).ConfigureAwait(false);   // yanıtlayan isek: §1 0-RTT yok

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inbox = new Inbox();
        var reader = Task.Run(() => ReadLoopAsync(inbox, cts), CancellationToken.None);
        var resumeOffsets = new Dictionary<int, long>();
        try
        {
            return await SendAllAsync(inbox, resumeOffsets, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (inbox.Fault is { } fault) ExceptionDispatchInfo.Throw(fault);
            if (inbox.RemoteCanceled)
                return new TransferResult(TransferOutcome.Canceled, [])
                    { CanceledByRemote = true, Error = inbox.RemoteReason, ResumeOffsets = resumeOffsets };
            if (cancellationToken.IsCancellationRequested)
            {
                await TransferCommon.TrySendCancelAsync(_channel, null).ConfigureAwait(false);
                return new TransferResult(TransferOutcome.Canceled, []) { ResumeOffsets = resumeOffsets };
            }
            throw;
        }
        finally
        {
            cts.Cancel();
            try { await reader.ConfigureAwait(false); } catch { /* okuyucu hatası Inbox.Fault'ta */ }
        }
    }

    private async Task<TransferResult> SendAllAsync(Inbox inbox, Dictionary<int, long> resumeOffsets, CancellationToken token)
    {
        var chunk = Math.Min(TransferLimits.DefaultChunkSize, _channel.MaxFrameBodySize);
        // PROTOCOL §9a MANIFEST sayfalama: her çerçevenin JSON gövdesi ≤ limit; son sayfa more=false.
        const int envelope = 32;   // {"files":[],"more":true} + pay
        var limit = Math.Min(FrameCodec.MaxBody, _channel.MaxFrameBodySize);
        var page = new List<ManifestFile>();
        var pageSize = envelope;
        for (var i = 0; i < _files.Count; i++)
        {
            var f = _files[i];
            var mf = new ManifestFile(i, f.Name, f.Size, f.MtimeMs);
            var size = FrameCodec.MeasureManifestFile(mf) + 1;   // + virgül
            if (page.Count > 0 && pageSize + size > limit)
            {
                await _channel.SendFrameAsync(FrameCodec.Encode(new ManifestBody(page, true)), token).ConfigureAwait(false);
                page = new List<ManifestFile>();
                pageSize = envelope;
            }
            page.Add(mf);
            pageSize += size;
        }
        await _channel.SendFrameAsync(FrameCodec.Encode(new ManifestBody(page, false)), token).ConfigureAwait(false);

        var total = _files.Sum(f => f.Size);
        long completedBytes = 0;
        var buffer = new byte[1 + chunk];
        buffer[0] = (byte)FrameType.Data;

        for (var i = 0; i < _files.Count; i++)
        {
            var file = _files[i];
            var resume = await inbox.Resumes.Reader.ReadAsync(token).ConfigureAwait(false);
            if (resume.Id != i) throw new ProtocolException($"RESUME id {resume.Id}, beklenen {i}.");
            if (resume.Offset < 0 || resume.Offset > file.Size)
                throw new ProtocolException($"RESUME ofseti geçersiz: {resume.Offset} (boyut {file.Size}).");
            resumeOffsets[i] = resume.Offset;

            await _channel.SendFrameAsync(FrameCodec.Encode(new FileStartBody(i, resume.Offset)), token).ConfigureAwait(false);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var stream = await file.OpenReadAsync(token).ConfigureAwait(false);

            // Devam: ilk offset baytı gönderilmez ama hash'e katılır.
            long position = 0;
            while (position < resume.Offset)
            {
                var n = await ReadAsync(stream, buffer, (int)Math.Min(chunk, resume.Offset - position), token).ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(1, n));
                position += n;
            }

            _tracker.Report(i, file.Name, i, _files.Count, position, file.Size, completedBytes + position, total, force: true);
            while (position < file.Size)
            {
                var n = await ReadAsync(stream, buffer, (int)Math.Min(chunk, file.Size - position), token).ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(1, n));
                await _channel.SendFrameAsync(buffer.AsMemory(0, 1 + n), token).ConfigureAwait(false);
                position += n;
                _tracker.AddTransferred(n);
                _tracker.Report(i, file.Name, i, _files.Count, position, file.Size, completedBytes + position, total, force: false);
            }

            var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            await _channel.SendFrameAsync(FrameCodec.Encode(new FileEndBody(i, sha)), token).ConfigureAwait(false);
            completedBytes += file.Size;
            _tracker.Report(i, file.Name, i, _files.Count, file.Size, file.Size, completedBytes, total, force: true);
        }

        var done = await inbox.Done.Task.WaitAsync(token).ConfigureAwait(false);
        var outcome = done.Ok && done.Failed.Count == 0 ? TransferOutcome.Completed : TransferOutcome.CompletedWithFailures;
        return new TransferResult(outcome, done.Failed) { ResumeOffsets = resumeOffsets };
    }

    private static async ValueTask<int> ReadAsync(Stream stream, byte[] buffer, int want, CancellationToken token)
    {
        var n = await stream.ReadAsync(buffer.AsMemory(1, want), token).ConfigureAwait(false);
        if (n == 0) throw new IOException("Kaynak dosya bildirilen boyuttan kısa (değişmiş olabilir).");
        return n;
    }

    private async Task ReadLoopAsync(Inbox inbox, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            while (true)
            {
                var frame = await _channel.ReceiveFrameAsync(token).ConfigureAwait(false);
                if (frame is null)
                {
                    inbox.Fault = new ConnectionLostException();
                    cts.Cancel();
                    return;
                }
                switch (frame.Value.Type)
                {
                    case FrameType.Resume:
                        inbox.Resumes.Writer.TryWrite(FrameCodec.DecodeResume(frame.Value.Body.Span));
                        break;
                    case FrameType.Ack:
                        FrameCodec.DecodeAck(frame.Value.Body.Span);   // ACK gönderen için bilgi amaçlı; geçerliliği denetlenir
                        break;
                    case FrameType.Cancel:
                        inbox.RemoteReason = TransferCommon.DecodeCancelReason(frame.Value.Body.Span);
                        inbox.RemoteCanceled = true;
                        cts.Cancel();
                        return;
                    case FrameType.Done:
                        inbox.Done.TrySetResult(FrameCodec.DecodeDone(frame.Value.Body.Span));
                        return;
                    default:
                        inbox.Fault = new ProtocolException($"Gönderen {frame.Value.Type} çerçevesi beklemiyor.");
                        cts.Cancel();
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal sonlanma
        }
        catch (Exception ex)
        {
            inbox.Fault = ex;
            cts.Cancel();
        }
    }

    private sealed class Inbox
    {
        public Channel<ResumeBody> Resumes { get; } = Channel.CreateUnbounded<ResumeBody>();
        public TaskCompletionSource<DoneBody> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile Exception? Fault;
        public volatile bool RemoteCanceled;
        public volatile string? RemoteReason;
    }
}

internal static class TransferCommon
{
    public static string? DecodeCancelReason(ReadOnlySpan<byte> body)
    {
        try { return FrameCodec.DecodeCancel(body).Reason; }
        catch (ProtocolException) { return null; }   // CANCEL gövdesi bozuksa yine iptal sayılır
    }

    /// <summary>En iyi çaba CANCEL: hata ve zaman aşımı (2 sn) yutulur.</summary>
    public static async Task TrySendCancelAsync(SecureChannel channel, string? reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await channel.SendFrameAsync(FrameCodec.Encode(new CancelBody(reason)), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ProtocolException or InvalidOperationException or ObjectDisposedException)
        {
            // karşı taraf zaten gitmiş olabilir
        }
    }
}
