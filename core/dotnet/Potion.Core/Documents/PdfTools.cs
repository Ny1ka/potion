using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Potion.Core.Engines;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Potion.Core.Documents;

/// <summary>
/// The PDF tools on Windows (merge, split, extract, reorder, compress) with PDFsharp, and PDF → document text with
/// PdfPig. Same names and behavior as the Swift <c>PDFEngine</c>. Protected PDFs: anything encrypted (a password,
/// or owner restrictions) is refused, so Potion never produces an unprotected copy of a protected file.
/// </summary>
public static class PdfTools
{
    /// <summary>Opens a PDF to check it: returns the page count, or throws Unreadable / CopyProtected.</summary>
    public static int Inspect(string path)
    {
        try
        {
            using var pdf = PigDocument.Open(path);
            if (pdf.IsEncrypted) throw ConversionException.CopyProtected();
            return pdf.NumberOfPages;
        }
        catch (ConversionException) { throw; }
        catch (Exception e) when (e.GetType().Name.Contains("Encrypt", StringComparison.Ordinal)) { throw ConversionException.CopyProtected(); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw ConversionException.Unreadable(Path.GetFileName(path)); }
    }

    private static PdfSharp.Pdf.PdfDocument Import(string path)
    {
        Inspect(path);
        try { return PdfReader.Open(path, PdfDocumentOpenMode.Import); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw ConversionException.Unreadable(Path.GetFileName(path)); }
    }

    /// <summary>Joins PDFs in the given order into one file named <paramref name="baseName"/>.</summary>
    public static string Merge(IReadOnlyList<string> inputs, string outputDirectory, string baseName)
    {
        if (inputs.Count < 2) throw ConversionException.Unsupported("One PDF", "a merged PDF");
        OutputFiles.CheckWritable(outputDirectory);
        var merged = new PdfSharp.Pdf.PdfDocument();
        foreach (var input in inputs)
        {
            using var source = Import(input);
            foreach (var page in source.Pages) merged.AddPage(page);
        }
        return Save(merged, outputDirectory, baseName, Path.GetFileName(inputs[0]));
    }

    /// <summary>One file per <paramref name="pagesPerFile"/> pages: "Report page 1.pdf" or "Report pages 1–3.pdf".</summary>
    public static IReadOnlyList<string> Split(string input, string outputDirectory, int pagesPerFile = 1)
    {
        OutputFiles.CheckWritable(outputDirectory);
        using var source = Import(input);
        if (source.PageCount < 2 || pagesPerFile < 1) throw ConversionException.Unsupported("A one-page PDF", "separate pages");
        var name = Path.GetFileNameWithoutExtension(input);
        var groups = Enumerable.Range(0, (source.PageCount + pagesPerFile - 1) / pagesPerFile)
            .Select(g => Enumerable.Range(g * pagesPerFile + 1, Math.Min(pagesPerFile, source.PageCount - g * pagesPerFile)).ToList()).ToList();
        var saved = new List<string>();
        try
        {
            foreach (var group in groups)
            {
                var label = group.Count == 1 ? $"page {group[0]}" : $"pages {PageSelection.Describe(group)}";
                saved.Add(Save(Copy(source, group), outputDirectory, $"{name} {label}", Path.GetFileName(input)));
            }
            return saved;
        }
        catch
        {
            foreach (var path in saved) OutputFiles.RemoveIfPresent(path);   // no partial set left behind
            throw;
        }
    }

    /// <summary>A new PDF with only the chosen pages, in the order given: "Report pages 2–4.pdf".</summary>
    public static string Extract(string input, IReadOnlyList<int> pages, string outputDirectory)
    {
        OutputFiles.CheckWritable(outputDirectory);
        using var source = Import(input);
        Validate(pages, source.PageCount);
        var description = PageSelection.Describe(pages);
        var label = pages.Count == 1 ? $"page {description}" : description.Length <= 40 ? $"pages {description}" : "extract";
        return Save(Copy(source, pages), outputDirectory, $"{Path.GetFileNameWithoutExtension(input)} {label}", Path.GetFileName(input));
    }

    /// <summary>The same pages in a new order; <paramref name="order"/> lists every page once.</summary>
    public static string Reorder(string input, IReadOnlyList<int> order, string outputDirectory)
    {
        OutputFiles.CheckWritable(outputDirectory);
        using var source = Import(input);
        if (!order.OrderBy(p => p).SequenceEqual(Enumerable.Range(1, source.PageCount)))
            throw ConversionException.InvalidPages(PageSelection.Describe(order), source.PageCount);
        return Save(Copy(source, order), outputDirectory, $"{Path.GetFileNameWithoutExtension(input)} reordered", Path.GetFileName(input));
    }

    /// <summary>Re-saves with maximum stream compression. Fails with NotSmaller when that doesn't help. (Unlike macOS,
    /// images aren't recompressed: PDFsharp can't re-encode them.)</summary>
    public static string Compress(string input, string outputDirectory)
    {
        OutputFiles.CheckWritable(outputDirectory);
        Inspect(input);
        PdfSharp.Pdf.PdfDocument document;
        try { document = PdfReader.Open(input, PdfDocumentOpenMode.Modify); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw ConversionException.Unreadable(Path.GetFileName(input)); }
        using (document)
        {
            document.Options.CompressContentStreams = true;
            document.Options.NoCompression = false;
            document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            document.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Automatic;
            var name = Path.GetFileName(input);
            var pageCount = document.PageCount;   // read before saving: the document can't be inspected afterwards
            var temporary = OutputFiles.TemporaryPath(outputDirectory, "pdf");
            try
            {
                document.Save(temporary);
                Verify(temporary, pageCount, name);
                if (new FileInfo(temporary).Length >= new FileInfo(input).Length) throw ConversionException.NotSmaller(name);
                return OutputFiles.Finalize(temporary, outputDirectory, $"{Path.GetFileNameWithoutExtension(input)} compressed", "pdf");
            }
            finally { OutputFiles.RemoveIfPresent(temporary); }
        }
    }

    private static void Validate(IReadOnlyList<int> pages, int pageCount)
    {
        if (pages.Count == 0 || pages.Any(p => p < 1 || p > pageCount)) throw ConversionException.InvalidPages(PageSelection.Describe(pages), pageCount);
    }

    private static PdfSharp.Pdf.PdfDocument Copy(PdfSharp.Pdf.PdfDocument source, IReadOnlyList<int> pages)
    {
        var copy = new PdfSharp.Pdf.PdfDocument();
        foreach (var page in pages) copy.AddPage(source.Pages[page - 1]);
        return copy;
    }

    private static string Save(PdfSharp.Pdf.PdfDocument document, string outputDirectory, string baseName, string inputName)
    {
        var temporary = OutputFiles.TemporaryPath(outputDirectory, "pdf");
        try
        {
            using (document) document.Save(temporary);
            Verify(temporary, -1, inputName);
            return OutputFiles.Finalize(temporary, outputDirectory, baseName, "pdf");
        }
        catch (UnauthorizedAccessException) { throw ConversionException.CannotWrite(Path.GetFileName(outputDirectory)); }
        finally { OutputFiles.RemoveIfPresent(temporary); }
    }

    private static void Verify(string path, int expectedPages, string inputName)
    {
        try
        {
            using var check = PigDocument.Open(path);
            if (check.NumberOfPages == 0 || (expectedPages >= 0 && check.NumberOfPages != expectedPages)) throw ConversionException.VerificationFailed(inputName);
        }
        catch (ConversionException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException) { throw ConversionException.VerificationFailed(inputName); }
    }

    /// <summary>
    /// PDF → document: each page's text in reading order, one paragraph per line, with page breaks between pages.
    /// <paramref name="renderPage"/> (Windows) returns a PNG of a page; pages without text (scans) become pictures.
    /// </summary>
    public static Document ToDocument(string input, IReadOnlyList<int>? pages = null, Func<int, (byte[] Png, double Width, double Height)?>? renderPage = null)
    {
        var count = Inspect(input);
        var chosen = pages ?? Enumerable.Range(1, count).ToList();
        Validate(chosen, count);
        var document = new Document { Title = Path.GetFileNameWithoutExtension(input) };
        using var pdf = PigDocument.Open(input);
        var firstPage = pdf.GetPage(chosen[0]);
        document.Page = new PageSetup(firstPage.Width, firstPage.Height, 72, 72, 72, 72);
        for (var i = 0; i < chosen.Count; i++)
        {
            if (i > 0) document.Blocks.Add(new PageBreak());
            var page = pdf.GetPage(chosen[i]);
            var text = ContentOrderTextExtractor.GetText(page);
            if (text.Trim().Length > 0)
            {
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                    document.Blocks.Add(new Paragraph(line.Trim().Length == 0 ? [] : [new TextRun(line.TrimEnd(), TextStyle.Plain)]));
            }
            else if (renderPage?.Invoke(chosen[i]) is { } picture)
            {
                document.Blocks.Add(new Paragraph([new ImageRun(picture.Png, "image/png", picture.Width, picture.Height, $"Page {chosen[i]}")]));
            }
        }
        return document;
    }
}
