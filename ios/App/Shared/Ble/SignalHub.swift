import CoreBluetooth
import Foundation
import Observation
@preconcurrency import WinDropKit

/// `hello`/`status` için yerel durum.
struct HelloInfo {
    var name: String
    var wifiConnected: Bool
    var btAudio: Bool

    var helloMessage: HelloMessage {
        HelloMessage(
            v: ProtocolIds.helloVersion, name: name, caps: ["tcp", "hotspot"],
            wifi: wifiConnected ? "connected" : "none", btAudio: btAudio)
    }

    var statusMessage: StatusMessage {
        StatusMessage(wifi: wifiConnected ? "connected" : "none", btAudio: btAudio)
    }
}

/// Arka plan görevi başlatıcı: dönen kapatıcı çağrılınca görev biter (birden çok çağrı güvenli).
/// Uygulama `UIApplication.beginBackgroundTask` ile doldurur; Paylaşım Uzantısı'nda API yok (varsayılan: boş işlem).
typealias BackgroundTaskStarter = @MainActor (_ name: String) -> (@MainActor () -> Void)

/// Yakındaki (BLE oturumu kurulmuş) bir PC. `id` = açık anahtar kimliği (hex, Kit `DeviceId`).
struct NearbyPeer: Identifiable, Equatable, Sendable {
    var id: String
    /// Tanıdıksa kayıttaki ad; değilse karşının `hello.name` değeri.
    var name: String
    var publicKey: Data
    var peripheralID: UUID
    var isKnown: Bool
    var autoAccept: Bool
    /// Karşının Bluetooth ses durumu (`hello`/`status`); bilinmiyorsa true (BLE yolu için temkinli varsayım).
    var remoteBtAudio = true
    /// BLE oturumu IK ile mi kuruldu (veri kanalı aynı deseni kullanır; PROTOCOL §2b).
    var usedIK: Bool
    /// 4 haneli doğrulama kodu (el sıkışma özetinden; iki ekranda aynı olmalı).
    var code: String
}

/// BLE oturumu üstünde küçük dosya aktarımı için kanal (PROTOCOL §6.4): Kit kanalı + okuma döngüsünün
/// ayırdığı çerçeveleri veren okuyucu (kanalı doğrudan okumak oturum okuma döngüsüyle yarışır).
struct BleDataSession {
    let channel: SecureChannel
    let nextFrame: FrameReader
}

/// Aktarım katmanının sinyal gönderme ihtiyacı (dar arayüz).
@MainActor
protocol SignalSending: AnyObject {
    func isReady(_ keyID: String) -> Bool
    func peer(_ keyID: String) -> NearbyPeer?
    func send(_ message: SignalMessage, to keyID: String) async throws
    /// Bilinmeyen PC'yi (kullanıcı onayından sonra) "tanıdık" olarak saklar. Reddedilen saklanmaz.
    func remember(_ keyID: String)
    /// BLE veri aktarımı başlatır: sonraki çerçeveler `BleDataSession.nextFrame`'e yönlenir. Oturum yoksa nil.
    func beginBleTransfer(_ keyID: String) -> BleDataSession?
    func endBleTransfer(_ keyID: String)
}

enum SignalError: Error, LocalizedError {
    case notConnected

    var errorDescription: String? {
        "Bilgisayar şu an yakında değil (Bluetooth bağlantısı yok)."
    }
}

/// BLE oturumlarını yönetir (PROTOCOL §2b): tarama -> bağlan -> IK (tanıdık) veya XX -> `hello` -> ileti okuma döngüsü.
/// CBCentralManager tembeldir: `start()` çağrılana (Gönder / izin verilmişse ön plan) kadar hiçbir izin istenmez.
@MainActor
@Observable
final class SignalHub: BleCentralDelegate, SignalSending {
    /// Açık BLE oturumu: Kit kanalı + sinyal yazmalarını sıralayan kilit.
    private final class PeerChannel {
        let channel: SecureChannel
        let link: BlePeripheralLink
        let lock = AsyncLock()
        /// Etkin BLE veri aktarımının çerçeve kuyruğu (yoksa nil).
        var frames: AsyncMailbox<Frame>?
        init(channel: SecureChannel, link: BlePeripheralLink) {
            self.channel = channel
            self.link = link
        }
    }

    private(set) var peers: [String: NearbyPeer] = [:]
    private(set) var bluetoothState: CBManagerState = .unknown
    private(set) var hasStarted = false
    private(set) var isSearching = false

    @ObservationIgnored var onMessage: ((SignalMessage, NearbyPeer) -> Void)?
    @ObservationIgnored var onPeerGone: ((String) -> Void)?
    @ObservationIgnored var beginBackgroundTask: BackgroundTaskStarter = { _ in {} }
    @ObservationIgnored let central = BleCentral()
    @ObservationIgnored private var channels: [String: PeerChannel] = [:]
    @ObservationIgnored private var connecting: Set<UUID> = []
    @ObservationIgnored private var forceXX: Set<UUID> = []
    @ObservationIgnored private var pairingDepth = 0
    @ObservationIgnored private let identity: IdentityStore
    @ObservationIgnored private let helloInfo: @MainActor () -> HelloInfo

    init(identity: IdentityStore, helloInfo: @escaping @MainActor () -> HelloInfo) {
        self.identity = identity
        self.helloInfo = helloInfo
        central.delegate = self
    }

    /// Yakındaki listesi: tanıdıklar önce, sonra ada göre.
    var nearbyList: [NearbyPeer] {
        peers.values.sorted { ($0.isKnown ? 0 : 1, $0.name) < ($1.isKnown ? 0 : 1, $1.name) }
    }

    // MARK: Başlatma (tembel)

    /// Merkezi başlatır (ilk seferde Bluetooth izin istemi çıkar). Gönder dokunuşunda ve izin verilmişse uygulama öne gelince çağrılır.
    func start() {
        hasStarted = true
        central.start()
        syncWanted()
    }

    /// Açılışta YALNIZ Bluetooth izni zaten verilmişse ve tanıdık PC varsa (izin istemi çıkmaz): durum geri yükleme
    /// ve bekleyen bağlantılar için merkezi kurar (durum geri yükleme; ProtocolIds.restoreIdentifier).
    func startIfAlreadyAuthorized() {
        guard CBCentralManager.authorization == .allowedAlways, !identity.knownPCs.isEmpty else { return }
        start()
    }

    func startNearby() {
        start()
        isSearching = true
        central.startNearbyScan()
    }

    func stopNearby() {
        isSearching = false
        central.stopNearbyScan()
    }

    /// QR eşleştirme boyunca tarama/otomatik bağlanma duraklatılır.
    func beginPairing() -> BleCentral {
        pairingDepth += 1
        central.start()
        central.setPaused(true)
        return central
    }

    func endPairing() {
        pairingDepth = max(0, pairingDepth - 1)
        if pairingDepth == 0 { central.setPaused(false) }
    }

    private var isPairing: Bool { pairingDepth > 0 }

    // MARK: SignalSending

    func isReady(_ keyID: String) -> Bool { channels[keyID] != nil }

    func peer(_ keyID: String) -> NearbyPeer? { peers[keyID] }

    func send(_ message: SignalMessage, to keyID: String) async throws {
        guard let pc = channels[keyID] else { throw SignalError.notConnected }
        try await pc.lock.withLock {
            try await pc.channel.sendSignal(message)
        }
    }

    func remember(_ keyID: String) {
        guard var peer = peers[keyID], !peer.isKnown else { return }
        let pc = KnownPC(
            id: keyID, name: peer.name, publicKey: peer.publicKey, peripheralID: peer.peripheralID,
            addedAt: Date(), autoAccept: false)
        guard (try? identity.upsert(pc)) != nil else { return }
        peer.isKnown = true
        peers[keyID] = peer
        syncWanted()
        #if PUSH_ENABLED
        sendPushId(to: keyID)   // yeni tanıdık oldu
        #endif
    }

    func beginBleTransfer(_ keyID: String) -> BleDataSession? {
        guard let pc = channels[keyID], pc.frames == nil else { return nil }
        let mailbox = AsyncMailbox<Frame>()
        pc.frames = mailbox
        let reader: FrameReader = {
            do { return try await mailbox.pop(timeout: nil) } catch { return nil }   // kopma/bitiş = temiz kapanış
        }
        return BleDataSession(channel: pc.channel, nextFrame: reader)
    }

    func endBleTransfer(_ keyID: String) {
        guard let pc = channels[keyID], let mailbox = pc.frames else { return }
        pc.frames = nil
        mailbox.fail(MailboxError.closed)   // bekleyen okuyucuyu uyandırır
    }

    /// Ağ/ses durumu değişince `status` gönderir (PROTOCOL §4).
    func broadcastStatus() {
        let status = helloInfo().statusMessage
        for keyID in channels.keys {
            Task { try? await send(.status(status), to: keyID) }
        }
    }

    #if PUSH_ENABLED
    /// Push kaydı kimliği (PROTOCOL §6.5); nil = kayıt yok / ayar kapalı. AppModel doldurur.
    @ObservationIgnored var pushIdProvider: (@MainActor () -> String?)?

    /// `pushId` yalnız tanıdık/eşli PC'ye verilir (tanıdık olmayana asla).
    func sendPushId(to keyID: String) {
        guard peers[keyID]?.isKnown == true, let id = pushIdProvider?() else { return }
        Task { try? await send(.pushId(PushIdMessage(pushId: id)), to: keyID) }
    }

    func broadcastPushId() {
        for keyID in channels.keys { sendPushId(to: keyID) }
    }
    #endif

    // MARK: Tanıdık liste değişimi

    func syncWanted() {
        central.setWanted(Set(identity.knownPCs.compactMap(\.peripheralID)))
    }

    func forget(_ pc: KnownPC) {
        if let channel = channels[pc.id] { central.closeLink(channel.link.peripheralID) }
        channels[pc.id] = nil
        peers[pc.id] = nil
        if let id = pc.peripheralID { central.closeLink(id) }
        identity.remove(pc)
        syncWanted()
    }

    // MARK: BleCentralDelegate

    func bleCentral(_ central: BleCentral, didUpdateState state: CBManagerState) {
        bluetoothState = state
        if state != .poweredOn {
            channels.values.forEach { $0.frames?.fail(BleError.disconnected) }
            channels.removeAll()
            let gone = Array(peers.keys)
            peers.removeAll()
            gone.forEach { onPeerGone?($0) }
        }
    }

    func bleCentral(_ central: BleCentral, didOpen link: BlePeripheralLink) {
        guard !isPairing, !connecting.contains(link.peripheralID),
              identity.pc(withPeripheral: link.peripheralID) != nil else {
            central.closeLink(link.peripheralID)
            return
        }
        connecting.insert(link.peripheralID)
        Task { [weak self] in await self?.runSession(link: link) }
    }

    func bleCentral(_ central: BleCentral, didClose peripheralID: UUID) {
        // Okuma döngüsü kendi temizliğini yapar (inbox hataya düşer).
    }

    func bleCentral(_ central: BleCentral, didDiscover peripheral: CBPeripheral) {
        let id = peripheral.identifier
        guard !isPairing, !connecting.contains(id), !peers.values.contains(where: { $0.peripheralID == id }) else { return }
        connecting.insert(id)
        Task { [weak self] in
            guard let self else { return }
            do {
                let link = try await self.central.openLink(to: peripheral, timeout: 10)
                await self.runSession(link: link)
            } catch {
                self.connecting.remove(id)   // bir sonraki tarama turunda yeniden denenir
            }
        }
    }

    // MARK: Oturum

    private func runSession(link: BlePeripheralLink) async {
        let pid = link.peripheralID
        // Arka planda uyandırıldıysak IK/XX + hello bitene kadar süre iste (uzantıda yok sayılır).
        let endHandshakeTask = beginBackgroundTask("com.efe.windrop.handshake")
        var keyID: String?
        do {
            let localKey = try identity.deviceKey()
            let transport = BleMessageTransport(link: BleLinkAdapter(link: link))
            let channel: SecureChannel
            var usedIK = false
            if let known = identity.pc(withPeripheral: pid), !forceXX.contains(pid) {
                do {
                    channel = try await SecureChannel.connectIK(
                        transport: transport, prefix: Prefix.ble(), localKey: localKey, remoteStaticKey: known.publicKey)
                    usedIK = true
                } catch {
                    forceXX.insert(pid)   // bağlantı yeniden kurulunca XX denenir (PC anahtarı değişmiş olabilir)
                    throw error
                }
            } else {
                forceXX.remove(pid)
                channel = try await SecureChannel.connectXX(transport: transport, prefix: Prefix.ble(), localKey: localKey)
            }

            let kid = DeviceId.hex(publicKey: channel.remoteStaticKey)
            keyID = kid
            let known = identity.pc(withKeyID: kid)
            if let known, known.peripheralID != pid {
                var updated = known
                updated.peripheralID = pid
                try? identity.upsert(updated)
                syncWanted()
            }
            let peer = NearbyPeer(
                id: kid, name: known?.name ?? "Bilgisayar", publicKey: channel.remoteStaticKey, peripheralID: pid,
                isKnown: known != nil, autoAccept: known?.autoAccept ?? false, usedIK: usedIK,
                code: channel.verificationCode)
            channels[kid] = PeerChannel(channel: channel, link: link)
            peers[kid] = peer
            try await send(.hello(helloInfo().helloMessage), to: kid)
            endHandshakeTask()   // el sıkışma + hello bitti; okuma döngüsü arka plan görevi tutmaz
            #if PUSH_ENABLED
            sendPushId(to: kid)
            #endif

            while let message = try await channel.receiveMessage() {
                guard case .signal(let signal) = message else {
                    // BLE çerçevesi (PROTOCOL §6.4a): yalnız `bleData` ile başlayan aktarım kuyruğa alınır, başıboş atılır.
                    if case .frame(let frame) = message, let pc = channels[kid], pc.channel === channel {
                        pc.frames?.push(frame)
                    }
                    continue
                }
                if case .hello(let hello) = signal, var current = peers[kid], !current.isKnown {
                    let name = hello.name.trimmingCharacters(in: .whitespacesAndNewlines)
                    if !name.isEmpty {
                        current.name = String(name.prefix(64))
                        peers[kid] = current
                    }
                }
                switch signal {
                case .hello(let hello): peers[kid]?.remoteBtAudio = hello.btAudio
                case .status(let status): peers[kid]?.remoteBtAudio = status.btAudio
                default: break
                }
                if let current = peers[kid] { onMessage?(signal, current) }
            }
        } catch {
            // Hata/kopma: oturum düşer; bağlantı kapatılır (tanıdık PC'lerde merkez yeniden bağlanır).
        }
        endHandshakeTask()
        if let keyID, channels[keyID]?.link === link {
            channels[keyID]?.frames?.fail(BleError.disconnected)
            channels[keyID] = nil
            peers[keyID] = nil
            onPeerGone?(keyID)
        }
        connecting.remove(pid)
        if link.isOpen { central.closeLink(pid) }
    }
}
