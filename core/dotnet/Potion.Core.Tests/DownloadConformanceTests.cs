using System.Text;
using System.Text.Json;
using Potion.Core.Dropper;
using Xunit;

/// <summary>Runs the shared cases in core/spec/tests/downloads.json. core/swift runs the same file.</summary>
public class DownloadConformanceTests
{
    private static readonly JsonElement Cases = JsonDocument.Parse(RepoFiles.Text("core/spec/tests/downloads.json")).RootElement;
    private static SourceRegistry Registry => SourceRegistry.Shared;

    private static TheoryData<string> Names(string section)
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.GetProperty(section).EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
        return data;
    }
    public static TheoryData<string> ResponseNames() => Names("responses");
    public static TheoryData<string> SniffNames() => Names("sniff");
    public static TheoryData<string> FileNameNames() => Names("fileNames");
    public static TheoryData<string> RobotsNames() => Names("robots");

    private static JsonElement Case(string section, string name) =>
        Cases.GetProperty(section).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    [Theory, MemberData(nameof(ResponseNames))]
    public void Response(string name)
    {
        var c = Case("responses", name);
        var headers = c.GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var range = c.TryGetProperty("range", out var r) && r.GetBoolean();
        var maxBytes = c.TryGetProperty("maxBytes", out var m) ? m.GetInt64() : Registry.Download.DefaultMaxBytes;
        var verdict = DownloadRules.Classify(c.GetProperty("status").GetInt32(), headers, range, maxBytes, Registry.Download);
        var expect = c.GetProperty("expect").GetString();
        switch (verdict.Kind)
        {
            case VerdictKind.Accept:
                Assert.Equal(expect, verdict.Sniff ? "sniff" : "media");
                if (c.TryGetProperty("length", out var length)) Assert.Equal(length.GetInt64(), verdict.Length);
                if (c.TryGetProperty("resumed", out var resumed)) Assert.Equal(resumed.GetBoolean(), verdict.Resumed);
                break;
            case VerdictKind.Redirect:
                Assert.Equal("redirect", expect);
                Assert.Equal(c.GetProperty("location").GetString(), verdict.Location);
                break;
            case VerdictKind.Retry:
                Assert.Equal("retry", expect);
                Assert.Equal(c.GetProperty("seconds").GetInt32(), verdict.Seconds);
                break;
            case VerdictKind.Restart:
                Assert.Equal("restart", expect);
                break;
            default:
                Assert.Equal(expect, verdict.Error!.Id);
                break;
        }
    }

    [Theory, MemberData(nameof(SniffNames))]
    public void Sniff(string name)
    {
        var c = Case("sniff", name);
        var bytes = new List<byte>();
        foreach (var s in c.GetProperty("bytes").EnumerateArray())
        {
            if (s.TryGetProperty("hex", out var hex)) bytes.AddRange(Convert.FromHexString(hex.GetString()!));
            if (s.TryGetProperty("ascii", out var ascii)) bytes.AddRange(Encoding.UTF8.GetBytes(ascii.GetString()!));
            if (s.TryGetProperty("zeros", out var zeros)) bytes.AddRange(new byte[zeros.GetInt32()]);
        }
        var (format, error) = DownloadRules.Sniff(bytes.ToArray(), c.GetProperty("file").GetString()!);
        Assert.Equal(c.GetProperty("expect").GetString(), format ?? error!.Id);
    }

    [Theory, MemberData(nameof(FileNameNames))]
    public void FileName(string name)
    {
        var c = Case("fileNames", name);
        var (link, _) = Registry.Parse(c.GetProperty("link").GetString()!);
        var disposition = c.GetProperty("disposition").ValueKind == JsonValueKind.Null ? null : c.GetProperty("disposition").GetString();
        Assert.Equal(c.GetProperty("expect").GetString(), DownloadRules.FileName(disposition, link!.Parts, Registry.Download.MaxFileNameLength));
    }

    [Fact]
    public void RobotsStatus()
    {
        foreach (var c in Cases.GetProperty("robotsStatus").EnumerateArray())
        {
            var expected = c.GetProperty("expect").GetString()!;
            Assert.Equal(expected, JsonNamingPolicy.CamelCase.ConvertName(DownloadRules.RobotsFetchFor(c.GetProperty("status").GetInt32()).ToString()));
        }
    }

    [Theory, MemberData(nameof(RobotsNames))]
    public void Robots(string name)
    {
        var c = Case("robots", name);
        var rules = new RobotsRules(c.GetProperty("robots").GetString()!);
        foreach (var p in c.GetProperty("paths").EnumerateArray())
        {
            var path = p.GetProperty("path").GetString()!;
            Assert.True(p.GetProperty("allowed").GetBoolean() == rules.IsAllowed(path, Registry.RobotsAgent), $"{name}: {path}");
        }
    }
}

public class DownloadRulesTests
{
    [Fact]
    public void RobotsAgentComesFromTheUserAgent() => Assert.Equal("PotionDropper", SourceRegistry.Shared.RobotsAgent);

    [Fact]
    public void EveryDownloadErrorHasAMessage()
    {
        foreach (var error in DownloadError.All) Assert.NotEmpty(SourceRegistry.Shared.Message(error));
    }

    [Fact]
    public void FixedRobotsModes()
    {
        Assert.True(RobotsRules.AllowAll.IsAllowed("/a.mp4", "PotionDropper"));
        Assert.False(RobotsRules.DisallowAll.IsAllowed("/a.mp4", "PotionDropper"));
        Assert.True(RobotsRules.DisallowAll.IsAllowed("/robots.txt", "PotionDropper"));
    }

    [Fact]
    public void RetryAfterDateWithoutDateHeaderUsesNow()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_792_567_650);   // Wed, 21 Oct 2026 07:27:30 GMT
        Assert.Equal(10, DownloadRules.RetryAfter("Wed, 21 Oct 2026 07:27:40 GMT", null, now));
    }
}
