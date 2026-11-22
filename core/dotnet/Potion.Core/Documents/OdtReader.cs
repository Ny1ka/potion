using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Potion.Core.Engines;

namespace Potion.Core.Documents;

/// <summary>
/// Reads OpenDocument text (ODT): headings, paragraphs, lists, tables, links, images, formatting from automatic
/// and named styles, and the page layout. Password-protected files are reported as copy-protected.
/// </summary>
public static class OdtReader
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    public static bool IsProtected(ZipArchive zip)
    {
        if (zip.GetEntry("META-INF/manifest.xml") is not { } entry) return false;
        using var stream = entry.Open();
        return XDocument.Load(stream).Descendants(Manifest + "encryption-data").Any();
    }

    public static Document Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (IsProtected(zip)) throw ConversionException.CopyProtected();
        var content = Load(zip, "content.xml") ?? throw new InvalidDataException("no content.xml");
        var styles = Load(zip, "styles.xml");
        var reader = new Reader(zip, content, styles);
        var body = content.Root?.Element(Office + "body")?.Element(Office + "text") ?? throw new InvalidDataException("no text body");
        var document = new Document { Page = reader.PageSetup() };
        document.Blocks.AddRange(reader.Blocks(body.Elements(), 0));
        return document;
    }

    private static XDocument? Load(ZipArchive zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private sealed class Reader(ZipArchive zip, XDocument content, XDocument? stylesDoc)
    {
        private readonly Dictionary<string, XElement> _styles = Index(content, stylesDoc, Style + "style");
        private readonly Dictionary<string, XElement> _listStyles = Index(content, stylesDoc, Text + "list-style");
        private int _listId;

        private static Dictionary<string, XElement> Index(XDocument content, XDocument? styles, XName kind)
        {
            var map = new Dictionary<string, XElement>();
            foreach (var doc in new[] { styles, content })
                foreach (var s in doc?.Descendants(kind) ?? [])
                    if (s.Attribute(Style + "name")?.Value is { } name) map[name] = s;
            return map;
        }

        public PageSetup? PageSetup()
        {
            var layout = stylesDoc?.Descendants(Style + "page-layout-properties").FirstOrDefault();
            if (layout is null || Length(layout.Attribute(Fo + "page-width")?.Value) is not { } w || Length(layout.Attribute(Fo + "page-height")?.Value) is not { } h)
                return null;
            double M(string name) => Length(layout.Attribute(Fo + name)?.Value) ?? 72;
            return new PageSetup(w, h, M("margin-top"), M("margin-right"), M("margin-bottom"), M("margin-left"));
        }

        public IEnumerable<Block> Blocks(IEnumerable<XElement> elements, int listDepth, (bool Ordered, int Id, List<int> Counter)? list = null)
        {
            foreach (var e in elements)
            {
                if (e.Name == Text + "h")
                {
                    var level = int.TryParse(e.Attribute(Text + "outline-level")?.Value, out var l) ? Math.Clamp(l, 1, 6) : 1;
                    yield return new Paragraph(Inlines(e, TextStyle.Plain), level, AlignmentOf(e));
                }
                else if (e.Name == Text + "p")
                {
                    var inlines = Inlines(e, StyleOf(e.Attribute(Text + "style-name")?.Value, TextStyle.Plain));
                    if (list is { } current)
                    {
                        current.Counter[0]++;
                        if (inlines.Count > 0) yield return new ListItem(inlines, listDepth - 1, current.Ordered, current.Counter[0], current.Id);
                    }
                    else
                    {
                        yield return new Paragraph(inlines, 0, AlignmentOf(e));
                    }
                }
                else if (e.Name == Text + "list")
                {
                    var styleName = e.Attribute(Text + "style-name")?.Value;
                    var ordered = styleName is not null && _listStyles.TryGetValue(styleName, out var ls)
                        && ls.Elements().FirstOrDefault()?.Name == Text + "list-level-style-number";
                    var start = 1;
                    if (styleName is not null && _listStyles.TryGetValue(styleName, out var ls2)
                        && int.TryParse(ls2.Elements().FirstOrDefault()?.Attribute(Text + "start-value")?.Value, out var s)) start = s;
                    var id = ++_listId;
                    var counter = new List<int> { start - 1 };
                    foreach (var item in e.Elements(Text + "list-item"))
                        foreach (var block in Blocks(item.Elements(), listDepth + 1, (ordered, id, counter)))
                            yield return block;
                }
                else if (e.Name == TableNs + "table")
                {
                    var rows = e.Descendants(TableNs + "table-row")
                        .Select(r => r.Elements(TableNs + "table-cell").Select(c => new TableCell(Blocks(c.Elements(), 0).ToList())).ToList())
                        .Where(r => r.Count > 0).ToList();
                    yield return new Table(rows, e.Element(TableNs + "table-header-rows") is not null);
                }
                else if (e.Name == Text + "section" || e.Name == Text + "list-header")
                {
                    foreach (var block in Blocks(e.Elements(), listDepth, list)) yield return block;
                }
            }
        }

        private List<Inline> Inlines(XElement element, TextStyle style)
        {
            var inlines = new List<Inline>();
            void Add(string text, TextStyle s)
            {
                if (text.Length == 0) return;
                if (inlines.Count > 0 && inlines[^1] is TextRun last && last.Style == s) inlines[^1] = last with { Text = last.Text + text };
                else inlines.Add(new TextRun(text, s));
            }
            void Walk(XElement e, TextStyle s)
            {
                foreach (var node in e.Nodes())
                {
                    if (node is XText t) { Add(t.Value, s); continue; }
                    if (node is not XElement child) continue;
                    if (child.Name == Text + "span") Walk(child, StyleOf(child.Attribute(Text + "style-name")?.Value, s));
                    else if (child.Name == Text + "a")
                    {
                        var href = child.Attribute(XLink + "href")?.Value ?? "";
                        Walk(child, HtmlReader.IsLink(href) ? s with { Link = href } : s);
                    }
                    else if (child.Name == Text + "s") Add(new string(' ', int.TryParse(child.Attribute(Text + "c")?.Value, out var c) ? c : 1), s);
                    else if (child.Name == Text + "tab") Add("\t", s);
                    else if (child.Name == Text + "line-break") inlines.Add(new LineBreak());
                    else if (child.Name == Draw + "frame" && Image(child) is { } image) inlines.Add(image);
                    else if (child.Name != Text + "note" && child.Name != Office + "annotation") Walk(child, s);
                }
            }
            Walk(element, style);
            if (inlines.Count > 0 && inlines.All(i => i is TextRun r && r.Text.Trim().Length == 0)) return [];
            return inlines;
        }

        private ImageRun? Image(XElement frame)
        {
            var href = frame.Element(Draw + "image")?.Attribute(XLink + "href")?.Value;
            var mime = href is null ? null : HtmlReader.MimeForExtension(Path.GetExtension(href));
            if (href is null || mime is null || zip.GetEntry(href.TrimStart('.', '/')) is not { } entry || entry.Length > 50_000_000) return null;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            double width = Length(frame.Attribute(Svg + "width")?.Value) ?? 0, height = Length(frame.Attribute(Svg + "height")?.Value) ?? 0;
            if (width <= 0 || height <= 0) { var (w, h) = ImageSize.Of(bytes); (width, height) = (w * 0.75, h * 0.75); }
            return new ImageRun(bytes, mime, width, height, frame.Attribute(Draw + "name")?.Value);
        }

        /// <summary>Text formatting from a style and its parents.</summary>
        private TextStyle StyleOf(string? name, TextStyle inherited)
        {
            var chain = new List<XElement>();
            while (name is not null && _styles.TryGetValue(name, out var s) && chain.Count < 10)
            {
                chain.Insert(0, s);
                name = s.Attribute(Style + "parent-style-name")?.Value;
            }
            var style = inherited;
            foreach (var s in chain)
            {
                if (s.Element(Style + "text-properties") is not { } p) continue;
                if (p.Attribute(Fo + "font-weight")?.Value is { } weight) style = style with { Bold = weight is "bold" or "600" or "700" or "800" or "900" };
                if (p.Attribute(Fo + "font-style")?.Value is { } italic) style = style with { Italic = italic == "italic" };
                if (p.Attribute(Style + "text-underline-style")?.Value is { } u) style = style with { Underline = u != "none" };
                if (p.Attribute(Style + "text-line-through-style")?.Value is { } st) style = style with { Strike = st != "none" };
                if (p.Attribute(Fo + "color")?.Value is { Length: 7 } color) style = style with { Color = color[1..].ToUpperInvariant() };
                if (Length(p.Attribute(Fo + "font-size")?.Value) is { } size) style = style with { FontSize = size };
                if (p.Attribute(Style + "font-name")?.Value is { } font) style = style with { FontFamily = font };
                if (p.Attribute(Style + "text-position")?.Value is { } pos)
                    style = style with { Superscript = pos.StartsWith("super") || pos.StartsWith("33%"), Subscript = pos.StartsWith("sub") || pos.StartsWith("-") };
            }
            return style;
        }

        private Alignment AlignmentOf(XElement paragraph)
        {
            var name = paragraph.Attribute(Text + "style-name")?.Value;
            while (name is not null && _styles.TryGetValue(name, out var s))
            {
                if (s.Element(Style + "paragraph-properties")?.Attribute(Fo + "text-align")?.Value is { } align)
                    return align switch { "center" => Alignment.Center, "end" or "right" => Alignment.End, "justify" => Alignment.Justify, _ => Alignment.Start };
                name = s.Attribute(Style + "parent-style-name")?.Value;
            }
            return Alignment.Start;
        }
    }

    /// <summary>ODF lengths ("2.54cm", "1in", "12pt", "25.4mm") in points.</summary>
    public static double? Length(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var units = new (string Suffix, double Points)[] { ("cm", 72 / 2.54), ("mm", 72 / 25.4), ("in", 72), ("pt", 1), ("pc", 12), ("px", 0.75) };
        foreach (var (suffix, points) in units)
            if (value.EndsWith(suffix, StringComparison.Ordinal)
                && double.TryParse(value[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return n * points;
        return null;
    }
}
