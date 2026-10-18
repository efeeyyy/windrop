import SwiftUI

/// Paylaşım menüsü arayüzü: yakındaki bilgisayarlar belirir, dokununca gönderir, ilerleme gösterir, bitince kapanır.
struct ShareView: View {
    let model: ShareModel

    private let columns = [GridItem(.adaptive(minimum: 110), spacing: 16)]

    var body: some View {
        NavigationStack {
            Group {
                if let item = model.item {
                    SendingView(item: item, model: model)
                } else if case .failed(let message) = model.phase {
                    VStack(spacing: 12) {
                        Image(systemName: "exclamationmark.triangle.fill").font(.largeTitle).foregroundStyle(.orange)
                        Text(message).multilineTextAlignment(.center)
                        Button("Kapat") { model.cancel() }.buttonStyle(.borderedProminent)
                    }
                    .padding()
                } else {
                    chooser
                }
            }
            .navigationTitle("WinDrop")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Vazgeç") { model.cancel() }
                }
            }
        }
        .task { await model.start() }
    }

    private var chooser: some View {
        ScrollView {
            VStack(spacing: 20) {
                summary
                if let warning = bluetoothWarning {
                    Label(warning, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                }
                let peers = model.hub.nearbyList
                if peers.isEmpty {
                    VStack(spacing: 12) {
                        ProgressView()
                        Text("Yakındaki bilgisayarlar aranıyor…").foregroundStyle(.secondary)
                        Text("Bilgisayarda WinDrop'un açık ve Bluetooth'un açık olduğundan emin olun.")
                            .font(.footnote).foregroundStyle(.secondary).multilineTextAlignment(.center)
                    }
                    .padding(.top, 40)
                } else {
                    LazyVGrid(columns: columns, spacing: 20) {
                        ForEach(peers) { peer in
                            PeerTile(peer: peer) { model.send(to: peer) }
                                .disabled(model.phase != .choosing)
                        }
                    }
                    if !peers.allSatisfy(\.isKnown) {
                        Text("Tanıdık olmayan bilgisayarda gönderimi onaylamanız gerekir; ekrandaki kod iki yerde aynı olmalı.")
                            .font(.footnote).foregroundStyle(.secondary).multilineTextAlignment(.center)
                    }
                }
                if let errorText = model.errorText {
                    Text(errorText).foregroundStyle(.red).font(.footnote)
                }
            }
            .padding()
        }
    }

    @ViewBuilder private var summary: some View {
        if model.phase == .loading {
            HStack {
                ProgressView()
                Text("Hazırlanıyor… \(model.loadedCount)/\(model.totalCount)")
            }
            .font(.subheadline).foregroundStyle(.secondary)
        } else {
            let count = model.totalCount - model.skippedCount
            Text("\(count == 1 ? "1 dosya" : "\(count) dosya") gönderilecek")
                .font(.subheadline).foregroundStyle(.secondary)
        }
    }

    private var bluetoothWarning: String? {
        switch model.hub.bluetoothState {
        case .poweredOff: return "Bluetooth kapalı. Bilgisayarı bulmak için açın."
        case .unauthorized: return "WinDrop'un Bluetooth izni yok. Ayarlar'dan izin verin."
        case .unsupported: return "Bu cihaz Bluetooth LE desteklemiyor."
        default: return nil
        }
    }
}

/// Gönderim sürerken: durum, ilerleme, (bilinmeyen PC için) doğrulama kodu; bitince otomatik kapanır.
private struct SendingView: View {
    let item: TransferItem
    let model: ShareModel

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: icon)
                .font(.system(size: 52))
                .foregroundStyle(item.state == .done ? .green : (item.isFinished ? .orange : Color.accentColor))
            Text(item.pcName).font(.headline)
            Text(item.statusText).multilineTextAlignment(.center).foregroundStyle(.secondary)
            if item.showsVerificationCode {
                Text("Bilgisayarda onaylayın.").font(.subheadline)
                VerificationCodeBanner(code: item.code)
            }
            if item.state == .awaitingRelayConsent {
                HStack {
                    Button("Devam et") { model.confirmRelay() }.buttonStyle(.borderedProminent)
                    Button("Vazgeç", role: .destructive) { model.cancel() }.buttonStyle(.bordered)
                }
            }
            if item.wifiOffNotice, item.state == .joiningHotspot || item.state == .needsManualHotspot {
                Label(TransferItem.wifiOffNoticeText, systemImage: "wifi")
                    .font(.subheadline.weight(.semibold)).multilineTextAlignment(.center)
            }
            if item.state == .needsManualHotspot, let creds = item.hotspotPrompt {
                Text("Ayarlar > Wi-Fi'dan \"\(creds.ssid)\" ağına katılın (parola: \(creds.pass)); sonra buraya dönün.")
                    .font(.footnote).multilineTextAlignment(.center)
            }
            if item.state == .transferring || item.state == .done {
                ProgressView(value: Double(item.bytesDone), total: Double(max(item.totalBytes, 1)))
                Text(progressText).font(.footnote.monospacedDigit()).foregroundStyle(.secondary)
            } else if !item.isFinished {
                ProgressView()
            }
            if item.isFinished && item.state != .done {
                Button("Kapat") { model.cancel() }.buttonStyle(.borderedProminent)
            }
        }
        .padding()
        .onChange(of: item.state) { _, _ in model.itemStateChanged() }
    }

    private var icon: String {
        switch item.state {
        case .done: return "checkmark.circle.fill"
        case .failed, .rejected, .cancelled: return "xmark.circle.fill"
        default: return "paperplane.fill"
        }
    }

    private var progressText: String {
        let done = ByteCountFormatter.string(fromByteCount: item.bytesDone, countStyle: .file)
        let total = ByteCountFormatter.string(fromByteCount: item.totalBytes, countStyle: .file)
        return "\(done) / \(total)"
    }
}
