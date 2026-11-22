import AppKit
import ImageIO
import PDFKit

/// PDF settings from core/spec/presets.json ("pdf").
public struct PDFPresets: Sendable {
    public struct CompressionLevel: Decodable, Sendable, Equatable, Identifiable {
        public let id: String
        public let name: String
        public let jpegImages: Bool
        public let screenResolution: Bool
    }

    public let defaultDPI: Double
    public let dpiChoices: [Double]
    public let maxRenderPixels: Int
    public let compressionLevels: [CompressionLevel]
    public let defaultCompressionID: String

    public static let shared: PDFPresets = {
        guard let url = Bundle.module.url(forResource: "presets", withExtension: "json"),
              let data = try? Data(contentsOf: url), let presets = try? PDFPresets(data: data) else {
            fatalError("presets.json is missing or invalid in the PotionCore bundle")
        }
        return presets
    }()

    public init(data: Data) throws {
        struct File: Decodable { let pdf: PDF }
        struct PDF: Decodable {
            struct DPI: Decodable { let `default`: Double; let choices: [Double] }
            struct Compression: Decodable { let `default`: String; let levels: [CompressionLevel] }
            let renderDPI: DPI
            let maxRenderPixels: Int
            let compression: Compression
        }
        let pdf = try JSONDecoder().decode(File.self, from: data).pdf
        defaultDPI = pdf.renderDPI.default
        dpiChoices = pdf.renderDPI.choices
        maxRenderPixels = pdf.maxRenderPixels
        compressionLevels = pdf.compression.levels
        defaultCompressionID = pdf.compression.default
    }

    public func compressionLevel(_ id: String?) -> CompressionLevel {
        compressionLevels.first { $0.id == (id ?? defaultCompressionID) } ?? compressionLevels[0]
    }
}

public struct PDFOptions: Sendable, Equatable {
    /// 1-based pages to convert (see `PageSelection`), or nil for every page.
    public var pages: [Int]?
    /// Resolution for PDF → image (and for pictures of scanned pages in PDF → document).
    public var dpi: Double?
    /// JPEG quality preset (image presets).
    public var imagePresetID: String?

    public init(pages: [Int]? = nil, dpi: Double? = nil, imagePresetID: String? = nil) {
        self.pages = pages
        self.dpi = dpi
        self.imagePresetID = imagePresetID
    }
}

/// PDF work with PDFKit: the matrix operations `pdfToImage` and `pdfToDocument`, plus the PDF tools
/// (merge, split, extract, reorder, compress).
///
/// Protected PDFs: a PDF that needs a password to open is refused. A PDF that opens but carries owner
/// restrictions is converted only where its permissions allow it (copying for text, printing for images),
/// and is never rewritten by the tools, because the rewritten file would lose those restrictions.
public final class PDFEngine: Sendable {
    let matrix: ConversionMatrix
    let presets: PDFPresets
    let imagePresets: ImagePresets

    public init(matrix: ConversionMatrix = .shared, presets: PDFPresets = .shared, imagePresets: ImagePresets = .shared) {
        self.matrix = matrix
        self.presets = presets
        self.imagePresets = imagePresets
    }

    // MARK: - Opening

    enum Use { case readText, render, rewrite }

    func open(_ url: URL, for use: Use) throws -> PDFDocument {
        guard let document = PDFDocument(url: url) else { throw ConversionError.unreadable(fileName: url.lastPathComponent) }
        if document.isLocked { throw ConversionError.copyProtected }
        switch use {
        case .readText where !document.allowsCopying, .render where !document.allowsPrinting:
            throw ConversionError.copyProtected
        case .rewrite where document.isEncrypted:
            throw ConversionError.copyProtected
        default:
            return document
        }
    }

    private func selectedPages(_ options: PDFOptions, in document: PDFDocument) throws -> [Int] {
        let all = Array(1...max(document.pageCount, 1)).filter { $0 <= document.pageCount }
        guard let pages = options.pages else { return all }
        guard !pages.isEmpty, pages.allSatisfy({ (1...document.pageCount).contains($0) }) else {
            throw ConversionError.invalidPages(selection: PageSelection.describe(pages), pageCount: document.pageCount)
        }
        return pages
    }

    // MARK: - Matrix conversions

    /// PDF → PNG/JPG/SVG (one file per page) or PDF → DOCX/TXT/HTML (one file). Returns the saved files.
    public func convert(_ input: URL, from inputFormat: FileFormat, to outputFormat: FileFormat,
                        outputDirectory: URL, options: PDFOptions = PDFOptions()) async throws -> [URL] {
        guard let conversion = matrix.conversion(from: inputFormat.id, to: outputFormat.id) else {
            throw ConversionError.unsupported(from: inputFormat.name, to: outputFormat.name)
        }
        try OutputFiles.checkWritable(outputDirectory)
        switch conversion.operation {
        case .pdfToImage:
            return try renderImages(input, formatID: outputFormat.id, ext: outputFormat.preferredExtension,
                                    outputDirectory: outputDirectory, options: options)
        case .pdfToDocument:
            return [try await extractDocument(input, formatID: outputFormat.id, ext: outputFormat.preferredExtension,
                                              outputDirectory: outputDirectory, options: options)]
        default:
            throw ConversionError.unsupported(from: inputFormat.name, to: outputFormat.name)
        }
    }

    // MARK: PDF → images

    private func renderImages(_ input: URL, formatID: String, ext: String, outputDirectory: URL, options: PDFOptions) throws -> [URL] {
        let document = try open(input, for: .render)
        let pages = try selectedPages(options, in: document)
        let base = input.deletingPathExtension().lastPathComponent
        let dpi = options.dpi ?? presets.defaultDPI
        let quality = imagePresets.quality(for: ImageOptions(presetID: options.imagePresetID))

        return try savingAll(pages, outputDirectory: outputDirectory) { number in
            guard let page = document.page(at: number - 1) else { throw ConversionError.unreadable(fileName: input.lastPathComponent) }
            let image = try render(page, dpi: dpi, inputName: input.lastPathComponent)
            let temporary = OutputFiles.temporaryURL(in: outputDirectory, fileExtension: ext)
            defer { OutputFiles.removeIfPresent(temporary) }
            switch formatID {
            case "svg": try svg(for: image, page: page).write(to: temporary, options: .withoutOverwriting)
            case "jpg": try writeImage(image, type: "public.jpeg", quality: quality, dpi: dpi, to: temporary)
            default: try writeImage(image, type: "public.png", quality: nil, dpi: dpi, to: temporary)
            }
            guard Self.verifyImage(temporary, formatID: formatID) else {
                throw ConversionError.verificationFailed(fileName: input.lastPathComponent)
            }
            let name = document.pageCount == 1 ? base : "\(base) page \(number)"
            return try OutputFiles.finalize(temporary, in: outputDirectory, baseName: name, fileExtension: ext)
        }
    }

    /// Draws a page (crop box, rotation applied) on white at the given resolution, capped so neither side
    /// exceeds `maxRenderPixels`.
    func render(_ page: PDFPage, dpi: Double, inputName: String) throws -> CGImage {
        var size = page.bounds(for: .cropBox).size
        if page.rotation % 180 != 0 { size = CGSize(width: size.height, height: size.width) }
        var scale = dpi / 72
        let longest = max(size.width, size.height) * scale
        if longest > Double(presets.maxRenderPixels) { scale *= Double(presets.maxRenderPixels) / longest }
        let width = max(Int((size.width * scale).rounded()), 1), height = max(Int((size.height * scale).rounded()), 1)
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else {
            throw ConversionError.conversionFailed(fileName: inputName, detail: "bitmap context \(width)×\(height)")
        }
        context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        context.interpolationQuality = .high
        context.scaleBy(x: scale, y: scale)
        page.draw(with: .cropBox, to: context)
        guard let image = context.makeImage() else {
            throw ConversionError.conversionFailed(fileName: inputName, detail: "makeImage")
        }
        return image
    }

    private func writeImage(_ image: CGImage, type: String, quality: Double?, dpi: Double, to url: URL) throws {
        guard let destination = CGImageDestinationCreateWithURL(url as CFURL, type as CFString, 1, nil) else {
            throw ConversionError.cannotWrite(folder: url.deletingLastPathComponent().lastPathComponent)
        }
        var properties: [CFString: Any] = [kCGImagePropertyDPIWidth: dpi, kCGImagePropertyDPIHeight: dpi]
        if let quality { properties[kCGImageDestinationLossyCompressionQuality] = quality }
        CGImageDestinationAddImage(destination, image, properties as CFDictionary)
        guard CGImageDestinationFinalize(destination) else {
            throw ConversionError.cannotWrite(folder: url.deletingLastPathComponent().lastPathComponent)
        }
    }

    /// An SVG the size of the page holding the rendered page as a PNG (see the matrix note `pdfToSvg`).
    private func svg(for image: CGImage, page: PDFPage) throws -> Data {
        var size = page.bounds(for: .cropBox).size
        if page.rotation % 180 != 0 { size = CGSize(width: size.height, height: size.width) }
        let png = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(png, "public.png" as CFString, 1, nil) else {
            throw ConversionError.conversionFailed(fileName: "", detail: "png encoder")
        }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { throw ConversionError.conversionFailed(fileName: "", detail: "png encode") }
        let w = String(format: "%.2f", size.width), h = String(format: "%.2f", size.height)
        let uri = HTMLResources.dataURI(png as Data, mimeType: "image/png")
        return Data("""
        <?xml version="1.0" encoding="UTF-8"?>
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="\(w)pt" height="\(h)pt" \
        viewBox="0 0 \(w) \(h)"><image width="\(w)" height="\(h)" preserveAspectRatio="none" xlink:href="\(uri)"/></svg>

        """.utf8)
    }

    static func verifyImage(_ url: URL, formatID: String) -> Bool {
        if formatID == "svg" {
            guard let data = FileManager.default.contents(atPath: url.path), let xml = try? XMLDocument(data: data) else { return false }
            return xml.rootElement()?.name == "svg"
        }
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil), CGImageSourceGetCount(source) > 0,
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return false }
        return image.width > 0 && image.height > 0
    }

    // MARK: PDF → documents

    private func extractDocument(_ input: URL, formatID: String, ext: String, outputDirectory: URL,
                                 options: PDFOptions) async throws -> URL {
        let pdf = try open(input, for: .readText)
        let pages = try selectedPages(options, in: pdf)
        let text = NSMutableAttributedString()
        let dpi = options.dpi ?? presets.defaultDPI
        var hasText = false
        for (i, number) in pages.enumerated() {
            guard let page = pdf.page(at: number - 1) else { continue }
            if i > 0 { text.append(NSAttributedString(string: formatID == "docx" ? "\n\u{000C}" : "\n\n")) }
            if let content = page.attributedString, !content.string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                text.append(content)
                hasText = true
            } else if formatID != "txt", pdf.allowsPrinting {
                // A page without text (usually a scan) becomes a picture of the page.
                let image = try render(page, dpi: dpi, inputName: input.lastPathComponent)
                let rep = NSBitmapImageRep(cgImage: image)
                guard let png = rep.representation(using: .png, properties: [:]) else { continue }
                let wrapper = FileWrapper(regularFileWithContents: png)
                wrapper.preferredFilename = "page\(number).png"
                let attachment = NSTextAttachment(fileWrapper: wrapper)
                var size = page.bounds(for: .cropBox).size
                if page.rotation % 180 != 0 { size = CGSize(width: size.height, height: size.width) }
                attachment.bounds = CGRect(origin: .zero, size: size)
                text.append(NSAttributedString(attachment: attachment))
            }
        }

        let base = input.deletingPathExtension().lastPathComponent
        var attributes: [NSAttributedString.DocumentAttributeKey: Any] = [:]
        if let first = pdf.page(at: pages[0] - 1) {
            attributes[.paperSize] = NSValue(size: first.bounds(for: .mediaBox).size)
        }
        let temporary = OutputFiles.temporaryURL(in: outputDirectory, fileExtension: ext)
        defer { OutputFiles.removeIfPresent(temporary) }
        let engine = DocumentEngine(matrix: matrix)
        try engine.write(DocumentEngine.Document(text: text, attributes: attributes), as: formatID, title: base,
                         to: temporary, outputDirectory: outputDirectory)
        guard DocumentEngine.verify(temporary, formatID: formatID, expectText: hasText) else {
            throw ConversionError.verificationFailed(fileName: input.lastPathComponent)
        }
        return try OutputFiles.finalize(temporary, in: outputDirectory, baseName: base, fileExtension: ext)
    }

    // MARK: - PDF tools

    /// Joins PDFs in the given order into one file named `baseName`.
    public func merge(_ inputs: [URL], outputDirectory: URL, baseName: String) throws -> URL {
        guard inputs.count >= 2 else { throw ConversionError.unsupported(from: "One PDF", to: "a merged PDF") }
        try OutputFiles.checkWritable(outputDirectory)
        let merged = PDFDocument()
        for input in inputs {
            let document = try open(input, for: .rewrite)
            for index in 0..<document.pageCount {
                guard let page = document.page(at: index)?.copy() as? PDFPage else { continue }
                merged.insert(page, at: merged.pageCount)
            }
        }
        return try save(merged, baseName: baseName, outputDirectory: outputDirectory, inputName: inputs[0].lastPathComponent)
    }

    /// One file per `pagesPerFile` pages: "Report page 1.pdf" or "Report pages 1–3.pdf".
    public func split(_ input: URL, pagesPerFile: Int = 1, outputDirectory: URL) throws -> [URL] {
        try OutputFiles.checkWritable(outputDirectory)
        let document = try open(input, for: .rewrite)
        guard document.pageCount > 1, pagesPerFile >= 1 else {
            throw ConversionError.unsupported(from: "A one-page PDF", to: "separate pages")
        }
        let base = input.deletingPathExtension().lastPathComponent
        let groups = stride(from: 1, through: document.pageCount, by: pagesPerFile).map {
            Array($0...min($0 + pagesPerFile - 1, document.pageCount))
        }
        return try savingAll(groups, outputDirectory: outputDirectory) { group in
            let label = group.count == 1 ? "page \(group[0])" : "pages \(PageSelection.describe(group))"
            return try save(copy(group, of: document), baseName: "\(base) \(label)", outputDirectory: outputDirectory,
                            inputName: input.lastPathComponent)
        }
    }

    /// A new PDF with only the chosen pages, in the order given: "Report pages 2–4.pdf".
    public func extract(_ input: URL, pages: [Int], outputDirectory: URL) throws -> URL {
        try OutputFiles.checkWritable(outputDirectory)
        let document = try open(input, for: .rewrite)
        let chosen = try selectedPages(PDFOptions(pages: pages), in: document)
        let description = PageSelection.describe(chosen)
        let label = chosen.count == 1 ? "page \(description)" : description.count <= 40 ? "pages \(description)" : "extract"
        return try save(copy(chosen, of: document), baseName: "\(input.deletingPathExtension().lastPathComponent) \(label)",
                        outputDirectory: outputDirectory, inputName: input.lastPathComponent)
    }

    /// The same pages in a new order. `order` must list every page once.
    public func reorder(_ input: URL, order: [Int], outputDirectory: URL) throws -> URL {
        try OutputFiles.checkWritable(outputDirectory)
        let document = try open(input, for: .rewrite)
        guard order.sorted() == Array(1...max(document.pageCount, 1)) else {
            throw ConversionError.invalidPages(selection: PageSelection.describe(order), pageCount: document.pageCount)
        }
        return try save(copy(order, of: document), baseName: "\(input.deletingPathExtension().lastPathComponent) reordered",
                        outputDirectory: outputDirectory, inputName: input.lastPathComponent)
    }

    /// Re-saves with images recompressed as JPEG (and, for "smallest", reduced to screen resolution).
    /// Text and vector graphics stay as they are. Fails with `notSmaller` if the result isn't smaller.
    public func compress(_ input: URL, levelID: String? = nil, outputDirectory: URL) throws -> URL {
        guard #available(macOS 13.4, *) else {
            throw ConversionError.needsNewerSystem(feature: "Compressing PDFs", version: "macOS 13.4")
        }
        try OutputFiles.checkWritable(outputDirectory)
        let document = try open(input, for: .rewrite)
        let level = presets.compressionLevel(levelID)
        var options: [PDFDocumentWriteOption: Any] = [:]
        if level.jpegImages { options[.saveImagesAsJPEGOption] = true }
        if level.screenResolution { options[.optimizeImagesForScreenOption] = true }

        let temporary = OutputFiles.temporaryURL(in: outputDirectory, fileExtension: "pdf")
        defer { OutputFiles.removeIfPresent(temporary) }
        guard document.write(to: temporary, withOptions: options) else {
            throw ConversionError.cannotWrite(folder: outputDirectory.lastPathComponent)
        }
        try verifyPDF(temporary, pageCount: document.pageCount, inputName: input.lastPathComponent)
        let before = (try? input.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        let after = (try? temporary.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? .max
        guard after < before else { throw ConversionError.notSmaller(fileName: input.lastPathComponent) }
        return try OutputFiles.finalize(temporary, in: outputDirectory,
                                        baseName: "\(input.deletingPathExtension().lastPathComponent) compressed", fileExtension: "pdf")
    }

    // MARK: - Helpers

    private func copy(_ pages: [Int], of document: PDFDocument) -> PDFDocument {
        let out = PDFDocument()
        for number in pages {
            guard let page = document.page(at: number - 1)?.copy() as? PDFPage else { continue }
            out.insert(page, at: out.pageCount)
        }
        return out
    }

    private func save(_ document: PDFDocument, baseName: String, outputDirectory: URL, inputName: String) throws -> URL {
        let temporary = OutputFiles.temporaryURL(in: outputDirectory, fileExtension: "pdf")
        defer { OutputFiles.removeIfPresent(temporary) }
        guard document.write(to: temporary) else { throw ConversionError.cannotWrite(folder: outputDirectory.lastPathComponent) }
        try verifyPDF(temporary, pageCount: document.pageCount, inputName: inputName)
        return try OutputFiles.finalize(temporary, in: outputDirectory, baseName: baseName, fileExtension: "pdf")
    }

    private func verifyPDF(_ url: URL, pageCount: Int, inputName: String) throws {
        guard let check = PDFDocument(url: url), check.pageCount == pageCount, pageCount > 0 else {
            throw ConversionError.verificationFailed(fileName: inputName)
        }
    }

    /// Saves several outputs; if one fails, removes the ones already saved so no partial set is left behind.
    private func savingAll<T>(_ items: [T], outputDirectory: URL, _ body: (T) throws -> URL) throws -> [URL] {
        var saved: [URL] = []
        do {
            for item in items { saved.append(try body(item)) }
            return saved
        } catch {
            for url in saved { OutputFiles.removeIfPresent(url) }
            throw error
        }
    }
}
