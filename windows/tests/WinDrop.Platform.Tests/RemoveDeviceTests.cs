using WinDrop.Core.Session;
using Xunit;

namespace WinDrop.Platform.Tests;

/// <summary>Eşleştirme kaldırma: anında etkili, açık BLE oturumu WDR ile sıfırlanır, ayar bayrağı kalıcıdır.</summary>
public class RemoveDeviceTests
{
    [Fact]
    public async Task DropPeer_RemovesFromNearbyImmediately_AndSendsWdr()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync("connected");
        Assert.True(h.Trusted.Remove(h.PhoneKey.PublicKey));
        h.Coord.DropPeer(h.PhoneId);

        Assert.False(h.Coord.IsNearby(h.PhoneId));          // kapanışı beklemeden listeden düştü
        Assert.Empty(h.Coord.NearbyPeers);

        using var cts = new CancellationTokenSource(T.Wait);
        Assert.Equal(BleResync.Marker, await phone.Link.ReceiveAsync(cts.Token));   // §3a WDR
    }

    [Fact]
    public async Task AfterRemove_Reconnect_IsUnknownXx_WithoutTrustedEntry()
    {
        await using var h = new Harness();
        using (var phone = await h.ConnectPhoneAsync("connected"))
        {
            h.Trusted.Remove(h.PhoneKey.PublicKey);
            h.Coord.DropPeer(h.PhoneId);
        }
        using var again = await h.ConnectPhoneAsync("connected", xx: true);
        var peer = Assert.Single(h.Coord.NearbyPeers);
        Assert.False(peer.IsKnown);
        Assert.Empty(h.Trusted.List());                     // oturum cihazı yeniden kaydetmedi
    }

    [Fact]
    public void TrustedDevices_Remove_One_And_All_Persist()
    {
        using var dir = new TempDir();
        var t = new TrustedDevices(dir.Path);
        var keys = Enumerable.Range(1, 3).Select(i => Enumerable.Repeat((byte)i, 32).ToArray()).ToArray();
        foreach (var k in keys) t.Add(k, "d" + k[0]);

        Assert.True(t.Remove(keys[0]));
        Assert.False(t.Remove(keys[0]));
        Assert.Equal(2, new TrustedDevices(dir.Path).List().Count);   // diskten okununca da gitmiş

        foreach (var d in t.List().ToArray()) Assert.True(t.RemoveByDeviceId(d.DeviceId));
        Assert.Empty(new TrustedDevices(dir.Path).List());
    }

    [Fact]
    public void TrayHintShown_DefaultsFalse_AndPersists()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        Assert.False(AppSettings.Load(path).TrayHintShown);
        new AppSettings { TrayHintShown = true }.Save(path);
        Assert.True(AppSettings.Load(path).TrayHintShown);
    }
}
