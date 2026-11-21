import Foundation

/// Audio quality presets and per-format encoder settings, loaded from `presets.json` (core/spec).
public struct AudioPresets: Sendable {
    public struct Preset: Decodable, Sendable, Equatable, Identifiable {
        public let id: String
        public let name: String
        public let bitrateKbps: Int
    }

    public struct FormatSettings: Decodable, Sendable, Equatable {
        public let muxer: String
        let encoder: [String: String]
        public let highBitDepthEncoder: String?
        public let bitrates: [Int]?
        public let sampleRates: [Int]?
        public let lossless: Bool?
        public let artwork: Bool?

        /// The encoder for this platform ("macos"), falling back to "default".
        public func encoder(platform: String = "macos") -> String? { encoder[platform] ?? encoder["default"] }
        public var isLossless: Bool { lossless ?? false }
        public var supportsArtwork: Bool { artwork ?? false }
    }

    public let presets: [Preset]
    public let defaultPresetID: String
    public let sampleRates: [Int]
    public let formats: [String: FormatSettings]

    public static let shared: AudioPresets = {
        do {
            guard let url = Bundle.module.url(forResource: "presets", withExtension: "json") else {
                throw ConversionError.toolsMissing
            }
            return try AudioPresets(data: Data(contentsOf: url))
        } catch {
            preconditionFailure("PotionCore's bundled presets.json is invalid: \(error)")
        }
    }()

    public init(data: Data) throws {
        struct File: Decodable { let version: Int; let audio: Audio }
        struct Audio: Decodable {
            let presets: [Preset]
            let defaultPreset: String
            let sampleRates: [Int]
            let formats: [String: FormatSettings]
        }
        let file = try JSONDecoder().decode(File.self, from: data)
        guard file.version == 1 else { throw ConversionError.toolsMissing }
        guard file.audio.presets.contains(where: { $0.id == file.audio.defaultPreset }) else {
            throw DecodingError.dataCorrupted(.init(codingPath: [], debugDescription: "defaultPreset not in presets"))
        }
        presets = file.audio.presets
        defaultPresetID = file.audio.defaultPreset
        sampleRates = file.audio.sampleRates
        formats = file.audio.formats
    }

    public func preset(id: String?) -> Preset {
        presets.first { $0.id == id } ?? presets.first { $0.id == defaultPresetID }!
    }

    /// The allowed value closest to `requested` (ties go to the lower value).
    static func nearest(_ requested: Int, in allowed: [Int]) -> Int {
        allowed.min { a, b in
            let da = abs(a - requested), db = abs(b - requested)
            return da != db ? da < db : a < b
        } ?? requested
    }
}
