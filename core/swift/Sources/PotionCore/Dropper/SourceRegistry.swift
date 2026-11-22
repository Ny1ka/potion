import Foundation

/// A link Potion Dropper can try to download.
public struct DropLink: Hashable, Sendable {
    public let parts: LinkParts
    /// The source adapter that accepted the link (an id in sources.json).
    public let sourceID: String
    /// The media format the link points to, when its file name says so.
    public let formatID: String?
    /// True when only the server's response can tell whether this is a media file (no media extension).
    public let needsCheck: Bool

    /// The normalized link, ASCII only.
    public var string: String { parts.string }
    public var host: String { parts.host }
    public var url: URL {
        guard let url = URL(string: parts.string) else { preconditionFailure("Normalized link isn't a URL: \(parts.string)") }
        return url
    }
}

/// A word in pasted text that looked like a link but can't be used.
public struct LinkProblem: Hashable, Sendable {
    public let input: String
    public let error: LinkError
}

/// The links found in pasted or dropped text.
public struct LinkList: Hashable, Sendable {
    /// Usable links in the order they appeared, without duplicates.
    public var links: [DropLink] = []
    public var problems: [LinkProblem] = []
    /// Usable links left out because the paste had more than `maxLinksPerPaste`.
    public var overLimit = 0
}

/// One allowlisted source from sources.json. Each needs an entry in docs/SOURCES.md.
public struct SourceInfo: Decodable, Hashable, Sendable {
    public let id: String
    public let name: String
    /// The kinds of media this source provides.
    public let categories: [FormatCategory]
    /// When the source's terms were last reviewed (YYYY-MM-DD).
    public let reviewed: String
}

/// What a source adapter decided about a link.
public enum SourceMatch: Equatable, Sendable {
    case accept(formatID: String?, needsCheck: Bool)
    case reject(LinkError)
}

/// A source module: decides whether it handles a link. Public adapters live in `Dropper/SourceAdapters` and are
/// listed in `SourceAdapters.builtIn`. An adapter runs only if sources.json lists its id.
public protocol SourceAdapter: Sendable {
    var id: String { get }
    /// Nil when the link isn't for this source, so the next adapter is asked.
    func match(_ link: LinkParts, source: SourceInfo, formats: FormatRegistry) -> SourceMatch?
}

/// The Dropper source allowlist (core/spec/sources.json) and link parsing (core/spec/README.md, "Links").
/// core/dotnet implements the same behavior and runs the same cases.
public final class SourceRegistry: Sendable {
    public enum LoadError: Error, Equatable {
        case missingResource
        case invalid(String)
    }

    public static let shared: SourceRegistry = {
        do { return try SourceRegistry.bundled() } catch {
            preconditionFailure("PotionCore's bundled sources.json is invalid: \(error)")
        }
    }()

    /// Allowlisted sources in matching order.
    public let sources: [SourceInfo]
    public let maxLinkLength: Int
    public let maxLinksPerPaste: Int
    public let playlistExtensions: Set<String>
    public let download: DownloadSettings
    private let userAgentTemplate: String
    private let messages: [String: String]
    private let adapters: [(SourceInfo, any SourceAdapter)]
    private let formats: FormatRegistry

    private struct SpecFile: Decodable {
        struct Limits: Decodable { let maxLinkLength: Int; let maxLinksPerPaste: Int }
        let version: Int
        let userAgent: String
        let limits: Limits
        let playlistExtensions: [String]
        let download: DownloadSettings
        let sources: [SourceInfo]
        let messages: [String: String]
    }

    public static func bundled() throws -> SourceRegistry {
        guard let url = Bundle.module.url(forResource: "sources", withExtension: "json") else { throw LoadError.missingResource }
        return try SourceRegistry(data: Data(contentsOf: url))
    }

    public init(data: Data, formats: FormatRegistry = .shared, adapters: [any SourceAdapter] = SourceAdapters.builtIn) throws {
        let spec: SpecFile
        do { spec = try JSONDecoder().decode(SpecFile.self, from: data) } catch {
            throw LoadError.invalid("sources.json can't be decoded: \(error)")
        }
        guard spec.version == 1 else { throw LoadError.invalid("Unsupported sources.json version \(spec.version)") }
        guard spec.limits.maxLinkLength > 0, spec.limits.maxLinksPerPaste > 0 else { throw LoadError.invalid("Limits must be positive") }
        for id in LinkError.allCases.map(\.rawValue) + DownloadError.allCases.map(\.id) where spec.messages[id] == nil {
            throw LoadError.invalid("No message for \(id)")
        }

        var byID: [String: any SourceAdapter] = [:]
        for adapter in adapters {
            guard byID[adapter.id] == nil else { throw LoadError.invalid("Two adapters use the id \(adapter.id)") }
            byID[adapter.id] = adapter
        }
        var pairs: [(SourceInfo, any SourceAdapter)] = []
        for source in spec.sources {
            guard !pairs.contains(where: { $0.0.id == source.id }) else { throw LoadError.invalid("Duplicate source \(source.id)") }
            guard let adapter = byID.removeValue(forKey: source.id) else { throw LoadError.invalid("No adapter for source \(source.id)") }
            pairs.append((source, adapter))
        }
        // No adapter without an allowlist entry (and a docs/SOURCES.md entry, checked by tests).
        if let stray = byID.keys.sorted().first { throw LoadError.invalid("Adapter \(stray) isn't in sources.json") }

        sources = spec.sources
        maxLinkLength = spec.limits.maxLinkLength
        maxLinksPerPaste = spec.limits.maxLinksPerPaste
        playlistExtensions = Set(spec.playlistExtensions)
        download = spec.download
        userAgentTemplate = spec.userAgent
        messages = spec.messages
        self.adapters = pairs
        self.formats = formats
    }

    /// The honest User-Agent sent with every request.
    public func userAgent(version: String) -> String {
        userAgentTemplate.replacingOccurrences(of: "{version}", with: version)
    }

    /// The product token robots.txt groups are matched against ("PotionDropper").
    public var robotsAgent: String { String(userAgentTemplate.prefix { $0 != "/" && $0 != " " }) }

    /// The text shown for a link error (follows the design system's voice rules).
    public func message(for error: LinkError) -> String { messages[error.rawValue] ?? "" }

    /// The text shown for a download error.
    public func message(for error: DownloadError) -> String { messages[error.id] ?? "" }

    /// Parses one link, as typed into the link field.
    public func link(from input: String) -> Result<DropLink, LinkError> {
        let parts: LinkParts
        switch LinkSyntax.normalize(input, maxLength: maxLinkLength) {
        case .failure(let error): return .failure(error)
        case .success(let p): parts = p
        }
        if let ext = parts.fileExtension, playlistExtensions.contains(ext) { return .failure(.playlist) }
        for (source, adapter) in adapters {
            switch adapter.match(parts, source: source, formats: formats) {
            case nil: continue
            case .reject(let error): return .failure(error)
            case .accept(let formatID, let needsCheck):
                return .success(DropLink(parts: parts, sourceID: source.id, formatID: formatID, needsCheck: needsCheck))
            }
        }
        return .failure(.unsupportedSite)
    }

    /// Finds the links in pasted or dropped text: words separated by whitespace, with surrounding quotes, brackets
    /// and sentence punctuation removed. Words that don't look like links are ignored.
    public func links(in text: String) -> LinkList {
        var list = LinkList()
        var seen: Set<String> = []
        for word in text.split(whereSeparator: \.isWhitespace) {
            let token = Self.trimPunctuation(word)
            guard !token.isEmpty, LinkSyntax.isCandidate(token) else { continue }
            switch link(from: token) {
            case .failure(let error):
                list.problems.append(LinkProblem(input: token, error: error))
            case .success(let link):
                guard seen.insert(link.string).inserted else { continue }
                if list.links.count < maxLinksPerPaste { list.links.append(link) } else { list.overLimit += 1 }
            }
        }
        return list
    }

    private static let leading: Set<Character> = ["<", "(", "[", "{", "\"", "'", "\u{201C}", "\u{2018}", "\u{00AB}"]
    private static let trailing: Set<Character> = [">", ")", "]", "}", "\"", "'", ".", ",", ";", ":", "!", "?", "\u{201D}", "\u{2019}", "\u{00BB}"]

    static func trimPunctuation(_ word: Substring) -> String {
        var token = word
        while let first = token.first, leading.contains(first) { token = token.dropFirst() }
        while let last = token.last, trailing.contains(last) { token = token.dropLast() }
        return String(token)
    }
}
