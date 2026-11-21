import Foundation

/// What `ffprobe` reports about a media file.
public struct MediaInfo: Sendable, Equatable {
    public struct Stream: Sendable, Equatable {
        public enum Kind: String, Sendable { case audio, video, subtitle, data, attachment, unknown }
        public let index: Int
        public let kind: Kind
        public let codec: String
        public let codecTag: String
        public let sampleRate: Int?
        public let channels: Int?
        /// Bits per sample for PCM/lossless audio, when known.
        public let bitsPerSample: Int?
        public let bitRate: Int?
        /// Display width and height, after applying any rotation flag (portrait phone videos).
        public let width: Int?
        public let height: Int?
        /// Average frames per second for video streams.
        public let frameRate: Double?
        public let pixelFormat: String?
        /// Cover art embedded as a video stream (MP3/M4A/FLAC artwork).
        public let isAttachedPicture: Bool
        public let duration: Double?
    }

    public let formatName: String
    public let duration: Double?
    public let bitRate: Int?
    public let streams: [Stream]

    public var audioStreams: [Stream] { streams.filter { $0.kind == .audio } }
    /// Real video streams (cover art excluded).
    public var videoStreams: [Stream] { streams.filter { $0.kind == .video && !$0.isAttachedPicture } }
    public var attachedPicture: Stream? { streams.first { $0.isAttachedPicture } }

    /// Streams flagged as encrypted (FairPlay and Common Encryption sample entries).
    public var isCopyProtected: Bool {
        let protectedTags: Set<String> = ["drms", "drmi", "enca", "encv", "encs", "enct"]
        return streams.contains { protectedTags.contains($0.codecTag.lowercased()) }
    }
}

enum MediaProbe {
    static func probe(_ url: URL, tools: FFmpegTools) async throws -> MediaInfo {
        let result = try await ProcessRunner.run(tools.ffprobe, [
            "-v", "error", "-print_format", "json", "-show_format", "-show_streams", url.path,
        ])
        if looksCopyProtected(result.stderr) { throw ConversionError.copyProtected }
        guard result.status == 0, let info = try? parse(result.stdout) else {
            throw ConversionError.unreadable(fileName: url.lastPathComponent)
        }
        return info
    }

    static func looksCopyProtected(_ ffmpegOutput: String) -> Bool {
        let text = ffmpegOutput.lowercased()
        return text.range(of: #"\bdrm\b"#, options: .regularExpression) != nil
            || text.contains("encrypted") || text.contains("decryption key")
    }

    /// "30000/1001" → 29.97. Nil for "0/0" or malformed values.
    static func rate(_ fraction: String?) -> Double? {
        guard let parts = fraction?.split(separator: "/"), parts.count == 2,
              let n = Double(parts[0]), let d = Double(parts[1]), n > 0, d > 0 else { return nil }
        return n / d
    }

    static func parse(_ json: Data) throws -> MediaInfo {
        struct Root: Decodable { let format: Format?; let streams: [RawStream]? }
        struct Format: Decodable { let format_name: String?; let duration: String?; let bit_rate: String? }
        struct Disposition: Decodable { let attached_pic: Int? }
        struct SideData: Decodable { let rotation: Double? }
        struct RawStream: Decodable {
            let index: Int
            let codec_type: String?
            let codec_name: String?
            let codec_tag_string: String?
            let sample_rate: String?
            let channels: Int?
            let bits_per_raw_sample: String?
            let bits_per_sample: Int?
            let bit_rate: String?
            let width: Int?
            let height: Int?
            let avg_frame_rate: String?
            let r_frame_rate: String?
            let pix_fmt: String?
            let duration: String?
            let disposition: Disposition?
            let side_data_list: [SideData]?
            let tags: [String: String]?
        }
        let root = try JSONDecoder().decode(Root.self, from: json)
        guard let format = root.format else { throw ConversionError.unreadable(fileName: "") }
        let streams = (root.streams ?? []).map { s -> MediaInfo.Stream in
            let rawBits = s.bits_per_raw_sample.flatMap(Int.init)
            let bits = [rawBits, s.bits_per_sample].compactMap { $0 }.first { $0 > 0 }
            // Rotation comes from the display matrix (side data) or an older "rotate" tag.
            let matrixRotation: Double? = s.side_data_list?.compactMap { $0.rotation }.first
            let tagRotation: Double? = s.tags?["rotate"].flatMap { Double($0) }
            let rotation: Double = matrixRotation ?? tagRotation ?? 0
            let sideways: Bool = Int(abs(rotation).rounded()) % 180 == 90
            return MediaInfo.Stream(
                index: s.index,
                kind: MediaInfo.Stream.Kind(rawValue: s.codec_type ?? "") ?? .unknown,
                codec: s.codec_name ?? "",
                codecTag: s.codec_tag_string ?? "",
                sampleRate: s.sample_rate.flatMap(Int.init),
                channels: s.channels,
                bitsPerSample: bits,
                bitRate: s.bit_rate.flatMap(Int.init),
                width: sideways ? s.height : s.width,
                height: sideways ? s.width : s.height,
                frameRate: rate(s.avg_frame_rate) ?? rate(s.r_frame_rate),
                pixelFormat: s.pix_fmt,
                isAttachedPicture: (s.disposition?.attached_pic ?? 0) == 1,
                duration: s.duration.flatMap(Double.init))
        }
        return MediaInfo(formatName: format.format_name ?? "",
                         duration: format.duration.flatMap(Double.init),
                         bitRate: format.bit_rate.flatMap(Int.init),
                         streams: streams)
    }
}
