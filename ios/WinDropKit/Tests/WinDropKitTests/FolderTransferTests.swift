import XCTest
import Foundation
@testable import WinDropKit

/// PROTOCOL §6.7 klasör gönderme.
extension TransferTests {
    func dirItem(_ id: Int, _ dir: String?, _ name: String, _ data: Data) -> SendItem {
        SendItem(id: id, name: name, size: Int64(data.count), mtime: 1_700_000_000_000, source: MemoryFileSource(data), dir: dir)
    }

    func read(_ rel: String) throws -> Data {
        try Data(contentsOf: inbox.appendingPathComponent(rel))
    }

    func visibleNames(_ rel: String = "") throws -> Set<String> {
        let url = rel.isEmpty ? inbox! : inbox.appendingPathComponent(rel)
        return Set(try FileManager.default.contentsOfDirectory(atPath: url.path).filter { !$0.hasPrefix(".") })
    }

    // MARK: JSON

    func testManifestDirIsOptionalInJson() throws {
        let plain = try FrameCodec.encode(ManifestBody(files: [ManifestFile(id: 1, name: "a", size: 1, mtime: 2)]))
        XCTAssertFalse(String(decoding: plain.dropFirst(), as: UTF8.self).contains("dir"))
        let withDir = try FrameCodec.encode(ManifestBody(files: [ManifestFile(id: 1, name: "a", size: 1, mtime: 2, dir: "Foo/bar")]))
        XCTAssertTrue(String(decoding: withDir.dropFirst(), as: UTF8.self).contains("\"dir\":\"Foo/bar\""))
        let back = try FrameCodec.decodeManifest(withDir.dropFirst())
        XCTAssertEqual(back.files[0].dir, "Foo/bar")
        let old = try FrameCodec.decodeManifest(Data(#"{"files":[{"id":1,"name":"a","size":1,"mtime":2}]}"#.utf8))
        XCTAssertNil(old.files[0].dir)
    }

    // MARK: Alım

    func testNestedThreeLevelsAndSameNamesInDifferentFolders() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let items = [
            dirItem(0, "Proje", "readme.txt", Data("kök".utf8)),
            dirItem(1, "Proje/src", "a.txt", Data("src-a".utf8)),
            dirItem(2, "Proje/src/lib/util", "a.txt", Data("util-a".utf8)),
            dirItem(3, "Proje/docs", "a.txt", Data("docs-a".utf8)),
            dirItem(4, nil, "tek.txt", Data("dir yok".utf8)),
        ]
        let (sr, rr) = try await runTransfer(s, items: items, opts: options(id, items: items))
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(rr.outcome, .completed)
        XCTAssertEqual(try read("Proje/readme.txt"), Data("kök".utf8))
        XCTAssertEqual(try read("Proje/src/a.txt"), Data("src-a".utf8))
        XCTAssertEqual(try read("Proje/src/lib/util/a.txt"), Data("util-a".utf8))
        XCTAssertEqual(try read("Proje/docs/a.txt"), Data("docs-a".utf8))
        XCTAssertEqual(try read("tek.txt"), Data("dir yok".utf8))
        XCTAssertEqual(try visibleNames(), ["Proje", "tek.txt"])
        XCTAssertEqual(try visibleNames("Proje"), ["readme.txt", "src", "docs"])
        // Aynı klasörde aynı ad: §6.6 kuralıyla numaralanır.
        XCTAssertEqual(rr.receivedFiles.count, 5)
        await s.closeAll()
    }

    func testSameNameInSameFolderIsNumberedAndComponentsAreSanitized() async throws {
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let items = [
            dirItem(0, "F", "x.txt", Data("1".utf8)),
            dirItem(1, "F", "x.txt", Data("2".utf8)),
            dirItem(2, "F/ab:c?/CON", "y.txt", Data("3".utf8)),
            dirItem(3, "F/ab..", "z.txt", Data("4".utf8)),
        ]
        let (_, rr) = try await runTransfer(s, items: items, opts: options(id, items: items))
        XCTAssertEqual(rr.outcome, .completed)
        XCTAssertEqual(try visibleNames("F"), ["x.txt", "x (2).txt", "ab_c_", "ab"])
        XCTAssertEqual(try read("F/ab_c_/_CON/y.txt"), Data("3".utf8))
        XCTAssertEqual(try read("F/ab/z.txt"), Data("4".utf8))
        await s.closeAll()
    }

    func testTraversalAndAbsoluteDirsAreProtocolErrors() async throws {
        let bad = ["..", "../x", "a/../b", "a/..", "/etc", "/", "a//b", "a/", ".", "a/./b", "", "a\\b", "C:", "C:/x", "x/d:y", "..\\x"]
        for dir in bad {
            let id = TransferId.random()
            let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
            let files = [ManifestFile(id: 0, name: "evil.txt", size: 5, mtime: 1, dir: dir)]
            do {
                _ = try await rawSender(s, id: id, files: files) { ch in
                    let f = try await ch.receiveFrame()
                    XCTAssertEqual(f?.type, .cancel, "dir '\(dir)'")
                }
                XCTFail("dir '\(dir)' reddedilmeli")
            } catch let e as TransferError {
                guard case .protocolViolation = e else { return XCTFail("dir '\(dir)': \(e)") }
            }
            await s.closeAll()
        }
        // Hiçbir şey kök dışına yazılmadı.
        XCTAssertFalse(FileManager.default.fileExists(atPath: base.appendingPathComponent("evil.txt").path))
        XCTAssertEqual(try visibleNames(), [])
    }

    func testFolderPathEscapeCheck() throws {
        let root = URL(fileURLWithPath: "/tmp/windrop-root", isDirectory: true)
        XCTAssertThrowsError(try FolderPath.resolve(root: root, components: ["..", "x"]))
        XCTAssertThrowsError(try FolderPath.resolve(root: root, components: ["a", "..", "..", "x"]))
        XCTAssertThrowsError(try FolderPath.resolve(root: root, components: ["a/b"]))
        XCTAssertEqual(try FolderPath.resolve(root: root, components: ["a", "b"]).path, "/tmp/windrop-root/a/b")
        XCTAssertThrowsError(try FolderPath.sanitizedComponents("a/../b")) { XCTAssertEqual($0 as? FolderPathError, .malformed("'..' klasör bileşeni")) }
        // Depolama düzeyinde de reddedilir (savunma derinliği).
        let store = try DirectoryReceiverStorage(directory: inbox)
        let partial = try store.openPartial(for: PartialKey(peerId: "p", transferId: TransferId.random(), fileId: 0), size: 0, mtime: 0)
        XCTAssertThrowsError(try partial.commit(directory: ["..", "out"], fileName: "x.txt", sha256: "")) {
            XCTAssertEqual($0 as? FolderPathError, .escapesRoot)
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: base.appendingPathComponent("out").path))
    }

    func testTopFolderCollisionUsesOneSuffixedRootForWholeTransfer() async throws {
        try FileManager.default.createDirectory(at: inbox.appendingPathComponent("Foo"), withIntermediateDirectories: true)
        try Data("eski".utf8).write(to: inbox.appendingPathComponent("Foo/var.txt"))
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let items = [
            dirItem(0, "Foo", "var.txt", Data("yeni".utf8)),
            dirItem(1, "Foo/alt", "b.txt", Data("b".utf8)),
            dirItem(2, "Foo/alt/derin", "c.txt", Data("c".utf8)),
            dirItem(3, "Bar", "d.txt", Data("d".utf8)),
        ]
        let (_, rr) = try await runTransfer(s, items: items, opts: options(id, items: items))
        XCTAssertEqual(rr.outcome, .completed)
        XCTAssertEqual(try read("Foo/var.txt"), Data("eski".utf8), "var olan klasöre dokunulmaz")
        XCTAssertEqual(try read("Foo (2)/var.txt"), Data("yeni".utf8), "alt klasörde çakışma yok, kök hep Foo (2)")
        XCTAssertEqual(try read("Foo (2)/alt/b.txt"), Data("b".utf8))
        XCTAssertEqual(try read("Foo (2)/alt/derin/c.txt"), Data("c".utf8))
        XCTAssertEqual(try read("Bar/d.txt"), Data("d".utf8))
        XCTAssertEqual(try visibleNames(), ["Foo", "Foo (2)", "Bar"])
        await s.closeAll()

        // Yeni aktarım aynı adla gelirse "Foo (3)".
        let id2 = TransferId.random()
        let s2 = try await makeSession(transferId: id2, phoneKey: phoneKey, pcKey: pcKey)
        let items2 = [dirItem(0, "Foo", "z.txt", Data("z".utf8))]
        let (_, rr2) = try await runTransfer(s2, items: items2, opts: options(id2, items: items2))
        XCTAssertEqual(rr2.outcome, .completed)
        XCTAssertEqual(try read("Foo (3)/z.txt"), Data("z".utf8))
        await s2.closeAll()
    }

    func testTopFolderNameKeepsUtf8ByteLimit() async throws {
        let long = String(repeating: "ş", count: 200)   // 400 bayt
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let items = [dirItem(0, long + "/" + long, "a.txt", Data("a".utf8))]
        let (_, rr) = try await runTransfer(s, items: items, opts: options(id, items: items))
        XCTAssertEqual(rr.outcome, .completed)
        let top = try XCTUnwrap(try visibleNames().first)
        XCTAssertLessThanOrEqual(top.decomposedStringWithCanonicalMapping.utf8.count, 255)
        let sub = try XCTUnwrap(try visibleNames(top).first)
        XCTAssertLessThanOrEqual(sub.decomposedStringWithCanonicalMapping.utf8.count, 255)
        await s.closeAll()

        // Çakışmada ek de bayt sınırını aşmaz.
        let id2 = TransferId.random()
        let s2 = try await makeSession(transferId: id2, phoneKey: phoneKey, pcKey: pcKey)
        let (_, rr2) = try await runTransfer(s2, items: items, opts: options(id2, items: items))
        XCTAssertEqual(rr2.outcome, .completed)
        let tops = try visibleNames()
        XCTAssertEqual(tops.count, 2)
        XCTAssertTrue(tops.allSatisfy { $0.decomposedStringWithCanonicalMapping.utf8.count <= 255 })
        XCTAssertTrue(tops.contains { $0.hasSuffix(" (2)") })
        await s2.closeAll()
    }

    func testDepthAndLengthLimits() async throws {
        func comps(_ n: Int) -> String { (1...n).map { "d\($0)" }.joined(separator: "/") }
        // 16 seviye tamam.
        do {
            let id = TransferId.random()
            let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
            let items = [dirItem(0, comps(16), "ok.txt", Data("ok".utf8))]
            let (_, rr) = try await runTransfer(s, items: items, opts: options(id, items: items))
            XCTAssertEqual(rr.outcome, .completed)
            XCTAssertEqual(try read(comps(16) + "/ok.txt"), Data("ok".utf8))
            await s.closeAll()
        }
        // 17 seviye ve 1025 karakter reddedilir (aborted).
        for dir in [comps(17), String(repeating: "a", count: 100) + "/" + String(repeating: "b", count: 924)] {
            XCTAssertGreaterThan(dir.utf16.count, dir == comps(17) ? 0 : 1024)
            let id = TransferId.random()
            let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
            let files = [ManifestFile(id: 0, name: "x.txt", size: 5, mtime: 1, dir: dir)]
            let r = try await rawSender(s, id: id, files: files) { ch in
                let f = try await ch.receiveFrame()
                XCTAssertEqual(f?.type, .cancel)
            }
            XCTAssertEqual(r.outcome, .aborted, "dir uzunluğu \(dir.utf16.count)")
            await s.closeAll()
        }
        XCTAssertThrowsError(try FolderPath.sanitizedComponents(comps(17))) { XCTAssertEqual($0 as? FolderPathError, .tooDeep(17)) }
        XCTAssertNoThrow(try FolderPath.sanitizedComponents(comps(16)))
        XCTAssertThrowsError(try FolderPath.sanitizedComponents(String(repeating: "a", count: 1025))) {
            XCTAssertEqual($0 as? FolderPathError, .tooLong(1025))
        }
        XCTAssertNoThrow(try FolderPath.sanitizedComponents(String(repeating: "a", count: 1024)))
    }

    // MARK: Devam ettirme

    func testResumeWithFoldersKeepsSameRootAndDoneRecords() async throws {
        // Var olan "Foo" nedeniyle kök "Foo (2)". 1. oturumda a.bin tamamlanır, b.bin yarıda kalır.
        try FileManager.default.createDirectory(at: inbox.appendingPathComponent("Foo"), withIntermediateDirectories: true)
        let id = TransferId.random()
        let a = patternData(count: 300_000, seed: 21), b = patternData(count: 2_000_000, seed: 22)
        let items = [dirItem(0, "Foo/x", "a.bin", a), dirItem(1, "Foo/y/z", "b.bin", b)]
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        s.phoneEnd.writeBudget = 900_000
        let store = try DirectoryReceiverStorage(directory: inbox)
        let sender = FileSender(channel: s.phone, items: items)
        let receiver = FileReceiver(channel: s.pc, storage: store, options: options(id, items: items, ack: 128 * 1024))
        async let first: Result<TransferResult, Error> = {
            do { return .success(try await receiver.run()) } catch { return .failure(error) }
        }()
        do { _ = try await sender.run(); XCTFail("kesilmeli") } catch {}
        _ = await first
        XCTAssertEqual(try read("Foo (2)/x/a.bin"), a)

        // Yeni oturum, aynı transferId: ikinci kez "Foo (3)" açılmamalı.
        let s2 = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let (sr, r2) = try await runTransfer(s2, items: items, opts: options(id, items: items, ack: 128 * 1024))
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(r2.outcome, .completed)
        XCTAssertEqual(r2.resumeOffsets[0], Int64(a.count), "tamamlanmış dosya (.done, klasörlü) yeniden gönderilmez")
        XCTAssertGreaterThan(r2.resumeOffsets[1] ?? 0, 0)
        XCTAssertEqual(try read("Foo (2)/x/a.bin"), a)
        XCTAssertEqual(try read("Foo (2)/y/z/b.bin"), b)
        XCTAssertEqual(try visibleNames(), ["Foo", "Foo (2)"])
        XCTAssertEqual(r2.receivedFiles.count, 2)
        XCTAssertTrue(try XCTUnwrap(r2.receivedFiles.first { $0.id == 0 }).storedPath.hasSuffix("Foo (2)/x/a.bin"))
        await s2.closeAll()
    }

    // MARK: Gönderen: klasör tarama

    func testFolderScannerListsRecursivelySkipsSymlinksAndSetsDir() async throws {
        let src = base.appendingPathComponent("Proje", isDirectory: true)
        let fm = FileManager.default
        try fm.createDirectory(at: src.appendingPathComponent("a/b"), withIntermediateDirectories: true)
        try fm.createDirectory(at: src.appendingPathComponent("bos"), withIntermediateDirectories: true)
        try Data("1".utf8).write(to: src.appendingPathComponent("k.txt"))
        try Data("22".utf8).write(to: src.appendingPathComponent("a/x.txt"))
        try Data("333".utf8).write(to: src.appendingPathComponent("a/b/y.txt"))
        let outside = base.appendingPathComponent("disarida", isDirectory: true)
        try fm.createDirectory(at: outside, withIntermediateDirectories: true)
        try Data("gizli".utf8).write(to: outside.appendingPathComponent("s.txt"))
        try fm.createSymbolicLink(at: src.appendingPathComponent("lnk"), withDestinationURL: outside)
        try fm.createSymbolicLink(at: src.appendingPathComponent("lnk.txt"), withDestinationURL: outside.appendingPathComponent("s.txt"))

        let items = try FolderScanner.items(folder: src, startId: 10)
        XCTAssertEqual(items.map { "\($0.dir ?? "-")|\($0.name)" }, ["Proje|k.txt", "Proje/a|x.txt", "Proje/a/b|y.txt"])
        XCTAssertEqual(items.map(\.id), [10, 11, 12])
        XCTAssertEqual(items.map(\.size), [1, 2, 3])

        // Uçtan uca: tarayıcı çıktısı alıcıda aynı ağacı kurar.
        let id = TransferId.random()
        let s = try await makeSession(transferId: id, phoneKey: phoneKey, pcKey: pcKey)
        let (sr, rr) = try await runTransfer(s, items: items, opts: options(id, items: items))
        XCTAssertEqual(sr.outcome, .completed)
        XCTAssertEqual(rr.outcome, .completed)
        XCTAssertEqual(try read("Proje/a/b/y.txt"), Data("333".utf8))
        XCTAssertEqual(try visibleNames("Proje"), ["k.txt", "a"])
        await s.closeAll()
    }

    func testFolderScannerRejectsTooDeepTree() throws {
        var url = base.appendingPathComponent("Kok", isDirectory: true)
        for i in 1...16 { url.appendPathComponent("d\(i)", isDirectory: true) }   // Kok + 16 = 17 seviye
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        try Data("x".utf8).write(to: url.appendingPathComponent("f.txt"))
        XCTAssertThrowsError(try FolderScanner.items(folder: base.appendingPathComponent("Kok"))) {
            guard case FolderScanError.tooDeep = $0 else { return XCTFail("\($0)") }
        }
    }
}
