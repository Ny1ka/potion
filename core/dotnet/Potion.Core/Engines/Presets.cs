using System.Text.Json;
using System.Text.Json.Serialization;

namespace Potion.Core.Engines;

/// <summary>Which column of presets.json's per-platform encoder tables applies ("windows" or "macos").</summary>
public static class EnginePlatform
{
    public static string Current => Formats.ConversionMatrix.CurrentPlatform;

    internal static string? Pick(IReadOnlyDictionary<string, string> encoders, string platform) =>
        encoders.TryGetValue(platform, out var e) ? e : encoders.TryGetValue("default", out var d) ? d : null;

    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    internal static string BundledPresetsJson()
    {
        using var stream = typeof(EnginePlatform).Assembly.GetManifestResourceStream("Potion.Core.presets.json")
            ?? throw new InvalidOperationException("presets.json is missing from Potion.Core");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Audio quality presets and per-format encoder settings, from presets.json (core/spec).</summary>
public sealed class AudioPresets
{
    public sealed record Preset(string Id, string Name, int BitrateKbps);

    public sealed record FormatSettings(
        string Muxer, IReadOnlyDictionary<string, string> Encoder, string? HighBitDepthEncoder,
        IReadOnlyList<int>? Bitrates, IReadOnlyList<int>? SampleRates, bool? Lossless, bool? Artwork)
    {
        public string? EncoderFor(string platform) => EnginePlatform.Pick(Encoder, platform);
        public bool IsLossless => Lossless ?? false;
        public bool SupportsArtwork => Artwork ?? false;
    }

    private sealed record AudioSection(List<Preset> Presets, string DefaultPreset, List<int> SampleRates,
                                       Dictionary<string, FormatSettings> Formats);
    private sealed record PresetFile(int Version, AudioSection Audio);

    private static readonly Lazy<AudioPresets> SharedInstance = new(() => new AudioPresets(EnginePlatform.BundledPresetsJson()));
    public static AudioPresets Shared => SharedInstance.Value;

    public IReadOnlyList<Preset> Presets { get; }
    public string DefaultPresetId { get; }
    public IReadOnlyList<int> SampleRates { get; }
    public IReadOnlyDictionary<string, FormatSettings> Formats { get; }

    public AudioPresets(string json)
    {
        var file = JsonSerializer.Deserialize<PresetFile>(json, EnginePlatform.Json) ?? throw new JsonException("empty presets.json");
        if (file.Version != 1) throw new JsonException("Unsupported presets.json version");
        if (file.Audio.Presets.All(p => p.Id != file.Audio.DefaultPreset)) throw new JsonException("defaultPreset not in presets");
        Presets = file.Audio.Presets;
        DefaultPresetId = file.Audio.DefaultPreset;
        SampleRates = file.Audio.SampleRates;
        Formats = file.Audio.Formats;
    }

    public Preset PresetById(string? id) => Presets.FirstOrDefault(p => p.Id == id) ?? Presets.First(p => p.Id == DefaultPresetId);

    /// <summary>The allowed value closest to <paramref name="requested"/> (ties go to the lower value).</summary>
    public static int Nearest(int requested, IReadOnlyList<int> allowed) =>
        allowed.Count == 0 ? requested
            : allowed.OrderBy(a => Math.Abs(a - requested)).ThenBy(a => a).First();
}

/// <summary>Video quality presets, codecs and per-container settings, from presets.json (core/spec).</summary>
public sealed class VideoPresets
{
    /// <param name="BitsPerPixel">Bits per pixel per frame used to size the bitrate. Null for "Original quality".</param>
    public sealed record Preset(string Id, string Name, double? BitsPerPixel);

    /// <param name="Efficiency">Bitrate multiplier relative to H.264 for the same quality.</param>
    /// <param name="Tag">Container tag, e.g. "hvc1" so HEVC in MP4/MOV plays in Apple apps.</param>
    public sealed record Codec(string Name, IReadOnlyDictionary<string, string> Encoder, double Efficiency, string? Tag,
                               bool? StandardFrameRates)
    {
        public string? EncoderFor(string platform) => EnginePlatform.Pick(Encoder, platform);
    }

    public sealed record AudioCodec(IReadOnlyDictionary<string, string> Encoder, int BitrateKbps)
    {
        public string? EncoderFor(string platform) => EnginePlatform.Pick(Encoder, platform);
    }

    public sealed record Container(string Muxer, IReadOnlyList<string> VideoCodecs, string AudioCodec,
                                   IReadOnlyList<string> CopyVideo, IReadOnlyList<string> CopyAudio, bool? Faststart)
    {
        public bool CanCopyVideo(string codec) => CopyVideo.Contains("*") || CopyVideo.Contains(codec);
        public bool CanCopyAudio(string codec) => CopyAudio.Contains("*") || CopyAudio.Contains(codec);
    }

    public sealed record GifSettings(double Fps, int MaxWidth);

    private sealed record VideoSection(List<Preset> Presets, string DefaultPreset, List<int> Resolutions, List<double> FrameRates,
        List<string> AspectRatios, int MinBitrateKbps, int MaxBitrateKbps, Dictionary<string, Codec> Codecs,
        Dictionary<string, AudioCodec> AudioCodecs, Dictionary<string, Container> Formats, Dictionary<string, GifSettings> Gif);
    private sealed record PresetFile(VideoSection Video);

    private static readonly Lazy<VideoPresets> SharedInstance = new(() => new VideoPresets(EnginePlatform.BundledPresetsJson()));
    public static VideoPresets Shared => SharedInstance.Value;

    public IReadOnlyList<Preset> Presets { get; }
    public string DefaultPresetId { get; }
    public IReadOnlyList<int> Resolutions { get; }
    public IReadOnlyList<double> FrameRates { get; }
    public IReadOnlyList<string> AspectRatios { get; }
    public int MinBitrateKbps { get; }
    public int MaxBitrateKbps { get; }
    public IReadOnlyDictionary<string, Codec> Codecs { get; }
    public IReadOnlyDictionary<string, AudioCodec> AudioCodecs { get; }
    public IReadOnlyDictionary<string, Container> Formats { get; }
    public IReadOnlyDictionary<string, GifSettings> Gif { get; }

    public VideoPresets(string json)
    {
        var v = (JsonSerializer.Deserialize<PresetFile>(json, EnginePlatform.Json) ?? throw new JsonException("empty presets.json")).Video;
        foreach (var (id, container) in v.Formats)
        {
            foreach (var codec in container.VideoCodecs)
                if (!v.Codecs.ContainsKey(codec)) throw new JsonException($"{id} lists unknown codec {codec}");
            if (!v.AudioCodecs.ContainsKey(container.AudioCodec)) throw new JsonException($"{id} lists unknown audio codec");
        }
        Presets = v.Presets; DefaultPresetId = v.DefaultPreset; Resolutions = v.Resolutions; FrameRates = v.FrameRates;
        AspectRatios = v.AspectRatios; MinBitrateKbps = v.MinBitrateKbps; MaxBitrateKbps = v.MaxBitrateKbps;
        Codecs = v.Codecs; AudioCodecs = v.AudioCodecs; Formats = v.Formats; Gif = v.Gif;
    }

    public Preset PresetById(string? id) => Presets.FirstOrDefault(p => p.Id == id) ?? Presets.First(p => p.Id == DefaultPresetId);

    /// <summary>Broadcast frame rates MPEG-2 accepts, as FFmpeg rationals.</summary>
    internal static readonly (double Value, string Text)[] StandardRates =
    [
        (24000.0 / 1001, "24000/1001"), (24, "24"), (25, "25"), (30000.0 / 1001, "30000/1001"),
        (30, "30"), (50, "50"), (60000.0 / 1001, "60000/1001"), (60, "60"),
    ];
}
