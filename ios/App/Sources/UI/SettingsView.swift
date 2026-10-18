import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @AppStorage(AppModel.deviceNameKey, store: SharedGroup.defaults) private var deviceName = ""
    @AppStorage(AutoAcceptSettings.enabledKey) private var autoAcceptPaired = true
    @AppStorage(AutoAcceptSettings.limitKey) private var autoAcceptLimit = AutoAcceptSettings.maxLimit
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

    /// Eşli (QR) bilgisayarlar önce, sonra tanıdıklar; ada göre.
    private var sortedPCs: [KnownPC] {
        model.identity.knownPCs.sorted { ($0.autoAccept ? 0 : 1, $0.name) < ($1.autoAccept ? 0 : 1, $1.name) }
    }

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
                    Button {
                        if let url = URL(string: UIApplication.openSettingsURLString) {
                            UIApplication.shared.open(url)
                        }
                    } label: {
                        HStack {
                            Text("Dil / Language")
                            Spacer()
                            Image(systemName: "arrow.up.forward.app")
                                .foregroundStyle(.secondary)
                        }
                        .contentShape(Rectangle())
                    }
                    .foregroundStyle(.primary)
                } footer: {
                    Text("Uygulama dilini iPhone Ayarlar'ından değiştirin")
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
                    TextField(DeviceInfo.defaultName, text: $deviceName)
                        .focused($fieldFocused)
                        .autocorrectionDisabled()
                } header: {
                    Text("Bu cihazın adı")
                } footer: {
                    Text("Bilgisayarda bu ad görünür. Apple, uygulamaların iPhone'unuza verdiğiniz adı okumasına izin vermez; bu yüzden adı buraya yazın.")
                }

                Section {
                    Button {
                        showPairing = true
                    } label: {
                        Label("Otomatik kabul için eşleştir", systemImage: "qrcode.viewfinder")
                    }
                    Toggle("Eşli bilgisayarlardan gelenleri otomatik kabul et", isOn: $autoAcceptPaired)
                    if autoAcceptPaired {
                        Picker("Otomatik kabul üst sınırı", selection: $autoAcceptLimit) {
                            ForEach(AutoAcceptSettings.limitOptions) { option in
                                Text(option.title).tag(option.bytes)
                            }
                        }
                    }
                } footer: {
                    Text("İsteğe bağlı. Windows'taki WinDrop'ta gösterilen QR kodu okutursanız o bilgisayardan gelen dosyalar (üst sınıra kadar) sormadan kabul edilir. Yalnız QR ile eşli bilgisayarlar için geçerlidir; \"Tanıdık\" bilgisayarlardan gelenler her zaman sorulur. Uygulama arka plandayken otomatik kabul edilen aktarımda bildirim yalnız bittiğinde gelir.")
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
                         ? String(localized: "Uygulamayı kapatsanız da bilgisayardan gelen dosyalar için bildirim alırsınız (internet gerekir). Bildirimde dosya adı olmaz; yalnız tanıdık bilgisayarlar bildirim gönderebilir.")
                         : String(localized: "Bunun için yukarıda aktarma sunucusu adresi girilmelidir."))
                }
                #endif

                Section("Bilgisayarlar") {
                    if model.identity.knownPCs.isEmpty {
                        Text("Henüz yok. QR ile eşleştirdiğiniz veya bir bilgisayara gönderip ondan dosya kabul ettiğiniz bilgisayarlar burada görünür.")
                            .foregroundStyle(.secondary)
                    }
                    ForEach(sortedPCs) { pc in
                        HStack {
                            VStack(alignment: .leading, spacing: 2) {
                                HStack(spacing: 6) {
                                    Text(pc.name)
                                    Text(pc.autoAccept ? String(localized: "Eşli") : String(localized: "Tanıdık"))
                                        .font(.caption2.bold())
                                        .padding(.horizontal, 6).padding(.vertical, 2)
                                        .background(pc.autoAccept ? Color.green.opacity(0.2) : Color.secondary.opacity(0.2), in: Capsule())
                                        .foregroundStyle(pc.autoAccept ? Color.green : Color.secondary)
                                }
                                Text(pc.autoAccept
                                     ? (autoAcceptPaired ? String(localized: "Otomatik kabul açık") : String(localized: "Eşli; otomatik kabul kapalı (her seferinde sorulur)"))
                                     : String(localized: "Her seferinde sorulur"))
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
                         ? String(localized: "Bilgisayarın etkin noktasına gerektiğinde otomatik katılınır.")
                         : String(localized: "Wi-Fi yokken bilgisayar etkin noktasını açar. Bir kereye mahsus Ayarlar > Wi-Fi'den bu ağa katılın; iPhone sonra hatırlar."))
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
