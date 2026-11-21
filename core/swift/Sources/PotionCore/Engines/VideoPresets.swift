import Foundation

/// Video quality presets, codecs and per-container settings, loaded from `presets.json` (core/spec).
public struct VideoPresets: Sendable {
    public struct Preset: Decodable, Sendable, Equatable, Identifiable {
        public let id: String
        public let name: String
        /// Bits per pixel per frame used to size the bitrate. Nil for "Original quality".
        public let bitsPerPixel: Double?
    }

    public struct Codec: Decodable, Sendable, Equatable {
        let encoder: [String: String]
        /// Bitrate multiplier relative to H.264 for the same quality (HEVC/VP9 need less, older codecs more).
        public let efficiency: Double
        /// Container tag, e.g. "hvc1" so HEVC in MP4/MOV plays in Apple apps.
        public let tag: String?
        /// MPEG-2 only allows broadcast frame rates.
        public let standardFrameRates: Bool?
        public func encoder(platform: String = "macos") -> String? { encoder[platform] ?? encoder["default"] }
    }

    public struct AudioCodec: Decodable, Sendable, Equatable {
        let encoder: [String: String]
        public let bitrateKbps: Int
        public func encoder(platform: String = "macos") -> String? { encoder[platform] ?? encoder["default"] }
    }

    public struct Container: Decodable, Sendable, Equatable {
        public let muxer: String
        /// Codecs offered for this container; the first is the default.
        public let videoCodecs: [String]
        public let audioCodec: String
        /// Source codecs that can be copied without re-encoding ("*" = any).
        public let copyVideo: [String]
        public let copyAudio: [String]
        public let faststart: Bool?

        func canCopyVideo(_ codec: String) -> Bool { copyVideo.contains("*") || copyVideo.contains(codec) }
        func canCopyAudio(_ codec: String) -> Bool { copyAudio.contains("*") || copyAudio.contains(codec) }
    }

    public struct GIFSettings: Decodable, Sendable, Equatable {
        public let fps: Double
        public let maxWidth: Int
    }

    public let presets: [Preset]
    public let defaultPresetID: String
    public let resolutions: [Int]
    public let frameRates: [Double]
    public let aspectRatios: [String]
    public let minBitrateKbps: Int
    public let maxBitrateKbps: Int
    public let codecs: [String: Codec]
    public let audioCodecs: [String: AudioCodec]
    public let formats: [String: Container]
    public let gif: [String: GIFSettings]

    public static let shared: VideoPresets = {
        do {
            guard let url = Bundle.module.url(forResource: "presets", withExtension: "json") else {
                throw ConversionError.toolsMissing
            }
            return try VideoPresets(data: Data(contentsOf: url))
        } catch {
            preconditionFailure("PotionCore's bundled presets.json is invalid: \(error)")
        }
    }()

    public init(data: Data) throws {
        struct File: Decodable { let video: Video }
        struct Video: Decodable {
            let presets: [Preset]
            let defaultPreset: String
            let resolutions: [Int]
            let frameRates: [Double]
            let aspectRatios: [String]
            let minBitrateKbps: Int
            let maxBitrateKbps: Int
            let codecs: [String: Codec]
            let audioCodecs: [String: AudioCodec]
            let formats: [String: Container]
            let gif: [String: GIFSettings]
        }
        let v = try JSONDecoder().decode(File.self, from: data).video
        for (id, container) in v.formats {
            for codec in container.videoCodecs where v.codecs[codec] == nil {
                throw DecodingError.dataCorrupted(.init(codingPath: [], debugDescription: "\(id) lists unknown codec \(codec)"))
            }
            if v.audioCodecs[container.audioCodec] == nil {
                throw DecodingError.dataCorrupted(.init(codingPath: [], debugDescription: "\(id) lists unknown audio codec"))
            }
        }
        presets = v.presets
        defaultPresetID = v.defaultPreset
        resolutions = v.resolutions
        frameRates = v.frameRates
        aspectRatios = v.aspectRatios
        minBitrateKbps = v.minBitrateKbps
        maxBitrateKbps = v.maxBitrateKbps
        codecs = v.codecs
        audioCodecs = v.audioCodecs
        formats = v.formats
        gif = v.gif
    }

    public func preset(id: String?) -> Preset {
        presets.first { $0.id == id } ?? presets.first { $0.id == defaultPresetID }!
    }

    /// Broadcast frame rates MPEG-2 accepts, as FFmpeg rationals.
    static let standardRates: [(value: Double, text: String)] = [
        (24000.0 / 1001, "24000/1001"), (24, "24"), (25, "25"), (30000.0 / 1001, "30000/1001"),
        (30, "30"), (50, "50"), (60000.0 / 1001, "60000/1001"), (60, "60"),
    ]
}
