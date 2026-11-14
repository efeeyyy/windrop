import CoreBluetooth
import Foundation

@MainActor
protocol BleCentralDelegate: AnyObject {
    func bleCentral(_ central: BleCentral, didUpdateState state: CBManagerState)
    /// Eşli bir PC'ye bağlantı kuruldu ve `p2c` bildirimleri açıldı (eşleştirme bağlantıları hariç).
    func bleCentral(_ central: BleCentral, didOpen link: BlePeripheralLink)
    func bleCentral(_ central: BleCentral, didClose peripheralID: UUID)
    /// Yakındaki tarama (AirDrop benzeri) bir WinDrop çevre birimi buldu.
    func bleCentral(_ central: BleCentral, didDiscover peripheral: CBPeripheral)
}

/// CoreBluetooth merkezi (central): PROTOCOL §3.
/// - Eşli PC'lere zaman aşımsız "bekleyen bağlantı" (`connect` bitmez; menzile girince bağlanır).
/// - Durum geri yükleme (`CBCentralManagerOptionRestoreIdentifierKey`) + `bluetooth-central` arka plan modu.
/// - Eşleştirme için ön planda tarama (`discoverForPairing`) ve tek seferlik bağlantı (`openLink`).
/// Tüm CoreBluetooth geri çağrıları ana kuyrukta gelir (`queue: .main`).
@MainActor
final class BleCentral: NSObject {
    weak var delegate: BleCentralDelegate?
    private(set) var state: CBManagerState = .unknown

    private var manager: CBCentralManager?
    private var wanted: Set<UUID> = []
    private var peripherals: [UUID: CBPeripheral] = [:]
    private var pendingCharacteristics: [UUID: (c2p: CBCharacteristic, p2c: CBCharacteristic)] = [:]
    private var settingUp: Set<UUID> = []
    private var links: [UUID: BlePeripheralLink] = [:]
    /// GATT bağlantısı bilerek kesilmekte olan çevre birimleri (`didDisconnect` gelene dek): bu aralıkta yeniden
    /// bağlanma / servis keşfi başlatılmaz (aynı çevre birimine ikinci oturum açılmasın).
    private var resetting: Set<UUID> = []
    /// Çevre birimi başına en çok BİR bekleyen yeniden bağlanma (1 sn debounce); tekrarlı tetikler birleşir.
    private var reconnectTasks: [UUID: Task<Void, Never>] = [:]

    private var linkWaiters: [UUID: CheckedContinuation<BlePeripheralLink, Error>] = [:]
    private var linkWaiterTokens: [UUID: Int] = [:]
    private var scanWaiter: CheckedContinuation<CBPeripheral, Error>?
    private var scanToken = 0
    private var scanPcName = ""
    private var tokenCounter = 0

    var isStarted: Bool { manager != nil }

    /// CBCentralManager TEMBEL oluşturulur (ilk oluşturma Bluetooth izin istemini çıkarır): yalnız kullanıcı
    /// Gönder dediğinde veya uygulama öne gelince veya izin zaten verilmişken açılışta (durum geri yükleme) çağrılır.
    func start() {
        guard manager == nil, !isShutDown else { return }
        var options: [String: Any] = [CBCentralManagerOptionShowPowerAlertKey: true]
        if !ProtocolIds.isExtension {
            // Uzantı geri yükleme kimliği kullanmaz: yalnız ana uygulama arka planda yeniden doğurulur.
            options[CBCentralManagerOptionRestoreIdentifierKey] = ProtocolIds.restoreIdentifier
        }
        manager = CBCentralManager(delegate: self, queue: .main, options: options)
    }

    // MARK: Kapatma ve IK ret işareti

    /// Uzantı kapandı: bir daha bağlanılmaz, taranmaz.
    private var isShutDown = false

    /// PC'nin `WDU` ("güvenilmeyen anahtar", DECISIONS 2) bildirimini aldığı çevre birimleri ve zamanı. Eşleşme/tanıdık kaydı
    /// YALNIZ bu işaretle silinir; `SignalHub` IK hatasında `consumeUnknownPeerMarker` ile bakar.
    private var unknownPeerMarkers: [UUID: Date] = [:]

    /// Yakın zamanda (≤ `seconds`) `WDU` alındıysa true döner ve işareti tüketir.
    func consumeUnknownPeerMarker(_ id: UUID, within seconds: TimeInterval = 15) -> Bool {
        guard let date = unknownPeerMarkers.removeValue(forKey: id) else { return false }
        return Date().timeIntervalSince(date) < seconds
    }

    /// Eski (süren oturumdan önceki) işareti yeni oturumdan önce temizler.
    func clearUnknownPeerMarker(_ id: UUID) {
        unknownPeerMarkers[id] = nil
    }

    /// Açık (kapatılmamış) bağlantı nesnesi; kapatılmış ama `links` içinde kalmış girdi yok sayılır (bkz. bulgu 23).
    private func openLinkEntry(_ id: UUID) -> BlePeripheralLink? {
        guard let link = links[id], link.isOpen else { return nil }
        return link
    }

    private func dropConnection(_ peripheral: CBPeripheral) {
        manager?.cancelPeripheralConnection(peripheral)
    }

    /// Paylaşım Uzantısı kapanırken: tarama durur, tüm çevre birimi bağlantıları iptal edilir, bekleyen yeniden
    /// bağlanmalar ve açılmakta olan bağlantılar bırakılır; sonrasında hiçbir şey yeniden kurulmaz.
    func shutdown() {
        guard !isShutDown else { return }
        isShutDown = true
        wanted.removeAll()
        wantsNearbyScan = false
        scanRestartTask?.cancel()
        scanRestartTask = nil
        manager?.stopScan()
        reconnectTasks.values.forEach { $0.cancel() }
        reconnectTasks.removeAll()
        for id in Array(linkWaiters.keys) {
            failLinkWaiter(id, error: BleError.disconnected, cancelConnection: false)
        }
        if scanWaiter != nil { finishScan(.failure(BleError.disconnected)) }
        for link in links.values { link.close() }
        for p in peripherals.values where p.state != .disconnected {
            dropConnection(p)
        }
        if let manager {
            for p in manager.retrieveConnectedPeripherals(withServices: ProtocolIds.discoveryServices)
            where p.state != .disconnected && peripherals[p.identifier] == nil {
                dropConnection(p)
            }
        }
    }

    // MARK: Bekleyen bağlantılar (eşli PC'ler)

    /// Eşli PC çevre birimi tanımlayıcıları. Listeden çıkanların bağlantısı kesilir.
    func setWanted(_ requested: Set<UUID>) {
        // Uzantıda bekleyen (kalıcı) bağlantı ve otomatik yeniden bağlanma yok: yalnız açıkken taramayla bulunanlara bağlanılır.
        let ids: Set<UUID> = (ProtocolIds.isExtension || isShutDown) ? [] : requested
        let removed = wanted.subtracting(ids)
        wanted = ids
        for id in removed {
            reconnectTasks.removeValue(forKey: id)?.cancel()
            if let p = peripherals[id], p.state != .disconnected {
                dropConnection(p)
            }
        }
        ensureConnections()
    }

    /// Bağlı olmayan eşli PC'ler için `connect` çağırır (bitmeyen bekleyen bağlantı).
    func ensureConnections() {
        guard state == .poweredOn, !isPaused, !isYielding, !wanted.isEmpty else { return }
        for p in manager?.retrievePeripherals(withIdentifiers: Array(wanted)) ?? [] {
            peripherals[p.identifier] = p
            p.delegate = self
            // Sıfırlanan / bekleyen yeniden bağlanması olan / bağlantısı açılmakta olan çevre birimine dokunulmaz.
            if resetting.contains(p.identifier) || reconnectTasks[p.identifier] != nil
                || linkWaiters[p.identifier] != nil { continue }
            switch p.state {
            case .disconnected:
                manager?.connect(p, options: nil)
            case .connected:
                // Durum geri yüklemesinden geldi: servis keşfini baştan yap.
                if openLinkEntry(p.identifier) == nil, !settingUp.contains(p.identifier) {
                    settingUp.insert(p.identifier)
                    p.discoverServices(ProtocolIds.discoveryServices)
                }
            default:
                break
            }
        }
    }

    /// PROTOCOL §3a: oturumu kapat, GATT bağlantısını kes; `didDisconnect` 1 sn sonra yeniden bağlanmayı planlar.
    /// Tek seferlik: sıfırlama sürerken (WDR / `didModifyServices` tekrarları) gelen yeni tetikler yok sayılır.
    private func resetSession(_ peripheral: CBPeripheral) {
        let id = peripheral.identifier
        links[id]?.close()
        guard !resetting.contains(id) else { return }
        if peripheral.state != .disconnected {
            resetting.insert(id)
            manager?.cancelPeripheralConnection(peripheral)
        } else {
            scheduleReconnect(id)
        }
    }

    /// Açılmakta olan (henüz `BlePeripheralLink` vermemiş) `openLink` beklemesini bağlantıyı kesmeden bitirir.
    func abortPendingLink(_ id: UUID) {
        failLinkWaiter(id, error: BleError.disconnected, cancelConnection: false)
    }

    /// Oturumu tazeler (teklif zaman aşımı): bağlantı kesilir; tanıdık PC'de 1 sn sonra bekleyen `connect`,
    /// tanıdık değilse sonraki tarama turu yeniden bağlanır (servis keşfi -> ön ek -> el sıkışma).
    func resetLink(_ id: UUID) {
        guard let p = peripherals[id] else { return }
        resetSession(p)
    }

    /// Daha önce görülen çevre birimi (kimliğinden); yoksa sistemin hatırladığı.
    func knownPeripheral(_ id: UUID) -> CBPeripheral? {
        peripherals[id] ?? manager?.retrievePeripherals(withIdentifiers: [id]).first
    }

    func closeLink(_ id: UUID) {
        if let p = peripherals[id], p.state != .disconnected {
            resetting.insert(id)
            dropConnection(p)
        }
    }

    // MARK: Yakındaki tarama (AirDrop benzeri, uygulama açıkken)

    private var wantsNearbyScan = false
    private var isPaused = false
    /// Paylaşım uzantısı açıkken ana uygulama BLE bağlantılarını bırakır (PROTOCOL §3a).
    private var isYielding = false
    private var scanRestartTask: Task<Void, Never>?

    /// Gönderme ekranı açıkken tarar; her ~4 sn tarama yeniden başlatılır (`allowDuplicates=false` aynı cihazı
    /// bir tarama oturumunda bir kez bildirir; başarısız bağlanılanlar böylece yeniden denenir).
    func startNearbyScan() {
        guard !isShutDown else { return }
        start()
        wantsNearbyScan = true
        applyScan()
        guard scanRestartTask == nil else { return }
        scanRestartTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 4_000_000_000)
                guard let self, self.wantsNearbyScan else { return }
                self.manager?.stopScan()
                self.applyScan()
            }
        }
    }

    func stopNearbyScan() {
        wantsNearbyScan = false
        scanRestartTask?.cancel()
        scanRestartTask = nil
        if scanWaiter == nil { manager?.stopScan() }
    }

    /// Eşleştirme (QR) sırasında yakındaki tarama ve otomatik bağlanma duraklatılır.
    func setPaused(_ paused: Bool) {
        isPaused = paused
        if paused {
            if scanWaiter == nil { manager?.stopScan() }
        } else {
            applyScan()
            ensureConnections()
        }
    }

    /// Paylaşım uzantısı açıldı (`true`): tüm bağlantılar kesilir, tarama durur; kapanınca (`false`) yeniden kurulur.
    func setYielding(_ yielding: Bool) {
        guard yielding != isYielding else { return }
        isYielding = yielding
        if yielding {
            if scanWaiter == nil { manager?.stopScan() }
            for p in peripherals.values where p.state != .disconnected {
                dropConnection(p)
            }
        } else {
            applyScan()
            ensureConnections()
        }
    }

    private func applyScan() {
        guard state == .poweredOn, wantsNearbyScan, !isShutDown, !isPaused, !isYielding, scanWaiter == nil else { return }
        manager?.scanForPeripherals(
            withServices: nil, options: [CBCentralManagerScanOptionAllowDuplicatesKey: false])
    }

    // MARK: Eşleştirme: tarama ve tek seferlik bağlantı

    /// İlk oluşturmadan sonra durum (`poweredOn` vb.) gelene kadar en çok `timeout` bekler (izin istemi dahil değil: 8 sn).
    func awaitReady(timeout: TimeInterval = 8) async {
        start()
        let deadline = Date().addingTimeInterval(timeout)
        while (state == .unknown || state == .resetting), Date() < deadline {
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
    }

    func discoverForPairing(pcName: String, timeout: TimeInterval) async throws -> CBPeripheral {
        await awaitReady()
        guard state == .poweredOn else { throw BleError.unavailable(state) }
        guard scanWaiter == nil else { throw BleError.alreadyScanning }
        tokenCounter += 1
        let token = tokenCounter
        scanToken = token
        scanPcName = pcName
        return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<CBPeripheral, Error>) in
            scanWaiter = cont
            manager?.scanForPeripherals(withServices: nil, options: nil)
            Task { [weak self] in
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                self?.expireScan(token)
            }
        }
    }

    /// iOS bağlı çevre birimlerini taramada bildirmez: zaten bağlı olan PC'yi tanımlayıcıdan (`preferred`) ya da
    /// WinDrop servisleriyle bağlı çevre birimlerinden bulur. `excluding` = başka PC'ye ait olduğu bilinenler;
    /// tanımlayıcı eşleşmesi yoksa ve tek aday kalırsa o döner (birden çoksa belirsiz: nil, tarama yapılır).
    func findConnectedPeripheral(preferred: UUID?, excluding: Set<UUID>) -> CBPeripheral? {
        guard let manager else { return nil }
        if let preferred, let p = manager.retrievePeripherals(withIdentifiers: [preferred]).first,
           p.state == .connected || p.state == .connecting {
            return p
        }
        let candidates = manager.retrieveConnectedPeripherals(withServices: ProtocolIds.discoveryServices)
            .filter { !excluding.contains($0.identifier) }
        return candidates.count == 1 ? candidates[0] : nil
    }

    /// Eşleştirme için çevre birimini temiz bir oturumla açar: mevcut oturum kapatılır, GATT bağlantısı kesilip
    /// hemen yeniden bağlanılır (aynı PC'ye ikinci bir GATT oturumu açılamaz; PC yeni ön eki baştan okur).
    func reopenForPairing(_ peripheral: CBPeripheral, timeout: TimeInterval) async throws -> BlePeripheralLink {
        guard state == .poweredOn else { throw BleError.unavailable(state) }
        let id = peripheral.identifier
        peripherals[id] = peripheral
        peripheral.delegate = self
        reconnectTasks.removeValue(forKey: id)?.cancel()   // eşleştirme sırasında otomatik yeniden bağlanma yok
        var attempt = 0
        while true {
            attempt += 1
            links.removeValue(forKey: id)?.close()
            if peripheral.state != .disconnected {
                resetting.insert(id)
                manager?.cancelPeripheralConnection(peripheral)
                let deadline = Date().addingTimeInterval(3)
                while peripheral.state != .disconnected, Date() < deadline {
                    try? await Task.sleep(nanoseconds: 50_000_000)
                }
                // `didDisconnect` geri çağrısı durum değişiminden biraz sonra gelebilir; yeni bekleyeni bozmasın.
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
            do {
                return try await openLink(to: peripheral, timeout: timeout)
            } catch BleError.disconnected where attempt < 2 {
                continue
            }
        }
    }

    private func expireScan(_ token: Int) {
        guard token == scanToken else { return }
        finishScan(.failure(BleError.timeout))
    }

    private func finishScan(_ result: Result<CBPeripheral, Error>) {
        guard let waiter = scanWaiter else { return }
        scanWaiter = nil
        manager?.stopScan()
        waiter.resume(with: result)
        applyScan()
    }

    /// Eşleştirme için bir çevre birimine bağlanır; GATT hazır olunca bağlantıyı döndürür.
    func openLink(to peripheral: CBPeripheral, timeout: TimeInterval) async throws -> BlePeripheralLink {
        guard !isShutDown else { throw BleError.disconnected }
        guard state == .poweredOn else { throw BleError.unavailable(state) }
        let id = peripheral.identifier
        peripherals[id] = peripheral
        peripheral.delegate = self
        tokenCounter += 1
        let token = tokenCounter
        reconnectTasks.removeValue(forKey: id)?.cancel()
        if linkWaiters[id] != nil {   // aynı çevre birimi için ikinci bekleme: eskisi bırakılır (süzülen continuation kalmasın)
            failLinkWaiter(id, error: BleError.disconnected, cancelConnection: false)
        }
        return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<BlePeripheralLink, Error>) in
            linkWaiters[id] = cont
            linkWaiterTokens[id] = token
            if peripheral.state == .disconnected {
                manager?.connect(peripheral, options: nil)
            } else if peripheral.state == .connected {
                settingUp.insert(id)
                peripheral.discoverServices(ProtocolIds.discoveryServices)
            }
            Task { [weak self] in
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                self?.expireLinkWaiter(id, token: token)
            }
        }
    }

    private func expireLinkWaiter(_ id: UUID, token: Int) {
        guard linkWaiterTokens[id] == token else { return }
        failLinkWaiter(id, error: BleError.timeout, cancelConnection: true)
    }

    private func failLinkWaiter(_ id: UUID, error: Error, cancelConnection: Bool) {
        linkWaiterTokens[id] = nil
        guard let waiter = linkWaiters.removeValue(forKey: id) else { return }
        if cancelConnection, let p = peripherals[id], p.state != .disconnected {
            manager?.cancelPeripheralConnection(p)
        }
        waiter.resume(throwing: error)
    }

    // MARK: İç yardımcılar

    private func abortSetup(_ peripheral: CBPeripheral, error: Error) {
        let id = peripheral.identifier
        settingUp.remove(id)
        pendingCharacteristics[id] = nil
        if linkWaiters[id] != nil {
            failLinkWaiter(id, error: error, cancelConnection: true)
        } else if peripheral.state != .disconnected {
            manager?.cancelPeripheralConnection(peripheral)
        }
    }

    /// 1 sn sonra TEK SEFERLİK yeniden bağlanma; bekleyen varsa yenisi eklenmez (didDisconnect / WDR /
    /// didModifyServices / didFailToConnect art arda gelse de çevre birimi başına tek `connect`).
    private func scheduleReconnect(_ id: UUID) {
        guard wanted.contains(id), reconnectTasks[id] == nil else { return }
        reconnectTasks[id] = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            guard let self, !Task.isCancelled else { return }
            self.reconnectTasks[id] = nil
            self.reconnectNow(id)
        }
    }

    private func reconnectNow(_ id: UUID) {
        guard state == .poweredOn, !isPaused, !isYielding, wanted.contains(id), !resetting.contains(id),
              openLinkEntry(id) == nil, linkWaiters[id] == nil,
              let p = peripherals[id], p.state == .disconnected else { return }
        manager?.connect(p, options: nil)
    }

    private func tearDownAllLinks() {
        resetting.removeAll()
        reconnectTasks.values.forEach { $0.cancel() }
        reconnectTasks.removeAll()
        for (id, link) in links {
            link.close()
            delegate?.bleCentral(self, didClose: id)
        }
        links.removeAll()
        pendingCharacteristics.removeAll()
        settingUp.removeAll()
        for id in Array(linkWaiters.keys) {
            failLinkWaiter(id, error: BleError.disconnected, cancelConnection: false)
        }
        if scanWaiter != nil { finishScan(.failure(BleError.unavailable(state))) }
    }
}

// MARK: CBCentralManagerDelegate

extension BleCentral: @preconcurrency CBCentralManagerDelegate {
    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        state = central.state
        if central.state == .poweredOn {
            ensureConnections()
            applyScan()
        } else {
            tearDownAllLinks()
        }
        delegate?.bleCentral(self, didUpdateState: central.state)
    }

    func centralManager(_ central: CBCentralManager, willRestoreState dict: [String: Any]) {
        // iOS uygulamayı arka planda yeniden başlattı (BLE olayı): bekleyen/bağlı çevre birimleri geri verilir.
        // Delegate burada atanmazsa bağlı olanların bildirimleri düşer.
        guard let restored = dict[CBCentralManagerRestoredStatePeripheralsKey] as? [CBPeripheral] else { return }
        for p in restored {
            peripherals[p.identifier] = p
            p.delegate = self
        }
        // Bağlantılar `centralManagerDidUpdateState` -> `ensureConnections` ile sürdürülür:
        // hâlâ bağlı olanlar servis keşfinden, kopmuş olanlar yeniden `connect`'ten devam eder.
    }

    func centralManager(
        _ central: CBCentralManager,
        didDiscover peripheral: CBPeripheral,
        advertisementData: [String: Any],
        rssi RSSI: NSNumber
    ) {
        if scanWaiter != nil {
            if ProtocolIds.advertisementMatches(advertisementData, pcName: scanPcName) {
                peripherals[peripheral.identifier] = peripheral
                finishScan(.success(peripheral))
            }
            return
        }
        guard wantsNearbyScan, !isPaused else { return }
        // Filtresiz tarama: yalnız WinDrop reklamları (servis UUID'si veya üretici verisi işareti).
        guard ProtocolIds.advertisementMatches(advertisementData, pcName: "") else { return }
        peripherals[peripheral.identifier] = peripheral
        delegate?.bleCentral(self, didDiscover: peripheral)
    }

    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        peripheral.delegate = self
        settingUp.insert(peripheral.identifier)
        peripheral.discoverServices(ProtocolIds.discoveryServices)
    }

    func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        let id = peripheral.identifier
        settingUp.remove(id)
        resetting.remove(id)
        if linkWaiters[id] != nil {
            failLinkWaiter(id, error: error ?? BleError.disconnected, cancelConnection: false)
        }
        scheduleReconnect(id)
    }

    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        let id = peripheral.identifier
        settingUp.remove(id)
        resetting.remove(id)
        pendingCharacteristics[id] = nil
        if let link = links.removeValue(forKey: id) {
            link.close()
        }
        if linkWaiters[id] != nil {
            failLinkWaiter(id, error: error ?? BleError.disconnected, cancelConnection: false)
        }
        delegate?.bleCentral(self, didClose: id)
        scheduleReconnect(id)
    }
}

// MARK: CBPeripheralDelegate

extension BleCentral: @preconcurrency CBPeripheralDelegate {
    /// PC'de WinDrop kapanıp açılınca LE bağlantısı açık kalır, yalnız GATT servisi kaybolur: oturum ölüdür, baştan kurulur.
    func peripheral(_ peripheral: CBPeripheral, didModifyServices invalidatedServices: [CBService]) {
        guard invalidatedServices.contains(where: {
            $0.uuid == ProtocolIds.serviceUUID || ProtocolIds.isWinDropService($0.uuid)
        }) else { return }
        let id = peripheral.identifier
        settingUp.remove(id)
        pendingCharacteristics[id] = nil
        if linkWaiters[id] != nil {
            failLinkWaiter(id, error: BleError.disconnected, cancelConnection: false)
        }
        resetSession(peripheral)
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        if let error {
            abortSetup(peripheral, error: error)
            return
        }
        let services = peripheral.services ?? []
        // Önce ana servis, yoksa yedek (SIG tabanlı 32 bit) servis; karakteristikler aynıdır.
        guard let service = services.first(where: { $0.uuid == ProtocolIds.serviceUUID })
                ?? services.first(where: { ProtocolIds.isWinDropService($0.uuid) }) else {
            abortSetup(peripheral, error: BleError.serviceNotFound)
            return
        }
        peripheral.discoverCharacteristics([ProtocolIds.c2pUUID, ProtocolIds.p2cUUID], for: service)
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        if let error {
            abortSetup(peripheral, error: error)
            return
        }
        let characteristics = service.characteristics ?? []
        guard let c2p = characteristics.first(where: { $0.uuid == ProtocolIds.c2pUUID }),
              let p2c = characteristics.first(where: { $0.uuid == ProtocolIds.p2cUUID }) else {
            abortSetup(peripheral, error: BleError.serviceNotFound)
            return
        }
        pendingCharacteristics[peripheral.identifier] = (c2p, p2c)
        peripheral.setNotifyValue(true, for: p2c)
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateNotificationStateFor characteristic: CBCharacteristic, error: Error?) {
        let id = peripheral.identifier
        if let error {
            abortSetup(peripheral, error: error)
            return
        }
        guard characteristic.uuid == ProtocolIds.p2cUUID, characteristic.isNotifying,
              openLinkEntry(id) == nil, let pending = pendingCharacteristics[id] else { return }
        pendingCharacteristics[id] = nil
        settingUp.remove(id)
        let link = BlePeripheralLink(peripheral: peripheral, c2p: pending.c2p)
        links[id] = link
        if let waiter = linkWaiters.removeValue(forKey: id) {
            linkWaiterTokens[id] = nil
            waiter.resume(returning: link)
        } else {
            delegate?.bleCentral(self, didOpen: link)
        }
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard error == nil, characteristic.uuid == ProtocolIds.p2cUUID, let value = characteristic.value else { return }
        // `WDU` / `WDR` (tam 3 bayt ASCII) parçalama başlığından önce denetlenir.
        if value == ProtocolIds.unknownMarker {
            // PC IK'daki statik anahtarımızı tanımıyor (DECISIONS 2): işaret kaydedilir, bekleyen el sıkışma hata ile
            // uyandırılır (GATT bağlantısını PC kapatır); `SignalHub` kaydı siler ve XX ile bir kez yeniden dener.
            unknownPeerMarkers[peripheral.identifier] = Date()
            links[peripheral.identifier]?.close()
            return
        }
        if value == ProtocolIds.resetMarker {
            resetSession(peripheral)
            return
        }
        links[peripheral.identifier]?.receive(value)
    }

    func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        guard characteristic.uuid == ProtocolIds.c2pUUID else { return }
        links[peripheral.identifier]?.didWrite(error: error)
    }

}
