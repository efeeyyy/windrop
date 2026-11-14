using System.Collections.Concurrent;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using Xunit;

namespace WinDrop.Platform.Tests;

/// <summary>Aynı iPhone'un iki kimliği: ad güncelleme, aynı BLE kimliğinden tek aktif oturum, döngü uyarısı, yinelenen kayıt gizleme.</summary>
public class DuplicateIdentityTests
{
    [Fact]
    public async Task Ik_Hello_WithNewName_DoesNotChangeRegisteredName()
    {
        await using var h = new Harness();
        var longName = "Efe\u0007'nin‮ " + new string('x', 80);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected, helloName: longName);
        // PROTOCOL §0 (windows-core #11): kayıtlı ad eşleştirme anındaki addır; hello.name kaydı değiştirmez.
        var device = Assert.Single(h.Trusted.List());
        Assert.Equal("Test iPhone", device.Name);
        Assert.Equal("Test iPhone", Assert.Single(h.Coord.NearbyPeers).Name);
        Assert.Equal("Test iPhone", new TrustedDevices(Path.GetDirectoryName(h.Trusted.FilePath)).List()[0].Name);
    }

    [Fact]
    public async Task Xx_Hello_DoesNotRegisterOrRename()
    {
        await using var h = new Harness(trustPhone: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected, xx: true, helloName: "Yeni Ad");
        Assert.Empty(h.Trusted.List());
    }

    [Fact]
    public async Task SameBleId_SecondIdentity_IsTheOnlyNearby_AndLastBleIdRecorded()
    {
        await using var h = new Harness();
        using var key2 = NoiseKeyPair.Generate();
        h.Trusted.Add(key2.PublicKey, "iPhone");
        using var a = await h.ConnectPhoneAsync(WifiState.Connected, bleId: "BLE-1");
        using var b = await h.ConnectPhoneAsync(WifiState.Connected, key: key2, bleId: "BLE-1");

        var nearby = Assert.Single(h.Coord.NearbyPeers);
        Assert.Equal(DeviceId.HexFromPublicKey(key2.PublicKey), nearby.PeerId);
        Assert.False(h.Coord.IsNearby(h.PhoneId));
        var recorded = h.Trusted.FindByPublicKey(key2.PublicKey)!;
        Assert.Equal("BLE-1", recorded.LastBleId);
        Assert.NotNull(recorded.LastSeen);
        Assert.Equal("BLE-1", h.Trusted.FindByPublicKey(h.PhoneKey.PublicKey)!.LastBleId);
    }

    [Fact]
    public async Task IdentityFlapping_MoreThan3In10Seconds_LogsWarning_AndKeepsLatest()
    {
        var lines = new ConcurrentBag<string>();
        var previous = PlatformLog.Sink;
        PlatformLog.Sink = lines.Add;
        try
        {
            await using var h = new Harness();
            using var key2 = NoiseKeyPair.Generate();
            h.Trusted.Add(key2.PublicKey, "iPhone");
            var phones = new List<Phone>();
            try
            {
                for (var i = 0; i < 5; i++)
                    phones.Add(await h.ConnectPhoneAsync(WifiState.Connected, key: i % 2 == 0 ? h.PhoneKey : key2, bleId: "BLE-X"));
                Assert.Contains(lines, l => l.Contains("kimlik değişimi"));
                var nearby = Assert.Single(h.Coord.NearbyPeers);
                Assert.Equal(h.PhoneId, nearby.PeerId);   // 5. oturum (indeks 4) h.PhoneKey
            }
            finally { foreach (var p in phones) p.Dispose(); }
        }
        finally { PlatformLog.Sink = previous; }
    }

    [Fact]
    public async Task ThreeIdentityChanges_DoNotWarn()
    {
        var lines = new ConcurrentBag<string>();
        var previous = PlatformLog.Sink;
        PlatformLog.Sink = lines.Add;
        try
        {
            await using var h = new Harness();
            using var key2 = NoiseKeyPair.Generate();
            h.Trusted.Add(key2.PublicKey, "iPhone");
            var phones = new List<Phone>();
            try
            {
                for (var i = 0; i < 4; i++)
                    phones.Add(await h.ConnectPhoneAsync(WifiState.Connected, key: i % 2 == 0 ? h.PhoneKey : key2, bleId: "BLE-Y"));
                Assert.DoesNotContain(lines, l => l.Contains("kimlik değişimi"));
            }
            finally { foreach (var p in phones) p.Dispose(); }
        }
        finally { PlatformLog.Sink = previous; }
    }

    private static TrustedDevice Dev(byte tag, string name, string? ble, int seenMinutes) =>
        new([.. Enumerable.Repeat(tag, 32)], name, DateTimeOffset.UnixEpoch, false, null, ble, DateTimeOffset.UnixEpoch.AddMinutes(seenMinutes));

    [Fact]
    public void SupersededKeys_HidesOlderDuplicate_KeepsNearbyOrNewest()
    {
        var oldDev = Dev(1, "iPhone", "B", 1);
        var newDev = Dev(2, "iPhone", "B", 5);
        var other = Dev(3, "iPhone", "C", 0);        // farklı BLE kimliği: yinelenen sayılmaz
        var noBle = Dev(4, "iPhone", null, 0);
        var all = new[] { oldDev, newDev, other, noBle };

        var hidden = DeviceListing.SupersededKeys(all, _ => false);
        Assert.Equal([Convert.ToBase64String(oldDev.PublicKey)], hidden);

        // Eski kimlik yakındaysa o kalır, yeni olan (yakında değil) gizlenir.
        hidden = DeviceListing.SupersededKeys(all, d => d == oldDev);
        Assert.Equal([Convert.ToBase64String(newDev.PublicKey)], hidden);

        // Farklı ad = farklı cihaz.
        Assert.Empty(DeviceListing.SupersededKeys([Dev(5, "A", "B", 1), Dev(6, "B", "B", 2)], _ => false));
    }

    [Fact]
    public void TrustedDevices_PersistsLastSeen_AndKeepsItOnAdd()
    {
        using var dir = new TempDir();
        var key = new byte[32];
        key[0] = 9;
        var store = new TrustedDevices(dir.Path);
        store.Add(key, "A");
        var t = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        Assert.True(store.NoteSeen(key, "BLE-9", t));
        store.Add(key, "A2");
        var loaded = new TrustedDevices(dir.Path).FindByPublicKey(key)!;
        Assert.Equal("BLE-9", loaded.LastBleId);
        Assert.Equal(t, loaded.LastSeen);
        Assert.False(store.UpdateName(key, "  "));
        Assert.Equal("iPhone", TrustedDevices.CleanName("\u0001iPhone")!);
    }
}
