import UIKit

/// `UIApplication.beginBackgroundTask` sarmalayıcısı (yalnız ana uygulama; Paylaşım Uzantısı'nda bu API yoktur).
/// BLE olayıyla arka planda uyanınca IK + hello, bildirim ekleme ve `reject` gönderimi bu görevlerin içinde biter.
@MainActor
enum AppBackgroundTask {
    /// Görev kimliğini tutar; süre dolunca (expirationHandler) ve elle kapatmada aynı kapatıcı çalışır (tek sefer).
    @MainActor
    private final class Token {
        var id: UIBackgroundTaskIdentifier = .invalid

        func end() {
            guard id != .invalid else { return }
            let finished = id
            id = .invalid
            UIApplication.shared.endBackgroundTask(finished)
        }
    }

    static let starter: BackgroundTaskStarter = { name in
        let token = Token()
        token.id = UIApplication.shared.beginBackgroundTask(withName: name) {
            // Süre doldu: iOS bu çağrıdan dönmeden görevin bitirilmesini bekler (ana iş parçacığında çağrılır).
            MainActor.assumeIsolated { token.end() }
        }
        return { token.end() }
    }
}
