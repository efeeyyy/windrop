using System.Security.Cryptography;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Core.Tests.Support;
using Xunit;

namespace WinDrop.Core.Tests.Session;

public sealed class SecureChannelTests
{
    [Fact]
    public async Task Ik_FramesAndSignals_FlowBothWays_WithShortReads()
    {
        using var rig = new Rig();
        using var link = await rig.ConnectAsync(maxChunk: 5);

        Assert.True(link.Phone.IsInitiator);
        Assert.Equal(rig.PcKey.PublicKey, link.Phone.RemoteStaticPublicKey);
        Assert.Equal(rig.PhoneKey.PublicKey, link.Pc.RemoteStaticPublicKey);
        Assert.Equal(link.Phone.HandshakeHash, link.Pc.HandshakeHash);
        Assert.Equal(rig.PhoneId, DeviceId.ToHex(link.Pc.RemoteDeviceId));

        await link.Phone.SendFrameAsync(FrameCodec.EncodeData([1, 2, 3]));
        var f = await link.Pc.ReceiveFrameAsync();
        Assert.Equal(FrameType.Data, f!.Value.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, f.Value.Body.ToArray());

        var offer = new OfferMessage(rig.TransferId, 2, 10, ["a", "b"]);
        await link.Pc.SendSignalAsync(offer);
        Assert.Equal(offer.TransferId, ((OfferMessage)(await link.Phone.ReceiveSignalAsync())!).TransferId);

        // Sinyal ve çerçeve aynı kanalda ayrışır ('{' ≠ 1–8).
        await link.Phone.SendSignalAsync(new CancelSignalMessage(rig.TransferId));
        await link.Phone.SendFrameAsync(FrameCodec.Encode(new AckBody(1, 2)));
        var m1 = await link.Pc.ReceiveAsync();
        var m2 = await link.Pc.ReceiveAsync();
        Assert.IsType<CancelSignalMessage>(m1!.Signal);
        Assert.Equal(FrameType.Ack, m2!.Frame!.Value.Type);
    }

    [Fact]
    public async Task Responder_IsNotEstablished_UntilFirstValidTransportMessage()
    {
        using var rig = new Rig();
        using var link = await rig.ConnectAsync();

        Assert.True(link.Phone.IsEstablished);
        Assert.False(link.Pc.IsEstablished);                                       // msg1 yeniden oynatılabilir
        Assert.Throws<InvalidOperationException>(() => link.Pc.SendFrameAsync(FrameCodec.EncodeData([1])).AsTask().GetAwaiter().GetResult());

        await link.Pc.EnsureEstablishedAsync();                                    // otomatik onay mesajını okur
        Assert.True(link.Pc.IsEstablished);
        await link.Pc.SendFrameAsync(FrameCodec.EncodeData([9]));                  // artık gönderebilir
        Assert.Equal(new byte[] { 9 }, (await link.Phone.ReceiveFrameAsync())!.Value.Body.ToArray());
    }

    [Fact]
    public async Task Responder_EnsureEstablished_KeepsFirstRealMessage()
    {
        using var rig = new Rig();
        using var link = await rig.ConnectAsync();
        // Onay mesajını tüketmeden başlatan gerçek çerçeve de yollasın: yanıtlayan ikisini sırayla görür.
        await link.Phone.SendFrameAsync(FrameCodec.EncodeData([7]));
        await link.Pc.EnsureEstablishedAsync();
        Assert.True(link.Pc.IsEstablished);
        var f = await link.Pc.ReceiveFrameAsync();
        Assert.Equal(new byte[] { 7 }, f!.Value.Body.ToArray());                  // onay yutuldu, veri kayıpsız
    }

    [Fact]
    public async Task ReplayedFirstMessage_NeverEstablishesSession()
    {
        using var rig = new Rig();
        var (a, b) = ChannelStream.CreatePair();
        var prefix = rig.TcpPrefix;

        // Saldırgan kaydedilmiş prefix+msg1'i yeni bir yanıtlayana oynatır.
        using var legit = new HandshakeState(NoisePattern.IK, true, prefix.Bytes, rig.PhoneKey, rig.PcKey.PublicKey);
        var msg1 = legit.WriteMessage([]);
        await a.WriteAsync(prefix.Bytes);
        await TcpFraming.WriteMessageAsync(a, msg1);

        using var pc = await SecureChannel.AcceptIkAsync(new TcpMessageTransport(b), rig.PcKey, _ => true);
        Assert.False(pc.IsEstablished);

        // Saldırgan msg2'yi çözemez, geçerli taşıma mesajı üretemez: sahte mesaj oturumu kurmaz.
        await TcpFraming.WriteMessageAsync(a, RandomNumberGenerator.GetBytes(40));
        await Assert.ThrowsAsync<NoiseException>(async () => await pc.EnsureEstablishedAsync());
        Assert.False(pc.IsEstablished);
    }

    [Fact]
    public async Task Ik_UntrustedInitiator_IsRejected_BeforeMsg2()
    {
        using var rig = new Rig();
        var (a, b) = ChannelStream.CreatePair();
        var phone = SecureChannel.ConnectIkAsync(new TcpMessageTransport(a), rig.TcpPrefix, rig.PhoneKey, rig.PcKey.PublicKey);

        var ex = await Assert.ThrowsAsync<PeerNotTrustedException>(() =>
            SecureChannel.AcceptIkAsync(new TcpMessageTransport(b), rig.PcKey, _ => false));
        Assert.Equal(rig.PhoneKey.PublicKey, ex.RemotePublicKey);

        // PC msg2 göndermeden keser; başlatan EOF görür.
        b.CompleteWriting();
        await Assert.ThrowsAsync<ProtocolException>(() => phone);
    }

    [Fact]
    public async Task Ik_WrongPcKey_Fails()
    {
        using var rig = new Rig();
        using var other = NoiseKeyPair.Generate();
        var (a, b) = ChannelStream.CreatePair();
        var phone = SecureChannel.ConnectIkAsync(new TcpMessageTransport(a), rig.TcpPrefix, rig.PhoneKey, other.PublicKey);
        var ex = await Assert.ThrowsAsync<NoiseException>(() =>
            SecureChannel.AcceptIkAsync(new TcpMessageTransport(b), rig.PcKey, _ => true));
        Assert.Equal(NoiseErrorKind.AuthenticationFailed, ex.Kind);
        b.CompleteWriting();
        await Assert.ThrowsAnyAsync<Exception>(() => phone);
    }

    [Fact]
    public async Task Ik_DifferentPrologue_Fails_ViaTamperedTransferId()
    {
        using var rig = new Rig();
        var (a, b) = ChannelStream.CreatePair();
        var phone = SecureChannel.ConnectIkAsync(new TcpMessageTransport(a), rig.TcpPrefix, rig.PhoneKey, rig.PcKey.PublicKey);

        // Ortadaki saldırgan: ön ekteki transferId'yi değiştirir (msg1 prologue ile bağlıdır).
        var prefix = await TcpFraming.ReadExactAsync(b, Prefix.Length);
        var msg1 = await TcpFraming.ReadMessageAsync(b);
        prefix[19] ^= 0xFF;
        var (c, d) = ChannelStream.CreatePair();
        await c.WriteAsync(prefix);
        await TcpFraming.WriteMessageAsync(c, msg1!);
        var ex = await Assert.ThrowsAsync<NoiseException>(() =>
            SecureChannel.AcceptIkAsync(new TcpMessageTransport(d), rig.PcKey, _ => true));
        Assert.Equal(NoiseErrorKind.AuthenticationFailed, ex.Kind);
        b.CompleteWriting();
        await Assert.ThrowsAnyAsync<Exception>(() => phone);
    }

    [Fact]
    public async Task Pairing_XXpsk3_Succeeds_AndVerifiesPcKey()
    {
        using var rig = new Rig();
        var psk = RandomNumberGenerator.GetBytes(32);
        var (a, b) = ChannelStream.CreatePair();
        var prefix = rig.TcpPrefix;

        var phoneT = SecureChannel.ConnectPairingAsync(new TcpMessageTransport(a), prefix, rig.PhoneKey, psk,
            k => k.Span.SequenceEqual(rig.PcKey.PublicKey));      // QR'daki k ile aynı mı?
        var pcT = SecureChannel.AcceptPairingAsync(new TcpMessageTransport(b), rig.PcKey, psk);
        await Task.WhenAll(phoneT, pcT);
        using var phone = await phoneT;
        using var pc = await pcT;

        Assert.True(pc.IsEstablished);                             // msg3 başlatandan, psk'lı ve taze
        Assert.Equal(rig.PhoneKey.PublicKey, pc.RemoteStaticPublicKey);
        await phone.SendFrameAsync(FrameCodec.EncodeData([5]));
        Assert.Equal(new byte[] { 5 }, (await pc.ReceiveFrameAsync())!.Value.Body.ToArray());
    }

    [Fact]
    public async Task Pairing_WrongPsk_FailsOnPc_AndWrongQrKey_StopsPhoneBeforeMsg3()
    {
        using var rig = new Rig();
        var (a, b) = ChannelStream.CreatePair();
        var phoneT = SecureChannel.ConnectPairingAsync(new TcpMessageTransport(a), rig.TcpPrefix, rig.PhoneKey,
            RandomNumberGenerator.GetBytes(32), _ => true);
        var ex = await Assert.ThrowsAsync<NoiseException>(() =>
            SecureChannel.AcceptPairingAsync(new TcpMessageTransport(b), rig.PcKey, RandomNumberGenerator.GetBytes(32)));
        Assert.Equal(NoiseErrorKind.AuthenticationFailed, ex.Kind);
        await phoneT;   // telefon msg3'ü gönderdi, kendi tarafında hata yok

        var (c, d) = ChannelStream.CreatePair();
        var psk = RandomNumberGenerator.GetBytes(32);
        var phone2 = SecureChannel.ConnectPairingAsync(new TcpMessageTransport(c), rig.TcpPrefix, rig.PhoneKey, psk, _ => false);
        var pc2 = SecureChannel.AcceptPairingAsync(new TcpMessageTransport(d), rig.PcKey, psk);
        await Assert.ThrowsAsync<PeerNotTrustedException>(() => phone2);   // QR'daki k ile uyuşmadı: iptal
        c.CompleteWriting();
        await Assert.ThrowsAnyAsync<Exception>(() => pc2);                 // msg3 gelmedi
    }

    [Fact]
    public async Task VersionMismatch_Tcp_ResponderRepliesAndBothSidesThrow()
    {
        using var rig = new Rig();
        var (a, b) = ChannelStream.CreatePair();
        var future = new Prefix(9, TransportKind.Tcp, rig.TransferId.Span);
        var phone = SecureChannel.ConnectIkAsync(new TcpMessageTransport(a), future, rig.PhoneKey, rig.PcKey.PublicKey);

        var pcEx = await Assert.ThrowsAsync<VersionMismatchException>(() =>
            SecureChannel.AcceptIkAsync(new TcpMessageTransport(b), rig.PcKey, _ => true));
        Assert.Equal(Prefix.CurrentVersion, pcEx.LocalVersion);
        Assert.Equal(9, pcEx.PeerVersion);

        var phoneEx = await Assert.ThrowsAsync<VersionMismatchException>(() => phone);
        Assert.Equal(9, phoneEx.LocalVersion);
        Assert.Equal(Prefix.CurrentVersion, phoneEx.PeerVersion);
    }

    [Fact]
    public async Task PrefixTransportKind_MustMatchConnection()
    {
        using var rig = new Rig();
        var (a, b) = ChannelStream.CreatePair();
        var relayPrefix = Prefix.Create(TransportKind.Relay, rig.TransferId);
        await a.WriteAsync(relayPrefix.Bytes);
        await Assert.ThrowsAsync<ProtocolException>(() =>
            SecureChannel.AcceptIkAsync(new TcpMessageTransport(b), rig.PcKey, _ => true));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            SecureChannel.ConnectIkAsync(new TcpMessageTransport(a), relayPrefix, rig.PhoneKey, rig.PcKey.PublicKey));
    }

    [Theory]
    [InlineData(23)]
    [InlineData(185)]
    [InlineData(527)]
    public async Task Ble_Session_FramesFragmentedAndReassembled(int mtu)
    {
        using var rig = new Rig();
        var (phoneLink, pcLink) = MemoryBleLink.CreatePair(mtu);
        var prefix = Prefix.CreateBle();

        var phoneT = SecureChannel.ConnectIkAsync(new BleMessageTransport(phoneLink), prefix, rig.PhoneKey, rig.PcKey.PublicKey);
        var pcT = SecureChannel.AcceptIkAsync(new BleMessageTransport(pcLink), rig.PcKey, _ => true);
        await Task.WhenAll(phoneT, pcT);
        using var phone = await phoneT;
        using var pc = await pcT;

        Assert.Equal(8192 - 16, phone.MaxPlaintextSize);
        Assert.Equal(20, phoneLink.SentSizes[0]);              // ön ek tek, parçalanmamış yazma
        Assert.All(phoneLink.SentSizes, s => Assert.InRange(s, 1, mtu - 3));

        var data = TestData.Random(phone.MaxFrameBodySize);    // en büyük DATA gövdesi (8175)
        await phone.SendFrameAsync(FrameCodec.EncodeData(data));
        var f = await pc.ReceiveFrameAsync();
        Assert.Equal(data, f!.Value.Body.ToArray());

        var hello = new HelloMessage(1, "PC", ["tcp", "relay"], null, false);
        await pc.EnsureEstablishedAsync();
        await pc.SendSignalAsync(hello);
        var got = (HelloMessage)(await phone.ReceiveSignalAsync())!;
        Assert.Equal("PC", got.Name);
        Assert.Null(got.Wifi);

        await Assert.ThrowsAsync<ProtocolException>(async () =>
            await phone.SendFrameAsync(FrameCodec.Encode(FrameType.Data, new byte[phone.MaxPlaintextSize])));   // 8176 + tür baytı
    }

    [Fact]
    public async Task Ble_VersionMismatch_ReplyIsRecognised()
    {
        using var rig = new Rig();
        var (phoneLink, pcLink) = MemoryBleLink.CreatePair(185);
        var future = new Prefix(4, TransportKind.Ble, new byte[16]);
        var phone = SecureChannel.ConnectIkAsync(new BleMessageTransport(phoneLink), future, rig.PhoneKey, rig.PcKey.PublicKey);
        await Assert.ThrowsAsync<VersionMismatchException>(() =>
            SecureChannel.AcceptIkAsync(new BleMessageTransport(pcLink), rig.PcKey, _ => true));
        var ex = await Assert.ThrowsAsync<VersionMismatchException>(() => phone);
        Assert.Equal(Prefix.CurrentVersion, ex.PeerVersion);
    }

    [Fact]
    public async Task Ble_SequenceSkip_DropsSession()
    {
        using var rig = new Rig();
        var (phoneLink, pcLink) = MemoryBleLink.CreatePair(185);
        var phoneT = SecureChannel.ConnectIkAsync(new BleMessageTransport(phoneLink), Prefix.CreateBle(), rig.PhoneKey, rig.PcKey.PublicKey);
        var pcT = SecureChannel.AcceptIkAsync(new BleMessageTransport(pcLink), rig.PcKey, _ => true);
        await Task.WhenAll(phoneT, pcT);
        using var phone = await phoneT;
        using var pc = await pcT;

        await pc.EnsureEstablishedAsync();       // msg1 = sıra 0, onay mesajı = sıra 1
        // Araya sıra no 5 (beklenen: 2) olan bir mesaj sokulur → oturum düşer.
        var forged = BleFragmenter.Fragment(RandomNumberGenerator.GetBytes(40), 185, 5)[0];
        await phoneLink.SendAsync(forged, default);
        var ex = await Assert.ThrowsAsync<ProtocolException>(async () => await pc.ReceiveAsync());
        Assert.Contains("atlama", ex.Message);
    }
}
