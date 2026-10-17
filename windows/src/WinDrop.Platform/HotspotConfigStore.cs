using System.Security.Cryptography;
using System.Text;

namespace WinDrop.Platform;

/// <summary>Mobil etkin nokta SSID/parolası. Gizli değer: <see cref="ToString"/> parolayı yazmaz.</summary>
public sealed record HotspotConfig(string Ssid, string Passphrase)
{
    public override string ToString() => $"HotspotConfig {{ Ssid = {Ssid}, Passphrase = *** }}";
}

/// <summary>
/// PROTOCOL §6.2: ilk çağrıda rastgele SSID (<c>WinDrop-</c> + 32 bit base32) ve parola (128 bit base32, 26 karakter)
/// üretilir, <c>hotspot.bin</c>'de DPAPI ile saklanır; türetilmez.
/// </summary>
public sealed class HotspotConfigStore
{
    private const string Purpose = "hotspot.v1";
    public const string SsidPrefix = "WinDrop-";

    private readonly string _path;
    private readonly object _lock = new();
    private HotspotConfig? _config;

    public HotspotConfigStore(string? rootDirectory = null)
    {
        _path = Path.Combine(rootDirectory ?? Storage.DefaultRoot, "hotspot.bin");
    }

    public string FilePath => _path;

    public HotspotConfig GetOrCreate()
    {
        lock (_lock)
        {
            return _config ??= TryLoad() ?? CreateAndSave();
        }
    }

    /// <summary>
    /// Parolayı yeniler (SSID aynı kalır) ve saklar. PROTOCOL §6.2: eşli/tanıdık olmayan bir eşe etkin nokta bilgisi
    /// verildiyse, aktarım bitince (iptal/hata dahil) çağrılır; eşli cihazların <c>hotspot</c> iletisi güncel parolayı taşır.
    /// </summary>
    public HotspotConfig RotatePassphrase()
    {
        lock (_lock)
        {
            var current = _config ??= TryLoad() ?? CreateAndSave();
            var fresh = current with { Passphrase = Base32.Encode(RandomNumberGenerator.GetBytes(16)) };
            Save(fresh);
            return _config = fresh;
        }
    }

    private HotspotConfig CreateAndSave()
    {
        var cfg = Generate();
        Save(cfg);
        return cfg;
    }

    private void Save(HotspotConfig cfg)
    {
        var plain = Encoding.UTF8.GetBytes(cfg.Ssid + "\n" + cfg.Passphrase);
        Storage.WriteAtomic(_path, Storage.Protect(plain, Purpose));
        CryptographicOperations.ZeroMemory(plain);
    }

    private HotspotConfig? TryLoad()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var plain = Storage.Unprotect(File.ReadAllBytes(_path), Purpose);
            var parts = Encoding.UTF8.GetString(plain).Split('\n');
            CryptographicOperations.ZeroMemory(plain);
            if (parts.Length == 2 && IsValid(parts[0], parts[1])) return new HotspotConfig(parts[0], parts[1]);
        }
        catch (CryptographicException) { }
        return null; // bozuk: yeniden üretilir (iPhone'lara eşleştirmeyle yeniden gönderilir)
    }

    internal static bool IsValid(string ssid, string pass) =>
        ssid.StartsWith(SsidPrefix, StringComparison.Ordinal) && ssid.Length == SsidPrefix.Length + 7 && pass.Length >= 22;

    internal static HotspotConfig Generate()
    {
        var ssid = SsidPrefix + Base32.Encode(RandomNumberGenerator.GetBytes(4)); // 32 bit -> 7 karakter
        var pass = Base32.Encode(RandomNumberGenerator.GetBytes(16));             // 128 bit -> 26 karakter
        return new HotspotConfig(ssid, pass);
    }
}

/// <summary>RFC 4648 base32 (büyük harf, dolgusuz).</summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}
