import Foundation

/// PROTOCOL §3 BLE parçalama: bir Noise mesajı (en çok 8192 bayt) GATT yüklerine bölünür.
/// Yük = `1 B başlık || parça`; başlık bit7 = son parça, bit0–6 = mesaj sıra no (mod 128).
/// Sıra no mesaj başınadır: bir mesajın tüm parçaları aynı no'yu taşır (PROTOCOL §9a).
/// `mtu` ATT MTU'dur (en az 23); tek GATT yükü en çok `mtu − 3`, parça verisi en çok `mtu − 4` bayttır.
/// 20 baytlık ön ek parçalanmaz ve sayılmaz.
public enum BleFragmenter {
    public static let maxMessageSize = 8192
    public static let minMtu = 23
    public static let attOverhead = 3
    public static let headerSize = 1
    public static let sequenceModulo = 128
    static let lastFragmentFlag: UInt8 = 0x80
    static let sequenceMask: UInt8 = 0x7F

    /// MTU'ya göre tek parçadaki en çok mesaj baytı.
    public static func maxFragmentData(mtu: Int) throws -> Int {
        guard mtu >= minMtu else { throw ProtocolError.invalidFragment("ATT MTU en az \(minMtu) olmalı") }
        return mtu - attOverhead - headerSize
    }

    /// Mesajı GATT yüklerine böler. Boş veya 8192'den büyük mesaj hatadır.
    public static func fragment(_ message: Data, mtu: Int, sequence: Int) throws -> [Data] {
        let chunk = try maxFragmentData(mtu: mtu)
        guard !message.isEmpty else { throw ProtocolError.invalidFragment("boş mesaj parçalanamaz") }
        guard message.count <= maxMessageSize else { throw ProtocolError.messageTooLarge(message.count) }
        let seq = UInt8(((sequence % sequenceModulo) + sequenceModulo) % sequenceModulo)
        let bytes = [UInt8](message)
        let count = (bytes.count + chunk - 1) / chunk
        var result: [Data] = []
        result.reserveCapacity(count)
        for i in 0..<count {
            let start = i * chunk
            let end = min(start + chunk, bytes.count)
            var payload = Data([seq | (i == count - 1 ? lastFragmentFlag : 0)])
            payload.append(contentsOf: bytes[start..<end])
            result.append(payload)
        }
        return result
    }

    /// Tek bir mesajın tüm parçalarını birleştirir; eksik/fazla parça veya sıra hatasında hata.
    public static func reassemble(_ fragments: [Data], expectedSequence: Int = 0) throws -> Data {
        let r = BleReassembler(firstSequence: expectedSequence)
        var message: Data?
        for f in fragments {
            if message != nil { throw ProtocolError.invalidFragment("son parçadan sonra fazladan parça") }
            message = try r.push(f)
        }
        guard let message else { throw ProtocolError.invalidFragment("mesaj tamamlanmadı (son parça yok)") }
        return message
    }
}

/// Bir yöndeki GATT yüklerinden Noise mesajlarını birleştiren durum makinesi (G/Ç yok).
/// Herhangi bir hata sonrası nesne kalıcı bozulur: PROTOCOL §3 gereği oturum düşer, yeni el sıkışma gerekir.
public final class BleReassembler {
    private var buffer = Data()
    private var expectedSequence: Int
    private var currentSequence = -1
    private var faulted = false

    public init(firstSequence: Int = 0) {
        let m = BleFragmenter.sequenceModulo
        expectedSequence = ((firstSequence % m) + m) % m
    }

    /// Sıradaki mesajdan beklenen sıra no.
    public var nextExpectedSequence: Int { expectedSequence }

    /// Bir GATT yükü verir; mesaj tamamlandıysa birleşmiş mesajı, değilse nil döndürür.
    public func push(_ gattPayload: Data) throws -> Data? {
        guard !faulted else { throw ProtocolError.invalidFragment("birleştirici daha önce hata verdi; oturum düşürülmeli") }
        do {
            return try pushCore([UInt8](gattPayload))
        } catch {
            faulted = true
            throw error
        }
    }

    private func pushCore(_ payload: [UInt8]) throws -> Data? {
        guard payload.count > BleFragmenter.headerSize else {
            throw ProtocolError.invalidFragment("parça boş (yalnız başlık veya hiç bayt yok)")
        }
        let header = payload[0]
        let last = (header & BleFragmenter.lastFragmentFlag) != 0
        let seq = Int(header & BleFragmenter.sequenceMask)

        if currentSequence < 0 {
            guard seq == expectedSequence else {
                throw ProtocolError.invalidFragment("sıra no atlaması: beklenen \(expectedSequence), gelen \(seq)")
            }
            currentSequence = seq
        } else if seq != currentSequence {
            throw ProtocolError.invalidFragment("mesaj ortasında sıra no değişti: \(currentSequence) -> \(seq)")
        }

        guard buffer.count + payload.count - BleFragmenter.headerSize <= BleFragmenter.maxMessageSize else {
            throw ProtocolError.messageTooLarge(buffer.count + payload.count - BleFragmenter.headerSize)
        }
        buffer.append(contentsOf: payload[BleFragmenter.headerSize...])

        guard last else { return nil }
        let message = buffer
        buffer = Data()
        currentSequence = -1
        expectedSequence = (seq + 1) % BleFragmenter.sequenceModulo
        return message
    }
}
