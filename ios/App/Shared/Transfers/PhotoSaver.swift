import Foundation
import Photos
import QuickLookThumbnailing
import UIKit

enum PhotoSaveError: Error, LocalizedError {
    case denied
    case nothingToSave

    var errorDescription: String? {
        switch self {
        case .denied: return String(localized: "Fotoğraflar'a ekleme izni verilmedi. Ayarlar'dan izin verin.")
        case .nothingToSave: return String(localized: "Kaydedilecek fotoğraf veya video yok.")
        }
    }
}

/// Ayar: gelen fotoğraf ve videolar nereye kaydedilsin (`@AppStorage(IncomingMediaDestination.key)`).
enum IncomingMediaDestination: String, CaseIterable, Identifiable {
    case photos
    case files

    static let key = "incomingMediaDestination"

    var id: String { rawValue }
    var title: String { self == .photos ? String(localized: "Fotoğraflar") : String(localized: "Dosyalar") }

    /// Kayıtlı seçim; yoksa/okunamıyorsa varsayılan: Fotoğraflar.
    static var current: IncomingMediaDestination {
        IncomingMediaDestination(rawValue: UserDefaults.standard.string(forKey: key) ?? "") ?? .photos
    }
}

/// Alınan fotoğraf/videoları Fotoğraflar'a ekler (yalnız ekleme izni: `.addOnly`; ilk kullanımda sorulur).
enum PhotoSaver {
    /// Fotoğraflar "yalnız ekleme" izni henüz sorulmadı mı? (Arka planda istem gösterilemez: taşıma ertelenir.)
    static var needsPermissionPrompt: Bool {
        PHPhotoLibrary.authorizationStatus(for: .addOnly) == .notDetermined
    }

    /// Medya dosyalarını Fotoğraflar'a ekler; kaydedilenleri döndürür (kalan dosyalar yerinde durur).
    /// - Parameter move: true ise dosya Fotoğraflar'a taşınır (Documents'tan kalkar); false ise kopyalanır.
    /// Tarih "şimdi" yapılır: EXIF tarihi ne olursa olsun galeride en sonda görünür.
    static func save(_ files: [StoredFile], move: Bool = false) async throws -> [StoredFile] {
        try await saveWithAssets(files, move: move).files
    }

    /// `save` + oluşturulan öğelerin `PHAsset.localIdentifier`'ları (giriş sırasıyla; alınamayan atlanır).
    static func saveWithAssets(
        _ files: [StoredFile], move: Bool = false
    ) async throws -> (files: [StoredFile], assetIDs: [String]) {
        let media = files.filter { $0.mediaKind != nil }
        guard !media.isEmpty else { throw PhotoSaveError.nothingToSave }

        let status = await PHPhotoLibrary.requestAuthorization(for: .addOnly)
        guard status == .authorized || status == .limited else { throw PhotoSaveError.denied }

        let now = Date()
        let collected = IdentifierBox()
        try await PHPhotoLibrary.shared().performChanges {
            for (index, file) in media.enumerated() {
                let options = PHAssetResourceCreationOptions()
                options.shouldMoveFile = move
                let request = PHAssetCreationRequest.forAsset()
                request.addResource(
                    with: file.mediaKind == .video ? .video : .photo, fileURL: file.url, options: options)
                // Aynı gruptaki dosyalar gönderim sırasını korusun (1 ms aralıklı).
                request.creationDate = now.addingTimeInterval(Double(index) * 0.001)
                if let id = request.placeholderForCreatedAsset?.localIdentifier { collected.append(id) }
            }
        }
        return (media, collected.values)
    }

    /// Küçük resmi KAYDETMEDEN önce dosyadan üretir (QuickLook; `.addOnly` izinde Fotoğraflar okunamayabilir,
    /// ek izin istenmez). Üretilemezse nil.
    static func makeThumbnail(for url: URL, points: CGFloat = 56, scale: CGFloat = 3) async -> UIImage? {
        guard FileManager.default.isReadableFile(atPath: url.path) else { return nil }
        let request = QLThumbnailGenerator.Request(
            fileAt: url, size: CGSize(width: points, height: points), scale: scale, representationTypes: .thumbnail)
        return try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: request).uiImage
    }

    /// Yedek: Fotoğraflar okuma izni ZATEN varsa (izin istenmez) kaydedilen öğenin küçük resmi.
    static func assetThumbnail(localIdentifier: String, points: CGFloat = 56, scale: CGFloat) async -> UIImage? {
        let status = PHPhotoLibrary.authorizationStatus(for: .readWrite)
        guard status == .authorized || status == .limited,
              let asset = PHAsset.fetchAssets(withLocalIdentifiers: [localIdentifier], options: nil).firstObject
        else { return nil }
        let options = PHImageRequestOptions()
        options.deliveryMode = .highQualityFormat   // tek geri çağrı
        options.resizeMode = .fast
        options.isNetworkAccessAllowed = false
        let size = CGSize(width: points * scale, height: points * scale)
        return await withCheckedContinuation { (continuation: CheckedContinuation<UIImage?, Never>) in
            let once = OnceFlag()
            PHImageManager.default().requestImage(for: asset, targetSize: size, contentMode: .aspectFill, options: options) {
                image, info in
                if (info?[PHImageResultIsDegradedKey] as? Bool) == true { return }
                guard once.claim() else { return }
                continuation.resume(returning: image)
            }
        }
    }
}

private final class IdentifierBox: @unchecked Sendable {
    private let lock = NSLock()
    private var ids: [String] = []
    func append(_ id: String) { lock.lock(); ids.append(id); lock.unlock() }
    var values: [String] { lock.lock(); defer { lock.unlock() }; return ids }
}

private final class OnceFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var used = false
    func claim() -> Bool { lock.lock(); defer { lock.unlock() }; if used { return false }; used = true; return true }
}
