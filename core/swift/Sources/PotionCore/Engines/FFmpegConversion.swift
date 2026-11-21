import Foundation

/// The steps every FFmpeg-based conversion shares: write to a hidden temporary file, report progress,
/// map failures to user-facing errors, verify the result, then save it under a name that doesn't exist yet.
enum FFmpegConversion {
    static func run(tools: FFmpegTools, input: URL, outputDirectory: URL, outputExtension: String,
                    duration: Double?, progress: @escaping @Sendable (Double) -> Void,
                    arguments: (URL) throws -> [String],
                    verify: (MediaInfo) -> Bool) async throws -> URL {
        let temporary = OutputFiles.temporaryURL(in: outputDirectory, fileExtension: outputExtension)
        defer { OutputFiles.removeIfPresent(temporary) }   // no-op once moved into place

        let args = try arguments(temporary)
        let result = try await ProcessRunner.run(tools.ffmpeg, args) { line in
            if let fraction = progressFraction(line, duration: duration) { progress(fraction) }
        }
        guard result.status == 0 else {
            if MediaProbe.looksCopyProtected(result.stderr) { throw ConversionError.copyProtected }
            throw ConversionError.conversionFailed(fileName: input.lastPathComponent, detail: result.stderr)
        }

        guard let out = try? await MediaProbe.probe(temporary, tools: tools), verify(out) else {
            throw ConversionError.verificationFailed(fileName: input.lastPathComponent)
        }

        let saved = try OutputFiles.finalize(temporary, in: outputDirectory,
                                             baseName: input.deletingPathExtension().lastPathComponent,
                                             fileExtension: outputExtension)
        progress(1)
        return saved
    }

    /// Arguments every FFmpeg run starts with: quiet, never overwrite, machine-readable progress on stdout.
    static func commonArguments(input: URL) -> [String] {
        ["-hide_banner", "-nostdin", "-n", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-i", input.path]
    }

    /// Parses FFmpeg `-progress` output ("out_time_us=…", "progress=end") into a 0…1 fraction.
    static func progressFraction(_ line: String, duration: Double?) -> Double? {
        if line == "progress=end" { return 1 }
        guard line.hasPrefix("out_time_us="), let duration, duration > 0,
              let micros = Double(line.dropFirst("out_time_us=".count)) else { return nil }
        return min(max(micros / 1_000_000 / duration, 0), 1)
    }

    static func hasDuration(_ info: MediaInfo, stream: MediaInfo.Stream?) -> Bool {
        (info.duration ?? stream?.duration ?? 0) > 0
    }
}
