using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Potion.Core.Documents;

/// <summary>
/// Reads Word documents (DOCX) with the Open XML SDK: heading styles (or outline levels), paragraphs with alignment,
/// numbered and bulleted lists, tables, hyperlinks, images, run formatting and the page setup.
/// </summary>
public static class DocxReader
{
    public static Document Read(string path)
    {
        using var word = WordprocessingDocument.Open(path, false);
        var main = word.MainDocumentPart ?? throw new InvalidDataException("no document part");
        var body = main.Document?.Body ?? throw new InvalidDataException("no body");
        var context = new Context(main);
        var document = new Document
        {
            Title = word.PackageProperties.Title is { Length: > 0 } title ? title : null,
            Page = PageSetupOf(body.Elements<W.SectionProperties>().LastOrDefault()),
        };
        document.Blocks.AddRange(context.Blocks(body.ChildElements));
        return document;
    }

    private static PageSetup? PageSetupOf(W.SectionProperties? section)
    {
        var size = section?.GetFirstChild<W.PageSize>();
        var margin = section?.GetFirstChild<W.PageMargin>();
        if (size?.Width?.Value is not { } w || size.Height?.Value is not { } h) return null;
        static double Twips(double? v, double fallback) => v is { } t ? t / 20 : fallback;
        return new PageSetup(w / 20.0, h / 20.0, Twips(margin?.Top?.Value, 72), Twips(margin?.Right?.Value, 72),
                             Twips(margin?.Bottom?.Value, 72), Twips(margin?.Left?.Value, 72));
    }

    private sealed class Context(MainDocumentPart main)
    {
        private readonly Dictionary<(int NumId, int Level), int> _counters = [];
        private readonly Dictionary<string, int> _headingStyles = HeadingStyles(main);

        public IEnumerable<Block> Blocks(IEnumerable<OpenXmlElement> elements)
        {
            foreach (var element in elements)
            {
                switch (element)
                {
                    case W.Paragraph p:
                        foreach (var block in Paragraph(p)) yield return block;
                        break;
                    case W.Table t:
                        yield return Table(t);
                        break;
                    case W.SdtBlock sdt when sdt.SdtContentBlock is { } content:
                        foreach (var block in Blocks(content.ChildElements)) yield return block;
                        break;
                }
            }
        }

        private IEnumerable<Block> Paragraph(W.Paragraph p)
        {
            var props = p.ParagraphProperties;
            var inlines = new List<Inline>();
            var pageBreakAfter = false;
            foreach (var child in p.ChildElements) Inlines(child, null, inlines, ref pageBreakAfter);
            if (props?.PageBreakBefore is not null) yield return new PageBreak();

            var heading = 0;
            if (props?.ParagraphStyleId?.Val?.Value is { } styleId && _headingStyles.TryGetValue(styleId, out var level)) heading = level;
            if (props?.OutlineLevel?.Val?.Value is { } outline && outline < 6) heading = outline + 1;

            if (props?.NumberingProperties is { NumberingId.Val.Value: var numId and > 0 } numbering)
            {
                var ilvl = numbering.NumberingLevelReference?.Val?.Value ?? 0;
                var (ordered, start) = ListLevel(numId, ilvl);
                // A higher level resets the deeper ones.
                foreach (var key in _counters.Keys.Where(k => k.NumId == numId && k.Level > ilvl).ToList()) _counters.Remove(key);
                var number = _counters.TryGetValue((numId, ilvl), out var n) ? n + 1 : start;
                _counters[(numId, ilvl)] = number;
                if (inlines.Count > 0) yield return new ListItem(inlines, ilvl, ordered, number, numId);
            }
            else
            {
                yield return new Paragraph(inlines, heading, AlignmentOf(props?.Justification?.Val?.Value));
            }
            if (pageBreakAfter) yield return new PageBreak();
        }

        private void Inlines(OpenXmlElement element, string? link, List<Inline> inlines, ref bool pageBreak)
        {
            switch (element)
            {
                case W.Run run:
                    var style = RunStyle(run.RunProperties) with { Link = link };
                    foreach (var piece in run.ChildElements)
                    {
                        switch (piece)
                        {
                            case W.Text t: Append(inlines, t.Text, style); break;
                            case W.TabChar: Append(inlines, "\t", style); break;
                            case W.Break br when br.Type?.Value == W.BreakValues.Page: pageBreak = true; break;
                            case W.Break or W.CarriageReturn: inlines.Add(new LineBreak()); break;
                            case W.Drawing drawing when Image(drawing) is { } image: inlines.Add(image); break;
                        }
                    }
                    break;
                case W.Hyperlink hyperlink:
                    var target = hyperlink.Id?.Value is { } id
                        ? main.HyperlinkRelationships.FirstOrDefault(r => r.Id == id)?.Uri.ToString() : null;
                    foreach (var child in hyperlink.ChildElements)
                        Inlines(child, target is not null && HtmlReader.IsLink(target) ? target : link, inlines, ref pageBreak);
                    break;
                case W.SimpleField field:
                    var url = HyperlinkFromField(field.Instruction?.Value);
                    foreach (var child in field.ChildElements) Inlines(child, url ?? link, inlines, ref pageBreak);
                    break;
                case W.SdtRun sdt when sdt.SdtContentRun is { } content:
                    foreach (var child in content.ChildElements) Inlines(child, link, inlines, ref pageBreak);
                    break;
                case W.InsertedRun or W.CustomXmlRun:
                    foreach (var child in element.ChildElements) Inlines(child, link, inlines, ref pageBreak);
                    break;
            }
        }

        private static string? HyperlinkFromField(string? instruction)
        {
            if (instruction is null) return null;
            var m = System.Text.RegularExpressions.Regex.Match(instruction, @"HYPERLINK\s+""([^""]+)""");
            return m.Success && HtmlReader.IsLink(m.Groups[1].Value) ? m.Groups[1].Value : null;
        }

        private static void Append(List<Inline> inlines, string text, TextStyle style)
        {
            if (text.Length == 0) return;
            if (inlines.Count > 0 && inlines[^1] is TextRun last && last.Style == style) inlines[^1] = last with { Text = last.Text + text };
            else inlines.Add(new TextRun(text, style));
        }

        private static TextStyle RunStyle(W.RunProperties? rp)
        {
            if (rp is null) return TextStyle.Plain;
            static bool On(W.OnOffType? flag) => flag is not null && (flag.Val is null || flag.Val.Value);
            var vertical = rp.VerticalTextAlignment?.Val?.Value;
            return new TextStyle
            {
                Bold = On(rp.Bold),
                Italic = On(rp.Italic),
                Underline = rp.Underline?.Val is { } u && u.Value != W.UnderlineValues.None,
                Strike = On(rp.Strike) || On(rp.DoubleStrike),
                Superscript = vertical == W.VerticalPositionValues.Superscript,
                Subscript = vertical == W.VerticalPositionValues.Subscript,
                FontFamily = rp.RunFonts?.Ascii?.Value,
                FontSize = double.TryParse(rp.FontSize?.Val?.Value, out var half) ? half / 2 : null,
                Color = rp.Color?.Val?.Value is { Length: 6 } c && c != "auto" ? c.ToUpperInvariant() : null,
            };
        }

        private ImageRun? Image(W.Drawing drawing)
        {
            var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
            if (blip?.Embed?.Value is not { } id || main.GetPartById(id) is not ImagePart part) return null;
            using var stream = part.GetStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var extent = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().FirstOrDefault();
            double width = (extent?.Cx?.Value ?? 0) / 12700.0, height = (extent?.Cy?.Value ?? 0) / 12700.0;
            var bytes = buffer.ToArray();
            if (width <= 0 || height <= 0) { var (w, h) = ImageSize.Of(bytes); (width, height) = (w * 0.75, h * 0.75); }
            return new ImageRun(bytes, part.ContentType, width, height);
        }

        private (bool Ordered, int Start) ListLevel(int numId, int ilvl)
        {
            var numbering = main.NumberingDefinitionsPart?.Numbering;
            var num = numbering?.Elements<W.NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
            var abstractId = num?.AbstractNumId?.Val?.Value;
            var abstractNum = numbering?.Elements<W.AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId);
            var level = abstractNum?.Elements<W.Level>().FirstOrDefault(l => l.LevelIndex?.Value == ilvl);
            var format = level?.NumberingFormat?.Val?.Value;
            var start = num?.Elements<W.LevelOverride>().FirstOrDefault(o => o.LevelIndex?.Value == ilvl)?.StartOverrideNumberingValue?.Val?.Value
                        ?? level?.StartNumberingValue?.Val?.Value ?? 1;
            return (format is not null && format != W.NumberFormatValues.Bullet && format != W.NumberFormatValues.None, start);
        }

        private Table Table(W.Table table)
        {
            var rows = new List<List<TableCell>>();
            var hasHeader = false;
            foreach (var row in table.Elements<W.TableRow>())
            {
                if (rows.Count == 0 && row.TableRowProperties?.GetFirstChild<W.TableHeader>() is not null) hasHeader = true;
                rows.Add(row.Elements<W.TableCell>().Select(cell => new TableCell(Blocks(cell.ChildElements).ToList())).ToList());
            }
            return new Table(rows, hasHeader);
        }

        /// <summary>Style id → heading level, from built-in names ("heading 1") or the style's outline level.</summary>
        private static Dictionary<string, int> HeadingStyles(MainDocumentPart main)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var style in main.StyleDefinitionsPart?.Styles?.Elements<W.Style>() ?? [])
            {
                if (style.StyleId?.Value is not { } id) continue;
                var name = style.StyleName?.Val?.Value?.ToLowerInvariant() ?? "";
                if (name == "title") map[id] = 1;
                else if (name.StartsWith("heading ") && int.TryParse(name[8..], out var n) && n is >= 1 and <= 6) map[id] = n;
                else if (style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is { } outline && outline < 6) map[id] = outline + 1;
            }
            return map;
        }

        private static Alignment AlignmentOf(W.JustificationValues? value) =>
            value == W.JustificationValues.Center ? Alignment.Center
            : value == W.JustificationValues.Right || value == W.JustificationValues.End ? Alignment.End
            : value == W.JustificationValues.Both || value == W.JustificationValues.Distribute ? Alignment.Justify
            : Alignment.Start;
    }
}
