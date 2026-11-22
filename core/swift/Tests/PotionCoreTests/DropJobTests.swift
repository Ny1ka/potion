import XCTest
@testable import PotionCore

/// A converter that reports a chosen duration and "converts" by writing a small file named after the input.
final class FakeConverter: MediaConverting, @unchecked Sendable {
    var duration: Double? = 60
    var copyProtected = false
    private(set) var conversions: [(input: String, from: String, to: String)] = []

    func probe(_ file: URL) async throws -> MediaInfo {
        let tag = copyProtected ? "drms" : "mp4a"
        return MediaInfo(formatName: "mov,mp4", duration: duration, bitRate: nil, streams: [
            .init(index: 0, kind: .audio, codec: "aac", codecTag: tag, sampleRate: 44100, channels: 2, bitsPerSample: nil, bitRate: nil,
                  width: nil, height: nil, frameRate: nil, pixelFormat: nil, isAttachedPicture: false, duration: duration),
        ])
    }

    func convert(_ input: URL, from inputFormat: FileFormat, to outputFormat: FileFormat, outputDirectory: URL,
                 settings: ConversionSettings, progress: @escaping @Sendable (Double) -> Void) async throws -> [URL] {
        conversions.append((input.lastPathComponent, inputFormat.id, outputFormat.id))
        progress(0.5)
        let out = OutputFiles.uniqueURL(in: outputDirectory, baseName: input.deletingPathExtension().lastPathComponent,
                                        fileExtension: outputFormat.preferredExtension)
        try Data("converted".utf8).write(to: out)
        return [out]
    }
}

final class DropNamingConformanceTests: XCTestCase {
    struct CaseFile: Decodable { let names: [NameCase] }
    struct NameCase: Decodable {
        let name: String; let template: String; let fileName: String; let host: String; let date: String; let format: String; let expect: String
    }

    func testNames() throws {
        let registry = SourceRegistry.shared
        for c in try JSONDecoder().decode(CaseFile.self, from: RepoFiles.data("core/spec/tests/downloads.json")).names {
            let name = DropNaming.render(c.template, fileName: c.fileName, host: c.host, date: c.date, formatName: c.format,
                                         maxLength: registry.download.maxFileNameLength - registry.naming.reserveForSuffix,
                                         fallback: registry.naming.fallbackName)
            XCTAssertEqual(name, c.expect, c.name)
        }
    }
}

final class DropJobTests: XCTestCase {
    var folder: URL!
    var transport: ScriptedTransport!
    var converter: FakeConverter!

    override func setUpWithError() throws {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("potion-job-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        transport = ScriptedTransport()
        converter = FakeConverter()
    }

    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: folder) }

    var runner: DropJobRunner {
        DropJobRunner(downloader: Downloader(transport: transport, appVersion: "1.0.0", sleep: { _ in }), converter: converter, today: { "2026-11-22" })
    }

    func link(_ text: String) throws -> DropLink { try SourceRegistry.shared.link(from: text).get() }
    func visibleFiles() throws -> [String] { try FileManager.default.contentsOfDirectory(atPath: folder.path).sorted() }

    func serveMP4(_ url: String, size: Int = 3000) {
        transport.on(url, .init(headers: ["Content-Type": "video/mp4"], chunks: [DownloaderTests.mp4(size)]))
    }

    func testKeepsTheDownloadedFormat() async throws {
        serveMP4("https://example.com/media/talk.mp4")
        let stages = StageLog()
        let outcome = try await runner.run(link("https://example.com/media/talk.mp4"), into: folder, options: DropOptions(), stage: { stages.add($0) })
        XCTAssertEqual(outcome.files.map(\.lastPathComponent), ["talk.mp4"])
        XCTAssertEqual(outcome.outputFormatID, "mp4")
        XCTAssertTrue(converter.conversions.isEmpty)
        XCTAssertEqual(try visibleFiles(), ["talk.mp4"], "no work folder left behind")
        XCTAssertTrue(stages.names.contains("inspecting"))
        XCTAssertEqual(stages.names.last, "saving")
    }

    func testConvertsAndNamesFromTheTemplate() async throws {
        serveMP4("https://www.example.com/media/talk.mp4")
        var options = DropOptions(outputFormatID: "mp3")
        options.nameTemplate = "{site} - {title} ({format})"
        let outcome = try await runner.run(link("https://www.example.com/media/talk.mp4"), into: folder, options: options)
        XCTAssertEqual(outcome.files.map(\.lastPathComponent), ["example.com - talk (MP3).mp3"])
        XCTAssertEqual(converter.conversions.first?.input, "example.com - talk (MP3).mp4")
        XCTAssertEqual(converter.conversions.first?.to, "mp3")
        XCTAssertEqual(try visibleFiles(), ["example.com - talk (MP3).mp3"])
    }

    func testKeepsTheSourceWhenAsked() async throws {
        serveMP4("https://example.com/a.mp4")
        let outcome = try await runner.run(link("https://example.com/a.mp4"), into: folder, options: DropOptions(outputFormatID: "mp3", keepSource: true))
        XCTAssertEqual(outcome.files.map(\.lastPathComponent).sorted(), ["a.mp3", "a.mp4"])
    }

    func testNeverOverwrites() async throws {
        try Data("mine".utf8).write(to: folder.appendingPathComponent("a.mp4"))
        serveMP4("https://example.com/a.mp4")
        let outcome = try await runner.run(link("https://example.com/a.mp4"), into: folder, options: DropOptions())
        XCTAssertEqual(outcome.files.map(\.lastPathComponent), ["a 2.mp4"])
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("a.mp4"), encoding: .utf8), "mine")
    }

    func testDurationLimit() async throws {
        converter.duration = 31 * 60 + 5
        serveMP4("https://example.com/long.mp4")
        do {
            _ = try await runner.run(link("https://example.com/long.mp4"), into: folder, options: DropOptions())
            XCTFail("over the limit")
        } catch let error as DropError {
            guard case .tooLong(let minutes, let limit) = error else { return XCTFail("\(error)") }
            XCTAssertEqual(minutes, 32)
            XCTAssertEqual(limit, 30)
            XCTAssertEqual(error.message(), "This file is 32 minutes long. The limit in Settings is 30 minutes.")
        }
        XCTAssertEqual(try visibleFiles(), [], "the download is deleted")

        let outcome = try await runner.run(link("https://example.com/long.mp4"), into: folder, options: DropOptions(maxDurationMinutes: nil))
        XCTAssertEqual(outcome.files.count, 1, "no limit when the user turns it off")
    }

    func testCopyProtectedMediaIsRefused() async throws {
        converter.copyProtected = true
        serveMP4("https://example.com/p.mp4")
        do {
            _ = try await runner.run(link("https://example.com/p.mp4"), into: folder, options: DropOptions(outputFormatID: "mp3"))
            XCTFail("protected")
        } catch DropError.conversion(let error) {
            XCTAssertEqual(error, .copyProtected)
        }
        XCTAssertEqual(try visibleFiles(), [])
    }

    func testFailedDownloadKeepsOnlyResumableWork() async throws {
        transport.on("https://example.com/r.mp4", .init(headers: ["Content-Type": "video/mp4", "Content-Length": "5000", "ETag": "\"r\""],
                                                         chunks: [DownloaderTests.mp4(5000).prefix(2000)], failAfterChunks: .network))
        var resume: ResumeState?
        do {
            _ = try await runner.run(link("https://example.com/r.mp4"), into: folder, options: DropOptions())
        } catch DropError.download(let error, let state) {
            XCTAssertEqual(error, .interrupted)
            resume = state
        }
        let state = try XCTUnwrap(resume)
        XCTAssertTrue(FileManager.default.fileExists(atPath: state.file.path))

        transport.on("https://example.com/404.mp4", .init(status: 404))
        do { _ = try await runner.run(link("https://example.com/404.mp4"), into: folder, options: DropOptions()) } catch {}
        XCTAssertEqual(try visibleFiles().filter { !$0.hasPrefix(".") }, [])
        XCTAssertEqual(try visibleFiles().count, 1, "only the resumable work folder")
    }

    func testEndToEndWithFFmpeg() async throws {
        let tools = try EngineTestSupport.tools()
        let samples = try EngineTestSupport.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: samples) }
        let wav = try await EngineTestSupport.tone(tools, in: samples, seconds: 2)
        transport.on("https://example.com/sounds/tone.wav", .init(headers: ["Content-Type": "audio/wav"], chunks: DownloaderTests.chunks(try Data(contentsOf: wav))))
        let real = DropJobRunner(downloader: Downloader(transport: transport, appVersion: "1.0.0"), converter: ConversionService(tools: tools))
        let outcome = try await real.run(link("https://example.com/sounds/tone.wav"), into: folder, options: DropOptions(outputFormatID: "mp3"))
        XCTAssertEqual(outcome.files.map(\.lastPathComponent), ["tone.mp3"])
        let info = try await EngineTestSupport.probe(outcome.files[0], tools)
        XCTAssertEqual(info.audioStreams.first?.codec, "mp3")
        XCTAssertEqual(try visibleFiles(), ["tone.mp3"])
    }
}

final class StageLog: @unchecked Sendable {
    private let lock = NSLock()
    private var stages: [DropStage] = []
    func add(_ s: DropStage) { lock.withLock { stages.append(s) } }
    var names: [String] {
        lock.withLock {
            stages.map {
                switch $0 {
                case .downloading: "downloading"
                case .inspecting: "inspecting"
                case .converting: "converting"
                case .saving: "saving"
                }
            }
        }
    }
}

final class DropHistoryTests: XCTestCase {
    func testAddClearAndLimit() throws {
        let dir = try EngineTestSupport.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let url = dir.appendingPathComponent("Potion Dropper/history.json")
        let history = DropHistory(url: url)
        try history.add(.init(link: "https://example.com/a.mp4", site: "example.com", name: "a.mp4", files: ["/x/a.mp4"], formatID: "mp4", bytes: 10))
        try history.add(.init(link: "https://example.com/b.mp4", site: "example.com", name: "b.mp4", error: "notFound"))
        XCTAssertEqual(DropHistory(url: url).entries.map(\.name), ["b.mp4", "a.mp4"], "newest first, saved")

        try history.remove(history.entries[0].id)
        XCTAssertEqual(DropHistory(url: url).entries.map(\.name), ["a.mp4"])

        for i in 0..<(DropHistory.maxEntries + 5) { try history.add(.init(link: "https://example.com/\(i).mp4", site: "example.com", name: "\(i)")) }
        XCTAssertEqual(history.entries.count, DropHistory.maxEntries)

        try history.clear()
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
        XCTAssertTrue(DropHistory(url: url).entries.isEmpty)
    }
}
