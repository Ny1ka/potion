using System.Globalization;
using Potion.Core.Engines;
using Potion.Core.Formats;

namespace Potion.Core.App;

/// <summary>Which Advanced controls a conversion gets (06-components.md, "Format picker"). Same as macOS.</summary>
public abstract record OptionsKind
{
    public sealed record Video(string Container) : OptionsKind;
    public sealed record Gif : OptionsKind;
    public sealed record Audio(string Format, bool Lossless) : OptionsKind;
    public sealed record Image(bool Lossy) : OptionsKind;
    public sealed record ImagesToPdf : OptionsKind;
    public sealed record PdfToImage(bool Lossy) : OptionsKind;
    public sealed record PdfPages : OptionsKind;
    public sealed record None : OptionsKind;

    public bool UsesPages => this is PdfToImage or PdfPages;

    public static OptionsKind Of(ConversionOperation operation, FileFormat output) => operation switch
    {
        ConversionOperation.TranscodeVideo or ConversionOperation.GifToVideo => new Video(output.Id),
        ConversionOperation.VideoToGif => new Gif(),
        ConversionOperation.TranscodeAudio or ConversionOperation.ExtractAudio =>
            new Audio(output.Id, AudioPresets.Shared.Formats.TryGetValue(output.Id, out var a) && a.IsLossless),
        ConversionOperation.ConvertImage => new Image(ImagePresets.Shared.Lossy.Contains(output.Id)),
        ConversionOperation.ImageToPdf => new ImagesToPdf(),
        ConversionOperation.PdfToImage => new PdfToImage(ImagePresets.Shared.Lossy.Contains(output.Id)),
        ConversionOperation.PdfToDocument => new PdfPages(),
        _ => new None(),
    };

    /// <summary>One kind for several rows going to one output, or None when they'd need different controls.</summary>
    public static OptionsKind For(IEnumerable<QueueItem> items, FileFormat output, ConversionMatrix? matrix = null)
    {
        matrix ??= ConversionMatrix.Shared;
        var operations = items.Select(i => matrix.Conversion(i.Format.Id, output.Id)?.Operation).Distinct().ToList();
        return operations is [{ } op] ? Of(op, output) : new None();
    }
}

/// <summary>Display names and the one-line summary shown above the Advanced controls (same wording as macOS).</summary>
public static class OptionsText
{
    public static string Resolution(int height) => height == 2160 ? "4K (2160p)" : $"{height}p";
    public static string FrameRate(double fps) => $"{fps.ToString("0.###", CultureInfo.CurrentCulture)} fps";
    public static string SampleRate(int hz) => $"{(hz / 1000.0).ToString("0.###", CultureInfo.CurrentCulture)} kHz";

    /// <summary>Limitation notes for these rows going to <paramref name="output"/>, each once.</summary>
    public static IReadOnlyList<string> Notes(IEnumerable<QueueItem> items, FileFormat output, ConversionMatrix? matrix = null)
    {
        matrix ??= ConversionMatrix.Shared;
        return items.SelectMany(i => matrix.Conversion(i.Format.Id, output.Id)?.Notes ?? [])
            .DistinctBy(n => n.Id).Select(n => n.Text).ToList();
    }

    /// <summary>"MP4 · HEVC (H.265) · 1080p · 30 fps · standard".</summary>
    public static string Summary(OptionsKind kind, FileFormat output, ConversionSettings settings, string pagesText)
    {
        var parts = new List<string> { output.Name };
        var video = settings.Video ?? new VideoOptions();
        var audio = settings.Audio ?? new AudioOptions();
        var image = settings.Image ?? new ImageOptions();
        var pdf = settings.Pdf ?? new PdfOptions();
        switch (kind)
        {
            case OptionsKind.Video v:
                var codecs = VideoPresets.Shared.Formats.TryGetValue(v.Container, out var c) ? c.VideoCodecs : [];
                if ((video.Codec ?? codecs.FirstOrDefault()) is { } codec && VideoPresets.Shared.Codecs.TryGetValue(codec, out var info)) parts.Add(info.Name);
                parts.Add(video.MaxHeight is { } h ? Resolution(h) : "original size");
                if (video.FrameRate is { } fps) parts.Add(FrameRate(fps));
                parts.Add(video.BitrateKbps is { } kbps ? $"{kbps.ToString("N0", CultureInfo.CurrentCulture)} kbps" : VideoPresets.Shared.PresetById(video.PresetId).Name.ToLowerInvariant());
                break;
            case OptionsKind.Gif:
                parts.Add(VideoPresets.Shared.PresetById(video.PresetId).Name.ToLowerInvariant());
                break;
            case OptionsKind.Audio a:
                parts.Add(a.Lossless ? "lossless" : $"{audio.BitrateKbps ?? AudioPresets.Shared.PresetById(audio.PresetId).BitrateKbps} kbps");
                if (audio.SampleRate is { } rate) parts.Add(SampleRate(rate));
                break;
            case OptionsKind.Image or OptionsKind.ImagesToPdf:
                parts.Add(image.ResizeOrOriginal switch
                {
                    ImageResize.Percent p => $"{p.Value:0}% size",
                    ImageResize.Fit f => $"fit within {f.Width?.ToString(CultureInfo.CurrentCulture) ?? "any"} × {f.Height?.ToString(CultureInfo.CurrentCulture) ?? "any"}",
                    ImageResize.Exact e => $"{e.Width} × {e.Height}",
                    _ => "original size",
                });
                if (image.Rotation != 0) parts.Add($"rotated {image.Rotation}°");
                break;
            case OptionsKind.PdfToImage:
                parts.Add($"{pdf.Dpi ?? PdfPresets.Shared.DefaultDpi:0} dpi");
                parts.Add(Pages(pagesText));
                break;
            case OptionsKind.PdfPages:
                parts.Add(Pages(pagesText));
                break;
        }
        return string.Join(" · ", parts);
    }

    private static string Pages(string text) => text.Trim().Length == 0 ? "all pages" : $"pages {text.Trim()}";

    /// <summary>The caption under the pages field: a hint, or what's wrong.</summary>
    public static (string Text, bool IsError) PagesHint(string text, int pageCount)
    {
        if (text.Trim().Length == 0) return (pageCount == 1 ? "This PDF has 1 page" : $"1–{pageCount}, for example “1–3, 5”", false);
        try { PageSelection.Pages(text, pageCount); return (pageCount == 1 ? "This PDF has 1 page" : $"1–{pageCount}, for example “1–3, 5”", false); }
        catch (ConversionException) { return (pageCount == 1 ? "This PDF has only 1 page" : $"Use pages 1–{pageCount}, for example “1–3, 5”", true); }
    }
}

/// <summary>Settings › Output: the formats offered as a file type's default (same list as macOS).</summary>
public static class DefaultFormats
{
    /// <summary>
    /// Every format files of this category can become (union over its formats), the same category first, so a remembered
    /// choice like "video → MP3" always appears in the list.
    /// </summary>
    public static IReadOnlyList<FileFormat> For(FormatCategory category, FormatRegistry? registry = null, ConversionMatrix? matrix = null)
    {
        registry ??= FormatRegistry.Shared;
        matrix ??= ConversionMatrix.Shared;
        var seen = new HashSet<string>();
        var result = new List<FileFormat>();
        foreach (var format in registry.Formats.Where(f => f.Category == category && f.IsAvailableOn(ConversionMatrix.CurrentPlatform)))
            foreach (var option in matrix.Outputs(format.Id))
                if (seen.Add(option.Output.Id)) result.Add(option.Output);
        return [.. result.Where(f => f.Category == category), .. result.Where(f => f.Category != category)];
    }
}
