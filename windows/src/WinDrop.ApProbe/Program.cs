using System.Security.Cryptography;
using Windows.Devices.WiFiDirect;
using Windows.Security.Credentials;
using WinDrop.ApProbe;

// WinDrop.ApProbe: eduroam bağlıyken Wi-Fi Direct "legacy" soft AP açılabiliyor mu?
// Kullanım: WinDrop.ApProbe [--duration N] [--ssid ad]  (N sn; verilmezse Ctrl+C'ye kadar)

int duration = 0;
string? ssidArg = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--duration" && i + 1 < args.Length) duration = int.Parse(args[++i]);
    else if (args[i] == "--ssid" && i + 1 < args.Length) ssidArg = args[++i];
}

static string RandomString(string alphabet, int len)
{
    var chars = new char[len];
    for (int i = 0; i < len; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
    return new string(chars);
}

var ssid = ssidArg ?? "WinDrop-TEST-" + RandomString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 4);
var pass = RandomString("abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789", 16);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; Log.Info("Ctrl+C: kapatılıyor..."); cts.Cancel(); };
if (duration > 0) cts.CancelAfter(TimeSpan.FromSeconds(duration));

var publisher = new WiFiDirectAdvertisementPublisher();
publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true;
publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
publisher.Advertisement.LegacySettings.IsEnabled = true;
publisher.Advertisement.LegacySettings.Ssid = ssid;
publisher.Advertisement.LegacySettings.Passphrase = new PasswordCredential { Password = pass };

var startedTcs = new TaskCompletionSource<bool>();
publisher.StatusChanged += (s, e) =>
{
    var msg = $"Publisher StatusChanged: Status={e.Status}, Error={e.Error} ({(int)e.Error})";
    if (e.Error != WiFiDirectError.Success || e.Status == WiFiDirectAdvertisementPublisherStatus.Aborted) Log.Error(msg);
    else Log.Good(msg);
    if (e.Status == WiFiDirectAdvertisementPublisherStatus.Started) startedTcs.TrySetResult(true);
    if (e.Status == WiFiDirectAdvertisementPublisherStatus.Aborted) startedTcs.TrySetResult(false);
};

var listener = new WiFiDirectConnectionListener();
listener.ConnectionRequested += (s, e) =>
{
    try
    {
        var req = e.GetConnectionRequest();
        Log.Good($"Bağlantı isteği: {req.DeviceInformation.Name} ({req.DeviceInformation.Id})");
        req.Dispose(); // yalnız loglanır, kabul edilmez
    }
    catch (Exception ex) { Log.Error("ConnectionRequested hata: " + ex.Message); }
};

Console.WriteLine();
Console.WriteLine("==================================================");
Console.WriteLine($"  SSID  : {ssid}");
Console.WriteLine($"  PAROLA: {pass}");
Console.WriteLine("==================================================");
Console.WriteLine($"QR metni: WIFI:T:WPA;S:{ssid};P:{pass};;");
Console.WriteLine();

Log.Info($"Başlangıç: wlan={NetStatus.WlanState()}");
Log.Info("Publisher.Start()...");
publisher.Start();
Log.Info($"Publisher durum (Start sonrası): {publisher.Status}");

var winner = await Task.WhenAny(startedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
var started = winner == startedTcs.Task && startedTcs.Task.Result;
if (started) Log.Good("SOFT AP BAŞLADI (Status=Started)");
else Log.Error("Soft AP Started olmadı (abort ya da 10 sn zaman aşımı): bkz. StatusChanged Error");

try
{
    while (!cts.IsCancellationRequested)
    {
        var inet = await NetStatus.InternetAsync();
        Log.Info($"AP IPv4: {NetStatus.ApInterfaces()} | WLAN: {NetStatus.WlanState()} | İnternet: {inet} | Publisher={publisher.Status}");
        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
    }
}
catch (OperationCanceledException) { }

publisher.Stop();
Log.Info($"publisher.Stop() çağrıldı, durum={publisher.Status}");
await Task.Delay(500);
Log.Info($"Kapandı. Son durum: wlan={NetStatus.WlanState()}");
GC.KeepAlive(listener);
