#if canImport(CryptoKit)
import CryptoKit
#else
import Crypto
#endif
import Foundation

/// Genel anahtar türleri (Apple'da CryptoKit, Linux'ta swift-crypto; aynı API).
public typealias NoisePrivateKey = Curve25519.KeyAgreement.PrivateKey
public typealias NoisePublicKey = Curve25519.KeyAgreement.PublicKey

/// Noise rev 34 ilkel işlemleri: `25519_ChaChaPoly_SHA256`.
enum NoiseCrypto {
    static let hashLength = 32      // HASHLEN (SHA-256)
    static let dhLength = 32        // DHLEN (X25519)
    static let tagLength = 16       // ChaChaPoly etiketi
    static let keyLength = 32
    static let maxMessageSize = 65535

    static func hash(_ data: Data) -> Data {
        Data(SHA256.hash(data: data))
    }

    static func hmac(key: Data, data: Data) -> Data {
        let mac = HMAC<SHA256>.authenticationCode(for: data, using: SymmetricKey(data: key))
        return Data(mac)
    }

    /// Noise HKDF (§4.3): HMAC tabanlı, 2 veya 3 çıktı.
    static func hkdf(chainingKey: Data, inputKeyMaterial: Data, outputs: Int) -> [Data] {
        precondition(outputs == 2 || outputs == 3)
        let tempKey = hmac(key: chainingKey, data: inputKeyMaterial)
        let output1 = hmac(key: tempKey, data: Data([0x01]))
        var output2Input = output1
        output2Input.append(0x02)
        let output2 = hmac(key: tempKey, data: output2Input)
        if outputs == 2 { return [output1, output2] }
        var output3Input = output2
        output3Input.append(0x03)
        let output3 = hmac(key: tempKey, data: output3Input)
        return [output1, output2, output3]
    }

    /// Noise nonce'u: 4 sıfır bayt || 64 bit küçük-endian sayaç.
    static func nonceBytes(_ n: UInt64) -> Data {
        var d = Data(count: 12)
        var v = n
        for i in 0..<8 {
            d[4 + i] = UInt8(truncatingIfNeeded: v)
            v >>= 8
        }
        return d
    }

    static func encrypt(key: SymmetricKey, nonce: UInt64, ad: Data, plaintext: Data) throws -> Data {
        do {
            let n = try ChaChaPoly.Nonce(data: nonceBytes(nonce))
            let box = try ChaChaPoly.seal(plaintext, using: key, nonce: n, authenticating: ad)
            var out = Data(box.ciphertext)
            out.append(contentsOf: box.tag)
            return out
        } catch {
            throw NoiseError.cryptoFailure
        }
    }

    static func decrypt(key: SymmetricKey, nonce: UInt64, ad: Data, ciphertext: Data) throws -> Data {
        guard ciphertext.count >= tagLength else { throw NoiseError.messageTooShort }
        do {
            let n = try ChaChaPoly.Nonce(data: nonceBytes(nonce))
            let bytes = [UInt8](ciphertext)
            let ctEnd = bytes.count - tagLength
            let box = try ChaChaPoly.SealedBox(
                nonce: n,
                ciphertext: Data(bytes[0..<ctEnd]),
                tag: Data(bytes[ctEnd...]))
            return Data(try ChaChaPoly.open(box, using: key, authenticating: ad))
        } catch {
            throw NoiseError.decryptionFailed
        }
    }

    /// X25519. Tümü-sıfır çıktı (düşük dereceli nokta) hatadır.
    static func dh(_ privateKey: NoisePrivateKey, _ publicKey: NoisePublicKey) throws -> Data {
        let bytes: Data
        do {
            let secret = try privateKey.sharedSecretFromKeyAgreement(with: publicKey)
            bytes = secret.withUnsafeBytes { Data($0) }
        } catch {
            throw NoiseError.dhFailed
        }
        if bytes.allSatisfy({ $0 == 0 }) { throw NoiseError.dhFailed }
        return bytes
    }

    static func publicKey(from raw: Data) throws -> NoisePublicKey {
        guard raw.count == dhLength else { throw NoiseError.invalidKeyLength }
        do { return try NoisePublicKey(rawRepresentation: raw) } catch { throw NoiseError.invalidKeyLength }
    }
}
