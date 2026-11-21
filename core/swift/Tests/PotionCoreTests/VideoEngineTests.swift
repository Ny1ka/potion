import XCTest
@testable import PotionCore

/// End-to-end video conversions with the real LGPL FFmpeg build.
final class VideoEngineTests: XCTestCase {
    typealias S = EngineTestSupport
    var tools: FFmpegTools!
    var dir: URL!
    var engine: VideoEngine!

    override func setUp() async throws {
        tools = try S.tools()
        dir = try S.makeTempDirectory()
        engine = VideoEngine(tools: tools)
    }

    override func tearDown() async throws {
        if let dir { try? FileManager.default.removeItem(at: dir) }
    }

    /// 640×480, 30 fps, 2 s, MPEG-4 video with PCM audio.
    func makeClip(name: String = "clip.mov", size: String = "640x480", rate: Int = 30) async throws -> URL {
        try await S.generate(tools, [
            "-f", "lavfi", "-i", "testsrc=size=\(size):rate=\(rate):duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-c:v", "mpeg4", "-q:v", "4", "-c:a", "pcm_s16le", "-shortest",
        ], output: dir.appendingPathComponent(name))
    }

    func outputDirectory() throws -> URL {
        let out = dir.appendingPathComponent("out-\(UUID().uuidString.prefix(6))")
        try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
        return out
    }

    func testMovToEveryVideoFormat() async throws {
        let clip = try await makeClip()
        let expected = ["mp4": "h264", "mov": "h264", "m4v": "h264", "mkv": "h264", "webm": "vp9", "avi": "mpeg4",
                        "wmv": "wmv2", "flv": "h264", "mpeg": "mpeg2video", "3gp": "h264", "ts": "h264"]
        let out = try outputDirectory()
        for (id, codec) in expected.sorted(by: { $0.key < $1.key }) {
            let saved = try await engine.convert(clip, from: S.format("mov"), to: S.format(id), outputDirectory: out)
            let info = try await S.probe(saved, tools)
            XCTAssertEqual(info.videoStreams.first?.codec, codec, id)
            XCTAssertEqual(info.audioStreams.count, 1, "\(id) keeps the audio")
            XCTAssertEqual(info.duration ?? 0, 2, accuracy: 0.5, id)
            XCTAssertEqual(try FormatRegistry.shared.detect(fileAt: saved).format?.id, id, "\(id) is detected as itself")
        }
    }

    func testOriginalQualityCopiesStreamsWhenTheyFit() async throws {
        let clip = try await makeClip()
        let mp4 = try await engine.convert(clip, from: S.format("mov"), to: S.format("mp4"), outputDirectory: dir)
        let mp4Info = try await S.probe(mp4, tools)
        let args = try engine.arguments(input: mp4, info: mp4Info, output: dir.appendingPathComponent("x.mkv"),
                                        outputFormat: S.format("mkv"), options: VideoOptions(presetID: "original"))
        let joined = args.joined(separator: " ")
        XCTAssertTrue(joined.contains("-c:v copy"), "H.264 fits in MKV without re-encoding")
        XCTAssertTrue(joined.contains("-c:a copy"), "AAC fits in MKV without re-encoding")

        let mkv = try await engine.convert(mp4, from: S.format("mp4"), to: S.format("mkv"), outputDirectory: dir,
                                           options: VideoOptions(presetID: "original"))
        let mkvInfo = try await S.probe(mkv, tools)
        XCTAssertEqual(mkvInfo.videoStreams.first?.codec, "h264")
        XCTAssertEqual(mkvInfo.videoStreams.first?.width, 640)
    }

    func testReencodesAudioOnlyWhenTheContainerNeedsIt() async throws {
        let clip = try await makeClip()   // MPEG-4 video + PCM audio
        let info = try await S.probe(clip, tools)
        let args = try engine.arguments(input: clip, info: info, output: dir.appendingPathComponent("x.avi"),
                                        outputFormat: S.format("avi"), options: VideoOptions(presetID: "original"))
        let joined = args.joined(separator: " ")
        XCTAssertTrue(joined.contains("-c:v copy"), "MPEG-4 video fits in AVI")
        XCTAssertTrue(joined.contains("-c:a pcm_s16le") == false && joined.contains("-c:a copy"), "PCM fits in AVI")
    }

    func testResolutionNeverUpscales() async throws {
        let clip = try await makeClip()
        let smaller = try await engine.convert(clip, from: S.format("mov"), to: S.format("mp4"), outputDirectory: dir,
                                               options: VideoOptions(maxHeight: 360))
        let small = try await S.probe(smaller, tools).videoStreams.first
        XCTAssertEqual(small?.height, 360)
        XCTAssertEqual(small?.width, 480)

        let info = try await S.probe(clip, tools)
        let args = try engine.arguments(input: clip, info: info, output: dir.appendingPathComponent("x.mp4"),
                                        outputFormat: S.format("mp4"), options: VideoOptions(maxHeight: 1080))
        XCTAssertFalse(args.contains("-vf"), "a 480p source isn't scaled up to 1080p")
    }

    func testAspectRatioLetterboxes() async throws {
        let clip = try await makeClip()
        let square = try await engine.convert(clip, from: S.format("mov"), to: S.format("mp4"), outputDirectory: dir,
                                              options: VideoOptions(aspectRatio: "1:1"))
        let v = try await S.probe(square, tools).videoStreams.first
        XCTAssertEqual(v?.width, 480)
        XCTAssertEqual(v?.height, 480)
    }

    func testLowersFrameRate() async throws {
        let clip = try await makeClip()
        let saved = try await engine.convert(clip, from: S.format("mov"), to: S.format("mp4"), outputDirectory: dir,
                                             options: VideoOptions(frameRate: 24))
        let fps = try await S.probe(saved, tools).videoStreams.first?.frameRate ?? 0
        XCTAssertEqual(fps, 24, accuracy: 0.1)
        XCTAssertNil(engine.targetFrameRate(source: 24, requested: 60, standardOnly: false), "never raises the rate")
        XCTAssertEqual(engine.targetFrameRate(source: 15, requested: nil, standardOnly: true)?.text, "24000/1001",
                       "MPEG-2 snaps to a broadcast rate")
    }

    func testHEVCInMP4IsTaggedForAppleApps() async throws {
        let clip = try await makeClip()
        let saved = try await engine.convert(clip, from: S.format("mov"), to: S.format("mp4"), outputDirectory: dir,
                                             options: VideoOptions(codec: "hevc"))
        let v = try await S.probe(saved, tools).videoStreams.first
        XCTAssertEqual(v?.codec, "hevc")
        XCTAssertEqual(v?.codecTag, "hvc1")
    }

    func testVideoToGIF() async throws {
        let clip = try await makeClip()
        let gif = try await engine.convert(clip, from: S.format("mov"), to: S.format("gif"), outputDirectory: dir)
        let info = try await S.probe(gif, tools)
        XCTAssertEqual(info.streams.first?.codec, "gif")
        XCTAssertEqual(info.streams.first?.width, 480, "Standard GIFs are at most 480 px wide")
        XCTAssertTrue(info.audioStreams.isEmpty)
        XCTAssertEqual(try FormatRegistry.shared.detect(fileAt: gif).format?.id, "gif")
    }

    func testGIFToVideoWithOddSize() async throws {
        let gif = try await S.generate(tools, ["-f", "lavfi", "-i", "testsrc=size=33x33:rate=10:duration=1"],
                                       output: dir.appendingPathComponent("anim.gif"))
        let mp4 = try await engine.convert(gif, from: S.format("gif"), to: S.format("mp4"), outputDirectory: dir)
        let info = try await S.probe(mp4, tools)
        XCTAssertEqual(info.videoStreams.first?.codec, "h264")
        XCTAssertEqual(info.videoStreams.first?.width, 32, "odd sizes become even for the encoder")
        XCTAssertTrue(info.audioStreams.isEmpty)
    }

    func testAudioOnlyFileIsRejected() async throws {
        let wav = try await S.tone(tools, in: dir)
        do {
            _ = try await engine.convert(wav, from: S.format("wav"), to: S.format("mp4"), outputDirectory: dir)
            XCTFail("expected unsupported")
        } catch let error as ConversionError {
            XCTAssertEqual(error, .unsupported(from: "WAV", to: "MP4"))
        }
    }

    func testBitrateScalesWithPresetAndCodec() throws {
        let stream = MediaInfo.Stream(index: 0, kind: .video, codec: "h264", codecTag: "", sampleRate: nil, channels: nil,
                                      bitsPerSample: nil, bitRate: 8_000_000, width: 1920, height: 1080, frameRate: 30,
                                      pixelFormat: "yuv420p", isAttachedPicture: false, duration: 10)
        let g = VideoEngine.Geometry(source: stream, maxHeight: nil, aspectRatio: nil)
        let p = VideoPresets.shared
        let standard = engine.bitrateKbps(geometry: g, fps: 30, preset: p.preset(id: "standard"), source: stream, efficiency: 1)
        let small = engine.bitrateKbps(geometry: g, fps: 30, preset: p.preset(id: "small"), source: stream, efficiency: 1)
        let hevc = engine.bitrateKbps(geometry: g, fps: 30, preset: p.preset(id: "standard"), source: stream, efficiency: 0.6)
        let original = engine.bitrateKbps(geometry: g, fps: 30, preset: p.preset(id: "original"), source: stream, efficiency: 1)
        XCTAssertEqual(standard, 4355)
        XCTAssertLessThan(small, standard)
        XCTAssertLessThan(hevc, standard)
        XCTAssertEqual(original, 8000, "Original quality matches the source bitrate")
    }

    func testPortraitPhoneVideoUsesDisplaySize() async throws {
        let clip = try await makeClip()
        let rotated = try await S.generate(tools, ["-display_rotation", "90", "-i", clip.path, "-c", "copy"],
                                           output: dir.appendingPathComponent("portrait.mov"))
        let v = try await S.probe(rotated, tools).videoStreams.first
        XCTAssertEqual(v?.width, 480, "displayed sideways: 480 wide")
        XCTAssertEqual(v?.height, 640)
        let saved = try await engine.convert(rotated, from: S.format("mov"), to: S.format("mp4"), outputDirectory: dir,
                                             options: VideoOptions(maxHeight: 360))
        let out = try await S.probe(saved, tools).videoStreams.first
        XCTAssertEqual(out?.height, 360)
        XCTAssertEqual(out?.width, 270, "portrait stays portrait")
    }

    func testPresetsCoverEveryVideoOutput() {
        let outputs = ConversionMatrix.shared.outputs(for: "mov").filter { $0.operation == .transcodeVideo }.map(\.output.id)
        XCTAssertEqual(Set(outputs).subtracting(VideoPresets.shared.formats.keys), [])
    }
}
