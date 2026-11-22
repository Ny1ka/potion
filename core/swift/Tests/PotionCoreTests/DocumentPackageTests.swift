import AppKit
import PDFKit
import XCTest
@testable import PotionCore

/// Zip-based documents: the zip reader/writer, our DOCX and EPUB writers, the EPUB reader and the
/// password/DRM checks. Packages are built by the test.
extension DocumentEngineTests {
    func zip(_ entries: [(String, String)], storeFirst: Bool = true) throws -> Data {
        var writer = ZipWriter()
        for (i, entry) in entries.enumerated() { writer.add(entry.0, Data(entry.1.utf8), compress: !(storeFirst && i == 0)) }
        return writer.finish()
    }

    func assertWellFormed(_ data: Data?, _ name: String, file: StaticString = #filePath, line: UInt = #line) {
        guard let data else { return XCTFail("\(name) missing", file: file, line: line) }
        XCTAssertNoThrow(try XMLDocument(data: data), "\(name) isn't well-formed XML", file: file, line: line)
    }

    // MARK: Zip

    func testZipRoundTripAndSystemUnzipAcceptsIt() throws {
        let long = String(repeating: "Potion converts files. ", count: 500)
        var writer = ZipWriter()
        writer.add("mimetype", Data("application/epub+zip".utf8), compress: false)
        writer.add("dir/long.txt", Data(long.utf8))
        writer.add("dir/naïve é.txt", Data("accents".utf8))
        writer.add("empty", Data())
        let data = writer.finish()
        XCTAssertLessThan(data.count, long.utf8.count, "long text should be deflated")

        let archive = try ZipArchive(data: data)
        XCTAssertEqual(archive.entries.map(\.name), ["mimetype", "dir/long.txt", "dir/naïve é.txt", "empty"])
        XCTAssertEqual(try archive.read("dir/long.txt"), Data(long.utf8))
        XCTAssertEqual(try archive.read("dir/naïve é.txt"), Data("accents".utf8))
        XCTAssertEqual(try archive.read("empty"), Data())
        XCTAssertNil(try archive.read("missing"))

        let url = dir.appendingPathComponent("check.zip")
        try data.write(to: url)
        let unzip = Process()
        unzip.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        unzip.arguments = ["-tq", url.path]
        unzip.standardOutput = FileHandle.nullDevice
        try unzip.run()
        unzip.waitUntilExit()
        XCTAssertEqual(unzip.terminationStatus, 0, "unzip -t rejected the archive")
    }

    func testZipDetectsCorruption() throws {
        var data = try zip([("a.txt", String(repeating: "abc", count: 100))], storeFirst: true)
        data[40] ^= 0xFF   // inside the stored contents
        XCTAssertThrowsError(try ZipArchive(data: data).read("a.txt"))
        XCTAssertThrowsError(try ZipArchive(data: Data("not a zip at all, just some text".utf8)))
    }

    // MARK: DOCX

    func testDOCXKeepsHeadingStylesLinksListsTablesAndImages() async throws {
        let out = try await convert(try makeHTML(), "html", "docx")
        let docx = try ZipArchive(contentsOf: out)
        for part in ["[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/styles.xml", "word/numbering.xml",
                     "word/_rels/document.xml.rels", "docProps/core.xml", "docProps/app.xml"] {
            assertWellFormed(try docx.read(part), part)
        }
        let document = String(decoding: try XCTUnwrap(docx.read("word/document.xml")), as: UTF8.self)
        XCTAssertTrue(document.contains(#"<w:pStyle w:val="Heading1"/>"#))
        XCTAssertTrue(document.contains(#"<w:pStyle w:val="Heading2"/>"#))
        XCTAssertTrue(document.contains("<w:hyperlink r:id="))
        XCTAssertEqual(document.components(separatedBy: "<w:numPr>").count - 1, 2, "two list items")
        XCTAssertTrue(document.contains("<w:tbl>"))
        XCTAssertTrue(document.contains("<w:drawing>"))
        let rels = String(decoding: try XCTUnwrap(docx.read("word/_rels/document.xml.rels")), as: UTF8.self)
        XCTAssertTrue(rels.contains(#"Target="https://example.com/page" TargetMode="External""#))
        XCTAssertTrue(docx.contains("word/media/image1.png"))
        let core = String(decoding: try XCTUnwrap(docx.read("docProps/core.xml")), as: UTF8.self)
        XCTAssertTrue(core.contains("<dc:title>report</dc:title>"))
        XCTAssertFalse(core.contains("creator"), "no author is added")

        // The system reader opens it, with the table and the link.
        let reread = try NSAttributedString(url: out, options: [:], documentAttributes: nil)
        XCTAssertTrue(reread.string.contains("Quarterly Report") && reread.string.contains("North"))
    }

    func testDOCXRoundTripToMarkdownKeepsStructure() async throws {
        let docx = try await convert(try makeHTML(), "html", "docx")
        let md = try text(try await convert(docx, "docx", "md"))
        XCTAssertTrue(md.contains("# Quarterly Report"), md)
        XCTAssertTrue(md.contains("First point") && md.contains("Second point"), md)
        XCTAssertTrue(md.contains("North"), md)
    }

    func testDOCXUsesTheDocumentPageSize() async throws {
        let text = NSAttributedString(string: "Landscape", attributes: [.font: NSFont.systemFont(ofSize: 12)])
        let data = try text.data(from: NSRange(location: 0, length: text.length), documentAttributes: [
            .documentType: NSAttributedString.DocumentType.rtf, .paperSize: NSValue(size: CGSize(width: 792, height: 612)),
            .leftMargin: 36, .rightMargin: 36, .topMargin: 36, .bottomMargin: 36,
        ])
        let rtf = dir.appendingPathComponent("wide.rtf")
        try data.write(to: rtf)
        let out = try await convert(rtf, "rtf", "docx")
        let document = String(decoding: try XCTUnwrap(ZipArchive(contentsOf: out).read("word/document.xml")), as: UTF8.self)
        XCTAssertTrue(document.contains(#"<w:pgSz w:w="15840" w:h="12240" w:orient="landscape"/>"#), document)
        XCTAssertTrue(document.contains(#"w:top="720""#))
    }

    // MARK: EPUB

    func testEPUBWriterMakesAValidPackage() async throws {
        let out = try await convert(try makeHTML(), "html", "epub")
        let epub = try ZipArchive(contentsOf: out)
        XCTAssertEqual(epub.entries.first?.name, "mimetype")
        XCTAssertEqual(epub.entries.first?.method, 0, "mimetype must be stored")
        XCTAssertEqual(try epub.read("mimetype"), Data("application/epub+zip".utf8))
        for part in ["META-INF/container.xml", "OEBPS/content.opf", "OEBPS/nav.xhtml", "OEBPS/content.xhtml"] {
            assertWellFormed(try epub.read(part), part)
        }
        let nav = String(decoding: try XCTUnwrap(epub.read("OEBPS/nav.xhtml")), as: UTF8.self)
        XCTAssertTrue(nav.contains("Quarterly Report") && nav.contains("#section1"))
        let content = String(decoding: try XCTUnwrap(epub.read("OEBPS/content.xhtml")), as: UTF8.self)
        XCTAssertTrue(content.contains(#"src="images/image1.png""#), content)
        XCTAssertFalse(content.contains("cellspacing") || content.contains("valign"), "HTML 4 attributes removed")
        XCTAssertTrue(epub.contains("OEBPS/images/image1.png"))
        let opf = String(decoding: try XCTUnwrap(epub.read("OEBPS/content.opf")), as: UTF8.self)
        XCTAssertTrue(opf.contains(#"href="images/image1.png" media-type="image/png""#))
    }

    func testEPUBRoundTrips() async throws {
        let epub = try await convert(try makeHTML(), "html", "epub")
        let md = try text(try await convert(epub, "epub", "md"))
        XCTAssertTrue(md.contains("Quarterly Report") && md.contains("Second point") && md.contains("North"), md)
        let html = try text(try await convert(epub, "epub", "html"))
        XCTAssertTrue(html.contains("data:image/png;base64,"), "the image survives")
        let fromMarkdown = try await convert(try write(markdown, "notes.md"), "md", "epub")
        XCTAssertNoThrow(try EPUB.read(ZipArchive(contentsOf: fromMarkdown)))
    }

    /// An EPUB 2 laid out the way many publishers do it: OPF in a subfolder, chapters and images in others.
    func makeEPUB(extra: [(String, String)] = []) throws -> URL {
        _ = try makePNG(name: "cover.png", width: 6, height: 6)
        let png = try Data(contentsOf: dir.appendingPathComponent("cover.png"))
        var writer = ZipWriter()
        writer.add("mimetype", Data("application/epub+zip".utf8), compress: false)
        let files: [(String, String)] = [
            ("META-INF/container.xml", #"<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OPS/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>"#),
            ("OPS/book.opf", """
            <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
            <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Two Chapters</dc:title><dc:identifier id="id">x</dc:identifier></metadata>
            <manifest><item id="c2" href="text/ch%202.xhtml" media-type="application/xhtml+xml"/>
            <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
            <item id="css" href="styles/book.css" media-type="text/css"/><item id="img" href="images/cover.png" media-type="image/png"/></manifest>
            <spine><itemref idref="c1"/><itemref idref="c2"/></spine></package>
            """),
            ("OPS/text/ch1.xhtml", #"<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><head><link rel="stylesheet" href="../styles/book.css" type="text/css"/></head><body><h1>Chapter One</h1><p>First words.</p><img src="../images/cover.png" alt=""/></body></html>"#),
            ("OPS/text/ch 2.xhtml", #"<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><body><h1>Chapter Two</h1><p>Last words.</p></body></html>"#),
            ("OPS/styles/book.css", "h1 { color: #336699; }"),
        ] + extra
        for (name, contents) in files { writer.add(name, Data(contents.utf8)) }
        writer.add("OPS/images/cover.png", png, compress: false)
        let url = dir.appendingPathComponent("book.epub")
        try writer.finish().write(to: url)
        return url
    }

    func testEPUBReaderFollowsTheSpine() async throws {
        let book = try EPUB.read(ZipArchive(contentsOf: try makeEPUB()))
        XCTAssertEqual(book.title, "Two Chapters")
        XCTAssertTrue(book.html.contains("#336699"), "linked stylesheet is inlined")
        XCTAssertTrue(book.html.contains("data:image/png;base64,"), "image is embedded")

        let txt = try text(try await convert(try makeEPUB(), "epub", "txt"))
        let one = try XCTUnwrap(txt.range(of: "Chapter One")), two = try XCTUnwrap(txt.range(of: "Chapter Two"))
        XCTAssertLessThan(one.lowerBound, two.lowerBound, "spine order, not manifest order")
        XCTAssertTrue(txt.contains("Last words."))
    }

    func testEPUBResolvesPaths() {
        XCTAssertEqual(EPUB.resolve("../images/a%20b.png#x", against: "OPS/text"), "OPS/images/a b.png")
        XCTAssertEqual(EPUB.resolve("ch1.xhtml", against: ""), "ch1.xhtml")
        XCTAssertEqual(EPUB.resolve("./x/../y.css", against: "OPS"), "OPS/y.css")
    }

    // MARK: Protection

    func assertCopyProtected(_ url: URL, _ format: String, file: StaticString = #filePath, line: UInt = #line) async {
        do {
            _ = try await convert(url, format, "txt")
            XCTFail("expected copyProtected", file: file, line: line)
        } catch {
            XCTAssertEqual(error as? ConversionError, .copyProtected, file: file, line: line)
        }
    }

    func testDRMProtectedEPUBs() async throws {
        let adept = #"<encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><EncryptedData xmlns="http://www.w3.org/2001/04/xmlenc#"><EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes128-cbc"/></EncryptedData></encryption>"#
        await assertCopyProtected(try makeEPUB(extra: [("META-INF/encryption.xml", adept)]), "epub")
        await assertCopyProtected(try makeEPUB(extra: [("META-INF/rights.xml", "<rights/>")]), "epub")

        // Font obfuscation alone isn't DRM: the book converts.
        let fonts = #"<encryption><EncryptedData><EncryptionMethod Algorithm="http://www.idpf.org/2008/embedding"/></EncryptedData></encryption>"#
        let out = try await convert(try makeEPUB(extra: [("META-INF/encryption.xml", fonts)]), "epub", "txt")
        XCTAssertTrue(try text(out).contains("Chapter One"))
    }

    func testPasswordProtectedODT() async throws {
        let manifest = #"<manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0"><manifest:file-entry manifest:full-path="content.xml"><manifest:encryption-data manifest:checksum-type="SHA1"/></manifest:file-entry></manifest:manifest>"#
        let url = dir.appendingPathComponent("locked.odt")
        try zip([("mimetype", "application/vnd.oasis.opendocument.text"), ("META-INF/manifest.xml", manifest), ("content.xml", "x")]).write(to: url)
        await assertCopyProtected(url, "odt")
    }
}
