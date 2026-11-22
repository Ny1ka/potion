using System.Text.Json;
using PdfSharp.Pdf.IO;
using Potion.Core.Documents;
using Potion.Core.Engines;
using Xunit;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using S = Potion.Core.Tests.EngineTestSupport;

namespace Potion.Core.Tests;

/// <summary>PDF tools and PDF → document on Windows (platform-neutral code, so these run everywhere).</summary>
public sealed class PdfToolsTests : EngineTestBase
{
    private string MakePdf(string name, params string[] pageTexts)
    {
        var doc = new Document();
        for (var i = 0; i < pageTexts.Length; i++)
        {
            if (i > 0) doc.Blocks.Add(new PageBreak());
            doc.Blocks.Add(new Paragraph([new TextRun(pageTexts[i], TextStyle.Plain)]));
        }
        var path = Path.Combine(Dir, name);
        File.WriteAllBytes(path, PdfDocumentWriter.Write(doc, Path.GetFileNameWithoutExtension(name)));
        return path;
    }

    private string ThreePages() => MakePdf("doc.pdf", "Page one text", "Page two text", "Page three text");

    private static List<string> Texts(string path)
    {
        using var pdf = PigDocument.Open(path);
        return pdf.GetPages().Select(p => p.Text).ToList();
    }

    [Fact]
    public void PageSelectionConformance()
    {
        using var cases = JsonDocument.Parse(RepoFiles.Text("core/spec/tests/pages.json"));
        var count = 0;
        foreach (var c in cases.RootElement.GetProperty("cases").EnumerateArray())
        {
            var selection = c.GetProperty("selection").GetString()!;
            var pageCount = c.GetProperty("pageCount").GetInt32();
            if (c.TryGetProperty("error", out var error) && error.GetBoolean())
                Assert.Equal(ConversionErrorKind.InvalidPages, Assert.Throws<ConversionException>(() => PageSelection.Pages(selection, pageCount)).Kind);
            else
                Assert.Equal(c.GetProperty("pages").EnumerateArray().Select(p => p.GetInt32()), PageSelection.Pages(selection, pageCount));
            count++;
        }
        Assert.True(count > 10);
        Assert.Equal("2–4, 7, 9–10", PageSelection.Describe([2, 3, 4, 7, 9, 10]));
    }

    [Fact]
    public void MergeSplitExtractReorder()
    {
        var a = MakePdf("a.pdf", "Alpha one", "Alpha two");
        var b = MakePdf("b.pdf", "Bravo one");
        var merged = PdfTools.Merge([a, b], Dir, "Combined");
        Assert.Equal("Combined.pdf", Path.GetFileName(merged));
        var mergedTexts = Texts(merged);
        Assert.Equal(3, mergedTexts.Count);
        Assert.Contains("Bravo", mergedTexts[2]);

        var pdf = ThreePages();
        var singles = PdfTools.Split(pdf, Dir);
        Assert.Equal(["doc page 1.pdf", "doc page 2.pdf", "doc page 3.pdf"], singles.Select(Path.GetFileName));
        Assert.Contains("two", Texts(singles[1])[0]);

        var extracted = PdfTools.Extract(pdf, [3, 1], Dir);
        Assert.Equal("doc pages 3, 1.pdf", Path.GetFileName(extracted));
        var texts = Texts(extracted);
        Assert.Contains("three", texts[0]);
        Assert.Contains("one", texts[1]);

        var reordered = PdfTools.Reorder(pdf, [2, 3, 1], Dir);
        Assert.Contains("two", Texts(reordered)[0]);
        Assert.Equal(ConversionErrorKind.InvalidPages, Assert.Throws<ConversionException>(() => PdfTools.Reorder(pdf, [1, 2], Dir)).Kind);
        Assert.Empty(Directory.GetFiles(Dir, ".potion-*"));
    }

    [Fact]
    public void CompressKeepsTextOrSaysItsAlreadySmall()
    {
        var pdf = ThreePages();
        try
        {
            var compressed = PdfTools.Compress(pdf, Dir);
            Assert.Contains("one", Texts(compressed)[0]);
        }
        catch (ConversionException e)
        {
            Assert.Equal(ConversionErrorKind.NotSmaller, e.Kind);
            Assert.Equal("“doc.pdf” is already as small as Potion can make it, so no compressed copy was saved.", e.Message);
        }
    }

    [Fact]
    public async Task PdfToDocuments()
    {
        var pdf = ThreePages();
        var engine = new DocumentEngine();
        var txt = await engine.ConvertAsync(pdf, S.Format("pdf"), S.Format("txt"), Dir);
        var text = File.ReadAllText(txt);
        Assert.True(text.IndexOf("Page one", StringComparison.Ordinal) < text.IndexOf("Page three", StringComparison.Ordinal));
        var docx = await engine.ConvertAsync(pdf, S.Format("pdf"), S.Format("docx"), Dir, pages: [1, 3]);
        var doc = DocumentReader.Read(docx, S.Format("docx"));
        Assert.Contains("Page three", doc.PlainText);
        Assert.DoesNotContain("Page two", doc.PlainText);
        Assert.Contains(doc.Blocks, b => b is PageBreak);
    }

    [Fact]
    public void ProtectedPdfsAreRefused()
    {
        var source = ThreePages();
        var locked = Path.Combine(Dir, "locked.pdf");
        using (var doc = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            doc.SecuritySettings.UserPassword = "user";
            doc.SecuritySettings.OwnerPassword = "owner";
            doc.Save(locked);
        }
        var restricted = Path.Combine(Dir, "restricted.pdf");
        using (var doc = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            doc.SecuritySettings.OwnerPassword = "owner";
            doc.SecuritySettings.PermitExtractContent = false;
            doc.Save(restricted);
        }
        foreach (var file in new[] { locked, restricted })
        {
            Assert.Equal(ConversionErrorKind.CopyProtected, Assert.Throws<ConversionException>(() => PdfTools.Split(file, Dir)).Kind);
            Assert.Equal(ConversionErrorKind.CopyProtected, Assert.Throws<ConversionException>(() => PdfTools.ToDocument(file)).Kind);
        }
        var junk = Path.Combine(Dir, "junk.pdf");
        File.WriteAllText(junk, "%PDF-1.4 not really");
        Assert.Equal(ConversionErrorKind.Unreadable, Assert.Throws<ConversionException>(() => PdfTools.Inspect(junk)).Kind);
    }
}
