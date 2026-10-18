import SwiftUI

/// Kullanıcıya yazılı uyarılar (ARCHITECTURE "Uyarılar uygulamada yazılı olacak").
enum HowItWorksContent {
    /// (1) Uygulama uygulama değiştiricide kapatılırsa PC telefonu göremez.
    /// `PUSH_ENABLED` (ücretli hesap + APNs, PROTOCOL §6.5): metin değişir.
    static var closedAppNote: String {
        #if PUSH_ENABLED
        return "Uygulamayı kapatsanız da bilgisayardan gelen dosyalar için bildirim alırsınız (internet gerekir)."
        #else
        return "Uygulamayı uygulama değiştiricide yukarı kaydırıp kapatırsanız bilgisayarınız telefonunuzu göremez. WinDrop'u bir kez açmanız yeterli."
        #endif
    }

    /// (2) Yeni bilgisayarla ilk paylaşım.
    static let newComputerNote = "Yeni bir bilgisayarla ilk kez paylaşırken WinDrop'u bir kez açın."

    /// (3) Bilgisayardan gelen dosyalar.
    static let incomingNote = "Bilgisayardan gelen dosyalar için bildirime dokunmanız gerekir — AirDrop'taki Kabul gibi."

    static var all: [(icon: String, text: String)] {
        [("rectangle.stack.badge.minus", closedAppNote),
         ("desktopcomputer", newComputerNote),
         ("bell.badge", incomingNote)]
    }
}

private struct NoteList: View {
    var body: some View {
        ForEach(Array(HowItWorksContent.all.enumerated()), id: \.offset) { _, note in
            Label(note.text, systemImage: note.icon)
        }
    }
}

/// Ayarlar > "Nasıl çalışır".
struct HowItWorksView: View {
    var body: some View {
        List {
            Section {
                NoteList()
            } footer: {
                Text("Bilgisayara göndermek için WinDrop'u açıp Dosya gönder'e dokunmanız yeterli; cihazlar kendiliğinden belirir.")
            }
            Section { ShareGuideLink() }
        }
        .navigationTitle("Nasıl çalışır")
        .navigationBarTitleDisplayMode(.inline)
    }
}

/// İlk başarılı aktarımdan sonra bir kez gösterilir.
struct InfoCardSheet: View {
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            List {
                Section { NoteList() }
                Section { ShareGuideLink() }
            }
                .navigationTitle("Bilmeniz gerekenler")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Tamam") { dismiss() }
                    }
                }
        }
        .presentationDetents([.medium, .large])
    }
}
