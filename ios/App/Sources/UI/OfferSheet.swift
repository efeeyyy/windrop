import SwiftUI

/// Gelen teklif onay sayfası (AirDrop tarzı): gönderen, dosya sayısı, boyut, kod, Kabul/Reddet.
struct OfferSheet: View {
    let item: TransferItem
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(spacing: 18) {
            Spacer(minLength: 8)
            Image(systemName: "desktopcomputer")
                .font(.system(size: 54))
                .foregroundStyle(.tint)
            Text(item.pcName).font(.title2.bold())
            Text(item.peerIsKnown ? String(localized: "Tanıdık cihaz") : String(localized: "Yeni cihaz"))
                .font(.caption.bold())
                .padding(.horizontal, 10).padding(.vertical, 3)
                .background((item.peerIsKnown ? Color.green : Color.orange).opacity(0.18), in: Capsule())
            Text("\(item.fileCount) dosya göndermek istiyor")
                .font(.headline)
            Text(FileSizeText.string(item.totalBytes))
                .foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 2) {
                ForEach(item.fileNames, id: \.self) { name in
                    // PROTOCOL §5a: sonda "/" olan önizleme girdisi klasördür.
                    if name.hasSuffix("/") {
                        Label(String(name.dropLast()), systemImage: "folder").font(.footnote).lineLimit(1)
                    } else {
                        Text(name).font(.footnote).lineLimit(1)
                    }
                }
                if item.fileCount > item.fileNames.count, !item.fileNames.contains(where: { $0.hasSuffix("/") }) {
                    Text("ve \(item.fileCount - item.fileNames.count) dosya daha…")
                        .font(.footnote).foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: 320, alignment: .leading)
            if item.showsVerificationCode {
                VerificationCodeBanner(code: item.code)
                    .frame(maxWidth: 320)
            }
            Spacer()
            HStack(spacing: 12) {
                Button(role: .destructive) {
                    model.transfers.reject(item)
                } label: {
                    Text("Reddet").frame(maxWidth: .infinity)
                }
                .buttonStyle(.bordered)
                Button {
                    model.transfers.accept(item)
                } label: {
                    Text("Kabul et").frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
            }
            .controlSize(.large)
        }
        .padding(24)
        .presentationDetents([.medium, .large])
    }
}
