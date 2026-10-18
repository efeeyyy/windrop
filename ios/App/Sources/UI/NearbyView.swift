import SwiftUI

/// Yakındaki bilgisayarlar sayfası (AirDrop benzeri): tarama sayfa açılınca başlar, PC'ler kendiliğinden belirir,
/// dokununca gönderilir. Bluetooth izni ilk burada (ilk Gönder) istenir.
struct NearbyView: View {
    let session: SendSession
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var showHint = false
    @State private var errorText: String?
    @AppStorage(DeviceInfo.nameHintDismissedKey) private var nameHintDismissed = false
    /// Ad hâlâ varsayılan ("iPhone"/"iPad") ve ipucu kapatılmadıysa küçük, kapatılabilir ipucu gösterilir.
    private var showNameHint: Bool { !nameHintDismissed && DeviceInfo.isDefaultName }

    private let columns = [GridItem(.adaptive(minimum: 110), spacing: 16)]

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 20) {
                    if showNameHint {
                        HStack(alignment: .top, spacing: 8) {
                            Image(systemName: "info.circle").foregroundStyle(.secondary)
                            Text("Bilgisayarda '\(DeviceInfo.defaultName)' olarak görünüyorsunuz — Ayarlar'dan adınızı değiştirin.")
                                .font(.footnote)
                            Spacer(minLength: 4)
                            Button {
                                nameHintDismissed = true
                            } label: {
                                Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                            }
                            .buttonStyle(.borderless)
                            .accessibilityLabel("Kapat")
                        }
                        .padding(10)
                        .background(Color.secondary.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
                    }
                    Text("\(session.files.count) dosya gönderilecek")
                        .font(.subheadline).foregroundStyle(.secondary)
                    if let warning = bluetoothWarning {
                        Label(warning, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                    }
                    let peers = model.hub.nearbyList
                    if peers.isEmpty {
                        VStack(spacing: 12) {
                            ProgressView()
                            Text("Yakındaki bilgisayarlar aranıyor…").foregroundStyle(.secondary)
                            if showHint {
                                Text("Her iki cihazda da WinDrop'un açık olduğundan ve Bluetooth'un açık olduğundan emin olun.")
                                    .font(.footnote).foregroundStyle(.secondary)
                                    .multilineTextAlignment(.center)
                            }
                        }
                        .padding(.top, 40)
                    } else {
                        LazyVGrid(columns: columns, spacing: 20) {
                            ForEach(peers) { peer in
                                PeerTile(peer: peer) { send(to: peer) }
                            }
                        }
                    }
                    if let errorText {
                        Text(errorText).foregroundStyle(.red).font(.footnote)
                    }
                }
                .padding()
            }
            .navigationTitle("Gönder")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Vazgeç") { cancelAndClose() }
                }
            }
        }
        .task {
            model.hub.startNearby()
            LocalNetworkPermission.shared.trigger()   // Dosya gönder: Bluetooth ile birlikte yerel ağ izni
            try? await Task.sleep(nanoseconds: 5_000_000_000)
            showHint = true
        }
    }

    private var bluetoothWarning: String? {
        switch model.hub.bluetoothState {
        case .poweredOff: return String(localized: "Bluetooth kapalı. Bilgisayarı bulmak için açın.")
        case .unauthorized: return String(localized: "WinDrop'un Bluetooth izni yok. Ayarlar'dan izin verin.")
        case .unsupported: return String(localized: "Bu cihaz Bluetooth LE desteklemiyor.")
        default: return nil
        }
    }

    private func send(to peer: NearbyPeer) {
        if model.transfers.startSending(files: session.files, to: peer) != nil {
            model.selectedTab = .transfers
            dismiss()
        } else {
            errorText = String(localized: "\(peer.name) şu an yakında değil. Tekrar deneyin.")
        }
    }

    private func cancelAndClose() {
        FilePreparer.release(session.files)
        dismiss()
    }
}
