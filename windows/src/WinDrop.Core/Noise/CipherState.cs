using System.Buffers.Binary;
using NSec.Cryptography;

namespace WinDrop.Core.Noise;

/// <summary>
/// Noise CipherState (rev 34, §5.1): ChaCha20-Poly1305, nonce = 4 sıfır bayt || 64 bit little-endian sayaç.
/// İş parçacığı güvenli değildir; her yön için ayrı örnek ve tek yazar/okuyucu varsayılır.
/// </summary>
public sealed class CipherState : IDisposable
{
    public const int KeySize = 32;
    public const int TagSize = 16;
    /// <summary>Noise mesajı üst sınırı (Noise §3: 65535 bayt).</summary>
    public const int MaxMessageSize = 65535;

    private static readonly AeadAlgorithm Aead = AeadAlgorithm.ChaCha20Poly1305;

    private Key? _key;
    private ulong _nonce;

    public bool HasKey => _key is not null;

    /// <summary>Sıradaki nonce sayacı (test/tanılama).</summary>
    public ulong Nonce => _nonce;

    internal CipherState() { }

    internal void InitializeKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize) throw new ArgumentException("Anahtar 32 bayt olmalı.", nameof(key));
        _key?.Dispose();
        _key = Key.Import(Aead, key, KeyBlobFormat.RawSymmetricKey); // ExportPolicy.None: anahtar geri alınamaz
        _nonce = 0;
    }

    /// <summary>EncryptWithAd: anahtar yoksa düz metni aynen döndürür (sayaç artmaz).</summary>
    public byte[] EncryptWithAd(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext)
    {
        if (_key is null) return plaintext.ToArray();
        if (_nonce == ulong.MaxValue)
            throw new NoiseException(NoiseErrorKind.NonceExhausted, "Nonce sayacı tükendi.");
        if (plaintext.Length + TagSize > MaxMessageSize)
            throw new NoiseException(NoiseErrorKind.MessageTooLarge, "Şifreli mesaj 65535 baytı aşar.");

        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, _nonce);
        var output = new byte[plaintext.Length + TagSize];
        Aead.Encrypt(_key, nonce, associatedData, plaintext, output);
        _nonce++;
        return output;
    }

    /// <summary>DecryptWithAd: anahtar yoksa şifreli metni aynen döndürür. Etiket hatasında sayaç artmaz.</summary>
    public byte[] DecryptWithAd(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertext)
    {
        if (_key is null) return ciphertext.ToArray();
        if (_nonce == ulong.MaxValue)
            throw new NoiseException(NoiseErrorKind.NonceExhausted, "Nonce sayacı tükendi.");
        if (ciphertext.Length > MaxMessageSize)
            throw new NoiseException(NoiseErrorKind.MessageTooLarge, "Mesaj 65535 baytı aşar.");
        if (ciphertext.Length < TagSize)
            throw new NoiseException(NoiseErrorKind.InvalidMessageLength, "Mesaj etiketten kısa.");

        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, _nonce);
        var plaintext = new byte[ciphertext.Length - TagSize];
        if (!Aead.Decrypt(_key, nonce, associatedData, ciphertext, plaintext))
            throw new NoiseException(NoiseErrorKind.AuthenticationFailed, "AEAD etiketi doğrulanamadı.");
        _nonce++;
        return plaintext;
    }

    /// <summary>Taşıma mesajı şifreler (boş ad).</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext) => EncryptWithAd(default, plaintext);

    /// <summary>Taşıma mesajı çözer (boş ad).</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext) => DecryptWithAd(default, ciphertext);

    private static void WriteNonce(Span<byte> nonce, ulong counter)
    {
        nonce.Clear(); // ilk 4 bayt sıfır
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }

    /// <summary>Test için: sayacı zorla ayarlar (nonce tükenmesi senaryosu).</summary>
    internal void SetNonceForTest(ulong value) => _nonce = value;

    public void Dispose()
    {
        _key?.Dispose();
        _key = null;
    }
}
