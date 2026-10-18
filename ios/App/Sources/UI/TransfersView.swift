import SwiftUI

struct TransfersView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        NavigationStack {
            Group {
                if model.transfers.items.isEmpty {
                    ContentUnavailableView(
                        "Aktarım yok", systemImage: "tray",
                        description: Text("Gönderdiğiniz ve aldığınız dosyalar burada görünür."))
                } else {
                    List(model.transfers.items) { item in
                        TransferRow(item: item)
                    }
                }
            }
            .themedList()
            .themedBackground()
            .navigationTitle("Aktarımlar")
            .toolbar {
                ToolbarItem(placement: .primaryAction) {
                    Button("Temizle") { model.transfers.clearFinished() }
                        .disabled(!model.transfers.items.contains { $0.isFinished })
                }
            }
        }
    }
}

private struct TransferRow: View {
    let item: TransferItem
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Image(systemName: item.direction == .send ? "arrow.up.circle" : "arrow.down.circle")
                    .font(.title3)
                VStack(alignment: .leading, spacing: 2) {
                    Text(item.title).font(.headline).lineLimit(2)
                    Text(item.statusText).font(.subheadline).foregroundStyle(.secondary)
                    Text(ByteCountFormatter.string(fromByteCount: item.totalBytes, countStyle: .file))
                        .font(.caption).foregroundStyle(.secondary)
                }
            }

            if item.state == .transferring {
                ProgressView(value: Double(item.bytesDone), total: Double(max(item.totalBytes, 1)))
            }

            if item.state == .needsManualHotspot, let credentials = item.hotspotPrompt {
                ManualHotspotBox(credentials: credentials)
            }

            if item.state == .awaitingRelayConsent {
                HStack {
                    Button("Devam et") { model.transfers.confirmRelay(item) }
                        .buttonStyle(.borderedProminent)
                    Button("Vazgeç", role: .destructive) { model.transfers.cancel(item) }   // karşıya `cancel` gider
                        .buttonStyle(.bordered)
                }
            }
            if item.state == .awaitingUser {
                HStack {
                    Button("Kabul et") { model.transfers.accept(item) }
                        .buttonStyle(.borderedProminent)
                    Button("Reddet", role: .destructive) { model.transfers.reject(item) }
                        .buttonStyle(.bordered)
                }
            }

            if item.isCancellable, item.state != .awaitingRelayConsent {
                Button("İptal", role: .destructive) { model.transfers.cancel(item) }
                    .buttonStyle(.bordered)
            }

            if item.state == .done, item.direction == .receive {
                Text("Dosyalar uygulamasında WinDrop klasöründe.")
                    .font(.caption).foregroundStyle(.secondary)
                if item.receivedFiles.contains(where: { $0.mediaKind != nil }) {
                    Button {
                        model.transfers.saveToPhotos(item)
                    } label: {
                        Label("Fotoğraflar'a kaydet", systemImage: "photo.badge.plus")
                    }
                    .buttonStyle(.bordered)
                }
                if let message = item.photosSaveMessage {
                    Text(message).font(.caption)
                }
            }
        }
        .padding(.vertical, 4)
    }
}

/// Yetki yokken: Ayarlar'dan etkin noktaya bir kez katılma yönergesi (SSID/parola kopyalanabilir).
struct ManualHotspotBox: View {
    let credentials: HotspotCredentials

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Bir kereye mahsus").font(.headline)
            Text("Bilgisayarın etkin noktasına bir kez katılın: Ayarlar > Wi-Fi'den aşağıdaki ağı seçin. iPhone ağı hatırlar; sonraki seferlerde otomatik katılır.")
                .font(.footnote)
            LabeledContent("Ağ adı", value: credentials.ssid)
            HStack {
                Button("Ağ adını kopyala") { Clipboard.copy(credentials.ssid) }
                Button("Parolayı kopyala") { Clipboard.copy(credentials.pass, sensitive: true) }
            }
            .buttonStyle(.bordered)
            Button("Ayarlar'ı aç") {
                if let url = URL(string: UIApplication.openSettingsURLString) {
                    UIApplication.shared.open(url)
                }
            }
            .buttonStyle(.bordered)
        }
        .padding(10)
        .background(Color.orange.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
    }
}
