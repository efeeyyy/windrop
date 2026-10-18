namespace WinDrop.Platform;

/// <summary>
/// Kullanıcıya gösterilen hata türleri. Platform yalnız kodu + bağımsız değişkenleri taşır; arayüz dili App katmanında
/// <c>Strings.resx</c> (<c>Err.&lt;Ad&gt;</c>) ile çevrilir. <see cref="UserError.ToString"/> günlük/test için Türkçe özet verir.
/// </summary>
public enum UserErrorCode
{
    Raw,
    BtAudioLocalStopped, BtAudioPhoneStopped, PairingRemoved, BleDisconnected, BleNoResponse, BleTransferCut,
    NotNearby, FileNotFound, FileUnreadable, NoFilesToSend, TooManyFiles, FolderTooDeep, FolderPathTooLong,
    PushDisabled, PushUnknown, PushRateLimited, PushServerError, PushUnreachable, PhoneNoResponse,
    OfferNoResponse, RejectedByPeer, RejectedByPeerReason, NoDiskSpace,
    PathNoHotspotSupport, PathNoHotspotInVersion, HotspotOpenFailed, HotspotInUse, NoNetworkAddress,
    DirectFailedReported, PhoneDataTimeout, ConnectionLostNoReconnect,
    BleAlsoSize, BleAlsoLocalAudio, BleAlsoPhoneAudio, BleBusy,
    RelayUrlInvalidHint, RelayUrlInvalid, RelayNotApproved, RelayDropped, RelayTooManyRooms, RelayRoomFull, RelayRoomClosed,
    RelayUnreachable, RelayRefused, RelayPhoneJoinTimeout, RelayPhoneFailed, RelayHandshakeFailed, RelayAlsoFailed, NoPath,
    HashMismatch, RemoteCanceled, TransferCut, TransferCutWith, UnexpectedEnd, TransferFailedWith, TransferFailedUnexpected,
    HotspotRestartFailed, HotspotInTransition, HotspotStartFailed, HotspotNeedNetwork, HotspotUnavailableHere,
    CapGroupPolicy, CapHardware, CapOperator, CapSku, CapAppMissing, CapSystem,
    HsWifiOff, HsEntitlement, HsOpInProgress, HsBtOff, HsLimited, HsStartStatus,
}

/// <summary>Tek hata parçası: kod + en çok iki bağımsız değişken ({0}, {1}).</summary>
public readonly record struct UserErrorPart(UserErrorCode Code, string? Arg = null, string? Arg2 = null)
{
    /// <summary>Türkçe metin (günlük, test ve çevirisiz yedek).</summary>
    public override string ToString() => string.Format(System.Globalization.CultureInfo.InvariantCulture, Template(Code), Arg, Arg2);

    private static string Template(UserErrorCode code) => code switch
    {
        UserErrorCode.Raw => "{0}",
        UserErrorCode.BtAudioLocalStopped => "Bu bilgisayarda Bluetooth ses başladı; BLE ile aktarım durduruldu.",
        UserErrorCode.BtAudioPhoneStopped => "iPhone'da Bluetooth ses başladı; BLE ile aktarım durduruldu.",
        UserErrorCode.PairingRemoved => "Eşleştirme kaldırıldı.",
        UserErrorCode.BleDisconnected => "Bluetooth bağlantısı koptu.",
        UserErrorCode.BleNoResponse => "iPhone Bluetooth üzerinden yanıt vermedi (zaman aşımı). Yol yok.",
        UserErrorCode.BleTransferCut => "Bluetooth üzerinden aktarım kesildi.",
        UserErrorCode.NotNearby => "Cihaz yakında değil (Bluetooth bağlantısı yok).",
        UserErrorCode.FileNotFound => "Dosya bulunamadı: {0}",
        UserErrorCode.FileUnreadable => "Dosya veya klasör okunamadı: {0}",
        UserErrorCode.NoFilesToSend => "Gönderilecek dosya yok.",
        UserErrorCode.TooManyFiles => "En çok {0} dosya gönderilebilir.",
        UserErrorCode.FolderTooDeep => "Klasör en çok {0} seviye derinlikte olabilir: {1}",
        UserErrorCode.FolderPathTooLong => "Klasör yolu en çok {0} karakter olabilir: {1}",
        UserErrorCode.PushDisabled => "Push bu sunucuda kapalı.",
        UserErrorCode.PushUnknown => "Telefonun bildirim kaydı sunucuda yok; telefonda uygulamayı açıp Bluetooth ile bağlanmasını bekleyin.",
        UserErrorCode.PushRateLimited => "Telefona çok fazla bildirim gönderildi; biraz sonra yeniden deneyin.",
        UserErrorCode.PushServerError => "Bildirim sunucusu hata verdi (HTTP {0}).",
        UserErrorCode.PushUnreachable => "Bildirim sunucusuna ulaşılamadı.",
        UserErrorCode.PhoneNoResponse => "Telefon yanıt vermedi.",
        UserErrorCode.OfferNoResponse => "Karşı cihaz teklife yanıt vermedi.",
        UserErrorCode.RejectedByPeer => "Karşı cihaz reddetti.",
        UserErrorCode.RejectedByPeerReason => "Karşı cihaz reddetti ({0}).",
        UserErrorCode.NoDiskSpace => "İndirme klasörünün bulunduğu sürücüde yeterli boş alan yok.",
        UserErrorCode.PathNoHotspotSupport => "iPhone etkin nokta yolunu desteklemiyor ve bir Wi-Fi ağına bağlı değil. Yol yok.",
        UserErrorCode.PathNoHotspotInVersion => "iPhone bir Wi-Fi ağına bağlı değil ve etkin nokta yolu bu sürümde kullanılamıyor. Yol yok.",
        UserErrorCode.HotspotOpenFailed => "Windows Mobil etkin noktası açılamadı:",
        UserErrorCode.HotspotInUse => "Windows Mobil etkin noktası zaten sizin tarafınızdan kullanılıyor; WinDrop kendi etkin noktasını açamıyor. " +
                                      "Etkin noktayı kapatın ya da iPhone'u bir Wi-Fi ağına bağlayıp yeniden deneyin.",
        UserErrorCode.NoNetworkAddress => "Bu bilgisayarda kullanılabilir ağ adresi yok (ağa bağlı değil).",
        UserErrorCode.DirectFailedReported => "Doğrudan bağlantı kurulamadı (iPhone bildirdi: {0}). Yol yok.",
        UserErrorCode.PhoneDataTimeout => "iPhone veri bağlantısını kuramadı (zaman aşımı). Yol yok.",
        UserErrorCode.ConnectionLostNoReconnect => "Bağlantı koptu ve iPhone yeniden bağlanmadı.",
        UserErrorCode.BleAlsoSize => "BLE (küçük dosya) yolu da kullanılamıyor: toplam boyut {0} KB'ı aşıyor.",
        UserErrorCode.BleAlsoLocalAudio => "BLE (küçük dosya) yolu da kullanılamıyor: bu bilgisayarda Bluetooth ses çıkışı kullanılıyor.",
        UserErrorCode.BleAlsoPhoneAudio => "BLE (küçük dosya) yolu da kullanılamıyor: iPhone'da Bluetooth ses çıkışı kullanılıyor.",
        UserErrorCode.BleBusy => "BLE yolu bu eşle başka bir aktarım için kullanılıyor.",
        UserErrorCode.RelayUrlInvalidHint => "Aktarma sunucusu adresi geçersiz (Ayarlar'da wss://… biçiminde girin).",
        UserErrorCode.RelayUrlInvalid => "Aktarma sunucusu adresi geçersiz.",
        UserErrorCode.RelayNotApproved => "Aktarma sunucusu üzerinden gönderim onaylanmadı.",
        UserErrorCode.RelayDropped => "Aktarma bağlantısı koptu.",
        UserErrorCode.RelayTooManyRooms => "Aktarma sunucusu çok sık oda açıldığı için reddetti (dakikada en çok 10). Biraz sonra deneyin.",
        UserErrorCode.RelayRoomFull => "Aktarma odası dolu (aynı rol zaten bağlı).",
        UserErrorCode.RelayRoomClosed => "Aktarma odası kapanmış.",
        UserErrorCode.RelayUnreachable => "Aktarma sunucusuna bağlanılamadı: {0}",
        UserErrorCode.RelayRefused => "Aktarma sunucusu bağlantıyı reddetti (HTTP {0}).",
        UserErrorCode.RelayPhoneJoinTimeout => "iPhone aktarma odasına zamanında bağlanmadı.",
        UserErrorCode.RelayPhoneFailed => "iPhone aktarma sunucusuna bağlanamadı (relay_fail).",
        UserErrorCode.RelayHandshakeFailed => "Aktarma el sıkışması başarısız: {0}",
        UserErrorCode.RelayAlsoFailed => "Aktarma yolu da başarısız. Yol yok.",
        UserErrorCode.NoPath => "Yol yok.",
        UserErrorCode.HashMismatch => "{0} dosya doğrulanamadı (SHA-256 uyuşmadı).",
        UserErrorCode.RemoteCanceled => "Karşı taraf iptal etti.",
        UserErrorCode.TransferCut => "Aktarım kesildi.",
        UserErrorCode.TransferCutWith => "Aktarım kesildi: {0}",
        UserErrorCode.UnexpectedEnd => "Aktarım beklenmedik şekilde sona erdi.",
        UserErrorCode.TransferFailedWith => "Aktarım başarısız: {0}",
        UserErrorCode.TransferFailedUnexpected => "Aktarım başarısız (beklenmeyen hata).",
        UserErrorCode.HotspotRestartFailed => "Mobil etkin nokta yeniden başlatılamadı: {0}",
        UserErrorCode.HotspotInTransition => "Mobil etkin nokta şu an değişiyor; birkaç saniye sonra tekrar deneyin.",
        UserErrorCode.HotspotStartFailed => "Mobil etkin nokta başlatılamadı: {0}",
        UserErrorCode.HotspotNeedNetwork => "Mobil etkin nokta için bilgisayarın bir ağa bağlı olması gerekir (internet bağlantı profili yok). " +
                                            "Wi-Fi veya Ethernet'e bağlanıp tekrar deneyin.",
        UserErrorCode.HotspotUnavailableHere => "Mobil etkin nokta bu bilgisayarda kullanılamıyor:",
        UserErrorCode.CapGroupPolicy => "grup ilkesiyle kapatılmış.",
        UserErrorCode.CapHardware => "donanım desteklemiyor.",
        UserErrorCode.CapOperator => "operatör tarafından kapatılmış.",
        UserErrorCode.CapSku => "Windows sürümü desteklemiyor.",
        UserErrorCode.CapAppMissing => "gerekli uygulama yüklü değil.",
        UserErrorCode.CapSystem => "sistem özelliği kapalı.",
        UserErrorCode.HsWifiOff => "Wi-Fi kapalı; Wi-Fi'ı açıp tekrar deneyin.",
        UserErrorCode.HsEntitlement => "Mobil etkin nokta yetkilendirmesi başarısız.",
        UserErrorCode.HsOpInProgress => "Başka bir etkin nokta işlemi sürüyor.",
        UserErrorCode.HsBtOff => "Bluetooth kapalı.",
        UserErrorCode.HsLimited => "Ağ bağlantısı sınırlı; etkin nokta açılamadı.",
        UserErrorCode.HsStartStatus => "Mobil etkin nokta başlatılamadı ({0}).",
        _ => code.ToString(),
    };
}

/// <summary>Birden çok parçadan oluşan kullanıcı hatası (parçalar boşlukla birleşir).</summary>
public sealed class UserError
{
    public UserError(params UserErrorPart[] parts) => Parts = parts;

    public IReadOnlyList<UserErrorPart> Parts { get; }

    public static UserError Of(UserErrorCode code, string? arg = null, string? arg2 = null) => new(new UserErrorPart(code, arg, arg2));

    public static UserError Raw(string text) => Of(UserErrorCode.Raw, text);

    public static UserError Join(params UserError[] errors) => new([.. errors.SelectMany(e => e.Parts)]);

    public UserError Then(UserErrorCode code, string? arg = null, string? arg2 = null) => new([.. Parts, new UserErrorPart(code, arg, arg2)]);

    public UserError Then(UserErrorPart part) => new([.. Parts, part]);

    public UserError Then(IEnumerable<UserErrorPart> parts) => new([.. Parts, .. parts]);

    public override string ToString() => string.Join(" ", Parts);
}
