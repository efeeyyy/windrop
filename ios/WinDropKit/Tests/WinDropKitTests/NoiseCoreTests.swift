import XCTest
import Foundation
@testable import WinDropKit

final class NoiseCoreTests: XCTestCase {
    func testNonceLayoutIsFourZeroBytesThenLittleEndianCounter() {
        XCTAssertEqual(toHex(NoiseCrypto.nonceBytes(1)), "000000000100000000000000")
        XCTAssertEqual(toHex(NoiseCrypto.nonceBytes(0x0102030405060708)), "000000000807060504030201")
    }

    func testHkdfMatchesManualHmacConstruction() {
        let ck = Data(repeating: 7, count: 32)
        let ikm = Data("ikm".utf8)
        let out = NoiseCrypto.hkdf(chainingKey: ck, inputKeyMaterial: ikm, outputs: 3)
        let temp = NoiseCrypto.hmac(key: ck, data: ikm)
        let o1 = NoiseCrypto.hmac(key: temp, data: Data([1]))
        let o2 = NoiseCrypto.hmac(key: temp, data: o1 + Data([2]))
        let o3 = NoiseCrypto.hmac(key: temp, data: o2 + Data([3]))
        XCTAssertEqual(out, [o1, o2, o3])
    }

    func testCipherStateWithoutKeyIsIdentity() throws {
        var c = CipherState()
        XCTAssertEqual(try c.encryptWithAd(Data(), Data([1, 2, 3])), Data([1, 2, 3]))
        XCTAssertEqual(try c.decryptWithAd(Data(), Data([1, 2, 3])), Data([1, 2, 3]))
        XCTAssertEqual(c.nonce, 0)
    }

    func testCipherStateFailedDecryptDoesNotAdvanceNonce() throws {
        var a = CipherState(key: Data(repeating: 1, count: 32))
        var b = CipherState(key: Data(repeating: 1, count: 32))
        var ct = try a.encryptWithAd(Data(), Data("hello".utf8))
        let good = ct
        ct[0] ^= 1
        XCTAssertThrowsError(try b.decryptWithAd(Data(), ct)) { XCTAssertEqual($0 as? NoiseError, .decryptionFailed) }
        XCTAssertEqual(b.nonce, 0)
        XCTAssertEqual(try b.decryptWithAd(Data(), good), Data("hello".utf8))
        XCTAssertEqual(b.nonce, 1)
    }

    func testNonceExhaustion() throws {
        var c = CipherState(key: Data(repeating: 1, count: 32))
        c.setNonce(UInt64.max)
        XCTAssertThrowsError(try c.encryptWithAd(Data(), Data([1]))) { XCTAssertEqual($0 as? NoiseError, .nonceExhausted) }
        XCTAssertThrowsError(try c.decryptWithAd(Data(), Data(count: 20))) { XCTAssertEqual($0 as? NoiseError, .nonceExhausted) }
    }

    func testRekeyChangesKeyConsistently() throws {
        var a = CipherState(key: Data(repeating: 9, count: 32))
        var b = CipherState(key: Data(repeating: 9, count: 32))
        try a.rekey(); try b.rekey()
        let ct = try a.encryptWithAd(Data(), Data("x".utf8))
        XCTAssertEqual(try b.decryptWithAd(Data(), ct), Data("x".utf8))
        var old = CipherState(key: Data(repeating: 9, count: 32))
        XCTAssertThrowsError(try old.decryptWithAd(Data(), ct))
    }

    func testPatternParsing() throws {
        XCTAssertEqual(try HandshakePattern(name: "XXpsk3").pskCount, 1)
        XCTAssertEqual(try HandshakePattern(name: "XXpsk3").messages[2], [.s, .se, .psk])
        XCTAssertEqual(try HandshakePattern(name: "NNpsk0").messages[0], [.psk, .e])
        XCTAssertEqual(try HandshakePattern(name: "IK").protocolName, "Noise_IK_25519_ChaChaPoly_SHA256")
        XCTAssertTrue(try HandshakePattern(name: "N").isOneWay)
        XCTAssertThrowsError(try HandshakePattern(name: "ZZ")) { XCTAssertEqual($0 as? NoiseError, .unsupportedPattern("ZZ")) }
        XCTAssertThrowsError(try HandshakePattern(name: "NNpsk9"))
        XCTAssertThrowsError(try HandshakePattern(protocolName: "Noise_IK_448_ChaChaPoly_SHA256"))
        XCTAssertThrowsError(try HandshakePattern(protocolName: "Noise_IK_25519_AESGCM_SHA256"))
    }

    /// Rastgele (enjekte edilmemiş) ephemeral ile bütün desenlerin uçtan uca çalıştığı.
    func testEveryPatternRoundTripsWithRandomKeys() throws {
        let names = ["N", "K", "X", "NN", "KN", "NK", "KK", "NX", "KX", "XN", "IN", "XK", "IK", "XX", "IX",
                     "NK1", "NX1", "X1N", "X1K", "XK1", "X1K1", "X1X", "XX1", "X1X1", "K1N", "K1K", "KK1",
                     "K1K1", "K1X", "KX1", "K1X1", "I1N", "I1K", "IK1", "I1K1", "IX1", "I1X", "I1X1",
                     "NNpsk0", "NNpsk2", "XXpsk3", "IKpsk1", "IKpsk2", "KKpsk0", "XXpsk0+psk3"]
        for name in names {
            let pattern = try HandshakePattern(name: name)
            let si = NoisePrivateKey(), sr = NoisePrivateKey()
            let psks = (0..<pattern.pskCount).map { Data(repeating: UInt8($0 + 1), count: 32) }
            let ini = try HandshakeState(
                pattern: pattern, role: .initiator, prologue: Data("p".utf8), staticKey: si,
                remoteStaticKey: pattern.responderPreMessage.isEmpty ? nil : sr.publicKey.rawRepresentation, preSharedKeys: psks)
            let rsp = try HandshakeState(
                pattern: pattern, role: .responder, prologue: Data("p".utf8), staticKey: sr,
                remoteStaticKey: pattern.initiatorPreMessage.isEmpty ? nil : si.publicKey.rawRepresentation, preSharedKeys: psks)
            var i = 0
            while !ini.isComplete {
                let (w, r) = i % 2 == 0 ? (ini, rsp) : (rsp, ini)
                XCTAssertTrue(w.shouldWrite, name)
                let payload = Data("m\(i)".utf8)
                XCTAssertEqual(try r.readMessage(try w.writeMessage(payload: payload)), payload, name)
                i += 1
            }
            XCTAssertTrue(rsp.isComplete, name)
            let ti = try ini.finish(), tr = try rsp.finish()
            XCTAssertEqual(ti.handshakeHash, tr.handshakeHash, name)
            XCTAssertEqual(try tr.decrypt(try ti.encrypt(Data("a".utf8))), Data("a".utf8), name)
            if !pattern.isOneWay {
                XCTAssertEqual(try ti.decrypt(try tr.encrypt(Data("b".utf8))), Data("b".utf8), name)
            }
        }
    }

    func testIKWrongPrologueOrKeyFails() throws {
        let si = NoisePrivateKey(), sr = NoisePrivateKey()
        func make(role: NoiseRole, prologue: String, s: NoisePrivateKey, rs: NoisePrivateKey) throws -> HandshakeState {
            try HandshakeState(pattern: .ik, role: role, prologue: Data(prologue.utf8), staticKey: s,
                               remoteStaticKey: role == .initiator ? rs.publicKey.rawRepresentation : nil)
        }
        let ini = try make(role: .initiator, prologue: "A", s: si, rs: sr)
        let rsp = try make(role: .responder, prologue: "B", s: sr, rs: si)
        let m1 = try ini.writeMessage()
        XCTAssertThrowsError(try rsp.readMessage(m1)) { XCTAssertEqual($0 as? NoiseError, .decryptionFailed) }
        // Hatadan sonra durum kullanılamaz.
        XCTAssertThrowsError(try rsp.readMessage(m1)) { XCTAssertEqual($0 as? NoiseError, .handshakeFailed) }
    }

    func testIKResponderLearnsInitiatorStaticKey() throws {
        let si = NoisePrivateKey(), sr = NoisePrivateKey()
        let ini = try HandshakeState(pattern: .ik, role: .initiator, staticKey: si,
                                     remoteStaticKey: sr.publicKey.rawRepresentation)
        let rsp = try HandshakeState(pattern: .ik, role: .responder, staticKey: sr)
        XCTAssertNil(rsp.remoteStaticKey)
        _ = try rsp.readMessage(try ini.writeMessage())
        XCTAssertEqual(rsp.remoteStaticKey, si.publicKey.rawRepresentation)
    }

    func testXXpsk3WrongPskFails() throws {
        let a = NoisePrivateKey(), b = NoisePrivateKey()
        let ini = try HandshakeState(pattern: .xxpsk3, role: .initiator, staticKey: a,
                                     preSharedKeys: [Data(repeating: 1, count: 32)])
        let rsp = try HandshakeState(pattern: .xxpsk3, role: .responder, staticKey: b,
                                     preSharedKeys: [Data(repeating: 2, count: 32)])
        _ = try rsp.readMessage(try ini.writeMessage())
        _ = try ini.readMessage(try rsp.writeMessage())
        XCTAssertThrowsError(try rsp.readMessage(try ini.writeMessage())) {
            XCTAssertEqual($0 as? NoiseError, .decryptionFailed)
        }
    }

    func testConfigurationErrors() throws {
        XCTAssertThrowsError(try HandshakeState(pattern: .ik, role: .initiator, staticKey: NoisePrivateKey())) {
            XCTAssertEqual($0 as? NoiseError, .missingKey("rs"))
        }
        XCTAssertThrowsError(try HandshakeState(pattern: .xxpsk3, role: .initiator, staticKey: NoisePrivateKey())) {
            XCTAssertEqual($0 as? NoiseError, .wrongPreSharedKeyCount(expected: 1, got: 0))
        }
        XCTAssertThrowsError(try HandshakeState(pattern: .xxpsk3, role: .initiator, staticKey: NoisePrivateKey(),
                                                preSharedKeys: [Data(count: 5)])) {
            XCTAssertEqual($0 as? NoiseError, .invalidPreSharedKeyLength)
        }
        let nn = try HandshakePattern(name: "NN")
        let r = try HandshakeState(pattern: nn, role: .responder)
        XCTAssertThrowsError(try r.writeMessage()) { XCTAssertEqual($0 as? NoiseError, .notYourTurn) }
        XCTAssertThrowsError(try r.finish()) { XCTAssertEqual($0 as? NoiseError, .handshakeNotComplete) }
        XCTAssertThrowsError(try r.readMessage(Data(count: 10))) { XCTAssertEqual($0 as? NoiseError, .messageTooShort) }
    }

    func testTruncatedHandshakeMessageIsRejected() throws {
        let si = NoisePrivateKey(), sr = NoisePrivateKey()
        let ini = try HandshakeState(pattern: .ik, role: .initiator, staticKey: si,
                                     remoteStaticKey: sr.publicKey.rawRepresentation)
        let rsp = try HandshakeState(pattern: .ik, role: .responder, staticKey: sr)
        let m1 = try ini.writeMessage()
        XCTAssertThrowsError(try rsp.readMessage(m1.dropLast(1)))
    }
}
