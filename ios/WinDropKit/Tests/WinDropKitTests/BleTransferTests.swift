import XCTest
import Foundation
@testable import WinDropKit

/// PROTOCOL §7.3: küçük dosya BLE oturumu üstünden (bellek içi GATT bağlantısı, MTU 185).
/// iPhone tarafı SignalHub gibi davranır: tek okuma döngüsü sinyalleri ayırır, çerçeveleri `FrameReader`'a besler.
final class BleTransferTests: XCTestCase {
    let phoneKey = NoisePrivateKey()
    let pcKey = NoisePrivateKey()
    var inbox: URL!

    override func setUpWithError() throws {
        inbox = FileManager.default.temporaryDirectory.appendingPathComponent("windrop-ble-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: inbox, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: inbox)
    }

    /// Okuma döngüsünün çıktıları: çerçeve kuyruğu + alınan sinyaller.
    actor Pump {
        private var frames: [Frame] = []
        private var finished = false
        private var waiter: CheckedContinuation<Frame?, Never>?
        private(set) var signals: [SignalMessage] = []

        func push(_ f: Frame) {
            if let w = waiter { waiter = nil; w.resume(returning: f) } else { frames.append(f) }
        }
        func signal(_ s: SignalMessage) { signals.append(s) }
        func finish() {
            finished = true
            if let w = waiter { waiter = nil; w.resume(returning: nil) }
        }
        func next() async -> Frame? {
            if !frames.isEmpty { return frames.removeFirst() }
            if finished { return nil }
            return await withCheckedContinuation { waiter = $0 }
        }
    }

    func startPump(_ channel: SecureChannel) -> (Pump, Task<Void, Never>) {
        let pump = Pump()
        let task = Task {
            do {
                while let m = try await channel.receiveMessage() {
                    switch m {
                    case .frame(let f): await pump.push(f)
                    case .signal(let s): await pump.signal(s)
                    }
                }
            } catch {}
            await pump.finish()
        }
        return (pump, task)
    }

    func makeBleSession() async throws -> (phone: SecureChannel, pc: SecureChannel, links: (MemoryBleLink, MemoryBleLink)) {
        let links = MemoryBleLink.makePair(mtu: 185)
        let pcKey = self.pcKey, phoneKey = self.phoneKey
        let pcT = BleMessageTransport(link: links.1)
        async let pcTask = SecureChannel.acceptIK(transport: pcT, localKey: pcKey, isTrusted: { _ in true })
        let phone = try await SecureChannel.connectIK(
            transport: BleMessageTransport(link: links.0), prefix: Prefix.ble(), localKey: phoneKey,
            remoteStaticKey: pcKey.publicKey.rawRepresentation)
        return (phone, try await pcTask, links)
    }

    func item(_ id: Int, _ name: String, _ data: Data) -> SendItem {
        SendItem(id: id, name: name, size: Int64(data.count), mtime: 1_700_000_000_000, source: MemoryFileSource(data))
    }

    func receiverOptions(_ id: TransferId, items: [SendItem]) -> ReceiverOptions {
        var o = ReceiverOptions(peerId: "phone", transferId: id, expectedCount: items.count,
                                expectedTotal: items.reduce(0) { $0 + $1.size })
        o.spaceMarginBytes = 0
        return o
    }

    func testChunkSizeFitsBleFrames() async throws {
        let s = try await makeBleSession()
        XCTAssertEqual(s.phone.maxFrameBodySize, 8192 - 16 - 1)
        XCTAssertLessThan(TransferLimits.bleChunkSize, s.phone.maxFrameBodySize)
    }

    /// 64 KB (üst sınır) iPhone -> PC, BLE taşıması üstünden; SHA-256 doğrulanır.
    func test64KBPhoneToPcOverBle() async throws {
        let s = try await makeBleSession()
        let payload = patternData(count: Int(TransferLimits.bleMaxTotalBytes), seed: 3)
        let items = [item(0, "kucuk.bin", payload)]
        let id = TransferId.random()
        let sender = FileSender(channel: s.phone, items: items, chunkSize: TransferLimits.bleChunkSize)
        let receiver = FileReceiver(channel: s.pc, storage: try DirectoryReceiverStorage(directory: inbox),
                                    options: receiverOptions(id, items: items))
        async let rr = receiver.run()
        let sr = try await sender.run()
        let r = try await rr
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(r.outcome, .completed)
        let f = try XCTUnwrap(r.receivedFiles.first)
        let stored = try Data(contentsOf: URL(fileURLWithPath: f.storedPath))
        XCTAssertEqual(stored.count, payload.count)
        XCTAssertEqual(sha256Of(stored), sha256Of(payload))
        // Her GATT yazması MTU - 3 sınırında (MemoryBleLink precondition) ve 1 mesaj = çok parça.
        XCTAssertGreaterThan(s.links.0.written.count, 64)
    }

    /// 64 KB PC -> iPhone: iPhone alıcı, okuma döngüsü `FrameReader` besler; aynı anda sinyaller akar.
    func test64KBPcToPhoneWithInterleavedSignalsAndPacing() async throws {
        let s = try await makeBleSession()
        let payload = patternData(count: Int(TransferLimits.bleMaxTotalBytes), seed: 7)
        let items = [item(0, "pc-den.bin", payload)]
        let id = TransferId.random()
        let (pump, pumpTask) = startPump(s.phone)
        let (pcPump, pcPumpTask) = startPump(s.pc)
        // Yanıtlayan (PC) başlatanın boş onay mesajını okuma döngüsüyle yutar; kurulmuş sayılana dek bekle.
        for _ in 0..<200 where !s.pc.isEstablished { try await Task.sleep(nanoseconds: 10_000_000) }
        XCTAssertTrue(s.pc.isEstablished)

        // PC: alıcı değil, gönderen (hızlı pacing: ~0,15 sn). Aynı anda iPhone'a sinyaller yollar.
        let rate = 450_000
        let pcSender = FileSender(channel: s.pc, items: items, chunkSize: TransferLimits.bleChunkSize,
                                  bytesPerSecond: rate, frameReader: { await pcPump.next() })
        let phoneReceiver = FileReceiver(
            channel: s.phone, storage: try DirectoryReceiverStorage(directory: inbox),
            options: receiverOptions(id, items: items), frameReader: { await pump.next() })
        let pc = s.pc
        let signalTask = Task {
            for _ in 0..<10 {
                try await pc.sendSignal(.status(StatusMessage(wifi: "none", btAudio: false)))
                try await Task.sleep(nanoseconds: 10_000_000)
            }
        }
        async let rr = phoneReceiver.run()
        let started = Date()
        let sr = try await pcSender.run()
        let elapsed = Date().timeIntervalSince(started)
        let r = try await rr
        try await signalTask.value
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(r.outcome, .completed)
        let f = try XCTUnwrap(r.receivedFiles.first)
        XCTAssertEqual(sha256Of(try Data(contentsOf: URL(fileURLWithPath: f.storedPath))), sha256Of(payload))
        // Pacing: 64 KB / 450 KB/s ~ 0,146 sn; (son parçanın beklemesi dahil) en az ~0,1 sn sürmeli.
        XCTAssertGreaterThan(elapsed, 0.1)
        // Sinyaller çerçevelerle karışmadan ayrıldı.
        try await Task.sleep(nanoseconds: 50_000_000)
        let got = await pump.signals
        XCTAssertEqual(got.count, 10)
        XCTAssertTrue(got.allSatisfy { $0 == .status(StatusMessage(wifi: "none", btAudio: false)) })
        pumpTask.cancel()
        pcPumpTask.cancel()
        await s.links.0.close()
        await s.links.1.close()
    }

    func testPacingSleepsToTargetRate() async throws {
        let s = try await makeBleSession()
        let payload = patternData(count: 8000, seed: 2)
        let items = [item(0, "p.bin", payload)]
        let id = TransferId.random()
        // 8000 B @ 40 KB/s = 0,2 sn
        let sender = FileSender(channel: s.phone, items: items, chunkSize: TransferLimits.bleChunkSize, bytesPerSecond: 40_000)
        let receiver = FileReceiver(channel: s.pc, storage: try DirectoryReceiverStorage(directory: inbox),
                                    options: receiverOptions(id, items: items))
        async let rr = receiver.run()
        let started = Date()
        let sr = try await sender.run()
        let elapsed = Date().timeIntervalSince(started)
        _ = try await rr
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertGreaterThanOrEqual(elapsed, 0.19)
        XCTAssertLessThan(elapsed, 2.0)
    }

    /// Alıcı RESUME göndermezse gönderen zaman aşımıyla `TransferError.timeout` fırlatır.
    func testResumeTimeoutFailsTransfer() async throws {
        let s = try await makeBleSession()
        let items = [item(0, "t.bin", patternData(count: 100, seed: 4))]
        let sender = FileSender(channel: s.phone, items: items, chunkSize: TransferLimits.bleChunkSize,
                                resumeTimeout: 0.3)
        let started = Date()
        do {
            _ = try await sender.run()
            XCTFail("zaman aşımı beklenirdi")
        } catch {
            XCTAssertEqual(error as? TransferError, .timeout)
        }
        XCTAssertLessThan(Date().timeIntervalSince(started), 3)
    }

    func testBleDataSignalJson() throws {
        let id = TransferId.random().description
        let data = try SignalMessage.bleData(BleDataMessage(transferId: id)).encode()
        XCTAssertEqual(String(decoding: data, as: UTF8.self), #"{"t":"bleData","transferId":""# + id + #""}"#)
        XCTAssertEqual(try SignalMessage.decode(data), .bleData(BleDataMessage(transferId: id)))
        XCTAssertThrowsError(try SignalMessage.decode(Data(#"{"t":"bleData"}"#.utf8)))
    }
}
