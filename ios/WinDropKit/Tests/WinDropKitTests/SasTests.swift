import XCTest
import Foundation
@testable import WinDropKit

/// PROTOCOL §3.1: yakındaki herkes (XX) + 4 haneli doğrulama kodu. Ortak vektör: protocol/vectors/sas.json.
final class SasTests: XCTestCase {
    func testSasVectorFromSharedFile() throws {
        let url = repoRoot().appendingPathComponent("protocol/vectors/sas.json")
        let v = try XCTUnwrap(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        func s(_ k: String) -> String { v[k] as! String }
        let prologue = try hex(s("prologue"))
        let pattern = try HandshakePattern(protocolName: s("protocol_name"))

        let ini = try HandshakeState(
            pattern: pattern, role: .initiator, prologue: prologue,
            staticKey: try NoisePrivateKey(rawRepresentation: try hex(s("init_static"))), remoteStaticKey: nil,
            preSharedKeys: [], ephemeralKey: try NoisePrivateKey(rawRepresentation: try hex(s("init_ephemeral"))))
        let rsp = try HandshakeState(
            pattern: pattern, role: .responder, prologue: prologue,
            staticKey: try NoisePrivateKey(rawRepresentation: try hex(s("resp_static"))), remoteStaticKey: nil,
            preSharedKeys: [], ephemeralKey: try NoisePrivateKey(rawRepresentation: try hex(s("resp_ephemeral"))))
        for i in 0..<3 {
            let (w, r) = i % 2 == 0 ? (ini, rsp) : (rsp, ini)
            _ = try r.readMessage(try w.writeMessage())
        }
        XCTAssertEqual(toHex(ini.handshakeHash), s("handshake_hash"))
        XCTAssertEqual(toHex(rsp.handshakeHash), s("handshake_hash"))
        XCTAssertEqual(VerificationCode.compute(handshakeHash: ini.handshakeHash), s("sas"))
        XCTAssertEqual(VerificationCode.compute(handshakeHash: rsp.handshakeHash), s("sas"))
    }

    func testCodeFormat() {
        for i in 0..<200 {
            let c = VerificationCode.compute(handshakeHash: Data(repeating: UInt8(i), count: 32))
            XCTAssertEqual(c.count, 4)
            XCTAssertTrue(c.allSatisfy { $0.isASCII && $0.isNumber })
        }
    }

    func testXXChannelBothSidesSameCodeAndLearnKeys() async throws {
        let phoneKey = NoisePrivateKey(), pcKey = NoisePrivateKey()
        func connect() async throws -> (SecureChannel, SecureChannel) {
            let (a, b) = PipeEnd.makePair()
            let prefix = try Prefix(transport: .tcp, transferId: TransferId.random().bytes)
            async let pcTask = SecureChannel.acceptXX(transport: TcpMessageTransport(stream: b), localKey: pcKey)
            let phone = try await SecureChannel.connectXX(transport: TcpMessageTransport(stream: a), prefix: prefix, localKey: phoneKey)
            return (phone, try await pcTask)
        }
        let (phone, pc) = try await connect()
        XCTAssertEqual(phone.remoteStaticKey, pcKey.publicKey.rawRepresentation)   // bilinmeyen PC'nin anahtarı öğrenilir
        XCTAssertEqual(pc.remoteStaticKey, phoneKey.publicKey.rawRepresentation)
        XCTAssertEqual(phone.verificationCode, pc.verificationCode)
        XCTAssertEqual(phone.handshakeHash, pc.handshakeHash)
        XCTAssertTrue(phone.isEstablished && pc.isEstablished)

        try await phone.sendSignal(.status(StatusMessage(wifi: "none", btAudio: false)))
        let got = try await pc.receiveSignal()
        XCTAssertEqual(got, .status(StatusMessage(wifi: "none", btAudio: false)))

        // Farklı el sıkışma -> farklı h; kodlar da (olasılıkla) değişir.
        let (phone2, pc2) = try await connect()
        XCTAssertNotEqual(phone2.handshakeHash, phone.handshakeHash)
        XCTAssertEqual(phone2.verificationCode, pc2.verificationCode)
        var codes = Set([phone.verificationCode, phone2.verificationCode])
        for _ in 0..<8 {
            let (p, _) = try await connect()
            codes.insert(p.verificationCode)
        }
        XCTAssertGreaterThan(codes.count, 1, "farklı el sıkışmalar farklı kod üretmeli")
    }

    func testXXExpectedRemoteStaticMatchSucceeds() async throws {
        let phoneKey = NoisePrivateKey(), pcKey = NoisePrivateKey()
        let (a, b) = PipeEnd.makePair()
        let prefix = try Prefix(transport: .tcp, transferId: TransferId.random().bytes)
        async let pcTask = SecureChannel.acceptXX(transport: TcpMessageTransport(stream: b), localKey: pcKey)
        let phone = try await SecureChannel.connectXX(
            transport: TcpMessageTransport(stream: a), prefix: prefix, localKey: phoneKey,
            expectedRemoteStatic: pcKey.publicKey.rawRepresentation)
        let pc = try await pcTask
        XCTAssertEqual(phone.remoteStaticKey, pcKey.publicKey.rawRepresentation)
        XCTAssertEqual(pc.remoteStaticKey, phoneKey.publicKey.rawRepresentation)
    }

    func testXXExpectedRemoteStaticMismatchFailsBeforeThirdMessage() async throws {
        let phoneKey = NoisePrivateKey(), pcKey = NoisePrivateKey(), otherKey = NoisePrivateKey()
        let (a, b) = PipeEnd.makePair()
        let prefix = try Prefix(transport: .tcp, transferId: TransferId.random().bytes)
        let pcTransport = TcpMessageTransport(stream: b)
        // PC tarafı: 3. mesaj gelmemeli (iPhone göndermeden hata verir; yoksa 3. mesaj okunurdu).
        let pcTask = Task { () -> Bool in
            do { _ = try await SecureChannel.acceptXX(transport: pcTransport, localKey: pcKey); return true } catch { return false }
        }
        do {
            _ = try await SecureChannel.connectXX(
                transport: TcpMessageTransport(stream: a), prefix: prefix, localKey: phoneKey,
                expectedRemoteStatic: otherKey.publicKey.rawRepresentation)
            XCTFail("peerKeyMismatch bekleniyordu")
        } catch SessionError.peerKeyMismatch {
            // beklenen
        }
        await a.close()   // PC'nin 3. mesajı beklemesi biter; el sıkışma tamamlanmamış olmalı
        let completed = await pcTask.value
        XCTAssertFalse(completed, "iPhone 3. mesajı göndermemeli")
    }

    func testMitmGetsDifferentCodeOnEachSide() throws {
        // Araya giren, iki tarafla ayrı XX yapar -> iki taraftaki h (ve kod) farklıdır.
        let a = NoisePrivateKey(), mitm = NoisePrivateKey(), b = NoisePrivateKey()
        func xx(_ i: NoisePrivateKey, _ r: NoisePrivateKey) throws -> Data {
            let hi = try HandshakeState(pattern: .xx, role: .initiator, staticKey: i)
            let hr = try HandshakeState(pattern: .xx, role: .responder, staticKey: r)
            for k in 0..<3 {
                let (w, rd) = k % 2 == 0 ? (hi, hr) : (hr, hi)
                _ = try rd.readMessage(try w.writeMessage())
            }
            return hi.handshakeHash
        }
        XCTAssertNotEqual(VerificationCode.compute(handshakeHash: try xx(a, mitm)),
                          VerificationCode.compute(handshakeHash: try xx(mitm, b)))
    }
}
