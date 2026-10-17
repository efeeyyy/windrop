using System.Net;
using WinDrop.Platform;
using Xunit;

namespace WinDrop.Platform.Tests;

public class NetworkInfoTests
{
    [Fact]
    public void GetCandidateAddresses_NonEmpty_AndAllCandidates()
    {
        var list = NetworkInfo.GetCandidateAddresses();
        Assert.NotEmpty(list);
        Assert.All(list, c => Assert.True(NetworkInfo.IsCandidate(c.Address)));
    }

    [Fact]
    public void WifiInterfacesComeFirst()
    {
        var list = NetworkInfo.GetCandidateAddresses();
        int firstNonWifi = list.ToList().FindIndex(c => !c.IsWifi);
        if (firstNonWifi >= 0)
            Assert.DoesNotContain(list.Skip(firstNonWifi), c => c.IsWifi);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.10.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("192.168.1.5", true)]
    [InlineData("161.9.21.119", true)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd12:3456:789a::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2a02:1234:5678::1", true)]
    public void IsCandidate_Rules(string ip, bool expected) =>
        Assert.Equal(expected, NetworkInfo.IsCandidate(IPAddress.Parse(ip)));

    [Fact]
    public void HasInternet_DoesNotThrow() => _ = NetworkInfo.HasInternet();
}

public class MarkOfTheWebTests
{
    [Fact]
    public void TryMark_WritesZoneIdentifierStream()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "a.txt");
        File.WriteAllText(file, "x");
        Assert.True(MarkOfTheWeb.TryMark(file));
        var mark = MarkOfTheWeb.ReadMark(file);
        Assert.NotNull(mark);
        Assert.Contains("[ZoneTransfer]", mark);
        Assert.Contains("ZoneId=3", mark);
        Assert.Equal("x", File.ReadAllText(file)); // ana akış değişmez
    }

    [Fact]
    public void TryMark_MissingFile_ReturnsFalse() =>
        Assert.False(MarkOfTheWeb.TryMark(Path.Combine(Path.GetTempPath(), "yok-" + Guid.NewGuid().ToString("N"))));
}

public class ShellIntegrationTests
{
    [Fact]
    public void SendToShortcut_CreateReadBackRemove()
    {
        using var dir = new TempDir();
        var lnk = Path.Combine(dir.Path, "SendTo", "WinDrop.lnk");
        var exe = Path.Combine(dir.Path, "WinDrop.exe");
        File.WriteAllText(exe, "");
        var shell = new ShellIntegration(exe, lnk, runKeyPath: @"Software\WinDropTests\unused");

        Assert.False(shell.IsSendToInstalled);
        shell.CreateSendToShortcut();
        Assert.True(shell.IsSendToInstalled);

        var read = ShellIntegration.ReadShortcut(lnk);
        Assert.NotNull(read);
        Assert.Equal(exe, read!.Value.Target, ignoreCase: true);
        Assert.Equal("--send", read.Value.Arguments);

        shell.CreateSendToShortcut(); // ikinci kez: üzerine yazar
        Assert.True(shell.RemoveSendToShortcut());
        Assert.False(shell.RemoveSendToShortcut());
        Assert.False(File.Exists(lnk));
    }

    [Fact]
    public void AutoStart_UsesIsolatedRunKey_AndCleansUp()
    {
        var keyPath = @"Software\WinDropTests\Run-" + Guid.NewGuid().ToString("N");
        using var dir = new TempDir();
        var shell = new ShellIntegration(Path.Combine(dir.Path, "WinDrop.exe"), Path.Combine(dir.Path, "x.lnk"), keyPath);
        try
        {
            Assert.False(shell.IsAutoStartEnabled);
            shell.EnableAutoStart();
            Assert.True(shell.IsAutoStartEnabled);
            Assert.EndsWith("--hidden", shell.AutoStartCommand);
            Assert.True(shell.DisableAutoStart());
            Assert.False(shell.IsAutoStartEnabled);
            Assert.False(shell.DisableAutoStart());
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Apply_FollowsSettings()
    {
        var keyPath = @"Software\WinDropTests\Run-" + Guid.NewGuid().ToString("N");
        using var dir = new TempDir();
        var shell = new ShellIntegration(Path.Combine(dir.Path, "WinDrop.exe"), Path.Combine(dir.Path, "x.lnk"), keyPath);
        try
        {
            shell.Apply(new AppSettings { SendToEnabled = true, AutoStartEnabled = true });
            Assert.True(shell.IsSendToInstalled);
            Assert.True(shell.IsAutoStartEnabled);
            shell.Apply(new AppSettings { SendToEnabled = false, AutoStartEnabled = false });
            Assert.False(shell.IsSendToInstalled);
            Assert.False(shell.IsAutoStartEnabled);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }
}
