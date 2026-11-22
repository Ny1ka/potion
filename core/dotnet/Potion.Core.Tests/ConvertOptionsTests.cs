using Potion.Core.App;
using Potion.Core.Engines;
using Potion.Core.Formats;
using Xunit;

namespace Potion.Core.Tests;

public sealed class ConvertOptionsTests : EngineTestBase
{
    private static FileFormat F(string id) => FormatRegistry.Shared.FormatById(id)!;
    private static QueueItem Item(string id, string output) =>
        new($"/x/file.{id}", F(id), 1000, ConversionMatrix.Shared.Outputs(id).Select(o => o.Output).ToList(), output);

    [Fact]
    public void KindsFollowTheOperation()
    {
        Assert.Equal(new OptionsKind.Video("mp4"), OptionsKind.For([Item("mov", "mp4")], F("mp4")));
        Assert.IsType<OptionsKind.Gif>(OptionsKind.For([Item("mov", "gif")], F("gif")));
        Assert.Equal(new OptionsKind.Audio("wav", true), OptionsKind.For([Item("mp3", "wav")], F("wav")));
        Assert.Equal(new OptionsKind.Image(true), OptionsKind.For([Item("png", "jpg")], F("jpg")));
        Assert.True(OptionsKind.For([Item("pdf", "png")], F("png")).UsesPages);
        Assert.IsType<OptionsKind.None>(OptionsKind.For([Item("md", "pdf")], F("pdf")));
        // Different operations to one output can't share controls.
        Assert.IsType<OptionsKind.None>(OptionsKind.For([Item("png", "pdf"), Item("md", "pdf")], F("pdf")));
    }

    [Fact]
    public void SummaryAndNotesMatchTheMac()
    {
        var settings = new ConversionSettings(Video: new VideoOptions(MaxHeight: 1080, FrameRate: 30, Codec: "hevc"));
        Assert.Equal("MP4 · HEVC (H.265) · 1080p · 30 fps · standard", OptionsText.Summary(new OptionsKind.Video("mp4"), F("mp4"), settings, ""));
        Assert.Equal("WAV · lossless", OptionsText.Summary(new OptionsKind.Audio("wav", true), F("wav"), new ConversionSettings(), ""));
        Assert.Equal("DOCX · pages 2-3", OptionsText.Summary(new OptionsKind.PdfPages(), F("docx"), new ConversionSettings(), "2-3"));
        Assert.Equal(["Converting to a lossless format won't restore quality lost in the original."], OptionsText.Notes([Item("mp3", "wav")], F("wav")));
        Assert.Equal(("This PDF has only 1 page", true), OptionsText.PagesHint("2-3", 1));
        Assert.False(OptionsText.PagesHint("1-3, 5", 9).IsError);
        Assert.Equal("4K (2160p)", OptionsText.Resolution(2160));
    }

    [Fact]
    public void DefaultFormatChoicesPerFileType()
    {
        var video = DefaultFormats.For(FormatCategory.Video).Select(f => f.Id).ToList();
        Assert.Contains("mp4", video);
        Assert.DoesNotContain("mp3", video);
        var images = DefaultFormats.For(FormatCategory.Image).Select(f => f.Id).ToList();
        Assert.Contains("webp", images);
        Assert.DoesNotContain("pdf", images);   // other categories aren't defaults
    }

    [Fact]
    public async Task SettingsTravelWithTheRow()
    {
        var path = Path.Combine(Dir, "a.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[24]]);
        var queue = new QueueModel(new SettingsStore(Path.Combine(Dir, "s.json")));
        var items = await queue.AddAsync([path]);
        var settings = new ConversionSettings(Image: new ImageOptions(Quality: 0.5, Rotation: 90));
        queue.Apply(items, "jpg", settings);
        Assert.Equal("jpg", items[0].OutputId);
        Assert.Equal(settings, items[0].Settings);
    }
}
