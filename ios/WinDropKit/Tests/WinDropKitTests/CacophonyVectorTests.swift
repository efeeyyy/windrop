import XCTest
import Foundation
@testable import WinDropKit

/// cacophony test vektörleri (protocol/vectors/cacophony.txt): `25519_ChaChaPoly_SHA256` olan tüm girdiler.
final class CacophonyVectorTests: XCTestCase {
    private struct Summary { var run = 0; var failedNames: [String] = [] }

    private func loadVectors() throws -> [[String: Any]] {
        let url = repoRoot().appendingPathComponent("protocol/vectors/cacophony.txt")
        let data = try Data(contentsOf: url)
        let root = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        return (root?["vectors"] as? [[String: Any]]) ?? []
    }

    private func key(_ v: [String: Any], _ name: String) throws -> NoisePrivateKey? {
        guard let s = v[name] as? String else { return nil }
        return try NoisePrivateKey(rawRepresentation: try hex(s))
    }

    private func bytes(_ v: [String: Any], _ name: String) throws -> Data? {
        guard let s = v[name] as? String else { return nil }
        return try hex(s)
    }

    private func psks(_ v: [String: Any], _ name: String) throws -> [Data] {
        try ((v[name] as? [String]) ?? []).map { try hex($0) }
    }

    /// Bir vektörü çalıştırır; hata mesajlarını döndürür.
    private func run(_ v: [String: Any]) throws -> Int {
        let name = v["protocol_name"] as! String
        let pattern = try HandshakePattern(protocolName: name)

        let initiator = try HandshakeState(
            pattern: pattern, role: .initiator,
            prologue: try bytes(v, "init_prologue") ?? Data(),
            staticKey: try key(v, "init_static"),
            remoteStaticKey: try bytes(v, "init_remote_static"),
            preSharedKeys: try psks(v, "init_psks"),
            ephemeralKey: try key(v, "init_ephemeral"))
        let responder = try HandshakeState(
            pattern: pattern, role: .responder,
            prologue: try bytes(v, "resp_prologue") ?? Data(),
            staticKey: try key(v, "resp_static"),
            remoteStaticKey: try bytes(v, "resp_remote_static"),
            preSharedKeys: try psks(v, "resp_psks"),
            ephemeralKey: try key(v, "resp_ephemeral"))

        let messages = v["messages"] as! [[String: String]]
        var transport: (NoiseTransport, NoiseTransport)?
        for (i, m) in messages.enumerated() {
            let payload = try hex(m["payload"]!)
            let expected = try hex(m["ciphertext"]!)
            if i < pattern.messageCount {
                let (writer, reader) = i % 2 == 0 ? (initiator, responder) : (responder, initiator)
                let ct = try writer.writeMessage(payload: payload)
                XCTAssertEqual(toHex(ct), toHex(expected), "\(name) el sıkışma mesajı \(i)")
                let pt = try reader.readMessage(ct)
                XCTAssertEqual(toHex(pt), toHex(payload), "\(name) el sıkışma yükü \(i)")
                if i == pattern.messageCount - 1 {
                    XCTAssertEqual(toHex(initiator.handshakeHash), toHex(responder.handshakeHash), name)
                    if let h = try bytes(v, "handshake_hash") {
                        XCTAssertEqual(toHex(initiator.handshakeHash), toHex(h), "\(name) handshake_hash")
                    }
                    transport = (try initiator.finish(), try responder.finish())
                }
            } else {
                guard let (ti, tr) = transport else { XCTFail("\(name): el sıkışma bitmedi"); return 0 }
                let fromInitiator = pattern.isOneWay || i % 2 == 0
                let (sender, receiver) = fromInitiator ? (ti, tr) : (tr, ti)
                let ct = try sender.encrypt(payload)
                XCTAssertEqual(toHex(ct), toHex(expected), "\(name) taşıma mesajı \(i)")
                XCTAssertEqual(toHex(try receiver.decrypt(ct)), toHex(payload), "\(name) taşıma yükü \(i)")
            }
        }
        return messages.count
    }

    private func runAll(where filter: (String) -> Bool) throws -> Int {
        var count = 0
        for v in try loadVectors() {
            let name = v["protocol_name"] as! String
            guard name.hasSuffix("_25519_ChaChaPoly_SHA256"), filter(name) else { continue }
            do {
                _ = try run(v)
                count += 1
            } catch {
                XCTFail("\(name): \(error)")
            }
        }
        return count
    }

    func testAllChaChaPolySHA256Vectors() throws {
        let total = try loadVectors().filter { ($0["protocol_name"] as! String).hasSuffix("_25519_ChaChaPoly_SHA256") }.count
        XCTAssertGreaterThan(total, 50)
        let ran = try runAll { _ in true }
        XCTAssertEqual(ran, total, "tüm 25519_ChaChaPoly_SHA256 vektörleri çalışmalı")
        print("CACOPHONY: \(ran)/\(total) vektör (25519_ChaChaPoly_SHA256)")
    }

    func testIKVectors() throws {
        let ran = try runAll { $0 == "Noise_IK_25519_ChaChaPoly_SHA256" }
        XCTAssertGreaterThanOrEqual(ran, 1)
        print("CACOPHONY IK: \(ran) vektör")
    }

    func testXXpsk3Vectors() throws {
        let ran = try runAll { $0 == "Noise_XXpsk3_25519_ChaChaPoly_SHA256" }
        XCTAssertGreaterThanOrEqual(ran, 1)
        print("CACOPHONY XXpsk3: \(ran) vektör")
    }
}
