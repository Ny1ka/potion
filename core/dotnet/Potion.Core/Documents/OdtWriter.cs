using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Potion.Core.Documents;

/// <summary>
/// Writes OpenDocument text (ODT) from a <see cref="Document"/>: headings (outline levels with heading styles),
/// paragraphs with alignment, styled text (one automatic style per distinct formatting), bulleted and numbered
/// lists, tables, links, inline images and the page layout.
/// </summary>
public sealed class OdtWriter
{
    private readonly Document _document;
    private readonly Dictionary<TextStyle, string> _textStyles = [];
    private readonly Dictionary<Alignment, string> _paragraphStyles = [];
    private readonly List<(string Name, byte[] Data, string Mime)> _pictures = [];
    private readonly HashSet<int> _orderedListsStarting = [];
    private readonly Dictionary<int, int> _numberedStarts = [];

    private OdtWriter(Document document) => _document = document;

    public static byte[] Write(Document document) => new OdtWriter(document).Build();

    private static string Esc(string s) => HtmlWriter.Escape(s);
    private static string Pt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture) + "pt";

    private byte[] Build()
    {
        var body = Body();
        var automatic = new StringBuilder();
        foreach (var (style, name) in _textStyles) automatic.Append($"<style:style style:name=\"{name}\" style:family=\"text\"><style:text-properties{TextProperties(style)}/></style:style>");
        foreach (var (align, name) in _paragraphStyles)
            automatic.Append($"<style:style style:name=\"{name}\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"><style:paragraph-properties fo:text-align=\"{AlignValue(align)}\"/></style:style>");
        automatic.Append("<style:style style:name=\"Quote\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"><style:paragraph-properties fo:margin-left=\"0.5in\"/></style:style>");
        automatic.Append("<style:style style:name=\"Code\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"><style:text-properties style:font-name=\"Consolas\" fo:font-family=\"Consolas\" fo:font-size=\"10pt\"/></style:style>");
        automatic.Append("<style:style style:name=\"Table\" style:family=\"table\"><style:table-properties table:border-model=\"collapsing\"/></style:style>");
        automatic.Append("<style:style style:name=\"Cell\" style:family=\"table-cell\"><style:table-cell-properties fo:border=\"0.5pt solid #999999\" fo:padding=\"0.04in\"/></style:style>");
        automatic.Append("<style:style style:name=\"Image\" style:family=\"graphic\"><style:graphic-properties style:vertical-pos=\"top\" style:vertical-rel=\"baseline\"/></style:style>");
        automatic.Append(ListStyle("Bullets", ordered: false, 1));
        foreach (var (id, start) in _numberedStarts) automatic.Append(ListStyle($"Numbers{id}", ordered: true, start));

        var content = $"""<?xml version="1.0" encoding="UTF-8"?>{"\n"}<office:document-content {Namespaces} office:version="1.3"><office:automatic-styles>{automatic}</office:automatic-styles><office:body><office:text>{body}</office:text></office:body></office:document-content>""";
        var page = _document.Page ?? PageSetup.A4;
        var headings = string.Concat(Enumerable.Range(1, 6).Select(level =>
            $"<style:style style:name=\"Heading_20_{level}\" style:display-name=\"Heading {level}\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:default-outline-level=\"{level}\">"
            + $"<style:paragraph-properties fo:margin-top=\"12pt\" fo:margin-bottom=\"6pt\" fo:keep-with-next=\"always\"/>"
            + $"<style:text-properties fo:font-size=\"{Pt(12 * new[] { 2.0, 1.5, 1.25, 1.1, 1.0, 1.0 }[level - 1])}\" fo:font-weight=\"bold\"/></style:style>"));
        var styles = $"""<?xml version="1.0" encoding="UTF-8"?>{"\n"}<office:document-styles {Namespaces} office:version="1.3"><office:font-face-decls><style:font-face style:name="Calibri" svg:font-family="Calibri"/><style:font-face style:name="Consolas" svg:font-family="Consolas"/></office:font-face-decls><office:styles><style:default-style style:family="paragraph"><style:paragraph-properties fo:margin-bottom="6pt"/><style:text-properties style:font-name="Calibri" fo:font-size="12pt"/></style:default-style><style:style style:name="Standard" style:family="paragraph"/>{headings}</office:styles><office:automatic-styles><style:page-layout style:name="Page"><style:page-layout-properties fo:page-width="{Pt(page.Width)}" fo:page-height="{Pt(page.Height)}" fo:margin-top="{Pt(page.Top)}" fo:margin-bottom="{Pt(page.Bottom)}" fo:margin-left="{Pt(page.Left)}" fo:margin-right="{Pt(page.Right)}" style:print-orientation="{(page.Width > page.Height ? "landscape" : "portrait")}"/></style:page-layout></office:automatic-styles><office:master-styles><style:master-page style:name="Standard" style:page-layout-name="Page"/></office:master-styles></office:document-styles>""";
        var manifest = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3"><manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.text"/><manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/><manifest:file-entry manifest:full-path="styles.xml" manifest:media-type="text/xml"/>""");
        foreach (var (name, _, mime) in _pictures) manifest.Append($"<manifest:file-entry manifest:full-path=\"Pictures/{name}\" manifest:media-type=\"{mime}\"/>");
        manifest.Append("</manifest:manifest>");

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] data, CompressionLevel level = CompressionLevel.Optimal) { using var s = zip.CreateEntry(name, level).Open(); s.Write(data); }
            Add("mimetype", Encoding.ASCII.GetBytes("application/vnd.oasis.opendocument.text"), CompressionLevel.NoCompression);   // first, stored
            Add("content.xml", Encoding.UTF8.GetBytes(content));
            Add("styles.xml", Encoding.UTF8.GetBytes(styles));
            Add("META-INF/manifest.xml", Encoding.UTF8.GetBytes(manifest.ToString()));
            foreach (var (name, data, _) in _pictures) Add("Pictures/" + name, data, CompressionLevel.NoCompression);
        }
        return buffer.ToArray();
    }

    private const string Namespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" "
        + "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" "
        + "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" "
        + "xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\"";

    private static string AlignValue(Alignment a) => a switch { Alignment.Center => "center", Alignment.End => "end", Alignment.Justify => "justify", _ => "start" };

    private static string ListStyle(string name, bool ordered, int start) =>
        $"<text:list-style style:name=\"{name}\">" + string.Concat(Enumerable.Range(1, 10).Select(level => ordered
            ? $"<text:list-level-style-number text:level=\"{level}\" style:num-format=\"1\" style:num-suffix=\".\"{(start != 1 ? $" text:start-value=\"{start}\"" : "")}><style:list-level-properties text:space-before=\"{Pt(18 * (level - 1))}\" text:min-label-width=\"18pt\"/></text:list-level-style-number>"
            : $"<text:list-level-style-bullet text:level=\"{level}\" text:bullet-char=\"{new[] { "•", "◦", "▪" }[(level - 1) % 3]}\"><style:list-level-properties text:space-before=\"{Pt(18 * (level - 1))}\" text:min-label-width=\"18pt\"/></text:list-level-style-bullet>"))
        + "</text:list-style>";

    private static string TextProperties(TextStyle s)
    {
        var p = new StringBuilder();
        if (s.Bold) p.Append(" fo:font-weight=\"bold\"");
        if (s.Italic) p.Append(" fo:font-style=\"italic\"");
        if (s.Underline || s.Link is not null) p.Append(" style:text-underline-style=\"solid\" style:text-underline-width=\"auto\" style:text-underline-color=\"font-color\"");
        if (s.Strike) p.Append(" style:text-line-through-style=\"solid\"");
        if (s.Superscript) p.Append(" style:text-position=\"super 58%\"");
        if (s.Subscript) p.Append(" style:text-position=\"sub 58%\"");
        var font = s.Code ? "Consolas" : s.FontFamily;
        if (font is not null) p.Append($" style:font-name=\"{Esc(font)}\" fo:font-family=\"{Esc(font)}\"");
        if (s.FontSize is { } size) p.Append($" fo:font-size=\"{Pt(size)}\"");
        if (s.Link is not null) p.Append(" fo:color=\"#0563C1\"");
        else if (s.Color is { } color) p.Append($" fo:color=\"#{color}\"");
        return p.ToString();
    }

    // Body

    private string Body()
    {
        var xml = new StringBuilder();
        var blocks = _document.Blocks;
        var i = 0;
        while (i < blocks.Count)
        {
            if (blocks[i] is ListItem first)
            {
                // Consecutive items of one list (and their nested items) become one text:list.
                var items = new List<ListItem>();
                while (i < blocks.Count && blocks[i] is ListItem item && (item.ListId == first.ListId || item.Depth > 0)) { items.Add(item); i++; }
                xml.Append(List(items, 0));
                continue;
            }
            xml.Append(Block(blocks[i++]));
        }
        return xml.ToString();
    }

    private string List(List<ListItem> items, int depth)
    {
        var top = items.FirstOrDefault(i => i.Depth == depth) ?? items[0];
        var style = top.Ordered ? $"Numbers{top.ListId}" : "Bullets";
        if (top.Ordered) _numberedStarts.TryAdd(top.ListId, top.Number);
        var xml = new StringBuilder($"<text:list text:style-name=\"{style}\">");
        var i = 0;
        while (i < items.Count)
        {
            var item = items[i++];
            var nested = new List<ListItem>();
            while (i < items.Count && items[i].Depth > item.Depth) nested.Add(items[i++]);
            xml.Append($"<text:list-item><text:p>{Inlines(item.Inlines)}</text:p>");
            if (nested.Count > 0) xml.Append(List(nested, item.Depth + 1));
            xml.Append("</text:list-item>");
        }
        return xml.Append("</text:list>").ToString();
    }

    private string Block(Block block)
    {
        switch (block)
        {
            case Paragraph { HeadingLevel: > 0 } h:
                return $"<text:h text:style-name=\"Heading_20_{h.HeadingLevel}\" text:outline-level=\"{h.HeadingLevel}\">{Inlines(h.Inlines)}</text:h>";
            case Paragraph p:
                var styleName = p.Quote ? "Quote" : p.Alignment == Alignment.Start ? "Standard" : ParagraphStyle(p.Alignment);
                return $"<text:p text:style-name=\"{styleName}\">{Inlines(p.Inlines)}</text:p>";
            case ListItem item:
                return List([item], item.Depth);
            case CodeBlock code:
                return string.Concat(code.Code.Split('\n').Select(line => $"<text:p text:style-name=\"Code\">{Spaces(Esc(line))}</text:p>"));
            case HorizontalRule:
                return "<text:p text:style-name=\"Standard\">―――</text:p>";
            case PageBreak:
                return "<text:soft-page-break/>";
            case Table table:
                var columns = Math.Max(table.ColumnCount, 1);
                var xml = new StringBuilder($"<table:table table:style-name=\"Table\"><table:table-column table:number-columns-repeated=\"{columns}\"/>");
                for (var r = 0; r < table.Rows.Count; r++)
                {
                    if (r == 0 && table.HasHeader) xml.Append("<table:table-header-rows>");
                    xml.Append("<table:table-row>");
                    for (var c = 0; c < columns; c++)
                    {
                        var cell = c < table.Rows[r].Count ? table.Rows[r][c] : new TableCell([]);
                        var content = string.Concat(cell.Blocks.Select(Block));
                        xml.Append($"<table:table-cell table:style-name=\"Cell\" office:value-type=\"string\">{(content.Length > 0 ? content : "<text:p/>")}</table:table-cell>");
                    }
                    xml.Append("</table:table-row>");
                    if (r == 0 && table.HasHeader) xml.Append("</table:table-header-rows>");
                }
                return xml.Append("</table:table>").ToString();
            default:
                return "";
        }
    }

    private string ParagraphStyle(Alignment alignment)
    {
        if (!_paragraphStyles.TryGetValue(alignment, out var name)) _paragraphStyles[alignment] = name = $"P{_paragraphStyles.Count + 1}";
        return name;
    }

    private string Inlines(List<Inline> inlines)
    {
        var xml = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LineBreak: xml.Append("<text:line-break/>"); break;
                case ImageRun image:
                    var name = $"image{_pictures.Count + 1}.{image.Extension}";
                    _pictures.Add((name, image.Data, image.MimeType));
                    xml.Append($"<draw:frame draw:style-name=\"Image\" draw:name=\"{Esc(image.Alt ?? name)}\" text:anchor-type=\"as-char\" svg:width=\"{Pt(image.Width)}\" svg:height=\"{Pt(image.Height)}\">"
                               + $"<draw:image xlink:href=\"Pictures/{name}\" xlink:type=\"simple\" xlink:show=\"embed\" xlink:actuate=\"onLoad\"/></draw:frame>");
                    break;
                case TextRun run:
                    var text = Spaces(Esc(run.Text).Replace("\t", "<text:tab/>"));
                    if (run.Style != TextStyle.Plain)
                    {
                        var key = run.Style with { Link = run.Style.Link is null ? null : "link" };
                        if (!_textStyles.TryGetValue(key, out var styleName)) _textStyles[key] = styleName = $"T{_textStyles.Count + 1}";
                        text = $"<text:span text:style-name=\"{styleName}\">{text}</text:span>";
                    }
                    if (run.Style.Link is { } link && HtmlReader.IsLink(link)) text = $"<text:a xlink:type=\"simple\" xlink:href=\"{Esc(link)}\">{text}</text:a>";
                    xml.Append(text);
                    break;
            }
        }
        return xml.ToString();
    }

    /// <summary>ODF collapses runs of spaces; keep them with text:s.</summary>
    private static string Spaces(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, "  +", m => " " + $"<text:s text:c=\"{m.Length - 1}\"/>");
}
