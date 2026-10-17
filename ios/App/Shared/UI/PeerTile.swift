import SwiftUI

/// Yakındaki bir bilgisayarın karosu (ana uygulama ve Paylaşım Uzantısı ortak kullanır).
struct PeerTile: View {
    let peer: NearbyPeer
    let action: () -> Void

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
                    .font(.caption2.monospacedDigit()).foregroundStyle(.tertiary)
            }
        }
        .buttonStyle(.plain)
    }
}
