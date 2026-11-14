import Foundation
#if canImport(CryptoKit)
import CryptoKit
#else
import Crypto
#endif

/// Cihaz kimliği = açık anahtarın SHA-256'sının ilk 16 baytı (PROTOCOL §0).
public enum DeviceId {
    public static let length = 16
    public static func from(publicKey: Data) -> Data { Data(SHA256.hash(data: publicKey).prefix(length)) }
    public static func hex(publicKey: Data) -> String { hex(from(publicKey: publicKey)) }
    public static func hex(_ id: Data) -> String { id.map { String(format: "%02x", $0) }.joined() }
}

/// Eşleştirmesiz ("yakındaki herkes", PROTOCOL §2b) doğrulama kodu: `SHA-256("WinDrop-SAS" || h)`in ilk 4 baytı
/// big-endian UInt32, mod 10000, 4 haneye sıfırla doldurulmuş. `h` el sıkışma bitince el sıkışma karmasıdır.
public enum VerificationCode {
    public static func compute(handshakeHash h: Data) -> String {
        var input = Data("WinDrop-SAS".utf8)
        input.append(h)
        let d = [UInt8](SHA256.hash(data: input))
        let n = (UInt32(d[0]) << 24) | (UInt32(d[1]) << 16) | (UInt32(d[2]) << 8) | UInt32(d[3])
        return String(format: "%04d", n % 10000)
    }
}

/// Yazmaları sıraya sokan FIFO kilit: şifreleme + taşıma yazması tek parça olur (aynı kanalda aynı anda çerçeve ve
/// sinyal gönderilebilir; BLE'de iki mesajın parçaları iç içe geçmez, Noise sayacı gönderim sırasıyla gider).
final class SendGate {
    private let lock = NSLock()
    private var busy = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func acquire() async {
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            if enqueue(cont) { cont.resume() }
        }
    }

    private func enqueue(_ cont: CheckedContinuation<Void, Never>) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if !busy {
            busy = true
            return true
        }
        waiters.append(cont)
        return false
    }

    func release() {
        lock.lock()
        let next = waiters.isEmpty ? nil : waiters.removeFirst()
        if next == nil { busy = false }
        lock.unlock()
        next?.resume()
    }
}

/// Atlanan (bilinmeyen/çözülemeyen) sinyallerin sayacı ve son nedeni (günlük/tanılama; sınama için). İş parçacığı güvenli.
public final class IgnoredSignals: @unchecked Sendable {
    public static let shared = IgnoredSignals()
    private let lock = NSLock()
    private var storedCount = 0
    private var storedLast: String?

    func record(_ reason: String) {
        lock.lock(); defer { lock.unlock() }
        storedCount += 1
        storedLast = reason
    }

    public var count: Int { lock.lock(); defer { lock.unlock() }; return storedCount }
    public var last: String? { lock.lock(); defer { lock.unlock() }; return storedLast }
}

/// Kanaldan gelen tek mesaj: §5 çerçevesi veya §4 sinyali (BLE oturumunda ayrım: düz metnin ilk baytı `{` ise sinyal,
/// 1–8 ise çerçeve; PROTOCOL §9a).
public enum SecureMessage: Equatable {
    case frame(Frame)
    case signal(SignalMessage)
}

/// Kurulmuş Noise oturumu üstünde çerçeve/sinyal alışverişi. Roller açıktır: iPhone `connect*` (başlatan),
/// PC `accept*` (yanıtlayan; testlerde kullanılır).
///
/// Kurulmuş sayma (PROTOCOL §1, §9a): IK'de el sıkışmayı yanıtlayan bitirir, bu yüzden başlatan hemen sonra
/// **boş düz metinli** bir taşıma mesajı yollar; yanıtlayan onu yutar ve oturumu ancak o zaman kurulmuş sayar
/// (`isEstablished`). Yanıtlayan o ana dek veri göndermez. XXpsk3'te el sıkışmayı başlatan bitirir; yanıtlayan
/// hemen kurulmuş sayılır.
///
/// Eşzamanlılık: bir okuyucu + birden çok yazıcı görevi güvenlidir (yazmalar FIFO kilitle sıralanır).
public final class SecureChannel {
    public let prefix: Prefix
    public let isInitiator: Bool
    public let remoteStaticKey: Data
    public let handshakeHash: Data
    public private(set) var isEstablished: Bool

    private let transport: MessageTransport
    private let noise: NoiseTransport
    private var pending: Data?
    private let sendGate = SendGate()

    private init(transport: MessageTransport, noise: NoiseTransport, prefix: Prefix, initiator: Bool,
                 remoteStatic: Data, established: Bool) {
        self.transport = transport
        self.noise = noise
        self.prefix = prefix
        self.isInitiator = initiator
        self.remoteStaticKey = remoteStatic
        self.handshakeHash = noise.handshakeHash
        self.isEstablished = established
    }

    /// 4 haneli doğrulama kodu (iki tarafta aynı olmalı); bkz. `VerificationCode`.
    public var verificationCode: String { VerificationCode.compute(handshakeHash: handshakeHash) }

    public var remoteDeviceId: Data { DeviceId.from(publicKey: remoteStaticKey) }

    /// Tek düz metnin en büyük boyutu: taşıma sınırı − 16 B etiket, en çok 65519.
    public var maxPlaintextSize: Int { min(transport.maxMessageSize - NoiseCrypto.tagLength, FrameCodec.maxPlaintext) }
    /// DATA ve kontrol gövdesinin en büyük boyutu (BLE'de 8192 − 16 − 1).
    public var maxFrameBodySize: Int { maxPlaintextSize - 1 }

    // MARK: Gönderme

    /// Kodlanmış çerçeve (`FrameCodec.encode(...)` çıktısı: tür || gövde).
    public func sendEncodedFrame(_ encoded: Data) async throws {
        guard isEstablished || isInitiator else { throw SessionError.notEstablished }
        try await sendPlaintext(encoded)
    }

    public func sendFrame(_ type: FrameType, body: Data) async throws {
        try await sendEncodedFrame(try FrameCodec.encode(type, body: body))
    }

    public func sendSignal(_ message: SignalMessage) async throws {
        guard isEstablished || isInitiator else { throw SessionError.notEstablished }
        try await sendPlaintext(try message.encode())
    }

    private func sendPlaintext(_ plaintext: Data) async throws {
        guard plaintext.count <= maxPlaintextSize else { throw ProtocolError.messageTooLarge(plaintext.count) }
        await sendGate.acquire()
        defer { sendGate.release() }
        try await transport.sendMessage(try noise.encrypt(plaintext))
    }

    // MARK: Alma

    /// Yanıtlayanda ilk geçerli taşıma mesajı gelene kadar bekler. Gelen gerçek veri bir sonraki okumaya saklanır.
    public func ensureEstablished() async throws {
        guard !isEstablished else { return }
        guard let plaintext = try await receivePlaintext(swallowEmpty: false) else { throw SessionError.connectionClosed }
        if !plaintext.isEmpty { pending = plaintext }
    }

    /// Sıradaki çerçeve veya sinyal; karşı taraf temiz kapattıysa nil.
    ///
    /// Çözülemeyen / bilinmeyen `t` içeren sinyal oturumu DÜŞÜRMEZ: o mesaj atlanır, sıradakine geçilir (DECISIONS 1,
    /// ileri uyumluluk). Çerçeve (§5) çözme hataları ve Noise hataları eskisi gibi hata verir.
    public func receiveMessage() async throws -> SecureMessage? {
        while true {
            let plaintext: Data
            if let p = pending {
                plaintext = p
                pending = nil
            } else {
                guard let p = try await receivePlaintext(swallowEmpty: true) else { return nil }
                plaintext = p
            }
            if let message = try Self.classify(plaintext) { return message }
        }
    }

    /// Yalnız çerçeve bekler; sinyal gelirse hata.
    public func receiveFrame() async throws -> Frame? {
        guard let m = try await receiveMessage() else { return nil }
        guard case .frame(let f) = m else { throw SessionError.protocolViolation("çerçeve beklenirken sinyal geldi") }
        return f
    }

    public func receiveSignal() async throws -> SignalMessage? {
        guard let m = try await receiveMessage() else { return nil }
        guard case .signal(let s) = m else { throw SessionError.protocolViolation("sinyal beklenirken çerçeve geldi") }
        return s
    }

    private func receivePlaintext(swallowEmpty: Bool) async throws -> Data? {
        while true {
            guard let cipher = try await transport.receiveMessage(handshakePhase: false) else { return nil }
            let plaintext = try noise.decrypt(cipher)   // etiket hatası -> NoiseError.decryptionFailed (oturum düşer)
            isEstablished = true                         // ilk geçerli taşıma mesajı
            if plaintext.isEmpty && swallowEmpty { continue }
            return plaintext
        }
    }

    /// nil = çözülemeyen sinyal (atlanır).
    private static func classify(_ plaintext: Data) throws -> SecureMessage? {
        guard let first = plaintext.first else { throw SessionError.protocolViolation("boş düz metin") }
        if first == 0x7B {   // '{'
            do {
                return .signal(try SignalMessage.decode(plaintext))
            } catch let error as ProtocolError {
                IgnoredSignals.shared.record("\(error)")
                return nil
            }
        }
        return .frame(try FrameCodec.decode(plaintext))
    }

    // MARK: El sıkışma: başlatan (iPhone)

    /// Ön ek + IK. `remoteStaticKey` eşleştirmede saklanan PC açık anahtarıdır. İlk mesaj yükü boştur (0-RTT yok);
    /// el sıkışmadan sonra boş onay mesajı gönderilir.
    public static func connectIK(
        transport: MessageTransport, prefix: Prefix, localKey: NoisePrivateKey, remoteStaticKey: Data
    ) async throws -> SecureChannel {
        try checkKind(transport, prefix)
        let hs = try HandshakeState(
            pattern: .ik, role: .initiator, prologue: prefix.bytes,
            staticKey: localKey, remoteStaticKey: remoteStaticKey)
        try await transport.sendRaw(prefix.bytes)
        try await transport.sendMessage(try hs.writeMessage())
        let reply = try await receiveHandshakeReply(transport)
        _ = try hs.readMessage(reply)
        let channel = try makeChannel(transport, prefix, hs, initiator: true, established: true)
        try await channel.sendPlaintext(Data())   // yanıtlayanın oturumu kurulmuş saymasını sağlayan boş mesaj
        return channel
    }

    /// Eşleştirme: ön ek + XXpsk3. PC'nin statik anahtarı QR'daki `k` ile aynı değilse 3. mesaj gönderilmeden
    /// `SessionError.peerKeyMismatch` fırlatılır.
    public static func connectPairing(
        transport: MessageTransport, prefix: Prefix, localKey: NoisePrivateKey, psk: Data, expectedRemoteKey: Data
    ) async throws -> SecureChannel {
        try checkKind(transport, prefix)
        let hs = try HandshakeState(
            pattern: .xxpsk3, role: .initiator, prologue: prefix.bytes,
            staticKey: localKey, preSharedKeys: [psk])
        try await transport.sendRaw(prefix.bytes)
        try await transport.sendMessage(try hs.writeMessage())
        let reply = try await receiveHandshakeReply(transport)
        _ = try hs.readMessage(reply)
        guard hs.remoteStaticKey == expectedRemoteKey else { throw SessionError.peerKeyMismatch }
        try await transport.sendMessage(try hs.writeMessage())
        return try makeChannel(transport, prefix, hs, initiator: true, established: true)
    }

    /// Eşleştirmesiz bağlantı (PROTOCOL §2b): ön ek + `Noise_XX` (psk yok), bilinmeyen PC ile. Dönen kanalın
    /// `remoteStaticKey` değeri el sıkışmada öğrenilen PC anahtarıdır (çağıran saklar/karşılaştırır);
    /// `verificationCode` iki ekranda gösterilir. `expectedRemoteStatic` verilmişse (BLE oturumunda öğrenilen PC
    /// anahtarı) 2. mesajdan sonra karşılaştırılır; uyuşmazsa 3. mesaj (kendi statik anahtarımız) gönderilmeden
    /// `SessionError.peerKeyMismatch` fırlatılır.
    public static func connectXX(
        transport: MessageTransport, prefix: Prefix, localKey: NoisePrivateKey, expectedRemoteStatic: Data? = nil
    ) async throws -> SecureChannel {
        try checkKind(transport, prefix)
        let hs = try HandshakeState(pattern: .xx, role: .initiator, prologue: prefix.bytes, staticKey: localKey)
        try await transport.sendRaw(prefix.bytes)
        try await transport.sendMessage(try hs.writeMessage())
        let reply = try await receiveHandshakeReply(transport)
        _ = try hs.readMessage(reply)
        if let expectedRemoteStatic, hs.remoteStaticKey != expectedRemoteStatic { throw SessionError.peerKeyMismatch }
        try await transport.sendMessage(try hs.writeMessage())
        return try makeChannel(transport, prefix, hs, initiator: true, established: true)
    }

    // MARK: El sıkışma: yanıtlayan (PC; testlerde)

    /// IK bekler. Sürüm uyuşmazsa `0xFF || sürüm` yazıp `versionMismatch` fırlatır. Başlatanın statik anahtarı
    /// `isTrusted` ile reddedilirse yanıt vermeden `peerNotTrusted` fırlatır.
    public static func acceptIK(
        transport: MessageTransport, localKey: NoisePrivateKey,
        supportedVersion: UInt8 = Prefix.currentVersion, isTrusted: (Data) -> Bool
    ) async throws -> SecureChannel {
        let prefix = try await receivePrefix(transport, supportedVersion: supportedVersion)
        let hs = try HandshakeState(pattern: .ik, role: .responder, prologue: prefix.bytes, staticKey: localKey)
        guard let m1 = try await transport.receiveMessage(handshakePhase: false) else { throw SessionError.connectionClosed }
        let payload = try hs.readMessage(m1)
        guard payload.isEmpty else { throw SessionError.protocolViolation("IK ilk mesajı boş yük taşımalı (0-RTT yok)") }
        let peer = hs.remoteStaticKey!
        guard isTrusted(peer) else { throw SessionError.peerNotTrusted(publicKey: peer) }
        try await transport.sendMessage(try hs.writeMessage())
        return try makeChannel(transport, prefix, hs, initiator: false, established: false)
    }

    /// XX bekler (eşleştirmesiz bağlantı). Başlatanın statik anahtarı `remoteStaticKey` ile döner; kabul/ret çağıranın işidir.
    public static func acceptXX(
        transport: MessageTransport, localKey: NoisePrivateKey, supportedVersion: UInt8 = Prefix.currentVersion
    ) async throws -> SecureChannel {
        let prefix = try await receivePrefix(transport, supportedVersion: supportedVersion)
        let hs = try HandshakeState(pattern: .xx, role: .responder, prologue: prefix.bytes, staticKey: localKey)
        guard let m1 = try await transport.receiveMessage(handshakePhase: false) else { throw SessionError.connectionClosed }
        _ = try hs.readMessage(m1)
        try await transport.sendMessage(try hs.writeMessage())
        guard let m3 = try await transport.receiveMessage(handshakePhase: false) else { throw SessionError.connectionClosed }
        _ = try hs.readMessage(m3)
        return try makeChannel(transport, prefix, hs, initiator: false, established: true)
    }

    /// XXpsk3 bekler (eşleştirme modu). Dönen kanalın `remoteStaticKey` değerini kullanıcı onayına sunmak çağıranın işidir.
    public static func acceptPairing(
        transport: MessageTransport, localKey: NoisePrivateKey, psk: Data,
        supportedVersion: UInt8 = Prefix.currentVersion
    ) async throws -> SecureChannel {
        let prefix = try await receivePrefix(transport, supportedVersion: supportedVersion)
        let hs = try HandshakeState(
            pattern: .xxpsk3, role: .responder, prologue: prefix.bytes, staticKey: localKey, preSharedKeys: [psk])
        guard let m1 = try await transport.receiveMessage(handshakePhase: false) else { throw SessionError.connectionClosed }
        _ = try hs.readMessage(m1)
        try await transport.sendMessage(try hs.writeMessage())
        guard let m3 = try await transport.receiveMessage(handshakePhase: false) else { throw SessionError.connectionClosed }
        _ = try hs.readMessage(m3)
        return try makeChannel(transport, prefix, hs, initiator: false, established: true)
    }

    // MARK: İç

    private static func checkKind(_ transport: MessageTransport, _ prefix: Prefix) throws {
        guard transport.kind == prefix.transport else { throw SessionError.transportMismatch }
    }

    private static func receiveHandshakeReply(_ transport: MessageTransport) async throws -> Data {
        guard let reply = try await transport.receiveMessage(handshakePhase: true) else { throw SessionError.connectionClosed }
        return reply
    }

    private static func receivePrefix(_ transport: MessageTransport, supportedVersion: UInt8) async throws -> Prefix {
        let raw = try await transport.receiveRaw(count: Prefix.length)
        let prefix = try Prefix.decode(raw)
        guard prefix.transport == transport.kind else { throw SessionError.transportMismatch }
        guard prefix.isVersionSupported(supportedVersion) else {
            try await transport.sendRaw(Prefix.versionMismatchResponse(supportedVersion: supportedVersion))
            throw SessionError.versionMismatch(supportedVersion: supportedVersion)
        }
        return prefix
    }

    private static func makeChannel(
        _ transport: MessageTransport, _ prefix: Prefix, _ hs: HandshakeState, initiator: Bool, established: Bool
    ) throws -> SecureChannel {
        guard let remote = hs.remoteStaticKey else { throw SessionError.protocolViolation("karşı statik anahtar yok") }
        let noise = try hs.finish()
        return SecureChannel(transport: transport, noise: noise, prefix: prefix, initiator: initiator,
                             remoteStatic: remote, established: established)
    }
}
