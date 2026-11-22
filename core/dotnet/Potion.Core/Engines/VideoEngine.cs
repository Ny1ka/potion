using System.Globalization;
using Potion.Core.Formats;

namespace Potion.Core.Engines;

/// <summary>Settings for one video conversion. Unset values keep the source or come from the default preset.</summary>
/// <param name="PresetId">"original", "high", "standard" or "small". "original" copies streams when nothing else changes.</param>
/// <param name="MaxHeight">Maximum output height (2160, 1080, 720…). Never upscales.</param>
/// <param name="FrameRate">Output frame rate. Only lowers the source rate.</param>
/// <param name="Codec">One of the container's video codecs (e.g. "hevc" for MP4). Null uses the container's default.</param>
/// <param name="BitrateKbps">Overrides the preset's computed bitrate.</param>
/// <param name="AspectRatio">"16:9", "9:16", "1:1" or "4:3". The picture is fitted and letterboxed, never stretched.</param>
public sealed record VideoOptions(string? PresetId = null, int? MaxHeight = null, double? FrameRate = null, string? Codec = null,
                                  int? BitrateKbps = null, string? AspectRatio = null);

/// <summary>
/// Converts between video formats, turns video into animated GIFs and GIFs into video, using the bundled FFmpeg.
/// Handles <c>TranscodeVideo</c>, <c>VideoToGif</c> and <c>GifToVideo</c>. Same behavior as the Swift <c>VideoEngine</c>.
/// </summary>
public sealed class VideoEngine(FFmpegTools tools, VideoPresets? presets = null, ConversionMatrix? matrix = null,
                                string? platform = null)
{
    private readonly VideoPresets _presets = presets ?? VideoPresets.Shared;
    private readonly ConversionMatrix _matrix = matrix ?? ConversionMatrix.Shared;
    private readonly string _platform = platform ?? EnginePlatform.Current;
    private static readonly string[] AppleContainers = ["mp4", "mov", "m4v"];

    public FFmpegTools Tools => tools;

    public async Task<string> ConvertAsync(string input, FileFormat inputFormat, FileFormat outputFormat, string outputDirectory,
        VideoOptions? options = null, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var conversion = _matrix.Conversion(inputFormat.Id, outputFormat.Id);
        if (conversion is null || conversion.Operation is not (ConversionOperation.TranscodeVideo or ConversionOperation.VideoToGif
                                                               or ConversionOperation.GifToVideo))
            throw ConversionException.Unsupported(inputFormat.Name, outputFormat.Name);
        OutputFiles.CheckWritable(outputDirectory);
        options ??= new VideoOptions();

        var info = await MediaProbe.ProbeAsync(input, tools, token).ConfigureAwait(false);
        if (info.IsCopyProtected) throw ConversionException.CopyProtected();
        var video = info.VideoStreams.FirstOrDefault() ?? throw ConversionException.NoVideo(Path.GetFileName(input));
        var keepsAudio = conversion.Operation == ConversionOperation.TranscodeVideo && info.AudioStreams.Any();

        return await FFmpegConversion.RunAsync(tools, input, outputDirectory, outputFormat.PreferredExtension,
            info.Duration ?? video.Duration, progress,
            temporary => conversion.Operation == ConversionOperation.VideoToGif
                ? GifArguments(input, info, temporary, options)
                : Arguments(input, info, temporary, outputFormat, options),
            output =>
            {
                var v = output.VideoStreams.FirstOrDefault() ?? output.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
                if (v is null) return false;
                if (keepsAudio && !output.AudioStreams.Any()) return false;
                return FFmpegConversion.HasDuration(output, v) || outputFormat.Id == "gif";
            }, token).ConfigureAwait(false);
    }

    // Video → video (also GIF → video)

    public IReadOnlyList<string> Arguments(string input, MediaInfo info, string output, FileFormat outputFormat, VideoOptions options)
    {
        if (!_presets.Formats.TryGetValue(outputFormat.Id, out var container)) throw ConversionException.Unsupported("Video", outputFormat.Name);
        var video = info.VideoStreams.FirstOrDefault() ?? throw ConversionException.NoVideo(Path.GetFileName(input));
        var audio = info.AudioStreams.FirstOrDefault();
        var preset = _presets.PresetById(options.PresetId);
        var codecId = options.Codec is { } c && container.VideoCodecs.Contains(c) ? c : container.VideoCodecs[0];
        if (!_presets.Codecs.TryGetValue(codecId, out var codec) || codec.EncoderFor(_platform) is not { } encoder)
            throw ConversionException.Unsupported("Video", outputFormat.Name);

        var geometry = new Geometry(video, options.MaxHeight, options.AspectRatio);
        var fps = TargetFrameRate(video.FrameRate, options.FrameRate, codec.StandardFrameRates == true);
        var copyVideo = preset.Id == "original" && !geometry.ChangesPicture && fps is null
            && container.CanCopyVideo(video.Codec) && (options.Codec is null || options.Codec == video.Codec) && video.Codec != "gif";

        var args = FFmpegConversion.CommonArguments(input);
        args.AddRange(["-map", $"0:{video.Index}"]);
        if (audio is not null) args.AddRange(["-map", $"0:{audio.Index}"]);
        args.AddRange(["-sn", "-dn", "-map_metadata", "0"]);

        if (copyVideo)
        {
            args.AddRange(["-c:v", "copy"]);
            if (video.Codec == "hevc" && _presets.Codecs.TryGetValue("hevc", out var hevc) && hevc.Tag is { } hevcTag
                && AppleContainers.Contains(outputFormat.Id))
                args.AddRange(["-tag:v", hevcTag]);
        }
        else
        {
            var filters = new List<string>();
            if (fps is { } f) filters.Add($"fps={f.Text}");
            if (geometry.Filter is { } scale) filters.Add(scale);
            if (filters.Count > 0) args.AddRange(["-vf", string.Join(',', filters)]);
            args.AddRange(["-c:v", encoder, "-pix_fmt", "yuv420p"]);
            if (encoder.EndsWith("_videotoolbox", StringComparison.Ordinal)) args.AddRange(["-allow_sw", "1"]);   // software fallback
            if (encoder == "libvpx-vp9") args.AddRange(["-row-mt", "1", "-deadline", "good", "-cpu-used", "4"]);
            if (codec.Tag is { } tag && AppleContainers.Contains(outputFormat.Id)) args.AddRange(["-tag:v", tag]);
            var kbps = options.BitrateKbps ?? BitrateKbps(geometry, fps?.Value ?? video.FrameRate ?? 30, preset, video, codec.Efficiency);
            args.AddRange(["-b:v", $"{kbps}k", "-maxrate", $"{kbps * 3 / 2}k", "-bufsize", $"{kbps * 2}k"]);
        }

        if (audio is not null)
        {
            if (container.CanCopyAudio(audio.Codec))
            {
                args.AddRange(["-c:a", "copy"]);
            }
            else if (_presets.AudioCodecs.TryGetValue(container.AudioCodec, out var audioCodec) && audioCodec.EncoderFor(_platform) is { } audioEncoder)
            {
                args.AddRange(["-c:a", audioEncoder, "-b:a", $"{audioCodec.BitrateKbps}k"]);
                if (container.AudioCodec == "opus") args.AddRange(["-ar", "48000"]);
                // These encoders top out at 48 kHz.
                if (container.AudioCodec is "mp2" or "aac" && (audio.SampleRate ?? 48000) > 48000) args.AddRange(["-ar", "48000"]);
            }
        }
        if (container.Faststart == true) args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-f", container.Muxer, Path.GetFullPath(output)]);
        return args;
    }

    /// <summary>The output picture size and the scale/pad filter that produces it.</summary>
    public sealed class Geometry
    {
        public int Width { get; }
        public int Height { get; }
        public string? Filter { get; }
        public bool ChangesPicture => Filter is not null;

        public Geometry(MediaStream source, int? maxHeight, string? aspectRatio)
        {
            int srcW = source.Width ?? 0, srcH = source.Height ?? 0;
            var outH = Even(Math.Min(maxHeight ?? srcH, srcH));
            if (aspectRatio is not null && Ratio(aspectRatio) is { } ratio)
            {
                var outW = Even((int)Math.Round(outH * ratio));
                Width = outW; Height = outH;
                Filter = $"scale={outW}:{outH}:force_original_aspect_ratio=decrease,pad={outW}:{outH}:(ow-iw)/2:(oh-ih)/2,setsar=1";
            }
            else if (outH != srcH)
            {
                Height = outH;
                Width = srcH > 0 ? Even(srcW * outH / srcH) : srcW;
                Filter = $"scale=-2:{outH}";
            }
            else if (srcW % 2 != 0 || srcH % 2 != 0)
            {
                Width = Even(srcW); Height = Even(srcH);
                Filter = "scale=trunc(iw/2)*2:trunc(ih/2)*2";   // most encoders need even dimensions
            }
            else
            {
                Width = srcW; Height = srcH; Filter = null;
            }
        }

        public static int Even(int n) => Math.Max(2, n - n % 2);

        public static double? Ratio(string text)
        {
            var parts = text.Split(':');
            if (parts.Length != 2) return null;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) || a <= 0 || b <= 0) return null;
            return a / b;
        }
    }

    /// <summary>Frame rate to force, or null to keep the source. Requests only lower the rate; MPEG-2 snaps to broadcast rates.</summary>
    public static (double Value, string Text)? TargetFrameRate(double? source, double? requested, bool standardOnly)
    {
        double? target = null;
        if (requested is > 0 && requested < (source ?? double.PositiveInfinity) - 0.01) target = requested;
        if (standardOnly)
        {
            var wanted = target ?? source ?? 25;
            var snapped = VideoPresets.StandardRates.OrderBy(r => Math.Abs(r.Value - wanted)).First();
            if (source is { } s && Math.Abs(snapped.Value - s) < 0.01 && target is null) return null;
            return snapped;
        }
        if (target is not { } t) return null;
        var text = Math.Round(t) == t ? ((int)t).ToString(CultureInfo.InvariantCulture) : t.ToString("0.000", CultureInfo.InvariantCulture);
        return (t, text);
    }

    /// <summary>Bitrate from picture size × frame rate × bits per pixel, adjusted for the codec. "Original quality" uses
    /// the source bitrate when known.</summary>
    public int BitrateKbps(Geometry geometry, double fps, VideoPresets.Preset preset, MediaStream source, double efficiency)
    {
        double raw;
        if (preset.BitsPerPixel is { } bpp) raw = geometry.Width * geometry.Height * fps * bpp * efficiency / 1000;
        else if (source.BitRate is > 0) raw = source.BitRate.Value / 1000.0 * Math.Max(efficiency, 1);
        else raw = geometry.Width * geometry.Height * fps * 0.12 * efficiency / 1000;
        return Math.Clamp((int)Math.Round(raw), _presets.MinBitrateKbps, _presets.MaxBitrateKbps);
    }

    // Video → GIF

    public IReadOnlyList<string> GifArguments(string input, MediaInfo info, string output, VideoOptions options)
    {
        var video = info.VideoStreams.FirstOrDefault() ?? throw ConversionException.NoVideo(Path.GetFileName(input));
        var settings = _presets.Gif.TryGetValue(options.PresetId ?? _presets.DefaultPresetId, out var s) ? s : _presets.Gif[_presets.DefaultPresetId];
        var width = Math.Min(video.Width ?? settings.MaxWidth, settings.MaxWidth);
        var fps = Math.Min(video.FrameRate ?? settings.Fps, settings.Fps);
        var fpsText = Math.Round(fps) == fps ? ((int)fps).ToString(CultureInfo.InvariantCulture) : fps.ToString("0.000", CultureInfo.InvariantCulture);
        // Two passes in one graph: build an optimal 256-color palette, then map frames onto it.
        var graph = $"fps={fpsText},scale={width}:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5";
        var args = FFmpegConversion.CommonArguments(input);
        args.AddRange(["-map", $"0:{video.Index}", "-an", "-sn", "-dn", "-vf", graph, "-loop", "0", "-f", "gif", Path.GetFullPath(output)]);
        return args;
    }
}
