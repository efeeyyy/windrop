import SwiftUI

/// Uygulama teması. "Sistem/Açık/Koyu" iOS'un kendi renklerini kullanır; "Sarı kağıt" ve "Koyu mavi" özel paletlidir.
/// Seçim `@AppStorage(AppTheme.key)` ile saklanır. Paylaşım uzantısı bu dosyayı derlemez: sistem temasında kalır.
enum AppTheme: String, CaseIterable, Identifiable {
    case system, light, dark, sepia, navy

    static let key = "appTheme"
    var id: String { rawValue }

    var title: String {
        switch self {
        case .system: return "Sistem"
        case .light: return "Açık"
        case .dark: return "Koyu"
        case .sepia: return "Sarı kağıt"
        case .navy: return "Koyu mavi"
        }
    }

    var colorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light, .sepia: return .light
        case .dark, .navy: return .dark
        }
    }

    /// Özel tema paleti; sistem temalarında nil (iOS varsayılanları).
    var palette: ThemePalette? {
        switch self {
        case .sepia: return .sepia
        case .navy: return .navy
        default: return nil
        }
    }
}

/// Renk paleti. WCAG kontrast oranları (göreli parlaklıkla hesaplandı), hepsi >= 4.5:1:
///
/// Sarı kağıt: arka plan #F4ECD8, ikincil arka plan #EADFC3, metin #3B2A1A, ikincil metin #5C4631, vurgu #8A4B08
///   metin/arka plan 11.64, metin/ikincil 10.34, ikincil metin/arka plan 7.51 (ikincil/ikincil 6.67),
///   vurgu/arka plan 5.77, vurgu/ikincil arka plan 5.13.
/// Koyu mavi: arka plan #0B1B33, ikincil arka plan #14294A, metin #E8EEF8, ikincil metin #B4C4DD, vurgu #7DB8FF
///   metin/arka plan 14.78, metin/ikincil 12.46, ikincil metin/arka plan 9.75 (ikincil/ikincil 8.22),
///   vurgu/arka plan 8.35, vurgu/ikincil arka plan 7.04.
struct ThemePalette: Equatable, Sendable {
    let background: Color
    let secondaryBackground: Color
    let text: Color
    let secondaryText: Color
    let accent: Color

    static let sepia = ThemePalette(
        background: Color(red: 0xF4 / 255, green: 0xEC / 255, blue: 0xD8 / 255),
        secondaryBackground: Color(red: 0xEA / 255, green: 0xDF / 255, blue: 0xC3 / 255),
        text: Color(red: 0x3B / 255, green: 0x2A / 255, blue: 0x1A / 255),
        secondaryText: Color(red: 0x5C / 255, green: 0x46 / 255, blue: 0x31 / 255),
        accent: Color(red: 0x8A / 255, green: 0x4B / 255, blue: 0x08 / 255))

    static let navy = ThemePalette(
        background: Color(red: 0x0B / 255, green: 0x1B / 255, blue: 0x33 / 255),
        secondaryBackground: Color(red: 0x14 / 255, green: 0x29 / 255, blue: 0x4A / 255),
        text: Color(red: 0xE8 / 255, green: 0xEE / 255, blue: 0xF8 / 255),
        secondaryText: Color(red: 0xB4 / 255, green: 0xC4 / 255, blue: 0xDD / 255),
        accent: Color(red: 0x7D / 255, green: 0xB8 / 255, blue: 0xFF / 255))
}

private struct ThemePaletteKey: EnvironmentKey {
    static let defaultValue: ThemePalette? = nil
}

extension EnvironmentValues {
    /// Özel tema paleti (nil: sistem/açık/koyu).
    var themePalette: ThemePalette? {
        get { self[ThemePaletteKey.self] }
        set { self[ThemePaletteKey.self] = newValue }
    }
}

/// Kök görünümde bir kez uygulanır.
private struct ThemeRoot: ViewModifier {
    @AppStorage(AppTheme.key) private var themeRaw = AppTheme.system.rawValue

    func body(content: Content) -> some View {
        let theme = AppTheme(rawValue: themeRaw) ?? .system
        let palette = theme.palette
        content
            .environment(\.themePalette, palette)
            .preferredColorScheme(theme.colorScheme)
            .tint(palette?.accent)
            .foregroundStyle(palette?.text ?? Color.primary)
    }
}

/// List/Form: özel temada sistem gri zeminini gizleyip tema rengini verir.
private struct ThemedList: ViewModifier {
    @Environment(\.themePalette) private var palette

    func body(content: Content) -> some View {
        if let palette {
            content
                .scrollContentBackground(.hidden)
                .background(palette.background)
                .listRowBackground(palette.secondaryBackground)
        } else {
            content
        }
    }
}

private struct ThemedBackground: ViewModifier {
    @Environment(\.themePalette) private var palette

    func body(content: Content) -> some View {
        if let palette {
            content.background(palette.background.ignoresSafeArea())
        } else {
            content
        }
    }
}

extension View {
    func appTheme() -> some View { modifier(ThemeRoot()) }
    func themedList() -> some View { modifier(ThemedList()) }
    func themedBackground() -> some View { modifier(ThemedBackground()) }
}
