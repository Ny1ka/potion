using System.Text.Json;

namespace Potion.Core.Formats;

/// <summary>
/// Every format Potion understands, and the logic that identifies a file by its contents.
/// Loaded from formats.json (core/spec, embedded in this assembly). The algorithm is specified in
/// core/spec/README.md and is implemented identically in core/swift.
/// </summary>
public sealed class FormatRegistry
{
    private static readonly Lazy<FormatRegistry> SharedInstance = new(Bundled);

    /// <summary>The registry built from the formats.json embedded in Potion.Core.</summary>
    public static FormatRegistry Shared => SharedInstance.Value;

    /// <summary>All formats, in spec order.</summary>
    public IReadOnlyList<FileFormat> Formats { get; }

    /// <summary>How many bytes from the start of a file detection reads.</summary>
    public int HeaderSize { get; }

    private readonly IReadOnlyList<DetectionRule> _rules;
    private readonly Dictionary<string, FileFormat> _byId = new();
    private readonly Dictionary<string, FileFormat> _byExtension = new();

    public static FormatRegistry Bundled()
    {
        using var stream = typeof(FormatRegistry).Assembly.GetManifestResourceStream("Potion.Core.formats.json")
            ?? throw new FormatSpecException("formats.json is missing from Potion.Core");
        using var reader = new StreamReader(stream);
        return new FormatRegistry(reader.ReadToEnd());
    }

    public FormatRegistry(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new FormatSpecException($"formats.json can't be parsed: {e.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) throw new FormatSpecException("Unsupported formats.json version");
            HeaderSize = root.GetProperty("headerSize").GetInt32();
            if (HeaderSize <= 0) throw new FormatSpecException("headerSize must be positive");

            var formats = new List<FileFormat>();
            foreach (var f in root.GetProperty("formats").EnumerateArray())
            {
                var format = ParseFormat(f);
                if (!_byId.TryAdd(format.Id, format)) throw new FormatSpecException($"Duplicate format id {format.Id}");
                if (format.Extensions.Count == 0) throw new FormatSpecException($"{format.Id} has no extensions");
                foreach (var ext in format.Extensions) _byExtension.TryAdd(ext, format);
                formats.Add(format);
            }
            Formats = formats;

            var rules = root.GetProperty("rules").EnumerateArray().Select(DetectionRule.Parse).ToList();
            foreach (var rule in rules)
            {
                if (!_byId.ContainsKey(rule.Format)) throw new FormatSpecException($"Rule refers to unknown format {rule.Format}");
                foreach (var id in rule.ExtensionHint.Where(id => !_byId.ContainsKey(id)))
                    throw new FormatSpecException($"Rule for {rule.Format} hints unknown format {id}");
            }
            _rules = rules;
        }
    }

    private static FileFormat ParseFormat(JsonElement f)
    {
        static IReadOnlyList<string> Strings(JsonElement e, string name) =>
            e.TryGetProperty(name, out var a) ? a.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
        static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var b) && b.GetBoolean();

        var category = f.GetProperty("category").GetString() switch
        {
            "image" => FormatCategory.Image,
            "audio" => FormatCategory.Audio,
            "video" => FormatCategory.Video,
            "docs" => FormatCategory.Docs,
            var other => throw new FormatSpecException($"Unknown category {other}"),
        };
        return new FileFormat(
            f.GetProperty("id").GetString()!,
            f.GetProperty("name").GetString()!,
            category,
            Strings(f, "extensions"),
            Strings(f, "mimeTypes"),
            Flag(f, "text"),
            Flag(f, "raw"));
    }

    public FileFormat? FormatById(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The format that owns an extension (case-insensitive, with or without the leading dot).</summary>
    public FileFormat? FormatForExtension(string ext) =>
        _byExtension.GetValueOrDefault(ext.TrimStart('.').ToLowerInvariant());

    /// <summary>The lowercased extension of a file name, or null (".png" and "README" have none).</summary>
    public static string? FileExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1) return null;
        return fileName[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>Reads the start of the file at <paramref name="path"/> and identifies it.</summary>
    public FormatDetection Detect(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[HeaderSize];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        return Detect(buffer.AsSpan(0, read), Path.GetFileName(path));
    }

    /// <summary>Identifies a file from its first bytes and its name.</summary>
    public FormatDetection Detect(ReadOnlySpan<byte> header, string fileName)
    {
        if (header.Length > HeaderSize) header = header[..HeaderSize];
        var ext = FileExtension(fileName);
        var extFormat = ext is null ? null : _byExtension.GetValueOrDefault(ext);

        FormatDetection Result(FileFormat? format, DetectionMethod method) =>
            new(format, method, extFormat, format is not null && ext is not null && !format.Extensions.Contains(ext));

        // 1. Empty file.
        if (header.IsEmpty) return Result(null, DetectionMethod.None);
        // 2–3. Text with a text extension: trust the extension.
        var isText = LooksLikeText(header);
        if (isText && extFormat is { IsText: true }) return Result(extFormat, DetectionMethod.Extension);
        // 4. Content rules, first match wins.
        foreach (var rule in _rules)
        {
            if (!rule.Matches(header, extFormat?.Id)) continue;
            if (extFormat is not null && rule.ExtensionHint.Contains(extFormat.Id)) return Result(extFormat, DetectionMethod.Content);
            return Result(_byId[rule.Format], DetectionMethod.Content);
        }
        // 5. Extension fallback when the header's textness agrees with the format.
        if (extFormat is not null && extFormat.IsText == isText) return Result(extFormat, DetectionMethod.Extension);
        // 6. Not supported.
        return Result(null, DetectionMethod.None);
    }

    /// <summary>
    /// Text if it starts with a UTF-16 byte-order mark, or has no control bytes other than
    /// tab, line feed, vertical tab, form feed, carriage return and escape.
    /// </summary>
    internal static bool LooksLikeText(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 2 && ((header[0] == 0xFF && header[1] == 0xFE) || (header[0] == 0xFE && header[1] == 0xFF)))
            return true;
        foreach (var b in header)
        {
            if (b <= 0x08 || b is >= 0x0E and <= 0x1A || b is >= 0x1C and <= 0x1F) return false;
        }
        return true;
    }
}
