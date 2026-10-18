import CoreBluetooth
import PhotosUI
import SwiftUI
import UniformTypeIdentifiers
@preconcurrency import WinDropKit

/// Ana ekran (AirDrop gibi): açılışta izin/eşleştirme yok. Büyük "Dosya gönder" -> fotoğraf/dosya seç ->
/// yakındaki bilgisayarlar sayfası. İzinler (Bluetooth, fotoğraflar, bildirim) yalnız bu akışta, tembel istenir.
struct HomeView: View {
    @Environment(AppModel.self) private var model
    @State private var showSourceDialog = false
    @State private var showPhotoPicker = false
    @State private var showImporter = false
    /// Aynı `.fileImporter` hem dosya hem klasör seçer (bir görünümde tek `fileImporter` güvenilir çalışır).
    @State private var importingFolders = false
    @State private var photoItems: [PhotosPickerItem] = []
    @State private var isLoading = false
    @State private var errorText: String?
    @State private var session: SendSession?

    var body: some View {
        NavigationStack {
            VStack(spacing: 24) {
                Spacer()
                Button {
                    showSourceDialog = true
                } label: {
                    VStack(spacing: 12) {
                        Image(systemName: "paperplane.fill").font(.system(size: 52))
                        Text("Dosya gönder").font(.title.bold())
                    }
                    .frame(maxWidth: 360, minHeight: 190)
                }
                .buttonStyle(.borderedProminent)
                .buttonBorderShape(.roundedRectangle(radius: 28))
                .disabled(isLoading)

                if isLoading {
                    HStack { ProgressView(); Text("Hazırlanıyor…") }
                }
                if let errorText {
                    Text(errorText).foregroundStyle(.red).font(.footnote).multilineTextAlignment(.center)
                }

                if !model.hub.nearbyList.isEmpty {
                    VStack(spacing: 4) {
                        ForEach(model.hub.nearbyList) { peer in
                            Text("\(peer.name) yakında").font(.footnote).foregroundStyle(.green)
                        }
                    }
                }
                if let warning = bluetoothWarning {
                    Label(warning, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange).font(.footnote)
                } else if needsBluetoothPrompt {
                    Button {
                        model.hub.startNearby()   // kullanıcı dokunuşu: Bluetooth izni burada istenir
                    } label: {
                        Label("Bluetooth izni vermek için dokunun", systemImage: "dot.radiowaves.left.and.right")
                            .font(.footnote)
                    }
                    .buttonStyle(.borderless)
                } else {
                    Label("Yakındaki bilgisayarlardan dosya alabilirsiniz", systemImage: "dot.radiowaves.left.and.right")
                        .foregroundStyle(.secondary).font(.footnote)
                }
                Spacer()
            }
            .padding()
            .themedBackground()
            .navigationTitle("WinDrop")
            .confirmationDialog("Ne göndermek istersiniz?", isPresented: $showSourceDialog, titleVisibility: .visible) {
                Button("Fotoğraf veya video") { showPhotoPicker = true }
                Button("Dosya") { importingFolders = false; showImporter = true }
                Button("Klasör") { importingFolders = true; showImporter = true }
                Button("Vazgeç", role: .cancel) {}
            }
            .photosPicker(
                isPresented: $showPhotoPicker, selection: $photoItems,
                matching: .any(of: [.images, .videos]), preferredItemEncoding: .current)
            .onChange(of: photoItems) { _, items in loadPhotos(items) }
            .fileImporter(
                isPresented: $showImporter, allowedContentTypes: importingFolders ? [.folder] : [.item],
                allowsMultipleSelection: true
            ) { result in
                switch result {
                case .success(let urls):
                    if importingFolders {
                        loadFolders(urls)
                    } else {
                        present(FilePreparer.outgoing(fromImported: urls))
                    }
                case .failure(let error): errorText = error.localizedDescription
                }
            }
            .sheet(item: $session) { session in
                NearbyView(session: session)
            }
        }
    }

    /// İzin henüz sorulmadı (açılışta sorulmaz; ilk "Dosya gönder" akışında ya da bu dokunuşla istenir).
    private var needsBluetoothPrompt: Bool {
        !model.hub.hasStarted && CBCentralManager.authorization == .notDetermined
    }

    private var bluetoothWarning: String? {
        guard model.hub.hasStarted else { return nil }
        switch model.hub.bluetoothState {
        case .poweredOff: return "Bluetooth kapalı. Bilgisayarınızı bulmak için açın."
        case .unauthorized: return "WinDrop'un Bluetooth izni yok. Ayarlar'dan izin verin."
        case .unsupported: return "Bu cihaz Bluetooth LE desteklemiyor."
        default: return nil
        }
    }

    private func loadPhotos(_ items: [PhotosPickerItem]) {
        guard !items.isEmpty else { return }
        isLoading = true
        errorText = nil
        Task {
            var files: [OutgoingFile] = []
            for item in items {
                if let picked = try? await item.loadTransferable(type: PickedFile.self),
                   let file = FilePreparer.outgoing(url: picked.url, securityScoped: false, isTemporary: true) {
                    files.append(file)
                }
            }
            isLoading = false
            photoItems = []
            present(files)
        }
    }

    /// Klasörler: güvenlik kapsamlı erişim açılır, ağaç arka planda taranır (PROTOCOL §5a).
    private func loadFolders(_ urls: [URL]) {
        isLoading = true
        errorText = nil
        Task {
            let result = await Task.detached(priority: .userInitiated) {
                FilePreparer.outgoing(fromImportedFolders: urls)
            }.value
            isLoading = false
            if let message = result.error {
                errorText = message
            } else {
                present(result.files)
            }
        }
    }

    private func present(_ files: [OutgoingFile]) {
        guard !files.isEmpty else {
            errorText = "Gönderilecek dosya seçilmedi (boş klasörler ve klasör seçmeden dosya eklenemeyenler gönderilemez)."
            return
        }
        guard files.count <= TransferLimits.maxFiles else {
            FilePreparer.release(files)
            errorText = "En çok \(TransferLimits.maxFiles) dosya gönderilebilir."
            return
        }
        errorText = nil
        session = SendSession(files: files)
    }
}
