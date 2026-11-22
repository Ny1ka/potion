import AppKit
import PDFKit
import XCTest
@testable import PotionCore

/// Document conversions with the macOS text system. Samples are written by the test (HTML, Markdown and
/// text by hand; Word, ODT and RTF with the system writers).
final class DocumentEngineTests: XCTestCase {
    typealias S = EngineTestSupport
    var dir: URL!
    let engine = DocumentEngine()

    override func setUp() async throws {
        dir = try S.makeTempDirectory()
    }

    override func tearDown() async throws {
        if let dir { try? FileManager.default.removeItem(at: dir) }
    }

    // MARK: Samples

    func makePNG(name: String, width: Int, height: Int) throws -> URL {
        let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(CGColor(red: 0, green: 0.5, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        let url = dir.appendingPathComponent(name)
        let dest = CGImageDestinationCreateWithURL(url as CFURL, "public.png" as CFString, 1, nil)!
        CGImageDestinationAddImage(dest, ctx.makeImage()!, nil)
        XCTAssertTrue(CGImageDestinationFinalize(dest))
        return url
    }

    func write(_ text: String, _ name: String) throws -> URL {
        let url = dir.appendingPathComponent(name)
        try Data(text.utf8).write(to: url)
        return url
    }

    func makeHTML(image: String = "dot.png", imageSize: Int = 8) throws -> URL {
        _ = try makePNG(name: image, width: imageSize, height: imageSize)
        return try write("""
        <!DOCTYPE html><html><head><title>Sample</title></head><body>
        <h1>Quarterly Report</h1>
        <p>Plain, <b>bold</b>, <i>italic</i> and a <a href="https://example.com/page">link</a>.</p>
        <h2>Details</h2>
        <ul><li>First point</li><li>Second point</li></ul>
        <table border="1"><tr><th>Region</th><th>Sales</th></tr><tr><td>North</td><td>120</td></tr></table>
        <p><img src="\(image)" alt="chart"></p>
        </body></html>
        """, "report.html")
    }

    let markdown = """
    # Release Notes

    Some *emphasis*, **strong** text and `code`, with a [link](https://example.com/notes).

    3. third
    4. fourth

    | Name | Count |
    |:-----|------:|
    | Apples | 3 |

    ![diagram](pic.png)
    """

    /// A system-written document (DOCX, ODT, RTF, DOC) made from the HTML sample.
    func makeSystemDocument(_ formatID: String) async throws -> URL {
        let html = try makeHTML()
        let loaded = try await engine.load(html, format: S.format("html"))
        let url = dir.appendingPathComponent("report.\(formatID)")
        let data = try loaded.text.data(from: NSRange(location: 0, length: loaded.text.length),
                                        documentAttributes: [.documentType: DocumentEngine.systemTypes[formatID]!])
        try data.write(to: url)
        return url
    }

    func text(_ url: URL) throws -> String { try String(contentsOf: url, encoding: .utf8) }

    func convert(_ url: URL, _ from: String, _ to: String) async throws -> URL {
        try await engine.convert(url, from: S.format(from), to: S.format(to), outputDirectory: dir)
    }

    // MARK: Markdown

    func testMarkdownToHTMLKeepsStructureAndEmbedsLocalImages() async throws {
        _ = try makePNG(name: "pic.png", width: 4, height: 4)
        let out = try await convert(try write(markdown, "notes.md"), "md", "html")
        XCTAssertEqual(out.lastPathComponent, "notes.html")
        let html = try text(out)
        XCTAssertTrue(html.contains("<h1>Release Notes</h1>"))
        XCTAssertTrue(html.contains("<em>emphasis</em>") && html.contains("<strong>strong</strong>") && html.contains("<code>code</code>"))
        XCTAssertTrue(html.contains(#"<a href="https://example.com/notes">link</a>"#))
        XCTAssertTrue(html.contains(#"<ol start="3">"#))
        XCTAssertTrue(html.contains("<th>Name</th>") && html.contains(#"<td style="text-align: right">3</td>"#))
        XCTAssertTrue(html.contains(#"src="data:image/png;base64,"#), "local image should be embedded")
        XCTAssertFalse(html.contains("pic.png\""))
    }

    func testMarkdownToPDFAndDOCX() async throws {
        let md = try write(markdown, "notes.md")
        let pdfURL = try await convert(md, "md", "pdf")
        let pdf = try XCTUnwrap(PDFDocument(url: pdfURL))
        XCTAssertTrue(pdf.string?.contains("Release Notes") == true)
        let docx = try await convert(md, "md", "docx")
        let reread = try NSAttributedString(url: docx, options: [:], documentAttributes: nil)
        XCTAssertTrue(reread.string.contains("Apples"))
    }

    // MARK: HTML and system formats

    func testHTMLToPDFIsSearchableWithWorkingLinks() async throws {
        let out = try await convert(try makeHTML(), "html", "pdf")
        let pdf = try XCTUnwrap(PDFDocument(url: out))
        XCTAssertEqual(pdf.pageCount, 1)
        let page = try XCTUnwrap(pdf.page(at: 0))
        XCTAssertTrue(page.string?.contains("Quarterly Report") == true)
        XCTAssertTrue(page.string?.contains("North") == true)
        let links = page.annotations.compactMap { $0.url?.absoluteString }
        XCTAssertTrue(links.contains("https://example.com/page"), "links: \(links)")
    }

    func testLongDocumentsSpanPagesAndBigImagesFit() async throws {
        let paragraphs = (1...200).map { "<p>Paragraph \($0) with enough words to wrap across the line at least once on a page.</p>" }
        _ = try makePNG(name: "huge.png", width: 4000, height: 6000)
        let html = try write("<html><body><img src=\"huge.png\">\(paragraphs.joined())</body></html>", "long.html")
        let pdfURL = try await convert(html, "html", "pdf")
        let pdf = try XCTUnwrap(PDFDocument(url: pdfURL))
        XCTAssertGreaterThan(pdf.pageCount, 3)
        XCTAssertLessThan(pdf.pageCount, 40)
        XCTAssertTrue(pdf.string?.contains("Paragraph 200") == true)
    }

    func testHTMLToSystemFormatsRoundTrip() async throws {
        let html = try makeHTML()
        for id in ["docx", "odt", "rtf", "doc"] {
            let out = try await convert(html, "html", id)
            XCTAssertEqual(out.pathExtension, id)
            let reread = try NSAttributedString(url: out, options: [.documentType: DocumentEngine.systemTypes[id]!], documentAttributes: nil)
            XCTAssertTrue(reread.string.contains("Quarterly Report"), id)
            XCTAssertTrue(reread.string.contains("Second point"), id)
        }
    }

    func testHTMLOutputEmbedsImages() async throws {
        let doc = try await engine.load(try makeHTML(), format: S.format("html"))
        let temporary = dir.appendingPathComponent("direct.html")
        try engine.write(doc, as: "html", title: "Direct", to: temporary, outputDirectory: dir)
        let html = try text(temporary)
        XCTAssertTrue(html.contains("data:image/png;base64,"), "image should be embedded")
        XCTAssertFalse(html.contains("file:///dot.png"))
        XCTAssertTrue(html.contains("<h1"))
    }

    /// The system Word writer stores headings as big bold text and lists as typed bullets; both come back.
    func testWordToMarkdownRecoversHeadingsAndLists() async throws {
        let docx = try await makeSystemDocument("docx")
        let md = try text(try await convert(docx, "docx", "md"))
        XCTAssertTrue(md.contains("# Quarterly Report"), md)
        XCTAssertTrue(md.contains("## Details"), md)
        XCTAssertTrue(md.contains("- First point\n- Second point"), md)
        XCTAssertTrue(md.contains("**bold**") && md.contains("*italic*"), md)
    }

    func testODTToMarkdownKeepsTables() async throws {
        let odt = try await makeSystemDocument("odt")
        let md = try text(try await convert(odt, "odt", "md"))
        XCTAssertTrue(md.contains("| Region | Sales |\n| --- | --- |\n| North | 120 |"), md)
        XCTAssertTrue(md.contains("# Quarterly Report"), md)
    }

    func testHTMLToMarkdownKeepsLinks() async throws {
        let md = try text(try await convert(try makeHTML(), "html", "md"))
        XCTAssertTrue(md.contains("[link](https://example.com/page)"), md)
        XCTAssertTrue(md.contains("# Quarterly Report"), md)
    }

    func testToPlainText() async throws {
        let txt = try text(try await convert(try await makeSystemDocument("odt"), "odt", "txt"))
        XCTAssertTrue(txt.contains("Quarterly Report"))
        XCTAssertTrue(txt.contains("• First point"), txt)
        XCTAssertTrue(txt.contains("Region\tSales"), txt)
        XCTAssertFalse(txt.contains("\u{FFFC}"))
    }

    func testRTFPageSizeCarriesIntoPDF() async throws {
        let text = NSAttributedString(string: "A5 page", attributes: [.font: NSFont.systemFont(ofSize: 12)])
        let a5 = CGSize(width: 420, height: 595)
        let data = try text.data(from: NSRange(location: 0, length: text.length), documentAttributes: [
            .documentType: NSAttributedString.DocumentType.rtf, .paperSize: NSValue(size: a5),
            .leftMargin: 36, .rightMargin: 36, .topMargin: 36, .bottomMargin: 36,
        ])
        let rtf = dir.appendingPathComponent("small.rtf")
        try data.write(to: rtf)
        let pdfURL = try await convert(rtf, "rtf", "pdf")
        let pdf = try XCTUnwrap(PDFDocument(url: pdfURL))
        let box = try XCTUnwrap(pdf.page(at: 0)).bounds(for: .mediaBox)
        XCTAssertEqual(box.width, a5.width, accuracy: 1)
        XCTAssertEqual(box.height, a5.height, accuracy: 1)
    }

    // MARK: Text files

    func testWindowsLatin1TextToWord() async throws {
        let url = dir.appendingPathComponent("legacy.txt")
        try XCTUnwrap("Café menu\nCrème brûlée".data(using: .windowsCP1252)).write(to: url)
        let docx = try await convert(url, "txt", "docx")
        let reread = try NSAttributedString(url: docx, options: [:], documentAttributes: nil)
        XCTAssertTrue(reread.string.contains("Crème brûlée"))
    }

    func testTextDecoding() {
        XCTAssertEqual(DocumentEngine.decodeText(Data([0xEF, 0xBB, 0xBF]) + Data("hi".utf8)), "hi")
        XCTAssertEqual(DocumentEngine.decodeText(Data([0xFF, 0xFE, 0x68, 0x00, 0x69, 0x00])), "hi")
        XCTAssertEqual(DocumentEngine.decodeText(Data([0x63, 0x61, 0x66, 0xE9])), "café")
    }

    func testEmptyTextFileMakesOneBlankPage() async throws {
        let pdfURL = try await convert(try write("", "empty.txt"), "txt", "pdf")
        let pdf = try XCTUnwrap(PDFDocument(url: pdfURL))
        XCTAssertEqual(pdf.pageCount, 1)
    }

    // MARK: Safety

    func testNeverOverwrites() async throws {
        let md = try write("# Hi", "hi.md")
        try Data("existing".utf8).write(to: dir.appendingPathComponent("hi.txt"))
        let out = try await convert(md, "md", "txt")
        XCTAssertEqual(out.lastPathComponent, "hi 2.txt")
        XCTAssertEqual(try text(dir.appendingPathComponent("hi.txt")), "existing")
        let leftovers = try FileManager.default.contentsOfDirectory(atPath: dir.path).filter { $0.hasPrefix(".potion-") }
        XCTAssertEqual(leftovers, [])
    }

    func testEncryptedWordFileIsCopyProtected() async throws {
        var data = Data([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]) + Data(count: 504)
        data += Data("EncryptedPackage".utf16.flatMap { [UInt8($0 & 0xFF), UInt8($0 >> 8)] })
        let url = dir.appendingPathComponent("locked.docx")
        try data.write(to: url)
        do {
            _ = try await convert(url, "doc", "pdf")
            XCTFail("expected copyProtected")
        } catch let error as ConversionError {
            XCTAssertEqual(error, .copyProtected)
        }
    }

    func testUnreadableAndUnsupported() async throws {
        let junk = try write("not really a word file", "junk.docx")
        do { _ = try await convert(junk, "docx", "pdf"); XCTFail("expected unreadable") } catch let error as ConversionError {
            XCTAssertEqual(error, .unreadable(fileName: "junk.docx"))
        }
        do { _ = try await convert(junk, "pdf", "docx"); XCTFail("expected unsupported") } catch let error as ConversionError {
            XCTAssertEqual(error, .unsupported(from: "PDF", to: "DOCX"))   // pdfToDocument is the PDF tools' job
        }
    }

    func testRemoteResourcesAreRemovedBeforeReading() {
        let html = """
        <html><head><link rel="stylesheet" href="https://cdn.example/x.css"><link rel="stylesheet" href="local.css">
        <meta http-equiv="refresh" content="0; url=https://example.com"><script src="https://x/y.js"></script>
        <style>@import url("https://x/z.css"); body { background: url(//x/bg.png) }</style></head>
        <body><img src="https://tracker.example/p.gif"><img src='local.png'><img srcset="a.png 1x, https://x/b.png 2x">
        <iframe src="https://x"></iframe><a href="https://example.com">kept</a></body></html>
        """
        let clean = HTMLResources.removeRemoteResources(html)
        XCTAssertFalse(clean.contains("cdn.example"))
        XCTAssertTrue(clean.contains("local.css"))
        XCTAssertFalse(clean.contains("<script"))
        XCTAssertFalse(clean.contains("refresh"))
        XCTAssertFalse(clean.contains("x/z.css"))
        XCTAssertFalse(clean.contains("bg.png"))
        XCTAssertFalse(clean.contains("tracker.example"))
        XCTAssertTrue(clean.contains("local.png"))
        XCTAssertFalse(clean.contains("b.png"))
        XCTAssertFalse(clean.contains("<iframe"))
        XCTAssertTrue(clean.contains(#"<a href="https://example.com">kept</a>"#))
    }

    func testMarkdownEscaping() {
        let text = NSMutableAttributedString(string: "Use *stars* and [brackets]\n# not a heading\n",
                                             attributes: [.font: NSFont.systemFont(ofSize: 12)])
        let md = DocumentStructure(text).markdown
        XCTAssertTrue(md.contains(#"Use \*stars\* and \[brackets\]"#), md)
        XCTAssertTrue(md.contains(##"\# not a heading"##), md)
    }

    func testPageLayoutDefaultsByRegion() {
        XCTAssertEqual(DocumentPDFRenderer.PageLayout.standard(locale: Locale(identifier: "en_US")).paperSize,
                       DocumentPDFRenderer.PageLayout.letter)
        XCTAssertEqual(DocumentPDFRenderer.PageLayout.standard(locale: Locale(identifier: "de_DE")).paperSize,
                       DocumentPDFRenderer.PageLayout.a4)
        // Margins that leave almost no room are ignored.
        let bad = DocumentPDFRenderer.PageLayout.from([.paperSize: NSValue(size: CGSize(width: 612, height: 792)),
                                                       .leftMargin: 300, .rightMargin: 300, .topMargin: 10, .bottomMargin: 10],
                                                      locale: Locale(identifier: "en_US"))
        XCTAssertEqual(bad.margins.left, 72)
    }
}
