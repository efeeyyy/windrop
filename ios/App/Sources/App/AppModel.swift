import CoreBluetooth
import Foundation
import Observation
import UIKit

/// Gönderilecek dosyalar seçildi: yakındaki PC sayfasının girdisi.
struct SendSession: Identifiable {
    let id = UUID()
    let files: [OutgoingFile]
}

/// Uygulamanın kökü: bileşenleri bağlar, sekme durumunu tutar.
/// Açılışta izin/eşleştirme yok: Bluetooth, kamera, fotoğraf ve bildirim izinleri tembeldir.
@MainActor
@Observable
final class AppModel {
    enum Tab: Hashable {
        case home, transfers, settings
    }

    static let deviceNameKey = DeviceInfo.nameKey
    /// Uzantı çökerse "inactive" sinyali gelmez: bırakma bu süre sonra kalkar (uzantı açıkken sinyal tazelenir).
    static let shareYieldTimeoutNanos: UInt64 = 120_000_000_000

    var selectedTab: Tab = .home

    let identity: IdentityStore
    let network: NetworkMonitor
    let audio: BluetoothAudioMonitor
    let notifications: NotificationManager
    let hub: SignalHub
    let transfers: TransferCoordinator
    let pairing: PairingController
    @ObservationIgnored private var shareYieldTimeout: Task<Void, Never>?
    #if PUSH_ENABLED
    let push: PushManager
    #endif

    init() {
        SharedGroup.migrateSettings(keys: [DeviceInfo.nameKey, RelaySettings.key])   // grup öncesi ayarlar gruba taşınır
        let identity = IdentityStore()
        // Ortak alan varken ortak cihaz anahtarı açılışta üretilir (izin istemez): uzantı ilk açılışta ayrı kimlikle başlamasın.
        if identity.usesSharedStore { _ = try? identity.deviceKey() }
        let network = NetworkMonitor()
        let audio = BluetoothAudioMonitor()
        let notifications = NotificationManager()   // delegate atanır, izin istemi çıkmaz
        let helloInfo: @MainActor () -> HelloInfo = {
            HelloInfo(
                name: DeviceInfo.name(), wifiConnected: network.isWifiConnected,
                btAudio: audio.currentValue())
        }
        let hub = SignalHub(identity: identity, helloInfo: helloInfo)
        let transfers = TransferCoordinator(identity: identity, signals: hub, notifications: notifications)
        let pairing = PairingController(identity: identity, hub: hub, helloInfo: helloInfo)

        self.identity = identity
        self.network = network
        self.audio = audio
        self.notifications = notifications
        self.hub = hub
        self.transfers = transfers
        self.pairing = pairing
        #if PUSH_ENABLED
        let push = PushManager(notifications: notifications)
        self.push = push
        hub.pushIdProvider = { [weak push] in push?.pushId }
        push.onPushIdChanged = { [weak hub] in hub?.broadcastPushId() }
        PushAppDelegate.onToken = { [weak push] token in push?.didRegister(deviceToken: token) }
        PushAppDelegate.onFailure = { [weak push] in push?.didFailToRegister() }
        #endif

        hub.onMessage = { [weak transfers] message, peer in
            transfers?.handle(message, from: peer)
        }
        hub.onPeerGone = { [weak transfers] pcID in
            transfers?.peerGone(pcID)
        }
        network.onChange = { [weak hub, weak transfers] in
            hub?.broadcastStatus()   // Wi-Fi bir ağa bağlanınca PC, bekleyen aktarım için `addr` yollar (bekleme sırasında da)
            transfers?.wifiChanged()
        }
        audio.onChange = { [weak hub] in
            hub?.broadcastStatus()
        }
        notifications.onOpen = { [weak self] transferId in
            // Biten aktarım bildirimi: Aktarımlar sekmesi; teklif bildirimi: ana sayfa (onay sayfası).
            if let transferId, self?.transfers.item(withID: transferId)?.state == .done { self?.selectedTab = .transfers } else { self?.selectedTab = .home }
            #if PUSH_ENABLED
            // Uzaktan (push) bildirimde `transferId` yoktur: PC'leri tara ve bağlan; PC bekleyen teklifi yeniden yollar (§6.5).
            if transferId == nil { self?.startScanFromPush() }
            #endif
        }
        // Arka plan: BLE ile uyanınca IK + hello, bildirim ve ret gönderimi arka plan görevi içinde biter.
        hub.beginBackgroundTask = AppBackgroundTask.starter
        notifications.beginBackgroundTask = AppBackgroundTask.starter
        transfers.beginBackgroundTask = AppBackgroundTask.starter
        transfers.isWifiConnected = { network.isWifiConnected }
        transfers.isBluetoothAudioActive = { audio.currentValue() }
        transfers.isAppInBackground = { UIApplication.shared.applicationState != .active }
        notifications.onAccept = { [weak self] transferId in
            self?.selectedTab = .transfers
            self?.transfers.acceptFromNotification(transferId: transferId)
        }
        notifications.onReject = { [weak transfers] transferId in
            await transfers?.rejectFromNotification(transferId: transferId)
        }
        // QR eşleştirme bitince bildirim izni (ilk ön plan etkileşimi): arka planda gelen ilk teklif görünmez kalmasın.
        pairing.onSucceeded = { [weak notifications] in
            Task { await notifications?.requestAuthorizationIfNeeded() }
        }
        network.start()
        audio.start()   // yalnız okur; ses oturumunu etkinleştirmez
        // İzin zaten verilmişse (ve tanıdık PC varsa) merkez kurulur; izin istemi çıkarmaz.
        hub.startIfAlreadyAuthorized()
        // PROTOCOL §3a: Paylaşım uzantısı açıkken BLE bağlantıları bırakılır, kapanınca yeniden kurulur.
        ShareActivitySignal.observe { [weak self] active in self?.shareActivityChanged(active) }
    }

    #if PUSH_ENABLED
    /// Push bildirimine dokunuldu: uygulama öne geldi; taramayı başlatıp tanıdık PC'lere bağlanır.
    private func startScanFromPush() {
        hub.startNearby()
        hub.central.ensureConnections()
    }
    #endif

    /// Paylaşım uzantısı açıldı/kapandı. Açıkken bağlantılar bırakılır; uzantı çökerse "inactive" gelmez, bu yüzden
    /// `shareYieldTimeout` sonra (uzantı açıkken ~30 sn arayla tazelenir) bırakma kendiliğinden kalkar.
    private func shareActivityChanged(_ active: Bool) {
        shareYieldTimeout?.cancel()
        shareYieldTimeout = nil
        hub.central.setYielding(active)
        if active {
            shareYieldTimeout = Task { [weak self] in
                try? await Task.sleep(nanoseconds: Self.shareYieldTimeoutNanos)
                guard !Task.isCancelled else { return }
                self?.shareYieldTimeout = nil
                self?.hub.central.setYielding(false)
            }
        } else {
            // Uzantı yeni tanıdık PC ekledi/çıkarmış olabilir (ortak kimlik deposu).
            identity.reload()
            hub.syncWanted()
        }
    }

    func didBecomeActive() {
        // Uygulama öndeyken başka bir uygulamanın paylaşım sayfası açık olamaz: "inactive" sinyali kaçırılmış (askıdayken
        // Darwin bildirimi ulaşmaz) olsa da bırakma kalkar; aksi halde yakındaki listesi 120 sn boş kalırdı.
        shareYieldTimeout?.cancel()
        shareYieldTimeout = nil
        hub.central.setYielding(false)
        identity.reload()   // uzantı değişikliklerini al (ortak depo)
        #if PUSH_ENABLED
        push.refresh()   // APNs kaydını tazele (belirteç değişmiş olabilir)
        #endif
        // Bluetooth izni zaten verilmişse alma dinlemesi kendiliğinden başlar; verilmemişse (ilk açılış)
        // izin istemi çıkarmamak için bekler (ilk "Dosya gönder" akışında istenir).
        guard hub.hasStarted || CBCentralManager.authorization == .allowedAlways else { return }
        hub.startNearby()
        hub.central.ensureConnections()
    }

    func didResignActive() {
        hub.stopNearby()   // tarama yalnız uygulama açıkken; tanıdık PC bekleyen bağlantıları sürer
    }

    func removeKnown(_ pc: KnownPC) {
        hub.forget(pc)
        // Not: PC tarafında da silinmeli; PC bir sonraki IK denemesinde reddeder (PROTOCOL §0).
    }
}
