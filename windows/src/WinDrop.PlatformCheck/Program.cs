using WinDrop.Platform;

// Donanım doğrulama aracı (testlerde çalışmaz). Parola ASLA yazdırılmaz.
//   PlatformCheck hotspot-read    : mevcut etkin nokta ayarını/durumunu oku
//   PlatformCheck hotspot-cycle   : oku -> WinDrop ayarıyla başlat -> 10 sn -> durdur -> eski ayarı geri yükle ve doğrula
//   PlatformCheck net             : aday adresler

var cmd = args.Length > 0 ? args[0] : "hotspot-read";
switch (cmd)
{
    case "net":
        foreach (var c in NetworkInfo.GetCandidateAddresses())
            Console.WriteLine($"{c.Address}  [{c.InterfaceName}]{(c.IsWifi ? " wifi" : "")}");
        Console.WriteLine($"HasInternet: {NetworkInfo.HasInternet()}");
        return 0;

    case "hotspot-read":
        return Read();

    case "hotspot-start":
    {
        // Yalnız kullanıcının etkin noktası AÇIKKEN güvenli: StartAsync false dönmeli, hiçbir şey değişmemeli.
        await using var m = new HotspotManager(Path.Combine(Path.GetTempPath(), "WinDropCheck"));
        Console.WriteLine($"Önce: {m.GetOperationalState()}");
        if (m.GetOperationalState() != Windows.Networking.NetworkOperators.TetheringOperationalState.On)
        {
            Console.WriteLine("Etkin nokta kapalı; bu komut yalnız açıkken çalışır.");
            return 2;
        }
        Console.WriteLine($"StartAsync -> {await m.StartAsync(new HotspotConfig("WinDrop-XXXXXXX", "AAAAAAAAAAAAAAAAAAAAAAAAAA"))} (false bekleniyor)");
        Console.WriteLine($"Sonra: {m.GetOperationalState()}, Ayar: {m.ReadCurrentConfiguration()}");
        return 0;
    }

    case "hotspot-cycle":
        return await Cycle();

    default:
        Console.WriteLine("Bilinmeyen komut: " + cmd);
        return 2;
}

static int Read()
{
    var m = new HotspotManager(Path.Combine(Path.GetTempPath(), "WinDropCheck"));
    try
    {
        Console.WriteLine($"Durum: {m.GetOperationalState()}, istemci: {m.GetClientCount()}");
        Console.WriteLine($"Ayar: {m.ReadCurrentConfiguration()}");
        Console.WriteLine($"2,4 GHz destekli: {m.IsTwoPointFourSupported()}");
        return 0;
    }
    catch (HotspotException ex)
    {
        Console.WriteLine("HotspotException: " + ex.Message);
        return 1;
    }
}

static async Task<int> Cycle()
{
    var root = Path.Combine(Path.GetTempPath(), "WinDropCheck-" + Guid.NewGuid().ToString("N"));
    var cfg = new HotspotConfigStore(root).GetOrCreate();
    await using var m = new HotspotManager(root);
    HotspotSnapshot before;
    try
    {
        var state = m.GetOperationalState();
        Console.WriteLine($"[1] Başlangıç durumu: {state}, istemci: {m.GetClientCount()}");
        before = m.ReadCurrentConfiguration();
        Console.WriteLine($"[1] Mevcut ayar: {before}");
        if (state != Windows.Networking.NetworkOperators.TetheringOperationalState.Off)
        {
            Console.WriteLine("Etkin nokta zaten açık/geçişte: DOKUNULMADI.");
            return 0;
        }
    }
    catch (HotspotException ex)
    {
        Console.WriteLine("Okuma başarısız: " + ex.Message);
        return 1;
    }

    int rc = 0;
    try
    {
        Console.WriteLine($"[2] WinDrop ayarı: Ssid={cfg.Ssid}");
        bool started = await m.StartAsync(cfg);
        Console.WriteLine($"[2] StartAsync -> {started}");
        var during = m.ReadCurrentConfiguration();
        Console.WriteLine($"[3] Durum: {m.GetOperationalState()}, istemci: {m.GetClientCount()}, Ssid={during.Ssid}, Band={during.Band}");
        await Task.Delay(TimeSpan.FromSeconds(10));
        Console.WriteLine($"[3] 10 sn sonra: {m.GetOperationalState()}, istemci: {m.GetClientCount()}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"HATA: {ex.GetType().Name}: {ex.Message}");
        rc = 1;
    }
    finally
    {
        try
        {
            bool stopped = await m.StopAsync();
            Console.WriteLine($"[4] StopAsync -> {stopped}");
            await m.RecoverAsync(); // yedek kaldıysa (başarısız yol) geri yükle
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DURDURMA/GERİ YÜKLEME HATASI: {ex.GetType().Name}: {ex.Message}");
            rc = 1;
        }
    }

    try
    {
        var after = m.ReadCurrentConfiguration();
        Console.WriteLine($"[5] Durum: {m.GetOperationalState()}, geri okunan ayar: {after}");
        bool ssidSame = after.Ssid == before.Ssid;
        bool bandSame = after.Band == before.Band;
        bool passSame = after.Passphrase == before.Passphrase;
        Console.WriteLine($"[5] Geri yükleme doğrulama: ssid={ssidSame} band={bandSame} parola={passSame}");
        if (!(ssidSame && bandSame && passSame)) rc = 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Doğrulama okunamadı: {ex.Message}");
        rc = 1;
    }
    try { Directory.Delete(root, true); } catch (IOException) { }
    return rc;
}
