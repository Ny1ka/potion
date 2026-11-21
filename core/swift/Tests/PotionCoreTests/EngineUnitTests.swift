import XCTest
@testable import PotionCore

/// Engine logic that doesn't need FFmpeg.
final class EngineUnitTests: XCTestCase {
    func testBundledPresetsMatchSharedSpec() throws {
        let bundled = try Data(contentsOf: XCTUnwrap(Bundle.module.url(forResource: "presets", withExtension: "json")))
        XCTAssertEqual(bundled, try RepoFiles.data("core/spec/presets.json"), "Run: make sync-spec")
    }

    func testEveryAudioOutputHasSettings() {
        let outputs = Set(ConversionMatrix.shared.outputs(for: "wav").map(\.output.id))
        XCTAssertEqual(outputs.subtracting(AudioPresets.shared.formats.keys), [])
        for (id, settings) in AudioPresets.shared.formats {
            XCTAssertNotNil(settings.encoder(platform: "macos"), "\(id) has a macOS encoder")
            XCTAssertNotNil(settings.encoder(platform: "windows"), "\(id) has a Windows encoder")
            XCTAssertTrue(settings.isLossless || settings.bitrates?.isEmpty == false, "\(id) lossy formats list bitrates")
        }
    }

    func testNearestValue() {
        XCTAssertEqual(AudioPresets.nearest(320, in: [64, 96, 128, 256]), 256)
        XCTAssertEqual(AudioPresets.nearest(112, in: [96, 128]), 96, "ties go to the lower value")
        XCTAssertEqual(AudioPresets.nearest(44100, in: [48000]), 48000)
    }

    func testPresetFallsBackToDefault() {
        XCTAssertEqual(AudioPresets.shared.preset(id: nil).id, "standard")
        XCTAssertEqual(AudioPresets.shared.preset(id: "nope").bitrateKbps, 192)
    }

    func testProgressParsing() {
        XCTAssertEqual(FFmpegConversion.progressFraction("out_time_us=2500000", duration: 10), 0.25)
        XCTAssertEqual(FFmpegConversion.progressFraction("out_time_us=N/A", duration: 10), nil)
        XCTAssertEqual(FFmpegConversion.progressFraction("out_time_us=99000000", duration: 10), 1)
        XCTAssertEqual(FFmpegConversion.progressFraction("progress=end", duration: nil), 1)
        XCTAssertNil(FFmpegConversion.progressFraction("bitrate=128kbits/s", duration: 10))
    }

    func testUniqueNamesFollowFinderStyle() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("potion-names-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        XCTAssertEqual(OutputFiles.uniqueURL(in: dir, baseName: "Song", fileExtension: "mp3").lastPathComponent, "Song.mp3")
        FileManager.default.createFile(atPath: dir.appendingPathComponent("Song.mp3").path, contents: Data())
        FileManager.default.createFile(atPath: dir.appendingPathComponent("Song 2.mp3").path, contents: Data())
        XCTAssertEqual(OutputFiles.uniqueURL(in: dir, baseName: "Song", fileExtension: "mp3").lastPathComponent, "Song 3.mp3")
    }

    func testCopyProtectionDetection() throws {
        let json = #"{"format":{"format_name":"mov,mp4,m4a"},"streams":[{"index":0,"codec_type":"audio","codec_name":"aac","codec_tag_string":"drms"}]}"#
        XCTAssertTrue(try MediaProbe.parse(Data(json.utf8)).isCopyProtected)
        XCTAssertTrue(MediaProbe.looksCopyProtected("Error: stream is DRM protected"))
        XCTAssertFalse(MediaProbe.looksCopyProtected("Invalid data found when processing input"))
    }

    func testErrorMessagesFollowVoiceRules() {
        let messages = [ConversionError.toolsMissing, .unsupported(from: "MP3", to: "PNG"), .unreadable(fileName: "clip.mkv"),
                        .copyProtected, .noAudio(fileName: "a.mov"), .cannotWrite(folder: "Out"),
                        .conversionFailed(fileName: "a.wav", detail: "x"), .verificationFailed(fileName: "a.wav")]
            .compactMap(\.errorDescription)
        XCTAssertEqual(messages.count, 8)
        for m in messages { XCTAssertFalse(m.contains("!"), "no exclamation marks: \(m)") }
        XCTAssertEqual(ConversionError.unreadable(fileName: "clip.mkv").errorDescription, "Can't read “clip.mkv”. The file may be damaged.")
    }

    func testLocateUsesEnvironmentThenBundle() {
        XCTAssertThrowsError(try FFmpegTools.locate(environment: ["POTION_FFMPEG_DIR": "/nonexistent"]))
        XCTAssertThrowsError(try FFmpegTools.locate(environment: [:], bundle: Bundle(for: EngineUnitTests.self)))
    }
}
