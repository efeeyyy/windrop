import Foundation

public enum FolderScanError: Error, Equatable {
    case notADirectory(String)
    /// Klasör ağacı > 16 seviye (PROTOCOL §6.7).
    case tooDeep(String)
    /// Göreli yol > 1024 karakter.
    case pathTooLong(String)
    case tooManyFiles
}

/// PROTOCOL §6.7 gönderen: klasörden özyinelemeli dosya listesi. Sembolik bağlar izlenmez (atlanır), boş klasörler taşınmaz.
/// `dir` = `<klasör adı>/<alt/yol>` (ayırıcı `/`); kök klasörün kendi adı en üst bileşendir.
public enum FolderScanner {
    /// Dosyaları göreli yola göre sıralı ve `startId`ten artan kimliklerle döndürür (devamda aynı kimlikler için sıra sabit).
    /// Kaynaklar tembeldir (okuma başına aç/kapat): binlerce dosyada tutamaç tükenmez.
    public static func items(folder: URL, startId: Int = 0, maxFiles: Int = TransferLimits.maxFiles) throws -> [SendItem] {
        try files(folder: folder, maxFiles: maxFiles).enumerated().map { i, f in
            try DiskFileSource.makeItem(id: startId + i, url: f.url, dir: f.dir, lazy: true)
        }
    }

    /// Yalnız (URL, `dir`) listesi; sıralama `items` ile aynı.
    public static func files(folder: URL, maxFiles: Int = TransferLimits.maxFiles) throws -> [(url: URL, dir: String)] {
        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: folder.path, isDirectory: &isDir), isDir.boolValue else {
            throw FolderScanError.notADirectory(folder.lastPathComponent)
        }
        var found: [(dirComponents: [String], url: URL)] = []
        try walk(folder, components: [folder.lastPathComponent], fm: fm, into: &found, maxFiles: maxFiles)
        found.sort { a, b in
            let l = a.dirComponents.joined(separator: "/"), r = b.dirComponents.joined(separator: "/")
            return l != r ? l < r : a.url.lastPathComponent < b.url.lastPathComponent
        }
        return found.map { ($0.url, $0.dirComponents.joined(separator: "/")) }
    }

    private static func walk(_ dir: URL, components: [String], fm: FileManager,
                             into found: inout [(dirComponents: [String], url: URL)], maxFiles: Int) throws {
        guard components.count <= FolderPath.maxDepth else { throw FolderScanError.tooDeep(components.joined(separator: "/")) }
        guard components.joined(separator: "/").utf16.count <= FolderPath.maxLength else {
            throw FolderScanError.pathTooLong(components.joined(separator: "/"))
        }
        let keys: [URLResourceKey] = [.isSymbolicLinkKey, .isDirectoryKey, .isRegularFileKey]
        let children = try fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: keys, options: [])
        for child in children {
            let v = try child.resourceValues(forKeys: Set(keys))
            if v.isSymbolicLink == true { continue }   // izlenmez
            if v.isDirectory == true {
                try walk(child, components: components + [child.lastPathComponent], fm: fm, into: &found, maxFiles: maxFiles)
            } else if v.isRegularFile == true {
                if found.count + 1 > maxFiles { throw FolderScanError.tooManyFiles }
                found.append((components, child))
            }
        }
    }
}
