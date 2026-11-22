import AppKit
import PDFKit
import UniformTypeIdentifiers

/// Converts documents with the macOS text system. Handles the matrix operations `convertDocument` and
/// `documentToPdf`.
///
/// Reading: RTF, DOC, DOCX and ODT use the system readers; HTML uses the system HTML reader after removing
/// anything that would load from the network; Markdown is rendered to HTML first; EPUB chapters are joined
/// into one HTML document; TXT is decoded (UTF-8, UTF-16 or Windows Latin 1). Writing: DOCX and EPUB use our
/// own writers; RTF, DOC, ODT and HTML use the system writers (HTML with its images embedded); Markdown and
/// TXT keep headings, lists and tables; PDF is laid out on pages with searchable text and working links.
public final class DocumentEngine: Sendable {
    let matrix: ConversionMatrix

    public init(matrix: ConversionMatrix = .shared) {
        self.matrix = matrix
    }

    static let supported: Set<String> = ["docx", "doc", "odt", "rtf", "txt", "html", "md", "epub", "pdf"]

    static let systemTypes: [String: NSAttributedString.DocumentType] = [
        "docx": .officeOpenXML, "doc": .docFormat, "odt": .openDocument, "rtf": .rtf, "html": .html,
    ]

    public func convert(_ input: URL, from inputFormat: FileFormat, to outputFormat: FileFormat,
                        outputDirectory: URL) async throws -> URL {
        guard let conversion = matrix.conversion(from: inputFormat.id, to: outputFormat.id),
              conversion.operation == .convertDocument || conversion.operation == .documentToPdf,
              Self.supported.contains(inputFormat.id), Self.supported.contains(outputFormat.id) else {
            throw ConversionError.unsupported(from: inputFormat.name, to: outputFormat.name)
        }
        try OutputFiles.checkWritable(outputDirectory)
        if Self.isProtected(input, formatID: inputFormat.id) { throw ConversionError.copyProtected }

        let inputName = input.lastPathComponent
        let baseName = input.deletingPathExtension().lastPathComponent
        let ext = outputFormat.preferredExtension
        let temporary = OutputFiles.temporaryURL(in: outputDirectory, fileExtension: ext)
        defer { OutputFiles.removeIfPresent(temporary) }

        let hasText: Bool
        if inputFormat.id == "md", outputFormat.id == "html" {
            // Straight from Markdown gives cleaner HTML than going through the text system.
            let markdown = try Self.readText(input)
            let html = HTMLResources.embeddingLocalImages(MarkdownRenderer.document(markdown, title: baseName),
                                                          directory: input.deletingLastPathComponent())
            try Self.write(Data(html.utf8), to: temporary, outputDirectory: outputDirectory)
            hasText = !markdown.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        } else {
            let document = try await load(input, format: inputFormat)
            hasText = !document.text.string.trimmingCharacters(in: .whitespacesAndNewlines.union(["\u{FFFC}"])).isEmpty
            try write(document, as: outputFormat.id, title: baseName, to: temporary, outputDirectory: outputDirectory)
        }

        guard Self.verify(temporary, formatID: outputFormat.id, expectText: hasText) else {
            throw ConversionError.verificationFailed(fileName: inputName)
        }
        return try OutputFiles.finalize(temporary, in: outputDirectory, baseName: baseName, fileExtension: ext)
    }

    // MARK: - Reading

    struct Document {
        var text: NSAttributedString
        var attributes: [NSAttributedString.DocumentAttributeKey: Any]
    }

    /// Lets a document cross from the main actor (where the HTML reader has to run) back to the caller.
    private struct Transfer: @unchecked Sendable { let document: Document }

    func load(_ input: URL, format: FileFormat) async throws -> Document {
        let name = input.lastPathComponent
        switch format.id {
        case "txt":
            let text = try Self.readText(input)
            return Document(text: NSAttributedString(string: text, attributes: Self.plainTextAttributes), attributes: [:])
        case "md":
            let html = MarkdownRenderer.document(try Self.readText(input), title: input.deletingPathExtension().lastPathComponent)
            return try await Self.readHTML(html, directory: input.deletingLastPathComponent(), inputName: name)
        case "html":
            return try await Self.readHTML(try Self.readText(input), directory: input.deletingLastPathComponent(), inputName: name)
        case "epub":
            let book: EPUB.Book
            do { book = try EPUB.read(ZipArchive(contentsOf: input)) } catch {
                throw ConversionError.unreadable(fileName: name)
            }
            return try await Self.readHTML(book.html, directory: input.deletingLastPathComponent(), inputName: name)
        default:
            guard let type = Self.systemTypes[format.id] else {
                throw ConversionError.unsupported(from: format.name, to: "")
            }
            var attributes: NSDictionary?
            do {
                let text = try NSAttributedString(url: input, options: [.documentType: type], documentAttributes: &attributes)
                return Document(text: text, attributes: Self.documentAttributes(attributes))
            } catch {
                throw ConversionError.unreadable(fileName: name)
            }
        }
    }

    static var plainTextAttributes: [NSAttributedString.Key: Any] {
        [.font: NSFont.userFont(ofSize: 12) ?? NSFont.systemFont(ofSize: 12)]
    }

    /// The system HTML reader must run on the main thread. Remote resources are removed first, so opening
    /// a document never touches the network.
    static func readHTML(_ html: String, directory: URL, inputName: String) async throws -> Document {
        let prepared = HTMLResources.settingBase(HTMLResources.removeRemoteResources(html), directory: directory)
        let transfer: Transfer = try await MainActor.run {
            var attributes: NSDictionary?
            do {
                let text = try NSAttributedString(
                    data: Data(prepared.utf8),
                    options: [.documentType: NSAttributedString.DocumentType.html,
                              .characterEncoding: String.Encoding.utf8.rawValue,
                              .timeout: 30],
                    documentAttributes: &attributes)
                return Transfer(document: Document(text: text, attributes: documentAttributes(attributes)))
            } catch {
                throw ConversionError.unreadable(fileName: inputName)
            }
        }
        return transfer.document
    }

    private static func documentAttributes(_ dictionary: NSDictionary?) -> [NSAttributedString.DocumentAttributeKey: Any] {
        var out: [NSAttributedString.DocumentAttributeKey: Any] = [:]
        dictionary?.forEach { key, value in
            if let key = key as? String { out[NSAttributedString.DocumentAttributeKey(rawValue: key)] = value }
        }
        return out
    }

    /// Text files: UTF-8 (with or without a byte-order mark), UTF-16 with a byte-order mark, otherwise
    /// Windows Latin 1 (what older Windows text files use).
    static func readText(_ url: URL) throws -> String {
        guard let data = FileManager.default.contents(atPath: url.path) else {
            throw ConversionError.unreadable(fileName: url.lastPathComponent)
        }
        if let text = decodeText(data) { return text }
        throw ConversionError.unreadable(fileName: url.lastPathComponent)
    }

    static func decodeText(_ data: Data) -> String? {
        let bytes = [UInt8](data.prefix(3))
        if bytes.starts(with: [0xEF, 0xBB, 0xBF]) { return String(data: data.dropFirst(3), encoding: .utf8) }
        if bytes.starts(with: [0xFF, 0xFE]) { return String(data: data.dropFirst(2), encoding: .utf16LittleEndian) }
        if bytes.starts(with: [0xFE, 0xFF]) { return String(data: data.dropFirst(2), encoding: .utf16BigEndian) }
        return String(data: data, encoding: .utf8) ?? String(data: data, encoding: .windowsCP1252)
    }

    /// Files that can't be opened without a password or a DRM licence:
    /// - Word files saved with a password: an OLE container with an `EncryptedPackage` stream instead of a zip.
    /// - Zip-based files (DOCX, ODT, EPUB) with password-protected entries.
    /// - ODT files saved with a password (encryption data in the manifest).
    /// - EPUBs with DRM (see `EPUB.isProtected`).
    static func isProtected(_ url: URL, formatID: String) -> Bool {
        guard let data = try? Data(contentsOf: url, options: .alwaysMapped) else { return false }
        if data.starts(with: [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]) {
            let marker = Data("EncryptedPackage".utf16.flatMap { [UInt8($0 & 0xFF), UInt8($0 >> 8)] })
            return data.range(of: marker) != nil
        }
        guard ["docx", "odt", "epub"].contains(formatID), let zip = try? ZipArchive(data: data) else { return false }
        if zip.entries.contains(where: \.isEncrypted) { return true }
        if formatID == "odt", let manifest = try? zip.read("META-INF/manifest.xml"),
           String(decoding: manifest, as: UTF8.self).contains("encryption-data") { return true }
        return formatID == "epub" && EPUB.isProtected(zip)
    }

    // MARK: - Writing

    /// Document settings worth carrying into the output: page setup and title.
    private static let carriedAttributes: [NSAttributedString.DocumentAttributeKey] = [
        .paperSize, .topMargin, .leftMargin, .bottomMargin, .rightMargin,
    ]

    func write(_ document: Document, as formatID: String, title: String, to url: URL, outputDirectory: URL) throws {
        let text = formatID == "html" || formatID == "epub" ? DocumentImages.uniquelyNamed(document.text) : document.text
        let full = NSRange(location: 0, length: text.length)
        let data: Data
        switch formatID {
        case "txt":
            data = Data(DocumentStructure(text).plainText.utf8)
        case "md":
            data = Data(DocumentStructure(text).markdown.utf8)
        case "docx":
            data = DocxWriter(text, layout: .from(document.attributes), title: title).data()
        case "epub":
            do {
                let html = try text.data(from: full, documentAttributes: [.documentType: NSAttributedString.DocumentType.html, .title: title])
                data = try EPUB.write(html: String(decoding: html, as: UTF8.self), title: title,
                                      language: Locale.current.language.languageCode?.identifier ?? "en",
                                      images: DocumentImages.byName(text))
            } catch {
                throw ConversionError.conversionFailed(fileName: title, detail: "\(error)")
            }
        case "pdf":
            do {
                try DocumentPDFRenderer.render(text, layout: .from(document.attributes), title: title, to: url)
            } catch let error as ConversionError {
                throw error
            } catch {
                throw ConversionError.cannotWrite(folder: outputDirectory.lastPathComponent)
            }
            return
        default:
            guard let type = Self.systemTypes[formatID] else { throw ConversionError.unsupported(from: "", to: formatID) }
            var attributes: [NSAttributedString.DocumentAttributeKey: Any] = [.documentType: type, .title: title]
            for key in Self.carriedAttributes { attributes[key] = document.attributes[key] }
            do {
                data = try text.data(from: full, documentAttributes: attributes)
            } catch {
                throw ConversionError.conversionFailed(fileName: title, detail: "\(error)")
            }
            if formatID == "html" {
                let html = String(decoding: data, as: UTF8.self)
                try Self.write(Data(HTMLResources.embeddingAttachments(html, images: DocumentImages.byName(text)).utf8),
                               to: url, outputDirectory: outputDirectory)
                return
            }
        }
        try Self.write(data, to: url, outputDirectory: outputDirectory)
    }

    private static func write(_ data: Data, to url: URL, outputDirectory: URL) throws {
        // withoutOverwriting: the temporary name is new, so an existing file here means something is wrong.
        do { try data.write(to: url, options: .withoutOverwriting) } catch {
            throw ConversionError.cannotWrite(folder: outputDirectory.lastPathComponent)
        }
    }

    // MARK: - Verifying

    /// Re-opens the output to check it's a valid file of the right kind, with text in it if the input had text.
    static func verify(_ url: URL, formatID: String, expectText: Bool) -> Bool {
        switch formatID {
        case "pdf":
            guard let pdf = PDFDocument(url: url), pdf.pageCount > 0 else { return false }
            return !expectText || !(pdf.string ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        case "txt", "md":
            guard let data = FileManager.default.contents(atPath: url.path), let text = String(data: data, encoding: .utf8) else { return false }
            return !expectText || !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        case "html":
            guard let data = FileManager.default.contents(atPath: url.path), let text = String(data: data, encoding: .utf8) else { return false }
            return text.range(of: "<html", options: .caseInsensitive) != nil
        case "epub":
            guard let book = try? EPUB.read(ZipArchive(contentsOf: url)) else { return false }
            let withoutStyles = HTMLResources.replace(#"<(style|title)\b.*?</\1\s*>"#, in: book.html) { _ in "" }
            let visible = HTMLResources.replace("<[^>]*>", in: withoutStyles) { _ in "" }
            return !expectText || !visible.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        default:
            guard let type = systemTypes[formatID],
                  let text = try? NSAttributedString(url: url, options: [.documentType: type], documentAttributes: nil) else { return false }
            return !expectText || !text.string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        }
    }
}
