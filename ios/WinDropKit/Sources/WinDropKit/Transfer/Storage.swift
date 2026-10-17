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
    /// Tamamlanan dosyayı hedef klasörün `directory` (temizlenmiş, en üst klasörü çözülmüş; PROTOCOL §5a) alt klasörüne
    /// benzersiz adla ("ad (2).ext") taşır (klasörleri oluşturur), tamamlandı kaydını yazar; son yolu döndürür.
    func commit(directory: [String], fileName: String, sha256: String) throws -> String
    /// Kısmi veriyi ve kaydını siler.
    func discard() throws
}

extension PartialFile {
    /// Kökteki dosya (klasörsüz).
    public func commit(fileName: String, sha256: String) throws -> String {
        try commit(directory: [], fileName: fileName, sha256: sha256)
    }
}

/// Alıcı depolama soyutlaması.
public protocol ReceiverStorage: AnyObject {
    func freeSpaceBytes() throws -> Int64
    /// Önceki oturumda tamamlanmış ve hâlâ yerinde duran dosya (yoksa nil).
    func completedFile(for key: PartialKey) throws -> CompletedFile?
    /// Mevcut kısmi dosyayı açar: boyut ve mtime aynıysa son kalıcı ofsete kırpılmış olarak; aksi halde eskisi silinip
    /// boş yeni dosya.
    func openPartial(for key: PartialKey, size: Int64, mtime: Int64) throws -> PartialFile
    /// PROTOCOL §5a: aktarımın en üst klasörü `name` (temizlenmiş) için hedef adı. Hedefte aynı adlı öğe varsa "ad (2)";
    /// seçim `(peerId, transferId)` için kalıcıdır (devamda ve aynı aktarımdaki tüm dosyalar için aynı döner).
    func resolveTopFolder(peerId: String, transferId: TransferId, name: String) throws -> String
}

// MARK: Dosya sistemi uygulamaları

/// `FileManager` tabanlı gönderen kaynağı. `lazy` modda tutamaç her okumada açılıp kapanır (klasör gönderiminde
/// binlerce dosya için dosya tanımlayıcısı tükenmesin diye); varsayılan modda tutamaç baştan açık kalır.
public final class DiskFileSource: FileSource {
    private let url: URL
    private let handle: FileHandle?

    public init(url: URL, lazy: Bool = false) throws {
        self.url = url
        handle = lazy ? nil : try FileHandle(forReadingFrom: url)
    }

    deinit { try? handle?.close() }

    public func read(at offset: Int64, length: Int) throws -> Data {
        if let handle {
            try handle.seek(toOffset: UInt64(offset))
            return try handle.read(upToCount: length) ?? Data()
        }
        let h = try FileHandle(forReadingFrom: url)
        defer { try? h.close() }
        try h.seek(toOffset: UInt64(offset))
        return try h.read(upToCount: length) ?? Data()
    }

    /// Dosyanın boyutunu ve değişiklik zamanını (Unix ms) okuyup `SendItem` üretir.
    public static func makeItem(id: Int, url: URL, name: String? = nil, dir: String? = nil, lazy: Bool = false) throws -> SendItem {
        let attrs = try FileManager.default.attributesOfItem(atPath: url.path)
        let size = (attrs[.size] as? NSNumber)?.int64Value ?? 0
        let date = (attrs[.modificationDate] as? Date) ?? Date(timeIntervalSince1970: 0)
        let mtime = Int64((date.timeIntervalSince1970 * 1000).rounded())
        return SendItem(id: id, name: name ?? url.lastPathComponent, size: size, mtime: mtime,
                        source: try DiskFileSource(url: url, lazy: lazy), dir: dir)
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

    func transferBase(peerId: String, transferId: TransferId) -> String {
        let safePeer = String(peerId.unicodeScalars.map { c -> Character in
            let v = c.value
            let ok = (v >= 0x30 && v <= 0x39) || (v >= 0x41 && v <= 0x5A) || (v >= 0x61 && v <= 0x7A) || v == 0x2D || v == 0x5F
            return ok ? Character(c) : "_"
        })
        return "\(safePeer)-\(transferId)"
    }

    func base(_ key: PartialKey) -> String {
        "\(transferBase(peerId: key.peerId, transferId: key.transferId))-\(key.fileId)"
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

    /// `directory` (zaten çözülmüş bileşenler) altında, kök dışına çıkışı denetleyerek klasörü oluşturur ve benzersiz son URL'yi verir.
    func uniqueFinalURL(_ name: String, directory: [String] = []) throws -> URL {
        let dirURL = try FolderPath.resolve(root: root, components: directory)
        if !directory.isEmpty {
            try fm.createDirectory(at: dirURL, withIntermediateDirectories: true)
        }
        let unique = FileNameSanitizer.makeUnique(name, in: DirectoryExistence(root: dirURL))
        return dirURL.appendingPathComponent(unique)
    }

    // MARK: PROTOCOL §5a en üst klasör eşlemesi

    private func dirsURL(peerId: String, transferId: TransferId) -> URL {
        stateDir.appendingPathComponent(transferBase(peerId: peerId, transferId: transferId) + ".dirs")
    }

    public func resolveTopFolder(peerId: String, transferId: TransferId, name: String) throws -> String {
        let url = dirsURL(peerId: peerId, transferId: transferId)
        var map = (try? Data(contentsOf: url)).flatMap { try? JSONDecoder().decode([String: String].self, from: $0) } ?? [:]
        if let known = map[name] { return known }
        let chosen = Self.uniqueFolderName(name) { fm.fileExists(atPath: root.appendingPathComponent($0).path) }
        map[name] = chosen
        try JSONEncoder().encode(map).write(to: url, options: .atomic)
        return chosen
    }

    /// Klasör için "ad (2)", "ad (3)"… (uzantı ayrılmaz); toplam ad ≤ 255 UTF-8 bayt.
    static func uniqueFolderName(_ name: String, exists: (String) -> Bool) -> String {
        if !exists(name) { return name }
        for n in 2...10_000 {
            let suffix = " (\(n))"
            var stem = name
            while stem.utf8.count + suffix.utf8.count > FileNameSanitizer.maxLength, !stem.isEmpty { stem.removeLast() }
            let candidate = stem + suffix
            if !exists(candidate) { return candidate }
        }
        return name + " (" + UUID().uuidString + ")"
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

    func commit(directory: [String], fileName: String, sha256: String) throws -> String {
        try handle.synchronize()
        try handle.close()
        closed = true
        let dest = try storage.uniqueFinalURL(fileName, directory: directory)
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
