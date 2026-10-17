using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace WinDrop.BleProbe;

/// <summary>GATT sunucusu (peripheral) ölçüm modu.</summary>
internal static class ProbeServer
{
    private const string ProbeVersion = "0.1";

    private static GattLocalCharacteristic _tx = null!;
    private static long _rxBytes;
    private static long _rxLastReported;
    private static long _rxWrites;
    private static long _rxLastTicks = Stopwatch.GetTimestamp();
    private static readonly ConcurrentDictionary<string, GattSession> Sessions = new();
    private static int _streaming; // 0/1: aynı anda tek stream
    private static double[]? _sweepRates; // --sweep; null ise tarama yok
    private static int _sweepKb = 512;
    private static int _sweepStarted; // 0/1: tarama yalnız bir kez başlar
    private static CancellationToken _stopToken;

    /// <param name="durationSeconds">Verilirse (test için) bu sürede temiz kapanır; yoksa Ctrl+C.</param>
    /// <param name="sweepRates">Verilirse: tx'e ilk abone gelince bu KB/s hızlarını sırayla dene.</param>
    public static async Task<int> RunAsync(int? durationSeconds = null, bool useFull128 = false,
        double[]? sweepRates = null, int sweepKb = 512)
    {
        _sweepRates = sweepRates;
        _sweepKb = sweepKb;

        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
        {
            Log.Error("Bluetooth adapter bulunamadı.");
            return 2;
        }
        if (!adapter.IsLowEnergySupported)
        {
            Log.Error("Adapter Bluetooth LE desteklemiyor.");
            return 2;
        }
        if (!adapter.IsPeripheralRoleSupported)
        {
            Log.Error("Adapter peripheral (GATT sunucusu) rolünü desteklemiyor; bu makine sunucu olamaz. " +
                      "--check ile ayrıntıya bakın; sürücü/donanım gerekebilir.");
            return 3;
        }

        var serviceUuid = useFull128 ? ProbeUuids.Service128 : ProbeUuids.Service;
        var created = await GattServiceProvider.CreateAsync(serviceUuid);
        if (created.Error != BluetoothError.Success)
        {
            Log.Error($"GattServiceProvider.CreateAsync başarısız: BluetoothError.{created.Error}");
            return 4;
        }
        var provider = created.ServiceProvider;

        // info: Read, sabit değer
        var infoJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["name"] = Environment.MachineName,
            ["probe"] = ProbeVersion,
        });
        var infoWriter = new DataWriter();
        infoWriter.WriteBytes(Encoding.UTF8.GetBytes(infoJson));
        var info = await AddCharacteristic(provider, "info", ProbeUuids.Info, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read,
            StaticValue = infoWriter.DetachBuffer(),
            UserDescription = "info",
        });
        if (info is null) return 4;

        // rx: Write + WriteWithoutResponse
        var rx = await AddCharacteristic(provider, "rx", ProbeUuids.Rx, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse,
            UserDescription = "rx",
        });
        if (rx is null) return 4;
        rx.WriteRequested += OnRxWriteRequested;

        // tx: Notify + Read
        var tx = await AddCharacteristic(provider, "tx", ProbeUuids.Tx, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Notify | GattCharacteristicProperties.Read,
            UserDescription = "tx",
        });
        if (tx is null) return 4;
        _tx = tx;
        tx.ReadRequested += OnTxReadRequested;
        tx.SubscribedClientsChanged += OnSubscribedClientsChanged;

        // ctrl: Write (UTF-8 komutlar)
        var ctrl = await AddCharacteristic(provider, "ctrl", ProbeUuids.Ctrl, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Write,
            UserDescription = "ctrl",
        });
        if (ctrl is null) return 4;
        ctrl.WriteRequested += OnCtrlWriteRequested;

        provider.AdvertisementStatusChanged += (_, e) =>
            Log.Info($"AdvertisementStatusChanged: {e.Status}" +
                     (e.Error != BluetoothError.Success ? $" (BluetoothError.{e.Error})" : ""));

        var adv = new GattServiceProviderAdvertisingParameters
        {
            IsConnectable = true,
            IsDiscoverable = true,
        };
        try
        {
            provider.StartAdvertising(adv);
        }
        catch (Exception ex)
        {
            Log.Error($"StartAdvertising hata verdi: {ex.GetType().Name}: {ex.Message}");
            return 5;
        }
        Log.Info($"Servis {serviceUuid} için reklam istendi. Bilgisayar adı: {Environment.MachineName}. Çıkış: Ctrl+C");
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            var st = provider.AdvertisementStatus;
            if (st == GattServiceProviderAdvertisementStatus.Started) return;
            Log.Error($"Reklam 3 sn sonra hâlâ '{st}' (Started değil); iPhone cihazı göremez. " +
                      (useFull128 ? "128 bit UUID bazı adapterlerde reddedilir; --uuid128 olmadan deneyin."
                                  : "Radyo/sürücü durumunu --check ile kontrol edin."));
        });

        using var cts = new CancellationTokenSource();
        _stopToken = cts.Token;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        if (durationSeconds is { } d) cts.CancelAfter(TimeSpan.FromSeconds(d));

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cts.Token))
                ReportRx();
        }
        catch (OperationCanceledException)
        {
        }

        Log.Info("Kapanıyor...");
        try { provider.StopAdvertising(); }
        catch (Exception ex) { Log.Error($"StopAdvertising hata verdi: {ex.Message}"); }
        Log.Info($"Toplam rx: {Interlocked.Read(ref _rxBytes)} bayt, {Interlocked.Read(ref _rxWrites)} yazma.");
        return 0;
    }

    private static async Task<GattLocalCharacteristic?> AddCharacteristic(
        GattServiceProvider provider, string name, Guid uuid, GattLocalCharacteristicParameters p)
    {
        var r = await provider.Service.CreateCharacteristicAsync(uuid, p);
        if (r.Error != BluetoothError.Success)
        {
            Log.Error($"Karakteristik '{name}' oluşturulamadı: BluetoothError.{r.Error}");
            return null;
        }
        return r.Characteristic;
    }

    // ---- oturum izleme ----

    /// <returns>true: oturum ilk kez görüldü.</returns>
    private static bool TrackSession(GattSession session)
    {
        if (!Sessions.TryAdd(session.DeviceId.Id, session)) return false;
        Log.Info($"Yeni oturum: {session.DeviceId.Id}  MaxPduSize={session.MaxPduSize}  Durum={session.SessionStatus}");
        session.MaxPduSizeChanged += (s, _) => Log.Info($"MaxPduSize değişti: {s.MaxPduSize}");
        session.SessionStatusChanged += (s, e) =>
        {
            Log.Info($"Oturum durumu: {e.Status}" +
                     (e.Error != BluetoothError.Success ? $" (BluetoothError.{e.Error})" : ""));
            if (e.Status == GattSessionStatus.Closed) Sessions.TryRemove(s.DeviceId.Id, out _);
        };
        return true;
    }

    // ---- rx ----

    private static async void OnRxWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (TrackSession(args.Session))
                Log.Info($"İlk rx yazması: MaxPduSize={args.Session.MaxPduSize}");

            var request = await args.GetRequestAsync();
            if (request is null) return;
            Interlocked.Add(ref _rxBytes, request.Value.Length);
            Interlocked.Increment(ref _rxWrites);
            if (request.Option == GattWriteOption.WriteWithResponse) request.Respond();
        }
        catch (Exception ex)
        {
            Log.Error($"rx yazma işlenemedi: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static void ReportRx()
    {
        var total = Interlocked.Read(ref _rxBytes);
        var delta = total - _rxLastReported;
        var now = Stopwatch.GetTimestamp();
        var secs = Stopwatch.GetElapsedTime(_rxLastTicks, now).TotalSeconds;
        _rxLastTicks = now;
        _rxLastReported = total;
        if (delta == 0) return;
        Log.Info($"rx: toplam {total / 1024.0:F1} KB, hız {delta / 1024.0 / secs:F2} KB/s");
    }

    // ---- tx ----

    private static async void OnTxReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            request?.RespondWithValue(new DataWriter().DetachBuffer());
        }
        catch (Exception ex)
        {
            Log.Error($"tx okuma işlenemedi: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        var clients = sender.SubscribedClients;
        Log.Info($"tx abone sayısı: {clients.Count}");
        foreach (var c in clients)
        {
            TrackSession(c.Session);
            Log.Info($"  abone: {c.Session.DeviceId.Id}  MaxNotificationSize={c.MaxNotificationSize}  MaxPduSize={c.Session.MaxPduSize}");
        }

        if (_sweepRates is { } rates && clients.Count > 0 && Interlocked.Exchange(ref _sweepStarted, 1) == 0)
            _ = Task.Run(() => RunSweepAsync(rates, _sweepKb, _stopToken));
    }

    /// <summary>--sweep: her hız için StreamAsync çağırır; aşamalar arasında 10 sn ara verir.</summary>
    private static async Task RunSweepAsync(double[] rates, int kb, CancellationToken ct)
    {
        var n = rates.Length;
        try
        {
            Log.Info($"Tarama: {n} aşama, aşama başına {kb} KB; 5 sn sonra başlıyor.");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            for (var i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rate = rates[i];
                var rateText = rate.ToString("0.##", CultureInfo.InvariantCulture);
                Log.Info($"===== AŞAMA {i + 1}/{n}: {rateText} KB/s BAŞLIYOR =====");
                try
                {
                    await StreamAsync(kb, rate);
                }
                catch (Exception ex)
                {
                    Log.Error($"Tarama aşama {i + 1} istisna: {ex.GetType().Name}: {ex.Message}");
                }
                Log.Info($"===== AŞAMA {i + 1} BİTTİ — 10 sn ara =====");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            Log.Info("===== TARAMA BİTTİ =====");
        }
        catch (OperationCanceledException)
        {
            Log.Info("Tarama iptal edildi (kapanış).");
        }
        catch (Exception ex)
        {
            Log.Error($"Tarama hatası: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- ctrl ----

    private static async void OnCtrlWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        string text;
        try
        {
            TrackSession(args.Session);
            var request = await args.GetRequestAsync();
            if (request is null) return;
            var reader = DataReader.FromBuffer(request.Value);
            var bytes = new byte[request.Value.Length];
            reader.ReadBytes(bytes);
            text = Encoding.UTF8.GetString(bytes).Trim();
            if (request.Option == GattWriteOption.WriteWithResponse) request.Respond();
        }
        catch (Exception ex)
        {
            Log.Error($"ctrl yazma işlenemedi: {ex.Message}");
            return;
        }
        finally
        {
            deferral.Complete();
        }

        Log.Info($"ctrl komutu: \"{text}\"");
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 2 or 3 && parts[0].Equals("stream", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], out var kb) && kb > 0 && TryParseRate(parts, out var kbps))
        {
            _ = Task.Run(() => StreamAsync(kb, kbps));
        }
        else if (parts.Length == 1 && parts[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _rxBytes, 0);
            Interlocked.Exchange(ref _rxWrites, 0);
            _rxLastReported = 0;
            _rxLastTicks = Stopwatch.GetTimestamp();
            Log.Info("Sayaçlar sıfırlandı.");
        }
        else
        {
            Log.Error($"Bilinmeyen/hatalı komut: \"{text}\" (beklenen: 'stream <KB>' veya 'reset')");
        }
    }

    /// <summary>İsteğe bağlı 3. parametre (KB/s hedef hız). Yoksa true + null; geçersizse (sayı değil, &lt;= 0) false.</summary>
    private static bool TryParseRate(string[] parts, out double? rateKbps)
    {
        rateKbps = null;
        if (parts.Length == 2) return true;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) || r <= 0)
            return false;
        rateKbps = r;
        return true;
    }

    private static async Task StreamAsync(int kb, double? kbps)
    {
        if (Interlocked.Exchange(ref _streaming, 1) == 1)
        {
            Log.Error("Zaten bir stream sürüyor; yeni komut yok sayıldı.");
            return;
        }
        try
        {
            var clients = _tx.SubscribedClients.ToList();
            if (clients.Count == 0)
            {
                Log.Error("stream: tx'e abone istemci yok (nRF Connect'te tx için bildirimleri açın).");
                return;
            }

            long totalBytes = kb * 1024L;
            foreach (var client in clients)
            {
                var chunk = Math.Max(20, client.Session.MaxPduSize - 3);
                Log.Info($"stream başlıyor: {kb} KB, parça={chunk} bayt (MaxPduSize={client.Session.MaxPduSize}, " +
                         $"MaxNotificationSize={client.MaxNotificationSize}) -> {client.Session.DeviceId.Id}");
                long sent = 0;
                long ok = 0, failed = 0;
                var consecutiveFail = 0;
                uint seq = 0;
                var lastStatus = GattCommunicationStatus.Success;
                var sw = Stopwatch.StartNew();
                while (sent < totalBytes)
                {
                    var size = (int)Math.Min(chunk, totalBytes - sent);
                    var data = new byte[size];
                    BitConverter.TryWriteBytes(data.AsSpan(0, Math.Min(4, size)), seq);
                    for (var i = 4; i < size; i++) data[i] = (byte)i;
                    var w = new DataWriter();
                    w.WriteBytes(data);

                    try
                    {
                        var res = await _tx.NotifyValueAsync(w.DetachBuffer(), client);
                        if (res.Status == GattCommunicationStatus.Success)
                        {
                            ok++;
                            consecutiveFail = 0;
                            sent += size;
                            seq++;
                        }
                        else
                        {
                            failed++;
                            consecutiveFail++;
                            lastStatus = res.Status;
                            Log.Error($"NotifyValueAsync durum={res.Status} protokolHatası={res.ProtocolError}");
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        consecutiveFail++;
                        Log.Error($"NotifyValueAsync istisna: {ex.GetType().Name}: {ex.Message}");
                    }

                    if (consecutiveFail >= 20)
                    {
                        Log.Error($"20 ardışık başarısızlık (son durum {lastStatus}); stream iptal.");
                        break;
                    }
                    if (consecutiveFail > 0) await Task.Delay(50);
                    // Hedef hız: başlangıçtan beri gönderilen bayta göre kümülatif hedef süre.
                    if (kbps is { } rate)
                    {
                        var waitSecs = sent / (rate * 1024.0) - sw.Elapsed.TotalSeconds;
                        if (waitSecs > 0) await Task.Delay(TimeSpan.FromSeconds(waitSecs));
                    }
                }
                sw.Stop();
                var secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                var rateText = kbps is { } r ? $", hedef={r:0.##} KB/s" : "";
                Log.Info($"stream bitti: gönderilen={sent / 1024.0:F1} KB, süre={secs:F2} s, " +
                         $"hız={sent / 1024.0 / secs:F2} KB/s{rateText}, başarılı bildirim={ok}, başarısız={failed}");
                if (kbps is not null)
                    Log.Info("  (gönderen tarafı ölçümü; gerçek hız alıcıda ölçülür)");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"stream hatası: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _streaming, 0);
        }
    }
}
