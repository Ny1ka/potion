using Potion.Core.Engines;
using Xunit;
using S = Potion.Core.Tests.EngineTestSupport;

namespace Potion.Core.Tests;

/// <summary>End-to-end audio and video conversions with the bundled FFmpeg (samples generated per run).</summary>
public sealed class AudioVideoEngineTests : EngineTestBase
{
    // Audio

    [Fact]
    public async Task WavToMp3UsesThePresetBitrate()
    {
        if (S.Tools() is not { } tools) return;
        var wav = await S.Tone(tools, Dir, seconds: 2);
        var progress = new List<double>();
        var outPath = await new AudioEngine(tools).ConvertAsync(wav, S.Format("wav"), S.Format("mp3"), Dir,
            new AudioOptions(PresetId: "small"), new SyncProgress(progress));
        Assert.Equal("tone.mp3", Path.GetFileName(outPath));
        var info = await S.Probe(outPath, tools);
        Assert.Equal("mp3", info.AudioStreams.Single().Codec);
        Assert.InRange(info.AudioStreams.Single().BitRate ?? 0, 120_000, 136_000);
        Assert.Equal(1, progress.Last());
    }

    [Fact]
    public async Task TwentyFourBitStaysTwentyFourBitAndSampleRatesSnap()
    {
        if (S.Tools() is not { } tools) return;
        var wav = await S.Tone(tools, Dir, "hi.wav", 1, "-c:a", "pcm_s24le", "-ar", "96000");
        var aiff = await new AudioEngine(tools).ConvertAsync(wav, S.Format("wav"), S.Format("aiff"), Dir);
        Assert.Equal(24, (await S.Probe(aiff, tools)).AudioStreams.Single().BitsPerSample);
        var opus = await new AudioEngine(tools).ConvertAsync(wav, S.Format("wav"), S.Format("opus"), Dir);
        Assert.Equal(48000, (await S.Probe(opus, tools)).AudioStreams.Single().SampleRate);
    }

    [Fact]
    public async Task ExtractsAudioFromVideoWithoutPictures()
    {
        if (S.Tools() is not { } tools) return;
        var clip = await S.Clip(tools, Dir);
        var flac = await new AudioEngine(tools).ConvertAsync(clip, S.Format("mp4"), S.Format("flac"), Dir);
        var info = await S.Probe(flac, tools);
        Assert.Single(info.AudioStreams);
        Assert.Empty(info.VideoStreams);
        Assert.Null(info.AttachedPicture);
    }

    [Fact]
    public async Task NeverOverwritesAndLeavesNoTemporaryFiles()
    {
        if (S.Tools() is not { } tools) return;
        var wav = await S.Tone(tools, Dir);
        File.WriteAllText(Path.Combine(Dir, "tone.flac"), "existing");
        var flac = await new AudioEngine(tools).ConvertAsync(wav, S.Format("wav"), S.Format("flac"), Dir);
        Assert.Equal("tone 2.flac", Path.GetFileName(flac));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(Dir, "tone.flac")));
        Assert.Empty(Directory.GetFiles(Dir, ".potion-*"));
    }

    [Fact]
    public async Task ErrorsUseTheUserFacingWording()
    {
        if (S.Tools() is not { } tools) return;
        var silent = await S.Generate(tools, Path.Combine(Dir, "silent.mp4"),
            "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10:duration=0.5", "-c:v", "mpeg4");
        var noAudio = await Assert.ThrowsAsync<ConversionException>(() =>
            new AudioEngine(tools).ConvertAsync(silent, S.Format("mp4"), S.Format("mp3"), Dir));
        Assert.Equal(ConversionErrorKind.NoAudio, noAudio.Kind);
        Assert.Equal("“silent.mp4” has no audio to convert.", noAudio.Message);

        var junk = Path.Combine(Dir, "junk.mp3");
        File.WriteAllText(junk, "not audio");
        var unreadable = await Assert.ThrowsAsync<ConversionException>(() =>
            new AudioEngine(tools).ConvertAsync(junk, S.Format("mp3"), S.Format("wav"), Dir));
        Assert.Equal(ConversionErrorKind.Unreadable, unreadable.Kind);

        var unsupported = await Assert.ThrowsAsync<ConversionException>(() =>
            new AudioEngine(tools).ConvertAsync(junk, S.Format("mp3"), S.Format("png"), Dir));
        Assert.Equal("MP3 can't be converted to PNG.", unsupported.Message);
    }

    [Fact]
    public async Task CancellingStopsFFmpegAndRemovesPartialOutput()
    {
        if (S.Tools() is not { } tools) return;
        var clip = await S.Clip(tools, Dir, "long.mp4", 1280, 720, seconds: 20);
        using var cts = new CancellationTokenSource();
        var task = new VideoEngine(tools).ConvertAsync(clip, S.Format("mp4"), S.Format("webm"), Dir,
            new VideoOptions(PresetId: "high"), new Progress<double>(f => { if (f > 0.02) cts.Cancel(); }), cts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(File.Exists(Path.Combine(Dir, "long.webm")));
        Assert.Empty(Directory.GetFiles(Dir, ".potion-*"));
    }

    // Video

    [Fact]
    public async Task OriginalQualityCopiesStreamsIntoMkv()
    {
        if (S.Tools() is not { } tools) return;
        var clip = await S.Clip(tools, Dir);
        var mkv = await new VideoEngine(tools).ConvertAsync(clip, S.Format("mp4"), S.Format("mkv"), Dir, new VideoOptions(PresetId: "original"));
        var info = await S.Probe(mkv, tools);
        Assert.Equal("mpeg4", info.VideoStreams.Single().Codec);
        Assert.Equal("mp3", info.AudioStreams.Single().Codec);
    }

    [Fact]
    public async Task WebmScalesDownAndReencodesAudio()
    {
        if (S.Tools() is not { } tools) return;
        var clip = await S.Clip(tools, Dir, width: 640, height: 480);
        var webm = await new VideoEngine(tools).ConvertAsync(clip, S.Format("mp4"), S.Format("webm"), Dir, new VideoOptions(MaxHeight: 240));
        var info = await S.Probe(webm, tools);
        var video = info.VideoStreams.Single();
        Assert.Equal("vp9", video.Codec);
        Assert.Equal((320, 240), (video.Width, video.Height));
        Assert.Equal("opus", info.AudioStreams.Single().Codec);
    }

    [Fact]
    public async Task VideoToGifAndBack()
    {
        if (S.Tools() is not { } tools) return;
        var clip = await S.Clip(tools, Dir, width: 640, height: 360);
        var gif = await new VideoEngine(tools).ConvertAsync(clip, S.Format("mp4"), S.Format("gif"), Dir);
        var gifInfo = await S.Probe(gif, tools);
        Assert.Equal("gif", gifInfo.Streams.Single().Codec);
        Assert.True(gifInfo.Streams.Single().Width <= VideoPresets.Shared.Gif[VideoPresets.Shared.DefaultPresetId].MaxWidth);
        var back = await new VideoEngine(tools).ConvertAsync(gif, S.Format("gif"), S.Format("webm"), Dir);
        Assert.Empty((await S.Probe(back, tools)).AudioStreams);
    }

    [Fact]
    public async Task OSEncodersMakeMp4AndM4a()
    {
        if (S.Tools() is not { } tools) return;
        var video = VideoPresets.Shared.Codecs["h264"].EncoderFor(EnginePlatform.Current)!;
        var aac = AudioPresets.Shared.Formats["m4a"].EncoderFor(EnginePlatform.Current)!;
        if (!await S.CanEncode(tools, video, video: true) || !await S.CanEncode(tools, aac, video: false)) return;   // no Media Foundation here
        var clip = await S.Clip(tools, Dir);
        var mp4 = await new VideoEngine(tools).ConvertAsync(clip, S.Format("mp4"), S.Format("mov"), Dir, new VideoOptions(PresetId: "standard"));
        Assert.Equal("h264", (await S.Probe(mp4, tools)).VideoStreams.Single().Codec);
        var m4a = await new AudioEngine(tools).ConvertAsync(clip, S.Format("mp4"), S.Format("m4a"), Dir);
        Assert.Equal("aac", (await S.Probe(m4a, tools)).AudioStreams.Single().Codec);
    }

    /// <summary>Records progress synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) { lock (values) values.Add(value); }
    }
}
