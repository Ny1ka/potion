import AppKit

/// Images inside documents (text attachments), prepared for writers that store them as files: HTML (as
/// `data:` URIs), EPUB and DOCX (as files inside the zip).
enum DocumentImages {
    struct Image {
        let data: Data
        /// png, jpg or gif: formats every DOCX and EPUB reader supports. Anything else is converted to PNG.
        let fileExtension: String
        let mimeType: String
        /// Display size in points.
        let size: CGSize
    }

    static func image(of attachment: NSTextAttachment) -> Image? {
        let raw = attachment.fileWrapper?.regularFileContents ?? attachment.contents
        let picture = raw.flatMap(NSImage.init(data:)) ?? DocumentPDFRenderer.image(of: attachment)
        var size = attachment.bounds.size
        if size.width <= 0 || size.height <= 0 { size = picture?.size ?? .zero }
        guard size.width > 0, size.height > 0 else { return nil }

        if let raw, let kind = sniff(raw) {
            return Image(data: raw, fileExtension: kind.ext, mimeType: kind.mime, size: size)
        }
        guard let tiff = picture?.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
              let png = rep.representation(using: .png, properties: [:]) else { return nil }
        return Image(data: png, fileExtension: "png", mimeType: "image/png", size: size)
    }

    private static func sniff(_ data: Data) -> (ext: String, mime: String)? {
        if data.starts(with: [0x89, 0x50, 0x4E, 0x47]) { return ("png", "image/png") }
        if data.starts(with: [0xFF, 0xD8, 0xFF]) { return ("jpg", "image/jpeg") }
        if data.starts(with: [0x47, 0x49, 0x46, 0x38]) { return ("gif", "image/gif") }
        return nil
    }

    /// A copy of the text whose images are named image1.png, image2.jpg… The system HTML reader names every
    /// embedded image "unknown.png", and the HTML writer refers to images by name, so names must be unique.
    static func uniquelyNamed(_ text: NSAttributedString) -> NSAttributedString {
        let copy = NSMutableAttributedString(attributedString: text)
        var n = 0
        copy.enumerateAttribute(.attachment, in: NSRange(location: 0, length: copy.length)) { value, range, _ in
            guard let attachment = value as? NSTextAttachment, let image = image(of: attachment) else { return }
            n += 1
            let wrapper = FileWrapper(regularFileWithContents: image.data)
            wrapper.preferredFilename = "image\(n).\(image.fileExtension)"
            let renamed = NSTextAttachment(fileWrapper: wrapper)
            renamed.bounds = CGRect(origin: .zero, size: image.size)
            copy.addAttribute(.attachment, value: renamed, range: range)
        }
        return copy
    }

    /// The document's images by file name.
    static func byName(_ text: NSAttributedString) -> [String: Image] {
        var images: [String: Image] = [:]
        text.enumerateAttribute(.attachment, in: NSRange(location: 0, length: text.length)) { value, _, _ in
            guard let attachment = value as? NSTextAttachment,
                  let name = attachment.fileWrapper?.preferredFilename ?? attachment.fileWrapper?.filename,
                  let image = image(of: attachment) else { return }
            images[name] = image
        }
        return images
    }
}
