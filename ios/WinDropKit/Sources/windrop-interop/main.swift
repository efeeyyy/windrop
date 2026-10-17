import Foundation
import WinDropKit
#if canImport(CryptoKit)
import CryptoKit
#else
import Crypto
#endif
#if canImport(Glibc)
import Glibc
#elseif canImport(Darwin)
import Darwin
#endif

// windrop-interop: C# WinDrop.Interop ile gerçek TCP üzerinden çapraz uyum denemesi (docs/INTEROP.md).
// Swift her zaman bağlanan + Noise başlatan taraftır (iPhone rolü); veri yönü komutla seçilir.
//
//   send         --host H --port P --prefix-transfer-id <b64url> --pattern ik|xx --my-key <hex32> --peer-pub <hex32>
//                [--kill-at-bytes N] <dosyalar...>        Swift -> C# (C# alıcı)
//   receive-from --host H --port P --prefix-transfer-id <b64url> --pattern ik|xx --my-key <hex32> --peer-pub <hex32>
//                --out DIR --count N --total BYTES         C# -> Swift (Swift alıcı)
//   pubkey       --my-key <hex32>                          açık anahtarı yazdırır
//
// Çıktı: "KEY=değer" satırları (betikler ayrıştırır). Çıkış kodu: 0 = Completed, 1 = diğer sonuç, 2 = hata.

setvbuf(stdout, nil, _IOLBF, 0)
signal(SIGPIPE, SIG_IGN)

func out(_ s: String) { print(s) }

func fail(_ s: String) -> Never {
    FileHandle.standardError.write(Data(("ERROR: " + s + "\n").utf8))
    exit(2)
}

func parseHex(_ s: String) -> Data? {
    guard s.count % 2 == 0 else { return nil }
    var d = Data()
    var i = s.startIndex
    while i < s.endIndex {
        let j = s.index(i, offsetBy: 2)
        guard let b = UInt8(s[i..<j], radix: 16) else { return nil }
        d.append(b)
        i = j
    }
    return d
}

func hex(_ d: Data) -> String { d.map { String(format: "%02x", $0) }.joined() }

// MARK: POSIX TCP akışı

final class PosixTcpStream: ByteStream, @unchecked Sendable {
    private let fd: Int32
    private let readQueue = DispatchQueue(label: "interop.read")
    private let writeQueue = DispatchQueue(label: "interop.write")

    private init(fd: Int32) { self.fd = fd }

    static func connect(host: String, port: UInt16) throws -> PosixTcpStream {
        #if canImport(Glibc)
        let sockType = Int32(SOCK_STREAM.rawValue)
        #else
        let sockType = SOCK_STREAM
        #endif
        let fd = socket(AF_INET, sockType, 0)
        guard fd >= 0 else { throw POSIXError(.EIO) }
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = port.bigEndian
        let h = host == "localhost" ? "127.0.0.1" : host
        guard inet_pton(AF_INET, h, &addr.sin_addr) == 1 else { Foundation.close(fd); fail("IPv4 adresi bekleniyor: \(host)") }
        let rc = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Foundation.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard rc == 0 else {
            let e = errno
            Foundation.close(fd)
            throw POSIXError(POSIXErrorCode(rawValue: e) ?? .EIO)
        }
        var one: Int32 = 1
        setsockopt(fd, Int32(IPPROTO_TCP), TCP_NODELAY, &one, socklen_t(MemoryLayout<Int32>.size))
        return PosixTcpStream(fd: fd)
    }

    func read(exactly count: Int) async throws -> Data {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Data, Error>) in
            readQueue.async {
                var buf = [UInt8](repeating: 0, count: count)
                var got = 0
                while got < count {
                    let n = buf.withUnsafeMutableBytes { recv(self.fd, $0.baseAddress! + got, count - got, 0) }
                    if n > 0 { got += n; continue }
                    if n < 0 && errno == EINTR { continue }
                    cont.resume(throwing: ByteStreamError.endOfStream(partial: Data(buf[0..<got])))
                    return
                }
                cont.resume(returning: Data(buf))
            }
        }
    }

    func write(_ data: Data) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            writeQueue.async {
                let bytes = [UInt8](data)
                var sent = 0
                while sent < bytes.count {
                    #if canImport(Glibc)
                    let flags = Int32(MSG_NOSIGNAL)
                    #else
                    let flags: Int32 = 0
                    #endif
                    let n = bytes.withUnsafeBytes { send(self.fd, $0.baseAddress! + sent, bytes.count - sent, flags) }
                    if n > 0 { sent += n; continue }
                    if n < 0 && errno == EINTR { continue }
                    cont.resume(throwing: POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO))
                    return
                }
                cont.resume()
            }
        }
    }
}

// MARK: Bağımsız değişkenler

struct Options {
    var values: [String: String] = [:]
    var positional: [String] = []
    init(_ args: [String]) {
        var i = 0
        while i < args.count {
            let a = args[i]
            if a.hasPrefix("--") {
                guard i + 1 < args.count else { fail("\(a) için değer yok") }
                values[String(a.dropFirst(2))] = args[i + 1]
                i += 2
            } else {
                positional.append(a)
                i += 1
            }
        }
    }
    func need(_ k: String) -> String {
        guard let v = values[k] else { fail("--\(k) gerekli") }
        return v
    }
}

let argv = Array(CommandLine.arguments.dropFirst())
guard let command = argv.first else { fail("komut yok: send | receive-from | pubkey") }
let opts = Options(Array(argv.dropFirst()))

func loadKey(_ name: String) -> NoisePrivateKey {
    guard let raw = parseHex(opts.need(name)), raw.count == 32, let k = try? NoisePrivateKey(rawRepresentation: raw) else {
        fail("--\(name) 32 bayt onaltılık olmalı")
    }
    return k
}

if command == "pubkey" {
    out("PUB=" + hex(loadKey("my-key").publicKey.rawRepresentation))
    exit(0)
}

// C# `signals` ile birebir aynı değerler (PROTOCOL §4/§9a).
func sampleSignals() -> [SignalMessage] {
    let id = "AQIDBAUGBwgJCgsMDQ4PEA"
    return [
        .hello(HelloMessage(v: 1, name: "Efe'nin iPhone'u", caps: ["tcp", "hotspot", "relay"], wifi: "connected", btAudio: true)),
        .status(StatusMessage(wifi: "none", btAudio: false)),
        .offer(OfferMessage(transferId: id, count: 3, total: 12345678901, preview: ["a.txt", "b.png"])),
        .accept(ReplyMessage(transferId: id)),
        .reject(ReplyMessage(transferId: id, reason: "meşgul")),
        .addr(AddrMessage(transferId: id, candidates: [AddressCandidate(ip: "192.168.1.5", port: 53600), AddressCandidate(ip: "fe80::1", port: 53600)])),
        .hotspot(HotspotMessage(transferId: id, ip: "192.168.137.1", port: 53600, ssid: "WinDrop-1A2B3C4D", pass: "p@ss/word")),
        .pairInfo(PairInfo(ssid: "WinDrop-1A2B3C4D", pass: "p@ss/word")),
        .relay(RelayMessage(transferId: id, token: "tok123")),
        .path(PathMessage(transferId: id, result: .hotspotFail)),
        .cancel(SignalCancelMessage(transferId: id)),
    ]
}

func encodedLine(_ m: SignalMessage) -> String {
    guard let d = try? m.encode(), let s = String(data: d, encoding: .utf8) else { fail("kodlanamadı") }
    return s
}

if command == "signals" {
    let lines = sampleSignals().map(encodedLine)
    do { try (lines.joined(separator: "\n") + "\n").write(toFile: opts.need("out"), atomically: true, encoding: .utf8) }
    catch { fail("\(error)") }
    out("SIGNALS_WRITTEN=\(lines.count)")
    exit(0)
}

if command == "signals-verify" {
    let own = sampleSignals()
    guard let text = try? String(contentsOfFile: opts.need("file"), encoding: .utf8) else { fail("dosya okunamadı") }
    let lines = text.split(separator: "\n").map(String.init)
    var bad = lines.count == own.count ? 0 : 1
    if bad == 1 { out("SIGNAL_COUNT_MISMATCH other=\(lines.count) own=\(own.count)") }
    for i in 0..<min(lines.count, own.count) {
        var status = "OK"
        do {
            let decoded = try SignalMessage.decode(Data(lines[i].utf8))
            let a = encodedLine(decoded), b = encodedLine(own[i])
            if a != b { status = "MISMATCH decoded=\(a) expected=\(b)" }
        } catch {
            status = "DECODE_FAIL \(error)"
        }
        if status != "OK" { bad += 1 }
        out("SIGNAL \(i) \(own[i].type) \(status)")
    }
    out(bad == 0 ? "SIGNALS=ok" : "SIGNALS=FAIL")
    exit(bad == 0 ? 0 : 1)
}

let host = opts.need("host")
guard let port = UInt16(opts.need("port")) else { fail("--port geçersiz") }
guard let transferId = TransferId.parse(opts.need("prefix-transfer-id")) else { fail("--prefix-transfer-id 22 karakter base64url olmalı") }
let pattern = opts.need("pattern")
let myKey = loadKey("my-key")
guard let peerPub = parseHex(opts.need("peer-pub")), peerPub.count == 32 else { fail("--peer-pub 32 bayt onaltılık olmalı") }

func sha256Hex(of url: URL) throws -> String {
    var hasher = SHA256()
    let h = try FileHandle(forReadingFrom: url)
    defer { try? h.close() }
    while let chunk = try h.read(upToCount: 1 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
    return hasher.finalize().map { String(format: "%02x", $0) }.joined()
}

func connectChannel() async throws -> SecureChannel {
    out("CONNECT=\(host):\(port) pattern=\(pattern) transferId=\(transferId)")
    let stream = try PosixTcpStream.connect(host: host, port: port)
    let transport = TcpMessageTransport(stream: stream)
    let prefix = try Prefix(transport: .tcp, transferId: transferId.bytes)
    switch pattern {
    case "ik":
        return try await SecureChannel.connectIK(transport: transport, prefix: prefix, localKey: myKey, remoteStaticKey: peerPub)
    case "xx":
        let ch = try await SecureChannel.connectXX(transport: transport, prefix: prefix, localKey: myKey)
        guard ch.remoteStaticKey == peerPub else { fail("XX: karşı anahtar beklenenle aynı değil") }
        return ch
    default:
        fail("--pattern ik|xx")
    }
}

func report(_ r: TransferResult) -> Int32 {
    out("OUTCOME=\(r.outcome)")
    out("FAILED=\(r.failedFileIds)")
    if let e = r.error { out("TRANSFER_ERROR=\(e)") }
    for (id, off) in r.resumeOffsets.sorted(by: { $0.key < $1.key }) { out("RESUME file=\(id) offset=\(off)") }
    return r.outcome == .completed ? 0 : 1
}

func run() async -> Int32 {
    do {
        let channel = try await connectChannel()
        out("HANDSHAKE=ok kind=\(pattern) sas=\(channel.verificationCode) hash=\(hex(channel.handshakeHash))")
        switch command {
        case "send":
            guard !opts.positional.isEmpty else { fail("gönderilecek dosya yok") }
            var items: [SendItem] = []
            for (i, path) in opts.positional.enumerated() {
                items.append(try DiskFileSource.makeItem(id: i + 1, url: URL(fileURLWithPath: path)))
            }
            let killAt = opts.values["kill-at-bytes"].flatMap { Int64($0) }
            let sender = FileSender(channel: channel, items: items, progress: { p in
                if let k = killAt, p.bytesDone >= k {
                    out("KILL=at bytesDone=\(p.bytesDone)")
                    kill(getpid(), SIGKILL)   // gerçek ölüm: temiz kapanış yok
                }
            })
            return report(try await sender.run())
        case "receive-from":
            guard let count = Int(opts.need("count")), let total = Int64(opts.need("total")) else { fail("--count/--total sayı olmalı") }
            let dir = URL(fileURLWithPath: opts.need("out"))
            let storage = try DirectoryReceiverStorage(directory: dir)
            let ropts = ReceiverOptions(peerId: DeviceId.hex(publicKey: peerPub), transferId: transferId,
                                        expectedCount: count, expectedTotal: total)
            let result = try await FileReceiver(channel: channel, storage: storage, options: ropts).run()
            for f in result.receivedFiles {
                let url = URL(fileURLWithPath: f.storedPath)
                let size = (try? FileManager.default.attributesOfItem(atPath: f.storedPath)[.size] as? NSNumber)??.int64Value ?? -1
                out("FILE id=\(f.id) name=\(f.originalName) size=\(size) sha256=\((try? sha256Hex(of: url)) ?? "?")")
            }
            return report(result)
        default:
            fail("bilinmeyen komut: \(command)")
        }
    } catch {
        out("ERROR=\(type(of: error)): \(error)")
        return 2
    }
}

exit(await run())
