import XCTest
import Foundation
@testable import WinDropKit

/// PROTOCOL §6.3 aktarma sunucusu: oda kimliği vektörü (protocol/vectors/relay.json), sahte WebSocket üstünde IK + aktarım.
final class RelayTests: XCTestCase {
    var inbox: URL!

    override func setUpWithError() throws {
        inbox = FileManager.default.temporaryDirectory.appendingPathComponent("windrop-relay-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: inbox, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: inbox)
    }

    // MARK: Sahte WebSocket

    actor MessageQueue {
        private var items: [WebSocketMessage] = []
        private var closed = false
        private var waiter: CheckedContinuation<WebSocketMessage?, Never>?

        func push(_ m: WebSocketMessage) {
            if let w = waiter { waiter = nil; w.resume(returning: m) } else { items.append(m) }
        }
        func close() {
            closed = true
            if let w = waiter { waiter = nil; w.resume(returning: nil) }
        }
        func pop() async -> WebSocketMessage? {
            if !items.isEmpty { return items.removeFirst() }
            if closed { return nil }
            return await withCheckedContinuation { waiter = $0 }
        }
    }

    /// Sunucu gibi davranır: bir uçtan gönderilen mesaj sırayla karşı uca düşer; kapanış karşıya iletilir.
    final class FakeWebSocket: WebSocketLike {
        private let outgoing: MessageQueue
        private let incoming: MessageQueue
        private(set) var sent: [Data] = []
        private(set) var closeCalls = 0
        init(outgoing: MessageQueue, incoming: MessageQueue) { self.outgoing = outgoing; self.incoming = incoming }

        func send(binary: Data) async throws {
            sent.append(binary)
            await outgoing.push(.binary(binary))
        }
        func sendText(_ text: String) async { await outgoing.push(.text(text)) }
        func receive() async throws -> WebSocketMessage? { await incoming.pop() }
        func close() async {
            closeCalls += 1
            await outgoing.close()
        }

        static func makePair() -> (phone: FakeWebSocket, pc: FakeWebSocket) {
            let a = MessageQueue(), b = MessageQueue()
            return (FakeWebSocket(outgoing: a, incoming: b), FakeWebSocket(outgoing: b, incoming: a))
        }
    }

    func item(_ id: Int, _ name: String, _ data: Data) -> SendItem {
        SendItem(id: id, name: name, size: Int64(data.count), mtime: 1_700_000_000_000, source: MemoryFileSource(data))
    }

    // MARK: Oda kimliği

    func testRoomIdVectorFromSharedFile() throws {
        let url = repoRoot().appendingPathComponent("protocol/vectors/relay.json")
        let root = try XCTUnwrap(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        let vectors = try XCTUnwrap(root["vectors"] as? [[String: String]])
        XCTAssertGreaterThanOrEqual(vectors.count, 3)
        for v in vectors {
            let token = try hex(try XCTUnwrap(v["token_hex"]))
            XCTAssertEqual(RelayRoom.roomId(token: token), v["room_id"])
            XCTAssertEqual(RelayRoom.roomId(token: token).count, 32)
            XCTAssertEqual(RelayRoom.encodeToken(token), v["token_b64url"])
            XCTAssertEqual(RelayRoom.decodeToken(try XCTUnwrap(v["token_b64url"])), token)
        }
        XCTAssertEqual(try hex(try XCTUnwrap(vectors[0]["token_hex"])), Data(0..<32))   // 00..1f
    }

    func testTokenDecodeIsStrict() {
        XCTAssertNil(RelayRoom.decodeToken(""))
        XCTAssertNil(RelayRoom.decodeToken(String(repeating: "A", count: 42)))
        XCTAssertNil(RelayRoom.decodeToken(String(repeating: "A", count: 44)))
        XCTAssertNil(RelayRoom.decodeToken(String(repeating: "+", count: 43)))
        XCTAssertNil(RelayRoom.decodeToken(String(repeating: "B", count: 43)))   // kanonik olmayan son karakter
    }

    func testEndpointConstruction() {
        let id = "35b2bac200b0b327c1c7da7b54c4ef51"
        XCTAssertEqual(RelayRoom.endpoint(relayUrl: "wss://relay.example.workers.dev", roomId: id, role: .phone)?.absoluteString,
                       "wss://relay.example.workers.dev/v1/room/\(id)?role=phone")
        XCTAssertEqual(RelayRoom.endpoint(relayUrl: " https://relay.example.dev:8443/x/ ", roomId: id, role: .pc)?.absoluteString,
                       "wss://relay.example.dev:8443/x/v1/room/\(id)?role=pc")
        XCTAssertEqual(RelayRoom.endpoint(relayUrl: "relay.example.dev", roomId: id, role: .phone)?.scheme, "wss")
        XCTAssertEqual(RelayRoom.endpoint(relayUrl: "ws://127.0.0.1:8787", roomId: id, role: .phone)?.absoluteString,
                       "ws://127.0.0.1:8787/v1/room/\(id)?role=phone")
        XCTAssertNil(RelayRoom.endpoint(relayUrl: "ws://relay.example.dev", roomId: id, role: .phone))   // düz ws yalnız yerelde
        XCTAssertNil(RelayRoom.endpoint(relayUrl: "", roomId: id, role: .phone))
        XCTAssertNil(RelayRoom.endpoint(relayUrl: "ftp://relay.example.dev", roomId: id, role: .phone))
        XCTAssertNil(RelayRoom.endpoint(relayUrl: "wss://u:p@relay.example.dev", roomId: id, role: .phone))
        XCTAssertNil(RelayRoom.endpoint(relayUrl: "wss://relay.example.dev", roomId: "ZZ", role: .phone))
    }

    // MARK: Aktarım

    /// iPhone (başlatan, IK) -> PC: 1 MB; her WebSocket mesajı bir Noise mesajı (<= 65535), ilk mesaj 20 baytlık ön ek (taşıma=3).
    func testIKTransferOneMegabyteOverFakeWebSocket() async throws {
        let phoneKey = NoisePrivateKey(), pcKey = NoisePrivateKey()
        let (phoneWs, pcWs) = FakeWebSocket.makePair()
        let id = TransferId.random()
        let prefix = try Prefix(transport: .relay, transferId: id.bytes)

        async let pcChannel = SecureChannel.acceptIK(
            transport: RelayMessageTransport(socket: pcWs), localKey: pcKey, isTrusted: { $0 == phoneKey.publicKey.rawRepresentation })
        let phone = try await SecureChannel.connectIK(
            transport: RelayMessageTransport(socket: phoneWs), prefix: prefix, localKey: phoneKey,
            remoteStaticKey: pcKey.publicKey.rawRepresentation)
        let pc = try await pcChannel
        XCTAssertEqual(pc.prefix, prefix)
        XCTAssertEqual(phone.prefix.transport, .relay)

        let payload = patternData(count: 1 << 20, seed: 9)
        let items = [item(0, "bir-mb.bin", payload)]
        var o = ReceiverOptions(peerId: "phone", transferId: id, expectedCount: 1, expectedTotal: Int64(payload.count))
        o.spaceMarginBytes = 0
        let receiver = FileReceiver(channel: pc, storage: try DirectoryReceiverStorage(directory: inbox), options: o)
        async let rr = receiver.run()
        let sr = try await FileSender(channel: phone, items: items).run()
        let r = try await rr
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(r.outcome, .completed)
        let f = try XCTUnwrap(r.receivedFiles.first)
        XCTAssertEqual(sha256Of(try Data(contentsOf: URL(fileURLWithPath: f.storedPath))), sha256Of(payload))

        XCTAssertEqual(phoneWs.sent.first, prefix.bytes)
        XCTAssertEqual(phoneWs.sent.first?.count, Prefix.length)
        XCTAssertTrue(phoneWs.sent.allSatisfy { !$0.isEmpty && $0.count <= 65535 })
        XCTAssertTrue(pcWs.sent.allSatisfy { !$0.isEmpty && $0.count <= 65535 })
        XCTAssertGreaterThan(phoneWs.sent.count, 16)   // 1 MB birden çok Noise mesajına bölünmüş
    }

    /// XX + beklenen PC anahtarı (BLE'de öğrenilen anahtar) relay üstünde de çalışır; uyuşmazlık 3. mesajdan önce reddedilir.
    func testXXWithExpectedRemoteStaticOverFakeWebSocket() async throws {
        let phoneKey = NoisePrivateKey(), pcKey = NoisePrivateKey()
        let prefix = try Prefix(transport: .relay, transferId: TransferId.random().bytes)
        do {
            let (phoneWs, pcWs) = FakeWebSocket.makePair()
            async let pcChannel = SecureChannel.acceptXX(transport: RelayMessageTransport(socket: pcWs), localKey: pcKey)
            let phone = try await SecureChannel.connectXX(
                transport: RelayMessageTransport(socket: phoneWs), prefix: prefix, localKey: phoneKey,
                expectedRemoteStatic: pcKey.publicKey.rawRepresentation)
            let pc = try await pcChannel
            XCTAssertEqual(phone.remoteStaticKey, pcKey.publicKey.rawRepresentation)
            XCTAssertEqual(pc.remoteStaticKey, phoneKey.publicKey.rawRepresentation)
            XCTAssertEqual(phone.verificationCode, pc.verificationCode)
        }
        do {
            let (phoneWs, pcWs) = FakeWebSocket.makePair()
            let acceptTask = Task { try await SecureChannel.acceptXX(transport: RelayMessageTransport(socket: pcWs), localKey: pcKey) }
            do {
                _ = try await SecureChannel.connectXX(
                    transport: RelayMessageTransport(socket: phoneWs), prefix: prefix, localKey: phoneKey,
                    expectedRemoteStatic: NoisePrivateKey().publicKey.rawRepresentation)
                XCTFail("anahtar uyuşmazlığı beklenirdi")
            } catch {
                XCTAssertEqual(error as? SessionError, .peerKeyMismatch)
            }
            XCTAssertEqual(phoneWs.sent.count, 2)   // ön ek + 1. el sıkışma mesajı; 3. mesaj gitmedi
            acceptTask.cancel()
        }
    }

    /// Sunucu/eş metin mesajı gönderirse oturum protokol hatasıyla düşer ve soket kapatılır.
    func testTextMessageIsAnError() async throws {
        let (phoneWs, pcWs) = FakeWebSocket.makePair()
        let transport = RelayMessageTransport(socket: phoneWs)
        await pcWs.sendText("merhaba")
        do {
            _ = try await transport.receiveMessage(handshakePhase: false)
            XCTFail("hata beklenirdi")
        } catch {
            guard case .protocolViolation = error as? SessionError else { return XCTFail("beklenmeyen hata: \(error)") }
        }
        XCTAssertEqual(phoneWs.closeCalls, 1)

        // El sıkışma sırasında da aynı.
        let (phone2, pc2) = FakeWebSocket.makePair()
        await pc2.sendText("{}")
        do {
            _ = try await SecureChannel.connectIK(
                transport: RelayMessageTransport(socket: phone2), prefix: try Prefix(transport: .relay, transferId: TransferId.random().bytes),
                localKey: NoisePrivateKey(), remoteStaticKey: NoisePrivateKey().publicKey.rawRepresentation)
            XCTFail("hata beklenirdi")
        } catch {
            guard case .protocolViolation = error as? SessionError else { return XCTFail("beklenmeyen hata: \(error)") }
        }
    }

    func testVersionMismatchReplyAndLimits() async throws {
        let (phoneWs, pcWs) = FakeWebSocket.makePair()
        let transport = RelayMessageTransport(socket: phoneWs)
        try await pcWs.send(binary: Prefix.versionMismatchResponse(supportedVersion: 2))
        do {
            _ = try await transport.receiveMessage(handshakePhase: true)
            XCTFail("sürüm uyuşmazlığı beklenirdi")
        } catch {
            XCTAssertEqual(error as? SessionError, .versionMismatch(supportedVersion: 2))
        }
        // Gönderme sınırları: boş ve > 65535 reddedilir.
        do { try await transport.sendMessage(Data()); XCTFail() } catch { XCTAssertTrue(error is ProtocolError) }
        do { try await transport.sendMessage(Data(count: 65536)); XCTFail() } catch {
            XCTAssertEqual(error as? ProtocolError, .messageTooLarge(65536))
        }
        // Kapanış: nil.
        await pcWs.close()
        let end = try await transport.receiveMessage(handshakePhase: false)
        XCTAssertNil(end)
    }
}
