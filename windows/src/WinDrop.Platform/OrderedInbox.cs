using System.Threading.Channels;

namespace WinDrop.Platform;

/// <summary>
/// Eşzamansız işlenen GATT yazmalarını geliş sırasıyla kuyruğa koyar: işleyici, <c>await</c>'ten önce eşzamanlı olarak
/// <see cref="Reserve"/> ile sıra alır; veri hazır olunca <see cref="Complete"/> çağrılır ve yalnız ardışık sıralar okuyucuya akar.
/// (WinRT <c>WriteRequested</c> işleyicileri <c>GetRequestAsync</c> beklerken örtüşebilir; parça sırası bozulmamalı.)
/// </summary>
internal sealed class OrderedInbox
{
    private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _lock = new();
    private readonly Dictionary<long, byte[]?> _done = [];
    private long _reserved;
    private long _next;

    public ChannelReader<byte[]> Reader => _channel.Reader;

    /// <summary>Eşzamanlı çağrılmalıdır (işleyicinin ilk <c>await</c>'inden önce).</summary>
    public long Reserve()
    {
        lock (_lock) return _reserved++;
    }

    /// <param name="data">null: yazma okunamadı, sıra atlanır.</param>
    public void Complete(long slot, byte[]? data)
    {
        lock (_lock)
        {
            _done[slot] = data;
            while (_done.Remove(_next, out var d))
            {
                if (d is not null) _channel.Writer.TryWrite(d);
                _next++;
            }
        }
    }

    public void Close() => _channel.Writer.TryComplete();
}
