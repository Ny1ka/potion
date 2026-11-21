import Foundation

/// The four file categories. Each matches one band of the Potion logo and decides a file's tile color and glyph.
public enum FormatCategory: String, Codable, CaseIterable, Sendable {
    case image, audio, video, docs
}

/// A file format Potion understands, loaded from `core/spec/formats.json`.
public struct FileFormat: Codable, Hashable, Identifiable, Sendable {
    /// Stable lowercase id, e.g. `mp4`. Used by the conversion matrix, settings and tests.
    public let id: String
    /// Display name for badges and menus, e.g. "MP4" or "Markdown".
    public let name: String
    public let category: FormatCategory
    /// Lowercase extensions without the dot. The first is used for output files.
    public let extensions: [String]
    public let mimeTypes: [String]
    /// Text formats (TXT, Markdown, HTML, SVG, RTF) are identified mainly by extension.
    public let isText: Bool
    /// Camera RAW formats.
    public let isRaw: Bool

    /// Extension for files written in this format.
    public var preferredExtension: String { extensions[0] }

    enum CodingKeys: String, CodingKey {
        case id, name, category, extensions, mimeTypes
        case isText = "text"
        case isRaw = "raw"
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id)
        name = try c.decode(String.self, forKey: .name)
        category = try c.decode(FormatCategory.self, forKey: .category)
        extensions = try c.decode([String].self, forKey: .extensions)
        mimeTypes = try c.decodeIfPresent([String].self, forKey: .mimeTypes) ?? []
        isText = try c.decodeIfPresent(Bool.self, forKey: .isText) ?? false
        isRaw = try c.decodeIfPresent(Bool.self, forKey: .isRaw) ?? false
    }
}

/// The result of identifying a file.
public struct FormatDetection: Equatable, Sendable {
    public enum Method: String, Sendable {
        /// Identified from the file's bytes.
        case content
        /// Identified from the extension (text formats, or binary files no content rule recognized).
        case `extension`
        /// Not a supported format.
        case none
    }

    /// The detected format, or `nil` when the file isn't supported.
    public let format: FileFormat?
    public let method: Method
    /// The format the file's extension points to, if any.
    public let extensionFormat: FileFormat?
    /// True when a format was found but the file's extension isn't one of that format's extensions
    /// (e.g. a JPEG named "photo.png").
    public let extensionMismatch: Bool

    public var isSupported: Bool { format != nil }
}
