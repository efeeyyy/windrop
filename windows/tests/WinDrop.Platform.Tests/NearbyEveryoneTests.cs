using System.Security.Cryptography;
using System.Text;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Core.Transfer;
using Xunit;

namespace WinDrop.Platform.Tests;

/// <summary>PROTOCOL §2b: eşleştirmesiz "yakındaki herkes" modu (Noise XX, alıcı onayı, tanıdık cihaz, doğrulama kodu).</summary>
public class NearbyEveryoneTests
{
    [Fact]
    public async Task UnknownPhone_Xx_AppearsNearby_WithHelloName()
    {
        await using var h = new Harness(trustPhone: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected, xx: true);
        var nearby = Assert.Single(h.Coord.NearbyPeers);
        Assert.Equal(h.PhoneId, nearby.PeerId);
        Assert.Equal("iPhone", nearby.Name);       // hello.name (kayıt yok)
        Assert.False(nearby.IsKnown);
        Assert.False(nearby.IsPaired);
        Assert.Empty(h.Trusted.List());             // bağlanmak kayıt oluşturmaz
        Assert.Equal(HandshakeKind.XX, phone.Channel.Kind);
    }

    [Fact]
    public async Task UnknownPhone_Offer_AlwaysAsksUser_EvenWithAutoAccept_ShowsMatchingCode()
    {
        await using var h = new Harness(autoAccept: true, trustPhone: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected, xx: true);
        var offered = new TaskCompletionSource<OfferRequest>();
        h.Coord.OfferReceived += o => offered.TrySetResult(o);

        var data = T.Random(60_000, 31);
        var id = TransferId.Create();
        await phone.SendAsync(new OfferMessage(id, 1, data.Length, ["a.bin"]));
        var req = await offered.Task.WaitAsync(T.Wait);
        Assert.Equal(phone.Sas, req.VerificationCode);   // iki ekranda aynı 4 hane
        Assert.Matches("^[0-9]{4}$", req.VerificationCode!);
        Assert.False(req.IsKnownPeer);
        Assert.Empty(h.Trusted.List());                   // onaydan önce kayıt yok

        h.Coord.RespondToOffer(req.Id, true);
        Assert.IsType<AcceptMessage>(await phone.ReceiveAsync());
        var addr = Assert.IsType<AddrMessage>(await phone.ReceiveAsync());

        // Veri kanalı da XX; karşı anahtar BLE oturumundaki eşle aynı olduğu için kabul edilir.
        var (channel, client) = await T.ConnectDataAsync(addr.Candidates[0].Port, id, h.PhoneKey, h.PcPublicKey, xx: true);
        using (client)
        using (channel)
            Assert.Equal(TransferOutcome.Completed, (await new FileSender(channel, [new MemoryFileSource("a.bin", data)]).RunAsync().WaitAsync(T.Wait)).Outcome);
        Assert.Equal(TransferPhase.Completed, (await h.Coord.WaitAsync(id).WaitAsync(T.Wait)).Phase);

        // Onaylanan cihaz "tanıdık" olarak hatırlandı (eşli değil).
        var device = Assert.Single(h.Trusted.List());
        Assert.True(device.IsFamiliar);
        Assert.Equal(h.PhoneKey.PublicKey, device.PublicKey);
    }

    [Fact]
    public async Task UnknownPhone_Declined_NotRemembered_AndSilencedFor2Minutes()
    {
        await using var h = new Harness(trustPhone: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected, xx: true);
        h.Coord.OfferReceived += o => h.Coord.RespondToOffer(o.Id, false);

        await phone.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["x"]));
        Assert.Equal("declined", Assert.IsType<RejectMessage>(await phone.ReceiveAsync()).Reason);
        Assert.Empty(h.Trusted.List());

        await phone.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["x"]));
        Assert.Equal("silenced", Assert.IsType<RejectMessage>(await phone.ReceiveAsync()).Reason);
    }

    [Fact]
    public async Task UnpairedPhone_OfferRateLimit_IsThreePerMinute()
    {
        await using var h = new Harness(trustPhone: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected, xx: true);
        h.Coord.OfferReceived += o => h.Coord.RespondToOffer(o.Id, true);   // kabul: sessizlik devreye girmez
        for (var i = 0; i < 3; i++)
        {
            await phone.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["x"]));
            Assert.IsType<AcceptMessage>(await phone.ReceiveAsync());
            Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        }
        await phone.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["x"]));
        Assert.Equal("rate_limited", Assert.IsType<RejectMessage>(await phone.ReceiveAsync()).Reason);
    }

    [Fact]
    public async Task FamiliarPhone_ReconnectsWithIk_ButStillNeedsApproval()
    {
        await using var h = new Harness(autoAccept: true, trustPhone: false);
        h.Trusted.Add(h.PhoneKey.PublicKey, "Tanıdık iPhone", familiar: true);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);   // IK
        Assert.Equal(HandshakeKind.IK, phone.Channel.Kind);
        var nearby = Assert.Single(h.Coord.NearbyPeers);
        Assert.True(nearby.IsKnown);
        Assert.False(nearby.IsPaired);
        Assert.Equal("Tanıdık iPhone", nearby.Name);   // kayıttaki ad (hello.name değil)

        var offered = new TaskCompletionSource<OfferRequest>();
        h.Coord.OfferReceived += o => offered.TrySetResult(o);
        await phone.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["x"]));
        var req = await offered.Task.WaitAsync(T.Wait);   // autoAccept açık olsa da eşli olmayan için onay
        Assert.Null(req.VerificationCode);                // IK oturumunda kod yok
        Assert.True(req.IsKnownPeer);
    }

    [Fact]
    public async Task PairedPhone_StillAutoAccepts()
    {
        await using var h = new Harness(autoAccept: true);   // QR ile eşli (familiar değil)
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        h.Coord.OfferReceived += _ => throw new Xunit.Sdk.XunitException("eşli cihaz için onay istenmemeli");
        await phone.SendAsync(new OfferMessage(TransferId.Create(), 1, 10, ["x"]));
        Assert.IsType<AcceptMessage>(await phone.ReceiveAsync());
    }

    [Fact]
    public async Task PcSends_ToUnknownNearbyPhone_OverXxSession()
    {
        await using var h = new Harness(trustPhone: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected, xx: true);
        var data = T.Random(90_000, 41);
        var path = Path.Combine(h.Dir.Path, "send.bin");
        File.WriteAllBytes(path, data);

        var snap = h.Coord.Send(h.PhoneId, [path]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        var addr = Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        var (channel, client) = await T.ConnectDataAsync(addr.Candidates[0].Port, offer.TransferId, h.PhoneKey, h.PcPublicKey, xx: true);
        using (client)
        using (channel)
        {
            var opts = new ReceiverOptions(h.PcId, offer.TransferId, new OfferInfo(1, data.Length));
            Assert.Equal(TransferOutcome.Completed, (await new FileReceiver(channel, T.Store(h.Dir.Path, "ph"), opts).RunAsync().WaitAsync(T.Wait)).Outcome);
        }
        Assert.Equal(TransferPhase.Completed, (await h.Coord.WaitAsync(snap.Id).WaitAsync(T.Wait)).Phase);
    }

    [Fact]
    public async Task DataChannel_Xx_WithDifferentKeyThanBleSession_IsRejected()
    {
        await using var h = new Harness(trustPhone: false);
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected, xx: true);
        var path = Path.Combine(h.Dir.Path, "s.bin");
        File.WriteAllBytes(path, T.Random(1000));
        var snap = h.Coord.Send(h.PhoneId, [path]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        var addr = Assert.IsType<AddrMessage>(await phone.ReceiveAsync());

        using var impostor = NoiseKeyPair.Generate();
        // XX'te başlatan msg3'ten sonra hemen "bitmiş" görünür; reddi kapanışla anlar.
        var (ch, cl) = await T.ConnectDataAsync(addr.Candidates[0].Port, offer.TransferId, impostor, h.PcPublicKey, xx: true);
        using (cl) using (ch)
        {
            var read = Task.Run(async () =>
            {
                try { return await ch.ReceiveAsync(); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or ProtocolException or NoiseException) { return null; }
            });
            Assert.Null(await read.WaitAsync(T.Wait));
        }
        h.Coord.Cancel(snap.Id);
    }

    [Fact]
    public async Task KnownPhone_IkWorks_WhileQrPairingModeIsOpen()
    {
        await using var h = new Harness();   // eşli telefon
        h.Pairing.Start();                    // QR ekranda: XXpsk3 beklenir, ama IK (96 B) uzunluğundan ayrılır
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        Assert.Equal(HandshakeKind.IK, phone.Channel.Kind);
        Assert.True(h.Coord.IsNearby(h.PhoneId));
    }

    [Fact]
    public async Task StrangerXx_StillWorks_WhileQrPairingModeIsOpen()
    {
        await using var h = new Harness(trustPhone: false);
        h.Pairing.Start();   // XXpsk3 ilk mesajı 48 B, XX 32 B: uzunluktan ayrılır, mod XX'i engellemez
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected, xx: true);
        Assert.Equal(HandshakeKind.XX, phone.Channel.Kind);
        Assert.True(h.Pairing.IsActive);   // QR hâlâ geçerli
        Assert.Empty(h.Trusted.List());
    }

    [Fact]
    public async Task DataChannel_PatternMustMatchBleSession_IkSessionRejectsXx()
    {
        await using var h = new Harness();   // eşli: BLE IK
        using var phone = await h.ConnectPhoneAsync(WifiState.Connected);
        var path = Path.Combine(h.Dir.Path, "s.bin");
        File.WriteAllBytes(path, T.Random(1000));
        var snap = h.Coord.Send(h.PhoneId, [path]);
        var offer = Assert.IsType<OfferMessage>(await phone.ReceiveAsync());
        await phone.SendAsync(new AcceptMessage(offer.TransferId));
        var addr = Assert.IsType<AddrMessage>(await phone.ReceiveAsync());
        var (ch, cl) = await T.ConnectDataAsync(addr.Candidates[0].Port, offer.TransferId, h.PhoneKey, h.PcPublicKey, xx: true);
        using (cl) using (ch)
        {
            var read = Task.Run(async () =>
            {
                try { return await ch.ReceiveAsync(); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or ProtocolException or NoiseException) { return null; }
            });
            Assert.Null(await read.WaitAsync(T.Wait));
        }
        h.Coord.Cancel(snap.Id);
    }

    [Fact]
    public void Sas_IsFirst4BytesBigEndianMod10000_OfSha256WithLabel()
    {
        var hash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var digest = SHA256.HashData([.. Encoding.ASCII.GetBytes("WinDrop-SAS"), .. hash]);
        var expected = ((uint)digest[0] << 24 | (uint)digest[1] << 16 | (uint)digest[2] << 8 | digest[3]) % 10000;
        Assert.Equal(expected.ToString("D4"), Sas.Compute(hash));
        Assert.Equal(4, Sas.Compute(hash).Length);
    }

    [Fact]
    public void Marker_HasWdPrefix_AsciiLabel_AndFitsAdvertisement()
    {
        var m = ProtocolIds.BuildMarker("Efe'nin Çok Uzun Bilgisayarı");
        Assert.Equal((byte)'W', m[0]);
        Assert.Equal((byte)'D', m[1]);
        Assert.Equal(1, m[2]);
        Assert.InRange(m.Length, 4, 19);
        var label = Encoding.ASCII.GetString(m[3..]);
        Assert.StartsWith("WinDrop-", label);
        Assert.Equal("WinDrop-Efe", Encoding.ASCII.GetString(ProtocolIds.BuildMarker("Efe")[3..]));
        Assert.StartsWith("WinDrop-PC", Encoding.ASCII.GetString(ProtocolIds.BuildMarker("ÇÖ")[3..]));
    }
}

public class TrustedDevicesFamiliarTests
{
    [Fact]
    public void Familiar_IsPersisted_AndPairedIsNotDowngraded()
    {
        using var dir = new TempDir();
        var key = new byte[32];
        key[0] = 7;
        var store = new TrustedDevices(dir.Path);
        store.Add(key, "A", familiar: true);
        Assert.True(new TrustedDevices(dir.Path).FindByPublicKey(key)!.IsFamiliar);

        store.Add(key, "A", familiar: false);   // QR ile eşleşti: tanıdık → eşli
        Assert.False(new TrustedDevices(dir.Path).FindByPublicKey(key)!.IsFamiliar);

        store.Add(key, "A", familiar: true);    // sonradan XX ile gelmesi eşliği düşürmez
        Assert.False(new TrustedDevices(dir.Path).FindByPublicKey(key)!.IsFamiliar);
    }
}
