using System.Text.Json;
using Potion.Core.Formats;
using Xunit;

/// <summary>Runs the shared cases in core/spec/tests/matrix.json. core/swift runs the same file.</summary>
public class MatrixConformanceTests
{
    private static readonly JsonElement Cases = JsonDocument.Parse(RepoFiles.Text("core/spec/tests/matrix.json")).RootElement;
    private static readonly ConversionMatrix Matrix = ConversionMatrix.Shared;

    private static TheoryData<string> Names(string section)
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.GetProperty(section).EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
        return data;
    }
    public static TheoryData<string> ConversionNames() => Names("conversions");
    public static TheoryData<string> OutputNames() => Names("outputs");
    public static TheoryData<string> CommonNames() => Names("common");

    private static JsonElement Case(string section, string name) =>
        Cases.GetProperty(section).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
    private static List<string> Strings(JsonElement c, string name) => c.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToList();

    [Theory, MemberData(nameof(ConversionNames))]
    public void Conversion(string name)
    {
        var c = Case("conversions", name);
        var option = Matrix.Conversion(c.GetProperty("from").GetString()!, c.GetProperty("to").GetString()!);
        var expected = c.GetProperty("operation").ValueKind == JsonValueKind.Null ? null : c.GetProperty("operation").GetString();
        Assert.Equal(expected, option is null ? null : JsonNamingPolicy.CamelCase.ConvertName(option.Operation.ToString()));
        if (c.TryGetProperty("notes", out _)) Assert.Equal(Strings(c, "notes"), option?.Notes.Select(n => n.Id).ToList());
    }

    [Theory, MemberData(nameof(OutputNames))]
    public void Outputs(string name)
    {
        var c = Case("outputs", name);
        var ids = Matrix.Outputs(c.GetProperty("from").GetString()!).Select(o => o.Output.Id).ToList();
        Assert.Equal(c.GetProperty("first").GetString(), ids.FirstOrDefault());
        Assert.Empty(Strings(c, "includes").Except(ids));
        Assert.Empty(Strings(c, "excludes").Intersect(ids));
    }

    [Theory, MemberData(nameof(CommonNames))]
    public void CommonOutputs(string name)
    {
        var c = Case("common", name);
        var ids = Matrix.CommonOutputs(Strings(c, "from")).Select(f => f.Id).ToList();
        Assert.Empty(Strings(c, "includes").Except(ids));
        Assert.Empty(Strings(c, "excludes").Intersect(ids));
        if (c.TryGetProperty("empty", out var empty) && empty.GetBoolean()) Assert.Empty(ids);
    }
}

public class ConversionMatrixTests
{
    [Fact]
    public void EveryFormatCanBecomeSomething()
    {
        foreach (var format in FormatRegistry.Shared.Formats)
            Assert.True(ConversionMatrix.Shared.Outputs(format.Id).Count > 0, $"{format.Id} has no outputs");
    }

    [Fact]
    public void UnknownInputHasNoOutputs()
    {
        Assert.Empty(ConversionMatrix.Shared.Outputs("nope"));
        Assert.Empty(ConversionMatrix.Shared.CommonOutputs([]));
    }

    [Fact]
    public void RejectsConflictingOperations()
    {
        const string json = """
            { "version": 1, "groups": {}, "notes": {},
              "routes": [{ "from": ["png"], "to": ["jpg"], "operation": "convertImage" },
                         { "from": ["png"], "to": ["jpg"], "operation": "imageToPdf" }] }
            """;
        var e = Assert.Throws<FormatSpecException>(() => new ConversionMatrix(json, FormatRegistry.Shared));
        Assert.Equal("png → jpg has two operations: convertImage and imageToPdf", e.Message);
    }

    [Theory]
    [InlineData("""{ "from": ["#sound"], "to": ["mp3"], "operation": "transcodeAudio" }""")]
    [InlineData("""{ "from": ["@nope"], "to": ["mp3"], "operation": "transcodeAudio" }""")]
    [InlineData("""{ "from": ["xyz"], "to": ["mp3"], "operation": "transcodeAudio" }""")]
    [InlineData("""{ "from": ["wav"], "to": ["mp3"], "operation": "teleport" }""")]
    [InlineData("""{ "from": ["wav"], "to": ["mp3"], "note": "missing" }""")]
    public void RejectsUnknownReferences(string route) =>
        Assert.Throws<FormatSpecException>(() =>
            new ConversionMatrix($$"""{ "version": 1, "groups": {}, "notes": {}, "routes": [{{route}}] }""", FormatRegistry.Shared));
}
