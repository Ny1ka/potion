using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using Potion.Core.Engines;
using Potion.Core.Formats;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace Potion.Core.Windows;

/// <summary>
/// Converts images on Windows (matrix operation <c>ConvertImage</c>), the counterpart of the Swift <c>ImageEngine</c>.
/// Windows Imaging Component decodes (EXIF orientation applied, colors managed to sRGB) and writes PNG, JPEG, BMP,
/// GIF, TIFF and HEIC; FFmpeg writes WebP and AVIF and decodes what WIC can't. Pixel work is done by
/// <see cref="PixelBuffer"/> in Potion.Core. Formats whose codec is a Microsoft Store extension (HEIF, AV1, WebP,
/// RAW) report which extension to install when it's missing.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WicImageEngine(FFmpegTools? tools, ImagePresets? presets = null, ConversionMatrix? matrix = null)
{
    private readonly ImagePresets _presets = presets ?? ImagePresets.Shared;
    private readonly ConversionMatrix _matrix = matrix ?? ConversionMatrix.Shared;

    private static readonly Dictionary<string, Guid> WicEncoders = new()
    {
        ["png"] = BitmapEncoder.PngEncoderId, ["jpg"] = BitmapEncoder.JpegEncoderId, ["bmp"] = BitmapEncoder.BmpEncoderId,
        ["gif"] = BitmapEncoder.GifEncoderId, ["tiff"] = BitmapEncoder.TiffEncoderId, ["heic"] = BitmapEncoder.HeifEncoderId,
    };

    public async Task<string> ConvertAsync(string input, FileFormat inputFormat, FileFormat outputFormat, string outputDirectory,
                                           ImageOptions? options = null, CancellationToken token = default)
    {
        var conversion = _matrix.Conversion(inputFormat.Id, outputFormat.Id);
        if (conversion?.Operation != ConversionOperation.ConvertImage) throw ConversionException.Unsupported(inputFormat.Name, outputFormat.Name);
        OutputFiles.CheckWritable(outputDirectory);
        options ??= new ImageOptions();
        var name = Path.GetFileName(input);

        var image = await LoadAsync(input, inputFormat, options.ResizeOrOriginal, token).ConfigureAwait(false);
        image = image.Transformed(options.Rotation, options.FlipHorizontal, options.FlipVertical);
        if (!_presets.Transparency.Contains(outputFormat.Id)) image = image.Flattened();
        if (outputFormat.Id == "ico")
        {
            // Icons use standard square sizes up to 256 px: fit (never enlarge), then center on the smallest one that holds it.
            var longest = Math.Max(image.Width, image.Height);
            if (longest > _presets.IcoMaxSize)
            {
                var scale = (double)_presets.IcoMaxSize / longest;
                image = image.Resized(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
            }
            image = image.Squared(ImagePresets.IconSizes.First(s => s >= Math.Max(image.Width, image.Height)));
        }
        double? quality = _presets.Lossy.Contains(outputFormat.Id) ? _presets.QualityFor(options) : null;

        var ext = outputFormat.PreferredExtension;
        var temporary = OutputFiles.TemporaryPath(outputDirectory, ext);
        try
        {
            switch (outputFormat.Id)
            {
                case "webp" or "avif":
                    await EncodeWithFFmpegAsync(image, outputFormat.Id, quality ?? 0.82, temporary, name, token).ConfigureAwait(false);
                    break;
                case "ico":
                    var png = await EncodeToBytesAsync(image, BitmapEncoder.PngEncoderId, null).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(temporary, IcoWriter.Wrap(png, image.Width), token).ConfigureAwait(false);
                    break;
                default:
                    if (!WicEncoders.TryGetValue(outputFormat.Id, out var encoder)) throw ConversionException.Unsupported(inputFormat.Name, outputFormat.Name);
                    byte[] bytes;
                    try { bytes = await EncodeToBytesAsync(image, encoder, quality).ConfigureAwait(false); }
                    catch (Exception) when (outputFormat.Id == "heic") { throw ConversionException.NeedsExtension(name, "HEVC Video Extensions"); }
                    await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
                    break;
            }

            if (!await VerifyAsync(temporary, outputFormat.Id, token).ConfigureAwait(false)) throw ConversionException.VerificationFailed(name);
            return OutputFiles.Finalize(temporary, outputDirectory, Path.GetFileNameWithoutExtension(input), ext);
        }
        catch (UnauthorizedAccessException) { throw ConversionException.CannotWrite(Path.GetFileName(outputDirectory)); }
        finally { OutputFiles.RemoveIfPresent(temporary); }
    }

    // Decoding

    /// <summary>Decodes the first frame upright, sized for <paramref name="resize"/>. Falls back to FFmpeg when WIC
    /// has no codec for the file.</summary>
    public async Task<PixelBuffer> LoadAsync(string path, FileFormat format, ImageResize resize, CancellationToken token)
    {
        var name = Path.GetFileName(path);
        if (format.Id == "svg") throw ConversionException.Unsupported("SVG", "other formats on Windows");   // see docs/PROGRESS.md
        PixelBuffer image;
        try
        {
            image = await DecodeAsync(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception wicError) when (wicError is not OperationCanceledException)
        {
            image = await DecodeWithFFmpegAsync(path, token).ConfigureAwait(false) ?? throw MissingCodec(name, format);
        }
        var (w, h) = resize.TargetSize(image.Width, image.Height);
        return image.Resized(w, h);
    }

    private static ConversionException MissingCodec(string fileName, FileFormat format) => format switch
    {
        { Id: "heic" } => ConversionException.NeedsExtension(fileName, "HEIF Image Extensions"),
        { Id: "avif" } => ConversionException.NeedsExtension(fileName, "AV1 Video Extension"),
        { Id: "webp" } => ConversionException.NeedsExtension(fileName, "Webp Image Extensions"),
        { IsRaw: true } => ConversionException.NeedsExtension(fileName, "Raw Image Extension"),
        _ => ConversionException.Unreadable(fileName),
    };

    public static async Task<PixelBuffer> DecodeAsync(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var ras = stream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(ras);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return new PixelBuffer((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight, pixels.DetachPixelData());
    }

    private async Task<PixelBuffer?> DecodeWithFFmpegAsync(string path, CancellationToken token)
    {
        if (tools is null) return null;
        var png = Path.Combine(Path.GetTempPath(), $".potion-{Guid.NewGuid():N}.png");
        try
        {
            var result = await ProcessRunner.RunAsync(tools.Ffmpeg,
                ["-hide_banner", "-nostdin", "-n", "-loglevel", "error", "-i", Path.GetFullPath(path), "-frames:v", "1", "-f", "image2", "-c:v", "png", png],
                token: token).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(png)) return null;
            return await DecodeAsync(await File.ReadAllBytesAsync(png, token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
        finally { OutputFiles.RemoveIfPresent(png); }
    }

    // Encoding

    public static async Task<byte[]> EncodeToBytesAsync(PixelBuffer image, Guid encoderId, double? quality)
    {
        using var stream = new MemoryStream();
        using (var ras = stream.AsRandomAccessStream())
        {
            BitmapEncoder encoder;
            if (quality is { } q)
            {
                var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue((float)q, PropertyType.Single) };
                encoder = await BitmapEncoder.CreateAsync(encoderId, ras, options);
            }
            else
            {
                encoder = await BitmapEncoder.CreateAsync(encoderId, ras);
            }
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, image.HasTransparency ? BitmapAlphaMode.Straight : BitmapAlphaMode.Ignore,
                                 (uint)image.Width, (uint)image.Height, 96, 96, image.Bgra);
            await encoder.FlushAsync();
        }
        return stream.ToArray();
    }

    /// <summary>WebP (libwebp) and AVIF (SVT-AV1): hand FFmpeg a lossless PNG of the processed image.</summary>
    private async Task EncodeWithFFmpegAsync(PixelBuffer image, string formatId, double quality, string output, string inputName,
                                             CancellationToken token)
    {
        if (tools is null) throw ConversionException.ToolsMissing();
        var png = Path.Combine(Path.GetDirectoryName(output)!, $".potion-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(png, await EncodeToBytesAsync(image, BitmapEncoder.PngEncoderId, null).ConfigureAwait(false), token)
                .ConfigureAwait(false);
            var args = new List<string> { "-hide_banner", "-nostdin", "-n", "-loglevel", "error", "-i", png, "-frames:v", "1" };
            if (formatId == "webp")
                args.AddRange(["-c:v", "libwebp", "-quality", $"{(int)Math.Round(quality * 100)}", "-f", "webp"]);
            else
                args.AddRange(["-c:v", "libsvtav1", "-crf", $"{(int)Math.Round(63 - quality * 45)}", "-pix_fmt", "yuv420p", "-f", "avif"]);
            args.Add(Path.GetFullPath(output));
            var result = await ProcessRunner.RunAsync(tools.Ffmpeg, args, token: token).ConfigureAwait(false);
            if (result.ExitCode != 0) throw ConversionException.ConversionFailed(inputName, result.Stderr);
        }
        finally { OutputFiles.RemoveIfPresent(png); }
    }

    private async Task<bool> VerifyAsync(string path, string formatId, CancellationToken token)
    {
        if (formatId is "webp" or "avif")
        {
            if (tools is null) return false;
            try { return (await MediaProbe.ProbeAsync(path, tools, token).ConfigureAwait(false)).Streams.Any(s => s.Width > 0); }
            catch (ConversionException) { return false; }
        }
        try
        {
            var image = await DecodeAsync(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false)).ConfigureAwait(false);
            return image.Width > 0 && image.Height > 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return formatId == "heic";   // writing HEIC worked, but this PC may lack the decoder; trust the encoder
        }
    }
}
