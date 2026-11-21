import XCTest
@testable import PotionCore

/// Runs the shared cases in core/spec/tests/matrix.json. core/dotnet runs the same file.
final class MatrixConformanceTests: XCTestCase {
    struct CaseFile: Decodable {
        let conversions: [ConversionCase]
        let outputs: [OutputsCase]
        let common: [CommonCase]
    }
    struct ConversionCase: Decodable { let name: String; let from: String; let to: String; let operation: String?; let notes: [String]? }
    struct OutputsCase: Decodable { let name: String; let from: String; let first: String; let includes: [String]; let excludes: [String] }
    struct CommonCase: Decodable { let name: String; let from: [String]; let includes: [String]; let excludes: [String]; let empty: Bool? }

    let matrix = ConversionMatrix.shared

    func cases() throws -> CaseFile {
        try JSONDecoder().decode(CaseFile.self, from: RepoFiles.data("core/spec/tests/matrix.json"))
    }

    func testConversions() throws {
        for c in try cases().conversions {
            let option = matrix.conversion(from: c.from, to: c.to)
            XCTAssertEqual(option?.operation.rawValue, c.operation, c.name)
            if let notes = c.notes { XCTAssertEqual(option?.notes.map(\.id), notes, c.name) }
        }
    }

    func testOutputs() throws {
        for c in try cases().outputs {
            let ids = matrix.outputs(for: c.from).map(\.output.id)
            XCTAssertEqual(ids.first, c.first, "\(c.name): first")
            for id in c.includes { XCTAssertTrue(ids.contains(id), "\(c.name): should include \(id)") }
            for id in c.excludes { XCTAssertFalse(ids.contains(id), "\(c.name): should exclude \(id)") }
        }
    }

    func testCommonOutputs() throws {
        for c in try cases().common {
            let ids = matrix.commonOutputs(for: c.from).map(\.id)
            for id in c.includes { XCTAssertTrue(ids.contains(id), "\(c.name): should include \(id)") }
            for id in c.excludes { XCTAssertFalse(ids.contains(id), "\(c.name): should exclude \(id)") }
            if c.empty == true { XCTAssertTrue(ids.isEmpty, c.name) }
        }
    }
}

final class ConversionMatrixTests: XCTestCase {
    func testBundledSpecMatchesSharedSpec() throws {
        let bundled = try Data(contentsOf: XCTUnwrap(Bundle.module.url(forResource: "matrix", withExtension: "json")))
        XCTAssertEqual(bundled, try RepoFiles.data("core/spec/matrix.json"), "PotionCore's matrix.json is out of date. Run: make sync-spec")
    }

    func testEveryFormatCanBecomeSomething() {
        for format in FormatRegistry.shared.formats {
            XCTAssertFalse(ConversionMatrix.shared.outputs(for: format.id).isEmpty, "\(format.id) has no outputs")
        }
    }

    func testNotesHaveText() {
        let option = ConversionMatrix.shared.conversion(from: "pdf", to: "docx")
        XCTAssertEqual(option?.notes.first?.text.isEmpty, false)
    }

    func testUnknownInputHasNoOutputs() {
        XCTAssertTrue(ConversionMatrix.shared.outputs(for: "nope").isEmpty)
        XCTAssertTrue(ConversionMatrix.shared.commonOutputs(for: []).isEmpty)
    }

    func testRejectsConflictingOperations() {
        let json = """
        { "version": 1, "groups": {}, "notes": {},
          "routes": [{ "from": ["png"], "to": ["jpg"], "operation": "convertImage" },
                     { "from": ["png"], "to": ["jpg"], "operation": "imageToPdf" }] }
        """
        XCTAssertThrowsError(try ConversionMatrix(data: Data(json.utf8), registry: .shared)) { error in
            XCTAssertEqual(error as? ConversionMatrix.LoadError, .invalid("png → jpg has two operations: convertImage and imageToPdf"))
        }
    }

    func testRejectsUnknownReferences() {
        for route in [##"{ "from": ["#sound"], "to": ["mp3"], "operation": "transcodeAudio" }"##,
                      ##"{ "from": ["@nope"], "to": ["mp3"], "operation": "transcodeAudio" }"##,
                      ##"{ "from": ["xyz"], "to": ["mp3"], "operation": "transcodeAudio" }"##,
                      ##"{ "from": ["wav"], "to": ["mp3"], "operation": "teleport" }"##,
                      ##"{ "from": ["wav"], "to": ["mp3"], "note": "missing" }"##] {
            let json = #"{ "version": 1, "groups": {}, "notes": {}, "routes": [\#(route)] }"#
            XCTAssertThrowsError(try ConversionMatrix(data: Data(json.utf8), registry: .shared), route)
        }
    }
}
