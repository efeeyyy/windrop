using System.Security.Cryptography;
using NSec.Cryptography;

namespace WinDrop.Core.Noise;

/// <summary>
/// Noise SymmetricState (rev 34, §5.2) — HASH=SHA-256, HKDF=HKDF-SHA256 (boş info). Bu sınıf kasıtlı
/// olarak <c>internal</c>: uygulama yalnız <see cref="HandshakeState"/> ve <see cref="CipherState"/> görür.
/// Noise HKDF(ck, ikm, n) = RFC 5869 Extract(salt=ck, ikm) + Expand(info=boş) → NSec <c>HkdfSha256.DeriveBytes</c>.
/// DH çıktısı NSec <see cref="SharedSecret"/> olarak kalır (ham bayta çevrilmez); açık anahtar/psk gibi
/// ham girdiler NSec bayt aralığı aşırı yüklemesini kullanır (eşitlik testle doğrulanır). İstisna: Split boş IKM ile HKDF ister, NSec reddeder → BCL HKDF.
/// </summary>
internal sealed class SymmetricState : IDisposable
{
    internal const int HashLen = 32;

    private static readonly NSec.Cryptography.HashAlgorithm Sha256 = NSec.Cryptography.HashAlgorithm.Sha256;
    private static readonly KeyDerivationAlgorithm Hkdf = KeyDerivationAlgorithm.HkdfSha256;

    private readonly byte[] _ck = new byte[HashLen];
    private readonly byte[] _h = new byte[HashLen];
    private readonly CipherState _cipher = new();

    public SymmetricState(string protocolName)
    {
        var name = System.Text.Encoding.ASCII.GetBytes(protocolName);
        if (name.Length <= HashLen) name.CopyTo(_h, 0);          // sıfırlarla sağdan doldur
        else Sha256.Hash(name, _h);
        _h.CopyTo(_ck, 0);
    }

    public bool HasKey => _cipher.HasKey;

    public byte[] GetHandshakeHash() => (byte[])_h.Clone();

    public void MixHash(ReadOnlySpan<byte> data)
    {
        var buf = new byte[HashLen + data.Length];
        _h.CopyTo(buf, 0);
        data.CopyTo(buf.AsSpan(HashLen));
        Sha256.Hash(buf, _h);
    }

    /// <summary>MixKey(ham girdi): açık anahtar (psk modifikatörlü e belirteci).</summary>
    public void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
    {
        Span<byte> output = stackalloc byte[2 * HashLen];
        Hkdf.DeriveBytes(inputKeyMaterial, _ck, default, output);
        FinishMixKey(output);
    }

    /// <summary>MixKey(DH çıktısı).</summary>
    public void MixKey(SharedSecret dh)
    {
        Span<byte> output = stackalloc byte[2 * HashLen];
        Hkdf.DeriveBytes(dh, _ck, default, output);
        FinishMixKey(output);
    }

    private void FinishMixKey(Span<byte> output)
    {
        output[..HashLen].CopyTo(_ck);
        _cipher.InitializeKey(output.Slice(HashLen, 32));
        CryptographicOperations.ZeroMemory(output);
    }

    public void MixKeyAndHash(ReadOnlySpan<byte> inputKeyMaterial)
    {
        Span<byte> output = stackalloc byte[3 * HashLen];
        Hkdf.DeriveBytes(inputKeyMaterial, _ck, default, output);
        output[..HashLen].CopyTo(_ck);
        MixHash(output.Slice(HashLen, HashLen));
        _cipher.InitializeKey(output.Slice(2 * HashLen, 32));
        CryptographicOperations.ZeroMemory(output);
    }

    public byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        var ciphertext = _cipher.EncryptWithAd(_h, plaintext);
        MixHash(ciphertext);
        return ciphertext;
    }

    public byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        var plaintext = _cipher.DecryptWithAd(_h, ciphertext);
        MixHash(ciphertext);
        return plaintext;
    }

    /// <summary>Split: HKDF(ck, boş, 2) → iki taşıma CipherState'i (First: başlatan→yanıtlayan).</summary>
    public (CipherState First, CipherState Second) Split()
    {
        Span<byte> output = stackalloc byte[2 * HashLen];
        // NSec boş IKM'i reddeder ("input keying material must be non-empty"); Noise Split ise HKDF(ck, zerolen).
        // BCL HKDF boş IKM'i Windows'ta kabul eder ama Linux/OpenSSL 3'te CryptographicException atar (interop denemesi,
        // docs/INTEROP.md). Bu yüzden Noise §4.3 HKDF'i HMAC ile elle yapılır: temp = HMAC(ck, zerolen);
        // out1 = HMAC(temp, 0x01); out2 = HMAC(temp, out1 || 0x02). Sonuç cacophony vektörleriyle doğrulanır.
        var temp = HMACSHA256.HashData((ReadOnlySpan<byte>)_ck, ReadOnlySpan<byte>.Empty);
        var out1 = HMACSHA256.HashData((ReadOnlySpan<byte>)temp, new byte[] { 0x01 });
        var out2Input = new byte[HashLen + 1];
        out1.CopyTo(out2Input, 0);
        out2Input[HashLen] = 0x02;
        var out2 = HMACSHA256.HashData((ReadOnlySpan<byte>)temp, out2Input);
        out1.CopyTo(output);
        out2.CopyTo(output[HashLen..]);
        CryptographicOperations.ZeroMemory(temp);
        CryptographicOperations.ZeroMemory(out1);
        CryptographicOperations.ZeroMemory(out2);
        CryptographicOperations.ZeroMemory(out2Input);
        var c1 = new CipherState();
        var c2 = new CipherState();
        c1.InitializeKey(output[..32]);
        c2.InitializeKey(output.Slice(HashLen, 32));
        CryptographicOperations.ZeroMemory(output);
        return (c1, c2);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_ck);
        _cipher.Dispose();
    }
}
