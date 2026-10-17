import XCTest
import Foundation
#if canImport(CryptoKit)
import CryptoKit
#else
import Crypto
#endif
@testable import WinDropKit

final class MemoryFileSource: FileSource {
    let data: Data
    init(_ data: Data) { self.data = data }
    func read(at offset: Int64, length: Int) throws -> Data {
        let start = Int(offset)
        guard start < data.count else { return Data() }
        return data.subdata(in: start..<min(data.count, start + length))
    }
}

/// Okuma ofseti eşiği aşınca kanca çağıran kaynak (iptali deterministik tetiklemek için).
final class HookSource: FileSource {
    let inner: FileSource
    let threshold: Int64
    let hook: () -> Void
    init(_ inner: FileSource, threshold: Int64, hook: @escaping () -> Void) { self.inner = inner; self.threshold = threshold; self.hook = hook }
    func read(at offset: Int64, length: Int) throws -> Data {
        if offset >= threshold { hook() }
        return try inner.read(at: offset, length: length)
    }
}

final class HookPartial: PartialFile {
    let inner: PartialFile
    let threshold: Int64
    let hook: () -> Void
    init(_ inner: PartialFile, threshold: Int64, hook: @escaping () -> Void) { self.inner = inner; self.threshold = threshold; self.hook = hook }
    var offset: Int64 { inner.offset }
    func read(at: Int64, length: Int) throws -> Data { try inner.read(at: at, length: length) }
    func append(_ data: Data) throws { try inner.append(data); if inner.offset >= threshold { hook() } }
    func sync() throws { try inner.sync() }
    func commit(fileName: String, sha256: String) throws -> String { try inner.commit(fileName: fileName, sha256: sha256) }
    func discard() throws { try inner.discard() }
}

final class HookStorage: ReceiverStorage {
    let inner: ReceiverStorage
    let threshold: Int64
    let hook: () -> Void
    init(_ inner: ReceiverStorage, threshold: Int64, hook: @escaping () -> Void) { self.inner = inner; self.threshold = threshold; self.hook = hook }
    func freeSpaceBytes() throws -> Int64 { try inner.freeSpaceBytes() }
    func completedFile(for key: PartialKey) throws -> CompletedFile? { try inner.completedFile(for: key) }
    func openPartial(for key: PartialKey, size: Int64, mtime: Int64) throws -> PartialFile {
        HookPartial(try inner.openPartial(for: key, size: size, mtime: mtime), threshold: threshold, hook: hook)
    }
}

/// Hep aynı bayt akışı üreten sahte kaynak (büyük dosyalar için; belleğe sığdırmadan).
func patternData(count: Int, seed: UInt8 = 1) -> Data {
    var d = Data(count: count)
    var x = UInt32(seed) &* 2_654_435_761 | 1
    d.withUnsafeMutableBytes { (p: UnsafeMutableRawBufferPointer) in
        for i in 0..<count {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5
            p[i] = UInt8(truncatingIfNeeded: x)
        }
    }
    return d
}

func sha256Of(_ d: Data) -> String { sha256Hex(SHA256.hash(data: d)) }

/// Kurulmuş IK oturumu (iPhone = başlatan).
struct Session {
    let phone: SecureChannel
    let pc: SecureChannel
    let phoneEnd: PipeEnd
    let pcEnd: PipeEnd

    func closeAll() async { await phoneEnd.closeBoth(); await pcEnd.closeBoth() }
}

func makeSession(transferId: TransferId, phoneKey: NoisePrivateKey, pcKey: NoisePrivateKey) async throws -> Session {
    let (phoneEnd, pcEnd) = PipeEnd.makePair()
    let prefix = try Prefix(transport: .tcp, transferId: transferId.bytes)
    async let pcTask = SecureChannel.acceptIK(transport: TcpMessageTransport(stream: pcEnd), localKey: pcKey, isTrusted: { _ in true })
    let phone = try await SecureChannel.connectIK(transport: TcpMessageTransport(stream: phoneEnd), prefix: prefix,
                                                  localKey: phoneKey, remoteStaticKey: pcKey.publicKey.rawRepresentation)
    return Session(phone: phone, pc: try await pcTask, phoneEnd: phoneEnd, pcEnd: pcEnd)
}

final class TransferTests: XCTestCase {
    let phoneKey = NoisePrivateKey()
    let pcKey = NoisePrivateKey()
    var base: URL!
    var inbox: URL!
    var peerId: String { DeviceId.hex(publicKey: phoneKey.publicKey.rawRepresentation) }

    override func setUpWithError() throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent("windrop-test-\(UUID().uuidString)")
        inbox = base.appendingPathComponent("inbox")
        try FileManager.default.createDirectory(at: inbox, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: base)
    }

    // MARK: Yardımcılar

    func options(_ id: TransferId, items: [SendItem], ack: Int = TransferLimits.defaultAckIntervalBytes) -> ReceiverOptions {
        var o = ReceiverOptions(peerId: peerId, transferId: id, expectedCount: items.count,
                                expectedTotal: items.reduce(0) { $0 + $1.size })
        o.ackIntervalBytes = ack
        o.spaceMarginBytes = 0
        return o
    }

    func memItem(_ id: Int, _ name: String, _ data: Data, mtime: Int64 = 1_700_000_000_000) -> SendItem {
        SendItem(id: id, name: name, size: Int64(data.count), mtime: mtime, source: MemoryFileSource(data))
    }

    func runTransfer(_ s: Session, items: [SendItem], opts: ReceiverOptions, storage: ReceiverStorage? = nil,
                     chunk: Int = TransferLimits.defaultChunkSize,
                     senderProgress: ((TransferProgress) -> Void)? = nil,
                     receiverProgress: ((TransferProgress) -> Void)? = nil)
        async throws -> (sender: TransferResult, receiver: TransferResult) {
        let store = try storage ?? DirectoryReceiverStorage(directory: inbox)
        let sender = FileSender(channel: s.phone, items: items, chunkSize: chunk, progress: senderProgress)
        let receiver = FileReceiver(channel: s.pc, storage: store, options: opts, progress: receiverProgress)
        async let r = receiver.run()
        let sr = try await sender.run()
        return (sr, try await r)
    }

    func readStored(_ r: TransferResult, _ id: Int) throws -> Data {
        let f = try XCTUnwrap(r.receivedFiles.first { $0.id == id }, "dosya \(id) alınmadı")
        return try Data(contentsOf: URL(fileURLWithPath: f.storedPath))
    }

    // MARK: Temel akış

    func testMultipleFilesIncludingEmptyAndTinyAndProgress() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let items = [
            memItem(0, "bos.txt", Data()),
            memItem(1, "bir.bin", Data([7])),
            memItem(2, "orta.bin", patternData(count: 150_000, seed: 2)),
            memItem(3, "Türkçe ğüş.dat", patternData(count: 64_000, seed: 3)),
            memItem(4, "buyuk.bin", patternData(count: 700_000, seed: 4)),
        ]
        final class Collector { var events: [TransferProgress] = []; func add(_ p: TransferProgress) { events.append(p) } }
        let sp = Collector(), rp = Collector()
        let (sr, rr) = try await runTransfer(s, items: items, opts: options(id, items: items),
                                             senderProgress: sp.add, receiverProgress: rp.add)
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(rr.outcome, .completed)
        XCTAssertEqual(rr.failedFileIds, [])
        XCTAssertEqual(rr.receivedFiles.count, 5)
        for it in items {
            let got = try readStored(rr, it.id)
            XCTAssertEqual(got.count, Int(it.size), it.name)
            XCTAssertEqual(sha256Of(got), sha256Of((it.source as! MemoryFileSource).data), it.name)
        }
        XCTAssertEqual(sr.resumeOffsets, [0: 0, 1: 0, 2: 0, 3: 0, 4: 0])
        // İlerleme: son olay toplamı verir, bayt sayıları azalmaz.
        for events in [sp.events, rp.events] {
            let last = try XCTUnwrap(events.last)
            XCTAssertEqual(last.bytesDone, last.totalBytes)
            XCTAssertEqual(last.totalBytes, 914_001)
            XCTAssertEqual(last.fileCount, 5)
            XCTAssertTrue(zip(events, events.dropFirst()).allSatisfy { $0.bytesDone <= $1.bytesDone })
        }
        // Kısmi dosya kalmaz (yalnız tamamlandı kayıtları).
        let leftovers = try FileManager.default.contentsOfDirectory(atPath: inbox.appendingPathComponent(".windrop").path)
        XCTAssertTrue(leftovers.allSatisfy { $0.hasSuffix(".done") }, "\(leftovers)")
        await s.closeAll()
    }

    func testInitiatorCanBeTheReceiver() async throws {
        // İki yön: iPhone (başlatan) alıcı, PC gönderen. PC, boş onay mesajına dek bekler.
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let items = [memItem(0, "pc-den.bin", patternData(count: 200_000, seed: 9))]
        let sender = FileSender(channel: s.pc, items: items)
        let receiver = FileReceiver(channel: s.phone, storage: try DirectoryReceiverStorage(directory: inbox),
                                    options: options(id, items: items))
        async let rr = receiver.run()
        let sr = try await sender.run()
        let r = try await rr
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(sha256Of(try readStored(r, 0)), sha256Of((items[0].source as! MemoryFileSource).data))
        await s.closeAll()
    }

    func test20MBTransferWithAcks() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let payload = patternData(count: 20 * 1024 * 1024, seed: 5)
        let src = base.appendingPathComponent("big.bin")
        try payload.write(to: src)
        let item = try DiskFileSource.makeItem(id: 0, url: src)
        XCTAssertEqual(item.size, Int64(payload.count))
        let t0 = Date()
        let (sr, rr) = try await runTransfer(s, items: [item], opts: options(id, items: [item]))
        let dt = Date().timeIntervalSince(t0)
        print("20MB aktarım: \(String(format: "%.2f", dt)) sn")
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(rr.outcome, .completed)
        XCTAssertEqual(sha256Of(try readStored(rr, 0)), sha256Of(payload))
        await s.closeAll()
    }

    // MARK: Kopma sonrası devam

    /// 1. oturum `cutAfter` bayt yazıldıktan sonra kesilir; sonuçlar (hata) döner.
    func runInterrupted(id: TransferId, item: SendItem, cutAfter: Int, ack: Int) async throws {
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        s.phoneEnd.writeBudget = cutAfter
        let store = try DirectoryReceiverStorage(directory: inbox)
        let sender = FileSender(channel: s.phone, items: [item])
        let receiver = FileReceiver(channel: s.pc, storage: store, options: options(id, items: [item], ack: ack))
        async let rr: Result<TransferResult, Error> = {
            do { return .success(try await receiver.run()) } catch { return .failure(error) }
        }()
        var senderFailed = false
        do { _ = try await sender.run() } catch { senderFailed = true }
        XCTAssertTrue(senderFailed, "kesilen gönderen hata vermeli")
        switch await rr {
        case .failure(let e): XCTAssertEqual(e as? TransferError, .connectionLost)
        case .success(let r): XCTFail("alıcı kopmayı fark etmeli: \(r)")
        }
    }

    func testResumeAfterDropVerifiesWholeFileSha() async throws {
        let id = TransferId.random()
        let payload = patternData(count: 3_000_000, seed: 6)
        let src = base.appendingPathComponent("src.bin")
        try payload.write(to: src)
        let item = try DiskFileSource.makeItem(id: 0, url: src)

        try await runInterrupted(id: id, item: item, cutAfter: 1_400_000, ack: 256 * 1024)

        // Yeni el sıkışma (yeni Noise oturumu), aynı transferId ve eş.
        let s2 = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let item2 = try DiskFileSource.makeItem(id: 0, url: src)
        let (sr, rr) = try await runTransfer(s2, items: [item2], opts: options(id, items: [item2], ack: 256 * 1024))
        let offset = try XCTUnwrap(rr.resumeOffsets[0])
        XCTAssertGreaterThan(offset, 0, "devam edilmeli")
        XCTAssertLessThan(offset, Int64(payload.count))
        XCTAssertEqual(offset % 64_000, 0)             // son ACK'lenen DATA sınırı
        XCTAssertEqual(sr.resumeOffsets[0], offset)
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(rr.outcome, .completed)
        XCTAssertEqual(sha256Of(try readStored(rr, 0)), sha256Of(payload))
        await s2.closeAll()
    }

    func testSourceChangedRestartsFromZero() async throws {
        let id = TransferId.random()
        let src = base.appendingPathComponent("src.bin")
        let original = patternData(count: 2_000_000, seed: 7)
        try original.write(to: src)
        try await runInterrupted(id: id, item: try DiskFileSource.makeItem(id: 0, url: src), cutAfter: 900_000, ack: 256 * 1024)

        // Kaynak değişti: aynı boyut, farklı içerik ve mtime.
        let changed = patternData(count: 2_000_000, seed: 8)
        try changed.write(to: src)
        try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSinceNow: 3600)], ofItemAtPath: src.path)
        let item2 = try DiskFileSource.makeItem(id: 0, url: src)

        let s2 = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let (sr, rr) = try await runTransfer(s2, items: [item2], opts: options(id, items: [item2], ack: 256 * 1024))
        XCTAssertEqual(rr.resumeOffsets[0], 0, "size/mtime değişince baştan")
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(sha256Of(try readStored(rr, 0)), sha256Of(changed))
        await s2.closeAll()
    }

    func testCorruptedPartialFailsShaAndIsReported() async throws {
        let id = TransferId.random()
        let payload = patternData(count: 1_500_000, seed: 10)
        let src = base.appendingPathComponent("src.bin")
        try payload.write(to: src)
        let item = try DiskFileSource.makeItem(id: 0, url: src)
        try await runInterrupted(id: id, item: item, cutAfter: 800_000, ack: 128 * 1024)

        // Kalıcı bölgedeki bir baytı boz.
        let stateDir = inbox.appendingPathComponent(".windrop")
        let part = try XCTUnwrap(try FileManager.default.contentsOfDirectory(atPath: stateDir.path).first { $0.hasSuffix(".part") })
        let h = try FileHandle(forUpdating: stateDir.appendingPathComponent(part))
        try h.seek(toOffset: 10)
        try h.write(contentsOf: Data([0xFF, 0xFE]))
        try h.close()

        let s2 = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let (sr, rr) = try await runTransfer(s2, items: [try DiskFileSource.makeItem(id: 0, url: src)],
                                             opts: options(id, items: [item], ack: 128 * 1024))
        XCTAssertGreaterThan(rr.resumeOffsets[0] ?? 0, 0)
        XCTAssertEqual(rr.outcome, .completedWithFailures)
        XCTAssertEqual(rr.failedFileIds, [0])
        XCTAssertEqual(sr.outcome, .completedWithFailures)
        XCTAssertEqual(sr.failedFileIds, [0])
        XCTAssertTrue(rr.receivedFiles.isEmpty)
        let visible = try FileManager.default.contentsOfDirectory(atPath: inbox.path).filter { !$0.hasPrefix(".") }
        XCTAssertEqual(visible, [], "bozuk dosya teslim edilmemeli")
        await s2.closeAll()
    }

    func testCompletedFileIsNotResentAfterCancelAndResume() async throws {
        // 2 dosya: ilki tamamlanır, ikincisinde kopar. Devamda 1. dosya RESUME = boyut (yeniden gönderilmez), SHA yine doğrulanır.
        let id = TransferId.random()
        let a = patternData(count: 300_000, seed: 11), b = patternData(count: 2_000_000, seed: 12)
        let items = [memItem(0, "a.bin", a), memItem(1, "b.bin", b)]
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        s.phoneEnd.writeBudget = 900_000
        let store = try DirectoryReceiverStorage(directory: inbox)
        let sender = FileSender(channel: s.phone, items: items)
        let receiver = FileReceiver(channel: s.pc, storage: store, options: options(id, items: items, ack: 128 * 1024))
        async let rr: Result<TransferResult, Error> = {
            do { return .success(try await receiver.run()) } catch { return .failure(error) }
        }()
        do { _ = try await sender.run(); XCTFail() } catch {}
        _ = await rr

        let s2 = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let (sr, r2) = try await runTransfer(s2, items: items, opts: options(id, items: items, ack: 128 * 1024))
        XCTAssertEqual(r2.resumeOffsets[0], Int64(a.count), "tamamlanmış dosya yeniden gönderilmez")
        XCTAssertGreaterThan(r2.resumeOffsets[1] ?? 0, 0)
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(sha256Of(try readStored(r2, 0)), sha256Of(a))
        XCTAssertEqual(sha256Of(try readStored(r2, 1)), sha256Of(b))
        let visible = try FileManager.default.contentsOfDirectory(atPath: inbox.path).filter { !$0.hasPrefix(".") }
        XCTAssertEqual(Set(visible), ["a.bin", "b.bin"])
        await s2.closeAll()
    }

    // MARK: İptal

    final class TaskBox<T> { var task: Task<T, Error>? }

    func testSenderCancelMidFile() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let box = TaskBox<TransferResult>()
        let src = HookSource(MemoryFileSource(patternData(count: 4_000_000, seed: 13)), threshold: 500_000) { box.task?.cancel() }
        let items = [SendItem(id: 0, name: "buyuk.bin", size: 4_000_000, mtime: 1, source: src)]
        let sender = FileSender(channel: s.phone, items: items)
        let receiver = FileReceiver(channel: s.pc, storage: try DirectoryReceiverStorage(directory: inbox),
                                    options: options(id, items: items))
        async let rr = receiver.run()
        box.task = Task { try await sender.run() }
        let sr = try await box.task!.value
        let r = try await rr
        XCTAssertEqual(sr.outcome, .canceled)
        XCTAssertFalse(sr.canceledByRemote)
        XCTAssertEqual(r.outcome, .canceled)
        XCTAssertTrue(r.canceledByRemote)
        XCTAssertEqual(r.error, "canceled")
        XCTAssertTrue(r.receivedFiles.isEmpty)
        await s.closeAll()
    }

    func testReceiverCancelMidFile() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let items = [memItem(0, "buyuk.bin", patternData(count: 4_000_000, seed: 14))]
        let box = TaskBox<TransferResult>()
        let hooked = HookStorage(try DirectoryReceiverStorage(directory: inbox), threshold: 500_000) { box.task?.cancel() }
        let receiver = FileReceiver(channel: s.pc, storage: hooked, options: options(id, items: items))
        let sender = FileSender(channel: s.phone, items: items)
        async let sr = sender.run()
        box.task = Task { try await receiver.run() }
        let r = try await box.task!.value
        let sres = try await sr
        XCTAssertEqual(r.outcome, .canceled)
        XCTAssertFalse(r.canceledByRemote)
        XCTAssertEqual(sres.outcome, .canceled)
        XCTAssertTrue(sres.canceledByRemote)
        await s.closeAll()
    }

    // MARK: Sınırlar ve kötü niyetli gönderen

    /// Alıcıyı ham çerçevelerle süren sahte gönderen.
    func rawSender(_ s: Session, id: TransferId, files: [ManifestFile], count: Int? = nil, total: Int64? = nil,
                   storage: ReceiverStorage? = nil, script: @escaping (SecureChannel) async throws -> Void)
        async throws -> TransferResult {
        let opts = ReceiverOptions(peerId: peerId, transferId: id, expectedCount: count ?? files.count,
                                   expectedTotal: total ?? files.reduce(0) { $0 + $1.size })
        var o = opts
        o.spaceMarginBytes = 0
        let receiver = FileReceiver(channel: s.pc, storage: try storage ?? DirectoryReceiverStorage(directory: inbox), options: o)
        async let rr = receiver.run()
        try await s.phone.sendEncodedFrame(try FrameCodec.encode(ManifestBody(files: files)))
        try await script(s.phone)
        return try await rr
    }

    func testDataBeyondDeclaredSizeAbortsTransfer() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let r = try await rawSender(s, id: id, files: [ManifestFile(id: 0, name: "x.bin", size: 100, mtime: 1)]) { ch in
            _ = try await ch.receiveFrame()   // RESUME
            try await ch.sendEncodedFrame(try FrameCodec.encode(FileStartBody(id: 0, offset: 0)))
            try await ch.sendEncodedFrame(try FrameCodec.encodeData(Data(count: 60)))
            try await ch.sendEncodedFrame(try FrameCodec.encodeData(Data(count: 60)))   // 120 > 100
            let cancel = try await ch.receiveFrame()
            XCTAssertEqual(cancel?.type, .cancel)
        }
        XCTAssertEqual(r.outcome, .aborted)
        XCTAssertTrue(r.error?.contains("exceeds declared size") ?? false, r.error ?? "")
        let visible = try FileManager.default.contentsOfDirectory(atPath: inbox.path).filter { !$0.hasPrefix(".") }
        XCTAssertEqual(visible, [])
        await s.closeAll()
    }

    func testWrongFileEndShaIsReportedInDone() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let r = try await rawSender(s, id: id, files: [ManifestFile(id: 5, name: "x.bin", size: 10, mtime: 1)]) { ch in
            _ = try await ch.receiveFrame()
            try await ch.sendEncodedFrame(try FrameCodec.encode(FileStartBody(id: 5, offset: 0)))
            try await ch.sendEncodedFrame(try FrameCodec.encodeData(Data(count: 10)))
            try await ch.sendEncodedFrame(try FrameCodec.encode(FileEndBody(id: 5, sha256: String(repeating: "0", count: 64))))
            let done = try await ch.receiveFrame()
            XCTAssertEqual(done?.type, .done)
            XCTAssertEqual(try FrameCodec.decodeDone(done!.body), DoneBody(ok: false, failed: [5]))
        }
        XCTAssertEqual(r.outcome, .completedWithFailures)
        XCTAssertEqual(r.failedFileIds, [5])
        await s.closeAll()
    }

    func testShortFileEndFails() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let data = Data(count: 4)
        let r = try await rawSender(s, id: id, files: [ManifestFile(id: 0, name: "x.bin", size: 10, mtime: 1)]) { ch in
            _ = try await ch.receiveFrame()
            try await ch.sendEncodedFrame(try FrameCodec.encode(FileStartBody(id: 0, offset: 0)))
            try await ch.sendEncodedFrame(try FrameCodec.encodeData(data))
            try await ch.sendEncodedFrame(try FrameCodec.encode(FileEndBody(id: 0, sha256: sha256Of(data))))   // sha doğru ama eksik
            _ = try await ch.receiveFrame()
        }
        XCTAssertEqual(r.failedFileIds, [0])
        await s.closeAll()
    }

    func testTooManyFilesAcrossPagesAborts() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let items = (0..<10_001).map { memItem($0, "n\($0)", Data()) }
        let (sr, rr) = try await runTransfer(s, items: items, opts: options(id, items: items))
        XCTAssertEqual(rr.outcome, .aborted)
        XCTAssertTrue(rr.error?.contains("too many files") ?? false, rr.error ?? "")
        XCTAssertEqual(sr.outcome, .canceled)
        XCTAssertTrue(sr.canceledByRemote)
        await s.closeAll()
    }

    func testExactly10000FilesAreAccepted() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let items = (0..<10_000).map { memItem($0, "f\($0)", Data()) }
        let (sr, rr) = try await runTransfer(s, items: items, opts: options(id, items: items))
        XCTAssertEqual(rr.outcome, .completed)
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(rr.receivedFiles.count, 10_000)
        await s.closeAll()
    }

    func testManifestPagination() throws {
        let files = (0..<1500).map { ManifestFile(id: $0, name: "f\($0)-" + String(repeating: "x", count: 200), size: Int64($0), mtime: 1) }
        let pages = try FileSender.paginate(files)
        XCTAssertGreaterThan(pages.count, 3)
        XCTAssertEqual(pages.flatMap { $0 }, files)
        for (i, p) in pages.enumerated() {
            let frame = try FrameCodec.encode(ManifestBody(files: p, more: i < pages.count - 1 ? true : nil))
            XCTAssertLessThanOrEqual(frame.count, FrameCodec.maxPlaintext)
        }
    }

    func testNameTooLongCountMismatchAndTotalMismatchAbort() async throws {
        for variant in 0..<3 {
            let id = TransferId.random()
            let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
            let name = variant == 0 ? String(repeating: "a", count: 256) : "ok.txt"
            let r = try await rawSender(s, id: id, files: [ManifestFile(id: 0, name: name, size: 5, mtime: 1)],
                                        count: variant == 1 ? 2 : 1, total: variant == 2 ? 6 : 5) { ch in
                let f = try await ch.receiveFrame()
                XCTAssertEqual(f?.type, .cancel)
            }
            XCTAssertEqual(r.outcome, .aborted, "variant \(variant)")
            await s.closeAll()
        }
    }

    func testDuplicateIdAndNegativeSizeAbort() async throws {
        for files in [
            [ManifestFile(id: 1, name: "a", size: 0, mtime: 1), ManifestFile(id: 1, name: "b", size: 0, mtime: 1)],
            [ManifestFile(id: 1, name: "a", size: -5, mtime: 1)],
        ] {
            let id = TransferId.random()
            let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
            let r = try await rawSender(s, id: id, files: files, total: files.reduce(0) { $0 + $1.size }) { ch in
                _ = try await ch.receiveFrame()
            }
            XCTAssertEqual(r.outcome, .aborted)
            await s.closeAll()
        }
    }

    final class LowSpaceStorage: ReceiverStorage {
        let inner: ReceiverStorage
        let free: Int64
        init(_ inner: ReceiverStorage, free: Int64) { self.inner = inner; self.free = free }
        func freeSpaceBytes() throws -> Int64 { free }
        func completedFile(for key: PartialKey) throws -> CompletedFile? { try inner.completedFile(for: key) }
        func openPartial(for key: PartialKey, size: Int64, mtime: Int64) throws -> PartialFile {
            try inner.openPartial(for: key, size: size, mtime: mtime)
        }
    }

    func testInsufficientDiskSpaceAbortsBeforeReceiving() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let items = [memItem(0, "x.bin", Data(count: 1000))]
        let low = LowSpaceStorage(try DirectoryReceiverStorage(directory: inbox), free: 999)
        let (sr, rr) = try await runTransfer(s, items: items, opts: options(id, items: items), storage: low)
        XCTAssertEqual(rr.outcome, .aborted)
        XCTAssertTrue(rr.error?.contains("insufficient disk space") ?? false)
        XCTAssertEqual(sr.outcome, .canceled)
        XCTAssertTrue(sr.canceledByRemote)
        await s.closeAll()
    }

    // MARK: Tehlikeli adlar ve çakışma

    func testDangerousNamesStayInsideInboxAndCollisionsAreNumbered() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let names = ["../../etc/passwd", "..\\..\\evil.dll", "CON", "nul.txt", "a:b?.txt", "..", "evil\u{202E}gpj.exe",
                     "dup.txt", "dup.txt", "dup.txt", "trailing. . ", "C:\\Windows\\x.bat", "x\u{0}y"]
        let items = names.enumerated().map { memItem($0.offset, $0.element, Data("içerik \($0.offset)".utf8)) }
        let (sr, rr) = try await runTransfer(s, items: items, opts: options(id, items: items))
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(rr.outcome, .completed)
        // Hedef klasörün dışında hiçbir şey oluşmadı.
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: base.path).sorted(), ["inbox"])
        let visible = Set(try FileManager.default.contentsOfDirectory(atPath: inbox.path).filter { !$0.hasPrefix(".") })
        XCTAssertEqual(visible, ["passwd", "evil.dll", "_CON", "_nul.txt", "a_b_.txt", "file", "evil_gpj.exe",
                                 "dup.txt", "dup (2).txt", "dup (3).txt", "trailing", "x.bat", "x_y"])
        for f in rr.receivedFiles {
            XCTAssertEqual(URL(fileURLWithPath: f.storedPath).deletingLastPathComponent().standardized.path, inbox.standardized.path)
            XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: f.storedPath)), Data("içerik \(f.id)".utf8))
        }
        await s.closeAll()
    }

    func testManyFilesWithLongNamesUsePagedManifest() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let items = (0..<1500).map { memItem($0, "f\($0)-" + String(repeating: "x", count: 200), Data("\($0)".utf8)) }
        let (sr, rr) = try await runTransfer(s, items: items, opts: options(id, items: items))
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(rr.receivedFiles.count, 1500)
        await s.closeAll()
    }
}
