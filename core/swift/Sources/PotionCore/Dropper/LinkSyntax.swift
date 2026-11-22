import Foundation

/// Why a link can't be used. The raw values are the ids in core/spec/sources.json `messages`.
public enum LinkError: String, Error, CaseIterable, Sendable {
    case notALink, unsupportedScheme, credentials, tooLong, invalidHost, invalidPort, playlist, notMedia, unsupportedSite
}

/// A link split into normalized parts (core/spec/README.md, "Links"). Source adapters match against these.
public struct LinkParts: Hashable, Sendable {
    /// `http` or `https`.
    public let scheme: String
    /// Lowercase ASCII host (internationalized names in Punycode, IPv6 in brackets).
    public let host: String
    /// The port, unless it's the scheme's default.
    public let port: Int?
    /// Percent-encoded path, at least `/`.
    public let path: String
    /// Percent-encoded query without the `?`, or nil when there's none.
    public let query: String?

    /// The normalized link.
    public var string: String {
        "\(scheme)://\(host)\(port.map { ":\($0)" } ?? "")\(path)\(query.map { "?\($0)" } ?? "")"
    }

    /// The last path segment, percent-decoded ("" when the path ends with `/`).
    public var fileName: String {
        let segment = path.split(separator: "/", omittingEmptySubsequences: false).last.map(String.init) ?? ""
        return LinkSyntax.percentDecode(segment)
    }

    /// The file name's extension, lowercased. A name whose only dot is the first character has none.
    public var fileExtension: String? {
        let name = fileName
        guard let dot = name.lastIndex(of: "."), dot != name.startIndex else { return nil }
        let ext = name[name.index(after: dot)...].lowercased()
        return ext.isEmpty ? nil : ext
    }
}

/// Parses and normalizes links the same way core/dotnet does, without relying on the platform's URL parser,
/// so both apps accept, reject and de-duplicate exactly the same input.
enum LinkSyntax {
    static func normalize(_ raw: String, maxLength: Int) -> Result<LinkParts, LinkError> {
        var text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.count >= 2, text.hasPrefix("<"), text.hasSuffix(">") {
            text = String(text.dropFirst().dropLast()).trimmingCharacters(in: .whitespacesAndNewlines)
        }
        guard !text.isEmpty else { return .failure(.notALink) }
        guard text.utf8.count <= maxLength * 4 else { return .failure(.tooLong) }

        let scheme: String
        let rest: Substring
        if let separator = text.range(of: "://"), isScheme(text[..<separator.lowerBound]) {
            scheme = text[..<separator.lowerBound].lowercased()
            rest = text[separator.upperBound...]
        } else if hostInferable(text) {
            scheme = "https"
            rest = text[...]
        } else if let colon = text.firstIndex(of: ":"), isScheme(text[..<colon]) {
            return .failure(.unsupportedScheme)
        } else {
            return .failure(.notALink)
        }
        guard scheme == "http" || scheme == "https" else { return .failure(.unsupportedScheme) }

        let authorityEnd = rest.firstIndex(where: { "/?#".contains($0) }) ?? rest.endIndex
        let authority = rest[..<authorityEnd]
        var remainder = rest[authorityEnd...]
        if authority.contains("@") { return .failure(.credentials) }

        let host: String
        let portText: Substring?
        if authority.hasPrefix("[") {
            guard let close = authority.firstIndex(of: "]") else { return .failure(.invalidHost) }
            let literal = authority[authority.index(after: authority.startIndex)..<close]
            guard !literal.isEmpty, literal.contains(":"), literal.allSatisfy({ $0.isHexDigit || $0 == ":" || $0 == "." }) else {
                return .failure(.invalidHost)
            }
            host = "[\(literal.lowercased())]"
            let after = authority[authority.index(after: close)...]
            if after.isEmpty { portText = nil } else if after.hasPrefix(":") { portText = after.dropFirst() } else { return .failure(.invalidHost) }
        } else {
            let hostText: Substring
            if let colon = authority.lastIndex(of: ":") {
                hostText = authority[..<colon]
                portText = authority[authority.index(after: colon)...]
            } else {
                hostText = authority
                portText = nil
            }
            guard let normalized = normalizeHost(hostText) else { return .failure(.invalidHost) }
            host = normalized
        }

        var port: Int?
        if let portText, !portText.isEmpty {
            guard portText.count <= 5, portText.allSatisfy({ $0.isASCII && $0.isNumber }), let number = Int(portText), (1...65535).contains(number) else {
                return .failure(.invalidPort)
            }
            port = number
        }
        if (scheme == "http" && port == 80) || (scheme == "https" && port == 443) { port = nil }

        if let hash = remainder.firstIndex(of: "#") { remainder = remainder[..<hash] }
        var path: String
        var query: String?
        if let mark = remainder.firstIndex(of: "?") {
            path = percentEncode(remainder[..<mark], allowQuestionMark: false)
            query = percentEncode(remainder[remainder.index(after: mark)...], allowQuestionMark: true)
        } else {
            path = percentEncode(remainder, allowQuestionMark: false)
        }
        if path.isEmpty { path = "/" }
        if query?.isEmpty == true { query = nil }

        let parts = LinkParts(scheme: scheme, host: host, port: port, path: path, query: query)
        guard parts.string.utf8.count <= maxLength else { return .failure(.tooLong) }
        return .success(parts)
    }

    /// Whether a word in pasted text should be treated as a link: it has `://`, or it starts with something that
    /// is clearly a host name (`example.com/…`, `www.example.org`).
    static func isCandidate(_ token: String) -> Bool {
        token.contains("://") || hostInferable(token)
    }

    // MARK: - Parts

    private static func isScheme(_ text: Substring) -> Bool {
        guard let first = text.first, first.isASCII, first.isLetter else { return false }
        return text.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "+" || $0 == "-" || $0 == ".") }
    }

    /// A link typed without a scheme: an ASCII host with a dot whose last label is letters (a top-level domain),
    /// or a dotted IPv4 address, optionally followed by a numeric port.
    private static func hostInferable(_ text: String) -> Bool {
        let authority = text.prefix { !"/?#".contains($0) }
        guard !authority.contains("@") else { return false }
        var host = authority
        if let colon = authority.lastIndex(of: ":") {
            guard authority[authority.index(after: colon)...].allSatisfy({ $0.isASCII && $0.isNumber }) else { return false }
            host = authority[..<colon]
        }
        let labels = host.split(separator: ".", omittingEmptySubsequences: false)
        guard labels.count >= 2, labels.allSatisfy({ !$0.isEmpty && $0.allSatisfy(isHostCharacter) }) else { return false }
        if labels.count == 4, labels.allSatisfy({ $0.allSatisfy { $0.isNumber } }) { return true }
        let last = labels[labels.count - 1]
        return last.count >= 2 && last.allSatisfy { $0.isLetter }
    }

    private static func isHostCharacter(_ c: Character) -> Bool {
        c.isASCII && (c.isLetter || c.isNumber || c == "-" || c == "_")
    }

    /// Lowercase, NFC, Punycode for non-ASCII labels; each label 1–63 host characters without a leading or trailing
    /// hyphen; at most 253 characters. A single trailing dot is removed.
    private static func normalizeHost(_ text: Substring) -> String? {
        var raw = String(text)
        if raw.hasSuffix(".") { raw.removeLast() }
        guard !raw.isEmpty else { return nil }
        var labels: [String] = []
        for label in raw.lowercased().precomposedStringWithCanonicalMapping.split(separator: ".", omittingEmptySubsequences: false) {
            guard !label.isEmpty else { return nil }
            let ascii: String
            if label.unicodeScalars.allSatisfy(\.isASCII) {
                ascii = String(label)
            } else {
                guard let encoded = Punycode.encode(String(label)) else { return nil }
                ascii = "xn--" + encoded
            }
            guard ascii.count <= 63, ascii.first != "-", ascii.last != "-", ascii.allSatisfy(isHostCharacter) else { return nil }
            labels.append(ascii)
        }
        let host = labels.joined(separator: ".")
        return host.count <= 253 ? host : nil
    }

    private static let allowedBytes: Set<UInt8> = Set("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~!$&'()*+,;=:@/".utf8)
    private static let hexDigits = Array("0123456789ABCDEF".utf8)

    /// Keeps valid `%XX` escapes and characters allowed in a path (or query); encodes everything else as UTF-8 `%XX`.
    static func percentEncode(_ text: Substring, allowQuestionMark: Bool) -> String {
        let bytes = Array(text.utf8)
        var out: [UInt8] = []
        out.reserveCapacity(bytes.count)
        var i = 0
        while i < bytes.count {
            let b = bytes[i]
            if b == UInt8(ascii: "%"), i + 2 < bytes.count, isHex(bytes[i + 1]), isHex(bytes[i + 2]) {
                out.append(contentsOf: bytes[i...(i + 2)])
                i += 3
                continue
            }
            if allowedBytes.contains(b) || (allowQuestionMark && b == UInt8(ascii: "?")) {
                out.append(b)
            } else {
                out.append(contentsOf: [UInt8(ascii: "%"), hexDigits[Int(b >> 4)], hexDigits[Int(b & 0x0F)]])
            }
            i += 1
        }
        return String(decoding: out, as: UTF8.self)
    }

    /// Decodes `%XX` escapes as UTF-8 (invalid sequences become U+FFFD).
    static func percentDecode(_ text: String) -> String {
        let bytes = Array(text.utf8)
        var out: [UInt8] = []
        var i = 0
        while i < bytes.count {
            if bytes[i] == UInt8(ascii: "%"), i + 2 < bytes.count, let hi = hexValue(bytes[i + 1]), let lo = hexValue(bytes[i + 2]) {
                out.append(hi << 4 | lo)
                i += 3
            } else {
                out.append(bytes[i])
                i += 1
            }
        }
        return String(decoding: out, as: UTF8.self)
    }

    private static func isHex(_ b: UInt8) -> Bool { hexValue(b) != nil }

    private static func hexValue(_ b: UInt8) -> UInt8? {
        switch b {
        case UInt8(ascii: "0")...UInt8(ascii: "9"): b - UInt8(ascii: "0")
        case UInt8(ascii: "a")...UInt8(ascii: "f"): b - UInt8(ascii: "a") + 10
        case UInt8(ascii: "A")...UInt8(ascii: "F"): b - UInt8(ascii: "A") + 10
        default: nil
        }
    }
}

/// RFC 3492 Punycode encoder for internationalized host labels (the part after `xn--`).
enum Punycode {
    private static let base = 36, tMin = 1, tMax = 26, skew = 38, damp = 700

    static func encode(_ label: String) -> String? {
        let input = label.unicodeScalars.map { Int($0.value) }
        var output = input.filter { $0 < 0x80 }.map { Character(Unicode.Scalar(UInt8($0))) }
        let basicCount = output.count
        var handled = basicCount
        if basicCount > 0 { output.append("-") }

        var n = 0x80, delta = 0, bias = 72
        while handled < input.count {
            guard let m = input.filter({ $0 >= n }).min() else { return nil }
            let (product, overflow) = (m - n).multipliedReportingOverflow(by: handled + 1)
            guard !overflow else { return nil }
            delta += product
            n = m
            for c in input {
                if c < n { delta += 1 }
                if c == n {
                    var q = delta
                    var k = base
                    while true {
                        let t = k <= bias ? tMin : (k >= bias + tMax ? tMax : k - bias)
                        if q < t { break }
                        output.append(digit(t + (q - t) % (base - t)))
                        q = (q - t) / (base - t)
                        k += base
                    }
                    output.append(digit(q))
                    bias = adapt(delta, points: handled + 1, first: handled == basicCount)
                    delta = 0
                    handled += 1
                }
            }
            delta += 1
            n += 1
        }
        return String(output)
    }

    private static func digit(_ d: Int) -> Character {
        Character(Unicode.Scalar(UInt8(d < 26 ? 97 + d : 22 + d)))   // a–z, then 0–9
    }

    private static func adapt(_ delta: Int, points: Int, first: Bool) -> Int {
        var delta = first ? delta / damp : delta / 2
        delta += delta / points
        var k = 0
        while delta > ((base - tMin) * tMax) / 2 {
            delta /= base - tMin
            k += base
        }
        return k + (base - tMin + 1) * delta / (delta + skew)
    }
}
