import SwiftUI

/// Bilinmeyen (XX) bilgisayar için 4 haneli doğrulama kodu: iPhone ve bilgisayar ekranında aynı olmalı (PROTOCOL §2b).
/// Uygulama ve Paylaşım Uzantısı ortak kullanır.
struct VerificationCodeBanner: View {
    let code: String

    var body: some View {
        VStack(spacing: 4) {
            Text("Doğrulama kodu: \(code)")
                .font(.title2.bold().monospacedDigit())
            Text("Bilgisayardakiyle aynı olmalı")
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .multilineTextAlignment(.center)
        .frame(maxWidth: .infinity)
        .padding(.vertical, 10)
        .padding(.horizontal, 12)
        .background(Color.accentColor.opacity(0.14), in: RoundedRectangle(cornerRadius: 12))
        .accessibilityElement(children: .combine)
    }
}
