import Foundation

/// Why a download failed. `id` is the key in core/spec/sources.json `messages` and in the shared test cases.
public enum DownloadError: Error, Hashable, Sendable {
    case accessDenied, notFound, unavailableForLegalReasons, rateLimited, serverError, refused, tooLarge
    case robotsDisallowed, tooManyRedirects, network, timedOut, interrupted
    /// The link (or a redirect target) isn't usable, or the server's answer isn't media.
    case link(LinkError)

    public static let allCases: [DownloadError] = [
        .accessDenied, .notFound, .unavailableForLegalReasons, .rateLimited, .serverError, .refused, .tooLarge,
        .robotsDisallowed, .tooManyRedirects, .network, .timedOut, .interrupted,
    ]

    public var id: String {
        switch self {
        case .accessDenied: "accessDenied"
        case .notFound: "notFound"
        case .unavailableForLegalReasons: "unavailableForLegalReasons"
        case .rateLimited: "rateLimited"
        case .serverError: "serverError"
        case .refused: "refused"
        case .tooLarge: "tooLarge"
        case .robotsDisallowed: "robotsDisallowed"
        case .tooManyRedirects: "tooManyRedirects"
        case .network: "network"
        case .timedOut: "timedOut"
        case .interrupted: "interrupted"
        case .link(let error): error.rawValue
        }
    }
}

/// Download settings from sources.json `download`.
public struct DownloadSettings: Decodable, Hashable, Sendable {
    public struct ContentTypes: Decodable, Hashable, Sendable {
        public let playlist: Set<String>
        public let media: Set<String>
        public let generic: Set<String>
        public let page: Set<String>
    }
    public let maxRedirects: Int
    public let timeoutSeconds: Int
    public let retries: Int
    public let defaultRetrySeconds: Int
    public let maxRetrySeconds: Int
    public let maxRobotsBytes: Int
    public let defaultMaxBytes: Int64
    public let defaultMaxDurationMinutes: Int
    public let maxFileNameLength: Int
    public let contentTypes: ContentTypes
}

/// What to do with a server's answer (core/spec/README.md, "Downloads").
public enum ResponseVerdict: Equatable, Sendable {
    /// Download the body. `sniff`: only the first bytes can tell whether it's media. `length`: bytes in this
    /// response, when the server says. `resumed`: the body continues a partial file.
    case accept(sniff: Bool, length: Int64?, resumed: Bool)
    /// Follow `Location` (possibly relative), after checking it against the allowlist again.
    case redirect(String)
    /// Ask again after this many seconds.
    case retry(seconds: Int)
    /// The partial file can't be resumed: start again from the beginning.
    case restart
    case fail(DownloadError)
}

/// How to treat the answer to a robots.txt request (RFC 9309, section 2.3.1).
public enum RobotsFetch: String, Sendable {
    case parse, allowAll, disallowAll

    public init(status: Int) {
        switch status {
        case 200..<300: self = .parse
        case 400..<500: self = .allowAll
        default: self = .disallowAll
        }
    }
}

/// The pure decisions of a download, shared with core/dotnet and covered by core/spec/tests/downloads.json.
public enum DownloadRules {
    /// Decides what to do with a response. `headers` keys are matched without regard to case.
    public static func classify(status: Int, headers: [String: String], rangeRequested: Bool, maxBytes: Int64,
                                settings: DownloadSettings) -> ResponseVerdict {
        var lower: [String: String] = [:]
        for (key, value) in headers { lower[key.lowercased()] = value }
        switch status {
        case 200, 206:
            if status == 206 && !rangeRequested { return .fail(.refused) }
            let type = (lower["content-type"] ?? "").split(separator: ";", maxSplits: 1).first
                .map { $0.trimmingCharacters(in: .whitespaces).lowercased() } ?? ""
            let types = settings.contentTypes
            if types.playlist.contains(type) { return .fail(.link(.playlist)) }
            if types.page.contains(type) { return .fail(.link(.unsupportedSite)) }
            let sniff: Bool
            if type.hasPrefix("audio/") || type.hasPrefix("video/") || types.media.contains(type) {
                sniff = false
            } else if type.isEmpty || types.generic.contains(type) {
                sniff = true
            } else {
                return .fail(.link(.notMedia))
            }
            let length = lower["content-length"].flatMap { Int64($0.trimmingCharacters(in: .whitespaces)) }.flatMap { $0 >= 0 ? $0 : nil }
            if let length, length > maxBytes { return .fail(.tooLarge) }
            return .accept(sniff: sniff, length: length, resumed: status == 206)
        case 301, 302, 303, 307, 308:
            let location = lower["location"]?.trimmingCharacters(in: .whitespaces) ?? ""
            return location.isEmpty ? .fail(.refused) : .redirect(location)
        case 401, 403, 407: return .fail(.accessDenied)
        case 404, 410: return .fail(.notFound)
        case 416: return rangeRequested ? .restart : .fail(.refused)
        case 451: return .fail(.unavailableForLegalReasons)
        case 429:
            let seconds = retryAfter(lower["retry-after"], date: lower["date"]) ?? settings.defaultRetrySeconds
            return seconds <= settings.maxRetrySeconds ? .retry(seconds: seconds) : .fail(.rateLimited)
        case 503:
            guard let seconds = retryAfter(lower["retry-after"], date: lower["date"]) else { return .fail(.serverError) }
            return seconds <= settings.maxRetrySeconds ? .retry(seconds: seconds) : .fail(.serverError)
        case 500..<600: return .fail(.serverError)
        default: return .fail(.refused)
        }
    }

    /// `Retry-After` in seconds: a number, or an HTTP date measured from the response's `Date` (or now).
    static func retryAfter(_ value: String?, date: String?, now: Date = Date()) -> Int? {
        guard let value = value?.trimmingCharacters(in: .whitespaces), !value.isEmpty else { return nil }
        if value.allSatisfy({ $0.isASCII && $0.isNumber }) { return value.count > 9 ? Int.max : Int(value) }
        guard let target = httpDate(value) else { return nil }
        let reference = date.flatMap(httpDate) ?? now
        return max(0, Int(target.timeIntervalSince(reference).rounded(.up)))
    }

    private static func httpDate(_ text: String) -> Date? {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "GMT")
        formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss 'GMT'"
        return formatter.date(from: text.trimmingCharacters(in: .whitespaces))
    }

    /// Checks the first bytes of a body the server didn't label as media: returns the media format, or why not.
    public static func sniff(header: Data, fileName: String, formats: FormatRegistry = .shared) -> Result<String, DownloadError> {
        let bytes = [UInt8](header.prefix(formats.headerSize))
        guard !bytes.isEmpty else { return .failure(.link(.notMedia)) }
        let detection = formats.detect(header: Data(bytes), fileName: fileName)
        if let format = detection.format {
            if format.category == .audio || format.category == .video { return .success(format.id) }
            return .failure(.link(format.isText ? .unsupportedSite : .notMedia))
        }
        // Text that isn't a known format is almost always an error or sign-in page.
        return .failure(.link(FormatRegistry.looksLikeText(bytes) ? .unsupportedSite : .notMedia))
    }

    // MARK: - File names

    /// The name to save a download under (before the extension is checked against the detected format):
    /// `filename*` (RFC 8187), then `filename` from Content-Disposition, then the link's file name, then the host.
    public static func fileName(contentDisposition: String?, link: LinkParts, maxLength: Int) -> String {
        if let disposition = contentDisposition {
            let params = dispositionParameters(disposition)
            if let extended = params["filename*"].flatMap(decodeExtended), let name = sanitize(lastComponent(extended), maxLength: maxLength) {
                return name
            }
            if let plain = params["filename"], let name = sanitize(lastComponent(plain), maxLength: maxLength) { return name }
        }
        return sanitize(link.fileName, maxLength: maxLength) ?? link.host
    }

    /// Parameters after the disposition type, names lowercased, quoted strings unescaped. The first of a name wins.
    static func dispositionParameters(_ header: String) -> [String: String] {
        let chars = Array(header)
        var i = chars.firstIndex(of: ";") ?? chars.count
        var params: [String: String] = [:]
        while i < chars.count {
            while i < chars.count, chars[i] == ";" || chars[i].isWhitespace { i += 1 }
            var name = ""
            while i < chars.count, chars[i] != "=", chars[i] != ";" { name.append(chars[i]); i += 1 }
            guard i < chars.count, chars[i] == "=" else { continue }
            i += 1
            while i < chars.count, chars[i] == " " || chars[i] == "\t" { i += 1 }
            var value = ""
            if i < chars.count, chars[i] == "\"" {
                i += 1
                while i < chars.count, chars[i] != "\"" {
                    if chars[i] == "\\", i + 1 < chars.count { i += 1 }
                    value.append(chars[i])
                    i += 1
                }
                i += 1
            } else {
                while i < chars.count, chars[i] != ";" { value.append(chars[i]); i += 1 }
                value = value.trimmingCharacters(in: .whitespaces)
            }
            let key = name.trimmingCharacters(in: .whitespaces).lowercased()
            if !key.isEmpty, params[key] == nil { params[key] = value }
        }
        return params
    }

    /// `charset'language'percent-encoded` with UTF-8 or ISO-8859-1.
    static func decodeExtended(_ value: String) -> String? {
        let parts = value.split(separator: "'", maxSplits: 2, omittingEmptySubsequences: false)
        guard parts.count == 3 else { return nil }
        var bytes: [UInt8] = []
        let encoded = Array(parts[2].utf8)
        var i = 0
        while i < encoded.count {
            if encoded[i] == UInt8(ascii: "%"), i + 2 < encoded.count, let byte = UInt8(String(decoding: encoded[(i + 1)...(i + 2)], as: UTF8.self), radix: 16) {
                bytes.append(byte)
                i += 3
            } else {
                bytes.append(encoded[i])
                i += 1
            }
        }
        switch parts[0].lowercased() {
        case "utf-8": return String(decoding: bytes, as: UTF8.self)
        case "iso-8859-1": return String(bytes.map { Character(Unicode.Scalar($0)) })
        default: return nil
        }
    }

    private static func lastComponent(_ name: String) -> String {
        guard let separator = name.lastIndex(where: { $0 == "/" || $0 == "\\" }) else { return name }
        return String(name[name.index(after: separator)...])
    }

    private static let reservedCharacters: Set<Unicode.Scalar> = ["<", ">", ":", "\"", "/", "\\", "|", "?", "*"]
    private static let reservedNames: Set<String> = Set(["CON", "PRN", "AUX", "NUL"] + (1...9).flatMap { ["COM\($0)", "LPT\($0)"] })

    /// A file name that's safe on macOS and Windows, or nil when nothing usable is left.
    static func sanitize(_ name: String, maxLength: Int) -> String? {
        var scalars: [Unicode.Scalar] = []
        for scalar in name.unicodeScalars {
            if scalar.value < 0x20 || scalar.value == 0x7F { continue }
            scalars.append(reservedCharacters.contains(scalar) ? "_" : scalar)
        }
        func trimmable(_ s: Unicode.Scalar) -> Bool { s == "." || s.properties.isWhitespace }
        while let first = scalars.first, trimmable(first) { scalars.removeFirst() }
        while let last = scalars.last, trimmable(last) { scalars.removeLast() }
        guard !scalars.isEmpty else { return nil }

        let stem = String(String.UnicodeScalarView(scalars.prefix { $0 != "." }))
        if reservedNames.contains(stem.uppercased()) { scalars.insert("_", at: 0) }

        if scalars.count > maxLength {
            var ext: [Unicode.Scalar] = []
            if let dot = scalars.lastIndex(of: "."), dot > 0, scalars.count - dot <= 11 { ext = Array(scalars[dot...]) }
            var head = Array(scalars.prefix(maxLength - ext.count))
            while let last = head.last, trimmable(last) { head.removeLast() }
            scalars = head + ext
        }
        return String(String.UnicodeScalarView(scalars))
    }
}

/// robots.txt rules (RFC 9309) for one site, matched against Potion Dropper's product token.
public struct RobotsRules: Sendable {
    struct Rule: Sendable { let allow: Bool; let pattern: [UInt8] }
    struct Group: Sendable { var agents: [String] = []; var rules: [Rule] = [] }

    private enum Mode: Sendable { case rules([Group]), allowAll, disallowAll }
    private let mode: Mode

    public static let allowAll = RobotsRules(mode: .allowAll)
    public static let disallowAll = RobotsRules(mode: .disallowAll)

    private init(mode: Mode) { self.mode = mode }

    public init(text: String) {
        var groups: [Group] = []
        var lastWasAgent = false
        for rawLine in text.split(omittingEmptySubsequences: true, whereSeparator: \.isNewline) {
            let line = rawLine.split(separator: "#", maxSplits: 1, omittingEmptySubsequences: false).first ?? ""
            guard let colon = line.firstIndex(of: ":") else { continue }
            let key = line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            switch key {
            case "user-agent":
                if !lastWasAgent { groups.append(Group()) }
                groups[groups.count - 1].agents.append(Self.agentToken(value))
                lastWasAgent = true
            case "allow", "disallow":
                lastWasAgent = false
                guard !groups.isEmpty, !value.isEmpty else { continue }
                let pattern = Array(LinkSyntax.percentEncode(value[...], allowQuestionMark: true).utf8)
                groups[groups.count - 1].rules.append(Rule(allow: key == "allow", pattern: pattern))
            default:
                continue
            }
        }
        mode = .rules(groups)
    }

    /// "*" or the product token at the start of a user-agent line, lowercased ("PotionDropper/2.0" → "potiondropper").
    private static func agentToken(_ value: String) -> String {
        if value == "*" { return "*" }
        return String(value.prefix { $0.isASCII && ($0.isLetter || $0 == "-" || $0 == "_") }).lowercased()
    }

    /// Whether `path` (a normalized path with its query) may be fetched by `agent`.
    public func isAllowed(path: String, agent: String) -> Bool {
        guard path != "/robots.txt" else { return true }
        let groups: [Group]
        switch mode {
        case .allowAll: return true
        case .disallowAll: return false
        case .rules(let g): groups = g
        }
        let token = agent.lowercased()
        var selected = groups.filter { $0.agents.contains(token) }
        if selected.isEmpty { selected = groups.filter { $0.agents.contains("*") } }
        let target = Array(path.utf8)
        var best: Rule?
        for rule in selected.flatMap(\.rules) where Self.matches(rule.pattern, target) {
            if let current = best {
                if rule.pattern.count > current.pattern.count || (rule.pattern.count == current.pattern.count && rule.allow) { best = rule }
            } else {
                best = rule
            }
        }
        return best?.allow ?? true
    }

    /// `*` matches any run of bytes; a final `$` anchors the end; otherwise the pattern is a prefix.
    static func matches(_ pattern: [UInt8], _ path: [UInt8]) -> Bool {
        var p = pattern
        let anchored = p.last == UInt8(ascii: "$")
        if anchored { p.removeLast() } else { p.append(UInt8(ascii: "*")) }
        let star = UInt8(ascii: "*")
        var pi = 0, si = 0
        var starAt = -1, matchAt = 0
        while si < path.count {
            if pi < p.count, p[pi] != star, p[pi] == path[si] {
                pi += 1
                si += 1
            } else if pi < p.count, p[pi] == star {
                starAt = pi
                matchAt = si
                pi += 1
            } else if starAt >= 0 {
                pi = starAt + 1
                matchAt += 1
                si = matchAt
            } else {
                return false
            }
        }
        while pi < p.count, p[pi] == star { pi += 1 }
        return pi == p.count
    }
}
