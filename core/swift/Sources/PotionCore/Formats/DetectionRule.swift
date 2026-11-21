import Foundation

/// One content-detection rule from `formats.json`. See core/spec/README.md for the semantics.
struct DetectionRule: Decodable, Sendable {
    let format: String
    let conditions: [Condition]
    let extensionHint: [String]

    enum CodingKeys: String, CodingKey { case format, all, extensionHint }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        format = try c.decode(String.self, forKey: .format)
        conditions = try c.decode([Condition].self, forKey: .all)
        extensionHint = try c.decodeIfPresent([String].self, forKey: .extensionHint) ?? []
        if conditions.isEmpty {
            throw DecodingError.dataCorruptedError(forKey: .all, in: c, debugDescription: "Rule for \(format) has no conditions")
        }
    }

    func matches(_ header: [UInt8], extensionFormatID: String?) -> Bool {
        conditions.allSatisfy { $0.matches(header, extensionFormatID: extensionFormatID) }
    }
}

enum Condition: Decodable, Sendable {
    case bytes(offset: Int, value: [UInt8], mask: [UInt8]?)
    case contains(value: [UInt8], within: Int, ignoreCase: Bool)
    case extensionIs([String])

    private enum Keys: String, CodingKey { case offset, hex, ascii, mask, contains, within, ignoreCase, `extension` }
    private enum ValueKeys: String, CodingKey { case hex, ascii }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: Keys.self)
        if let ids = try c.decodeIfPresent([String].self, forKey: .extension) {
            self = .extensionIs(ids)
        } else if c.contains(.contains) {
            let v = try c.nestedContainer(keyedBy: ValueKeys.self, forKey: .contains)
            let value = try Condition.decodeValue(hex: v.decodeIfPresent(String.self, forKey: .hex),
                                                  ascii: v.decodeIfPresent(String.self, forKey: .ascii),
                                                  codingPath: c.codingPath)
            self = .contains(value: value,
                             within: try c.decode(Int.self, forKey: .within),
                             ignoreCase: try c.decodeIfPresent(Bool.self, forKey: .ignoreCase) ?? false)
        } else {
            let value = try Condition.decodeValue(hex: c.decodeIfPresent(String.self, forKey: .hex),
                                                  ascii: c.decodeIfPresent(String.self, forKey: .ascii),
                                                  codingPath: c.codingPath)
            var mask: [UInt8]?
            if let maskHex = try c.decodeIfPresent(String.self, forKey: .mask) {
                guard let m = Hex.bytes(maskHex), m.count == value.count else {
                    throw DecodingError.dataCorrupted(.init(codingPath: c.codingPath, debugDescription: "Mask must be hex of the same length as the value"))
                }
                mask = m
            }
            self = .bytes(offset: try c.decode(Int.self, forKey: .offset), value: value, mask: mask)
        }
    }

    private static func decodeValue(hex: String?, ascii: String?, codingPath: [CodingKey]) throws -> [UInt8] {
        if let hex, let bytes = Hex.bytes(hex), !bytes.isEmpty { return bytes }
        if let ascii, !ascii.isEmpty, ascii.allSatisfy(\.isASCII) { return Array(ascii.utf8) }
        throw DecodingError.dataCorrupted(.init(codingPath: codingPath, debugDescription: "Condition needs a non-empty hex or ASCII value"))
    }

    func matches(_ header: [UInt8], extensionFormatID: String?) -> Bool {
        switch self {
        case let .bytes(offset, value, mask):
            guard offset >= 0, offset + value.count <= header.count else { return false }
            for i in value.indices {
                let m = mask?[i] ?? 0xFF
                if header[offset + i] & m != value[i] & m { return false }
            }
            return true
        case let .contains(value, within, ignoreCase):
            let limit = min(within, header.count)
            guard value.count <= limit else { return false }
            let needle = ignoreCase ? value.map(Condition.lower) : value
            for start in 0...(limit - value.count) {
                var found = true
                for i in needle.indices {
                    let b = ignoreCase ? Condition.lower(header[start + i]) : header[start + i]
                    if b != needle[i] { found = false; break }
                }
                if found { return true }
            }
            return false
        case let .extensionIs(ids):
            guard let extensionFormatID else { return false }
            return ids.contains(extensionFormatID)
        }
    }

    private static func lower(_ b: UInt8) -> UInt8 { (b >= 0x41 && b <= 0x5A) ? b + 0x20 : b }
}

enum Hex {
    static func bytes(_ s: String) -> [UInt8]? {
        let chars = Array(s.utf8)
        guard chars.count % 2 == 0 else { return nil }
        var out: [UInt8] = []
        out.reserveCapacity(chars.count / 2)
        var i = 0
        while i < chars.count {
            guard let hi = nibble(chars[i]), let lo = nibble(chars[i + 1]) else { return nil }
            out.append(hi << 4 | lo)
            i += 2
        }
        return out
    }

    private static func nibble(_ c: UInt8) -> UInt8? {
        switch c {
        case 0x30...0x39: return c - 0x30
        case 0x41...0x46: return c - 0x41 + 10
        case 0x61...0x66: return c - 0x61 + 10
        default: return nil
        }
    }
}
