import XCTest
@testable import PotionCore

final class FormatRegistryTests: XCTestCase {
    func testBundledSpecMatchesSharedSpec() throws {
        let bundled = try Data(contentsOf: XCTUnwrap(Bundle.module.url(forResource: "formats", withExtension: "json")))
        XCTAssertEqual(bundled, try RepoFiles.data("core/spec/formats.json"),
                       "PotionCore's formats.json is out of date. Run: make sync-spec")
    }

    func testCategoriesAgreeWithDesignSystem() throws {
        struct DesignFormats: Decodable { let byExtension: [String: String] }
        let design = try JSONDecoder().decode(DesignFormats.self, from: RepoFiles.data("design-system/data/formats.json"))
        for format in FormatRegistry.shared.formats {
            for ext in format.extensions {
                guard let designCategory = design.byExtension[ext] else { continue }
                XCTAssertEqual(format.category.rawValue, designCategory, ".\(ext) category differs from the design system")
            }
        }
    }

    func testEverySpecFormatIsListed() {
        let ids = Set(FormatRegistry.shared.formats.map(\.id))
        let required = ["mp4", "mov", "mkv", "avi", "webm", "wmv", "flv", "mpeg", "m4v", "3gp", "ts",
                        "mp3", "wav", "m4a", "aac", "flac", "ogg", "opus", "aiff", "wma", "alac",
                        "png", "jpg", "webp", "heic", "tiff", "bmp", "gif", "svg", "ico", "avif",
                        "pdf", "docx", "doc", "odt", "rtf", "txt", "html", "md", "epub"]
        XCTAssertEqual(Set(required).subtracting(ids), [])
    }

    func testExtensionLookup() {
        let r = FormatRegistry.shared
        XCTAssertEqual(r.format(forExtension: "JPEG")?.id, "jpg")
        XCTAssertEqual(r.format(forExtension: ".mpg")?.id, "mpeg")
        XCTAssertEqual(r.format(forExtension: "m4a")?.id, "m4a", "M4A is listed before ALAC, so it owns .m4a")
        XCTAssertNil(r.format(forExtension: "xyz"))
        XCTAssertEqual(r.format(id: "heic")?.preferredExtension, "heic")
    }

    func testFileExtensionParsing() {
        XCTAssertEqual(FormatRegistry.fileExtension(of: "Clip.Final.MOV"), "mov")
        XCTAssertNil(FormatRegistry.fileExtension(of: ".hidden"))
        XCTAssertNil(FormatRegistry.fileExtension(of: "README"))
        XCTAssertNil(FormatRegistry.fileExtension(of: "trailing."))
    }

    func testDetectReadsFromDisk() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("potion-\(UUID().uuidString).flac")
        try Data("fLaC".utf8 + [0, 0, 0, 0x22]).write(to: url)
        defer { try? FileManager.default.removeItem(at: url) }
        let result = try FormatRegistry.shared.detect(fileAt: url)
        XCTAssertEqual(result.format?.id, "flac")
        XCTAssertEqual(result.method, .content)
    }

    func testRejectsRuleForUnknownFormat() {
        let json = """
        { "version": 1, "headerSize": 16,
          "formats": [{ "id": "png", "name": "PNG", "category": "image", "extensions": ["png"] }],
          "rules": [{ "format": "nope", "all": [{ "offset": 0, "hex": "89" }] }] }
        """
        XCTAssertThrowsError(try FormatRegistry(data: Data(json.utf8))) { error in
            XCTAssertEqual(error as? FormatRegistry.LoadError, .invalid("Rule refers to unknown format nope"))
        }
    }

    func testRejectsMaskOfWrongLength() {
        let json = """
        { "version": 1, "headerSize": 16,
          "formats": [{ "id": "aac", "name": "AAC", "category": "audio", "extensions": ["aac"] }],
          "rules": [{ "format": "aac", "all": [{ "offset": 0, "hex": "FFF0", "mask": "FF" }] }] }
        """
        XCTAssertThrowsError(try FormatRegistry(data: Data(json.utf8)))
    }
}
