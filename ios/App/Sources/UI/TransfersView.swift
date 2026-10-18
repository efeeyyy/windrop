import QuickLookThumbnailing
import SwiftUI
import UniformTypeIdentifiers

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
                TransferThumbnail(
                    url: item.previewURL, isFolder: item.isFolderTransfer, direction: item.direction,
                    preview: item.previewImage, assetID: item.photoAssetID)
                VStack(alignment: .leading, spacing: 2) {
                    Text(item.title).font(.headline).lineLimit(2)
                    Text(item.statusText).font(.subheadline).foregroundStyle(.secondary)
                    Text(FileSizeText.string(item.totalBytes))
                        .font(.caption).foregroundStyle(.secondary)
                }
            }

            if item.showsVerificationCode {
                VerificationCodeBanner(code: item.code)
            }

            if item.state == .transferring {
                ProgressView(value: Double(item.bytesDone), total: Double(max(item.totalBytes, 1)))
            }

            if item.wifiOffNotice, item.state == .joiningHotspot || item.state == .needsManualHotspot {
                Label(TransferItem.wifiOffNoticeText, systemImage: "wifi")
                    .font(.subheadline.weight(.semibold))
                    .padding(10)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(Color.accentColor.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
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
                SavedPlaceLabel(place: item.savedPlace ?? .files)
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

/// URL -> küçük resim önbelleği (bellek baskısında NSCache kendini boşaltır).
private enum ThumbnailCache {
    nonisolated(unsafe) static let cache: NSCache<NSURL, UIImage> = {
        let cache = NSCache<NSURL, UIImage>()
        cache.countLimit = 200
        return cache
    }()

    static func image(for url: URL) -> UIImage? { cache.object(forKey: url as NSURL) }

    /// Arka planda üretir; dosya yok/okunamıyorsa nil (çağıran simgeye düşer).
    static func generate(for url: URL, scale: CGFloat) async -> UIImage? {
        if let cached = image(for: url) { return cached }
        guard FileManager.default.isReadableFile(atPath: url.path) else { return nil }
        let request = QLThumbnailGenerator.Request(
            fileAt: url, size: CGSize(width: 56, height: 56), scale: scale, representationTypes: .thumbnail)
        guard let representation = try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: request) else {
            return nil
        }
        let image = representation.uiImage
        cache.setObject(image, forKey: url as NSURL)
        return image
    }
}

/// Aktarım satırı önizlemesi: gerçek küçük resim; yoksa klasör/tür/yön simgesi.
private struct TransferThumbnail: View {
    let url: URL?
    let isFolder: Bool
    let direction: TransferItem.Direction
    /// Fotoğraflar'a taşınmadan önce üretilmiş küçük resim (varsa her şeyden önce gelir).
    var preview: UIImage?
    /// Fotoğraflar'a kaydedilen öğenin kimliği: yalnız okuma izni zaten varsa yedek kaynak.
    var assetID: String?
    @Environment(\.displayScale) private var displayScale
    @State private var image: UIImage?

    private var taskKey: String { "\(url?.path ?? "")|\(assetID ?? "")|\(preview != nil)" }

    var body: some View {
        Group {
            if !isFolder, let image {
                Image(uiImage: image).resizable().scaledToFill()
            } else {
                Image(systemName: symbol).font(.title2).foregroundStyle(.secondary)
            }
        }
        .frame(width: 56, height: 56)
        .background(Color.secondary.opacity(0.12))
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .task(id: taskKey) {
            if let preview { image = preview; return }
            guard !isFolder else { image = nil; return }
            guard let url else {
                if let assetID {
                    image = await PhotoSaver.assetThumbnail(localIdentifier: assetID, scale: displayScale)
                } else {
                    image = nil
                }
                return
            }
            if let cached = ThumbnailCache.image(for: url) { image = cached; return }
            image = await ThumbnailCache.generate(for: url, scale: displayScale)
        }
    }

    private var symbol: String {
        if isFolder { return "folder.fill" }
        if let url, let type = UTType(filenameExtension: url.pathExtension) {
            if type.conforms(to: .movie) { return "film" }
            if type.conforms(to: .image) { return "photo" }
            if type.conforms(to: .audio) { return "waveform" }
            if type.conforms(to: .archive) { return "doc.zipper" }
            return "doc.fill"
        }
        return url == nil ? (direction == .send ? "arrow.up.circle" : "arrow.down.circle") : "doc.fill"
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

/// "Nereye kaydedildi" satırı; dokununca Fotoğraflar (`photos-redirect://`) veya Dosyalar (`shareddocuments://<Documents>`)
/// açılır. Açılamıyorsa (canOpenURL false) yalnız metin görünür.
private struct SavedPlaceLabel: View {
    let place: TransferItem.SavedPlace
    @Environment(\.openURL) private var openURL

    private var target: URL? {
        let url: URL?
        if place.opensPhotos {
            url = URL(string: "photos-redirect://")
        } else {
            let path = AppPaths.receivedDirectory.path
            let encoded = path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? path
            url = URL(string: "shareddocuments://" + encoded)
        }
        guard let url, UIApplication.shared.canOpenURL(url) else { return nil }
        return url
    }

    var body: some View {
        if let target {
            Button {
                openURL(target)
            } label: {
                Label(place.text, systemImage: place.opensPhotos ? "photo.on.rectangle" : "folder")
                    .font(.caption)
            }
            .buttonStyle(.plain)
            .foregroundStyle(.tint)
        } else {
            Text(place.text).font(.caption).foregroundStyle(.secondary)
        }
    }
}
