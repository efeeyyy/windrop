import Foundation

/// Veri kanalı çerçeve türü (PROTOCOL §6.2).
public enum FrameType: UInt8, Equatable {
    case manifest = 1
    case resume = 2
    case fileStart = 3
    case data = 4
    case fileEnd = 5
    case ack = 6
    case cancel = 7
    case done = 8
}

// MARK: Kontrol gövdeleri (JSON, camelCase, alanlar zorunlu; PROTOCOL §6.2)

public struct ManifestFile: Codable, Equatable {
    public var id: Int
    public var name: String
    /// Bayt.
    public var size: Int64
    /// Unix epoch milisaniye.
    public var mtime: Int64
    /// PROTOCOL §6.7: gönderilen kökten göreli klasör yolu (`/` ayırıcı, ≤ 16 seviye, ≤ 1024 karakter); nil ise yazılmaz.
    public var dir: String?
    public init(id: Int, name: String, size: Int64, mtime: Int64, dir: String? = nil) {
        self.id = id; self.name = name; self.size = size; self.mtime = mtime; self.dir = dir
    }
}

/// MANIFEST sayfası (PROTOCOL §6.3): `{"files":[...],"more":bool}`; `more` yoksa false. Yazarken yalnız true ise yazılır.
public struct ManifestBody: Codable, Equatable {
    public var files: [ManifestFile]
    public var more: Bool?
    public init(files: [ManifestFile], more: Bool? = nil) { self.files = files; self.more = more }
    public var hasMore: Bool { more ?? false }
}

public struct ResumeBody: Codable, Equatable {
    public var id: Int
    public var offset: Int64
    public init(id: Int, offset: Int64) { self.id = id; self.offset = offset }
}

public struct FileStartBody: Codable, Equatable {
    public var id: Int
    public var offset: Int64
    public init(id: Int, offset: Int64) { self.id = id; self.offset = offset }
}

public struct FileEndBody: Codable, Equatable {
    public var id: Int
    /// 64 karakter küçük harf hex.
    public var sha256: String
    public init(id: Int, sha256: String) { self.id = id; self.sha256 = sha256 }
}

public struct AckBody: Codable, Equatable {
    public var id: Int
    public var offset: Int64
    public init(id: Int, offset: Int64) { self.id = id; self.offset = offset }
}

/// `{}` veya `{"reason": "..."}`; nil alan yazılmaz.
public struct CancelBody: Codable, Equatable {
    public var reason: String?
    public init(reason: String? = nil) { self.reason = reason }
}

public struct DoneBody: Codable, Equatable {
    public var ok: Bool
    public var failed: [Int]
    public init(ok: Bool, failed: [Int]) { self.ok = ok; self.failed = failed }
}

/// Çözülmüş düz metin çerçeve.
public struct Frame: Equatable {
    public var type: FrameType
    public var body: Data
    public init(type: FrameType, body: Data) { self.type = type; self.body = body }
}

/// §6.1 düz metin çerçeve kodlaması: `tür (1 B) || gövde`, en çok 65519 B (Noise şifrelemesi +16 → 65535).
public enum FrameCodec {
    public static let maxPlaintext = 65519
    /// DATA ve kontrol gövdesi üst sınırı.
    public static let maxBody = maxPlaintext - 1   // 65518
    /// Uygulamaların kullandığı DATA parça boyutu.
    public static let recommendedDataChunk = 64000

    public static func encode(_ type: FrameType, body: Data) throws -> Data {
        guard body.count <= maxBody else { throw ProtocolError.messageTooLarge(body.count) }
        var d = Data([type.rawValue])
        d.append(body)
        return d
    }

    public static func encodeData(_ data: Data) throws -> Data { try encode(.data, body: data) }

    public static func encode(_ body: ManifestBody) throws -> Data { try encodeControl(.manifest, body) }
    public static func encode(_ body: ResumeBody) throws -> Data { try encodeControl(.resume, body) }
    public static func encode(_ body: FileStartBody) throws -> Data { try encodeControl(.fileStart, body) }
    public static func encode(_ body: FileEndBody) throws -> Data { try encodeControl(.fileEnd, body) }
    public static func encode(_ body: AckBody) throws -> Data { try encodeControl(.ack, body) }
    public static func encode(_ body: CancelBody) throws -> Data { try encodeControl(.cancel, body) }
    public static func encode(_ body: DoneBody) throws -> Data { try encodeControl(.done, body) }

    /// Düz metin çerçeveyi türüne ve gövdesine ayırır. Boş, bilinmeyen tür veya aşırı büyük → hata.
    public static func decode(_ plaintext: Data) throws -> Frame {
        guard !plaintext.isEmpty else { throw ProtocolError.invalidFrame("boş çerçeve") }
        guard plaintext.count <= maxPlaintext else { throw ProtocolError.messageTooLarge(plaintext.count) }
        let bytes = [UInt8](plaintext)
        guard let type = FrameType(rawValue: bytes[0]) else {
            throw ProtocolError.invalidFrame("bilinmeyen çerçeve türü \(bytes[0])")
        }
        return Frame(type: type, body: Data(bytes[1...]))
    }

    public static func decodeManifest(_ body: Data) throws -> ManifestBody { try decodeControl(body) }
    public static func decodeResume(_ body: Data) throws -> ResumeBody { try decodeControl(body) }
    public static func decodeFileStart(_ body: Data) throws -> FileStartBody { try decodeControl(body) }
    public static func decodeAck(_ body: Data) throws -> AckBody { try decodeControl(body) }
    public static func decodeCancel(_ body: Data) throws -> CancelBody { try decodeControl(body) }
    public static func decodeDone(_ body: Data) throws -> DoneBody { try decodeControl(body) }

    /// `sha256` alanı 64 karakter küçük harf hex değilse hata.
    public static func decodeFileEnd(_ body: Data) throws -> FileEndBody {
        let v: FileEndBody = try decodeControl(body)
        guard isLowercaseHex64(v.sha256) else { throw ProtocolError.invalidFrame("sha256 64 karakter küçük harf hex olmalı") }
        return v
    }

    static func isLowercaseHex64(_ s: String) -> Bool {
        let u = Array(s.utf8)
        return u.count == 64 && u.allSatisfy { ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x61 && $0 <= 0x66) }
    }

    private static func encodeControl<T: Encodable>(_ type: FrameType, _ body: T) throws -> Data {
        let enc = JSONEncoder()
        enc.outputFormatting = [.withoutEscapingSlashes]
        let json: Data
        do { json = try enc.encode(body) } catch { throw ProtocolError.invalidFrame("JSON kodlanamadı") }
        return try encode(type, body: json)
    }

    private static func decodeControl<T: Decodable>(_ body: Data) throws -> T {
        do { return try JSONDecoder().decode(T.self, from: body) }
        catch { throw ProtocolError.invalidFrame("kontrol gövdesi geçersiz JSON") }
    }
}

/// §6.1 TCP çerçevesi: `uzunluk (2 B big-endian) || Noise mesajı` (1..65535 bayt).
/// Hem el sıkışma hem şifreli taşıma mesajları bu biçimle gider; 20 baytlık ön ek çerçevesizdir.
public enum TcpFraming {
    public static let lengthPrefixSize = 2
    public static let maxNoiseMessage = 65535

    /// Uzunluk + mesaj tek yazmada gider. Sıfır uzunluklu mesaj geçersizdir.
    public static func writeMessage(_ stream: ByteStream, _ noiseMessage: Data) async throws {
        guard !noiseMessage.isEmpty else { throw ProtocolError.invalidFrame("boş Noise mesajı gönderilemez") }
        guard noiseMessage.count <= maxNoiseMessage else { throw ProtocolError.messageTooLarge(noiseMessage.count) }
        var buf = Data([UInt8(noiseMessage.count >> 8), UInt8(noiseMessage.count & 0xFF)])
        buf.append(noiseMessage)
        try await stream.write(buf)
    }

    /// Bir Noise mesajı okur. Akış çerçeve sınırında temiz bittiyse `nil`; çerçeve ortasında bittiyse
    /// veya uzunluk 0 ise `ProtocolError`.
    public static func readMessage(_ stream: ByteStream) async throws -> Data? {
        let header: Data
        do {
            header = try await stream.read(exactly: lengthPrefixSize)
        } catch ByteStreamError.endOfStream(let partial) {
            if partial.isEmpty { return nil }
            throw ProtocolError.streamEnded
        }
        let h = [UInt8](header)
        let length = (Int(h[0]) << 8) | Int(h[1])
        guard length > 0 else { throw ProtocolError.invalidFrame("sıfır uzunluklu çerçeve") }
        do {
            return try await stream.read(exactly: length)
        } catch ByteStreamError.endOfStream {
            throw ProtocolError.streamEnded
        }
    }
}
