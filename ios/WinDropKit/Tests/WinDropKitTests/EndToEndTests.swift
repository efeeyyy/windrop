import XCTest
import Foundation
@testable import WinDropKit

/// Bellek içi uçtan uca: Prefix (prologue) + IK / XXpsk3 + TCP ve BLE taşıma + SecureChannel (PROTOCOL §1, §3, §5).
final class EndToEndTests: XCTestCase {
    let phoneKey = NoisePrivateKey()   // iPhone (başlatan)
    let pcKey = NoisePrivateKey()      // PC (yanıtlayan)

    func tcpPair() -> (PipeEnd, PipeEnd, TcpMessageTransport, TcpMessageTransport) {
        let (a, b) = PipeEnd.makePair()
        return (a, b, TcpMessageTransport(stream: a), TcpMessageTransport(stream: b))
    }

    func testIKOverTcpEstablishmentRules() async throws {
        let (_, _, phoneT, pcT) = tcpPair()
        let id = TransferId.random()
        let prefix = try Prefix(transport: .tcp, transferId: id.bytes)
        let pcKey = self.pcKey, phoneKey = self.phoneKey
        async let pcTask = SecureChannel.acceptIK(transport: pcT, localKey: pcKey, isTrusted: { $0 == phoneKey.publicKey.rawRepresentation })
        let phone = try await SecureChannel.connectIK(transport: phoneT, prefix: prefix, localKey: phoneKey,
                                                      remoteStaticKey: pcKey.publicKey.rawRepresentation)
        let pc = try await pcTask

        XCTAssertTrue(phone.isInitiator && !pc.isInitiator)
        XCTAssertEqual(phone.handshakeHash, pc.handshakeHash)
        XCTAssertEqual(phone.remoteStaticKey, pcKey.publicKey.rawRepresentation)
        XCTAssertEqual(pc.remoteStaticKey, phoneKey.publicKey.rawRepresentation)
        XCTAssertEqual(pc.prefix, prefix)
        XCTAssertEqual(pc.remoteDeviceId, DeviceId.from(publicKey: phoneKey.publicKey.rawRepresentation))
        XCTAssertEqual(pc.remoteDeviceId.count, 16)

        // Yanıtlayan, başlatanın ilk taşıma (boş onay) mesajına dek kurulmuş sayılmaz ve gönderemez.
        XCTAssertTrue(phone.isEstablished)
        XCTAssertFalse(pc.isEstablished)
        do { try await pc.sendFrame(.ack, body: Data("{}".utf8)); XCTFail("kurulmadan gönderim kabul edildi") } catch {
            XCTAssertEqual(error as? SessionError, .notEstablished)
        }
        try await pc.ensureEstablished()
        XCTAssertTrue(pc.isEstablished)

        // Çerçeveler iki yönde
        let manifest = ManifestBody(files: [ManifestFile(id: 0, name: "a.txt", size: 3, mtime: 1_700_000_000_000)])
        try await phone.sendEncodedFrame(try FrameCodec.encode(manifest))
        let f = try await pc.receiveFrame()
        XCTAssertEqual(f?.type, .manifest)
        XCTAssertEqual(try FrameCodec.decodeManifest(f!.body), manifest)
        try await pc.sendEncodedFrame(try FrameCodec.encode(ResumeBody(id: 0, offset: 0)))
        let r = try await phone.receiveFrame()
        XCTAssertEqual(try FrameCodec.decodeResume(r!.body), ResumeBody(id: 0, offset: 0))
    }

    func testFirstRealMessageIsKeptWhenEnsureEstablishedReadsIt() async throws {
        // Başlatan boş onay yerine hemen gerçek veri yollasa bile (bkz. SecureChannel.ensureEstablished) kaybolmaz.
        let (_, _, a, b) = tcpPair()
        let prefix = try Prefix(transport: .tcp, transferId: TransferId.random().bytes)
        let pcKey = self.pcKey, phoneKey = self.phoneKey
        async let pcTask = SecureChannel.acceptIK(transport: b, localKey: pcKey, isTrusted: { _ in true })
        let phone = try await SecureChannel.connectIK(transport: a, prefix: prefix, localKey: phoneKey,
                                                      remoteStaticKey: pcKey.publicKey.rawRepresentation)
        let pc = try await pcTask
        try await phone.sendFrame(.cancel, body: Data("{}".utf8))
        // Boş onay yutulur; ensureEstablished onayı tüketir, ardından gerçek mesaj okunur.
        try await pc.ensureEstablished()
        let f = try await pc.receiveFrame()
        XCTAssertEqual(f?.type, .cancel)
    }

    func testUntrustedPeerIsRejectedWithoutReply() async throws {
        let (phoneEnd, _, phoneT, pcT) = tcpPair()
        let prefix = try Prefix(transport: .tcp, transferId: TransferId.random().bytes)
        let pcKey = self.pcKey, phoneKey = self.phoneKey
        let pcTask = Task { () -> Error? in
            do { _ = try await SecureChannel.acceptIK(transport: pcT, localKey: pcKey, isTrusted: { _ in false }); return nil }
            catch { return error }
        }
        let phoneTask = Task { () -> Error? in
            do { _ = try await SecureChannel.connectIK(transport: phoneT, prefix: prefix, localKey: phoneKey,
                                                       remoteStaticKey: pcKey.publicKey.rawRepresentation); return nil }
            catch { return error }
        }
        let pcError = await pcTask.value
        XCTAssertEqual(pcError as? SessionError, .peerNotTrusted(publicKey: phoneKey.publicKey.rawRepresentation))
        await phoneEnd.closeBoth()   // PC yanıt vermedi; başlatan bağlantı kapanışını görür
        let phoneError = await phoneTask.value
        XCTAssertNotNil(phoneError)
    }

    func testVersionMismatchOverTcp() async throws {
        let (_, _, phoneT, pcT) = tcpPair()
        let newer = try Prefix(version: 2, transport: .tcp, transferId: TransferId.random().bytes)
        let pcKey = self.pcKey, phoneKey = self.phoneKey
        let pcTask = Task { () -> Error? in
            do { _ = try await SecureChannel.acceptIK(transport: pcT, localKey: pcKey, isTrusted: { _ in true }); return nil }
            catch { return error }
        }
        do {
            _ = try await SecureChannel.connectIK(transport: phoneT, prefix: newer, localKey: phoneKey,
                                                  remoteStaticKey: pcKey.publicKey.rawRepresentation)
            XCTFail("sürüm uyuşmazlığı kabul edildi")
        } catch {
            XCTAssertEqual(error as? SessionError, .versionMismatch(supportedVersion: 1))
        }
        let pcError = await pcTask.value
        XCTAssertEqual(pcError as? SessionError, .versionMismatch(supportedVersion: 1))
    }

    func testVersionMismatchOverBle() async throws {
        let (phoneLink, pcLink) = MemoryBleLink.makePair(mtu: 23)
        let phoneT = BleMessageTransport(link: phoneLink), pcT = BleMessageTransport(link: pcLink)
        let newer = Prefix.ble(version: 3)
        let pcKey = self.pcKey, phoneKey = self.phoneKey
        let pcTask = Task { () -> Error? in
            do { _ = try await SecureChannel.acceptIK(transport: pcT, localKey: pcKey, isTrusted: { _ in true }); return nil }
            catch { return error }
        }
        do {
            _ = try await SecureChannel.connectIK(transport: phoneT, prefix: newer, localKey: phoneKey,
                                                  remoteStaticKey: pcKey.publicKey.rawRepresentation)
            XCTFail()
        } catch {
            XCTAssertEqual(error as? SessionError, .versionMismatch(supportedVersion: 1))
        }
        _ = await pcTask.value
    }

    func testTransportKindMismatch() async throws {
        let (_, _, phoneT, _) = tcpPair()
        do {
            _ = try await SecureChannel.connectIK(transport: phoneT, prefix: Prefix.ble(), localKey: phoneKey,
                                                  remoteStaticKey: pcKey.publicKey.rawRepresentation)
            XCTFail()
        } catch {
            XCTAssertEqual(error as? SessionError, .transportMismatch)
        }
    }

    func testPairingXXpsk3OverBleWithSignalsAndFrames() async throws {
        let (phoneLink, pcLink) = MemoryBleLink.makePair(mtu: 23)
        let psk = Data((0..<32).map { UInt8($0) })
        let pcKey = self.pcKey, phoneKey = self.phoneKey
        async let pcTask = SecureChannel.acceptPairing(transport: BleMessageTransport(link: pcLink), localKey: pcKey, psk: psk)
        let phone = try await SecureChannel.connectPairing(
            transport: BleMessageTransport(link: phoneLink), prefix: Prefix.ble(), localKey: phoneKey, psk: psk,
            expectedRemoteKey: pcKey.publicKey.rawRepresentation)
        let pc = try await pcTask
        XCTAssertTrue(pc.isEstablished)   // XXpsk3'te yanıtlayan hemen kurulmuş
        XCTAssertEqual(pc.remoteStaticKey, phoneKey.publicKey.rawRepresentation)
        XCTAssertEqual(pc.maxFrameBodySize, 8192 - 16 - 1)

        // Ön ek ve her GATT yükü ATT MTU sınırında; ön ek tek yazma.
        XCTAssertEqual(phoneLink.written.first, Prefix.ble().bytes)
        XCTAssertTrue(phoneLink.written.allSatisfy { $0.count <= 20 })

        let hello = SignalMessage.hello(HelloMessage(v: 1, name: "iPhone", caps: ["tcp"], wifi: "none", btAudio: false))
        try await phone.sendSignal(hello)
        let got = try await pc.receiveMessage()
        XCTAssertEqual(got, .signal(hello))

        // Aynı oturumda çerçeve (ilk bayt 1–8) ve büyük mesaj (çok parçalı)
        let data = Data(repeating: 0x42, count: pc.maxFrameBodySize)
        try await pc.sendFrame(.data, body: data)
        let f = try await phone.receiveMessage()
        XCTAssertEqual(f, .frame(Frame(type: .data, body: data)))
        do { try await pc.sendFrame(.data, body: Data(count: pc.maxFrameBodySize + 1)); XCTFail() } catch {
            XCTAssertEqual(error as? ProtocolError, .messageTooLarge(pc.maxPlaintextSize + 1))
        }
    }

    func testPairingRejectsWrongPcKeyBeforeThirdMessage() async throws {
        let (phoneEnd, pcEnd, phoneT, pcT) = tcpPair()
        let psk = Data(repeating: 9, count: 32)
        let pcKey = self.pcKey, phoneKey = self.phoneKey
        let pcTask = Task { () -> Error? in
            do { _ = try await SecureChannel.acceptPairing(transport: pcT, localKey: pcKey, psk: psk); return nil }
            catch { return error }
        }
        do {
            _ = try await SecureChannel.connectPairing(
                transport: phoneT, prefix: try Prefix(transport: .tcp, transferId: TransferId.random().bytes),
                localKey: phoneKey, psk: psk, expectedRemoteKey: NoisePrivateKey().publicKey.rawRepresentation)
            XCTFail("yanlış PC anahtarı kabul edildi")
        } catch {
            XCTAssertEqual(error as? SessionError, .peerKeyMismatch)
        }
        await phoneEnd.close()   // 3. mesaj hiç gönderilmedi; PC bağlantı kapanışını görür
        await pcEnd.closeBoth()
        let pcError = await pcTask.value
        XCTAssertNotNil(pcError)
        let unsent = await phoneEnd.outgoing.pendingBytes
        XCTAssertEqual(unsent, 0, "3. mesaj gönderilmemeli")
    }

    func testWrongPskFailsPairing() async throws {
        let (phoneEnd, _, phoneT, pcT) = tcpPair()
        let pcKey = self.pcKey, phoneKey = self.phoneKey
        let pcTask = Task { () -> Error? in
            do { _ = try await SecureChannel.acceptPairing(transport: pcT, localKey: pcKey, psk: Data(repeating: 1, count: 32)); return nil }
            catch { return error }
        }
        let phoneTask = Task { () -> Error? in
            do {
                _ = try await SecureChannel.connectPairing(
                    transport: phoneT, prefix: try Prefix(transport: .tcp, transferId: TransferId.random().bytes),
                    localKey: phoneKey, psk: Data(repeating: 2, count: 32), expectedRemoteKey: pcKey.publicKey.rawRepresentation)
                return nil
            } catch { return error }
        }
        let pcError = await pcTask.value
        XCTAssertEqual(pcError as? NoiseError, .decryptionFailed)
        await phoneEnd.closeBoth()
        _ = await phoneTask.value
    }

    func testTamperedTransportMessageIsRejected() async throws {
        let (_, pcEnd, phoneT, pcT) = tcpPair()
        let prefix = try Prefix(transport: .tcp, transferId: TransferId.random().bytes)
        let pcKey = self.pcKey, phoneKey = self.phoneKey
        async let pcTask = SecureChannel.acceptIK(transport: pcT, localKey: pcKey, isTrusted: { _ in true })
        let phone = try await SecureChannel.connectIK(transport: phoneT, prefix: prefix, localKey: phoneKey,
                                                      remoteStaticKey: pcKey.publicKey.rawRepresentation)
        let pc = try await pcTask
        try await pc.ensureEstablished()
        try await phone.sendFrame(.cancel, body: Data("{}".utf8))
        let ok = try await pc.receiveFrame()
        XCTAssertEqual(ok?.type, .cancel)   // doğru mesaj sorunsuz gelir
        // Şimdi bozuk bir mesaj: phone'un nonce'u ilerlemiş; ham yazılan rastgele şifreli metin çözülemez.
        await pcEnd.incoming.write(Data([0, 20]) + Data(repeating: 0xAB, count: 20))
        do { _ = try await pc.receiveFrame(); XCTFail("kurcalanmış mesaj kabul edildi") } catch {
            XCTAssertEqual(error as? NoiseError, .decryptionFailed)
        }
    }
}

