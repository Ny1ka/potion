#if DEBUG
import XCTest
@testable import PotionCore

/// The Debug-only external source extension point, with a shell script standing in for an external program.
final class ExternalSourcesTests: XCTestCase {
    var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("potion-ext-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: root) }

    /// A source folder whose program is `/bin/sh <script>`.
    @discardableResult
    func makeSource(_ name: String, hosts: [String], script: String) throws -> URL {
        let folder = root.appendingPathComponent("sources/\(name)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try script.write(to: folder.appendingPathComponent("run.sh"), atomically: true, encoding: .utf8)
        let manifest: [String: Any] = ["version": 1, "id": name, "name": name.capitalized, "hosts": hosts,
                                       "executable": "/bin/sh", "arguments": [folder.appendingPathComponent("run.sh").path]]
        try JSONSerialization.data(withJSONObject: manifest).write(to: folder.appendingPathComponent("source.json"))
        return folder
    }

    static let wavScript = #"""
    out="$3"
    printf '{"progress":0.5}\n'
    printf '{"title":"Clip one"}\n'
    printf 'RIFF\044\000\000\000WAVEfmt ' > "$out/clip.wav"
    printf '{"file":"%s/clip.wav"}\n' "$out"
    """#

    func link(_ text: String) throws -> DropLink { try SourceRegistry.shared.link(from: text).get() }

    func testLoadsValidSourcesAndMatchesHosts() throws {
        try makeSource("alpha", hosts: ["example.org"], script: Self.wavScript)
        let broken = root.appendingPathComponent("sources/broken")
        try FileManager.default.createDirectory(at: broken, withIntermediateDirectories: true)
        try Data("not json".utf8).write(to: broken.appendingPathComponent("source.json"))

        let sources = ExternalSources.load(from: root.appendingPathComponent("sources"))
        XCTAssertEqual(sources.map(\.id), ["alpha"])
        XCTAssertTrue(sources[0].handles(try link("https://example.org/watch?v=1")))
        XCTAssertTrue(sources[0].handles(try link("https://media.example.org/a")))
        XCTAssertFalse(sources[0].handles(try link("https://notexample.org/a")))
        XCTAssertTrue(ExternalSources.load(from: root.appendingPathComponent("missing")).isEmpty)
    }

    func testJobUsesTheExternalSourceThenConverts() async throws {
        try makeSource("alpha", hosts: ["example.org"], script: Self.wavScript)
        let sources = ExternalSources.load(from: root.appendingPathComponent("sources"))
        let output = root.appendingPathComponent("out")
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        let converter = FakeConverter()
        let runner = DropJobRunner(downloader: Downloader(transport: ScriptedTransport(), appVersion: "1"), converter: converter,
                                   externalSources: sources, today: { "2026-11-22" })
        let outcome = try await runner.run(link("https://example.org/watch?v=1"), into: output, options: DropOptions(outputFormatID: "mp3"))
        XCTAssertEqual(outcome.sourceFormatID, "wav")
        XCTAssertEqual(outcome.files.map(\.lastPathComponent), ["Clip one.mp3"])
        XCTAssertEqual(converter.conversions.first?.from, "wav")
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: output.path), ["Clip one.mp3"], "work folder removed")
    }

    func testErrorsComeBackInTheSourcesOwnWords() async throws {
        try makeSource("failing", hosts: ["example.org"], script: "echo 'Something went wrong upstream' >&2\nexit 3\n")
        try makeSource("escaping", hosts: ["example.net"], script: "printf 'x' > /tmp/potion-escape-test\nprintf '{\"file\":\"/tmp/potion-escape-test\"}\\n'\n")
        let sources = ExternalSources.load(from: root.appendingPathComponent("sources"))
        let output = root.appendingPathComponent("out")
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        let runner = DropJobRunner(downloader: Downloader(transport: ScriptedTransport(), appVersion: "1"), converter: FakeConverter(),
                                   externalSources: sources)
        do {
            _ = try await runner.run(link("https://example.org/a"), into: output, options: DropOptions())
            XCTFail("the program failed")
        } catch let error as DropError {
            XCTAssertEqual(error.message(), "Something went wrong upstream")
        }
        do {
            _ = try await runner.run(link("https://example.net/a"), into: output, options: DropOptions())
            XCTFail("the file is outside the download folder")
        } catch let error as DropError {
            XCTAssertEqual(error.message(), "Escaping didn't save its file in the download folder.")
        }
        try? FileManager.default.removeItem(atPath: "/tmp/potion-escape-test")
    }
}
#endif
