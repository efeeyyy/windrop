import Foundation
import UniformTypeIdentifiers

enum ShareLoadError: Error, LocalizedError {
    case unreadable

    var errorDescription: String? { "Dosya okunamadı." }
}

/// `NSItemProvider`'ı eşzamanlılık sınırından geçirmek için (yalnız `loadFileRepresentation` çağrılır).
private struct ProviderBox: @unchecked Sendable {
    let provider: NSItemProvider
}

/// `NSItemProvider` -> geçici dosya. Bellek sınırı (~120 MB) nedeniyle içerik `Data` olarak yüklenmez:
/// `loadFileRepresentation` sistemin geçici dosyasını verir; geri çağrı dönmeden önce `copyItem` ile
/// kendi geçici dizinimize (akışla, diskten diske) kopyalanır. Gönderim bitince `FilePreparer.release` siler.
enum ShareItemLoader {
    @MainActor
    static func loadFile(from provider: NSItemProvider) async throws -> OutgoingFile {
        let typeID = pickType(provider)
        let dir = AppPaths.outgoingTempDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let copied: URL
        do {
            copied = try await copy(ProviderBox(provider: provider), typeID: typeID, into: dir)
        } catch {
            try? FileManager.default.removeItem(at: dir)
            throw error
        }
        guard let file = FilePreparer.outgoing(url: copied, securityScoped: false, isTemporary: true) else {
            try? FileManager.default.removeItem(at: dir)
            throw ShareLoadError.unreadable
        }
        return file
    }

    /// Klasör öğesi mi (Dosyalar'dan paylaşılan `public.folder`)?
    static func isFolder(_ provider: NSItemProvider) -> Bool {
        provider.hasItemConformingToTypeIdentifier(UTType.folder.identifier)
    }

    /// Öğeyi gönderilecek dosyalara çevirir: klasör ise (PROTOCOL §5a) içeriği geçici dizine kopyalanıp özyinelemeli taranır
    /// (her dosya `dir` taşır, sembolik bağlar izlenmez); değilse tek dosya.
    @MainActor
    static func loadFiles(from provider: NSItemProvider) async throws -> [OutgoingFile] {
        guard isFolder(provider) else { return [try await loadFile(from: provider)] }
        let root = AppPaths.outgoingTempDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        do {
            let copied = try await copy(ProviderBox(provider: provider), typeID: UTType.folder.identifier, into: root)
            let entries = try FolderScanner.files(folder: copied)
            var files: [OutgoingFile] = []
            for entry in entries {
                guard var file = FilePreparer.outgoing(url: entry.url, securityScoped: false, isTemporary: true) else { continue }
                file.dir = entry.dir
                file.tempRoot = root
                files.append(file)
            }
            guard !files.isEmpty else { throw ShareLoadError.unreadable }
            return files
        } catch {
            try? FileManager.default.removeItem(at: root)
            throw error
        }
    }

    /// Geri çağrı arka plan kuyruğunda çalışır; bu yüzden ana aktörden bağımsız bir işlevde kurulur.
    private nonisolated static func copy(_ box: ProviderBox, typeID: String, into dir: URL) async throws -> URL {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<URL, Error>) in
            _ = box.provider.loadFileRepresentation(forTypeIdentifier: typeID) { tempURL, error in
                guard let tempURL else {
                    cont.resume(throwing: error ?? ShareLoadError.unreadable)
                    return
                }
                do {
                    let destination = dir.appendingPathComponent(tempURL.lastPathComponent)
                    try FileManager.default.copyItem(at: tempURL, to: destination)
                    cont.resume(returning: destination)
                } catch {
                    cont.resume(throwing: error)
                }
            }
        }
    }

    /// Dosya verisi olarak yüklenebilen ilk (en özgün) tür; yoksa genel `public.data`.
    private static func pickType(_ provider: NSItemProvider) -> String {
        let ids = provider.registeredTypeIdentifiers
        return ids.first { UTType($0)?.conforms(to: .data) == true } ?? ids.first ?? UTType.data.identifier
    }
}
