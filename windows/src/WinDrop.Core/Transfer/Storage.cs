using System.Text.Json;
using System.Text.Json.Serialization;
using WinDrop.Core.Protocol;

namespace WinDrop.Core.Transfer;

/// <summary>Gönderilecek dosya. Boyut ve mtime nesne oluşturulurken sabitlenir (MANIFEST'e bunlar girer).</summary>
public interface IFileSource
{
    string Name { get; }
    long Size { get; }
    /// <summary>Unix epoch milisaniye (PROTOCOL §6.2).</summary>
    long MtimeMs { get; }
    /// <summary>PROTOCOL §6.7: seçilen klasörün adı + göreli alt yol ('/' ayırıcı); klasör dışı dosyada null.</summary>
    string? Dir => null;
    /// <summary>Dosyayı 0. bayttan okumak üzere açar (gönderen, devamda ilk <c>offset</c> baytı yeniden hash'ler).</summary>
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken);
}

/// <summary>Kısmi dosya anahtarı: <c>(eş kimliği, transferId, dosya id)</c> (PROTOCOL §6.4).</summary>
public readonly record struct PartialKey(string PeerId, TransferId TransferId, int FileId);

/// <summary>Alıcının kısmi dosya deposu. Üretim uygulaması <see cref="FileSystemPartialStore"/>.</summary>
public interface IPartialStore
{
    /// <summary>Hedef birimdeki boş alan (bayt); kabulden önce <c>total ≤ boş − pay</c> denetimi için.</summary>
    long GetFreeSpaceBytes();

    /// <summary>Varsa kısmi dosyayı açar, yoksa (veya bozuksa) null.</summary>
    ValueTask<IPartialFile?> OpenExistingAsync(PartialKey key, CancellationToken cancellationToken);

    /// <summary>Yeni kısmi dosya oluşturur; aynı anahtarla eskisi varsa silinir.</summary>
    ValueTask<IPartialFile> CreateAsync(PartialKey key, long size, long mtimeMs, CancellationToken cancellationToken);

    /// <summary>
    /// Aynı aktarımın önceki oturumunda tamamlanıp hedefe taşınmış dosya (varsa ve dosya hâlâ yerinde ise).
    /// Böylece kopan bir aktarımın devamında biten dosyalar yeniden gönderilip "ad (2)" olarak çoğalmaz.
    /// </summary>
    ValueTask<CompletedFile?> GetCompletedAsync(PartialKey key, CancellationToken cancellationToken);

    /// <summary>Bir aktarımın tüm kısmi dosyalarını (ve tamamlanma kayıtlarını) siler: iptal, politika kesmesi veya aktarım sonu.</summary>
    ValueTask DeleteTransferAsync(string peerId, TransferId transferId, CancellationToken cancellationToken);
}

/// <summary>Önceki oturumda doğrulanıp teslim edilmiş dosya kaydı.</summary>
public sealed record CompletedFile(long Size, long MtimeMs, string Sha256, string StoredPath);

public interface IPartialFile : IAsyncDisposable
{
    /// <summary>Oluşturulurken bildirilen boyut/mtime (MANIFEST ile karşılaştırılır).</summary>
    long Size { get; }
    long MtimeMs { get; }

    /// <summary>Kalıcı olarak diske yazılıp doğrulanmış son ACK ofseti; RESUME ofseti en çok budur.</summary>
    long AckedOffset { get; }

    /// <summary>
    /// <paramref name="offset"/> ≤ <see cref="AckedOffset"/> olmalı. İlk <c>offset</c> baytı <paramref name="consumeExistingBytes"/>'a
    /// verilir (hash'e katılsın), dosya o ofsete kırpılır ve yazma oradan sürer.
    /// </summary>
    ValueTask ResumeAtAsync(long offset, Action<ReadOnlyMemory<byte>> consumeExistingBytes, CancellationToken cancellationToken);

    ValueTask AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Veriyi <b>flush(true)</b> ile diske yazar, sonra <see cref="AckedOffset"/>'i kalıcılaştırır. ACK bundan sonra gönderilir.</summary>
    ValueTask SyncAckAsync(long ackedOffset, CancellationToken cancellationToken);

    /// <summary>Doğrulanan dosyayı hedefe taşır (<paramref name="safeName"/> temizlenmiş ad; çakışmada "ad (2).ext"),
    /// tamamlanma kaydını yazar. Saklanan yolu döndürür. <paramref name="safeDir"/> (§6.7, <see cref="RelativeDirectory.Parse"/> çıktısı)
    /// null değilse dosya indirme klasörü altındaki bu alt klasöre gider; en üst klasör çakışmasında "ad (2)" aktarım başına bir kez çözülür.</summary>
    ValueTask<string> CommitAsync(string safeName, IReadOnlyList<string>? safeDir, string sha256Hex, CancellationToken cancellationToken);

    /// <summary>Kısmi dosyayı ve üst verisini siler.</summary>
    ValueTask DeleteAsync(CancellationToken cancellationToken);
}

/// <summary>Dosya hedefe indikten sonra çağrılır; Windows'ta Mark-of-the-Web (Zone.Identifier) burada eklenir (Platform katmanı).</summary>
public interface IFileCommitHook
{
    /// <summary>
    /// Doğrulanan <c>.part</c> dosyası hedefe taşınmadan ÖNCE çağrılır (dosya kapalı). MOTW'yi burada yazmak, taşımadan
    /// sonra işaretsiz kalan pencereyi kapatır (aynı sürücüde NTFS akışı taşımayla korunur). Varsayılan: hiçbir şey.
    /// </summary>
    ValueTask OnBeforeCommitAsync(string partPath, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Dosya hedefe taşındıktan sonra (farklı sürücüde akış kopyalanmaz; burada yeniden yazılır).</summary>
    ValueTask OnFileCommittedAsync(string fullPath, CancellationToken cancellationToken);
}

/// <summary>System.IO tabanlı <see cref="IFileSource"/>.</summary>
public sealed class FileSystemFileSource : IFileSource
{
    private readonly string _path;

    /// <param name="dir">PROTOCOL §6.7 göreli klasör yolu (null: klasör dışı dosya).</param>
    public FileSystemFileSource(string path, string? dir = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Gönderilecek dosya yok.", path);
        _path = info.FullName;
        Name = info.Name;
        Dir = dir;
        Size = info.Length;
        MtimeMs = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    public string Name { get; }
    public long Size { get; }
    public long MtimeMs { get; }
    public string? Dir { get; }

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
}

/// <summary>
/// System.IO tabanlı kısmi depo. Düzen: <c>{partialRoot}/{peerId}/{transferId}/{fileId}.part</c> + <c>.meta</c>
/// (JSON: size, mtimeMs, ackedOffset). Tamamlananlar <c>finalDirectory</c>'ye taşınır.
/// </summary>
public sealed class FileSystemPartialStore : IPartialStore
{
    private readonly string _partialRoot;
    private readonly string _finalDirectory;
    private readonly Func<long>? _freeSpace;
    private readonly IFileCommitHook? _hook;

    /// <param name="freeSpaceProvider">Test/özel boş alan kaynağı; null ise <see cref="DriveInfo"/> (hedef dizinin sürücüsü).</param>
    public FileSystemPartialStore(string partialRoot, string finalDirectory, IFileCommitHook? hook = null, Func<long>? freeSpaceProvider = null)
    {
        _partialRoot = Path.GetFullPath(partialRoot);
        _finalDirectory = Path.GetFullPath(finalDirectory);
        _hook = hook;
        _freeSpace = freeSpaceProvider;
        Directory.CreateDirectory(_partialRoot);
        Directory.CreateDirectory(_finalDirectory);
    }

    /// <summary>Sürücü kökü → boş bayt (testte değiştirilir).</summary>
    internal Func<string, long> DriveFreeSpace { get; set; } = root => new DriveInfo(root).AvailableFreeSpace;

    /// <summary>Kısmi dosyalar önce <c>partialRoot</c>'a yazılır, sonra hedefe taşınır: iki sürücü farklıysa küçüğü geçerlidir.</summary>
    public long GetFreeSpaceBytes()
    {
        if (_freeSpace is not null) return _freeSpace();
        var finalRoot = Path.GetPathRoot(_finalDirectory);
        var partialRoot = Path.GetPathRoot(_partialRoot);
        return finalRoot is null || partialRoot is null ? 0 : MinFreeSpace(finalRoot, partialRoot, DriveFreeSpace);
    }

    internal static long MinFreeSpace(string finalRoot, string partialRoot, Func<string, long> free)
    {
        var f = free(finalRoot);
        return string.Equals(finalRoot, partialRoot, StringComparison.OrdinalIgnoreCase) ? f : Math.Min(f, free(partialRoot));
    }

    internal string TransferDirectory(string peerId, TransferId id)
    {
        ValidateSegment(peerId);
        return Path.Combine(_partialRoot, peerId, id.ToString());
    }

    public async ValueTask<IPartialFile?> OpenExistingAsync(PartialKey key, CancellationToken cancellationToken)
    {
        var (part, meta) = Paths(key);
        if (!File.Exists(part) || !File.Exists(meta)) return null;
        try
        {
            var m = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(meta, cancellationToken).ConfigureAwait(false),
                PartialJsonContext.Default.PartialMeta);
            var length = new FileInfo(part).Length;
            if (m is null || m.Size < 0 || m.AckedOffset < 0 || m.AckedOffset > m.Size || m.AckedOffset > length)
                return null;   // bozuk üst veri → baştan
            return new FileSystemPartialFile(this, part, meta, DonePath(key), m);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask<IPartialFile> CreateAsync(PartialKey key, long size, long mtimeMs, CancellationToken cancellationToken)
    {
        var (part, meta) = Paths(key);
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        File.Delete(part);
        File.Delete(meta);
        File.Delete(DonePath(key));
        await using (File.Create(part)) { }
        var m = new PartialMeta(size, mtimeMs, 0);
        var file = new FileSystemPartialFile(this, part, meta, DonePath(key), m);
        await file.PersistMetaAsync(0, cancellationToken).ConfigureAwait(false);
        return file;
    }

    public async ValueTask<CompletedFile?> GetCompletedAsync(PartialKey key, CancellationToken cancellationToken)
    {
        var done = DonePath(key);
        if (!File.Exists(done)) return null;
        try
        {
            var c = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(done, cancellationToken).ConfigureAwait(false),
                PartialJsonContext.Default.CompletedFile);
            return c is not null && File.Exists(c.StoredPath) ? c : null;   // kullanıcı dosyayı sildi/taşıdıysa yeniden al
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal string DonePath(PartialKey key) => Path.Combine(TransferDirectory(key.PeerId, key.TransferId), key.FileId + ".done");

    public ValueTask DeleteTransferAsync(string peerId, TransferId transferId, CancellationToken cancellationToken)
    {
        var dir = TransferDirectory(peerId, transferId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Devam edilmeyen eski aktarım dizinlerini (kısmi dosyalar ve tamamlanma kayıtları) siler. Tamamlanma kayıtları
    /// bilerek aktarım bitince silinmez (DONE kaybolursa gönderen aynı transferId ile yeniden dener); temizlik buradan, yaşa göre.
    /// </summary>
    public int PurgeStaleTransfers(TimeSpan maxAge, DateTime? nowUtc = null)
    {
        var cutoff = (nowUtc ?? DateTime.UtcNow) - maxAge;
        var removed = 0;
        foreach (var peerDir in Directory.EnumerateDirectories(_partialRoot))
        {
            foreach (var transferDir in Directory.EnumerateDirectories(peerDir))
            {
                try   // kilitli/erişilemeyen bir dizin döngünün geri kalanını durdurmasın
                {
                    var newest = Directory.EnumerateFiles(transferDir).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(transferDir)).Max();
                    if (newest >= cutoff) continue;
                    Directory.Delete(transferDir, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            try { if (!Directory.EnumerateFileSystemEntries(peerDir).Any()) Directory.Delete(peerDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    private static readonly object RootsLock = new();

    /// <summary>
    /// §6.7: <paramref name="safeDir"/> için hedef dizini oluşturur ve döndürür. En üst klasör çakışmasında "ad (2)" yalnız bir kez
    /// (aktarım başına, <c>roots.json</c> içinde saklanır; devam oturumunda da aynı) çözülür, aynı aktarımın tüm dosyaları aynı köke gider.
    /// Sonuç, <see cref="FinalDirectory"/> altında kalır (<see cref="Path.GetFullPath(string)"/> ile doğrulanır), aksi halde <see cref="ProtocolException"/>.
    /// </summary>
    internal string ResolveTargetDirectory(string transferDir, IReadOnlyList<string> safeDir)
    {
        string resolvedTop;
        lock (RootsLock)
        {
            var mapPath = Path.Combine(transferDir, "roots.json");
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(mapPath))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize(File.ReadAllBytes(mapPath), PartialJsonContext.Default.DictionaryStringString);
                    if (loaded is not null) foreach (var kv in loaded) map[kv.Key] = kv.Value;
                }
                catch (JsonException) { /* bozuk kayıt: yeniden çözülür */ }
            }
            if (!map.TryGetValue(safeDir[0], out resolvedTop!))
            {
                resolvedTop = FileNameSanitizer.MakeUnique(safeDir[0], new DirectoryNameExistence(_finalDirectory));
                Directory.CreateDirectory(Path.Combine(_finalDirectory, resolvedTop));   // başka aktarım aynı adı almasın
                map[safeDir[0]] = resolvedTop;
                Directory.CreateDirectory(transferDir);
                var tmp = mapPath + ".tmp";
                File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(map, PartialJsonContext.Default.DictionaryStringString));
                File.Move(tmp, mapPath, overwrite: true);
            }
        }

        var parts = new List<string>(safeDir.Count) { resolvedTop };
        for (var i = 1; i < safeDir.Count; i++) parts.Add(safeDir[i]);
        var target = Path.GetFullPath(Path.Combine(_finalDirectory, Path.Combine(parts.ToArray())));
        var rootPrefix = Path.TrimEndingDirectorySeparator(_finalDirectory) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ProtocolException("dir hedef klasörün dışına çıkıyor.");
        Directory.CreateDirectory(target);
        return target;
    }

    internal sealed class DirectoryNameExistence(string directory) : IFileNameExistence
    {
        public bool Exists(string fileName)
        {
            var p = Path.Combine(directory, fileName);
            return File.Exists(p) || Directory.Exists(p);
        }
    }

    internal string FinalDirectory => _finalDirectory;
    internal IFileCommitHook? Hook => _hook;

    private (string Part, string Meta) Paths(PartialKey key)
    {
        var dir = TransferDirectory(key.PeerId, key.TransferId);
        return (Path.Combine(dir, key.FileId + ".part"), Path.Combine(dir, key.FileId + ".meta"));
    }

    private static void ValidateSegment(string segment)
    {
        // Eş kimliği onaltılık dize beklenir; yol ayırıcı/üst dizin kaçışına izin verme.
        if (string.IsNullOrEmpty(segment) || segment.IndexOfAny(['/', '\\', ':']) >= 0 || segment is "." or "..")
            throw new ArgumentException("Geçersiz eş kimliği.", nameof(segment));
    }
}

internal sealed record PartialMeta(long Size, long MtimeMs, long AckedOffset);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PartialMeta))]
[JsonSerializable(typeof(CompletedFile))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class PartialJsonContext : JsonSerializerContext;

internal sealed class FileSystemPartialFile : IPartialFile
{
    private readonly FileSystemPartialStore _store;
    private readonly string _partPath;
    private readonly string _metaPath;
    private FileStream? _stream;
    private long _acked;

    private readonly string _donePath;

    public FileSystemPartialFile(FileSystemPartialStore store, string partPath, string metaPath, string donePath, PartialMeta meta)
    {
        _store = store;
        _donePath = donePath;
        _partPath = partPath;
        _metaPath = metaPath;
        Size = meta.Size;
        MtimeMs = meta.MtimeMs;
        _acked = meta.AckedOffset;
    }

    public long Size { get; }
    public long MtimeMs { get; }
    public long AckedOffset => _acked;

    private FileStream Stream => _stream ??= new FileStream(_partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
        FileShare.None, 81920, FileOptions.Asynchronous);

    public async ValueTask ResumeAtAsync(long offset, Action<ReadOnlyMemory<byte>> consumeExistingBytes, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset > _acked)
            throw new ArgumentOutOfRangeException(nameof(offset), "RESUME ofseti son ACK'lenen ofseti aşamaz.");
        var s = Stream;
        if (s.Length < offset) throw new IOException("Kısmi dosya ACK'lenen ofsetten kısa.");

        s.Position = 0;
        var buffer = new byte[81920];
        for (var remaining = offset; remaining > 0;)
        {
            var n = await s.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (n == 0) throw new IOException("Kısmi dosya beklenenden kısa.");
            consumeExistingBytes(buffer.AsMemory(0, n));
            remaining -= n;
        }
        s.SetLength(offset);
        s.Position = offset;
        if (offset < _acked) await PersistMetaAsync(offset, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        Stream.WriteAsync(data, cancellationToken);

    public async ValueTask SyncAckAsync(long ackedOffset, CancellationToken cancellationToken)
    {
        var s = Stream;
        await s.FlushAsync(cancellationToken).ConfigureAwait(false);
        s.Flush(flushToDisk: true);               // fsync: veri diskte olmadan ACK'lenmez
        await PersistMetaAsync(ackedOffset, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask PersistMetaAsync(long ackedOffset, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new PartialMeta(Size, MtimeMs, ackedOffset), PartialJsonContext.Default.PartialMeta);
        var tmp = _metaPath + ".tmp";
        await using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await f.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            await f.FlushAsync(cancellationToken).ConfigureAwait(false);
            f.Flush(flushToDisk: true);
        }
        File.Move(tmp, _metaPath, overwrite: true);
        _acked = ackedOffset;
    }

    public async ValueTask<string> CommitAsync(string safeName, IReadOnlyList<string>? safeDir, string sha256Hex, CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            _stream.Flush(flushToDisk: true);
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
        if (_store.Hook is { } before) await before.OnBeforeCommitAsync(_partPath, cancellationToken).ConfigureAwait(false);
        var targetDir = safeDir is { Count: > 0 }
            ? _store.ResolveTargetDirectory(Path.GetDirectoryName(_partPath)!, safeDir)
            : _store.FinalDirectory;
        var existence = new FileSystemPartialStore.DirectoryNameExistence(targetDir);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var name = FileNameSanitizer.MakeUnique(safeName, existence);
            var dest = Path.Combine(targetDir, name);
            try
            {
                File.Move(_partPath, dest, overwrite: false);
            }
            catch (IOException) when (File.Exists(dest))
            {
                continue;   // aynı anda başka biri aldı: sıradaki adı dene
            }
            TryDelete(_metaPath);
            // Tamamlanma kaydı: devam eden oturumda bu dosya yeniden gönderilmesin.
            var record = new CompletedFile(Size, MtimeMs, sha256Hex, dest);
            await File.WriteAllBytesAsync(_donePath,
                JsonSerializer.SerializeToUtf8Bytes(record, PartialJsonContext.Default.CompletedFile), cancellationToken).ConfigureAwait(false);
            if (_store.Hook is { } hook) await hook.OnFileCommittedAsync(dest, cancellationToken).ConfigureAwait(false);
            return dest;
        }
        throw new IOException("Hedef klasörde benzersiz ad bulunamadı.");
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        await CloseAsync().ConfigureAwait(false);
        TryDelete(_partPath);
        TryDelete(_metaPath);
        TryDeleteEmptyDirectory(Path.GetDirectoryName(_partPath)!);
    }

    private async ValueTask CloseAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteEmptyDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
