using WinDrop.Core.Protocol;
using Xunit;

namespace WinDrop.Core.Tests.Protocol;

public sealed class PrefixAndFramesTests
{
    private static readonly byte[] Id = Enumerable.Range(0xA0, 16).Select(i => (byte)i).ToArray();

    // ---- Prefix ----

    [Fact]
    public void Prefix_Encode_Layout_And_RoundTrip()
    {
        var p = Prefix.Create(TransportKind.Tcp, Id);
        var bytes = p.Bytes;
        Assert.Equal(20, bytes.Length);
        Assert.Equal((byte)'W', bytes[0]);
        Assert.Equal((byte)'D', bytes[1]);
        Assert.Equal(Prefix.CurrentVersion, bytes[2]);
        Assert.Equal(2, bytes[3]);
        Assert.Equal(Id, bytes[4..]);

        var back = Prefix.Decode(bytes);
        Assert.Equal(p, back);
        Assert.Equal(TransportKind.Tcp, back.Transport);
        Assert.Equal(Id, back.TransferId);
    }

    [Fact]
    public void Prefix_Ble_RequiresZeroTransferId()
    {
        var ble = Prefix.CreateBle();
        Assert.Equal(1, ble.Bytes[3]);
        Assert.All(ble.Bytes[4..], b => Assert.Equal(0, b));
        Assert.Throws<ArgumentException>(() => Prefix.Create(TransportKind.Ble, Id));

        var forged = Prefix.Create(TransportKind.Relay, Id).Bytes;
        forged[3] = 1;                                   // BLE olarak işaretle ama transferId sıfır değil
        Assert.Throws<ProtocolException>(() => Prefix.Decode(forged));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(21)]
    public void Prefix_Decode_WrongLength_Throws(int length) =>
        Assert.Throws<ProtocolException>(() => Prefix.Decode(new byte[length]));

    [Fact]
    public void Prefix_Decode_BadMagicOrTransport_Throws()
    {
        var good = Prefix.Create(TransportKind.Tcp, Id).Bytes;
        var badMagic = (byte[])good.Clone();
        badMagic[0] = (byte)'X';
        Assert.Throws<ProtocolException>(() => Prefix.Decode(badMagic));
        Assert.False(Prefix.TryDecode(badMagic, out _));

        foreach (byte t in new byte[] { 0, 4, 255 })
        {
            var bad = (byte[])good.Clone();
            bad[3] = t;
            Assert.Throws<ProtocolException>(() => Prefix.Decode(bad));
        }
    }

    [Fact]
    public void Prefix_Decode_DoesNotCheckVersion_ButReportsIt()
    {
        var future = new Prefix(7, TransportKind.Tcp, Id);
        var decoded = Prefix.Decode(future.Bytes);
        Assert.Equal(7, decoded.Version);
        Assert.False(decoded.IsVersionSupported(Prefix.CurrentVersion));
        Assert.True(decoded.IsVersionSupported(7));
    }

    [Fact]
    public void Prefix_VersionMismatchReply_RoundTrips()
    {
        var reply = Prefix.EncodeVersionMismatch(3);
        Assert.Equal(new byte[] { 0xFF, 3 }, reply);
        Assert.True(Prefix.TryDecodeVersionMismatch(reply, out var v));
        Assert.Equal(3, v);
        Assert.False(Prefix.TryDecodeVersionMismatch([0x00, 0x30], out _));   // TCP uzunluk öneki, sürüm yanıtı değil
        Assert.False(Prefix.TryDecodeVersionMismatch([0xFF], out _));
    }

    // ---- FrameCodec ----

    [Fact]
    public void FrameCodec_TypeByteAndBodyLimits()
    {
        var data = FrameCodec.EncodeData(new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { (byte)FrameType.Data, 1, 2, 3 }, data);

        Assert.Equal(65519, FrameCodec.Encode(FrameType.Data, new byte[65518]).Length);   // sınırda geçer
        Assert.Throws<ProtocolException>(() => FrameCodec.Encode(FrameType.Data, new byte[65519]));

        var frame = FrameCodec.Decode(FrameCodec.Encode(FrameType.Data, new byte[65518]));
        Assert.Equal(FrameType.Data, frame.Type);
        Assert.Equal(65518, frame.Body.Length);
    }

    [Fact]
    public void FrameCodec_Decode_RejectsEmptyUnknownAndOversize()
    {
        Assert.Throws<ProtocolException>(() => FrameCodec.Decode(ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ProtocolException>(() => FrameCodec.Decode(new byte[] { 0, 1 }));
        Assert.Throws<ProtocolException>(() => FrameCodec.Decode(new byte[] { 99, 1 }));
        var big = new byte[65520];
        big[0] = (byte)FrameType.Data;
        Assert.Throws<ProtocolException>(() => FrameCodec.Decode(big));
    }

    [Fact]
    public void FrameCodec_ControlBodies_RoundTrip_WithSpecFieldNames()
    {
        var manifest = new ManifestBody([new ManifestFile(1, "a.txt", 10, 1_700_000_000_123), new ManifestFile(2, "b ü.bin", 0, 5)]);
        var enc = FrameCodec.Encode(manifest);
        Assert.Equal((byte)FrameType.Manifest, enc[0]);
        var json = System.Text.Encoding.UTF8.GetString(enc, 1, enc.Length - 1);
        Assert.Contains("\"files\"", json);
        Assert.Contains("\"mtime\"", json);
        var back = FrameCodec.DecodeManifest(enc.AsSpan(1));
        Assert.Equal(2, back.Files.Count);
        Assert.Equal(new ManifestFile(2, "b ü.bin", 0, 5), back.Files[1]);

        Assert.Equal(new ResumeBody(3, 4096), FrameCodec.DecodeResume(FrameCodec.Encode(new ResumeBody(3, 4096)).AsSpan(1)));
        Assert.Equal(new FileStartBody(3, 4096), FrameCodec.DecodeFileStart(FrameCodec.Encode(new FileStartBody(3, 4096)).AsSpan(1)));
        Assert.Equal(new FileEndBody(3, "ab"), FrameCodec.DecodeFileEnd(FrameCodec.Encode(new FileEndBody(3, "ab")).AsSpan(1)));
        Assert.Equal(new AckBody(3, 7), FrameCodec.DecodeAck(FrameCodec.Encode(new AckBody(3, 7)).AsSpan(1)));
        var done = FrameCodec.DecodeDone(FrameCodec.Encode(new DoneBody(false, [2, 5])).AsSpan(1));
        Assert.False(done.Ok);
        Assert.Equal(new[] { 2, 5 }, done.Failed);
        Assert.Null(FrameCodec.DecodeCancel("{}"u8).Reason);
        Assert.Equal((byte)FrameType.Cancel, FrameCodec.Encode(new CancelBody())[0]);
    }

    [Fact]
    public void FrameCodec_Json_IgnoresUnknownFields_RejectsMissingAndGarbage()
    {
        var ack = FrameCodec.DecodeAck("""{"id":1,"offset":2,"extra":true}"""u8);
        Assert.Equal(new AckBody(1, 2), ack);
        Assert.Throws<ProtocolException>(() => FrameCodec.DecodeAck("""{"id":1}"""u8));
        Assert.Throws<ProtocolException>(() => FrameCodec.DecodeAck("not json"u8));
        Assert.Throws<ProtocolException>(() => FrameCodec.DecodeAck("null"u8));
    }
}
