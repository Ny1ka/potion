using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using PdfSharp.Drawing;
using Potion.Core.Documents;
using Potion.Core.Engines;
using Potion.Core.Formats;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace Potion.Core.Windows;

/// <summary>
/// Windows-only PDF work: PDF → PNG/JPG/SVG with the built-in PDF renderer (Windows.Data.Pdf), pictures of scanned
/// pages for PDF → document, and images → PDF (decoded with WIC, written with PDFsharp). The counterpart of the
/// Swift <c>PDFEngine</c>'s rendering and the Swift <c>ImageEngine.makePDF</c>.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsPdfEngine(FFmpegTools? tools, PdfPresets? presets = null, ImagePresets? imagePresets = null)
{
    private readonly PdfPresets _presets = presets ?? PdfPresets.Shared;
    private readonly ImagePresets _imagePresets = imagePresets ?? ImagePresets.Shared;

    /// <summary>One file per page: "Report page 1.png" (or "Report.png" for a one-page PDF).</summary>
    public async Task<IReadOnlyList<string>> RenderAsync(string input, FileFormat outputFormat, string outputDirectory,
                                                          PdfOptions? options = null, CancellationToken token = default)
    {
        options ??= new PdfOptions();
        OutputFiles.CheckWritable(outputDirectory);
        var count = PdfTools.Inspect(input);
        var pages = options.Pages ?? Enumerable.Range(1, count).ToList();
        if (pages.Count == 0 || pages.Any(p => p < 1 || p > count)) throw ConversionException.InvalidPages(PageSelection.Describe(pages), count);
        var pdf = await Load(input).ConfigureAwait(false);
        var name = Path.GetFileNameWithoutExtension(input);
        var ext = outputFormat.PreferredExtension;
        var quality = _imagePresets.QualityFor(new ImageOptions(PresetId: options.ImagePresetId));
        var saved = new List<string>();
        try
        {
            foreach (var number in pages)
            {
                token.ThrowIfCancellationRequested();
                var (png, width, height) = await RenderPageAsync(pdf, number, options.Dpi ?? _presets.DefaultDpi).ConfigureAwait(false);
                var bytes = outputFormat.Id switch
                {
                    "jpg" => await WicImageEngine.EncodeToBytesAsync((await WicImageEngine.DecodeAsync(png).ConfigureAwait(false)).Flattened(),
                                                                     BitmapEncoder.JpegEncoderId, quality).ConfigureAwait(false),
                    "svg" => Svg(png, width, height),
                    _ => png,
                };
                var temporary = OutputFiles.TemporaryPath(outputDirectory, ext);
                try
                {
                    await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
                    if (outputFormat.Id != "svg") await WicImageEngine.DecodeAsync(await File.ReadAllBytesAsync(temporary, token).ConfigureAwait(false)).ConfigureAwait(false);
                    saved.Add(OutputFiles.Finalize(temporary, outputDirectory, count == 1 ? name : $"{name} page {number}", ext));
                }
                finally { OutputFiles.RemoveIfPresent(temporary); }
            }
            return saved;
        }
        catch
        {
            foreach (var path in saved) OutputFiles.RemoveIfPresent(path);
            throw;
        }
    }

    /// <summary>A PNG of one page for PDF → document (pages without text), at the default resolution.</summary>
    public Func<int, (byte[] Png, double Width, double Height)?> PageRenderer(string input)
    {
        WinPdf.PdfDocument? pdf = null;
        return number =>
        {
            try
            {
                pdf ??= Load(input).GetAwaiter().GetResult();
                return RenderPageAsync(pdf, number, _presets.DefaultDpi).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        };
    }

    private static async Task<WinPdf.PdfDocument> Load(string input)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input));
            return await WinPdf.PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { throw ConversionException.Unreadable(Path.GetFileName(input)); }
    }

    /// <summary>Renders a page on white; neither side exceeds the preset pixel cap. Size returned in points.</summary>
    private async Task<(byte[] Png, double Width, double Height)> RenderPageAsync(WinPdf.PdfDocument pdf, int number, double dpi)
    {
        using var page = pdf.GetPage((uint)(number - 1));
        var size = page.Size;   // device-independent pixels (1/96 inch), rotation applied
        var scale = dpi / 96;
        var longest = Math.Max(size.Width, size.Height) * scale;
        if (longest > _presets.MaxRenderPixels) scale *= _presets.MaxRenderPixels / longest;
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new WinPdf.PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * scale)),
            BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255),
            BitmapEncoderId = BitmapEncoder.PngEncoderId,
        });
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return (bytes, size.Width * 0.75, size.Height * 0.75);
    }

    /// <summary>An SVG the size of the page holding the rendered page (see the matrix note pdfToSvg).</summary>
    private static byte[] Svg(byte[] png, double width, double height)
    {
        var w = width.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var h = height.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return System.Text.Encoding.UTF8.GetBytes(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"{w}pt\" height=\"{h}pt\" viewBox=\"0 0 {w} {h}\">"
            + $"<image width=\"{w}\" height=\"{h}\" preserveAspectRatio=\"none\" xlink:href=\"data:image/png;base64,{Convert.ToBase64String(png)}\"/></svg>\n");
    }

    /// <summary>One page per image, each the image's size at 72 dpi (like macOS). Several images make one combined
    /// PDF named <paramref name="baseName"/>.</summary>
    public async Task<string> ImagesToPdfAsync(IReadOnlyList<(string Path, FileFormat Format)> inputs, string outputDirectory, string baseName,
                                               ImageOptions? options = null, CancellationToken token = default)
    {
        if (inputs.Count == 0) throw ConversionException.Unsupported("Images", "PDF");
        OutputFiles.CheckWritable(outputDirectory);
        options ??= new ImageOptions();
        var images = new WicImageEngine(tools);
        PdfFonts.Install();
        using var pdf = new PdfSharp.Pdf.PdfDocument();
        pdf.Info.Creator = "Potion";
        foreach (var (path, format) in inputs)
        {
            var pixels = await images.LoadAsync(path, format, options.ResizeOrOriginal, token).ConfigureAwait(false);
            pixels = pixels.Transformed(options.Rotation, options.FlipHorizontal, options.FlipVertical);
            var png = await WicImageEngine.EncodeToBytesAsync(pixels, BitmapEncoder.PngEncoderId, null).ConfigureAwait(false);
            using var stream = new MemoryStream(png);
            using var image = XImage.FromStream(stream);
            var page = pdf.AddPage();
            var scale = 72 / _imagePresets.PdfDefaultDpi;
            page.Width = XUnit.FromPoint(pixels.Width * scale);
            page.Height = XUnit.FromPoint(pixels.Height * scale);
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
        }
        var temporary = OutputFiles.TemporaryPath(outputDirectory, "pdf");
        try
        {
            pdf.Save(temporary);
            if (PdfTools.Inspect(temporary) != inputs.Count) throw ConversionException.VerificationFailed(Path.GetFileName(inputs[0].Path));
            return OutputFiles.Finalize(temporary, outputDirectory, baseName, "pdf");
        }
        finally { OutputFiles.RemoveIfPresent(temporary); }
    }
}

/// <summary>The single entry point the Windows apps use: looks the conversion up in the matrix and hands it to the
/// right engine (same role as the Swift <c>ConversionService</c>). Returns the saved files.</summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class ConversionService(FFmpegTools? tools, ConversionMatrix? matrix = null)
{
    private readonly ConversionMatrix _matrix = matrix ?? ConversionMatrix.Shared;

    public FFmpegTools? Tools => tools;

    /// <summary>Finds the bundled FFmpeg, or works without it (audio and video then report the tools as missing).</summary>
    public static ConversionService WithBundledTools()
    {
        FFmpegTools? found = null;
        try { found = FFmpegTools.Locate(); } catch (ConversionException) { }
        return new ConversionService(found);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, FileFormat inputFormat, FileFormat outputFormat, string outputDirectory,
                                                          ConversionSettings? settings = null, IProgress<double>? progress = null,
                                                          CancellationToken token = default)
    {
        settings ??= new ConversionSettings();
        var conversion = _matrix.Conversion(inputFormat.Id, outputFormat.Id) ?? throw ConversionException.Unsupported(inputFormat.Name, outputFormat.Name);
        token.ThrowIfCancellationRequested();
        IReadOnlyList<string> result = conversion.Operation switch
        {
            ConversionOperation.TranscodeVideo or ConversionOperation.VideoToGif or ConversionOperation.GifToVideo =>
                [await new VideoEngine(tools ?? throw ConversionException.ToolsMissing(), matrix: _matrix)
                    .ConvertAsync(input, inputFormat, outputFormat, outputDirectory, settings.Video, progress, token).ConfigureAwait(false)],
            ConversionOperation.TranscodeAudio or ConversionOperation.ExtractAudio =>
                [await new AudioEngine(tools ?? throw ConversionException.ToolsMissing(), matrix: _matrix)
                    .ConvertAsync(input, inputFormat, outputFormat, outputDirectory, settings.Audio, progress, token).ConfigureAwait(false)],
            ConversionOperation.ConvertImage =>
                [await new WicImageEngine(tools, matrix: _matrix).ConvertAsync(input, inputFormat, outputFormat, outputDirectory, settings.Image, token).ConfigureAwait(false)],
            ConversionOperation.ImageToPdf =>
                [await new WindowsPdfEngine(tools).ImagesToPdfAsync([(input, inputFormat)], outputDirectory, Path.GetFileNameWithoutExtension(input), settings.Image, token).ConfigureAwait(false)],
            ConversionOperation.PdfToImage =>
                await new WindowsPdfEngine(tools).RenderAsync(input, outputFormat, outputDirectory, settings.Pdf, token).ConfigureAwait(false),
            ConversionOperation.PdfToDocument =>
                [await new DocumentEngine(_matrix).ConvertAsync(input, inputFormat, outputFormat, outputDirectory, token,
                    settings.Pdf?.Pages, new WindowsPdfEngine(tools).PageRenderer(input)).ConfigureAwait(false)],
            ConversionOperation.ConvertDocument or ConversionOperation.DocumentToPdf =>
                [await new DocumentEngine(_matrix).ConvertAsync(input, inputFormat, outputFormat, outputDirectory, token).ConfigureAwait(false)],
            _ => throw ConversionException.Unsupported(inputFormat.Name, outputFormat.Name),
        };
        progress?.Report(1);
        return result;
    }
}
