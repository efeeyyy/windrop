import SwiftUI

/// Yakındaki bir bilgisayarın karosu (ana uygulama ve Paylaşım Uzantısı ortak kullanır).
struct PeerTile: View {
    let peer: NearbyPeer
    let action: () -> Void

    /// VoiceOver: "‹ad›, tanıdık bilgisayar, doğrulama kodu 1 2 3 4" (simge/rozet/kod ayrı ayrı okunmaz).
    private var accessibilityText: String {
        let kind = peer.isKnown ? String(localized: "tanıdık bilgisayar") : String(localized: "yeni bilgisayar")
        guard !peer.code.isEmpty else { return "\(peer.name), \(kind)" }
        let spaced = peer.code.map(String.init).joined(separator: " ")
        return String(localized: "\(peer.name), \(kind), doğrulama kodu \(spaced)")
    }

    var body: some View {
        Button(action: action) {
            VStack(spacing: 6) {
                Image(systemName: "desktopcomputer")
                    .font(.system(size: 40))
                    .frame(width: 76, height: 76)
                    .background(Color.accentColor.opacity(0.12), in: Circle())
                Text(peer.name).font(.subheadline.bold()).lineLimit(1)
                if peer.isKnown {
                    Text("Tanıdık")
                        .font(.caption2.bold())
                        .padding(.horizontal, 7).padding(.vertical, 2)
                        .background(Color.green.opacity(0.2), in: Capsule())
                        .foregroundStyle(.green)
                }
                Text(peer.code)
                    .font(.caption2.monospacedDigit()).foregroundStyle(.secondary)
            }
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibilityText)
        .accessibilityAddTraits(.isButton)
    }
}
