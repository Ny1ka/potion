import AppKit

/// Reads the block structure (headings, list items, tables) back out of an attributed string and writes it
/// as Markdown or plain text.
struct DocumentStructure {
    enum Block {
        /// `heading` is 1–6, or 0 for body text.
        case paragraph(NSRange, heading: Int)
        case listItem(NSRange, depth: Int, ordered: Bool, number: Int)
        /// Rows → cells → the paragraphs in each cell.
        case table([[[NSRange]]])
    }

    let text: NSAttributedString
    private let string: NSString
    private let bodySize: CGFloat

    init(_ text: NSAttributedString) {
        self.text = text
        self.string = text.string as NSString
        self.bodySize = Self.mostCommonFontSize(text)
    }

    // MARK: - Blocks

    var blocks: [Block] {
        var paragraphs: [NSRange] = []
        string.enumerateSubstrings(in: NSRange(location: 0, length: string.length),
                                   options: [.byParagraphs, .substringNotRequired]) { _, range, _, _ in
            paragraphs.append(range)
        }

        var blocks: [Block] = []
        var listCounts: [ObjectIdentifier: Int] = [:]
        var index = 0
        while index < paragraphs.count {
            let range = paragraphs[index]
            let style = paragraphStyle(at: range)
            if let table = Self.table(of: style) {
                var grid: [Int: [Int: [NSRange]]] = [:]
                while index < paragraphs.count, let cell = Self.cell(of: paragraphStyle(at: paragraphs[index])), cell.table === table {
                    grid[cell.startingRow, default: [:]][cell.startingColumn, default: []].append(paragraphs[index])
                    index += 1
                }
                let columns = (grid.values.flatMap(\.keys).max() ?? 0) + 1
                let rows = grid.keys.sorted().map { row in (0..<columns).map { grid[row]?[$0] ?? [] } }
                blocks.append(.table(rows))
                continue
            }
            if let list = style?.textLists.last {
                let key = ObjectIdentifier(list)
                let number = listCounts[key] ?? list.startingItemNumber
                listCounts[key] = number + 1
                blocks.append(.listItem(withoutListMarker(range), depth: max(style!.textLists.count - 1, 0),
                                        ordered: !Self.unorderedMarkers.contains(list.markerFormat), number: number))
            } else if let typed = typedListItem(range) {
                blocks.append(typed)
            } else {
                blocks.append(.paragraph(range, heading: headingLevel(range, style: style)))
            }
            index += 1
        }
        return blocks
    }

    private static let unorderedMarkers: Set<NSTextList.MarkerFormat> = [.disc, .circle, .square, .hyphen, .box, .check, .diamond]

    private func paragraphStyle(at range: NSRange) -> NSParagraphStyle? {
        guard range.location < text.length else { return nil }
        return text.attribute(.paragraphStyle, at: range.location, effectiveRange: nil) as? NSParagraphStyle
    }

    private static func cell(of style: NSParagraphStyle?) -> NSTextTableBlock? {
        style?.textBlocks.first { $0 is NSTextTableBlock } as? NSTextTableBlock
    }

    private static func table(of style: NSParagraphStyle?) -> NSTextTable? { cell(of: style)?.table }

    /// The system readers write list markers into the text ("\t•\titem"). Markdown and plain text add their own.
    private func withoutListMarker(_ range: NSRange) -> NSRange {
        let line = string.substring(with: range)
        guard let marker = line.range(of: #"^\t?[^\t\n]{0,12}\t"#, options: .regularExpression) else { return range }
        let length = (String(line[marker]) as NSString).length
        return NSRange(location: range.location + length, length: range.length - length)
    }

    /// A list item typed as text rather than marked as a list: "\t•\tPoint" or "\t2.\tStep" (what the system
    /// Word writer produces, and common in hand-made documents).
    private func typedListItem(_ range: NSRange) -> Block? {
        let line = string.substring(with: range)
        guard let match = line.range(of: #"^(\t*)([•◦▪‣·–-]|\d{1,3}[.)])\t"#, options: .regularExpression) else { return nil }
        let marker = String(line[match])
        let tabs = marker.prefix { $0 == "\t" }.count
        let symbol = marker.trimmingCharacters(in: CharacterSet(charactersIn: "\t"))
        let number = Int(symbol.dropLast())
        let length = (marker as NSString).length
        return .listItem(NSRange(location: range.location + length, length: range.length - length),
                         depth: max(tabs - 1, 0), ordered: number != nil, number: number ?? 1)
    }

    /// The paragraph's heading level: from the document when it says so (HTML, Markdown), otherwise guessed
    /// from a short, bold paragraph set noticeably larger than the body text (Word and ODT files).
    private func headingLevel(_ range: NSRange, style: NSParagraphStyle?) -> Int {
        if let level = style?.headerLevel, level > 0 { return min(level, 6) }
        guard range.length > 0, range.length <= 200, bodySize > 0 else { return 0 }
        var smallest = CGFloat.greatestFiniteMagnitude
        var allBold = true
        text.enumerateAttribute(.font, in: range) { value, sub, _ in
            guard !string.substring(with: sub).trimmingCharacters(in: .whitespaces).isEmpty else { return }
            guard let font = value as? NSFont else { allBold = false; return }
            smallest = min(smallest, font.pointSize)
            if !font.fontDescriptor.symbolicTraits.contains(.bold) { allBold = false }
        }
        guard allBold, smallest < .greatestFiniteMagnitude else { return 0 }
        switch smallest / bodySize {
        case 1.75...: return 1
        case 1.4...: return 2
        case 1.15...: return 3
        default: return 0
        }
    }

    private static func mostCommonFontSize(_ text: NSAttributedString) -> CGFloat {
        var counts: [CGFloat: Int] = [:]
        text.enumerateAttribute(.font, in: NSRange(location: 0, length: text.length)) { value, range, _ in
            if let font = value as? NSFont { counts[font.pointSize, default: 0] += range.length }
        }
        return counts.max { $0.value < $1.value }?.key ?? 12
    }

    // MARK: - Plain text

    /// Text only: list markers as "•" or "1.", table cells separated by tabs.
    var plainText: String {
        var lines: [String] = []
        for block in blocks {
            switch block {
            case let .paragraph(range, _):
                lines.append(plain(range))
            case let .listItem(range, depth, ordered, number):
                lines.append(String(repeating: "    ", count: depth) + (ordered ? "\(number)." : "•") + " " + plain(range))
            case let .table(rows):
                for row in rows {
                    lines.append(row.map { $0.map(plain).joined(separator: " ") }.joined(separator: "\t"))
                }
            }
        }
        return lines.joined(separator: "\n") + "\n"
    }

    private func plain(_ range: NSRange) -> String {
        string.substring(with: range)
            .replacingOccurrences(of: "\u{FFFC}", with: "")
            .replacingOccurrences(of: "\u{2028}", with: "\n")
    }

    // MARK: - Markdown

    var markdown: String {
        var out: [String] = []
        var previousWasList = false
        for block in blocks {
            var chunk: String
            var isList = false
            switch block {
            case let .paragraph(range, heading):
                let body = inlineMarkdown(range, atLineStart: heading == 0, boldImplied: heading > 0)
                guard !body.trimmingCharacters(in: .whitespaces).isEmpty else { continue }
                chunk = heading > 0 ? String(repeating: "#", count: heading) + " " + body : body
            case let .listItem(range, depth, ordered, number):
                chunk = String(repeating: "    ", count: depth) + (ordered ? "\(number)." : "-") + " " + inlineMarkdown(range, atLineStart: false)
                isList = true
            case let .table(rows):
                guard let header = rows.first else { continue }
                func line(_ cells: [[NSRange]], header: Bool = false) -> String {
                    "| " + cells.map { cell in
                        cell.map { inlineMarkdown($0, atLineStart: false, boldImplied: header) }.joined(separator: "<br>")
                            .replacingOccurrences(of: "|", with: "\\|").replacingOccurrences(of: "\n", with: " ")
                    }.joined(separator: " | ") + " |"
                }
                chunk = ([line(header, header: true), "|" + String(repeating: " --- |", count: header.count)] + rows.dropFirst().map { line($0) })
                    .joined(separator: "\n")
            }
            // Blank line between blocks; consecutive list items stay together.
            if !out.isEmpty { out.append(isList && previousWasList ? "\n" : "\n\n") }
            out.append(chunk)
            previousWasList = isList
        }
        return out.joined() + "\n"
    }

    private struct InlineStyle: Equatable {
        var bold = false, italic = false, strike = false, code = false
        var link: String?
    }

    /// `boldImplied`: headings and table header cells are bold in Markdown already.
    private func inlineMarkdown(_ range: NSRange, atLineStart: Bool, boldImplied: Bool = false) -> String {
        var pieces: [(InlineStyle, String)] = []
        text.enumerateAttributes(in: range) { attrs, sub, _ in
            let piece = string.substring(with: sub).replacingOccurrences(of: "\u{FFFC}", with: "")
            guard !piece.isEmpty else { return }
            var style = InlineStyle()
            if let font = attrs[.font] as? NSFont {
                let traits = font.fontDescriptor.symbolicTraits
                style.bold = traits.contains(.bold) && !boldImplied
                style.italic = traits.contains(.italic)
                style.code = traits.contains(.monoSpace)
            }
            style.strike = ((attrs[.strikethroughStyle] as? Int) ?? 0) != 0
            if let url = attrs[.link] as? URL { style.link = url.absoluteString } else if let s = attrs[.link] as? String { style.link = s }
            if let last = pieces.last, last.0 == style { pieces[pieces.count - 1].1 += piece } else { pieces.append((style, piece)) }
        }

        var out = ""
        for (style, piece) in pieces {
            // Keep the emphasis markers next to the words: "**bold** ", not "**bold **".
            let core = piece.trimmingCharacters(in: .whitespaces)
            guard !core.isEmpty else { out += piece; continue }
            let lead = String(piece.prefix { $0 == " " || $0 == "\t" })
            let trail = String(piece.reversed().prefix { $0 == " " || $0 == "\t" })
            var md: String
            if style.code {
                let fence = core.contains("`") ? "``" : "`"
                md = fence + core + fence
            } else {
                md = Self.escapeMarkdown(core)
                if style.strike { md = "~~\(md)~~" }
                if style.italic { md = "*\(md)*" }
                if style.bold { md = "**\(md)**" }
            }
            if let link = style.link { md = "[\(md)](\(link.replacingOccurrences(of: " ", with: "%20")))" }
            out += lead + md + trail
        }
        out = out.replacingOccurrences(of: "\u{2028}", with: "  \n")
        // Text that would read as Markdown syntax at the start of a line.
        if atLineStart, out.range(of: #"^(?:#|>|[-+=]\s|\d+\.\s)"#, options: .regularExpression) != nil { out = "\\" + out }
        return out
    }

    private static func escapeMarkdown(_ text: String) -> String {
        var out = ""
        for ch in text {
            if "\\`*_[]<".contains(ch) { out.append("\\") }
            out.append(ch)
        }
        return out
    }
}
