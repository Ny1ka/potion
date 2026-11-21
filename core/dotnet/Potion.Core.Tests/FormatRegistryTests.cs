using System.Text.Json;
using Potion.Core.Formats;
using Xunit;

public class FormatRegistryTests
{
    [Fact]
    public void CategoriesAgreeWithDesignSystem()
    {
        using var design = JsonDocument.Parse(RepoFiles.Text("design-system/data/formats.json"));
        var byExtension = design.RootElement.GetProperty("byExtension");
        foreach (var format in FormatRegistry.Shared.Formats)
        foreach (var ext in format.Extensions)
        {
            if (!byExtension.TryGetProperty(ext, out var category)) continue;
            Assert.Equal(category.GetString(), format.Category.ToString().ToLowerInvariant());
        }
    }

    [Fact]
    public void EverySpecFormatIsListed()
    {
        string[] required = ["mp4", "mov", "mkv", "avi", "webm", "wmv", "flv", "mpeg", "m4v", "3gp", "ts",
            "mp3", "wav", "m4a", "aac", "flac", "ogg", "opus", "aiff", "wma", "alac",
            "png", "jpg", "webp", "heic", "tiff", "bmp", "gif", "svg", "ico", "avif",
            "pdf", "docx", "doc", "odt", "rtf", "txt", "html", "md", "epub"];
        var ids = FormatRegistry.Shared.Formats.Select(f => f.Id).ToHashSet();
        Assert.Empty(required.Except(ids));
    }

    [Fact]
    public void ExtensionLookup()
    {
        var r = FormatRegistry.Shared;
        Assert.Equal("jpg", r.FormatForExtension("JPEG")?.Id);
        Assert.Equal("mpeg", r.FormatForExtension(".mpg")?.Id);
        Assert.Equal("m4a", r.FormatForExtension("m4a")?.Id); // M4A is listed before ALAC, so it owns .m4a
        Assert.Null(r.FormatForExtension("xyz"));
        Assert.Equal("heic", r.FormatById("heic")?.PreferredExtension);
    }

    [Theory]
    [InlineData("Clip.Final.MOV", "mov")]
    [InlineData(".hidden", null)]
    [InlineData("README", null)]
    [InlineData("trailing.", null)]
    public void FileExtensionParsing(string name, string? expected) => Assert.Equal(expected, FormatRegistry.FileExtension(name));

    [Fact]
    public void DetectReadsFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"potion-{Guid.NewGuid()}.flac");
        File.WriteAllBytes(path, [.. "fLaC"u8, 0, 0, 0, 0x22]);
        try
        {
            var result = FormatRegistry.Shared.Detect(path);
            Assert.Equal("flac", result.Format?.Id);
            Assert.Equal(DetectionMethod.Content, result.Method);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectsRuleForUnknownFormat()
    {
        const string json = """
            { "version": 1, "headerSize": 16,
              "formats": [{ "id": "png", "name": "PNG", "category": "image", "extensions": ["png"] }],
              "rules": [{ "format": "nope", "all": [{ "offset": 0, "hex": "89" }] }] }
            """;
        var e = Assert.Throws<FormatSpecException>(() => new FormatRegistry(json));
        Assert.Equal("Rule refers to unknown format nope", e.Message);
    }

    [Fact]
    public void RejectsMaskOfWrongLength()
    {
        const string json = """
            { "version": 1, "headerSize": 16,
              "formats": [{ "id": "aac", "name": "AAC", "category": "audio", "extensions": ["aac"] }],
              "rules": [{ "format": "aac", "all": [{ "offset": 0, "hex": "FFF0", "mask": "FF" }] }] }
            """;
        Assert.Throws<FormatSpecException>(() => new FormatRegistry(json));
    }
}
