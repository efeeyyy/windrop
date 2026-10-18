import Foundation
import Network

/// iOS "Yerel Ağ" izni. İzin sorusu ilk yerel TCP bağlantısında çıkarsa bağlantı yanıta kadar bekler ve aktarım
/// zaman aşımına uğrar; bu yüzden soru, kullanıcı ilk kez "Dosya gönder"e bastığında (veya gelen teklifi kabul
/// ettiğinde) `NWBrowser` ile Info.plist'teki `_windrop._tcp` Bonjour türüne kısa süreli göz atılarak erkenden çıkarılır.
final class LocalNetworkPermission: @unchecked Sendable {
    enum State: Sendable { case unknown, granted, denied }

    static let shared = LocalNetworkPermission()
    static let bonjourType = "_windrop._tcp"
    static var deniedMessage: String {
        String(localized: "WinDrop'un yerel ağ izni kapalı. Ayarlar > Gizlilik ve Güvenlik > Yerel Ağ'dan açın.")
    }
    /// kDNSServiceErr_PolicyDenied
    private static let policyDenied: Int32 = -65570

    private let lock = NSLock()
    private var _state: State = .unknown
    private var browser: NWBrowser?
    private let queue = DispatchQueue(label: "com.efe.windrop.localnetwork")

    var state: State {
        lock.lock(); defer { lock.unlock() }
        return _state
    }

    /// İzin sorusunu tetikler (idempotent; izin biliniyorsa bir şey yapmaz). Kullanıcı dokunuşundan çağrılmalı.
    func trigger() {
        lock.lock()
        if browser != nil || _state == .granted {
            lock.unlock()
            return
        }
        let browser = NWBrowser(
            for: .bonjour(type: Self.bonjourType, domain: nil), using: NWParameters())
        self.browser = browser
        lock.unlock()
        browser.stateUpdateHandler = { [weak self] newState in
            self?.handle(newState)
        }
        browser.start(queue: queue)
        // İzin kararı verildikten sonra göz atmaya gerek yok; yanıt gelmezse de 60 sn sonra bırak.
        queue.asyncAfter(deadline: .now() + 60) { [weak self] in self?.stopBrowser() }
    }

    /// `NWConnection` `.waiting` hatası yerel ağ reddi mi?
    static func isPolicyDenied(_ error: NWError) -> Bool {
        if case .dns(let code) = error { return code == policyDenied }
        return false
    }

    func markDenied() { set(.denied) }

    private func handle(_ newState: NWBrowser.State) {
        switch newState {
        case .ready:
            set(.granted)
            stopBrowser()
        case .waiting(let error):
            if Self.isPolicyDenied(error) {
                set(.denied)
                stopBrowser()
            }
        case .failed:
            stopBrowser()
        default:
            break
        }
    }

    private func set(_ new: State) {
        lock.lock()
        _state = new
        lock.unlock()
    }

    private func stopBrowser() {
        lock.lock()
        let current = browser
        browser = nil
        lock.unlock()
        current?.stateUpdateHandler = nil
        current?.cancel()
    }
}
