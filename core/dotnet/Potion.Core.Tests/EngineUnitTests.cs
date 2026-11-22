using Potion.Core.Engines;
using Xunit;

namespace Potion.Core.Tests;

/// <summary>Engine pieces that don't need FFmpeg: probe parsing, presets, geometry, frame rates, saving.</summary>
public sealed class EngineUnitTests : EngineTestBase
{
    [Fact]
    public void ParsesFFprobeJsonIncludingRotationAndCoverArt()
    {
        const string json = """
        {"streams":[
          {"index":0,"codec_type":"video","codec_name":"h264","codec_tag_string":"avc1","width":1920,"height":1080,
           "avg_frame_rate":"30000/1001","pix_fmt":"yuv420p","side_data_list":[{"rotation":-90}]},
          {"index":1,"codec_type":"audio","codec_name":"aac","codec_tag_string":"mp4a","sample_rate":"48000","channels":2,"bit_rate":"128000"},
          {"index":2,"codec_type":"video","codec_name":"mjpeg","codec_tag_string":"[0][0][0][0]","disposition":{"attached_pic":1}}],
         "format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"12.5","bit_rate":"5000000"}}
        """;
        var info = MediaProbe.Parse(json);
        Assert.Equal(12.5, info.Duration);
        var video = info.VideoStreams.Single();
        Assert.Equal((1080, 1920), (video.Width, video.Height));   // portrait after rotation
        Assert.Equal(29.97, video.FrameRate!.Value, 2);
        Assert.Equal(48000, info.AudioStreams.Single().SampleRate);
        Assert.Equal(2, info.AttachedPicture!.Index);
        Assert.False(info.IsCopyProtected);
    }

    [Fact]
    public void RecognizesCopyProtection()
    {
        var info = MediaProbe.Parse("""{"streams":[{"index":0,"codec_type":"audio","codec_tag_string":"drms"}],"format":{}}""");
        Assert.True(info.IsCopyProtected);
        Assert.True(MediaProbe.LooksCopyProtected("Error: DRM protected stream"));
        Assert.True(MediaProbe.LooksCopyProtected("decryption key not found"));
        Assert.False(MediaProbe.LooksCopyProtected("Invalid data found when processing input"));
    }

    [Fact]
    public void PresetsLoadWithPlatformEncoders()
    {
        Assert.Equal("aac_mf", AudioPresets.Shared.Formats["m4a"].EncoderFor("windows"));
        Assert.Equal("aac_at", AudioPresets.Shared.Formats["m4a"].EncoderFor("macos"));
        Assert.Equal("libmp3lame", AudioPresets.Shared.Formats["mp3"].EncoderFor("windows"));
        Assert.Equal("h264_mf", VideoPresets.Shared.Codecs["h264"].EncoderFor("windows"));
        Assert.Equal("HEVC (H.265)", VideoPresets.Shared.Codecs["hevc"].Name);
        Assert.Equal(192, AudioPresets.Nearest(200, [128, 192, 256]));
        Assert.Equal(128, AudioPresets.Nearest(160, [128, 192]));   // ties go lower
    }

    [Fact]
    public void PdfPresetsLoad()
    {
        Assert.Equal(150, PdfPresets.Shared.DefaultDpi);
        Assert.Contains(300.0, PdfPresets.Shared.DpiChoices);
        Assert.Equal(10000, PdfPresets.Shared.MaxRenderPixels);
    }

    [Fact]
    public void GeometryNeverUpscalesAndLetterboxes()
    {
        static MediaStream Video(int w, int h) => new(0, StreamKind.Video, "h264", "", null, null, null, null, w, h, 30, null, false, null);
        var down = new VideoEngine.Geometry(Video(1920, 1080), 720, null);
        Assert.Equal((1280, 720, "scale=-2:720"), (down.Width, down.Height, down.Filter));
        var noUpscale = new VideoEngine.Geometry(Video(640, 360), 1080, null);
        Assert.Null(noUpscale.Filter);
        var square = new VideoEngine.Geometry(Video(1920, 1080), null, "1:1");
        Assert.Equal((1080, 1080), (square.Width, square.Height));
        Assert.Contains("pad=1080:1080", square.Filter);
        Assert.Equal("scale=-2:360", new VideoEngine.Geometry(Video(641, 361), null, null).Filter);   // odd height rounds down
        Assert.Equal("scale=trunc(iw/2)*2:trunc(ih/2)*2", new VideoEngine.Geometry(Video(641, 360), null, null).Filter);
    }

    [Fact]
    public void FrameRatesOnlyGoDownAndMpeg2Snaps()
    {
        Assert.Null(VideoEngine.TargetFrameRate(30, 60, false));
        Assert.Equal("24", VideoEngine.TargetFrameRate(30, 24, false)?.Text);
        Assert.Equal("30000/1001", VideoEngine.TargetFrameRate(29.5, null, true)?.Text);
        Assert.Null(VideoEngine.TargetFrameRate(25, null, true));
    }

    [Fact]
    public void ProgressParsing()
    {
        Assert.Equal(0.5, FFmpegConversion.ProgressFraction("out_time_us=5000000", 10));
        Assert.Equal(1, FFmpegConversion.ProgressFraction("progress=end", null));
        Assert.Null(FFmpegConversion.ProgressFraction("frame=12", 10));
    }

    [Fact]
    public void UniqueNamesAndSafeMoves()
    {
        File.WriteAllText(Path.Combine(Dir, "Song.mp3"), "a");
        File.WriteAllText(Path.Combine(Dir, "Song 2.mp3"), "b");
        Assert.Equal("Song 3.mp3", Path.GetFileName(OutputFiles.UniquePath(Dir, "Song", "mp3")));
        var temp = OutputFiles.TemporaryPath(Dir, "mp3");
        File.WriteAllText(temp, "new");
        var saved = OutputFiles.Finalize(temp, Dir, "Song", "mp3");
        Assert.Equal("Song 3.mp3", Path.GetFileName(saved));
        Assert.Equal("a", File.ReadAllText(Path.Combine(Dir, "Song.mp3")));
        Assert.False(File.Exists(temp));
        OutputFiles.RemoveIfPresent(Path.Combine(Dir, "missing"));   // never throws
    }

    [Fact]
    public void LocatesToolsFromTheEnvironmentOrFailsClearly()
    {
        var missing = Assert.Throws<ConversionException>(() => FFmpegTools.Locate(new Dictionary<string, string?>(), Dir));
        Assert.Equal("Potion's conversion tools are missing. Reinstall Potion to restore them.", missing.Message);
    }
}
