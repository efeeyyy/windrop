import AVFoundation
import Foundation
import Network
import Observation

/// Wi-Fi durumu (`hello.wifi` / `status.wifi`, PROTOCOL §4) ve Bluetooth ses çıkışı (`btAudio`).
@MainActor
@Observable
final class NetworkMonitor {
    private(set) var isWifiConnected = false

    @ObservationIgnored var onChange: (() -> Void)?
    @ObservationIgnored private let monitor = NWPathMonitor()
    @ObservationIgnored private var started = false

    func start() {
        guard !started else { return }
        started = true
        monitor.pathUpdateHandler = { [weak self] path in
            // Wi-Fi arayüzü var mı (internet olmasa da: etkin nokta ağı). Doğrulanacak: etkin nokta
            // (internetsiz) ağında `availableInterfaces` Wi-Fi'ı listeliyor mu.
            let wifi = path.availableInterfaces.contains { $0.type == .wifi }
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

enum AudioRoute {
    /// Bluetooth ses çıkışı (A2DP/HFP/LE) etkin mi? (PROTOCOL §6.4: BLE yolu yalnız `btAudio=false` iken.)
    static func bluetoothAudioActive() -> Bool {
        let bluetoothPorts: [AVAudioSession.Port] = [.bluetoothA2DP, .bluetoothHFP, .bluetoothLE]
        return AVAudioSession.sharedInstance().currentRoute.outputs.contains { bluetoothPorts.contains($0.portType) }
    }
}
