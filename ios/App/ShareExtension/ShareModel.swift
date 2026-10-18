import Foundation
import Observation
import UIKit

/// Paylaşım Uzantısı'nın durumu: kendi (küçük) BLE + aktarım yığınını kurar. Kimlik: App Group varsa ana uygulamayla ortak
/// cihaz anahtarı ve tanıdık liste (`IdentityStore`); grup yoksa uzantının kendi kimliği olur. Yalnız ekran açıkken bağlanır.
/// Bilinmeyen PC: XX + doğrulama kodu; alıcı PC'de onay gerekir (PROTOCOL §2b).
@MainActor
@Observable
final class ShareModel {
    enum Phase: Equatable {
        case loading
        case choosing
        case sending
        case failed(String)
    }

    static let maxFiles = 100

    private(set) var phase: Phase = .loading
    private(set) var totalCount = 0
    private(set) var loadedCount = 0
    private(set) var skippedCount = 0
    private(set) var item: TransferItem?
    var errorText: String?

    let hub: SignalHub
    private let transfers: TransferCoordinator
    private let network: NetworkMonitor
    private let audio: BluetoothAudioMonitor
    private var providers: [NSItemProvider]
    private var files: [OutgoingFile] = []
    @ObservationIgnored private var started = false
    @ObservationIgnored private var finishing = false
    @ObservationIgnored private var isShutDown = false
    @ObservationIgnored var onFinish: (@MainActor () -> Void)?
    @ObservationIgnored var onCancel: (@MainActor () -> Void)?

    init(providers: [NSItemProvider]) {
        self.providers = Array(providers.prefix(Self.maxFiles))
        let identity = IdentityStore()
        let network = NetworkMonitor()
        let audio = BluetoothAudioMonitor()
        let helloInfo: @MainActor () -> HelloInfo = {
            HelloInfo(
                name: DeviceInfo.name(), wifiConnected: network.isWifiConnected,
                btAudio: audio.currentValue())
        }
        let hub = SignalHub(identity: identity, helloInfo: helloInfo)
        let transfers = TransferCoordinator(identity: identity, signals: hub, notifications: NoNotifications())
        transfers.rejectsIncomingOffers = true
        transfers.isWifiConnected = { network.isWifiConnected }
        transfers.isBluetoothAudioActive = { audio.currentValue() }
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
        self.network = network
        self.audio = audio
        self.hub = hub
        self.transfers = transfers
        self.totalCount = self.providers.count
    }

    /// Görünüm açılınca: yakındaki PC'ler taranır (Bluetooth izni burada istenebilir), dosyalar sırayla hazırlanır.
    func start() async {
        guard !started, !isShutDown else { return }
        started = true
        network.start()
        audio.start()   // yalnız okur
        hub.startNearby()
        LocalNetworkPermission.shared.trigger()
        guard !providers.isEmpty else {
            phase = .failed("Paylaşılacak dosya bulunamadı.")
            return
        }
        for provider in providers {
            do {
                files.append(contentsOf: try await ShareItemLoader.loadFiles(from: provider))
            } catch {
                skippedCount += 1
            }
            loadedCount += 1
        }
        providers = []
        for index in files.indices { files[index].id = index }
        if files.isEmpty {
            phase = .failed("Dosyalar okunamadı.")
        } else {
            phase = .choosing
        }
    }

    func send(to peer: NearbyPeer) {
        guard phase == .choosing else { return }
        guard let newItem = transfers.startSending(files: files, to: peer) else {
            errorText = "\(peer.name) şu an yakında değil. Tekrar deneyin."
            return
        }
        errorText = nil
        files = []   // artık aktarım öğesinin (bitince temizler)
        item = newItem
        phase = .sending
    }

    /// Aktarım öğesi bitti mi (başarı: kısa süre gösterilip kapanır; hata: kullanıcı kapatır).
    func itemStateChanged() {
        guard let item, item.isFinished, !finishing else { return }
        if item.state == .done {
            finishing = true
            Task {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                finish()
            }
        }
    }

    /// Aktarma yolu kota uyarısı onaylandı (ayar okunabiliyorsa; uzantıda `relayUrl` genelde boştur = yol kapalı).
    func confirmRelay() {
        if let item { transfers.confirmRelay(item) }
    }

    /// "Vazgeç" / "Kapat".
    func cancel() {
        if let item, !item.isFinished {
            transfers.cancel(item)
        }
        FilePreparer.release(files)
        files = []
        shutdown()
        onCancel?()
    }

    private func finish() {
        shutdown()
        onFinish?()
    }

    /// Uzantı ekranı kapanırken (tamamlandı / vazgeçildi / ekran kayboldu): tarama durur ve tüm Bluetooth bağlantıları
    /// iptal edilir; bir daha bağlanılmaz. Her çıkış yolundan güvenle birden çok kez çağrılabilir.
    func shutdown() {
        isShutDown = true
        hub.shutdown()
    }
}
