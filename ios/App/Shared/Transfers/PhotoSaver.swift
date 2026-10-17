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

/// Alınan fotoğraf/videoları Fotoğraflar'a ekler (yalnız ekleme izni: `.addOnly`).
enum PhotoSaver {
    static func save(_ files: [StoredFile]) async throws -> Int {
        let media: [(url: URL, isVideo: Bool)] = files.compactMap { file in
            guard let kind = file.mediaKind else { return nil }
            return (file.url, kind == .video)
        }
        guard !media.isEmpty else { throw PhotoSaveError.nothingToSave }

        let status = await PHPhotoLibrary.requestAuthorization(for: .addOnly)
        guard status == .authorized || status == .limited else { throw PhotoSaveError.denied }

        try await PHPhotoLibrary.shared().performChanges {
            for item in media {
                let request = PHAssetCreationRequest.forAsset()
                request.addResource(with: item.isVideo ? .video : .photo, fileURL: item.url, options: nil)
            }
        }
        return media.count
    }
}
