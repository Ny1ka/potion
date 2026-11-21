import XCTest
@testable import PotionCore

/// End-to-end audio conversions with the real LGPL FFmpeg build.
final class AudioEngineTests: XCTestCase {
    typealias S = EngineTestSupport
    var tools: FFmpegTools!
    var dir: URL!
    var engine: AudioEngine!

    override func setUp() async throws {
        tools = try S.tools()
        dir = try S.makeTempDirectory()
        engine = AudioEngine(tools: tools)
    }

    override func tearDown() async throws {
        if let dir { try? FileManager.default.removeItem(at: dir) }
    }

    func testWavToEveryAudioFormat() async throws {
        let wav = try await S.tone(tools, in: dir)
        let expectedCodec = ["mp3": "mp3", "m4a": "aac", "aac": "aac", "ogg": "vorbis", "opus": "opus", "wma": "wmav2",
                             "wav": "pcm_s16le", "aiff": "pcm_s16be", "flac": "flac", "alac": "alac"]
        let outDir = dir.appendingPathComponent("out")
        try FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
        for (id, codec) in expectedCodec.sorted(by: { $0.key < $1.key }) {
            let saved = try await engine.convert(wav, from: S.format("wav"), to: S.format(id), outputDirectory: outDir)
            let info = try await S.probe(saved, tools)
            XCTAssertEqual(info.audioStreams.first?.codec, codec, id)
            XCTAssertEqual(saved.pathExtension, S.format(id).preferredExtension, id)
            XCTAssertEqual(info.duration ?? 0, 1, accuracy: 0.15, id)
            let detected = try FormatRegistry.shared.detect(fileAt: saved)
            XCTAssertEqual(detected.format?.id, id == "alac" ? "alac" : id, "\(id): saved file is detected as its format")
        }
        XCTAssertTrue(FileManager.default.fileExists(atPath: wav.path), "the original is untouched")
    }

    func testNeverOverwritesAnExistingFile() async throws {
        let wav = try await S.tone(tools, in: dir)
        let existing = dir.appendingPathComponent("tone.mp3")
        try Data("keep me".utf8).write(to: existing)
        let saved = try await engine.convert(wav, from: S.format("wav"), to: S.format("mp3"), outputDirectory: dir)
        XCTAssertEqual(saved.lastPathComponent, "tone 2.mp3")
        XCTAssertEqual(try String(contentsOf: existing), "keep me")
    }

    func testPresetBitrateAndClamping() async throws {
        let wav = try await S.tone(tools, in: dir, seconds: 3)
        let mp3 = try await engine.convert(wav, from: S.format("wav"), to: S.format("mp3"), outputDirectory: dir,
                                           options: AudioOptions(presetID: "high"))
        let mp3Rate = try await S.probe(mp3, tools).audioStreams.first?.bitRate ?? 0
        XCTAssertEqual(Double(mp3Rate), 320_000, accuracy: 1_000)

        let info = try await S.probe(wav, tools)
        let args = try engine.arguments(input: wav, info: info, output: dir.appendingPathComponent("x.opus"),
                                        outputFormat: S.format("opus"), operation: .transcodeAudio,
                                        options: AudioOptions(presetID: "high"))
        XCTAssertTrue(args.joined(separator: " ").contains("-b:a 256k"), "Opus tops out at 256 kbps")
        XCTAssertTrue(args.joined(separator: " ").contains("-ar 48000"), "Opus output is always 48 kHz")
    }

    func testKeeps24BitAudio() async throws {
        let wav24 = try await S.tone(tools, in: dir, name: "hi.wav", extra: ["-c:a", "pcm_s24le"])
        let flac = try await engine.convert(wav24, from: S.format("wav"), to: S.format("flac"), outputDirectory: dir)
        let flacInfo = try await S.probe(flac, tools)
        XCTAssertEqual(flacInfo.audioStreams.first?.bitsPerSample, 24)
        let aiff = try await engine.convert(wav24, from: S.format("wav"), to: S.format("aiff"), outputDirectory: dir)
        let aiffInfo = try await S.probe(aiff, tools)
        XCTAssertEqual(aiffInfo.audioStreams.first?.codec, "pcm_s24be")
    }

    func testExtractsAudioFromVideo() async throws {
        let video = try await S.generate(tools, [
            "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10:duration=1",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-c:v", "mpeg4", "-c:a", "pcm_s16le", "-shortest",
        ], output: dir.appendingPathComponent("clip.mov"))
        let mp3 = try await engine.convert(video, from: S.format("mov"), to: S.format("mp3"), outputDirectory: dir)
        let info = try await S.probe(mp3, tools)
        XCTAssertEqual(info.audioStreams.count, 1)
        XCTAssertTrue(info.videoStreams.isEmpty)
        XCTAssertNil(info.attachedPicture, "video frames never become cover art")
    }

    func testKeepsCoverArtWhereSupported() async throws {
        let wav = try await S.tone(tools, in: dir)
        let cover = try await S.generate(tools, ["-f", "lavfi", "-i", "color=c=red:size=32x32", "-frames:v", "1"],
                                         output: dir.appendingPathComponent("cover.png"))
        let tagged = try await S.generate(tools, [
            "-i", wav.path, "-i", cover.path, "-map", "0", "-map", "1", "-c:a", "libmp3lame", "-c:v", "png",
            "-disposition:v:0", "attached_pic", "-metadata", "title=Test Tone",
        ], output: dir.appendingPathComponent("tagged.mp3"))

        let m4a = try await engine.convert(tagged, from: S.format("mp3"), to: S.format("m4a"), outputDirectory: dir)
        let m4aInfo = try await S.probe(m4a, tools)
        XCTAssertNotNil(m4aInfo.attachedPicture, "M4A keeps artwork")
        let ogg = try await engine.convert(tagged, from: S.format("mp3"), to: S.format("ogg"), outputDirectory: dir)
        let oggInfo = try await S.probe(ogg, tools)
        XCTAssertNil(oggInfo.attachedPicture, "OGG output drops artwork")
        XCTAssertEqual(oggInfo.audioStreams.count, 1)
    }

    func testRejectsConversionsTheMatrixDoesNotAllow() async throws {
        let wav = try await S.tone(tools, in: dir)
        do {
            _ = try await engine.convert(wav, from: S.format("wav"), to: S.format("png"), outputDirectory: dir)
            XCTFail("expected unsupported")
        } catch let error as ConversionError {
            XCTAssertEqual(error, .unsupported(from: "WAV", to: "PNG"))
            XCTAssertEqual(error.localizedDescription, "WAV can't be converted to PNG.")
        }
    }

    func testUnreadableFile() async throws {
        let junk = dir.appendingPathComponent("broken.mp3")
        try Data((0..<4096).map { UInt8($0 % 251) }).write(to: junk)
        do {
            _ = try await engine.convert(junk, from: S.format("mp3"), to: S.format("wav"), outputDirectory: dir)
            XCTFail("expected unreadable")
        } catch let error as ConversionError {
            XCTAssertEqual(error, .unreadable(fileName: "broken.mp3"))
        }
        XCTAssertEqual(try leftoverTemporaryFiles(), [])
    }

    func testVideoWithoutAudio() async throws {
        let silent = try await S.generate(tools, ["-f", "lavfi", "-i", "testsrc=size=64x48:rate=10:duration=1", "-c:v", "mpeg4"],
                                          output: dir.appendingPathComponent("silent.mov"))
        do {
            _ = try await engine.convert(silent, from: S.format("mov"), to: S.format("mp3"), outputDirectory: dir)
            XCTFail("expected noAudio")
        } catch let error as ConversionError {
            XCTAssertEqual(error, .noAudio(fileName: "silent.mov"))
        }
    }

    func testProgressReachesOneAndNeverGoesBack() async throws {
        let wav = try await S.tone(tools, in: dir, seconds: 5)
        let recorder = ProgressRecorder()
        _ = try await engine.convert(wav, from: S.format("wav"), to: S.format("flac"), outputDirectory: dir) { recorder.add($0) }
        let values = recorder.values
        XCTAssertEqual(values.last, 1)
        XCTAssertEqual(values, values.sorted(), "progress is monotonic")
    }

    func testCancellationStopsFFmpegAndCleansUp() async throws {
        let wav = try await S.tone(tools, in: dir, seconds: 600)   // long enough to still be running
        let outDir = dir.appendingPathComponent("out")
        try FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
        let engine = self.engine!
        let task = Task {
            try await engine.convert(wav, from: S.format("wav"), to: S.format("opus"), outputDirectory: outDir)
        }
        try await Task.sleep(nanoseconds: 300_000_000)
        task.cancel()
        do {
            _ = try await task.value
            XCTFail("expected cancellation")
        } catch is CancellationError {}
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: outDir.path), [], "no partial output left")
    }

    private func leftoverTemporaryFiles() throws -> [String] {
        try FileManager.default.contentsOfDirectory(atPath: dir.path).filter { $0.hasPrefix(".potion-") }
    }
}

/// Collects progress values from a @Sendable callback.
final class ProgressRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [Double] = []
    func add(_ v: Double) { lock.lock(); storage.append(v); lock.unlock() }
    var values: [Double] { lock.lock(); defer { lock.unlock() }; return storage }
}
