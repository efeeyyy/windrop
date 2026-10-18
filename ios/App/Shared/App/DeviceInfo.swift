import Foundation
import UIKit

enum DeviceInfo {
    /// Ayarlar'daki "Bu cihazın adı" alanının anahtarı. Değer `SharedGroup.defaults`'ta tutulur: grup varsa uygulama ve
    /// uzantı aynı adı görür; yoksa uzantı kendi varsayılanını kullanır.
    static let nameKey = "deviceName"
    /// Ad ipucu kartı kapatıldı mı (yalnız ana uygulama).
    static let nameHintDismissedKey = "deviceNameHintDismissed"

    /// Varsayılan ad. iOS 16+ `UIDevice.current.name` (yetki olmadan) genel "iPhone" döner ve kullanıcının verdiği adı
    /// okumak mümkün değildir; bu yüzden ad Ayarlar'dan elle girilir.
    @MainActor
    static var defaultName: String {
        UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone"
    }

    /// `hello.name` olarak gönderilen ad: Ayarlar'da girilen; boşsa varsayılan ("iPhone"/"iPad").
    @MainActor
    static func name() -> String {
        let custom = SharedGroup.defaults.string(forKey: nameKey)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return String((custom.isEmpty ? defaultName : custom).prefix(64))
    }

    /// Ad hâlâ varsayılan mı (girilmemiş ya da varsayılanın aynısı)?
    @MainActor
    static var isDefaultName: Bool { name() == defaultName }
}
