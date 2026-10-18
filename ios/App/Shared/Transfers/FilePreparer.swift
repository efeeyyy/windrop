import Foundation
import SwiftUI
import UniformTypeIdentifiers
@preconcurrency import WinDropKit

/// PhotosPicker öğesinden dosya alır; geçici dizine kopyalar (seçici dosyası kapanış dönünce silinir).
struct PickedFile: Transferable {
    let url: URL

    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(importedContentType: .data) { received in
            let dir = AppPaths.outgoingTempDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let destination = dir.appendingPathComponent(received.file.lastPathComponent)
            try FileManager.default.copyItem(at: received.file, to: destination)
            return PickedFile(url: destination)
        }
    }
}

enum FilePreparer {
    /// URL'den gönderim dosyası. Klasörler ve okunamayanlar `nil`. `id` sonra atanır.
    static func outgoing(url: URL, securityScoped: Bool, isTemporary: Bool) -> OutgoingFile? {
        guard let values = try? url.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey, .isDirectoryKey]),
              values.isDirectory != true,
              let size = values.fileSize else { return nil }
        return OutgoingFile(
            id: 0, url: url, name: url.lastPathComponent, size: Int64(size),
            modified: values.contentModificationDate, securityScoped: securityScoped, isTemporary: isTemporary)
    }

    /// `.fileImporter` sonucu: güvenlik kapsamlı erişimi açar (iş bitince `release` ile kapatılır).
    @MainActor
    static func outgoing(fromImported urls: [URL]) -> [OutgoingFile] {
        urls.compactMap { url -> OutgoingFile? in
            let scoped = url.startAccessingSecurityScopedResource()
            if let file = outgoing(url: url, securityScoped: scoped, isTemporary: false) {
                return file
            }
            if scoped { url.stopAccessingSecurityScopedResource() }
            return nil
        }
    }

    /// Klasörlerin dosyalarını (PROTOCOL §5a) özyinelemeli listeler; sembolik bağlar izlenmez, boş klasörler taşınmaz.
    /// Her klasör için güvenlik kapsamlı erişim açılır (ilk dosyanın `scopeURL`'ü ile `release`'te kapatılır). Ağır olabilir:
    /// ana iş parçacığı dışında çağrılmalı.
    static func outgoing(fromImportedFolders urls: [URL]) -> (files: [OutgoingFile], error: String?) {
        var files: [OutgoingFile] = []
        var errors: [String] = []
        var usedRoots = Set<String>()
        for url in urls {
            let scoped = url.startAccessingSecurityScopedResource()
            let before = files.count
            do {
                let entries = try FolderScanner.files(folder: url, maxFiles: TransferLimits.maxFiles - files.count)
                // Aynı adlı iki kök klasör: ikincisi "ad (2)" olur (Windows gönderenle aynı).
                let top = url.lastPathComponent
                var root = top
                var n = 2
                while !usedRoots.insert(root.lowercased()).inserted { root = "\(top) (\(n))"; n += 1 }
                for entry in entries {
                    guard var file = outgoing(url: entry.url, securityScoped: false, isTemporary: false) else { continue }
                    file.dir = root == top ? entry.dir : root + entry.dir.dropFirst(top.count)
                    files.append(file)
                }
            } catch {
                errors.append(describe(error, folder: url.lastPathComponent))
            }
            if files.count > before {
                if scoped { files[before].scopeURL = url }
            } else if scoped {
                url.stopAccessingSecurityScopedResource()
                if errors.isEmpty { errors.append(String(localized: "\"\(url.lastPathComponent)\" klasöründe gönderilecek dosya yok.")) }
            }
        }
        if !errors.isEmpty {
            release(files)
            return ([], errors.joined(separator: " "))
        }
        return (files, nil)
    }

    private static func describe(_ error: Error, folder: String) -> String {
        guard let scan = error as? FolderScanError else { return error.localizedDescription }
        switch scan {
        case .notADirectory: return String(localized: "\"\(folder)\" bir klasör değil.")
        case .tooDeep: return String(localized: "\"\(folder)\" klasörü en çok \(Int(FolderPath.maxDepth)) seviye derinlikte olabilir.")
        case .pathTooLong: return String(localized: "\"\(folder)\" içindeki klasör yolu çok uzun.")
        case .tooManyFiles: return String(localized: "En çok \(Int(TransferLimits.maxFiles)) dosya gönderilebilir.")
        }
    }

    /// Gönderim bitince: güvenlik kapsamını kapat, geçici kopyaları sil.
    static func release(_ files: [OutgoingFile]) {
        for file in files {
            if file.securityScoped { file.url.stopAccessingSecurityScopedResource() }
            file.scopeURL?.stopAccessingSecurityScopedResource()
            if file.isTemporary {
                try? FileManager.default.removeItem(at: file.tempRoot ?? file.url.deletingLastPathComponent())
            }
        }
    }
}
