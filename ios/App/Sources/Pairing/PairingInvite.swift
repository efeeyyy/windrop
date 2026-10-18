import Foundation

enum PairingError: Error, LocalizedError {
    case invalidCode
    case unsupportedVersion(Int)
    case notPaired
    case rejectedByPC
    case saveFailed
    case pcNotFound

    var errorDescription: String? {
        switch self {
        case .pcNotFound: return String(localized: "Bilgisayar bulunamadı — bilgisayarda WinDrop'un açık ve QR ekranının görünür olduğundan emin olun.")
        case .invalidCode: return String(localized: "Bu bir WinDrop eşleştirme kodu değil.")
        case .unsupportedVersion(let v): return String(localized: "Eşleştirme kodu sürümü (\(v)) desteklenmiyor. Uygulamayı güncelleyin.")
        case .notPaired: return String(localized: "Eşleştirme tamamlanmadı.")
        case .rejectedByPC: return String(localized: "Bilgisayar eşleşmeyi onaylamadı veya bağlantıyı kesti.")
        case .saveFailed: return String(localized: "Eşleşme kaydedilemedi.")
        }
    }
}

/// `windrop://pair?v=1&k=<PC açık anahtarı b64url>&s=<32 B sır b64url>&n=<PC adı>` (PROTOCOL §2).
struct PairingInvite: Equatable, Sendable {
    let version: Int
    let pcPublicKey: Data
    let secret: Data
    let pcName: String

    static func parse(_ text: String) throws -> PairingInvite {
        guard let components = URLComponents(string: text.trimmingCharacters(in: .whitespacesAndNewlines)),
              components.scheme?.lowercased() == "windrop",
              components.host?.lowercased() == "pair" else {
            throw PairingError.invalidCode
        }
        func value(_ name: String) -> String? {
            components.queryItems?.first { $0.name == name }?.value
        }
        guard let v = value("v").flatMap({ Int($0) }),
              let k = value("k").flatMap(Base64URL.decode),
              let s = value("s").flatMap(Base64URL.decode) else {
            throw PairingError.invalidCode
        }
        guard v == 1 else { throw PairingError.unsupportedVersion(v) }
        guard k.count == 32, s.count == 32 else { throw PairingError.invalidCode }
        let rawName = value("n")?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let name = rawName.isEmpty ? "Bilgisayar" : String(rawName.prefix(64))
        return PairingInvite(version: v, pcPublicKey: k, secret: s, pcName: name)
    }
}
