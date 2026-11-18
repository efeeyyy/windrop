import CoreBluetooth
import Foundation
import WinDropKit

/// Protokol sabitleri (iOS tarafı). TEK yer: Windows tarafıyla eşitlenecek değerler burada.
enum ProtocolIds {
    // Windows ProtocolIds.cs ile aynı (esas kaynak). İki serviste de karakteristik UUID'leri aynıdır.
    static let serviceUUIDString = "493C70B6-634D-4935-BE1F-B79B1D05DBD8"
    /// Yedek servis: SIG taban UUID'sine gömülü 32 bit (0x6C5A0F3D); Windows 128 bit reklam Aborted olursa bunu yayınlar.
    static let fallbackServiceUUIDString = "6C5A0F3D-0000-1000-8000-00805F9B34FB"
    static let c2pUUIDString = "C235BDC3-B25A-49A5-963B-5040E65B708D"
    static let p2cUUIDString = "AE26EAD2-8247-4508-BCB6-31D11568BF07"

    // CBUUID Sendable olmayabilir; statik saklamak yerine her seferinde üretilir.
    static var serviceUUID: CBUUID { CBUUID(string: serviceUUIDString) }
    static var fallbackServiceUUID: CBUUID { CBUUID(string: fallbackServiceUUIDString) }
    static var c2pUUID: CBUUID { CBUUID(string: c2pUUIDString) }
    static var p2cUUID: CBUUID { CBUUID(string: p2cUUIDString) }
    /// iOS 32 bit UUID'yi bazen kısa, bazen 128 bit gösterir: ikisi de aranır/eşlenir.
    static var fallbackServiceUUIDShort: CBUUID { CBUUID(string: "6C5A0F3D") }
    /// Servis keşfi: önce ana, yoksa yedek.
    static var discoveryServices: [CBUUID] { [serviceUUID, fallbackServiceUUID, fallbackServiceUUIDShort] }

    static func isWinDropService(_ uuid: CBUUID) -> Bool {
        uuid == serviceUUID || uuid == fallbackServiceUUID || uuid == fallbackServiceUUIDShort
    }

    /// Üretici verisi işareti: şirket kimliği 0xFFFF (LE: FF FF), ardından "WD", sürüm, "WinDrop-<ad>".
    static let markerCompanyId: UInt16 = 0xFFFF

    /// CoreBluetooth durum geri yükleme kimliği.
    static let restoreIdentifier = "com.efe.windrop.central"

    /// Kod Paylaşım Uzantısı (`.appex`) içinde mi çalışıyor? Uzantı kısa ömürlüdür: durum geri yükleme,
    /// arka plan görevi ve bildirim yok (ana uygulamanın durum geri yüklemesiyle çakışmaz).
    static let isExtension: Bool = Bundle.main.bundlePath.hasSuffix(".appex")

    /// PROTOCOL §4.3: PC'nin oturumu kapattığını bildiren, `p2c` üzerinden tek bildirim olarak gelen 3 bayt ASCII `WDR`.
    static let resetMarker = Data("WDR".utf8)

    /// DECISIONS 2: PC, IK ilk mesajındaki statik anahtar güvenilen listede yoksa bağlantıyı kapatmadan önce `p2c` üzerinden
    /// tek bildirim olarak 3 bayt ASCII `WDU` ("unknown") yollar. iPhone eşleşme/tanıdık kaydını YALNIZ bunu alınca siler.
    static let unknownMarker = Data("WDU".utf8)

    /// PROTOCOL §6.1: PC'nin dinlediği veri portu.
    static let tcpPort: UInt16 = 53600

    /// PROTOCOL §7.1: aday başına bağlanma süresi.
    static let candidateTimeout: TimeInterval = 1.5

    /// `hello.v` (bilgi amaçlı).
    static let helloVersion = Int(Prefix.currentVersion)

    /// Üretici verisi işaretinden PC adı ("WinDrop-<ad>" etiketinin `<ad>` kısmı); işaret yoksa nil.
    static func markerPCName(_ advertisement: [String: Any]) -> String? {
        guard let data = advertisement[CBAdvertisementDataManufacturerDataKey] as? Data, data.count >= 5 else { return nil }
        let bytes = [UInt8](data)
        guard UInt16(bytes[0]) | (UInt16(bytes[1]) << 8) == markerCompanyId, bytes[2] == 0x57, bytes[3] == 0x44 else { return nil }
        // bytes[4] = sürüm; sonrası ASCII "WinDrop-<ad>".
        let label = String(decoding: bytes[5...], as: UTF8.self)
        let prefix = "WinDrop-"
        return label.hasPrefix(prefix) ? String(label.dropFirst(prefix.count)) : label
    }

    /// Reklam bir WinDrop PC'sine mi ait: ana servis UUID'si VEYA yedek servis UUID'si VEYA üretici verisi işareti.
    /// Yanlış PC'yi `k` doğrulaması eler (`pcName` yalnız bilgi amaçlıdır; işaret adı kesik/ASCII olabilir).
    static func advertisementMatches(_ advertisement: [String: Any], pcName: String) -> Bool {
        if let uuids = advertisement[CBAdvertisementDataServiceUUIDsKey] as? [CBUUID], uuids.contains(where: isWinDropService) {
            return true
        }
        if let uuids = advertisement[CBAdvertisementDataOverflowServiceUUIDsKey] as? [CBUUID], uuids.contains(where: isWinDropService) {
            return true
        }
        return markerPCName(advertisement) != nil
    }
}
