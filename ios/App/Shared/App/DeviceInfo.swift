import Foundation
import UIKit

enum DeviceInfo {
    /// Ayarlar'daki cihaz adı alanının UserDefaults anahtarı (uzantı kendi UserDefaults'unu görür: App Groups yok).
    static let nameKey = "deviceName"

    /// Ayarlar'da girilen ad; yoksa cihaz adı. (iOS 16+ `UIDevice.name` genel ad dönebilir.)
    @MainActor
    static func name() -> String {
        let custom = UserDefaults.standard.string(forKey: nameKey)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return String((custom.isEmpty ? UIDevice.current.name : custom).prefix(64))
    }
}
