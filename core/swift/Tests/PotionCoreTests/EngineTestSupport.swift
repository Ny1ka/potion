import XCTest
@testable import PotionCore

/// Shared helpers for engine tests: finds the LGPL FFmpeg build and generates sample files with it.
/// Samples are generated per test run, so no media files are committed.
enum EngineTestSupport {
    /// The FFmpeg build from scripts/ffmpeg/ (or POTION_FFMPEG_DIR). Skips the test when it's missing,
    /// unless POTION_REQUIRE_FFMPEG=1 (set in CI), where a missing build is a failure.
    static func tools() throws -> FFmpegTools {
        let env = ProcessInfo.processInfo.environment
        let dir = env["POTION_FFMPEG_DIR"].map(URL.init(fileURLWithPath:)) ?? RepoFiles.url("build/ffmpeg/macos/bin")
        do {
            return try FFmpegTools(directory: dir)
        } catch {
            if env["POTION_REQUIRE_FFMPEG"] == "1" {
                XCTFail("FFmpeg build missing at \(dir.path). Run scripts/ffmpeg/build-macos.sh")
                throw error
            }
            throw XCTSkip("FFmpeg build missing at \(dir.path). Run scripts/ffmpeg/build-macos.sh to run engine tests.")
        }
    }

    static func makeTempDirectory() throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("potion-tests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    /// Runs the bundled ffmpeg to create a sample file.
    @discardableResult
    static func generate(_ tools: FFmpegTools, _ args: [String], output: URL) async throws -> URL {
        let result = try await ProcessRunner.run(tools.ffmpeg, ["-hide_banner", "-loglevel", "error", "-y"] + args + [output.path])
        XCTAssertEqual(result.status, 0, "sample generation failed: \(result.stderr)")
        return output
    }

    static func tone(_ tools: FFmpegTools, in dir: URL, name: String = "tone.wav", seconds: Double = 1,
                     extra: [String] = ["-c:a", "pcm_s16le"]) async throws -> URL {
        try await generate(tools, ["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=\(seconds)"] + extra,
                           output: dir.appendingPathComponent(name))
    }

    static func format(_ id: String) -> FileFormat { FormatRegistry.shared.format(id: id)! }

    static func probe(_ url: URL, _ tools: FFmpegTools) async throws -> MediaInfo {
        try await MediaProbe.probe(url, tools: tools)
    }
}
