import Foundation
#if canImport(CryptoKit)
import CryptoKit
#else
import Crypto
#endif

/// Gönderilecek dosya kaynağı (soyut; üretimde `DiskFileSource`).
public protocol FileSource: AnyObject {
    /// `offset`ten başlayarak en çok `length` bayt; dosya sonunda boş `Data`.
    func read(at offset: Int64, length: Int) throws -> Data
}

public struct SendItem {
    public var id: Int
    public var name: String
    public var size: Int64
    /// Unix epoch milisaniye.
    public var mtime: Int64
    public var source: FileSource
    public init(id: Int, name: String, size: Int64, mtime: Int64, source: FileSource) {
        self.id = id; self.name = name; self.size = size; self.mtime = mtime; self.source = source
    }
}

/// PROTOCOL §5 gönderen: MANIFEST (sayfalı) -> her dosya için [alıcıdan RESUME -> FILE_START -> DATA… -> FILE_END] -> DONE bekler.
/// Gelen ACK/CANCEL çerçeveleri gönderim sürerken işlenir (uzak iptal hemen fark edilir). Görev iptali CANCEL yollar.
public final class FileSender {
    private let channel: SecureChannel
    private let items: [SendItem]
    private let chunkSize: Int
    private let tracker: ProgressTracker
    private var resumeOffsets: [Int: Int64] = [:]

    public init(channel: SecureChannel, items: [SendItem], chunkSize: Int = TransferLimits.defaultChunkSize,
                progress: ((TransferProgress) -> Void)? = nil) {
        self.channel = channel
        self.items = items
        self.chunkSize = max(1, min(chunkSize, channel.maxFrameBodySize))
        self.tracker = ProgressTracker(progress)
    }

    /// Aktarımı yürütür. Bağlantı kopması `TransferError.connectionLost`, protokol ihlali `TransferError.protocolViolation`
    /// fırlatır; iptal/politika kesmesi sonuç olarak döner.
    public func run() async throws -> TransferResult {
        do {
            try await channel.ensureEstablished()   // yanıtlayan: başlatanın boş onay mesajını bekler (§1)
        } catch SessionError.connectionClosed {
            throw TransferError.connectionLost
        }
        let inbox = FrameInbox(channel: channel)
        defer { inbox.stop() }
        do {
            return try await sendAll(inbox)
        } catch is CancellationError {
            await trySendCancel("canceled")
            return TransferResult(outcome: .canceled, failedFileIds: [], resumeOffsets: resumeOffsets)
        } catch let r as RemoteCancel {
            return TransferResult(outcome: .canceled, failedFileIds: [], canceledByRemote: true, error: r.reason,
                                  resumeOffsets: resumeOffsets)
        } catch let a as AbortSignal {
            await trySendCancel(a.reason)
            return TransferResult(outcome: .aborted, failedFileIds: [], error: a.reason, resumeOffsets: resumeOffsets)
        } catch let e as ProtocolError {
            await trySendCancel("protocol error")
            throw TransferError.protocolViolation("\(e)")
        } catch let e as TransferError {
            if case .protocolViolation = e { await trySendCancel("protocol error") }
            throw e
        }
    }

    private func trySendCancel(_ reason: String) async {
        if let body = try? FrameCodec.encode(CancelBody(reason: reason)) { try? await channel.sendEncodedFrame(body) }
    }

    private func sendAll(_ inbox: FrameInbox) async throws -> TransferResult {
        let manifest = items.map { ManifestFile(id: $0.id, name: $0.name, size: $0.size, mtime: $0.mtime) }
        let pages = try Self.paginate(manifest)
        for (i, page) in pages.enumerated() {
            let body = ManifestBody(files: page, more: i < pages.count - 1 ? true : nil)
            try await channel.sendEncodedFrame(try FrameCodec.encode(body))
        }

        let total = items.reduce(Int64(0)) { $0 + $1.size }
        var completedBytes: Int64 = 0
        for (index, item) in items.enumerated() {
            try Task.checkCancellation()
            let resume = try await expect(.resume, inbox)
            let r = try FrameCodec.decodeResume(resume.body)
            guard r.id == item.id else { throw TransferError.protocolViolation("RESUME id \(r.id), beklenen \(item.id)") }
            guard r.offset >= 0, r.offset <= item.size else {
                throw TransferError.protocolViolation("RESUME ofseti geçersiz: \(r.offset) (boyut \(item.size))")
            }
            resumeOffsets[item.id] = r.offset
            try await channel.sendEncodedFrame(try FrameCodec.encode(FileStartBody(id: item.id, offset: r.offset)))

            // SHA-256 her zaman 0. bayttan: devamda ilk `offset` bayt yeniden okunup hash'e katılır.
            var hasher = SHA256()
            var position: Int64 = 0
            while position < r.offset {
                let want = Int(min(Int64(chunkSize), r.offset - position))
                let d = try item.source.read(at: position, length: want)
                guard !d.isEmpty else { throw AbortSignal(reason: "source changed (file \(item.id))") }
                hasher.update(data: d)
                position += Int64(d.count)
                try Task.checkCancellation()
            }
            tracker.report(fileId: item.id, name: item.name, index: index, count: items.count, fileDone: position,
                           fileSize: item.size, done: completedBytes + position, total: total, force: true)

            while position < item.size {
                try Task.checkCancellation()
                try await drain(inbox)
                let want = Int(min(Int64(chunkSize), item.size - position))
                let d = try item.source.read(at: position, length: want)
                guard !d.isEmpty else { throw AbortSignal(reason: "source changed (file \(item.id))") }
                hasher.update(data: d)
                try await channel.sendEncodedFrame(try FrameCodec.encodeData(d))
                position += Int64(d.count)
                tracker.addTransferred(d.count)
                tracker.report(fileId: item.id, name: item.name, index: index, count: items.count, fileDone: position,
                               fileSize: item.size, done: completedBytes + position, total: total, force: false)
            }
            let sha = sha256Hex(hasher.finalize())
            try await channel.sendEncodedFrame(try FrameCodec.encode(FileEndBody(id: item.id, sha256: sha)))
            completedBytes += item.size
            tracker.report(fileId: item.id, name: item.name, index: index, count: items.count, fileDone: item.size,
                           fileSize: item.size, done: completedBytes, total: total, force: true)
        }

        let doneFrame = try await expect(.done, inbox)
        let done = try FrameCodec.decodeDone(doneFrame.body)
        return TransferResult(outcome: done.ok && done.failed.isEmpty ? .completed : .completedWithFailures,
                              failedFileIds: done.failed, resumeOffsets: resumeOffsets)
    }

    /// İstenen türde çerçeve gelene kadar bekler; aradaki ACK'ler denetlenip atlanır.
    private func expect(_ type: FrameType, _ inbox: FrameInbox) async throws -> Frame {
        while true {
            let f = try await inbox.next()
            if f.type == .ack { _ = try FrameCodec.decodeAck(f.body); continue }
            guard f.type == type else { throw TransferError.protocolViolation("\(type) beklenirken \(f.type) geldi") }
            return f
        }
    }

    /// Bekleyen ACK/CANCEL'leri bloklamadan işler (CANCEL `RemoteCancel` fırlatır).
    private func drain(_ inbox: FrameInbox) async throws {
        while let f = try await inbox.poll() {
            guard f.type == .ack else { throw TransferError.protocolViolation("gönderim sürerken beklenmeyen \(f.type)") }
            _ = try FrameCodec.decodeAck(f.body)
        }
    }

    /// Her MANIFEST çerçevesi <= 65518 B olacak şekilde dosya listesini böler.
    static func paginate(_ files: [ManifestFile]) throws -> [[ManifestFile]] {
        let budget = FrameCodec.maxBody - 64   // {"files":[],"more":true} + pay
        let enc = JSONEncoder()
        var pages: [[ManifestFile]] = [[]]
        var size = 0
        for f in files {
            let n = try enc.encode(f).count + 1
            guard n <= budget else { throw TransferError.protocolViolation("MANIFEST girdisi çok büyük (id \(f.id))") }
            if size + n > budget {
                pages.append([])
                size = 0
            }
            pages[pages.count - 1].append(f)
            size += n
        }
        return pages
    }
}
