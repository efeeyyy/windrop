import Foundation

/// Hedef klasörde dosya adı var mı? Dosya sistemi erişimini soyutlar.
public protocol FileNameExistence {
    /// Yalnız dosya adı (yol değil).
    func exists(_ fileName: String) -> Bool
}

/// PROTOCOL §5 "Dosya adı (alıcı)" kuralları (iOS'ta da aynen uygulanır; C# `FileNameSanitizer` ile uyumlu):
/// yalnız son yol bileşeni; `..`, `/`, `\`, `:`, denetim karakterleri ve Windows ayrılmış adları reddedilmez,
/// `_` ile değiştirilir / öne `_` eklenir; sondaki nokta/boşluk silinir; üzerine yazılmaz ("ad (2).ext").
/// Ek (PROTOCOL §9a): `< > " | ? *`, C1 denetim, bidi denetim karakterleri, eşleşmemiş vekiller `_` olur;
/// COM¹²³ / CONIN$ / CONOUT$ ayrılmış sayılır; uzunluk sınırı 255 UTF-8 bayt (APFS; karakter ortasından kesilmez), uzantı (≤ 20 UTF-16 birimi) korunur.
/// Sonuç hiçbir girdide boş olmaz (en kötü ihtimalle `fallbackName`).
public enum FileNameSanitizer {
    /// En çok UTF-8 bayt (APFS dosya adı sınırı).
    public static let maxLength = 255
    public static let fallbackName = "file"
    private static let maxPreservedExtension = 20
    private static let maxUniqueAttempts = 10_000

    private static let dot: UInt16 = 0x2E
    private static let space: UInt16 = 0x20
    private static let underscore: UInt16 = 0x5F

    public static func sanitize(_ name: String?) -> String {
        guard let name, !name.isEmpty else { return fallbackName }
        var units = Array(name.utf16)

        // 1) Yalnız son yol bileşeni.
        while let last = units.last, last == 0x2F || last == 0x5C { units.removeLast() }
        if let cut = units.lastIndex(where: { $0 == 0x2F || $0 == 0x5C }) {
            units = Array(units[(cut + 1)...])
        }

        // 2) Yasak/tehlikeli karakterleri '_' yap (eşleşmiş vekiller korunur).
        var cleaned: [UInt16] = []
        cleaned.reserveCapacity(units.count)
        var i = 0
        while i < units.count {
            let c = units[i]
            if isHighSurrogate(c), i + 1 < units.count, isLowSurrogate(units[i + 1]) {
                cleaned.append(c); cleaned.append(units[i + 1])
                i += 2
                continue
            }
            cleaned.append(isForbidden(c) ? underscore : c)
            i += 1
        }

        // 3) Sondaki nokta/boşluk; 4) uzunluk; 5) ayrılmış ad.
        var result = trimTrailing(cleaned)
        if result.isEmpty { return fallbackName }
        result = truncate(result)
        if result.isEmpty { return fallbackName }
        if isReserved(result) {
            result = truncate([underscore] + result)
        }
        return string(result)
    }

    /// Var olan bir dosyanın üzerine yazmamak için "ad (2).ext", "ad (3).ext"… üretir.
    /// - Parameter sanitizedName: `sanitize` çıktısı.
    public static func makeUnique(_ sanitizedName: String, in fileSystem: FileNameExistence) -> String {
        if !fileSystem.exists(sanitizedName) { return sanitizedName }
        let (stem, ext) = splitExtension(Array(sanitizedName.utf16))
        for n in 2...maxUniqueAttempts {
            let suffix = Array(" (\(n))".utf16)
            let room = maxLength - utf8Length(suffix) - utf8Length(ext)
            let cutStem = utf8Length(stem) > room ? prefixFitting(stem, bytes: room) : stem
            let candidate = string(cutStem + suffix + ext)
            if !fileSystem.exists(candidate) { return candidate }
        }
        // Pratikte ulaşılmaz; yine de üzerine yazmamak için rastgele ek.
        return string(prefixFitting(Array(sanitizedName.utf16), bytes: 200)) + " (" + UUID().uuidString + ")"
    }

    public static func sanitizeAndMakeUnique(_ name: String?, in fileSystem: FileNameExistence) -> String {
        makeUnique(sanitize(name), in: fileSystem)
    }

    // MARK: İç

    private static func string(_ units: [UInt16]) -> String {
        String(decoding: units, as: UTF16.self)
    }

    private static func isHighSurrogate(_ c: UInt16) -> Bool { c >= 0xD800 && c <= 0xDBFF }
    private static func isLowSurrogate(_ c: UInt16) -> Bool { c >= 0xDC00 && c <= 0xDFFF }

    private static func isForbidden(_ c: UInt16) -> Bool {
        if c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F) { return true }       // C0, DEL, C1
        switch c {
        case 0x3C, 0x3E, 0x3A, 0x22, 0x7C, 0x3F, 0x2A, 0x2F, 0x5C: return true     // < > : " | ? * / \
        case 0x200E, 0x200F, 0x061C: return true                                    // LRM, RLM, ALM
        default: break
        }
        if c >= 0x202A && c <= 0x202E { return true }                               // bidi gömme/üzerine yazma
        if c >= 0x2066 && c <= 0x2069 { return true }                               // bidi izolasyon
        if c >= 0xD800 && c <= 0xDFFF { return true }                               // eşleşmemiş vekil
        return false
    }

    private static func trimTrailing(_ u: [UInt16]) -> [UInt16] {
        var u = u
        while let l = u.last, l == dot || l == space { u.removeLast() }
        return u
    }

    /// UTF-8 bayt sayısı (eşleşmiş vekil çifti 4 bayt; eşleşmemiş vekil 3, `sanitize` bunları zaten `_` yapar).
    /// Apple dosya sistemleri adı ayrıştırılmış (NFD) biçimde tutar; bu yüzden ölçü, NFC ve NFD UTF-8
    /// uzunluğunun büyüğüdür (örn. "ş" NFC 2, NFD 3 bayt).
    private static func utf8Length(_ u: [UInt16]) -> Int {
        let nfc = rawUtf8Length(u)
        if nfc == u.count { return nfc }   // salt ASCII: NFD aynı
        return max(nfc, rawUtf8Length(Array(string(u).decomposedStringWithCanonicalMapping.utf16)))
    }

    /// Diskteki (NFC/NFD'nin büyüğü) UTF-8 bayt ölçüsü; klasör adı kesimi de bunu kullanır.
    static func diskByteLength(_ s: String) -> Int { utf8Length(Array(s.utf16)) }

    private static func rawUtf8Length(_ u: [UInt16]) -> Int {
        var total = 0
        var i = 0
        while i < u.count {
            let c = u[i]
            if c < 0x80 {
                total += 1
            } else if c < 0x800 {
                total += 2
            } else if isHighSurrogate(c), i + 1 < u.count, isLowSurrogate(u[i + 1]) {
                total += 4
                i += 1
            } else {
                total += 3
            }
            i += 1
        }
        return total
    }

    /// En çok `bytes` UTF-8 bayta sığan en uzun önek; karakter (grafem kümesi) ortasından kesilmez.
    private static func prefixFitting(_ u: [UInt16], bytes: Int) -> [UInt16] {
        var out: [UInt16] = []
        var used = 0
        for ch in string(u) {
            let units = Array(String(ch).utf16)
            let n = utf8Length(units)
            if used + n > bytes { break }
            used += n
            out.append(contentsOf: units)
        }
        return out
    }

    private static func splitExtension(_ name: [UInt16]) -> (stem: [UInt16], ext: [UInt16]) {
        if let d = name.lastIndex(of: dot), d > 0 {
            return (Array(name[0..<d]), Array(name[d...]))
        }
        return (name, [])
    }

    private static func truncate(_ name: [UInt16]) -> [UInt16] {
        if utf8Length(name) <= maxLength { return name }
        var (stem, ext) = splitExtension(name)
        if ext.count > maxPreservedExtension { stem = name; ext = [] }
        let room = maxLength - utf8Length(ext)
        let cut = prefixFitting(stem, bytes: room)
        return trimTrailing(cut) + ext
    }

    /// ASCII büyük harfe çevirir (OrdinalIgnoreCase'e yakın; ASCII dışı olduğu gibi kalır).
    private static func asciiUpper(_ c: UInt16) -> UInt16 {
        (c >= 0x61 && c <= 0x7A) ? c - 0x20 : c
    }

    private static func isReserved(_ name: [UInt16]) -> Bool {
        let stemEnd = name.firstIndex(of: dot) ?? name.count
        var stem = Array(name[0..<stemEnd])
        while let l = stem.last, l == space { stem.removeLast() }
        guard stem.count >= 3, stem.count <= 7 else { return false }   // en uzunu CONOUT$ (7)
        let upper = String(decoding: stem.map(asciiUpper), as: UTF16.self)
        if ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"].contains(upper) { return true }
        if stem.count == 4 {
            let prefix = String(upper.prefix(3))
            if prefix == "COM" || prefix == "LPT" {
                let d = stem[3]
                return (d >= 0x31 && d <= 0x39) || d == 0xB9 || d == 0xB2 || d == 0xB3
            }
        }
        return false
    }
}
