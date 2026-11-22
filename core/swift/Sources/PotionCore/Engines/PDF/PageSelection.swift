import Foundation

/// Page selections typed by the user, such as "1-3, 5" or "4-". Rules are shared with Windows through
/// core/spec/tests/pages.json:
/// - items are separated by commas or semicolons; spaces are ignored; empty items are skipped
/// - an item is a page ("5"), a range ("1-3", also with an en dash), "4-" (to the last page) or "-2" (from page 1)
/// - pages come back 1-based, in the order typed, without duplicates; an empty selection means every page
/// - pages outside 1…pageCount, reversed ranges and anything else are invalid
public enum PageSelection {
    public static func pages(_ selection: String, pageCount: Int) throws -> [Int] {
        let invalid = ConversionError.invalidPages(selection: selection, pageCount: pageCount)
        let compact = selection.filter { !$0.isWhitespace }.replacingOccurrences(of: "–", with: "-")
        guard !compact.isEmpty else { return Array(1...max(pageCount, 1)).filter { $0 <= pageCount } }

        var pages: [Int] = []
        var seen = Set<Int>()
        for item in compact.split(whereSeparator: { $0 == "," || $0 == ";" }) {
            let range: ClosedRange<Int>
            if let dash = item.firstIndex(of: "-") {
                let start = item[..<dash], end = item[item.index(after: dash)...]
                guard !(start.isEmpty && end.isEmpty), !end.contains("-") else { throw invalid }
                guard let first = start.isEmpty ? 1 : number(start), let last = end.isEmpty ? pageCount : number(end),
                      first <= last else { throw invalid }
                range = first...last
            } else {
                guard let page = number(item) else { throw invalid }
                range = page...page
            }
            guard range.lowerBound >= 1, range.upperBound <= pageCount else { throw invalid }
            for page in range where seen.insert(page).inserted { pages.append(page) }
        }
        guard !pages.isEmpty else { throw invalid }
        return pages
    }

    private static func number(_ text: Substring) -> Int? {
        guard !text.isEmpty, text.allSatisfy(\.isASCII), text.allSatisfy(\.isNumber) else { return nil }
        return Int(text)
    }

    /// A short description for file names: [2,3,4] → "2–4", [1,3] → "1, 3".
    static func describe(_ pages: [Int]) -> String {
        var parts: [String] = []
        var index = 0
        while index < pages.count {
            var end = index
            while end + 1 < pages.count, pages[end + 1] == pages[end] + 1 { end += 1 }
            parts.append(end > index ? "\(pages[index])–\(pages[end])" : "\(pages[index])")
            index = end + 1
        }
        return parts.joined(separator: ", ")
    }
}
