import SwiftUI

/// Uygulama teması: Sistem / Açık / Koyu (iOS'un kendi renkleri). Seçim `@AppStorage(AppTheme.key)` ile saklanır.
/// Eski sürümlerde kaydedilmiş bilinmeyen değerler ("sepia", "navy" vb.) Sistem'e düşer.
enum AppTheme: String, CaseIterable, Identifiable {
    case system, light, dark

    static let key = "appTheme"
    var id: String { rawValue }

    var title: String {
        switch self {
        case .system: return String(localized: "Sistem")
        case .light: return String(localized: "Açık")
        case .dark: return String(localized: "Koyu")
        }
    }

    var colorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }
}

/// Kök görünümde bir kez uygulanır.
private struct ThemeRoot: ViewModifier {
    @AppStorage(AppTheme.key) private var themeRaw = AppTheme.system.rawValue

    func body(content: Content) -> some View {
        content.preferredColorScheme((AppTheme(rawValue: themeRaw) ?? .system).colorScheme)
            .onAppear {
                if AppTheme(rawValue: themeRaw) == nil { themeRaw = AppTheme.system.rawValue }
            }
    }
}

extension View {
    func appTheme() -> some View { modifier(ThemeRoot()) }
}
