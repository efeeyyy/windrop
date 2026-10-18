import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @AppStorage(AppModel.deviceNameKey) private var deviceName = ""
    @AppStorage(RelaySettings.key) private var relayUrl = ""
    #if PUSH_ENABLED
    @AppStorage(PushManager.enabledKey) private var pushEnabled = false
    private var relayConfigured: Bool {
        !relayUrl.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && RelaySettings.isValid(relayUrl)
    }
    #endif
    @AppStorage(AppTheme.key) private var themeRaw = AppTheme.system.rawValue
    @AppStorage(IncomingMediaDestination.key) private var mediaDestinationRaw = IncomingMediaDestination.photos.rawValue
    @FocusState private var fieldFocused: Bool
    @State private var removalCandidate: KnownPC?
    @State private var showRemovalDialog = false
    @State private var showPairing = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Picker("Tema", selection: $themeRaw) {
                        ForEach(AppTheme.allCases) { theme in
                            Text(theme.title).tag(theme.rawValue)
                        }
                    }
                } header: {
                    Text("Görünüm")
                }

                Section {
                    Picker("Gelen fotoğraf ve videolar", selection: $mediaDestinationRaw) {
                        ForEach(IncomingMediaDestination.allCases) { destination in
                            Text(destination.title).tag(destination.rawValue)
                        }
                    }
                } header: {
                    Text("Alınan dosyalar")
                } footer: {
                    Text("Bilgisayardan gelen tekil fotoğraf ve videolar seçtiğiniz yere kaydedilir. Fotoğraflar seçiliyse galeride en son eklenen olarak görünür; izin ilk alımda sorulur. Diğer dosyalar ve klasörler Dosyalar > WinDrop'ta kalır.")
                }

                Section {
                    TextField(UIDevice.current.name, text: $deviceName)
                        .focused($fieldFocused)
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

                Section {
                    TextField("wss://…workers.dev", text: $relayUrl)
                        .focused($fieldFocused)
                        .keyboardType(.URL)
                        .textContentType(.URL)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                    if !relayUrl.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !RelaySettings.isValid(relayUrl) {
                        Text("Geçersiz adres. wss://alan-adi biçiminde yazın.")
                            .font(.caption).foregroundStyle(.red)
                    }
                } header: {
                    Text("Aktarma sunucusu")
                } footer: {
                    Text("İsteğe bağlı. Bilgisayara ne doğrudan ne etkin noktayla ulaşılamazsa gönderim internet üzerinden bu sunucudan geçer (uçtan uca şifreli; sunucu içeriği göremez). Her seferinde kota uyarısıyla onayınız istenir. Boşsa bu yol kapalıdır.")
                }

                #if PUSH_ENABLED
                Section {
                    Toggle("Uygulama kapalıyken bildirim al", isOn: $pushEnabled)
                        .disabled(!relayConfigured)
                    if model.push.registrationFailed {
                        Text("Bildirim kaydı yapılamadı. İnternet ve aktarma sunucusu adresini kontrol edin; WinDrop'u yeniden açınca tekrar denenir.")
                            .font(.caption).foregroundStyle(.red)
                    }
                } footer: {
                    Text(relayConfigured
                         ? "Uygulamayı kapatsanız da bilgisayardan gelen dosyalar için bildirim alırsınız (internet gerekir). Bildirimde dosya adı olmaz; yalnız tanıdık bilgisayarlar bildirim gönderebilir."
                         : "Bunun için yukarıda aktarma sunucusu adresi girilmelidir.")
                }
                #endif

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
            .scrollDismissesKeyboard(.interactively)
            .toolbar {
                ToolbarItemGroup(placement: .keyboard) {
                    Spacer()
                    Button("Bitti") { fieldFocused = false }
                }
            }
            .navigationTitle("Ayarlar")
            #if PUSH_ENABLED
            .onChange(of: pushEnabled) { _, enabled in model.push.apply(enabled: enabled) }
            .onDisappear { model.push.refresh() }   // sunucu adresi değişmiş olabilir: kaydı tazele
            #endif
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
