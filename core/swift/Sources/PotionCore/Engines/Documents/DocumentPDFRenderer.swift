import AppKit

/// Lays out an attributed string on pages with TextKit and draws it into a PDF. Text stays real text
/// (searchable and selectable), links stay clickable, and images wider than the page are scaled to fit.
enum DocumentPDFRenderer {
    struct PageLayout: Equatable {
        var paperSize: CGSize
        var margins: NSEdgeInsets

        static func == (a: PageLayout, b: PageLayout) -> Bool {
            a.paperSize == b.paperSize && a.margins.top == b.margins.top && a.margins.left == b.margins.left
                && a.margins.bottom == b.margins.bottom && a.margins.right == b.margins.right
        }

        var contentSize: CGSize {
            CGSize(width: paperSize.width - margins.left - margins.right, height: paperSize.height - margins.top - margins.bottom)
        }

        static let letter = CGSize(width: 612, height: 792)
        static let a4 = CGSize(width: 595.28, height: 841.89)

        /// Letter in the regions that use it, A4 elsewhere, with 1-inch margins.
        static func standard(locale: Locale = .current) -> PageLayout {
            let letterRegions: Set<String> = ["US", "CA", "MX", "PH", "CL", "CO", "VE", "PR", "CR", "GT", "SV", "PA", "DO", "NI"]
            let region = locale.region?.identifier ?? "US"
            return PageLayout(paperSize: letterRegions.contains(region) ? letter : a4,
                              margins: NSEdgeInsets(top: 72, left: 72, bottom: 72, right: 72))
        }

        /// The page setup a document carries (Word, ODT, RTF), or the standard one when it has none or it's unusable.
        static func from(_ attributes: [NSAttributedString.DocumentAttributeKey: Any], locale: Locale = .current) -> PageLayout {
            var layout = standard(locale: locale)
            if let size = (attributes[.paperSize] as? NSValue)?.sizeValue, size.width >= 144, size.height >= 144,
               size.width <= 14_400, size.height <= 14_400 {
                layout.paperSize = size
            }
            func margin(_ key: NSAttributedString.DocumentAttributeKey) -> CGFloat? {
                (attributes[key] as? NSNumber).map { CGFloat($0.doubleValue) }
            }
            if let top = margin(.topMargin), let left = margin(.leftMargin), let bottom = margin(.bottomMargin),
               let right = margin(.rightMargin), [top, left, bottom, right].allSatisfy({ $0 >= 0 }) {
                let candidate = NSEdgeInsets(top: top, left: left, bottom: bottom, right: right)
                let content = PageLayout(paperSize: layout.paperSize, margins: candidate).contentSize
                if content.width >= layout.paperSize.width / 3, content.height >= layout.paperSize.height / 3 {
                    layout.margins = candidate
                }
            }
            return layout
        }
    }

    /// Writes the PDF and returns the number of pages.
    @discardableResult
    static func render(_ text: NSAttributedString, layout: PageLayout, title: String, to url: URL) throws -> Int {
        let content = layout.contentSize
        let storage = NSTextStorage(attributedString: fittingImages(text, in: content))
        let layoutManager = NSLayoutManager()
        storage.addLayoutManager(layoutManager)

        // One text container per page, until every glyph is placed.
        var containers: [NSTextContainer] = []
        var emptyPagesInARow = 0
        while containers.count < 10_000 {
            let container = NSTextContainer(size: content)
            container.lineFragmentPadding = 0
            layoutManager.addTextContainer(container)
            containers.append(container)
            let range = layoutManager.glyphRange(for: container)
            if NSMaxRange(range) >= layoutManager.numberOfGlyphs { break }
            emptyPagesInARow = range.length == 0 ? emptyPagesInARow + 1 : 0
            if emptyPagesInARow >= 2 { break }   // something can't be placed at all; stop rather than loop
        }

        var mediaBox = CGRect(origin: .zero, size: layout.paperSize)
        let info: [CFString: Any] = [kCGPDFContextTitle: title, kCGPDFContextCreator: "Potion"]
        guard let context = CGContext(url as CFURL, mediaBox: &mediaBox, info as CFDictionary) else {
            throw ConversionError.cannotWrite(folder: url.deletingLastPathComponent().lastPathComponent)
        }
        let origin = CGPoint(x: layout.margins.left, y: layout.margins.top)

        for container in containers {
            context.beginPDFPage(nil)
            let glyphs = layoutManager.glyphRange(for: container)

            context.saveGState()
            context.translateBy(x: 0, y: layout.paperSize.height)
            context.scaleBy(x: 1, y: -1)
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
            layoutManager.drawBackground(forGlyphRange: glyphs, at: origin)
            layoutManager.drawGlyphs(forGlyphRange: glyphs, at: origin)
            NSGraphicsContext.restoreGraphicsState()
            context.restoreGState()

            addLinks(layoutManager, storage: storage, container: container, glyphs: glyphs, origin: origin,
                     pageHeight: layout.paperSize.height, context: context)
            context.endPDFPage()
        }
        context.closePDF()
        return containers.count
    }

    /// Clickable areas for web and email links on one page.
    private static func addLinks(_ layoutManager: NSLayoutManager, storage: NSTextStorage, container: NSTextContainer,
                                 glyphs: NSRange, origin: CGPoint, pageHeight: CGFloat, context: CGContext) {
        let characters = layoutManager.characterRange(forGlyphRange: glyphs, actualGlyphRange: nil)
        storage.enumerateAttribute(.link, in: characters) { value, range, _ in
            let url = (value as? URL) ?? (value as? String).flatMap(URL.init(string:))
            guard let url, let scheme = url.scheme?.lowercased(), ["http", "https", "mailto"].contains(scheme) else { return }
            let linkGlyphs = layoutManager.glyphRange(forCharacterRange: range, actualCharacterRange: nil)
            layoutManager.enumerateEnclosingRects(forGlyphRange: linkGlyphs,
                                                  withinSelectedGlyphRange: NSRange(location: NSNotFound, length: 0),
                                                  in: container) { rect, _ in
                let pdfRect = CGRect(x: origin.x + rect.minX, y: pageHeight - (origin.y + rect.maxY),
                                     width: rect.width, height: rect.height)
                context.setURL(url as CFURL, for: pdfRect)
            }
        }
    }

    /// Scales images that are bigger than the page area down to fit (drawing size only, pixels are kept).
    static func fittingImages(_ text: NSAttributedString, in content: CGSize) -> NSAttributedString {
        let copy = NSMutableAttributedString(attributedString: text)
        copy.enumerateAttribute(.attachment, in: NSRange(location: 0, length: copy.length)) { value, _, _ in
            guard let attachment = value as? NSTextAttachment, let image = image(of: attachment) else { return }
            let size = image.size
            guard size.width > 0, size.height > 0, size.width > content.width || size.height > content.height else { return }
            let scale = min(content.width / size.width, content.height / size.height)
            let fitted = image.copy() as? NSImage ?? image
            fitted.size = CGSize(width: (size.width * scale).rounded(.down), height: (size.height * scale).rounded(.down))
            attachment.attachmentCell = NSTextAttachmentCell(imageCell: fitted)
        }
        return copy
    }

    static func image(of attachment: NSTextAttachment) -> NSImage? {
        if let image = attachment.image { return image }
        if let cell = attachment.attachmentCell as? NSTextAttachmentCell, let image = cell.image { return image }
        if let data = attachment.fileWrapper?.regularFileContents ?? attachment.contents { return NSImage(data: data) }
        return nil
    }
}
