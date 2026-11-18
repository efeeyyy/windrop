import CoreBluetooth
import Foundation
@preconcurrency import WinDropKit

enum BleError: Error, LocalizedError {
    case unavailable(CBManagerState)
    case timeout
    case disconnected
    case serviceNotFound
    case alreadyScanning

    var errorDescription: String? {
        switch self {
        case .unavailable(let state):
            switch state {
            case .poweredOff: return String(localized: "Bluetooth kapalı.")
            case .unauthorized: return String(localized: "WinDrop'un Bluetooth izni yok.")
            case .unsupported: return String(localized: "Bu cihaz Bluetooth LE desteklemiyor.")
            default: return String(localized: "Bluetooth hazır değil.")
            }
        case .timeout: return String(localized: "Bilgisayar bulunamadı veya yanıt vermedi.")
        case .disconnected: return String(localized: "Bluetooth bağlantısı koptu.")
        case .serviceNotFound: return String(localized: "Bilgisayarda WinDrop servisi bulunamadı.")
        case .alreadyScanning: return String(localized: "Başka bir tarama sürüyor.")
        }
    }
}

/// Bir PC ile açık GATT bağlantısı: `c2p` (onaylı yazma, Write With Response) ve `p2c` (Notify).
/// Tüm yazmalar onaylıdır ve sıralıdır (FIFO kuyruk); her onay en çok `writeTimeout` beklenir.
@MainActor
final class BlePeripheralLink {
    /// DECISIONS 6: onaylı BLE yazması zaman aşımı.
    static let writeTimeout: TimeInterval = 5

    let peripheral: CBPeripheral
    private let c2p: CBCharacteristic
    /// Gelen `p2c` bildirimleri (ham GATT yükleri).
    let inbox = AsyncMailbox<Data>()
    private(set) var isOpen = true
    private var writeWaiter: CheckedContinuation<Void, Error>?
    private var writeToken = 0
    /// Yazmaları sıraya koyar: ikinci çağrı birincinin onayını bekler (`writeWaiter` asla üzerine yazılmaz).
    private let writeLock = AsyncLock()

    init(peripheral: CBPeripheral, c2p: CBCharacteristic) {
        self.peripheral = peripheral
        self.c2p = c2p
    }

    var peripheralID: UUID { peripheral.identifier }

    /// ATT MTU = en çok yazılabilir bayt + 3 (PROTOCOL §4.2: parça yükü ≤ MTU − 3 − 1).
    var mtu: Int { peripheral.maximumWriteValueLength(for: .withoutResponse) + 3 }

    /// Onaylı yazma (Write With Response): karşı taraf aldığını bildirene kadar bekler. Eşzamanlı çağrılar sıraya girer.
    /// Onay `writeTimeout` içinde gelmezse bağlantı `BleError.timeout` ile kapatılır (oturum düşer, kilit tutulmaz).
    func sendReliable(_ payload: Data) async throws {
        try await writeLock.withLock {
            guard isOpen else { throw BleError.disconnected }
            writeToken += 1
            let token = writeToken
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                writeWaiter = cont
                peripheral.writeValue(payload, for: c2p, type: .withResponse)
                Task { [weak self] in
                    try? await Task.sleep(nanoseconds: UInt64(Self.writeTimeout * 1_000_000_000))
                    self?.expireWrite(token)
                }
            }
        }
    }

    private func expireWrite(_ token: Int) {
        guard token == writeToken, writeWaiter != nil else { return }
        close(error: BleError.timeout)   // bekleyen yazıcıyı hata ile uyandırır
    }

    // MARK: BleCentral tarafından çağrılır

    func receive(_ payload: Data) {
        inbox.push(payload)
    }

    func didWrite(error: Error?) {
        let waiter = writeWaiter
        writeWaiter = nil
        if let error { waiter?.resume(throwing: error) } else { waiter?.resume() }
    }

    func close(error: Error = BleError.disconnected) {
        guard isOpen else { return }
        isOpen = false
        inbox.fail(error)
        let writer = writeWaiter
        writeWaiter = nil
        writer?.resume(throwing: error)
    }
}

extension BlePeripheralLink {
    /// Sıradaki `p2c` yükü; bağlantı kapandıysa nil (Kit `BleLink.read` sözleşmesi).
    func nextPayload() async throws -> Data? {
        do {
            return try await inbox.pop(timeout: nil)
        } catch BleError.disconnected {
            return nil
        }
    }
}

/// `BlePeripheralLink`'i (ana aktör) WinDropKit `BleLink`'ine uyarlar.
/// `mtu` kurulumda bir kez okunur (bağlantı süresince sabit); yazmalar ana aktöre atlar ve sıralıdır
/// (Kit tek yazıcı varsayar; sinyal göndermeleri `SignalHub` tarafında `AsyncLock` ile sıralanır).
final class BleLinkAdapter: BleLink, @unchecked Sendable {
    private let link: BlePeripheralLink
    let mtu: Int

    @MainActor
    init(link: BlePeripheralLink) {
        self.link = link
        self.mtu = max(23, link.mtu)
    }

    /// Tüm `c2p` yazmaları onaylı gider: cihaz testinde (2026-10-18) Windows GATT sunucusu yanıtsız
    /// yazmaların bir kısmını düşürdü (ön ek kaybı, "sıra no atlaması"). BLE yalnız küçük mesaj taşıdığı için
    /// hız kaybı önemsiz.
    func write(_ payload: Data) async throws {
        try await link.sendReliable(payload)
    }

    func writeReliable(_ payload: Data) async throws {
        try await write(payload)
    }

    func read() async throws -> Data? {
        try await link.nextPayload()
    }
}
