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

    var selectedTab: Tab = .home

    let identity: IdentityStore
    let network: NetworkMonitor
    let audio: BluetoothAudioMonitor
    let notifications: NotificationManager
    let hub: SignalHub
    let transfers: TransferCoordinator
    let pairing: PairingController
    #if PUSH_ENABLED
    let push: PushManager
    #endif

    init() {
        let identity = IdentityStore()
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
        network.onChange = { [weak hub] in
            hub?.broadcastStatus()
        }
        audio.onChange = { [weak hub] in
            hub?.broadcastStatus()
        }
        notifications.onOpen = { [weak self] transferId in
            self?.selectedTab = .home
            #if PUSH_ENABLED
            // Uzaktan (push) bildirimde `transferId` yoktur: PC'leri tara ve bağlan; PC bekleyen teklifi yeniden yollar (§6.5).
            if transferId == nil { self?.startScanFromPush() }
            #endif
        }
        // Arka plan: BLE ile uyanınca IK + hello, bildirim ve ret gönderimi arka plan görevi içinde biter.
        hub.beginBackgroundTask = AppBackgroundTask.starter
        notifications.beginBackgroundTask = AppBackgroundTask.starter
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
        network.start()
        audio.start()   // yalnız okur; ses oturumunu etkinleştirmez
        // İzin zaten verilmişse (ve tanıdık PC varsa) merkez kurulur; izin istemi çıkarmaz.
        hub.startIfAlreadyAuthorized()
        // PROTOCOL §3a: Paylaşım uzantısı açıkken BLE bağlantıları bırakılır, kapanınca yeniden kurulur.
        ShareActivitySignal.observe { [weak hub] active in hub?.central.setYielding(active) }
    }

    #if PUSH_ENABLED
    /// Push bildirimine dokunuldu: uygulama öne geldi; taramayı başlatıp tanıdık PC'lere bağlanır.
    private func startScanFromPush() {
        hub.startNearby()
        hub.central.ensureConnections()
    }
    #endif

    func didBecomeActive() {
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
