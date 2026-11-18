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
    /// BLE oturumu IK ile mi kuruldu (veri kanalı aynı deseni kullanır; PROTOCOL §3.1).
    var usedIK: Bool
    /// 4 haneli doğrulama kodu (el sıkışma özetinden; iki ekranda aynı olmalı).
    var code: String
}

/// BLE oturumu üstünde küçük dosya aktarımı için kanal (PROTOCOL §7.3): Kit kanalı + okuma döngüsünün
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
    /// Bilinmeyen PC'yi (kullanıcı onayından sonra) "tanıdık" olarak saklar. Reddedilen saklanmaz. Anlık görüntüyle kaydeder:
    /// uzun (TCP) aktarımın sonunda BLE oturumu düşmüş olsa da kayıt yapılır.
    func remember(_ peer: NearbyPeer)
    /// BLE veri aktarımı başlatır: sonraki çerçeveler `BleDataSession.nextFrame`'e yönlenir. Oturum yoksa nil.
    func beginBleTransfer(_ keyID: String) -> BleDataSession?
    func endBleTransfer(_ keyID: String)
    /// Ölü olabilecek BLE oturumunu kapatıp yeniden kurar; yeni oturum hazır olursa true (en çok ~15 sn).
    func refreshSession(_ keyID: String) async -> Bool
}

enum SignalError: Error, LocalizedError {
    case notConnected

    var errorDescription: String? {
        String(localized: "Bilgisayar şu an yakında değil (Bluetooth bağlantısı yok).")
    }
}

/// BLE oturumlarını yönetir (PROTOCOL §3.1): tarama -> bağlan -> IK (tanıdık) veya XX -> `hello` -> ileti okuma döngüsü.
/// CBCentralManager tembeldir: `start()` çağrılana (Gönder / izin verilmişse ön plan) kadar hiçbir izin istenmez.
@MainActor
@Observable
final class SignalHub: BleCentralDelegate, SignalSending {
    /// Açık BLE oturumu: Kit kanalı + sinyal yazmalarını sıralayan kilit.
    private final class PeerChannel {
        let channel: SecureChannel
        let link: BlePeripheralLink
        let lock = AsyncLock()
        let since = Date()
        /// Etkin BLE veri aktarımının çerçeve kuyruğu (yoksa nil).
        var frames: AsyncMailbox<Frame>?
        init(channel: SecureChannel, link: BlePeripheralLink) {
            self.channel = channel
            self.link = link
        }
    }

    /// Çevre birimi başına oturum durumu (PROTOCOL §3.1): aynı GATT bağlantısına aynı anda tek oturum yazar.
    private enum SessionPhase {
        case idle, connecting, handshaking, established, closing
    }

    /// Çevre birimi kimliği başına TEK oturum sahibi. Tüm başlatma yolları (`didOpen`, `didDiscover`) `requestSession`
    /// kapısından geçer; yinelenen istekler birleşir, sürüyorsa beklenir. Yeni oturum, eskisi bitmeden başlamaz.
    private final class SessionSlot {
        var phase: SessionPhase = .idle
        /// Şu anki oturumun bağlantısı (bağlanma aşamasında nil).
        var link: BlePeripheralLink?
        /// Eski oturum bitince çalıştırılacak, daha yeni bağlantı (aynı anda ikinci oturum açmak yerine sıraya girer).
        var nextLink: BlePeripheralLink?
        var task: Task<Void, Never>?
    }

    private enum SessionRequest {
        case opened(BlePeripheralLink)          // merkez eşli PC'ye bağlandı (otomatik yeniden bağlanma dahil)
        case discovered(CBPeripheral)           // yakındaki taramada bulundu
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
    @ObservationIgnored private var slots: [UUID: SessionSlot] = [:]
    /// Eşleştirme sürerken gelen açık bağlantılar: eşleştirme bitince (hâlâ açıksa) oturuma çevrilir.
    @ObservationIgnored private var deferredLinks: [UUID: BlePeripheralLink] = [:]
    /// Çevre birimi başına tek `refreshSession` (teklif zaman aşımları birleşir).
    @ObservationIgnored private var refreshTasks: [String: Task<Bool, Never>] = [:]
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

    /// Paylaşım Uzantısı kapanırken (tamamlandı / vazgeçildi / ekran kapandı): tarama durur, tüm oturumlar ve
    /// çevre birimi bağlantıları iptal edilir, bir daha yeniden bağlanılmaz. Yinelenen çağrı zararsızdır.
    func shutdown() {
        isSearching = false
        for (pid, slot) in slots {
            slot.nextLink = nil
            interrupt(pid, slot)
        }
        channels.values.forEach { $0.frames?.fail(BleError.disconnected) }
        channels.removeAll()
        let gone = Array(peers.keys)
        peers.removeAll()
        central.shutdown()
        gone.forEach { onPeerGone?($0) }
    }

    /// QR eşleştirme boyunca tarama/otomatik bağlanma duraklatılır.
    func beginPairing() -> BleCentral {
        pairingDepth += 1
        central.start()
        central.setPaused(true)
        return central
    }

    /// Eşleştirme bu çevre birimini kullanmadan önce: oturum sahibi sıfırlanır, varsa o çevre birimindeki hub oturumu
    /// iptal edilip bitmesi (okuyucu + yazıcılar durana dek, en çok ~3 sn) beklenir. Eşleştirme boyunca
    /// `isPairing` kapısı o çevre birimi için yeni oturum başlatmayı reddeder.
    func claimForPairing(_ peripheralID: UUID) async {
        deferredLinks[peripheralID] = nil
        await terminateSession(peripheralID)
    }

    /// Eşleştirme bitti (başarılı ya da başarısız): otomatik yeniden bağlanma serbest kalır; eşleştirme sırasında
    /// gelip bekletilen açık bağlantılar oturuma çevrilir.
    func endPairing() {
        pairingDepth = max(0, pairingDepth - 1)
        guard pairingDepth == 0 else { return }
        central.setPaused(false)
        let waiting = deferredLinks
        deferredLinks.removeAll()
        for (pid, link) in waiting where link.isOpen {
            requestSession(pid, .opened(link))
        }
    }

    private var isPairing: Bool { pairingDepth > 0 }

    /// Eşleştirilecek PC zaten bağlı mı? `match` = karşı statik anahtarı `publicKey` olan oturumun (ya da kayıtlı
    /// PC'nin) çevre birimi; `others` = başka PC'lerin oturumlarına ait çevre birimleri.
    func pairingTarget(publicKey: Data) -> (match: UUID?, others: Set<UUID>) {
        var match: UUID?
        var others: Set<UUID> = []
        for peer in peers.values {
            if peer.publicKey == publicKey { match = peer.peripheralID } else { others.insert(peer.peripheralID) }
        }
        if match == nil, let pid = identity.pc(withKeyID: DeviceId.hex(publicKey: publicKey))?.peripheralID {
            match = pid
        }
        if let match { others.remove(match) }
        return (match, others)
    }

    // MARK: SignalSending

    func isReady(_ keyID: String) -> Bool { channels[keyID] != nil }

    func peer(_ keyID: String) -> NearbyPeer? { peers[keyID] }

    func send(_ message: SignalMessage, to keyID: String) async throws {
        guard let pc = channels[keyID] else { throw SignalError.notConnected }
        try await pc.lock.withLock {
            try await pc.channel.sendSignal(message)
        }
    }

    /// Eşzamanlı çağrılar (aynı PC için birden çok teklif zaman aşımı) tek yenilemede birleşir.
    func refreshSession(_ keyID: String) async -> Bool {
        if let running = refreshTasks[keyID] { return await running.value }
        let task = Task { [weak self] () -> Bool in
            guard let self else { return false }
            let renewed = await self.performRefresh(keyID)
            self.refreshTasks[keyID] = nil
            return renewed
        }
        refreshTasks[keyID] = task
        return await task.value
    }

    private func performRefresh(_ keyID: String) async -> Bool {
        guard !isPairing else { return false }   // eşleştirme bu çevre biriminin sahibi
        let old = channels[keyID]
        if let old {
            // Oturum yeni kurulduysa (teklif ondan önce gittiyse) yeniden sıfırlamaya gerek yok: teklif yenilenir.
            if Date().timeIntervalSince(old.since) < 5 { return true }
            let pid = old.link.peripheralID
            await terminateSession(pid)   // eski oturumun okuyucu/yazıcıları durmadan yenisi başlamaz
            guard !isPairing else { return false }
            central.resetLink(pid)
        }
        let deadline = Date().addingTimeInterval(15)
        while Date() < deadline {
            try? await Task.sleep(nanoseconds: 250_000_000)
            if Task.isCancelled || isPairing { return false }
            if let current = channels[keyID], current !== old { return true }
        }
        return false
    }

    func remember(_ snapshot: NearbyPeer) {
        let keyID = snapshot.id
        identity.reload()
        guard identity.pc(withKeyID: keyID) == nil else { return }   // zaten kayıtlı
        let pc = KnownPC(
            id: keyID, name: snapshot.name, publicKey: snapshot.publicKey, peripheralID: snapshot.peripheralID,
            addedAt: Date(), autoAccept: false)
        guard (try? identity.upsert(pc)) != nil else { return }
        if var live = peers[keyID] {
            live.isKnown = true
            peers[keyID] = live
        }
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
            do {
                return try await mailbox.pop(timeout: Timeouts.dataIdle)
            } catch MailboxError.timeout {
                throw TransferError.timeout   // 30 sn çerçeve gelmedi (DECISIONS 6): aktarım anlaşılır hatayla biter
            } catch {
                return nil   // kopma/bitiş = temiz kapanış
            }
        }
        return BleDataSession(channel: pc.channel, nextFrame: reader)
    }

    func endBleTransfer(_ keyID: String) {
        guard let pc = channels[keyID], let mailbox = pc.frames else { return }
        pc.frames = nil
        mailbox.fail(MailboxError.closed)   // bekleyen okuyucuyu uyandırır
    }

    /// Ağ/ses durumu değişince `status` gönderir (PROTOCOL §5).
    func broadcastStatus() {
        let status = helloInfo().statusMessage
        for keyID in channels.keys {
            Task { try? await send(.status(status), to: keyID) }
        }
    }

    #if PUSH_ENABLED
    /// Push kaydı kimliği (PROTOCOL §9); nil = kayıt yok / ayar kapalı. AppModel doldurur.
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
        if let id = pc.peripheralID {
            if let slot = slots[id] {
                slot.nextLink = nil
                interrupt(id, slot)   // oturum sahibi görevi durdurulur
            }
            central.closeLink(id)
        }
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
        guard identity.pc(withPeripheral: link.peripheralID) != nil else {
            central.closeLink(link.peripheralID)
            return
        }
        requestSession(link.peripheralID, .opened(link))
    }

    func bleCentral(_ central: BleCentral, didClose peripheralID: UUID) {
        // Okuma döngüsü kendi temizliğini yapar (inbox hataya düşer).
    }

    func bleCentral(_ central: BleCentral, didDiscover peripheral: CBPeripheral) {
        requestSession(peripheral.identifier, .discovered(peripheral))
    }

    // MARK: Oturum sahipliği (çevre birimi başına tek oturum)

    private func slot(for pid: UUID) -> SessionSlot {
        if let slot = slots[pid] { return slot }
        let slot = SessionSlot()
        slots[pid] = slot
        return slot
    }

    /// Oturum başlatmanın TEK kapısı. Eşleştirme sürerken hiçbir oturum başlamaz (açık bağlantı beklemeye alınır);
    /// çevre birimi boş değilse istek birleşir (aynı bağlantı yok sayılır, daha yeni bağlantı sıraya girer).
    private func requestSession(_ pid: UUID, _ request: SessionRequest) {
        let slot = slot(for: pid)
        if isPairing {
            if case .opened(let link) = request { deferredLinks[pid] = link }
            return
        }
        switch request {
        case .opened(let link):
            if slot.phase == .idle {
                beginSlot(pid, slot, link: link, peripheral: nil)
            } else if slot.link !== link {
                slot.nextLink = link          // daha yeni bağlantı: eski oturum bittikten sonra
                interrupt(pid, slot)
            }
        case .discovered(let peripheral):
            guard slot.phase == .idle, !peers.values.contains(where: { $0.peripheralID == pid }) else { return }
            beginSlot(pid, slot, link: nil, peripheral: peripheral)   // başarısızsa sonraki tarama turunda yeniden denenir
        }
    }

    private func beginSlot(_ pid: UUID, _ slot: SessionSlot, link: BlePeripheralLink?, peripheral: CBPeripheral?) {
        slot.phase = .connecting
        slot.link = link
        slot.task = Task { [weak self] in
            await self?.drive(pid, slot, link: link, peripheral: peripheral)
        }
    }

    /// Slot sahibi görev: (gerekirse bağlan) -> oturum -> bitince sıradaki daha yeni bağlantı varsa onunla devam.
    private func drive(_ pid: UUID, _ slot: SessionSlot, link initialLink: BlePeripheralLink?, peripheral initialPeripheral: CBPeripheral?) async {
        var link = initialLink
        var peripheral = initialPeripheral
        var xxRetried = false
        var extensionRetried = false
        while true {
            if link == nil, let p = peripheral, slot.phase != .closing {
                link = try? await central.openLink(to: p, timeout: 10)
                slot.link = link
            }
            peripheral = nil
            var end = SessionEnd.normal
            if let l = link { end = await runSession(link: l, slot: slot) }
            link = nil
            slot.link = nil
            // (a) PC `WDU` yolladı (eşleşme PC'de kaldırılmış): kayıt silindi; tek sefer, aynı çevre birimine XX ile yeniden bağlan.
            // (b) Paylaşım uzantısı: el sıkışma başka bir oturum yüzünden düştüyse (ana uygulama askıdayken Darwin sinyali
            //     ulaşmamış olabilir; PC aynı BLE kimliğinden gelen yeni oturumu tutar) bir kez yeniden dene (DECISIONS 10).
            let retry = (end == .ikRejected && !xxRetried)
                || (end == .handshakeFailed && ProtocolIds.isExtension && !extensionRetried)
            if retry, slot.phase != .closing, slot.nextLink == nil, !isPairing,
               let p = central.knownPeripheral(pid) {
                if end == .ikRejected { xxRetried = true } else { extensionRetried = true }
                for _ in 0..<20 where p.state != .disconnected { try? await Task.sleep(nanoseconds: 150_000_000) }
                if end == .handshakeFailed { try? await Task.sleep(nanoseconds: 500_000_000) }
                slot.phase = .connecting
                peripheral = p
                continue
            }
            guard let next = slot.nextLink else { break }
            slot.nextLink = nil
            guard next.isOpen else { break }
            if isPairing {
                deferredLinks[pid] = next
                break
            }
            link = next
            slot.link = next
            slot.phase = .connecting
        }
        slot.phase = .idle
        slot.task = nil
    }

    /// Slot'taki oturumu sonlandırmaya başlar (bağlantı kapanır, bekleyen okuma/yazma/`openLink` uyanır, görev iptal).
    private func interrupt(_ pid: UUID, _ slot: SessionSlot) {
        guard slot.phase != .idle else { return }
        slot.phase = .closing
        if let link = slot.link {
            link.close()
        } else {
            central.abortPendingLink(pid)
        }
        slot.task?.cancel()
    }

    /// Oturumu sonlandırır ve bitmesini bekler (en çok ~3 sn); ardından eski oturumun yazıcılarının bırakmasını bekler.
    private func terminateSession(_ pid: UUID) async {
        guard let slot = slots[pid], slot.phase != .idle else { return }
        slot.nextLink = nil
        let old = channels.values.first { $0.link.peripheralID == pid }
        interrupt(pid, slot)
        let deadline = Date().addingTimeInterval(3)
        while slot.phase != .idle, Date() < deadline {
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
        guard let old else { return }
        // Süren bir yazıcı kilidi tutuyorsa (bağlantı kapalı: hata ile hemen düşer) bırakmasını bekle.
        let drained = DrainFlag()
        Task { await old.lock.withLock {}; drained.done = true }
        while !drained.done, Date() < deadline.addingTimeInterval(1) {
            try? await Task.sleep(nanoseconds: 25_000_000)
        }
    }

    @MainActor private final class DrainFlag { var done = false }

    // MARK: Oturum

    /// Oturumun nasıl bittiği (`drive` buna göre yeniden dener).
    private enum SessionEnd {
        case normal
        /// PC `WDU` yolladı (güvenilmeyen anahtar): kayıt silindi; çağıran XX ile bir kez yeniden dener.
        case ikRejected
        /// El sıkışma (veya ilk `hello`) tamamlanamadı: kopma, zaman aşımı, sıra hatası, başka oturum. Kayıt SİLİNMEZ.
        case handshakeFailed
    }

    private func runSession(link: BlePeripheralLink, slot: SessionSlot) async -> SessionEnd {
        let pid = link.peripheralID
        // Kapatma isteği bağlantı beklenirken geldiyse oturum hiç başlamaz.
        guard slot.phase == .connecting else {
            link.close()
            return .normal
        }
        slot.phase = .handshaking
        central.clearUnknownPeerMarker(pid)
        // Arka planda uyandırıldıysak IK/XX + hello bitene kadar süre iste (uzantıda yok sayılır).
        let endHandshakeTask = beginBackgroundTask("com.efe.windrop.handshake")
        var keyID: String?
        var ikRejected = false
        var handshakeDone = false
        var timedOut = false
        do {
            // El sıkışma + ilk `hello` en çok 10 sn (DECISIONS 6): PC ön eki alıp yanıt vermezse oturum yuvası asılı kalmaz.
            let established: SecureChannel = try await withDeadline(seconds: Timeouts.bleHandshake, onTimeout: {
                timedOut = true
                link.close(error: BleError.timeout)
            }) {
                let localKey = try identity.deviceKey()
                let transport = BleMessageTransport(link: BleLinkAdapter(link: link))
                let channel: SecureChannel
                var usedIK = false
                if let known = identity.pc(withPeripheral: pid) {
                    do {
                        channel = try await SecureChannel.connectIK(
                            transport: transport, prefix: Prefix.ble(), localKey: localKey, remoteStaticKey: known.publicKey)
                        usedIK = true
                    } catch {
                        // Eşleşme/tanıdık kaydı YALNIZ PC `WDU` ("güvenilmeyen anahtar") yolladıysa silinir (DECISIONS 2).
                        // Kopma, zaman aşımı, WDR, sıra hatası ya da yerel kesme kaydı silmez; IK sonraki bağlantıda yeniden denenir.
                        if central.consumeUnknownPeerMarker(pid), !Task.isCancelled, slot.phase == .handshaking {
                            identity.remove(known)   // eşli bayrağı (autoAccept) ve etkin nokta bilgisi de gider
                            syncWanted()
                            ikRejected = true
                        }
                        throw error
                    }
                } else {
                    channel = try await SecureChannel.connectXX(transport: transport, prefix: Prefix.ble(), localKey: localKey)
                }

                guard slot.phase == .handshaking else { throw BleError.disconnected }   // el sıkışma sırasında kapatma istendi
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
                    id: kid, name: known?.name ?? String(localized: "Bilgisayar"), publicKey: channel.remoteStaticKey,
                    peripheralID: pid, isKnown: known != nil, autoAccept: known?.autoAccept ?? false, usedIK: usedIK,
                    // Doğrulama kodu yalnız XX özetinden (PROTOCOL §3.1); IK (tanıdık/eşli) oturumda kod gösterilmez.
                    code: usedIK ? "" : channel.verificationCode)
                if let stale = channels[kid], stale.link !== link {
                    stale.link.close()   // aynı PC için eski oturum: yeni oturum tek yazıcı olsun
                    stale.frames?.fail(BleError.disconnected)
                }
                channels[kid] = PeerChannel(channel: channel, link: link)
                slot.phase = .established
                peers[kid] = peer
                try await send(.hello(helloInfo().helloMessage), to: kid)
                handshakeDone = true
                return channel
            }
            endHandshakeTask()   // el sıkışma + hello bitti; okuma döngüsü arka plan görevi tutmaz
            guard let kid = keyID else { throw BleError.disconnected }
            #if PUSH_ENABLED
            sendPushId(to: kid)
            #endif

            while let message = try await established.receiveMessage() {
                try Task.checkCancellation()
                guard case .signal(let signal) = message else {
                    // BLE çerçevesi (PROTOCOL §7.3): yalnız `bleData` ile başlayan aktarım kuyruğa alınır, başıboş atılır.
                    if case .frame(let frame) = message, let pc = channels[kid], pc.channel === established {
                        pc.frames?.push(frame)
                    }
                    continue
                }
                if case .hello(let hello) = signal, var current = peers[kid], !current.isKnown {
                    let name = PeerName.sanitize(hello.name)
                    if !name.isEmpty {
                        current.name = name
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
        // Bu oturumun yazıcıları dursun (bağlantı nesnesi kapanınca bekleyen yazmalar hata ile döner); GATT bağlantısı
        // yalnız oturum hata ile bittiyse (ya da el sıkışma zaman aşımına uğradıysa: PC takılmış olabilir) ve sırada daha
        // yeni bir bağlantı yoksa kesilir (tanıdık PC'lerde merkez 1 sn sonra tek seferlik yeniden bağlanır).
        let wasOpen = link.isOpen
        link.close()
        if wasOpen || timedOut, slot.nextLink == nil { central.closeLink(pid) }
        if ikRejected { return .ikRejected }
        return handshakeDone || Task.isCancelled || slot.phase == .closing ? .normal : .handshakeFailed
    }
}

/// Karşı PC'nin bildirdiği ad: yalnız kontrol (Cc) ve biçim (Cf: bidi vb.) karakterleri atılır, kırpılır, en çok 64 karakter.
/// Ad sahteciliği (RLO) engellenir; PC tarafındaki ayıklamayla (PROTOCOL §3.2 madde 8) aynı kural.
enum PeerName {
    static func sanitize(_ raw: String) -> String {
        var scalars = String.UnicodeScalarView()
        for scalar in raw.unicodeScalars {
            switch scalar.properties.generalCategory {
            case .control, .format, .lineSeparator, .paragraphSeparator: continue
            default: scalars.append(scalar)
            }
        }
        let trimmed = String(scalars).trimmingCharacters(in: .whitespacesAndNewlines)
        return String(trimmed.prefix(64))
    }
}
