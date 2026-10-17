import Foundation

// PROTOCOL §4 sinyal mesajları (BLE oturumu içinde): UTF-8 JSON `{"t": "<tür>", ...}`.
// Bilinmeyen alanlar yok sayılır; bilinmeyen `t` hatadır.
//
// Alan tipleri PROTOCOL §9a: `transferId` = 16 B base64url dolgusuz (22 karakter, bkz. `TransferId`; decode'da
// katı doğrulanır); `wifi` dize (`none`/`connected`); `caps` [String]; `count`/`v`/`port` Int; `total` Int64; `btAudio` Bool.

public struct HelloMessage: Codable, Equatable {
    public var v: Int
    public var name: String
    public var caps: [String]
    public var wifi: String?        // iPhone: "none" / "connected"
    public var btAudio: Bool
    public init(v: Int, name: String, caps: [String], wifi: String? = nil, btAudio: Bool) {
        self.v = v; self.name = name; self.caps = caps; self.wifi = wifi; self.btAudio = btAudio
    }
}

public struct StatusMessage: Codable, Equatable {
    public var wifi: String
    public var btAudio: Bool
    public init(wifi: String, btAudio: Bool) { self.wifi = wifi; self.btAudio = btAudio }
}

public struct OfferMessage: Codable, Equatable {
    public var transferId: String
    public var count: Int
    public var total: Int64
    /// En çok 5 dosya adı.
    public var preview: [String]
    public init(transferId: String, count: Int, total: Int64, preview: [String]) {
        self.transferId = transferId; self.count = count; self.total = total; self.preview = preview
    }
}

/// `accept` ve `reject` gövdesi.
public struct ReplyMessage: Codable, Equatable {
    public var transferId: String
    public var reason: String?
    public init(transferId: String, reason: String? = nil) { self.transferId = transferId; self.reason = reason }
}

public struct AddressCandidate: Codable, Equatable {
    public var ip: String
    public var port: Int
    public init(ip: String, port: Int) { self.ip = ip; self.port = port }
}

public struct AddrMessage: Codable, Equatable {
    public var transferId: String
    public var candidates: [AddressCandidate]
    public init(transferId: String, candidates: [AddressCandidate]) {
        self.transferId = transferId; self.candidates = candidates
    }
}

public struct HotspotMessage: Codable, Equatable {
    public var transferId: String
    public var ip: String
    public var port: Int
    public var ssid: String?
    public var pass: String?
    public init(transferId: String, ip: String, port: Int, ssid: String? = nil, pass: String? = nil) {
        self.transferId = transferId; self.ip = ip; self.port = port; self.ssid = ssid; self.pass = pass
    }
}

/// `pairInfo` (PC→iPhone, yalnız eşleştirme kanalında; PROTOCOL §2.7, §4): etkin nokta SSID/parolası.
public struct PairInfo: Codable, Equatable {
    public var ssid: String
    public var pass: String
    public init(ssid: String, pass: String) { self.ssid = ssid; self.pass = pass }
}

public struct RelayMessage: Codable, Equatable {
    public var transferId: String
    public var token: String
    public init(transferId: String, token: String) { self.transferId = transferId; self.token = token }
}

public enum PathResult: String, Codable, Equatable {
    case tcpOk = "tcp_ok"
    case tcpFail = "tcp_fail"
    case hotspotOk = "hotspot_ok"
    case hotspotFail = "hotspot_fail"
    case relayOk = "relay_ok"
    case relayFail = "relay_fail"
}

public struct PathMessage: Codable, Equatable {
    public var transferId: String
    public var result: PathResult
    public init(transferId: String, result: PathResult) { self.transferId = transferId; self.result = result }
}

public struct SignalCancelMessage: Codable, Equatable {
    public var transferId: String
    public init(transferId: String) { self.transferId = transferId }
}

/// `t` alanına göre ayrışan sinyal mesajı.
public enum SignalMessage: Equatable {
    case hello(HelloMessage)
    case status(StatusMessage)
    case offer(OfferMessage)
    case accept(ReplyMessage)
    case reject(ReplyMessage)
    case addr(AddrMessage)
    case hotspot(HotspotMessage)
    case pairInfo(PairInfo)
    case relay(RelayMessage)
    case path(PathMessage)
    case cancel(SignalCancelMessage)

    public var type: String {
        switch self {
        case .hello: return "hello"
        case .status: return "status"
        case .offer: return "offer"
        case .accept: return "accept"
        case .reject: return "reject"
        case .addr: return "addr"
        case .hotspot: return "hotspot"
        case .pairInfo: return "pairInfo"
        case .relay: return "relay"
        case .path: return "path"
        case .cancel: return "cancel"
        }
    }

    /// `{"t": ..., ...}` UTF-8 JSON (anahtarlar sıralı).
    public func encode() throws -> Data {
        let enc = JSONEncoder()
        enc.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let body: Data
        switch self {
        case .hello(let m): body = try enc.encode(m)
        case .status(let m): body = try enc.encode(m)
        case .offer(let m): body = try enc.encode(m)
        case .accept(let m), .reject(let m): body = try enc.encode(m)
        case .addr(let m): body = try enc.encode(m)
        case .hotspot(let m): body = try enc.encode(m)
        case .pairInfo(let m): body = try enc.encode(m)
        case .relay(let m): body = try enc.encode(m)
        case .path(let m): body = try enc.encode(m)
        case .cancel(let m): body = try enc.encode(m)
        }
        guard var object = try JSONSerialization.jsonObject(with: body) as? [String: Any] else {
            throw ProtocolError.invalidMessage("gövde nesne değil")
        }
        object["t"] = type
        return try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes])
    }

    /// Bilinmeyen `t` veya eksik/hatalı alan → `ProtocolError.invalidMessage`.
    public static func decode(_ data: Data) throws -> SignalMessage {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let t = object["t"] as? String else {
            throw ProtocolError.invalidMessage("'t' alanı yok veya JSON nesnesi değil")
        }
        let dec = JSONDecoder()
        do {
            let message: SignalMessage
            switch t {
            case "hello": message = .hello(try dec.decode(HelloMessage.self, from: data))
            case "status": message = .status(try dec.decode(StatusMessage.self, from: data))
            case "offer": message = .offer(try dec.decode(OfferMessage.self, from: data))
            case "accept": message = .accept(try dec.decode(ReplyMessage.self, from: data))
            case "reject": message = .reject(try dec.decode(ReplyMessage.self, from: data))
            case "addr": message = .addr(try dec.decode(AddrMessage.self, from: data))
            case "hotspot": message = .hotspot(try dec.decode(HotspotMessage.self, from: data))
            case "pairInfo": message = .pairInfo(try dec.decode(PairInfo.self, from: data))
            case "relay": message = .relay(try dec.decode(RelayMessage.self, from: data))
            case "path": message = .path(try dec.decode(PathMessage.self, from: data))
            case "cancel": message = .cancel(try dec.decode(SignalCancelMessage.self, from: data))
            default: throw ProtocolError.invalidMessage("bilinmeyen t: \(t)")
            }
            if let id = message.transferId, TransferId.parse(id) == nil {
                throw ProtocolError.invalidMessage("transferId 16 B base64url (22 karakter) olmalı")
            }
            return message
        } catch let e as ProtocolError {
            throw e
        } catch {
            throw ProtocolError.invalidMessage("'\(t)' mesajı geçersiz")
        }
    }

    /// Mesajdaki `transferId` (hello/status'ta yok).
    public var transferId: String? {
        switch self {
        case .hello, .status, .pairInfo: return nil
        case .offer(let m): return m.transferId
        case .accept(let m), .reject(let m): return m.transferId
        case .addr(let m): return m.transferId
        case .hotspot(let m): return m.transferId
        case .relay(let m): return m.transferId
        case .path(let m): return m.transferId
        case .cancel(let m): return m.transferId
        }
    }
}
