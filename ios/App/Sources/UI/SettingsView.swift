import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @AppStorage(AppModel.deviceNameKey) private var deviceName = ""
    @State private var removalCandidate: KnownPC?
    @State private var showRemovalDialog = false
    @State private var showPairing = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField(UIDevice.current.name, text: $deviceName)
                        .autocorrectionDisabled()
                } header: {
                    Text("Bu cihazın adı")
                } footer: {
                    Text("Bilgisayara gönderilen ad. Boşsa cihaz adı kullanılır.")
                }

                Section {
                    Button {
                        showPairing = true
                    } label: {
                        Label("Otomatik kabul için eşleştir", systemImage: "qrcode.viewfinder")
                    }
                } footer: {
                    Text("İsteğe bağlı. Windows'taki WinDrop'ta gösterilen QR kodu okutursanız o bilgisayardan gelen küçük dosyalar sormadan kabul edilir. Normal kullanım için eşleştirme gerekmez.")
                }

                Section("Tanıdık bilgisayarlar") {
                    if model.identity.knownPCs.isEmpty {
                        Text("Henüz yok. Bir bilgisayara gönderdiğinizde veya ondan dosya kabul ettiğinizde burada görünür.")
                            .foregroundStyle(.secondary)
                    }
                    ForEach(model.identity.knownPCs) { pc in
                        HStack {
                            VStack(alignment: .leading) {
                                Text(pc.name)
                                Text(pc.autoAccept ? "Otomatik kabul açık" : "Her seferinde sorulur")
                                    .font(.caption).foregroundStyle(.secondary)
                                Text("Kimlik: \(String(pc.id.prefix(8)))")
                                    .font(.caption2).foregroundStyle(.tertiary)
                            }
                            Spacer()
                            Button("Kaldır", role: .destructive) {
                                removalCandidate = pc
                                showRemovalDialog = true
                            }
                            .buttonStyle(.borderless)
                        }
                    }
                }

                Section {
                    ForEach(model.identity.knownPCs) { pc in
                        if let credentials = model.identity.hotspot(for: pc.id) {
                            VStack(alignment: .leading, spacing: 6) {
                                Text(pc.name).font(.subheadline.bold())
                                LabeledContent("Ağ adı", value: credentials.ssid)
                                HStack {
                                    Button("Ağ adını kopyala") { Clipboard.copy(credentials.ssid) }
                                    Button("Parolayı kopyala") { Clipboard.copy(credentials.pass, sensitive: true) }
                                }
                                .buttonStyle(.bordered)
                            }
                        }
                    }
                } header: {
                    Text("Etkin nokta bilgisi")
                } footer: {
                    Text(HotspotJoiner.automaticJoinAvailable
                         ? "Bilgisayarın etkin noktasına gerektiğinde otomatik katılınır."
                         : "Wi-Fi yokken bilgisayar etkin noktasını açar. Bir kereye mahsus Ayarlar > Wi-Fi'den bu ağa katılın; iPhone sonra hatırlar.")
                }

                Section {
                    NavigationLink("Nasıl çalışır") { HowItWorksView() }
                    LabeledContent("Sürüm", value: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "-")
                } header: {
                    Text("Hakkında")
                }
            }
            .navigationTitle("Ayarlar")
            .sheet(isPresented: $showPairing) {
                PairView()
            }
            .confirmationDialog(
                "Bilgisayar listeden kaldırılsın mı?",
                isPresented: $showRemovalDialog,
                titleVisibility: .visible,
                presenting: removalCandidate
            ) { pc in
                Button("Kaldır", role: .destructive) {
                    model.removeKnown(pc)
                    removalCandidate = nil
                }
                Button("Vazgeç", role: .cancel) {
                    removalCandidate = nil
                }
            } message: { pc in
                Text("\(pc.name) bu iPhone'dan silinir. Bir sonraki gönderimde yeniden onay gerekir.")
            }
        }
    }
}
