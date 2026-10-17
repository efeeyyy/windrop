namespace WinDrop.BleProbe;

/// <summary>
/// Ölçüm aracı için sabit UUID'ler (rastgele üretildi, 2026-10-16).
/// Ürün protokolü değildir; docs/BLE-PROBE.md ile aynı tutulmalı.
/// </summary>
public static class ProbeUuids
{
    /// <summary>
    /// Varsayılan servis UUID'si: rastgele 32 bit değer (351fca71) Bluetooth taban UUID'sine gömülü.
    /// Neden: bu makinede (Realtek RTL8852BE) 128 bit özel servis UUID'si ile
    /// GattServiceProvider reklamı hep "Aborted" oluyor; 16/32 bit biçim "Started" oluyor
    /// (bkz. docs/BLE-PROBE.md, "Bilinen sorun").
    /// </summary>
    public static readonly Guid Service = Guid.Parse("351fca71-0000-1000-8000-00805f9b34fb");

    /// <summary>Tam 128 bit rastgele servis UUID'si; <c>--uuid128</c> ile denenir.</summary>
    public static readonly Guid Service128 = Guid.Parse("351fca71-398a-4d79-b872-bcb6f4101984");

    public static readonly Guid Info = Guid.Parse("d66fb783-a725-47aa-8e14-c00f66435c36");
    public static readonly Guid Rx = Guid.Parse("c4204721-2c6b-4c83-9c8c-c4985b560f40");
    public static readonly Guid Tx = Guid.Parse("a19b2217-bc01-4b50-9b4f-77ea776974cf");
    public static readonly Guid Ctrl = Guid.Parse("8daa0d14-5554-4b2a-8e35-525cbf9e73b7");
}
