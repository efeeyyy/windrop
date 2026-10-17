using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace WinDrop.BleProbe;

internal static class AdapterCheck
{
    /// <summary>Çıkış kodu: 0 = adapter var, 2 = adapter yok / hata.</summary>
    public static async Task<int> RunAsync()
    {
        BluetoothAdapter? adapter;
        try
        {
            adapter = await BluetoothAdapter.GetDefaultAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Bluetooth adapter sorgulanamadı: {ex.Message}");
            return 2;
        }

        if (adapter is null)
        {
            Log.Error("Bluetooth adapter bulunamadı (aygıt yok, sürücü yok veya Bluetooth devre dışı).");
            return 2;
        }

        Console.WriteLine($"AdapterId                    : {adapter.DeviceId}");
        Console.WriteLine($"BluetoothAddress             : {FormatAddress(adapter.BluetoothAddress)}");
        Console.WriteLine($"IsLowEnergySupported         : {adapter.IsLowEnergySupported}");
        Console.WriteLine($"IsPeripheralRoleSupported    : {adapter.IsPeripheralRoleSupported}");
        Console.WriteLine($"IsCentralRoleSupported       : {adapter.IsCentralRoleSupported}");
        Console.WriteLine($"IsAdvertisementOffloadSupp.  : {adapter.IsAdvertisementOffloadSupported}");
        Console.WriteLine($"MaxAdvertisementDataLength   : {adapter.MaxAdvertisementDataLength}");
        Console.WriteLine($"IsExtendedAdvertisingSupp.   : {adapter.IsExtendedAdvertisingSupported}");

        try
        {
            var radio = await adapter.GetRadioAsync();
            Console.WriteLine(radio is null
                ? "Radyo                        : alınamadı"
                : $"Radyo                        : {radio.Name}, durum = {radio.State}");
            if (radio is not null && radio.State != RadioState.On)
                Log.Error("Bluetooth radyosu açık değil; Windows ayarlarından açın.");
        }
        catch (Exception ex)
        {
            Log.Error($"Radyo durumu okunamadı: {ex.Message}");
        }

        if (!adapter.IsLowEnergySupported)
            Log.Error("Bu adapter Bluetooth LE desteklemiyor.");
        if (!adapter.IsPeripheralRoleSupported)
            Log.Error("Bu adapter peripheral (GATT sunucusu) rolünü desteklemiyor; sunucu modu çalışmaz.");

        return 0;
    }

    private static string FormatAddress(ulong a) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((a >> (8 * (5 - i))) & 0xFF).ToString("X2")));
}
