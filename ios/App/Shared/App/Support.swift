import Foundation
import UIKit
import UniformTypeIdentifiers

// Küçük, platform yardımcıları: base64url, tek okuyuculu posta kutusu, async kilit, pano.

enum Base64URL {
    /// Dolgusuz base64url (PROTOCOL §9a: transferId 16 bayt -> 22 karakter).
    static func encode(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    static func decode(_ string: String) -> Data? {
        var s = string
            .replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        while s.count % 4 != 0 { s += "=" }
        return Data(base64Encoded: s)
    }

    static func randomTransferId() -> Data {
        Data((0..<16).map { _ in UInt8.random(in: 0...255) })
    }
}

enum MailboxError: Error, LocalizedError {
    case timeout
    case closed

    var errorDescription: String? {
        switch self {
        case .timeout: return String(localized: "Bilgisayardan yanıt gelmedi (zaman aşımı).")
        case .closed: return String(localized: "Bağlantı kapandı.")
        }
    }
}

/// Tek okuyuculu, ana aktörde çalışan async kuyruk (BLE yüklerini ve ileti akışını taşır).
@MainActor
final class AsyncMailbox<Element: Sendable> {
    private var items: [Element] = []
    private var waiter: CheckedContinuation<Element, Error>?
    private var waiterToken = 0
    private var failure: Error?

    func push(_ element: Element) {
        if let w = waiter {
            waiter = nil
            w.resume(returning: element)
        } else {
            items.append(element)
        }
    }

    func removeAll() { items.removeAll() }

    /// Kuyruğu kalıcı olarak hataya çevirir; bekleyen okuyucu uyandırılır.
    func fail(_ error: Error) {
        if failure == nil { failure = error }
        if let w = waiter {
            waiter = nil
            w.resume(throwing: error)
        }
    }

    func pop(timeout: TimeInterval?) async throws -> Element {
        if !items.isEmpty { return items.removeFirst() }
        if let failure { throw failure }
        precondition(waiter == nil, "AsyncMailbox tek okuyuculu")
        waiterToken += 1
        let token = waiterToken
        return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Element, Error>) in
            waiter = cont
            if let timeout {
                Task { [weak self] in
                    try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                    self?.expire(token)
                }
            }
        }
    }

    private func expire(_ token: Int) {
        guard token == waiterToken, let w = waiter else { return }
        waiter = nil
        w.resume(throwing: MailboxError.timeout)
    }
}

/// Ana aktörde sıralı (FIFO) async karşılıklı dışlama.
@MainActor
final class AsyncLock {
    private var locked = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func withLock<T>(_ body: () async throws -> T) async rethrows -> T {
        await acquire()
        defer { release() }
        return try await body()
    }

    private func acquire() async {
        if !locked {
            locked = true
            return
        }
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            waiters.append(cont)
        }
    }

    private func release() {
        if waiters.isEmpty {
            locked = false
        } else {
            waiters.removeFirst().resume()
        }
    }
}

enum Clipboard {
    /// `sensitive` ise yerel kalır ve 2 dk sonra silinir (parola gibi).
    @MainActor
    static func copy(_ string: String, sensitive: Bool = false) {
        if sensitive {
            UIPasteboard.general.setItems(
                [[UTType.plainText.identifier: string]],
                options: [.localOnly: true, .expirationDate: Date().addingTimeInterval(120)])
        } else {
            UIPasteboard.general.string = string
        }
    }
}
