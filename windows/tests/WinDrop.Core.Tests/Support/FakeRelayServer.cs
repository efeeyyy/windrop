using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WinDrop.Core.Tests.Support;

/// <summary>Yerel Kestrel üstünde, istediği WebSocket davranışını yazabileceğin sunucu (ws://127.0.0.1:port).</summary>
internal sealed class LocalWsServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LocalWsServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    /// <summary><c>ws://127.0.0.1:port</c> (RelayUrl olarak verilebilir).</summary>
    public string Url => $"ws://127.0.0.1:{Port}";

    public static async Task<LocalWsServer> StartAsync(Func<HttpContext, Task> handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/{**path}", handler);
        await app.StartAsync();
        return new LocalWsServer(app, new Uri(app.Urls.First()).Port);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TimeSpan.FromSeconds(1));
        await _app.DisposeAsync();
    }
}

/// <summary>
/// relay/src/index.ts davranışının test sahtesi (PROTOCOL §7.4): <c>/v1/room/&lt;32 hex&gt;?role=pc|phone</c>; ilk pc + ilk phone eşlenir,
/// aynı rol ikinci kez 409, kapanmış oda 410; ikili mesajlar sırayla karşıya geçer, eş gelmeden önce gelenler tamponlanır,
/// bir taraf kapanınca öbürü de kapanır. <see cref="RejectStatus"/> verilirse bağlantı o HTTP durumuyla reddedilir (ör. 429).
/// </summary>
internal sealed class FakeRelayServer : IAsyncDisposable
{
    private static readonly Regex RoomPath = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly ConcurrentQueue<string> _roomIds = new();
    private LocalWsServer _server = null!;
    private long _forwardedBytes;
    private int _rejected;

    private FakeRelayServer() { }

    public static async Task<FakeRelayServer> StartAsync()
    {
        var relay = new FakeRelayServer();
        relay._server = await LocalWsServer.StartAsync(relay.HandleAsync);
        return relay;
    }

    public string Url => _server.Url;

    /// <summary>Açılış sırasıyla görülen oda kimlikleri.</summary>
    public IReadOnlyList<string> RoomIds => [.. _roomIds];

    public long ForwardedBytes => Interlocked.Read(ref _forwardedBytes);

    /// <summary><see cref="RejectStatus"/> ile reddedilen bağlantı sayısı.</summary>
    public int RejectedCount => Volatile.Read(ref _rejected);

    /// <summary>null değilse yeni bağlantılar bu HTTP durumuyla reddedilir.</summary>
    public int? RejectStatus { get; set; }

    /// <summary>Tüm odaların bütün bağlantılarını aniden keser (ağ kopması).</summary>
    public void DropAll()
    {
        foreach (var room in _rooms.Values) room.Kill(abort: true);
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        if (RejectStatus is { } status)
        {
            Interlocked.Increment(ref _rejected);
            ctx.Response.StatusCode = status;
            return;
        }
        var path = ctx.Request.Path.Value ?? "";
        var role = ctx.Request.Query["role"].ToString();
        const string prefix = "/v1/room/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !RoomPath.IsMatch(path[prefix.Length..]))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        if (role is not ("pc" or "phone") || !ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        var roomId = path[prefix.Length..];
        var room = _rooms.GetOrAdd(roomId, id => { _roomIds.Enqueue(id); return new Room(this); });
        switch (room.TryTake(role))
        {
            case TakeResult.Dead: ctx.Response.StatusCode = 410; return;
            case TakeResult.Taken: ctx.Response.StatusCode = 409; return;
        }
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        await room.RunAsync(role, socket);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var room in _rooms.Values) room.Kill(abort: true);
        await _server.DisposeAsync();
    }

    private enum TakeResult { Ok, Taken, Dead }

    private sealed class Room(FakeRelayServer owner)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, WebSocket> _sockets = [];
        private readonly HashSet<string> _taken = [];
        private readonly Dictionary<string, SemaphoreSlim> _send = new() { ["pc"] = new(1, 1), ["phone"] = new(1, 1) };
        private List<byte[]> _buffer = [];
        private bool _dead;

        public TakeResult TryTake(string role)
        {
            lock (_gate)
            {
                if (_dead) return TakeResult.Dead;
                return _taken.Add(role) ? TakeResult.Ok : TakeResult.Taken;
            }
        }

        public async Task RunAsync(string role, WebSocket socket)
        {
            var other = role == "pc" ? "phone" : "pc";
            try
            {
                // Eş gelmeden önce yazılanları (ön tampon) sırayla boşalt; sonra canlı iletime geç.
                await _send[role].WaitAsync();
                try
                {
                    while (true)
                    {
                        List<byte[]> pending;
                        lock (_gate)
                        {
                            pending = _buffer;
                            _buffer = [];
                            if (pending.Count == 0 || !_sockets.ContainsKey(other))
                            {
                                if (pending.Count > 0) _buffer = pending;   // eş yok: tutmaya devam
                                _sockets[role] = socket;
                                break;
                            }
                        }
                        foreach (var m in pending) await socket.SendAsync(m, WebSocketMessageType.Binary, true, CancellationToken.None);
                    }
                }
                finally { _send[role].Release(); }

                var buffer = new byte[70_000];
                while (true)
                {
                    var length = 0;
                    ValueWebSocketReceiveResult r;
                    do
                    {
                        r = await socket.ReceiveAsync(buffer.AsMemory(length), CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        if (r.MessageType == WebSocketMessageType.Text) return;
                        length += r.Count;
                    } while (!r.EndOfMessage);

                    var message = buffer.AsSpan(0, length).ToArray();
                    Interlocked.Add(ref owner._forwardedBytes, length);
                    WebSocket? dest;
                    lock (_gate)
                    {
                        _sockets.TryGetValue(other, out dest);
                        if (dest is null) _buffer.Add(message);
                    }
                    if (dest is null) continue;
                    await _send[other].WaitAsync();
                    try { await dest.SendAsync(message, WebSocketMessageType.Binary, true, CancellationToken.None); }
                    finally { _send[other].Release(); }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                // bağlantı koptu
            }
            finally
            {
                Kill(abort: false);
            }
        }

        public void Kill(bool abort)
        {
            WebSocket[] sockets;
            lock (_gate)
            {
                _dead = true;
                sockets = [.. _sockets.Values];
                _sockets.Clear();
            }
            foreach (var s in sockets)
            {
                try
                {
                    if (abort) s.Abort();
                    else _ = s.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "peer closed", CancellationToken.None);
                }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException) { }
            }
        }
    }
}
