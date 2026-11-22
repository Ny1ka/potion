namespace Potion.Core.Engines;

/// <summary>
/// Locations of the bundled LGPL <c>ffmpeg</c> and <c>ffprobe</c> programs (built by scripts/ffmpeg/).
/// They run as separate processes and are never linked into the app.
/// </summary>
public sealed record FFmpegTools(string Ffmpeg, string Ffprobe)
{
    private static string ExeName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    /// <summary>Uses <c>ffmpeg</c> and <c>ffprobe</c> (".exe" on Windows) from one folder.</summary>
    public static FFmpegTools FromDirectory(string directory)
    {
        var ffmpeg = Path.GetFullPath(Path.Combine(directory, ExeName("ffmpeg")));
        var ffprobe = Path.GetFullPath(Path.Combine(directory, ExeName("ffprobe")));
        if (!File.Exists(ffmpeg) || !File.Exists(ffprobe)) throw ConversionException.ToolsMissing();
        return new FFmpegTools(ffmpeg, ffprobe);
    }

    /// <summary>
    /// Finds the tools: <c>POTION_FFMPEG_DIR</c> (development and tests) first, then an <c>ffmpeg</c> folder next to
    /// the app, then the app folder itself.
    /// </summary>
    public static FFmpegTools Locate(IDictionary<string, string?>? environment = null, string? appDirectory = null)
    {
        var dir = environment is null ? Environment.GetEnvironmentVariable("POTION_FFMPEG_DIR")
                                      : environment.TryGetValue("POTION_FFMPEG_DIR", out var v) ? v : null;
        if (!string.IsNullOrEmpty(dir)) return FromDirectory(dir);
        var app = appDirectory ?? AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(app, "ffmpeg"), app })
        {
            try { return FromDirectory(candidate); } catch (ConversionException) { }
        }
        throw ConversionException.ToolsMissing();
    }
}
