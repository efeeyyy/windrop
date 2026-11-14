import Foundation
@preconcurrency import WinDropKit

/// Kit hatalarını (`SessionError`, `ProtocolError`, `NoiseError`, `TransferError`, `ByteStreamError`) kullanıcıya anlaşılır,
/// yerelleştirilmiş metne çevirir. Kit hata türleri `LocalizedError` değildir: doğrudan `localizedDescription`
/// "İşlem tamamlanamadı. (WinDropKit.SessionError hata 1.)" gibi teknik metin verir.
enum UserFacingError {
    static var connectionLost: String { String(localized: "Bağlantı koptu, tekrar deneyin.") }
    static var protocolFailure: String { String(localized: "Bilgisayarla iletişim kurulamadı (protokol hatası).") }
    static var secureChannelFailure: String { String(localized: "Güvenli bağlantı kurulamadı.") }
    static var versionMismatch: String { String(localized: "Bilgisayardaki WinDrop sürümü uyumsuz. Güncelleme gerekli.") }
    static var peerKeyMismatch: String { String(localized: "Bilgisayarın kimliği doğrulanamadı.") }
    static var peerNotTrusted: String { String(localized: "Bilgisayar bu iPhone'u tanımıyor. Eşleştirmeyi yeniden yapın.") }
    static var peerTimeout: String { String(localized: "Bilgisayar zamanında yanıt vermedi.") }
    static var generic: String { String(localized: "İşlem tamamlanamadı, tekrar deneyin.") }

    /// Kullanıcıya gösterilecek metin. Uygulamanın kendi hataları (`LocalizedError`) olduğu gibi, Kit hataları eşlemeyle,
    /// sistem hataları işletim sisteminin yerelleştirilmiş metniyle gelir; teknik Swift metni asla gösterilmez.
    static func message(for error: Error) -> String {
        switch error {
        case let e as SessionError:
            switch e {
            case .versionMismatch: return versionMismatch
            case .peerNotTrusted: return peerNotTrusted
            case .peerKeyMismatch: return peerKeyMismatch
            case .connectionClosed: return connectionLost
            case .transportMismatch, .notEstablished, .protocolViolation: return protocolFailure
            }
        case let e as ProtocolError:
            return e == .streamEnded ? connectionLost : protocolFailure
        case is ByteStreamError:
            return connectionLost
        case let e as TransferError:
            switch e {
            case .connectionLost: return connectionLost
            case .timeout: return peerTimeout
            case .protocolViolation: return protocolFailure
            }
        case let e as NoiseError:
            if e == .decryptionFailed || e == .handshakeFailed || e == .dhFailed { return secureChannelFailure }
            return protocolFailure
        case is LocalizedError:
            return error.localizedDescription
        default:
            let ns = error as NSError
            // Swift'in kendi sentezlediği metin (alan adı modül adıdır): teknik, gösterilmez.
            if ns.domain.hasPrefix("WinDrop") || ns.domain.hasPrefix("Swift") { return generic }
            return error.localizedDescription
        }
    }
}
