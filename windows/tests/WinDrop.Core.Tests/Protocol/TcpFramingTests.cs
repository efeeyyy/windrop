using WinDrop.Core.Protocol;
using WinDrop.Core.Tests.Support;
using Xunit;

namespace WinDrop.Core.Tests.Protocol;

public sealed class TcpFramingTests
{
    [Fact]
    public async Task Write_PrependsBigEndianLength()
    {
        var ms = new MemoryStream();
        await TcpFraming.WriteMessageAsync(ms, new byte[] { 0xAA, 0xBB, 0xCC });
        Assert.Equal(new byte[] { 0x00, 0x03, 0xAA, 0xBB, 0xCC }, ms.ToArray());

        var big = new MemoryStream();
        await TcpFraming.WriteMessageAsync(big, new byte[0x0102]);
        Assert.Equal(new byte[] { 0x01, 0x02 }, big.ToArray()[..2]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public async Task RoundTrip_AtLimits(int length)
    {
        var payload = new byte[length];
        Random.Shared.NextBytes(payload);
        var ms = new MemoryStream();
        await TcpFraming.WriteMessageAsync(ms, payload);
        ms.Position = 0;
        Assert.Equal(payload, await TcpFraming.ReadMessageAsync(ms));
        Assert.Null(await TcpFraming.ReadMessageAsync(ms));        // sınırda temiz EOF
    }

    [Fact]
    public async Task Write_RejectsEmptyAndOversize()
    {
        await Assert.ThrowsAsync<ProtocolException>(async () => await TcpFraming.WriteMessageAsync(new MemoryStream(), ReadOnlyMemory<byte>.Empty));
        await Assert.ThrowsAsync<ProtocolException>(async () => await TcpFraming.WriteMessageAsync(new MemoryStream(), new byte[65536]));
    }

    [Fact]
    public async Task Read_ZeroLengthFrame_Throws()
    {
        var ms = new MemoryStream([0x00, 0x00]);
        await Assert.ThrowsAsync<ProtocolException>(async () => await TcpFraming.ReadMessageAsync(ms));
    }

    [Fact]
    public async Task Read_TruncatedHeaderOrBody_Throws()
    {
        await Assert.ThrowsAsync<ProtocolException>(async () => await TcpFraming.ReadMessageAsync(new MemoryStream([0x00])));
        await Assert.ThrowsAsync<ProtocolException>(async () => await TcpFraming.ReadMessageAsync(new MemoryStream([0x00, 0x05, 1, 2, 3])));
    }

    [Fact]
    public async Task Read_HandlesShortReads_OneByteAtATime()
    {
        var (a, b) = ChannelStream.CreatePair(maxChunk: 1);
        var m1 = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var m2 = new byte[] { 9, 8, 7 };
        await TcpFraming.WriteMessageAsync(a, m1);
        await TcpFraming.WriteMessageAsync(a, m2);
        a.CompleteWriting();

        Assert.Equal(m1, await TcpFraming.ReadMessageAsync(b));
        Assert.Equal(m2, await TcpFraming.ReadMessageAsync(b));
        Assert.Null(await TcpFraming.ReadMessageAsync(b));
    }

    [Fact]
    public async Task Read_HonorsCancellation()
    {
        var (_, b) = ChannelStream.CreatePair();
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await TcpFraming.ReadMessageAsync(b, cts.Token));
    }

    [Fact]
    public async Task ReadExact_ThrowsOnEarlyEof()
    {
        await Assert.ThrowsAsync<ProtocolException>(async () => await TcpFraming.ReadExactAsync(new MemoryStream(new byte[19]), 20));
        Assert.Equal(20, (await TcpFraming.ReadExactAsync(new MemoryStream(new byte[20]), 20)).Length);
    }
}
