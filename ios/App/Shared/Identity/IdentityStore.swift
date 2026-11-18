import Foundation
import Observation
import WinDropKit

/// Tanıdık bilgisayar kaydı (PROTOCOL §1, §3.1). `id` = açık anahtarın SHA-256'sının ilk 16 baytı (hex; Kit `DeviceId`).
/// `autoAccept` yalnız QR ile eşleştirilenlerde true (PROTOCOL §8); onayla "tanıdık" olanlarda false.
struct KnownPC: Codable, Identifiable, Equatable, Sendable {
    var id: String
    /// Kayıt anındaki ad; arayüzde her zaman bu gösterilir (`hello.name` değil).
    var name: String
    var publicKey: Data
    /// CoreBluetooth çevre birimi tanımlayıcısı (bekleyen bağlantı için).
    var peripheralID: UUID?
    var addedAt: Date
    var autoAccept: Bool
}

/// PC'nin Mobil Etkin Nokta bilgisi (eşleştirmede veya `hotspot` mesajında şifreli kanaldan gelir).
struct HotspotCredentials: Codable, Equatable, Sendable {
    var ssid: String
    var pass: String
}

/// Kimlik: cihaz anahtarı (Keychain), tanıdık PC listesi (Keychain), etkin nokta bilgileri (Keychain).
/// Ortak alan (`Keychain.isSharedAvailable`): uygulama ve Paylaşım Uzantısı aynı anahtarı/listeyi kullanır; böylece PC'de
/// tek "iPhone" görünür. Ortak alan yoksa her hedef kendi alanını kullanır (uzantının ayrı kimliği olur).
@MainActor
@Observable
final class IdentityStore {
    private(set) var knownPCs: [KnownPC] = []
    private(set) var hotspots: [String: HotspotCredentials] = [:]

    @ObservationIgnored private var cachedKey: NoisePrivateKey?
    /// Bu örnek ortak Keychain alanını mı kullanıyor?
    @ObservationIgnored let usesSharedStore: Bool

    private static let keyAccount = "device-key-v1"
    private static let knownAccount = "known-pcs-v1"
    private static func hotspotAccount(_ pcID: String) -> String { "hotspot-v1-\(pcID)" }

    init() {
        if Keychain.isSharedAvailable {
            if ProtocolIds.isExtension {
                // Uzantı ortak anahtarı YALNIZ ana uygulama göç ettirdiyse/ürettiyse kullanır; yoksa kendi alanında kalır
                // (uzantı ortak alana anahtar yazmaz: ana uygulamanın mevcut anahtarı korunur).
                usesSharedStore = (try? Keychain.read(Self.keyAccount, shared: true)) != nil
            } else {
                Self.migrateLegacyToShared()
                usesSharedStore = true
            }
        } else {
            usesSharedStore = false
        }
        reload()
    }

    /// Ana uygulamanın mevcut (yerel alandaki) kimliğini ortak alana taşır; kimlik değişmez. Anahtar EN SON yazılır
    /// (ortak alanda anahtar varlığı = göç tamam). Ortak alanda zaten anahtar varsa dokunulmaz. Yerel kopya silinmez.
    private static func migrateLegacyToShared() {
        if (try? Keychain.read(keyAccount, shared: true)) != nil { return }
        if (try? Keychain.read(knownAccount, shared: true)) == nil,
           let data = try? Keychain.read(knownAccount) {
            try? Keychain.write(data, account: knownAccount, shared: true)
            if let list = try? JSONDecoder().decode([KnownPC].self, from: data) {
                for pc in list {
                    if let creds = try? Keychain.read(hotspotAccount(pc.id)) {
                        try? Keychain.write(creds, account: hotspotAccount(pc.id), shared: true)
                    }
                }
            }
        }
        if let raw = try? Keychain.read(keyAccount) {
            try? Keychain.write(raw, account: keyAccount, shared: true)
        }
    }

    /// Saklı listeyi yeniden okur (uzantı/uygulama diğer tarafın değişikliğini görsün). Okuma hatasında mevcut değer korunur.
    func reload() {
        do {
            if let data = try Keychain.read(Self.knownAccount, shared: usesSharedStore) {
                knownPCs = try JSONDecoder().decode([KnownPC].self, from: data)
            } else {
                knownPCs = []
            }
        } catch {
            // okunamadı: mevcut liste korunur
        }
        var loaded: [String: HotspotCredentials] = [:]
        for pc in knownPCs {
            if let data = try? Keychain.read(Self.hotspotAccount(pc.id), shared: usesSharedStore),
               let creds = try? JSONDecoder().decode(HotspotCredentials.self, from: data) {
                loaded[pc.id] = creds
            }
        }
        hotspots = loaded
    }

    /// Kalıcı X25519 cihaz anahtarı; yoksa üretilip Keychain'e yazılır.
    func deviceKey() throws -> NoisePrivateKey {
        if let cachedKey { return cachedKey }
        if let raw = try Keychain.read(Self.keyAccount, shared: usesSharedStore) {
            let key = try NoisePrivateKey(rawRepresentation: raw)
            cachedKey = key
            return key
        }
        let key = NoisePrivateKey()
        try Keychain.write(key.rawRepresentation, account: Self.keyAccount, shared: usesSharedStore)
        cachedKey = key
        return key
    }

    func hotspot(for pcID: String) -> HotspotCredentials? { hotspots[pcID] }

    func setHotspot(_ credentials: HotspotCredentials, for pcID: String) {
        hotspots[pcID] = credentials
        if let data = try? JSONEncoder().encode(credentials) {
            try? Keychain.write(data, account: Self.hotspotAccount(pcID), shared: usesSharedStore)
        }
    }

    /// Aynı `id` varsa değiştirir. Var olan `autoAccept` düşürülmez (onaylı tanıdık, QR ile eşleşmiş kaydı ezmesin).
    func upsert(_ pc: KnownPC, hotspot: HotspotCredentials? = nil) throws {
        reload()   // uygulama/uzantı arasında eskimiş listeyi ezmemek için
        var entry = pc
        if let old = knownPCs.first(where: { $0.id == pc.id }) {
            entry.autoAccept = old.autoAccept || pc.autoAccept
            if entry.peripheralID == nil { entry.peripheralID = old.peripheralID }
        }
        var list = knownPCs.filter { $0.id != pc.id }
        list.append(entry)
        try persist(list)
        knownPCs = list
        if let hotspot { setHotspot(hotspot, for: pc.id) }
    }

    func remove(_ pc: KnownPC) {
        reload()
        let list = knownPCs.filter { $0.id != pc.id }
        try? persist(list)
        knownPCs = list
        hotspots[pc.id] = nil
        try? Keychain.delete(Self.hotspotAccount(pc.id), shared: usesSharedStore)
    }

    func pc(withPeripheral id: UUID) -> KnownPC? {
        knownPCs.first { $0.peripheralID == id }
    }

    func pc(withKeyID id: String) -> KnownPC? {
        knownPCs.first { $0.id == id }
    }

    private func persist(_ list: [KnownPC]) throws {
        let data = try JSONEncoder().encode(list)
        try Keychain.write(data, account: Self.knownAccount, shared: usesSharedStore)
    }
}
