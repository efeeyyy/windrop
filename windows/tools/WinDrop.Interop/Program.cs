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
//   Çıkış kodu: 0 = beklenen sayıda aktarım tamamlandı (veya expect=0 ve oturum yok), 3 = zaman aşımı, 1 = beklenmeyen oturum.

return await Cli.RunAsync(args);

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) return Fail("komut yok: pubkey | serve");
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

    /// <summary>Swift `signals` ile birebir aynı değerler (PROTOCOL §4/§9a).</summary>
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

    private static async Task<int> ServeAsync(Opts o)
    {
        var port = int.Parse(o.Need("port"));
        var mode = o.Get("mode") ?? "receive";
        var pattern = o.Need("pattern");
        var kind = pattern switch { "ik" => HandshakeKind.IK, "xx" => HandshakeKind.XX, _ => throw new ArgumentException("--pattern ik|xx") };
        var id = TransferId.Parse(o.Need("transfer-id"));
        var expect = int.Parse(o.Get("expect-completed") ?? "1");
        var timeout = TimeSpan.FromSeconds(double.Parse(o.Get("timeout") ?? "60"));
        var peerPub = Hex(o.Need("peer-pub"));
        using var myKey = NoiseKeyPair.FromPrivateKey(Hex(o.Need("my-key")));

        PlatformLog.Sink = s => Console.WriteLine("LOG " + s);

        FileSystemPartialStore? store = null;
        string? outDir = null;
        if (mode == "receive")
        {
            outDir = Path.GetFullPath(o.Need("out"));
            store = new FileSystemPartialStore(Path.Combine(outDir, ".partial"), Path.Combine(outDir, "final"));
        }
        else if (mode != "send") throw new ArgumentException("--mode receive|send");
        var count = int.Parse(o.Get("count") ?? "0");
        var total = long.Parse(o.Get("total") ?? "0");

        var completed = 0;
        var sessions = 0;
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (expect == 0) { /* yalnız zaman aşımını bekler */ }

        async Task Handler(SecureChannel ch)
        {
            Interlocked.Increment(ref sessions);
            Console.WriteLine($"HANDSHAKE=ok kind={ch.Kind.ToString().ToLowerInvariant()} sas={Sas.Compute(ch.HandshakeHash)} hash={Convert.ToHexStringLower(ch.HandshakeHash)}");
            TransferResult result;
            try
            {
                if (mode == "receive")
                {
                    var ropts = new ReceiverOptions(DeviceId.HexFromPublicKey(peerPub), id, new OfferInfo(count, total));
                    result = await new FileReceiver(ch, store!, ropts).RunAsync();
                    foreach (var f in result.ReceivedFiles)
                    {
                        await using var fs = File.OpenRead(f.StoredPath);
                        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs));
                        Console.WriteLine($"FILE id={f.Id} name={f.OriginalName} size={fs.Length} sha256={sha}");
                    }
                }
                else
                {
                    var sources = o.Positional.Select(p => (IFileSource)new FileSystemFileSource(p)).ToList();
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
            if (result.Outcome == TransferOutcome.Completed && Interlocked.Increment(ref completed) >= expect && expect > 0)
                enough.TrySetResult();
        }

        var pending = new PendingTransferTable();
        using var reg = pending.Register(id, peerPub, Handler, kind);
        await using var listener = new DataListener(myKey, pending, port);
        listener.Start();
        Console.WriteLine($"LISTENING port={listener.Port} mode={mode} pattern={pattern} transferId={id}");

        var finished = await Task.WhenAny(enough.Task, Task.Delay(timeout));
        await Task.Delay(200);   // son günlük satırları
        var ok = finished == enough.Task;
        Console.WriteLine($"SERVER_DONE completed={completed} sessions={sessions} timedOut={(!ok).ToString().ToLowerInvariant()}");
        if (expect == 0) return sessions == 0 ? 0 : 1;
        return ok ? 0 : 3;
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
