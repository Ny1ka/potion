using Potion.Core.App;
using Xunit;

namespace Potion.Core.Tests;

public sealed class QueueModelTests : EngineTestBase
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[24]];
    private static readonly byte[] Text = "Plain notes\n"u8.ToArray();

    private string Put(string relative, byte[] bytes)
    {
        var path = Path.Combine(Dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void CollectorExpandsFoldersSkipsHiddenAndSortsNaturally()
    {
        Put("album/Track 10.txt", Text);
        Put("album/Track 2.txt", Text);
        Put("album/.DS_Store", [0, 1, 2]);
        Put("album/.potion-1234.mp3", Text);
        Put("album/inner/cover.png", Png);
        Put("album/data.bin", [0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE]);
        var album = Path.Combine(Dir, "album");

        var flat = new FileCollector().Collect([album], includeSubfolders: false);
        Assert.Equal(["Track 2.txt", "Track 10.txt"], flat.Files.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal(["data.bin"], flat.Unsupported.Select(Path.GetFileName));
        Assert.Equal(Text.Length, flat.Files[0].Size);

        var deep = new FileCollector().Collect([album], includeSubfolders: true);
        Assert.Contains(deep.Files, f => Path.GetFileName(f.Path) == "cover.png");
    }

    [Fact]
    public void CollectorDropsDuplicatesQueuedFilesAndHonorsTheLimit()
    {
        var a = Put("a.png", Png);
        var b = Put("b.png", Png);
        var result = new FileCollector().Collect([a, a, Dir, b], includeSubfolders: false, excluding: [b]);
        Assert.Equal(["a.png"], result.Files.Select(f => Path.GetFileName(f.Path)));
        for (var i = 0; i < 5; i++) Put($"many/{i}.txt", Text);
        var limited = new FileCollector(limit: 3).Collect([Path.Combine(Dir, "many")], includeSubfolders: true);
        Assert.Equal(3, limited.Files.Count);
        Assert.True(limited.Truncated);
    }

    [Fact]
    public async Task QueueDefaultsRemembersFormatsAndReportsUnsupportedFiles()
    {
        var settings = new SettingsStore(Path.Combine(Dir, "settings.json"));
        var queue = new QueueModel(settings);
        var added = await queue.AddAsync([Put("cover.png", Png), Put("notes.txt", Text), Put("mystery.bin", [0, 1, 2, 3, 0xFF])]);
        Assert.Equal(2, added.Count);
        Assert.False(queue.IsEmpty);
        Assert.Equal("2 files", queue.CountText);
        Assert.Equal("“mystery.bin” can't be converted.", queue.BannerMessage);

        var png = queue.Items.Single(i => i.Format.Id == "png");
        Assert.NotEqual("png", png.OutputId);                 // never defaults to its own format
        Assert.Equal("PNG · 32 bytes", png.SecondaryText);
        Assert.Equal("Waiting", png.StatusText);
        Assert.Equal(Potion.Core.Formats.FormatCategory.Image, png.OutputGroups[0].Category);

        queue.SetOutput([png], "webp");
        Assert.Equal("webp", png.OutputId);
        // A new queue (next launch) starts PNGs on the remembered format.
        var later = new QueueModel(new SettingsStore(Path.Combine(Dir, "settings.json")));
        var again = await later.AddAsync([Put("other.png", Png)]);
        Assert.Equal("webp", again[0].OutputId);

        Assert.DoesNotContain(queue.CommonOutputs(queue.Items.ToList()), f => f.Id == "mp3");

        queue.Remove([png]);
        Assert.Single(queue.Items);
        queue.Clear();
        Assert.True(queue.IsEmpty);
        Assert.Null(queue.BannerMessage);
    }

    [Fact]
    public void StatusAndSizeText()
    {
        var format = Potion.Core.Formats.FormatRegistry.Shared.FormatById("mov")!;
        var item = new QueueItem("/x/Interview.mov", format, 48_200_000, [format], "mov") { Status = QueueStatus.Converting, Progress = 0.62 };
        Assert.Equal("Interview.mov", item.Name);
        Assert.Contains("62", item.StatusText);
        Assert.Equal("MOV · 46 MB", item.SecondaryText);
        item.Status = QueueStatus.Failed;
        item.Failure = "Can't read “Interview.mov”. The file may be damaged.";
        Assert.Equal(item.Failure, item.SecondaryText);
        Assert.Equal(("6 bytes", "105 KB", "1.5 MB"), (ByteSize.Format(6), ByteSize.Format(107_000), ByteSize.Format(1_572_864)));
    }

    [Fact]
    public void DropStatesMatchTheMacWording()
    {
        var png = Put("a.png", Png);
        var bin = Put("b.bin", [0, 1, 2, 3, 0xFF]);
        Assert.Equal("Drop to add 1 file", DropState.Classify([png]).Label);
        Assert.Equal("Drop to add 1 folder", DropState.Classify([Dir]).Label);
        Assert.Equal("Drop to add 2 items", DropState.Classify([png, Dir]).Label);
        var invalid = DropState.Classify([bin]);
        Assert.True(invalid.Invalid);
        Assert.Equal("These files can't be converted", invalid.Label);
    }
}
