import SwiftUI

/// "WinDrop'u AirDrop gibi paylaş menüsüne ekle" rehberi: sayfalı, her adımda SwiftUI ile çizilmiş sade "mini ekran".
/// Kişisel veri, fotoğraf ya da üçüncü taraf marka/simge YOK; yer tutucular nötr gri. Açılışta zorla gösterilmez
/// (kurulumsuz deneyim): giriş noktaları `ShareGuideCard` (Ana ekran), Ayarlar > Nasıl çalışır ve ilk aktarım bilgi kartı.
enum ShareGuideStorage {
    /// Ana ekrandaki kartın kapatıldığı (ya da rehberin bitirildiği) bilgisi.
    static let cardDismissedKey = "shareGuideCardDismissed"
}

private enum GuideMock {
    case photoGrid, shareSheetMore, appsList, favorites, shareSheetReady, windows
}

private struct GuideStep: Identifiable {
    let id: Int
    let mock: GuideMock
    let title: String
    let text: String
    /// VoiceOver için mini ekranın sözlü tarifi (çizim kendisi erişilebilirlikten gizlenir).
    let mockDescription: String
}

private var guideSteps: [GuideStep] {
    [
        GuideStep(
            id: 0, mock: .photoGrid,
            title: String(localized: "Öğeyi seçin, Paylaş'a dokunun"),
            text: String(localized: "Fotoğraflar'da (veya herhangi bir uygulamada) bir öğe seçin, sonra Paylaş (Share) simgesine dokunun. Paylaşım sayfası açılır."),
            mockDescription: String(localized: "Çizim: bir öğe seçili, alt çubukta vurgulanmış Paylaş simgesi.")),
        GuideStep(
            id: 1, mock: .shareSheetMore,
            title: String(localized: "Uygulama satırını sona kaydırın"),
            text: String(localized: "Paylaşım sayfasında alttaki uygulama simgeleri satırını sağa doğru sona kadar kaydırın ve Diğer (More) ••• simgesine dokunun."),
            mockDescription: String(localized: "Çizim: paylaşım sayfası; uygulama satırının sonunda vurgulanmış Diğer üç nokta simgesi.")),
        GuideStep(
            id: 2, mock: .appsList,
            title: String(localized: "WinDrop'un yanındaki + işaretine dokunun"),
            text: String(localized: "Uygulamalar (Apps) listesinde sağ üstte Düzenle (Edit) varsa dokunun. Öneriler (Suggestions) bölümünde WinDrop'un solundaki yeşil + düğmesine dokunun."),
            mockDescription: String(localized: "Çizim: Favoriler ve Öneriler listesi; Öneriler'deki WinDrop satırında vurgulanmış yeşil artı düğmesi.")),
        GuideStep(
            id: 3, mock: .favorites,
            title: String(localized: "AirDrop'un altına yerleşir"),
            text: String(localized: "WinDrop, Favoriler (Favourites) bölümüne AirDrop'un hemen altına eklenir. İsterseniz sağdaki ≡ tutamacıyla yukarı sürükleyin. Sonra sağ üstteki mavi ✓ (Bitti / Done) düğmesine dokunun."),
            mockDescription: String(localized: "Çizim: WinDrop, Favoriler'de AirDrop'un altında; sağ üstte vurgulanmış mavi onay düğmesi.")),
        GuideStep(
            id: 4, mock: .shareSheetReady,
            title: String(localized: "Artık AirDrop gibi"),
            text: String(localized: "Her paylaşımda WinDrop ilk sıralarda olur: dokunun, yakındaki bilgisayar belirir, bilgisayara dokunun. Bilgisayarda Kabul'a basılınca dosya gider."),
            mockDescription: String(localized: "Çizim: uygulama satırında WinDrop ikinci sırada; altında yakındaki bilgisayarın vurgulanmış kutusu.")),
        GuideStep(
            id: 5, mock: .windows,
            title: String(localized: "Bilgisayardan göndermek"),
            text: String(localized: "Windows'ta dosyaya sağ tıklayıp Gönder > WinDrop'u seçin ya da dosyayı WinDrop penceresine sürükleyip bırakın. Telefonda WinDrop açık (veya arka planda) olmalı.")
                + "\n\n" + HowItWorksContent.closedAppNote,
            mockDescription: String(localized: "Çizim: Windows penceresi; dosyanın sağ tık menüsünde Gönder altında vurgulanmış WinDrop; altında sürükle-bırak alanı.")),
    ]
}

// MARK: - Ekran

struct ShareGuideView: View {
    /// Sayfa olarak sunulduğunda (Ana ekran kartı) kapatma düğmesi gösterilir; itme (push) ile açılınca geri düğmesi yeter.
    var showsCloseButton = false

    @Environment(\.dismiss) private var dismiss
    @AppStorage(ShareGuideStorage.cardDismissedKey) private var cardDismissed = false
    @State private var index = 0

    private var isLast: Bool { index >= guideSteps.count - 1 }

    var body: some View {
        VStack(spacing: 0) {
            TabView(selection: $index) {
                ForEach(guideSteps) { step in
                    GuidePage(step: step).tag(step.id)
                }
            }
            .tabViewStyle(.page(indexDisplayMode: .always))
            .indexViewStyle(.page(backgroundDisplayMode: .always))

            Button {
                if isLast {
                    cardDismissed = true
                    dismiss()
                } else {
                    withAnimation { index += 1 }
                }
            } label: {
                Text(isLast ? LocalizedStringKey("Bitti") : LocalizedStringKey("İleri")).frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .padding([.horizontal, .bottom])
            .padding(.top, 8)
        }
        .navigationTitle("Paylaş menüsüne ekle")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            if showsCloseButton {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Kapat") { dismiss() }
                }
            }
        }
    }
}

private struct GuidePage: View {
    let step: GuideStep

    var body: some View {
        ScrollView {
            VStack(spacing: 20) {
                mock
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel(step.mockDescription)
                VStack(spacing: 10) {
                    Text(step.title)
                        .font(.title3.bold())
                        .multilineTextAlignment(.center)
                        .accessibilityAddTraits(.isHeader)
                    Text(step.text)
                        .font(.body)
                        .multilineTextAlignment(.center)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxWidth: 520)
            }
            .padding(.horizontal)
            .padding(.top, 16)
            .padding(.bottom, 36)   // sayfa göstergesi için boşluk
            .frame(maxWidth: .infinity)
        }
    }

    @ViewBuilder private var mock: some View {
        switch step.mock {
        case .photoGrid: PhotoGridMock()
        case .shareSheetMore: ShareSheetMock(stage: .more)
        case .appsList: AppsListMock()
        case .favorites: FavoritesMock()
        case .shareSheetReady: ShareSheetMock(stage: .ready)
        case .windows: WindowsMock()
        }
    }
}

// MARK: - Giriş noktası: Ana ekran kartı

/// Ana ekranda küçük, kapatılabilir kart. Kapatılınca ya da rehber bitince bir daha çıkmaz.
struct ShareGuideCard: View {
    @AppStorage(ShareGuideStorage.cardDismissedKey) private var dismissed = false
    @State private var showGuide = false

    var body: some View {
        Group {
            if !dismissed { card }
        }
        // Sheet koşullu içeriğin dışında: rehber bitip kart kaybolurken sunum düzgün kapanır.
        .sheet(isPresented: $showGuide) {
            NavigationStack { ShareGuideView(showsCloseButton: true) }
        }
    }

    private var card: some View {
        HStack(spacing: 4) {
                Button {
                    showGuide = true
                } label: {
                    HStack(spacing: 10) {
                        Image(systemName: "square.and.arrow.up.on.square")
                            .font(.title3)
                            .foregroundStyle(.tint)
                            .accessibilityHidden(true)
                        Text("WinDrop'u AirDrop gibi paylaş menüsüne ekle · 1 dk")
                            .font(.footnote.weight(.medium))
                            .multilineTextAlignment(.leading)
                            .foregroundStyle(.primary)
                        Spacer(minLength: 0)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityHint("Kısa görsel rehberi açar")

                Button {
                    withAnimation { dismissed = true }
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.title3)
                        .foregroundStyle(.secondary)
                        .frame(minWidth: 44, minHeight: 44)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Kartı kapat")
            }
            .padding(.leading, 14)
            .padding(.trailing, 2)
            .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            .frame(maxWidth: 360)
    }
}

/// Ayarlar > Nasıl çalışır ve bilgi kartı içindeki bağlantı (çevreleyen `NavigationStack` gerekir).
struct ShareGuideLink: View {
    var body: some View {
        NavigationLink {
            ShareGuideView()
        } label: {
            Label("Paylaş menüsüne ekleme rehberi", systemImage: "square.and.arrow.up")
        }
    }
}

// MARK: - Çizim yardımcıları

/// Vurgulanan öğede hafif nabız halkası. "Hareketi azalt" açıksa halka sabit kalır.
private struct Pulse: ViewModifier {
    let active: Bool
    var cornerRadius: CGFloat = 8
    var color: Color = .accentColor

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var grown = false

    func body(content: Content) -> some View {
        content.overlay {
            if active {
                RoundedRectangle(cornerRadius: cornerRadius + 4, style: .continuous)
                    .stroke(color, lineWidth: 2.5)
                    .padding(-4)
                    .scaleEffect(grown && !reduceMotion ? 1.14 : 1)
                    .opacity(grown && !reduceMotion ? 0.3 : 1)
                    .animation(
                        reduceMotion ? nil : .easeInOut(duration: 0.9).repeatForever(autoreverses: true),
                        value: grown)
                    .onAppear { grown = true }
            }
        }
    }
}

private extension View {
    func pulse(_ active: Bool = true, radius: CGFloat = 8, color: Color = .accentColor) -> some View {
        modifier(Pulse(active: active, cornerRadius: radius, color: color))
    }
}

/// Telefon çerçevesi.
private struct MiniPhone<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        content
            .frame(width: 232, height: 340)
            .background(Color(.systemGroupedBackground))
            .clipShape(RoundedRectangle(cornerRadius: 30, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: 30, style: .continuous)
                    .strokeBorder(Color.primary.opacity(0.75), lineWidth: 5)
            }
            .shadow(color: .black.opacity(0.15), radius: 8, y: 3)
    }
}

private let placeholderGray = Color.gray.opacity(0.35)

/// WinDrop'un kendi (kendi çizdiğimiz) simgesi: mavi degrade + kağıt uçak.
private struct WinDropGlyph: View {
    var size: CGFloat = 38
    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.23, style: .continuous)
            .fill(LinearGradient(colors: [Color(red: 0.25, green: 0.65, blue: 1), Color(red: 0.05, green: 0.3, blue: 0.85)],
                                 startPoint: .topLeading, endPoint: .bottomTrailing))
            .frame(width: size, height: size)
            .overlay {
                Image(systemName: "paperplane.fill")
                    .font(.system(size: size * 0.45))
                    .foregroundStyle(.white)
            }
    }
}

private struct AppTile: View {
    enum Kind { case plain, windrop, more }
    let label: LocalizedStringKey
    var kind: Kind = .plain
    var highlighted = false

    var body: some View {
        VStack(spacing: 3) {
            icon
                .frame(width: 38, height: 38)
                .pulse(highlighted, radius: kind == .more ? 19 : 9)
            Text(label)
                .font(.system(size: 8))
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
        .frame(width: 48)
    }

    @ViewBuilder private var icon: some View {
        switch kind {
        case .plain:
            RoundedRectangle(cornerRadius: 9, style: .continuous).fill(placeholderGray)
        case .windrop:
            WinDropGlyph(size: 38)
        case .more:
            Circle().fill(placeholderGray)
                .overlay { Image(systemName: "ellipsis").font(.system(size: 14, weight: .bold)).foregroundStyle(.primary) }
        }
    }
}

private struct MiniRow: View {
    enum Leading { case none, plus, minus }
    let label: LocalizedStringKey
    var leading: Leading = .none
    var isWinDrop = false
    var handle = false
    var highlightPlus = false
    var highlightRow = false

    var body: some View {
        HStack(spacing: 7) {
            switch leading {
            case .none: EmptyView()
            case .plus:
                Image(systemName: "plus.circle.fill")
                    .font(.system(size: 16))
                    .foregroundStyle(.green)
                    .pulse(highlightPlus, radius: 8, color: .green)
            case .minus:
                Image(systemName: "minus.circle.fill").font(.system(size: 16)).foregroundStyle(.red)
            }
            if isWinDrop {
                WinDropGlyph(size: 22)
            } else {
                RoundedRectangle(cornerRadius: 5, style: .continuous).fill(placeholderGray).frame(width: 22, height: 22)
            }
            Text(label).font(.system(size: 10, weight: isWinDrop ? .semibold : .regular))
            Spacer(minLength: 0)
            if handle {
                Image(systemName: "line.3.horizontal").font(.system(size: 11)).foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 8)
        .frame(height: 32)
        .background(highlightRow ? Color.accentColor.opacity(0.12) : Color.clear)
    }
}

private struct MiniGroup<Content: View>: View {
    let header: LocalizedStringKey
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(header).font(.system(size: 8, weight: .semibold)).foregroundStyle(.secondary).padding(.leading, 6)
            VStack(spacing: 0) { content }
                .background(Color(.secondarySystemGroupedBackground))
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        }
    }
}

private struct CheckButton: View {
    var body: some View {
        Circle().fill(Color.accentColor).frame(width: 22, height: 22)
            .overlay { Image(systemName: "checkmark").font(.system(size: 11, weight: .bold)).foregroundStyle(.white) }
            .pulse(true, radius: 11)
    }
}

// MARK: - Mini ekranlar

/// Adım 1: öğe seçili, alt çubukta Paylaş.
private struct PhotoGridMock: View {
    private let shades: [Double] = [0.30, 0.45, 0.35, 0.50, 0.28, 0.40, 0.33, 0.48, 0.38, 0.30, 0.44, 0.36]

    var body: some View {
        MiniPhone {
            VStack(spacing: 0) {
                HStack {
                    Text("1 öğe seçildi").font(.system(size: 11, weight: .semibold))
                    Spacer()
                    Text("Bitti").font(.system(size: 10)).foregroundStyle(.tint)
                }
                .padding(.horizontal, 14).padding(.top, 22).padding(.bottom, 8)

                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 2), count: 3), spacing: 2) {
                    ForEach(0..<12, id: \.self) { i in
                        Rectangle().fill(Color.gray.opacity(shades[i]))
                            .aspectRatio(1, contentMode: .fit)
                            .overlay(alignment: .bottomTrailing) {
                                if i == 4 {
                                    Circle().fill(Color.accentColor).frame(width: 16, height: 16)
                                        .overlay { Image(systemName: "checkmark").font(.system(size: 9, weight: .bold)).foregroundStyle(.white) }
                                        .padding(4)
                                }
                            }
                            .overlay { if i == 4 { Rectangle().stroke(Color.accentColor, lineWidth: 2) } }
                    }
                }
                Spacer(minLength: 0)
                HStack {
                    Image(systemName: "square.and.arrow.up")
                        .font(.system(size: 16))
                        .foregroundStyle(.tint)
                        .frame(width: 30, height: 30)
                        .pulse(radius: 8)
                    Spacer()
                    Image(systemName: "trash").font(.system(size: 14)).foregroundStyle(.secondary)
                }
                .padding(.horizontal, 18).padding(.vertical, 10)
                .background(Color(.secondarySystemGroupedBackground))
            }
        }
    }
}

/// Adım 2 ve 5: paylaşım sayfası.
private struct ShareSheetMock: View {
    enum Stage { case more, ready }
    let stage: Stage

    var body: some View {
        MiniPhone {
            VStack(spacing: 0) {
                if stage == .more {
                    Spacer(minLength: 0)
                    sheet
                } else {
                    sheet.padding(.top, 20)
                    Image(systemName: "arrow.down").font(.system(size: 14, weight: .semibold)).foregroundStyle(.secondary)
                        .padding(.vertical, 8)
                    nearbyCard
                    Spacer(minLength: 0)
                }
            }
            .padding(.horizontal, 8)
            .padding(.bottom, stage == .more ? 10 : 0)
        }
    }

    private var sheet: some View {
        VStack(alignment: .leading, spacing: 8) {
            if stage == .more {
                HStack(spacing: 8) {
                    RoundedRectangle(cornerRadius: 5, style: .continuous).fill(placeholderGray).frame(width: 28, height: 28)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Seçilen öğe").font(.system(size: 10, weight: .semibold))
                        Text("1 öğe").font(.system(size: 8)).foregroundStyle(.secondary)
                    }
                    Spacer()
                }
                HStack(spacing: 6) {
                    ForEach(0..<4, id: \.self) { _ in
                        VStack(spacing: 3) {
                            Circle().fill(placeholderGray).frame(width: 34, height: 34)
                            Text("Kişi").font(.system(size: 8)).foregroundStyle(.secondary)
                        }.frame(maxWidth: .infinity)
                    }
                }
            }
            HStack(spacing: 2) {
                if stage == .more {
                    AppTile(label: "Uygulama")
                    AppTile(label: "Uygulama")
                    AppTile(label: "Uygulama")
                    AppTile(label: "Diğer", kind: .more, highlighted: true)
                } else {
                    AppTile(label: "AirDrop")
                    AppTile(label: "WinDrop", kind: .windrop, highlighted: true)
                    AppTile(label: "Mesajlar")
                    AppTile(label: "Mail")
                }
            }
            .frame(maxWidth: .infinity)
        }
        .padding(10)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 16, style: .continuous))
    }

    private var nearbyCard: some View {
        VStack(spacing: 8) {
            Text("Yakındaki bilgisayarlar").font(.system(size: 10, weight: .semibold))
            VStack(spacing: 3) {
                Image(systemName: "desktopcomputer").font(.system(size: 24)).foregroundStyle(.tint)
                    .frame(width: 56, height: 46)
                    .background(Color.accentColor.opacity(0.12), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                    .pulse(radius: 10)
                Text("Bilgisayar").font(.system(size: 8)).foregroundStyle(.secondary)
            }
        }
        .padding(10)
        .frame(maxWidth: .infinity)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 16, style: .continuous))
    }
}

/// Adım 3: Uygulamalar listesi (Favoriler + Öneriler), WinDrop'un + düğmesi.
private struct AppsListMock: View {
    var body: some View {
        MiniPhone {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text("Uygulamalar").font(.system(size: 12, weight: .bold))
                    Spacer()
                    Text("Düzenle").font(.system(size: 10)).foregroundStyle(.tint)
                }
                .padding(.top, 22)
                MiniGroup(header: "FAVORİLER") {
                    MiniRow(label: "AirDrop")
                    Divider()
                    MiniRow(label: "Mesajlar")
                    Divider()
                    MiniRow(label: "Mail")
                }
                MiniGroup(header: "ÖNERİLER") {
                    MiniRow(label: "Uygulama", leading: .plus)
                    Divider()
                    MiniRow(label: "WinDrop", leading: .plus, isWinDrop: true, highlightPlus: true)
                    Divider()
                    MiniRow(label: "Uygulama", leading: .plus)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 10)
        }
    }
}

/// Adım 4: WinDrop Favoriler'de AirDrop'un altında; ≡ tutamaç, ✓ düğmesi.
private struct FavoritesMock: View {
    var body: some View {
        MiniPhone {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text("Uygulamalar").font(.system(size: 12, weight: .bold))
                    Spacer()
                    CheckButton()
                }
                .padding(.top, 22)
                MiniGroup(header: "FAVORİLER") {
                    MiniRow(label: "AirDrop", leading: .minus, handle: true)
                    Divider()
                    MiniRow(label: "WinDrop", leading: .minus, isWinDrop: true, handle: true, highlightRow: true)
                    Divider()
                    MiniRow(label: "Mesajlar", leading: .minus, handle: true)
                    Divider()
                    MiniRow(label: "Mail", leading: .minus, handle: true)
                }
                MiniGroup(header: "ÖNERİLER") {
                    MiniRow(label: "Uygulama", leading: .plus)
                    Divider()
                    MiniRow(label: "Uygulama", leading: .plus)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 10)
        }
    }
}

/// Adım 6: Windows'ta sağ tık > Gönder > WinDrop ve sürükle-bırak.
private struct WindowsMock: View {
    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                Text("Klasör").font(.system(size: 10, weight: .semibold))
                Spacer()
                ForEach(0..<3, id: \.self) { _ in
                    RoundedRectangle(cornerRadius: 2).fill(placeholderGray).frame(width: 10, height: 6)
                }
            }
            .padding(.horizontal, 10).frame(height: 26)
            .background(Color(.tertiarySystemGroupedBackground))

            ZStack(alignment: .topLeading) {
                VStack(alignment: .leading, spacing: 10) {
                    HStack(spacing: 6) {
                        Image(systemName: "doc.fill").font(.system(size: 22)).foregroundStyle(.gray)
                        Text("Dosya").font(.system(size: 10))
                    }
                    .padding(6)
                    .background(Color.accentColor.opacity(0.15), in: RoundedRectangle(cornerRadius: 5))

                    Spacer(minLength: 0)

                    HStack(spacing: 8) {
                        Image(systemName: "arrow.down.doc").font(.system(size: 16)).foregroundStyle(.tint)
                        Text("veya WinDrop penceresine sürükleyip bırakın")
                            .font(.system(size: 9)).foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity).padding(10)
                    .overlay {
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .stroke(style: StrokeStyle(lineWidth: 1.2, dash: [4, 3]))
                            .foregroundStyle(.secondary)
                    }
                }
                .padding(10)

                // Sağ tık menüsü
                VStack(alignment: .leading, spacing: 0) {
                    menuRow("Aç")
                    menuRow("Kopyala")
                    menuRow("Gönder (menü)", chevron: true, selected: true)
                }
                .frame(width: 96)
                .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 6))
                .shadow(color: .black.opacity(0.2), radius: 4, y: 2)
                .offset(x: 62, y: 28)

                // Gönder alt menüsü
                VStack(alignment: .leading, spacing: 0) {
                    menuRow("Masaüstü")
                    HStack(spacing: 5) {
                        WinDropGlyph(size: 14)
                        Text("WinDrop").font(.system(size: 10, weight: .semibold))
                    }
                    .padding(.horizontal, 7).frame(height: 24)
                    .pulse(radius: 4)
                }
                .frame(width: 96, alignment: .leading)
                .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 6))
                .shadow(color: .black.opacity(0.2), radius: 4, y: 2)
                .offset(x: 150, y: 76)
            }
            .frame(height: 240)
            .clipped()
        }
        .frame(width: 270)
        .background(Color(.systemGroupedBackground))
        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay { RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Color.primary.opacity(0.6), lineWidth: 3) }
        .shadow(color: .black.opacity(0.15), radius: 8, y: 3)
    }

    private func menuRow(_ text: LocalizedStringKey, chevron: Bool = false, selected: Bool = false) -> some View {
        HStack {
            Text(text).font(.system(size: 10))
            Spacer(minLength: 0)
            if chevron { Image(systemName: "chevron.right").font(.system(size: 8)) }
        }
        .padding(.horizontal, 7).frame(height: 24)
        .background(selected ? Color.accentColor.opacity(0.2) : Color.clear)
    }
}
