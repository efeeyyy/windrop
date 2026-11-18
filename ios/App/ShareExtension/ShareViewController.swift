import SwiftUI
import UIKit

/// Paylaşım menüsü uzantısının ana sınıfı (Info.plist `NSExtensionPrincipalClass`). SwiftUI arayüzü barındırır.
/// Uzantı YALNIZ ekranı açıkken Bluetooth'a bağlanır: her çıkış yolunda (tamamlandı, vazgeçildi, ekran kapandı)
/// `teardown()` tüm bağlantıları iptal eder ve Darwin "inactive" sinyalini gönderir (PROTOCOL §4.4).
@objc(ShareViewController)
final class ShareViewController: UIViewController {
    private var model: ShareModel?
    private var heartbeat: Timer?
    private var tornDown = false

    override func viewDidLoad() {
        super.viewDidLoad()
        ShareActivitySignal.post(active: true)   // ana uygulama BLE bağlantılarını bırakır (PROTOCOL §4.4)
        // Ana uygulama "active" sinyalini 2 dk sonra (uzantı çökmüş olabilir) zaman aşımıyla bırakır: açıkken tazele.
        heartbeat = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] timer in
            MainActor.assumeIsolated {
                guard let self, !self.tornDown else {
                    timer.invalidate()
                    return
                }
                ShareActivitySignal.post(active: true)
            }
        }

        let items = extensionContext?.inputItems as? [NSExtensionItem] ?? []
        let providers = items.flatMap { $0.attachments ?? [] }
        let model = ShareModel(providers: providers)
        self.model = model
        model.onFinish = { [weak self] in
            self?.teardown()
            self?.extensionContext?.completeRequest(returningItems: nil, completionHandler: nil)
        }
        model.onCancel = { [weak self] in
            self?.teardown()
            self?.extensionContext?.cancelRequest(withError: CocoaError(.userCancelled))
        }

        let host = UIHostingController(rootView: ShareView(model: model))
        addChild(host)
        host.view.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(host.view)
        NSLayoutConstraint.activate([
            host.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            host.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            host.view.topAnchor.constraint(equalTo: view.topAnchor),
            host.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        host.didMove(toParent: self)
    }

    override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        teardown()
    }

    /// Tüm Bluetooth bağlantılarını ve taramayı bırakır, "inactive" sinyalini gönderir. Yinelenen çağrı zararsız.
    private func teardown() {
        heartbeat?.invalidate()
        heartbeat = nil
        if !tornDown {
            tornDown = true
            model?.shutdown()
        }
        ShareActivitySignal.post(active: false)
    }

    deinit {
        ShareActivitySignal.post(active: false)
    }
}
