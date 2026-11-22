using Potion.Core.Engines;
using Xunit;

namespace Potion.Core.Tests;

/// <summary>The platform-neutral image pieces used by the Windows image engine.</summary>
public sealed class ImageUnitTests
{
    /// <summary>A 4×2 image: left half red, right half transparent.</summary>
    private static PixelBuffer Sample()
    {
        var p = new PixelBuffer(4, 2);
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 2; x++)
            {
                var i = (y * 4 + x) * 4;
                p.Bgra[i + 2] = 255; p.Bgra[i + 3] = 255;
            }
        return p;
    }

    private static (byte B, byte G, byte R, byte A) Pixel(PixelBuffer p, int x, int y)
    {
        var i = (y * p.Width + x) * 4;
        return (p.Bgra[i], p.Bgra[i + 1], p.Bgra[i + 2], p.Bgra[i + 3]);
    }

    [Fact]
    public void TargetSizesMatchTheSwiftEngine()
    {
        Assert.Equal((100, 60), new ImageResize.Original().TargetSize(100, 60));
        Assert.Equal((50, 30), new ImageResize.Fit(50, null).TargetSize(100, 60));
        Assert.Equal((100, 60), new ImageResize.Fit(400, 400).TargetSize(100, 60));   // never enlarges
        Assert.Equal((25, 15), new ImageResize.Percent(25).TargetSize(100, 60));
        Assert.Equal((10, 10), new ImageResize.Exact(10, 10).TargetSize(100, 60));
    }

    [Fact]
    public void RotatesClockwiseAndFlips()
    {
        var rotated = Sample().Transformed(90, false, false);
        Assert.Equal((2, 4), (rotated.Width, rotated.Height));
        Assert.Equal(255, Pixel(rotated, 0, 0).A);   // the red left half is now the top
        Assert.Equal(0, Pixel(rotated, 0, 3).A);
        var flipped = Sample().Transformed(0, true, false);
        Assert.Equal(0, Pixel(flipped, 0, 0).A);
        Assert.Equal(255, Pixel(flipped, 3, 0).A);
        var upside = Sample().Transformed(180, false, false);
        Assert.Equal(255, Pixel(upside, 3, 1).A);
    }

    [Fact]
    public void FlattensOntoWhite()
    {
        var flat = Sample().Flattened();
        Assert.False(flat.HasTransparency);
        Assert.Equal((0, 0, 255, 255), Pixel(flat, 0, 0));
        Assert.Equal((255, 255, 255, 255), Pixel(flat, 3, 0));
    }

    [Fact]
    public void ResizingKeepsColorsAtTransparentEdges()
    {
        var half = Sample().Resized(2, 1);
        Assert.Equal((0, 0, 255, 255), Pixel(half, 0, 0));   // no dark fringe from transparent neighbors
        Assert.Equal(0, Pixel(half, 1, 0).A);
        var big = Sample().Resized(8, 4);
        Assert.Equal((0, 0, 255, 255), Pixel(big, 0, 0));
    }

    [Fact]
    public void IconsAreSquareAndWrapPng()
    {
        var square = new PixelBuffer(10, 4).Squared(16);
        Assert.Equal((16, 16), (square.Width, square.Height));
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var ico = IcoWriter.Wrap(png, 256);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 1, 0, 0, 0 }, ico[..8]);
        Assert.Equal(22 + png.Length, ico.Length);
        Assert.Equal(png, ico[22..]);
    }

    [Fact]
    public void ImagePresetsLoad()
    {
        Assert.Contains("jpg", ImagePresets.Shared.Lossy);
        Assert.Contains("png", ImagePresets.Shared.Transparency);
        Assert.Equal(256, ImagePresets.Shared.IcoMaxSize);
        Assert.Equal(0.6, ImagePresets.Shared.QualityFor(new ImageOptions(PresetId: "small")));
        Assert.Equal(0.5, ImagePresets.Shared.QualityFor(new ImageOptions(Quality: 0.5)));
    }
}
