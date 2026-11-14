using System.Security.Cryptography;
using WinDrop.Platform;
using Xunit;

namespace WinDrop.Platform.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinDropTests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

public class DpapiTests
{
    [Fact]
    public void ProtectUnprotect_RoundTrips_AndCiphertextDiffers()
    {
        var plain = RandomNumberGenerator.GetBytes(64);
        var enc = Storage.Protect(plain, "test");
        Assert.NotEqual(plain, enc);
        Assert.Equal(plain, Storage.Unprotect(enc, "test"));
    }

    [Fact]
    public void Unprotect_WrongPurpose_Throws()
    {
        var enc = Storage.Protect(new byte[] { 1, 2, 3 }, "a");
        Assert.ThrowsAny<CryptographicException>(() => Storage.Unprotect(enc, "b"));
    }

    [Fact]
    public void WriteAtomic_ReplacesExisting_AndLeavesNoTempFiles()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sub", "f.bin");
        Storage.WriteAtomic(path, new byte[] { 1 });
        Storage.WriteAtomic(path, new byte[] { 2, 3 });
        Assert.Equal(new byte[] { 2, 3 }, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }
}

public class IdentityStoreTests
{
    [Fact]
    public void CreatesOnce_ThenReloadsSameKey_AndFileIsNotPlaintext()
    {
        using var dir = new TempDir();
        var a = new IdentityStore(dir.Path);
        var pub = a.PublicKey;
        Assert.Equal(32, pub.Length);
        Assert.True(File.Exists(a.FilePath));

        var b = new IdentityStore(dir.Path); // yeni örnek: diskten yükler
        Assert.Equal(pub, b.PublicKey);
        Assert.Equal(a.DeviceId, b.DeviceId);
        Assert.Equal(16, a.DeviceId.Length);
        Assert.Equal(SHA256.HashData(pub)[..16], a.DeviceId);

        // Özel anahtar dosyada düz metin olarak bulunmamalı: yüklenen anahtar dışa aktarılamaz.
        Assert.Throws<InvalidOperationException>(() => b.LoadOrCreate().ExportPrivateKey());
    }

    [Fact]
    public void CorruptFile_Throws_AndDoesNotRegenerate()
    {
        using var dir = new TempDir();
        var s = new IdentityStore(dir.Path);
        _ = s.PublicKey;
        File.WriteAllBytes(s.FilePath, new byte[] { 1, 2, 3, 4 });
        var t = new IdentityStore(dir.Path);
        Assert.Throws<InvalidDataException>(() => t.LoadOrCreate());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(s.FilePath));
    }

    [Fact]
    public void Reset_GeneratesNewKey()
    {
        using var dir = new TempDir();
        var s = new IdentityStore(dir.Path);
        var first = s.PublicKey;
        s.Reset();
        Assert.NotEqual(first, s.PublicKey);
    }
}

public class TrustedDevicesTests
{
    private static byte[] Key(byte seed) => Enumerable.Repeat(seed, 32).ToArray();

    [Fact]
    public void AddListFindRemove_Persist()
    {
        using var dir = new TempDir();
        var t = new TrustedDevices(dir.Path);
        Assert.Empty(t.List());

        t.Add(Key(1), "iPhone A", new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        t.Add(Key(2), "iPhone B");

        var t2 = new TrustedDevices(dir.Path); // diskten
        Assert.Equal(2, t2.List().Count);
        var a = t2.FindByPublicKey(Key(1));
        Assert.NotNull(a);
        Assert.Equal("iPhone A", a!.Name);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), a.PairedAt);
        Assert.Equal("iPhone B", t2.FindByDeviceId(IdentityStore.ComputeDeviceId(Key(2)))!.Name);
        Assert.Null(t2.FindByPublicKey(Key(3)));

        Assert.True(t2.Remove(Key(1)));
        Assert.False(t2.Remove(Key(1)));
        Assert.True(t2.RemoveByDeviceId(IdentityStore.ComputeDeviceId(Key(2))));
        Assert.Empty(new TrustedDevices(dir.Path).List());
    }

    [Fact]
    public void Add_SameKey_UpdatesNameWithoutDuplicate()
    {
        using var dir = new TempDir();
        var t = new TrustedDevices(dir.Path);
        t.Add(Key(1), "Eski");
        t.Add(Key(1), "Yeni");
        var list = t.List();
        Assert.Single(list);
        Assert.Equal("Yeni", list[0].Name);
    }

    [Fact]
    public void Add_RejectsBadInput()
    {
        using var dir = new TempDir();
        var t = new TrustedDevices(dir.Path);
        Assert.Throws<ArgumentException>(() => t.Add(new byte[31], "x"));
        Assert.Throws<ArgumentException>(() => t.Add(Key(1), " "));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("empty")]
    public void CorruptFile_TreatedAsEmpty_FlaggedAndMovedAside(string kind)
    {
        using var dir = new TempDir();
        var path = new TrustedDevices(dir.Path).FilePath;
        File.WriteAllBytes(path, kind == "empty" ? Array.Empty<byte>() : new byte[] { 9, 9, 9, 9, 9 });

        var t = new TrustedDevices(dir.Path);
        Assert.Empty(t.List());
        Assert.True(t.WasCorrupt);
        Assert.True(File.Exists(path + ".corrupt"));

        t.Add(Key(5), "Yeni"); // bozuk dosyadan sonra yazma çalışır
        Assert.Single(new TrustedDevices(dir.Path).List());
    }

    [Fact]
    public void ValidDpapiButBadJson_IsCorrupt()
    {
        using var dir = new TempDir();
        var t = new TrustedDevices(dir.Path);
        File.WriteAllBytes(t.FilePath, Storage.Protect("not json"u8.ToArray(), "trusted.v1"));
        Assert.Empty(t.List());
        Assert.True(t.WasCorrupt);
    }
}

public class HotspotConfigStoreTests
{
    [Fact]
    public void Generate_HasExpectedFormat()
    {
        var c = HotspotConfigStore.Generate();
        Assert.Matches("^WinDrop-[A-Z2-7]{7}$", c.Ssid);
        Assert.Matches("^[A-Z2-7]{26}$", c.Passphrase);
        Assert.True(c.Passphrase.Length >= 22);
        Assert.DoesNotContain(c.Passphrase, c.ToString());
    }

    [Fact]
    public void Generate_IsRandom_NoRepeatsAndSpreadOfCharacters()
    {
        var passes = Enumerable.Range(0, 200).Select(_ => HotspotConfigStore.Generate().Passphrase).ToList();
        Assert.Equal(passes.Count, passes.Distinct().Count());
        // 200*26 karakter, 32 harflik alfabe: hepsi görülmeli (rastgelelik dağılım denetimi).
        Assert.Equal(32, passes.SelectMany(p => p).Distinct().Count());
    }

    [Fact]
    public void Base32_KnownVectors()
    {
        Assert.Equal("MZXW6YQ", Base32.Encode("foob"u8));          // RFC 4648 (dolgusuz)
        Assert.Equal("MZXW6YTBOI", Base32.Encode("foobar"u8));
        Assert.Equal("", Base32.Encode(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void GetOrCreate_PersistsAcrossInstances_AndIsEncryptedOnDisk()
    {
        using var dir = new TempDir();
        var a = new HotspotConfigStore(dir.Path).GetOrCreate();
        var b = new HotspotConfigStore(dir.Path).GetOrCreate();
        Assert.Equal(a, b);
        var raw = File.ReadAllBytes(new HotspotConfigStore(dir.Path).FilePath);
        Assert.DoesNotContain(a.Passphrase, System.Text.Encoding.UTF8.GetString(raw));
    }

    [Fact]
    public void CorruptFile_RegeneratesValidConfig()
    {
        using var dir = new TempDir();
        var s = new HotspotConfigStore(dir.Path);
        File.WriteAllBytes(s.FilePath, new byte[] { 1, 2, 3 });
        var c = s.GetOrCreate();
        Assert.True(HotspotConfigStore.IsValid(c.Ssid, c.Passphrase));
        Assert.Equal(c, new HotspotConfigStore(dir.Path).GetOrCreate());
    }
}

public class AppSettingsTests
{
    [Fact]
    public void Defaults()
    {
        var s = new AppSettings();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "WinDrop"), s.DownloadFolder);
        Assert.False(s.AutoAccept);
        Assert.Equal(2L * 1024 * 1024 * 1024, s.AutoAcceptMaxBytes);
        Assert.True(s.SendToEnabled);
        Assert.False(s.AutoStartEnabled);
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        new AppSettings { DownloadFolder = @"D:\Gelen", AutoAccept = true, AutoAcceptMaxBytes = 5, AutoStartEnabled = true, SendToEnabled = false }.Save(path);
        var s = AppSettings.Load(path);
        Assert.Equal(@"D:\Gelen", s.DownloadFolder);
        Assert.True(s.AutoAccept);
        Assert.Equal(5, s.AutoAcceptMaxBytes);
        Assert.True(s.AutoStartEnabled);
        Assert.False(s.SendToEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{\"downloadFolder\":\"\",\"autoAcceptMaxBytes\":-1}")]
    public void MissingOrCorrupt_GivesDefaults(string? content)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        if (content != null) File.WriteAllText(path, content);
        var s = AppSettings.Load(path);
        Assert.Equal(AppSettings.DefaultDownloadFolder(), s.DownloadFolder);
        Assert.Equal(AppSettings.DefaultAutoAcceptMaxBytes, s.AutoAcceptMaxBytes);
    }

    [Fact]
    public void Utf8Bom_Settings_Are_Read()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{\"language\":\"en\",\"downloadFolder\":\"D:\\\\Gelen\"}", new System.Text.UTF8Encoding(true));
        Assert.Equal(0xEF, File.ReadAllBytes(path)[0]);
        var s = AppSettings.Load(path);
        Assert.Equal("en", s.Language);
        Assert.Equal(@"D:\Gelen", s.DownloadFolder);
        Assert.False(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Corrupt_Settings_Are_Backed_Up_Not_Overwritten()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{ not json");
        var problems = new List<string>();
        var s = AppSettings.Load(path, problems.Add);
        Assert.Equal("system", s.Language);
        Assert.Single(problems);
        Assert.False(File.Exists(path));
        Assert.Equal("{ not json", File.ReadAllText(path + ".corrupt"));
        s.Save(path);   // sonraki kayıt yedeğe dokunmaz
        Assert.Equal("{ not json", File.ReadAllText(path + ".corrupt"));
    }
}
