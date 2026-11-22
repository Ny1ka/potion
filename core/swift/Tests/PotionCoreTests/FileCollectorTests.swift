import XCTest
@testable import PotionCore

final class FileCollectorTests: XCTestCase {
    var dir: URL!

    override func setUpWithError() throws { dir = try EngineTestSupport.makeTempDirectory() }
    override func tearDownWithError() throws { if let dir { try? FileManager.default.removeItem(at: dir) } }

    @discardableResult
    func file(_ path: String, _ bytes: [UInt8]) throws -> URL {
        let url = dir.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(bytes).write(to: url)
        return url
    }

    let png: [UInt8] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] + [UInt8](repeating: 0, count: 24)
    let text: [UInt8] = Array("Plain notes\n".utf8)

    func testExpandsFoldersSkipsHiddenAndSortsByName() throws {
        try file("album/Track 10.txt", text)
        try file("album/Track 2.txt", text)
        try file("album/.DS_Store", [0, 1, 2])
        try file("album/.potion-1234.mp3", text)
        try file("album/inner/cover.png", png)
        try file("album/data.bin", [0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE])

        let collector = FileCollector()
        let flat = collector.collect([dir.appendingPathComponent("album")], includeSubfolders: false)
        XCTAssertEqual(flat.files.map(\.url.lastPathComponent), ["Track 2.txt", "Track 10.txt"])
        XCTAssertEqual(flat.unsupported.map(\.lastPathComponent), ["data.bin"])
        XCTAssertEqual(flat.files.first?.format.id, "txt")
        XCTAssertEqual(flat.files.first?.size, Int64(text.count))

        let deep = collector.collect([dir.appendingPathComponent("album")], includeSubfolders: true)
        XCTAssertEqual(deep.files.map(\.url.lastPathComponent), ["cover.png", "Track 2.txt", "Track 10.txt"])
    }

    func testDropsDuplicatesAndFilesAlreadyQueued() throws {
        let a = try file("a.png", png), b = try file("b.png", png)
        let result = FileCollector().collect([a, a, dir, b], includeSubfolders: false, excluding: [b])
        XCTAssertEqual(result.files.map(\.url.lastPathComponent), ["a.png"])
    }

    func testSkipsPackagesAndHonorsTheLimit() throws {
        try file("Tool.app/Contents/Info.plist", text)
        for i in 1...5 { try file("many/\(i).txt", text) }
        let result = FileCollector(limit: 3).collect([dir.appendingPathComponent("Tool.app"), dir.appendingPathComponent("many")],
                                                     includeSubfolders: true)
        XCTAssertEqual(result.files.count, 3)
        XCTAssertTrue(result.truncated)
    }
}
