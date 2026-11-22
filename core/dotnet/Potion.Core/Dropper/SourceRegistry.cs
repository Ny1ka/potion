using System.Text.Json;
using Potion.Core.Formats;

namespace Potion.Core.Dropper;

/// <summary>A link Potion Dropper can try to download.</summary>
/// <param name="SourceId">The source adapter that accepted the link (an id in sources.json).</param>
/// <param name="FormatId">The media format the link points to, when its file name says so.</param>
/// <param name="NeedsCheck">True when only the server's response can tell whether this is a media file (no media extension).</param>
public sealed record DropLink(LinkParts Parts, string SourceId, string? FormatId, bool NeedsCheck)
{
    /// <summary>The normalized link, ASCII only.</summary>
    public string Text => Parts.ToString();
    public string Host => Parts.Host;
    public Uri Uri => new(Text, UriKind.Absolute);
}

/// <summary>A word in pasted text that looked like a link but can't be used.</summary>
public sealed record LinkProblem(string Input, LinkError Error);

/// <summary>The links found in pasted or dropped text.</summary>
/// <param name="Links">Usable links in the order they appeared, without duplicates.</param>
/// <param name="OverLimit">Usable links left out because the paste had more than MaxLinksPerPaste.</param>
public sealed record LinkList(IReadOnlyList<DropLink> Links, IReadOnlyList<LinkProblem> Problems, int OverLimit);

/// <summary>One allowlisted source from sources.json. Each needs an entry in docs/SOURCES.md.</summary>
/// <param name="Categories">The kinds of media this source provides.</param>
/// <param name="Reviewed">When the source's terms were last reviewed (YYYY-MM-DD).</param>
public sealed record SourceInfo(string Id, string Name, IReadOnlyList<FormatCategory> Categories, string Reviewed);

/// <summary>Naming settings from sources.json "naming".</summary>
/// <param name="FallbackName">Used when neither the template nor the title leaves a usable name.</param>
/// <param name="ReserveForSuffix">Characters kept free for " 2" and the extension.</param>
public sealed record NamingSettings(string DefaultTemplate, string FallbackName, int ReserveForSuffix);

/// <summary>What a source adapter decided about a link: accept (with the format, if known) or reject.</summary>
public sealed record SourceMatch(LinkError? Error, string? FormatId = null, bool NeedsCheck = false)
{
    public static SourceMatch Accept(string? formatId, bool needsCheck) => new(null, formatId, needsCheck);
    public static SourceMatch Reject(LinkError error) => new(error);
}

/// <summary>
/// A source module: decides whether it handles a link. Public adapters live in Dropper/SourceAdapters and are listed
/// in <see cref="SourceAdapters.BuiltIn"/>. An adapter runs only if sources.json lists its id.
/// </summary>
public interface ISourceAdapter
{
    string Id { get; }
    /// <summary>Null when the link isn't for this source, so the next adapter is asked.</summary>
    SourceMatch? Match(LinkParts link, SourceInfo source, FormatRegistry formats);
}

/// <summary>
/// The Dropper source allowlist (core/spec/sources.json) and link parsing (core/spec/README.md, "Links").
/// core/swift implements the same behavior and runs the same cases.
/// </summary>
public sealed class SourceRegistry
{
    private static readonly Lazy<SourceRegistry> SharedInstance = new(Bundled);
    public static SourceRegistry Shared => SharedInstance.Value;

    /// <summary>Allowlisted sources in matching order.</summary>
    public IReadOnlyList<SourceInfo> Sources { get; }
    public int MaxLinkLength { get; }
    public int MaxLinksPerPaste { get; }
    public IReadOnlySet<string> PlaylistExtensions { get; }
    public DownloadSettings Download { get; }
    public NamingSettings Naming { get; }

    private readonly string _userAgent;
    private readonly Dictionary<string, string> _messages;
    private readonly List<(SourceInfo Source, ISourceAdapter Adapter)> _adapters = [];
    private readonly FormatRegistry _formats;

    public static SourceRegistry Bundled()
    {
        using var stream = typeof(SourceRegistry).Assembly.GetManifestResourceStream("Potion.Core.sources.json")
            ?? throw new FormatSpecException("sources.json is missing from Potion.Core");
        using var reader = new StreamReader(stream);
        return new SourceRegistry(reader.ReadToEnd());
    }

    public SourceRegistry(string json, FormatRegistry? formats = null, IEnumerable<ISourceAdapter>? adapters = null)
    {
        _formats = formats ?? FormatRegistry.Shared;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new FormatSpecException($"sources.json can't be parsed: {e.Message}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) throw new FormatSpecException("Unsupported sources.json version");
            _userAgent = root.GetProperty("userAgent").GetString()!;
            var limits = root.GetProperty("limits");
            MaxLinkLength = limits.GetProperty("maxLinkLength").GetInt32();
            MaxLinksPerPaste = limits.GetProperty("maxLinksPerPaste").GetInt32();
            if (MaxLinkLength <= 0 || MaxLinksPerPaste <= 0) throw new FormatSpecException("Limits must be positive");
            PlaylistExtensions = root.GetProperty("playlistExtensions").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
            _messages = root.GetProperty("messages").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            foreach (var id in Enum.GetValues<LinkError>().Select(Id).Concat(DownloadError.All.Select(e => e.Id)).Append("tooLong"))
                if (!_messages.ContainsKey(id)) throw new FormatSpecException($"No message for {id}");
            Download = ParseDownload(root.GetProperty("download"));
            var naming = root.GetProperty("naming");
            Naming = new NamingSettings(naming.GetProperty("defaultTemplate").GetString()!, naming.GetProperty("fallbackName").GetString()!,
                naming.GetProperty("reserveForSuffix").GetInt32());

            var byId = new Dictionary<string, ISourceAdapter>();
            foreach (var adapter in adapters ?? SourceAdapters.BuiltIn)
                if (!byId.TryAdd(adapter.Id, adapter)) throw new FormatSpecException($"Two adapters use the id {adapter.Id}");
            var sources = new List<SourceInfo>();
            foreach (var s in root.GetProperty("sources").EnumerateArray())
            {
                var source = new SourceInfo(
                    s.GetProperty("id").GetString()!,
                    s.GetProperty("name").GetString()!,
                    s.GetProperty("categories").EnumerateArray().Select(c => Enum.Parse<FormatCategory>(c.GetString()!, ignoreCase: true)).ToList(),
                    s.GetProperty("reviewed").GetString()!);
                if (sources.Any(x => x.Id == source.Id)) throw new FormatSpecException($"Duplicate source {source.Id}");
                if (!byId.Remove(source.Id, out var adapter)) throw new FormatSpecException($"No adapter for source {source.Id}");
                sources.Add(source);
                _adapters.Add((source, adapter));
            }
            // No adapter without an allowlist entry (and a docs/SOURCES.md entry, checked by tests).
            if (byId.Keys.Order(StringComparer.Ordinal).FirstOrDefault() is { } stray)
                throw new FormatSpecException($"Adapter {stray} isn't in sources.json");
            Sources = sources;
        }
    }

    private static DownloadSettings ParseDownload(JsonElement d)
    {
        var types = d.GetProperty("contentTypes");
        IReadOnlySet<string> Set(string name) => types.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        return new DownloadSettings(
            d.GetProperty("maxRedirects").GetInt32(), d.GetProperty("timeoutSeconds").GetInt32(), d.GetProperty("retries").GetInt32(),
            d.GetProperty("defaultRetrySeconds").GetInt32(), d.GetProperty("maxRetrySeconds").GetInt32(), d.GetProperty("maxRobotsBytes").GetInt32(),
            d.GetProperty("defaultMaxBytes").GetInt64(), d.GetProperty("defaultMaxDurationMinutes").GetInt32(), d.GetProperty("maxFileNameLength").GetInt32(),
            Set("playlist"), Set("media"), Set("generic"), Set("page"));
    }

    /// <summary>The product token robots.txt groups are matched against ("PotionDropper").</summary>
    public string RobotsAgent => new(_userAgent.TakeWhile(c => c is not ('/' or ' ')).ToArray());

    /// <summary>A message by its id in sources.json.</summary>
    public string Message(string id) => _messages.GetValueOrDefault(id, "");

    /// <summary>The text shown for a download error.</summary>
    public string Message(DownloadError error) => _messages[error.Id];

    /// <summary>The id used in sources.json and the shared test cases ("notALink").</summary>
    public static string Id(LinkError error) => JsonNamingPolicy.CamelCase.ConvertName(error.ToString());

    /// <summary>The honest User-Agent sent with every request.</summary>
    public string UserAgent(string version) => _userAgent.Replace("{version}", version, StringComparison.Ordinal);

    /// <summary>The text shown for a link error (follows the design system's voice rules).</summary>
    public string Message(LinkError error) => _messages[Id(error)];

    /// <summary>Parses one link, as typed into the link field.</summary>
    public (DropLink? Link, LinkError? Error) Parse(string input)
    {
        var (parts, error) = LinkSyntax.Normalize(input, MaxLinkLength);
        if (parts is null) return (null, error);
        if (parts.FileExtension is { } ext && PlaylistExtensions.Contains(ext)) return (null, LinkError.Playlist);
        foreach (var (source, adapter) in _adapters)
        {
            if (adapter.Match(parts, source, _formats) is not { } match) continue;
            return match.Error is { } rejected ? (null, rejected) : (new DropLink(parts, source.Id, match.FormatId, match.NeedsCheck), null);
        }
        return (null, LinkError.UnsupportedSite);
    }

    /// <summary>
    /// The link a redirect points to: location resolved against the link that answered, then parsed and matched like
    /// a typed link (so a redirect can't lead outside the allowlist).
    /// </summary>
    public (DropLink? Link, LinkError? Error) Redirect(DropLink from, string location) => Parse(LinkSyntax.Resolve(location, from.Parts));

    internal FormatRegistry Formats => _formats;

    /// <summary>
    /// Finds the links in pasted or dropped text: words separated by whitespace, with surrounding quotes, brackets and
    /// sentence punctuation removed. Words that don't look like links are ignored.
    /// </summary>
    public LinkList FindLinks(string text)
    {
        var links = new List<DropLink>();
        var problems = new List<LinkProblem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var overLimit = 0;
        foreach (var word in SplitWhitespace(text))
        {
            var token = TrimPunctuation(word);
            if (token.Length == 0 || !LinkSyntax.IsCandidate(token)) continue;
            var (link, error) = Parse(token);
            if (link is null) problems.Add(new LinkProblem(token, error!.Value));
            else if (!seen.Add(link.Text)) continue;
            else if (links.Count < MaxLinksPerPaste) links.Add(link);
            else overLimit++;
        }
        return new LinkList(links, problems, overLimit);
    }

    private static IEnumerable<string> SplitWhitespace(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var space = i == text.Length || char.IsWhiteSpace(text[i]);
            if (space && start >= 0) { yield return text[start..i]; start = -1; }
            else if (!space && start < 0) start = i;
        }
    }

    private const string Leading = "<([{\"'“‘«";
    private const string Trailing = ">)]}\"'.,;:!?”’»";

    internal static string TrimPunctuation(string word) => word.TrimStart(Leading.ToCharArray()).TrimEnd(Trailing.ToCharArray());
}
