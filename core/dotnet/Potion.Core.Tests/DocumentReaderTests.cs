using System.IO.Compression;
using System.Text;
using Potion.Core.Documents;
using Potion.Core.Engines;
using Xunit;
using S = Potion.Core.Tests.EngineTestSupport;

namespace Potion.Core.Tests;

/// <summary>Windows document readers (platform-neutral code, so these run everywhere).</summary>
public sealed class DocumentReaderTests : EngineTestBase
{
    private static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private const string Html = """
        <!DOCTYPE html><html><head><title>Quarterly Report</title><style>p { color: red }</style>
        <script>alert(1)</script></head><body>
        <h1>Quarterly <em>Report</em></h1>
        <p>Plain, <b>bold</b>, <i>italic</i> and a <a href="https://example.com/page">link</a> &amp; more.</p>
        <p style="text-align:center"><span style="color:#336699;font-weight:700">Centered</span></p>
        <ul><li>First point</li><li>Second point<ol start="3"><li>Nested</li></ol></li></ul>
        <table><tr><th>Region</th><th>Sales</th></tr><tr><td>North</td><td>120</td></tr></table>
        <p><img src="https://tracker.example/p.gif"><img src="dot.png" width="20" alt="dot"></p>
        <pre>let x = 1
        let y = 2</pre>
        <blockquote><p>Quoted</p></blockquote>
        </body></html>
        """;

    [Fact]
    public void ReadsHtmlStructureAndFormatting()
    {
        File.WriteAllBytes(Path.Combine(Dir, "dot.png"), Png1x1);
        var doc = HtmlReader.Read(Html, HtmlReader.LocalImages(Dir));
        Assert.Equal("Quarterly Report", doc.Title);

        var heading = Assert.IsType<Paragraph>(doc.Blocks[0]);
        Assert.Equal(1, heading.HeadingLevel);
        Assert.Equal("Quarterly Report", heading.PlainText);

        var body = Assert.IsType<Paragraph>(doc.Blocks[1]);
        Assert.Equal("Plain, bold, italic and a link & more.", body.PlainText);
        Assert.Contains(body.Inlines.OfType<TextRun>(), r => r.Text == "bold" && r.Style.Bold);
        Assert.Contains(body.Inlines.OfType<TextRun>(), r => r.Text == "link" && r.Style.Link == "https://example.com/page");

        var centered = Assert.IsType<Paragraph>(doc.Blocks[2]);
        Assert.Equal(Alignment.Center, centered.Alignment);
        Assert.Contains(centered.Inlines.OfType<TextRun>(), r => r.Style.Bold && r.Style.Color == "336699");

        var items = doc.Blocks.OfType<ListItem>().ToList();
        Assert.Equal(["First point", "Second point", "Nested"], items.Select(i => i.PlainText));
        Assert.Equal((0, false, 1), (items[0].Depth, items[0].Ordered, items[0].Number));
        Assert.Equal((1, true, 3), (items[2].Depth, items[2].Ordered, items[2].Number));
        Assert.Equal(items[0].ListId, items[1].ListId);

        var table = Assert.Single(doc.Blocks.OfType<Table>());
        Assert.True(table.HasHeader);
        Assert.Equal("Region\tSales\nNorth\t120", table.PlainText);

        var images = doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<ImageRun>().ToList();
        var image = Assert.Single(images);   // the remote one is never fetched
        Assert.Equal((15.0, 15.0), (image.Width, image.Height));   // 20 px wide, square → 15 pt
        Assert.Equal("dot", image.Alt);

        Assert.Equal("let x = 1\nlet y = 2", Assert.Single(doc.Blocks.OfType<CodeBlock>()).Code.Replace("        ", ""));
        Assert.True(doc.Blocks.OfType<Paragraph>().Single(p => p.PlainText == "Quoted").Quote);
        Assert.DoesNotContain("alert", doc.PlainText);
    }

    [Fact]
    public void ReadsMarkdownLikeTheMacReader()
    {
        File.WriteAllBytes(Path.Combine(Dir, "pic.png"), Png1x1);
        var doc = DocumentReader.ReadMarkdown("""
            # Release Notes

            Some *emphasis*, **strong**, ~~gone~~ and `code`, with a [link](https://example.com/notes).

            3. third
            4. fourth

            | Name | Count |
            |:-----|------:|
            | Apples | 3 |

            ![diagram](pic.png)
            """, Dir);
        Assert.Equal(1, Assert.IsType<Paragraph>(doc.Blocks[0]).HeadingLevel);
        var runs = Assert.IsType<Paragraph>(doc.Blocks[1]).Inlines.OfType<TextRun>().ToList();
        Assert.Contains(runs, r => r.Text == "emphasis" && r.Style.Italic);
        Assert.Contains(runs, r => r.Text == "gone" && r.Style.Strike);
        Assert.Contains(runs, r => r.Text == "code" && r.Style.Code);
        Assert.Equal([3, 4], doc.Blocks.OfType<ListItem>().Select(i => i.Number));
        Assert.Equal("Name\tCount\nApples\t3", Assert.Single(doc.Blocks.OfType<Table>()).PlainText);
        Assert.Single(doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<ImageRun>());
    }

    [Fact]
    public void DecodesTextFiles()
    {
        Assert.Equal("hi", DocumentReader.DecodeText([0xEF, 0xBB, 0xBF, 0x68, 0x69]));
        Assert.Equal("hi", DocumentReader.DecodeText([0xFF, 0xFE, 0x68, 0x00, 0x69, 0x00]));
        Assert.Equal("café", DocumentReader.DecodeText([0x63, 0x61, 0x66, 0xE9]));
        var doc = DocumentReader.ReadText("one\r\n\r\nthree\n");
        Assert.Equal(["one", "", "three"], doc.Blocks.Select(b => b.PlainText));
    }

    private string MakeEpub(params (string Name, string Text)[] extra)
    {
        var path = Path.Combine(Dir, "book.epub");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] data) { using var s = zip.CreateEntry(name).Open(); s.Write(data); }
            void Text(string name, string text) => Add(name, Encoding.UTF8.GetBytes(text));
            Text("mimetype", "application/epub+zip");
            Text("META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OPS/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
            Text("OPS/book.opf", """
                <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="2.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Two Chapters</dc:title></metadata>
                <manifest><item id="c2" href="text/ch%202.xhtml" media-type="application/xhtml+xml"/><item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/></manifest>
                <spine><itemref idref="c1"/><itemref idref="c2"/></spine></package>
                """);
            Text("OPS/text/ch1.xhtml", """<html xmlns="http://www.w3.org/1999/xhtml"><body><h1>Chapter One</h1><p>First words.</p><img src="../images/cover.png" alt="cover"/></body></html>""");
            Text("OPS/text/ch 2.xhtml", """<html xmlns="http://www.w3.org/1999/xhtml"><body><h1>Chapter Two</h1><p>Last words.</p></body></html>""");
            Add("OPS/images/cover.png", Png1x1);
            foreach (var (name, text) in extra) Text(name, text);
        }
        return path;
    }

    [Fact]
    public void ReadsEpubInSpineOrderWithImages()
    {
        var doc = DocumentReader.Read(MakeEpub(), S.Format("epub"));
        Assert.Equal("Two Chapters", doc.Title);
        var text = doc.PlainText;
        Assert.True(text.IndexOf("Chapter One", StringComparison.Ordinal) < text.IndexOf("Chapter Two", StringComparison.Ordinal));
        Assert.Single(doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<ImageRun>());
        Assert.Equal("OPS/images/a b.png", DocumentReader.Resolve("../images/a%20b.png#x", "OPS/text"));
    }

    [Fact]
    public void DrmEpubsAreCopyProtected()
    {
        var adept = """<encryption><EncryptedData><EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes128-cbc"/></EncryptedData></encryption>""";
        var error = Assert.Throws<ConversionException>(() => DocumentReader.Read(MakeEpub(("META-INF/encryption.xml", adept)), S.Format("epub")));
        Assert.Equal(ConversionErrorKind.CopyProtected, error.Kind);
        File.Delete(Path.Combine(Dir, "book.epub"));
        var fonts = """<encryption><EncryptedData><EncryptionMethod Algorithm="http://www.idpf.org/2008/embedding"/></EncryptedData></encryption>""";
        Assert.Contains("Chapter One", DocumentReader.Read(MakeEpub(("META-INF/encryption.xml", fonts)), S.Format("epub")).PlainText);
    }

    [Fact]
    public void BrokenFilesAreUnreadable()
    {
        var junk = Path.Combine(Dir, "junk.epub");
        File.WriteAllText(junk, "not a zip");
        Assert.Equal(ConversionErrorKind.Unreadable, Assert.Throws<ConversionException>(() => DocumentReader.Read(junk, S.Format("epub"))).Kind);
    }

    [Fact]
    public void ReadsImageSizesFromHeaders() => Assert.Equal((1, 1), ImageSize.Of(Png1x1));
}
