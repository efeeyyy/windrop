import Foundation

/// Noise el sıkışma deseni (rev 34 §7, Ek A): jeton listeleri.
///
/// Temel (N, K, X, NN, ..., IX) ve ertelenmiş (NK1, X1N, ..., I1X1) desenler ile `pskN` değiştiricileri
/// desteklenir. `fallback` değiştiricisi desteklenmez (WinDrop kullanmaz).
public struct HandshakePattern: Equatable {
    public enum Token: String, Equatable {
        case e, s, ee, es, se, ss, psk
    }

    /// Desen adı, ör. `IK`, `XXpsk3`.
    public let name: String
    /// Ön mesajlar (yalnız `s` kullanılır).
    let initiatorPreMessage: [Token]
    let responderPreMessage: [Token]
    /// Mesaj `i`yi `i` çift ise başlatan, tek ise yanıtlayan gönderir.
    let messages: [[Token]]

    /// Tam Noise protokol adı: `Noise_<desen>_25519_ChaChaPoly_SHA256`.
    public var protocolName: String { "Noise_\(name)_25519_ChaChaPoly_SHA256" }

    /// Yalnız başlatanın mesaj gönderdiği tek mesajlık desenler (N, K, X + psk).
    public var isOneWay: Bool { messages.count == 1 }

    /// Desendeki `psk` jetonu sayısı (= beklenen ön paylaşımlı anahtar sayısı).
    public var pskCount: Int { messages.reduce(0) { $0 + $1.filter { $0 == .psk }.count } }

    /// Desende herhangi bir `psk` jetonu varsa `e` jetonu ayrıca MixKey(e.public) yapar (rev 34).
    var usesPsk: Bool { pskCount > 0 }

    public var messageCount: Int { messages.count }

    // MARK: Ayrıştırma

    /// `IK`, `XXpsk3`, `NNpsk0+psk2` gibi bir desen adını çözer.
    public init(name: String) throws {
        var base = name
        var modifiers: [Int] = []
        if let r = name.range(of: "psk") {
            base = String(name[name.startIndex..<r.lowerBound])
            let tail = String(name[r.lowerBound...])
            for part in tail.split(separator: "+", omittingEmptySubsequences: false) {
                guard part.hasPrefix("psk"), let n = Int(part.dropFirst(3)), n >= 0 else {
                    throw NoiseError.unsupportedPattern(name)
                }
                modifiers.append(n)
            }
        }
        guard let def = HandshakePattern.baseDefinitions[base] else {
            throw NoiseError.unsupportedPattern(name)
        }
        let pre = try HandshakePattern.parse(def.pre)
        let msgs = try HandshakePattern.parse(def.msgs)
        // Mesaj yönleri -> e, <- e, ... diye ardışık olmalı (tablo hatasına karşı).
        for (i, m) in msgs.enumerated() where m.fromInitiator != (i % 2 == 0) {
            throw NoiseError.unsupportedPattern(name)
        }
        var messages = msgs.map { $0.tokens }
        for n in modifiers {
            if n == 0 {
                messages[0].insert(.psk, at: 0)
            } else {
                guard n <= messages.count else { throw NoiseError.unsupportedPattern(name) }
                messages[n - 1].append(.psk)
            }
        }
        self.name = name
        self.initiatorPreMessage = pre.filter { $0.fromInitiator }.flatMap { $0.tokens }
        self.responderPreMessage = pre.filter { !$0.fromInitiator }.flatMap { $0.tokens }
        self.messages = messages
    }

    /// `Noise_IK_25519_ChaChaPoly_SHA256` gibi tam protokol adını çözer; yalnız bu şifre takımı kabul edilir.
    public init(protocolName: String) throws {
        let parts = protocolName.split(separator: "_", omittingEmptySubsequences: false)
        guard parts.count == 5, parts[0] == "Noise",
              parts[2] == "25519", parts[3] == "ChaChaPoly", parts[4] == "SHA256" else {
            throw NoiseError.invalidProtocolName(protocolName)
        }
        try self.init(name: String(parts[1]))
    }

    public static let ik = try! HandshakePattern(name: "IK")
    public static let xx = try! HandshakePattern(name: "XX")
    public static let xxpsk3 = try! HandshakePattern(name: "XXpsk3")

    private struct Line {
        let fromInitiator: Bool
        let tokens: [Token]
    }

    private static func parse(_ text: String) throws -> [Line] {
        var out: [Line] = []
        for raw in text.split(separator: ";") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty { continue }
            let fromInitiator: Bool
            if line.hasPrefix("->") { fromInitiator = true }
            else if line.hasPrefix("<-") { fromInitiator = false }
            else { throw NoiseError.unsupportedPattern(text) }
            let rest = line.dropFirst(2)
            var tokens: [Token] = []
            for t in rest.split(separator: ",") {
                let s = t.trimmingCharacters(in: .whitespaces)
                guard let tok = Token(rawValue: s) else { throw NoiseError.unsupportedPattern(text) }
                tokens.append(tok)
            }
            out.append(Line(fromInitiator: fromInitiator, tokens: tokens))
        }
        return out
    }

    /// Noise rev 34: temel desenler (§7.4–7.5) ve ertelenmiş desenler (Ek A). "pre" = ön mesajlar, "msgs" = mesajlar.
    private static let baseDefinitions: [String: (pre: String, msgs: String)] = [
        // Tek yönlü
        "N": ("<- s", "-> e, es"),
        "K": ("-> s; <- s", "-> e, es, ss"),
        "X": ("<- s", "-> e, es, s, ss"),
        // Temel etkileşimli
        "NN": ("", "-> e; <- e, ee"),
        "KN": ("-> s", "-> e; <- e, ee, se"),
        "NK": ("<- s", "-> e, es; <- e, ee"),
        "KK": ("-> s; <- s", "-> e, es, ss; <- e, ee, se"),
        "NX": ("", "-> e; <- e, ee, s, es"),
        "KX": ("-> s", "-> e; <- e, ee, se, s, es"),
        "XN": ("", "-> e; <- e, ee; -> s, se"),
        "IN": ("", "-> e, s; <- e, ee, se"),
        "XK": ("<- s", "-> e, es; <- e, ee; -> s, se"),
        "IK": ("<- s", "-> e, es, s, ss; <- e, ee, se"),
        "XX": ("", "-> e; <- e, ee, s, es; -> s, se"),
        "IX": ("", "-> e, s; <- e, ee, se, s, es"),
        // Ertelenmiş
        "NK1": ("<- s", "-> e; <- e, ee, es"),
        "NX1": ("", "-> e; <- e, ee, s; -> es"),
        "X1N": ("", "-> e; <- e, ee; -> s; <- se"),
        "X1K": ("<- s", "-> e, es; <- e, ee; -> s; <- se"),
        "XK1": ("<- s", "-> e; <- e, ee, es; -> s, se"),
        "X1K1": ("<- s", "-> e; <- e, ee, es; -> s; <- se"),
        "X1X": ("", "-> e; <- e, ee, s, es; -> s; <- se"),
        "XX1": ("", "-> e; <- e, ee, s; -> es, s, se"),
        "X1X1": ("", "-> e; <- e, ee, s; -> es, s; <- se"),
        "K1N": ("-> s", "-> e; <- e, ee; -> se"),
        "K1K": ("-> s; <- s", "-> e, es; <- e, ee; -> se"),
        "KK1": ("-> s; <- s", "-> e; <- e, ee, se, es"),
        "K1K1": ("-> s; <- s", "-> e; <- e, ee, es; -> se"),
        "K1X": ("-> s", "-> e; <- e, ee, s, es; -> se"),
        "KX1": ("-> s", "-> e; <- e, ee, se, s; -> es"),
        "K1X1": ("-> s", "-> e; <- e, ee, s; -> se, es"),
        "I1N": ("", "-> e, s; <- e, ee; -> se"),
        "I1K": ("<- s", "-> e, es, s; <- e, ee; -> se"),
        "IK1": ("<- s", "-> e, s; <- e, ee, se, es"),
        "I1K1": ("<- s", "-> e, s; <- e, ee, es; -> se"),
        "IX1": ("", "-> e, s; <- e, ee, se, s; -> es"),
        "I1X": ("", "-> e, s; <- e, ee, s, es; -> se"),
        "I1X1": ("", "-> e, s; <- e, ee, s; -> se, es"),
    ]
}
