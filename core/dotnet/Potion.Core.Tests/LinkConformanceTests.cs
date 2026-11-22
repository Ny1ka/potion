using System.Text.Json;
using Potion.Core.Dropper;
using Potion.Core.Formats;
using Xunit;

/// <summary>Runs the shared cases in core/spec/tests/links.json. core/swift runs the same file.</summary>
public class LinkConformanceTests
{
    private static readonly JsonElement Cases = JsonDocument.Parse(RepoFiles.Text("core/spec/tests/links.json")).RootElement;

    private static TheoryData<string> Names(string section)
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.GetProperty(section).EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
        return data;
    }
    public static TheoryData<string> LinkNames() => Names("links");
    public static TheoryData<string> ListNames() => Names("lists");

    private static JsonElement Case(string section, string name) =>
        Cases.GetProperty(section).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static string? String(JsonElement c, string property) =>
        c.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [Theory, MemberData(nameof(LinkNames))]
    public void Link(string name)
    {
        var c = Case("links", name);
        var (link, error) = SourceRegistry.Shared.Parse(c.GetProperty("input").GetString()!);
        if (String(c, "error") is { } expectedError)
        {
            Assert.Null(link);
            Assert.Equal(expectedError, SourceRegistry.Id(error!.Value));
            return;
        }
        Assert.True(link is not null, $"{name}: {error}");
        Assert.Equal(String(c, "url"), link.Text);
        Assert.Equal(String(c, "host"), link.Host);
        Assert.Equal(String(c, "source"), link.SourceId);
        Assert.Equal(String(c, "format"), link.FormatId);
        Assert.Equal(c.GetProperty("needsCheck").GetBoolean(), link.NeedsCheck);
        // System.Uri may unescape unreserved characters (%2E → "."), which RFC 3986 treats as the same address.
        Assert.Equal(Uri.UnescapeDataString(link.Text), Uri.UnescapeDataString(link.Uri.AbsoluteUri));
    }

    [Theory, MemberData(nameof(ListNames))]
    public void List(string name)
    {
        var c = Case("lists", name);
        var list = SourceRegistry.Shared.FindLinks(c.GetProperty("input").GetString()!);
        Assert.Equal(c.GetProperty("links").EnumerateArray().Select(l => l.GetString()), list.Links.Select(l => l.Text));
        var errors = c.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(errors.Select(e => e.GetProperty("input").GetString()), list.Problems.Select(p => p.Input));
        Assert.Equal(errors.Select(e => e.GetProperty("error").GetString()), list.Problems.Select(p => SourceRegistry.Id(p.Error)));
        Assert.Equal(c.GetProperty("overLimit").GetInt32(), list.OverLimit);
    }
}

public class SourceRegistryTests
{
    /// <summary>No adapter without an allowlist entry, and no allowlist entry without docs/SOURCES.md (spec Part 6.3.2).</summary>
    [Fact]
    public void EverySourceIsDocumented()
    {
        var registry = SourceRegistry.Shared;
        Assert.Equal(SourceAdapters.BuiltIn.Select(a => a.Id).Order(), registry.Sources.Select(s => s.Id).Order());
        var doc = RepoFiles.Text("docs/SOURCES.md");
        foreach (var source in registry.Sources)
        {
            Assert.Contains($"Source id: `{source.Id}`", doc);
            Assert.Contains($"Reviewed: {source.Reviewed}", doc);
        }
    }

    [Fact]
    public void EveryErrorHasAMessage()
    {
        foreach (var error in Enum.GetValues<LinkError>()) Assert.NotEmpty(SourceRegistry.Shared.Message(error));
        Assert.Equal("Potion Dropper can't download from this site.", SourceRegistry.Shared.Message(LinkError.UnsupportedSite));
    }

    [Fact]
    public void UserAgentIsHonest() =>
        Assert.Equal("PotionDropper/1.0.0 (+https://github.com/Ny1ka/potion)", SourceRegistry.Shared.UserAgent("1.0.0"));

    [Fact]
    public void TooLong()
    {
        Assert.Equal(LinkError.TooLong, SourceRegistry.Shared.Parse("https://example.com/" + new string('a', 2100) + ".mp4").Error);
        Assert.Equal(LinkError.TooLong, SourceRegistry.Shared.Parse("https://example.com/" + string.Concat(Enumerable.Repeat("é", 5000))).Error);
    }

    [Fact]
    public void PasteLimit()
    {
        var text = string.Join('\n', Enumerable.Range(1, 105).Select(i => $"https://example.com/{i}.mp4"));
        var list = SourceRegistry.Shared.FindLinks(text);
        Assert.Equal(100, list.Links.Count);
        Assert.Equal("https://example.com/100.mp4", list.Links[^1].Text);
        Assert.Equal(5, list.OverLimit);
    }

    private sealed class Stray : ISourceAdapter
    {
        public string Id => "stray";
        public SourceMatch? Match(LinkParts link, SourceInfo source, FormatRegistry formats) => null;
    }

    [Fact]
    public void AdapterWithoutAllowlistEntryIsRefused()
    {
        var json = RepoFiles.Text("core/spec/sources.json");
        Assert.Throws<FormatSpecException>(() => new SourceRegistry(json, adapters: [.. SourceAdapters.BuiltIn, new Stray()]));
        Assert.Throws<FormatSpecException>(() => new SourceRegistry(json, adapters: []));
    }

    [Theory]
    [InlineData("bücher", "bcher-kva")]
    [InlineData("例え", "r8jz45g")]
    [InlineData("テスト", "zckzah")]
    [InlineData("münchen-straße", "mnchen-strae-v9a90b")]
    public void Punycode(string label, string expected) => Assert.Equal(expected, Potion.Core.Dropper.Punycode.Encode(label));
}
