import Foundation
import Network
import WinDropKit

struct HostPort: Sendable, Hashable {
    var host: String
    var port: UInt16
}

enum DataClientError: Error, LocalizedError {
    case noCandidates
    case invalidEndpoint
    case timeout
    case allFailed
    case localNetworkDenied
    /// TCP veri oturumunda 30 sn boyunca hiç veri akmadı (DECISIONS 6).
    case idleTimeout

    var errorDescription: String? {
        switch self {
        case .noCandidates: return String(localized: "Bağlanılacak adres yok.")
        case .invalidEndpoint: return String(localized: "Geçersiz adres veya port.")
        case .timeout: return String(localized: "Bağlantı zaman aşımına uğradı.")
        case .allFailed: return String(localized: "Hiçbir adrese bağlanılamadı.")
        case .localNetworkDenied: return LocalNetworkPermission.deniedMessage
        case .idleTimeout: return String(localized: "Bağlantı zaman aşımına uğradı: bilgisayardan veri gelmiyor.")
        }
    }
}

/// `Network.framework` NWConnection'ı WinDropKit `ByteStream`'ine uyarlar.
/// `read(exactly:)` tam istenen kadar okur (fazla okumaz), bu yüzden ara tampon gerekmez.
final class NWByteStream: ByteStream, @unchecked Sendable {
    private let connection: NWConnection
    private let lock = NSLock()
    private var idleExpired = false
    /// Okuma/yazma bu süre ilerlemezse bağlantı kesilir (DECISIONS 6: TCP veri oturumunda boşta kalma 30 sn).
    static let idleTimeout: TimeInterval = Timeouts.dataIdle

    init(connection: NWConnection) {
        self.connection = connection
    }

    /// Bekleyen bir okuma/yazma için boşta kalma zamanlayıcısı; süre dolarsa bağlantı iptal edilir (bekleyen çağrı hata ile döner).
    private func armIdleTimer() -> DispatchWorkItem {
        let item = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.lock.lock(); self.idleExpired = true; self.lock.unlock()
            self.connection.cancel()
        }
        DispatchQueue.global().asyncAfter(deadline: .now() + Self.idleTimeout, execute: item)
        return item
    }

    private func mapIdle(_ error: Error) -> Error {
        lock.lock(); defer { lock.unlock() }
        return idleExpired ? DataClientError.idleTimeout : error
    }

    func read(exactly count: Int) async throws -> Data {
        var result = Data()
        while result.count < count {
            let (chunk, isComplete) = try await receiveChunk(maxLength: count - result.count)
            result.append(chunk)
            if result.count < count, isComplete {
                throw ByteStreamError.endOfStream(partial: result)
            }
        }
        return result
    }

    func write(_ data: Data) async throws {
        let timer = armIdleTimer()
        defer { timer.cancel() }
        do {
            try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                    connection.send(content: data, completion: .contentProcessed { error in
                        if let error {
                            cont.resume(throwing: error)
                        } else {
                            cont.resume()
                        }
                    })
                }
            } onCancel: {
                self.connection.cancel()
            }
        } catch {
            throw mapIdle(error)
        }
    }

    func close() {
        connection.cancel()
    }

    private func receiveChunk(maxLength: Int) async throws -> (Data, Bool) {
        let timer = armIdleTimer()
        defer { timer.cancel() }
        do {
            return try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { (cont: CheckedContinuation<(Data, Bool), Error>) in
                    connection.receive(minimumIncompleteLength: 1, maximumLength: maxLength) { data, _, isComplete, error in
                        if let error {
                            cont.resume(throwing: error)
                        } else {
                            cont.resume(returning: (data ?? Data(), isComplete))
                        }
                    }
                }
            } onCancel: {
                self.connection.cancel()
            }
        } catch {
            throw mapIdle(error)
        }
    }
}

/// Tek adaya TCP bağlantı denemesi (zaman aşımlı, iptal edilebilir).
private final class NWConnectAttempt: @unchecked Sendable {
    private let connection: NWConnection
    private let queue = DispatchQueue(label: "com.efe.windrop.connect")
    private let lock = NSLock()
    private var continuation: CheckedContinuation<NWByteStream, Error>?
    private var finished = false
    private var waiting = false
    private var connectionStart = Date()
    /// Yerel ağ izin sorusu açıkken (.waiting) bağlantı en çok bu kadar beklenir.
    static let permissionWait: TimeInterval = 30

    init(candidate: HostPort) throws {
        guard let port = NWEndpoint.Port(rawValue: candidate.port), !candidate.host.isEmpty else {
            throw DataClientError.invalidEndpoint
        }
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        // TCP keepalive (DECISIONS 6): PC uyursa / menzil dışına çıkılırsa RST gelmese de ölü bağlantı fark edilir.
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 10
        tcp.keepaliveInterval = 5
        tcp.keepaliveCount = 4
        connection = NWConnection(
            host: NWEndpoint.Host(candidate.host), port: port, using: NWParameters(tls: nil, tcp: tcp))
    }

    func run(timeout: TimeInterval) async throws -> NWByteStream {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<NWByteStream, Error>) in
                lock.lock()
                if finished {
                    lock.unlock()
                    cont.resume(throwing: CancellationError())
                    return
                }
                continuation = cont
                lock.unlock()
                connection.stateUpdateHandler = { [weak self] state in
                    self?.handle(state)
                }
                connectionStart = Date()
                connection.start(queue: queue)
                queue.asyncAfter(deadline: .now() + timeout) { [weak self] in
                    self?.expire()
                }
            }
        } onCancel: {
            self.finish(.failure(CancellationError()))
        }
    }

    private func handle(_ state: NWConnection.State) {
        switch state {
        case .ready:
            finish(.success(NWByteStream(connection: connection)))
        case .failed(let error):
            finish(.failure(error))
        case .cancelled:
            finish(.failure(CancellationError()))
        case .waiting(let error):
            // Yerel ağ izin sorusu açıkken bağlantı .waiting'de kalır: başarısız sayılmaz, yanıt beklenir.
            if LocalNetworkPermission.isPolicyDenied(error) {
                LocalNetworkPermission.shared.markDenied()
                finish(.failure(DataClientError.localNetworkDenied))
                return
            }
            lock.lock(); waiting = true; lock.unlock()
        default:
            break
        }
    }

    /// Aday zaman aşımı: izin sorusu yanıtlanmamışsa (bağlantı .waiting, izin durumu bilinmiyor) 30 sn'ye kadar bekler.
    private func expire() {
        lock.lock()
        let isWaiting = waiting
        let done = finished
        lock.unlock()
        if done { return }
        switch LocalNetworkPermission.shared.state {
        case .denied:
            finish(.failure(DataClientError.localNetworkDenied))
        case .unknown where isWaiting && connectionStart.timeIntervalSinceNow > -Self.permissionWait:
            queue.asyncAfter(deadline: .now() + 0.5) { [weak self] in self?.expire() }
        default:
            finish(.failure(DataClientError.timeout))
        }
    }

    private func finish(_ result: Result<NWByteStream, Error>) {
        lock.lock()
        if finished {
            lock.unlock()
            return
        }
        finished = true
        let cont = continuation
        continuation = nil
        lock.unlock()
        if case .failure = result {
            connection.stateUpdateHandler = nil
            connection.cancel()
        }
        cont?.resume(with: result)
    }
}

/// PROTOCOL §6.1: adaylara paralel bağlanır (aday başına 1,5 sn), ilk başarılı kazanır.
enum DataClient {
    /// Tek tur: tüm adaylara paralel deneme.
    static func connect(
        to candidates: [HostPort],
        perAttemptTimeout: TimeInterval = ProtocolIds.candidateTimeout
    ) async throws -> NWByteStream {
        guard !candidates.isEmpty else { throw DataClientError.noCandidates }
        return try await withThrowingTaskGroup(of: NWByteStream.self, returning: NWByteStream.self) { group in
            for candidate in candidates {
                group.addTask {
                    let attempt = try NWConnectAttempt(candidate: candidate)
                    return try await attempt.run(timeout: perAttemptTimeout)
                }
            }
            while true {
                do {
                    guard let winner = try await group.next() else { break }
                    group.cancelAll()
                    // Aynı anda başarılı olan diğer bağlantıları kapat (sızıntı olmasın).
                    while true {
                        do {
                            guard let extra = try await group.next() else { break }
                            extra.close()
                        } catch {
                            continue
                        }
                    }
                    return winner
                } catch {
                    continue
                }
            }
            throw DataClientError.allFailed
        }
    }

    /// `deadline`'a kadar turları tekrarlar (ilk yerel ağ izni istemi 1,5 sn'den uzun sürebilir;
    /// etkin noktaya katılma gecikmesi de buradan karşılanır).
    static func connect(
        to candidates: [HostPort],
        retryUntil deadline: Date,
        perAttemptTimeout: TimeInterval = ProtocolIds.candidateTimeout
    ) async throws -> NWByteStream {
        var lastError: Error = DataClientError.allFailed
        repeat {
            try Task.checkCancellation()
            do {
                return try await connect(to: candidates, perAttemptTimeout: perAttemptTimeout)
            } catch {
                lastError = error
                if LocalNetworkPermission.shared.state == .denied { throw DataClientError.localNetworkDenied }
            }
            try await Task.sleep(nanoseconds: 500_000_000)
        } while Date() < deadline
        throw lastError
    }
}
