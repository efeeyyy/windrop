import Foundation
import UserNotifications

/// Yerel bildirimler (PROTOCOL §7): arka planda gelen `offer` -> bildirim (Kabul / Reddet eylemleri).
/// - Kabul: `.foreground` eylemi; uygulama öne gelir, teklif kabul edilir, aktarım başlar.
/// - Reddet: arka plan eylemi; uygulama kısa süre uyanır, `reject` BLE oturumundan gönderilir (arka plan görevi içinde).
/// - Gövdeye dokunma: uygulama açılır, onay sayfası görünür.
@MainActor
final class NotificationManager: NSObject, UNUserNotificationCenterDelegate, OfferNotifying {
    static let offerCategory = "OFFER"
    static let acceptAction = "OFFER_ACCEPT"
    static let rejectAction = "OFFER_REJECT"

    /// Bildirimin gövdesine dokunulunca (transferId varsa) çağrılır.
    var onOpen: ((String?) -> Void)?
    /// "Kabul" eylemi (uygulama öne geliyor).
    var onAccept: ((String) -> Void)?
    /// "Reddet" eylemi: `reject` gönderilene kadar döner.
    var onReject: ((String) async -> Void)?
    /// Arka plan görevi (AppModel doldurur): bildirim ekleme ve ret gönderimi uyku öncesi bitsin.
    var beginBackgroundTask: BackgroundTaskStarter = { _ in {} }

    override init() {
        super.init()
        let center = UNUserNotificationCenter.current()
        center.delegate = self
        // Kategori kaydı izin istemi çıkarmaz.
        let accept = UNNotificationAction(identifier: Self.acceptAction, title: "Kabul", options: [.foreground])
        let reject = UNNotificationAction(identifier: Self.rejectAction, title: "Reddet", options: [])
        let category = UNNotificationCategory(
            identifier: Self.offerCategory, actions: [accept, reject], intentIdentifiers: [], options: [])
        center.setNotificationCategories([category])
    }

    func requestAuthorizationIfNeeded() async {
        let center = UNUserNotificationCenter.current()
        let settings = await center.notificationSettings()
        guard settings.authorizationStatus == .notDetermined else { return }
        _ = try? await center.requestAuthorization(options: [.alert, .sound, .badge])
    }

    func postOffer(transferId: String, peerName: String, summary: String) {
        let content = UNMutableNotificationContent()
        content.title = "\(peerName) dosya göndermek istiyor"
        content.body = summary
        content.sound = .default
        content.categoryIdentifier = Self.offerCategory
        content.userInfo = ["transferId": transferId]
        let request = UNNotificationRequest(identifier: "offer-\(transferId)", content: content, trigger: nil)
        let endTask = beginBackgroundTask("com.efe.windrop.notify")
        Task {
            try? await UNUserNotificationCenter.current().add(request)
            endTask()
        }
    }

    func clear(transferId: String) {
        let center = UNUserNotificationCenter.current()
        center.removeDeliveredNotifications(withIdentifiers: ["offer-\(transferId)"])
        center.removePendingNotificationRequests(withIdentifiers: ["offer-\(transferId)"])
    }

    // MARK: Eylemler

    private func handle(action: String, transferId: String?) async {
        switch action {
        case Self.acceptAction:
            if let transferId { onAccept?(transferId) }
        case Self.rejectAction:
            guard let transferId else { return }
            let endTask = beginBackgroundTask("com.efe.windrop.reject")
            await onReject?(transferId)
            endTask()
        default:
            onOpen?(transferId)   // UNNotificationDefaultActionIdentifier: gövdeye dokunuldu
        }
    }

    // MARK: UNUserNotificationCenterDelegate

    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification
    ) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }

    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse
    ) async {
        let transferId = response.notification.request.content.userInfo["transferId"] as? String
        let action = response.actionIdentifier
        await self.handle(action: action, transferId: transferId)
    }
}
