import Foundation

/// Süreçler arası basit sinyal (PROTOCOL §3a): Paylaşım uzantısı açılınca ana uygulama BLE bağlantılarını bırakır,
/// uzantı kapanınca yeniden kurar. Darwin bildirimi (yük taşımaz).
enum ShareActivitySignal {
    static let activeName = "com.efe.windrop.share.active"
    static let inactiveName = "com.efe.windrop.share.inactive"

    /// Uzantı tarafı: açıldı (`true`) / kapandı (`false`).
    static func post(active: Bool) {
        CFNotificationCenterPostNotification(
            CFNotificationCenterGetDarwinNotifyCenter(),
            CFNotificationName((active ? activeName : inactiveName) as CFString), nil, nil, true)
    }

    /// Ana uygulama tarafı: dinleyici ömür boyu kalır. Geri çağrı ana iş parçacığında çalışır.
    @MainActor
    static func observe(_ handler: @escaping @MainActor (_ active: Bool) -> Void) {
        let box = Unmanaged.passRetained(HandlerBox(handler)).toOpaque()
        let center = CFNotificationCenterGetDarwinNotifyCenter()
        for name in [activeName, inactiveName] {
            CFNotificationCenterAddObserver(
                center, box,
                { _, observer, name, _, _ in
                    guard let observer, let name else { return }
                    let box = Unmanaged<HandlerBox>.fromOpaque(observer).takeUnretainedValue()
                    let active = (name.rawValue as String) == ShareActivitySignal.activeName
                    Task { @MainActor in box.handler(active) }
                },
                name as CFString, nil, .deliverImmediately)
        }
    }

    private final class HandlerBox: @unchecked Sendable {
        let handler: @MainActor (Bool) -> Void
        init(_ handler: @escaping @MainActor (Bool) -> Void) { self.handler = handler }
    }
}
