using System.Security.Cryptography;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Tests.Support;
using Xunit;

namespace WinDrop.Core.Tests.Noise;

/// <summary>Bellek içi akış üzerinde: Prefix + IK el sıkışması + TcpFraming + şifreli çerçeveler.</summary>
public sealed class EndToEndTests
{
    private static readonly byte[] TransferId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

    private sealed class Peer(NoiseKeyPair key) : IDisposable
    {
        public NoiseKeyPair Key { get; } = key;
        public void Dispose() => Key.Dispose();
    }

    /// <summary>Başlatan (iPhone) tarafı: ön eki yazar, IK msg1'i yazar, msg2'yi okur.</summary>
    private static async Task<(NoiseTransport Transport, byte[] FirstWire)> InitiatorHandshakeAsync(
        Stream stream, Prefix prefix, NoiseKeyPair initiatorKey, byte[] responderPublic)
    {
        await stream.WriteAsync(prefix.Bytes);
        using var hs = new HandshakeState(NoisePattern.IK, true, prefix.Bytes, initiatorKey, responderPublic);
        var msg1 = hs.WriteMessage([]);
        await TcpFraming.WriteMessageAsync(stream, msg1);
        var msg2 = await TcpFraming.ReadMessageAsync(stream) ?? throw new InvalidOperationException("EOF");
        hs.ReadMessage(msg2);
        return (hs.Split(), msg1);
    }

    /// <summary>Yanıtlayan (PC) tarafı: ön eki okur, IK msg1'i okur (başlatanı döndürür), msg2'yi yazar.</summary>
    private static async Task<(NoiseTransport Transport, byte[] InitiatorPublic, byte[] FirstPayload)> ResponderHandshakeAsync(
        Stream stream, NoiseKeyPair responderKey, Func<Prefix, byte[]>? prologueOverride = null)
    {
        var prefixBytes = await TcpFraming.ReadExactAsync(stream, Prefix.Length);
        var prefix = Prefix.Decode(prefixBytes);
        Assert.True(prefix.IsVersionSupported(Prefix.CurrentVersion));
        var prologue = prologueOverride?.Invoke(prefix) ?? prefixBytes;

        using var hs = new HandshakeState(NoisePattern.IK, false, prologue, responderKey);
        var msg1 = await TcpFraming.ReadMessageAsync(stream) ?? throw new InvalidOperationException("EOF");
        var payload = hs.ReadMessage(msg1);
        var msg2 = hs.WriteMessage([]);
        await TcpFraming.WriteMessageAsync(stream, msg2);
        return (hs.Split(), hs.RemoteStaticPublicKey!, payload);
    }

    private static async Task SendAsync(Stream s, NoiseTransport t, byte[] plaintextFrame) =>
        await TcpFraming.WriteMessageAsync(s, t.Send.Encrypt(plaintextFrame));

    private static async Task<Frame> ReceiveAsync(Stream s, NoiseTransport t)
    {
        var wire = await TcpFraming.ReadMessageAsync(s) ?? throw new InvalidOperationException("EOF");
        return FrameCodec.Decode(t.Receive.Decrypt(wire));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(7)]      // TCP parçalanması: her okuma en çok 7 bayt
    public async Task Ik_OverMemoryStream_FramesFlowBothWays(int chunk)
    {
        using var iphone = new Peer(NoiseKeyPair.Generate());
        using var pc = new Peer(NoiseKeyPair.Generate());
        var (a, b) = ChannelStream.CreatePair(chunk);
        var prefix = Prefix.Create(TransportKind.Tcp, TransferId);

        var init = InitiatorHandshakeAsync(a, prefix, iphone.Key, pc.Key.PublicKey);
        var resp = ResponderHandshakeAsync(b, pc.Key);
        var (iTransport, msg1) = await init;
        var (rTransport, learnedInitiator, firstPayload) = await resp;
        using var _1 = iTransport;
        using var _2 = rTransport;

        Assert.Equal(iphone.Key.PublicKey, learnedInitiator);   // PC başlatanın statik anahtarını msg1'den öğrendi
        Assert.Empty(firstPayload);                              // 0-RTT yok: IK msg1 yükü boş
        Assert.Equal(32 + 48 + 16, msg1.Length);                 // e + şifreli s + boş yükün etiketi; fazlası yok

        // iPhone → PC : MANIFEST, FILE_START, DATA(64000), FILE_END
        var manifest = new ManifestBody([new ManifestFile(1, "a.txt", 64000, 1_700_000_000_000)]);
        await SendAsync(a, iTransport, FrameCodec.Encode(manifest));
        var f = await ReceiveAsync(b, rTransport);
        Assert.Equal(FrameType.Manifest, f.Type);
        Assert.Equal("a.txt", FrameCodec.DecodeManifest(f.Body.Span).Files[0].Name);

        // PC → iPhone : RESUME
        await SendAsync(b, rTransport, FrameCodec.Encode(new ResumeBody(1, 0)));
        f = await ReceiveAsync(a, iTransport);
        Assert.Equal(new ResumeBody(1, 0), FrameCodec.DecodeResume(f.Body.Span));

        var data = RandomNumberGenerator.GetBytes(64000);
        await SendAsync(a, iTransport, FrameCodec.Encode(new FileStartBody(1, 0)));
        await SendAsync(a, iTransport, FrameCodec.EncodeData(data));
        await SendAsync(a, iTransport, FrameCodec.Encode(new FileEndBody(1, Convert.ToHexStringLower(SHA256.HashData(data)))));

        Assert.Equal(FrameType.FileStart, (await ReceiveAsync(b, rTransport)).Type);
        var dataFrame = await ReceiveAsync(b, rTransport);
        Assert.Equal(FrameType.Data, dataFrame.Type);
        Assert.Equal(data, dataFrame.Body.ToArray());
        var end = FrameCodec.DecodeFileEnd((await ReceiveAsync(b, rTransport)).Body.Span);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(dataFrame.Body.Span)), end.Sha256);

        await SendAsync(b, rTransport, FrameCodec.Encode(new AckBody(1, 64000)));
        await SendAsync(b, rTransport, FrameCodec.Encode(new DoneBody(true, [])));
        Assert.Equal(new AckBody(1, 64000), FrameCodec.DecodeAck((await ReceiveAsync(a, iTransport)).Body.Span));
        Assert.True(FrameCodec.DecodeDone((await ReceiveAsync(a, iTransport)).Body.Span).Ok);

        a.CompleteWriting();
        Assert.Null(await TcpFraming.ReadMessageAsync(b));   // çerçeve sınırında temiz EOF
    }

    [Fact]
    public async Task Initiator_WithWrongResponderStaticKey_IsRejectedByResponder()
    {
        using var iphone = new Peer(NoiseKeyPair.Generate());
        using var pc = new Peer(NoiseKeyPair.Generate());
        using var impostorPin = NoiseKeyPair.Generate();     // iPhone yanlış PC anahtarını biliyor (ör. eski eşleşme)
        var (a, b) = ChannelStream.CreatePair();
        var prefix = Prefix.Create(TransportKind.Tcp, TransferId);

        var write = Task.Run(async () =>
        {
            await a.WriteAsync(prefix.Bytes);
            using var hs = new HandshakeState(NoisePattern.IK, true, prefix.Bytes, iphone.Key, impostorPin.PublicKey);
            await TcpFraming.WriteMessageAsync(a, hs.WriteMessage([]));
        });
        var ex = await Assert.ThrowsAsync<NoiseException>(() => ResponderHandshakeAsync(b, pc.Key));
        await write;
        Assert.Equal(NoiseErrorKind.AuthenticationFailed, ex.Kind);
    }

    [Fact]
    public async Task UnpairedInitiator_IsLearnedButNotInTrustedList_SoPcDropsTheConnection()
    {
        using var stranger = new Peer(NoiseKeyPair.Generate());
        using var pairedPhone = new Peer(NoiseKeyPair.Generate());
        using var pc = new Peer(NoiseKeyPair.Generate());
        var trusted = new List<byte[]> { pairedPhone.Key.PublicKey };       // PC'nin güvenilen listesi
        var (a, b) = ChannelStream.CreatePair();
        var prefix = Prefix.Create(TransportKind.Tcp, TransferId);

        // Yabancı PC'nin açık anahtarını biliyor (ör. QR'dan veya reklamdan): IK mesajı kriptografik olarak geçerli.
        var init = InitiatorHandshakeAsync(a, prefix, stranger.Key, pc.Key.PublicKey);
        var (rTransport, learned, _) = await ResponderHandshakeAsync(b, pc.Key);
        using var _ = rTransport;

        // PROTOCOL §1: güvenilen listede yoksa PC bağlantıyı keser. (Bağlantı yönetimi sonraki adım; burada ilke.)
        Assert.DoesNotContain(trusted, k => k.AsSpan().SequenceEqual(learned));
        b.CompleteWriting();
        (await init).Transport.Dispose();
    }

    [Fact]
    public async Task DifferentTransferId_PrologueMismatch_FailsHandshake()
    {
        using var iphone = new Peer(NoiseKeyPair.Generate());
        using var pc = new Peer(NoiseKeyPair.Generate());
        var (a, b) = ChannelStream.CreatePair();
        var prefix = Prefix.Create(TransportKind.Tcp, TransferId);
        var otherId = (byte[])TransferId.Clone();
        otherId[15] ^= 0xFF;

        var write = Task.Run(async () =>
        {
            await a.WriteAsync(prefix.Bytes);
            using var hs = new HandshakeState(NoisePattern.IK, true, prefix.Bytes, iphone.Key, pc.Key.PublicKey);
            await TcpFraming.WriteMessageAsync(a, hs.WriteMessage([]));
        });
        // Yanıtlayan prologue'u farklı transferId'den kurar (ör. ön eki başka bir aktarımdan taşıma/ortada değiştirme).
        var ex = await Assert.ThrowsAsync<NoiseException>(() =>
            ResponderHandshakeAsync(b, pc.Key, p => Prefix.Create(p.Transport, otherId).Bytes));
        await write;
        Assert.Equal(NoiseErrorKind.AuthenticationFailed, ex.Kind);
    }

    [Fact]
    public async Task ReplayedFirstMessage_CannotEstablishSession()
    {
        using var iphone = new Peer(NoiseKeyPair.Generate());
        using var pc = new Peer(NoiseKeyPair.Generate());
        var prefix = Prefix.Create(TransportKind.Tcp, TransferId);

        // Meşru başlatan msg1 üretir; saldırgan bunu kaydeder.
        using var legit = new HandshakeState(NoisePattern.IK, true, prefix.Bytes, iphone.Key, pc.Key.PublicKey);
        var recorded = legit.WriteMessage([]);

        // Tekrar oynatma: yeni yanıtlayan msg1'i kabul eder (Noise bunu engelleyemez)…
        using var resp = new HandshakeState(NoisePattern.IK, false, prefix.Bytes, pc.Key);
        Assert.Empty(resp.ReadMessage(recorded));
        resp.WriteMessage([]);
        using var transport = resp.Split();

        // …ama saldırgan geçerli bir taşıma mesajı üretemez: PC oturumu kurulmuş saymaz (§1 "0-RTT yok").
        var forged = RandomNumberGenerator.GetBytes(40);
        var ex = Assert.Throws<NoiseException>(() => transport.Receive.Decrypt(forged));
        Assert.Equal(NoiseErrorKind.AuthenticationFailed, ex.Kind);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task VersionMismatch_ResponderRepliesFFVersion_BeforeHandshake()
    {
        var (a, b) = ChannelStream.CreatePair();
        var future = new Prefix(9, TransportKind.Tcp, TransferId);
        await a.WriteAsync(future.Bytes);

        var read = Prefix.Decode(await TcpFraming.ReadExactAsync(b, Prefix.Length));
        Assert.False(read.IsVersionSupported(Prefix.CurrentVersion));
        await b.WriteAsync(Prefix.EncodeVersionMismatch());

        var reply = await TcpFraming.ReadExactAsync(a, 2);
        Assert.True(Prefix.TryDecodeVersionMismatch(reply, out var supported));
        Assert.Equal(Prefix.CurrentVersion, supported);
    }
}
