using System.Security.Cryptography;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Core.Tests.Support;
using WinDrop.Core.Transfer;
using Xunit;

namespace WinDrop.Core.Tests.Transfer;

/// <summary>Aktarım motoru: Noise kanalı üstünde bellek içi akışla, gerçek System.IO deposu ve geçici dizinlerle.</summary>
public sealed class TransferTests : IDisposable
{
    private readonly Rig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ---------------------------------------------------------------- yardımcılar

    private static OfferInfo OfferOf(IReadOnlyList<IFileSource> files) => new(files.Count, files.Sum(f => f.Size));

    private ReceiverOptions Options(IReadOnlyList<IFileSource> files, string peerId, int ackInterval = TransferLimits.DefaultAckIntervalBytes) =>
        new(peerId, _rig.TransferId, OfferOf(files)) { AckIntervalBytes = ackInterval, SpaceMarginBytes = 0 };

    private string WriteFile(string name, byte[] data, string dir = "src")
    {
        var path = Path.Combine(_rig.Dir(dir), name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string[] FinalFiles(string dir) =>
        Directory.EnumerateFiles(dir).Select(Path.GetFileName).Order().ToArray()!;

    private string PartialDir(string storeName) => Path.Combine(_rig.Root, storeName + "-partial");

    private static bool AnyPartFiles(string partialRoot) =>
        Directory.Exists(partialRoot) && Directory.EnumerateFiles(partialRoot, "*.part", SearchOption.AllDirectories).Any();

    // ---------------------------------------------------------------- tam akış

    [Fact]
    public async Task MultiFile_PhoneToPc_AllFilesArriveIntact()
    {
        var datas = new Dictionary<string, byte[]>
        {
            ["a.txt"] = "merhaba"u8.ToArray(),
            ["empty.bin"] = [],
            ["one.bin"] = [42],
            ["büyük dosya ✓.bin"] = TestData.Random(300_000, 7),
            ["exactly64000.bin"] = TestData.Random(64_000, 8),
        };
        var files = datas.Select(kv => (IFileSource)new FileSystemFileSource(WriteFile(kv.Key, kv.Value))).ToList();

        using var link = await _rig.ConnectAsync(maxChunk: 1000);   // TCP parçalanmasıyla
        var store = _rig.NewStore("pc");
        var sender = new FileSender(link.Phone, files);
        var receiver = new FileReceiver(link.Pc, store, Options(files, _rig.PhoneId));

        var results = await Task.WhenAll(sender.RunAsync(), receiver.RunAsync());

        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
        Assert.All(results[1].ResumeOffsets.Values, o => Assert.Equal(0, o));
        Assert.Equal(datas.Keys.Order().ToArray(), FinalFiles(Path.Combine(_rig.Root, "pc-final")));
        foreach (var (name, data) in datas)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(_rig.Root, "pc-final", name)));
        Assert.False(AnyPartFiles(PartialDir("pc")));
        Assert.Equal(5, results[1].ReceivedFiles.Count);
    }

    [Fact]
    public async Task MultiFile_PcToPhone_ResponderCanSend()
    {
        var files = new List<IFileSource>
        {
            new MemoryFileSource("x.bin", TestData.Random(100_000, 1)),
            new MemoryFileSource("y.bin", TestData.Random(5, 2)),
        };
        using var link = await _rig.ConnectAsync();
        var store = _rig.NewStore("phone");
        var sender = new FileSender(link.Pc, files);                       // yanıtlayan gönderir: önce EnsureEstablished
        var receiver = new FileReceiver(link.Phone, store, Options(files, _rig.PcId));

        var results = await Task.WhenAll(sender.RunAsync(), receiver.RunAsync());

        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
        Assert.True(link.Pc.IsEstablished);
        Assert.Equal(TestData.Random(100_000, 1), File.ReadAllBytes(Path.Combine(_rig.Root, "phone-final", "x.bin")));
    }

    [Fact]
    public async Task TwentyMegabytes_ProgressEventsAreMonotonic_AndHashMatches()
    {
        var data = TestData.Random(20 * 1024 * 1024, 99);
        var src = WriteFile("big.bin", data);
        var files = new List<IFileSource> { new FileSystemFileSource(src) };

        var sendEvents = new SyncProgress(_ => { });
        var recvEvents = new SyncProgress(_ => { });
        using var link = await _rig.ConnectAsync();
        var store = _rig.NewStore("pc");
        var sender = new FileSender(link.Phone, files, sendEvents);
        var receiver = new FileReceiver(link.Pc, store, Options(files, _rig.PhoneId), recvEvents);

        var results = await Task.WhenAll(sender.RunAsync(), receiver.RunAsync());

        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
        var dest = Path.Combine(_rig.Root, "pc-final", "big.bin");
        Assert.Equal(TestData.Sha(data), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dest))));

        foreach (var events in new[] { sendEvents.Events, recvEvents.Events })
        {
            Assert.True(events.Count >= 2);
            long last = -1;
            foreach (var e in events)
            {
                Assert.True(e.BytesDone >= last, "BytesDone azalmamalı");
                Assert.True(e.BytesPerSecond >= 0);
                Assert.Equal(data.Length, e.TotalBytes);
                last = e.BytesDone;
            }
            Assert.Equal(data.Length, events[^1].BytesDone);
        }
        Assert.Contains(recvEvents.Events, e => e.BytesPerSecond > 0);   // hız ölçülüyor
    }

    // ---------------------------------------------------------------- devam ettirme

    private async Task<(IFileSource[] Files, byte[] Small, byte[] Big)> DropMidTransferAsync()
    {
        var small = TestData.Random(100_000, 3);
        var big = TestData.Random(12 * 1024 * 1024, 4);
        var files = new IFileSource[]
        {
            new FileSystemFileSource(WriteFile("small.bin", small)),
            new FileSystemFileSource(WriteFile("big.bin", big)),
        };

        using var link = await _rig.ConnectAsync(droppable: true);
        var store = _rig.NewStore("pc");
        var sender = new FileSender(link.Phone, files);
        var receiver = new FileReceiver(link.Pc, store, Options(files, _rig.PhoneId));

        link.Dropper!.Arm(100);   // MANIFEST(1) + small(4) + big: START(1) + 94 DATA (6 016 000 B) → tek ACK, 66. çerçevede
        var s = sender.RunAsync();
        var r = receiver.RunAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => s);
        await Assert.ThrowsAsync<ConnectionLostException>(() => r);
        return (files, small, big);
    }

    [Fact]
    public async Task ConnectionDropMidFile_NewSessionResumesFromAckedOffset_WithCorrectSha()
    {
        var (files, small, big) = await DropMidTransferAsync();

        // Oturum 1 sonrası: small teslim edildi, big için ACK'lenmiş kısmi dosya var (4 MB'ın katları civarı).
        Assert.Equal(["small.bin"], FinalFiles(Path.Combine(_rig.Root, "pc-final")));
        var key = new PartialKey(_rig.PhoneId, _rig.TransferId, 1);
        long acked;
        await using (var partial = await _rig.NewStore("pc").OpenExistingAsync(key, default))
        {
            Assert.NotNull(partial);
            acked = partial!.AckedOffset;
        }
        Assert.Equal(66 * 64_000, acked);   // 4 MiB eşiğini ilk aşan DATA sınırı (65 çerçeve 4 160 000 < 4 194 304)

        // Oturum 2: yeni el sıkışma, aynı transferId ve eş.
        using var link2 = await _rig.ConnectAsync();
        var store2 = _rig.NewStore("pc");
        var sender = new FileSender(link2.Phone, files);
        var receiver = new FileReceiver(link2.Pc, store2, Options(files, _rig.PhoneId));
        var results = await Task.WhenAll(sender.RunAsync(), receiver.RunAsync());

        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
        Assert.Equal(small.Length, results[1].ResumeOffsets[0]);     // biten dosya yeniden gönderilmedi
        Assert.Equal(acked, results[1].ResumeOffsets[1]);            // büyük dosya ACK'lenen ofsetten sürdü
        Assert.Equal(acked, results[0].ResumeOffsets[1]);            // gönderen de aynı ofseti aldı
        Assert.Equal(["big.bin", "small.bin"], FinalFiles(Path.Combine(_rig.Root, "pc-final")));   // "(2)" kopyası yok
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(_rig.Root, "pc-final", "big.bin")));
        Assert.Equal(small, File.ReadAllBytes(Path.Combine(_rig.Root, "pc-final", "small.bin")));
        Assert.False(AnyPartFiles(PartialDir("pc")));
    }

    [Theory]
    [InlineData("size")]
    [InlineData("mtime")]
    public async Task SourceChangedBetweenSessions_RestartsFromZero(string change)
    {
        var (_, _, _) = await DropMidTransferAsync();

        byte[] expected;
        var bigPath = Path.Combine(_rig.Root, "src", "big.bin");
        if (change == "size")
        {
            expected = TestData.Random(12 * 1024 * 1024 + 123, 5);
            File.WriteAllBytes(bigPath, expected);
        }
        else
        {
            expected = File.ReadAllBytes(bigPath);
            File.SetLastWriteTimeUtc(bigPath, File.GetLastWriteTimeUtc(bigPath).AddMinutes(7));   // aynı içerik/boyut, farklı mtime
        }
        var files = new IFileSource[]
        {
            new FileSystemFileSource(Path.Combine(_rig.Root, "src", "small.bin")),
            new FileSystemFileSource(bigPath),
        };

        using var link2 = await _rig.ConnectAsync();
        var store2 = _rig.NewStore("pc");
        var sender = new FileSender(link2.Phone, files);
        var receiver = new FileReceiver(link2.Pc, store2, Options(files, _rig.PhoneId));
        var results = await Task.WhenAll(sender.RunAsync(), receiver.RunAsync());

        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
        Assert.Equal(0, results[1].ResumeOffsets[1]);                 // baştan
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(_rig.Root, "pc-final", "big.bin")));
    }

    [Fact]
    public async Task CorruptedPartialFile_IsDetectedBySha_DeletedAndReportedFailed()
    {
        var (files, small, _) = await DropMidTransferAsync();

        var part = Directory.EnumerateFiles(PartialDir("pc"), "1.part", SearchOption.AllDirectories).Single();
        using (var fs = new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.Position = 1000;                       // ACK'lenen aralığın içinde bir bayt boz
            fs.WriteByte((byte)(fs.ReadByte() ^ 0xFF));
        }

        using var link2 = await _rig.ConnectAsync();
        var sender = new FileSender(link2.Phone, files);
        var receiver = new FileReceiver(link2.Pc, _rig.NewStore("pc"), Options(files, _rig.PhoneId));
        var results = await Task.WhenAll(sender.RunAsync(), receiver.RunAsync());

        Assert.All(results, r => Assert.Equal(TransferOutcome.CompletedWithFailures, r.Outcome));
        Assert.Equal([1], results[0].FailedFileIds);              // gönderen DONE'dan öğrendi
        Assert.Equal(["small.bin"], FinalFiles(Path.Combine(_rig.Root, "pc-final")));
        Assert.False(AnyPartFiles(PartialDir("pc")));
        _ = small;
    }

    // ---------------------------------------------------------------- elle yazılmış uç (bozuk/kötü niyetli gönderen)

    private sealed class Manual(SecureChannel ch)
    {
        public SecureChannel Channel { get; } = ch;
        public Task Send(byte[] frame) => Channel.SendFrameAsync(frame).AsTask();
        public async Task<Frame> Recv() => await Channel.ReceiveFrameAsync() ?? throw new InvalidOperationException("EOF");

        public Task SendManifest(params (string Name, long Size)[] files) =>
            Send(FrameCodec.Encode(new ManifestBody(files.Select((f, i) => new ManifestFile(i, f.Name, f.Size, 1)).ToList())));

        /// <summary>RESUME bekler, FILE_START yollar.</summary>
        public async Task<long> Start(int id)
        {
            var resume = FrameCodec.DecodeResume((await Recv()).Body.Span);
            Assert.Equal(id, resume.Id);
            await Send(FrameCodec.Encode(new FileStartBody(id, resume.Offset)));
            return resume.Offset;
        }
    }

    [Fact]
    public async Task ShaMismatch_Injected_FileDeletedAndInDoneFailed_OthersStillArrive()
    {
        using var link = await _rig.ConnectAsync();
        var store = _rig.NewStore("pc");
        var good = TestData.Random(1000, 11);
        var offer = new OfferInfo(2, 2000);
        var receiver = new FileReceiver(link.Pc, store, new ReceiverOptions(_rig.PhoneId, _rig.TransferId, offer) { SpaceMarginBytes = 0 });
        var run = receiver.RunAsync();

        var m = new Manual(link.Phone);
        await m.SendManifest(("bad.bin", 1000), ("good.bin", 1000));
        await m.Start(0);
        await m.Send(FrameCodec.EncodeData(TestData.Random(1000, 10)));
        await m.Send(FrameCodec.Encode(new FileEndBody(0, new string('0', 64))));      // yanlış SHA
        await m.Start(1);
        await m.Send(FrameCodec.EncodeData(good));
        await m.Send(FrameCodec.Encode(new FileEndBody(1, TestData.Sha(good))));
        var done = FrameCodec.DecodeDone((await m.Recv()).Body.Span);

        var result = await run;
        Assert.False(done.Ok);
        Assert.Equal([0], done.Failed);
        Assert.Equal(TransferOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal(["good.bin"], FinalFiles(Path.Combine(_rig.Root, "pc-final")));
        Assert.False(AnyPartFiles(PartialDir("pc")));
    }

    [Fact]
    public async Task SizeExceeded_AbortsTransfer_SendsCancel_KeepsNothing()
    {
        using var link = await _rig.ConnectAsync();
        var store = _rig.NewStore("pc");
        var receiver = new FileReceiver(link.Pc, store,
            new ReceiverOptions(_rig.PhoneId, _rig.TransferId, new OfferInfo(1, 100)) { SpaceMarginBytes = 0 });
        var run = receiver.RunAsync();

        var m = new Manual(link.Phone);
        await m.SendManifest(("a.bin", 100));
        await m.Start(0);
        await m.Send(FrameCodec.EncodeData(new byte[60]));
        await m.Send(FrameCodec.EncodeData(new byte[41]));                             // 101 > 100

        var cancel = await m.Recv();
        Assert.Equal(FrameType.Cancel, cancel.Type);
        Assert.Contains("exceeds", FrameCodec.DecodeCancel(cancel.Body.Span).Reason);
        var result = await run;
        Assert.Equal(TransferOutcome.Aborted, result.Outcome);
        Assert.Empty(FinalFiles(Path.Combine(_rig.Root, "pc-final")));
        Assert.False(AnyPartFiles(PartialDir("pc")));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("total")]
    [InlineData("too-many")]
    [InlineData("long-name")]
    [InlineData("space")]
    public async Task ManifestLimits_AreEnforced_ReceiverCancelsWithReason(string violation)
    {
        using var link = await _rig.ConnectAsync();
        var store = _rig.NewStore("pc", free: () => violation == "space" ? 1_000 : long.MaxValue / 2);
        var size = violation == "space" ? 600 : 20;
        var options = violation switch
        {
            "count" => new ReceiverOptions(_rig.PhoneId, _rig.TransferId, new OfferInfo(2, 20)),
            "total" => new ReceiverOptions(_rig.PhoneId, _rig.TransferId, new OfferInfo(1, 21)),
            "too-many" => new ReceiverOptions(_rig.PhoneId, _rig.TransferId, new OfferInfo(3, 30)) { MaxFiles = 2 },
            _ => new ReceiverOptions(_rig.PhoneId, _rig.TransferId, new OfferInfo(1, size)),
        };
        options = options with { SpaceMarginBytes = violation == "space" ? 500 : 0 };
        var run = new FileReceiver(link.Pc, store, options).RunAsync();

        var m = new Manual(link.Phone);
        switch (violation)
        {
            case "count" or "total" or "space": await m.SendManifest(("a", size)); break;
            case "too-many": await m.SendManifest(("a", 10), ("b", 10), ("c", 10)); break;
            case "long-name": await m.SendManifest((new string('x', 256), 20)); break;
        }

        var cancel = await m.Recv();
        Assert.Equal(FrameType.Cancel, cancel.Type);
        var result = await run;
        Assert.Equal(TransferOutcome.Aborted, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Equal(result.Error, FrameCodec.DecodeCancel(cancel.Body.Span).Reason);
    }

    [Fact]
    public async Task DiskSpace_ExactBoundary_IsAccepted()
    {
        var files = new List<IFileSource> { new MemoryFileSource("a.bin", TestData.Random(1000)) };
        using var link = await _rig.ConnectAsync();
        var store = _rig.NewStore("pc", free: () => 1000 + 64);
        var options = new ReceiverOptions(_rig.PhoneId, _rig.TransferId, OfferOf(files)) { SpaceMarginBytes = 64 };   // total == boş − pay
        var results = await Task.WhenAll(new FileSender(link.Phone, files).RunAsync(), new FileReceiver(link.Pc, store, options).RunAsync());
        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));

        var files2 = new List<IFileSource> { new MemoryFileSource("b.bin", TestData.Random(1000)) };
        using var link2 = await _rig.ConnectAsync();
        var store2 = _rig.NewStore("pc2", free: () => 1000 + 63);
        var options2 = new ReceiverOptions(_rig.PhoneId, _rig.TransferId, OfferOf(files2)) { SpaceMarginBytes = 64 };   // 1 bayt eksik
        var results2 = await Task.WhenAll(new FileSender(link2.Phone, files2).RunAsync(), new FileReceiver(link2.Pc, store2, options2).RunAsync());
        Assert.Equal(TransferOutcome.Canceled, results2[0].Outcome);       // gönderen: uzak iptal
        Assert.True(results2[0].CanceledByRemote);
        Assert.Contains("insufficient", results2[0].Error);
        Assert.Equal(TransferOutcome.Aborted, results2[1].Outcome);
    }

    [Fact]
    public async Task DuplicateFileIds_AreRejected()
    {
        using var link = await _rig.ConnectAsync();
        var run = new FileReceiver(link.Pc, _rig.NewStore("pc"),
            new ReceiverOptions(_rig.PhoneId, _rig.TransferId, new OfferInfo(2, 20)) { SpaceMarginBytes = 0 }).RunAsync();
        var m = new Manual(link.Phone);
        await m.Send(FrameCodec.Encode(new ManifestBody([new ManifestFile(1, "a", 10, 1), new ManifestFile(1, "b", 10, 1)])));
        Assert.Equal(FrameType.Cancel, (await m.Recv()).Type);
        Assert.Contains("duplicate", (await run).Error);
    }

    // ---------------------------------------------------------------- MANIFEST sayfalama

    [Fact]
    public async Task Manifest_10000LongNamedFiles_PagedAcrossFrames_Succeeds()
    {
        var files = new List<IFileSource>();
        for (var i = 0; i < 10_000; i++)
            files.Add(new MemoryFileSource($"{i:D5}-" + new string('n', 240), []));
        using var link = await _rig.ConnectAsync();
        var store = _rig.NewStore("pc");
        var options = new ReceiverOptions(_rig.PhoneId, _rig.TransferId, OfferOf(files)) { SpaceMarginBytes = 0 };
        var results = await Task.WhenAll(new FileSender(link.Phone, files).RunAsync(), new FileReceiver(link.Pc, store, options).RunAsync());
        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
    }

    [Fact]
    public async Task Manifest_10001Files_AcrossPages_IsCutOff()
    {
        using var link = await _rig.ConnectAsync();
        var run = new FileReceiver(link.Pc, _rig.NewStore("pc"),
            new ReceiverOptions(_rig.PhoneId, _rig.TransferId, new OfferInfo(10_001, 0)) { SpaceMarginBytes = 0 }).RunAsync();
        var m = new Manual(link.Phone);
        for (var start = 0; start < 10_001; start += 200)
        {
            var page = Enumerable.Range(start, Math.Min(200, 10_001 - start))
                .Select(i => new ManifestFile(i, $"{i:D5}-" + new string('n', 240), 0, 1)).ToList();
            await m.Send(FrameCodec.Encode(new ManifestBody(page, start + 200 < 10_001)));
        }
        Assert.Equal(FrameType.Cancel, (await m.Recv()).Type);
        var result = await run;
        Assert.Equal(TransferOutcome.Aborted, result.Outcome);
        Assert.Contains("too many", result.Error);
    }

    [Fact]
    public void Manifest_SinglePage_WithoutMoreField_IsBackwardCompatible()
    {
        var body = FrameCodec.DecodeManifest("""{"files":[{"id":0,"name":"a","size":1,"mtime":2}]}"""u8);
        Assert.False(body.More);
        Assert.Single(body.Files);
        Assert.DoesNotContain("more", System.Text.Encoding.UTF8.GetString(FrameCodec.Encode(body)));
    }

    [Fact]
    public async Task Manifest_MoreTrueThenNonManifest_IsProtocolError()
    {
        using var link = await _rig.ConnectAsync();
        var run = new FileReceiver(link.Pc, _rig.NewStore("pc"),
            new ReceiverOptions(_rig.PhoneId, _rig.TransferId, new OfferInfo(2, 20)) { SpaceMarginBytes = 0 }).RunAsync();
        var m = new Manual(link.Phone);
        await m.Send(FrameCodec.Encode(new ManifestBody([new ManifestFile(0, "a", 10, 1)], true)));
        await m.Send(FrameCodec.EncodeData(new byte[5]));
        await Assert.ThrowsAsync<ProtocolException>(() => run);
        Assert.Equal(FrameType.Cancel, (await m.Recv()).Type);
    }

    [Fact]
    public async Task ProtocolViolation_DataBeforeFileStart_ThrowsAndCancels()
    {
        using var link = await _rig.ConnectAsync();
        var run = new FileReceiver(link.Pc, _rig.NewStore("pc"),
            new ReceiverOptions(_rig.PhoneId, _rig.TransferId, new OfferInfo(1, 5)) { SpaceMarginBytes = 0 }).RunAsync();
        var m = new Manual(link.Phone);
        await m.SendManifest(("a", 5));
        FrameCodec.DecodeResume((await m.Recv()).Body.Span);
        await m.Send(FrameCodec.EncodeData(new byte[5]));                  // FILE_START atlandı
        await Assert.ThrowsAsync<ProtocolException>(() => run);
        Assert.Equal(FrameType.Cancel, (await m.Recv()).Type);
    }

    // ---------------------------------------------------------------- iptal

    [Fact]
    public async Task Cancel_BySender_MidTransfer_BothSidesStop_PartialsRemoved()
    {
        var files = new List<IFileSource> { new MemoryFileSource("big.bin", TestData.Random(8 * 1024 * 1024, 21)) };
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.BytesDone > 1_000_000) cts.Cancel(); });
        using var link = await _rig.ConnectAsync();
        var sender = new FileSender(link.Phone, files, progress, TimeSpan.Zero);
        var receiver = new FileReceiver(link.Pc, _rig.NewStore("pc"), Options(files, _rig.PhoneId));

        var results = await Task.WhenAll(sender.RunAsync(cts.Token), receiver.RunAsync());

        Assert.Equal(TransferOutcome.Canceled, results[0].Outcome);
        Assert.False(results[0].CanceledByRemote);
        Assert.Equal(TransferOutcome.Canceled, results[1].Outcome);
        Assert.True(results[1].CanceledByRemote);
        Assert.Empty(FinalFiles(Path.Combine(_rig.Root, "pc-final")));
        Assert.False(Directory.Exists(Path.Combine(PartialDir("pc"), _rig.PhoneId, _rig.TransferId.ToString())));
    }

    [Fact]
    public async Task Cancel_ByReceiver_MidTransfer_SenderSeesRemoteCancel()
    {
        var files = new List<IFileSource> { new MemoryFileSource("big.bin", TestData.Random(8 * 1024 * 1024, 22)) };
        using var cts = new CancellationTokenSource();
        var recvProgress = new SyncProgress(p => { if (p.BytesDone > 1_000_000) cts.Cancel(); });
        using var link = await _rig.ConnectAsync();
        var sender = new FileSender(link.Phone, files);
        var receiver = new FileReceiver(link.Pc, _rig.NewStore("pc"), Options(files, _rig.PhoneId), recvProgress, TimeSpan.Zero);

        var results = await Task.WhenAll(sender.RunAsync(), receiver.RunAsync(cts.Token));

        Assert.Equal(TransferOutcome.Canceled, results[1].Outcome);
        Assert.False(results[1].CanceledByRemote);
        Assert.Equal(TransferOutcome.Canceled, results[0].Outcome);
        Assert.True(results[0].CanceledByRemote);
        Assert.Empty(FinalFiles(Path.Combine(_rig.Root, "pc-final")));
    }

    // ---------------------------------------------------------------- dosya adları

    [Fact]
    public async Task DangerousNames_LandUnderSafeNames_InsideTargetDirectory_Only()
    {
        var content = "x"u8.ToArray();
        var names = new[] { @"..\..\evil.txt", "../../evil2.txt", "CON", "aux.txt", "a:b", @"C:\Windows\hosts", "trail. ", "x\ty\u0001" };
        var files = names.Select(n => (IFileSource)new MemoryFileSource(n, content)).ToList();

        using var link = await _rig.ConnectAsync();
        var store = _rig.NewStore("pc");
        var results = await Task.WhenAll(new FileSender(link.Phone, files).RunAsync(),
            new FileReceiver(link.Pc, store, Options(files, _rig.PhoneId)).RunAsync());

        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
        var finalDir = Path.Combine(_rig.Root, "pc-final");
        // Hepsi aynı klasörde ve temizlenmiş adlarla; dizin dışına hiçbir şey yazılmadı.
        Assert.Equal(new[] { "_CON", "_aux.txt", "a_b", "evil.txt", "evil2.txt", "hosts", "trail", "x_y_" }.Order().ToArray(), FinalFiles(finalDir));
        Assert.All(results[1].ReceivedFiles, f => Assert.Equal(finalDir, Path.GetDirectoryName(f.StoredPath)));
        Assert.DoesNotContain(Directory.EnumerateFiles(_rig.Root, "evil*.txt", SearchOption.AllDirectories),
            p => Path.GetDirectoryName(p) != finalDir);
    }

    [Fact]
    public async Task NameCollision_NeverOverwrites_AddsCounter()
    {
        var finalDir = _rig.Dir("pc-final");
        File.WriteAllText(Path.Combine(finalDir, "a.txt"), "ESKİ");

        var files = new List<IFileSource>
        {
            new MemoryFileSource("a.txt", "bir"u8.ToArray()),
            new MemoryFileSource("a.txt", "iki"u8.ToArray()),
        };
        using var link = await _rig.ConnectAsync();
        var results = await Task.WhenAll(new FileSender(link.Phone, files).RunAsync(),
            new FileReceiver(link.Pc, _rig.NewStore("pc"), Options(files, _rig.PhoneId)).RunAsync());

        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
        Assert.Equal(["a (2).txt", "a (3).txt", "a.txt"], FinalFiles(finalDir));
        Assert.Equal("ESKİ", File.ReadAllText(Path.Combine(finalDir, "a.txt")));
        Assert.Equal("bir", File.ReadAllText(Path.Combine(finalDir, "a (2).txt")));
        Assert.Equal("iki", File.ReadAllText(Path.Combine(finalDir, "a (3).txt")));
    }

    private sealed class RecordingHook : IFileCommitHook
    {
        public List<string> Paths { get; } = [];
        public ValueTask OnFileCommittedAsync(string fullPath, CancellationToken cancellationToken)
        {
            Paths.Add(fullPath);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task CommitHook_IsCalledPerFile_AfterFileIsInPlace()
    {
        var hook = new RecordingHook();
        var files = new List<IFileSource> { new MemoryFileSource("m.bin", [1, 2, 3]), new MemoryFileSource("n.bin", [4]) };
        using var link = await _rig.ConnectAsync();
        var store = _rig.NewStore("pc", hook: hook);
        await Task.WhenAll(new FileSender(link.Phone, files).RunAsync(),
            new FileReceiver(link.Pc, store, Options(files, _rig.PhoneId)).RunAsync());

        Assert.Equal(2, hook.Paths.Count);
        Assert.All(hook.Paths, p => Assert.True(File.Exists(p)));
    }

    // ---------------------------------------------------------------- ACK kuralları

    private sealed class SpyStore(IPartialStore inner) : IPartialStore
    {
        public List<long> Synced { get; } = [];
        public long GetFreeSpaceBytes() => inner.GetFreeSpaceBytes();
        public ValueTask<CompletedFile?> GetCompletedAsync(PartialKey key, CancellationToken ct) => inner.GetCompletedAsync(key, ct);
        public ValueTask DeleteTransferAsync(string peerId, TransferId id, CancellationToken ct) => inner.DeleteTransferAsync(peerId, id, ct);
        public async ValueTask<IPartialFile?> OpenExistingAsync(PartialKey key, CancellationToken ct) =>
            await inner.OpenExistingAsync(key, ct) is { } f ? new SpyFile(f, Synced) : null;
        public async ValueTask<IPartialFile> CreateAsync(PartialKey key, long size, long mtimeMs, CancellationToken ct) =>
            new SpyFile(await inner.CreateAsync(key, size, mtimeMs, ct), Synced);
    }

    private sealed class SpyFile(IPartialFile inner, List<long> synced) : IPartialFile
    {
        public long Size => inner.Size;
        public long MtimeMs => inner.MtimeMs;
        public long AckedOffset => inner.AckedOffset;
        public ValueTask ResumeAtAsync(long o, Action<ReadOnlyMemory<byte>> c, CancellationToken ct) => inner.ResumeAtAsync(o, c, ct);
        public ValueTask AppendAsync(ReadOnlyMemory<byte> d, CancellationToken ct) => inner.AppendAsync(d, ct);
        public async ValueTask SyncAckAsync(long ackedOffset, CancellationToken ct)
        {
            await inner.SyncAckAsync(ackedOffset, ct);
            Assert.Equal(ackedOffset, inner.AckedOffset);          // kalıcılaştı
            synced.Add(ackedOffset);
        }
        public ValueTask<string> CommitAsync(string n, string sha, CancellationToken ct) => inner.CommitAsync(n, sha, ct);
        public ValueTask DeleteAsync(CancellationToken ct) => inner.DeleteAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task Ack_IsSentEveryInterval_OnlyForOffsetsAlreadySyncedToDisk()
    {
        const int interval = 1024 * 1024;
        var spy = new SpyStore(_rig.NewStore("pc"));
        using var link = await _rig.ConnectAsync();
        var receiver = new FileReceiver(link.Pc, spy,
            new ReceiverOptions(_rig.PhoneId, _rig.TransferId, new OfferInfo(1, 3_300_000)) { SpaceMarginBytes = 0, AckIntervalBytes = interval });
        var run = receiver.RunAsync();

        var m = new Manual(link.Phone);
        await m.SendManifest(("a.bin", 3_300_000));
        await m.Start(0);
        var data = TestData.Random(3_300_000, 5);
        for (var pos = 0; pos < data.Length; pos += 64_000)
            await m.Send(FrameCodec.EncodeData(data.AsMemory(pos, Math.Min(64_000, data.Length - pos)).Span));
        await m.Send(FrameCodec.Encode(new FileEndBody(0, TestData.Sha(data))));

        var acks = new List<long>();
        while (true)
        {
            var f = await m.Recv();
            if (f.Type == FrameType.Done) break;
            Assert.Equal(FrameType.Ack, f.Type);
            acks.Add(FrameCodec.DecodeAck(f.Body.Span).Offset);
        }
        await run;

        Assert.Equal(3, acks.Count);                                 // 1 MiB'lık eşik 3 kez aşıldı
        Assert.All(acks, a => Assert.InRange(a % 64_000, 0, 0));     // DATA sınırlarında
        Assert.All(acks, a => Assert.True(a >= interval));
        Assert.Equal(acks, spy.Synced);                              // her ACK, flush(true)+kalıcı kayıt tamamlanmış ofsete ait
    }

    [Fact]
    public async Task PartialFile_ResumeOffsetBeyondAckedOffset_IsRejected()
    {
        var store = _rig.NewStore("pc");
        var key = new PartialKey(_rig.PhoneId, _rig.TransferId, 0);
        await using var partial = await store.CreateAsync(key, 100, 1, default);
        await partial.ResumeAtAsync(0, _ => { }, default);
        await partial.AppendAsync(new byte[50], default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await partial.ResumeAtAsync(10, _ => { }, default));   // henüz ACK yok
        await partial.SyncAckAsync(50, default);
        await partial.DisposeAsync();

        await using var reopened = await store.OpenExistingAsync(key, default);
        Assert.Equal(50, reopened!.AckedOffset);
        var seen = new List<byte>();
        await reopened.ResumeAtAsync(30, b => seen.AddRange(b.ToArray()), default);   // ≤ ACK: izinli, ilk 30 bayt hash'e verilir
        Assert.Equal(30, seen.Count);
        Assert.Equal(30, reopened.AckedOffset);                                       // düşürüldü
    }

    [Fact]
    public async Task PartialStore_CorruptMeta_IsTreatedAsMissing_AndStalePurge()
    {
        var store = _rig.NewStore("pc");
        var key = new PartialKey(_rig.PhoneId, _rig.TransferId, 3);
        await using (var p = await store.CreateAsync(key, 10, 1, default)) { }
        var meta = Directory.EnumerateFiles(PartialDir("pc"), "3.meta", SearchOption.AllDirectories).Single();
        File.WriteAllText(meta, "{ bozuk");
        Assert.Null(await store.OpenExistingAsync(key, default));

        Assert.Equal(0, store.PurgeStaleTransfers(TimeSpan.FromDays(1)));
        Assert.Equal(1, store.PurgeStaleTransfers(TimeSpan.FromDays(1), DateTime.UtcNow.AddDays(3)));
        Assert.False(Directory.Exists(Path.Combine(PartialDir("pc"), _rig.PhoneId)));
    }

    [Fact]
    public async Task PartialStore_RejectsPathTraversalInPeerId()
    {
        var store = _rig.NewStore("pc");
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.CreateAsync(new PartialKey(@"..\..\x", _rig.TransferId, 0), 1, 1, default));
    }

    // ---------------------------------------------------------------- BLE üstünden küçük aktarım

    [Fact]
    public async Task SmallTransfer_OverBleSession_UsesSmallerChunks()
    {
        var files = new List<IFileSource> { new MemoryFileSource("kucuk.bin", TestData.Random(30_000, 31)) };
        var (phoneLink, pcLink) = MemoryBleLink.CreatePair(185);
        var phoneT = SecureChannel.ConnectIkAsync(new BleMessageTransport(phoneLink), Prefix.CreateBle(), _rig.PhoneKey, _rig.PcKey.PublicKey);
        var pcT = SecureChannel.AcceptIkAsync(new BleMessageTransport(pcLink), _rig.PcKey, _ => true);
        await Task.WhenAll(phoneT, pcT);
        using var phone = await phoneT;
        using var pc = await pcT;

        var store = _rig.NewStore("pc");
        var results = await Task.WhenAll(new FileSender(phone, files).RunAsync(),
            new FileReceiver(pc, store, Options(files, _rig.PhoneId)).RunAsync());

        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
        Assert.Equal(TestData.Random(30_000, 31), File.ReadAllBytes(Path.Combine(_rig.Root, "pc-final", "kucuk.bin")));
        Assert.Equal(8175, pc.MaxFrameBodySize);
    }
}
