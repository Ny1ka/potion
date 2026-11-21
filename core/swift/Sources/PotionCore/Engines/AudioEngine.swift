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

        let source = info.audioStreams.first
        return try await FFmpegConversion.run(
            tools: tools, input: input, outputDirectory: outputDirectory,
            outputExtension: outputFormat.preferredExtension, duration: source?.duration ?? info.duration,
            progress: progress,
            arguments: { temporary in
                try arguments(input: input, info: info, output: temporary, outputFormat: outputFormat,
                              operation: conversion.operation, options: options)
            },
            verify: { out in !out.audioStreams.isEmpty && FFmpegConversion.hasDuration(out, stream: out.audioStreams.first) })
    }

    /// FFmpeg arguments for one conversion. Paths are always absolute, so they can't be mistaken for options.
    func arguments(input: URL, info: MediaInfo, output: URL, outputFormat: FileFormat,
                   operation: ConversionOperation, options: AudioOptions) throws -> [String] {
        guard let settings = presets.formats[outputFormat.id], let encoder = settings.encoder() else {
            throw ConversionError.unsupported(from: "Audio", to: outputFormat.name)
        }
        guard let source = info.audioStreams.first else { throw ConversionError.noAudio(fileName: input.lastPathComponent) }

        var args = FFmpegConversion.commonArguments(input: input) + ["-map", "0:\(source.index)"]

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
}
