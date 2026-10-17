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
/// Kimlik paylaşımı: App Groups / Keychain Sharing ücretsiz hesapta YOK. Varsayılan kipte uygulama ve Paylaşım
/// Uzantısı ayrı Keychain alanları kullanır (uzantının kendi cihaz anahtarı olur; PC'de ayrı "tanıdık" kayıt).
/// Ücretli hesapta `APP_GROUP` derleme bayrağı (project.yml, `keychain-access-groups` yetkisi) ile ortak grup açılır.
enum Keychain {
    #if APP_GROUP
    /// Info.plist `AppIdentifierPrefix` (= `$(AppIdentifierPrefix)`, ör. "ABCDE12345.") + ortak grup adı.
    private static var accessGroup: String? {
        guard let prefix = Bundle.main.object(forInfoDictionaryKey: "AppIdentifierPrefix") as? String,
              !prefix.isEmpty else { return nil }
        return prefix + "com.efe.windrop.shared"
    }
    private static var service: String { "com.efe.windrop" }
    #else
    private static var service: String { Bundle.main.bundleIdentifier ?? "com.efe.windrop" }
    #endif

    private static func baseQuery(_ account: String) -> [String: Any] {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        #if APP_GROUP
        if let group = accessGroup { query[kSecAttrAccessGroup as String] = group }
        #endif
        return query
    }

    static func read(_ account: String) throws -> Data? {
        var query = baseQuery(account)
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

    static func write(_ data: Data, account: String) throws {
        let attributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        let updateStatus = SecItemUpdate(baseQuery(account) as CFDictionary, attributes as CFDictionary)
        if updateStatus == errSecSuccess { return }
        guard updateStatus == errSecItemNotFound else { throw KeychainError.status(updateStatus) }
        var add = baseQuery(account)
        add.merge(attributes) { _, new in new }
        let addStatus = SecItemAdd(add as CFDictionary, nil)
        guard addStatus == errSecSuccess else { throw KeychainError.status(addStatus) }
    }

    static func delete(_ account: String) throws {
        let status = SecItemDelete(baseQuery(account) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw KeychainError.status(status) }
    }
}
