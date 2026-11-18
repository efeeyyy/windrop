import AVFoundation
import Foundation
import Observation

/// Bluetooth ses çıkışı (`hello.btAudio` / `status.btAudio`, PROTOCOL §5, §7.3): çıkış rotasında A2DP/HFP/LE var VE
/// başka bir uygulama ses çalıyor. Yalnız OKUR: uygulama kendi ses oturumunu etkinleştirmez (başkasının müziğini kesmesin).
/// `isOtherAudioPlaying` için bildirim güvenilir olmadığından rota bildirimine ek olarak hafif bir yoklama vardır.
@MainActor
@Observable
final class BluetoothAudioMonitor {
    private(set) var isActive = false

    @ObservationIgnored var onChange: (() -> Void)?
    @ObservationIgnored private var started = false
    @ObservationIgnored private var observers: [NSObjectProtocol] = []
    @ObservationIgnored private var pollTask: Task<Void, Never>?

    private static let bluetoothPorts: [AVAudioSession.Port] = [.bluetoothA2DP, .bluetoothHFP, .bluetoothLE]
    private static let pollInterval: UInt64 = 5_000_000_000

    /// Şu anki değer (her çağrıda oturumdan taze okunur; karar anlarında bu kullanılır).
    func currentValue() -> Bool {
        let session = AVAudioSession.sharedInstance()
        let bluetooth = session.currentRoute.outputs.contains { Self.bluetoothPorts.contains($0.portType) }
        return bluetooth && session.isOtherAudioPlaying
    }

    func start() {
        guard !started else { return }
        started = true
        isActive = currentValue()
        let center = NotificationCenter.default
        let names: [Notification.Name] = [
            AVAudioSession.routeChangeNotification,
            AVAudioSession.silenceSecondaryAudioHintNotification,
        ]
        observers = names.map { name in
            center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.refresh() }
            }
        }
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: Self.pollInterval)
                self?.refresh()
            }
        }
    }

    private func refresh() {
        let value = currentValue()
        guard value != isActive else { return }
        isActive = value
        onChange?()   // `status` gönderimi
    }
}
