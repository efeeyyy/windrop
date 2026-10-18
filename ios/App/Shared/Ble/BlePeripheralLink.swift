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
            case .poweredOff: return "Bluetooth kapalı."
            case .unauthorized: return "WinDrop'un Bluetooth izni yok."
            case .unsupported: return "Bu cihaz Bluetooth LE desteklemiyor."
            default: return "Bluetooth hazır değil."
            }
        case .timeout: return "Bilgisayar bulunamadı veya yanıt vermedi."
        case .disconnected: return "Bluetooth bağlantısı koptu."
        case .serviceNotFound: return "Bilgisayarda WinDrop servisi bulunamadı."
        case .alreadyScanning: return "Başka bir tarama sürüyor."
        }
    }
}

/// Bir PC ile açık GATT bağlantısı: `c2p` (Write Without Response) ve `p2c` (Notify).
/// Akış kontrolü: `canSendWriteWithoutResponse` / `peripheralIsReady(toSendWriteWithoutResponse:)`.
@MainActor
final class BlePeripheralLink {
    let peripheral: CBPeripheral
    private let c2p: CBCharacteristic
    /// Gelen `p2c` bildirimleri (ham GATT yükleri).
    let inbox = AsyncMailbox<Data>()
    private(set) var isOpen = true
    private var readyWaiter: CheckedContinuation<Void, Error>?
    private var writeWaiter: CheckedContinuation<Void, Error>?

    init(peripheral: CBPeripheral, c2p: CBCharacteristic) {
        self.peripheral = peripheral
        self.c2p = c2p
    }

    var peripheralID: UUID { peripheral.identifier }

    /// ATT MTU = en çok yazılabilir bayt + 3 (PROTOCOL §9a: parça yükü ≤ MTU − 3 − 1).
    var mtu: Int { peripheral.maximumWriteValueLength(for: .withoutResponse) + 3 }

    /// Yükleri sırayla yazar; kuyruk doluysa `peripheralIsReady` gelene kadar bekler.
    /// Aynı anda tek gönderici olmalı (çağıran `AsyncLock` ile sıralar).
    func send(_ chunks: [Data]) async throws {
        for chunk in chunks {
            while !peripheral.canSendWriteWithoutResponse {
                guard isOpen else { throw BleError.disconnected }
                try await waitUntilReady()
            }
            guard isOpen else { throw BleError.disconnected }
            peripheral.writeValue(chunk, for: c2p, type: .withoutResponse)
        }
    }

    /// Onaylı yazma (Write With Response): karşı taraf aldığını bildirene kadar bekler.
    func sendReliable(_ payload: Data) async throws {
        guard isOpen else { throw BleError.disconnected }
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            writeWaiter = cont
            peripheral.writeValue(payload, for: c2p, type: .withResponse)
        }
    }

    private func waitUntilReady() async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            readyWaiter = cont
        }
    }

    // MARK: BleCentral tarafından çağrılır

    func peripheralIsReadyToWrite() {
        let waiter = readyWaiter
        readyWaiter = nil
        waiter?.resume()
    }

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
        let waiter = readyWaiter
        readyWaiter = nil
        waiter?.resume(throwing: error)
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

    func write(_ payload: Data) async throws {
        try await link.send([payload])
    }

    func writeReliable(_ payload: Data) async throws {
        try await link.sendReliable(payload)
    }

    func read() async throws -> Data? {
        try await link.nextPayload()
    }
}
