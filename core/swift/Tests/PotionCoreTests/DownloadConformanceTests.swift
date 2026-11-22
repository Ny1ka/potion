import XCTest
@testable import PotionCore

/// Runs the shared cases in core/spec/tests/downloads.json. core/dotnet runs the same file.
final class DownloadConformanceTests: XCTestCase {
    struct CaseFile: Decodable {
        let responses: [ResponseCase]
        let sniff: [SniffCase]
        let fileNames: [FileNameCase]
        let robotsStatus: [RobotsStatusCase]
        let robots: [RobotsCase]
    }
    struct ResponseCase: Decodable {
        let name: String
        let status: Int
        let headers: [String: String]
        let range: Bool?
        let maxBytes: Int64?
        let expect: String
        let length: Int64?
        let resumed: Bool?
        let location: String?
        let seconds: Int?
    }
    struct Segment: Decodable { let hex: String?; let ascii: String?; let zeros: Int? }
    struct SniffCase: Decodable { let name: String; let file: String; let bytes: [Segment]; let expect: String }
    struct FileNameCase: Decodable { let name: String; let disposition: String?; let link: String; let expect: String }
    struct RobotsStatusCase: Decodable { let status: Int; let expect: String }
    struct RobotsCase: Decodable {
        struct PathCase: Decodable { let path: String; let allowed: Bool }
        let name: String
        let robots: String
        let paths: [PathCase]
    }

    let registry = SourceRegistry.shared

    func cases() throws -> CaseFile {
        try JSONDecoder().decode(CaseFile.self, from: RepoFiles.data("core/spec/tests/downloads.json"))
    }

    func testResponses() throws {
        for c in try cases().responses {
            let verdict = DownloadRules.classify(status: c.status, headers: c.headers, rangeRequested: c.range ?? false,
                                                 maxBytes: c.maxBytes ?? registry.download.defaultMaxBytes, settings: registry.download)
            switch verdict {
            case .accept(let sniff, let length, let resumed):
                XCTAssertEqual(sniff ? "sniff" : "media", c.expect, c.name)
                if let expected = c.length { XCTAssertEqual(length, expected, c.name) }
                if let expected = c.resumed { XCTAssertEqual(resumed, expected, c.name) }
            case .redirect(let location):
                XCTAssertEqual("redirect", c.expect, c.name)
                XCTAssertEqual(location, c.location, c.name)
            case .retry(let seconds):
                XCTAssertEqual("retry", c.expect, c.name)
                XCTAssertEqual(seconds, c.seconds, c.name)
            case .restart:
                XCTAssertEqual("restart", c.expect, c.name)
            case .fail(let error):
                XCTAssertEqual(error.id, c.expect, c.name)
            }
        }
    }

    func testSniff() throws {
        for c in try cases().sniff {
            var bytes: [UInt8] = []
            for s in c.bytes {
                if let hex = s.hex { bytes += try XCTUnwrap(Hex.bytes(hex), "\(c.name): bad hex") }
                if let ascii = s.ascii { bytes += Array(ascii.utf8) }
                if let zeros = s.zeros { bytes += [UInt8](repeating: 0, count: zeros) }
            }
            switch DownloadRules.sniff(header: Data(bytes), fileName: c.file) {
            case .success(let format): XCTAssertEqual(format, c.expect, c.name)
            case .failure(let error): XCTAssertEqual(error.id, c.expect, c.name)
            }
        }
    }

    func testFileNames() throws {
        for c in try cases().fileNames {
            let link = try XCTUnwrap(try? registry.link(from: c.link).get(), c.name)
            let name = DownloadRules.fileName(contentDisposition: c.disposition, link: link.parts, maxLength: registry.download.maxFileNameLength)
            XCTAssertEqual(name, c.expect, c.name)
        }
    }

    func testRobotsStatus() throws {
        for c in try cases().robotsStatus {
            XCTAssertEqual(RobotsFetch(status: c.status).rawValue, c.expect, "\(c.status)")
        }
    }

    func testRobots() throws {
        for c in try cases().robots {
            let rules = RobotsRules(text: c.robots)
            for p in c.paths {
                XCTAssertEqual(rules.isAllowed(path: p.path, agent: registry.robotsAgent), p.allowed, "\(c.name): \(p.path)")
            }
        }
    }
}

final class DownloadRulesTests: XCTestCase {
    func testRobotsAgentComesFromTheUserAgent() {
        XCTAssertEqual(SourceRegistry.shared.robotsAgent, "PotionDropper")
    }

    func testEveryDownloadErrorHasAMessage() {
        for error in DownloadError.allCases { XCTAssertFalse(SourceRegistry.shared.message(for: error).isEmpty, error.id) }
    }

    func testFixedRobotsModes() {
        XCTAssertTrue(RobotsRules.allowAll.isAllowed(path: "/a.mp4", agent: "PotionDropper"))
        XCTAssertFalse(RobotsRules.disallowAll.isAllowed(path: "/a.mp4", agent: "PotionDropper"))
        XCTAssertTrue(RobotsRules.disallowAll.isAllowed(path: "/robots.txt", agent: "PotionDropper"))
    }

    func testRetryAfterDateWithoutDateHeaderUsesNow() {
        let now = Date(timeIntervalSince1970: 1_792_567_650)   // Wed, 21 Oct 2026 07:27:30 GMT
        XCTAssertEqual(DownloadRules.retryAfter("Wed, 21 Oct 2026 07:27:40 GMT", date: nil, now: now), 10)
    }
}
