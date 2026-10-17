#if PUSH_ENABLED
import Foundation
import Observation
import UIKit

/// Push bildirimi (PROTOCOL §6.5; yalnız `PUSH_ENABLED` derlemesinde, ücretli hesap + `aps-environment` yetkisi).
/// Akış: ayar açılır -> bildirim izni -> `registerForRemoteNotifications` -> APNs belirteci -> aktarma sunucusuna
/// `POST /v1/push/register` -> `pushId` (Keychain) -> tanıdık/eşli PC'lere `pushId` sinyali (SignalHub).
/// Cihaz belirteci yalnız aktarma sunucusuna gider; log'a yazılmaz. Kapatılınca sunucuya silme isteği yoktur
/// (yeni kayıt aynı belirteç için eskisini siler); kayıt yalnız yerelden silinir.
@MainActor
@Observable
final class PushManager {
    static let enabledKey = "pushEnabled"
    private static let registrationAccount = "push-registration-v1"

    /// Sunucudan alınan geçerli `pushId` (ayar kapalı / kayıt yok / sunucu adresi değişmişse nil).
    private(set) var pushId: String?
    /// Son kayıt denemesi başarısız oldu (ağ/sunucu); sonraki öne gelişte yeniden denenir.
    private(set) var registrationFailed = false

    /// `pushId` değişince (PC'lere yeniden verilir).
    @ObservationIgnored var onPushIdChanged: (() -> Void)?
    @ObservationIgnored private let notifications: NotificationManager
    @ObservationIgnored private var registrationTask: Task<Void, Never>?

    private struct Registration: Codable {
        var pushId: String
        var token: String
        var relay: String
    }

    init(notifications: NotificationManager) {
        self.notifications = notifications
        if isEnabled, let relay = RelaySettings.configuredURL(), let reg = Self.loadRegistration(), reg.relay == relay {
            pushId = reg.pushId
        }
    }

    var isEnabled: Bool { UserDefaults.standard.bool(forKey: Self.enabledKey) }

    /// Ayar anahtarı değişti (Ayarlar). Açılış: izin + APNs kaydı. Kapanış: yalnız yerel silme.
    func apply(enabled: Bool) {
        registrationTask?.cancel()
        registrationFailed = false
        if enabled {
            guard RelaySettings.configuredURL() != nil else {
                UserDefaults.standard.set(false, forKey: Self.enabledKey)
                return
            }
            Task {
                await notifications.requestAuthorizationIfNeeded()
                UIApplication.shared.registerForRemoteNotifications()
            }
        } else {
            try? Keychain.delete(Self.registrationAccount)
            UIApplication.shared.unregisterForRemoteNotifications()
            setPushId(nil)
        }
    }

    /// Uygulama öne gelince / Ayarlar kapanınca: APNs kaydını tazeler (belirteç değişmiş olabilir); sunucu adresi
    /// silinmişse ayar kapatılır.
    func refresh() {
        guard isEnabled else { return }
        guard RelaySettings.configuredURL() != nil else {
            UserDefaults.standard.set(false, forKey: Self.enabledKey)
            apply(enabled: false)
            return
        }
        UIApplication.shared.registerForRemoteNotifications()
    }

    /// `didRegisterForRemoteNotificationsWithDeviceToken`.
    func didRegister(deviceToken: Data) {
        guard isEnabled, let relay = RelaySettings.configuredURL() else { return }
        let token = deviceToken.map { String(format: "%02x", $0) }.joined()
        if let reg = Self.loadRegistration(), reg.token == token, reg.relay == relay, Self.isValid(pushId: reg.pushId) {
            registrationFailed = false
            setPushId(reg.pushId)   // aynı belirteç + aynı sunucu: yeniden kayıt gerekmez
            return
        }
        registrationTask?.cancel()
        registrationTask = Task {
            let newId = await Self.postRegister(relayUrl: relay, token: token)
            guard !Task.isCancelled else { return }
            // Beklerken ayar kapanmış veya adres değişmiş olabilir.
            guard isEnabled, RelaySettings.configuredURL() == relay else { return }
            guard let newId else {
                registrationFailed = true
                return
            }
            registrationFailed = false
            if let data = try? JSONEncoder().encode(Registration(pushId: newId, token: token, relay: relay)) {
                try? Keychain.write(data, account: Self.registrationAccount)
            }
            setPushId(newId)
        }
    }

    /// `didFailToRegisterForRemoteNotificationsWithError`.
    func didFailToRegister() {
        registrationFailed = true
    }

    private func setPushId(_ id: String?) {
        guard id != pushId else { return }
        pushId = id
        onPushIdChanged?()
    }

    // MARK: Yardımcılar

    /// PROTOCOL §6.5: 32 küçük onaltılık karakter.
    nonisolated static func isValid(pushId: String) -> Bool {
        pushId.utf8.count == 32 && pushId.utf8.allSatisfy { ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x61 && $0 <= 0x66) }
    }

    private static func loadRegistration() -> Registration? {
        guard let data = try? Keychain.read(registrationAccount) else { return nil }
        return try? JSONDecoder().decode(Registration.self, from: data)
    }

    /// DEBUG derlemesi APNs sandbox, aksi production (PROTOCOL §6.5).
    nonisolated private static var apnsEnvironment: String {
        #if DEBUG
        return "sandbox"
        #else
        return "production"
        #endif
    }

    /// `wss://host[/önek]` -> `https://host[/önek]/v1/push/register` (şema yoksa `wss`; düz `ws/http` yalnız geri döngü).
    nonisolated static func registerEndpoint(relayUrl: String) -> URL? {
        var text = relayUrl.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        if !text.contains("://") { text = "wss://" + text }
        guard var comps = URLComponents(string: text), let scheme = comps.scheme?.lowercased(),
              let host = comps.host, !host.isEmpty, comps.user == nil, comps.password == nil else { return nil }
        let loopback = host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "[::1]"
        switch scheme {
        case "wss", "https": comps.scheme = "https"
        case "ws", "http":
            guard loopback else { return nil }
            comps.scheme = "http"
        default: return nil
        }
        var path = comps.path
        while path.hasSuffix("/") { path.removeLast() }
        comps.path = path + "/v1/push/register"
        comps.query = nil
        comps.fragment = nil
        return comps.url
    }

    /// Sunucuya kayıt; başarıda `pushId`, aksi halde nil. Belirteç ve `pushId` loglanmaz.
    nonisolated private static func postRegister(relayUrl: String, token: String) async -> String? {
        guard let url = registerEndpoint(relayUrl: relayUrl),
              let body = try? JSONSerialization.data(withJSONObject: ["deviceToken": token, "env": apnsEnvironment])
        else { return nil }
        var request = URLRequest(url: url, timeoutInterval: 15)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = body
        let session = URLSession(configuration: .ephemeral)
        defer { session.finishTasksAndInvalidate() }
        guard let (data, response) = try? await session.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200,
              let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let id = object["pushId"] as? String, isValid(pushId: id) else { return nil }
        return id
    }
}

/// APNs belirteci geri çağrıları için (SwiftUI App yaşam döngüsünde yalnız bu iki çağrı AppDelegate ister).
@MainActor
final class PushAppDelegate: NSObject, UIApplicationDelegate {
    static var onToken: ((Data) -> Void)?
    static var onFailure: (() -> Void)?

    func application(_ application: UIApplication, didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data) {
        Self.onToken?(deviceToken)
    }

    func application(_ application: UIApplication, didFailToRegisterForRemoteNotificationsWithError error: Error) {
        Self.onFailure?()
    }
}
#endif
