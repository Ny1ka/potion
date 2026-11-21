import Foundation

/// Every format Potion understands, and the logic that identifies a file by its contents.
/// Loaded from `formats.json` (core/spec). The algorithm is specified in core/spec/README.md and is
/// implemented identically in core/dotnet.
public final class FormatRegistry: Sendable {
    public enum LoadError: Error, Equatable {
        case missingResource
        case invalid(String)
    }

    /// The registry built from the formats.json bundled with PotionCore.
    public static let shared: FormatRegistry = {
        do { return try FormatRegistry.bundled() } catch {
            preconditionFailure("PotionCore's bundled formats.json is invalid: \(error)")
        }
    }()

    /// All formats, in spec order.
    public let formats: [FileFormat]
    /// How many bytes from the start of a file detection reads.
    public let headerSize: Int

    private let rules: [DetectionRule]
    private let byID: [String: FileFormat]
    private let byExtension: [String: FileFormat]

    private struct SpecFile: Decodable {
        let version: Int
        let headerSize: Int
        let formats: [FileFormat]
        let rules: [DetectionRule]
    }

    public static func bundled() throws -> FormatRegistry {
        guard let url = Bundle.module.url(forResource: "formats", withExtension: "json") else {
            throw LoadError.missingResource
        }
        return try FormatRegistry(data: Data(contentsOf: url))
    }

    public init(data: Data) throws {
        let spec: SpecFile
        do { spec = try JSONDecoder().decode(SpecFile.self, from: data) } catch {
            throw LoadError.invalid("formats.json can't be decoded: \(error)")
        }
        guard spec.version == 1 else { throw LoadError.invalid("Unsupported formats.json version \(spec.version)") }
        guard spec.headerSize > 0 else { throw LoadError.invalid("headerSize must be positive") }

        var byID: [String: FileFormat] = [:]
        var byExtension: [String: FileFormat] = [:]
        for format in spec.formats {
            guard byID[format.id] == nil else { throw LoadError.invalid("Duplicate format id \(format.id)") }
            guard !format.extensions.isEmpty else { throw LoadError.invalid("\(format.id) has no extensions") }
            byID[format.id] = format
            for ext in format.extensions where byExtension[ext] == nil {
                byExtension[ext] = format
            }
        }
        for rule in spec.rules {
            guard byID[rule.format] != nil else { throw LoadError.invalid("Rule refers to unknown format \(rule.format)") }
            for id in rule.extensionHint where byID[id] == nil {
                throw LoadError.invalid("Rule for \(rule.format) hints unknown format \(id)")
            }
        }

        formats = spec.formats
        headerSize = spec.headerSize
        rules = spec.rules
        self.byID = byID
        self.byExtension = byExtension
    }

    public func format(id: String) -> FileFormat? { byID[id] }

    /// The format that owns an extension (case-insensitive, with or without the leading dot).
    public func format(forExtension ext: String) -> FileFormat? {
        let e = ext.hasPrefix(".") ? String(ext.dropFirst()) : ext
        return byExtension[e.lowercased()]
    }

    /// The lowercased extension of a file name, or nil (".png" and "README" have none).
    public static func fileExtension(of fileName: String) -> String? {
        guard let dot = fileName.lastIndex(of: "."), dot != fileName.startIndex else { return nil }
        let ext = fileName[fileName.index(after: dot)...]
        return ext.isEmpty ? nil : ext.lowercased()
    }

    /// Identifies a file from its first bytes and its name.
    public func detect(header: Data, fileName: String) -> FormatDetection {
        detect(header: [UInt8](header.prefix(headerSize)), fileName: fileName)
    }

    /// Reads the start of the file at `url` and identifies it.
    public func detect(fileAt url: URL) throws -> FormatDetection {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let header = try handle.read(upToCount: headerSize) ?? Data()
        return detect(header: header, fileName: url.lastPathComponent)
    }

    func detect(header: [UInt8], fileName: String) -> FormatDetection {
        let ext = Self.fileExtension(of: fileName)
        let extFormat = ext.flatMap { byExtension[$0] }

        func result(_ format: FileFormat?, _ method: FormatDetection.Method) -> FormatDetection {
            let mismatch: Bool
            if let format, let ext { mismatch = !format.extensions.contains(ext) } else { mismatch = false }
            return FormatDetection(format: format, method: method, extensionFormat: extFormat, extensionMismatch: mismatch)
        }

        // 1. Empty file.
        guard !header.isEmpty else { return result(nil, .none) }
        // 2–3. Text with a text extension: trust the extension.
        let isText = Self.looksLikeText(header)
        if isText, let extFormat, extFormat.isText { return result(extFormat, .extension) }
        // 4. Content rules, first match wins.
        for rule in rules where rule.matches(header, extensionFormatID: extFormat?.id) {
            if let extFormat, rule.extensionHint.contains(extFormat.id) { return result(extFormat, .content) }
            return result(byID[rule.format], .content)
        }
        // 5. Extension fallback when the header's textness agrees with the format.
        if let extFormat, extFormat.isText == isText { return result(extFormat, .extension) }
        // 6. Not supported.
        return result(nil, .none)
    }

    /// Text if it starts with a UTF-16 byte-order mark, or has no control bytes other than
    /// tab, line feed, vertical tab, form feed, carriage return and escape.
    static func looksLikeText(_ header: [UInt8]) -> Bool {
        if header.count >= 2, (header[0] == 0xFF && header[1] == 0xFE) || (header[0] == 0xFE && header[1] == 0xFF) {
            return true
        }
        return !header.contains { b in b <= 0x08 || (0x0E...0x1A).contains(b) || (0x1C...0x1F).contains(b) }
    }
}
