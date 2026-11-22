import Foundation
import UniformTypeIdentifiers

/// HTML clean-up around the system HTML reader and writer.
///
/// Potion never sends files or requests anywhere, so before an HTML (or Markdown) document is opened, every
/// reference that would make the reader fetch something from the network is removed: remote images,
/// stylesheets, frames, scripts and CSS `url(…)`s. Links (`<a href>`) stay, because they aren't fetched.
enum HTMLResources {
    static func escape(_ text: String) -> String {
        var out = ""
        out.reserveCapacity(text.count)
        for ch in text {
            switch ch {
            case "&": out += "&amp;"
            case "<": out += "&lt;"
            case ">": out += "&gt;"
            case "\"": out += "&quot;"
            default: out.append(ch)
            }
        }
        return out
    }

    private static let remote = #"^\s*(?:https?:|ftp:|//)"#

    /// Removes everything that would load a resource from the network, plus scripts and refresh redirects.
    static func removeRemoteResources(_ html: String) -> String {
        var s = html
        s = replace(#"<script\b[^>]*>.*?</script\s*>"#, in: s) { _ in "" }
        s = replace(#"</?(?:iframe|frame|frameset|object|embed|applet|audio|video|source|track|base|portal)\b[^>]*>"#, in: s) { _ in "" }
        s = replace(#"<meta\b[^>]*http-equiv[^>]*>"#, in: s) { _ in "" }
        // <link> tags (stylesheets, icons, preloads) pointing at the network.
        s = replace(#"<link\b[^>]*>"#, in: s) { tag in
            hasRemoteAttribute(tag.full, names: "href|imagesrcset") ? "" : tag.full
        }
        // Remote values in attributes that load something.
        s = replace(#"(\s(?:src|srcset|lowsrc|dynsrc|poster|background|data|imagesrcset|xlink:href)\s*=\s*)("[^"]*"|'[^']*'|[^\s>]+)"#, in: s) { match in
            let value = unquote(match.group(2))
            let loadsRemote = value.range(of: remote, options: .regularExpression) != nil || value.contains("://")
            return loadsRemote ? match.group(1) + "\"\"" : match.full
        }
        // CSS: url(https://…) and @import of remote stylesheets.
        s = replace(#"@import\s+(?:url\()?\s*['"]?\s*(?:https?:|ftp:|//)[^;]*;?"#, in: s) { _ in "" }
        s = replace(#"url\(\s*['"]?\s*(?:https?:|ftp:|//)[^)]*\)"#, in: s) { _ in "none" }
        return s
    }

    /// Makes relative image paths resolve against the document's folder.
    static func settingBase(_ html: String, directory: URL) -> String {
        let base = "<base href=\"\(escape(directory.absoluteURL.absoluteString))\">"
        if let head = html.range(of: #"<head\b[^>]*>"#, options: [.regularExpression, .caseInsensitive]) {
            return html.replacingCharacters(in: head.upperBound..<head.upperBound, with: base)
        }
        return base + html
    }

    /// Replaces `<img src>` paths to local image files with `data:` URIs, so an HTML file made from
    /// Markdown stays a single self-contained file. Remote images are left as written.
    static func embeddingLocalImages(_ html: String, directory: URL, fileManager: FileManager = .default) -> String {
        replace(#"(<img\b[^>]*\ssrc\s*=\s*)("[^"]*"|'[^']*')"#, in: html) { match in
            let value = unquote(match.group(2))
            guard !value.isEmpty, !value.hasPrefix("data:"), value.range(of: remote, options: .regularExpression) == nil,
                  let url = URL(string: value, relativeTo: directory) ?? URL(string: value.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? "", relativeTo: directory),
                  url.isFileURL, let data = imageData(at: url.standardizedFileURL, fileManager: fileManager) else {
                return match.full
            }
            return match.group(1) + "\"\(dataURI(data.data, mimeType: data.mimeType))\""
        }
    }

    /// The system HTML writer refers to images by file name (`file:///photo.png`) without saving them.
    /// This swaps those references for `data:` URIs made from the document's attachments.
    static func embeddingAttachments(_ html: String, images: [String: (data: Data, mimeType: String)]) -> String {
        guard !images.isEmpty else { return html }
        return replace(#"(<img\b[^>]*\ssrc\s*=\s*)"file:///([^"]*)""#, in: html) { match in
            let name = match.group(2).removingPercentEncoding ?? match.group(2)
            guard let image = images[name] else { return match.full }
            return match.group(1) + "\"\(dataURI(image.data, mimeType: image.mimeType))\""
        }
    }

    static func dataURI(_ data: Data, mimeType: String) -> String {
        "data:\(mimeType);base64,\(data.base64EncodedString())"
    }

    /// Image files only, up to 50 MB, so a document can't pull arbitrary files into its output.
    private static func imageData(at url: URL, fileManager: FileManager) -> (data: Data, mimeType: String)? {
        guard let type = UTType(filenameExtension: url.pathExtension), type.conforms(to: .image),
              let mime = type.preferredMIMEType,
              let size = (try? fileManager.attributesOfItem(atPath: url.path))?[.size] as? Int, size <= 50_000_000,
              let data = fileManager.contents(atPath: url.path) else { return nil }
        return (data, mime)
    }

    private static func hasRemoteAttribute(_ tag: String, names: String) -> Bool {
        var found = false
        _ = replace(#"\s(?:\#(names))\s*=\s*("[^"]*"|'[^']*'|[^\s>]+)"#, in: tag) { match in
            let value = unquote(match.group(1))
            if value.range(of: remote, options: .regularExpression) != nil || value.contains("://") { found = true }
            return match.full
        }
        return found
    }

    private static func unquote(_ value: String) -> String {
        guard let first = value.first, first == "\"" || first == "'", value.count >= 2 else { return value }
        return String(value.dropFirst().dropLast())
    }

    // MARK: - Regex helper

    struct Match {
        let full: String
        let groups: [String]
        func group(_ index: Int) -> String { groups.indices.contains(index - 1) ? groups[index - 1] : "" }
    }

    /// Case-insensitive, `.` matches newlines. The closure returns each match's replacement.
    static func replace(_ pattern: String, in text: String, with transform: (Match) -> String) -> String {
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive, .dotMatchesLineSeparators]) else {
            return text
        }
        let ns = text as NSString
        var out = ""
        var last = 0
        for result in regex.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
            out += ns.substring(with: NSRange(location: last, length: result.range.location - last))
            let groups = (1..<max(result.numberOfRanges, 1)).map { i -> String in
                let r = result.range(at: i)
                return r.location == NSNotFound ? "" : ns.substring(with: r)
            }
            out += transform(Match(full: ns.substring(with: result.range), groups: groups))
            last = result.range.location + result.range.length
        }
        out += ns.substring(from: last)
        return out
    }
}
