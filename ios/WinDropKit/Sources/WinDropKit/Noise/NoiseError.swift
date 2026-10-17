import Foundation

/// Noise katmanı hataları.
public enum NoiseError: Error, Equatable {
    case invalidProtocolName(String)
    case unsupportedPattern(String)
    case invalidKeyLength
    /// Desen bu anahtarı gerektiriyor ama verilmedi (`s`, `rs`, ...).
    case missingKey(String)
    case wrongPreSharedKeyCount(expected: Int, got: Int)
    case invalidPreSharedKeyLength
    case messageTooLong(Int)
    case messageTooShort
    case decryptionFailed
    case nonceExhausted
    case dhFailed
    case cryptoFailure
    /// Sıra bu tarafta değil (yazma/okuma yönü yanlış).
    case notYourTurn
    case handshakeComplete
    case handshakeNotComplete
    /// Önceki bir hatadan sonra el sıkışma durumu kullanılamaz.
    case handshakeFailed
    /// Aynı anahtar türü iki kez alındı/üretildi (ör. iki `e` jetonu).
    case unexpectedToken(String)
}
