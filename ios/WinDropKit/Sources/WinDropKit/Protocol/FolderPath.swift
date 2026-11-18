import Foundation

/// PROTOCOL §6.7 klasör yolu (MANIFEST `dir`) doğrulama ve temizleme.
public enum FolderPathError: Error, Equatable {
    /// `.`/`..`/boş bileşen, baştaki/sondaki/çift `/` (mutlak yol dahil): protokol hatası.
    case malformed(String)
    /// > 16 seviye.
    case tooDeep(Int)
    /// > 1024 karakter.
    case tooLong(Int)
    /// Hedef kök klasörün dışına çıkıyor.
    case escapesRoot
}

public enum FolderPath {
    public static let maxDepth = 16
    /// Toplam `dir` uzunluğu (UTF-16 kod birimi, `/` dahil).
    public static let maxLength = 1024

    /// `dir` dizgesini doğrular ve her bileşeni `FileNameSanitizer` ile temizler. `nil`/boş değil ama boş bileşenli → hata.
    /// - Returns: temizlenmiş bileşenler (en az 1).
    public static func sanitizedComponents(_ dir: String) throws -> [String] {
        let length = dir.utf16.count
        guard length <= maxLength else { throw FolderPathError.tooLong(length) }
        let raw = dir.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        guard raw.count <= maxDepth else { throw FolderPathError.tooDeep(raw.count) }
        var out: [String] = []
        out.reserveCapacity(raw.count)
        for part in raw {
            if part.isEmpty || part == "." || part == ".." {
                throw FolderPathError.malformed(part.isEmpty ? "boş klasör bileşeni" : "'\(part)' klasör bileşeni")
            }
            // Windows alıcıyla aynı: '\' ve sürücü biçimli ("X:", "C:foo") bileşenler protokol hatasıdır.
            if part.contains("\\") { throw FolderPathError.malformed("'\\' içeren klasör bileşeni") }
            let u = Array(part.utf16)
            if u.count >= 2, u[1] == 0x3A, (u[0] >= 0x41 && u[0] <= 0x5A) || (u[0] >= 0x61 && u[0] <= 0x7A) {
                throw FolderPathError.malformed("sürücü biçimli klasör bileşeni")
            }
            out.append(FileNameSanitizer.sanitize(part))
        }
        return out
    }

    /// `components` ile `root` altındaki hedef klasör; `standardizedFileURL` ile kök dışına çıkışı reddeder.
    public static func resolve(root: URL, components: [String]) throws -> URL {
        var url = root
        for c in components { url.appendPathComponent(c, isDirectory: true) }
        let base = root.standardizedFileURL.path
        let prefix = base.hasSuffix("/") ? base : base + "/"
        let target = url.standardizedFileURL.path
        guard components.isEmpty || target.hasPrefix(prefix) else { throw FolderPathError.escapesRoot }
        for c in components where c.isEmpty || c == "." || c == ".." || c.contains("/") { throw FolderPathError.escapesRoot }
        return url
    }
}
