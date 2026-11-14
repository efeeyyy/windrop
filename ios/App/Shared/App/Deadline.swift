import Foundation

/// Tüm zaman aşımları tek yerde (docs/review/DECISIONS.md madde 3 ve 6): hiçbir bekleme süresiz kalmasın.
/// (Onaylı BLE yazması 5 sn: `BlePeripheralLink.writeTimeout`.)
enum Timeouts {
    /// BLE el sıkışması + ilk `hello`.
    static let bleHandshake: TimeInterval = 10
    /// Kabulden sonra yol bilgisi (`addr`/`hotspot`/`bleData`/`relay`) bekleme.
    static let pathInfo: TimeInterval = 30
    /// TCP / BLE / aktarma veri oturumunda boşta kalma.
    static let dataIdle: TimeInterval = 30
    /// Etkin noktaya katılma bekleme.
    static let hotspotJoin: TimeInterval = 120
    /// Kota uyarısı (aktarma yolu) onayı bekleme; PC aktarma odasını ~60-75 sn bekler.
    static let relayConsent: TimeInterval = 60
    /// iPhone teklifi: yanıt yoksa oturumu tazeler (20 sn); toplam 80 sn sonra "yanıt vermedi" (PC onay penceresi 60 sn).
    static let offerFirstWait: TimeInterval = 20
    static let offerSecondWait: TimeInterval = 60
    /// iPhone onay sayfası (PC -> iPhone): kullanıcı bu sürede karar vermezse teklif zaman aşımına düşer (PC gönderen 80 sn bekler).
    static let incomingApproval: TimeInterval = 60
}

enum DeadlineError: Error, LocalizedError {
    case timedOut

    var errorDescription: String? {
        String(localized: "Bilgisayar zamanında yanıt vermedi.")
    }
}

@MainActor
private final class DeadlineFlag { var fired = false }

private final class DeadlineGate<T>: @unchecked Sendable {
    private let lock = NSLock()
    private var cont: CheckedContinuation<T, Error>?
    init(_ cont: CheckedContinuation<T, Error>) { self.cont = cont }
    func resume(_ result: Result<T, Error>) {
        lock.lock()
        let c = cont
        cont = nil
        lock.unlock()
        c?.resume(with: result)
    }
}

/// Süreli çalıştırıcı (ana aktör): `operation` süre dolmadan bitmezse `onTimeout` çağrılır (bağlantıyı kapatıp
/// bekleyen okuma/yazmayı uyandırmalıdır) ve işlem hata ile dönünce `DeadlineError.timedOut` fırlatılır.
/// İşlem çağıranın görevinde çalışır (Sendable gerekmez); zamanlayıcı ayrı bir görevdir.
@MainActor
func withDeadline<T>(
    seconds: TimeInterval,
    onTimeout: @escaping @MainActor () -> Void,
    operation: () async throws -> T
) async throws -> T {
    let flag = DeadlineFlag()
    let timer = Task { @MainActor in
        try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
        guard !Task.isCancelled else { return }
        flag.fired = true
        onTimeout()
    }
    defer { timer.cancel() }
    do {
        return try await operation()
    } catch {
        if flag.fired { throw DeadlineError.timedOut }
        throw error
    }
}

/// İptal edilemeyen bir sistem çağrısını (ör. `NEHotspotConfigurationManager.apply`) süreyle yarıştırır: süre dolunca
/// `DeadlineError.timedOut` ile döner; asıl çağrı arkada bitebilir (sonucu atılır).
func raceWithTimeout<T: Sendable>(
    seconds: TimeInterval,
    _ operation: @escaping @Sendable () async throws -> T
) async throws -> T {
    return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<T, Error>) in
        let gate = DeadlineGate<T>(cont)
        Task {
            do { gate.resume(.success(try await operation())) } catch { gate.resume(.failure(error)) }
        }
        Task {
            try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            gate.resume(.failure(DeadlineError.timedOut))
        }
    }
}
