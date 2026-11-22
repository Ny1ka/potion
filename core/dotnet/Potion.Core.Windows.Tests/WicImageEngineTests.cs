using Potion.Core.Engines;
using Potion.Core.Formats;
using Potion.Core.Windows;
using Windows.Graphics.Imaging;
using Xunit;

namespace Potion.Core.Windows.Tests;

/// <summary>Image conversions with WIC (and FFmpeg for WebP/AVIF). Windows only; runs in the Windows CI job.</summary>
public sealed class WicImageEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"potion-wic-{Guid.NewGuid():N}");
    private readonly WicImageEngine _engine;

    public WicImageEngineTests()
    {
        Directory.CreateDirectory(_dir);
        _engine = new WicImageEngine(FindTools());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static FFmpegTools? FindTools()
    {
        var dir = Environment.GetEnvironmentVariable("POTION_FFMPEG_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
            dir = root is null ? "" : Path.Combine(root.FullName, "build", "ffmpeg", "windows", "bin");
        }
        try { return FFmpegTools.FromDirectory(dir); }
        catch (ConversionException)
        {
            if (Environment.GetEnvironmentVariable("POTION_REQUIRE_FFMPEG") == "1") Assert.Fail($"FFmpeg build missing at {dir}");
            return null;
        }
    }

    private static FileFormat F(string id) => FormatRegistry.Shared.FormatById(id)!;

    /// <summary>100×60: left half opaque red, right half transparent.</summary>
    private static PixelBuffer RedHalf(int width = 100, int height = 60)
    {
        var p = new PixelBuffer(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width / 2; x++)
            {
                var i = (y * width + x) * 4;
                p.Bgra[i + 2] = 255; p.Bgra[i + 3] = 255;
            }
        return p;
    }

    private async Task<string> Save(PixelBuffer image, string name, Guid encoder)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(path, await WicImageEngine.EncodeToBytesAsync(image, encoder, null));
        return path;
    }

    private static async Task<PixelBuffer> Read(string path) => await WicImageEngine.DecodeAsync(await File.ReadAllBytesAsync(path));

    private static (byte B, byte G, byte R, byte A) Pixel(PixelBuffer p, int x, int y)
    {
        var i = (y * p.Width + x) * 4;
        return (p.Bgra[i], p.Bgra[i + 1], p.Bgra[i + 2], p.Bgra[i + 3]);
    }

    [Fact]
    public async Task PngToJpgFlattensOntoWhite()
    {
        var png = await Save(RedHalf(), "sample.png", BitmapEncoder.PngEncoderId);
        var jpg = await _engine.ConvertAsync(png, F("png"), F("jpg"), _dir);
        Assert.Equal("sample.jpg", Path.GetFileName(jpg));
        var image = await Read(jpg);
        Assert.Equal((100, 60), (image.Width, image.Height));
        var white = Pixel(image, 90, 30);
        Assert.True(white.R > 240 && white.G > 240 && white.B > 240, $"expected white, got {white}");
        var red = Pixel(image, 10, 30);
        Assert.True(red.R > 200 && red.G < 60, $"expected red, got {red}");
    }

    [Fact]
    public async Task KeepsTransparencyAndResizes()
    {
        var png = await Save(RedHalf(), "sample.png", BitmapEncoder.PngEncoderId);
        var tiff = await _engine.ConvertAsync(png, F("png"), F("tiff"), _dir, new ImageOptions(Resize: new ImageResize.Fit(50, null)));
        var image = await Read(tiff);
        Assert.Equal((50, 30), (image.Width, image.Height));
        Assert.Equal(0, Pixel(image, 45, 15).A);
    }

    [Fact]
    public async Task AppliesExifOrientationAndRotation()
    {
        // Stored 100×60 with orientation 6 (shown rotated 90° clockwise).
        var path = Path.Combine(_dir, "photo.jpg");
        using (var file = File.Create(path))
        using (var ras = file.AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ras);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 100, 60, 96, 96, RedHalf().Flattened().Bgra);
            await encoder.BitmapProperties.SetPropertiesAsync(new[]
            {
                new KeyValuePair<string, BitmapTypedValue>("System.Photo.Orientation", new BitmapTypedValue((ushort)6, global::Windows.Foundation.PropertyType.UInt16)),
            });
            await encoder.FlushAsync();
        }
        var png = await _engine.ConvertAsync(path, F("jpg"), F("png"), _dir);
        Assert.Equal((60, 100), ((await Read(png)).Width, (await Read(png)).Height));
        var turned = await _engine.ConvertAsync(path, F("jpg"), F("bmp"), _dir, new ImageOptions(Rotation: 90));
        Assert.Equal((100, 60), ((await Read(turned)).Width, (await Read(turned)).Height));
    }

    [Fact]
    public async Task IconsAreSquareStandardSizes()
    {
        var png = await Save(RedHalf(600, 300), "logo.png", BitmapEncoder.PngEncoderId);
        var ico = await _engine.ConvertAsync(png, F("png"), F("ico"), _dir);
        var image = await Read(ico);
        Assert.Equal((256, 256), (image.Width, image.Height));
    }

    [Fact]
    public async Task WebpAndAvifGoThroughFFmpeg()
    {
        var png = await Save(RedHalf(), "sample.png", BitmapEncoder.PngEncoderId);
        var webp = await _engine.ConvertAsync(png, F("png"), F("webp"), _dir);
        Assert.Equal(".webp", Path.GetExtension(webp));
        var avif = await _engine.ConvertAsync(png, F("png"), F("avif"), _dir);
        Assert.True(new FileInfo(avif).Length > 0);
        // And back: WebP decodes with WIC or, without the extension, FFmpeg.
        var back = await _engine.ConvertAsync(webp, F("webp"), F("png"), _dir);
        Assert.Equal((100, 60), ((await Read(back)).Width, (await Read(back)).Height));
    }

    [Fact]
    public async Task NeverOverwritesAndReportsBadFiles()
    {
        var png = await Save(RedHalf(), "sample.png", BitmapEncoder.PngEncoderId);
        await File.WriteAllTextAsync(Path.Combine(_dir, "sample.gif"), "existing");
        var gif = await _engine.ConvertAsync(png, F("png"), F("gif"), _dir);
        Assert.Equal("sample 2.gif", Path.GetFileName(gif));
        Assert.Empty(Directory.GetFiles(_dir, ".potion-*"));

        var junk = Path.Combine(_dir, "junk.png");
        await File.WriteAllTextAsync(junk, "not an image");
        var error = await Assert.ThrowsAsync<ConversionException>(() => _engine.ConvertAsync(junk, F("png"), F("jpg"), _dir));
        Assert.Equal(ConversionErrorKind.Unreadable, error.Kind);
    }
}
