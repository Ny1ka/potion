using System.Globalization;

namespace Potion.Core.Engines;

/// <summary>
/// The steps every FFmpeg-based conversion shares: write to a hidden temporary file, report progress, map
/// failures to user-facing errors, verify the result, then save it under a name that doesn't exist yet.
/// </summary>
internal static class FFmpegConversion
{
    public static async Task<string> RunAsync(FFmpegTools tools, string input, string outputDirectory, string outputExtension,
        double? duration, IProgress<double>? progress, Func<string, IReadOnlyList<string>> arguments,
        Func<MediaInfo, bool> verify, CancellationToken token)
    {
        var temporary = OutputFiles.TemporaryPath(outputDirectory, outputExtension);
        var fileName = Path.GetFileName(input);
        try
        {
            var result = await ProcessRunner.RunAsync(tools.Ffmpeg, arguments(temporary), line =>
            {
                if (ProgressFraction(line, duration) is { } fraction) progress?.Report(fraction);
            }, token).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                if (MediaProbe.LooksCopyProtected(result.Stderr)) throw ConversionException.CopyProtected();
                throw ConversionException.ConversionFailed(fileName, result.Stderr);
            }

            MediaInfo output;
            try { output = await MediaProbe.ProbeAsync(temporary, tools, token).ConfigureAwait(false); }
            catch (ConversionException) { throw ConversionException.VerificationFailed(fileName); }
            if (!verify(output)) throw ConversionException.VerificationFailed(fileName);

            var saved = OutputFiles.Finalize(temporary, outputDirectory, Path.GetFileNameWithoutExtension(input), outputExtension);
            progress?.Report(1);
            return saved;
        }
        catch (UnauthorizedAccessException)
        {
            throw ConversionException.CannotWrite(Path.GetFileName(outputDirectory));
        }
        finally
        {
            OutputFiles.RemoveIfPresent(temporary);   // no-op once moved into place
        }
    }

    /// <summary>Arguments every FFmpeg run starts with: quiet, never overwrite, machine-readable progress on stdout.</summary>
    public static List<string> CommonArguments(string input) =>
        ["-hide_banner", "-nostdin", "-n", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-i", Path.GetFullPath(input)];

    /// <summary>Parses FFmpeg <c>-progress</c> output ("out_time_us=…", "progress=end") into a 0…1 fraction.</summary>
    public static double? ProgressFraction(string line, double? duration)
    {
        if (line == "progress=end") return 1;
        if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || duration is not > 0) return null;
        if (!double.TryParse(line["out_time_us=".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var micros)) return null;
        return Math.Clamp(micros / 1_000_000 / duration.Value, 0, 1);
    }

    public static bool HasDuration(MediaInfo info, MediaStream? stream) => (info.Duration ?? stream?.Duration ?? 0) > 0;
}
