import Foundation
import SwiftUI
import UniformTypeIdentifiers

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

    /// Gönderim bitince: güvenlik kapsamını kapat, geçici kopyaları sil.
    static func release(_ files: [OutgoingFile]) {
        for file in files {
            if file.securityScoped { file.url.stopAccessingSecurityScopedResource() }
            if file.isTemporary { try? FileManager.default.removeItem(at: file.url.deletingLastPathComponent()) }
        }
    }
}
