using System.IO.Compression;
using System.Xml.Linq;
using Potion.Core.Documents;
using Potion.Core.Engines;
using Xunit;
using S = Potion.Core.Tests.EngineTestSupport;

namespace Potion.Core.Tests;

/// <summary>TXT, Markdown, HTML and EPUB writers and the Windows document engine (platform-neutral code).</summary>
public sealed class DocumentWriterTests : EngineTestBase
{
    private static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static Document Sample()
    {
        var doc = new Document { Title = "Report" };
        var bold = TextStyle.Plain with { Bold = true };
        doc.Blocks.Add(new Paragraph([new TextRun("Quarterly Report", bold)], 1));
        doc.Blocks.Add(new Paragraph([new TextRun("Plain, ", TextStyle.Plain), new TextRun("bold", bold), new TextRun(" and a ", TextStyle.Plain),
            new TextRun("link", TextStyle.Plain with { Link = "https://example.com/page" }), new TextRun(" *literal*.", TextStyle.Plain)]));
        doc.Blocks.Add(new ListItem([new TextRun("First", TextStyle.Plain)], 0, false, 1, 1));
        doc.Blocks.Add(new ListItem([new TextRun("Second", TextStyle.Plain)], 0, false, 2, 1));
        doc.Blocks.Add(new ListItem([new TextRun("Nested", TextStyle.Plain)], 1, true, 3, 2));
        doc.Blocks.Add(new Table([
            [new TableCell([new Paragraph([new TextRun("Region", bold)])]), new TableCell([new Paragraph([new TextRun("Sales", bold)])])],
            [new TableCell([new Paragraph([new TextRun("North", TextStyle.Plain)])]), new TableCell([new Paragraph([new TextRun("120", TextStyle.Plain)])])],
        ], HasHeader: true));
        doc.Blocks.Add(new Paragraph([new ImageRun(Png1x1, "image/png", 30, 30, "dot")]));
        doc.Blocks.Add(new CodeBlock("let x = 1"));
        return doc;
    }

    [Fact]
    public void PlainText()
    {
        Assert.Equal("Quarterly Report\nPlain, bold and a link *literal*.\n• First\n• Second\n    3. Nested\nRegion\tSales\nNorth\t120\n\nlet x = 1\n",
                     PlainTextWriter.Write(Sample()));
    }

    [Fact]
    public void Markdown()
    {
        var md = MarkdownWriter.Write(Sample());
        Assert.StartsWith("# Quarterly Report\n\nPlain, **bold** and a [link](https://example.com/page) \\*literal\\*.\n\n- First\n- Second\n    3. Nested\n\n| Region | Sales |\n| --- | --- |\n| North | 120 |", md);
        Assert.Contains("```\nlet x = 1\n```", md);
        // Markdown we write reads back the same way.
        var back = DocumentReader.ReadMarkdown(md, Dir);
        Assert.Equal("Quarterly Report", back.Blocks.OfType<Paragraph>().First(p => p.HeadingLevel == 1).PlainText);
        Assert.Contains(back.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<TextRun>(), r => r.Style.Link == "https://example.com/page");
    }

    [Fact]
    public void HtmlRoundTrips()
    {
        var html = HtmlWriter.Write(Sample(), "Report");
        Assert.Contains("<h1><strong>Quarterly Report</strong></h1>", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("<ol start=\"3\">", html);
        var back = HtmlReader.Read(html);
        Assert.Equal("Report", back.Title);
        Assert.Equal(["First", "Second", "Nested"], back.Blocks.OfType<ListItem>().Select(i => i.PlainText));
        Assert.Equal("Region\tSales\nNorth\t120", back.Blocks.OfType<Table>().Single().PlainText);
        Assert.Single(back.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<ImageRun>());
    }

    [Fact]
    public void EpubIsAValidPackageAndReadsBack()
    {
        var path = Path.Combine(Dir, "book.epub");
        File.WriteAllBytes(path, EpubWriter.Write(Sample(), "Report", "en"));
        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.Equal("mimetype", zip.Entries[0].FullName);
            Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength);   // stored
            foreach (var name in new[] { "META-INF/container.xml", "OEBPS/content.opf", "OEBPS/nav.xhtml", "OEBPS/content.xhtml" })
            {
                using var s = zip.GetEntry(name)!.Open();
                XDocument.Load(s);   // well-formed XML
            }
            Assert.NotNull(zip.GetEntry("OEBPS/images/image1.png"));
        }
        var back = DocumentReader.Read(path, S.Format("epub"));
        Assert.Contains("Quarterly Report", back.PlainText);
        Assert.Single(back.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<ImageRun>());
    }

    private static Document WithPage()
    {
        var doc = Sample();
        doc.Page = new PageSetup(792, 612, 36, 36, 36, 36);
        doc.Blocks.Insert(2, new Paragraph([new TextRun("Centered and colored", TextStyle.Plain with { Color = "336699", Italic = true })], 0, Alignment.Center));
        return doc;
    }

    private static void AssertRoundTrip(Document back, bool checksPage = true)
    {
        Assert.Equal("Quarterly Report", back.Blocks.OfType<Paragraph>().First(p => p.HeadingLevel == 1).PlainText);
        var runs = back.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<TextRun>().ToList();
        Assert.Contains(runs, r => r.Text.Contains("bold") && r.Style.Bold);
        Assert.Contains(runs, r => r.Style.Link == "https://example.com/page");
        Assert.Contains(runs, r => r.Text.Contains("Centered") && r.Style.Italic && r.Style.Color == "336699");
        Assert.Equal(Alignment.Center, back.Blocks.OfType<Paragraph>().First(p => p.PlainText.Contains("Centered")).Alignment);
        var items = back.Blocks.OfType<ListItem>().ToList();
        Assert.Equal(["First", "Second", "Nested"], items.Select(i => i.PlainText.Trim()));
        Assert.True(items[2].Ordered);
        Assert.Equal(3, items[2].Number);
        Assert.Equal("Region\tSales\nNorth\t120", back.Blocks.OfType<Table>().Single().PlainText);
        Assert.Single(back.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<ImageRun>());
        if (checksPage) Assert.Equal(new PageSetup(792, 612, 36, 36, 36, 36), back.Page);
    }

    [Fact]
    public void DocxRoundTrips()
    {
        var path = Path.Combine(Dir, "out.docx");
        File.WriteAllBytes(path, DocxWriter.Write(WithPage(), "Report"));
        using (var zip = ZipFile.OpenRead(path))
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
            {
                using var s = entry.Open();
                XDocument.Load(s);
            }
        AssertRoundTrip(DocumentReader.Read(path, S.Format("docx")));
    }

    [Fact]
    public void RtfRoundTrips()
    {
        var rtf = RtfWriter.Write(WithPage());
        Assert.DoesNotContain(rtf, c => c > 127);   // pure 7-bit RTF
        AssertRoundTrip(RtfReader.Read(rtf));
    }

    [Fact]
    public void OdtRoundTrips()
    {
        var path = Path.Combine(Dir, "out.odt");
        File.WriteAllBytes(path, OdtWriter.Write(WithPage()));
        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.Equal("mimetype", zip.Entries[0].FullName);
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml")))
            {
                using var s = entry.Open();
                XDocument.Load(s);
            }
        }
        AssertRoundTrip(DocumentReader.Read(path, S.Format("odt")));
    }

    [Fact]
    public async Task EngineConvertsAndNeverOverwrites()
    {
        var md = Path.Combine(Dir, "notes.md");
        File.WriteAllText(md, "# Notes\n\nSome *text*.");
        File.WriteAllText(Path.Combine(Dir, "notes.html"), "existing");
        var engine = new DocumentEngine();
        var html = await engine.ConvertAsync(md, S.Format("md"), S.Format("html"), Dir);
        Assert.Equal("notes 2.html", Path.GetFileName(html));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(Dir, "notes.html")));
        var txt = await engine.ConvertAsync(html, S.Format("html"), S.Format("txt"), Dir);
        Assert.Contains("Some text.", File.ReadAllText(txt));
        var epub = await engine.ConvertAsync(md, S.Format("md"), S.Format("epub"), Dir);
        Assert.EndsWith(".epub", epub);
        Assert.Empty(Directory.GetFiles(Dir, ".potion-*"));

        var unsupported = await Assert.ThrowsAsync<ConversionException>(() => engine.ConvertAsync(md, S.Format("md"), S.Format("mp3"), Dir));
        Assert.Equal(ConversionErrorKind.Unsupported, unsupported.Kind);
    }
}
