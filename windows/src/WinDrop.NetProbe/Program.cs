using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using QRCoder;

const long MaxMb = 200;
int port = 53999;
int duration = 0;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
    else if (args[i] == "--duration" && i + 1 < args.Length) duration = int.Parse(args[++i]);
}

static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");

Console.OutputEncoding = System.Text.Encoding.UTF8;
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = MaxMb * 1024 * 1024;
    o.ListenAnyIP(port); // IPv6 [::] dual-stack (IPv4 0.0.0.0 dahil); IPv6 yoksa yalnız IPv4
});
var app = builder.Build();

var buffer = new byte[1024 * 1024];
Array.Fill(buffer, (byte)0x5A);
bool firstSeen = false;

app.Use(async (ctx, next) =>
{
    var rip = ctx.Connection.RemoteIpAddress;
    var ripText = rip is null ? "?" : (rip.IsIPv4MappedToIPv6 ? rip.MapToIPv4().ToString() : rip.ToString());
    var remote = $"{ripText}:{ctx.Connection.RemotePort}";
    if (!firstSeen)
    {
        firstSeen = true;
        Console.WriteLine();
        Console.WriteLine($"===== TELEFON BAĞLANDI: {ripText} =====");
        Console.WriteLine();
    }
    var sw = Stopwatch.StartNew();
    ctx.Items["bytes"] = 0L;
    ctx.Items["ip"] = ripText;
    try { await next(); }
    finally
    {
        sw.Stop();
        long bytes = (long)ctx.Items["bytes"]!;
        double s = Math.Max(sw.Elapsed.TotalSeconds, 1e-6);
        Log($"{remote} {ctx.Request.Method} {ctx.Request.Path} bayt={bytes} süre={sw.ElapsedMilliseconds}ms {(bytes / 1048576.0 / s):F2} MB/s durum={ctx.Response.StatusCode}");
    }
});

app.MapGet("/", async ctx =>
{
    var html = Html.Page.Replace("{{IP}}", System.Net.WebUtility.HtmlEncode((string)ctx.Items["ip"]!));
    var data = System.Text.Encoding.UTF8.GetBytes(html);
    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.ContentLength = data.Length;
    await ctx.Response.Body.WriteAsync(data);
    ctx.Items["bytes"] = (long)data.Length;
});

app.MapGet("/down", async ctx =>
{
    long mb = 20;
    if (ctx.Request.Query.TryGetValue("mb", out var v) && !long.TryParse(v, out mb)) mb = 20;
    mb = Math.Clamp(mb, 1, MaxMb);
    ctx.Response.ContentType = "application/octet-stream";
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.ContentLength = mb * buffer.Length;
    for (long i = 0; i < mb; i++)
    {
        await ctx.Response.Body.WriteAsync(buffer, ctx.RequestAborted);
        ctx.Items["bytes"] = (i + 1) * buffer.Length;
    }
});

app.MapPost("/up", async ctx =>
{
    var sw = Stopwatch.StartNew();
    var buf = new byte[256 * 1024];
    long total = 0;
    int n;
    while ((n = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
    {
        total += n;
        ctx.Items["bytes"] = total;
        if (total > MaxMb * 1024 * 1024) { ctx.Response.StatusCode = 413; return; }
    }
    sw.Stop();
    double s = Math.Max(sw.Elapsed.TotalSeconds, 1e-6);
    ctx.Response.Headers.CacheControl = "no-store";
    await ctx.Response.WriteAsJsonAsync(new
    {
        bytes = total,
        mb = Math.Round(total / 1048576.0, 2),
        seconds = Math.Round(s, 3),
        mbps = Math.Round(total / 1048576.0 / s, 2)
    });
});

// Açılış çıktısı
Console.WriteLine($"WinDrop NetProbe - port {port}" + (duration > 0 ? $", {duration} sn sonra kapanır" : ""));
if (!Socket.OSSupportsIPv6) Console.WriteLine("UYARI: IPv6 desteklenmiyor, yalnız IPv4 dinleniyor.");
var urls = new List<(string name, string url, bool wifi)>();
foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
{
    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
    foreach (var ua in nic.GetIPProperties().UnicastAddresses)
    {
        var a = ua.Address;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            if (a.ToString().StartsWith("169.254.")) continue;
            var u = $"http://{a}:{port}/";
            Console.WriteLine($"  [{nic.Name}] IPv4 {a}  ->  {u}");
            urls.Add((nic.Name, u, nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211));
        }
        else if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            bool ll = a.IsIPv6LinkLocal;
            var ip = a.ToString();
            var pct = ip.IndexOf('%');
            if (pct >= 0) ip = ip[..pct];
            Console.WriteLine($"  [{nic.Name}] IPv6 {ip}{(ll ? " (link-local, yalnız bilgi)" : "")}  ->  http://[{ip}]:{port}/");
        }
    }
}
var primary = urls.FirstOrDefault(x => x.wifi);
if (primary.url == null) primary = urls.FirstOrDefault();
if (primary.url != null)
{
    Console.WriteLine($"\nBirincil URL ({primary.name}): {primary.url}\n");
    using var gen = new QRCodeGenerator();
    using var qr = gen.CreateQrCode(primary.url, QRCodeGenerator.ECCLevel.L);
    Console.WriteLine(new AsciiQRCode(qr).GetGraphic(1, "██", "  ", drawQuietZones: true));
}
Console.WriteLine("UYARI: Windows güvenlik duvarı izin sorarsa 'Özel ağlar' için İzin ver'e basın.");
Console.WriteLine("Durdurmak için Ctrl+C.\n");

if (duration > 0)
    app.Lifetime.ApplicationStarted.Register(() =>
        _ = Task.Delay(TimeSpan.FromSeconds(duration)).ContinueWith(_ =>
        {
            Log("Süre doldu, kapatılıyor.");
            app.Lifetime.StopApplication();
        }));
app.Lifetime.ApplicationStopped.Register(() => Log("Kapandı."));

try { await app.RunAsync(); }
catch (Exception e) { Console.WriteLine("HATA: " + e.Message); return 1; }
return 0;

static class Html
{
    public const string Page = """
<!doctype html><html lang="tr"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>WinDrop NetProbe</title>
<style>body{font-family:-apple-system,sans-serif;margin:16px;line-height:1.4}
h1{font-size:1.3em;color:#0a7a2f}button{display:block;width:100%;font-size:1.1em;padding:14px;margin:10px 0;border-radius:10px;border:0;background:#0a64d6;color:#fff}
pre{background:#f2f2f2;padding:10px;border-radius:8px;white-space:pre-wrap}</style></head><body>
<h1>Bağlantı başarılı — telefon PC'ye doğrudan ulaştı</h1>
<p>İstemci IP: <b>{{IP}}</b></p>
<button id="d">İndirme testi (PC→telefon)</button>
<button id="u">Yükleme testi (telefon→PC)</button>
<pre id="o">Sonuç bekleniyor…</pre>
<pre id="s">Özet: indirme —, yükleme —</pre>
<script>
var R={d:'—',u:'—'},o=document.getElementById('o');
function sum(){document.getElementById('s').textContent='Özet: indirme '+R.d+', yükleme '+R.u}
document.getElementById('d').onclick=async function(){
 o.textContent='İndiriliyor (20 MB)…';
 try{var t=performance.now(),r=await fetch('/down?mb=20&t='+Date.now(),{cache:'no-store'}),b=await r.arrayBuffer(),s=(performance.now()-t)/1000,m=b.byteLength/1048576;
 R.d=(m/s).toFixed(2)+' MB/s';o.textContent='İndirme: '+m.toFixed(1)+' MB, '+s.toFixed(2)+' sn, '+R.d}catch(e){o.textContent='İndirme hatası: '+e;R.d='hata'}sum()};
document.getElementById('u').onclick=async function(){
 o.textContent='Yükleniyor (10 MB)…';
 try{var n=10,a=new Uint8Array(n*1048576),t=performance.now(),r=await fetch('/up',{method:'POST',body:a,cache:'no-store'}),j=await r.json(),s=(performance.now()-t)/1000;
 R.u=(n/s).toFixed(2)+' MB/s (sunucu: '+j.mbps+' MB/s)';o.textContent='Yükleme: '+j.mb+' MB, istemci '+s.toFixed(2)+' sn, sunucu '+j.seconds+' sn, '+R.u}catch(e){o.textContent='Yükleme hatası: '+e;R.u='hata'}sum()};
</script></body></html>
""";
}
