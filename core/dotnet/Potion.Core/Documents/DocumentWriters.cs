using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace Potion.Core.Documents;

/// <summary>Plain text: list markers as "•" or "1.", table cells separated by tabs (same as macOS).</summary>
public static class PlainTextWriter
{
    public static string Write(Document document)
    {
        var lines = new List<string>();
        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case ListItem item:
                    lines.Add(new string(' ', item.Depth * 4) + (item.Ordered ? $"{item.Number}." : "•") + " " + item.PlainText);
                    break;
                case Table table:
                    lines.AddRange(table.Rows.Select(r => string.Join("\t", r.Select(c => c.PlainText.Replace('\n', ' ')))));
                    break;
                case HorizontalRule or PageBreak:
                    lines.Add("");
                    break;
                default:
                    lines.Add(block.PlainText);
                    break;
            }
        }
        return string.Join("\n", lines) + "\n";
    }
}

/// <summary>Markdown: headings, emphasis, links, lists, tables, code and quotes (images and fonts are dropped).</summary>
public static class MarkdownWriter
{
    public static string Write(Document document)
    {
        var output = new StringBuilder();
        var previousWasList = false;
        foreach (var block in document.Blocks)
        {
            string? chunk = block switch
            {
                Paragraph { HeadingLevel: > 0 } p => new string('#', p.HeadingLevel) + " " + Inline(p.Inlines, boldImplied: true),
                Paragraph p when Inline(p.Inlines, false, atLineStart: true) is { Length: > 0 } text => p.Quote ? "> " + text : text,
                ListItem item => new string(' ', item.Depth * 4) + (item.Ordered ? $"{item.Number}." : "-") + " " + Inline(item.Inlines, false),
                Table { Rows.Count: > 0 } table => Table(table),
                CodeBlock code => "```\n" + code.Code + "\n```",
                HorizontalRule or PageBreak => "---",
                _ => null,
            };
            if (chunk is null) continue;
            var isList = block is ListItem;
            if (output.Length > 0) output.Append(isList && previousWasList ? "\n" : "\n\n");
            output.Append(chunk);
            previousWasList = isList;
        }
        return output.Append('\n').ToString();
    }

    private static string Table(Table table)
    {
        var columns = table.ColumnCount;
        string Row(List<TableCell> cells, bool header) => "| " + string.Join(" | ", Enumerable.Range(0, columns).Select(i =>
            i < cells.Count ? string.Join("<br>", cells[i].Blocks.Select(b => b is Paragraph p ? Inline(p.Inlines, header) : b.PlainText))
                .Replace("|", "\\|").Replace("\n", " ") : "")) + " |";
        var lines = new List<string> { Row(table.Rows[0], true), "|" + string.Concat(Enumerable.Repeat(" --- |", columns)) };
        lines.AddRange(table.Rows.Skip(1).Select(r => Row(r, false)));
        return string.Join("\n", lines);
    }

    /// <param name="boldImplied">Headings and table header cells are bold in Markdown already.</param>
    public static string Inline(List<Inline> inlines, bool boldImplied, bool atLineStart = false)
    {
        var output = new StringBuilder();
        foreach (var inline in inlines)
        {
            if (inline is LineBreak) { output.Append("  \n"); continue; }
            if (inline is not TextRun run) continue;
            var core = run.Text.Trim(' ', '\t');
            if (core.Length == 0) { output.Append(run.Text); continue; }
            var lead = run.Text[..run.Text.IndexOf(core, StringComparison.Ordinal)];
            var trail = run.Text[(lead.Length + core.Length)..];
            string md;
            if (run.Style.Code)
            {
                var fence = core.Contains('`') ? "``" : "`";
                md = fence + core + fence;
            }
            else
            {
                md = Escape(core);
                if (run.Style.Strike) md = $"~~{md}~~";
                if (run.Style.Italic) md = $"*{md}*";
                if (run.Style.Bold && !boldImplied) md = $"**{md}**";
            }
            if (run.Style.Link is { } link) md = $"[{md}]({link.Replace(" ", "%20")})";
            output.Append(lead).Append(md).Append(trail);
        }
        var text = output.ToString();
        if (atLineStart && System.Text.RegularExpressions.Regex.IsMatch(text, @"^(?:#|>|[-+=]\s|\d+\.\s)")) text = "\\" + text;
        return text;
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if ("\\`*_[]<".Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// HTML (also well-formed XHTML, which EPUB needs): semantic elements, inline styles for alignment, colors, fonts
/// and sizes, images embedded as data: URIs (or referenced by file name for EPUB).
/// </summary>
public static class HtmlWriter
{
    public static string Write(Document document, string title, Func<ImageRun, string>? imageSource = null, bool xhtml = false)
    {
        imageSource ??= image => $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Data)}";
        var body = new StringBuilder();
        var openLists = new Stack<(bool Ordered, int Id, int Depth)>();
        void CloseLists(int toDepth)
        {
            while (openLists.Count > 0 && openLists.Peek().Depth >= toDepth) body.Append(openLists.Pop().Ordered ? "</ol>\n" : "</ul>\n");
        }

        foreach (var block in document.Blocks)
        {
            if (block is ListItem item)
            {
                CloseLists(item.Depth + 1);
                if (openLists.Count > 0 && openLists.Peek().Depth == item.Depth && (openLists.Peek().Id != item.ListId || openLists.Peek().Ordered != item.Ordered))
                    CloseLists(item.Depth);
                if (openLists.Count == 0 || openLists.Peek().Depth < item.Depth)
                {
                    body.Append(item.Ordered ? (item.Number != 1 ? $"<ol start=\"{item.Number}\">\n" : "<ol>\n") : "<ul>\n");
                    openLists.Push((item.Ordered, item.ListId, item.Depth));
                }
                body.Append("<li>").Append(Inlines(item.Inlines, imageSource, xhtml)).Append("</li>\n");
                continue;
            }
            CloseLists(0);
            body.Append(BlockHtml(block, imageSource, xhtml));
        }
        CloseLists(0);

        var t = Escape(title);
        if (xhtml)
            return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><meta charset=\"utf-8\"/>"
                   + $"<title>{t}</title><link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\"/></head><body>\n{body}</body></html>\n";
        return $"<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>{t}</title>\n<style>\n{Stylesheet}</style>\n</head>\n<body>\n{body}</body>\n</html>\n";
    }

    public const string Stylesheet = """
        body { font-family: "Segoe UI", "Helvetica Neue", Arial, sans-serif; font-size: 12pt; line-height: 1.4; }
        pre, code { font-family: Consolas, Menlo, monospace; font-size: 10.5pt; }
        table { border-collapse: collapse; }
        th, td { border: 1px solid #999999; padding: 4px 8px; vertical-align: top; }
        blockquote { margin-left: 1.5em; color: #555555; }
        img { max-width: 100%; height: auto; }

        """;

    private static string BlockHtml(Block block, Func<ImageRun, string> imageSource, bool xhtml) => block switch
    {
        Paragraph { HeadingLevel: > 0 } p => $"<h{p.HeadingLevel}{Align(p.Alignment)}>{Inlines(p.Inlines, imageSource, xhtml)}</h{p.HeadingLevel}>\n",
        Paragraph { Quote: true } p => $"<blockquote><p>{Inlines(p.Inlines, imageSource, xhtml)}</p></blockquote>\n",
        Paragraph p when p.Inlines.Count == 0 => xhtml ? "<p>&#160;</p>\n" : "<p>&nbsp;</p>\n",
        Paragraph p => $"<p{Align(p.Alignment)}>{Inlines(p.Inlines, imageSource, xhtml)}</p>\n",
        CodeBlock c => $"<pre><code>{Escape(c.Code)}</code></pre>\n",
        HorizontalRule => xhtml ? "<hr/>\n" : "<hr>\n",
        PageBreak => "<div style=\"page-break-after: always\"></div>\n",
        Table t => Table(t, imageSource, xhtml),
        _ => "",
    };

    private static string Table(Table table, Func<ImageRun, string> imageSource, bool xhtml)
    {
        var sb = new StringBuilder("<table>\n");
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var tag = r == 0 && table.HasHeader ? "th" : "td";
            sb.Append("<tr>");
            foreach (var cell in table.Rows[r])
            {
                var content = string.Join(xhtml ? "<br/>" : "<br>", cell.Blocks.Select(b => b is Paragraph p ? Inlines(p.Inlines, imageSource, xhtml) : Escape(b.PlainText)));
                sb.Append($"<{tag}>{content}</{tag}>");
            }
            sb.Append("</tr>\n");
        }
        return sb.Append("</table>\n").ToString();
    }

    private static string Align(Alignment a) => a switch
    {
        Alignment.Center => " style=\"text-align: center\"", Alignment.End => " style=\"text-align: right\"",
        Alignment.Justify => " style=\"text-align: justify\"", _ => "",
    };

    public static string Inlines(List<Inline> inlines, Func<ImageRun, string> imageSource, bool xhtml)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LineBreak: sb.Append(xhtml ? "<br/>" : "<br>"); break;
                case ImageRun image:
                    var size = $" width=\"{(int)Math.Round(image.Width / 0.75)}\" height=\"{(int)Math.Round(image.Height / 0.75)}\"";
                    sb.Append($"<img src=\"{Escape(imageSource(image))}\" alt=\"{Escape(image.Alt ?? "")}\"{size}{(xhtml ? "/" : "")}>");
                    break;
                case TextRun run:
                    var html = Escape(run.Text);
                    var s = run.Style;
                    var css = new List<string>();
                    if (s.Color is { } color) css.Add($"color: #{color}");
                    if (s.FontFamily is { } font) css.Add($"font-family: '{font.Replace("'", "")}'");
                    if (s.FontSize is { } pt) css.Add($"font-size: {pt.ToString("0.#", CultureInfo.InvariantCulture)}pt");
                    if (css.Count > 0) html = $"<span style=\"{Escape(string.Join("; ", css))}\">{html}</span>";
                    if (s.Code) html = $"<code>{html}</code>";
                    if (s.Superscript) html = $"<sup>{html}</sup>";
                    if (s.Subscript) html = $"<sub>{html}</sub>";
                    if (s.Strike) html = $"<del>{html}</del>";
                    if (s.Underline && s.Link is null) html = $"<u>{html}</u>";
                    if (s.Italic) html = $"<em>{html}</em>";
                    if (s.Bold) html = $"<strong>{html}</strong>";
                    if (s.Link is { } link) html = $"<a href=\"{Escape(link)}\">{html}</a>";
                    sb.Append(html);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Escapes for HTML/XML and drops control characters XML doesn't allow.</summary>
    public static string Escape(string text)
    {
        var clean = new string(text.Where(c => c >= 0x20 || c is '\t' or '\n' or '\r').ToArray());
        return WebUtility.HtmlEncode(clean).Replace("&#39;", "'");
    }
}

/// <summary>EPUB 3: one XHTML chapter, a navigation document built from the headings, images as files.</summary>
public static class EpubWriter
{
    public static byte[] Write(Document document, string title, string language)
    {
        var images = new List<(string Name, ImageRun Image)>();
        string Source(ImageRun image)
        {
            var existing = images.FindIndex(i => ReferenceEquals(i.Image, image));
            if (existing >= 0) return "images/" + images[existing].Name;
            var name = $"image{images.Count + 1}.{image.Extension}";
            images.Add((name, image));
            return "images/" + name;
        }

        // Give the h1/h2 headings ids for the navigation document.
        var nav = new List<(string Id, string Text)>();
        var withIds = new Document { Title = document.Title, Page = document.Page };
        foreach (var block in document.Blocks)
        {
            if (block is Paragraph { HeadingLevel: 1 or 2 } heading && heading.PlainText.Trim().Length > 0)
                nav.Add(($"section{nav.Count + 1}", heading.PlainText.Trim()));
            withIds.Blocks.Add(block);
        }
        var content = HtmlWriter.Write(withIds, title, Source, xhtml: true);
        var index = 0;
        content = System.Text.RegularExpressions.Regex.Replace(content, "<h([12])([ >])", m =>
            index < nav.Count ? $"<h{m.Groups[1].Value} id=\"{nav[index++].Id}\"{m.Groups[2].Value}" : m.Value);
        if (nav.Count == 0) nav.Add(("", title));

        var t = HtmlWriter.Escape(title);
        var lang = HtmlWriter.Escape(language);
        content = content.Replace("<html xmlns=\"http://www.w3.org/1999/xhtml\">", $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xml:lang=\"{lang}\" lang=\"{lang}\">");
        var navItems = string.Concat(nav.Select(n => $"<li><a href=\"content.xhtml{(n.Id.Length > 0 ? "#" + n.Id : "")}\">{HtmlWriter.Escape(n.Text)}</a></li>"));
        var navXhtml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"{lang}\" lang=\"{lang}\">"
                       + $"<head><meta charset=\"utf-8\"/><title>{t}</title></head><body><nav epub:type=\"toc\" id=\"toc\"><h1>{t}</h1><ol>{navItems}</ol></nav></body></html>\n";
        var modified = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var imageItems = string.Concat(images.Select((img, i) => $"<item id=\"img{i + 1}\" href=\"images/{img.Name}\" media-type=\"{img.Image.MimeType}\"/>"));
        var opf = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"uid\" xml:lang=\"{lang}\">"
                  + $"<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"uid\">urn:uuid:{Guid.NewGuid()}</dc:identifier>"
                  + $"<dc:title>{t}</dc:title><dc:language>{lang}</dc:language><meta property=\"dcterms:modified\">{modified}</meta></metadata>"
                  + "<manifest><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>"
                  + "<item id=\"content\" href=\"content.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>"
                  + imageItems + "</manifest><spine><itemref idref=\"content\"/></spine></package>\n";
        const string container = """<?xml version="1.0" encoding="UTF-8"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""";

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] data, CompressionLevel level = CompressionLevel.Optimal)
            {
                using var s = zip.CreateEntry(name, level).Open();
                s.Write(data);
            }
            Add("mimetype", Encoding.ASCII.GetBytes("application/epub+zip"), CompressionLevel.NoCompression);   // first, stored
            Add("META-INF/container.xml", Encoding.UTF8.GetBytes(container));
            Add("OEBPS/content.opf", Encoding.UTF8.GetBytes(opf));
            Add("OEBPS/nav.xhtml", Encoding.UTF8.GetBytes(navXhtml));
            Add("OEBPS/content.xhtml", Encoding.UTF8.GetBytes(content));
            Add("OEBPS/style.css", Encoding.UTF8.GetBytes(HtmlWriter.Stylesheet));
            foreach (var (name, image) in images) Add("OEBPS/images/" + name, image.Data, CompressionLevel.NoCompression);
        }
        return buffer.ToArray();
    }
}
