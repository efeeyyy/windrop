using WinDrop.Core.Session;

namespace WinDrop.Platform;

/// <summary>
/// Kurulmuş BLE sinyal oturumu: iPhone ile IK (tanıdık/eşli) veya XX (bilinmeyen, PROTOCOL §3.1) kanalı (PROTOCOL §4.1, §5).
/// <see cref="Wifi"/>/<see cref="BtAudio"/>/<see cref="Caps"/> iPhone'un <c>hello</c>/<c>status</c> mesajlarından gelir.
/// </summary>
public sealed class PeerSession : IDisposable
{
    private readonly CancellationTokenSource _closed = new();

    /// <param name="known">Güvenilen listede (QR eşli veya tanıdık).</param>
    /// <param name="name">Kayıttaki ad (bilinmeyen cihazda null; <c>hello</c> ile dolar).</param>
    public PeerSession(byte[] publicKey, string? name, bool known, bool paired, SecureChannel channel, string? bleId = null)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(channel);
        PublicKey = (byte[])publicKey.Clone();
        _name = name ?? "iPhone";
        IsKnown = known;
        IsPaired = paired;
        Channel = channel;
        BleId = bleId;
        PeerId = DeviceId.HexFromPublicKey(publicKey);
        if (channel.Kind == HandshakeKind.XX) VerificationCode = Sas.Compute(channel.HandshakeHash);
    }

    private volatile string _name;

    /// <summary>Oturumun geldiği BLE bağlantı kimliği (GATT oturumu); bilinmiyorsa null. Aynı telefonun iki kimliği aynı değeri paylaşabilir.</summary>
    public string? BleId { get; }

    /// <summary>Oturumun geliş sırası (aynı BLE kimliğinde en yenisi aktiftir).</summary>
    internal long Sequence { get; set; }

    /// <summary>Kayıtlı ad güncellendi (kimliği doğrulanmış oturumda yeni hello.name).</summary>
    internal void SetRegisteredName(string name) => _name = name;

    /// <summary>Eşin açık anahtarı (BLE oturumunda doğrulanmış).</summary>
    public byte[] PublicKey { get; }

    /// <summary>Arayüzde gösterilen ad: kayıtlıysa kayıttaki ad (PROTOCOL §1), değilse temizlenmiş <c>hello.name</c>.</summary>
    public string Name => _name;

    /// <summary>Kayıtlı ad yoksa <c>hello</c> adını kullanır.</summary>
    internal void SetHelloName(string helloName)
    {
        if (!IsKnown) _name = helloName;
    }

    /// <summary>Güvenilen listede (QR eşli veya tanıdık) mi; tanıdık yapıldıktan sonra true olur.</summary>
    public volatile bool IsKnown;

    /// <summary>QR ile eşli (otomatik kabul yalnız buna açık).</summary>
    public volatile bool IsPaired;

    /// <summary>XX oturumunda 4 haneli doğrulama kodu (PROTOCOL §3.1); IK/XXpsk3 oturumunda null.</summary>
    public string? VerificationCode { get; }

    /// <summary>iPhone <c>hello</c> gönderdi (yakındaki cihaz listesinde görünür).</summary>
    public volatile bool HelloReceived;
    public SecureChannel Channel { get; }

    /// <summary>Cihaz kimliği, küçük harf onaltılık (32 karakter).</summary>
    public string PeerId { get; }

    /// <summary>iPhone'un bildirdiği Wi-Fi durumu (<c>none</c>/<c>connected</c>); henüz bilinmiyorsa null.</summary>
    public volatile string? Wifi;

    public volatile bool BtAudio;

    /// <summary>Oturum protokol/şifre hatasıyla bitti (PROTOCOL §4.3: iPhone'a sıfırlama işareti gider).</summary>
    public volatile bool Faulted;

    public volatile IReadOnlyList<string> Caps = [];

    /// <summary>Oturum kapatıldı (yerine yenisi geldi, eşleştirme kaldırıldı, bağlantı koptu).</summary>
    public CancellationToken Closed => _closed.Token;

    public void Close()
    {
        try { _closed.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Close();
        Channel.Dispose();
    }
}
