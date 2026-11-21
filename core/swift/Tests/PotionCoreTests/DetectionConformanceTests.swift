import XCTest
@testable import PotionCore

/// Runs the shared cases in core/spec/tests/detection.json. core/dotnet runs the same file.
final class DetectionConformanceTests: XCTestCase {
    struct CaseFile: Decodable { let cases: [Case] }
    struct Case: Decodable {
        let name: String
        let file: String
        let bytes: [Segment]
        let expect: Expect
    }
    struct Segment: Decodable { let hex: String?; let ascii: String?; let zeros: Int? }
    struct Expect: Decodable { let format: String?; let method: String; let mismatch: Bool? }

    func testSharedDetectionCases() throws {
        let file = try JSONDecoder().decode(CaseFile.self, from: RepoFiles.data("core/spec/tests/detection.json"))
        XCTAssertGreaterThan(file.cases.count, 50)
        let registry = FormatRegistry.shared

        for c in file.cases {
            var bytes: [UInt8] = []
            for s in c.bytes {
                if let hex = s.hex { bytes += try XCTUnwrap(Hex.bytes(hex), "\(c.name): bad hex") }
                if let ascii = s.ascii { bytes += Array(ascii.utf8) }
                if let zeros = s.zeros { bytes += [UInt8](repeating: 0, count: zeros) }
            }
            let result = registry.detect(header: Data(bytes), fileName: c.file)
            XCTAssertEqual(result.format?.id, c.expect.format, "\(c.name): format")
            XCTAssertEqual(result.method.rawValue, c.expect.method, "\(c.name): method")
            if let mismatch = c.expect.mismatch {
                XCTAssertEqual(result.extensionMismatch, mismatch, "\(c.name): mismatch")
            }
        }
    }
}
