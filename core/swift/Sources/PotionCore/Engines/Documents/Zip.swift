import Compression
import Foundation

/// A small zip reader and writer for the zip-based document formats (DOCX, ODT, EPUB). Supports stored and
/// deflated entries, which is what those formats use. No Zip64 (entries and archives over 4 GB).
struct ZipArchive {
    struct Entry {
        let name: String
        let method: UInt16
        let crc: UInt32
        let compressedSize: Int
        let size: Int
        let localHeaderOffset: Int
        /// Zip-level password protection.
        let isEncrypted: Bool
    }

    enum ZipError: Error { case invalid, unsupported, tooLarge }

    /// Refuses to inflate a single entry beyond this, so a malicious file can't exhaust memory.
    static let maxEntrySize = 512 * 1024 * 1024

    let data: Data
    let entries: [Entry]
    private let index: [String: Int]

    init(data: Data) throws {
        self.data = data
        let bytes = data.count
        guard bytes >= 22 else { throw ZipError.invalid }

        // End of central directory: scan back over a possible archive comment (at most 64 KB).
        var eocd = -1
        var position = bytes - 22
        let lowest = max(0, bytes - 22 - 65_535)
        while position >= lowest {
            if Self.u32(data, position) == 0x0605_4B50 { eocd = position; break }
            position -= 1
        }
        guard eocd >= 0 else { throw ZipError.invalid }
        let count = Int(Self.u16(data, eocd + 10))
        let directoryOffset = Int(Self.u32(data, eocd + 16))
        guard directoryOffset != 0xFFFF_FFFF else { throw ZipError.unsupported }

        var entries: [Entry] = []
        var index: [String: Int] = [:]
        var cursor = directoryOffset
        for _ in 0..<count {
            guard cursor + 46 <= bytes, Self.u32(data, cursor) == 0x0201_4B50 else { throw ZipError.invalid }
            let flags = Self.u16(data, cursor + 8)
            let nameLength = Int(Self.u16(data, cursor + 28))
            let extraLength = Int(Self.u16(data, cursor + 30))
            let commentLength = Int(Self.u16(data, cursor + 32))
            guard cursor + 46 + nameLength <= bytes else { throw ZipError.invalid }
            let nameData = data.subdata(in: data.startIndex + cursor + 46 ..< data.startIndex + cursor + 46 + nameLength)
            let name = String(data: nameData, encoding: .utf8) ?? String(decoding: nameData, as: UTF8.self)
            let entry = Entry(name: name, method: Self.u16(data, cursor + 10), crc: Self.u32(data, cursor + 16),
                              compressedSize: Int(Self.u32(data, cursor + 20)), size: Int(Self.u32(data, cursor + 24)),
                              localHeaderOffset: Int(Self.u32(data, cursor + 42)), isEncrypted: flags & 1 != 0)
            guard entry.compressedSize != 0xFFFF_FFFF, entry.size != 0xFFFF_FFFF else { throw ZipError.unsupported }
            index[name] = entries.count
            entries.append(entry)
            cursor += 46 + nameLength + extraLength + commentLength
        }
        self.entries = entries
        self.index = index
    }

    init(contentsOf url: URL) throws {
        try self.init(data: Data(contentsOf: url, options: .alwaysMapped))
    }

    func contains(_ name: String) -> Bool { index[name] != nil }

    /// The entry's contents, or nil when there's no such entry.
    func read(_ name: String) throws -> Data? {
        guard let i = index[name] else { return nil }
        return try read(entries[i])
    }

    func read(_ entry: Entry) throws -> Data {
        guard !entry.isEncrypted else { throw ZipError.unsupported }
        guard entry.size <= Self.maxEntrySize else { throw ZipError.tooLarge }
        let local = entry.localHeaderOffset
        guard local + 30 <= data.count, Self.u32(data, local) == 0x0403_4B50 else { throw ZipError.invalid }
        let start = local + 30 + Int(Self.u16(data, local + 26)) + Int(Self.u16(data, local + 28))
        guard start + entry.compressedSize <= data.count else { throw ZipError.invalid }
        let raw = data.subdata(in: data.startIndex + start ..< data.startIndex + start + entry.compressedSize)

        let contents: Data
        switch entry.method {
        case 0: contents = raw
        case 8: contents = try Self.inflate(raw, size: entry.size)
        default: throw ZipError.unsupported
        }
        guard contents.count == entry.size, CRC32.checksum(contents) == entry.crc else { throw ZipError.invalid }
        return contents
    }

    private static func inflate(_ raw: Data, size: Int) throws -> Data {
        guard size > 0 else { return Data() }
        var out = Data(count: size)
        let written = out.withUnsafeMutableBytes { dst in
            raw.withUnsafeBytes { src in
                compression_decode_buffer(dst.bindMemory(to: UInt8.self).baseAddress!, size,
                                          src.bindMemory(to: UInt8.self).baseAddress!, raw.count, nil, COMPRESSION_ZLIB)
            }
        }
        guard written == size else { throw ZipError.invalid }
        return out
    }

    static func u16(_ d: Data, _ offset: Int) -> UInt16 {
        guard offset + 2 <= d.count else { return 0 }
        let i = d.startIndex + offset
        return UInt16(d[i]) | UInt16(d[i + 1]) << 8
    }

    static func u32(_ d: Data, _ offset: Int) -> UInt32 {
        guard offset + 4 <= d.count else { return 0 }
        let i = d.startIndex + offset
        return UInt32(d[i]) | UInt32(d[i + 1]) << 8 | UInt32(d[i + 2]) << 16 | UInt32(d[i + 3]) << 24
    }
}

/// Builds a zip archive in memory. Entries are written in the order they're added (EPUB needs its
/// uncompressed `mimetype` entry first).
struct ZipWriter {
    private var body = Data()
    private var directory = Data()
    private var count = 0

    mutating func add(_ name: String, _ contents: Data, compress: Bool = true) {
        let nameData = Data(name.utf8)
        let crc = CRC32.checksum(contents)
        var method: UInt16 = 0
        var stored = contents
        if compress, contents.count > 64, let deflated = Self.deflate(contents), deflated.count < contents.count {
            method = 8
            stored = deflated
        }
        let offset = body.count
        // DOS date/time 1980-01-01 00:00: fixed, so the same input gives the same file.
        let time: UInt16 = 0, date: UInt16 = (0 << 9) | (1 << 5) | 1

        var local = Data()
        local.le32(0x0403_4B50); local.le16(20); local.le16(0x0800); local.le16(method)
        local.le16(time); local.le16(date); local.le32(crc)
        local.le32(UInt32(stored.count)); local.le32(UInt32(contents.count))
        local.le16(UInt16(nameData.count)); local.le16(0)
        body += local + nameData + stored

        var central = Data()
        central.le32(0x0201_4B50); central.le16(20); central.le16(20); central.le16(0x0800); central.le16(method)
        central.le16(time); central.le16(date); central.le32(crc)
        central.le32(UInt32(stored.count)); central.le32(UInt32(contents.count))
        central.le16(UInt16(nameData.count)); central.le16(0); central.le16(0); central.le16(0); central.le16(0)
        central.le32(0); central.le32(UInt32(offset))
        directory += central + nameData
        count += 1
    }

    func finish() -> Data {
        var end = Data()
        end.le32(0x0605_4B50); end.le16(0); end.le16(0)
        end.le16(UInt16(count)); end.le16(UInt16(count))
        end.le32(UInt32(directory.count)); end.le32(UInt32(body.count)); end.le16(0)
        return body + directory + end
    }

    private static func deflate(_ contents: Data) -> Data? {
        let capacity = contents.count + 1024
        var out = Data(count: capacity)
        let written = out.withUnsafeMutableBytes { dst in
            contents.withUnsafeBytes { src in
                compression_encode_buffer(dst.bindMemory(to: UInt8.self).baseAddress!, capacity,
                                          src.bindMemory(to: UInt8.self).baseAddress!, contents.count, nil, COMPRESSION_ZLIB)
            }
        }
        guard written > 0 else { return nil }
        return out.prefix(written)
    }
}

enum CRC32 {
    private static let table: [UInt32] = (0..<256).map { n -> UInt32 in
        var c = UInt32(n)
        for _ in 0..<8 { c = c & 1 != 0 ? 0xEDB8_8320 ^ (c >> 1) : c >> 1 }
        return c
    }

    static func checksum(_ data: Data) -> UInt32 {
        var crc: UInt32 = 0xFFFF_FFFF
        data.withUnsafeBytes { buffer in
            for byte in buffer { crc = table[Int((crc ^ UInt32(byte)) & 0xFF)] ^ (crc >> 8) }
        }
        return crc ^ 0xFFFF_FFFF
    }
}

private extension Data {
    mutating func le16(_ v: UInt16) { append(UInt8(v & 0xFF)); append(UInt8(v >> 8)) }
    mutating func le32(_ v: UInt32) { for shift in stride(from: 0, to: 32, by: 8) { append(UInt8((v >> UInt32(shift)) & 0xFF)) } }
}
