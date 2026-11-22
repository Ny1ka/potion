using Potion.Core.Engines;
using Potion.Core.Formats;
using Xunit;

namespace Potion.Core.Tests;

/// <summary>
/// Shared helpers for engine tests: finds the LGPL FFmpeg build and generates sample files with it, so no media
/// files are committed. Without the build the tests return early, unless POTION_REQUIRE_FFMPEG=1 (CI), where a
/// missing build fails.
/// </summary>
internal static class EngineTestSupport
{
    public static FFmpegTools? Tools()
    {
        var dir = Environment.GetEnvironmentVariable("POTION_FFMPEG_DIR");
        if (string.IsNullOrEmpty(dir))
            dir = RepoFiles.Path(OperatingSystem.IsWindows() ? "build/ffmpeg/windows/bin" : "build/ffmpeg/macos/bin");
        try { return FFmpegTools.FromDirectory(dir); }
        catch (ConversionException)
        {
            if (Environment.GetEnvironmentVariable("POTION_REQUIRE_FFMPEG") == "1")
                Assert.Fail($"FFmpeg build missing at {dir}. Run scripts/ffmpeg/build-{(OperatingSystem.IsWindows() ? "windows" : "macos")}.sh");
            return null;
        }
    }

    public static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"potion-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static FileFormat Format(string id) => FormatRegistry.Shared.FormatById(id)!;

    /// <summary>Runs the bundled ffmpeg to create a sample file.</summary>
    public static async Task<string> Generate(FFmpegTools tools, string output, params string[] args)
    {
        var result = await ProcessRunner.RunAsync(tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", .. args, output]);
        Assert.True(result.ExitCode == 0, $"sample generation failed: {result.Stderr}");
        return output;
    }

    public static Task<string> Tone(FFmpegTools tools, string dir, string name = "tone.wav", double seconds = 1, params string[] extra) =>
        Generate(tools, Path.Combine(dir, name),
            ["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={seconds}", .. (extra.Length > 0 ? extra : ["-c:a", "pcm_s16le"])]);

    /// <summary>320×240 MPEG-4 video with an MP3 tone, encoded with FFmpeg's own codecs (available everywhere).</summary>
    public static Task<string> Clip(FFmpegTools tools, string dir, string name = "clip.mp4", int width = 320, int height = 240, double seconds = 1) =>
        Generate(tools, Path.Combine(dir, name),
            "-f", "lavfi", "-i", $"testsrc=size={width}x{height}:rate=25:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={seconds}",
            "-c:v", "mpeg4", "-q:v", "5", "-c:a", "libmp3lame", "-b:a", "128k", "-shortest");

    /// <summary>Whether this machine can use an OS encoder (Media Foundation is missing on some Windows Server images).</summary>
    public static async Task<bool> CanEncode(FFmpegTools tools, string encoder, bool video)
    {
        var dir = TempDirectory();
        try
        {
            var input = video ? new[] { "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10:duration=0.5", "-c:v", encoder, "-f", "mp4" }
                              : new[] { "-f", "lavfi", "-i", "sine=duration=0.5", "-c:a", encoder, "-f", "mp4" };
            var result = await ProcessRunner.RunAsync(tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", .. input, Path.Combine(dir, "probe.mp4")]);
            return result.ExitCode == 0;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    public static Task<MediaInfo> Probe(string path, FFmpegTools tools) => MediaProbe.ProbeAsync(path, tools);
}

/// <summary>A per-test temporary folder, deleted afterwards.</summary>
public abstract class EngineTestBase : IDisposable
{
    protected readonly string Dir = EngineTestSupport.TempDirectory();

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
