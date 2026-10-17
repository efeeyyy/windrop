using System.Security.Cryptography;
using WinDrop.Core.Noise;

namespace WinDrop.Platform;

/// <summary>
/// Kalıcı X25519 cihaz anahtarı (PROTOCOL §0). <c>identity.bin</c>: DPAPI(CurrentUser) ile korunan
/// <c>sürüm(1) || özel anahtar(32)</c>. Yüklenen anahtar dışa aktarılamaz.
/// </summary>
public sealed class IdentityStore
{
    private const string Purpose = "identity.v1";
    private const byte Version = 1;

    private readonly string _path;
    private readonly object _lock = new();
    private NoiseKeyPair? _keyPair;

    public IdentityStore(string? rootDirectory = null)
    {
        _path = Path.Combine(rootDirectory ?? Storage.DefaultRoot, "identity.bin");
    }

    public string FilePath => _path;

    /// <summary>Anahtarı yükler; yoksa oluşturup kaydeder. Döndürülen nesne store'a aittir (Dispose etmeyin).</summary>
    /// <exception cref="InvalidDataException">Dosya bozuk veya bu kullanıcıya ait değil. Sessizce yeniden üretilmez
    /// (eşleştirmeler boşa giderdi); <see cref="Reset"/> bilinçli çağrılmalı.</exception>
    public NoiseKeyPair LoadOrCreate()
    {
        lock (_lock)
        {
            if (_keyPair != null) return _keyPair;
            _keyPair = File.Exists(_path) ? Load() : Create();
            return _keyPair;
        }
    }

    public byte[] PublicKey => LoadOrCreate().PublicKey;

    /// <summary>Cihaz kimliği = açık anahtarın SHA-256'sının ilk 16 baytı.</summary>
    public byte[] DeviceId => ComputeDeviceId(PublicKey);

    public static byte[] ComputeDeviceId(ReadOnlySpan<byte> publicKey) =>
        SHA256.HashData(publicKey)[..16];

    /// <summary>Anahtarı siler (bir sonraki <see cref="LoadOrCreate"/> yenisini üretir). Yeniden eşleştirme gerektirir.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _keyPair?.Dispose();
            _keyPair = null;
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    private NoiseKeyPair Create()
    {
        byte[] priv;
        using (var fresh = NoiseKeyPair.Generate(exportable: true))
            priv = fresh.ExportPrivateKey();
        try
        {
            var plain = new byte[1 + priv.Length];
            plain[0] = Version;
            priv.CopyTo(plain, 1);
            Storage.WriteAtomic(_path, Storage.Protect(plain, Purpose));
            CryptographicOperations.ZeroMemory(plain);
            return NoiseKeyPair.FromPrivateKey(priv);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(priv);
        }
    }

    private NoiseKeyPair Load()
    {
        byte[] plain;
        try
        {
            plain = Storage.Unprotect(File.ReadAllBytes(_path), Purpose);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("identity.bin okunamadı (bozuk veya başka kullanıcıya ait).", ex);
        }
        try
        {
            if (plain.Length != 1 + NoiseKeyPair.KeySize || plain[0] != Version)
                throw new InvalidDataException("identity.bin biçimi geçersiz.");
            return NoiseKeyPair.FromPrivateKey(plain.AsSpan(1));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
