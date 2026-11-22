using System.Text.Json;
using Potion.Core.Dropper;
using Potion.Core.Engines;
using Potion.Core.Formats;
using Potion.Core.Tests;
using Xunit;

/// <summary>A converter that reports a chosen duration and "converts" by writing a small file named after the input.</summary>
internal sealed class FakeConverter : IMediaConverter
{
    public double? Duration { get; set; } = 60;
    public bool CopyProtected { get; set; }
    public List<(string Input, string From, string To)> Conversions { get; } = [];

    public Task<MediaInfo> ProbeAsync(string file, CancellationToken token) =>
        Task.FromResult(new MediaInfo("mov,mp4", Duration, null,
            [new MediaStream(0, StreamKind.Audio, "aac", CopyProtected ? "drms" : "mp4a", 44100, 2, null, null, null, null, null, null, false, Duration)]));

    public Task<IReadOnlyList<string>> ConvertAsync(string input, FileFormat inputFormat, FileFormat outputFormat, string outputDirectory,
        ConversionSettings? settings, IProgress<double>? progress, CancellationToken token)
    {
        Conversions.Add((Path.GetFileName(input), inputFormat.Id, outputFormat.Id));
        progress?.Report(0.5);
        var output = OutputFiles.UniquePath(outputDirectory, Path.GetFileNameWithoutExtension(input), outputFormat.PreferredExtension);
        File.WriteAllText(output, "converted");
        return Task.FromResult<IReadOnlyList<string>>([output]);
    }
}

public class DropNamingConformanceTests
{
    private static readonly JsonElement Cases = JsonDocument.Parse(RepoFiles.Text("core/spec/tests/downloads.json")).RootElement;

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.GetProperty("names").EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    [Theory, MemberData(nameof(Names))]
    public void Name(string name)
    {
        var c = Cases.GetProperty("names").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        string S(string p) => c.GetProperty(p).GetString()!;
        var registry = SourceRegistry.Shared;
        Assert.Equal(S("expect"), DropNaming.Render(S("template"), S("fileName"), S("host"), S("date"), S("format"),
            registry.Download.MaxFileNameLength - registry.Naming.ReserveForSuffix, registry.Naming.FallbackName));
    }
}

public sealed class DropJobTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"potion-job-{Guid.NewGuid():N}");
    private readonly ScriptedTransport _transport = new();
    private readonly FakeConverter _converter = new();

    public DropJobTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } }

    private DropJobRunner Runner() =>
        new(new Downloader(_transport, "1.0.0", sleep: (_, _) => Task.CompletedTask), _converter, today: () => "2026-11-22");

    private static DropLink Link(string text) => SourceRegistry.Shared.Parse(text).Link!;
    private string[] Visible() => Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    private static byte[] Mp4(int size)
    {
        var head = new List<byte> { 0, 0, 0, 0x20 };
        head.AddRange("ftypisom"u8.ToArray());
        head.AddRange(new byte[] { 0, 0, 2, 0 });
        head.AddRange("isomiso2avc1mp41"u8.ToArray());
        while (head.Count < size) head.Add((byte)(head.Count % 251));
        return [.. head];
    }

    private void ServeMp4(string url, int size = 3000) =>
        _transport.On(url, new(Headers: new() { ["Content-Type"] = "video/mp4" }, Chunks: [Mp4(size)]));

    [Fact]
    public async Task KeepsTheDownloadedFormat()
    {
        ServeMp4("https://example.com/media/talk.mp4");
        var stages = new List<DropStageKind>();
        var outcome = await Runner().RunAsync(Link("https://example.com/media/talk.mp4"), _folder, new DropOptions(), stage: s => stages.Add(s.Kind));
        Assert.Equal(["talk.mp4"], outcome.Files.Select(Path.GetFileName));
        Assert.Equal("mp4", outcome.OutputFormatId);
        Assert.Empty(_converter.Conversions);
        Assert.Equal(["talk.mp4"], Visible());
        Assert.Contains(DropStageKind.Inspecting, stages);
        Assert.Equal(DropStageKind.Saving, stages[^1]);
    }

    [Fact]
    public async Task ConvertsAndNamesFromTheTemplate()
    {
        ServeMp4("https://www.example.com/media/talk.mp4");
        var outcome = await Runner().RunAsync(Link("https://www.example.com/media/talk.mp4"), _folder,
            new DropOptions("mp3", NameTemplate: "{site} - {title} ({format})"));
        Assert.Equal(["example.com - talk (MP3).mp3"], outcome.Files.Select(Path.GetFileName));
        Assert.Equal("example.com - talk (MP3).mp4", _converter.Conversions[0].Input);
        Assert.Equal("mp3", _converter.Conversions[0].To);
        Assert.Equal(["example.com - talk (MP3).mp3"], Visible());
    }

    [Fact]
    public async Task KeepsTheSourceWhenAsked()
    {
        ServeMp4("https://example.com/a.mp4");
        var outcome = await Runner().RunAsync(Link("https://example.com/a.mp4"), _folder, new DropOptions("mp3", KeepSource: true));
        Assert.Equal(["a.mp3", "a.mp4"], outcome.Files.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task NeverOverwrites()
    {
        File.WriteAllText(Path.Combine(_folder, "a.mp4"), "mine");
        ServeMp4("https://example.com/a.mp4");
        var outcome = await Runner().RunAsync(Link("https://example.com/a.mp4"), _folder, new DropOptions());
        Assert.Equal(["a 2.mp4"], outcome.Files.Select(Path.GetFileName));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_folder, "a.mp4")));
    }

    [Fact]
    public async Task DurationLimit()
    {
        _converter.Duration = 31 * 60 + 5;
        ServeMp4("https://example.com/long.mp4");
        var e = await Assert.ThrowsAsync<DropException>(() => Runner().RunAsync(Link("https://example.com/long.mp4"), _folder, new DropOptions()));
        Assert.True(e.TooLong);
        Assert.Equal(32, e.Minutes);
        Assert.Equal(30, e.Limit);
        Assert.Equal("This file is 32 minutes long. The limit in Settings is 30 minutes.", e.Message);
        Assert.Empty(Visible());

        var outcome = await Runner().RunAsync(Link("https://example.com/long.mp4"), _folder, new DropOptions(MaxDurationMinutes: null));
        Assert.Single(outcome.Files);
    }

    [Fact]
    public async Task CopyProtectedMediaIsRefused()
    {
        _converter.CopyProtected = true;
        ServeMp4("https://example.com/p.mp4");
        var e = await Assert.ThrowsAsync<DropException>(() => Runner().RunAsync(Link("https://example.com/p.mp4"), _folder, new DropOptions("mp3")));
        Assert.Equal(ConversionErrorKind.CopyProtected, e.Conversion?.Kind);
        Assert.Empty(Visible());
    }

    [Fact]
    public async Task FailedDownloadKeepsOnlyResumableWork()
    {
        _transport.On("https://example.com/r.mp4", new(Headers: new() { ["Content-Type"] = "video/mp4", ["Content-Length"] = "5000", ["ETag"] = "\"r\"" },
            Chunks: [Mp4(5000)[..2000]], FailAfterChunks: true));
        var e = await Assert.ThrowsAsync<DropException>(() => Runner().RunAsync(Link("https://example.com/r.mp4"), _folder, new DropOptions()));
        Assert.Equal(DownloadError.Interrupted, e.Download);
        Assert.True(File.Exists(e.Resume!.File));

        _transport.On("https://example.com/404.mp4", new(404));
        await Assert.ThrowsAsync<DropException>(() => Runner().RunAsync(Link("https://example.com/404.mp4"), _folder, new DropOptions()));
        Assert.Single(Visible());
        Assert.StartsWith(".potion-dropper-", Visible()[0]);
    }

    [Fact]
    public async Task EndToEndWithFFmpeg()
    {
        var tools = EngineTestSupport.Tools();
        if (tools is null) return;
        var samples = EngineTestSupport.TempDirectory();
        try
        {
            var wav = await EngineTestSupport.Tone(tools, samples, seconds: 2);
            _transport.On("https://example.com/sounds/tone.wav", new(Headers: new() { ["Content-Type"] = "audio/wav" }, Chunks: (await File.ReadAllBytesAsync(wav)).Chunk(1000).ToArray()));
            var runner = new DropJobRunner(new Downloader(_transport, "1.0.0"), new FFmpegMediaConverter(tools));
            var outcome = await runner.RunAsync(Link("https://example.com/sounds/tone.wav"), _folder, new DropOptions("mp3"));
            Assert.Equal(["tone.mp3"], outcome.Files.Select(Path.GetFileName));
            var info = await EngineTestSupport.Probe(outcome.Files[0], tools);
            Assert.Equal("mp3", info.AudioStreams.First().Codec);
            Assert.Equal(["tone.mp3"], Visible());
        }
        finally { Directory.Delete(samples, recursive: true); }
    }
}

public class DropHistoryTests
{
    [Fact]
    public void AddClearAndLimit()
    {
        var dir = EngineTestSupport.TempDirectory();
        try
        {
            var path = Path.Combine(dir, "Potion Dropper", "history.json");
            var history = new DropHistory(path);
            history.Add(DropHistory.Entry.Create("https://example.com/a.mp4", "example.com", "a.mp4", ["/x/a.mp4"], "mp4", 10));
            history.Add(DropHistory.Entry.Create("https://example.com/b.mp4", "example.com", "b.mp4", error: "notFound"));
            Assert.Equal(["b.mp4", "a.mp4"], new DropHistory(path).Entries.Select(e => e.Name));

            history.Remove(history.Entries[0].Id);
            Assert.Equal(["a.mp4"], new DropHistory(path).Entries.Select(e => e.Name));

            for (var i = 0; i < DropHistory.MaxEntries + 5; i++) history.Add(DropHistory.Entry.Create($"https://example.com/{i}.mp4", "example.com", $"{i}"));
            Assert.Equal(DropHistory.MaxEntries, history.Entries.Count);

            history.Clear();
            Assert.False(File.Exists(path));
            Assert.Empty(new DropHistory(path).Entries);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
