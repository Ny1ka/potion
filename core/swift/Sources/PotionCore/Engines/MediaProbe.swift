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
        public let width: Int?
        public let height: Int?
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

    static func parse(_ json: Data) throws -> MediaInfo {
        struct Root: Decodable { let format: Format?; let streams: [RawStream]? }
        struct Format: Decodable { let format_name: String?; let duration: String?; let bit_rate: String? }
        struct Disposition: Decodable { let attached_pic: Int? }
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
            let duration: String?
            let disposition: Disposition?
        }
        let root = try JSONDecoder().decode(Root.self, from: json)
        guard let format = root.format else { throw ConversionError.unreadable(fileName: "") }
        let streams = (root.streams ?? []).map { s -> MediaInfo.Stream in
            let rawBits = s.bits_per_raw_sample.flatMap(Int.init)
            let bits = [rawBits, s.bits_per_sample].compactMap { $0 }.first { $0 > 0 }
            return MediaInfo.Stream(
                index: s.index,
                kind: MediaInfo.Stream.Kind(rawValue: s.codec_type ?? "") ?? .unknown,
                codec: s.codec_name ?? "",
                codecTag: s.codec_tag_string ?? "",
                sampleRate: s.sample_rate.flatMap(Int.init),
                channels: s.channels,
                bitsPerSample: bits,
                bitRate: s.bit_rate.flatMap(Int.init),
                width: s.width,
                height: s.height,
                isAttachedPicture: (s.disposition?.attached_pic ?? 0) == 1,
                duration: s.duration.flatMap(Double.init))
        }
        return MediaInfo(formatName: format.format_name ?? "",
                         duration: format.duration.flatMap(Double.init),
                         bitRate: format.bit_rate.flatMap(Int.init),
                         streams: streams)
    }
}
