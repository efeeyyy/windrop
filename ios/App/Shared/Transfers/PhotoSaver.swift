import Foundation
import Photos

enum PhotoSaveError: Error, LocalizedError {
    case denied
    case nothingToSave

    var errorDescription: String? {
        switch self {
        case .denied: return "Fotoğraflar'a ekleme izni verilmedi. Ayarlar'dan izin verin."
        case .nothingToSave: return "Kaydedilecek fotoğraf veya video yok."
        }
    }
}

/// Ayar: gelen fotoğraf ve videolar nereye kaydedilsin (`@AppStorage(IncomingMediaDestination.key)`).
enum IncomingMediaDestination: String, CaseIterable, Identifiable {
    case photos
    case files

    static let key = "incomingMediaDestination"

    var id: String { rawValue }
    var title: String { self == .photos ? "Fotoğraflar" : "Dosyalar" }

    /// Kayıtlı seçim; yoksa/okunamıyorsa varsayılan: Fotoğraflar.
    static var current: IncomingMediaDestination {
        IncomingMediaDestination(rawValue: UserDefaults.standard.string(forKey: key) ?? "") ?? .photos
    }
}

/// Alınan fotoğraf/videoları Fotoğraflar'a ekler (yalnız ekleme izni: `.addOnly`; ilk kullanımda sorulur).
enum PhotoSaver {
    /// Medya dosyalarını Fotoğraflar'a ekler; kaydedilenleri döndürür (kalan dosyalar yerinde durur).
    /// - Parameter move: true ise dosya Fotoğraflar'a taşınır (Documents'tan kalkar); false ise kopyalanır.
    /// Tarih "şimdi" yapılır: EXIF tarihi ne olursa olsun galeride en sonda görünür.
    static func save(_ files: [StoredFile], move: Bool = false) async throws -> [StoredFile] {
        let media = files.filter { $0.mediaKind != nil }
        guard !media.isEmpty else { throw PhotoSaveError.nothingToSave }

        let status = await PHPhotoLibrary.requestAuthorization(for: .addOnly)
        guard status == .authorized || status == .limited else { throw PhotoSaveError.denied }

        let now = Date()
        try await PHPhotoLibrary.shared().performChanges {
            for (index, file) in media.enumerated() {
                let options = PHAssetResourceCreationOptions()
                options.shouldMoveFile = move
                let request = PHAssetCreationRequest.forAsset()
                request.addResource(
                    with: file.mediaKind == .video ? .video : .photo, fileURL: file.url, options: options)
                // Aynı gruptaki dosyalar gönderim sırasını korusun (1 ms aralıklı).
                request.creationDate = now.addingTimeInterval(Double(index) * 0.001)
            }
        }
        return media
    }
}
