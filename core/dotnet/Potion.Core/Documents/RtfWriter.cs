using System.Text;

namespace Potion.Core.Documents;

/// <summary>
/// Writes RTF from a <see cref="Document"/>: fonts and colors, bold/italic/underline/strike/super/subscript,
/// headings, alignment, lists (bullet or number with a hanging indent, readable by any RTF reader), tables,
/// hyperlink fields, PNG/JPEG pictures, page breaks and the page setup. Non-ASCII text uses \uN escapes.
/// </summary>
public sealed class RtfWriter
{
    private readonly List<string> _fonts = ["Calibri", "Consolas"];
    private readonly List<string> _colors = ["0563C1"];   // \cf1 is the link color
    private readonly Document _document;
    /// <summary>List id → (\ls index, ordered, start).</summary>
    private readonly Dictionary<int, (int Ls, bool Ordered, int Start)> _lists = [];

    private RtfWriter(Document document) => _document = document;

    public static string Write(Document document) => new RtfWriter(document).Build();

    private string Build()
    {
        var body = new StringBuilder();
        foreach (var block in _document.Blocks) Block(block, body, inTable: false);
        var page = _document.Page ?? PageSetup.Letter;
        static int Tw(double pt) => (int)Math.Round(pt * 20);
        var header = new StringBuilder(@"{\rtf1\ansi\ansicpg1252\deff0\uc1");
        header.Append(@"{\fonttbl");
        for (var i = 0; i < _fonts.Count; i++) header.Append($@"{{\f{i}\fnil {Text(_fonts[i])};}}");
        header.Append(@"}{\colortbl;");
        foreach (var c in _colors) header.Append($@"\red{Convert.ToInt32(c[..2], 16)}\green{Convert.ToInt32(c[2..4], 16)}\blue{Convert.ToInt32(c[4..], 16)};");
        header.Append('}');
        header.Append(ListTables());
        header.Append($@"\paperw{Tw(page.Width)}\paperh{Tw(page.Height)}\margl{Tw(page.Left)}\margr{Tw(page.Right)}\margt{Tw(page.Top)}\margb{Tw(page.Bottom)}");
        if (page.Width > page.Height) header.Append(@"\landscape");
        header.Append('\n');
        return header.Append(body).Append('}').ToString();
    }

    private void Block(Block block, StringBuilder rtf, bool inTable)
    {
        var intbl = inTable ? @"\intbl" : "";
        switch (block)
        {
            case Paragraph p:
                rtf.Append($@"\pard\plain{intbl}{Align(p.Alignment)}{(p.Quote ? @"\li720" : "")}\sa120 ");
                if (p.HeadingLevel > 0)
                {
                    var size = (int)(24 * new[] { 2.0, 1.5, 1.25, 1.1, 1.0, 1.0 }[p.HeadingLevel - 1]);
                    rtf.Append($@"\outlinelevel{p.HeadingLevel - 1}{{\b\fs{size} {Inlines(p.Inlines)}}}");
                }
                else rtf.Append(Inlines(p.Inlines));
                if (!inTable) rtf.Append("\\par\n");
                break;
            case ListItem item:
                if (!_lists.TryGetValue(item.ListId, out var list)) _lists[item.ListId] = list = (_lists.Count + 1, item.Ordered, item.Number);
                var marker = item.Ordered ? $"{item.Number}." : @"\u8226 ?";
                var left = 720 * (item.Depth + 1);
                rtf.Append($@"\pard\plain{intbl}\ls{list.Ls}\ilvl{Math.Min(item.Depth, 8)}\li{left}\fi-360\sa60 {{\listtext {marker}\tab}}{Inlines(item.Inlines)}");
                if (!inTable) rtf.Append("\\par\n");
                break;
            case CodeBlock code:
                foreach (var line in code.Code.Split('\n')) rtf.Append($@"\pard\plain{intbl}\f1\fs20 {Text(line)}\par" + "\n");
                break;
            case HorizontalRule:
                rtf.Append(@"\pard\plain\brdrb\brdrs\brdrw10\brsp20 \par" + "\n");
                break;
            case PageBreak:
                rtf.Append(@"\page" + "\n");
                break;
            case Table table:
                var columns = Math.Max(table.ColumnCount, 1);
                var page = _document.Page ?? PageSetup.Letter;
                var width = (int)Math.Round((page.Width - page.Left - page.Right) * 20 / columns);
                for (var r = 0; r < table.Rows.Count; r++)
                {
                    rtf.Append(@"\trowd\trgaph108");
                    if (r == 0 && table.HasHeader) rtf.Append(@"\trhdr");
                    for (var c = 1; c <= columns; c++)
                        rtf.Append($@"\clbrdrt\brdrs\brdrw10\clbrdrl\brdrs\brdrw10\clbrdrb\brdrs\brdrw10\clbrdrr\brdrs\brdrw10\cellx{width * c}");
                    rtf.Append('\n');
                    for (var c = 0; c < columns; c++)
                    {
                        var cell = c < table.Rows[r].Count ? table.Rows[r][c] : new TableCell([]);
                        if (cell.Blocks.Count == 0) rtf.Append(@"\pard\plain\intbl ");
                        for (var b = 0; b < cell.Blocks.Count; b++)
                        {
                            if (b > 0) rtf.Append(@"\par ");
                            Block(cell.Blocks[b], rtf, inTable: true);
                        }
                        rtf.Append(@"\cell" + "\n");
                    }
                    rtf.Append(@"\row" + "\n");
                }
                rtf.Append(@"\pard" + "\n");
                break;
        }
    }

    /// <summary>The list and override tables Word needs to number \ls paragraphs (one list per document list).</summary>
    private string ListTables()
    {
        if (_lists.Count == 0) return "";
        var table = new StringBuilder(@"{\*\listtable");
        var overrides = new StringBuilder(@"{\*\listoverridetable");
        foreach (var (_, (ls, ordered, start)) in _lists)
        {
            table.Append($@"{{\list\listtemplateid{ls}\listhybrid");
            for (var level = 0; level < 9; level++)
            {
                var text = ordered ? $@"{{\leveltext\'02\'0{level}.;}}{{\levelnumbers\'01;}}" : @"{\leveltext\'01\u8226 ?;}{\levelnumbers;}";
                table.Append($@"{{\listlevel\levelnfc{(ordered ? 0 : 23)}\leveljc0\levelfollow0\levelstartat{(level == 0 ? start : 1)}{text}\fi-360\li{720 * (level + 1)}}}");
            }
            table.Append($@"\listid{ls}}}");
            overrides.Append($@"{{\listoverride\listid{ls}\listoverridecount0\ls{ls}}}");
        }
        return table.Append('}').Append(overrides).Append('}').ToString();
    }

    private static string Align(Alignment a) => a switch { Alignment.Center => @"\qc", Alignment.End => @"\qr", Alignment.Justify => @"\qj", _ => @"\ql" };

    private string Inlines(List<Inline> inlines)
    {
        var rtf = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LineBreak: rtf.Append(@"\line "); break;
                case ImageRun image when image.MimeType is "image/png" or "image/jpeg":
                    rtf.Append($@"{{\pict\{(image.MimeType == "image/png" ? "pngblip" : "jpegblip")}\picwgoal{(int)Math.Round(image.Width * 20)}\pichgoal{(int)Math.Round(image.Height * 20)} ");
                    rtf.Append(Convert.ToHexString(image.Data)).Append('}');
                    break;
                case TextRun run:
                    var s = run.Style;
                    var fmt = new StringBuilder();
                    if (s.Bold) fmt.Append(@"\b");
                    if (s.Italic) fmt.Append(@"\i");
                    if (s.Underline || s.Link is not null) fmt.Append(@"\ul");
                    if (s.Strike) fmt.Append(@"\strike");
                    if (s.Superscript) fmt.Append(@"\super");
                    if (s.Subscript) fmt.Append(@"\sub");
                    if (s.Code) fmt.Append(@"\f1");
                    else if (s.FontFamily is { } font) fmt.Append($@"\f{Index(_fonts, font)}");
                    if (s.FontSize is { } size) fmt.Append($@"\fs{(int)Math.Round(size * 2)}");
                    if (s.Link is not null) fmt.Append(@"\cf1");
                    else if (s.Color is { } color) fmt.Append($@"\cf{Index(_colors, color) + 1}");
                    // The space only delimits a control word; after "{" it would be text.
                    var text = fmt.Length > 0 ? $"{{{fmt} {Text(run.Text)}}}" : $"{{{Text(run.Text)}}}";
                    rtf.Append(s.Link is { } link && HtmlReader.IsLink(link)
                        ? $@"{{\field{{\*\fldinst{{HYPERLINK ""{Text(link)}""}}}}{{\fldrslt{text}}}}}"
                        : text);
                    break;
            }
        }
        return rtf.ToString();
    }

    private static int Index(List<string> list, string value)
    {
        var i = list.IndexOf(value);
        if (i >= 0) return i;
        list.Add(value);
        return list.Count - 1;
    }

    /// <summary>Escapes RTF specials; tabs become \tab; anything outside ASCII becomes \uN with a "?" fallback.</summary>
    private static string Text(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\' or '{' or '}': sb.Append('\\').Append(c); break;
                case '\t': sb.Append(@"\tab "); break;
                case '\n': sb.Append(@"\line "); break;
                case < (char)0x20: break;
                case < (char)0x80: sb.Append(c); break;
                default: sb.Append($@"\u{(short)c}?"); break;
            }
        }
        return sb.ToString();
    }
}
