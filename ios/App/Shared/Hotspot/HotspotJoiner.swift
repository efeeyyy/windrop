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

    /// Etkin noktaya katılır (en çok `Timeouts.hotspotJoin` = 120 sn; aşılırsa `DeadlineError.timedOut` ve profil geri alınır).
    /// - Parameter persistent: tanıdık/eşli PC (parola sabit) için true: ağ kalıcı kaydedilir (`joinOnce = false`), iOS
    ///   katılma onayını yalnız ilk seferde sorar ve arka plana geçince ağı bırakmaz. Bilinmeyen PC için false: profil aktarımla biter.
    static func join(_ credentials: HotspotCredentials, persistent: Bool) async throws -> HotspotJoinOutcome {
        #if HOTSPOT_ENTITLEMENT
        let ssid = credentials.ssid
        let passphrase = credentials.pass
        do {
            return try await raceWithTimeout(seconds: Timeouts.hotspotJoin) {
                let configuration = NEHotspotConfiguration(ssid: ssid, passphrase: passphrase, isWEP: false)
                configuration.joinOnce = !persistent
                do {
                    try await NEHotspotConfigurationManager.shared.apply(configuration)
                } catch {
                    let nsError = error as NSError
                    if nsError.domain == NEHotspotConfigurationErrorDomain,
                       nsError.code == NEHotspotConfigurationError.alreadyAssociated.rawValue {
                        return HotspotJoinOutcome.joined
                    }
                    throw error
                }
                return HotspotJoinOutcome.joined
            }
        } catch let error as DeadlineError {
            NEHotspotConfigurationManager.shared.removeConfiguration(forSSID: ssid)   // geç tamamlansa da iPhone etkin noktaya geçmesin
            throw error
        }
        #else
        return .manualRequired
        #endif
    }
}
