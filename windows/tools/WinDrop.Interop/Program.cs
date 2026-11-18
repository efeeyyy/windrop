using System.Security.Cryptography;
using WinDrop.Core.Noise;
using WinDrop.Core.Protocol;
using WinDrop.Core.Session;
using WinDrop.Core.Transfer;
using WinDrop.Platform;

// WinDrop.Interop: Swift `windrop-interop` ile gerçek TCP üzerinden çapraz uyum denemesi (docs/INTEROP.md).
// C# her zaman dinleyen + yanıtlayan taraftır (PC rolü; gerçek DataListener). Çıktı "KEY=değer" satırlarıdır.
//
//   pubkey --my-key <hex32>
//   serve  --port P --my-key <hex32> --peer-pub <hex32> --transfer-id <b64url> --pattern ik|xx
//          [--mode receive|send] [--out DIR] [--count N --total BYTES] [--expect-completed N] [--timeout SN]
//          [dosyalar...]            (mode=send: bu dosyalar Swift'e gönderilir)
//   relay-pc --relay-url URL --token <43 karakter base64url | 64 hex> --my-key <hex32> --peer-pub <hex32> --transfer-id <b64url>
//          --pattern ik|xx [--mode receive|send] [--out DIR] [--count N --total BYTES] [--timeout SN] [dosyalar...]
//          PC rolünde (yanıtlayan) aktarma odasına (PROTOCOL §7.4, role=pc) bağlanır; odaya girince "RELAY_READY room=<id>" yazar,
//          iPhone (Swift, başlatan) token'dan aynı odaya girer. Sonrası serve ile aynı (HANDSHAKE/FILE/OUTCOME/SERVER_DONE satırları).
//   Çıkış kodu: 0 = beklenen sayıda aktarım tamamlandı (veya expect=0 ve oturum yok), 3 = zaman aşımı, 1 = beklenmeyen oturum.

return await Cli.RunAsync(args);

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) return Fail("komut yok: pubkey | serve | relay-pc");
        var o = new Opts(args.Skip(1));
        try
        {
            switch (args[0])
            {
                case "pubkey":
                    using (var k = NoiseKeyPair.FromPrivateKey(Hex(o.Need("my-key"))))
                        Console.WriteLine("PUB=" + Convert.ToHexStringLower(k.PublicKey));
                    return 0;
                case "serve":
                    return await ServeAsync(o);
                case "relay-pc":
                    return await RelayPcAsync(o);
                case "signals":
                    File.WriteAllLines(o.Need("out"), SampleSignals().Select(m => System.Text.Encoding.UTF8.GetString(SignalMessages.Encode(m))));
                    Console.WriteLine($"SIGNALS_WRITTEN={SampleSignals().Count}");
                    return 0;
                case "signals-verify":
                    return VerifySignals(o.Need("file"));
                default:
                    return Fail("bilinmeyen komut: " + args[0]);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR={ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    /// <summary>Swift `signals` ile birebir aynı değerler (PROTOCOL §5).</summary>
    private static List<SignalMessage> SampleSignals()
    {
        var id = TransferId.Parse("AQIDBAUGBwgJCgsMDQ4PEA");
        return
        [
            new HelloMessage(1, "Efe'nin iPhone'u", ["tcp", "hotspot", "relay"], WifiState.Connected, true),
            new StatusMessage(WifiState.None, false),
            new OfferMessage(id, 3, 12345678901, ["a.txt", "b.png"]),
            new AcceptMessage(id),
            new RejectMessage(id, "meşgul"),
            new AddrMessage(id, [new AddrCandidate("192.168.1.5", 53600), new AddrCandidate("fe80::1", 53600)]),
            new HotspotMessage(id, "192.168.137.1", 53600, "WinDrop-1A2B3C4D", "p@ss/word"),
            new PairInfoMessage("WinDrop-1A2B3C4D", "p@ss/word"),
            new RelayMessage(id, "tok123"),
            new PathMessage(id, PathResult.HotspotFail),
            new CancelSignalMessage(id),
        ];
    }

    /// <summary>Diğer tarafın ürettiği satırları çözer, kendi kodlamasına çevirir ve kendi örneğiyle karşılaştırır.</summary>
    private static int VerifySignals(string file)
    {
        var own = SampleSignals();
        var lines = File.ReadAllLines(file).Where(l => l.Length > 0).ToArray();
        var bad = lines.Length != own.Count ? 1 : 0;
        if (bad == 1) Console.WriteLine($"SIGNAL_COUNT_MISMATCH other={lines.Length} own={own.Count}");
        for (var i = 0; i < Math.Min(lines.Length, own.Count); i++)
        {
            string status;
            try
            {
                var decoded = SignalMessages.Decode(System.Text.Encoding.UTF8.GetBytes(lines[i]));
                var a = System.Text.Encoding.UTF8.GetString(SignalMessages.Encode(decoded));
                var b = System.Text.Encoding.UTF8.GetString(SignalMessages.Encode(own[i]));
                status = a == b ? "OK" : $"MISMATCH decoded={a} expected={b}";
            }
            catch (Exception ex)
            {
                status = $"DECODE_FAIL {ex.Message}";
            }
            if (status != "OK") bad++;
            Console.WriteLine($"SIGNAL {i} {own[i].GetType().Name} {status}");
        }
        Console.WriteLine(bad == 0 ? "SIGNALS=ok" : "SIGNALS=FAIL");
        return bad == 0 ? 0 : 1;
    }

    private static int Fail(string m)
    {
        Console.Error.WriteLine("ERROR: " + m);
        return 2;
    }

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    /// <summary>serve ve relay-pc için ortak oturum yürütücüsü (kurulmuş SecureChannel üstünde gönder/al + günlük satırları).</summary>
    private sealed class SessionRunner
    {
        private readonly Opts _o;
        private readonly FileSystemPartialStore? _store;
        private readonly int _count;
        private readonly long _total;
        private readonly int _expect;
        private int _completed;
        private int _sessions;
        private readonly TaskCompletionSource _enough = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SessionRunner(Opts o)
        {
            _o = o;
            Mode = o.Get("mode") ?? "receive";
            Pattern = o.Need("pattern");
            Kind = Pattern switch { "ik" => HandshakeKind.IK, "xx" => HandshakeKind.XX, _ => throw new ArgumentException("--pattern ik|xx") };
            Id = TransferId.Parse(o.Need("transfer-id"));
            _expect = int.Parse(o.Get("expect-completed") ?? "1");
            Timeout = TimeSpan.FromSeconds(double.Parse(o.Get("timeout") ?? "60"));
            PeerPub = Hex(o.Need("peer-pub"));
            if (Mode == "receive")
            {
                var outDir = Path.GetFullPath(o.Need("out"));
                _store = new FileSystemPartialStore(Path.Combine(outDir, ".partial"), Path.Combine(outDir, "final"));
            }
            else if (Mode != "send") throw new ArgumentException("--mode receive|send");
            _count = int.Parse(o.Get("count") ?? "0");
            _total = long.Parse(o.Get("total") ?? "0");
        }

        public string Mode { get; }
        public string Pattern { get; }
        public HandshakeKind Kind { get; }
        public TransferId Id { get; }
        public byte[] PeerPub { get; }
        public TimeSpan Timeout { get; }
        public int Completed => _completed;
        public Task Enough => _enough.Task;

        public async Task HandleAsync(SecureChannel ch)
        {
            Interlocked.Increment(ref _sessions);
            Console.WriteLine($"HANDSHAKE=ok kind={ch.Kind.ToString().ToLowerInvariant()} sas={Sas.Compute(ch.HandshakeHash)} hash={Convert.ToHexStringLower(ch.HandshakeHash)}");
            TransferResult result;
            try
            {
                if (Mode == "receive")
                {
                    var ropts = new ReceiverOptions(DeviceId.HexFromPublicKey(PeerPub), Id, new OfferInfo(_count, _total));
                    result = await new FileReceiver(ch, _store!, ropts).RunAsync();
                    foreach (var f in result.ReceivedFiles)
                    {
                        await using var fs = File.OpenRead(f.StoredPath);
                        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs));
                        Console.WriteLine($"FILE id={f.Id} name={f.OriginalName} size={fs.Length} sha256={sha}");
                    }
                }
                else
                {
                    var sources = _o.Positional.Select(p => (IFileSource)new FileSystemFileSource(p)).ToList();
                    result = await new FileSender(ch, sources).RunAsync();
                }
            }
            catch (Exception ex) when (ex is IOException or ProtocolException or NoiseException or OperationCanceledException)
            {
                Console.WriteLine($"SESSION_LOST={ex.GetType().Name}: {ex.Message}");
                return;
            }
            Console.WriteLine($"OUTCOME={result.Outcome}");
            Console.WriteLine($"FAILED=[{string.Join(", ", result.FailedFileIds)}]");
            if (result.Error is { } err) Console.WriteLine($"TRANSFER_ERROR={err}");
            foreach (var (fid, off) in result.ResumeOffsets.OrderBy(p => p.Key)) Console.WriteLine($"RESUME file={fid} offset={off}");
            if (result.Outcome == TransferOutcome.Completed && Interlocked.Increment(ref _completed) >= _expect && _expect > 0)
                _enough.TrySetResult();
        }

        /// <summary>SERVER_DONE satırı + çıkış kodu (expect=0: oturum olmamalı).</summary>
        public async Task<int> FinishAsync()
        {
            await Task.Delay(200);   // son günlük satırları
            var ok = _enough.Task.IsCompleted;
            Console.WriteLine($"SERVER_DONE completed={_completed} sessions={_sessions} timedOut={(!ok).ToString().ToLowerInvariant()}");
            if (_expect == 0) return _sessions == 0 ? 0 : 1;
            return ok ? 0 : 3;
        }
    }

    private static async Task<int> ServeAsync(Opts o)
    {
        var port = int.Parse(o.Need("port"));
        var runner = new SessionRunner(o);
        using var myKey = NoiseKeyPair.FromPrivateKey(Hex(o.Need("my-key")));
        PlatformLog.Sink = s => Console.WriteLine("LOG " + s);

        var pending = new PendingTransferTable();
        using var reg = pending.Register(runner.Id, runner.PeerPub, runner.HandleAsync, runner.Kind);
        await using var listener = new DataListener(myKey, pending, port);
        listener.Start();
        Console.WriteLine($"LISTENING port={listener.Port} mode={runner.Mode} pattern={runner.Pattern} transferId={runner.Id}");

        await Task.WhenAny(runner.Enough, Task.Delay(runner.Timeout));
        return await runner.FinishAsync();
    }

    /// <summary>relay-pc: PC rolünde (yanıtlayan) aktarma odasına bağlanır (PROTOCOL §7.4); iPhone başlatandır.</summary>
    private static async Task<int> RelayPcAsync(Opts o)
    {
        var runner = new SessionRunner(o);
        using var myKey = NoiseKeyPair.FromPrivateKey(Hex(o.Need("my-key")));
        var tokenText = o.Need("token");
        byte[] token;
        if (tokenText.Length == 64 && tokenText.All(Uri.IsHexDigit)) token = Convert.FromHexString(tokenText);
        else if (!RelayRoom.TryDecodeToken(tokenText, out token)) throw new ArgumentException("--token 43 karakter base64url veya 64 hex olmalı");
        var roomId = RelayRoom.ComputeRoomId(token);
        var uri = RelayRoom.BuildRoomUri(o.Need("relay-url"), roomId, "pc") ?? throw new ArgumentException("--relay-url geçersiz");
        PlatformLog.Sink = s => Console.WriteLine("LOG " + s);

        RelayPcSession session;
        try
        {
            session = await RelayPcClient.AcceptAsync(uri, runner.Id, myKey, runner.PeerPub, runner.Kind,
                _ =>
                {
                    Console.WriteLine($"RELAY_READY room={roomId} mode={runner.Mode} pattern={runner.Pattern} transferId={runner.Id}");
                    return Task.CompletedTask;
                },
                runner.Timeout, CancellationToken.None);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ProtocolException or NoiseException or PeerNotTrustedException)
        {
            Console.WriteLine($"RELAY_FAILED={ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"SERVER_DONE completed=0 sessions=0 timedOut={(ex is TimeoutException).ToString().ToLowerInvariant()}");
            return ex is TimeoutException ? 3 : 1;
        }
        using (session)
        {
            await runner.HandleAsync(session.Channel);
            await session.CloseAsync();
        }
        return await runner.FinishAsync();
    }

    private sealed class Opts
    {
        private readonly Dictionary<string, string> _values = [];
        public List<string> Positional { get; } = [];

        public Opts(IEnumerable<string> args)
        {
            var a = args.ToArray();
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i].StartsWith("--", StringComparison.Ordinal))
                {
                    if (i + 1 >= a.Length) throw new ArgumentException($"{a[i]} için değer yok");
                    _values[a[i][2..]] = a[++i];
                }
                else Positional.Add(a[i]);
            }
        }

        public string? Get(string k) => _values.GetValueOrDefault(k);
        public string Need(string k) => Get(k) ?? throw new ArgumentException($"--{k} gerekli");
    }
}
