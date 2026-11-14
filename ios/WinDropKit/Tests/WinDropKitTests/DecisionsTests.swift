import XCTest
import Foundation
@testable import WinDropKit

/// docs/review/DECISIONS.md: 1 (sinyal toleransı) ve 7 (BLE'de MANIFEST sayfalaması).
final class DecisionsTests: XCTestCase {
    let phoneKey = NoisePrivateKey()
    let pcKey = NoisePrivateKey()
    var inbox: URL!

    override func setUpWithError() throws {
        inbox = FileManager.default.temporaryDirectory.appendingPathComponent("windrop-dec-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: inbox, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: inbox)
    }

    /// PC'nin `status` biçimi: `wifi` yok (PC göndermez) -> çözülür; `wifi` ile de çözülür; yazarken eksikse alan yazılmaz.
    func testStatusWithoutWifiDecodes() throws {
        let pcShape = Data(#"{"btAudio":true,"t":"status"}"#.utf8)
        XCTAssertEqual(try SignalMessage.decode(pcShape), .status(StatusMessage(btAudio: true)))
        let withWifi = Data(#"{"btAudio":false,"t":"status","wifi":"none"}"#.utf8)
        XCTAssertEqual(try SignalMessage.decode(withWifi), .status(StatusMessage(wifi: "none", btAudio: false)))
        let encoded = try SignalMessage.status(StatusMessage(btAudio: true)).encode()
        XCTAssertFalse(String(decoding: encoded, as: UTF8.self).contains("wifi"))
    }

    func makeBleSession() async throws -> (phone: SecureChannel, pc: SecureChannel) {
        let links = MemoryBleLink.makePair(mtu: 185)
        let pcKey = self.pcKey, phoneKey = self.phoneKey
        let pcT = BleMessageTransport(link: links.1)
        async let pcTask = SecureChannel.acceptIK(transport: pcT, localKey: pcKey, isTrusted: { _ in true })
        let phone = try await SecureChannel.connectIK(
            transport: BleMessageTransport(link: links.0), prefix: Prefix.ble(), localKey: phoneKey,
            remoteStaticKey: pcKey.publicKey.rawRepresentation)
        return (phone, try await pcTask)
    }

    /// Bilinmeyen `t`, bozuk gövde ve çözülemeyen alan oturumu düşürmez: o mesaj atlanır, sıradaki sinyal gelir.
    func testUnknownAndMalformedSignalsDoNotDropSession() async throws {
        let s = try await makeBleSession()
        try await s.pc.ensureEstablished()   // PC (yanıtlayan) iPhone'un boş onay mesajını okuyunca kurulmuş sayılır
        let before = IgnoredSignals.shared.count
        let bad: [String] = [
            #"{"t":"future-thing","x":1}"#,          // bilinmeyen t
            #"{"t":"offer","count":"yok"}"#,         // alan tipleri bozuk
            #"{"nokta":1}"#,                         // `t` yok
        ]
        for text in bad {
            // Ham düz metin: `sendSignal` yalnız geçerli mesaj yazar; `sendEncodedFrame` herhangi bir düz metni şifreler.
            try await s.pc.sendEncodedFrame(Data(text.utf8))
        }
        try await s.pc.sendSignal(.status(StatusMessage(btAudio: true)))
        let got = try await s.phone.receiveMessage()
        XCTAssertEqual(got, .signal(.status(StatusMessage(btAudio: true))))
        XCTAssertEqual(IgnoredSignals.shared.count - before, bad.count)
    }

    /// BLE'de MANIFEST sayfaları <= 8175 B gövde (Noise mesajı <= 8176 düz metin): 120 uzun adlı dosyalık liste sayfalanır ve aktarılır.
    func testManifestOverBleIsPagedToChannelLimit() async throws {
        let s = try await makeBleSession()
        let files = (0..<120).map { ManifestFile(id: $0, name: "dosya-\($0)-" + String(repeating: "ş", count: 150), size: 0, mtime: 1) }
        let budget = min(FrameCodec.maxBody, s.phone.maxFrameBodySize) - FileSender.manifestEnvelope
        let pages = try FileSender.paginate(files, budget: budget)
        XCTAssertGreaterThan(pages.count, 2)
        for (i, p) in pages.enumerated() {
            let frame = try FrameCodec.encode(ManifestBody(files: p, more: i < pages.count - 1 ? true : nil))
            XCTAssertLessThanOrEqual(frame.count, s.phone.maxPlaintextSize)
        }
        // Uçtan uca: 120 boş dosya BLE kanalında (eski sabit bütçe 8175'i aşıp messageTooLarge verirdi).
        let items = files.map { SendItem(id: $0.id, name: $0.name, size: 0, mtime: $0.mtime, source: MemoryFileSource(Data())) }
        let id = TransferId.random()
        var o = ReceiverOptions(peerId: "phone", transferId: id, expectedCount: items.count, expectedTotal: 0)
        o.spaceMarginBytes = 0
        let sender = FileSender(channel: s.phone, items: items, chunkSize: TransferLimits.bleChunkSize)
        let receiver = FileReceiver(channel: s.pc, storage: try DirectoryReceiverStorage(directory: inbox), options: o)
        async let rr = receiver.run()
        let sr = try await sender.run()
        let r = try await rr
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(r.outcome, .completed)
        XCTAssertEqual(r.receivedFiles.count, items.count)
    }
}
