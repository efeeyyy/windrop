using WinDrop.Core.Protocol;
using WinDrop.Core.Tests.Support;
using WinDrop.Core.Transfer;
using Xunit;

namespace WinDrop.Core.Tests.Transfer;

/// <summary>PROTOCOL §6.7 klasör gönderme: tarama, MANIFEST <c>dir</c>, alıcı yol güvenliği, üst klasör çakışması, devam ettirme.</summary>
public sealed class FolderTransferTests : IDisposable
{
    private readonly Rig _rig = new();

    public void Dispose() => _rig.Dispose();

    private string Final => Path.Combine(_rig.Root, "pc-final");

    private ReceiverOptions Options(IReadOnlyList<IFileSource> files) =>
        new(_rig.PhoneId, _rig.TransferId, new OfferInfo(files.Count, files.Sum(f => f.Size))) { SpaceMarginBytes = 0 };

    private async Task<TransferResult[]> RunAsync(IReadOnlyList<IFileSource> files)
    {
        using var link = await _rig.ConnectAsync();
        var store = _rig.NewStore("pc");
        return await Task.WhenAll(new FileSender(link.Phone, files).RunAsync(), new FileReceiver(link.Pc, store, Options(files)).RunAsync());
    }

    private string Write(string relative, byte[] data)
    {
        var path = Path.Combine(_rig.Root, "src", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    // ---------------------------------------------------------------- tarama + uçtan uca

    [Fact]
    public async Task NestedThreeLevels_SameNamesInDifferentFolders_ArriveIntactWithStructure()
    {
        var expected = new Dictionary<string, byte[]>
        {
            ["Proje/a.txt"] = TestData.Random(1000, 1),
            ["Proje/x/a.txt"] = TestData.Random(2000, 2),          // aynı ad, farklı klasör
            ["Proje/x/y/a.txt"] = TestData.Random(3000, 3),
            ["Proje/x/y/z/a.txt"] = TestData.Random(150_000, 4),
            ["Proje/x/y/z/bos.bin"] = [],
            ["Proje/gizli.txt"] = TestData.Random(10, 5),
        };
        foreach (var (rel, data) in expected) Write(rel, data);
        File.SetAttributes(Path.Combine(_rig.Root, "src", "Proje", "gizli.txt"), FileAttributes.Hidden);   // gizli dosya da gider

        var log = new List<string>();
        var files = FolderScanner.Collect([Path.Combine(_rig.Root, "src", "Proje")], log.Add);
        Assert.Equal(expected.Count, files.Count);
        Assert.All(files, f => Assert.StartsWith("Proje", f.Dir));

        var results = await RunAsync(files);

        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
        foreach (var (rel, data) in expected)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(Final, rel.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(expected.Count, Directory.EnumerateFiles(Final, "*", SearchOption.AllDirectories).Count());
        Assert.Equal(expected.Count, results[1].ReceivedFiles.Count);
    }

    [Fact]
    public void Scanner_MixedFilesAndFolders_UsesFolderNameAsRoot_AndDeduplicatesSameNamedRoots()
    {
        Write("p1/Foto/1.jpg", [1]);
        Write("p2/Foto/2.jpg", [2]);
        var loose = Write("tek.txt", [3]);
        var files = FolderScanner.Collect([loose, Path.Combine(_rig.Root, "src", "p1", "Foto"), Path.Combine(_rig.Root, "src", "p2", "Foto")]);
        Assert.Equal<IEnumerable<string?>>(new string?[] { null, "Foto", "Foto (2)" }, files.Select(f => f.Dir));
    }

    [Fact]
    public void Scanner_SixteenLevels_Accepted_SeventeenRejected()
    {
        var ok = string.Join('/', Enumerable.Range(0, 16).Select(i => "d" + i));      // 16 bileşen
        Write(ok + "/f.txt", [1]);
        var files = FolderScanner.Collect([Path.Combine(_rig.Root, "src", "d0")]);
        Assert.Equal(16, files.Single().Dir!.Split('/').Length);

        Write(ok + "/d16/f2.txt", [2]);                                                  // 17 bileşen
        var ex = Assert.Throws<FolderScanException>(() => FolderScanner.Collect([Path.Combine(_rig.Root, "src", "d0")]));
        Assert.Contains("16", ex.Message);
    }

    [Fact]
    public void Scanner_DoesNotFollowSymlinkedDirectories()
    {
        Write("T/real/in.txt", [1]);
        var link = Path.Combine(_rig.Root, "src", "T", "link");
        try { Directory.CreateSymbolicLink(link, Path.Combine(_rig.Root, "src", "T", "real")); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }   // ayrıcalık yok

        var log = new List<string>();
        var files = FolderScanner.Collect([Path.Combine(_rig.Root, "src", "T")], log.Add);
        Assert.Equal<IEnumerable<string?>>(new string?[] { "T/real" }, files.Select(f => f.Dir));
        Assert.Contains(log, l => l.Contains("link"));
    }

    [Fact]
    public void Manifest_DirOmittedWhenNull_AndUnknownFieldsIgnored()
    {
        var withoutDir = System.Text.Encoding.UTF8.GetString(FrameCodec.Encode(new ManifestBody([new ManifestFile(0, "a", 1, 2)])));
        Assert.DoesNotContain("dir", withoutDir);
        var withDir = FrameCodec.Encode(new ManifestBody([new ManifestFile(0, "a", 1, 2, "K/s")]));
        Assert.Contains("\"dir\":\"K/s\"", System.Text.Encoding.UTF8.GetString(withDir));
        var back = FrameCodec.DecodeManifest(withDir.AsSpan(1));
        Assert.Equal("K/s", back.Files[0].Dir);
        var unknown = "{\"files\":[{\"id\":0,\"name\":\"a\",\"size\":1,\"mtime\":2,\"extra\":5}]}"u8;
        Assert.Null(FrameCodec.DecodeManifest(unknown).Files[0].Dir);
    }

    // ---------------------------------------------------------------- alıcı: yol güvenliği

    [Theory]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("../x")]
    [InlineData("a/..")]
    [InlineData(".")]
    [InlineData("a/./b")]
    [InlineData("/abs")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows")]
    [InlineData("C:\\Windows")]
    [InlineData("a\\b")]
    [InlineData("a\\..\\b")]
    [InlineData("")]
    [InlineData("a//b")]
    [InlineData("a/")]
    public void Parse_RejectsEscapingOrMalformedDir(string dir) =>
        Assert.Throws<ProtocolException>(() => RelativeDirectory.Parse(dir));

    [Fact]
    public void Parse_SanitizesEachComponent_AndEnforcesLimits()
    {
        Assert.Null(RelativeDirectory.Parse(null));
        Assert.Equal(["_CON", "a_b", "x"], RelativeDirectory.Parse("CON/a:b/x.."));
        Assert.Equal(16, RelativeDirectory.Parse(string.Join('/', Enumerable.Repeat("d", 16)))!.Count);
        Assert.Throws<ProtocolException>(() => RelativeDirectory.Parse(string.Join('/', Enumerable.Repeat("d", 17))));
        Assert.Throws<ProtocolException>(() => RelativeDirectory.Parse(new string('d', 1025)));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("/abs")]
    [InlineData("a\\b")]
    public async Task Receiver_RejectsBadDir_BeforeWritingAnything(string dir)
    {
        using var link = await _rig.ConnectAsync();
        var store = _rig.NewStore("pc");
        var run = new FileReceiver(link.Pc, store, new ReceiverOptions(_rig.PhoneId, _rig.TransferId, new OfferInfo(1, 1)) { SpaceMarginBytes = 0 }).RunAsync();
        await link.Phone.SendFrameAsync(FrameCodec.Encode(new ManifestBody([new ManifestFile(0, "f.txt", 1, 1, dir)])));
        Assert.Equal(FrameType.Cancel, (await link.Phone.ReceiveFrameAsync())!.Value.Type);
        await Assert.ThrowsAsync<ProtocolException>(() => run);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Final));
    }

    [Fact]
    public async Task Receiver_SanitizesComponentNames_AndStaysUnderDownloadFolder()
    {
        var files = new List<IFileSource>
        {
            new MemoryFileSource("f.txt", [1, 2, 3], dir: "NUL/we:ird?/ok"),
        };
        var results = await RunAsync(files);
        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
        var stored = results[1].ReceivedFiles.Single().StoredPath;
        Assert.Equal(Path.Combine(Final, "_NUL", "we_ird_", "ok", "f.txt"), stored);
    }

    // ---------------------------------------------------------------- en üst klasör çakışması

    [Fact]
    public async Task TopFolderCollision_AllFilesGoToSameRenamedRoot_ExistingFolderUntouched()
    {
        _rig.NewStore("pc");   // final klasörünü oluştur
        Directory.CreateDirectory(Path.Combine(Final, "Proje"));
        File.WriteAllText(Path.Combine(Final, "Proje", "eski.txt"), "eski");
        var files = new List<IFileSource>
        {
            new MemoryFileSource("a.txt", [1], dir: "Proje"),
            new MemoryFileSource("b.txt", [2], dir: "Proje/alt"),
            new MemoryFileSource("a.txt", [3], dir: "Proje/alt/derin"),
        };

        var results = await RunAsync(files);

        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
        Assert.Equal<IEnumerable<string?>>(new string?[] { "eski.txt" }, Directory.EnumerateFiles(Path.Combine(Final, "Proje")).Select(Path.GetFileName));
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(Final, "Proje (2)", "a.txt")));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(Final, "Proje (2)", "alt", "b.txt")));
        Assert.Equal([3], File.ReadAllBytes(Path.Combine(Final, "Proje (2)", "alt", "derin", "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(Final, "Proje (3)")));
    }

    [Fact]
    public async Task SubfolderFileCollision_UsesFileRule()
    {
        _rig.NewStore("pc");
        Directory.CreateDirectory(Path.Combine(Final, "K"));   // K zaten var -> K (2)
        var files = new List<IFileSource>
        {
            new MemoryFileSource("a.txt", [1], dir: "K/s"),
            new MemoryFileSource("a.txt", [2], dir: "K/s"),     // aynı klasör, aynı ad -> "a (2).txt"
        };
        var results = await RunAsync(files);
        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
        Assert.Equal<IEnumerable<string?>>(new string?[] { "a (2).txt", "a.txt" }, Directory.EnumerateFiles(Path.Combine(Final, "K (2)", "s")).Select(Path.GetFileName).Order());
    }

    // ---------------------------------------------------------------- devam ettirme

    [Fact]
    public async Task ResumeInsideFolder_KeepsRootAndCompletedFiles_NoDuplicates()
    {
        var small = TestData.Random(100_000, 3);
        var big = TestData.Random(12 * 1024 * 1024, 4);
        _rig.NewStore("pc");
        Directory.CreateDirectory(Path.Combine(Final, "Proje"));   // çakışma: kök "Proje (2)" olarak çözülmeli ve oturumlar arası sabit kalmalı
        var files = new IFileSource[]
        {
            new FileSystemFileSource(Write("Proje/a/small.bin", small), "Proje/a"),
            new FileSystemFileSource(Write("Proje/a/b/big.bin", big), "Proje/a/b"),
        };

        using (var link = await _rig.ConnectAsync(droppable: true))
        {
            var sender = new FileSender(link.Phone, files);
            var receiver = new FileReceiver(link.Pc, _rig.NewStore("pc"), Options(files));
            link.Dropper!.Arm(100);
            var s = sender.RunAsync();
            var r = receiver.RunAsync();
            await Assert.ThrowsAnyAsync<IOException>(() => s);
            await Assert.ThrowsAsync<ConnectionLostException>(() => r);
        }
        Assert.True(File.Exists(Path.Combine(Final, "Proje (2)", "a", "small.bin")));

        using var link2 = await _rig.ConnectAsync();
        var results = await Task.WhenAll(new FileSender(link2.Phone, files).RunAsync(),
            new FileReceiver(link2.Pc, _rig.NewStore("pc"), Options(files)).RunAsync());

        Assert.All(results, r => Assert.Equal(TransferOutcome.Completed, r.Outcome));
        Assert.Equal(small.Length, results[1].ResumeOffsets[0]);     // biten dosya yeniden gönderilmedi
        Assert.True(results[1].ResumeOffsets[1] > 0);                // büyük dosya ACK'lenen ofsetten sürdü
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(Final, "Proje (2)", "a", "b", "big.bin")));
        Assert.Equal(small, File.ReadAllBytes(Path.Combine(Final, "Proje (2)", "a", "small.bin")));
        Assert.False(Directory.Exists(Path.Combine(Final, "Proje (3)")));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(Final, "Proje (2)"), "*", SearchOption.AllDirectories).Count());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Final, "Proje")));
    }
}
