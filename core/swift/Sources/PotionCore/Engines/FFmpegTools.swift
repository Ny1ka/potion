import Foundation

/// Locations of the bundled LGPL `ffmpeg` and `ffprobe` programs (built by scripts/ffmpeg/).
/// They run as separate processes and are never linked into the app.
public struct FFmpegTools: Sendable, Equatable {
    public let ffmpeg: URL
    public let ffprobe: URL

    /// Uses `ffmpeg` and `ffprobe` from one folder.
    public init(directory: URL) throws {
        let ffmpeg = directory.appendingPathComponent("ffmpeg")
        let ffprobe = directory.appendingPathComponent("ffprobe")
        let fm = FileManager.default
        guard fm.isExecutableFile(atPath: ffmpeg.path), fm.isExecutableFile(atPath: ffprobe.path) else {
            throw ConversionError.toolsMissing
        }
        self.ffmpeg = ffmpeg
        self.ffprobe = ffprobe
    }

    /// Finds the tools: `POTION_FFMPEG_DIR` (development and tests) first, then the app bundle
    /// (`Contents/Helpers`, then next to the main executable).
    public static func locate(environment: [String: String] = ProcessInfo.processInfo.environment,
                              bundle: Bundle = .main) throws -> FFmpegTools {
        if let dir = environment["POTION_FFMPEG_DIR"], !dir.isEmpty {
            return try FFmpegTools(directory: URL(fileURLWithPath: dir))
        }
        let candidates = [
            bundle.bundleURL.appendingPathComponent("Contents/Helpers"),
            bundle.executableURL?.deletingLastPathComponent(),
        ].compactMap { $0 }
        for dir in candidates {
            if let tools = try? FFmpegTools(directory: dir) { return tools }
        }
        throw ConversionError.toolsMissing
    }
}
