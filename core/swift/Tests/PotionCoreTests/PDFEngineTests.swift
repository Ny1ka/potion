import AppKit
import CoreText
import PDFKit
import XCTest
@testable import PotionCore

/// PDF tools and PDF → image/document conversions. Sample PDFs are drawn by the test with real text.
final class PDFEngineTests: XCTestCase {
    typealias S = EngineTestSupport
    var dir: URL!
    let engine = PDFEngine()

    override func setUp() async throws { dir = try S.makeTempDirectory() }
    override func tearDown() async throws { if let dir { try? FileManager.default.removeItem(at: dir) } }

    // MARK: Samples

    struct Page {
        var text: String?
        var size = CGSize(width: 612, height: 792)
        /// A black bar across the top of the page, to check orientation.
        var topBar = false
        /// A noisy picture instead of text (a "scan"), `n`×`n` pixels.
        var picture: Int?
    }

    @discardableResult
    func makePDF(_ name: String, _ pages: [Page], rotateFirst: Int = 0) throws -> URL {
        let url = dir.appendingPathComponent(name)
        guard let context = CGContext(url as CFURL, mediaBox: nil, nil) else { throw XCTSkip("no PDF context") }
        for page in pages {
            var box = CGRect(origin: .zero, size: page.size)
            context.beginPage(mediaBox: &box)
            if page.topBar {
                context.setFillColor(CGColor(gray: 0, alpha: 1))
                context.fill(CGRect(x: 0, y: page.size.height - 60, width: page.size.width, height: 60))
            }
            if let text = page.text {
                let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: [
                    .font: NSFont(name: "Helvetica", size: 18)!,
                ]))
                context.textPosition = CGPoint(x: 72, y: page.size.height - 144)
                CTLineDraw(line, context)
            }
            if let n = page.picture { context.draw(noise(n), in: CGRect(x: 36, y: 36, width: page.size.width - 72, height: page.size.height - 72)) }
            context.endPage()
        }
        context.closePDF()
        if rotateFirst != 0 {
            let document = try XCTUnwrap(PDFDocument(url: url))
            document.page(at: 0)?.rotation = rotateFirst
            XCTAssertTrue(document.write(to: url))
        }
        return url
    }

    func noise(_ n: Int) -> CGImage {
        var bytes = [UInt8](repeating: 0, count: n * n * 4)
        var seed: UInt32 = 12345
        for i in 0..<bytes.count { seed = seed &* 1_103_515_245 &+ 12345; bytes[i] = UInt8(truncatingIfNeeded: seed >> 16) }
        for i in stride(from: 3, to: bytes.count, by: 4) { bytes[i] = 255 }
        let provider = CGDataProvider(data: Data(bytes) as CFData)!
        return CGImage(width: n, height: n, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: n * 4,
                       space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)!
    }

    func threePages() throws -> URL {
        try makePDF("doc.pdf", [Page(text: "Page one text"), Page(text: "Page two text"), Page(text: "Page three text")])
    }

    func pageTexts(_ url: URL) throws -> [String] {
        let document = try XCTUnwrap(PDFDocument(url: url))
        return (0..<document.pageCount).map { document.page(at: $0)?.string ?? "" }
    }

    func image(_ url: URL) throws -> CGImage {
        let source = try XCTUnwrap(CGImageSourceCreateWithURL(url as CFURL, nil))
        return try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
    }

    func convert(_ url: URL, to: String, options: PDFOptions = PDFOptions()) async throws -> [URL] {
        try await engine.convert(url, from: S.format("pdf"), to: S.format(to), outputDirectory: dir, options: options)
    }

    func assertError(_ expected: ConversionError, file: StaticString = #filePath, line: UInt = #line, _ body: () async throws -> Any) async {
        do {
            _ = try await body()
            XCTFail("expected \(expected)", file: file, line: line)
        } catch {
            XCTAssertEqual(error as? ConversionError, expected, file: file, line: line)
        }
    }

    // MARK: Page selection (shared cases)

    func testPageSelectionConformance() throws {
        struct File: Decodable {
            struct Case: Decodable { let selection: String; let pageCount: Int; let pages: [Int]?; let error: Bool? }
            let cases: [Case]
        }
        let file = try JSONDecoder().decode(File.self, from: RepoFiles.data("core/spec/tests/pages.json"))
        XCTAssertGreaterThan(file.cases.count, 10)
        for c in file.cases {
            if c.error == true {
                XCTAssertThrowsError(try PageSelection.pages(c.selection, pageCount: c.pageCount), "“\(c.selection)” should be invalid")
            } else {
                XCTAssertEqual(try PageSelection.pages(c.selection, pageCount: c.pageCount), c.pages, "“\(c.selection)”")
            }
        }
        XCTAssertEqual(PageSelection.describe([2, 3, 4, 7, 9, 10]), "2–4, 7, 9–10")
    }

    // MARK: PDF → images

    func testPagesToPNGAndJPG() async throws {
        let pdf = try threePages()
        let pngs = try await convert(pdf, to: "png")
        XCTAssertEqual(pngs.map(\.lastPathComponent), ["doc page 1.png", "doc page 2.png", "doc page 3.png"])
        let first = try image(pngs[0])
        XCTAssertEqual(first.width, 1275)    // 8.5 in × 150 dpi
        XCTAssertEqual(first.height, 1650)

        let jpgs = try await convert(pdf, to: "jpg", options: PDFOptions(pages: [2], dpi: 72))
        XCTAssertEqual(jpgs.map(\.lastPathComponent), ["doc page 2.jpg"])
        XCTAssertEqual(try image(jpgs[0]).width, 612)

        let single = try makePDF("single.pdf", [Page(text: "Only")])
        let only = try await convert(single, to: "png")
        XCTAssertEqual(only.map(\.lastPathComponent), ["single.png"])
    }

    func testRotatedPagesRenderUpright() async throws {
        // Portrait page with a bar at the top, rotated 90° clockwise: the bar ends up on the right.
        let pdf = try makePDF("rotated.pdf", [Page(topBar: true)], rotateFirst: 90)
        let out = try image(try await convert(pdf, to: "png", options: PDFOptions(dpi: 72))[0])
        XCTAssertEqual(out.width, 792)
        XCTAssertEqual(out.height, 612)
        let context = CGContext(data: nil, width: out.width, height: out.height, bitsPerComponent: 8, bytesPerRow: out.width * 4,
                                space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
        context.draw(out, in: CGRect(x: 0, y: 0, width: out.width, height: out.height))
        let pixels = context.data!.bindMemory(to: UInt8.self, capacity: out.width * out.height * 4)
        func gray(x: Int, y: Int) -> UInt8 { pixels[(y * out.width + x) * 4] }   // row 0 = top
        XCTAssertLessThan(gray(x: out.width - 10, y: out.height / 2), 50, "right edge should be the black bar")
        XCTAssertGreaterThan(gray(x: 10, y: out.height / 2), 200, "left edge should be white")
    }

    func testHugePagesAreCapped() async throws {
        let pdf = try makePDF("poster.pdf", [Page(text: "Big", size: CGSize(width: 2000, height: 1000))])
        let out = try image(try await convert(pdf, to: "png", options: PDFOptions(dpi: 600))[0])
        XCTAssertEqual(out.width, PDFPresets.shared.maxRenderPixels)
        XCTAssertEqual(out.height, PDFPresets.shared.maxRenderPixels / 2)
    }

    func testPagesToSVG() async throws {
        let out = try await convert(try threePages(), to: "svg", options: PDFOptions(pages: [1]))
        let xml = try XMLDocument(data: Data(contentsOf: out[0]))
        XCTAssertEqual(xml.rootElement()?.name, "svg")
        XCTAssertEqual(xml.rootElement()?.attribute(forName: "width")?.stringValue, "612.00pt")
    }

    func testInvalidPageSelection() async throws {
        await assertError(.invalidPages(selection: "5", pageCount: 3)) {
            try await self.convert(try self.threePages(), to: "png", options: PDFOptions(pages: [5]))
        }
    }

    // MARK: PDF → documents

    func testPDFToTextDocxAndHTML() async throws {
        let pdf = try threePages()
        let txt = try String(contentsOf: try await convert(pdf, to: "txt")[0], encoding: .utf8)
        let one = try XCTUnwrap(txt.range(of: "Page one text")), three = try XCTUnwrap(txt.range(of: "Page three text"))
        XCTAssertLessThan(one.lowerBound, three.lowerBound)

        let docx = try await convert(pdf, to: "docx", options: PDFOptions(pages: [1, 3]))[0]
        let xml = String(decoding: try XCTUnwrap(ZipArchive(contentsOf: docx).read("word/document.xml")), as: UTF8.self)
        XCTAssertTrue(xml.contains("Page one text") && xml.contains("Page three text") && !xml.contains("Page two"))
        XCTAssertTrue(xml.contains(#"<w:br w:type="page"/>"#))

        let html = try String(contentsOf: try await convert(pdf, to: "html")[0], encoding: .utf8)
        XCTAssertTrue(html.contains("Page two text"))
    }

    func testScannedPagesBecomePictures() async throws {
        let pdf = try makePDF("scan.pdf", [Page(text: "Typed page"), Page(picture: 64)])
        let docx = try await convert(pdf, to: "docx")[0]
        let zip = try ZipArchive(contentsOf: docx)
        XCTAssertTrue(String(decoding: try XCTUnwrap(zip.read("word/document.xml")), as: UTF8.self).contains("<w:drawing>"))
        XCTAssertTrue(zip.contains("word/media/image1.png"))
    }

    // MARK: Tools

    func testMerge() throws {
        let a = try makePDF("a.pdf", [Page(text: "Alpha one"), Page(text: "Alpha two")])
        let b = try makePDF("b.pdf", [Page(text: "Bravo one", size: CGSize(width: 842, height: 595))])
        let out = try engine.merge([a, b], outputDirectory: dir, baseName: "Combined")
        XCTAssertEqual(out.lastPathComponent, "Combined.pdf")
        let texts = try pageTexts(out)
        XCTAssertEqual(texts.count, 3)
        XCTAssertTrue(texts[0].contains("Alpha one") && texts[2].contains("Bravo one"))
        XCTAssertEqual(PDFDocument(url: out)?.page(at: 2)?.bounds(for: .mediaBox).width, 842, "page sizes are kept")
    }

    func testSplit() throws {
        let pdf = try threePages()
        let singles = try engine.split(pdf, outputDirectory: dir)
        XCTAssertEqual(singles.map(\.lastPathComponent), ["doc page 1.pdf", "doc page 2.pdf", "doc page 3.pdf"])
        XCTAssertTrue(try pageTexts(singles[1])[0].contains("Page two"))
        let pairsDirectory = dir.appendingPathComponent("pairs")
        try FileManager.default.createDirectory(at: pairsDirectory, withIntermediateDirectories: false)
        let pairs = try engine.split(pdf, pagesPerFile: 2, outputDirectory: pairsDirectory)
        XCTAssertEqual(pairs.map(\.lastPathComponent), ["doc pages 1–2.pdf", "doc page 3.pdf"])
        XCTAssertEqual(try pageTexts(pairs[0]).count, 2)
    }

    func testExtractAndReorder() throws {
        let pdf = try threePages()
        let extracted = try engine.extract(pdf, pages: [3, 1], outputDirectory: dir)
        XCTAssertEqual(extracted.lastPathComponent, "doc pages 3, 1.pdf")
        let texts = try pageTexts(extracted)
        XCTAssertTrue(texts[0].contains("Page three") && texts[1].contains("Page one"))

        let reordered = try engine.reorder(pdf, order: [2, 3, 1], outputDirectory: dir)
        XCTAssertTrue(try pageTexts(reordered)[0].contains("Page two"))
        XCTAssertThrowsError(try engine.reorder(pdf, order: [1, 2], outputDirectory: dir))
        XCTAssertTrue(try pageTexts(pdf)[0].contains("Page one"), "the original is untouched")
    }

    func testCompressShrinksImages() throws {
        guard #available(macOS 13.4, *) else { throw XCTSkip("needs macOS 13.4") }
        let pdf = try makePDF("photos.pdf", [Page(text: "Photo page", picture: 1200)])
        let out = try engine.compress(pdf, outputDirectory: dir)
        XCTAssertEqual(out.lastPathComponent, "photos compressed.pdf")
        let before = try XCTUnwrap(pdf.resourceValues(forKeys: [.fileSizeKey]).fileSize)
        let after = try XCTUnwrap(out.resourceValues(forKeys: [.fileSizeKey]).fileSize)
        XCTAssertLessThan(after, before)
        XCTAssertTrue(try pageTexts(out)[0].contains("Photo page"), "text stays searchable")
    }

    // MARK: Protection

    func testPasswordAndRestrictedPDFs() async throws {
        let source = try threePages()
        let document = try XCTUnwrap(PDFDocument(url: source))
        let locked = dir.appendingPathComponent("locked.pdf")
        XCTAssertTrue(document.write(to: locked, withOptions: [.userPasswordOption: "user", .ownerPasswordOption: "owner"]))
        let restricted = dir.appendingPathComponent("restricted.pdf")
        XCTAssertTrue(document.write(to: restricted, withOptions: [.ownerPasswordOption: "owner", .accessPermissionsOption: 0]))
        XCTAssertFalse(try XCTUnwrap(PDFDocument(url: restricted)).isLocked)

        await assertError(.copyProtected) { try await self.convert(locked, to: "png") }
        await assertError(.copyProtected) { try await self.convert(restricted, to: "txt") }
        await assertError(.copyProtected) { try await self.convert(restricted, to: "png") }
        await assertError(.copyProtected) { try self.engine.split(restricted, outputDirectory: self.dir) }
        await assertError(.copyProtected) { try self.engine.merge([source, restricted], outputDirectory: self.dir, baseName: "x") }
    }

    func testUnsupportedAndUnreadable() async throws {
        await assertError(.unsupported(from: "PDF", to: "MP3")) {
            try await self.engine.convert(try self.threePages(), from: S.format("pdf"), to: S.format("mp3"), outputDirectory: self.dir)
        }
        let junk = dir.appendingPathComponent("junk.pdf")
        try Data("%PDF-1.4 not really".utf8).write(to: junk)
        await assertError(.unreadable(fileName: "junk.pdf")) { try await self.convert(junk, to: "png") }
    }
}
