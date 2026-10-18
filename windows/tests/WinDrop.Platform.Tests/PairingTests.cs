using System.Buffers.Text;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using Xunit;

namespace WinDrop.Platform.Tests;

public class PairingTests
{
    private static (byte[] Key, byte[] Psk, string Name) ParseQr(string payload)
    {
        var uri = new Uri(payload);
        Assert.Equal("windrop", uri.Scheme);
        Assert.Equal("pair", uri.Host);
        var q = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal("1", q["v"]);
        return (Base64Url.DecodeFromChars(q["k"]), Base64Url.DecodeFromChars(q["s"]), Uri.UnescapeDataString(q["n"]));
    }

    [Fact]
    public async Task QrPayload_HasKeySecretAndName()
    {
        await using var h = new Harness(trustPhone: false);
        var mode = h.Pairing.Start();
        var (k, s, n) = ParseQr(mode.QrPayload);
        Assert.Equal(h.PcPublicKey, k);
        Assert.Equal(32, s.Length);
        Assert.Equal("TestPC", n);
        Assert.True(h.Pairing.IsActive);
        Assert.InRange((mode.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, 100, 121);   // 2 dk
        mode.Cancel();
        Assert.False(h.Pairing.IsActive);
    }

    [Fact]
    public void Qr_NameWithSpecialCharacters_RoundTrips()
    {
        var payload = PairingService.BuildQrPayload(new byte[32], new byte[32], "Efe'nin PC & Ev");
        Assert.Equal("Efe'nin PC & Ev", ParseQr(payload).Name);
    }

    [Fact]
    public async Task ExpiredMode_IsInactive()
    {
        using var dir = new TempDir();
        var id = new IdentityStore(dir.Path);
        var p = new PairingService(id, new TrustedDevices(dir.Path), new HotspotConfigStore(dir.Path), "X", TimeSpan.FromMilliseconds(50));
        p.Start();
        Assert.True(p.IsActive);
        await Task.Delay(120);
        Assert.False(p.IsActive);
    }

    private static async Task<(SecureChannel Channel, MemoryBleLink Link, Task Run)> PhonePairAsync(Harness h, byte[] psk, byte[]? expectedKey = null)
    {
        var (phoneLink, pcLink) = MemoryBleLink.CreatePair();
        var run = h.Acceptor.RunLinkAsync(pcLink, CancellationToken.None);
        var channel = await SecureChannel.ConnectPairingAsync(new BleMessageTransport(phoneLink), Prefix.CreateBle(), h.PhoneKey, psk,
            k => k.Span.SequenceEqual(expectedKey ?? h.PcPublicKey)).WaitAsync(T.Wait);
        return (channel, phoneLink, run);
    }

    [Fact]
    public async Task Pairing_Accepted_AddsTrustedDevice_AndSendsPairInfo()
    {
        await using var h = new Harness(trustPhone: false);
        var mode = h.Pairing.Start();
        var (_, psk, _) = ParseQr(mode.QrPayload);
        var request = new TaskCompletionSource<PairingRequestInfo>();
        h.Pairing.PairingRequested += r => request.TrySetResult(r);

        var (channel, link, run) = await PhonePairAsync(h, psk);
        using (channel)
        {
            await channel.SendSignalAsync(new HelloMessage(1, "  Efe‮'in iPhone\u0007 ", ["tcp"], "connected", false));
            var req = await request.Task.WaitAsync(T.Wait);
            Assert.Equal("Efe'in iPhone", req.DeviceName);          // denetim/bidi karakterleri ayıklandı
            Assert.Empty(h.Trusted.List());                          // onaydan önce kayıt yok
            h.Pairing.Respond(req.RequestId, true);

            Assert.IsType<HelloMessage>(await channel.ReceiveSignalAsync().AsTask().WaitAsync(T.Wait));
            var info = Assert.IsType<PairInfoMessage>(await channel.ReceiveSignalAsync().AsTask().WaitAsync(T.Wait));
            var cfg = h.HotspotCfg.GetOrCreate();
            Assert.Equal(cfg.Ssid, info.Ssid);
            Assert.Equal(cfg.Passphrase, info.Pass);
            link.Close();
        }
        await run.WaitAsync(T.Wait);

        var device = Assert.Single(h.Trusted.List());
        Assert.Equal(h.PhoneKey.PublicKey, device.PublicKey);
        Assert.Equal("Efe'in iPhone", device.Name);
        Assert.False(h.Pairing.IsActive);   // tek kullanımlık

        // Aynı sır ikinci kez kullanılamaz: eşleştirme modu kapalı → IK beklenir, XXpsk3 başarısız olur.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var (c2, _, _) = await PhonePairAsync(h, psk);
            using (c2) await c2.SendSignalAsync(new HelloMessage(1, "x", [], null, false));
            await c2.ReceiveSignalAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        });
    }


    [Fact]
    public async Task Pairing_AlreadyPairedDevice_NoApprovalPrompt_AlreadyPairedEvent_PairInfoStillSent()
    {
        await using var h = new Harness(trustPhone: false);
        h.Trusted.Add(h.PhoneKey.PublicKey, "Eski iPhone");
        var before = Assert.Single(h.Trusted.List());
        Assert.False(before.IsFamiliar);
        var mode = h.Pairing.Start();
        var (_, psk, _) = ParseQr(mode.QrPayload);
        var prompted = false;
        var already = new TaskCompletionSource<string>();
        h.Pairing.PairingRequested += _ => prompted = true;
        h.Pairing.AlreadyPaired += n => already.TrySetResult(n);

        var (channel, link, run) = await PhonePairAsync(h, psk);
        using (channel)
        {
            await channel.SendSignalAsync(new HelloMessage(1, "Yeni ad", ["tcp"], "connected", false));
            Assert.IsType<HelloMessage>(await channel.ReceiveSignalAsync().AsTask().WaitAsync(T.Wait));
            Assert.IsType<PairInfoMessage>(await channel.ReceiveSignalAsync().AsTask().WaitAsync(T.Wait));
            link.Close();
        }
        await run.WaitAsync(T.Wait);

        Assert.Equal("Eski iPhone", await already.Task.WaitAsync(T.Wait));   // sonuç: AlreadyPaired
        Assert.False(prompted);
        Assert.False(h.Pairing.IsActive);                                    // sır tüketildi
        Assert.Equal("Eski iPhone", Assert.Single(h.Trusted.List()).Name);   // kayıt değişmedi
    }
    [Fact]
    public async Task Pairing_Rejected_NotTrusted_NoPairInfo()
    {
        await using var h = new Harness(trustPhone: false);
        var mode = h.Pairing.Start();
        var (_, psk, _) = ParseQr(mode.QrPayload);
        h.Pairing.PairingRequested += r => h.Pairing.Respond(r.RequestId, false);

        var (channel, link, run) = await PhonePairAsync(h, psk);
        using (channel)
        {
            await channel.SendSignalAsync(new HelloMessage(1, "Yabancı", [], null, false));
            await run.WaitAsync(T.Wait);
            link.Close();
        }
        Assert.Empty(h.Trusted.List());
        Assert.False(h.Pairing.IsActive);   // sır tüketildi
    }

    [Fact]
    public async Task Pairing_WrongSecret_Fails_AndRateLimits()
    {
        await using var h = new Harness(trustPhone: false);
        h.Pairing.Start();
        var (phoneLink, pcLink) = MemoryBleLink.CreatePair();
        var run = h.Acceptor.RunLinkAsync(pcLink, CancellationToken.None);
        // Yanlış psk: başlatan msg3'ü gönderir (kendi tarafında hata görmez), yanıtlayan msg3'te doğrulayamaz.
        using var ch = await SecureChannel.ConnectPairingAsync(new BleMessageTransport(phoneLink), Prefix.CreateBle(), h.PhoneKey,
            new byte[32], _ => true);
        await run.WaitAsync(T.Wait);   // PC oturumu düşürdü
        phoneLink.Close();
        Assert.True(h.Pairing.RemainingBackoff() > TimeSpan.Zero);   // 1/sn hız sınırı
        Assert.True(h.Pairing.IsActive);                              // sayaçla QR iptal edilmez
        Assert.Empty(h.Trusted.List());
    }

    [Fact]
    public async Task Pairing_WrongPcKey_PhoneAborts()
    {
        await using var h = new Harness(trustPhone: false);
        var mode = h.Pairing.Start();
        var (_, psk, _) = ParseQr(mode.QrPayload);
        await Assert.ThrowsAsync<PeerNotTrustedException>(() => PhonePairAsync(h, psk, new byte[32]));
        Assert.Empty(h.Trusted.List());
    }

    [Theory]
    [InlineData("  Ali  ", "Ali")]
    [InlineData("", "iPhone")]
    [InlineData("\u0001\u0002", "iPhone")]
    public void SanitizeName(string raw, string expected) => Assert.Equal(expected, PairingService.SanitizeName(raw));

    [Fact]
    public void SanitizeName_Truncates()
    {
        Assert.Equal(64, PairingService.SanitizeName(new string('a', 200)).Length);
    }
}

public class OrderedInboxTests
{
    [Fact]
    public async Task OutOfOrderCompletions_AreDeliveredInReservationOrder()
    {
        var inbox = new OrderedInbox();
        var s0 = inbox.Reserve();
        var s1 = inbox.Reserve();
        var s2 = inbox.Reserve();
        inbox.Complete(s2, [3]);
        inbox.Complete(s0, [1]);
        Assert.True(inbox.Reader.TryRead(out var a));
        Assert.Equal([1], a);
        Assert.False(inbox.Reader.TryRead(out _));   // s1 hâlâ bekleniyor
        inbox.Complete(s1, [2]);
        Assert.True(inbox.Reader.TryRead(out var b));
        Assert.True(inbox.Reader.TryRead(out var c));
        Assert.Equal([2], b);
        Assert.Equal([3], c);
        inbox.Close();
        Assert.False(await inbox.Reader.WaitToReadAsync());
    }

    [Fact]
    public void FailedSlot_IsSkipped()
    {
        var inbox = new OrderedInbox();
        var s0 = inbox.Reserve();
        var s1 = inbox.Reserve();
        inbox.Complete(s0, null);
        inbox.Complete(s1, [9]);
        Assert.True(inbox.Reader.TryRead(out var x));
        Assert.Equal([9], x);
    }
}

public class ProtocolIdsTests
{
    [Fact]
    public void Uuids_AreDistinct_AndNotProbeValues()
    {
        var ids = new[] { ProtocolIds.Service, ProtocolIds.C2p, ProtocolIds.P2c, ProtocolIds.FallbackService };
        Assert.Equal(ids.Length, ids.Distinct().Count());
        var probe = new[]
        {
            "351fca71-0000-1000-8000-00805f9b34fb", "351fca71-398a-4d79-b872-bcb6f4101984",
            "d66fb783-a725-47aa-8e14-c00f66435c36", "c4204721-2c6b-4c83-9c8c-c4985b560f40",
            "a19b2217-bc01-4b50-9b4f-77ea776974cf", "8daa0d14-5554-4b2a-8e35-525cbf9e73b7",
        };
        Assert.DoesNotContain(ids, i => probe.Contains(i.ToString()));
    }
}
