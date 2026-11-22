using System.Text.Json;

namespace Potion.Core.Formats;

/// <summary>What an engine does to produce an output. Engines (M2) dispatch on this.</summary>
public enum ConversionOperation
{
    TranscodeVideo, VideoToGif, ExtractAudio, TranscodeAudio,
    ConvertImage, ImageToPdf, GifToVideo,
    ConvertDocument, DocumentToPdf, PdfToDocument, PdfToImage,
}

/// <summary>A limitation shown to the user before converting, e.g. "Only the text is kept."</summary>
public sealed record ConversionNote(string Id, string Text);

/// <summary>One output a file can be converted into.</summary>
/// <param name="Notes">Limitations to show before converting, in spec order. Empty for clean conversions.</param>
public sealed record ConversionOption(FileFormat Output, ConversionOperation Operation, IReadOnlyList<ConversionNote> Notes);

/// <summary>
/// Which conversions are allowed, loaded from matrix.json (core/spec, embedded in this assembly). Only
/// conversions listed here are ever offered. The rules are specified in core/spec/README.md and implemented
/// identically in core/swift.
/// </summary>
public sealed class ConversionMatrix
{
    private static readonly Lazy<ConversionMatrix> SharedInstance = new(() =>
    {
        using var stream = typeof(ConversionMatrix).Assembly.GetManifestResourceStream("Potion.Core.matrix.json")
            ?? throw new FormatSpecException("matrix.json is missing from Potion.Core");
        using var reader = new StreamReader(stream);
        return new ConversionMatrix(reader.ReadToEnd(), FormatRegistry.Shared);
    });

    /// <summary>The matrix built from the embedded matrix.json, over <see cref="FormatRegistry.Shared"/>.</summary>
    public static ConversionMatrix Shared => SharedInstance.Value;

    public FormatRegistry Registry { get; }

    // input id → output id → option
    private readonly Dictionary<string, Dictionary<string, ConversionOption>> _table = new();

    /// <summary>The platform this app runs on ("windows" or "macos").</summary>
    public static string CurrentPlatform => OperatingSystem.IsWindows() ? "windows" : "macos";

    /// <param name="platform">Formats limited to other platforms (formats.json <c>platforms</c>) are left out. Defaults to this one.</param>
    public ConversionMatrix(string json, FormatRegistry registry, string? platform = null)
    {
        platform ??= CurrentPlatform;
        Registry = registry;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("version").GetInt32() != 1) throw new FormatSpecException("Unsupported matrix.json version");

        var groups = root.GetProperty("groups").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(x => x.GetString()!).ToList());
        var noteTexts = root.GetProperty("notes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var routes = root.GetProperty("routes").EnumerateArray().ToList();

        List<string> Resolve(JsonElement refs)
        {
            var ids = new List<string>();
            foreach (var r in refs.EnumerateArray().Select(x => x.GetString()!))
            {
                if (r.StartsWith('#'))
                {
                    var category = r[1..] switch
                    {
                        "image" => FormatCategory.Image,
                        "audio" => FormatCategory.Audio,
                        "video" => FormatCategory.Video,
                        "docs" => FormatCategory.Docs,
                        _ => throw new FormatSpecException($"Unknown category {r}"),
                    };
                    ids.AddRange(registry.Formats.Where(f => f.Category == category).Select(f => f.Id));
                }
                else if (r.StartsWith('@'))
                {
                    ids.AddRange(groups.TryGetValue(r[1..], out var g) ? g : throw new FormatSpecException($"Unknown group {r}"));
                }
                else ids.Add(r);
            }
            foreach (var id in ids.Where(id => registry.FormatById(id) is null)) throw new FormatSpecException($"Unknown format {id}");
            return ids;
        }

        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;

        // Pass 1: operation routes create pairs. Pass 2: every route adds its note to pairs that exist.
        var operations = new Dictionary<(string From, string To), ConversionOperation>();
        var notes = new Dictionary<(string From, string To), List<string>>();
        foreach (var route in routes)
        {
            if (Str(route, "note") is { } note && !noteTexts.ContainsKey(note)) throw new FormatSpecException($"Unknown note {note}");
            if (Str(route, "operation") is not { } name) continue;
            var operation = Enum.GetValues<ConversionOperation>()
                .Where(o => JsonNamingPolicy.CamelCase.ConvertName(o.ToString()) == name)
                .Select(o => (ConversionOperation?)o).FirstOrDefault()
                ?? throw new FormatSpecException($"Unknown operation {name}");
            var sameFormat = route.TryGetProperty("sameFormat", out var sf) && sf.GetBoolean();
            foreach (var from in Resolve(route.GetProperty("from")))
            foreach (var to in Resolve(route.GetProperty("to")).Where(to => to != from || sameFormat))
            {
                if (operations.TryGetValue((from, to), out var existing) && existing != operation)
                    throw new FormatSpecException($"{from} → {to} has two operations: {CamelName(existing)} and {name}");
                operations[(from, to)] = operation;
            }
        }
        foreach (var route in routes)
        {
            if (Str(route, "note") is not { } note) continue;
            foreach (var from in Resolve(route.GetProperty("from")))
            foreach (var to in Resolve(route.GetProperty("to")).Where(to => operations.ContainsKey((from, to))))
            {
                var list = notes.TryGetValue((from, to), out var l) ? l : notes[(from, to)] = [];
                if (!list.Contains(note)) list.Add(note);
            }
        }

        foreach (var ((from, to), operation) in operations)
        {
            if (!registry.FormatById(from)!.IsAvailableOn(platform) || !registry.FormatById(to)!.IsAvailableOn(platform)) continue;
            var noteList = (notes.TryGetValue((from, to), out var n) ? n : []).Select(id => new ConversionNote(id, noteTexts[id])).ToList();
            if (!_table.TryGetValue(from, out var row)) _table[from] = row = new();
            row[to] = new ConversionOption(registry.FormatById(to)!, operation, noteList);
        }
    }

    private static string CamelName(ConversionOperation o) => JsonNamingPolicy.CamelCase.ConvertName(o.ToString());

    /// <summary>The conversion from one format to another, or null when it isn't allowed.</summary>
    public ConversionOption? Conversion(string inputId, string outputId) =>
        _table.TryGetValue(inputId, out var row) ? row.GetValueOrDefault(outputId) : null;

    /// <summary>Everything an input can become: its own category first, then the rest, each in formats.json order.</summary>
    public IReadOnlyList<ConversionOption> Outputs(string inputId)
    {
        if (Registry.FormatById(inputId) is not { } input || !_table.TryGetValue(inputId, out var row)) return [];
        var ordered = Registry.Formats.Select(f => row.GetValueOrDefault(f.Id)).OfType<ConversionOption>().ToList();
        return [.. ordered.Where(o => o.Output.Category == input.Category), .. ordered.Where(o => o.Output.Category != input.Category)];
    }

    /// <summary>
    /// Outputs a whole batch can be converted to. An output qualifies when every input either converts to it
    /// or is already in that format (those files are skipped), and at least one input actually converts.
    /// Ordered like <see cref="Outputs"/>, using the first input's category.
    /// </summary>
    public IReadOnlyList<FileFormat> CommonOutputs(IReadOnlyList<string> inputIds)
    {
        if (inputIds.Count == 0 || Registry.FormatById(inputIds[0]) is not { } first) return [];
        var qualifying = Registry.Formats.Where(output =>
            inputIds.All(i => i == output.Id || Conversion(i, output.Id) is not null)
            && inputIds.Any(i => Conversion(i, output.Id) is not null)).ToList();
        return [.. qualifying.Where(f => f.Category == first.Category), .. qualifying.Where(f => f.Category != first.Category)];
    }
}
