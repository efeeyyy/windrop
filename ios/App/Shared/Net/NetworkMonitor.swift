import Foundation
import Network
import Observation

/// Wi-Fi durumu (`hello.wifi` / `status.wifi`, PROTOCOL §4). Bluetooth ses için bkz. `BluetoothAudioMonitor`.
@MainActor
@Observable
final class NetworkMonitor {
    private(set) var isWifiConnected = false

    @ObservationIgnored var onChange: (() -> Void)?
    /// Yalnız Wi-Fi arayüzünü izler: yol `.satisfied` ise Wi-Fi bir ağa bağlıdır (internet olmasa da: etkin nokta ağı).
    /// Radyo kapalıyken ya da ağa katılmamışken `.unsatisfied`. (`availableInterfaces` radyo açılır açılmaz Wi-Fi'ı
    /// listeleyebildiği için "bağlandı" ölçütü olarak yetmez; gerçek cihazda doğrulanacak.)
    @ObservationIgnored private let monitor = NWPathMonitor(requiredInterfaceType: .wifi)
    @ObservationIgnored private var started = false

    func start() {
        guard !started else { return }
        started = true
        monitor.pathUpdateHandler = { [weak self] path in
            let wifi = path.status == .satisfied
            Task { @MainActor in
                self?.update(wifi: wifi)
            }
        }
        monitor.start(queue: DispatchQueue(label: "com.efe.windrop.netmonitor"))
    }

    private func update(wifi: Bool) {
        guard wifi != isWifiConnected else { return }
        isWifiConnected = wifi
        onChange?()
    }
}
