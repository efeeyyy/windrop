import Foundation

/// Noise SymmetricState (rev 34 §5.2).
struct SymmetricState {
    private(set) var cipherState = CipherState()
    private(set) var chainingKey: Data
    private(set) var h: Data

    init(protocolName: String) {
        let name = Data(protocolName.utf8)
        if name.count <= NoiseCrypto.hashLength {
            var padded = name
            padded.append(Data(count: NoiseCrypto.hashLength - name.count))
            h = padded
        } else {
            h = NoiseCrypto.hash(name)
        }
        chainingKey = h
    }

    mutating func mixKey(_ inputKeyMaterial: Data) {
        let out = NoiseCrypto.hkdf(chainingKey: chainingKey, inputKeyMaterial: inputKeyMaterial, outputs: 2)
        chainingKey = out[0]
        cipherState.initializeKey(out[1])
    }

    mutating func mixHash(_ data: Data) {
        var buf = h
        buf.append(data)
        h = NoiseCrypto.hash(buf)
    }

    mutating func mixKeyAndHash(_ inputKeyMaterial: Data) {
        let out = NoiseCrypto.hkdf(chainingKey: chainingKey, inputKeyMaterial: inputKeyMaterial, outputs: 3)
        chainingKey = out[0]
        mixHash(out[1])
        cipherState.initializeKey(out[2])
    }

    var handshakeHash: Data { h }
    var hasKey: Bool { cipherState.hasKey }

    mutating func encryptAndHash(_ plaintext: Data) throws -> Data {
        let ct = try cipherState.encryptWithAd(h, plaintext)
        mixHash(ct)
        return ct
    }

    mutating func decryptAndHash(_ ciphertext: Data) throws -> Data {
        let pt = try cipherState.decryptWithAd(h, ciphertext)
        mixHash(ciphertext)
        return pt
    }

    /// Split: (c1, c2). c1 başlatanın gönderme, c2 alma şifresi.
    func split() -> (CipherState, CipherState) {
        let out = NoiseCrypto.hkdf(chainingKey: chainingKey, inputKeyMaterial: Data(), outputs: 2)
        return (CipherState(key: out[0]), CipherState(key: out[1]))
    }
}
