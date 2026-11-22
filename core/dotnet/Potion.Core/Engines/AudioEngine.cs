using Potion.Core.Formats;

namespace Potion.Core.Engines;

/// <summary>Settings for one audio conversion. Unset values come from the default preset.</summary>
/// <param name="PresetId">One of the presets in presets.json ("high", "standard", "small").</param>
/// <param name="BitrateKbps">Overrides the preset. Snapped to the nearest bitrate the output format allows.</param>
/// <param name="SampleRate">Snapped to the nearest rate the format allows. Null keeps the source rate when allowed.</param>
public sealed record AudioOptions(string? PresetId = null, int? BitrateKbps = null, int? SampleRate = null);

/// <summary>
/// Converts audio files and extracts audio from video, using the bundled FFmpeg. Handles the matrix operations
/// <c>TranscodeAudio</c> and <c>ExtractAudio</c>. Same behavior as the Swift <c>AudioEngine</c>.
/// </summary>
public sealed class AudioEngine(FFmpegTools tools, AudioPresets? presets = null, ConversionMatrix? matrix = null,
                                string? platform = null)
{
    private readonly AudioPresets _presets = presets ?? AudioPresets.Shared;
    private readonly ConversionMatrix _matrix = matrix ?? ConversionMatrix.Shared;
    private readonly string _platform = platform ?? EnginePlatform.Current;

    public FFmpegTools Tools => tools;

    /// <summary>Converts <paramref name="input"/> into <paramref name="outputDirectory"/> under the input's name with
    /// the new extension (never replacing an existing file). Cancelling stops FFmpeg and removes partial output.</summary>
    public async Task<string> ConvertAsync(string input, FileFormat inputFormat, FileFormat outputFormat, string outputDirectory,
        AudioOptions? options = null, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var conversion = _matrix.Conversion(inputFormat.Id, outputFormat.Id);
        if (conversion is null || conversion.Operation is not (ConversionOperation.TranscodeAudio or ConversionOperation.ExtractAudio))
            throw ConversionException.Unsupported(inputFormat.Name, outputFormat.Name);
        OutputFiles.CheckWritable(outputDirectory);

        var info = await MediaProbe.ProbeAsync(input, tools, token).ConfigureAwait(false);
        if (info.IsCopyProtected) throw ConversionException.CopyProtected();
        var source = info.AudioStreams.FirstOrDefault() ?? throw ConversionException.NoAudio(Path.GetFileName(input));

        return await FFmpegConversion.RunAsync(tools, input, outputDirectory, outputFormat.PreferredExtension,
            source.Duration ?? info.Duration, progress,
            temporary => Arguments(input, info, temporary, outputFormat, conversion.Operation, options ?? new AudioOptions()),
            output => output.AudioStreams.Any() && FFmpegConversion.HasDuration(output, output.AudioStreams.First()),
            token).ConfigureAwait(false);
    }

    /// <summary>FFmpeg arguments for one conversion. Paths are always absolute, so they can't be mistaken for options.</summary>
    public IReadOnlyList<string> Arguments(string input, MediaInfo info, string output, FileFormat outputFormat,
                                           ConversionOperation operation, AudioOptions options)
    {
        if (!_presets.Formats.TryGetValue(outputFormat.Id, out var settings) || settings.EncoderFor(_platform) is not { } encoder)
            throw ConversionException.Unsupported("Audio", outputFormat.Name);
        var source = info.AudioStreams.FirstOrDefault() ?? throw ConversionException.NoAudio(Path.GetFileName(input));

        var args = FFmpegConversion.CommonArguments(input);
        args.AddRange(["-map", $"0:{source.Index}"]);

        // Keep cover art when converting audio to a format that can hold it. Video files never contribute frames.
        if (operation == ConversionOperation.TranscodeAudio && settings.SupportsArtwork && info.AttachedPicture is { } picture)
            args.AddRange(["-map", $"0:{picture.Index}", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
        else
            args.Add("-vn");
        args.AddRange(["-sn", "-dn", "-map_metadata", "0"]);

        // 24-bit sources stay 24-bit in WAV/AIFF.
        var highBitDepth = (source.BitsPerSample ?? 16) > 16;
        args.AddRange(["-c:a", highBitDepth ? settings.HighBitDepthEncoder ?? encoder : encoder]);

        if (!settings.IsLossless && settings.Bitrates is { } allowed)
        {
            var requested = options.BitrateKbps ?? _presets.PresetById(options.PresetId).BitrateKbps;
            args.AddRange(["-b:a", $"{AudioPresets.Nearest(requested, allowed)}k"]);
        }

        if (settings.SampleRates is { } allowedRates)
        {
            var wanted = options.SampleRate ?? source.SampleRate ?? 44100;
            var rate = AudioPresets.Nearest(wanted, allowedRates);
            if (rate != source.SampleRate || options.SampleRate is not null) args.AddRange(["-ar", $"{rate}"]);
        }
        else if (options.SampleRate is { } requestedRate)
        {
            args.AddRange(["-ar", $"{AudioPresets.Nearest(requestedRate, _presets.SampleRates)}"]);
        }

        args.AddRange(["-f", settings.Muxer, Path.GetFullPath(output)]);
        return args;
    }
}
