using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Potion.Core.Documents;

/// <summary>
/// Writes Word documents (Office Open XML) from a <see cref="Document"/>, with the same structure as the macOS
/// writer: heading styles, real hyperlinks, numbered and bulleted lists, tables, images, page setup, and no author
/// metadata. Element order follows the OOXML schema, which Word checks.
/// </summary>
public sealed class DocxWriter
{
    private const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private readonly Document _document;
    private readonly PageSetup _page;
    private readonly string _title;
    private readonly List<(string Id, string Type, string Target, bool External)> _relationships = [];
    private readonly List<(string Name, byte[] Data)> _media = [];
    private readonly Dictionary<int, (int NumId, bool Ordered, int Start, int Level)> _lists = [];
    private int _images;

    public DocxWriter(Document document, string title)
    {
        _document = document;
        _page = document.Page ?? PageSetup.Letter;
        _title = title;
    }

    public static byte[] Write(Document document, string title) => new DocxWriter(document, title).Build();

    private byte[] Build()
    {
        var body = Body();   // first: collects relationships, images and lists
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text) { using var s = zip.CreateEntry(name).Open(); s.Write(Encoding.UTF8.GetBytes(text)); }
            Add("[Content_Types].xml", ContentTypes);
            Add("_rels/.rels", PackageRels);
            Add("word/document.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>{"\n"}<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="{RelBase}" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><w:body>{body}</w:body></w:document>""");
            Add("word/styles.xml", Styles());
            Add("word/numbering.xml", Numbering());
            Add("word/_rels/document.xml.rels", DocumentRels());
            foreach (var (name, data) in _media) { using var s = zip.CreateEntry("word/media/" + name, CompressionLevel.NoCompression).Open(); s.Write(data); }
            Add("docProps/core.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>{"\n"}<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>{Esc(_title)}</dc:title></cp:coreProperties>""");
            Add("docProps/app.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Application>Potion</Application></Properties>""");
        }
        return buffer.ToArray();
    }

    private static string Esc(string s) => HtmlWriter.Escape(s);
    private static int Twips(double points) => (int)Math.Round(points * 20);

    private string Rel(string type, string target, bool external = false)
    {
        var id = $"rId{_relationships.Count + 10}";   // rId1–9 are left for the fixed parts
        _relationships.Add((id, type, target, external));
        return id;
    }

    // Body

    private string Body()
    {
        var xml = new StringBuilder();
        var blocks = _document.Blocks;
        for (var i = 0; i < blocks.Count; i++)
        {
            xml.Append(Block(blocks[i]));
            if (blocks[i] is Table && i == blocks.Count - 1) xml.Append("<w:p/>");   // Word expects a paragraph after a final table
        }
        var orient = _page.Width > _page.Height ? " w:orient=\"landscape\"" : "";
        xml.Append($"<w:sectPr><w:pgSz w:w=\"{Twips(_page.Width)}\" w:h=\"{Twips(_page.Height)}\"{orient}/>"
                   + $"<w:pgMar w:top=\"{Twips(_page.Top)}\" w:right=\"{Twips(_page.Right)}\" w:bottom=\"{Twips(_page.Bottom)}\" "
                   + $"w:left=\"{Twips(_page.Left)}\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>");
        return xml.ToString();
    }

    private string Block(Block block) => block switch
    {
        Paragraph p => ParagraphXml(p.Inlines, p.HeadingLevel > 0 ? $"Heading{p.HeadingLevel}" : null, null, p.Alignment, p.Quote),
        ListItem item => ParagraphXml(item.Inlines, null, (NumberingId(item), Math.Min(item.Depth, 8)), Alignment.Start, false),
        Table t => TableXml(t),
        CodeBlock c => string.Concat(c.Code.Split('\n').Select(line =>
            ParagraphXml([new TextRun(line, TextStyle.Plain with { Code = true })], null, null, Alignment.Start, false))),
        HorizontalRule => "<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"auto\"/></w:pBdr></w:pPr></w:p>",
        PageBreak => "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>",
        _ => "",
    };

    private int NumberingId(ListItem item)
    {
        if (_lists.TryGetValue(item.ListId, out var known)) return known.NumId;
        var id = _lists.Count + 1;
        _lists[item.ListId] = (id, item.Ordered, item.Number, Math.Min(item.Depth, 8));
        return id;
    }

    private string ParagraphXml(List<Inline> inlines, string? style, (int NumId, int Level)? numbering, Alignment alignment, bool quote)
    {
        var ppr = new StringBuilder();
        if (style is not null) ppr.Append($"<w:pStyle w:val=\"{style}\"/>");
        if (numbering is { } n) ppr.Append($"<w:numPr><w:ilvl w:val=\"{n.Level}\"/><w:numId w:val=\"{n.NumId}\"/></w:numPr>");
        if (quote) ppr.Append("<w:ind w:left=\"720\"/>");
        ppr.Append(alignment switch
        {
            Alignment.Center => "<w:jc w:val=\"center\"/>", Alignment.End => "<w:jc w:val=\"right\"/>", Alignment.Justify => "<w:jc w:val=\"both\"/>", _ => "",
        });
        return "<w:p>" + (ppr.Length > 0 ? $"<w:pPr>{ppr}</w:pPr>" : "") + Runs(inlines) + "</w:p>";
    }

    private string TableXml(Table table)
    {
        var columns = Math.Max(table.ColumnCount, 1);
        var width = Twips(_page.Width - _page.Left - _page.Right) / columns;
        const string border = "w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"";
        var xml = new StringBuilder("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>");
        foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" }) xml.Append($"<w:{side} {border}/>");
        xml.Append("</w:tblBorders></w:tblPr><w:tblGrid>");
        for (var c = 0; c < columns; c++) xml.Append($"<w:gridCol w:w=\"{width}\"/>");
        xml.Append("</w:tblGrid>");
        for (var r = 0; r < table.Rows.Count; r++)
        {
            xml.Append(r == 0 && table.HasHeader ? "<w:tr><w:trPr><w:tblHeader/></w:trPr>" : "<w:tr>");
            for (var c = 0; c < columns; c++)
            {
                var cell = c < table.Rows[r].Count ? table.Rows[r][c] : new TableCell([]);
                var content = string.Concat(cell.Blocks.Select(Block));
                xml.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{width}\" w:type=\"dxa\"/></w:tcPr>{(content.Contains("<w:p") ? content : "<w:p/>")}</w:tc>");
            }
            xml.Append("</w:tr>");
        }
        return xml.Append("</w:tbl>").ToString();
    }

    // Runs

    private string Runs(List<Inline> inlines)
    {
        var xml = new StringBuilder();
        string? openLink = null;
        foreach (var inline in inlines)
        {
            var link = inline is TextRun { Style.Link: { } l } && HtmlReader.IsLink(l) ? l : null;
            if (link != openLink)
            {
                if (openLink is not null) xml.Append("</w:hyperlink>");
                if (link is not null) xml.Append($"<w:hyperlink r:id=\"{Rel($"{RelBase}/hyperlink", link, external: true)}\" w:history=\"1\">");
                openLink = link;
            }
            switch (inline)
            {
                case LineBreak: xml.Append("<w:r><w:br/></w:r>"); break;
                case ImageRun image: xml.Append(Drawing(image)); break;
                case TextRun run:
                    var rpr = RunProperties(run.Style, link is not null);
                    var open = "<w:r>" + (rpr.Length > 0 ? $"<w:rPr>{rpr}</w:rPr>" : "");
                    var parts = run.Text.Split('\t');
                    for (var i = 0; i < parts.Length; i++)
                    {
                        if (i > 0) xml.Append(open).Append("<w:tab/></w:r>");
                        if (parts[i].Length > 0) xml.Append(open).Append($"<w:t xml:space=\"preserve\">{Esc(parts[i])}</w:t></w:r>");
                    }
                    break;
            }
        }
        if (openLink is not null) xml.Append("</w:hyperlink>");
        return xml.ToString();
    }

    private static string RunProperties(TextStyle s, bool isLink)
    {
        var p = new StringBuilder(isLink ? "<w:rStyle w:val=\"Hyperlink\"/>" : "");
        var font = s.Code ? "Consolas" : s.FontFamily;
        if (font is not null) p.Append($"<w:rFonts w:ascii=\"{Esc(font)}\" w:hAnsi=\"{Esc(font)}\" w:eastAsia=\"{Esc(font)}\" w:cs=\"{Esc(font)}\"/>");
        if (s.Bold) p.Append("<w:b/>");
        if (s.Italic) p.Append("<w:i/>");
        if (s.Strike) p.Append("<w:strike/>");
        if (s.Color is { } color && color != "000000") p.Append($"<w:color w:val=\"{color}\"/>");
        if (s.FontSize is { } size) { var half = (int)Math.Round(size * 2); p.Append($"<w:sz w:val=\"{half}\"/><w:szCs w:val=\"{half}\"/>"); }
        if (s.Underline && !isLink) p.Append("<w:u w:val=\"single\"/>");
        if (s.Superscript || s.Subscript) p.Append($"<w:vertAlign w:val=\"{(s.Superscript ? "superscript" : "subscript")}\"/>");
        return p.ToString();
    }

    private string Drawing(ImageRun image)
    {
        if (image.MimeType is not ("image/png" or "image/jpeg" or "image/gif")) return "";   // DOCX readers support these everywhere
        _images++;
        var name = $"image{_images}.{image.Extension}";
        _media.Add((name, image.Data));
        var id = Rel($"{RelBase}/image", $"media/{name}");
        var contentWidth = _page.Width - _page.Left - _page.Right;
        var contentHeight = _page.Height - _page.Top - _page.Bottom;
        var scale = Math.Min(1, Math.Min(contentWidth / image.Width, contentHeight / image.Height));
        long cx = (long)(image.Width * scale * 12700), cy = (long)(image.Height * scale * 12700);
        return $"<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/>"
               + $"<wp:docPr id=\"{_images}\" name=\"Picture {_images}\" descr=\"{Esc(image.Alt ?? "")}\"/>"
               + "<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>"
               + "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>"
               + $"<pic:nvPicPr><pic:cNvPr id=\"{_images}\" name=\"{name}\"/><pic:cNvPicPr/></pic:nvPicPr>"
               + $"<pic:blipFill><a:blip r:embed=\"{id}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
               + $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>"
               + "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";
    }

    // Parts

    private string Styles()
    {
        var factors = new[] { 2.0, 1.5, 1.25, 1.1, 1.0, 1.0 };
        var headings = string.Concat(factors.Select((f, i) =>
            $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{i + 1}\"><w:name w:val=\"heading {i + 1}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/>"
            + $"<w:uiPriority w:val=\"9\"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before=\"240\" w:after=\"120\"/><w:outlineLvl w:val=\"{i}\"/></w:pPr>"
            + $"<w:rPr><w:b/><w:sz w:val=\"{(int)(24 * f)}\"/></w:rPr></w:style>"));
        return """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" + "\n"
            + """<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">"""
            + """<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="Calibri" w:cs="Calibri"/><w:sz w:val="24"/><w:szCs w:val="24"/></w:rPr></w:rPrDefault>"""
            + """<w:pPrDefault><w:pPr><w:spacing w:after="120" w:line="264" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>"""
            + """<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>"""
            + headings
            + """<w:style w:type="character" w:styleId="Hyperlink"><w:name w:val="Hyperlink"/><w:uiPriority w:val="99"/><w:rPr><w:color w:val="0563C1"/><w:u w:val="single"/></w:rPr></w:style>"""
            + """<w:style w:type="table" w:styleId="TableGrid"><w:name w:val="Table Grid"/><w:tblPr><w:tblCellMar><w:left w:w="108" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>"""
            + "</w:styles>";
    }

    private string Numbering()
    {
        static string Levels(bool ordered) => string.Concat(Enumerable.Range(0, 9).Select(level =>
        {
            var format = ordered ? $"<w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%{level + 1}.\"/>"
                                 : $"<w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"{new[] { "•", "◦", "▪" }[level % 3]}\"/>";
            return $"<w:lvl w:ilvl=\"{level}\"><w:start w:val=\"1\"/>{format}<w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"{720 * (level + 1)}\" w:hanging=\"360\"/></w:pPr></w:lvl>";
        }));
        var nums = string.Concat(_lists.Values.OrderBy(l => l.NumId).Select(l =>
            $"<w:num w:numId=\"{l.NumId}\"><w:abstractNumId w:val=\"{(l.Ordered ? 1 : 0)}\"/>"
            + (l.Ordered ? $"<w:lvlOverride w:ilvl=\"{l.Level}\"><w:startOverride w:val=\"{l.Start}\"/></w:lvlOverride>" : "") + "</w:num>"));
        return """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" + "\n"
            + """<w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">"""
            + $"<w:abstractNum w:abstractNumId=\"0\"><w:multiLevelType w:val=\"hybridMultilevel\"/>{Levels(false)}</w:abstractNum>"
            + $"<w:abstractNum w:abstractNumId=\"1\"><w:multiLevelType w:val=\"hybridMultilevel\"/>{Levels(true)}</w:abstractNum>"
            + nums + "</w:numbering>";
    }

    private string DocumentRels()
    {
        var all = new StringBuilder($"<Relationship Id=\"rId1\" Type=\"{RelBase}/styles\" Target=\"styles.xml\"/><Relationship Id=\"rId2\" Type=\"{RelBase}/numbering\" Target=\"numbering.xml\"/>");
        foreach (var (id, type, target, external) in _relationships)
            all.Append($"<Relationship Id=\"{id}\" Type=\"{type}\" Target=\"{Esc(target)}\"{(external ? " TargetMode=\"External\"" : "")}/>");
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{all}</Relationships>";
    }

    private const string Main = "application/vnd.openxmlformats-officedocument.wordprocessingml";
    private static readonly string ContentTypes = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" + "\n"
        + """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">"""
        + """<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>"""
        + """<Default Extension="png" ContentType="image/png"/><Default Extension="jpg" ContentType="image/jpeg"/><Default Extension="gif" ContentType="image/gif"/>"""
        + $"<Override PartName=\"/word/document.xml\" ContentType=\"{Main}.document.main+xml\"/>"
        + $"<Override PartName=\"/word/styles.xml\" ContentType=\"{Main}.styles+xml\"/>"
        + $"<Override PartName=\"/word/numbering.xml\" ContentType=\"{Main}.numbering+xml\"/>"
        + """<Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>"""
        + """<Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/></Types>""";
    private static readonly string PackageRels = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" + "\n"
        + """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">"""
        + $"<Relationship Id=\"rId1\" Type=\"{RelBase}/officeDocument\" Target=\"word/document.xml\"/>"
        + """<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>"""
        + $"<Relationship Id=\"rId3\" Type=\"{RelBase}/extended-properties\" Target=\"docProps/app.xml\"/></Relationships>";
}
