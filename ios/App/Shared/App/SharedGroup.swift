import Foundation

/// Uygulama ile Paylaşım Uzantısı'nın ortak App Group'u (tek cihaz kimliği, ortak ayarlar).
/// Grup adı Info.plist `WinDropAppGroup` anahtarından gelir (project.yml: `group.$(WINDROP_BUNDLE_ID)`).
/// Grup, imzalama sırasında yetki olarak verilmemişse (ücretsiz hesap / Sideloadly grubu kaydetmediyse)
/// `containerURL(forSecurityApplicationGroupIdentifier:)` nil döner: bu durumda her hedef kendi alanını kullanır
/// (uzantının ayrı kimliği olur; uzantı yalnız açıkken bağlandığı için çakışma çıkmaz).
/// SideStore yeniden imzalarken paket kimliğini (ör. sonuna takım kimliği ekleyerek) ve grup adını değiştirebilir:
/// grup adı `group.<paket kimliği>` biçimindeyse yeni ad `group.<yeni paket kimliği>` olur (SideStore
/// FetchProvisioningProfilesOperation `adjustedGroupIdentifier`, 2026-11 okundu). Info.plist'e yeni ad yazılmaz;
/// bu yüzden çalışma anındaki paket kimliğinden türetilen ad da denenir ve konteyneri gerçekten açılan ilk ad kullanılır.
enum SharedGroup {
    /// Uzantının paket kimliği `<uygulama>.share`; ortak grup ana uygulamanın kimliğinden türetilir.
    private static let extensionSuffix = ".share"

    /// Aday grup adları: önce projenin kendi anahtarı, sonra çalışma anındaki (yeniden imzalanmış) paket kimliğinden türetilen.
    static var candidates: [String] {
        var ids: [String] = []
        if let id = Bundle.main.object(forInfoDictionaryKey: "WinDropAppGroup") as? String { ids.append(id) }
        if var bid = Bundle.main.bundleIdentifier {
            if bid.hasSuffix(extensionSuffix) { bid.removeLast(extensionSuffix.count) }
            ids.append("group." + bid)
        }
        var seen = Set<String>()
        return ids.filter { $0.hasPrefix("group.") && !$0.contains("$") && seen.insert($0).inserted }
    }

    /// Konteyneri açılabilen ilk grup adı; hiçbiri açılmıyorsa nil (grup yetkisi verilmemiş).
    static let identifier: String? = candidates.first {
        FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: $0) != nil
    }

    /// Grup konteyneri çalışma zamanında erişilebilir mi (yetki gerçekten verilmiş mi)?
    static var isAvailable: Bool { identifier != nil }

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
