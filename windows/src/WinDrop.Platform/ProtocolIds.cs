namespace WinDrop.Platform;

/// <summary>
/// PROTOCOL §4.1 BLE UUID'leri (2026-10-17'de rastgele üretildi; ölçüm aracının <c>ProbeUuids</c> değerlerinden bağımsız).
/// </summary>
public static class ProtocolIds
{
    /// <summary>128 bit GATT servisi (birincil).</summary>
    public static readonly Guid Service = Guid.Parse("493c70b6-634d-4935-be1f-b79b1d05dbd8");

    /// <summary>c2p: iPhone→PC, Write Without Response (+ Write).</summary>
    public static readonly Guid C2p = Guid.Parse("c235bdc3-b25a-49a5-963b-5040e65b708d");

    /// <summary>p2c: PC→iPhone, Notify.</summary>
    public static readonly Guid P2c = Guid.Parse("ae26ead2-8247-4508-bcb6-31d11568bf07");

    /// <summary>
    /// Yedek servis UUID'si (SIG taban UUID'sine gömülü 32 bit). Yalnız bu makinedeki gibi 128 bit reklam
    /// <c>Aborted</c> olursa kullanılır (2026-10-16 ölçümü: bu biçim Started oluyor ve iPhone bağlandı).
    /// Karakteristikler aynıdır; iPhone iki servis UUID'sinden birini arar.
    /// </summary>
    public static readonly Guid FallbackService = Guid.Parse("6c5a0f3d-0000-1000-8000-00805f9b34fb");

    /// <summary>İşaret reklamının üretici kimliği (0xFFFF = test/ayrılmamış; iPhone tarafı yalnız "WD" ön ekine bakar).</summary>
    public const ushort MarkerCompanyId = 0xFFFF;

    /// <summary>
    /// İşaret yükü: <c>"WD" || sürüm (1 B) || UTF-8 "WinDrop-"+PC adı</c>, toplam en çok 19 bayt (3 + 16) (reklam 31 baytı aşmasın).
    /// </summary>
    public static byte[] BuildMarker(string pcName)
    {
        var ascii = new string((pcName ?? "").Where(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '-').ToArray());
        if (ascii.Length == 0) ascii = "PC";
        var label = ("WinDrop-" + ascii)[..Math.Min(16, 8 + ascii.Length)];
        return [(byte)'W', (byte)'D', 1, .. System.Text.Encoding.ASCII.GetBytes(label)];
    }
}
