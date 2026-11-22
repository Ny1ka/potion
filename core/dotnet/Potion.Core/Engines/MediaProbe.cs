using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Potion.Core.Engines;

public enum StreamKind { Audio, Video, Subtitle, Data, Attachment, Unknown }

/// <summary>One stream as <c>ffprobe</c> reports it.</summary>
/// <param name="Width">Display width, after applying any rotation flag (portrait phone videos).</param>
/// <param name="IsAttachedPicture">Cover art embedded as a video stream (MP3/M4A/FLAC artwork).</param>
public sealed record MediaStream(
    int Index, StreamKind Kind, string Codec, string CodecTag, int? SampleRate, int? Channels, int? BitsPerSample,
    long? BitRate, int? Width, int? Height, double? FrameRate, string? PixelFormat, bool IsAttachedPicture, double? Duration);

/// <summary>What <c>ffprobe</c> reports about a media file.</summary>
public sealed record MediaInfo(string FormatName, double? Duration, long? BitRate, IReadOnlyList<MediaStream> Streams)
{
    public IEnumerable<MediaStream> AudioStreams => Streams.Where(s => s.Kind == StreamKind.Audio);
    /// <summary>Real video streams (cover art excluded).</summary>
    public IEnumerable<MediaStream> VideoStreams => Streams.Where(s => s.Kind == StreamKind.Video && !s.IsAttachedPicture);
    public MediaStream? AttachedPicture => Streams.FirstOrDefault(s => s.IsAttachedPicture);

    private static readonly HashSet<string> ProtectedTags = ["drms", "drmi", "enca", "encv", "encs", "enct"];

    /// <summary>Streams flagged as encrypted (FairPlay and Common Encryption sample entries).</summary>
    public bool IsCopyProtected => Streams.Any(s => ProtectedTags.Contains(s.CodecTag.ToLowerInvariant()));
}

public static partial class MediaProbe
{
    public static async Task<MediaInfo> ProbeAsync(string path, FFmpegTools tools, CancellationToken token = default)
    {
        var result = await ProcessRunner.RunAsync(tools.Ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", Path.GetFullPath(path)],
            token: token).ConfigureAwait(false);
        if (LooksCopyProtected(result.Stderr)) throw ConversionException.CopyProtected();
        if (result.ExitCode != 0) throw ConversionException.Unreadable(Path.GetFileName(path));
        try { return Parse(result.Stdout); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw ConversionException.Unreadable(Path.GetFileName(path));
        }
    }

    [GeneratedRegex(@"\bdrm\b")]
    private static partial Regex DrmWord();

    public static bool LooksCopyProtected(string ffmpegOutput)
    {
        var text = ffmpegOutput.ToLowerInvariant();
        return DrmWord().IsMatch(text) || text.Contains("encrypted") || text.Contains("decryption key");
    }

    /// <summary>"30000/1001" → 29.97. Null for "0/0" or malformed values.</summary>
    public static double? Rate(string? fraction)
    {
        var parts = fraction?.Split('/');
        if (parts is not { Length: 2 }) return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || n <= 0 || d <= 0) return null;
        return n / d;
    }

    public static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var format = root.GetProperty("format");
        var streams = new List<MediaStream>();
        if (root.TryGetProperty("streams", out var list))
        {
            foreach (var s in list.EnumerateArray())
            {
                var rawBits = Int(Str(s, "bits_per_raw_sample"));
                var bits = new[] { rawBits, IntProp(s, "bits_per_sample") }.FirstOrDefault(b => b > 0);
                // Rotation comes from the display matrix (side data) or an older "rotate" tag.
                double? rotation = null;
                if (s.TryGetProperty("side_data_list", out var side))
                    foreach (var item in side.EnumerateArray())
                        if (item.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Number) { rotation = r.GetDouble(); break; }
                if (rotation is null && s.TryGetProperty("tags", out var tags) && Str(tags, "rotate") is { } rotateTag)
                    rotation = Double(rotateTag);
                var sideways = (int)Math.Round(Math.Abs(rotation ?? 0)) % 180 == 90;
                var width = IntProp(s, "width");
                var height = IntProp(s, "height");
                var attached = s.TryGetProperty("disposition", out var disposition) && IntProp(disposition, "attached_pic") == 1;

                streams.Add(new MediaStream(
                    Index: s.GetProperty("index").GetInt32(),
                    Kind: (Str(s, "codec_type") ?? "") switch
                    {
                        "audio" => StreamKind.Audio, "video" => StreamKind.Video, "subtitle" => StreamKind.Subtitle,
                        "data" => StreamKind.Data, "attachment" => StreamKind.Attachment, _ => StreamKind.Unknown,
                    },
                    Codec: Str(s, "codec_name") ?? "",
                    CodecTag: Str(s, "codec_tag_string") ?? "",
                    SampleRate: Int(Str(s, "sample_rate")),
                    Channels: IntProp(s, "channels"),
                    BitsPerSample: bits,
                    BitRate: Long(Str(s, "bit_rate")),
                    Width: sideways ? height : width,
                    Height: sideways ? width : height,
                    FrameRate: Rate(Str(s, "avg_frame_rate")) ?? Rate(Str(s, "r_frame_rate")),
                    PixelFormat: Str(s, "pix_fmt"),
                    IsAttachedPicture: attached,
                    Duration: Double(Str(s, "duration"))));
            }
        }
        return new MediaInfo(Str(format, "format_name") ?? "", Double(Str(format, "duration")), Long(Str(format, "bit_rate")), streams);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? IntProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    private static long? Long(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;
    private static double? Double(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
