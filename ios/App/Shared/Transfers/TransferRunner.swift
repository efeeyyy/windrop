import Foundation
import UniformTypeIdentifiers
@preconcurrency import WinDropKit

// WinDropKit (Session/Transfer) ile gerçek bağlantı: TCP akışı -> ön ek + IK/XX -> FileSender/FileReceiver.
// Kit sınıfları Sendable değil: kanal ve gönderen/alıcı aynı görevde (bu işlevlerin içinde) oluşturulur ve yaşar.

/// Gönderilecek dosya. `id` MANIFEST'teki dosya kimliği (0'dan).
struct OutgoingFile: Sendable, Identifiable, Hashable {
    var id: Int
    var url: URL
    var name: String
    var size: Int64
    var modified: Date?
    /// `startAccessingSecurityScopedResource()` çağrıldı; iş bitince `stop` edilmeli.
    var securityScoped: Bool
    /// Geçici kopya (fotoğraf seçici); iş bitince silinir.
    var isTemporary: Bool
    /// PROTOCOL §5a: gönderilen klasör kökünden göreli klasör yolu (`Klasör/alt`, `/` ayırıcı); tek dosyada nil.
    var dir: String? = nil
    /// Klasör seçimi: güvenlik kapsamı bu URL için açıldı (klasörün ilk dosyasında yalnız bir kez set edilir; iş bitince kapatılır).
    var scopeURL: URL? = nil
    /// Klasör geçici kopyaysa silinecek kök (Paylaşım uzantısı).
    var tempRoot: URL? = nil

    /// `offer.preview` / arayüz adı (PROTOCOL §5a): klasör içindeyse en üst klasör adı + sonda "/", değilse dosya adı.
    var displayName: String {
        guard let dir, let top = dir.split(separator: "/", maxSplits: 1).first else { return name }
        return String(top) + "/"
    }
}

/// Alınıp diske yazılmış dosya (Kit `ReceivedFile`'ın uygulama karşılığı; ad çakışmasın diye ayrı).
struct StoredFile: Sendable, Identifiable, Hashable {
    var name: String
    var url: URL
    var size: Int64

    var id: URL { url }

    /// Fotoğraflar'ın kabul ettiği türler (SVG, PSD, ICO gibi `.image` alt türleri toplu kaydı bozmasın).
    private static let photoExtensions: Set<String> = ["jpg", "jpeg", "heic", "heif", "png", "gif", "tif", "tiff", "dng"]
    private static let videoExtensions: Set<String> = ["mov", "mp4", "m4v"]

    /// Fotoğraflar'a kaydedilebilir mi (desteklenen görüntü/video)?
    var mediaKind: MediaKind? {
        let ext = url.pathExtension.lowercased()
        if Self.videoExtensions.contains(ext) { return .video }
        if Self.photoExtensions.contains(ext) { return .photo }
        return nil
    }

    enum MediaKind: Sendable { case photo, video }
}

/// Veri kanalı kimliği (PROTOCOL §2b: BLE oturumu IK idiyse IK, değilse XX + anahtar eşitliği doğrulaması).
struct ChannelCrypto: Sendable {
    var localPrivateKey: Data
    /// BLE oturumunda öğrenilen/saklanan PC açık anahtarı.
    var remoteStaticKey: Data
    var useIK: Bool
}

struct TransferSummary: Sendable {
    var ok: Bool
    var canceledByRemote: Bool
    var failedFileIDs: [Int]
    var received: [StoredFile]
    var error: String?
}

enum TransferFailure: Error, LocalizedError {
    case invalidTransferId
    case relayNotConfigured
    case relayInvalidToken
    case message(String)

    var errorDescription: String? {
        switch self {
        case .invalidTransferId: return String(localized: "Geçersiz aktarım kimliği.")
        case .relayNotConfigured: return String(localized: "Aktarma sunucusu adresi ayarlı değil (Ayarlar > Aktarma sunucusu).")
        case .relayInvalidToken: return String(localized: "Aktarma bilgisi geçersiz.")
        case .message(let text): return text
        }
    }
}

enum TransferRunner {
    /// Taşıma (TCP veya aktarma sunucusu WebSocket'i) üstünde Noise kanalı (iPhone her zaman başlatan). Ön ekteki taşıma
    /// türü `transport.kind` olur (2 = TCP, 3 = aktarma).
    static func openChannel(transport: MessageTransport, transferId: TransferId, crypto: ChannelCrypto) async throws -> SecureChannel {
        let key = try NoisePrivateKey(rawRepresentation: crypto.localPrivateKey)
        let prefix = try Prefix(transport: transport.kind, transferId: transferId.bytes)
        if crypto.useIK {
            return try await SecureChannel.connectIK(
                transport: transport, prefix: prefix, localKey: key, remoteStaticKey: crypto.remoteStaticKey)
        }
        // BLE oturumundaki PC anahtarı 3. mesajdan önce doğrulanır (kendi statik anahtarımız yanlış karşıya gitmez).
        let channel = try await SecureChannel.connectXX(
            transport: transport, prefix: prefix, localKey: key, expectedRemoteStatic: crypto.remoteStaticKey)
        guard channel.remoteStaticKey == crypto.remoteStaticKey else { throw SessionError.peerKeyMismatch }
        return channel
    }

    static func send(
        transport: MessageTransport, transferId: Data, crypto: ChannelCrypto, files: [OutgoingFile],
        progress: @escaping @Sendable (TransferProgress) -> Void
    ) async throws -> TransferSummary {
        guard let tid = TransferId(bytes: transferId) else { throw TransferFailure.invalidTransferId }
        let channel = try await openChannel(transport: transport, transferId: tid, crypto: crypto)
        let items = try files.map { try DiskFileSource.makeItem(id: $0.id, url: $0.url, name: $0.name, dir: $0.dir, lazy: $0.dir != nil) }
        let result = try await FileSender(channel: channel, items: items, progress: progress).run()
        return summarize(result)
    }

    static func receive(
        transport: MessageTransport, transferId: Data, crypto: ChannelCrypto, peerKeyID: String,
        expectedCount: Int, expectedTotal: Int64, destination: URL,
        progress: @escaping @Sendable (TransferProgress) -> Void
    ) async throws -> TransferSummary {
        guard let tid = TransferId(bytes: transferId) else { throw TransferFailure.invalidTransferId }
        let channel = try await openChannel(transport: transport, transferId: tid, crypto: crypto)
        let storage = try DirectoryReceiverStorage(directory: destination)
        let options = ReceiverOptions(
            peerId: peerKeyID, transferId: tid, expectedCount: expectedCount, expectedTotal: expectedTotal)
        let result = try await FileReceiver(channel: channel, storage: storage, options: options, progress: progress).run()
        return summarize(result)
    }

    /// BLE oturumu üstünde gönderim (PROTOCOL §6.4): küçük DATA parçaları, ~20 KB/s pacing; kanal açık BLE oturumunundur.
    static func sendOverBle(
        session: BleDataSession, files: [OutgoingFile], progress: @escaping @Sendable (TransferProgress) -> Void
    ) async throws -> TransferSummary {
        let items = try files.map { try DiskFileSource.makeItem(id: $0.id, url: $0.url, name: $0.name, dir: $0.dir, lazy: $0.dir != nil) }
        let sender = FileSender(
            channel: session.channel, items: items, chunkSize: TransferLimits.bleChunkSize,
            bytesPerSecond: TransferLimits.bleBytesPerSecond, frameReader: session.nextFrame,
            resumeTimeout: TransferLimits.bleResumeTimeout, progress: progress)
        return summarize(try await sender.run())
    }

    /// BLE oturumu üstünde alma (karşı PC hız sınırını kendi uygular).
    static func receiveOverBle(
        session: BleDataSession, transferId: Data, peerKeyID: String, expectedCount: Int, expectedTotal: Int64,
        destination: URL, progress: @escaping @Sendable (TransferProgress) -> Void
    ) async throws -> TransferSummary {
        guard let tid = TransferId(bytes: transferId) else { throw TransferFailure.invalidTransferId }
        let storage = try DirectoryReceiverStorage(directory: destination)
        let options = ReceiverOptions(
            peerId: peerKeyID, transferId: tid, expectedCount: expectedCount, expectedTotal: expectedTotal)
        let receiver = FileReceiver(
            channel: session.channel, storage: storage, options: options, frameReader: session.nextFrame,
            progress: progress)
        return summarize(try await receiver.run())
    }

    private static func summarize(_ result: TransferResult) -> TransferSummary {
        let stored = result.receivedFiles.map { file -> StoredFile in
            let url = URL(fileURLWithPath: file.storedPath)
            let size = (try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0
            return StoredFile(name: file.originalName, url: url, size: Int64(size))
        }
        return TransferSummary(
            ok: result.outcome == .completed, canceledByRemote: result.canceledByRemote,
            failedFileIDs: result.failedFileIds, received: stored, error: result.error)
    }
}
