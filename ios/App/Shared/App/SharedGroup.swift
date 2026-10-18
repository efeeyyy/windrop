import Foundation

/// Uygulama ile Paylaşım Uzantısı'nın ortak App Group'u (tek cihaz kimliği, ortak ayarlar).
/// Grup adı Info.plist `WinDropAppGroup` anahtarından gelir (project.yml: `group.$(WINDROP_BUNDLE_ID)`).
/// Grup, imzalama sırasında yetki olarak verilmemişse (ücretsiz hesap / Sideloadly grubu kaydetmediyse)
/// `containerURL(forSecurityApplicationGroupIdentifier:)` nil döner: bu durumda her hedef kendi alanını kullanır
/// (uzantının ayrı kimliği olur; uzantı yalnız açıkken bağlandığı için çakışma çıkmaz).
enum SharedGroup {
    static var identifier: String? {
        guard let id = Bundle.main.object(forInfoDictionaryKey: "WinDropAppGroup") as? String,
              id.hasPrefix("group."), !id.contains("$") else { return nil }
        return id
    }

    /// Grup konteyneri çalışma zamanında erişilebilir mi (yetki gerçekten verilmiş mi)?
    static let isAvailable: Bool = {
        guard let id = identifier else { return false }
        return FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: id) != nil
    }()

    /// Ortak ayarlar: grup varsa grup UserDefaults'u, yoksa hedefin kendi UserDefaults'u.
    static var defaults: UserDefaults {
        if isAvailable, let id = identifier, let suite = UserDefaults(suiteName: id) { return suite }
        return .standard
    }

    /// Ana uygulama, gruptan önceki sürümde `.standard`'a yazılmış ortak ayarları gruba kopyalar (bir kez; var olanı ezmez).
    static func migrateSettings(keys: [String]) {
        guard isAvailable, !ProtocolIds.isExtension else { return }
        let shared = defaults
        for key in keys where shared.object(forKey: key) == nil {
            if let value = UserDefaults.standard.object(forKey: key) { shared.set(value, forKey: key) }
        }
    }
}
