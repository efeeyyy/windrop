import XCTest
import Foundation
@testable import WinDropKit

final class PrefixTests: XCTestCase {
    func testTcpPrefixLayout() throws {
        let id = Data((0..<16).map { UInt8($0 + 1) })
        let p = try Prefix(transport: .tcp, transferId: id)
        XCTAssertEqual(p.bytes.count, 20)
        XCTAssertEqual(toHex(p.bytes), "5744" + "01" + "02" + toHex(id))
        XCTAssertEqual(try Prefix.decode(p.bytes), p)
    }

    func testBlePrefixHasZeroTransferId() throws {
        let p = Prefix.ble()
        XCTAssertEqual(toHex(p.bytes), "5744" + "01" + "01" + String(repeating: "00", count: 16))
        XCTAssertThrowsError(try Prefix(transport: .ble, transferId: Data(repeating: 1, count: 16)))
        var bad = p.bytes; bad[10] = 1
        XCTAssertThrowsError(try Prefix.decode(bad))
    }

    func testDecodeRejectsMalformed() throws {
        XCTAssertThrowsError(try Prefix.decode(Data(count: 19)))
        var b = Prefix.ble().bytes
        b[0] = 0x58
        XCTAssertThrowsError(try Prefix.decode(b))
        b = Prefix.ble().bytes; b[3] = 9
        XCTAssertThrowsError(try Prefix.decode(b))
        XCTAssertThrowsError(try Prefix(transport: .tcp, transferId: Data(count: 15)))
    }

    func testVersionMismatchResponse() throws {
        XCTAssertEqual(Prefix.versionMismatchResponse(), Data([0xFF, 1]))
        XCTAssertEqual(Prefix.decodeVersionMismatch(Data([0xFF, 1])), 1)
        XCTAssertNil(Prefix.decodeVersionMismatch(Data([0x00, 1])))
        XCTAssertNil(Prefix.decodeVersionMismatch(Data([0xFF])))
        let p = try Prefix(version: 2, transport: .tcp, transferId: Data(count: 16))
        XCTAssertFalse(p.isVersionSupported())
        XCTAssertEqual(try Prefix.decode(p.bytes).version, 2)   // sürüm decode'da denetlenmez
    }
}

final class FramingTests: XCTestCase {
    func testFrameTypeBytes() {
        XCTAssertEqual([FrameType.manifest, .resume, .fileStart, .data, .fileEnd, .ack, .cancel, .done].map { $0.rawValue },
                       [1, 2, 3, 4, 5, 6, 7, 8])
    }

    func testLimits() throws {
        XCTAssertEqual(FrameCodec.maxPlaintext, 65519)
        XCTAssertEqual(FrameCodec.maxBody, 65518)
        XCTAssertNoThrow(try FrameCodec.encodeData(Data(count: 65518)))
        XCTAssertThrowsError(try FrameCodec.encodeData(Data(count: 65519)))
        XCTAssertThrowsError(try FrameCodec.decode(Data()))
        XCTAssertThrowsError(try FrameCodec.decode(Data([0])))
        XCTAssertThrowsError(try FrameCodec.decode(Data([9, 1])))
        XCTAssertThrowsError(try FrameCodec.decode(Data(count: 65520)))
    }

    func testControlBodiesRoundTripAndUseCSharpJsonShape() throws {
        let m = ManifestBody(files: [ManifestFile(id: 1, name: "a.txt", size: 10, mtime: 1_700_000_000_123)])
        let enc = try FrameCodec.encode(m)
        XCTAssertEqual(enc[0], 1)
        let f = try FrameCodec.decode(enc)
        XCTAssertEqual(f.type, .manifest)
        XCTAssertEqual(try FrameCodec.decodeManifest(f.body), m)

        // C# (System.Text.Json camelCase) çıktısı biçimi
        let csharp = Data(#"{"files":[{"id":2,"name":"b.bin","size":5000000000,"mtime":1700000000999}]}"#.utf8)
        let parsed = try FrameCodec.decodeManifest(csharp)
        XCTAssertEqual(parsed.files[0].size, 5_000_000_000)
        XCTAssertEqual(parsed.files[0].mtime, 1_700_000_000_999)

        XCTAssertEqual(try FrameCodec.decodeResume(FrameCodec.decode(FrameCodec.encode(ResumeBody(id: 3, offset: 4096))).body),
                       ResumeBody(id: 3, offset: 4096))
        XCTAssertEqual(try FrameCodec.decodeAck(Data(#"{"id":1,"offset":4194304}"#.utf8)), AckBody(id: 1, offset: 4_194_304))
        XCTAssertEqual(try FrameCodec.decodeFileStart(Data(#"{"id":1,"offset":0}"#.utf8)), FileStartBody(id: 1, offset: 0))
        XCTAssertEqual(try FrameCodec.decodeDone(Data(#"{"ok":false,"failed":[1,2]}"#.utf8)), DoneBody(ok: false, failed: [1, 2]))
    }

    func testCancelBody() throws {
        XCTAssertEqual(try FrameCodec.encode(CancelBody()), Data([7]) + Data("{}".utf8))
        XCTAssertEqual(try FrameCodec.decodeCancel(Data("{}".utf8)), CancelBody())
        XCTAssertEqual(try FrameCodec.decodeCancel(Data(#"{"reason":"x"}"#.utf8)), CancelBody(reason: "x"))
        let withReason = try FrameCodec.encode(CancelBody(reason: "kullanıcı"))
        XCTAssertEqual(try FrameCodec.decodeCancel(FrameCodec.decode(withReason).body).reason, "kullanıcı")
    }

    func testFileEndShaValidationAndRequiredFields() throws {
        let sha = String(repeating: "ab", count: 32)
        let e = try FrameCodec.encode(FileEndBody(id: 1, sha256: sha))
        XCTAssertEqual(try FrameCodec.decodeFileEnd(FrameCodec.decode(e).body).sha256, sha)
        XCTAssertThrowsError(try FrameCodec.decodeFileEnd(Data(#"{"id":1,"sha256":"AB"}"#.utf8)))
        XCTAssertThrowsError(try FrameCodec.decodeFileEnd(Data(#"{"id":1,"sha256":"\#(sha.uppercased())"}"#.utf8)))
        XCTAssertThrowsError(try FrameCodec.decodeResume(Data(#"{"id":1}"#.utf8)))
        XCTAssertThrowsError(try FrameCodec.decodeDone(Data("not json".utf8)))
    }

    func testTcpFramingWriteAndRead() async throws {
        let (a, b) = PipeEnd.makePair()
        try await TcpFraming.writeMessage(a, Data([1, 2, 3]))
        try await TcpFraming.writeMessage(a, Data(count: 65535))
        let raw = try await b.read(exactly: 5)
        XCTAssertEqual(raw, Data([0, 3, 1, 2, 3]))
        let big = try await TcpFraming.readMessage(b)
        XCTAssertEqual(big?.count, 65535)
        await a.close()
        let eof = try await TcpFraming.readMessage(b)
        XCTAssertNil(eof)
    }

    func testTcpFramingLengthIsBigEndian() async throws {
        let (a, b) = PipeEnd.makePair()
        try await TcpFraming.writeMessage(a, Data(count: 0x0102))
        let h = try await b.read(exactly: 2)
        XCTAssertEqual(h, Data([0x01, 0x02]))
    }

    func testTcpFramingErrors() async throws {
        let (a, b) = PipeEnd.makePair()
        await a.write0(Data([0, 0]))
        do { _ = try await TcpFraming.readMessage(b); XCTFail("sıfır uzunluk kabul edildi") } catch {
            XCTAssertEqual(error as? ProtocolError, .invalidFrame("sıfır uzunluklu çerçeve"))
        }
        let (c, d) = PipeEnd.makePair()
        await c.write0(Data([0, 10, 1, 2]))
        await c.close()
        do { _ = try await TcpFraming.readMessage(d); XCTFail("kesik çerçeve kabul edildi") } catch {
            XCTAssertEqual(error as? ProtocolError, .streamEnded)
        }
        let (e, f) = PipeEnd.makePair()
        await e.write0(Data([0]))
        await e.close()
        do { _ = try await TcpFraming.readMessage(f); XCTFail("kesik uzunluk öneki kabul edildi") } catch {
            XCTAssertEqual(error as? ProtocolError, .streamEnded)
        }
        do { try await TcpFraming.writeMessage(a, Data()); XCTFail() } catch {}
        do { try await TcpFraming.writeMessage(a, Data(count: 65536)); XCTFail() } catch {}
    }
}

private extension PipeEnd {
    func write0(_ d: Data) async { try? await write(d) }
}

final class BleFragmenterTests: XCTestCase {
    func testSingleFragmentHeader() throws {
        let frags = try BleFragmenter.fragment(Data([1, 2, 3]), mtu: 23, sequence: 5)
        XCTAssertEqual(frags, [Data([0x85, 1, 2, 3])])
    }

    func testMultiFragmentSharesSequenceAndMarksLast() throws {
        let msg = Data((0..<100).map { UInt8($0) })
        let frags = try BleFragmenter.fragment(msg, mtu: 23, sequence: 130)   // 130 mod 128 = 2
        XCTAssertEqual(try BleFragmenter.maxFragmentData(mtu: 23), 19)
        XCTAssertEqual(frags.count, 6)                                         // ceil(100/19)
        XCTAssertTrue(frags.dropLast().allSatisfy { $0[0] == 0x02 && $0.count == 20 })
        XCTAssertEqual(frags.last![0], 0x82)
        XCTAssertEqual(try BleFragmenter.reassemble(frags, expectedSequence: 2), msg)
    }

    func testLargeMtuAndMaxMessage() throws {
        let msg = Data(repeating: 0xAB, count: 8192)
        let frags = try BleFragmenter.fragment(msg, mtu: 185, sequence: 0)
        XCTAssertTrue(frags.allSatisfy { $0.count <= 185 - 3 })
        XCTAssertEqual(try BleFragmenter.reassemble(frags), msg)
        XCTAssertThrowsError(try BleFragmenter.fragment(Data(count: 8193), mtu: 185, sequence: 0))
        XCTAssertThrowsError(try BleFragmenter.fragment(Data(), mtu: 185, sequence: 0))
        XCTAssertThrowsError(try BleFragmenter.fragment(Data([1]), mtu: 22, sequence: 0))
    }

    func testReassemblerSequenceWrapAndSkip() throws {
        let r = BleReassembler(firstSequence: 126)
        for expected in [126, 127, 0, 1] {
            XCTAssertEqual(r.nextExpectedSequence, expected)
            let frag = try BleFragmenter.fragment(Data([UInt8(expected)]), mtu: 23, sequence: expected)
            XCTAssertEqual(try r.push(frag[0]), Data([UInt8(expected)]))
        }
        XCTAssertEqual(r.nextExpectedSequence, 2)
        XCTAssertThrowsError(try r.push(Data([0x84, 9])))                      // 2 beklenirken 4 geldi
        XCTAssertThrowsError(try r.push(Data([0x82, 9])))                      // bozuldu: kalıcı hata
    }

    func testReassemblerRejectsMidMessageSequenceChangeAndOversize() throws {
        let r = BleReassembler()
        XCTAssertNil(try r.push(Data([0x00, 1, 2])))
        XCTAssertThrowsError(try r.push(Data([0x81, 3])))

        let big = BleReassembler()
        var failed = false
        for _ in 0..<500 {
            do { _ = try big.push(Data([0x00]) + Data(count: 19)) } catch { failed = true; break }
        }
        XCTAssertTrue(failed, "8192 baytı aşan mesaj reddedilmeli")

        XCTAssertThrowsError(try BleReassembler().push(Data([0x80])))          // yalnız başlık
        XCTAssertThrowsError(try BleFragmenter.reassemble([Data([0x00, 1])]))  // son parça yok
    }
}

final class SignalMessageTests: XCTestCase {
    /// PROTOCOL §6.5; C# ile aynı JSON (anahtar sırası anlamsız).
    func testPushIdJson() throws {
        let id = "0123456789abcdef0123456789abcdef"
        let message = SignalMessage.pushId(PushIdMessage(pushId: id))
        XCTAssertEqual(String(decoding: try message.encode(), as: UTF8.self), #"{"pushId":"\#(id)","t":"pushId"}"#)
        XCTAssertEqual(try SignalMessage.decode(Data(#"{"t":"pushId","pushId":"\#(id)"}"#.utf8)), message)
        XCTAssertNil(message.transferId)
        XCTAssertThrowsError(try SignalMessage.decode(Data(#"{"t":"pushId"}"#.utf8)))   // zorunlu alan yok
    }

    func testRoundTripAllTypes() throws {
        let id = TransferId.random().description
        let all: [SignalMessage] = [
            .hello(HelloMessage(v: 1, name: "Efe iPhone", caps: ["tcp", "hotspot"], wifi: "none", btAudio: true)),
            .status(StatusMessage(wifi: "connected", btAudio: false)),
            .offer(OfferMessage(transferId: id, count: 2, total: 12_345, preview: ["a.txt", "b.png"])),
            .accept(ReplyMessage(transferId: id)),
            .reject(ReplyMessage(transferId: id, reason: "yer yok")),
            .addr(AddrMessage(transferId: id, candidates: [AddressCandidate(ip: "192.168.1.5", port: 53600)])),
            .hotspot(HotspotMessage(transferId: id, ip: "192.168.137.1", port: 53600)),
            .pairInfo(PairInfo(ssid: "WinDrop-1A2B3C4D", pass: "p@ss")),
            .relay(RelayMessage(transferId: id, token: "tok")),
            .path(PathMessage(transferId: id, result: .hotspotFail)),
            .cancel(SignalCancelMessage(transferId: id)),
            .bleData(BleDataMessage(transferId: id)),
            .pushId(PushIdMessage(pushId: "0123456789abcdef0123456789abcdef")),
        ]
        for m in all {
            let data = try m.encode()
            let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any]
            XCTAssertEqual(obj?["t"] as? String, m.type)
            XCTAssertEqual(try SignalMessage.decode(data), m)
        }
    }

    func testUnknownFieldsIgnoredUnknownTypeRejected() throws {
        let ok = Data(#"{"t":"status","wifi":"none","btAudio":true,"extra":[1,2]}"#.utf8)
        XCTAssertEqual(try SignalMessage.decode(ok), .status(StatusMessage(wifi: "none", btAudio: true)))
        XCTAssertThrowsError(try SignalMessage.decode(Data(#"{"t":"bogus"}"#.utf8))) {
            XCTAssertEqual($0 as? ProtocolError, .invalidMessage("bilinmeyen t: bogus"))
        }
        XCTAssertThrowsError(try SignalMessage.decode(Data(#"{"wifi":"none"}"#.utf8)))
        XCTAssertThrowsError(try SignalMessage.decode(Data(#"{"t":"status","wifi":"none"}"#.utf8)))
        XCTAssertThrowsError(try SignalMessage.decode(Data("[]".utf8)))
        XCTAssertThrowsError(try SignalMessage.decode(Data(#"{"t":"path","transferId":"x","result":"nope"}"#.utf8)))
        // transferId katı doğrulanır: 22 karakter base64url
        let good = TransferId.random().description
        XCTAssertNoThrow(try SignalMessage.decode(Data((#"{"t":"cancel","transferId":""# + good + #""}"#).utf8)))
        for bad in ["", "x", good + "=", String(good.dropLast()), good.replacingOccurrences(of: good.prefix(1), with: "+"), String(repeating: "0a", count: 16)] {
            XCTAssertThrowsError(try SignalMessage.decode(Data((#"{"t":"cancel","transferId":""# + bad + #""}"#).utf8)), bad)
        }
    }

    func testPathResultRawValues() throws {
        let m = SignalMessage.path(PathMessage(transferId: TransferId.random().description, result: .tcpOk))
        XCTAssertTrue(String(decoding: try m.encode(), as: UTF8.self).contains(#""result":"tcp_ok""#))
    }
}

final class FileNameSanitizerTests: XCTestCase {
    private struct FakeFS: FileNameExistence {
        var names: Set<String>
        func exists(_ fileName: String) -> Bool { names.contains(fileName.lowercased()) }
    }

    func testPlainNamesUnchanged() {
        XCTAssertEqual(FileNameSanitizer.sanitize("foto.jpg"), "foto.jpg")
        XCTAssertEqual(FileNameSanitizer.sanitize("Türkçe ğüşiöç.txt"), "Türkçe ğüşiöç.txt")
        XCTAssertEqual(FileNameSanitizer.sanitize("emoji 😀.png"), "emoji 😀.png")
    }

    func testOnlyLastPathComponent() {
        XCTAssertEqual(FileNameSanitizer.sanitize("../../etc/passwd"), "passwd")
        XCTAssertEqual(FileNameSanitizer.sanitize("C:\\Windows\\system32\\evil.dll"), "evil.dll")
        XCTAssertEqual(FileNameSanitizer.sanitize("a/b/"), "b")
        XCTAssertEqual(FileNameSanitizer.sanitize(".."), "file")
        XCTAssertEqual(FileNameSanitizer.sanitize("/"), "file")
        XCTAssertEqual(FileNameSanitizer.sanitize(""), "file")
        XCTAssertEqual(FileNameSanitizer.sanitize(nil), "file")
    }

    func testForbiddenCharactersReplaced() {
        XCTAssertEqual(FileNameSanitizer.sanitize("a:b.txt"), "a_b.txt")            // NTFS akışı
        XCTAssertEqual(FileNameSanitizer.sanitize("file.txt:Zone.Identifier"), "file.txt_Zone.Identifier")
        XCTAssertEqual(FileNameSanitizer.sanitize("a<b>c\"d|e?f*g"), "a_b_c_d_e_f_g")
        XCTAssertEqual(FileNameSanitizer.sanitize("a\u{0}b\u{1F}c\u{7F}d\u{85}e"), "a_b_c_d_e")
        XCTAssertEqual(FileNameSanitizer.sanitize("evil\u{202E}gpj.exe"), "evil_gpj.exe")
        XCTAssertEqual(FileNameSanitizer.sanitize("a\u{200E}b\u{2066}c"), "a_b_c")
    }

    func testTrailingDotsAndSpacesRemoved() {
        XCTAssertEqual(FileNameSanitizer.sanitize("name. . "), "name")
        XCTAssertEqual(FileNameSanitizer.sanitize("..."), "file")
        XCTAssertEqual(FileNameSanitizer.sanitize("   "), "file")
        XCTAssertEqual(FileNameSanitizer.sanitize(".bashrc"), ".bashrc")
    }

    func testReservedWindowsNames() {
        for n in ["CON", "con", "PRN", "AUX", "NUL", "COM1", "com9", "LPT1", "LPT9", "CON.txt", "nul.tar.gz", "COM1.txt", "COM\u{00B2}", "CONIN$"] {
            XCTAssertTrue(FileNameSanitizer.sanitize(n).hasPrefix("_"), n)
        }
        for n in ["COM0", "COM10", "CONSOLE", "NULL", "console.txt", "LPT", "xCON"] {
            XCTAssertEqual(FileNameSanitizer.sanitize(n), n)
        }
        XCTAssertEqual(FileNameSanitizer.sanitize("CON.txt"), "_CON.txt")
    }

    func testLengthLimitKeepsExtension() {
        let long = String(repeating: "a", count: 400) + ".jpg"
        let r = FileNameSanitizer.sanitize(long)
        XCTAssertEqual(r.utf16.count, 255)
        XCTAssertTrue(r.hasSuffix(".jpg"))
        let noExt = FileNameSanitizer.sanitize(String(repeating: "b", count: 300))
        XCTAssertEqual(noExt.utf16.count, 255)
        // Vekil çifti ortadan kesilmez.
        let emoji = String(repeating: "😀", count: 200)
        let e = FileNameSanitizer.sanitize(emoji)
        XCTAssertLessThanOrEqual(e.utf8.count, 255)
        XCTAssertFalse(e.unicodeScalars.contains("\u{FFFD}"))
    }

    func testLengthLimitIsUtf8Bytes() {
        // Türkçe: her "ğ" 2 bayt -> 127 karakter (254 bayt); karakter ortasından kesilmez.
        let tr = FileNameSanitizer.sanitize(String(repeating: "ğ", count: 200) + ".pdf")
        XCTAssertLessThanOrEqual(tr.utf8.count, 255)
        XCTAssertTrue(tr.hasSuffix(".pdf"))
        XCTAssertEqual(tr, String(repeating: "ğ", count: 125) + ".pdf")   // 250 + 4 = 254 bayt
        // Emoji (4 bayt) + uzantı.
        let emoji = FileNameSanitizer.sanitize(String(repeating: "😀", count: 100) + ".png")
        XCTAssertEqual(emoji, String(repeating: "😀", count: 62) + ".png")   // 248 + 4 = 252 bayt
        // 255 bayta tam sığan ad olduğu gibi kalır; 256 bayt kesilir.
        XCTAssertEqual(FileNameSanitizer.sanitize(String(repeating: "a", count: 255)).utf8.count, 255)
        XCTAssertEqual(FileNameSanitizer.sanitize(String(repeating: "ş", count: 128)).utf8.count, 254)
        XCTAssertEqual(FileNameSanitizer.sanitize(String(repeating: "a", count: 256)).utf8.count, 255)
        // Uzantısız uzun Türkçe ad.
        let noExt = FileNameSanitizer.sanitize(String(repeating: "ç", count: 300))
        XCTAssertEqual(noExt, String(repeating: "ç", count: 127))
        // Bileşik karakter (e + birleştirici) grafem ortasından kesilmez.
        let combining = FileNameSanitizer.sanitize(String(repeating: "e\u{0301}", count: 200))
        XCTAssertEqual(combining.utf8.count % 3, 0)   // yalnız bütün "e + ´" çiftleri
        XCTAssertLessThanOrEqual(combining.utf8.count, 255)
    }

    func testMakeUniqueRespectsUtf8Bytes() {
        let name = String(repeating: "ğ", count: 124) + ".txt"   // 248 + 4 = 252 bayt
        let fs = FakeFS(names: [name.lowercased()])
        let u = FileNameSanitizer.makeUnique(name, in: fs)
        XCTAssertLessThanOrEqual(u.utf8.count, 255)
        XCTAssertTrue(u.hasSuffix(" (2).txt"))
        XCTAssertFalse(u.unicodeScalars.contains("\u{FFFD}"))
        let emojiName = String(repeating: "😀", count: 62) + ".png"   // 252 bayt
        let u2 = FileNameSanitizer.makeUnique(emojiName, in: FakeFS(names: [emojiName.lowercased()]))
        XCTAssertLessThanOrEqual(u2.utf8.count, 255)
        XCTAssertTrue(u2.hasSuffix(" (2).png"))
        XCTAssertNotEqual(u2, emojiName)
    }

    func testConsoleDeviceNamesReserved() {
        for n in ["CONOUT$", "conout$", "CONIN$", "CONOUT$.txt", "conin$.log"] {
            XCTAssertTrue(FileNameSanitizer.sanitize(n).hasPrefix("_"), n)
        }
        XCTAssertEqual(FileNameSanitizer.sanitize("CONOUT$X"), "CONOUT$X")
    }

    func testMakeUnique() {
        let fs = FakeFS(names: ["a.txt", "a (2).txt", "noext", "noext (2)"])
        XCTAssertEqual(FileNameSanitizer.makeUnique("a.txt", in: fs), "a (3).txt")
        XCTAssertEqual(FileNameSanitizer.makeUnique("A.TXT", in: fs), "A (3).TXT")
        XCTAssertEqual(FileNameSanitizer.makeUnique("noext", in: fs), "noext (3)")
        XCTAssertEqual(FileNameSanitizer.makeUnique("free.txt", in: fs), "free.txt")
        XCTAssertEqual(FileNameSanitizer.sanitizeAndMakeUnique("x/a.txt", in: fs), "a (3).txt")
        let longName = String(repeating: "z", count: 251) + ".txt"
        let crowded = FakeFS(names: [longName])
        let u = FileNameSanitizer.makeUnique(longName, in: crowded)
        XCTAssertLessThanOrEqual(u.utf16.count, 255)
        XCTAssertTrue(u.hasSuffix(" (2).txt"))
    }
}
