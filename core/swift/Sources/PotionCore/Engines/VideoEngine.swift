import Foundation

/// Settings for one video conversion. Unset values keep the source or come from the default preset.
public struct VideoOptions: Sendable, Equatable {
    /// "original", "high", "standard" or "small". "original" copies streams when nothing else changes.
    public var presetID: String?
    /// Maximum output height (2160, 1080, 720…). Never upscales.
    public var maxHeight: Int?
    /// Output frame rate. Only lowers the source rate.
    public var frameRate: Double?
    /// One of the container's `videoCodecs` (e.g. "hevc" for MP4). Nil uses the container's default.
    public var codec: String?
    /// Overrides the preset's computed bitrate.
    public var bitrateKbps: Int?
    /// "16:9", "9:16", "1:1" or "4:3". The picture is fitted and letterboxed, never stretched.
    public var aspectRatio: String?

    public init(presetID: String? = nil, maxHeight: Int? = nil, frameRate: Double? = nil, codec: String? = nil,
                bitrateKbps: Int? = nil, aspectRatio: String? = nil) {
        self.presetID = presetID
        self.maxHeight = maxHeight
        self.frameRate = frameRate
        self.codec = codec
        self.bitrateKbps = bitrateKbps
        self.aspectRatio = aspectRatio
    }
}

/// Converts between video formats, turns video into animated GIFs and GIFs into video, using the bundled
/// FFmpeg. Handles the matrix operations `transcodeVideo`, `videoToGif` and `gifToVideo`.
public final class VideoEngine: Sendable {
    public let tools: FFmpegTools
    let presets: VideoPresets
    let matrix: ConversionMatrix

    public init(tools: FFmpegTools, presets: VideoPresets = .shared, matrix: ConversionMatrix = .shared) {
        self.tools = tools
        self.presets = presets
        self.matrix = matrix
    }

    /// Converts `input` (identified as `inputFormat`) to `outputFormat`, saving into `outputDirectory` under the
    /// input's name with the new extension (never replacing an existing file).
    public func convert(_ input: URL, from inputFormat: FileFormat, to outputFormat: FileFormat,
                        outputDirectory: URL, options: VideoOptions = VideoOptions(),
                        progress: @escaping @Sendable (Double) -> Void = { _ in }) async throws -> URL {
        guard let conversion = matrix.conversion(from: inputFormat.id, to: outputFormat.id),
              [.transcodeVideo, .videoToGif, .gifToVideo].contains(conversion.operation) else {
            throw ConversionError.unsupported(from: inputFormat.name, to: outputFormat.name)
        }
        try OutputFiles.checkWritable(outputDirectory)

        let info = try await MediaProbe.probe(input, tools: tools)
        if info.isCopyProtected { throw ConversionError.copyProtected }
        guard let video = info.videoStreams.first else { throw ConversionError.noVideo(fileName: input.lastPathComponent) }
        let keepsAudio = conversion.operation == .transcodeVideo && !info.audioStreams.isEmpty

        return try await FFmpegConversion.run(
            tools: tools, input: input, outputDirectory: outputDirectory,
            outputExtension: outputFormat.preferredExtension, duration: info.duration ?? video.duration,
            progress: progress,
            arguments: { temporary in
                conversion.operation == .videoToGif
                    ? try gifArguments(input: input, info: info, output: temporary, options: options)
                    : try arguments(input: input, info: info, output: temporary, outputFormat: outputFormat, options: options)
            },
            verify: { out in
                guard let v = out.videoStreams.first ?? out.streams.first(where: { $0.kind == .video }) else { return false }
                if keepsAudio && out.audioStreams.isEmpty { return false }
                return FFmpegConversion.hasDuration(out, stream: v) || outputFormat.id == "gif"
            })
    }

    // MARK: - Video → video (also GIF → video)

    func arguments(input: URL, info: MediaInfo, output: URL, outputFormat: FileFormat,
                   options: VideoOptions) throws -> [String] {
        guard let container = presets.formats[outputFormat.id] else {
            throw ConversionError.unsupported(from: "Video", to: outputFormat.name)
        }
        guard let video = info.videoStreams.first else { throw ConversionError.noVideo(fileName: input.lastPathComponent) }
        let audio = info.audioStreams.first
        let preset = presets.preset(id: options.presetID)
        let codecID = options.codec.flatMap { container.videoCodecs.contains($0) ? $0 : nil } ?? container.videoCodecs[0]
        guard let codec = presets.codecs[codecID], let encoder = codec.encoder() else {
            throw ConversionError.unsupported(from: "Video", to: outputFormat.name)
        }

        let geometry = Geometry(source: video, maxHeight: options.maxHeight, aspectRatio: options.aspectRatio)
        let fps = targetFrameRate(source: video.frameRate, requested: options.frameRate,
                                  standardOnly: codec.standardFrameRates == true)
        let copyVideo = preset.id == "original" && !geometry.changesPicture && fps == nil
            && container.canCopyVideo(video.codec) && (options.codec == nil || options.codec == video.codec)
            && video.codec != "gif"

        var args = FFmpegConversion.commonArguments(input: input) + ["-map", "0:\(video.index)"]
        if let audio { args += ["-map", "0:\(audio.index)"] }
        args += ["-sn", "-dn", "-map_metadata", "0"]

        if copyVideo {
            args += ["-c:v", "copy"]
            if video.codec == "hevc", let tag = presets.codecs["hevc"]?.tag, ["mp4", "mov", "m4v"].contains(outputFormat.id) {
                args += ["-tag:v", tag]
            }
        } else {
            var filters: [String] = []
            if let fps { filters.append("fps=\(fps.text)") }
            if let scale = geometry.filter { filters.append(scale) }
            if !filters.isEmpty { args += ["-vf", filters.joined(separator: ",")] }
            args += ["-c:v", encoder, "-pix_fmt", "yuv420p"]
            if encoder.hasSuffix("_videotoolbox") { args += ["-allow_sw", "1"] }   // software fallback without a hardware encoder
            if encoder == "libvpx-vp9" { args += ["-row-mt", "1", "-deadline", "good", "-cpu-used", "4"] }
            if let tag = codec.tag, ["mp4", "mov", "m4v"].contains(outputFormat.id) { args += ["-tag:v", tag] }
            let kbps = options.bitrateKbps ?? bitrateKbps(geometry: geometry, fps: fps?.value ?? video.frameRate ?? 30,
                                                          preset: preset, source: video, efficiency: codec.efficiency)
            args += ["-b:v", "\(kbps)k", "-maxrate", "\(kbps * 3 / 2)k", "-bufsize", "\(kbps * 2)k"]
        }

        if let audio {
            if container.canCopyAudio(audio.codec) {
                args += ["-c:a", "copy"]
            } else if let audioCodec = presets.audioCodecs[container.audioCodec], let audioEncoder = audioCodec.encoder() {
                args += ["-c:a", audioEncoder, "-b:a", "\(audioCodec.bitrateKbps)k"]
                if container.audioCodec == "opus" { args += ["-ar", "48000"] }
                if container.audioCodec == "mp2" || container.audioCodec == "aac" {
                    // These encoders top out at 48 kHz.
                    if (audio.sampleRate ?? 48000) > 48000 { args += ["-ar", "48000"] }
                }
            }
        }
        if container.faststart == true { args += ["-movflags", "+faststart"] }
        args += ["-f", container.muxer, output.path]
        return args
    }

    /// The output picture size and the scale/pad filter that produces it.
    struct Geometry {
        let width: Int
        let height: Int
        let filter: String?
        var changesPicture: Bool { filter != nil }

        init(source: MediaInfo.Stream, maxHeight: Int?, aspectRatio: String?) {
            let srcW = source.width ?? 0, srcH = source.height ?? 0
            let outH = Self.even(min(maxHeight ?? srcH, srcH))
            if let ratio = aspectRatio.flatMap(Self.ratio) {
                let outW = Self.even(Int((Double(outH) * ratio).rounded()))
                width = outW
                height = outH
                filter = "scale=\(outW):\(outH):force_original_aspect_ratio=decrease,"
                    + "pad=\(outW):\(outH):(ow-iw)/2:(oh-ih)/2,setsar=1"
            } else if outH != srcH {
                height = outH
                width = srcH > 0 ? Self.even(srcW * outH / srcH) : srcW
                filter = "scale=-2:\(outH)"
            } else if srcW % 2 != 0 || srcH % 2 != 0 {
                width = Self.even(srcW)
                height = Self.even(srcH)
                filter = "scale=trunc(iw/2)*2:trunc(ih/2)*2"   // most encoders need even dimensions
            } else {
                width = srcW
                height = srcH
                filter = nil
            }
        }

        static func even(_ n: Int) -> Int { max(2, n - n % 2) }

        static func ratio(_ text: String) -> Double? {
            let parts = text.split(separator: ":").compactMap { Double($0) }
            guard parts.count == 2, parts[0] > 0, parts[1] > 0 else { return nil }
            return parts[0] / parts[1]
        }
    }

    /// Frame rate to force, or nil to keep the source rate. Requests only lower the rate; MPEG-2 snaps to broadcast rates.
    func targetFrameRate(source: Double?, requested: Double?, standardOnly: Bool) -> (value: Double, text: String)? {
        var target: Double? = nil
        if let requested, requested > 0, requested < (source ?? .infinity) - 0.01 { target = requested }
        if standardOnly {
            let wanted = target ?? source ?? 25
            let snapped = VideoPresets.standardRates.min { abs($0.value - wanted) < abs($1.value - wanted) }!
            if let source, abs(snapped.value - source) < 0.01, target == nil { return nil }
            return snapped
        }
        guard let target else { return nil }
        let text = target.rounded() == target ? String(Int(target)) : String(format: "%.3f", target)
        return (target, text)
    }

    /// Bitrate from picture size × frame rate × bits per pixel, adjusted for the codec. "Original quality" uses the
    /// source bitrate when known.
    func bitrateKbps(geometry: Geometry, fps: Double, preset: VideoPresets.Preset, source: MediaInfo.Stream,
                     efficiency: Double) -> Int {
        let raw: Double
        if let bpp = preset.bitsPerPixel {
            raw = Double(geometry.width * geometry.height) * fps * bpp * efficiency / 1000
        } else if let sourceBitRate = source.bitRate, sourceBitRate > 0 {
            raw = Double(sourceBitRate) / 1000 * max(efficiency, 1)
        } else {
            raw = Double(geometry.width * geometry.height) * fps * 0.12 * efficiency / 1000
        }
        return min(max(Int(raw.rounded()), presets.minBitrateKbps), presets.maxBitrateKbps)
    }

    // MARK: - Video → GIF

    func gifArguments(input: URL, info: MediaInfo, output: URL, options: VideoOptions) throws -> [String] {
        guard let video = info.videoStreams.first else { throw ConversionError.noVideo(fileName: input.lastPathComponent) }
        let settings = presets.gif[options.presetID ?? presets.defaultPresetID] ?? presets.gif[presets.defaultPresetID]!
        let width = min(video.width ?? settings.maxWidth, settings.maxWidth)
        let fps = min(video.frameRate ?? settings.fps, settings.fps)
        let fpsText = fps.rounded() == fps ? String(Int(fps)) : String(format: "%.3f", fps)
        // Two passes in one graph: build an optimal 256-color palette, then map frames onto it.
        let graph = "fps=\(fpsText),scale=\(width):-1:flags=lanczos,split[a][b];"
            + "[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5"
        return FFmpegConversion.commonArguments(input: input)
            + ["-map", "0:\(video.index)", "-an", "-sn", "-dn", "-vf", graph, "-loop", "0", "-f", "gif", output.path]
    }
}
