import Foundation
import UniformTypeIdentifiers
import Observation
@preconcurrency import WinDropKit

/// Aktarım katmanının bildirim ihtiyacı. Uygulama `NotificationManager` ile, Paylaşım Uzantısı `NoNotifications` ile doldurur.
@MainActor
protocol OfferNotifying: AnyObject {
    func requestAuthorizationIfNeeded() async
    func postOffer(transferId: String, peerName: String, summary: String)
    func clear(transferId: String)
    /// Aktarım bitti bildirimi (yalnız uygulama arka plandayken çağrılır).
    func postResult(transferId: String, body: String)
}

/// Bildirim göstermeyen (Paylaşım Uzantısı) uygulama.
@MainActor
final class NoNotifications: OfferNotifying {
    init() {}
    func requestAuthorizationIfNeeded() async {}
    func postOffer(transferId: String, peerName: String, summary: String) {}
    func clear(transferId: String) {}
    func postResult(transferId: String, body: String) {}
}

/// Aktarımların yaşam döngüsü: teklif -> kabul -> adres/etkin nokta -> TCP -> Kit FileSender/FileReceiver
/// (PROTOCOL §2b, §4, §6, §7). `pcID` her yerde PC'nin açık anahtar kimliğidir (`NearbyPeer.id`).
@MainActor
@Observable
final class TransferCoordinator {
    private(set) var items: [TransferItem] = []
    /// İlk başarılı aktarımdan sonra bir kez gösterilecek bilgi kartı için bildirim (UI dinler).
    private(set) var firstSuccessCardPending = false

    private let identity: IdentityStore
    private let signals: SignalSending
    private let notifications: OfferNotifying
    /// Uygulama ön planda değil mi? (Uzantıda hep false; uygulama `UIApplication.applicationState` ile doldurur.)
    @ObservationIgnored var isAppInBackground: @MainActor () -> Bool = { false }
    /// Uzun aktarım arka planda sürerken süre ister (uygulama doldurur; uzantıda API yok: boş işlem).
    @ObservationIgnored var beginBackgroundTask: BackgroundTaskStarter = { _ in {} }
    /// Paylaşım Uzantısı: gelen teklif gösterilemez; hemen reddedilir (PC bekleyip zaman aşımına düşmesin).
    @ObservationIgnored var rejectsIncomingOffers = false
    /// iPhone şu an Wi-Fi'a bağlı mı? `hotspot` yalnız `wifi=none` iken işlenir; bağlanmazsa güvenli varsayılan: bağlı say.
    @ObservationIgnored var isWifiConnected: @MainActor () -> Bool = { true }
    /// Bu cihazda Bluetooth ses etkin mi? (PROTOCOL §6.4: BLE veri yolu yalnız false iken.) Bilinmezse temkinli: true.
    @ObservationIgnored var isBluetoothAudioActive: @MainActor () -> Bool = { true }
    /// Ayardaki aktarma sunucusu adresi (`relayUrl`); boş/okunamıyorsa nil = aktarma yolu kapalı. Uzantı kendi ayarını okur.
    @ObservationIgnored var relayUrl: () -> String? = { RelaySettings.configuredURL() }
    @ObservationIgnored private var offerTimes: [String: [Date]] = [:]
    @ObservationIgnored private var offerWatchers: [String: Task<Void, Never>] = [:]
    /// Teklife yanıt bekleme süreleri: ilki oturum tazelemeden önce, ikincisi PC onay penceresi (30 sn) için.
    static let offerFirstWait: UInt64 = 20_000_000_000
    static let offerSecondWait: UInt64 = 35_000_000_000

    static let infoCardShownKey = "infoCardShown"

    init(identity: IdentityStore, signals: SignalSending, notifications: OfferNotifying) {
        self.identity = identity
        self.signals = signals
        self.notifications = notifications
    }

    var pendingOfferCount: Int {
        items.filter { $0.state == .awaitingUser }.count
    }

    /// Onay sayfasında gösterilecek ilk bekleyen teklif.
    var pendingOffer: TransferItem? {
        items.first { $0.state == .awaitingUser }
    }

    func item(withID id: String) -> TransferItem? {
        items.first { $0.id == id }
    }

    func clearFinished() {
        items.removeAll { $0.isFinished }
    }

    func dismissInfoCard() {
        firstSuccessCardPending = false
        UserDefaults.standard.set(true, forKey: Self.infoCardShownKey)
    }

    // MARK: Gönderme

    /// Teklif (`offer`) gönderir. PC yakında değilse `nil` döner.
    @discardableResult
    func startSending(files: [OutgoingFile], to peer: NearbyPeer) -> TransferItem? {
        guard !files.isEmpty, signals.isReady(peer.id) else { return nil }
        var numbered = files
        for index in numbered.indices { numbered[index].id = index }
        let total = numbered.reduce(Int64(0)) { $0 + $1.size }
        // Klasör dosyaları için klasör adı (PROTOCOL §5a: `offer.preview` klasör adlarını gösterebilir); tekrarsız, sıralı.
        var seen = Set<String>()
        let labels = numbered.map(\.displayName).filter { seen.insert($0).inserted }
        let transferId = TransferId.random().description

        let item = TransferItem(
            id: transferId, direction: .send, pcID: peer.id, pcName: peer.name,
            fileNames: labels, fileCount: numbered.count, totalBytes: total, state: .awaitingPeer)
        item.outgoing = numbered
        item.sendPreviewURL = numbered.first?.url
        item.code = peer.code
        item.peerIsKnown = peer.isKnown
        items.insert(item, at: 0)
        Task { await notifications.requestAuthorizationIfNeeded() }   // tembel: ilk Gönder

        let offer = OfferMessage(
            transferId: transferId, count: numbered.count, total: total,
            preview: Array(labels.prefix(5)))
        Task {
            do {
                try await signals.send(.offer(offer), to: peer.id)
            } catch {
                fail(item, error.localizedDescription)
            }
        }
        watchOffer(item, offer: offer)
        return item
    }

    private func isAwaitingReply(_ item: TransferItem) -> Bool {
        !item.isFinished && item.state == .awaitingPeer && !item.peerAccepted
    }

    /// `offer` sonrası 20 sn içinde `accept`/`reject` gelmezse oturumu tazeler; oturum yenilendiyse teklifi aynı
    /// transferId ile bir kez yeniden gönderir (yenilenmediyse ilk teklif PC'de bekliyor olabilir: yeniden gönderilmez).
    /// Yine yanıt yoksa 35 sn sonra aktarım hatayla biter.
    private func watchOffer(_ item: TransferItem, offer: OfferMessage) {
        let pcID = item.pcID
        let id = item.id
        offerWatchers[id]?.cancel()
        offerWatchers[id] = Task { [weak self] in
            defer { self?.offerWatchers[id] = nil }
            try? await Task.sleep(nanoseconds: Self.offerFirstWait)
            guard let self, !Task.isCancelled, self.isAwaitingReply(item) else { return }
            let renewed = await self.signals.refreshSession(pcID)
            guard !Task.isCancelled, self.isAwaitingReply(item) else { return }
            if renewed {
                do {
                    try await self.signals.send(.offer(offer), to: pcID)
                } catch {
                    self.fail(item, error.localizedDescription)
                    return
                }
            }
            try? await Task.sleep(nanoseconds: Self.offerSecondWait)
            guard !Task.isCancelled, self.isAwaitingReply(item) else { return }
            self.fail(item, String(localized: "Bilgisayar yanıt vermedi. Bilgisayarda WinDrop'un açık olduğundan emin olun."))
        }
    }

    // MARK: Gelen sinyaller

    func handle(_ message: SignalMessage, from peer: NearbyPeer) {
        switch message {
        case .offer(let offer):
            handleOffer(offer, from: peer)
        case .accept(let reply):
            if let item = item(withID: reply.transferId), item.pcID == peer.id, item.direction == .send, item.state == .awaitingPeer {
                // Tanıdık kaydı burada yapılmaz: yalnız `DONE ok=true` ile biten aktarımdan sonra (run).
                item.peerAccepted = true
                item.state = .awaitingPeer   // PC şimdi `addr` veya `hotspot` gönderir
            }
        case .reject(let reply):
            if let item = item(withID: reply.transferId), item.pcID == peer.id, item.direction == .send {
                item.state = .rejected
                finishCleanup(item)
            }
        case .addr(let addr):
            if let item = item(withID: addr.transferId), item.pcID == peer.id, hasConsent(item) {
                beginDirect(item, candidates: addr.candidates, peer: peer)
            }
        case .hotspot(let hotspot):
            // Yalnız onaylanmış aktarımda ve iPhone o an Wi-Fi'a bağlı değilken (PROTOCOL §6.2).
            if let item = item(withID: hotspot.transferId), item.pcID == peer.id, hasConsent(item), !isWifiConnected() {
                beginHotspot(item, message: hotspot, peer: peer)
            }
        case .cancel(let cancel):
            if let item = item(withID: cancel.transferId), item.pcID == peer.id, !item.isFinished {
                item.cancelPaths()
                item.state = .cancelled
                notifications.clear(transferId: item.id)
                finishCleanup(item)
            }
        case .bleData(let message):
            // Yolu PC seçer (PROTOCOL §6.4a): gönderen MANIFEST'i BLE'den başlatır, alıcı BLE'den MANIFEST bekler.
            if let item = item(withID: message.transferId), item.pcID == peer.id, hasConsent(item), canUseBle(item) {
                beginBle(item, peer: peer)
            }
        case .relay(let relay):
            handleRelay(relay, from: peer)
        case .hello, .status, .path, .pairInfo, .pushId:
            break
        }
    }

    /// Veri yolu (`addr`/`hotspot`) yalnız onaydan sonra başlar: alırken kullanıcı Kabul'ü, gönderirken PC'nin `accept`i.
    private func hasConsent(_ item: TransferItem) -> Bool {
        switch item.direction {
        case .receive: return item.userAccepted
        case .send: return item.peerAccepted
        }
    }

    func peerGone(_ pcID: String) {
        for item in items where item.pcID == pcID && item.state == .awaitingUser {
            item.state = .cancelled   // gönderen gitti; onay beklenen teklif anlamsız
            notifications.clear(transferId: item.id)
        }
    }

    /// Cihaz başına teklif hız sınırı (PROTOCOL §2b, §5): tanıdık 5/dk, bilinmeyen 3/dk.
    private func allowOffer(from peer: NearbyPeer) -> Bool {
        let now = Date()
        var times = (offerTimes[peer.id] ?? []).filter { now.timeIntervalSince($0) < 60 }
        let limit = peer.isKnown ? 5 : 3
        guard times.count < limit else { return false }
        times.append(now)
        offerTimes[peer.id] = times
        return true
    }

    private func handleOffer(_ offer: OfferMessage, from peer: NearbyPeer) {
        if rejectsIncomingOffers {
            if let tid = TransferId.parse(offer.transferId) {
                Task { try? await signals.send(.reject(ReplyMessage(transferId: tid.description, reason: "busy")), to: peer.id) }
            }
            return
        }
        guard item(withID: offer.transferId) == nil,
              let tid = TransferId.parse(offer.transferId),
              offer.count > 0, offer.count <= TransferLimits.maxFiles, offer.total >= 0 else { return }
        guard allowOffer(from: peer) else {
            Task { try? await signals.send(.reject(ReplyMessage(transferId: tid.description, reason: "rate")), to: peer.id) }
            return
        }
        // Önizlemede sonda "/" klasör demektir (PROTOCOL §5a); ad yine temizlenir, "/" arayüz için korunur.
        let names = offer.preview.prefix(5).map { entry -> String in
            entry.hasSuffix("/") ? FileNameSanitizer.sanitize(String(entry.dropLast())) + "/" : FileNameSanitizer.sanitize(entry)
        }
        // Otomatik kabul yalnız QR ile eşleştirilmiş PC'den, Ayarlar'da açıksa ve boyut sınırı içinde (PROTOCOL §5, §7);
        // "tanıdık" (onayla eklenen) PC'lerden gelen her teklif sorulur.
        let auto = peer.isKnown && peer.autoAccept && AutoAcceptSettings.isEnabled && offer.total <= AutoAcceptSettings.limit
        let item = TransferItem(
            id: offer.transferId, direction: .receive, pcID: peer.id, pcName: peer.name,
            fileNames: Array(names), fileCount: offer.count, totalBytes: offer.total,
            state: auto ? .awaitingPeer : .awaitingUser)
        item.code = peer.code
        item.peerIsKnown = peer.isKnown
        items.insert(item, at: 0)

        if auto {
            item.userAccepted = true   // QR ile eşleştirilmiş PC için kullanıcının önceden verdiği otomatik kabul
            // Arka plandayken teklif bildirimi YOK; yalnız aktarım bitince `notifyFinished` bildirir (izin tembel istenir).
            Task { await notifications.requestAuthorizationIfNeeded() }
            sendAccept(item)
        } else {
            // Bildirim izni tembel: ilk teklifte (ya da ilk Gönder'de) istenir, açılışta değil.
            // Arka plandayken istem görünmez; ön plana gelince gösterilir.
            Task { await notifications.requestAuthorizationIfNeeded() }
            if isAppInBackground() {
                let size = FileSizeText.string(offer.total)
                let what = offer.count == 1 ? (names.first ?? String(localized: "\(1) dosya")) : String(localized: "\(Int(offer.count)) dosya")
                notifications.postOffer(transferId: offer.transferId, peerName: peer.name, summary: "\(what) (\(size))")
            }
        }
    }

    // MARK: Kullanıcı eylemleri

    func accept(_ item: TransferItem) {
        guard item.state == .awaitingUser, item.direction == .receive else { return }
        item.state = .awaitingPeer
        item.userAccepted = true   // bildirimdeki Kabul de buradan geçer (acceptFromNotification)
        notifications.clear(transferId: item.id)
        signals.remember(item.pcID)   // kullanıcı onayladı -> tanıdık (reddedilen saklanmaz)
        item.peerIsKnown = true
        Task { await notifications.requestAuthorizationIfNeeded() }   // tembel: ilk teklifin kabulü
        LocalNetworkPermission.shared.trigger()   // yerel ağ izni sorusunu TCP'den önce çıkar
        sendAccept(item)
    }

    private func sendAccept(_ item: TransferItem) {
        let pcID = item.pcID
        Task {
            do {
                try await signals.send(.accept(ReplyMessage(transferId: item.id)), to: pcID)
            } catch {
                fail(item, error.localizedDescription)
            }
        }
    }

    func reject(_ item: TransferItem) {
        guard markRejected(item) else { return }
        let pcID = item.pcID
        Task {
            try? await signals.send(.reject(ReplyMessage(transferId: item.id)), to: pcID)
        }
    }

    /// Bildirimdeki "Reddet" eylemi: `reject` gönderilene kadar bekler (çağıran arka plan görevi içinde çalıştırır).
    /// Teklif belleğimizde yoksa (süreç yeniden doğdu) yapılacak bir şey yok; PC kendi zaman aşımıyla düşürür.
    func rejectFromNotification(transferId: String) async {
        guard let item = item(withID: transferId) else { return }
        await rejectAndSend(item)
    }

    /// Bildirimdeki "Kabul" eylemi (uygulama öne getirilmiştir).
    func acceptFromNotification(transferId: String) {
        guard let item = item(withID: transferId) else { return }
        accept(item)
    }

    private func rejectAndSend(_ item: TransferItem) async {
        guard markRejected(item) else { return }
        try? await signals.send(.reject(ReplyMessage(transferId: item.id)), to: item.pcID)
    }

    private func markRejected(_ item: TransferItem) -> Bool {
        guard item.state == .awaitingUser, item.direction == .receive else { return false }
        item.state = .rejected
        notifications.clear(transferId: item.id)
        return true
    }

    func cancel(_ item: TransferItem) {
        guard item.isCancellable else { return }
        item.cancelPaths()
        item.state = .cancelled
        finishCleanup(item)
        let pcID = item.pcID
        Task {
            try? await signals.send(.cancel(SignalCancelMessage(transferId: item.id)), to: pcID)
        }
    }

    /// Kota uyarısı onaylandı (iki tarafta da kullanıcı onaylar; reddetmek = `cancel`): aktarma odasına bağlanılır
    /// (PROTOCOL §6.3). Bağlantı/el sıkışma başarısızsa `path relay_fail` gider ve aktarım başarısız olur.
    func confirmRelay(_ item: TransferItem) {
        guard item.state == .awaitingRelayConsent, item.task == nil else { return }
        guard let endpoint = item.relayEndpoint, let peer = signals.peer(item.pcID) else {
            Task { await report(.relayFail, item: item) }
            fail(item, TransferFailure.relayNotConfigured.localizedDescription)
            return
        }
        item.state = .connecting
        item.task = Task { [weak self] in
            guard let self else { return }
            let socket: RelaySocket
            do {
                socket = try await RelaySocket.connect(to: endpoint)
            } catch {
                if Task.isCancelled { return }
                await self.report(.relayFail, item: item)
                self.fail(item, error.localizedDescription)
                return
            }
            await self.report(.relayOk, item: item)
            await self.run(item, transport: RelayMessageTransport(socket: socket), peer: peer, onFinish: { await socket.close() })
        }
    }

    /// `relay {transferId, token}`: yalnız onaylanmış aktarımda. Adres mesajdan değil ayardan (`relayUrl`) gelir
    /// (PROTOCOL §6 madde 3); ayar boş/geçersizse veya token bozuksa `relay_fail` gider. Kullanıcı onayı beklenir.
    private func handleRelay(_ message: RelayMessage, from peer: NearbyPeer) {
        guard let item = item(withID: message.transferId), item.pcID == peer.id, hasConsent(item), !item.isFinished,
              !item.hasActivePath, item.state == .awaitingPeer, item.relayEndpoint == nil else { return }
        guard let base = relayUrl(), let token = RelayRoom.decodeToken(message.token),
              let endpoint = RelayRoom.endpoint(relayUrl: base, roomId: RelayRoom.roomId(token: token), role: .phone) else {
            Task { await report(.relayFail, item: item) }
            return
        }
        item.relayEndpoint = endpoint
        item.state = .awaitingRelayConsent   // bağlantı, kullanıcı kota uyarısını onaylayana dek kurulmaz
    }

    func saveToPhotos(_ item: TransferItem) {
        let files = item.receivedFiles
        Task {
            do {
                let count = try await PhotoSaver.save(files).count
                item.photosSaveMessage = String(localized: "\(count) öğe Fotoğraflar'a kaydedildi.")
            } catch {
                item.photosSaveMessage = error.localizedDescription
            }
        }
    }

    // MARK: Veri yolu (PROTOCOL §6)

    /// `addr` (wifi=connected): adaylara paralel TCP. Etkin nokta beklenirken de kabul edilir ve onunla yarışır:
    /// ilk başarılı bağlantı kazanır, diğer deneme iptal edilir (`claimPath`).
    private func beginDirect(_ item: TransferItem, candidates: [AddressCandidate], peer: NearbyPeer) {
        guard item.task == nil, !item.isFinished, !item.pathWon else { return }
        let targets = candidates.map { HostPort(host: $0.ip, port: UInt16(clamping: $0.port)) }
        if let running = item.attempts[.direct] {
            guard item.directTargets != targets else { return }   // aynı adaylar: süren deneme yeter
            running.cancel()                                      // yeni adaylar (ör. Wi-Fi değişti): yeniden dene
        }
        item.directTargets = targets
        if item.attempts.isEmpty { item.state = .connecting }
        // Etkin nokta yarışta ise iPhone yeni bağlandığından (DHCP/yönlendirme) daha uzun denenir.
        let window: TimeInterval = item.attempts[.hotspot] != nil ? 12 : 4
        item.attempts[.direct] = Task { [weak self] in
            guard let self else { return }
            do {
                // İlk yerel ağ izni istemi açıkken bağlantı .waiting kalır (DataClient en çok 30 sn bekler); sonra kısa tekrar turları.
                let stream = try await DataClient.connect(to: targets, retryUntil: Date().addingTimeInterval(window))
                guard self.claimPath(item, winner: .direct) else { stream.close(); return }
                await self.report(.tcpOk, item: item)
                await self.run(item, stream: stream, peer: peer)
            } catch {
                if Task.isCancelled { return }
                await self.report(.tcpFail, item: item)
                if let dataError = error as? DataClientError, dataError == .localNetworkDenied {
                    self.fail(item, LocalNetworkPermission.deniedMessage)
                    return
                }
                self.attemptFailed(item, .direct)
            }
        }
    }

    /// `hotspot` (wifi=none): etkin noktaya katıl, sonra TCP. Doğrudan deneme sürerken (Wi-Fi koptu) de kabul edilir.
    private func beginHotspot(_ item: TransferItem, message: HotspotMessage, peer: NearbyPeer) {
        guard item.task == nil, !item.isFinished, !item.pathWon, item.attempts[.hotspot] == nil else { return }
        if let ssid = message.ssid, let pass = message.pass {
            identity.setHotspot(HotspotCredentials(ssid: ssid, pass: pass), for: peer.id)
        }
        guard let credentials = identity.hotspot(for: peer.id) else {
            // TODO(windows-sync): eşleştirmesiz ilk temasta PC `hotspot` mesajına ssid/pass koymalı (PROTOCOL §4, §6.2).
            Task { await report(.hotspotFail, item: item) }
            if item.attempts.isEmpty { awaitNextPath(item) }
            return
        }
        let target = HostPort(host: message.ip, port: UInt16(clamping: message.port))
        item.hotspotCredentials = credentials
        item.wifiOffNotice = !isWifiConnected()   // Wi-Fi kapalı/bağlı değil: önce "Wi-Fi'ı açın" yönergesi
        item.state = .joiningHotspot
        item.attempts[.hotspot] = Task { [weak self] in
            guard let self else { return }
            do {
                let outcome = try await HotspotJoiner.join(credentials)
                if Task.isCancelled {   // join iptal edilemez: doğrudan yol kazanmışsa profili geri al
                    HotspotJoiner.abandon(credentials)
                    return
                }
                var wait: TimeInterval = 20
                if outcome == .manualRequired {
                    item.hotspotPrompt = credentials
                    item.state = .needsManualHotspot
                    wait = 120   // kullanıcı Ayarlar'a gidip katılana (ya da Wi-Fi'ı açıp PC `addr` yollayana) kadar bekle
                }
                let stream = try await DataClient.connect(to: [target], retryUntil: Date().addingTimeInterval(wait))
                guard self.claimPath(item, winner: .hotspot) else { stream.close(); return }
                await self.report(.hotspotOk, item: item)
                await self.run(item, stream: stream, peer: peer)
            } catch {
                if Task.isCancelled { return }
                await self.report(.hotspotFail, item: item)
                self.attemptFailed(item, .hotspot)
            }
        }
    }

    /// Wi-Fi durumu değişti (NetworkMonitor): etkin nokta beklenirken "Wi-Fi'ı açın" notu kendiliğinden kalkar/çıkar.
    /// `status` PC'ye ayrıca `SignalHub.broadcastStatus` ile gider; PC aynı transferId için `addr` yollar (bkz. `beginDirect`).
    func wifiChanged() {
        let connected = isWifiConnected()
        for item in items where item.attempts[.hotspot] != nil && !item.pathWon && !item.isFinished {
            item.wifiOffNotice = !connected
        }
    }

    /// İlk başarılı bağlantı yolu kazanır: diğer denemeler iptal edilir, "bir kereye mahsus" ekranı kapanır.
    /// Kaybeden bağlantıyı çağıran kapatır (`false` döner).
    private func claimPath(_ item: TransferItem, winner: TransferItem.PathKind) -> Bool {
        guard !item.pathWon, !item.isFinished else { return false }
        item.pathWon = true
        if winner != .hotspot, item.attempts[.hotspot] != nil, let credentials = item.hotspotCredentials {
            HotspotJoiner.abandon(credentials)   // yarım kalmış katılma iPhone'u aktarım ortasında ağdan atmasın
        }
        for (kind, attempt) in item.attempts where kind != winner { attempt.cancel() }
        item.attempts = item.attempts.filter { $0.key == winner }
        item.hotspotPrompt = nil
        item.wifiOffNotice = false
        return true
    }

    private func attemptFailed(_ item: TransferItem, _ kind: TransferItem.PathKind) {
        guard !item.pathWon else { return }
        item.attempts[kind] = nil
        if kind == .hotspot {
            item.hotspotPrompt = nil
            item.wifiOffNotice = false
        }
        if item.attempts.isEmpty {
            awaitNextPath(item)
        } else if item.attempts[.direct] != nil {
            item.state = .connecting
        }
    }

    /// Tüm denemeler başarısız: yolu PC seçer (`bleData` veya `relay`, §6.3/§6.4a). Kota uyarısı `relay` gelince gösterilir.
    private func awaitNextPath(_ item: TransferItem) {
        guard !item.isFinished else { return }
        item.task = nil
        item.state = .awaitingPeer
    }

    /// Kit kanalını açıp FileSender/FileReceiver'ı çalıştırır (TCP açıldıktan sonra).
    private func run(_ item: TransferItem, stream: NWByteStream, peer: NearbyPeer) async {
        await run(item, transport: TcpMessageTransport(stream: stream), peer: peer, onFinish: { stream.close() })
    }

    /// Ortak çalıştırıcı: taşıma TCP (`taşıma=2`) veya aktarma WebSocket'i (`taşıma=3`) olabilir; kanal deseni aynı.
    private func run(
        _ item: TransferItem, transport: MessageTransport, peer: NearbyPeer, onFinish: @escaping () async -> Void
    ) async {
        let endBackground = beginBackgroundTask("com.efe.windrop.transfer")   // arka planda alım/gönderim sürsün
        defer { endBackground(); Task { await onFinish() } }
        do {
            let key = try identity.deviceKey()
            guard let tid = TransferId.parse(item.id) else { throw TransferFailure.invalidTransferId }
            let crypto = ChannelCrypto(
                localPrivateKey: key.rawRepresentation, remoteStaticKey: peer.publicKey, useIK: peer.usedIK)
            item.state = .transferring
            let progress: @Sendable (TransferProgress) -> Void = { [weak item] value in
                Task { @MainActor in item?.apply(value) }
            }
            let summary: TransferSummary
            switch item.direction {
            case .send:
                summary = try await TransferRunner.send(
                    transport: transport, transferId: tid.bytes, crypto: crypto, files: item.outgoing, progress: progress)
            case .receive:
                summary = try await TransferRunner.receive(
                    transport: transport, transferId: tid.bytes, crypto: crypto, peerKeyID: peer.id,
                    expectedCount: item.fileCount, expectedTotal: item.totalBytes,
                    destination: AppPaths.receivedDirectory, progress: progress)
            }
            await complete(item, summary: summary, peer: peer)
        } catch {
            if Task.isCancelled || item.state == .cancelled { return }
            fail(item, error.localizedDescription)
        }
    }

    /// Aktarım sonucunu satıra işler (TCP ve BLE ortak). Alımda dosyalar SHA doğrulamasından geçmiş gelir; medya
    /// (tekil dosyalar) ayara göre Fotoğraflar'a taşınır, sonra satır "bitti"ye geçer ve gerekirse bildirim gider.
    private func complete(_ item: TransferItem, summary: TransferSummary, peer: NearbyPeer) async {
        item.receivedFiles = summary.received
        if summary.ok {
            if item.direction == .send {
                signals.remember(peer.id)   // giden aktarım `DONE ok=true` ile bitti -> tanıdık (PROTOCOL §2b)
                item.peerIsKnown = true
            } else {
                await storeReceived(item)
            }
            item.bytesDone = item.totalBytes
            item.state = .done
            noteSuccess()
            notifyFinished(item, peerName: peer.name)
        } else if summary.canceledByRemote {
            item.state = .cancelled
        } else {
            fail(item, summary.error ?? String(localized: "\(summary.failedFileIDs.count) dosya aktarılamadı."))
        }
        finishCleanup(item)
    }

    /// Alınan dosyaları yerleştirir: tekil medya dosyaları (klasör aktarımı hariç, yapı korunur) ayar "Fotoğraflar" ise
    /// Fotoğraflar'a taşınır; izin yok/hata olursa Documents'ta kalır ve satıra nedeni yazılır.
    private func storeReceived(_ item: TransferItem) async {
        let media = item.receivedFiles.filter { $0.mediaKind != nil }
        guard !item.isFolderTransfer, IncomingMediaDestination.current == .photos, !media.isEmpty else {
            item.savedPlace = .files
            return
        }
        // Taşıma dosyayı Documents'tan kaldırır: satır küçük resmi KAYDETMEDEN önce dosyadan üretilir (okuma izni istenmez).
        let thumbnail = await PhotoSaver.makeThumbnail(for: media[0].url)
        do {
            let result = try await PhotoSaver.saveWithAssets(media, move: true)
            let saved = result.files
            item.previewImage = thumbnail
            item.photoAssetID = result.assetIDs.first
            let savedURLs = Set(saved.map(\.url))
            item.receivedFiles = item.receivedFiles.filter { !savedURLs.contains($0.url) }
            item.savedPlace = item.receivedFiles.isEmpty ? .photos : .photosAndFiles
        } catch PhotoSaveError.denied {
            item.savedPlace = .photosDenied
        } catch {
            item.savedPlace = .photosFailed
        }
    }

    /// Uygulama arka plandayken biten aktarım için yerel bildirim (ön planda gösterilmez).
    private func notifyFinished(_ item: TransferItem, peerName: String) {
        guard isAppInBackground() else { return }
        let body: String
        switch item.direction {
        case .send:
            let what = item.isFolderTransfer
                ? String(localized: "\(item.fileCount) klasör öğesi")
                : String(localized: "\(item.fileCount) dosya")
            body = String(localized: "\(what) \(peerName) bilgisayarına gönderildi")
        case .receive:
            let kinds = Set(item.fileNames.map { name in
                UTType(filenameExtension: (name as NSString).pathExtension).map { type -> Int in
                    type.conforms(to: .movie) ? 2 : (type.conforms(to: .image) ? 1 : 0)
                } ?? 0
            })
            let count = item.fileCount
            let noun: String
            if item.isFolderTransfer { noun = String(localized: "\(count) dosya") }
            else if kinds == [1] { noun = String(localized: "\(count) fotoğraf") }
            else if kinds == [2] { noun = String(localized: "\(count) video") }
            else if !kinds.contains(0) { noun = String(localized: "\(count) fotoğraf/video") }
            else { noun = String(localized: "\(count) dosya") }
            let place = item.savedPlace?.text ?? String(localized: "Dosyalar'a kaydedildi")
            body = String(localized: "\(peerName)'dan \(noun) alındı — \(place)")
        }
        notifications.postResult(transferId: item.id, body: body)
    }

    // MARK: BLE küçük dosya yolu (PROTOCOL §6.4)

    /// BLE yolunu PC seçer (`bleData`, §6.4a); burada yalnız yerel sınırlar: toplam <= 64 KB ve bu cihazda Bluetooth ses yok.
    private func canUseBle(_ item: TransferItem) -> Bool {
        item.totalBytes <= TransferLimits.bleMaxTotalBytes && !isBluetoothAudioActive()
    }

    /// BLE oturumunu veri kanalı olarak devralır. Hub çerçeve yönlendirmesi eşzamanlı başlar (PC'nin hemen ardından
    /// gelen MANIFEST kaçmasın: `bleData` ve çerçeveler aynı ana aktör döngüsünde sırayla işlenir).
    @discardableResult
    private func beginBle(_ item: TransferItem, peer: NearbyPeer) -> Bool {
        guard !item.hasActivePath, item.state == .awaitingPeer || item.state == .awaitingRelayConsent,
              let session = signals.beginBleTransfer(peer.id) else { return false }
        item.state = .transferring
        let pcID = peer.id
        item.task = Task { [weak self] in
            guard let self else { return }
            let endBackground = self.beginBackgroundTask("com.efe.windrop.transfer")
            defer { endBackground(); self.signals.endBleTransfer(pcID) }
            do {
                let progress: @Sendable (TransferProgress) -> Void = { [weak item] value in
                    Task { @MainActor in item?.apply(value) }
                }
                let summary: TransferSummary
                switch item.direction {
                case .send:
                    summary = try await TransferRunner.sendOverBle(session: session, files: item.outgoing, progress: progress)
                case .receive:
                    guard let tid = TransferId.parse(item.id) else { throw TransferFailure.invalidTransferId }
                    summary = try await TransferRunner.receiveOverBle(
                        session: session, transferId: tid.bytes, peerKeyID: peer.id, expectedCount: item.fileCount,
                        expectedTotal: item.totalBytes, destination: AppPaths.receivedDirectory, progress: progress)
                }
                await self.complete(item, summary: summary, peer: peer)
            } catch {
                if Task.isCancelled || item.state == .cancelled { return }
                self.fail(item, error.localizedDescription)
            }
        }
        return true
    }

    /// İlk başarılı aktarımdan sonra bilgi kartı bir kez gösterilir.
    private func noteSuccess() {
        if !UserDefaults.standard.bool(forKey: Self.infoCardShownKey) { firstSuccessCardPending = true }
    }

    private func report(_ result: PathResult, item: TransferItem) async {
        try? await signals.send(.path(PathMessage(transferId: item.id, result: result)), to: item.pcID)
    }

    private func fail(_ item: TransferItem, _ text: String) {
        guard !item.isFinished else { return }
        item.errorMessage = text
        item.state = .failed
        finishCleanup(item)
    }

    private func finishCleanup(_ item: TransferItem) {
        notifications.clear(transferId: item.id)
        if !item.outgoing.isEmpty {
            FilePreparer.release(item.outgoing)
            item.outgoing = []
        }
        for attempt in item.attempts.values { attempt.cancel() }
        item.attempts = [:]
        item.task = nil
    }
}
