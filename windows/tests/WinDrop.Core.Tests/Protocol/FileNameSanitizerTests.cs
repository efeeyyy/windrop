using WinDrop.Core.Protocol;
using Xunit;

namespace WinDrop.Core.Tests.Protocol;

public sealed class FileNameSanitizerTests
{
    private sealed class FakeFs(params string[] existing) : IFileNameExistence
    {
        private readonly HashSet<string> _files = new(existing, StringComparer.OrdinalIgnoreCase);
        public bool Exists(string fileName) => _files.Contains(fileName);
        public void Add(string name) => _files.Add(name);
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("tatil fotoğrafı (1).jpg", "tatil fotoğrafı (1).jpg")]
    [InlineData(".gitignore", ".gitignore")]
    [InlineData("a.tar.gz", "a.tar.gz")]
    // yalnız son yol bileşeni
    [InlineData(@"..\x", "x")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Windows\System32\evil.dll", "evil.dll")]
    [InlineData("dir/sub/", "sub")]
    [InlineData(@"a\b/c.txt", "c.txt")]
    // ':' (NTFS akışı) ve diğer geçersiz karakterler
    [InlineData("a:b", "a_b")]
    [InlineData("file.txt:evil.exe", "file.txt_evil.exe")]
    [InlineData("file.txt::$DATA", "file.txt__$DATA")]
    [InlineData("a<b>c|d?e*f\"g", "a_b_c_d_e_f_g")]
    [InlineData("tab\there\u0000x\u001F", "tab_here_x_")]
    // sondaki nokta/boşluk
    [InlineData("name.", "name")]
    [InlineData("name. . ", "name")]
    [InlineData("name.txt  ", "name.txt")]
    [InlineData("a..", "a")]
    // ayrılmış adlar
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("aux.txt", "_aux.txt")]
    [InlineData("NUL.tar.gz", "_NUL.tar.gz")]
    [InlineData("COM1", "_COM1")]
    [InlineData("lpt9.log", "_lpt9.log")]
    [InlineData("COM\u00B9.txt", "_COM\u00B9.txt")]
    [InlineData("CON .txt", "_CON .txt")]
    [InlineData("CON.", "_CON")]
    [InlineData("CONIN$", "_CONIN$")]
    [InlineData("CONOUT$", "_CONOUT$")]
    [InlineData("conout$.txt", "_conout$.txt")]
    [InlineData("COM0", "COM0")]               // ayrılmış değil
    [InlineData("COM10", "COM10")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("aux1.txt", "aux1.txt")]
    // bidi sahteciliği
    [InlineData("photo\u202Egnp.exe", "photo_gnp.exe")]
    // boşa düşenler
    [InlineData("", "file")]
    [InlineData(null, "file")]
    [InlineData("..", "file")]
    [InlineData(".", "file")]
    [InlineData("...", "file")]
    [InlineData("   ", "file")]
    [InlineData("///", "file")]
    public void Sanitize_Cases(string? input, string expected) =>
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));

    [Fact]
    public void Sanitize_300Characters_TruncatesTo255_KeepingExtension()
    {
        var plain = FileNameSanitizer.Sanitize(new string('a', 300));
        Assert.Equal(255, plain.Length);

        var withExt = FileNameSanitizer.Sanitize(new string('a', 300) + ".txt");
        Assert.Equal(255, withExt.Length);
        Assert.EndsWith(".txt", withExt);

        var longExt = FileNameSanitizer.Sanitize("a." + new string('b', 300));
        Assert.Equal(255, longExt.Length);

        Assert.Equal(255, FileNameSanitizer.Sanitize(new string('a', 255)).Length);            // sınırda dokunulmaz
    }

    [Fact]
    public void Sanitize_Truncation_DoesNotSplitSurrogatePair_AndOutputIsAlwaysSafe()
    {
        var name = new string('a', 250) + "\U0001F600" + ".txt";        // kesme noktası emojiyi ortadan böler
        var result = FileNameSanitizer.Sanitize(name);
        Assert.True(result.Length <= 255);
        for (var i = 0; i < result.Length; i++)
        {
            if (!char.IsSurrogate(result[i])) continue;
            Assert.True(char.IsHighSurrogate(result[i]) && i + 1 < result.Length && char.IsLowSurrogate(result[i + 1]));
            i++;
        }
        Assert.EndsWith(".txt", result);

        Assert.Equal("a_b", FileNameSanitizer.Sanitize("a\uD800b"));    // eşleşmemiş vekil
        Assert.Equal("a\U0001F600", FileNameSanitizer.Sanitize("a\U0001F600"));
    }

    [Fact]
    public void Sanitize_ReservedPrefixAtLimit_StillWithin255()
    {
        var result = FileNameSanitizer.Sanitize("CON." + new string('x', 400));
        Assert.True(result.Length <= 255);
        Assert.StartsWith("_CON", result);
    }

    [Fact]
    public void MakeUnique_ReturnsSameName_WhenFree()
    {
        Assert.Equal("a.txt", FileNameSanitizer.MakeUnique("a.txt", new FakeFs()));
    }

    [Fact]
    public void MakeUnique_Collision_AddsCounterBeforeExtension()
    {
        var fs = new FakeFs("a.txt");
        Assert.Equal("a (2).txt", FileNameSanitizer.MakeUnique("a.txt", fs));
        fs.Add("a (2).txt");
        Assert.Equal("a (3).txt", FileNameSanitizer.MakeUnique("a.txt", fs));
        fs.Add("a (3).txt");
        fs.Add("A (4).TXT");                                              // büyük/küçük harf duyarsız dosya sistemi
        Assert.Equal("a (5).txt", FileNameSanitizer.MakeUnique("a.txt", fs));
    }

    [Fact]
    public void MakeUnique_NoExtension_And_DotFile()
    {
        Assert.Equal("README (2)", FileNameSanitizer.MakeUnique("README", new FakeFs("README")));
        Assert.Equal(".gitignore (2)", FileNameSanitizer.MakeUnique(".gitignore", new FakeFs(".gitignore")));
        Assert.Equal("a.tar (2).gz", FileNameSanitizer.MakeUnique("a.tar.gz", new FakeFs("a.tar.gz")));
    }

    [Fact]
    public void MakeUnique_AtLengthLimit_ShrinksStem()
    {
        var name = new string('a', 251) + ".txt";                         // 255
        var result = FileNameSanitizer.MakeUnique(name, new FakeFs(name));
        Assert.Equal(255, result.Length);
        Assert.EndsWith(" (2).txt", result);
    }

    [Fact]
    public void SanitizeAndMakeUnique_Combines()
    {
        var fs = new FakeFs("_con.txt");
        Assert.Equal("_con (2).txt", FileNameSanitizer.SanitizeAndMakeUnique(@"x\con.txt", fs));
    }

    [Fact]
    public void MakeUnique_GivesUp_WhenEverythingTaken()
    {
        var always = new AlwaysExists();
        Assert.Throws<InvalidOperationException>(() => FileNameSanitizer.MakeUnique("a.txt", always));
    }

    private sealed class AlwaysExists : IFileNameExistence
    {
        public bool Exists(string fileName) => true;
    }
}
