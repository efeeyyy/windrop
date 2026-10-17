import Foundation

/// Protokol katmanı (ön ek, çerçeveleme, parçalama, JSON) hatası.
public enum ProtocolError: Error, Equatable {
    case invalidPrefix(String)
    case invalidFrame(String)
    case invalidMessage(String)
    case invalidFragment(String)
    case messageTooLarge(Int)
    case streamEnded
}

/// Soyut bayt akışı (TCP soketi, bellek içi kanal vb.).
public protocol ByteStream: AnyObject {
    /// Tam `count` bayt okur. Akış erken biterse `ByteStreamError.endOfStream(partial:)` fırlatır.
    func read(exactly count: Int) async throws -> Data
    func write(_ data: Data) async throws
}

public enum ByteStreamError: Error, Equatable {
    /// Akış `partial.count < istenen` bayttan sonra bitti.
    case endOfStream(partial: Data)
}
