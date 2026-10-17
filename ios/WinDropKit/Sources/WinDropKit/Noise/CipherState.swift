import Foundation
#if canImport(CryptoKit)
import CryptoKit
#else
import Crypto
#endif

/// Noise CipherState (rev 34 §5.1).
public struct CipherState {
    private var key: SymmetricKey?
    /// Sonraki nonce sayacı.
    public private(set) var nonce: UInt64 = 0

    /// Rezerve son nonce (2^64-1); kullanılırsa hata.
    static let maxNonce = UInt64.max

    public init() {}

    init(key: Data) {
        self.key = SymmetricKey(data: key)
    }

    public var hasKey: Bool { key != nil }

    mutating func initializeKey(_ k: Data?) {
        key = k.map { SymmetricKey(data: $0) }
        nonce = 0
    }

    /// Yalnız test için: sayacı doğrudan ayarlar.
    mutating func setNonce(_ n: UInt64) { nonce = n }

    public mutating func encryptWithAd(_ ad: Data, _ plaintext: Data) throws -> Data {
        guard let key else { return plaintext }
        guard nonce != CipherState.maxNonce else { throw NoiseError.nonceExhausted }
        let total = plaintext.count + NoiseCrypto.tagLength
        if total > NoiseCrypto.maxMessageSize { throw NoiseError.messageTooLong(total) }
        let out = try NoiseCrypto.encrypt(key: key, nonce: nonce, ad: ad, plaintext: plaintext)
        nonce += 1
        return out
    }

    /// Başarısız çözmede sayaç ilerlemez.
    public mutating func decryptWithAd(_ ad: Data, _ ciphertext: Data) throws -> Data {
        guard let key else { return ciphertext }
        guard nonce != CipherState.maxNonce else { throw NoiseError.nonceExhausted }
        let out = try NoiseCrypto.decrypt(key: key, nonce: nonce, ad: ad, ciphertext: ciphertext)
        nonce += 1
        return out
    }

    /// Rekey (§4.2): ENCRYPT(k, maxnonce, zerolen, zeros)'un ilk 32 baytı.
    public mutating func rekey() throws {
        guard let key else { throw NoiseError.missingKey("k") }
        let out = try NoiseCrypto.encrypt(key: key, nonce: CipherState.maxNonce, ad: Data(), plaintext: Data(count: 32))
        self.key = SymmetricKey(data: out.prefix(32))
    }
}
