using System.Buffers.Binary;
using System.Text.Json;

namespace Potion.Core.Engines;

/// <summary>How to size an image. Fitting never enlarges.</summary>
public abstract record ImageResize
{
    public sealed record Original : ImageResize;
    /// <summary>Fit within the box, keeping proportions. Either side may be null (unconstrained).</summary>
    public sealed record Fit(int? Width, int? Height) : ImageResize;
    /// <summary>Scale both sides by a percentage (e.g. 50).</summary>
    public sealed record Percent(double Value) : ImageResize;
    /// <summary>Exactly this size (proportions may change).</summary>
    public sealed record Exact(int Width, int Height) : ImageResize;

    /// <summary>The output size for an upright source of <paramref name="width"/>×<paramref name="height"/> pixels.</summary>
    public (int Width, int Height) TargetSize(int width, int height)
    {
        static int Px(double v) => Math.Max(1, (int)Math.Round(v));
        switch (this)
        {
            case Fit fit:
                var scale = Math.Min(1, Math.Min((double)(fit.Width ?? width) / width, (double)(fit.Height ?? height) / height));
                return (Px(width * scale), Px(height * scale));
            case Percent p:
                var factor = Math.Clamp(p.Value, 1, 1000) / 100;
                return (Px(width * factor), Px(height * factor));
            case Exact e:
                return (Math.Max(1, e.Width), Math.Max(1, e.Height));
            default:
                return (width, height);
        }
    }
}

/// <summary>Settings for one image conversion.</summary>
/// <param name="PresetId">"high", "standard" or "small" (lossy formats only).</param>
/// <param name="Quality">0…1, overrides the preset.</param>
/// <param name="Rotation">Clockwise rotation: 0, 90, 180 or 270.</param>
public sealed record ImageOptions(string? PresetId = null, double? Quality = null, ImageResize? Resize = null, int Rotation = 0,
                                  bool FlipHorizontal = false, bool FlipVertical = false)
{
    public ImageResize ResizeOrOriginal => Resize ?? new ImageResize.Original();
}

/// <summary>Image quality presets and format capabilities, from presets.json (core/spec).</summary>
public sealed class ImagePresets
{
    public sealed record Preset(string Id, string Name, double Quality);

    private sealed record ImageSection(List<Preset> Presets, string DefaultPreset, List<string> Lossy, List<string> Transparency,
                                       int IcoMaxSize, double PdfDefaultDpi);
    private sealed record PresetFile(ImageSection Image);

    private static readonly Lazy<ImagePresets> SharedInstance = new(() => new ImagePresets(EnginePlatform.BundledPresetsJson()));
    public static ImagePresets Shared => SharedInstance.Value;

    public IReadOnlyList<Preset> Presets { get; }
    public string DefaultPresetId { get; }
    public IReadOnlySet<string> Lossy { get; }
    public IReadOnlySet<string> Transparency { get; }
    public int IcoMaxSize { get; }
    public double PdfDefaultDpi { get; }

    public ImagePresets(string json)
    {
        var image = (JsonSerializer.Deserialize<PresetFile>(json, EnginePlatform.Json) ?? throw new JsonException("empty presets.json")).Image;
        Presets = image.Presets; DefaultPresetId = image.DefaultPreset; Lossy = image.Lossy.ToHashSet();
        Transparency = image.Transparency.ToHashSet(); IcoMaxSize = image.IcoMaxSize; PdfDefaultDpi = image.PdfDefaultDpi;
    }

    public double QualityFor(ImageOptions options) =>
        options.Quality ?? (Presets.FirstOrDefault(p => p.Id == options.PresetId) ?? Presets.First(p => p.Id == DefaultPresetId)).Quality;

    /// <summary>Standard icon sizes; ICO files use the smallest that holds the picture.</summary>
    public static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256];
}

/// <summary>
/// An uncompressed image: 8-bit BGRA, straight (not premultiplied) alpha, rows top to bottom. The pixel work the
/// Windows image engine needs (resize, rotate, flip, flatten, square canvas) lives here so it is platform-neutral
/// and tested everywhere; only decoding and encoding use the OS.
/// </summary>
public sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    public PixelBuffer(int width, int height, byte[]? bgra = null)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "Images need at least one pixel.");
        Width = width; Height = height;
        Bgra = bgra ?? new byte[checked(width * height * 4)];
        if (Bgra.Length != width * height * 4) throw new ArgumentException("Pixel data doesn't match the size.", nameof(bgra));
    }

    public bool HasTransparency
    {
        get
        {
            for (var i = 3; i < Bgra.Length; i += 4) if (Bgra[i] != 255) return true;
            return false;
        }
    }

    /// <summary>Resamples to <paramref name="width"/>×<paramref name="height"/>: area averaging when shrinking (sharp,
    /// no aliasing), bilinear when enlarging. Colors are weighted by alpha so transparent edges don't darken.</summary>
    public PixelBuffer Resized(int width, int height)
    {
        if (width == Width && height == Height) return this;
        var output = new PixelBuffer(width, height);
        var sx = (double)Width / width;
        var sy = (double)Height / height;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double b = 0, g = 0, r = 0, a = 0, weight = 0;
                if (sx >= 1 && sy >= 1)
                {
                    // Area average over the source rectangle this pixel covers.
                    double x0 = x * sx, x1 = x0 + sx, y0 = y * sy, y1 = y0 + sy;
                    for (var iy = (int)y0; iy < Math.Min(Math.Ceiling(y1), Height); iy++)
                    {
                        var wy = Math.Min(iy + 1, y1) - Math.Max(iy, y0);
                        for (var ix = (int)x0; ix < Math.Min(Math.Ceiling(x1), Width); ix++)
                        {
                            var w = wy * (Math.Min(ix + 1, x1) - Math.Max(ix, x0));
                            Accumulate(ix, iy, w, ref b, ref g, ref r, ref a, ref weight);
                        }
                    }
                }
                else
                {
                    // Bilinear between the four nearest source pixels.
                    var fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, Width - 1);
                    var fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, Height - 1);
                    int ix = (int)fx, iy = (int)fy, ix1 = Math.Min(ix + 1, Width - 1), iy1 = Math.Min(iy + 1, Height - 1);
                    double tx = fx - ix, ty = fy - iy;
                    Accumulate(ix, iy, (1 - tx) * (1 - ty), ref b, ref g, ref r, ref a, ref weight);
                    Accumulate(ix1, iy, tx * (1 - ty), ref b, ref g, ref r, ref a, ref weight);
                    Accumulate(ix, iy1, (1 - tx) * ty, ref b, ref g, ref r, ref a, ref weight);
                    Accumulate(ix1, iy1, tx * ty, ref b, ref g, ref r, ref a, ref weight);
                }
                var o = (y * width + x) * 4;
                var alpha = weight > 0 ? a / weight : 0;
                output.Bgra[o + 3] = Byte(alpha);
                if (a > 0)
                {
                    output.Bgra[o] = Byte(b / a);
                    output.Bgra[o + 1] = Byte(g / a);
                    output.Bgra[o + 2] = Byte(r / a);
                }
            }
        }
        return output;
    }

    private void Accumulate(int x, int y, double w, ref double b, ref double g, ref double r, ref double a, ref double weight)
    {
        var i = (y * Width + x) * 4;
        var alpha = Bgra[i + 3] * w;
        b += Bgra[i] * alpha; g += Bgra[i + 1] * alpha; r += Bgra[i + 2] * alpha;
        a += alpha; weight += w;
    }

    private static byte Byte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>Rotates clockwise by 0/90/180/270 degrees, then flips.</summary>
    public PixelBuffer Transformed(int rotation, bool flipHorizontal, bool flipVertical)
    {
        rotation = ((rotation % 360) + 360) % 360;
        if (rotation == 0 && !flipHorizontal && !flipVertical) return this;
        var sideways = rotation is 90 or 270;
        int w = sideways ? Height : Width, h = sideways ? Width : Height;
        var output = new PixelBuffer(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var dx = flipHorizontal ? w - 1 - x : x;
                var dy = flipVertical ? h - 1 - y : y;
                // Source pixel for output (dx, dy) under a clockwise rotation.
                var (sx, sy) = rotation switch
                {
                    90 => (dy, Height - 1 - dx),
                    180 => (Width - 1 - dx, Height - 1 - dy),
                    270 => (Width - 1 - dy, dx),
                    _ => (dx, dy),
                };
                Buffer.BlockCopy(Bgra, (sy * Width + sx) * 4, output.Bgra, (y * w + x) * 4, 4);
            }
        }
        return output;
    }

    /// <summary>Composites onto white, for formats without transparency.</summary>
    public PixelBuffer Flattened()
    {
        if (!HasTransparency) return this;
        var output = new PixelBuffer(Width, Height);
        for (var i = 0; i < Bgra.Length; i += 4)
        {
            var a = Bgra[i + 3] / 255.0;
            for (var c = 0; c < 3; c++) output.Bgra[i + c] = Byte(Bgra[i + c] * a + 255 * (1 - a));
            output.Bgra[i + 3] = 255;
        }
        return output;
    }

    /// <summary>Centers the picture on a transparent <paramref name="side"/>×<paramref name="side"/> canvas.</summary>
    public PixelBuffer Squared(int side)
    {
        if (Width == side && Height == side) return this;
        var output = new PixelBuffer(side, side);
        int ox = (side - Width) / 2, oy = (side - Height) / 2;
        for (var y = 0; y < Height; y++)
            Buffer.BlockCopy(Bgra, y * Width * 4, output.Bgra, ((y + oy) * side + ox) * 4, Width * 4);
        return output;
    }
}

/// <summary>Writes a single-image .ico file holding a PNG (the format Windows Vista and later read natively).</summary>
public static class IcoWriter
{
    public static byte[] Wrap(byte[] png, int side)
    {
        if (side is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(side));
        var ico = new byte[6 + 16 + png.Length];
        var span = ico.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);            // type: icon
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);            // one image
        span[6] = (byte)(side == 256 ? 0 : side);                          // 0 means 256
        span[7] = (byte)(side == 256 ? 0 : side);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 1);           // planes
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 32);          // bits per pixel
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..], (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..], 22);          // data offset
        png.CopyTo(span[22..]);
        return ico;
    }
}
