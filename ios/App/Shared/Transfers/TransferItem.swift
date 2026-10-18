import Foundation
import Observation
@preconcurrency import WinDropKit

/// Aktarımlar listesindeki bir satır (gönderme veya alma).
@MainActor
@Observable
final class TransferItem: Identifiable {
    enum Direction { case send, receive }

    enum State: Equatable {
        /// Gelen teklif: kullanıcı kararı bekleniyor.
        case awaitingUser
        /// Karşı tarafın yanıtı / adres bilgisi bekleniyor.
        case awaitingPeer
        case connecting
        case joiningHotspot
        /// Yetki yok: kullanıcı Ayarlar'dan etkin noktaya bir kez katılmalı.
        case needsManualHotspot
        /// Aktarma (relay) yolu önerildi (`relay` sinyali geldi): kullanıcı kota uyarısını onaylamalı (PROTOCOL §6.3).
        case awaitingRelayConsent
        case transferring
        case done
        case failed
        case rejected
        case cancelled
    }

    /// PROTOCOL §6.3 / ARCHITECTURE: aktarma yolu seçilirse gösterilecek uyarı.
    static let relayWarning = "Bu gönderim Wi-Fi veya hücresel veri kotanızdan gidecektir."

    /// Uyarının tam metni: boyut ve uçtan uca şifreleme bilgisi dahil (iki tarafta da aynı).
    var relayWarningText: String {
        let size = ByteCountFormatter.string(fromByteCount: totalBytes, countStyle: .file)
        return "\(TransferItem.relayWarning) (\(size)) Uçtan uca şifreli."
    }

    let id: String
    let direction: Direction
    let pcID: String
    let pcName: String
    let fileNames: [String]
    let fileCount: Int
    let createdAt = Date()
    /// Alıcı ekranında gösterilen 4 haneli doğrulama kodu.
    var code = ""
    /// Gönderenin/alıcının tanıdık olup olmadığı (arayüz rozeti).
    var peerIsKnown = false
    /// Alırken: kullanıcı Kabul'e bastı (veya QR'lı PC için otomatik kabul). `addr`/`hotspot` bu olmadan işlenmez.
    var userAccepted = false
    /// Gönderirken: PC `accept` gönderdi. `addr`/`hotspot` bu olmadan işlenmez.
    var peerAccepted = false

    /// Doğrulama kodu şimdi gösterilmeli mi? Kod yalnız XX oturumunda vardır (IK'da boş); gönderirken PC onaylayana
    /// dek, alırken kullanıcı karar verene dek.
    var showsVerificationCode: Bool {
        guard !code.isEmpty, !isFinished else { return false }
        switch direction {
        case .send: return state == .awaitingPeer && !peerAccepted
        case .receive: return state == .awaitingUser
        }
    }

    var totalBytes: Int64
    var state: State
    var bytesDone: Int64 = 0
    var errorMessage: String?
    var receivedFiles: [StoredFile] = []
    var hotspotPrompt: HotspotCredentials?
    var photosSaveMessage: String?

    /// Gönderirken ilk dosyanın yerel kaynağı (önizleme için; `outgoing` bitince boşaltılır).
    @ObservationIgnored var sendPreviewURL: URL?

    /// Önizleme kaynağı: gönderirken yerel dosya, alırken kaydedilen ilk dosya.
    var previewURL: URL? {
        direction == .send ? sendPreviewURL : receivedFiles.first?.url
    }

    /// Klasör aktarımı mı (ad "/" ile bitiyorsa; PROTOCOL §5a)? Klasör için klasör simgesi gösterilir.
    var isFolderTransfer: Bool { fileNames.first?.hasSuffix("/") == true }

    @ObservationIgnored var outgoing: [OutgoingFile] = []
    /// Aktarma odası adresi (`relay` sinyalinden türetilir: ayardaki `relayUrl` + roomId). Kullanıcı onaylayana dek bağlanılmaz.
    @ObservationIgnored var relayEndpoint: URL?
    @ObservationIgnored var task: Task<Void, Never>?

    init(
        id: String, direction: Direction, pcID: String, pcName: String,
        fileNames: [String], fileCount: Int, totalBytes: Int64, state: State
    ) {
        self.id = id
        self.direction = direction
        self.pcID = pcID
        self.pcName = pcName
        self.fileNames = fileNames
        self.fileCount = fileCount
        self.totalBytes = totalBytes
        self.state = state
    }

    var isFinished: Bool {
        switch state {
        case .done, .failed, .rejected, .cancelled: return true
        default: return false
        }
    }

    var isCancellable: Bool {
        switch state {
        case .awaitingPeer, .connecting, .joiningHotspot, .needsManualHotspot, .awaitingRelayConsent, .transferring: return true
        default: return false
        }
    }

    var title: String {
        if fileCount == 1, let first = fileNames.first { return first }
        return "\(fileCount) dosya"
    }

    var statusText: String {
        let who = direction == .send ? "\(pcName) bilgisayarına" : "\(pcName) bilgisayarından"
        switch state {
        case .awaitingUser: return "\(who) gelen dosya: onayınız bekleniyor"
        case .awaitingPeer: return direction == .send ? "\(pcName) yanıtı bekleniyor…" : "Bağlantı hazırlanıyor…"
        case .connecting: return "Bağlanılıyor…"
        case .joiningHotspot: return "Bilgisayarın etkin noktasına katılınıyor…"
        case .needsManualHotspot: return "Etkin noktaya elle katılmanız gerekiyor"
        case .awaitingRelayConsent: return relayWarningText
        case .transferring: return direction == .send ? "Gönderiliyor…" : "Alınıyor…"
        case .done: return direction == .send ? "Gönderildi" : "Alındı"
        case .failed: return errorMessage ?? "Başarısız"
        case .rejected: return "Reddedildi"
        case .cancelled: return "İptal edildi"
        }
    }

    func apply(_ progress: TransferProgress) {
        bytesDone = progress.bytesDone
        if progress.totalBytes > 0 { totalBytes = progress.totalBytes }
    }
}
