using WinDrop.BleProbe;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("WinDrop.BleProbe - BLE GATT çevre birimi ölçüm aracı (Adım 0)");
    Console.WriteLine("  (argümansız)  GATT sunucusunu başlat ve reklam yap; Ctrl+C ile çık");
    Console.WriteLine("  --check       Bluetooth adapter yeteneklerini yazdır ve çık");
    Console.WriteLine("  --duration N  N saniye sonra temiz kapan (test için; varsayılan: Ctrl+C)");
    Console.WriteLine("  --uuid128     128 bit servis UUID kullan (bu makinede reklam Aborted olabilir)");
    Console.WriteLine("  --sweep L     tx'e ilk abone gelince KB/s hız listesini sırayla dene (örn. 20,50,100)");
    Console.WriteLine("  --sweep-kb N  tarama aşamalarında gönderilecek KB (varsayılan: 512)");
    return 0;
}

if (args.Contains("--check"))
    return await AdapterCheck.RunAsync();

int? duration = null;
var di = Array.IndexOf(args, "--duration");
if (di >= 0 && di + 1 < args.Length && int.TryParse(args[di + 1], out var secs) && secs > 0)
    duration = secs;

double[]? sweepRates = null;
var sweepKb = 512;
var si = Array.IndexOf(args, "--sweep");
if (si >= 0)
{
    if (si + 1 >= args.Length || !TryParseSweepRates(args[si + 1], out sweepRates))
    {
        Console.Error.WriteLine($"Hata: --sweep için geçersiz liste: '{(si + 1 < args.Length ? args[si + 1] : "")}'. " +
                                "Beklenen: pozitif KB/s değerleri, virgülle (örn. 20,50,100).");
        return 2;
    }
}
var ski = Array.IndexOf(args, "--sweep-kb");
if (ski >= 0)
{
    if (ski + 1 >= args.Length || !int.TryParse(args[ski + 1], out sweepKb) || sweepKb <= 0)
    {
        Console.Error.WriteLine("Hata: --sweep-kb pozitif bir tam sayı olmalı (örn. 512).");
        return 2;
    }
}

return await ProbeServer.RunAsync(duration, args.Contains("--uuid128"), sweepRates, sweepKb);

static bool TryParseSweepRates(string list, out double[]? rates)
{
    rates = null;
    var parts = list.Split(',');
    var parsed = new double[parts.Length];
    for (var i = 0; i < parts.Length; i++)
    {
        if (!double.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out parsed[i])
            || !double.IsFinite(parsed[i]) || parsed[i] <= 0)
            return false;
    }
    rates = parsed;
    return true;
}
