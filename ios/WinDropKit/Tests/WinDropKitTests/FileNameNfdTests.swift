import XCTest
@testable import WinDropKit

/// NFD'de uzayan adlar (Apple dosya sistemleri adı ayrıştırılmış tutar): sınır NFC ve NFD'nin büyüğüne göre.
final class FileNameNfdTests: XCTestCase {
    private func nfd(_ s: String) -> Int { s.decomposedStringWithCanonicalMapping.utf8.count }

    func testSanitizeLimitsNfdBytes() {
        let name = String(repeating: "ş", count: 200) + ".txt"   // NFC 404, NFD 604 bayt
        let out = FileNameSanitizer.sanitize(name)
        XCTAssertLessThanOrEqual(nfd(out), 255)
        XCTAssertLessThanOrEqual(out.utf8.count, 255)
        XCTAssertTrue(out.hasSuffix(".txt"))
    }

    private struct Taken: FileNameExistence {
        let names: Set<String>
        func exists(_ fileName: String) -> Bool { names.contains(fileName) }
    }

    func testMakeUniqueKeepsNfdLimit() {
        let base = FileNameSanitizer.sanitize(String(repeating: "ğ", count: 200) + ".bin")
        let unique = FileNameSanitizer.makeUnique(base, in: Taken(names: [base]))
        XCTAssertNotEqual(unique, base)
        XCTAssertTrue(unique.hasSuffix(" (2).bin"))
        XCTAssertLessThanOrEqual(nfd(unique), 255)
    }

    func testFolderUniqueNameKeepsNfdLimit() {
        let name = String(repeating: "ş", count: 85)   // NFD 255
        XCTAssertLessThanOrEqual(nfd(name), 255)
        let u = DirectoryReceiverStorage.uniqueFolderName(name) { $0 == name }
        XCTAssertTrue(u.hasSuffix(" (2)"))
        XCTAssertLessThanOrEqual(nfd(u), 255)
    }
}
