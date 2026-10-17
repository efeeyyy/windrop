namespace WinDrop.Core.Noise;

/// <summary>Noise çekirdeğinin hata sınıfları.</summary>
public enum NoiseErrorKind
{
    /// <summary>AEAD etiketi doğrulanamadı (bozuk/değiştirilmiş mesaj, yanlış anahtar, yanlış prologue/psk).</summary>
    AuthenticationFailed,
    /// <summary>Gelen mesaj, desenin beklediği yapıya göre çok kısa.</summary>
    InvalidMessageLength,
    /// <summary>Noise mesajı 65535 baytı aşıyor (gelen veya üretilecek).</summary>
    MessageTooLarge,
    /// <summary>El sıkışma durumu yanlış çağrı sırası (sıra bende değil, bitti, daha önce hata aldı).</summary>
    InvalidState,
    /// <summary>Geçersiz/uyumsuz açık anahtar (ör. düşük dereceli nokta: X25519 çıktısı tümüyle sıfır).</summary>
    InvalidKey,
    /// <summary>CipherState nonce sayacı tükendi (2^64-1).</summary>
    NonceExhausted,
    /// <summary>Desen adı tanınmıyor veya desteklenmiyor.</summary>
    UnsupportedPattern,
}

/// <summary>Noise işlemlerinde protokol/kriptografi hatası. Programlama hataları (null, yanlış boyutlu
/// argüman) <see cref="ArgumentException"/> olarak ayrı kalır.</summary>
public sealed class NoiseException : Exception
{
    public NoiseErrorKind Kind { get; }

    public NoiseException(NoiseErrorKind kind, string message) : base(message) => Kind = kind;
}
