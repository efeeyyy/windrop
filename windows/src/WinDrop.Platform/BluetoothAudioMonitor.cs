using System.Runtime.InteropServices;

namespace WinDrop.Platform;

/// <summary>
/// Bluetooth ses çıkışının anlık durumu (PROTOCOL §4 <c>btAudio</c>).
/// <para><see cref="ActiveSession"/>: o an çalan ses oturumu var mı; <c>null</c> = anlaşılamadı.</para>
/// </summary>
public readonly record struct BluetoothAudioState(bool DefaultOutputIsBluetooth, bool? ActiveSession, string? DeviceName)  // DeviceName: tanı amaçlı "ad [veriyolu]"
{
    public static BluetoothAudioState None => new(false, false, null);

    /// <summary>
    /// <c>btAudio</c> = Bluetooth çıkış aygıtı varsayılan VE aktif ses oturumu var. Oturum durumu bilinmiyorsa
    /// (emin olunamıyor) Bluetooth çıkış varsayılansa <c>true</c> (güvenli taraf: BLE'ye yük bindirilmez).
    /// </summary>
    public bool BtAudio => DefaultOutputIsBluetooth && (ActiveSession ?? true);
}

/// <summary>Varsayılan ses çıkışının Bluetooth ses kullanıp kullanmadığını bildirir (testte sahtesi verilir).</summary>
public interface IBluetoothAudioMonitor
{
    BluetoothAudioState Current { get; }

    /// <summary><see cref="BluetoothAudioState.BtAudio"/> değeri değişince (herhangi bir iş parçacığında) gelir.</summary>
    event Action<BluetoothAudioState>? Changed;
}

/// <summary>
/// Windows Core Audio (MMDevice + oturum yöneticisi) ile <see cref="IBluetoothAudioMonitor"/>. Varsayılan çıkış aygıtının
/// <c>PKEY_Device_EnumeratorName</c> değeri <c>BTH…</c> (BTHENUM, BTHHFENUM, BTHLEDevice) ise Bluetooth sayılır; varsayılan
/// aygıt değişimi olayı (IMMNotificationClient) ve 2 sn'lik yoklama (aktif oturum için) durumu günceller. Yalnız okur.
/// </summary>
public sealed class BluetoothAudioMonitor : IBluetoothAudioMonitor, IDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private readonly Func<BluetoothAudioState?> _probe;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Task _loop;
    private readonly IDisposable? _registration;
    private BluetoothAudioState _current = BluetoothAudioState.None;

    /// <summary>Gerçek Core Audio ile.</summary>
    public BluetoothAudioMonitor() : this(CoreAudio.Read, DefaultInterval, registerNotifications: true) { }

    internal BluetoothAudioMonitor(Func<BluetoothAudioState?> probe, TimeSpan interval, bool registerNotifications = false)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
        _interval = interval;
        if (registerNotifications) _registration = CoreAudio.RegisterDeviceChanges(Poke);
        Evaluate();   // ilk değer eşzamanlı: hello ilk değeri taşısın
        _loop = Task.Run(LoopAsync);
    }

    public BluetoothAudioState Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<BluetoothAudioState>? Changed;

    /// <summary>Hemen yeniden ölç (aygıt değişimi olayı).</summary>
    internal void Poke()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* zaten bekleyen bir ölçüm var */ }
        catch (ObjectDisposedException) { }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _wake.WaitAsync(_interval, _cts.Token).ConfigureAwait(false);
                Evaluate();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Evaluate()
    {
        BluetoothAudioState? read;
        try { read = _probe(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or ArgumentException)
        {
            PlatformLog.Write($"Bluetooth ses durumu okunamadı: {ex.GetType().Name}: {ex.Message}");
            return;   // önceki değer korunur
        }
        if (read is not { } next) return;

        BluetoothAudioState prev;
        lock (_gate)
        {
            prev = _current;
            _current = next;
        }
        if (prev == next) return;
        PlatformLog.Write($"Bluetooth ses: varsayılanBT={next.DefaultOutputIsBluetooth} aktifOturum={next.ActiveSession?.ToString() ?? "?"} btAudio={next.BtAudio}");
        if (prev.BtAudio == next.BtAudio)
            return;   // btAudio aynı; dinleyiciler yalnız btAudio değişince uyarılır
        try { Changed?.Invoke(next); }
        catch (Exception ex) { PlatformLog.Write($"BluetoothAudioMonitor.Changed işleyicisi hata verdi: {ex.Message}"); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _registration?.Dispose();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _wake.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Core Audio COM çağrıları (yalnız okuma; en küçük arayüz tanımları).</summary>
internal static class CoreAudio
{
    private const int EDataFlowRender = 0;
    private const int ERoleConsole = 0;
    private const int ERoleCommunications = 2;
    private const uint ClsctxAll = 0x17;
    private const int StgmRead = 0;
    private const int AudioSessionStateActive = 1;
    private const ushort VtLpwstr = 31;
    private const ushort VtBstr = 8;
    private static readonly int ENotFound = unchecked((int)0x80070490);

    private static readonly PropertyKey EnumeratorName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 24);
    private static readonly PropertyKey FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly Guid IidSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    /// <summary>Varsayılan oynatma aygıtlarının (konsol + iletişim) durumu; okunamazsa null (çağıran öncekini korur).</summary>
    public static BluetoothAudioState? Read()
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = CreateEnumerator();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bluetooth = false;
            bool? active = false;
            string? name = null;
            foreach (var role in new[] { ERoleConsole, ERoleCommunications })
            {
                IMMDevice? device = null;
                try
                {
                    try { enumerator.GetDefaultAudioEndpoint(EDataFlowRender, role, out device); }
                    catch (COMException ex) when (ex.HResult == ENotFound) { continue; }   // çıkış aygıtı yok
                    device.GetId(out var id);
                    if (!seen.Add(id)) continue;

                    var enumName = ReadString(device, EnumeratorName);
                    name ??= $"{ReadString(device, FriendlyName) ?? "?"} [{enumName ?? "?"}]";   // tanı için (yalnız ad/veriyolu)
                    if (enumName is null || !enumName.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) continue;
                    bluetooth = true;
                    var a = HasActiveSession(device);
                    if (a is true) active = true;
                    else if (a is null && active is not true) active = null;
                }
                finally { Release(device); }
            }
            return new BluetoothAudioState(bluetooth, bluetooth ? active : false, name);
        }
        finally { Release(enumerator); }
    }

    /// <summary>Aygıtta çalan (Active) sistem sesi dışı oturum var mı; okunamazsa null.</summary>
    private static bool? HasActiveSession(IMMDevice device)
    {
        object? managerObj = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            var iid = IidSessionManager2;
            device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out managerObj);
            var manager = (IAudioSessionManager2)managerObj;
            manager.GetSessionEnumerator(out sessions);
            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                IAudioSessionControl2? session = null;
                try
                {
                    sessions.GetSession(i, out session);
                    session.GetState(out var state);
                    if (state != AudioSessionStateActive) continue;   // Inactive/Expired sayılmaz
                    if (session.IsSystemSoundsSession() == 0) continue;   // S_OK = sistem sesleri
                    return true;
                }
                finally { Release(session); }
            }
            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            PlatformLog.Write($"Ses oturumları okunamadı: {ex.Message}");
            return null;
        }
        finally
        {
            Release(sessions);
            Release(managerObj);
        }
    }

    private static string? ReadString(IMMDevice device, PropertyKey key)
    {
        IPropertyStore? store = null;
        try
        {
            device.OpenPropertyStore(StgmRead, out store);
            var k = key;
            store.GetValue(ref k, out var value);
            try
            {
                return value.Vt is VtLpwstr or VtBstr && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                if (value.Vt == VtLpwstr && value.Pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(value.Pointer);
                else if (value.Vt == VtBstr && value.Pointer != IntPtr.Zero) Marshal.FreeBSTR(value.Pointer);
            }
        }
        catch (COMException) { return null; }
        finally { Release(store); }
    }

    private static IMMDeviceEnumerator CreateEnumerator() =>
        (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!)!;

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    // ---------------------------------------------------------------- varsayılan aygıt değişimi olayı

    /// <summary>Varsayılan/aygıt durumu değişimlerinde <paramref name="onChanged"/> çağrılır (COM iş parçacığında; yalnız işaret koy).</summary>
    public static IDisposable? RegisterDeviceChanges(Action onChanged)
    {
        try
        {
            var enumerator = CreateEnumerator();
            var client = new NotificationClient(onChanged);
            enumerator.RegisterEndpointNotificationCallback(client);
            return new Registration(enumerator, client);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            PlatformLog.Write($"Ses aygıtı değişim olayı kaydedilemedi (yoklama sürer): {ex.Message}");
            return null;
        }
    }

    private sealed class Registration(IMMDeviceEnumerator enumerator, NotificationClient client) : IDisposable
    {
        public void Dispose()
        {
            try { enumerator.UnregisterEndpointNotificationCallback(client); }
            catch (COMException) { }
            Release(enumerator);
        }
    }

    private sealed class NotificationClient(Action onChanged) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, int newState) => onChanged();
        public void OnDeviceAdded(string deviceId) => onChanged();
        public void OnDeviceRemoved(string deviceId) => onChanged();
        public void OnDefaultDeviceChanged(int flow, int role, string defaultDeviceId) => onChanged();
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { /* gürültülü; yoklama yeter */ }
    }

    // ---------------------------------------------------------------- COM tanımları (vtable sırası önemli)

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct PropertyKey(Guid fmtid, uint pid)
    {
        public readonly Guid Fmtid = fmtid;
        public readonly uint Pid = pid;
    }

    /// <summary>PROPVARIANT: 8 bayt başlık + birleşim (x64'te 24 bayt).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort Vt;
        private ushort _r1;
        private ushort _r2;
        private ushort _r3;
        public IntPtr Pointer;
        private IntPtr _extra;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, [MarshalAs(UnmanagedType.IUnknown)] out object devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IMMNotificationClient client);
        void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, out IntPtr audioVolume);
        // IAudioSessionManager2
        void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        void GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr value, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr value, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid overrideValue, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(int optOut);
    }
}
