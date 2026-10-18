import Foundation

/// Dosya boyutu metni (örn. "1,2 MB" / "1.2 MB"). `ByteCountFormatter` sayı ve ondalık ayırıcıyı kullanıcının
/// geçerli yerel ayarına (Locale.current) göre biçimler; tüm arayüz ve bildirimler bunu kullanır.
enum FileSizeText {
    static func string(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}
