import Foundation
#if canImport(CryptoKit)
import CryptoKit
#else
import Crypto
#endif

public struct ReceiverOptions {
    /// Eş kimliği (kısmi dosya anahtarı için; genelde `DeviceId.hex`).
    public var peerId: String
    public var transferId: TransferId
    /// `offer` mesajındaki `count`/`total`; MANIFEST ile uyuşmazsa alıcı keser.
    public var expectedCount: Int
    public var expectedTotal: Int64
    public var maxFiles = TransferLimits.maxFiles
    public var maxNameLength = TransferLimits.maxNameLength
    public var ackIntervalBytes = TransferLimits.defaultAckIntervalBytes
    public var spaceMarginBytes = TransferLimits.defaultSpaceMarginBytes

    public init(peerId: String, transferId: TransferId, expectedCount: Int, expectedTotal: Int64) {
        self.peerId = peerId; self.transferId = transferId
        self.expectedCount = expectedCount; self.expectedTotal = expectedTotal
    }
}

/// PROTOCOL §6 alıcı: MANIFEST'i (sayfalı) toplar, sınırları denetler, her dosya için RESUME yollar, DATA'yı kısmi dosyaya
/// yazar, ~4 MB'da bir fsync sonrası ACK yollar, FILE_END'de tam dosya SHA-256'sını doğrular, sonunda DONE yollar.
/// Dosya adları `FileNameSanitizer` ile temizlenir; çakışmada "ad (2).ext". Görev iptali CANCEL yollar.
public final class FileReceiver {
    private let channel: SecureChannel
    private let storage: ReceiverStorage
    private let options: ReceiverOptions
    private let tracker: ProgressTracker
    private let frameReader: FrameReader?
    private var resumeOffsets: [Int: Int64] = [:]
    private var failed: [Int] = []
    private var received: [ReceivedFile] = []
    /// PROTOCOL §6.7: dosya kimliği -> temizlenmiş klasör bileşenleri (en üst klasör çakışması henüz çözülmemiş).
    private var dirComponents: [Int: [String]] = [:]
    /// Temizlenmiş en üst klasör adı -> hedef adı (aynı aktarımdaki tüm dosyalar aynı köke).
    private var topFolders: [String: String] = [:]

    public init(channel: SecureChannel, storage: ReceiverStorage, options: ReceiverOptions,
                frameReader: FrameReader? = nil, progress: ((TransferProgress) -> Void)? = nil) {
        self.channel = channel
        self.frameReader = frameReader
        self.storage = storage
        self.options = options
        self.tracker = ProgressTracker(progress)
    }

    public func run() async throws -> TransferResult {
        do {
            try await channel.ensureEstablished()   // yanıtlayan: başlatanın boş onay mesajını bekler (§2)
        } catch SessionError.connectionClosed {
            throw TransferError.connectionLost
        }
        let inbox = frameReader.map { FrameInbox(reader: $0) } ?? FrameInbox(channel: channel)
        defer { inbox.stop() }
        do {
            return try await receiveAll(inbox)
        } catch is CancellationError {
            await trySendCancel("canceled")
            return result(.canceled)
        } catch let r as RemoteCancel {
            var res = result(.canceled)
            res.canceledByRemote = true
            res.error = r.reason
            return res
        } catch let a as AbortSignal {
            await trySendCancel(a.reason)
            var res = result(.aborted)
            res.error = a.reason
            return res
        } catch let e as ProtocolError {
            await trySendCancel("protocol error")
            throw TransferError.protocolViolation("\(e)")
        } catch let e as TransferError {
            if case .protocolViolation = e { await trySendCancel("protocol error") }
            throw e
        }
    }

    private func result(_ outcome: TransferOutcome) -> TransferResult {
        TransferResult(outcome: outcome, failedFileIds: failed, resumeOffsets: resumeOffsets, receivedFiles: received)
    }

    private func trySendCancel(_ reason: String) async {
        if let body = try? FrameCodec.encode(CancelBody(reason: reason)) { try? await channel.sendEncodedFrame(body) }
    }

    // MARK: Akış

    private func receiveAll(_ inbox: FrameInbox) async throws -> TransferResult {
        let files = try await receiveManifest(inbox)
        let total = files.reduce(Int64(0)) { $0 + $1.size }
        var completedBytes: Int64 = 0
        for (index, file) in files.enumerated() {
            try await receiveFile(file, index: index, count: files.count, completedBytes: completedBytes, total: total, inbox)
            completedBytes += file.size
        }
        let done = DoneBody(ok: failed.isEmpty, failed: failed)
        try await channel.sendEncodedFrame(try FrameCodec.encode(done))
        return result(failed.isEmpty ? .completed : .completedWithFailures)
    }

    /// `more=false` gelene kadar MANIFEST sayfalarını toplar; 10 000 sınırı sayfalar boyunca geçerlidir.
    private func receiveManifest(_ inbox: FrameInbox) async throws -> [ManifestFile] {
        var files: [ManifestFile] = []
        var ids = Set<Int>()
        var total: Int64 = 0
        while true {
            let f = try await inbox.next()
            guard f.type == .manifest else { throw TransferError.protocolViolation("MANIFEST beklenirken \(f.type) geldi") }
            let page = try FrameCodec.decodeManifest(f.body)
            for file in page.files {
                if files.count + 1 > options.maxFiles { throw AbortSignal(reason: "too many files (> \(options.maxFiles))") }
                if !ids.insert(file.id).inserted { throw AbortSignal(reason: "duplicate file id \(file.id)") }
                if file.size < 0 { throw AbortSignal(reason: "negative size for file \(file.id)") }
                if file.name.utf16.count > options.maxNameLength {
                    throw AbortSignal(reason: "file name longer than \(options.maxNameLength) characters (id \(file.id))")
                }
                if let dir = file.dir {
                    do { dirComponents[file.id] = try FolderPath.sanitizedComponents(dir) }
                    catch let e as FolderPathError {
                        switch e {
                        case .malformed(let m): throw TransferError.protocolViolation("invalid dir for file \(file.id): \(m)")
                        case .tooDeep, .tooLong: throw AbortSignal(reason: "dir limit exceeded (file \(file.id))")
                        case .escapesRoot: throw TransferError.protocolViolation("dir escapes root (file \(file.id))")
                        }
                    }
                }
                let (sum, overflow) = total.addingReportingOverflow(file.size)
                if overflow { throw AbortSignal(reason: "manifest total overflows") }
                total = sum
                files.append(file)
            }
            if !page.hasMore { break }
        }
        if files.count != options.expectedCount {
            throw AbortSignal(reason: "manifest count \(files.count) does not match offer \(options.expectedCount)")
        }
        if total != options.expectedTotal {
            throw AbortSignal(reason: "manifest total \(total) does not match offer \(options.expectedTotal)")
        }
        let free = try storage.freeSpaceBytes()
        if total > free - options.spaceMarginBytes {
            throw AbortSignal(reason: "insufficient disk space (need \(total), free \(free), margin \(options.spaceMarginBytes))")
        }
        return files
    }

    private func receiveFile(_ file: ManifestFile, index: Int, count: Int, completedBytes: Int64, total: Int64,
                             _ inbox: FrameInbox) async throws {
        let key = PartialKey(peerId: options.peerId, transferId: options.transferId, fileId: file.id)

        // Önceki oturumda tamamlanmış dosya: yeniden alma, yalnız SHA'yı doğrula (RESUME = boyut).
        if let completed = try storage.completedFile(for: key), completed.size == file.size, completed.mtime == file.mtime {
            try await receiveAlreadyCompleted(file, completed, index: index, count: count,
                                              completedBytes: completedBytes, total: total, inbox)
            return
        }

        let partial = try storage.openPartial(for: key, size: file.size, mtime: file.mtime)
        var hasher = SHA256()
        var position: Int64 = 0
        while position < partial.offset {
            let want = Int(min(Int64(65_536), partial.offset - position))
            let d = try partial.read(at: position, length: want)
            guard !d.isEmpty else { break }
            hasher.update(data: d)
            position += Int64(d.count)
        }
        let offset = position
        resumeOffsets[file.id] = offset
        try await channel.sendEncodedFrame(try FrameCodec.encode(ResumeBody(id: file.id, offset: offset)))

        let start = try await inbox.next()
        guard start.type == .fileStart else { throw TransferError.protocolViolation("FILE_START beklenirken \(start.type) geldi") }
        let startBody = try FrameCodec.decodeFileStart(start.body)
        guard startBody.id == file.id, startBody.offset == offset else {
            throw TransferError.protocolViolation("FILE_START (\(startBody.id), \(startBody.offset)) RESUME (\(file.id), \(offset)) ile uyuşmuyor")
        }

        var written = offset
        var sinceAck: Int64 = 0
        tracker.report(fileId: file.id, name: file.name, index: index, count: count, fileDone: written, fileSize: file.size,
                       done: completedBytes + written, total: total, force: true)
        let end: FileEndBody
        while true {
            let frame = try await inbox.next()
            if frame.type == .data {
                if written + Int64(frame.body.count) > file.size {
                    try? partial.discard()
                    throw AbortSignal(reason: "file \(file.id) exceeds declared size \(file.size)")
                }
                try partial.append(frame.body)
                hasher.update(data: frame.body)
                written += Int64(frame.body.count)
                sinceAck += Int64(frame.body.count)
                tracker.addTransferred(frame.body.count)
                if sinceAck >= Int64(options.ackIntervalBytes) {
                    try partial.sync()   // ACK yalnız veri diske (fsync) ve kayıt kalıcılaştıktan sonra
                    try await channel.sendEncodedFrame(try FrameCodec.encode(AckBody(id: file.id, offset: written)))
                    sinceAck = 0
                }
                tracker.report(fileId: file.id, name: file.name, index: index, count: count, fileDone: written,
                               fileSize: file.size, done: completedBytes + written, total: total, force: false)
            } else if frame.type == .fileEnd {
                end = try FrameCodec.decodeFileEnd(frame.body)
                guard end.id == file.id else { throw TransferError.protocolViolation("FILE_END id \(end.id), beklenen \(file.id)") }
                break
            } else {
                throw TransferError.protocolViolation("dosya sürerken beklenmeyen çerçeve: \(frame.type)")
            }
        }

        let actual = sha256Hex(hasher.finalize())
        if written == file.size && actual == end.sha256 {
            let path: String
            do {
                path = try partial.commit(directory: try resolvedDirectory(file), fileName: FileNameSanitizer.sanitize(file.name),
                                          sha256: actual)
            } catch let e as FolderPathError {
                try? partial.discard()
                throw TransferError.protocolViolation("dir rejected for file \(file.id): \(e)")
            }
            received.append(ReceivedFile(id: file.id, originalName: file.name, storedPath: path))
        } else {
            try partial.discard()   // bütünlük hatası: sil, DONE.failed'e gir
            failed.append(file.id)
        }
        tracker.report(fileId: file.id, name: file.name, index: index, count: count, fileDone: file.size, fileSize: file.size,
                       done: completedBytes + file.size, total: total, force: true)
    }

    /// Dosyanın hedef klasör bileşenleri; en üst klasör çakışması bir kez çözülür ve aktarım boyunca (devam dahil) sabit kalır.
    private func resolvedDirectory(_ file: ManifestFile) throws -> [String] {
        guard let comps = dirComponents[file.id], let top = comps.first else { return [] }
        let resolved: String
        if let known = topFolders[top] {
            resolved = known
        } else {
            resolved = try storage.resolveTopFolder(peerId: options.peerId, transferId: options.transferId, name: top)
            topFolders[top] = resolved
        }
        return [resolved] + comps.dropFirst()
    }

    /// Dosya önceki oturumda tamamen alınmıştı: RESUME = boyut. Gönderen FILE_START(boyut) ve FILE_END yollar
    /// (kaynağı baştan hash'ler); DATA gelmez. Kayıtlı SHA eşleşirse dosya teslim edilmiş sayılır.
    private func receiveAlreadyCompleted(_ file: ManifestFile, _ completed: CompletedFile, index: Int, count: Int,
                                         completedBytes: Int64, total: Int64, _ inbox: FrameInbox) async throws {
        resumeOffsets[file.id] = file.size
        try await channel.sendEncodedFrame(try FrameCodec.encode(ResumeBody(id: file.id, offset: file.size)))
        let start = try await inbox.next()
        guard start.type == .fileStart else { throw TransferError.protocolViolation("FILE_START beklenirken \(start.type) geldi") }
        let s = try FrameCodec.decodeFileStart(start.body)
        guard s.id == file.id, s.offset == file.size else {
            throw TransferError.protocolViolation("FILE_START (\(s.id), \(s.offset)) RESUME (\(file.id), \(file.size)) ile uyuşmuyor")
        }
        let endFrame = try await inbox.next()
        guard endFrame.type == .fileEnd else { throw TransferError.protocolViolation("FILE_END beklenirken \(endFrame.type) geldi") }
        let end = try FrameCodec.decodeFileEnd(endFrame.body)
        guard end.id == file.id else { throw TransferError.protocolViolation("FILE_END id \(end.id), beklenen \(file.id)") }
        if end.sha256 == completed.sha256 {
            received.append(ReceivedFile(id: file.id, originalName: file.name, storedPath: completed.storedPath))
        } else {
            failed.append(file.id)   // aynı boyut/mtime ama içerik farklı: teslim edilen kopya korunur, başarısız bildirilir
        }
        tracker.report(fileId: file.id, name: file.name, index: index, count: count, fileDone: file.size, fileSize: file.size,
                       done: completedBytes + file.size, total: total, force: true)
    }
}
