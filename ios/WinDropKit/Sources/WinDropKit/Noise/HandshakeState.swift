import Foundation

/// El sıkışmadaki rol.
public enum NoiseRole {
    case initiator
    case responder
}

/// Noise HandshakeState (rev 34 §5.3) — jeton listeleriyle çalışan genel desen motoru.
///
/// Kullanım: sıra bu taraftaysa `writeMessage`, değilse `readMessage`; `isComplete` olunca `finish()`.
/// Herhangi bir hatadan sonra durum kullanılamaz (`NoiseError.handshakeFailed`).
public final class HandshakeState {
    public let pattern: HandshakePattern
    public let role: NoiseRole

    private var symmetric: SymmetricState
    private let s: NoisePrivateKey?
    private var e: NoisePrivateKey?
    private var rs: NoisePublicKey?
    private var re: NoisePublicKey?
    private let psks: [Data]
    private var nextPsk = 0
    private var messageIndex = 0
    private var failed = false
    /// Test için enjekte edilen sabit ephemeral; ilk `e` jetonunda kullanılır.
    private var injectedEphemeral: NoisePrivateKey?

    /// - Parameters:
    ///   - staticKey: yerel statik anahtar (`s`); desen gerektiriyorsa zorunlu.
    ///   - remoteStaticKey: karşı tarafın statik açık anahtarı (32 B) — yalnız desen ön mesajında
    ///     (`<- s` / `-> s`) biliniyorsa verilir.
    ///   - preSharedKeys: `psk` jetonları sırasıyla 32 baytlık anahtarlar.
    public convenience init(
        pattern: HandshakePattern,
        role: NoiseRole,
        prologue: Data = Data(),
        staticKey: NoisePrivateKey? = nil,
        remoteStaticKey: Data? = nil,
        preSharedKeys: [Data] = []
    ) throws {
        try self.init(
            pattern: pattern, role: role, prologue: prologue, staticKey: staticKey,
            remoteStaticKey: remoteStaticKey, preSharedKeys: preSharedKeys, ephemeralKey: nil)
    }

    /// `ephemeralKey` yalnız testler içindir (cacophony vektörleri sabit ephemeral kullanır).
    internal init(
        pattern: HandshakePattern,
        role: NoiseRole,
        prologue: Data,
        staticKey: NoisePrivateKey?,
        remoteStaticKey: Data?,
        preSharedKeys: [Data],
        ephemeralKey: NoisePrivateKey?
    ) throws {
        guard preSharedKeys.count == pattern.pskCount else {
            throw NoiseError.wrongPreSharedKeyCount(expected: pattern.pskCount, got: preSharedKeys.count)
        }
        guard preSharedKeys.allSatisfy({ $0.count == 32 }) else { throw NoiseError.invalidPreSharedKeyLength }

        self.pattern = pattern
        self.role = role
        self.s = staticKey
        self.injectedEphemeral = ephemeralKey
        self.psks = preSharedKeys
        if let remoteStaticKey {
            self.rs = try NoiseCrypto.publicKey(from: remoteStaticKey)
        }
        var sym = SymmetricState(protocolName: pattern.protocolName)
        sym.mixHash(prologue)

        // Ön mesajlar: önce başlatanınki, sonra yanıtlayanınki.
        for (tokens, fromInitiator) in [(pattern.initiatorPreMessage, true), (pattern.responderPreMessage, false)] {
            for token in tokens {
                guard token == .s else { throw NoiseError.unsupportedPattern(pattern.name) }
                let isLocal = (fromInitiator == (role == .initiator))
                if isLocal {
                    guard let key = staticKey else { throw NoiseError.missingKey("s") }
                    sym.mixHash(key.publicKey.rawRepresentation)
                } else {
                    guard let key = self.rs else { throw NoiseError.missingKey("rs") }
                    sym.mixHash(key.rawRepresentation)
                }
            }
        }
        self.symmetric = sym
    }

    // MARK: Durum

    public var isComplete: Bool { messageIndex >= pattern.messageCount }

    /// Sıradaki mesajı bu taraf mı yazmalı?
    public var shouldWrite: Bool {
        !isComplete && ((messageIndex % 2 == 0) == (role == .initiator))
    }

    /// Şu ana kadar öğrenilen/verilen karşı statik anahtar (32 B).
    public var remoteStaticKey: Data? { rs?.rawRepresentation }

    public var handshakeHash: Data { symmetric.handshakeHash }

    // MARK: Mesajlar

    public func writeMessage(payload: Data = Data()) throws -> Data {
        guard !failed else { throw NoiseError.handshakeFailed }
        guard !isComplete else { throw NoiseError.handshakeComplete }
        guard shouldWrite else { throw NoiseError.notYourTurn }
        do {
            var out = Data()
            for token in pattern.messages[messageIndex] {
                switch token {
                case .e:
                    guard e == nil else { throw NoiseError.unexpectedToken("e") }
                    let key = injectedEphemeral ?? NoisePrivateKey()
                    injectedEphemeral = nil
                    e = key
                    let pub = key.publicKey.rawRepresentation
                    out.append(pub)
                    symmetric.mixHash(pub)
                    if pattern.usesPsk { symmetric.mixKey(pub) }
                case .s:
                    guard let s else { throw NoiseError.missingKey("s") }
                    out.append(try symmetric.encryptAndHash(s.publicKey.rawRepresentation))
                case .psk:
                    try mixPsk()
                case .ee, .es, .se, .ss:
                    symmetric.mixKey(try dh(token))
                }
            }
            out.append(try symmetric.encryptAndHash(payload))
            guard out.count <= NoiseCrypto.maxMessageSize else { throw NoiseError.messageTooLong(out.count) }
            messageIndex += 1
            return out
        } catch {
            failed = true
            throw error
        }
    }

    public func readMessage(_ message: Data) throws -> Data {
        guard !failed else { throw NoiseError.handshakeFailed }
        guard !isComplete else { throw NoiseError.handshakeComplete }
        guard !shouldWrite else { throw NoiseError.notYourTurn }
        do {
            guard message.count <= NoiseCrypto.maxMessageSize else { throw NoiseError.messageTooLong(message.count) }
            let bytes = [UInt8](message)
            var pos = 0
            func take(_ n: Int) throws -> Data {
                guard bytes.count - pos >= n else { throw NoiseError.messageTooShort }
                defer { pos += n }
                return Data(bytes[pos..<(pos + n)])
            }
            for token in pattern.messages[messageIndex] {
                switch token {
                case .e:
                    guard re == nil else { throw NoiseError.unexpectedToken("e") }
                    let raw = try take(NoiseCrypto.dhLength)
                    re = try NoiseCrypto.publicKey(from: raw)
                    symmetric.mixHash(raw)
                    if pattern.usesPsk { symmetric.mixKey(raw) }
                case .s:
                    guard rs == nil else { throw NoiseError.unexpectedToken("s") }
                    let len = NoiseCrypto.dhLength + (symmetric.hasKey ? NoiseCrypto.tagLength : 0)
                    let temp = try take(len)
                    let raw = try symmetric.decryptAndHash(temp)
                    rs = try NoiseCrypto.publicKey(from: raw)
                case .psk:
                    try mixPsk()
                case .ee, .es, .se, .ss:
                    symmetric.mixKey(try dh(token))
                }
            }
            let rest = try take(bytes.count - pos)
            let payload = try symmetric.decryptAndHash(rest)
            messageIndex += 1
            return payload
        } catch {
            failed = true
            throw error
        }
    }

    /// El sıkışma bitince Split uygular ve taşıma şifrelerini verir.
    public func finish() throws -> NoiseTransport {
        guard !failed else { throw NoiseError.handshakeFailed }
        guard isComplete else { throw NoiseError.handshakeNotComplete }
        let (c1, c2) = symmetric.split()
        return NoiseTransport(
            send: role == .initiator ? c1 : c2,
            receive: role == .initiator ? c2 : c1,
            handshakeHash: symmetric.handshakeHash,
            remoteStaticKey: rs?.rawRepresentation,
            isOneWay: pattern.isOneWay)
    }

    // MARK: İç

    private func mixPsk() throws {
        guard nextPsk < psks.count else { throw NoiseError.wrongPreSharedKeyCount(expected: pattern.pskCount, got: psks.count) }
        symmetric.mixKeyAndHash(psks[nextPsk])
        nextPsk += 1
    }

    private func dh(_ token: HandshakePattern.Token) throws -> Data {
        let isInitiator = role == .initiator
        switch token {
        case .ee:
            guard let e else { throw NoiseError.missingKey("e") }
            guard let re else { throw NoiseError.missingKey("re") }
            return try NoiseCrypto.dh(e, re)
        case .es:
            // başlatan: DH(e, rs); yanıtlayan: DH(s, re)
            if isInitiator {
                guard let e else { throw NoiseError.missingKey("e") }
                guard let rs else { throw NoiseError.missingKey("rs") }
                return try NoiseCrypto.dh(e, rs)
            } else {
                guard let s else { throw NoiseError.missingKey("s") }
                guard let re else { throw NoiseError.missingKey("re") }
                return try NoiseCrypto.dh(s, re)
            }
        case .se:
            // başlatan: DH(s, re); yanıtlayan: DH(e, rs)
            if isInitiator {
                guard let s else { throw NoiseError.missingKey("s") }
                guard let re else { throw NoiseError.missingKey("re") }
                return try NoiseCrypto.dh(s, re)
            } else {
                guard let e else { throw NoiseError.missingKey("e") }
                guard let rs else { throw NoiseError.missingKey("rs") }
                return try NoiseCrypto.dh(e, rs)
            }
        case .ss:
            guard let s else { throw NoiseError.missingKey("s") }
            guard let rs else { throw NoiseError.missingKey("rs") }
            return try NoiseCrypto.dh(s, rs)
        default:
            throw NoiseError.unexpectedToken(token.rawValue)
        }
    }
}

/// El sıkışma sonrası taşıma şifreleri. Her yön ayrı sayaçlı CipherState kullanır.
public final class NoiseTransport {
    private var sendState: CipherState
    private var receiveState: CipherState
    /// Kanal bağlama için el sıkışma karması (SHA-256, 32 B).
    public let handshakeHash: Data
    /// El sıkışmada öğrenilen karşı statik anahtar (varsa).
    public let remoteStaticKey: Data?
    /// Tek yönlü desen (N, K, X): yanıtlayan gönderemez/başlatan alamaz.
    public let isOneWay: Bool

    init(send: CipherState, receive: CipherState, handshakeHash: Data, remoteStaticKey: Data?, isOneWay: Bool) {
        self.sendState = send
        self.receiveState = receive
        self.handshakeHash = handshakeHash
        self.remoteStaticKey = remoteStaticKey
        self.isOneWay = isOneWay
    }

    /// Şifreler; çıktı = şifreli metin || 16 B etiket (en çok 65535 B).
    public func encrypt(_ plaintext: Data, ad: Data = Data()) throws -> Data {
        try sendState.encryptWithAd(ad, plaintext)
    }

    public func decrypt(_ ciphertext: Data, ad: Data = Data()) throws -> Data {
        guard ciphertext.count <= NoiseCrypto.maxMessageSize else { throw NoiseError.messageTooLong(ciphertext.count) }
        return try receiveState.decryptWithAd(ad, ciphertext)
    }

    var sendNonce: UInt64 { sendState.nonce }
    var receiveNonce: UInt64 { receiveState.nonce }
}
