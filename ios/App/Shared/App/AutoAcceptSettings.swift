import Foundation

/// "Eşli bilgisayarlardan gelenleri otomatik kabul et" ayarları (Windows ile aynı güvenlik kuralı: yalnız QR ile
/// eşleştirilmiş PC'ler; yalnız "tanıdık" olanlar her zaman sorulur).
enum AutoAcceptSettings {
    static let enabledKey = "autoAcceptPaired"
    static let limitKey = "autoAcceptLimitBytes"

    /// Protokol üst sınırı (PROTOCOL §5, §7): 2 GB.
    static let maxLimit: Int = Int(TransferRunner.acceptAutoLimit)

    struct Option: Identifiable, Sendable {
        let title: String
        let bytes: Int
        var id: Int { bytes }
    }

    static let limitOptions: [Option] = [
        Option(title: "100 MB", bytes: 100 * 1024 * 1024),
        Option(title: "500 MB", bytes: 500 * 1024 * 1024),
        Option(title: "1 GB", bytes: 1024 * 1024 * 1024),
        Option(title: "2 GB", bytes: 2 * 1024 * 1024 * 1024),
    ]

    /// Varsayılan: açık.
    static var isEnabled: Bool {
        UserDefaults.standard.object(forKey: enabledKey) as? Bool ?? true
    }

    /// Varsayılan 2 GB; protokol sınırını aşamaz.
    static var limit: Int64 {
        let stored = UserDefaults.standard.object(forKey: limitKey) as? Int ?? maxLimit
        return Int64(min(max(stored, 0), maxLimit))
    }
}
