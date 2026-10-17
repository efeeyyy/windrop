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
}

/// Alınıp diske yazılmış dosya (Kit `ReceivedFile`'ın uygulama karşılığı; ad çakışmasın diye ayrı).
struct StoredFile: Sendable, Identifiable, Hashable {
    var name: String
    var url: URL
    var size: Int64

    var id: URL { url }

    /// Fotoğraflar'a kaydedilebilir mi (görüntü/video)?
    var mediaKind: MediaKind? {
        guard let type = UTType(filenameExtension: url.pathExtension) else { return nil }
        if type.conforms(to: .movie) { return .video }
        if type.conforms(to: .image) { return .photo }
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
    case relayUnavailable
    case message(String)

    var errorDescription: String? {
        switch self {
        case .invalidTransferId: return "Geçersiz aktarım kimliği."
        case .relayUnavailable: return "Aktarma sunucusu bu sürümde henüz yok."
        case .message(let text): return text
        }
    }
}

enum TransferRunner {
    static let acceptAutoLimit: Int64 = 2 * 1024 * 1024 * 1024

    /// TCP üstünde Noise kanalı (iPhone her zaman başlatan).
    static func openChannel(stream: NWByteStream, transferId: TransferId, crypto: ChannelCrypto) async throws -> SecureChannel {
        let key = try NoisePrivateKey(rawRepresentation: crypto.localPrivateKey)
        let prefix = try Prefix(transport: .tcp, transferId: transferId.bytes)
        let transport = TcpMessageTransport(stream: stream)
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
        stream: NWByteStream, transferId: Data, crypto: ChannelCrypto, files: [OutgoingFile],
        progress: @escaping @Sendable (TransferProgress) -> Void
    ) async throws -> TransferSummary {
        guard let tid = TransferId(bytes: transferId) else { throw TransferFailure.invalidTransferId }
        let channel = try await openChannel(stream: stream, transferId: tid, crypto: crypto)
        let items = try files.map { try DiskFileSource.makeItem(id: $0.id, url: $0.url, name: $0.name) }
        let result = try await FileSender(channel: channel, items: items, progress: progress).run()
        return summarize(result)
    }

    static func receive(
        stream: NWByteStream, transferId: Data, crypto: ChannelCrypto, peerKeyID: String,
        expectedCount: Int, expectedTotal: Int64, destination: URL,
        progress: @escaping @Sendable (TransferProgress) -> Void
    ) async throws -> TransferSummary {
        guard let tid = TransferId(bytes: transferId) else { throw TransferFailure.invalidTransferId }
        let channel = try await openChannel(stream: stream, transferId: tid, crypto: crypto)
        let storage = try DirectoryReceiverStorage(directory: destination)
        let options = ReceiverOptions(
            peerId: peerKeyID, transferId: tid, expectedCount: expectedCount, expectedTotal: expectedTotal)
        let result = try await FileReceiver(channel: channel, storage: storage, options: options, progress: progress).run()
        return summarize(result)
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
