import Foundation

/// Taşıma türü (PROTOCOL §1 ön ek, 4. bayt).
public enum TransportKind: UInt8, Equatable {
    case ble = 1
    case tcp = 2
    case relay = 3
}

/// PROTOCOL §1 açık ön ek (20 bayt, şifresiz): `"WD" || sürüm (1) || taşıma (1) || transferId (16)`.
/// Aynı 20 bayt Noise prologue olarak da kullanılır (`bytes`). BLE'de transferId 16 sıfır bayttır.
/// BLE'de ön ek parçalanmaz; `c2p`'ye tek yazma olarak gider (PROTOCOL §9a).
public struct Prefix: Equatable {
    public static let length = 20
    public static let transferIdLength = 16
    public static let currentVersion: UInt8 = 1
    /// Sürüm uyuşmazlığı yanıtının ilk baytı.
    public static let versionMismatchMarker: UInt8 = 0xFF

    public let version: UInt8
    public let transport: TransportKind
    public let transferId: Data

    public init(version: UInt8 = Prefix.currentVersion, transport: TransportKind, transferId: Data) throws {
        guard transferId.count == Prefix.transferIdLength else {
            throw ProtocolError.invalidPrefix("transferId 16 bayt olmalı")
        }
        if transport == .ble, transferId.contains(where: { $0 != 0 }) {
            throw ProtocolError.invalidPrefix("BLE ön ekinde transferId 16 sıfır bayt olmalı")
        }
        self.version = version
        self.transport = transport
        self.transferId = Data(transferId)
    }

    /// BLE ön eki (transferId = 16 sıfır).
    public static func ble(version: UInt8 = Prefix.currentVersion) -> Prefix {
        // Sabit geçerli girdiler; try! güvenli.
        try! Prefix(version: version, transport: .ble, transferId: Data(count: transferIdLength))
    }

    /// 20 baytlık kodlama (aynı zamanda Noise prologue).
    public var bytes: Data {
        var d = Data([0x57, 0x44, version, transport.rawValue])  // "WD"
        d.append(transferId)
        return d
    }

    /// Ön eki çözer. Sürümü denetlemez: uyuşmazlığı çağıran `isVersionSupported` ile yönetir.
    public static func decode(_ data: Data) throws -> Prefix {
        let b = [UInt8](data)
        guard b.count == length else { throw ProtocolError.invalidPrefix("ön ek \(length) bayt olmalı (\(b.count) geldi)") }
        guard b[0] == 0x57, b[1] == 0x44 else { throw ProtocolError.invalidPrefix("ön ek 'WD' ile başlamıyor") }
        guard let transport = TransportKind(rawValue: b[3]) else {
            throw ProtocolError.invalidPrefix("bilinmeyen taşıma türü \(b[3])")
        }
        return try Prefix(version: b[2], transport: transport, transferId: Data(b[4...]))
    }

    public func isVersionSupported(_ supported: UInt8 = Prefix.currentVersion) -> Bool {
        version == supported
    }

    /// Yanıtlayanın sürüm uyuşmazlığında el sıkışmadan önce gönderdiği 2 bayt: `0xFF || desteklenen sürüm`.
    public static func versionMismatchResponse(supportedVersion: UInt8 = Prefix.currentVersion) -> Data {
        Data([versionMismatchMarker, supportedVersion])
    }

    /// Başlatan tarafı: gelen ilk baytlar sürüm uyuşmazlığı yanıtı mı? Yalnız tam 2 bayt ve ilk bayt 0xFF ise.
    public static func decodeVersionMismatch(_ data: Data) -> UInt8? {
        let b = [UInt8](data)
        guard b.count == 2, b[0] == versionMismatchMarker else { return nil }
        return b[1]
    }
}
