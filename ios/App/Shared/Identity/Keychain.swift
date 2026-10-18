import Foundation
import Security

enum KeychainError: Error, LocalizedError {
    case status(OSStatus)

    var errorDescription: String? {
        switch self {
        case .status(let s): return "Anahtar zinciri hatası (\(s))."
        }
    }
}

/// Genel parola (kSecClassGenericPassword) öğeleri üstünde ince sarmalayıcı.
/// Erişilebilirlik: `AfterFirstUnlockThisDeviceOnly` (kilitliyken arka plan BLE el sıkışması için).
///
/// İki alan vardır:
/// - Yerel (`shared: false`, varsayılan): hedefin kendi alanı (servis = paket kimliği). Uygulama ve Paylaşım Uzantısı
///   birbirinin öğesini görmez.
/// - Ortak (`shared: true`): App Group kimliği erişim grubu olarak kullanılır (`application-groups` yetkisi, App Group
///   kimliği Keychain erişim grubu sayılır); uygulama ve uzantı AYNI cihaz anahtarını ve tanıdık listesini paylaşır.
///   Grup yetkisi imzalamada verilmediyse `isSharedAvailable` false olur ve ortak alan kullanılmaz.
enum Keychain {
    /// Ortak alan kullanılabilir mi: grup konteyneri erişilebilir VE gruba bir sınama öğesi yazılıp okunabiliyor.
    static let isSharedAvailable: Bool = {
        guard SharedGroup.isAvailable else { return false }
        let probe = Data("1".utf8)
        do {
            try write(probe, account: "probe-v1", shared: true)
            return try read("probe-v1", shared: true) == probe
        } catch {
            return false
        }
    }()

    private static func baseQuery(_ account: String, shared: Bool) -> [String: Any] {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrAccount as String: account,
        ]
        if shared, let group = SharedGroup.identifier {
            query[kSecAttrService as String] = group
            query[kSecAttrAccessGroup as String] = group
        } else {
            query[kSecAttrService as String] = Bundle.main.bundleIdentifier ?? "com.efe.windrop"
        }
        return query
    }

    static func read(_ account: String, shared: Bool = false) throws -> Data? {
        var query = baseQuery(account, shared: shared)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        switch status {
        case errSecSuccess: return item as? Data
        case errSecItemNotFound: return nil
        default: throw KeychainError.status(status)
        }
    }

    static func write(_ data: Data, account: String, shared: Bool = false) throws {
        let attributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        let updateStatus = SecItemUpdate(baseQuery(account, shared: shared) as CFDictionary, attributes as CFDictionary)
        if updateStatus == errSecSuccess { return }
        guard updateStatus == errSecItemNotFound else { throw KeychainError.status(updateStatus) }
        var add = baseQuery(account, shared: shared)
        add.merge(attributes) { _, new in new }
        let addStatus = SecItemAdd(add as CFDictionary, nil)
        guard addStatus == errSecSuccess else { throw KeychainError.status(addStatus) }
    }

    static func delete(_ account: String, shared: Bool = false) throws {
        let status = SecItemDelete(baseQuery(account, shared: shared) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw KeychainError.status(status) }
    }
}
