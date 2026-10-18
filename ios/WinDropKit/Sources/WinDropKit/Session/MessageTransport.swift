import Foundation

/// Oturum katmanı hataları.
public enum SessionError: Error, Equatable {
    /// Yanıtlayan sürümü desteklemiyor (`0xFF || sürüm` yanıtı); kullanıcıya "güncelleme gerekli" gösterilebilir.
    case versionMismatch(supportedVersion: UInt8)
    /// Yanıtlayan: IK ile gelen statik anahtar güvenilen listede yok; bağlantı kesilir.
    case peerNotTrusted(publicKey: Data)
    /// Eşleştirme: gelen PC statik anahtarı QR'daki `k` ile aynı değil.
    case peerKeyMismatch
    case connectionClosed
    case transportMismatch
    /// Yanıtlayan, başlatandan ilk geçerli taşıma mesajı gelmeden veri gönderemez (§1).
    case notEstablished
    case protocolViolation(String)
}

/// Mesaj tabanlı soyut taşıma: bir `sendMessage` = bir Noise mesajı (TCP'de uzunluk önekli, BLE'de parçalı).
/// 20 baytlık ön ek ve sürüm uyuşmazlığı yanıtı çerçevesiz "ham" gider.
public protocol MessageTransport: AnyObject {
    var kind: TransportKind { get }
    /// Bir Noise mesajının (etiket dahil) en büyük boyutu.
    var maxMessageSize: Int { get }
    func sendRaw(_ data: Data) async throws
    /// Tam `count` baytlık ham veri (ön ek). BLE'de tek GATT yazması.
    func receiveRaw(count: Int) async throws -> Data
    func sendMessage(_ message: Data) async throws
    /// Sıradaki Noise mesajı; karşı taraf çerçeve sınırında temiz kapattıysa nil.
    /// `handshakePhase` true ise (başlatanın ilk yanıtı) `0xFF || sürüm` yanıtı `SessionError.versionMismatch` olur.
    func receiveMessage(handshakePhase: Bool) async throws -> Data?
}

/// TCP (ve relay) taşıması: `uzunluk (2 B BE) || Noise mesajı`.
/// Sürüm yanıtı ayrımı: gerçek bir el sıkışma yanıtının uzunluk üst baytı 0x00'dır, uyuşmazlık yanıtı 0xFF ile başlar.
public final class TcpMessageTransport: MessageTransport {
    private let stream: ByteStream
    public let kind: TransportKind

    public init(stream: ByteStream, kind: TransportKind = .tcp) {
        precondition(kind != .ble)
        self.stream = stream
        self.kind = kind
    }

    public var maxMessageSize: Int { TcpFraming.maxNoiseMessage }

    public func sendRaw(_ data: Data) async throws { try await stream.write(data) }

    public func receiveRaw(count: Int) async throws -> Data {
        do { return try await stream.read(exactly: count) }
        catch ByteStreamError.endOfStream { throw SessionError.connectionClosed }
    }

    public func sendMessage(_ message: Data) async throws {
        try await TcpFraming.writeMessage(stream, message)
    }

    public func receiveMessage(handshakePhase: Bool) async throws -> Data? {
        if !handshakePhase { return try await TcpFraming.readMessage(stream) }
        let header: Data
        do {
            header = try await stream.read(exactly: TcpFraming.lengthPrefixSize)
        } catch ByteStreamError.endOfStream(let partial) {
            if partial.isEmpty { return nil }
            throw ProtocolError.streamEnded
        }
        let h = [UInt8](header)
        if h[0] == Prefix.versionMismatchMarker { throw SessionError.versionMismatch(supportedVersion: h[1]) }
        let length = (Int(h[0]) << 8) | Int(h[1])
        guard length > 0 else { throw ProtocolError.invalidFrame("sıfır uzunluklu çerçeve") }
        do { return try await stream.read(exactly: length) }
        catch ByteStreamError.endOfStream { throw ProtocolError.streamEnded }
    }
}

/// GATT üstünde tek yönlü yükler: `write` = bu tarafın gönderdiği bir GATT yükü (iPhone: `c2p` yazması),
/// `read` = karşıdan gelen bir yük (`p2c` bildirimi). Gerçek CoreBluetooth uygulaması dışarıdadır.
public protocol BleLink: AnyObject {
    /// Geçerli ATT MTU (≥ 23). Parça verisi en çok `mtu − 4` bayttır.
    var mtu: Int { get }
    func write(_ payload: Data) async throws
    /// Karşı tarafın aldığı onaylanan yazma (iPhone: Write With Response). Ön ek bununla gider:
    /// bağlantının ilk anındaki yanıtsız yazma kaybolabiliyor (2026-10-18 cihaz testi).
    func writeReliable(_ payload: Data) async throws
    /// Sıradaki yük; bağlantı temiz kapandıysa nil.
    func read() async throws -> Data?
}

public extension BleLink {
    func writeReliable(_ payload: Data) async throws { try await write(payload) }
}

/// BLE taşıması: her Noise mesajı `BleFragmenter` ile parçalanır; sıra no yön başına, mesaj başına artar.
public final class BleMessageTransport: MessageTransport {
    private let link: BleLink
    private let reassembler = BleReassembler()
    private var sendSequence = 0
    public let kind: TransportKind = .ble

    public init(link: BleLink) { self.link = link }

    public var maxMessageSize: Int { BleFragmenter.maxMessageSize }

    /// Ön ek ve sürüm yanıtı parçalanmaz: tek yazma.
    public func sendRaw(_ data: Data) async throws { try await link.writeReliable(data) }

    public func receiveRaw(count: Int) async throws -> Data {
        guard let payload = try await link.read() else { throw SessionError.connectionClosed }
        guard payload.count == count else {
            throw ProtocolError.invalidPrefix("BLE'de ham yük \(count) bayt olmalı (\(payload.count) geldi)")
        }
        return payload
    }

    public func sendMessage(_ message: Data) async throws {
        let seq = sendSequence
        let fragments = try BleFragmenter.fragment(message, mtu: link.mtu, sequence: seq)
        sendSequence = (seq + 1) % BleFragmenter.sequenceModulo
        for f in fragments { try await link.write(f) }
    }

    public func receiveMessage(handshakePhase: Bool) async throws -> Data? {
        var first = handshakePhase
        while true {
            guard let payload = try await link.read() else { return nil }
            if first {
                first = false
                // İlk gerçek parça başlığı 0x00/0x80'dir; tam 2 baytlık 0xFF yükü sürüm yanıtıdır.
                if payload.count == 2, payload[payload.startIndex] == Prefix.versionMismatchMarker {
                    throw SessionError.versionMismatch(supportedVersion: payload[payload.startIndex + 1])
                }
            }
            if let message = try reassembler.push(payload) { return message }
        }
    }
}
