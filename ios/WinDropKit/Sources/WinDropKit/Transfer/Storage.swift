import Foundation

/// Kısmi dosya anahtarı: `(eş kimliği, transferId, dosya id)` (PROTOCOL §5).
public struct PartialKey: Hashable {
    public var peerId: String
    public var transferId: TransferId
    public var fileId: Int
    public init(peerId: String, transferId: TransferId, fileId: Int) {
        self.peerId = peerId; self.transferId = transferId; self.fileId = fileId
    }
}

/// Önceki oturumda tamamen alınmış ve teslim edilmiş dosya kaydı (devamda RESUME = boyut, yeniden gönderilmez).
public struct CompletedFile: Equatable {
    public var size: Int64
    public var mtime: Int64
    public var sha256: String
    public var storedPath: String
    public init(size: Int64, mtime: Int64, sha256: String, storedPath: String) {
        self.size = size; self.mtime = mtime; self.sha256 = sha256; self.storedPath = storedPath
    }
}

/// Alıcıdaki yarım dosya.
public protocol PartialFile: AnyObject {
    /// Dosyadaki bayt sayısı (açılışta son kalıcılaştırılmış ofset).
    var offset: Int64 { get }
    /// Dosyanın `at` ofsetinden okur (devamda mevcut ön eki hash'lemek için).
    func read(at: Int64, length: Int) throws -> Data
    func append(_ data: Data) throws
    /// Veriyi diske yazıp fsync eder, sonra kalıcı ofseti kaydeder. ACK yalnız bundan sonra gönderilir.
    func sync() throws
    /// Tamamlanan dosyayı hedef klasöre benzersiz adla ("ad (2).ext") taşır, tamamlandı kaydını yazar; son yolu döndürür.
    func commit(fileName: String, sha256: String) throws -> String
    /// Kısmi veriyi ve kaydını siler.
    func discard() throws
}

/// Alıcı depolama soyutlaması.
public protocol ReceiverStorage: AnyObject {
    func freeSpaceBytes() throws -> Int64
    /// Önceki oturumda tamamlanmış ve hâlâ yerinde duran dosya (yoksa nil).
    func completedFile(for key: PartialKey) throws -> CompletedFile?
    /// Mevcut kısmi dosyayı açar: boyut ve mtime aynıysa son kalıcı ofsete kırpılmış olarak; aksi halde eskisi silinip
    /// boş yeni dosya.
    func openPartial(for key: PartialKey, size: Int64, mtime: Int64) throws -> PartialFile
}

// MARK: Dosya sistemi uygulamaları

/// `FileManager` tabanlı gönderen kaynağı.
public final class DiskFileSource: FileSource {
    private let handle: FileHandle

    public init(url: URL) throws {
        handle = try FileHandle(forReadingFrom: url)
    }

    deinit { try? handle.close() }

    public func read(at offset: Int64, length: Int) throws -> Data {
        try handle.seek(toOffset: UInt64(offset))
        return try handle.read(upToCount: length) ?? Data()
    }

    /// Dosyanın boyutunu ve değişiklik zamanını (Unix ms) okuyup `SendItem` üretir.
    public static func makeItem(id: Int, url: URL, name: String? = nil) throws -> SendItem {
        let attrs = try FileManager.default.attributesOfItem(atPath: url.path)
        let size = (attrs[.size] as? NSNumber)?.int64Value ?? 0
        let date = (attrs[.modificationDate] as? Date) ?? Date(timeIntervalSince1970: 0)
        let mtime = Int64((date.timeIntervalSince1970 * 1000).rounded())
        return SendItem(id: id, name: name ?? url.lastPathComponent, size: size, mtime: mtime,
                        source: try DiskFileSource(url: url))
    }
}

/// Hedef klasör + gizli `.windrop` alt klasöründe kısmi dosyalar (`.part`), kayıtlar (`.meta`) ve tamamlandı kayıtları (`.done`).
public final class DirectoryReceiverStorage: ReceiverStorage {
    public let root: URL
    let stateDir: URL
    private let fm = FileManager.default

    public init(directory: URL) throws {
        root = directory
        stateDir = directory.appendingPathComponent(".windrop", isDirectory: true)
        try fm.createDirectory(at: stateDir, withIntermediateDirectories: true)
    }

    public func freeSpaceBytes() throws -> Int64 {
        let attrs = try fm.attributesOfFileSystem(forPath: root.path)
        return (attrs[.systemFreeSize] as? NSNumber)?.int64Value ?? 0
    }

    func base(_ key: PartialKey) -> String {
        let safePeer = String(key.peerId.unicodeScalars.map { c -> Character in
            let v = c.value
            let ok = (v >= 0x30 && v <= 0x39) || (v >= 0x41 && v <= 0x5A) || (v >= 0x61 && v <= 0x7A) || v == 0x2D || v == 0x5F
            return ok ? Character(c) : "_"
        })
        return "\(safePeer)-\(key.transferId)-\(key.fileId)"
    }

    func url(_ key: PartialKey, _ ext: String) -> URL { stateDir.appendingPathComponent(base(key) + "." + ext) }

    struct Meta: Codable { var size: Int64; var mtime: Int64; var durable: Int64 }
    struct Done: Codable { var size: Int64; var mtime: Int64; var sha256: String; var path: String }

    public func completedFile(for key: PartialKey) throws -> CompletedFile? {
        guard let data = try? Data(contentsOf: url(key, "done")),
              let d = try? JSONDecoder().decode(Done.self, from: data),
              fm.fileExists(atPath: d.path) else { return nil }
        return CompletedFile(size: d.size, mtime: d.mtime, sha256: d.sha256, storedPath: d.path)
    }

    public func openPartial(for key: PartialKey, size: Int64, mtime: Int64) throws -> PartialFile {
        let part = url(key, "part"), metaURL = url(key, "meta")
        var meta = Meta(size: size, mtime: mtime, durable: 0)
        if let data = try? Data(contentsOf: metaURL), let old = try? JSONDecoder().decode(Meta.self, from: data),
           old.size == size, old.mtime == mtime, fm.fileExists(atPath: part.path) {
            let onDisk = ((try? fm.attributesOfItem(atPath: part.path))?[.size] as? NSNumber)?.int64Value ?? 0
            meta.durable = min(old.durable, onDisk, size)
        } else {
            try? fm.removeItem(at: part)
            try? fm.removeItem(at: metaURL)
        }
        if !fm.fileExists(atPath: part.path) {
            guard fm.createFile(atPath: part.path, contents: nil) else { throw CocoaError(.fileWriteUnknown) }
            meta.durable = 0
        }
        let handle = try FileHandle(forUpdating: part)
        try handle.truncate(atOffset: UInt64(meta.durable))   // kalıcılaşmamış kuyruğu at
        try handle.seek(toOffset: UInt64(meta.durable))
        return DiskPartialFile(storage: self, key: key, handle: handle, meta: meta)
    }

    func uniqueFinalURL(_ name: String) -> URL {
        let unique = FileNameSanitizer.makeUnique(name, in: DirectoryExistence(root: root))
        return root.appendingPathComponent(unique)
    }

    struct DirectoryExistence: FileNameExistence {
        let root: URL
        func exists(_ fileName: String) -> Bool {
            FileManager.default.fileExists(atPath: root.appendingPathComponent(fileName).path)
        }
    }
}

final class DiskPartialFile: PartialFile {
    private unowned let storage: DirectoryReceiverStorage
    private let key: PartialKey
    private let handle: FileHandle
    private var meta: DirectoryReceiverStorage.Meta
    private(set) var offset: Int64
    private var closed = false

    init(storage: DirectoryReceiverStorage, key: PartialKey, handle: FileHandle, meta: DirectoryReceiverStorage.Meta) {
        self.storage = storage; self.key = key; self.handle = handle; self.meta = meta
        self.offset = meta.durable
    }

    deinit { if !closed { try? handle.close() } }

    func read(at position: Int64, length: Int) throws -> Data {
        try handle.seek(toOffset: UInt64(position))
        let d = try handle.read(upToCount: length) ?? Data()
        try handle.seek(toOffset: UInt64(offset))
        return d
    }

    func append(_ data: Data) throws {
        try handle.write(contentsOf: data)
        offset += Int64(data.count)
    }

    func sync() throws {
        try handle.synchronize()
        meta.durable = offset
        try JSONEncoder().encode(meta).write(to: storage.url(key, "meta"), options: .atomic)
    }

    func commit(fileName: String, sha256: String) throws -> String {
        try handle.synchronize()
        try handle.close()
        closed = true
        let dest = storage.uniqueFinalURL(fileName)
        try FileManager.default.moveItem(at: storage.url(key, "part"), to: dest)
        let done = DirectoryReceiverStorage.Done(size: meta.size, mtime: meta.mtime, sha256: sha256, path: dest.path)
        try JSONEncoder().encode(done).write(to: storage.url(key, "done"), options: .atomic)
        try? FileManager.default.removeItem(at: storage.url(key, "meta"))
        return dest.path
    }

    func discard() throws {
        if !closed { try? handle.close(); closed = true }
        try? FileManager.default.removeItem(at: storage.url(key, "part"))
        try? FileManager.default.removeItem(at: storage.url(key, "meta"))
    }
}
