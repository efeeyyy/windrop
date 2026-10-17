using System.Threading.Channels;

namespace WinDrop.Core.Tests.Support;

/// <summary>
/// Bellek içi çift yönlü akış (TCP benzeri). <c>maxChunk</c> verilirse yazılan veri en çok o kadar baytlık
/// parçalara bölünür; okuyan taraf kısa okumalarla karşılaşır (TCP parçalanmasını taklit eder).
/// </summary>
public sealed class ChannelStream : Stream
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;
    private readonly int _maxChunk;
    private byte[] _left = [];
    private int _leftPos;

    private ChannelStream(Channel<byte[]> incoming, Channel<byte[]> outgoing, int maxChunk)
    {
        _incoming = incoming;
        _outgoing = outgoing;
        _maxChunk = maxChunk;
    }

    /// <summary>Birbirine bağlı iki uç: A'nın yazdığı B'de okunur ve tersi.</summary>
    public static (ChannelStream A, ChannelStream B) CreatePair(int maxChunk = int.MaxValue)
    {
        var ab = Channel.CreateUnbounded<byte[]>();
        var ba = Channel.CreateUnbounded<byte[]>();
        return (new ChannelStream(ba, ab, maxChunk), new ChannelStream(ab, ba, maxChunk));
    }

    /// <summary>Yazmayı kapatır; karşı uç kalan veriyi okuyup sonra 0 (EOF) görür.</summary>
    public void CompleteWriting() => _outgoing.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        if (_leftPos >= _left.Length)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (!_incoming.Reader.TryRead(out var next)) return 0;
            _left = next;
            _leftPos = 0;
        }
        var n = Math.Min(buffer.Length, _left.Length - _leftPos);
        _left.AsMemory(_leftPos, n).CopyTo(buffer);
        _leftPos += n;
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        for (var pos = 0; pos < buffer.Length; pos += _maxChunk)
        {
            var part = buffer.Slice(pos, Math.Min(_maxChunk, buffer.Length - pos));
            await _outgoing.Writer.WriteAsync(part.ToArray(), cancellationToken);
        }
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
