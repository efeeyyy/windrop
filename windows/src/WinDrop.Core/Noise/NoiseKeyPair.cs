using NSec.Cryptography;

namespace WinDrop.Core.Noise;

/// <summary>
/// X25519 statik/ephemeral anahtar çifti (NSec/libsodium). Özel anahtar varsayılan olarak
/// <b>dışa aktarılamaz</b> (<see cref="KeyExportPolicies.None"/>): Noise işlemleri anahtarı NSec içinde
/// kullanır, ham bayt olarak yönetilen belleğe çıkarmaz.
/// <para>Dışa aktarma politikası bilinçli olarak tek yerde açılabilir: <c>exportable: true</c> — kalıcı
/// cihaz anahtarı DPAPI ile saklanacaksa (ilk üretimde bir kez <see cref="ExportPrivateKey"/> çağrılıp
/// DPAPI'ye verilir). Test vektörleri anahtarı <see cref="FromPrivateKey"/> ile <i>içe aktarır</i>;
/// içe aktarma dışa aktarma politikası gerektirmez, yani testler de <c>None</c> ile çalışır.</para>
/// </summary>
public sealed class NoiseKeyPair : IDisposable
{
    public const int KeySize = 32;

    private readonly Key _key;

    internal Key Key => _key;

    /// <summary>32 baytlık açık anahtar (kopya).</summary>
    public byte[] PublicKey { get; }

    private NoiseKeyPair(Key key)
    {
        _key = key;
        PublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    private static KeyCreationParameters Params(bool exportable) => new()
    {
        ExportPolicy = exportable ? KeyExportPolicies.AllowPlaintextExport : KeyExportPolicies.None,
    };

    /// <param name="exportable">Yalnız cihaz anahtarını DPAPI ile kalıcılaştırmak için true.</param>
    public static NoiseKeyPair Generate(bool exportable = false) =>
        new(Key.Create(KeyAgreementAlgorithm.X25519, Params(exportable)));

    /// <summary>Ham 32 bayt özel anahtarı içe aktarır (kalıcı anahtarı DPAPI'den yüklerken veya test vektörü).</summary>
    public static NoiseKeyPair FromPrivateKey(ReadOnlySpan<byte> privateKey, bool exportable = false)
    {
        if (privateKey.Length != KeySize)
            throw new ArgumentException("X25519 özel anahtarı 32 bayt olmalı.", nameof(privateKey));
        return new(Key.Import(KeyAgreementAlgorithm.X25519, privateKey, KeyBlobFormat.RawPrivateKey, Params(exportable)));
    }

    /// <summary>Ham özel anahtarı döndürür; yalnız <c>exportable: true</c> ile oluşturulmuşsa çalışır.</summary>
    /// <exception cref="InvalidOperationException">Anahtar dışa aktarılamaz.</exception>
    public byte[] ExportPrivateKey()
    {
        if (_key.ExportPolicy != KeyExportPolicies.AllowPlaintextExport)
            throw new InvalidOperationException("Anahtar dışa aktarılamaz olarak oluşturuldu.");
        return _key.Export(KeyBlobFormat.RawPrivateKey);
    }

    public void Dispose() => _key.Dispose();
}
