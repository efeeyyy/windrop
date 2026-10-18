import SwiftUI

struct PairView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var cameraProblem: CameraProblem?

    private enum CameraProblem {
        case denied
        case unavailable
    }

    var body: some View {
        let pairing = model.pairing
        NavigationStack {
            VStack(spacing: 20) {
                if case .scanning = pairing.phase {
                    scannerArea(pairing)
                }
                statusArea(pairing)
                Spacer(minLength: 0)
            }
            .padding()
            .themedBackground()
            .navigationTitle("Eşleştir")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Kapat") { dismiss() }
                }
            }
        }
        .onAppear { pairing.reset() }
        .onDisappear { pairing.cancel() }
    }

    @ViewBuilder
    private func scannerArea(_ pairing: PairingController) -> some View {
        if let problem = cameraProblem {
            VStack(spacing: 12) {
                Image(systemName: "camera.fill").font(.largeTitle)
                Text(problem == .denied
                     ? "Kamera izni verilmedi. QR kodu okumak için Ayarlar'dan kamera iznini açın."
                     : "Kamera kullanılamıyor.")
                    .multilineTextAlignment(.center)
                if problem == .denied {
                    Button("Ayarlar'ı aç") {
                        if let url = URL(string: UIApplication.openSettingsURLString) {
                            UIApplication.shared.open(url)
                        }
                    }
                    .buttonStyle(.bordered)
                }
            }
            .frame(maxWidth: .infinity, minHeight: 280)
        } else {
            QRScannerView(
                onCode: { code in pairing.handleScanned(code) },
                onUnavailable: { denied in cameraProblem = denied ? .denied : .unavailable })
                .frame(maxWidth: 480, minHeight: 280, maxHeight: 420)
                .clipShape(RoundedRectangle(cornerRadius: 16))
        }
    }

    @ViewBuilder
    private func statusArea(_ pairing: PairingController) -> some View {
        switch pairing.phase {
        case .scanning:
            Text("Windows'taki WinDrop'ta gösterilen eşleştirme QR kodunu kameraya gösterin.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
        case .searching(let name):
            progress("\(name) aranıyor…")
        case .connecting(let name):
            progress("\(name) ile bağlantı kuruluyor…")
        case .waitingApproval(let name, let code):
            VStack(spacing: 12) {
                progress("\(name) bilgisayarında eşleşmeyi onaylayın.")
                Text("Doğrulama kodu").font(.footnote).foregroundStyle(.secondary)
                Text(code)
                    .font(.system(size: 40, weight: .semibold, design: .monospaced))
                    .accessibilityLabel("Doğrulama kodu \(code.map(String.init).joined(separator: " "))")
                Text("Bilgisayardaki kodla aynıysa onaylayın.")
                    .font(.footnote).foregroundStyle(.secondary)
            }
        case .succeeded(let name):
            VStack(spacing: 12) {
                Image(systemName: "checkmark.circle.fill").font(.system(size: 48)).foregroundStyle(.green)
                Text("\(name) ile eşleşti.").font(.headline)
                Button("Bitti") { dismiss() }.buttonStyle(.borderedProminent)
            }
        case .failed(let message):
            VStack(spacing: 12) {
                Image(systemName: "xmark.octagon.fill").font(.system(size: 40)).foregroundStyle(.red)
                Text(message).multilineTextAlignment(.center)
                Button("Tekrar dene") { pairing.reset() }.buttonStyle(.borderedProminent)
            }
        }
    }

    private func progress(_ text: String) -> some View {
        VStack(spacing: 12) {
            ProgressView()
            Text(text).multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, minHeight: 120)
    }
}
