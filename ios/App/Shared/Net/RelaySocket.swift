import Foundation
@preconcurrency import WinDropKit

/// Aktarma sunucusu adresi (Ayarlar > Aktarma sunucusu). Adres yalnız burada tutulur; eşten gelen mesajla ASLA değişmez
/// (PROTOCOL §6 madde 3). Uzantı kendi `UserDefaults`'unu görür (App Groups yok): boşsa/okunamıyorsa aktarma yolu kapalıdır.
enum RelaySettings {
    static let key = "relayUrl"

    /// Kayıtlı adres (kırpılmış); boşsa nil.
    static func configuredURL(defaults: UserDefaults = .standard) -> String? {
        let text = defaults.string(forKey: key)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return text.isEmpty ? nil : text
    }

    /// `wss://host[:port][/önek]` biçiminde kullanılabilir mi?
    static func isValid(_ text: String) -> Bool {
        RelayRoom.endpoint(relayUrl: text, roomId: String(repeating: "0", count: RelayRoom.roomIdLength), role: .phone) != nil
    }
}

enum RelaySocketError: Error, LocalizedError {
    case timeout
    case rejected(Int)
    case failed(String)

    var errorDescription: String? {
        switch self {
        case .timeout: return "Aktarma sunucusuna bağlanılamadı (zaman aşımı)."
        case .rejected(let status):
            switch status {
            case 409: return "Aktarma odası dolu veya eş zaten bağlı."
            case 410: return "Aktarma odası kapandı."
            case 429: return "Aktarma sunucusu çok fazla istek aldı; biraz sonra deneyin."
            default: return "Aktarma sunucusu bağlantıyı reddetti (\(status))."
            }
        case .failed(let text): return "Aktarma sunucusuna bağlanılamadı: \(text)"
        }
    }
}

/// `URLSessionWebSocketTask` -> WinDropKit `WebSocketLike`. Her ikili mesaj = bir Noise mesajı (PROTOCOL §6.3).
final class RelaySocket: NSObject, WebSocketLike, URLSessionWebSocketDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var session: URLSession?
    private var task: URLSessionWebSocketTask?
    private var openContinuation: CheckedContinuation<Void, Error>?
    private var opened = false
    private var openError: Error?
    private var closeCode: URLSessionWebSocketTask.CloseCode = .invalid

    /// Bağlanır; yükseltme (101) tamamlanana dek bekler. HTTP 409/410/429 vb. `RelaySocketError.rejected` olur.
    static func connect(to url: URL, timeout: TimeInterval = 15) async throws -> RelaySocket {
        let socket = RelaySocket()
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 30
        configuration.waitsForConnectivity = false
        let session = URLSession(configuration: configuration, delegate: socket, delegateQueue: nil)
        let task = session.webSocketTask(with: url)
        task.maximumMessageSize = 1 << 17   // Noise mesajı en çok 65535 B
        socket.lock.lock()
        socket.session = session
        socket.task = task
        socket.lock.unlock()
        do {
            try await withTaskCancellationHandler {
                try await withThrowingTaskGroup(of: Void.self) { group in
                    group.addTask { try await socket.waitUntilOpen(resuming: task) }
                    group.addTask {
                        try await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                        // Zaman aşımı: görevi iptal etmek `didCompleteWithError` ile bekleyen devamı da sonlandırır.
                        task.cancel(with: .goingAway, reason: nil)
                        throw RelaySocketError.timeout
                    }
                    _ = try await group.next()
                    group.cancelAll()
                }
            } onCancel: {
                task.cancel(with: .goingAway, reason: nil)
            }
        } catch {
            await socket.close()
            if error is CancellationError || error is RelaySocketError { throw error }
            throw RelaySocketError.failed((error as NSError).localizedDescription)
        }
        return socket
    }

    private func waitUntilOpen(resuming task: URLSessionWebSocketTask) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            lock.lock()
            if opened {
                lock.unlock()
                cont.resume()
                return
            }
            if let error = openError {
                lock.unlock()
                cont.resume(throwing: error)
                return
            }
            openContinuation = cont
            lock.unlock()
            task.resume()
        }
    }

    func send(binary: Data) async throws {
        guard let task = currentTask() else { throw SessionError.connectionClosed }
        try await task.send(.data(binary))
    }

    func receive() async throws -> WebSocketMessage? {
        guard let task = currentTask() else { return nil }
        do {
            switch try await task.receive() {
            case .data(let data): return .binary(data)
            case .string(let text): return .text(text)
            @unknown default: throw SessionError.protocolViolation("bilinmeyen WebSocket mesajı")
            }
        } catch {
            if Task.isCancelled { throw error }
            lock.lock()
            let code = closeCode
            lock.unlock()
            // Karşı taraf bitirip temiz kapattı (sunucu: 1000/1001): akış sonu. Diğer her şey hata.
            if code == .normalClosure || code == .goingAway { return nil }
            throw error
        }
    }

    func close() async {
        lock.lock()
        let task = self.task
        let session = self.session
        self.task = nil
        self.session = nil
        lock.unlock()
        task?.cancel(with: .normalClosure, reason: nil)
        session?.finishTasksAndInvalidate()
    }

    private func currentTask() -> URLSessionWebSocketTask? {
        lock.lock()
        defer { lock.unlock() }
        return task
    }

    // MARK: URLSessionWebSocketDelegate

    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didOpenWithProtocol protocol: String?) {
        lock.lock()
        opened = true
        let cont = openContinuation
        openContinuation = nil
        lock.unlock()
        cont?.resume()
    }

    func urlSession(
        _ session: URLSession, webSocketTask: URLSessionWebSocketTask, didCloseWith closeCode: URLSessionWebSocketTask.CloseCode,
        reason: Data?
    ) {
        lock.lock()
        self.closeCode = closeCode
        lock.unlock()
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock()
        let alreadyOpen = opened
        let cont = openContinuation
        openContinuation = nil
        var failure: Error?
        if !alreadyOpen {
            if let http = task.response as? HTTPURLResponse, http.statusCode != 101 {
                failure = RelaySocketError.rejected(http.statusCode)
            } else {
                failure = error ?? RelaySocketError.failed("bağlantı kapandı")
            }
            openError = failure
        }
        lock.unlock()
        if let failure { cont?.resume(throwing: failure) }
    }
}
