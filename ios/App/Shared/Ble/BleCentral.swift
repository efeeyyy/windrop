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
        guard manager == nil else { return }
        var options: [String: Any] = [CBCentralManagerOptionShowPowerAlertKey: true]
        if !ProtocolIds.isExtension {
            // Uzantı geri yükleme kimliği kullanmaz: yalnız ana uygulama arka planda yeniden doğurulur.
            options[CBCentralManagerOptionRestoreIdentifierKey] = ProtocolIds.restoreIdentifier
        }
        manager = CBCentralManager(delegate: self, queue: .main, options: options)
    }

    // MARK: Bekleyen bağlantılar (eşli PC'ler)

    /// Eşli PC çevre birimi tanımlayıcıları. Listeden çıkanların bağlantısı kesilir.
    func setWanted(_ ids: Set<UUID>) {
        let removed = wanted.subtracting(ids)
        wanted = ids
        for id in removed {
            if let p = peripherals[id], p.state != .disconnected {
                manager?.cancelPeripheralConnection(p)
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
            switch p.state {
            case .disconnected:
                manager?.connect(p, options: nil)
            case .connected:
                // Durum geri yüklemesinden geldi: servis keşfini baştan yap.
                if links[p.identifier] == nil, !settingUp.contains(p.identifier) {
                    settingUp.insert(p.identifier)
                    p.discoverServices(ProtocolIds.discoveryServices)
                }
            default:
                break
            }
        }
    }

    /// PROTOCOL §3a: oturumu kapat, GATT bağlantısını kes; `didDisconnect` 1 sn sonra yeniden bağlanmayı planlar.
    private func resetSession(_ peripheral: CBPeripheral) {
        links[peripheral.identifier]?.close()
        if peripheral.state != .disconnected {
            manager?.cancelPeripheralConnection(peripheral)
        } else {
            scheduleReconnect(peripheral.identifier)
        }
    }

    /// Oturumu tazeler (teklif zaman aşımı): bağlantı kesilir; tanıdık PC'de 1 sn sonra bekleyen `connect`,
    /// tanıdık değilse sonraki tarama turu yeniden bağlanır (servis keşfi -> ön ek -> el sıkışma).
    func resetLink(_ id: UUID) {
        guard let p = peripherals[id] else { return }
        resetSession(p)
    }

    func closeLink(_ id: UUID) {
        if let p = peripherals[id], p.state != .disconnected {
            manager?.cancelPeripheralConnection(p)
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
                manager?.cancelPeripheralConnection(p)
            }
        } else {
            applyScan()
            ensureConnections()
        }
    }

    private func applyScan() {
        guard state == .poweredOn, wantsNearbyScan, !isPaused, !isYielding, scanWaiter == nil else { return }
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
        guard state == .poweredOn else { throw BleError.unavailable(state) }
        let id = peripheral.identifier
        peripherals[id] = peripheral
        peripheral.delegate = self
        tokenCounter += 1
        let token = tokenCounter
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

    private func scheduleReconnect(_ id: UUID) {
        guard wanted.contains(id) else { return }
        Task { [weak self] in
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            self?.reconnectNow(id)
        }
    }

    private func reconnectNow(_ id: UUID) {
        guard state == .poweredOn, !isYielding, wanted.contains(id), let p = peripherals[id], p.state == .disconnected else { return }
        manager?.connect(p, options: nil)
    }

    private func tearDownAllLinks() {
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
        if linkWaiters[id] != nil {
            failLinkWaiter(id, error: error ?? BleError.disconnected, cancelConnection: false)
        }
        scheduleReconnect(id)
    }

    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        let id = peripheral.identifier
        settingUp.remove(id)
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
              links[id] == nil, let pending = pendingCharacteristics[id] else { return }
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
        // `WDR` (tam 3 bayt) parçalama başlığından önce denetlenir.
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

    func peripheralIsReady(toSendWriteWithoutResponse peripheral: CBPeripheral) {
        links[peripheral.identifier]?.peripheralIsReadyToWrite()
    }
}
