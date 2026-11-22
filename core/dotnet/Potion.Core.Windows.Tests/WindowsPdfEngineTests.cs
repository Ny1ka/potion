using System.Xml.Linq;
using Potion.Core.Documents;
using Potion.Core.Engines;
using Potion.Core.Formats;
using Potion.Core.Windows;
using Windows.Graphics.Imaging;
using Xunit;

namespace Potion.Core.Windows.Tests;

/// <summary>PDF rendering, images → PDF and the conversion service on Windows. Runs in the Windows CI job.</summary>
public sealed class WindowsPdfEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"potion-winpdf-{Guid.NewGuid():N}");

    public WindowsPdfEngineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static FileFormat F(string id) => FormatRegistry.Shared.FormatById(id)!;

    private static FFmpegTools? Tools()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        try { return FFmpegTools.FromDirectory(Path.Combine(root!.FullName, "build", "ffmpeg", "windows", "bin")); }
        catch (ConversionException) { return null; }
    }

    private string ThreePages()
    {
        var doc = new Document();
        foreach (var (text, i) in new[] { "Page one text", "Page two text", "Page three text" }.Select((t, i) => (t, i)))
        {
            if (i > 0) doc.Blocks.Add(new PageBreak());
            doc.Blocks.Add(new Paragraph([new TextRun(text, TextStyle.Plain)]));
        }
        var path = Path.Combine(_dir, "doc.pdf");
        File.WriteAllBytes(path, PdfDocumentWriter.Write(doc, "doc"));
        return path;
    }

    private async Task<string> RedPng(string name = "red.png", int width = 100, int height = 60)
    {
        var pixels = new PixelBuffer(width, height);
        for (var i = 0; i < pixels.Bgra.Length; i += 4) { pixels.Bgra[i + 2] = 255; pixels.Bgra[i + 3] = 255; }
        var path = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(path, await WicImageEngine.EncodeToBytesAsync(pixels, BitmapEncoder.PngEncoderId, null));
        return path;
    }

    [Fact]
    public async Task RendersPagesToPngJpgAndSvg()
    {
        var engine = new WindowsPdfEngine(Tools());
        var pdf = ThreePages();
        var pngs = await engine.RenderAsync(pdf, F("png"), _dir, new PdfOptions(Dpi: 72));
        Assert.Equal(["doc page 1.png", "doc page 2.png", "doc page 3.png"], pngs.Select(Path.GetFileName));
        var first = await WicImageEngine.DecodeAsync(await File.ReadAllBytesAsync(pngs[0]));
        Assert.Equal((612, 792), (first.Width, first.Height));

        var jpgs = await engine.RenderAsync(pdf, F("jpg"), _dir, new PdfOptions(Pages: [2]));
        Assert.Equal(["doc page 2.jpg"], jpgs.Select(Path.GetFileName));
        Assert.Equal(1275, (await WicImageEngine.DecodeAsync(await File.ReadAllBytesAsync(jpgs[0]))).Width);   // 150 dpi

        var svg = (await engine.RenderAsync(pdf, F("svg"), _dir, new PdfOptions(Pages: [1])))[0];
        Assert.Equal("svg", XDocument.Load(svg).Root!.Name.LocalName);
    }

    [Fact]
    public async Task ImagesBecomePdfPagesAndScannedPagesBecomePictures()
    {
        var engine = new WindowsPdfEngine(Tools());
        var pdf = await engine.ImagesToPdfAsync([(await RedPng(), F("png")), (await RedPng("wide.png", 200, 100), F("png"))], _dir, "Scans");
        Assert.Equal("Scans.pdf", Path.GetFileName(pdf));
        Assert.Equal(2, PdfTools.Inspect(pdf));
        using (var check = UglyToad.PdfPig.PdfDocument.Open(pdf))
            Assert.Equal((100.0, 60.0), (Math.Round(check.GetPage(1).Width), Math.Round(check.GetPage(1).Height)));

        // A PDF of pictures has no text: converting it to DOCX keeps each page as a picture.
        var docx = (await new ConversionService(Tools()).ConvertAsync(pdf, F("pdf"), F("docx"), _dir))[0];
        var doc = DocumentReader.Read(docx, F("docx"));
        Assert.Equal(2, doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<ImageRun>().Count());
    }

    [Fact]
    public async Task ServiceRoutesEveryKindOfConversion()
    {
        var service = new ConversionService(Tools());
        var jpg = await service.ConvertAsync(await RedPng(), F("png"), F("jpg"), _dir);
        Assert.Equal("red.jpg", Path.GetFileName(jpg[0]));
        var imagePdf = await service.ConvertAsync(await RedPng("photo.png"), F("png"), F("pdf"), _dir);
        Assert.Equal(1, PdfTools.Inspect(imagePdf[0]));

        var md = Path.Combine(_dir, "notes.md");
        await File.WriteAllTextAsync(md, "# Notes\n\nText.");
        var docPdf = await service.ConvertAsync(md, F("md"), F("pdf"), _dir);
        Assert.Equal(1, PdfTools.Inspect(docPdf[0]));

        var unsupported = await Assert.ThrowsAsync<ConversionException>(() => service.ConvertAsync(md, F("md"), F("mp3"), _dir));
        Assert.Equal(ConversionErrorKind.Unsupported, unsupported.Kind);
        var noTools = await Assert.ThrowsAsync<ConversionException>(() => new ConversionService(null).ConvertAsync(md, F("mp4"), F("mp3"), _dir));
        Assert.Equal(ConversionErrorKind.ToolsMissing, noTools.Kind);
    }
}
