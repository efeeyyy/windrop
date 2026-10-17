import Foundation
import Observation
@preconcurrency import WinDropKit

/// Aktarım katmanının bildirim ihtiyacı. Uygulama `NotificationManager` ile, Paylaşım Uzantısı `NoNotifications` ile doldurur.
@MainActor
protocol OfferNotifying: AnyObject {
    func requestAuthorizationIfNeeded() async
    func postOffer(transferId: String, peerName: String, summary: String)
    func clear(transferId: String)
}

/// Bildirim göstermeyen (Paylaşım Uzantısı) uygulama.
@MainActor
final class NoNotifications: OfferNotifying {
    init() {}
    func requestAuthorizationIfNeeded() async {}
    func postOffer(transferId: String, peerName: String, summary: String) {}
    func clear(transferId: String) {}
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
    /// Paylaşım Uzantısı: gelen teklif gösterilemez; hemen reddedilir (PC bekleyip zaman aşımına düşmesin).
    @ObservationIgnored var rejectsIncomingOffers = false
    /// iPhone şu an Wi-Fi'a bağlı mı? `hotspot` yalnız `wifi=none` iken işlenir; bağlanmazsa güvenli varsayılan: bağlı say.
    @ObservationIgnored var isWifiConnected: @MainActor () -> Bool = { true }
    /// Bu cihazda Bluetooth ses etkin mi? (PROTOCOL §6.4: BLE veri yolu yalnız false iken.) Bilinmezse temkinli: true.
    @ObservationIgnored var isBluetoothAudioActive: @MainActor () -> Bool = { true }
    @ObservationIgnored private var offerTimes: [String: [Date]] = [:]

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
        let transferId = TransferId.random().description

        let item = TransferItem(
            id: transferId, direction: .send, pcID: peer.id, pcName: peer.name,
            fileNames: numbered.map(\.name), fileCount: numbered.count, totalBytes: total, state: .awaitingPeer)
        item.outgoing = numbered
        item.code = peer.code
        item.peerIsKnown = peer.isKnown
        items.insert(item, at: 0)
        Task { await notifications.requestAuthorizationIfNeeded() }   // tembel: ilk Gönder

        let offer = OfferMessage(
            transferId: transferId, count: numbered.count, total: total,
            preview: Array(numbered.prefix(5).map(\.name)))
        Task {
            do {
                try await signals.send(.offer(offer), to: peer.id)
            } catch {
                fail(item, error.localizedDescription)
            }
        }
        return item
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
                item.task?.cancel()
                item.state = .cancelled
                notifications.clear(transferId: item.id)
                finishCleanup(item)
            }
        case .bleData(let message):
            // Yolu PC seçer (PROTOCOL §6.4a): gönderen MANIFEST'i BLE'den başlatır, alıcı BLE'den MANIFEST bekler.
            if let item = item(withID: message.transferId), item.pcID == peer.id, hasConsent(item), canUseBle(item) {
                beginBle(item, peer: peer)
            }
        case .relay:
            // TODO(relay): PROTOCOL §6.3 aktarma sunucusu henüz tasarlanmadı.
            break
        case .hello, .status, .path, .pairInfo:
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
        let names = offer.preview.prefix(5).map { FileNameSanitizer.sanitize($0) }
        // Otomatik kabul yalnız QR ile eşleştirilmiş PC'den ve boyut sınırı içinde (PROTOCOL §5, §7).
        let auto = peer.isKnown && peer.autoAccept && offer.total <= TransferRunner.acceptAutoLimit
        let item = TransferItem(
            id: offer.transferId, direction: .receive, pcID: peer.id, pcName: peer.name,
            fileNames: Array(names), fileCount: offer.count, totalBytes: offer.total,
            state: auto ? .awaitingPeer : .awaitingUser)
        item.code = peer.code
        item.peerIsKnown = peer.isKnown
        items.insert(item, at: 0)

        if auto {
            item.userAccepted = true   // QR ile eşleştirilmiş PC için kullanıcının önceden verdiği otomatik kabul
            sendAccept(item)
        } else {
            // Bildirim izni tembel: ilk teklifte (ya da ilk Gönder'de) istenir, açılışta değil.
            // Arka plandayken istem görünmez; ön plana gelince gösterilir.
            Task { await notifications.requestAuthorizationIfNeeded() }
            if isAppInBackground() {
                let size = ByteCountFormatter.string(fromByteCount: offer.total, countStyle: .file)
                let what = offer.count == 1 ? (names.first ?? "1 dosya") : "\(offer.count) dosya"
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
        item.task?.cancel()
        item.state = .cancelled
        finishCleanup(item)
        let pcID = item.pcID
        Task {
            try? await signals.send(.cancel(SignalCancelMessage(transferId: item.id)), to: pcID)
        }
    }

    /// Kota uyarısı onaylandı. TODO(relay): PROTOCOL §6.3 aktarma sunucusu yok; şimdilik başarısız olur.
    func confirmRelay(_ item: TransferItem) {
        guard item.state == .awaitingRelayConsent else { return }
        fail(item, TransferFailure.relayUnavailable.localizedDescription)
    }

    func saveToPhotos(_ item: TransferItem) {
        let files = item.receivedFiles
        Task {
            do {
                let count = try await PhotoSaver.save(files)
                item.photosSaveMessage = "\(count) öğe Fotoğraflar'a kaydedildi."
            } catch {
                item.photosSaveMessage = error.localizedDescription
            }
        }
    }

    // MARK: Veri yolu (PROTOCOL §6)

    /// `addr` (wifi=connected): adaylara paralel TCP.
    private func beginDirect(_ item: TransferItem, candidates: [AddressCandidate], peer: NearbyPeer) {
        guard item.task == nil, !item.isFinished else { return }
        let targets = candidates.map { HostPort(host: $0.ip, port: UInt16(clamping: $0.port)) }
        item.state = .connecting
        item.task = Task { [weak self] in
            guard let self else { return }
            do {
                // İlk yerel ağ izni istemi uzun sürebilir: ~4 sn boyunca turlar tekrarlanır.
                let stream = try await DataClient.connect(to: targets, retryUntil: Date().addingTimeInterval(4))
                await self.report(.tcpOk, item: item)
                await self.run(item, stream: stream, peer: peer)
            } catch {
                if Task.isCancelled { return }
                await self.report(.tcpFail, item: item)
                self.offerRelay(item)
            }
        }
    }

    /// `hotspot` (wifi=none): etkin noktaya katıl, sonra TCP.
    private func beginHotspot(_ item: TransferItem, message: HotspotMessage, peer: NearbyPeer) {
        guard item.task == nil, !item.isFinished else { return }
        if let ssid = message.ssid, let pass = message.pass {
            identity.setHotspot(HotspotCredentials(ssid: ssid, pass: pass), for: peer.id)
        }
        guard let credentials = identity.hotspot(for: peer.id) else {
            // TODO(windows-sync): eşleştirmesiz ilk temasta PC `hotspot` mesajına ssid/pass koymalı (PROTOCOL §4, §6.2).
            Task { await report(.hotspotFail, item: item) }
            offerRelay(item)
            return
        }
        let target = HostPort(host: message.ip, port: UInt16(clamping: message.port))
        item.state = .joiningHotspot
        item.task = Task { [weak self] in
            guard let self else { return }
            do {
                let outcome = try await HotspotJoiner.join(credentials)
                var wait: TimeInterval = 20
                if outcome == .manualRequired {
                    item.hotspotPrompt = credentials
                    item.state = .needsManualHotspot
                    wait = 120   // kullanıcı Ayarlar'a gidip katılana kadar bekle
                }
                let stream = try await DataClient.connect(to: [target], retryUntil: Date().addingTimeInterval(wait))
                item.hotspotPrompt = nil
                await self.report(.hotspotOk, item: item)
                await self.run(item, stream: stream, peer: peer)
            } catch {
                if Task.isCancelled { return }
                item.hotspotPrompt = nil
                await self.report(.hotspotFail, item: item)
                self.offerRelay(item)
            }
        }
    }

    /// Aktarma yolu: kota uyarısı önce gösterilir (relay henüz uygulanmadı).
    private func offerRelay(_ item: TransferItem) {
        guard !item.isFinished else { return }
        item.task = nil
        item.state = .awaitingRelayConsent
    }

    /// Kit kanalını açıp FileSender/FileReceiver'ı çalıştırır (TCP açıldıktan sonra).
    private func run(_ item: TransferItem, stream: NWByteStream, peer: NearbyPeer) async {
        defer { stream.close() }
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
                    stream: stream, transferId: tid.bytes, crypto: crypto, files: item.outgoing, progress: progress)
            case .receive:
                summary = try await TransferRunner.receive(
                    stream: stream, transferId: tid.bytes, crypto: crypto, peerKeyID: peer.id,
                    expectedCount: item.fileCount, expectedTotal: item.totalBytes,
                    destination: AppPaths.receivedDirectory, progress: progress)
            }
            complete(item, summary: summary, peer: peer)
        } catch {
            if Task.isCancelled || item.state == .cancelled { return }
            fail(item, error.localizedDescription)
        }
    }

    /// Aktarım sonucunu satıra işler (TCP ve BLE ortak).
    private func complete(_ item: TransferItem, summary: TransferSummary, peer: NearbyPeer) {
        item.receivedFiles = summary.received
        if summary.ok {
            if item.direction == .send {
                signals.remember(peer.id)   // giden aktarım `DONE ok=true` ile bitti -> tanıdık (PROTOCOL §2b)
                item.peerIsKnown = true
            }
            item.bytesDone = item.totalBytes
            item.state = .done
            noteSuccess()
        } else if summary.canceledByRemote {
            item.state = .cancelled
        } else {
            fail(item, summary.error ?? "\(summary.failedFileIDs.count) dosya aktarılamadı.")
        }
        finishCleanup(item)
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
        guard item.task == nil, item.state == .awaitingPeer || item.state == .awaitingRelayConsent,
              let session = signals.beginBleTransfer(peer.id) else { return false }
        item.state = .transferring
        let pcID = peer.id
        item.task = Task { [weak self] in
            guard let self else { return }
            defer { self.signals.endBleTransfer(pcID) }
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
                self.complete(item, summary: summary, peer: peer)
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
        item.task = nil
    }
}
