using System.Text;
using System.Text.Json;
using Potion.Core.Formats;
using Xunit;

/// <summary>Runs the shared cases in core/spec/tests/detection.json. core/swift runs the same file.</summary>
public class DetectionConformanceTests
{
    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases().RootElement.GetProperty("cases").EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    [Fact]
    public void HasEnoughCases() => Assert.True(Cases().RootElement.GetProperty("cases").GetArrayLength() > 50);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void SharedDetectionCase(string name)
    {
        var c = Cases().RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var bytes = new List<byte>();
        foreach (var s in c.GetProperty("bytes").EnumerateArray())
        {
            if (s.TryGetProperty("hex", out var hex)) bytes.AddRange(Convert.FromHexString(hex.GetString()!));
            if (s.TryGetProperty("ascii", out var ascii)) bytes.AddRange(Encoding.UTF8.GetBytes(ascii.GetString()!));
            if (s.TryGetProperty("zeros", out var zeros)) bytes.AddRange(new byte[zeros.GetInt32()]);
        }

        var result = FormatRegistry.Shared.Detect(bytes.ToArray(), c.GetProperty("file").GetString()!);
        var expect = c.GetProperty("expect");
        var expectedFormat = expect.GetProperty("format").ValueKind == JsonValueKind.Null ? null : expect.GetProperty("format").GetString();

        Assert.Equal(expectedFormat, result.Format?.Id);
        Assert.Equal(expect.GetProperty("method").GetString(), result.Method.ToString().ToLowerInvariant());
        if (expect.TryGetProperty("mismatch", out var mismatch)) Assert.Equal(mismatch.GetBoolean(), result.ExtensionMismatch);
    }

    private static JsonDocument Cases() => JsonDocument.Parse(RepoFiles.Text("core/spec/tests/detection.json"));
}
