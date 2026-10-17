import Foundation
#if canImport(CryptoKit)
import CryptoKit
#else
import Crypto
#endif

// PROTOCOL §6.3 aktarma sunucusu: oda kimliği, adres kurma ve WebSocket üstünde MessageTransport.

/// Aktarma odasındaki rol (`?role=`).
public enum RelayRole: String, Equatable {
    case pc
    case phone
}

public enum RelayRoom {
    public static let tokenLength = 32
    public static let roomIdLength = 32

    /// `roomId = hex(SHA-256("WinDrop-relay" || token))[0:32]` (küçük harf onaltılık).
    public static func roomId(token: Data) -> String {
        var input = Data("WinDrop-relay".utf8)
        input.append(token)
        let hex = SHA256.hash(data: input).map { String(format: "%02x", $0) }.joined()
        return String(hex.prefix(roomIdLength))
    }

    /// `relay` sinyalindeki `token` dizesi: 32 baytın base64url, dolgusuz (43 karakter) kodlaması
    /// (`transferId` ile aynı biçim, PROTOCOL §9a). Geçersizse nil.
    public static func decodeToken(_ text: String) -> Data? {
        guard text.utf8.count == 43 else { return nil }
        let ok = text.utf8.allSatisfy {
            ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x41 && $0 <= 0x5A) || ($0 >= 0x61 && $0 <= 0x7A) || $0 == 0x2D || $0 == 0x5F
        }
        guard ok else { return nil }
        let std = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/") + "="
        guard let data = Data(base64Encoded: std), data.count == tokenLength, encodeToken(data) == text else { return nil }
        return data
    }

    public static func encodeToken(_ token: Data) -> String {
        token.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    /// `<relayUrl>/v1/room/<roomId>?role=...`. `relayUrl` `wss://host[:port][/önek]` (veya `https://`; şema yoksa `wss://`)
    /// olmalıdır. Düz `ws://`/`http://` yalnız yerel geri döngü adreslerinde (geliştirme) kabul edilir. Geçersizse nil.
    public static func endpoint(relayUrl: String, roomId: String, role: RelayRole) -> URL? {
        var text = relayUrl.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        if !text.contains("://") { text = "wss://" + text }
        guard var comps = URLComponents(string: text), let rawScheme = comps.scheme?.lowercased(),
              let host = comps.host, !host.isEmpty, comps.user == nil, comps.password == nil else { return nil }
        let loopback = host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "[::1]"
        switch rawScheme {
        case "wss", "https": comps.scheme = "wss"
        case "ws", "http":
            guard loopback else { return nil }
            comps.scheme = "ws"
        default: return nil
        }
        guard roomId.utf8.count == roomIdLength,
              roomId.utf8.allSatisfy({ ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x61 && $0 <= 0x66) }) else { return nil }
        var path = comps.path
        while path.hasSuffix("/") { path.removeLast() }
        comps.path = path + "/v1/room/" + roomId
        comps.queryItems = [URLQueryItem(name: "role", value: role.rawValue)]
        comps.fragment = nil
        return comps.url
    }
}

/// WebSocket'in platformdan bağımsız görünümü (iOS: `URLSessionWebSocketTask` uyarlayıcısı; testler: sahte).
public enum WebSocketMessage: Equatable {
    case binary(Data)
    case text(String)
}

public protocol WebSocketLike: AnyObject {
    /// Tek ikili mesaj gönderir.
    func send(binary: Data) async throws
    /// Sıradaki mesaj; karşı taraf temiz kapattıysa nil.
    func receive() async throws -> WebSocketMessage?
    /// Bağlantıyı kapatır (en iyi çaba).
    func close() async
}

/// Aktarma taşıması: her WebSocket ikili mesajı = bir Noise mesajı (uzunluk öneki yok, ≤ 65535 B).
/// 20 baytlık ön ek ve sürüm uyuşmazlığı yanıtı da ayrı birer ikili mesajdır (BLE'deki gibi "ham" gider).
/// Sunucu metin mesajında bağlantıyı kapatır; burada da metin mesajı protokol hatasıdır.
public final class RelayMessageTransport: MessageTransport {
    private let socket: WebSocketLike
    public let kind: TransportKind = .relay

    public init(socket: WebSocketLike) { self.socket = socket }

    public var maxMessageSize: Int { TcpFraming.maxNoiseMessage }

    public func sendRaw(_ data: Data) async throws {
        guard !data.isEmpty else { throw ProtocolError.invalidFrame("boş mesaj gönderilemez") }
        try await socket.send(binary: data)
    }

    public func receiveRaw(count: Int) async throws -> Data {
        guard let data = try await nextBinary() else { throw SessionError.connectionClosed }
        guard data.count == count else {
            throw ProtocolError.invalidPrefix("relay'de ham mesaj \(count) bayt olmalı (\(data.count) geldi)")
        }
        return data
    }

    public func sendMessage(_ message: Data) async throws {
        guard !message.isEmpty else { throw ProtocolError.invalidFrame("boş Noise mesajı gönderilemez") }
        guard message.count <= maxMessageSize else { throw ProtocolError.messageTooLarge(message.count) }
        try await socket.send(binary: message)
    }

    public func receiveMessage(handshakePhase: Bool) async throws -> Data? {
        guard let data = try await nextBinary() else { return nil }
        // Gerçek el sıkışma yanıtları ≥ 48 bayttır; tam 2 baytlık 0xFF mesajı sürüm yanıtıdır (BLE ile aynı kural).
        if handshakePhase, data.count == 2, data[data.startIndex] == Prefix.versionMismatchMarker {
            throw SessionError.versionMismatch(supportedVersion: data[data.startIndex + 1])
        }
        return data
    }

    /// Bağlantıyı kapatır.
    public func close() async { await socket.close() }

    private func nextBinary() async throws -> Data? {
        guard let message = try await socket.receive() else { return nil }
        switch message {
        case .text:
            await socket.close()
            throw SessionError.protocolViolation("relay: metin mesajı geldi (yalnız ikili beklenir)")
        case .binary(let data):
            guard !data.isEmpty else { throw ProtocolError.invalidFrame("boş relay mesajı") }
            guard data.count <= maxMessageSize else { throw ProtocolError.messageTooLarge(data.count) }
            return data
        }
    }
}
