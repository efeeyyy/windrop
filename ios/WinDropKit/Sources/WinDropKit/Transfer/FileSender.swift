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
    /// PROTOCOL §5a: gönderilen kökten göreli klasör yolu (`/` ayırıcı); dosya kökteyse nil.
    public var dir: String?
    public init(id: Int, name: String, size: Int64, mtime: Int64, source: FileSource, dir: String? = nil) {
        self.id = id; self.name = name; self.size = size; self.mtime = mtime; self.source = source; self.dir = dir
    }
}

/// PROTOCOL §5 gönderen: MANIFEST (sayfalı) -> her dosya için [alıcıdan RESUME -> FILE_START -> DATA… -> FILE_END] -> DONE bekler.
/// Gelen ACK/CANCEL çerçeveleri gönderim sürerken işlenir (uzak iptal hemen fark edilir). Görev iptali CANCEL yollar.
public final class FileSender {
    private let channel: SecureChannel
    private let items: [SendItem]
    private let chunkSize: Int
    private let tracker: ProgressTracker
    private let bytesPerSecond: Int?
    private let frameReader: FrameReader?
    private let resumeTimeout: TimeInterval?
    private var pacingStart: UInt64?
    private var pacedBytes: Int64 = 0
    private var resumeOffsets: [Int: Int64] = [:]

    public init(channel: SecureChannel, items: [SendItem], chunkSize: Int = TransferLimits.defaultChunkSize,
                bytesPerSecond: Int? = nil, frameReader: FrameReader? = nil,
                resumeTimeout: TimeInterval? = TransferLimits.tcpResumeTimeout,
                progress: ((TransferProgress) -> Void)? = nil) {
        self.channel = channel
        self.bytesPerSecond = bytesPerSecond.flatMap { $0 > 0 ? $0 : nil }
        self.frameReader = frameReader
        self.resumeTimeout = resumeTimeout
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
        let inbox = frameReader.map { FrameInbox(reader: $0) } ?? FrameInbox(channel: channel)
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
        let manifest = items.map { ManifestFile(id: $0.id, name: $0.name, size: $0.size, mtime: $0.mtime, dir: $0.dir) }
        // MANIFEST çerçevesi taşımanın çerçeve gövdesi sınırına sığmalı (BLE'de 8192 − 16 − 1; DECISIONS 7).
        let pages = try Self.paginate(
            manifest, budget: min(FrameCodec.maxBody, channel.maxFrameBodySize) - Self.manifestEnvelope)
        for (i, page) in pages.enumerated() {
            let body = ManifestBody(files: page, more: i < pages.count - 1 ? true : nil)
            try await channel.sendEncodedFrame(try FrameCodec.encode(body))
        }

        let total = items.reduce(Int64(0)) { $0 + $1.size }
        var completedBytes: Int64 = 0
        for (index, item) in items.enumerated() {
            try Task.checkCancellation()
            let resume = try await expectResume(inbox)
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
                try await pace(d.count)
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

    /// Hız sınırı (BLE): gönderilen toplam bayta göre hedef zamana kadar uyur (kısa dalgalanmayı sonraki parça kapatır).
    private func pace(_ count: Int) async throws {
        guard let rate = bytesPerSecond else { return }
        let now = DispatchTime.now().uptimeNanoseconds
        let start = pacingStart ?? now
        pacingStart = start
        pacedBytes += Int64(count)
        let due = start &+ UInt64(pacedBytes) &* 1_000_000_000 / UInt64(rate)
        if due > now { try await Task.sleep(nanoseconds: due - now) }
    }

    /// RESUME'u en çok `resumeTimeout` bekler; aşılırsa `TransferError.timeout` (aktarım hata).
    private func expectResume(_ inbox: FrameInbox) async throws -> Frame {
        guard let limit = resumeTimeout else { return try await expect(.resume, inbox) }
        return try await withThrowingTaskGroup(of: Frame?.self) { group in
            group.addTask { try await self.expect(.resume, inbox) }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(limit * 1_000_000_000))
                return nil
            }
            let first = try await group.next() ?? nil
            group.cancelAll()
            guard let frame = first else { throw TransferError.timeout }
            return frame
        }
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

    /// `{"files":[],"more":true}` + pay.
    static let manifestEnvelope = 64

    /// Her MANIFEST çerçevesi `budget` B (varsayılan <= 65518) olacak şekilde dosya listesini böler.
    static func paginate(_ files: [ManifestFile], budget: Int = FrameCodec.maxBody - manifestEnvelope) throws -> [[ManifestFile]] {
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
