import XCTest
@testable import PotionCore

/// Runs the shared cases in core/spec/tests/links.json. core/dotnet runs the same file.
final class LinkConformanceTests: XCTestCase {
    struct CaseFile: Decodable {
        let links: [LinkCase]
        let lists: [ListCase]
    }
    struct LinkCase: Decodable {
        let name: String
        let input: String
        let url: String?
        let host: String?
        let source: String?
        let format: String?
        let needsCheck: Bool?
        let error: String?
    }
    struct ListCase: Decodable {
        struct Problem: Decodable { let input: String; let error: String }
        let name: String
        let input: String
        let links: [String]
        let errors: [Problem]
        let overLimit: Int
    }

    func cases() throws -> CaseFile {
        try JSONDecoder().decode(CaseFile.self, from: RepoFiles.data("core/spec/tests/links.json"))
    }

    func testLinks() throws {
        for c in try cases().links {
            switch SourceRegistry.shared.link(from: c.input) {
            case .success(let link):
                XCTAssertNil(c.error, "\(c.name): expected \(c.error ?? "")")
                XCTAssertEqual(link.string, c.url, c.name)
                XCTAssertEqual(link.host, c.host, c.name)
                XCTAssertEqual(link.sourceID, c.source, c.name)
                XCTAssertEqual(link.formatID, c.format, c.name)
                XCTAssertEqual(link.needsCheck, c.needsCheck, c.name)
                XCTAssertEqual(link.url.absoluteString, c.url, "\(c.name): URL round trip")
            case .failure(let error):
                XCTAssertEqual(error.rawValue, c.error, c.name)
            }
        }
    }

    func testLists() throws {
        for c in try cases().lists {
            let list = SourceRegistry.shared.links(in: c.input)
            XCTAssertEqual(list.links.map(\.string), c.links, c.name)
            XCTAssertEqual(list.problems.map(\.input), c.errors.map(\.input), c.name)
            XCTAssertEqual(list.problems.map(\.error.rawValue), c.errors.map(\.error), c.name)
            XCTAssertEqual(list.overLimit, c.overLimit, c.name)
        }
    }
}

final class SourceRegistryTests: XCTestCase {
    func testBundledSpecMatchesSharedSpec() throws {
        let bundled = try Data(contentsOf: XCTUnwrap(Bundle.module.url(forResource: "sources", withExtension: "json")))
        XCTAssertEqual(bundled, try RepoFiles.data("core/spec/sources.json"), "PotionCore's sources.json is out of date. Run: make sync-spec")
    }

    /// No adapter without an allowlist entry, and no allowlist entry without docs/SOURCES.md (spec Part 6.3.2).
    func testEverySourceIsDocumented() throws {
        let registry = SourceRegistry.shared
        XCTAssertEqual(Set(registry.sources.map(\.id)), Set(SourceAdapters.builtIn.map(\.id)))
        let doc = try String(contentsOf: RepoFiles.url("docs/SOURCES.md"), encoding: .utf8)
        for source in registry.sources {
            XCTAssertTrue(doc.contains("Source id: `\(source.id)`"), "docs/SOURCES.md has no entry for \(source.id)")
            XCTAssertTrue(doc.contains("Reviewed: \(source.reviewed)"), "docs/SOURCES.md review date for \(source.id) differs from sources.json")
        }
    }

    func testEveryErrorHasAMessage() {
        for error in LinkError.allCases {
            XCTAssertFalse(SourceRegistry.shared.message(for: error).isEmpty, error.rawValue)
        }
        XCTAssertEqual(SourceRegistry.shared.message(for: .unsupportedSite), "Potion Dropper can't download from this site.")
    }

    func testUserAgentIsHonest() {
        XCTAssertEqual(SourceRegistry.shared.userAgent(version: "1.0.0"), "PotionDropper/1.0.0 (+https://github.com/Ny1ka/potion)")
    }

    func testTooLong() {
        let long = "https://example.com/" + String(repeating: "a", count: 2100) + ".mp4"
        XCTAssertEqual(SourceRegistry.shared.link(from: long), .failure(.tooLong))
        let huge = "https://example.com/" + String(repeating: "é", count: 5000)
        XCTAssertEqual(SourceRegistry.shared.link(from: huge), .failure(.tooLong))
    }

    func testPasteLimit() {
        let text = (1...105).map { "https://example.com/\($0).mp4" }.joined(separator: "\n")
        let list = SourceRegistry.shared.links(in: text)
        XCTAssertEqual(list.links.count, 100)
        XCTAssertEqual(list.links.last?.string, "https://example.com/100.mp4")
        XCTAssertEqual(list.overLimit, 5)
    }

    func testAdapterWithoutAllowlistEntryIsRefused() throws {
        struct Stray: SourceAdapter {
            let id = "stray"
            func match(_ link: LinkParts, source: SourceInfo, formats: FormatRegistry) -> SourceMatch? { nil }
        }
        let data = try RepoFiles.data("core/spec/sources.json")
        XCTAssertThrowsError(try SourceRegistry(data: data, adapters: SourceAdapters.builtIn + [Stray()]))
        XCTAssertThrowsError(try SourceRegistry(data: data, adapters: []))
    }

    func testPunycode() {
        XCTAssertEqual(Punycode.encode("bücher"), "bcher-kva")
        XCTAssertEqual(Punycode.encode("例え"), "r8jz45g")
        XCTAssertEqual(Punycode.encode("テスト"), "zckzah")
        XCTAssertEqual(Punycode.encode("münchen-straße"), "mnchen-strae-v9a90b")
    }
}
