using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Potion.Core.Documents;

/// <summary>
/// Reads HTML into a <see cref="Document"/>: headings, paragraphs, lists, tables, code, quotes, links, images and
/// inline formatting. Nothing is ever fetched: images come only from <c>data:</c> URIs or from the
/// <paramref name="resolveImage"/> callback (local files or EPUB entries); scripts, styles and remote
/// resources are ignored.
/// </summary>
public static partial class HtmlReader
{
    /// <summary>Returns an image's bytes and MIME type for an <c>src</c>, or null to skip it.</summary>
    public delegate (byte[] Data, string MimeType)? ImageResolver(string src);

    public static Document Read(string html, ImageResolver? resolveImage = null)
    {
        var page = new HtmlDocument { OptionFixNestedTags = true };
        page.LoadHtml(html);
        var document = new Document
        {
            Title = Decode(page.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "").Trim() is { Length: > 0 } t ? t : null,
        };
        var body = page.DocumentNode.SelectSingleNode("//body") ?? page.DocumentNode;
        new Builder(document.Blocks, resolveImage).Walk(body);
        return document;
    }

    private sealed class Builder(List<Block> output, ImageResolver? resolveImage)
    {
        private List<Inline> _inlines = [];
        private readonly Stack<TextStyle> _styles = new([TextStyle.Plain]);
        private readonly Stack<(bool Ordered, int Next, int Id)> _lists = new();
        private static int _nextListId;
        private int _heading;
        private int _quote;
        private Alignment _alignment;
        private bool _inListItem;

        private static readonly HashSet<string> BlockTags =
        [
            "p", "div", "section", "article", "header", "footer", "main", "nav", "aside", "figure", "figcaption",
            "address", "dl", "dt", "dd", "center", "body", "html", "form", "fieldset", "details", "summary",
        ];
        private static readonly HashSet<string> Ignored = ["script", "style", "head", "title", "noscript", "template", "iframe", "object", "embed", "svg", "math"];

        public void Walk(HtmlNode node)
        {
            foreach (var child in node.ChildNodes) Visit(child);
            Flush();
        }

        private void Visit(HtmlNode node)
        {
            if (node.NodeType == HtmlNodeType.Text) { AddText(Decode(((HtmlTextNode)node).Text)); return; }
            if (node.NodeType != HtmlNodeType.Element) return;
            var tag = node.Name.ToLowerInvariant();
            if (Ignored.Contains(tag)) return;

            switch (tag)
            {
                case "br": _inlines.Add(new LineBreak()); return;
                case "hr": Flush(); output.Add(new HorizontalRule()); return;
                case "img": AddImage(node); return;
                case "pre": Flush(); output.Add(new CodeBlock(Decode(node.InnerText).TrimEnd('\n', '\r'))); return;
                case "table": Flush(); output.Add(ReadTable(node)); return;
                case "ul" or "ol": VisitList(node, tag == "ol"); return;
                case "li": VisitListItem(node); return;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Flush();
                    _heading = tag[1] - '0';
                    WithAlignment(node, () => VisitChildren(node));
                    Flush();
                    _heading = 0;
                    return;
                case "blockquote":
                    Flush(); _quote++;
                    VisitChildren(node);
                    Flush(); _quote--;
                    return;
            }

            if (BlockTags.Contains(tag))
            {
                Flush();
                WithAlignment(node, () => VisitChildren(node));
                Flush();
                return;
            }

            // Inline element: push its formatting.
            var style = InlineStyle(node, tag, _styles.Peek());
            _styles.Push(style);
            VisitChildren(node);
            _styles.Pop();
        }

        private void VisitChildren(HtmlNode node)
        {
            foreach (var child in node.ChildNodes) Visit(child);
        }

        private void WithAlignment(HtmlNode node, Action body)
        {
            var saved = _alignment;
            if (AlignmentOf(node) is { } a) _alignment = a;
            body();
            Flush();
            _alignment = saved;
        }

        private void VisitList(HtmlNode node, bool ordered)
        {
            Flush();
            var start = int.TryParse(node.GetAttributeValue("start", "1"), out var s) ? s : 1;
            _lists.Push((ordered, start, Interlocked.Increment(ref _nextListId)));
            foreach (var child in node.ChildNodes)
            {
                if (child.NodeType == HtmlNodeType.Element && child.Name.Equals("li", StringComparison.OrdinalIgnoreCase)) VisitListItem(child);
                else Visit(child);
            }
            Flush();
            _lists.Pop();
        }

        private void VisitListItem(HtmlNode node)
        {
            Flush();
            if (_lists.Count == 0) _lists.Push((false, 1, Interlocked.Increment(ref _nextListId)));
            var saved = _inListItem;
            _inListItem = true;
            foreach (var child in node.ChildNodes)
            {
                // A nested list ends this item's own text.
                if (child.NodeType == HtmlNodeType.Element && child.Name.ToLowerInvariant() is "ul" or "ol") Flush();
                Visit(child);
            }
            Flush();
            _inListItem = saved;
        }

        private void Flush()
        {
            var inlines = Trim(_inlines);
            _inlines = [];
            if (inlines.Count == 0) return;
            if (_inListItem && _lists.Count > 0)
            {
                var (ordered, next, id) = _lists.Pop();
                output.Add(new ListItem(inlines, _lists.Count, ordered, next, id));
                _lists.Push((ordered, next + 1, id));
            }
            else
            {
                output.Add(new Paragraph(inlines, _heading, _alignment, _quote > 0));
            }
        }

        private void AddText(string text)
        {
            text = Whitespace().Replace(text, " ");
            if (text.Length == 0) return;
            // Collapse spaces across element boundaries, as browsers do.
            if (text[0] == ' ' && (_inlines.Count == 0 || _inlines[^1] is TextRun { Text: [.., ' '] } or LineBreak)) text = text[1..];
            if (text.Length == 0) return;
            var style = _styles.Peek();
            if (_heading > 0) style = style with { Bold = true };
            if (_inlines.Count > 0 && _inlines[^1] is TextRun last && last.Style == style) _inlines[^1] = last with { Text = last.Text + text };
            else _inlines.Add(new TextRun(text, style));
        }

        private void AddImage(HtmlNode node)
        {
            var src = node.GetAttributeValue("src", "").Trim();
            var image = ImageSource(src, resolveImage);
            if (image is null) return;
            var (pxW, pxH) = ImageSize.Of(image.Value.Data);
            double width = Dimension(node, "width") ?? pxW, height = Dimension(node, "height") ?? pxH;
            if (Dimension(node, "width") is not null && Dimension(node, "height") is null && pxW > 0) height = width * pxH / pxW;
            if (width <= 0 || height <= 0) (width, height) = (240, 160);
            _inlines.Add(new ImageRun(image.Value.Data, image.Value.MimeType, width * 0.75, height * 0.75,
                                      Decode(node.GetAttributeValue("alt", "")) is { Length: > 0 } alt ? alt : null));
        }

        private static double? Dimension(HtmlNode node, string name) =>
            double.TryParse(node.GetAttributeValue(name, "").Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

        private Table ReadTable(HtmlNode table)
        {
            var rows = new List<List<TableCell>>();
            var hasHeader = false;
            foreach (var tr in table.Descendants("tr").Where(tr => tr.Ancestors("table").FirstOrDefault() == table))
            {
                var cells = new List<TableCell>();
                foreach (var cell in tr.ChildNodes.Where(c => c.Name is "td" or "th"))
                {
                    var blocks = new List<Block>();
                    var builder = new Builder(blocks, resolveImage);
                    if (cell.Name == "th") builder._styles.Push(TextStyle.Plain with { Bold = true });
                    builder.Walk(cell);
                    cells.Add(new TableCell(blocks));
                }
                if (rows.Count == 0 && tr.ChildNodes.Any(c => c.Name == "th")) hasHeader = true;
                if (cells.Count > 0) rows.Add(cells);
            }
            return new Table(rows, hasHeader);
        }

        private static TextStyle InlineStyle(HtmlNode node, string tag, TextStyle style)
        {
            style = tag switch
            {
                "b" or "strong" => style with { Bold = true },
                "i" or "em" or "cite" or "var" or "dfn" => style with { Italic = true },
                "u" or "ins" => style with { Underline = true },
                "s" or "strike" or "del" => style with { Strike = true },
                "code" or "kbd" or "samp" or "tt" => style with { Code = true },
                "sup" => style with { Superscript = true },
                "sub" => style with { Subscript = true },
                "a" => node.GetAttributeValue("href", "") is var href && IsLink(href) ? style with { Link = href } : style,
                _ => style,
            };
            var css = node.GetAttributeValue("style", "").ToLowerInvariant();
            if (css.Length > 0)
            {
                if (Regex.IsMatch(css, @"font-weight\s*:\s*(bold|[6-9]00)")) style = style with { Bold = true };
                if (Regex.IsMatch(css, @"font-style\s*:\s*italic")) style = style with { Italic = true };
                if (css.Contains("underline")) style = style with { Underline = true };
                if (css.Contains("line-through")) style = style with { Strike = true };
                if (Regex.Match(css, @"(?<![-\w])color\s*:\s*#([0-9a-f]{6}|[0-9a-f]{3})\b") is { Success: true } m)
                    style = style with { Color = Hex(m.Groups[1].Value) };
            }
            return style;
        }

        private static Alignment? AlignmentOf(HtmlNode node)
        {
            var value = node.Name == "center" ? "center" : node.GetAttributeValue("align", "").ToLowerInvariant();
            var css = Regex.Match(node.GetAttributeValue("style", "").ToLowerInvariant(), @"text-align\s*:\s*(\w+)");
            if (css.Success) value = css.Groups[1].Value;
            return value switch { "center" => Alignment.Center, "right" or "end" => Alignment.End, "justify" => Alignment.Justify, "left" or "start" => Alignment.Start, _ => null };
        }

        private static List<Inline> Trim(List<Inline> inlines)
        {
            while (inlines.Count > 0 && inlines[^1] is LineBreak) inlines.RemoveAt(inlines.Count - 1);
            if (inlines.Count > 0 && inlines[0] is TextRun first) inlines[0] = first with { Text = first.Text.TrimStart() };
            if (inlines.Count > 0 && inlines[^1] is TextRun last) inlines[^1] = last with { Text = last.Text.TrimEnd() };
            inlines.RemoveAll(i => i is TextRun { Text: "" });
            return inlines.Any(i => i is not TextRun t || t.Text.Trim().Length > 0) ? inlines : [];
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static string Decode(string text) => WebUtility.HtmlDecode(text);

    private static string Hex(string value) =>
        (value.Length == 3 ? string.Concat(value.Select(c => $"{c}{c}")) : value).ToUpperInvariant();

    /// <summary>Links worth keeping: web and email addresses (relative links point nowhere once converted).</summary>
    public static bool IsLink(string href) =>
        href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    /// <summary>An image from a <c>data:</c> URI or the resolver. Remote URLs are never fetched.</summary>
    public static (byte[] Data, string MimeType)? ImageSource(string src, ImageResolver? resolve)
    {
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = src.IndexOf(',');
            if (comma < 0 || !src[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
            try { return (Convert.FromBase64String(src[(comma + 1)..]), src[5..comma].Split(';')[0]); }
            catch (FormatException) { return null; }
        }
        if (src.Length == 0 || src.Contains("://") || src.StartsWith("//")) return null;
        return resolve?.Invoke(src);
    }

    /// <summary>Resolves image paths against a folder on disk: image files only, at most 50 MB.</summary>
    public static ImageResolver LocalImages(string directory) => src =>
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(src.Split('?', '#')[0])));
            var mime = MimeForExtension(Path.GetExtension(path));
            if (mime is null || !File.Exists(path) || new FileInfo(path).Length > 50_000_000) return null;
            return (File.ReadAllBytes(path), mime);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    };

    public static string? MimeForExtension(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "png" => "image/png", "jpg" or "jpeg" => "image/jpeg", "gif" => "image/gif", "bmp" => "image/bmp",
        "webp" => "image/webp", "svg" => "image/svg+xml", _ => null,
    };
}

/// <summary>Pixel size from an image header (PNG, JPEG, GIF, BMP, WebP), without decoding it.</summary>
public static class ImageSize
{
    public static (int Width, int Height) Of(byte[] d)
    {
        if (d.Length >= 24 && d[0] == 0x89 && d[1] == 0x50) return (Be32(d, 16), Be32(d, 20));
        if (d.Length >= 10 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F') return (d[6] | d[7] << 8, d[8] | d[9] << 8);
        if (d.Length >= 26 && d[0] == 'B' && d[1] == 'M') return (BitConverter.ToInt32(d, 18), Math.Abs(BitConverter.ToInt32(d, 22)));
        if (d.Length >= 30 && Encoding.ASCII.GetString(d, 8, 4) == "WEBP")
        {
            var chunk = Encoding.ASCII.GetString(d, 12, 4);
            if (chunk == "VP8 ") return (BitConverter.ToUInt16(d, 26) & 0x3FFF, BitConverter.ToUInt16(d, 28) & 0x3FFF);
            if (chunk == "VP8L") { var b = BitConverter.ToUInt32(d, 21); return ((int)(b & 0x3FFF) + 1, (int)((b >> 14) & 0x3FFF) + 1); }
            if (chunk == "VP8X") return (1 + (d[24] | d[25] << 8 | d[26] << 16), 1 + (d[27] | d[28] << 8 | d[29] << 16));
        }
        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                var marker = d[i + 1];
                var length = d[i + 2] << 8 | d[i + 3];
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) return (d[i + 7] << 8 | d[i + 8], d[i + 5] << 8 | d[i + 6]);
                i += 2 + length;
            }
        }
        return (0, 0);
    }

    private static int Be32(byte[] d, int o) => d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3];
}
