import Foundation

/// Uygulamanın dosya konumları.
enum AppPaths {
    /// Alınan dosyalar: Documents (Dosyalar uygulamasında görünür; UIFileSharingEnabled).
    static var receivedDirectory: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
    }

    /// Yarım kalan alımlar (devam ettirme için kalıcı olmalı; yedeklenmesi gerekmez).
    static var partialDirectory: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let dir = base.appendingPathComponent("Partial", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    /// Fotoğraf seçicisinden kopyalanan geçici gönderim dosyaları.
    static var outgoingTempDirectory: URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("WinDropOutgoing", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }
}
