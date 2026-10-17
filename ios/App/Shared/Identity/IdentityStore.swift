import Foundation
import Observation
import WinDropKit

/// Tanıdık bilgisayar kaydı (PROTOCOL §0, §2b). `id` = açık anahtarın SHA-256'sının ilk 16 baytı (hex; Kit `DeviceId`).
/// `autoAccept` yalnız QR ile eşleştirilenlerde true (PROTOCOL §7); onayla "tanıdık" olanlarda false.
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
@MainActor
@Observable
final class IdentityStore {
    private(set) var knownPCs: [KnownPC] = []
    private(set) var hotspots: [String: HotspotCredentials] = [:]

    @ObservationIgnored private var cachedKey: NoisePrivateKey?

    private static let keyAccount = "device-key-v1"
    private static let knownAccount = "known-pcs-v1"
    private static func hotspotAccount(_ pcID: String) -> String { "hotspot-v1-\(pcID)" }

    init() {
        if let data = try? Keychain.read(Self.knownAccount),
           let list = try? JSONDecoder().decode([KnownPC].self, from: data) {
            knownPCs = list
        }
        for pc in knownPCs {
            if let data = try? Keychain.read(Self.hotspotAccount(pc.id)),
               let creds = try? JSONDecoder().decode(HotspotCredentials.self, from: data) {
                hotspots[pc.id] = creds
            }
        }
    }

    /// Kalıcı X25519 cihaz anahtarı; yoksa üretilip Keychain'e yazılır.
    func deviceKey() throws -> NoisePrivateKey {
        if let cachedKey { return cachedKey }
        if let raw = try Keychain.read(Self.keyAccount) {
            let key = try NoisePrivateKey(rawRepresentation: raw)
            cachedKey = key
            return key
        }
        let key = NoisePrivateKey()
        try Keychain.write(key.rawRepresentation, account: Self.keyAccount)
        cachedKey = key
        return key
    }

    func hotspot(for pcID: String) -> HotspotCredentials? { hotspots[pcID] }

    func setHotspot(_ credentials: HotspotCredentials, for pcID: String) {
        hotspots[pcID] = credentials
        if let data = try? JSONEncoder().encode(credentials) {
            try? Keychain.write(data, account: Self.hotspotAccount(pcID))
        }
    }

    /// Aynı `id` varsa değiştirir. Var olan `autoAccept` düşürülmez (onaylı tanıdık, QR ile eşleşmiş kaydı ezmesin).
    func upsert(_ pc: KnownPC, hotspot: HotspotCredentials? = nil) throws {
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
        let list = knownPCs.filter { $0.id != pc.id }
        try? persist(list)
        knownPCs = list
        hotspots[pc.id] = nil
        try? Keychain.delete(Self.hotspotAccount(pc.id))
    }

    func pc(withPeripheral id: UUID) -> KnownPC? {
        knownPCs.first { $0.peripheralID == id }
    }

    func pc(withKeyID id: String) -> KnownPC? {
        knownPCs.first { $0.id == id }
    }

    private func persist(_ list: [KnownPC]) throws {
        let data = try JSONEncoder().encode(list)
        try Keychain.write(data, account: Self.knownAccount)
    }
}
