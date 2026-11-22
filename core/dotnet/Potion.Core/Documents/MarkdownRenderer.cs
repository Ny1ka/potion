using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Potion.Core.Documents;

/// <summary>
/// Renders Markdown to HTML (CommonMark essentials plus GitHub tables and strikethrough): ATX and setext headings,
/// paragraphs, emphasis, code spans, fenced and indented code, nested lists, block quotes, links (inline and
/// reference), images, autolinks, hard line breaks, rules, tables and raw HTML. Written for Potion instead of a
/// third-party library so the Windows app ships no unwanted extensions (see docs/PROGRESS.md).
/// </summary>
public static partial class MarkdownRenderer
{
    public static string ToHtml(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(ExpandTabs).ToList();
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        lines = lines.Where(line =>
        {
            var m = ReferenceDefinition().Match(line);
            if (!m.Success) return true;
            references.TryAdd(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim('<', '>'));
            return false;
        }).ToList();
        var html = new StringBuilder();
        new BlockParser(references).Blocks(lines, html);
        return html.ToString();
    }

    private static string ExpandTabs(string line)
    {
        if (!line.Contains('\t')) return line;
        var sb = new StringBuilder();
        foreach (var c in line)
        {
            if (c == '\t') sb.Append(' ', 4 - sb.Length % 4); else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text).Replace("&#39;", "'");
    private static string Attr(string text) => WebUtility.HtmlEncode(text);

    private sealed class BlockParser(Dictionary<string, string> references)
    {
        public void Blocks(List<string> lines, StringBuilder html)
        {
            var i = 0;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (line.Trim().Length == 0) { i++; continue; }

                if (Fence().Match(line) is { Success: true } fence)
                {
                    var marker = fence.Groups[1].Value;
                    var info = fence.Groups[2].Value.Trim().Split(' ')[0];
                    var code = new List<string>();
                    i++;
                    while (i < lines.Count && !(lines[i].TrimStart().StartsWith(marker[..3]) && lines[i].Trim().All(c => c == marker[0]) && lines[i].Trim().Length >= marker.Length))
                        code.Add(lines[i++]);
                    i++;
                    var cls = info.Length > 0 ? $" class=\"language-{Attr(info)}\"" : "";
                    html.Append($"<pre><code{cls}>{Escape(string.Join("\n", code))}</code></pre>\n");
                    continue;
                }
                if (Heading().Match(line) is { Success: true } heading)
                {
                    var level = heading.Groups[1].Value.Length;
                    html.Append($"<h{level}>{Inline(heading.Groups[2].Value.Trim())}</h{level}>\n");
                    i++;
                    continue;
                }
                if (Rule().IsMatch(line)) { html.Append("<hr>\n"); i++; continue; }
                if (QuoteMarker().IsMatch(line))
                {
                    var quoted = new List<string>();
                    while (i < lines.Count && lines[i].Trim().Length > 0)
                    {
                        var m = QuoteMarker().Match(lines[i]);
                        quoted.Add(m.Success ? lines[i][m.Length..] : lines[i]);   // lazy continuation
                        i++;
                    }
                    html.Append("<blockquote>\n");
                    Blocks(quoted, html);
                    html.Append("</blockquote>\n");
                    continue;
                }
                if (i + 1 < lines.Count && line.Contains('|') && TableDelimiter().IsMatch(lines[i + 1]))
                {
                    i = Table(lines, i, html);
                    continue;
                }
                if (ListMarker().Match(line) is { Success: true })
                {
                    i = List(lines, i, html);
                    continue;
                }
                if (line.StartsWith("    "))
                {
                    var code = new List<string>();
                    while (i < lines.Count && (lines[i].StartsWith("    ") || lines[i].Trim().Length == 0))
                    {
                        code.Add(lines[i].Length >= 4 ? lines[i][4..] : "");
                        i++;
                    }
                    while (code.Count > 0 && code[^1].Length == 0) code.RemoveAt(code.Count - 1);
                    html.Append($"<pre><code>{Escape(string.Join("\n", code))}</code></pre>\n");
                    continue;
                }
                if (HtmlBlock().IsMatch(line))
                {
                    while (i < lines.Count && lines[i].Trim().Length > 0) html.Append(lines[i++]).Append('\n');
                    continue;
                }

                // Paragraph (possibly a setext heading).
                var paragraph = new List<string>();
                while (i < lines.Count && lines[i].Trim().Length > 0)
                {
                    if (paragraph.Count > 0)
                    {
                        if (SetextUnderline().Match(lines[i]) is { Success: true } underline)
                        {
                            var level = underline.Groups[1].Value[0] == '=' ? 1 : 2;
                            html.Append($"<h{level}>{Inline(string.Join("\n", paragraph).Trim())}</h{level}>\n");
                            paragraph.Clear();
                            i++;
                            break;
                        }
                        if (Interrupts(lines[i])) break;
                    }
                    paragraph.Add(lines[i++]);
                }
                if (paragraph.Count > 0) html.Append($"<p>{Inline(string.Join("\n", paragraph).Trim())}</p>\n");
            }
        }

        private static bool Interrupts(string line) =>
            Fence().IsMatch(line) || Heading().IsMatch(line) || Rule().IsMatch(line) || QuoteMarker().IsMatch(line)
            || ListMarker().Match(line) is { Success: true } m && (m.Groups[2].Value is "-" or "*" or "+" or "1." or "1)") && line[m.Length..].Trim().Length > 0;

        private int List(List<string> lines, int i, StringBuilder html)
        {
            var first = ListMarker().Match(lines[i]);
            var ordered = char.IsDigit(first.Groups[2].Value[0]);
            var delimiter = first.Groups[2].Value[^1];
            var start = ordered ? int.Parse(first.Groups[2].Value[..^1]) : 1;
            var items = new List<(List<string> Lines, bool BlankAfter)>();
            var tight = true;

            while (i < lines.Count)
            {
                var m = ListMarker().Match(lines[i]);
                if (!m.Success || char.IsDigit(m.Groups[2].Value[0]) != ordered || m.Groups[2].Value[^1] != delimiter) break;
                var contentIndent = m.Length;
                var item = new List<string> { lines[i][m.Length..] };
                i++;
                var blankSeen = false;
                while (i < lines.Count)
                {
                    var line = lines[i];
                    if (line.Trim().Length == 0)
                    {
                        // A blank line stays in the item only if indented content follows.
                        var next = lines.Skip(i + 1).FirstOrDefault(l => l.Trim().Length > 0);
                        if (next is null || Indent(next) < contentIndent) { blankSeen = true; i++; break; }
                        item.Add("");
                        blankSeen = true;
                        i++;
                        continue;
                    }
                    if (Indent(line) >= contentIndent) { item.Add(line[contentIndent..]); i++; continue; }
                    if (!blankSeen && !ListMarker().IsMatch(line) && !Interrupts(line)) { item.Add(line.TrimStart()); i++; continue; }   // lazy continuation
                    break;
                }
                if (blankSeen && i < lines.Count && ListMarker().IsMatch(lines[i])) tight = false;
                if (item.Skip(1).Any(l => l.Length == 0)) tight = false;
                items.Add((item, blankSeen));
            }

            var tag = ordered ? "ol" : "ul";
            html.Append(ordered && start != 1 ? $"<ol start=\"{start}\">\n" : $"<{tag}>\n");
            foreach (var (itemLines, _) in items)
            {
                var inner = new StringBuilder();
                Blocks(itemLines, inner);
                var content = inner.ToString();
                if (tight) content = TightParagraph().Replace(content, "$1\n");   // tight lists drop the <p> wrappers
                html.Append("<li>").Append(content.TrimEnd('\n')).Append("</li>\n");
            }
            html.Append($"</{tag}>\n");
            return i;
        }

        private int Table(List<string> lines, int i, StringBuilder html)
        {
            static List<string> Cells(string row)
            {
                var trimmed = row.Trim();
                if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
                if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|")) trimmed = trimmed[..^1];
                return PipeSplit().Split(trimmed).Select(c => c.Trim().Replace("\\|", "|")).ToList();
            }
            var header = Cells(lines[i]);
            var aligns = Cells(lines[i + 1]).Select(c => c.StartsWith(':') && c.EndsWith(':') ? "center" : c.EndsWith(':') ? "right" : c.StartsWith(':') ? "left" : null).ToList();
            string Align(int col) => col < aligns.Count && aligns[col] is { } a ? $" style=\"text-align: {a}\"" : "";
            html.Append("<table>\n<tr>");
            for (var c = 0; c < header.Count; c++) html.Append($"<th{Align(c)}>{Inline(header[c])}</th>");
            html.Append("</tr>\n");
            i += 2;
            while (i < lines.Count && lines[i].Trim().Length > 0 && lines[i].Contains('|'))
            {
                var cells = Cells(lines[i++]);
                html.Append("<tr>");
                for (var c = 0; c < header.Count; c++) html.Append($"<td{Align(c)}>{(c < cells.Count ? Inline(cells[c]) : "")}</td>");
                html.Append("</tr>\n");
            }
            html.Append("</table>\n");
            return i;
        }

        private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

        // Inline content

        public string Inline(string text)
        {
            var slots = new List<string>();
            string Keep(string html) { slots.Add(html); return $"\u0001{slots.Count - 1}\u0002"; }

            text = BackslashEscape().Replace(text, m => Keep(Escape(m.Groups[1].Value)));
            text = HardBreak().Replace(text, _ => Keep("<br>\n"));
            text = CodeSpan().Replace(text, m => Keep($"<code>{Escape(m.Groups[2].Value.Trim())}</code>"));
            text = Autolink().Replace(text, m => Keep($"<a href=\"{Attr(m.Groups[1].Value)}\">{Escape(m.Groups[1].Value.Replace("mailto:", ""))}</a>"));
            text = InlineHtml().Replace(text, m => Keep(m.Value));
            text = Image().Replace(text, m => Keep($"<img src=\"{Attr(m.Groups[2].Value)}\" alt=\"{Attr(m.Groups[1].Value)}\">"));
            text = Link().Replace(text, m => Keep($"<a href=\"{Attr(m.Groups[2].Value)}\">") + m.Groups[1].Value + Keep("</a>"));
            text = ReferenceLink().Replace(text, m =>
            {
                var key = m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[1].Value;
                return references.TryGetValue(key, out var href) ? Keep($"<a href=\"{Attr(href)}\">") + m.Groups[1].Value + Keep("</a>") : m.Value;
            });
            text = BareUrl().Replace(text, m => Keep($"<a href=\"{Attr(m.Value)}\">{Escape(m.Value)}</a>"));

            text = Escape(text);
            text = Strong().Replace(text, "<strong>$2</strong>");
            text = Emphasis().Replace(text, "<em>$2</em>");
            text = Strike().Replace(text, "<del>$1</del>");

            // Restore kept pieces (they may nest, e.g. a code span inside a link).
            for (var pass = 0; pass < 4 && text.Contains('\u0001'); pass++)
                text = Slot().Replace(text, m => slots[int.Parse(m.Groups[1].Value)]);
            return text;
        }
    }

    [GeneratedRegex(@"^ {0,3}\[([^\]]+)\]:\s*(\S+)(?:\s+[""'(].*[""')])?\s*$")] private static partial Regex ReferenceDefinition();
    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})(.*)$")] private static partial Regex Fence();
    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$")] private static partial Regex Heading();
    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$")] private static partial Regex Rule();
    [GeneratedRegex(@"^ {0,3}> ?")] private static partial Regex QuoteMarker();
    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")] private static partial Regex TableDelimiter();
    [GeneratedRegex(@"^( {0,3})([-+*]|\d{1,9}[.)])( +|$)")] private static partial Regex ListMarker();
    [GeneratedRegex(@"^ {0,3}<(/?[A-Za-z][A-Za-z0-9-]*)(\s|/?>|$)")] private static partial Regex HtmlBlock();
    [GeneratedRegex(@"^ {0,3}(=+|-+)\s*$")] private static partial Regex SetextUnderline();
    [GeneratedRegex(@"<p>(.*?)</p>\n", RegexOptions.Singleline)] private static partial Regex TightParagraph();
    [GeneratedRegex(@"(?<!\\)\|")] private static partial Regex PipeSplit();

    [GeneratedRegex(@"\\([!""#$%&'()*+,\-./:;<=>?@\[\\\]^_`{|}~])")] private static partial Regex BackslashEscape();
    [GeneratedRegex(@"(?: {2,}|\\)\n")] private static partial Regex HardBreak();
    [GeneratedRegex(@"(`+)(.+?)(?<!`)\1(?!`)", RegexOptions.Singleline)] private static partial Regex CodeSpan();
    [GeneratedRegex(@"<((?:https?|ftp)://[^\s<>]+|mailto:[^\s<>]+)>")] private static partial Regex Autolink();
    [GeneratedRegex(@"</?[A-Za-z][A-Za-z0-9-]*(?:\s+[A-Za-z_:][\w:.-]*(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s""'=<>`]+))?)*\s*/?>")] private static partial Regex InlineHtml();
    [GeneratedRegex(@"!\[([^\]]*)\]\(\s*<?([^\s)>]+)>?(?:\s+[""'][^""']*[""'])?\s*\)")] private static partial Regex Image();
    [GeneratedRegex(@"\[([^\]]+)\]\(\s*<?([^\s)>]*)>?(?:\s+[""'][^""']*[""'])?\s*\)")] private static partial Regex Link();
    [GeneratedRegex(@"\[([^\]]+)\]\[([^\]]*)\]")] private static partial Regex ReferenceLink();
    [GeneratedRegex(@"(?<![""'=\w/])https?://[^\s<\u0001]+[^\s<\u0001.,:;""')\]]")] private static partial Regex BareUrl();
    [GeneratedRegex(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", RegexOptions.Singleline)] private static partial Regex Strong();
    [GeneratedRegex(@"(?<![\w*])(\*|_)(?=\S)(.+?)(?<=\S)\1(?![\w*])", RegexOptions.Singleline)] private static partial Regex Emphasis();
    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Singleline)] private static partial Regex Strike();
    [GeneratedRegex("\u0001(\\d+)\u0002")] private static partial Regex Slot();
}
