using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Potion.Core.Documents;
using Potion.Core.Engines;
using Xunit;
using S = Potion.Core.Tests.EngineTestSupport;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Potion.Core.Tests;

/// <summary>DOCX, ODT and RTF readers. Samples are built by the test.</summary>
public sealed class OfficeReaderTests : EngineTestBase
{
    private static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // DOCX

    private string MakeDocx()
    {
        var path = Path.Combine(Dir, "report.docx");
        using var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        main.AddNewPart<StyleDefinitionsPart>().Styles = new W.Styles(
            new W.Style(new W.StyleName { Val = "heading 1" }) { Type = W.StyleValues.Paragraph, StyleId = "Heading1" });
        main.AddNewPart<NumberingDefinitionsPart>().Numbering = new W.Numbering(
            new W.AbstractNum(new W.Level(new W.StartNumberingValue { Val = 1 }, new W.NumberingFormat { Val = W.NumberFormatValues.Decimal }) { LevelIndex = 0 }) { AbstractNumberId = 0 },
            new W.NumberingInstance(new W.AbstractNumId { Val = 0 }) { NumberID = 1 });
        var link = main.AddHyperlinkRelationship(new Uri("https://example.com/page"), true);
        var image = main.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(Png1x1)) image.FeedData(stream);

        static W.Paragraph P(params OpenXmlElement[] children) => new(children);
        static W.Run R(string text, W.RunProperties? props = null) =>
            new(new OpenXmlElement[] { props ?? new W.RunProperties(), new W.Text(text) { Space = SpaceProcessingModeValues.Preserve } });
        var listProps = new W.NumberingProperties(new W.NumberingLevelReference { Val = 0 }, new W.NumberingId { Val = 1 });
        var drawing = new W.Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = 254000, Cy = 127000 },
            new DocumentFormat.OpenXml.Drawing.Graphic(new DocumentFormat.OpenXml.Drawing.GraphicData(
                new DocumentFormat.OpenXml.Drawing.Pictures.Picture(new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(
                    new DocumentFormat.OpenXml.Drawing.Blip { Embed = main.GetIdOfPart(image) }))) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));

        main.Document = new W.Document(new W.Body(
            P(new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }), R("Quarterly Report")),
            P(R("Plain, "), R("bold", new W.RunProperties(new W.Bold())), R(" and "),
              new W.Hyperlink(R("a link")) { Id = link.Id }, R(".")),
            P(new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center }), R("Centered", new W.RunProperties(new W.Color { Val = "336699" }))),
            P(new W.ParagraphProperties(listProps.CloneNode(true)), R("First")),
            P(new W.ParagraphProperties(listProps.CloneNode(true)), R("Second")),
            new W.Table(
                new W.TableRow(new W.TableCell(P(R("Region"))), new W.TableCell(P(R("Sales")))),
                new W.TableRow(new W.TableCell(P(R("North"))), new W.TableCell(P(R("120"))))),
            P(new W.Run(drawing)),
            new W.SectionProperties(new W.PageSize { Width = 15840, Height = 12240 }, new W.PageMargin { Top = 720, Right = 720, Bottom = 720, Left = 720 })));
        return path;
    }

    [Fact]
    public void ReadsDocx()
    {
        var doc = DocumentReader.Read(MakeDocx(), S.Format("docx"));
        Assert.Equal(1, Assert.IsType<Paragraph>(doc.Blocks[0]).HeadingLevel);
        var body = Assert.IsType<Paragraph>(doc.Blocks[1]);
        Assert.Equal("Plain, bold and a link.", body.PlainText);
        Assert.Contains(body.Inlines.OfType<TextRun>(), r => r.Text == "bold" && r.Style.Bold);
        Assert.Contains(body.Inlines.OfType<TextRun>(), r => r.Text == "a link" && r.Style.Link == "https://example.com/page");
        var centered = Assert.IsType<Paragraph>(doc.Blocks[2]);
        Assert.Equal(Alignment.Center, centered.Alignment);
        Assert.Equal("336699", centered.Inlines.OfType<TextRun>().Single().Style.Color);
        Assert.Equal([(true, 1), (true, 2)], doc.Blocks.OfType<ListItem>().Select(i => (i.Ordered, i.Number)));
        Assert.Equal("Region\tSales\nNorth\t120", Assert.Single(doc.Blocks.OfType<Table>()).PlainText);
        var image = doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<ImageRun>().Single();
        Assert.Equal((20.0, 10.0), (image.Width, image.Height));
        Assert.Equal(new PageSetup(792, 612, 36, 36, 36, 36), doc.Page);
    }

    [Fact]
    public void EncryptedWordFilesAreCopyProtected()
    {
        var path = Path.Combine(Dir, "locked.docx");
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[504], .. Encoding.Unicode.GetBytes("EncryptedPackage")]);
        Assert.Equal(ConversionErrorKind.CopyProtected, Assert.Throws<ConversionException>(() => DocumentReader.Read(path, S.Format("docx"))).Kind);
        File.WriteAllText(Path.Combine(Dir, "junk.docx"), "nope");
        Assert.Equal(ConversionErrorKind.Unreadable, Assert.Throws<ConversionException>(() => DocumentReader.Read(Path.Combine(Dir, "junk.docx"), S.Format("docx"))).Kind);
    }

    // ODT

    private string MakeOdt(bool encrypted = false)
    {
        var path = Path.Combine(Dir, encrypted ? "locked.odt" : "notes.odt");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Text(string name, string text) { using var s = zip.CreateEntry(name).Open(); s.Write(Encoding.UTF8.GetBytes(text)); }
        Text("mimetype", "application/vnd.oasis.opendocument.text");
        Text("META-INF/manifest.xml", encrypted
            ? """<manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0"><manifest:file-entry manifest:full-path="content.xml"><manifest:encryption-data/></manifest:file-entry></manifest:manifest>"""
            : """<manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0"/>""");
        Text("styles.xml", """
            <office:document-styles xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0">
            <office:automatic-styles><style:page-layout style:name="pm1"><style:page-layout-properties fo:page-width="21cm" fo:page-height="29.7cm" fo:margin-top="2cm" fo:margin-bottom="2cm" fo:margin-left="2cm" fo:margin-right="2cm"/></style:page-layout></office:automatic-styles>
            </office:document-styles>
            """);
        Text("content.xml", """
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0"
              xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"
              xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" xmlns:xlink="http://www.w3.org/1999/xlink">
            <office:automatic-styles>
              <style:style style:name="T1" style:family="text"><style:text-properties fo:font-weight="bold"/></style:style>
              <style:style style:name="P1" style:family="paragraph"><style:paragraph-properties fo:text-align="center"/></style:style>
              <text:list-style style:name="L1"><text:list-level-style-number text:level="1" text:start-value="5"/></text:list-style>
            </office:automatic-styles>
            <office:body><office:text>
              <text:h text:outline-level="2">Notes</text:h>
              <text:p>Plain <text:span text:style-name="T1">bold</text:span><text:s text:c="2"/>and <text:a xlink:href="https://example.com">link</text:a></text:p>
              <text:p text:style-name="P1">Centered</text:p>
              <text:list text:style-name="L1"><text:list-item><text:p>Five</text:p></text:list-item><text:list-item><text:p>Six</text:p></text:list-item></text:list>
              <table:table><table:table-row><table:table-cell><text:p>A</text:p></table:table-cell><table:table-cell><text:p>B</text:p></table:table-cell></table:table-row></table:table>
            </office:text></office:body></office:document-content>
            """);
        return path;
    }

    [Fact]
    public void ReadsOdt()
    {
        var doc = DocumentReader.Read(MakeOdt(), S.Format("odt"));
        Assert.Equal(2, Assert.IsType<Paragraph>(doc.Blocks[0]).HeadingLevel);
        var body = Assert.IsType<Paragraph>(doc.Blocks[1]);
        Assert.Equal("Plain bold  and link", body.PlainText);
        Assert.Contains(body.Inlines.OfType<TextRun>(), r => r.Text == "bold" && r.Style.Bold);
        Assert.Contains(body.Inlines.OfType<TextRun>(), r => r.Style.Link == "https://example.com");
        Assert.Equal(Alignment.Center, Assert.IsType<Paragraph>(doc.Blocks[2]).Alignment);
        Assert.Equal([(true, 5), (true, 6)], doc.Blocks.OfType<ListItem>().Select(i => (i.Ordered, i.Number)));
        Assert.Equal("A\tB", Assert.Single(doc.Blocks.OfType<Table>()).PlainText);
        Assert.Equal(595.3, doc.Page!.Width, 1);
        Assert.Equal(56.7, doc.Page.Left, 1);
    }

    [Fact]
    public void PasswordProtectedOdtIsCopyProtected() =>
        Assert.Equal(ConversionErrorKind.CopyProtected, Assert.Throws<ConversionException>(() => DocumentReader.Read(MakeOdt(encrypted: true), S.Format("odt"))).Kind);

    // RTF

    [Fact]
    public void ReadsRtf()
    {
        var hex = Convert.ToHexString(Png1x1);
        var rtf = $$$"""
            {\rtf1\ansi\ansicpg1252\cocoartf2822
            {\fonttbl\f0\fswiss\fcharset0 Helvetica;\f1\fnil Menlo-Regular;}
            {\colortbl;\red255\green255\blue255;\red51\green102\blue153;}
            {\*\expandedcolortbl;;}
            \paperw11900\paperh16840\margl1440\margr1440\vieww11520\viewh8400\viewkind0
            \pard\qc\f0\b\fs48 \cf2 Title\b0\fs24 \cf0 \par
            \pard Caf\'e9 and \i italic\i0 , \ul under\ulnone , \strike gone\strike0 , \uc1\u8364 ? euro.\line next line\par
            {\field{\*\fldinst{HYPERLINK "https://example.com/rtf"}}{\fldrslt link text}}\par
            {\listtext\tab \uc0舦 \tab}\ls1\ilvl0 Bullet one\par
            {\listtext\tab 2.\tab}\ls2\ilvl0 Numbered two\par
            \pard\trowd\cellx2000\cellx4000\intbl Region\cell Sales\cell\row
            \trowd\cellx2000\cellx4000\intbl North\cell 120\cell\row
            \pard {\pict\pngblip\picw1\pich1\picwgoal300\pichgoal150 {{{hex}}}}\par
            {\header ignored\par}
            End\par}
            """;
        var doc = RtfReader.Read(rtf);
        var title = Assert.IsType<Paragraph>(doc.Blocks[0]);
        Assert.Equal(("Title", Alignment.Center), (title.PlainText, title.Alignment));
        var titleRun = title.Inlines.OfType<TextRun>().First();
        Assert.True(titleRun.Style.Bold);
        Assert.Equal((24.0, "336699", "Helvetica"), (titleRun.Style.FontSize!.Value, titleRun.Style.Color, titleRun.Style.FontFamily));

        var body = Assert.IsType<Paragraph>(doc.Blocks[1]);
        Assert.Equal("Café and italic, under, gone, € euro.\nnext line", body.PlainText);
        Assert.Contains(body.Inlines.OfType<TextRun>(), r => r.Text == "italic" && r.Style.Italic);
        Assert.Contains(body.Inlines.OfType<TextRun>(), r => r.Text == "gone" && r.Style.Strike);

        var link = Assert.IsType<Paragraph>(doc.Blocks[2]);
        Assert.Equal("https://example.com/rtf", link.Inlines.OfType<TextRun>().Single().Style.Link);

        var items = doc.Blocks.OfType<ListItem>().ToList();
        Assert.Equal([("Bullet one", false), ("Numbered two", true)], items.Select(i => (i.PlainText, i.Ordered)));
        Assert.Equal(2, items[1].Number);

        Assert.Equal("Region\tSales\nNorth\t120", Assert.Single(doc.Blocks.OfType<Table>()).PlainText);
        var image = doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<ImageRun>().Single();
        Assert.Equal((15.0, 7.5), (image.Width, image.Height));
        Assert.DoesNotContain("ignored", doc.PlainText);
        Assert.Equal("End", doc.Blocks[^1].PlainText);
        Assert.Equal((595.0, 842.0), (doc.Page!.Width, doc.Page.Height));
    }

    [Fact]
    public void NotRtfIsUnreadable()
    {
        var path = Path.Combine(Dir, "fake.rtf");
        File.WriteAllText(path, "plain text pretending");
        Assert.Equal(ConversionErrorKind.Unreadable, Assert.Throws<ConversionException>(() => DocumentReader.Read(path, S.Format("rtf"))).Kind);
    }
}
