using WinDrop.Core.Protocol;
using Xunit;

namespace WinDrop.Core.Tests.Protocol;

public sealed class BleFragmenterTests
{
    private static byte[] Message(int length)
    {
        var m = new byte[length];
        new Random(length).NextBytes(m);
        return m;
    }

    [Theory]
    [InlineData(23, 19)]     // ATT yükü 20 − 1 başlık
    [InlineData(185, 181)]
    [InlineData(527, 523)]
    public void MaxFragmentData_FollowsMtu(int mtu, int expected) =>
        Assert.Equal(expected, BleFragmenter.MaxFragmentData(mtu));

    [Theory]
    [InlineData(23, 1)]
    [InlineData(23, 19)]
    [InlineData(23, 20)]
    [InlineData(23, 8192)]
    [InlineData(185, 181)]
    [InlineData(185, 182)]
    [InlineData(185, 8192)]
    [InlineData(527, 523)]
    [InlineData(527, 524)]
    [InlineData(527, 8192)]
    public void Fragment_Then_Reassemble_RoundTrips_AndRespectsMtu(int mtu, int length)
    {
        var message = Message(length);
        var fragments = BleFragmenter.Fragment(message, mtu, sequence: 5);

        Assert.Equal((length + BleFragmenter.MaxFragmentData(mtu) - 1) / BleFragmenter.MaxFragmentData(mtu), fragments.Count);
        Assert.All(fragments, f => Assert.InRange(f.Length, 2, mtu - 3));          // GATT yükü ≤ MTU − 3
        Assert.All(fragments, f => Assert.Equal(5, f[0] & 0x7F));                  // mesaj sıra no her parçada
        Assert.All(fragments.Take(fragments.Count - 1), f => Assert.Equal(0, f[0] & 0x80));
        Assert.Equal(0x80, fragments[^1][0] & 0x80);                               // yalnız sonuncu "son parça"
        Assert.Equal(message, BleFragmenter.Reassemble(fragments, expectedSequence: 5));
    }

    [Fact]
    public void Fragment_Rejects_Empty_Oversize_AndSmallMtu()
    {
        Assert.Throws<ProtocolException>(() => BleFragmenter.Fragment([], 185, 0));
        Assert.Throws<ProtocolException>(() => BleFragmenter.Fragment(new byte[8193], 185, 0));
        Assert.Equal(8192, BleFragmenter.Reassemble(BleFragmenter.Fragment(new byte[8192], 185, 0)).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => BleFragmenter.Fragment(new byte[3], 22, 0));
    }

    [Fact]
    public void Sequence_WrapsModulo128()
    {
        var f = BleFragmenter.Fragment(new byte[] { 1 }, 23, 130);
        Assert.Equal(0x80 | 2, f[0][0]);

        var r = new BleReassembler();
        for (var i = 0; i < 300; i++)
        {
            Assert.Equal(i % 128, r.ExpectedSequence);
            var msg = r.Push(BleFragmenter.Fragment(new byte[] { (byte)i }, 23, i)[0]);
            Assert.Equal(new[] { (byte)i }, msg);
        }
    }

    [Fact]
    public void Reassembler_SequenceSkip_Throws_AndStaysFaulted()
    {
        var r = new BleReassembler();
        Assert.NotNull(r.Push(BleFragmenter.Fragment(new byte[] { 1 }, 23, 0)[0]));            // seq 0 tamam
        var ex = Assert.Throws<ProtocolException>(() => r.Push(BleFragmenter.Fragment(new byte[] { 2 }, 23, 2)[0]));   // 1 atlandı
        Assert.Contains("atlama", ex.Message);
        // Oturum düştü: doğru sıradaki parça bile kabul edilmez.
        Assert.Throws<ProtocolException>(() => r.Push(BleFragmenter.Fragment(new byte[] { 3 }, 23, 1)[0]));
    }

    [Fact]
    public void Reassembler_FirstMessageMustStartAtExpectedSequence()
    {
        Assert.Throws<ProtocolException>(() => new BleReassembler().Push(BleFragmenter.Fragment(new byte[] { 1 }, 23, 1)[0]));
        Assert.NotNull(new BleReassembler(firstSequence: 1).Push(BleFragmenter.Fragment(new byte[] { 1 }, 23, 1)[0]));
    }

    [Fact]
    public void Reassembler_RejectsSequenceChangeMidMessage()
    {
        var frags = BleFragmenter.Fragment(Message(60), 23, 0);     // 4 parça
        var r = new BleReassembler();
        Assert.Null(r.Push(frags[0]));
        var tampered = (byte[])frags[1].Clone();
        tampered[0] = (byte)((tampered[0] & 0x80) | 1);
        Assert.Throws<ProtocolException>(() => r.Push(tampered));
    }

    [Fact]
    public void Reassembler_RejectsEmptyFragment_AndOverlongMessage()
    {
        Assert.Throws<ProtocolException>(() => new BleReassembler().Push(new byte[] { 0x80 }));
        Assert.Throws<ProtocolException>(() => new BleReassembler().Push(ReadOnlySpan<byte>.Empty));

        var r = new BleReassembler();
        var chunk = new byte[1 + 1000];                              // seq 0, "son değil"
        for (var i = 0; i < 8; i++) Assert.Null(r.Push(chunk));      // 8000 bayt
        Assert.Throws<ProtocolException>(() => r.Push(chunk));       // 9000 > 8192
    }

    [Fact]
    public void Reassemble_MissingLastFragment_OrExtraFragment_Throws()
    {
        var frags = BleFragmenter.Fragment(Message(60), 23, 0);
        Assert.Throws<ProtocolException>(() => BleFragmenter.Reassemble(frags.Take(frags.Count - 1)));
        Assert.Throws<ProtocolException>(() => BleFragmenter.Reassemble(frags.Append(frags[0])));
        // Ortadan bir parça kaybı: sıra no ile değil uzunlukla anlaşılır (aynı mesaj no) → birleşir, Noise etiketi yakalar.
        var lost = frags.Where((_, i) => i != 1).ToList();
        Assert.Equal(60 - 19, BleFragmenter.Reassemble(lost).Length);
    }
}
