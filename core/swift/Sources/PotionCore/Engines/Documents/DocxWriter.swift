import AppKit

/// Writes an attributed string as a Word document (Office Open XML). Unlike the system writer, it keeps
/// heading styles (so Word's navigation pane and table of contents work), real hyperlinks, numbered and
/// bulleted lists, tables, images and the page setup. Element order follows the OOXML schema, which Word checks.
final class DocxWriter {
    private let text: NSAttributedString
    private let string: NSString
    private let structure: DocumentStructure
    private let layout: DocumentPDFRenderer.PageLayout
    private let title: String

    private var relationships: [(id: String, type: String, target: String, external: Bool)] = []
    private var media: [(name: String, data: Data)] = []
    /// List id → (numId, ordered, start number).
    private var lists: [Int: (numID: Int, ordered: Bool, start: Int)] = [:]
    private var imageCount = 0

    private static let relBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"

    init(_ text: NSAttributedString, layout: DocumentPDFRenderer.PageLayout, title: String) {
        self.text = text
        self.string = text.string as NSString
        self.structure = DocumentStructure(text)
        self.layout = layout
        self.title = title
    }

    func data() -> Data {
        let body = bodyXML()   // first: collects relationships, images and lists
        var zip = ZipWriter()
        zip.add("[Content_Types].xml", Data(contentTypes().utf8))
        zip.add("_rels/.rels", Data(Self.packageRels.utf8))
        zip.add("word/document.xml", Data(document(body).utf8))
        zip.add("word/styles.xml", Data(styles().utf8))
        zip.add("word/numbering.xml", Data(numbering().utf8))
        zip.add("word/_rels/document.xml.rels", Data(documentRels().utf8))
        for item in media { zip.add("word/media/\(item.name)", item.data, compress: false) }
        zip.add("docProps/core.xml", Data(core().utf8))
        zip.add("docProps/app.xml", Data(Self.app.utf8))
        return zip.finish()
    }

    private func esc(_ s: String) -> String { HTMLResources.escapeXML(s) }
    private func twips(_ points: CGFloat) -> Int { Int((points * 20).rounded()) }

    private func addRelationship(type: String, target: String, external: Bool = false) -> String {
        let id = "rId\(relationships.count + 10)"   // rId1–9 are left for the fixed parts
        relationships.append((id, type, target, external))
        return id
    }

    // MARK: - Body

    private func bodyXML() -> String {
        var xml = ""
        let blocks = structure.blocks
        for (i, block) in blocks.enumerated() {
            switch block {
            case let .paragraph(range, heading):
                xml += paragraph(range, style: heading > 0 ? "Heading\(heading)" : nil)
            case let .listItem(range, depth, ordered, number, list):
                xml += paragraph(range, numbering: (numberingID(list: list, ordered: ordered, start: number), min(depth, 8)))
            case let .table(rows):
                xml += table(rows)
                if i == blocks.count - 1 { xml += "<w:p/>" }   // Word expects a paragraph after a final table
            }
        }
        let size = layout.paperSize
        let orient = size.width > size.height ? " w:orient=\"landscape\"" : ""
        let m = layout.margins
        xml += "<w:sectPr><w:pgSz w:w=\"\(twips(size.width))\" w:h=\"\(twips(size.height))\"\(orient)/>"
            + "<w:pgMar w:top=\"\(twips(m.top))\" w:right=\"\(twips(m.right))\" w:bottom=\"\(twips(m.bottom))\" "
            + "w:left=\"\(twips(m.left))\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>"
        return xml
    }

    private func numberingID(list: Int, ordered: Bool, start: Int) -> Int {
        if let known = lists[list] { return known.numID }
        let id = lists.count + 1
        lists[list] = (id, ordered, start)
        return id
    }

    private func paragraph(_ range: NSRange, style: String? = nil, numbering: (numID: Int, level: Int)? = nil) -> String {
        let ps = range.location < text.length
            ? text.attribute(.paragraphStyle, at: range.location, effectiveRange: nil) as? NSParagraphStyle : nil
        var ppr = ""
        if let style { ppr += "<w:pStyle w:val=\"\(style)\"/>" }
        if let numbering { ppr += "<w:numPr><w:ilvl w:val=\"\(numbering.level)\"/><w:numId w:val=\"\(numbering.numID)\"/></w:numPr>" }
        if let ps {
            if ps.paragraphSpacingBefore > 0 || ps.paragraphSpacing > 0 {
                ppr += "<w:spacing w:before=\"\(twips(ps.paragraphSpacingBefore))\" w:after=\"\(twips(ps.paragraphSpacing))\"/>"
            }
            if numbering == nil, ps.textBlocks.isEmpty, ps.headIndent > 0 || ps.firstLineHeadIndent > 0 {
                let first = ps.firstLineHeadIndent - ps.headIndent
                ppr += "<w:ind w:left=\"\(twips(ps.headIndent))\" "
                    + (first >= 0 ? "w:firstLine=\"\(twips(first))\"/>" : "w:hanging=\"\(twips(-first))\"/>")
            }
            switch ps.alignment {
            case .center: ppr += "<w:jc w:val=\"center\"/>"
            case .right: ppr += "<w:jc w:val=\"right\"/>"
            case .justified: ppr += "<w:jc w:val=\"both\"/>"
            default: break
            }
        }
        return "<w:p>" + (ppr.isEmpty ? "" : "<w:pPr>\(ppr)</w:pPr>") + runs(range) + "</w:p>"
    }

    private func table(_ rows: [[[NSRange]]]) -> String {
        let columns = max(rows.map(\.count).max() ?? 1, 1)
        let width = twips(layout.contentSize.width) / columns
        let border = "w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\""
        var xml = "<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>"
            + ["top", "left", "bottom", "right", "insideH", "insideV"].map { "<w:\($0) \(border)/>" }.joined()
            + "</w:tblBorders></w:tblPr><w:tblGrid>"
            + String(repeating: "<w:gridCol w:w=\"\(width)\"/>", count: columns) + "</w:tblGrid>"
        for row in rows {
            xml += "<w:tr>"
            for column in 0..<columns {
                let cell = column < row.count ? row[column] : []
                let content = cell.map { paragraph($0) }.joined()
                xml += "<w:tc><w:tcPr><w:tcW w:w=\"\(width)\" w:type=\"dxa\"/></w:tcPr>\(content.isEmpty ? "<w:p/>" : content)</w:tc>"
            }
            xml += "</w:tr>"
        }
        return xml + "</w:tbl>"
    }

    // MARK: - Runs

    private func runs(_ range: NSRange) -> String {
        var xml = ""
        var openLink: String?
        text.enumerateAttributes(in: range) { attrs, sub, _ in
            let link = Self.linkTarget(attrs[.link])
            if link != openLink {
                if openLink != nil { xml += "</w:hyperlink>" }
                if let link {
                    let id = addRelationship(type: "\(Self.relBase)/hyperlink", target: link, external: true)
                    xml += "<w:hyperlink r:id=\"\(id)\" w:history=\"1\">"
                }
                openLink = link
            }
            let rpr = runProperties(attrs, isLink: link != nil)
            let open = "<w:r>" + (rpr.isEmpty ? "" : "<w:rPr>\(rpr)</w:rPr>")

            var buffer: [unichar] = []
            func flush() {
                guard !buffer.isEmpty else { return }
                xml += open + "<w:t xml:space=\"preserve\">\(esc(String(utf16CodeUnits: buffer, count: buffer.count)))</w:t></w:r>"
                buffer.removeAll()
            }
            for offset in 0..<sub.length {
                let c = string.character(at: sub.location + offset)
                switch c {
                case 0x09: flush(); xml += open + "<w:tab/></w:r>"
                case 0x2028, 0x0B: flush(); xml += open + "<w:br/></w:r>"
                case 0xFFFC:
                    flush()
                    if let attachment = attrs[.attachment] as? NSTextAttachment { xml += drawing(attachment) }
                default: buffer.append(c)
                }
            }
            flush()
        }
        if openLink != nil { xml += "</w:hyperlink>" }
        return xml
    }

    private static func linkTarget(_ value: Any?) -> String? {
        let url = (value as? URL) ?? (value as? String).flatMap(URL.init(string:))
        guard let url, let scheme = url.scheme?.lowercased(), ["http", "https", "mailto", "ftp"].contains(scheme) else { return nil }
        return url.absoluteString
    }

    private func runProperties(_ attrs: [NSAttributedString.Key: Any], isLink: Bool) -> String {
        var p = isLink ? "<w:rStyle w:val=\"Hyperlink\"/>" : ""
        let font = attrs[.font] as? NSFont
        if let family = font?.familyName, !family.hasPrefix(".") {
            let f = esc(family)
            p += "<w:rFonts w:ascii=\"\(f)\" w:hAnsi=\"\(f)\" w:eastAsia=\"\(f)\" w:cs=\"\(f)\"/>"
        }
        let traits = font?.fontDescriptor.symbolicTraits ?? []
        if traits.contains(.bold) { p += "<w:b/>" }
        if traits.contains(.italic) { p += "<w:i/>" }
        if ((attrs[.strikethroughStyle] as? Int) ?? 0) != 0 { p += "<w:strike/>" }
        if let color = Self.hex(attrs[.foregroundColor] as? NSColor), color != "000000" { p += "<w:color w:val=\"\(color)\"/>" }
        if let size = font?.pointSize {
            let half = Int((size * 2).rounded())
            p += "<w:sz w:val=\"\(half)\"/><w:szCs w:val=\"\(half)\"/>"
        }
        let underline = (attrs[.underlineStyle] as? Int) ?? 0
        if underline != 0 { p += "<w:u w:val=\"\(underline & NSUnderlineStyle.double.rawValue != 0 ? "double" : "single")\"/>" }
        if let fill = Self.hex(attrs[.backgroundColor] as? NSColor) { p += "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"\(fill)\"/>" }
        let script = (attrs[.superscript] as? Int) ?? 0
        if script != 0 { p += "<w:vertAlign w:val=\"\(script > 0 ? "superscript" : "subscript")\"/>" }
        return p
    }

    private static func hex(_ color: NSColor?) -> String? {
        guard let c = color?.usingColorSpace(.sRGB), c.alphaComponent > 0.01 else { return nil }
        func byte(_ v: CGFloat) -> Int { Int((min(max(v, 0), 1) * 255).rounded()) }
        return String(format: "%02X%02X%02X", byte(c.redComponent), byte(c.greenComponent), byte(c.blueComponent))
    }

    private func drawing(_ attachment: NSTextAttachment) -> String {
        guard let image = DocumentImages.image(of: attachment) else { return "" }
        imageCount += 1
        let name = "image\(imageCount).\(image.fileExtension)"
        media.append((name, image.data))
        let id = addRelationship(type: "\(Self.relBase)/image", target: "media/\(name)")
        let content = layout.contentSize
        let scale = min(1, content.width / image.size.width, content.height / image.size.height)
        let cx = Int(image.size.width * scale * 12_700), cy = Int(image.size.height * scale * 12_700)
        return "<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">"
            + "<wp:extent cx=\"\(cx)\" cy=\"\(cy)\"/><wp:docPr id=\"\(imageCount)\" name=\"Picture \(imageCount)\"/>"
            + "<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>"
            + "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>"
            + "<pic:nvPicPr><pic:cNvPr id=\"\(imageCount)\" name=\"\(name)\"/><pic:cNvPicPr/></pic:nvPicPr>"
            + "<pic:blipFill><a:blip r:embed=\"\(id)\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
            + "<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"\(cx)\" cy=\"\(cy)\"/></a:xfrm>"
            + "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic>"
            + "</wp:inline></w:drawing></w:r>"
    }

    // MARK: - Parts

    private func document(_ body: String) -> String {
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" \
        xmlns:r="\(Self.relBase)" \
        xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" \
        xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" \
        xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><w:body>\(body)</w:body></w:document>
        """
    }

    private func styles() -> String {
        let bodyHalfPoints = Int((structure.bodySize * 2).rounded())
        let headingSizes = [2.0, 1.5, 1.25, 1.1, 1.0, 1.0]
        let headings = headingSizes.enumerated().map { i, factor in
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading\(i + 1)\"><w:name w:val=\"heading \(i + 1)\"/>"
                + "<w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"9\"/><w:qFormat/>"
                + "<w:pPr><w:keepNext/><w:spacing w:before=\"240\" w:after=\"120\"/><w:outlineLvl w:val=\"\(i)\"/></w:pPr>"
                + "<w:rPr><w:b/><w:sz w:val=\"\(Int(Double(bodyHalfPoints) * factor))\"/></w:rPr></w:style>"
        }.joined()
        return """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">\
        <w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val="\(bodyHalfPoints)"/><w:szCs w:val="\(bodyHalfPoints)"/></w:rPr></w:rPrDefault>\
        <w:pPrDefault><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>\
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>\
        \(headings)\
        <w:style w:type="character" w:styleId="Hyperlink"><w:name w:val="Hyperlink"/><w:uiPriority w:val="99"/>\
        <w:rPr><w:color w:val="0563C1"/><w:u w:val="single"/></w:rPr></w:style>\
        <w:style w:type="table" w:styleId="TableGrid"><w:name w:val="Table Grid"/><w:tblPr><w:tblCellMar>\
        <w:left w:w="108" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>\
        </w:styles>
        """
    }

    private func numbering() -> String {
        func levels(ordered: Bool) -> String {
            (0..<9).map { level in
                let format = ordered ? "<w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%\(level + 1).\"/>"
                                     : "<w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"\(["•", "◦", "▪"][level % 3])\"/>"
                return "<w:lvl w:ilvl=\"\(level)\"><w:start w:val=\"1\"/>\(format)<w:lvlJc w:val=\"left\"/>"
                    + "<w:pPr><w:ind w:left=\"\(720 * (level + 1))\" w:hanging=\"360\"/></w:pPr></w:lvl>"
            }.joined()
        }
        let nums = lists.values.sorted { $0.numID < $1.numID }.map { list in
            "<w:num w:numId=\"\(list.numID)\"><w:abstractNumId w:val=\"\(list.ordered ? 1 : 0)\"/>"
                + (list.ordered ? "<w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"\(list.start)\"/></w:lvlOverride>" : "")
                + "</w:num>"
        }.joined()
        return """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">\
        <w:abstractNum w:abstractNumId="0"><w:multiLevelType w:val="hybridMultilevel"/>\(levels(ordered: false))</w:abstractNum>\
        <w:abstractNum w:abstractNumId="1"><w:multiLevelType w:val="hybridMultilevel"/>\(levels(ordered: true))</w:abstractNum>\
        \(nums)</w:numbering>
        """
    }

    private func documentRels() -> String {
        let fixed = [("rId1", "\(Self.relBase)/styles", "styles.xml"), ("rId2", "\(Self.relBase)/numbering", "numbering.xml")]
        let all = fixed.map { "<Relationship Id=\"\($0.0)\" Type=\"\($0.1)\" Target=\"\($0.2)\"/>" }
            + relationships.map {
                "<Relationship Id=\"\($0.id)\" Type=\"\($0.type)\" Target=\"\(esc($0.target))\"\($0.external ? " TargetMode=\"External\"" : "")/>"
            }
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\(all.joined())</Relationships>"
    }

    private func contentTypes() -> String {
        let main = "application/vnd.openxmlformats-officedocument.wordprocessingml"
        return """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">\
        <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>\
        <Default Extension="xml" ContentType="application/xml"/>\
        <Default Extension="png" ContentType="image/png"/><Default Extension="jpg" ContentType="image/jpeg"/>\
        <Default Extension="gif" ContentType="image/gif"/>\
        <Override PartName="/word/document.xml" ContentType="\(main).document.main+xml"/>\
        <Override PartName="/word/styles.xml" ContentType="\(main).styles+xml"/>\
        <Override PartName="/word/numbering.xml" ContentType="\(main).numbering+xml"/>\
        <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>\
        <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>\
        </Types>
        """
    }

    private static let packageRels = """
    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">\
    <Relationship Id="rId1" Type="\(relBase)/officeDocument" Target="word/document.xml"/>\
    <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>\
    <Relationship Id="rId3" Type="\(relBase)/extended-properties" Target="docProps/app.xml"/>\
    </Relationships>
    """

    /// Title only: no author, so converting a file never adds the user's name to it.
    private func core() -> String {
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" \
        xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>\(esc(title))</dc:title></cp:coreProperties>
        """
    }

    private static let app = """
    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
    <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Application>Potion</Application></Properties>
    """
}
