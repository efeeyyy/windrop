import Foundation
@testable import WinDropKit

enum TestError: Error { case badHex(String) }

func hex(_ s: String) throws -> Data {
    let chars = Array(s.utf8)
    guard chars.count % 2 == 0 else { throw TestError.badHex(s) }
    var out = Data()
    var i = 0
    func nib(_ c: UInt8) throws -> UInt8 {
        switch c {
        case 0x30...0x39: return c - 0x30
        case 0x61...0x66: return c - 0x61 + 10
        case 0x41...0x46: return c - 0x41 + 10
        default: throw TestError.badHex(s)
        }
    }
    while i < chars.count {
        out.append(try nib(chars[i]) << 4 | nib(chars[i + 1]))
        i += 2
    }
    return out
}

func toHex(_ d: Data) -> String {
    d.map { String(format: "%02x", $0) }.joined()
}

/// Repo kökü: Tests/WinDropKitTests/<dosya> -> ../../../../.. (WinDrop/)
func repoRoot(file: String = #filePath) -> URL {
    var url = URL(fileURLWithPath: file)
    for _ in 0..<5 { url.deleteLastPathComponent() }   // dosya, WinDropKitTests, Tests, WinDropKit, ios
    return url
}

// MARK: Bellek içi bayt akışı

/// Parça kuyruğu (okuma imleci): büyük aktarımlarda O(n^2) kopya yapmaz.
actor ByteQueue {
    private var chunks: [Data] = []
    private var head = 0          // chunks[0] içindeki okunmuş bayt sayısı
    private var available = 0
    private var closed = false
    private var waiter: (need: Int, cont: CheckedContinuation<Data, Error>)?

    func write(_ d: Data) {
        guard !d.isEmpty else { return }
        chunks.append(d)
        available += d.count
        wake()
    }

    func close() {
        closed = true
        wake()
    }

    /// Okunmamış bayt sayısı (testler).
    var pendingBytes: Int { available }

    func read(exactly n: Int) async throws -> Data {
        if available >= n { return take(n) }
        if closed { throw endOfStream() }
        return try await withCheckedThrowingContinuation { cont in
            waiter = (n, cont)
        }
    }

    private func take(_ n: Int) -> Data {
        var out = Data()
        out.reserveCapacity(n)
        while out.count < n {
            let chunk = chunks[0]
            let start = chunk.startIndex + head
            let take = min(n - out.count, chunk.endIndex - start)
            out.append(chunk[start..<(start + take)])
            head += take
            if head == chunk.count { chunks.removeFirst(); head = 0 }
        }
        available -= n
        return out
    }

    private func endOfStream() -> ByteStreamError {
        let partial = take(available)
        return .endOfStream(partial: partial)
    }

    private func wake() {
        guard let w = waiter else { return }
        if available >= w.need {
            waiter = nil
            w.cont.resume(returning: take(w.need))
        } else if closed {
            waiter = nil
            w.cont.resume(throwing: endOfStream())
        }
    }
}

struct PipeCut: Error {}

final class PipeEnd: ByteStream {
    let incoming: ByteQueue
    let outgoing: ByteQueue
    /// Kalan yazma bütçesi; aşılırsa iki yön de kapanır ve yazma `PipeCut` fırlatır (kopma simülasyonu).
    var writeBudget: Int?
    init(incoming: ByteQueue, outgoing: ByteQueue) { self.incoming = incoming; self.outgoing = outgoing }
    func read(exactly count: Int) async throws -> Data { try await incoming.read(exactly: count) }
    func write(_ data: Data) async throws {
        if let b = writeBudget {
            if data.count > b {
                await outgoing.close()
                await incoming.close()
                throw PipeCut()
            }
            writeBudget = b - data.count
        }
        await outgoing.write(data)
    }
    func close() async { await outgoing.close() }
    func closeBoth() async { await outgoing.close(); await incoming.close() }

    static func makePair() -> (PipeEnd, PipeEnd) {
        let a = ByteQueue(), b = ByteQueue()
        return (PipeEnd(incoming: a, outgoing: b), PipeEnd(incoming: b, outgoing: a))
    }
}

// MARK: Bellek içi BLE bağlantısı

actor PayloadQueue {
    private var items: [Data] = []
    private var closed = false
    private var waiter: CheckedContinuation<Data?, Never>?

    func push(_ d: Data) {
        if let w = waiter { waiter = nil; w.resume(returning: d) } else { items.append(d) }
    }

    func close() {
        closed = true
        if let w = waiter { waiter = nil; w.resume(returning: nil) }
    }

    func pop() async -> Data? {
        if !items.isEmpty { return items.removeFirst() }
        if closed { return nil }
        return await withCheckedContinuation { waiter = $0 }
    }
}

final class MemoryBleLink: BleLink {
    let mtu: Int
    private let outgoing: PayloadQueue
    private let incoming: PayloadQueue
    private(set) var written: [Data] = []
    init(mtu: Int, outgoing: PayloadQueue, incoming: PayloadQueue) {
        self.mtu = mtu; self.outgoing = outgoing; self.incoming = incoming
    }
    func write(_ payload: Data) async throws {
        precondition(payload.count <= mtu - 3, "GATT yükü MTU-3 baytı aşıyor")
        written.append(payload)
        await outgoing.push(payload)
    }
    func read() async throws -> Data? { await incoming.pop() }
    func close() async { await outgoing.close() }

    static func makePair(mtu: Int = 23) -> (MemoryBleLink, MemoryBleLink) {
        let a = PayloadQueue(), b = PayloadQueue()
        return (MemoryBleLink(mtu: mtu, outgoing: a, incoming: b), MemoryBleLink(mtu: mtu, outgoing: b, incoming: a))
    }
}
