using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using Xunit;

namespace WinDrop.Platform.Tests;

/// <summary>PROTOCOL §3a: oturum protokol hatasıyla ölünce WDR, ardından eski yazmaları yok sayma, yeni ön ekle yeniden kurulum.</summary>
public class BleResyncTests
{
    private static async Task<byte[]> ReceiveAsync(MemoryBleLink link)
    {
        using var cts = new CancellationTokenSource(T.Wait);
        return await link.ReceiveAsync(cts.Token) ?? throw new InvalidOperationException("bağlantı kapandı");
    }

    private static async Task AssertSilentAsync(MemoryBleLink link)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await link.ReceiveAsync(cts.Token));
    }

    [Fact]
    public async Task SequenceGap_SendsWdr_IgnoresStaleWrites_AndReestablishesOnNewPrefix()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync("connected", resync: true);
        var link = phone.Link;

        // Eski oturumdan sıra no atlaması (PC'nin beklediği 87 değil).
        await link.SendAsync(new byte[] { 87 | 0x80, 1, 2, 3 }, CancellationToken.None);
        Assert.Equal(BleResync.Marker, await ReceiveAsync(link));

        // Eski oturumun yazmaları: yanıt yok (WDR yinelenmez), yeni bağlantı/hata yok.
        await link.SendAsync(new byte[33], CancellationToken.None);
        await link.SendAsync(new byte[150], CancellationToken.None);
        await link.SendAsync(new byte[20], CancellationToken.None);   // 20 bayt ama "WD" değil
        await link.SendAsync(new byte[] { 87, 0x44, 1, 1 }, CancellationToken.None);
        await AssertSilentAsync(link);

        // Yeni ön ek + el sıkışma: oturum yeniden kurulur.
        var transport = new BleMessageTransport(link);
        using var channel = await SecureChannel.ConnectIkAsync(transport, Prefix.CreateBle(), h.PhoneKey, h.PcPublicKey).WaitAsync(T.Wait);
        var hello = await channel.ReceiveSignalAsync().AsTask().WaitAsync(T.Wait);
        Assert.Equal("TestPC", Assert.IsType<HelloMessage>(hello).Name);
        await channel.SendSignalAsync(new HelloMessage(1, "iPhone", ["tcp"], "connected", false)).AsTask().WaitAsync(T.Wait);
        var deadline = DateTime.UtcNow + T.Wait;
        while (!h.Coord.IsNearby(h.PhoneId) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(h.Coord.IsNearby(h.PhoneId));
    }

    [Fact]
    public async Task PrefixMidSession_RestartsSessionWithoutWdr()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync("connected", resync: true);
        var link = phone.Link;

        // Oturum ortasında (taşıma aşaması) yeni ön ek: WDR yok, yeni el sıkışma kurulur.
        var transport = new BleMessageTransport(link);
        using var channel = await SecureChannel.ConnectIkAsync(transport, Prefix.CreateBle(), h.PhoneKey, h.PcPublicKey).WaitAsync(T.Wait);
        var hello = await channel.ReceiveSignalAsync().AsTask().WaitAsync(T.Wait);
        Assert.Equal("TestPC", Assert.IsType<HelloMessage>(hello).Name);
        await AssertSilentAsync(link);   // WDR gelmedi
    }

    [Fact]
    public async Task TwentyByteNonPrefixFragment_StillFailsNormally()
    {
        await using var h = new Harness();
        using var phone = await h.ConnectPhoneAsync("connected", resync: true);
        var bad = new byte[20];
        bad[0] = 87 | 0x80;   // 'W' benzeri başlık ama "WD"+sıfır ön ek değil: sıra no atlaması
        await phone.Link.SendAsync(bad, CancellationToken.None);
        Assert.Equal(BleResync.Marker, await ReceiveAsync(phone.Link));
    }

    [Fact]
    public async Task HandshakeProtocolError_SendsWdr()
    {
        await using var h = new Harness();
        var (phoneLink, pcLink) = MemoryBleLink.CreatePair();
        var run = h.Acceptor.RunLinkWithResyncAsync(pcLink, CancellationToken.None);

        await phoneLink.SendAsync(new byte[33], CancellationToken.None);   // ön ek 20 bayt olmalı
        Assert.Equal(BleResync.Marker, await ReceiveAsync(phoneLink));

        phoneLink.Close();
        await run.WaitAsync(T.Wait);
    }

    [Fact]
    public async Task CleanClose_DoesNotSendWdr()
    {
        await using var h = new Harness();
        var phone = await h.ConnectPhoneAsync("connected", resync: true);
        phone.Dispose();   // temiz kapanış
        await AssertSilentAsync(phone.Link);
    }
}
