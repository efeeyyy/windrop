import Foundation

/// 16 baytlık aktarım kimliği (PROTOCOL §1 ön ek; §4 JSON'da 16 B base64url, dolgusuz, 22 karakter).
public struct TransferId: Hashable, CustomStringConvertible {
    public static let length = 16
    public let bytes: Data

    public init?(bytes: Data) {
        guard bytes.count == TransferId.length else { return nil }
        self.bytes = Data(bytes)
    }

    public static func random() -> TransferId {
        var g = SystemRandomNumberGenerator()
        let b = Data((0..<length).map { _ in UInt8.random(in: 0...255, using: &g) })
        return TransferId(bytes: b)!
    }

    /// base64url, dolgusuz, 22 karakter.
    public var description: String {
        bytes.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    /// Katı ayrıştırma: tam 22 karakter, yalnız base64url alfabesi, kurallı (kanonik) kodlama.
    public static func parse(_ text: String) -> TransferId? {
        guard text.utf8.count == 22 else { return nil }
        let ok = text.utf8.allSatisfy {
            ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x41 && $0 <= 0x5A) || ($0 >= 0x61 && $0 <= 0x7A) || $0 == 0x2D || $0 == 0x5F
        }
        guard ok else { return nil }
        let std = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/") + "=="
        guard let data = Data(base64Encoded: std), let id = TransferId(bytes: data), id.description == text else { return nil }
        return id
    }
}
