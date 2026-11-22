import Foundation

/// EPUB 2/3 reading (into one HTML document) and EPUB 3 writing (from HTML).
enum EPUB {
    // MARK: - Reading

    struct Book {
        var title: String?
        /// The chapters in reading order as one HTML document, with stylesheets inlined and images as `data:` URIs.
        var html: String
    }

    /// Font obfuscation (IDPF and Adobe) only hides embedded fonts; the text is readable. Anything else
    /// listed in encryption.xml is DRM.
    private static let fontObfuscation: Set<String> = ["http://www.idpf.org/2008/embedding", "http://ns.adobe.com/pdf/enc#RC"]

    static func isProtected(_ zip: ZipArchive) -> Bool {
        if zip.contains("META-INF/rights.xml") || zip.contains("META-INF/license.lcpl") { return true }
        guard let data = try? zip.read("META-INF/encryption.xml") else { return false }
        let xml = String(decoding: data, as: UTF8.self)
        var algorithms: [String] = []
        _ = HTMLResources.replace(#"EncryptionMethod\b[^>]*\bAlgorithm\s*=\s*"([^"]*)""#, in: xml) { match in
            algorithms.append(match.group(1)); return match.full
        }
        return algorithms.contains { !fontObfuscation.contains($0) }
    }

    static func read(_ zip: ZipArchive) throws -> Book {
        guard let container = try zip.read("META-INF/container.xml"),
              let opfPath = firstMatch(#"full-path\s*=\s*"([^"]+)""#, in: String(decoding: container, as: UTF8.self)),
              let opfData = try zip.read(opfPath) else { throw ZipArchive.ZipError.invalid }
        let opf = try XMLDocument(data: opfData)
        let opfDirectory = (opfPath as NSString).deletingLastPathComponent

        var manifest: [String: (href: String, type: String)] = [:]
        for case let item as XMLElement in try opf.nodes(forXPath: "//*[local-name()='item']") {
            guard let id = item.attribute(forName: "id")?.stringValue, let href = item.attribute(forName: "href")?.stringValue else { continue }
            manifest[id] = (href, item.attribute(forName: "media-type")?.stringValue ?? "")
        }
        let spine = try opf.nodes(forXPath: "//*[local-name()='itemref']/@idref").compactMap(\.stringValue)
        let title = try opf.nodes(forXPath: "//*[local-name()='metadata']/*[local-name()='title']").first?.stringValue

        var styles: [String] = []
        var bodies: [String] = []
        for id in spine {
            guard let item = manifest[id], item.type.contains("html") else { continue }
            let path = resolve(item.href, against: opfDirectory)
            guard let data = try zip.read(path), let chapter = DocumentEngine.decodeText(data) else { continue }
            let directory = (path as NSString).deletingLastPathComponent

            for style in matches(#"<style\b[^>]*>(.*?)</style\s*>"#, in: chapter) { styles.append(style) }
            for href in matches(#"<link\b[^>]*rel\s*=\s*["'][^"']*stylesheet[^>]*href\s*=\s*["']([^"']+)["']"#, in: chapter)
                + matches(#"<link\b[^>]*href\s*=\s*["']([^"']+\.css)["'][^>]*>"#, in: chapter) {
                if let css = try? zip.read(resolve(href, against: directory)) { styles.append(String(decoding: css, as: UTF8.self)) }
            }
            let body = firstMatch(#"<body\b[^>]*>(.*)</body\s*>"#, in: chapter) ?? chapter
            bodies.append(embeddingImages(body, zip: zip, directory: directory, manifest: manifest, opfDirectory: opfDirectory))
        }
        guard !bodies.isEmpty else { throw ZipArchive.ZipError.invalid }

        let html = """
        <!DOCTYPE html><html><head><meta charset="utf-8"><title>\(HTMLResources.escape(title ?? ""))</title>
        <style>\(Array(Set(styles)).sorted().joined(separator: "\n"))</style></head><body>
        \(bodies.joined(separator: "\n"))
        </body></html>
        """
        return Book(title: title, html: html)
    }

    /// Images inside the book become `data:` URIs (`<img src>` and SVG `<image href>`).
    private static func embeddingImages(_ body: String, zip: ZipArchive, directory: String,
                                        manifest: [String: (href: String, type: String)], opfDirectory: String) -> String {
        var types: [String: String] = [:]
        for item in manifest.values { types[resolve(item.href, against: opfDirectory)] = item.type }
        return HTMLResources.replace(#"(<(?:img\b[^>]*\ssrc|image\b[^>]*\s(?:xlink:)?href)\s*=\s*)("[^"]*"|'[^']*')"#, in: body) { match in
            let value = String(match.group(2).dropFirst().dropLast())
            guard !value.contains(":") else { return match.full }
            let path = resolve(value, against: directory)
            guard let data = try? zip.read(path) else { return match.full }
            let mime = types[path] ?? "image/\((path as NSString).pathExtension.lowercased())"
            return match.group(1) + "\"\(HTMLResources.dataURI(data, mimeType: mime))\""
        }
    }

    /// Resolves an href inside the zip: percent-decoding, `..`, and dropping any #fragment.
    static func resolve(_ href: String, against directory: String) -> String {
        let clean = (href.components(separatedBy: "#").first ?? href).removingPercentEncoding ?? href
        var parts = directory.isEmpty || clean.hasPrefix("/") ? [] : directory.split(separator: "/").map(String.init)
        for part in clean.split(separator: "/") {
            if part == ".." { _ = parts.popLast() } else if part != "." { parts.append(String(part)) }
        }
        return parts.joined(separator: "/")
    }

    private static func firstMatch(_ pattern: String, in text: String) -> String? { matches(pattern, in: text).first }

    private static func matches(_ pattern: String, in text: String) -> [String] {
        var found: [String] = []
        _ = HTMLResources.replace(pattern, in: text) { match in found.append(match.group(1)); return match.full }
        return found
    }

    // MARK: - Writing

    /// Builds an EPUB 3 from an HTML document. `images` are the files its `<img src="name">` tags refer to.
    static func write(html: String, title: String, language: String, images: [String: DocumentImages.Image]) throws -> Data {
        // The HTML must become well-formed XHTML: let the system's HTML tidier parse it.
        let document = try XMLDocument(xmlString: html, options: [.documentTidyHTML])
        let css = try document.nodes(forXPath: "//style").compactMap(\.stringValue).joined(separator: "\n")
        guard let body = try document.nodes(forXPath: "//body").first as? XMLElement else { throw ZipArchive.ZipError.invalid }

        // Images: point at files in the package; drop any the book can't contain.
        var used: [String: DocumentImages.Image] = [:]
        for case let img as XMLElement in try body.nodes(forXPath: ".//img") {
            let src = img.attribute(forName: "src")?.stringValue ?? ""
            let name = (src.hasPrefix("file:///") ? String(src.dropFirst(8)) : src).removingPercentEncoding ?? src
            if let image = images[name] {
                used[name] = image
                img.attribute(forName: "src")?.stringValue = "images/\(name)"
                if img.attribute(forName: "alt") == nil { img.addAttribute(XMLNode.attribute(withName: "alt", stringValue: "") as! XMLNode) }
            } else {
                img.detach()
            }
        }

        // HTML 4 attributes the system writer uses that aren't valid in EPUB 3 (HTML5).
        for case let element as XMLElement in try body.nodes(forXPath: ".//*[@cellspacing or @cellpadding or @valign]") {
            for name in ["cellspacing", "cellpadding", "valign"] { element.removeAttribute(forName: name) }
        }

        // Navigation from the headings.
        var nav: [(id: String, level: Int, text: String)] = []
        for case let heading as XMLElement in try body.nodes(forXPath: ".//h1|.//h2") {
            let id = "section\(nav.count + 1)"
            heading.removeAttribute(forName: "id")
            heading.addAttribute(XMLNode.attribute(withName: "id", stringValue: id) as! XMLNode)
            let text = (heading.stringValue ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty { nav.append((id, heading.name == "h1" ? 1 : 2, text)) }
        }
        if nav.isEmpty { nav = [("", 1, title)] }

        let bodyXML = (body.children ?? []).map { $0.xmlString(options: [.nodeCompactEmptyElement]) }.joined()
        let t = HTMLResources.escapeXML(title)
        let lang = HTMLResources.escapeXML(language)

        let content = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xml:lang="\(lang)" lang="\(lang)"><head><meta charset="utf-8"/>\
        <title>\(t)</title><link rel="stylesheet" type="text/css" href="style.css"/></head><body>\(bodyXML)</body></html>
        """
        let navItems = nav.map { item in
            "<li><a href=\"content.xhtml\(item.id.isEmpty ? "" : "#\(item.id)")\">\(HTMLResources.escapeXML(item.text))</a></li>"
        }.joined()
        let navXHTML = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="\(lang)" lang="\(lang)">\
        <head><meta charset="utf-8"/><title>\(t)</title></head><body><nav epub:type="toc" id="toc"><h1>\(t)</h1>\
        <ol>\(navItems)</ol></nav></body></html>
        """
        let modified = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: #"\.\d+"#, with: "", options: .regularExpression)
        let imageItems = used.keys.sorted().enumerated().map { i, name in
            "<item id=\"img\(i + 1)\" href=\"images/\(HTMLResources.escapeXML(name))\" media-type=\"\(used[name]!.mimeType)\"/>"
        }.joined()
        let opf = """
        <?xml version="1.0" encoding="UTF-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid" xml:lang="\(lang)">\
        <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="uid">urn:uuid:\(UUID().uuidString.lowercased())</dc:identifier>\
        <dc:title>\(t)</dc:title><dc:language>\(lang)</dc:language><meta property="dcterms:modified">\(modified)</meta></metadata>\
        <manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>\
        <item id="content" href="content.xhtml" media-type="application/xhtml+xml"/>\
        <item id="css" href="style.css" media-type="text/css"/>\(imageItems)</manifest>\
        <spine><itemref idref="content"/></spine></package>
        """
        let container = """
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles>\
        <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
        """

        var zip = ZipWriter()
        zip.add("mimetype", Data("application/epub+zip".utf8), compress: false)   // must be first and stored
        zip.add("META-INF/container.xml", Data(container.utf8))
        zip.add("OEBPS/content.opf", Data(opf.utf8))
        zip.add("OEBPS/nav.xhtml", Data(navXHTML.utf8))
        zip.add("OEBPS/content.xhtml", Data(content.utf8))
        zip.add("OEBPS/style.css", Data(css.utf8))
        for (name, image) in used { zip.add("OEBPS/images/\(name)", image.data, compress: false) }
        return zip.finish()
    }
}
