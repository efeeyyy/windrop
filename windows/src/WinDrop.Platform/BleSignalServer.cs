using System.Threading.Channels;
using WinDrop.Core.Session;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace WinDrop.Platform;

public enum BleAdvertisingMode
{
    None,
    /// <summary>128 bit servis UUID'si (<see cref="ProtocolIds.Service"/>) ile reklam Started oldu.</summary>
    Primary128,
    /// <summary>128 bit Aborted oldu; aynı karakteristiklerle SIG tabanlı 32 bit servis (<see cref="ProtocolIds.FallbackService"/>) ile Started.</summary>
    FallbackSig32,
}

/// <summary>
/// PROTOCOL §3: PC = GATT sunucusu (<c>c2p</c>: WriteWithoutResponse + Write, <c>p2c</c>: Notify). Her istemci için bir
/// <see cref="IBleLink"/> (gelen yazmalar → sıralı kuyruk; giden → o istemciye <c>NotifyValueAsync</c>) üretir ve
/// <see cref="SignalSessionAcceptor"/>'a verir. Çalışan GATT kodu <c>WinDrop.BleProbe/ProbeServer.cs</c>'ten uyarlanmıştır.
/// <para>Reklam: önce 128 bit servis UUID'si; <c>Aborted</c> olursa (Realtek RTL8852BE'de olur) servis, ölçümde çalışmış
/// SIG tabanlı 32 bit UUID altında yeniden kurulur. GattServiceProvider reklamdan servis UUID'sini çıkaramaz; eşleşmiş
/// iPhone zaten bekleyen bağlantıyla gelir (PROTOCOL §3), ilk keşif yerel ad + bu UUID'dir.</para>
/// </summary>
public sealed class BleSignalServer : IAsyncDisposable
{
    private static readonly TimeSpan AdvertiseWait = TimeSpan.FromSeconds(3);

    private readonly SignalSessionAcceptor _acceptor;
    private readonly object _gate = new();
    private readonly Dictionary<string, ClientConn> _conns = [];
    private readonly CancellationTokenSource _stop = new();
    private Host? _host;
    private BluetoothLEAdvertisementPublisher? _marker;

    private readonly string _localName;

    public BleSignalServer(SignalSessionAcceptor acceptor, string? localName = null)
    {
        ArgumentNullException.ThrowIfNull(acceptor);
        _acceptor = acceptor;
        _localName = localName ?? Environment.MachineName;
    }

    public BleAdvertisingMode Mode { get; private set; }

    /// <summary>Adaptör/GATT/reklam kurulumu. Başarısızlık günlüğe yazılır ve <see cref="BleAdvertisingMode.None"/> döner (uygulama çalışmaya devam eder).</summary>
    public async Task<BleAdvertisingMode> StartAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null) { PlatformLog.Write("BLE: Bluetooth adaptörü yok."); return Mode = BleAdvertisingMode.None; }
            if (!adapter.IsLowEnergySupported) { PlatformLog.Write("BLE: adaptör Bluetooth LE desteklemiyor."); return Mode = BleAdvertisingMode.None; }
            if (!adapter.IsPeripheralRoleSupported) { PlatformLog.Write("BLE: adaptör çevre birimi (GATT sunucusu) rolünü desteklemiyor."); return Mode = BleAdvertisingMode.None; }

            var primary = await BuildHostAsync(ProtocolIds.Service);
            if (primary is null) return Mode = BleAdvertisingMode.None;
            _host = primary;
            if (await AdvertiseAsync(primary))
            {
                PlatformLog.Write("BLE reklam: Started (birincil mod: 128 bit servis UUID'si)");
                StartMarkerAdvertisement();
                return Mode = BleAdvertisingMode.Primary128;
            }

            PlatformLog.Write($"BLE reklam: 128 bit servis UUID'si ile {primary.Provider.AdvertisementStatus} (Aborted bu donanımda bilinen sorun); yedek moda düşülüyor.");
            try { primary.Provider.StopAdvertising(); } catch (Exception) { }
            _host = null;

            var fallback = await BuildHostAsync(ProtocolIds.FallbackService);
            if (fallback is null) return Mode = BleAdvertisingMode.None;
            _host = fallback;
            if (await AdvertiseAsync(fallback))
            {
                PlatformLog.Write("BLE reklam: Started (yedek mod: SIG tabanlı 32 bit servis UUID'si; bağlanılabilir + keşfedilebilir, yerel ad = bilgisayar adı)");
                StartMarkerAdvertisement();
                return Mode = BleAdvertisingMode.FallbackSig32;
            }
            PlatformLog.Write($"BLE reklam: yedek modda da başlamadı ({fallback.Provider.AdvertisementStatus}).");
            _host = null;
            return Mode = BleAdvertisingMode.None;
        }
        catch (Exception ex)
        {
            PlatformLog.Write($"BLE başlatılamadı: {ex.GetType().Name}: {ex.Message}");
            return Mode = BleAdvertisingMode.None;
        }
    }

    /// <summary>
    /// Yerel ad işareti (PROTOCOL §3): <c>GattServiceProvider</c> reklamına yerel ad koyamaz (Windows bilgisayar adını kullanır) ve
    /// <c>BluetoothLEAdvertisementPublisher</c> <c>LocalName</c>'i bu makinede reddeder (ArgumentException, 2026-10-17 ölçümü).
    /// Bu yüzden ikinci, bağlanılamaz bir reklam yayınlanır: üretici verisi <c>0xFFFF</c> = <c>"WD" || sürüm || "WinDrop-<ad>"</c>;
    /// iPhone ön planda filtresiz tararken bunu (veya servis UUID'sini) görüp PC'yi tanır. En iyi çaba; hata günlüğe yazılır.
    /// </summary>
    private void StartMarkerAdvertisement()
    {
        try
        {
            var writer = new DataWriter();
            writer.WriteBytes(ProtocolIds.BuildMarker(_localName));
            var pub = new BluetoothLEAdvertisementPublisher();
            pub.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData { CompanyId = ProtocolIds.MarkerCompanyId, Data = writer.DetachBuffer() });
            pub.StatusChanged += (_, e) => PlatformLog.Write($"BLE işaret reklamı: {e.Status}" + (e.Error != BluetoothError.Success ? $" ({e.Error})" : ""));
            pub.Start();
            _marker = pub;
        }
        catch (Exception ex)
        {
            PlatformLog.Write($"BLE işaret reklamı başlatılamadı: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<Host?> BuildHostAsync(Guid serviceUuid)
    {
        var created = await GattServiceProvider.CreateAsync(serviceUuid);
        if (created.Error != BluetoothError.Success)
        {
            PlatformLog.Write($"BLE: GattServiceProvider.CreateAsync başarısız: {created.Error}");
            return null;
        }
        var provider = created.ServiceProvider;

        var c2p = await provider.Service.CreateCharacteristicAsync(ProtocolIds.C2p, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse | GattCharacteristicProperties.Write,
            UserDescription = "c2p",
        });
        if (c2p.Error != BluetoothError.Success) { PlatformLog.Write($"BLE: c2p oluşturulamadı: {c2p.Error}"); return null; }

        var p2c = await provider.Service.CreateCharacteristicAsync(ProtocolIds.P2c, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Notify,
            UserDescription = "p2c",
        });
        if (p2c.Error != BluetoothError.Success) { PlatformLog.Write($"BLE: p2c oluşturulamadı: {p2c.Error}"); return null; }

        var host = new Host(provider, c2p.Characteristic, p2c.Characteristic);
        host.C2p.WriteRequested += OnWriteRequested;
        host.P2c.SubscribedClientsChanged += (s, _) => PlatformLog.Write($"BLE: p2c abone sayısı {s.SubscribedClients.Count}");
        return host;
    }

    private static async Task<bool> AdvertiseAsync(Host host)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Provider.AdvertisementStatusChanged += (_, e) =>
        {
            PlatformLog.Write($"BLE reklam durumu: {e.Status}" + (e.Error != BluetoothError.Success ? $" ({e.Error})" : ""));
            if (e.Status == GattServiceProviderAdvertisementStatus.Started) started.TrySetResult(true);
        };
        host.Provider.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true });
        await Task.WhenAny(started.Task, Task.Delay(AdvertiseWait));
        return host.Provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started;
    }

    // ---------------------------------------------------------------- c2p

    private async void OnWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();   // eşzamanlı: ilk await'ten önce
        ClientConn? conn = null;
        long slot = -1;
        try
        {
            conn = GetOrCreateConn(args.Session);
            slot = conn.Inbox.Reserve();
            var request = await args.GetRequestAsync();
            if (request is null) { conn.Inbox.Complete(slot, null); return; }
            var bytes = new byte[request.Value.Length];
            DataReader.FromBuffer(request.Value).ReadBytes(bytes);
            conn.Inbox.Complete(slot, bytes);
            if (request.Option == GattWriteOption.WriteWithResponse) request.Respond();
        }
        catch (Exception ex)
        {
            PlatformLog.Write($"BLE: c2p yazması işlenemedi: {ex.Message}");
            if (conn is not null && slot >= 0) conn.Inbox.Complete(slot, null);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private ClientConn GetOrCreateConn(GattSession session)
    {
        var key = session.DeviceId.Id;
        lock (_gate)
        {
            if (_conns.TryGetValue(key, out var existing) && !existing.IsClosed
                && existing.Session.SessionStatus != GattSessionStatus.Closed)
                return existing;

            var conn = new ClientConn(this, session, key);
            _conns[key] = conn;
            session.SessionStatusChanged += (_, e) =>
            {
                if (e.Status == GattSessionStatus.Closed) conn.Close();
            };
            PlatformLog.Write($"BLE: yeni istemci bağlantısı (MaxPduSize={session.MaxPduSize})");
            conn.Run = Task.Run(async () =>
            {
                try { await _acceptor.RunLinkWithResyncAsync(conn, _stop.Token); }
                finally
                {
                    conn.Close();
                    lock (_gate)
                    {
                        if (_conns.TryGetValue(key, out var cur) && ReferenceEquals(cur, conn)) _conns.Remove(key);
                    }
                    PlatformLog.Write("BLE: istemci bağlantısı kapandı.");
                }
            });
            return conn;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        try { _host?.Provider.StopAdvertising(); } catch (Exception) { }
        try { _marker?.Stop(); } catch (Exception) { }
        ClientConn[] all;
        lock (_gate) all = [.. _conns.Values];
        foreach (var c in all) c.Close();
        foreach (var c in all)
        {
            if (c.Run is null) continue;
            try { await c.Run.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        }
        _host = null;
    }

    private sealed record Host(GattServiceProvider Provider, GattLocalCharacteristic C2p, GattLocalCharacteristic P2c);

    /// <summary>Tek istemci için <see cref="IBleLink"/>.</summary>
    private sealed class ClientConn(BleSignalServer server, GattSession session, string key) : IBleLink
    {
        private static readonly TimeSpan SubscribeWait = TimeSpan.FromSeconds(5);
        private readonly CancellationTokenSource _closed = new();

        public GattSession Session { get; } = session;
        public OrderedInbox Inbox { get; } = new();
        public Task? Run { get; set; }
        public bool IsClosed => _closed.IsCancellationRequested;
        public string? LinkId => key;

        public int Mtu
        {
            get
            {
                var mtu = (int)Session.MaxPduSize;
                var client = FindClient();
                if (client is not null) mtu = Math.Min(mtu, (int)client.MaxNotificationSize + 3);
                return Math.Max(23, mtu);
            }
        }

        private GattSubscribedClient? FindClient() =>
            server._host?.P2c.SubscribedClients.FirstOrDefault(c => c.Session.DeviceId.Id == key);

        public async ValueTask SendAsync(ReadOnlyMemory<byte> gattPayload, CancellationToken cancellationToken)
        {
            var host = server._host ?? throw new IOException("BLE sunucusu kapalı.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            GattSubscribedClient? client = null;
            var deadline = DateTime.UtcNow + SubscribeWait;
            while ((client = FindClient()) is null)
            {
                if (DateTime.UtcNow > deadline) throw new IOException("İstemci p2c bildirimlerine abone olmadı.");
                await Task.Delay(50, cts.Token).ConfigureAwait(false);
            }

            GattCommunicationStatus status = GattCommunicationStatus.Unreachable;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var writer = new DataWriter();
                writer.WriteBytes(gattPayload.ToArray());
                var result = await host.P2c.NotifyValueAsync(writer.DetachBuffer(), client);
                status = result.Status;
                if (status == GattCommunicationStatus.Success) return;
                if (IsClosed) break;
                await Task.Delay(30 * (attempt + 1), cts.Token).ConfigureAwait(false);
            }
            throw new IOException($"BLE bildirimi gönderilemedi ({status}).");
        }

        /// <summary>PROTOCOL §3a: <c>WDR</c> tek bildirim; abone yoksa beklemeden atlanır.</summary>
        public ValueTask SendResetAsync(CancellationToken cancellationToken) => NotifyMarkerAsync(BleResync.Marker, "WDR");

        /// <summary>DECISIONS 2: <c>WDU</c> tek bildirim; abone yoksa beklemeden atlanır.</summary>
        public ValueTask SendUnknownAsync(CancellationToken cancellationToken) => NotifyMarkerAsync(BleResync.UnknownMarker, "WDU");

        private async ValueTask NotifyMarkerAsync(byte[] marker, string name)
        {
            var host = server._host;
            var client = FindClient();
            if (host is null || client is null) return;
            var writer = new DataWriter();
            writer.WriteBytes(marker);
            var result = await host.P2c.NotifyValueAsync(writer.DetachBuffer(), client);
            if (result.Status != GattCommunicationStatus.Success)
                throw new IOException($"{name} bildirimi gönderilemedi ({result.Status}).");
        }

        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var reader = Inbox.Reader;
            try
            {
                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (reader.TryRead(out var item)) return item;
                }
            }
            catch (ChannelClosedException) { }
            return null;
        }

        public void Close()
        {
            try { _closed.Cancel(); } catch (ObjectDisposedException) { }
            Inbox.Close();
        }
    }
}
