using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using M = MigraDoc.DocumentObjectModel;

namespace Potion.Core.Documents;

/// <summary>
/// Lays out a <see cref="Document"/> as PDF with MigraDoc (Windows): the document's page size and margins,
/// headings (also as PDF bookmarks), alignment, styled text, clickable links, lists with their own numbers,
/// tables with borders, images scaled to fit, code blocks and page breaks. Text stays real, searchable text.
/// </summary>
public static class PdfDocumentWriter
{
    private static readonly double[] HeadingSizes = [24, 18, 15, 13, 12, 12];

    public static byte[] Write(Document document, string title)
    {
        PdfFonts.Install();
        var page = document.Page ?? PageSetup.Letter;
        var pdf = new M.Document();
        pdf.Info.Title = title;
        var normal = pdf.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 11;
        normal.ParagraphFormat.SpaceAfter = 6;

        var section = pdf.AddSection();
        section.PageSetup.PageWidth = Unit.FromPoint(page.Width);
        section.PageSetup.PageHeight = Unit.FromPoint(page.Height);
        section.PageSetup.TopMargin = Unit.FromPoint(page.Top);
        section.PageSetup.BottomMargin = Unit.FromPoint(page.Bottom);
        section.PageSetup.LeftMargin = Unit.FromPoint(page.Left);
        section.PageSetup.RightMargin = Unit.FromPoint(page.Right);
        var content = (Width: page.Width - page.Left - page.Right, Height: page.Height - page.Top - page.Bottom);

        foreach (var block in document.Blocks) AddBlock(section, block, content);
        if (section.Elements.Count == 0) section.AddParagraph();

        var renderer = new PdfDocumentRenderer { Document = pdf };
        renderer.RenderDocument();
        renderer.PdfDocument.Info.Creator = "Potion";
        using var buffer = new MemoryStream();
        renderer.PdfDocument.Save(buffer, closeStream: false);
        return buffer.ToArray();
    }

    /// <summary>Adds a block to a section or a table cell (both hold paragraphs).</summary>
    private static void AddBlock(DocumentObject container, Block block, (double Width, double Height) content)
    {
        M.Paragraph NewParagraph() => container switch
        {
            Section s => s.AddParagraph(),
            Cell c => c.AddParagraph(),
            _ => throw new InvalidOperationException("unsupported container"),
        };

        switch (block)
        {
            case Paragraph { HeadingLevel: > 0 } h:
            {
                var p = NewParagraph();
                p.Format.Font.Size = HeadingSizes[h.HeadingLevel - 1];
                p.Format.Font.Bold = true;
                p.Format.SpaceBefore = 12;
                p.Format.SpaceAfter = 6;
                p.Format.KeepWithNext = true;
                p.Format.OutlineLevel = (OutlineLevel)h.HeadingLevel;   // 0 is body text, 1 is Level1
                p.Format.Alignment = Align(h.Alignment);
                AddInlines(p, h.Inlines, content);
                break;
            }
            case Paragraph para:
            {
                var p = NewParagraph();
                p.Format.Alignment = Align(para.Alignment);
                if (para.Quote) { p.Format.LeftIndent = Unit.FromPoint(24); p.Format.Font.Color = Colors.DimGray; }
                AddInlines(p, para.Inlines, content);
                break;
            }
            case ListItem item:
            {
                // Markers are written as text so numbering starts where the source does (MigraDoc lists always start at 1).
                var p = NewParagraph();
                var indent = 18 * (item.Depth + 1);
                p.Format.LeftIndent = Unit.FromPoint(indent);
                p.Format.FirstLineIndent = Unit.FromPoint(-14);
                p.Format.SpaceAfter = 2;
                p.Format.TabStops.AddTabStop(Unit.FromPoint(indent));
                p.AddText(item.Ordered ? $"{item.Number}." : "•");
                p.AddTab();
                AddInlines(p, item.Inlines, content);
                break;
            }
            case CodeBlock code:
            {
                var p = NewParagraph();
                p.Format.Font.Name = "Courier New";
                p.Format.Font.Size = 9.5;
                p.Format.Shading.Color = new Color(0xF2, 0xF2, 0xF2);
                var lines = code.Code.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0) p.AddLineBreak();
                    p.AddText(lines[i].Replace("\t", "    "));
                }
                break;
            }
            case HorizontalRule:
            {
                var p = NewParagraph();
                p.Format.Borders.Bottom.Width = 0.75;
                p.Format.Borders.Bottom.Color = Colors.Gray;
                break;
            }
            case PageBreak when container is Section s:
                s.AddPageBreak();
                break;
            case Table table when container is Section s && table.ColumnCount > 0:
                AddTable(s, table, content);
                break;
            case Table table:
                // Nested tables become tab-separated lines inside the cell.
                foreach (var row in table.Rows) NewParagraph().AddText(string.Join("    ", row.Select(c => c.PlainText)));
                break;
        }
    }

    private static void AddTable(Section section, Table table, (double Width, double Height) content)
    {
        var columns = table.ColumnCount;
        var t = section.AddTable();
        t.Borders.Width = 0.5;
        t.Borders.Color = Colors.Gray;
        t.LeftPadding = Unit.FromPoint(4);
        t.RightPadding = Unit.FromPoint(4);
        for (var c = 0; c < columns; c++) t.AddColumn(Unit.FromPoint(content.Width / columns));
        var cellContent = (Width: content.Width / columns - 8, content.Height);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = t.AddRow();
            if (r == 0 && table.HasHeader) row.HeadingFormat = true;
            for (var c = 0; c < columns && c < table.Rows[r].Count; c++)
                foreach (var block in table.Rows[r][c].Blocks) AddBlock(row.Cells[c], block, cellContent);
        }
        section.AddParagraph().Format.SpaceAfter = 4;   // a little room after the table
    }

    private static void AddInlines(M.Paragraph p, List<Inline> inlines, (double Width, double Height) content)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LineBreak:
                    p.AddLineBreak();
                    break;
                case ImageRun image when image.MimeType is "image/png" or "image/jpeg" or "image/gif" or "image/bmp":
                    var picture = p.AddImage("base64:" + Convert.ToBase64String(image.Data));
                    var scale = Math.Min(1, Math.Min(content.Width / image.Width, content.Height / image.Height));
                    picture.Width = Unit.FromPoint(image.Width * scale);
                    picture.Height = Unit.FromPoint(image.Height * scale);
                    picture.LockAspectRatio = true;
                    break;
                case TextRun run:
                    var parts = run.Text.Split('\t');
                    FormattedText Text(M.Paragraph target) => target.AddFormattedText();
                    FormattedText text;
                    if (run.Style.Link is { } link && HtmlReader.IsLink(link))
                    {
                        var hyperlink = p.AddHyperlink(link, HyperlinkType.Web);
                        text = hyperlink.AddFormattedText();
                        text.Color = new Color(0x05, 0x63, 0xC1);
                        text.Underline = Underline.Single;
                    }
                    else text = Text(p);
                    for (var i = 0; i < parts.Length; i++)
                    {
                        if (i > 0) text.AddTab();
                        if (parts[i].Length > 0) text.AddText(parts[i]);
                    }
                    Apply(text, run.Style);
                    break;
            }
        }
    }

    private static void Apply(FormattedText text, TextStyle s)
    {
        if (s.Bold) text.Bold = true;
        if (s.Italic) text.Italic = true;
        if (s.Underline) text.Underline = Underline.Single;
        if (s.Superscript) text.Superscript = true;
        if (s.Subscript) text.Subscript = true;
        if (s.Code) text.Font.Name = "Courier New";
        else if (s.FontFamily is { } family) text.Font.Name = family;
        if (s.FontSize is { } size) text.Size = size;
        if (s.Link is null && s.Color is { Length: 6 } hex)
            text.Color = new Color(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));
    }

    private static ParagraphAlignment Align(Alignment a) => a switch
    {
        Alignment.Center => ParagraphAlignment.Center, Alignment.End => ParagraphAlignment.Right,
        Alignment.Justify => ParagraphAlignment.Justify, _ => ParagraphAlignment.Left,
    };
}
