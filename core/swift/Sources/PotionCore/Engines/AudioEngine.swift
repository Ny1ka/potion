import Foundation

/// Settings for one audio conversion. Unset values come from the default preset.
public struct AudioOptions: Sendable, Equatable {
    /// One of the presets in presets.json ("high", "standard", "small").
    public var presetID: String?
    /// Overrides the preset. Snapped to the nearest bitrate the output format allows.
    public var bitrateKbps: Int?
    /// Output sample rate. Snapped to the nearest rate the format allows. Nil keeps the source rate when allowed.
    public var sampleRate: Int?

    public init(presetID: String? = nil, bitrateKbps: Int? = nil, sampleRate: Int? = nil) {
        self.presetID = presetID
        self.bitrateKbps = bitrateKbps
        self.sampleRate = sampleRate
    }
}

/// Converts audio files and extracts audio from video, using the bundled FFmpeg.
/// Handles the matrix operations `transcodeAudio` and `extractAudio`.
public final class AudioEngine: Sendable {
    public let tools: FFmpegTools
    let presets: AudioPresets
    let matrix: ConversionMatrix

    public init(tools: FFmpegTools, presets: AudioPresets = .shared, matrix: ConversionMatrix = .shared) {
        self.tools = tools
        self.presets = presets
        self.matrix = matrix
    }

    /// Converts `input` (already identified as `inputFormat`) to `outputFormat`, saving into `outputDirectory`
    /// under the input's name with the new extension (never replacing an existing file).
    /// `progress` receives values from 0 to 1. Cancelling the task stops FFmpeg and removes partial output.
    /// - Returns: The saved file.
    public func convert(_ input: URL, from inputFormat: FileFormat, to outputFormat: FileFormat,
                        outputDirectory: URL, options: AudioOptions = AudioOptions(),
                        progress: @escaping @Sendable (Double) -> Void = { _ in }) async throws -> URL {
        guard let conversion = matrix.conversion(from: inputFormat.id, to: outputFormat.id),
              [.transcodeAudio, .extractAudio].contains(conversion.operation) else {
            throw ConversionError.unsupported(from: inputFormat.name, to: outputFormat.name)
        }
        try OutputFiles.checkWritable(outputDirectory)

        let info = try await MediaProbe.probe(input, tools: tools)
        if info.isCopyProtected { throw ConversionError.copyProtected }
        guard !info.audioStreams.isEmpty else { throw ConversionError.noAudio(fileName: input.lastPathComponent) }

        let temporary = OutputFiles.temporaryURL(in: outputDirectory, fileExtension: outputFormat.preferredExtension)
        defer { try? FileManager.default.removeItem(at: temporary) }   // no-op once moved into place

        let args = try arguments(input: input, info: info, output: temporary, outputFormat: outputFormat,
                                 operation: conversion.operation, options: options)
        let duration = info.audioStreams.first?.duration ?? info.duration
        let result = try await ProcessRunner.run(tools.ffmpeg, args) { line in
            if let fraction = Self.progressFraction(line, duration: duration) { progress(fraction) }
        }
        guard result.status == 0 else {
            if MediaProbe.looksCopyProtected(result.stderr) { throw ConversionError.copyProtected }
            throw ConversionError.conversionFailed(fileName: input.lastPathComponent, detail: result.stderr)
        }

        // Verify before saving: the output must be readable and contain audio.
        guard let out = try? await MediaProbe.probe(temporary, tools: tools), !out.audioStreams.isEmpty,
              (out.duration ?? out.audioStreams[0].duration ?? 0) > 0 else {
            throw ConversionError.verificationFailed(fileName: input.lastPathComponent)
        }

        let saved = try OutputFiles.finalize(temporary, in: outputDirectory,
                                             baseName: input.deletingPathExtension().lastPathComponent,
                                             fileExtension: outputFormat.preferredExtension)
        progress(1)
        return saved
    }

    /// FFmpeg arguments for one conversion. Paths are always absolute, so they can't be mistaken for options.
    func arguments(input: URL, info: MediaInfo, output: URL, outputFormat: FileFormat,
                   operation: ConversionOperation, options: AudioOptions) throws -> [String] {
        guard let settings = presets.formats[outputFormat.id], let encoder = settings.encoder() else {
            throw ConversionError.unsupported(from: "Audio", to: outputFormat.name)
        }
        guard let source = info.audioStreams.first else { throw ConversionError.noAudio(fileName: input.lastPathComponent) }

        var args = ["-hide_banner", "-nostdin", "-n", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
                    "-i", input.path, "-map", "0:\(source.index)"]

        // Keep cover art when converting audio to a format that can hold it. Video files never contribute frames.
        if operation == .transcodeAudio, settings.supportsArtwork, let picture = info.attachedPicture {
            args += ["-map", "0:\(picture.index)", "-c:v", "copy", "-disposition:v:0", "attached_pic"]
        } else {
            args += ["-vn"]
        }
        args += ["-sn", "-dn", "-map_metadata", "0"]

        // 24-bit sources stay 24-bit in WAV/AIFF.
        let highBitDepth = (source.bitsPerSample ?? 16) > 16
        args += ["-c:a", highBitDepth ? (settings.highBitDepthEncoder ?? encoder) : encoder]

        if !settings.isLossless, let allowed = settings.bitrates {
            let requested = options.bitrateKbps ?? presets.preset(id: options.presetID).bitrateKbps
            args += ["-b:a", "\(AudioPresets.nearest(requested, in: allowed))k"]
        }

        if let allowedRates = settings.sampleRates {
            let wanted = options.sampleRate ?? source.sampleRate ?? 44100
            let rate = AudioPresets.nearest(wanted, in: allowedRates)
            if rate != source.sampleRate || options.sampleRate != nil { args += ["-ar", "\(rate)"] }
        } else if let rate = options.sampleRate {
            args += ["-ar", "\(AudioPresets.nearest(rate, in: presets.sampleRates))"]
        }

        args += ["-f", settings.muxer, output.path]
        return args
    }

    /// Parses FFmpeg `-progress` output ("out_time_us=…", "progress=end") into a 0…1 fraction.
    static func progressFraction(_ line: String, duration: Double?) -> Double? {
        if line == "progress=end" { return 1 }
        guard line.hasPrefix("out_time_us="), let duration, duration > 0,
              let micros = Double(line.dropFirst("out_time_us=".count)) else { return nil }
        return min(max(micros / 1_000_000 / duration, 0), 1)
    }
}
