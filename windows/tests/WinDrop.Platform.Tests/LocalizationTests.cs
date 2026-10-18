using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinDrop.Platform;
using Xunit;

namespace WinDrop.Platform.Tests;

/// <summary>Eksik çeviri denetimi: Strings.resx (Türkçe, nötr) ve Strings.en.resx aynı anahtarlara/yer tutuculara sahip olmalı.</summary>
public class LocalizationTests
{
    private static string ResourcesDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "WinDrop.App", "Resources");
            if (File.Exists(Path.Combine(candidate, "Strings.resx"))) return candidate;
        }
        throw new FileNotFoundException("Strings.resx bulunamadı.");
    }

    private static Dictionary<string, string> Load(string file) =>
        XDocument.Load(Path.Combine(ResourcesDir(), file)).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "");

    private static string[] Placeholders(string value) =>
        Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).Distinct().Order().ToArray();

    [Fact]
    public void English_And_Turkish_Resx_Have_Identical_Keys()
    {
        var tr = Load("Strings.resx");
        var en = Load("Strings.en.resx");
        Assert.Empty(tr.Keys.Except(en.Keys));   // İngilizcede eksik
        Assert.Empty(en.Keys.Except(tr.Keys));   // Türkçede eksik
    }

    [Fact]
    public void Values_Are_Not_Empty_And_Placeholders_Match()
    {
        var tr = Load("Strings.resx");
        var en = Load("Strings.en.resx");
        foreach (var (key, value) in tr)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"Strings.resx boş: {key}");
            Assert.False(string.IsNullOrWhiteSpace(en[key]), $"Strings.en.resx boş: {key}");
            Assert.True(Placeholders(value).SequenceEqual(Placeholders(en[key])), $"Yer tutucular uyuşmuyor: {key}");
        }
    }

    [Fact]
    public void Every_UserErrorCode_Has_A_Translation_In_Both_Languages()
    {
        var tr = Load("Strings.resx");
        var en = Load("Strings.en.resx");
        foreach (var code in Enum.GetValues<UserErrorCode>().Where(c => c != UserErrorCode.Raw))
        {
            Assert.True(tr.ContainsKey("Err." + code), $"Strings.resx: Err.{code} yok");
            Assert.True(en.ContainsKey("Err." + code), $"Strings.en.resx: Err.{code} yok");
        }
    }

    [Fact]
    public void Turkish_Resx_Err_Text_Matches_Platform_Log_Text()
    {
        var tr = Load("Strings.resx");
        foreach (var code in Enum.GetValues<UserErrorCode>().Where(c => c != UserErrorCode.Raw))
            Assert.Equal(new UserErrorPart(code, "A", "B").ToString(), string.Format(tr["Err." + code], "A", "B"));
    }

    [Fact]
    public void Settings_Language_Defaults_To_System_And_Rejects_Unknown_Values()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        Assert.Equal("system", AppSettings.Load(path).Language);
        File.WriteAllText(path, "{\"language\":\"EN\"}");
        Assert.Equal("en", AppSettings.Load(path).Language);
        File.WriteAllText(path, "{\"language\":\"de\"}");
        Assert.Equal("system", AppSettings.Load(path).Language);
    }
}
