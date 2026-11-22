import Foundation

/// The public source adapters, in no particular order (sources.json decides the matching order).
/// Adding one needs an entry in core/spec/sources.json and in docs/SOURCES.md.
public enum SourceAdapters {
    public static let builtIn: [any SourceAdapter] = [DirectLinkAdapter()]
}

/// Direct links to media files (docs/SOURCES.md, "Direct media links"): the link itself is an audio or video file,
/// served without a login or copy protection. It accepts any web link the adapters before it didn't claim:
/// - a media extension in the file name (`clip.mp4`) is accepted as that format;
/// - a non-media format (`photo.jpg`, `manual.pdf`) is rejected;
/// - anything else (`/download?id=4`, a page) needs a check: the download manager accepts it only if the server
///   answers with an audio or video file, and otherwise reports that Potion Dropper can't download from the site.
public struct DirectLinkAdapter: SourceAdapter {
    public let id = "direct"

    public init() {}

    public func match(_ link: LinkParts, source: SourceInfo, formats: FormatRegistry) -> SourceMatch? {
        guard let ext = link.fileExtension, let format = formats.format(forExtension: ext) else {
            return .accept(formatID: nil, needsCheck: true)
        }
        if source.categories.contains(format.category) { return .accept(formatID: format.id, needsCheck: false) }
        // Text extensions (.html, .txt) usually name a page, not a file: let the server's answer decide.
        return format.isText ? .accept(formatID: nil, needsCheck: true) : .reject(.notMedia)
    }
}
