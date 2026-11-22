import Foundation

/// Renders Markdown (CommonMark plus GitHub tables and strikethrough) to HTML with Foundation's parser.
/// The parser marks each run of text with its block structure; this rebuilds the nested HTML elements from it.
enum MarkdownRenderer {
    /// A complete HTML document with a plain, readable stylesheet.
    static func document(_ markdown: String, title: String) -> String {
        """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <title>\(HTMLResources.escape(title))</title>
        <style>
        body { font-family: "Helvetica Neue", Helvetica, Arial, sans-serif; font-size: 12pt; line-height: 1.4; }
        pre, code { font-family: Menlo, Consolas, monospace; font-size: 10.5pt; }
        table { border-collapse: collapse; }
        th, td { border: 1px solid #999999; padding: 4px 8px; }
        blockquote { margin-left: 1.5em; color: #555555; }
        </style>
        </head>
        <body>
        \(body(markdown))
        </body>
        </html>
        """
    }

    /// The HTML for the Markdown content alone (what goes inside `<body>`).
    static func body(_ markdown: String) -> String {
        let options = AttributedString.MarkdownParsingOptions(
            allowsExtendedAttributes: false, interpretedSyntax: .full, failurePolicy: .returnPartiallyParsedIfPossible)
        guard let parsed = try? AttributedString(markdown: markdown, options: options) else {
            return "<pre>\(HTMLResources.escape(markdown))</pre>"
        }

        var html = ""
        var open: [PresentationIntent.IntentType] = []   // outermost first
        for run in parsed.runs {
            let blocks = Array((run.presentationIntent?.components ?? []).reversed())
            var shared = 0
            while shared < open.count, shared < blocks.count, open[shared].identity == blocks[shared].identity { shared += 1 }
            html += closeTags(open, from: shared)
            open.removeLast(open.count - shared)
            for index in shared..<blocks.count {
                html += openTag(blocks[index], next: blocks.indices.contains(index + 1) ? blocks[index + 1] : nil, parents: open)
                open.append(blocks[index])
            }
            html += inline(String(parsed[run.range].characters), run: run)
        }
        html += closeTags(open, from: 0)
        return html
    }

    private static func openTag(_ block: PresentationIntent.IntentType, next: PresentationIntent.IntentType?,
                                parents: [PresentationIntent.IntentType]) -> String {
        switch block.kind {
        case .paragraph: return "<p>"
        case let .header(level): return "<h\(min(max(level, 1), 6))>"
        case .orderedList:
            if case let .listItem(ordinal)? = next?.kind, ordinal != 1 { return "<ol start=\"\(ordinal)\">\n" }
            return "<ol>\n"
        case .unorderedList: return "<ul>\n"
        case .listItem: return "<li>"
        case let .codeBlock(language):
            let cls = language.map { " class=\"language-\(HTMLResources.escape($0))\"" } ?? ""
            return "<pre><code\(cls)>"
        case .blockQuote: return "<blockquote>\n"
        case .thematicBreak: return "<hr>\n"
        case .table: return "<table>\n"
        case .tableHeaderRow, .tableRow: return "<tr>"
        case let .tableCell(column):
            let isHeader = parents.last.map { if case .tableHeaderRow = $0.kind { return true } else { return false } } ?? false
            let tag = isHeader ? "th" : "td"
            if let align = alignment(column: column, parents: parents) { return "<\(tag) style=\"text-align: \(align)\">" }
            return "<\(tag)>"
        @unknown default: return ""
        }
    }

    /// Closes `open[from...]`, innermost first.
    private static func closeTags(_ open: [PresentationIntent.IntentType], from start: Int) -> String {
        var html = ""
        for index in (start..<open.count).reversed() {
            html += closeTag(open[index], parent: index > 0 ? open[index - 1] : nil)
        }
        return html
    }

    private static func closeTag(_ block: PresentationIntent.IntentType, parent: PresentationIntent.IntentType?) -> String {
        switch block.kind {
        case .paragraph: return "</p>\n"
        case let .header(level): return "</h\(min(max(level, 1), 6))>\n"
        case .orderedList: return "</ol>\n"
        case .unorderedList: return "</ul>\n"
        case .listItem: return "</li>\n"
        case .codeBlock: return "</code></pre>\n"
        case .blockQuote: return "</blockquote>\n"
        case .thematicBreak: return ""
        case .table: return "</table>\n"
        case .tableHeaderRow, .tableRow: return "</tr>\n"
        case .tableCell:
            if case .tableHeaderRow? = parent?.kind { return "</th>" }
            return "</td>"
        @unknown default: return ""
        }
    }

    private static func alignment(column: Int, parents: [PresentationIntent.IntentType]) -> String? {
        for parent in parents.reversed() {
            guard case let .table(columns) = parent.kind else { continue }
            guard columns.indices.contains(column) else { return nil }
            switch columns[column].alignment {
            case .center: return "center"
            case .right: return "right"
            default: return nil
            }
        }
        return nil
    }

    private static func inline(_ text: String, run: AttributedString.Runs.Run) -> String {
        let intent = run.inlinePresentationIntent ?? []
        if intent.contains(.inlineHTML) || intent.contains(.blockHTML) { return text }   // the author's own HTML
        if intent.contains(.lineBreak) { return "<br>\n" }
        if intent.contains(.softBreak) { return "\n" }
        if let image = run.imageURL {
            return "<img src=\"\(HTMLResources.escape(image.relativeString))\" alt=\"\(HTMLResources.escape(text))\">"
        }
        var html = HTMLResources.escape(text)
        if intent.contains(.code) { html = "<code>\(html)</code>" }
        if intent.contains(.strikethrough) { html = "<del>\(html)</del>" }
        if intent.contains(.emphasized) { html = "<em>\(html)</em>" }
        if intent.contains(.stronglyEmphasized) { html = "<strong>\(html)</strong>" }
        if let link = run.link { html = "<a href=\"\(HTMLResources.escape(link.relativeString))\">\(html)</a>" }
        return html
    }
}
