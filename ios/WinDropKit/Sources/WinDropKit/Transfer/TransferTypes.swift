import Foundation
#if canImport(CryptoKit)
import CryptoKit
#else
import Crypto
#endif

/// PROTOCOL §5 sabitleri.
public enum TransferLimits {
    public static let maxFiles = 10_000
    /// Dosya adı üst sınırı (UTF-16 kod birimi).
    public static let maxNameLength = 255
    /// Uygulamaların kullandığı DATA gövdesi boyutu (spesifikasyon üst sınırı 65518).
    public static let defaultChunkSize = 64_000
    /// ACK aralığı: ~4 MB.
    public static let defaultAckIntervalBytes = 4 * 1024 * 1024
    /// Kabulden önce boş alandan düşülen pay.
    public static let defaultSpaceMarginBytes: Int64 = 64 * 1024 * 1024
}

public enum TransferOutcome: Equatable {
    /// Tüm dosyalar doğrulandı.
    case completed
    /// Bitti ama bazı dosyalar SHA-256 uyuşmazlığıyla başarısız (`failedFileIds`).
    case completedWithFailures
    /// İptal (yerel veya uzak, `canceledByRemote`).
    case canceled
    /// Bir sınır/politika ihlali nedeniyle kesildi (`error`).
    case aborted
}

public struct ReceivedFile: Equatable {
    public var id: Int
    public var originalName: String
    public var storedPath: String
}

public struct TransferResult: Equatable {
    public var outcome: TransferOutcome
    public var failedFileIds: [Int]
    /// İptali karşı taraf başlattı.
    public var canceledByRemote = false
    /// Kesme/iptal nedeni (karşıdan gelen `reason` dahil).
    public var error: String?
    /// Dosya başına RESUME ofseti (baştan başlayanlar 0).
    public var resumeOffsets: [Int: Int64] = [:]
    /// Alıcıda diske inen dosyalar.
    public var receivedFiles: [ReceivedFile] = []
}

/// İlerleme olayı. Bayt sayıları devam edilen ofseti de içerir; `bytesPerSecond` yalnız bu oturumda aktarılanlardan.
public struct TransferProgress: Equatable {
    public var fileId: Int
    public var fileName: String
    public var fileIndex: Int
    public var fileCount: Int
    public var fileBytesDone: Int64
    public var fileSize: Int64
    public var bytesDone: Int64
    public var totalBytes: Int64
    public var bytesPerSecond: Double
}

public enum TransferError: Error, Equatable {
    /// Aktarım ortasında bağlantı koptu. Kısmi dosyalar saklanır; yeni oturumla devam edilebilir.
    case connectionLost
    case protocolViolation(String)
}

/// İç: sınır/politika ihlali -> CANCEL gönder, `aborted` sonucu.
struct AbortSignal: Error { var reason: String }
/// İç: karşı taraf CANCEL gönderdi.
struct RemoteCancel: Error { var reason: String? }

func sha256Hex(_ digest: SHA256.Digest) -> String {
    digest.map { String(format: "%02x", $0) }.joined()
}

/// Olay sıklığı (en çok ~10/sn + her dosya sonu) ve hız (EMA) denetimi.
final class ProgressTracker {
    private let callback: ((TransferProgress) -> Void)?
    private let minInterval: UInt64
    private var lastReport: UInt64 = 0
    private var lastSampleTime: UInt64
    private var lastSampleBytes: Int64 = 0
    private var sessionBytes: Int64 = 0
    private var ema: Double = 0
    private var reportedOnce = false

    init(_ callback: ((TransferProgress) -> Void)?, interval: TimeInterval = 0.1) {
        self.callback = callback
        self.minInterval = UInt64(interval * 1e9)
        self.lastSampleTime = DispatchTime.now().uptimeNanoseconds
    }

    func addTransferred(_ n: Int) { sessionBytes += Int64(n) }

    func report(fileId: Int, name: String, index: Int, count: Int, fileDone: Int64, fileSize: Int64,
                done: Int64, total: Int64, force: Bool) {
        guard let callback else { return }
        let now = DispatchTime.now().uptimeNanoseconds
        if !force && reportedOnce && now - lastReport < minInterval { return }
        let dt = now - lastSampleTime
        if dt >= (ema == 0 ? 20_000_000 : 200_000_000) {
            let instant = Double(sessionBytes - lastSampleBytes) * 1e9 / Double(dt)
            ema = ema == 0 ? instant : 0.7 * ema + 0.3 * instant
            lastSampleTime = now
            lastSampleBytes = sessionBytes
        }
        lastReport = now
        reportedOnce = true
        callback(TransferProgress(fileId: fileId, fileName: name, fileIndex: index, fileCount: count,
                                  fileBytesDone: fileDone, fileSize: fileSize, bytesDone: done,
                                  totalBytes: total, bytesPerSecond: ema))
    }
}

// MARK: Gelen çerçeve kuyruğu (iptal farkında)

private actor InboxState {
    enum Item {
        case frame(Frame)
        case eof
        case failure(Error)
    }

    private var items: [Item] = []
    private var waiter: CheckedContinuation<Item, Error>?

    func push(_ item: Item) {
        if let w = waiter {
            waiter = nil
            w.resume(returning: item)
        } else {
            items.append(item)
        }
    }

    func pop() -> Item? { items.isEmpty ? nil : items.removeFirst() }

    func wait() async throws -> Item {
        if !items.isEmpty { return items.removeFirst() }
        if Task.isCancelled { throw CancellationError() }
        return try await withCheckedThrowingContinuation { waiter = $0 }
    }

    func cancelWaiter() {
        if let w = waiter {
            waiter = nil
            w.resume(throwing: CancellationError())
        }
    }
}

/// `SecureChannel`'dan çerçeve okuyan arka plan görevi; tüketici `next()` ile bekler veya `poll()` ile bakar.
/// CANCEL çerçevesi `RemoteCancel`, EOF/kopma `TransferError.connectionLost` olur. Görev iptali bekleyeni uyandırır.
final class FrameInbox {
    private let state = InboxState()
    private var task: Task<Void, Never>?

    init(channel: SecureChannel) {
        let state = self.state
        task = Task {
            while !Task.isCancelled {
                do {
                    guard let f = try await channel.receiveFrame() else {
                        await state.push(.eof)
                        return
                    }
                    await state.push(.frame(f))
                } catch {
                    await state.push(.failure(error))
                    return
                }
            }
        }
    }

    func stop() { task?.cancel() }

    func next() async throws -> Frame {
        try Task.checkCancellation()
        let item = try await withTaskCancellationHandler {
            try await state.wait()
        } onCancel: { [state] in
            Task { await state.cancelWaiter() }
        }
        return try unwrap(item)
    }

    func poll() async throws -> Frame? {
        guard let item = await state.pop() else { return nil }
        return try unwrap(item)
    }

    private func unwrap(_ item: InboxState.Item) throws -> Frame {
        switch item {
        case .eof:
            throw TransferError.connectionLost
        case .failure(let e):
            if let p = e as? ProtocolError, p == .streamEnded { throw TransferError.connectionLost }
            if let s = e as? SessionError, s == .connectionClosed { throw TransferError.connectionLost }
            throw e
        case .frame(let f):
            if f.type == .cancel {
                let reason = (try? FrameCodec.decodeCancel(f.body))?.reason
                throw RemoteCancel(reason: reason)
            }
            return f
        }
    }
}
