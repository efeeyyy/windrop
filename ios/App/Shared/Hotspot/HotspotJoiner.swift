import Foundation
#if HOTSPOT_ENTITLEMENT
import NetworkExtension
#endif

enum HotspotJoinOutcome: Sendable, Equatable {
    /// Ağa katılındı (veya zaten bağlı).
    case joined
    /// Yetki yok: kullanıcı Ayarlar'dan bir kez katılmalı (SSID/parola kopyalanabilir).
    case manualRequired
}

/// PC'nin Mobil Etkin Noktasına katılma (PROTOCOL §6.2).
/// `NEHotspotConfiguration` yalnız ücretli hesapta (`HOTSPOT_ENTITLEMENT` bayrağı, Release-Hotspot).
enum HotspotJoiner {
    static var automaticJoinAvailable: Bool {
        #if HOTSPOT_ENTITLEMENT
        return true
        #else
        return false
        #endif
    }

    /// Etkin noktaya katılma vazgeçildi (başka yol kazandı): ağ profilini siler, iPhone etkin noktaya geçmez.
    /// Yetki yoksa (elle katılım) yapılacak bir şey yok.
    static func abandon(_ credentials: HotspotCredentials) {
        #if HOTSPOT_ENTITLEMENT
        NEHotspotConfigurationManager.shared.removeConfiguration(forSSID: credentials.ssid)
        #endif
    }

    static func join(_ credentials: HotspotCredentials) async throws -> HotspotJoinOutcome {
        #if HOTSPOT_ENTITLEMENT
        let configuration = NEHotspotConfiguration(ssid: credentials.ssid, passphrase: credentials.pass, isWEP: false)
        configuration.joinOnce = true   // aktarım bitince ağ profili silinir (kalıcı kayıt yok)
        do {
            try await NEHotspotConfigurationManager.shared.apply(configuration)
        } catch {
            let nsError = error as NSError
            if nsError.domain == NEHotspotConfigurationErrorDomain,
               nsError.code == NEHotspotConfigurationError.alreadyAssociated.rawValue {
                return .joined
            }
            throw error
        }
        return .joined
        #else
        return .manualRequired
        #endif
    }
}
