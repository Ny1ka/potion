using System.Globalization;
using System.Text;
using Potion.Core.Formats;

namespace Potion.Core.Dropper;

/// <summary>Why a download failed. <see cref="Id"/> is the key in core/spec/sources.json "messages" and in the shared cases.</summary>
/// <param name="Link">Set when the link (or a redirect target) isn't usable, or the server's answer isn't media.</param>
public sealed record DownloadError(string Id, LinkError? Link = null)
{
    public static readonly DownloadError AccessDenied = new("accessDenied");
    public static readonly DownloadError NotFound = new("notFound");
    public static readonly DownloadError UnavailableForLegalReasons = new("unavailableForLegalReasons");
    public static readonly DownloadError RateLimited = new("rateLimited");
    public static readonly DownloadError ServerError = new("serverError");
    public static readonly DownloadError Refused = new("refused");
    public static readonly DownloadError TooLarge = new("tooLarge");
    public static readonly DownloadError RobotsDisallowed = new("robotsDisallowed");
    public static readonly DownloadError TooManyRedirects = new("tooManyRedirects");
    public static readonly DownloadError Network = new("network");
    public static readonly DownloadError TimedOut = new("timedOut");
    public static readonly DownloadError Interrupted = new("interrupted");

    public static IReadOnlyList<DownloadError> All { get; } =
    [
        AccessDenied, NotFound, UnavailableForLegalReasons, RateLimited, ServerError, Refused, TooLarge,
        RobotsDisallowed, TooManyRedirects, Network, TimedOut, Interrupted,
    ];

    public static DownloadError From(LinkError error) => new(SourceRegistry.Id(error), error);
}

/// <summary>Download settings from sources.json "download".</summary>
public sealed record DownloadSettings(
    int MaxRedirects,
    int TimeoutSeconds,
    int Retries,
    int DefaultRetrySeconds,
    int MaxRetrySeconds,
    int MaxRobotsBytes,
    long DefaultMaxBytes,
    int MaxFileNameLength,
    IReadOnlySet<string> PlaylistTypes,
    IReadOnlySet<string> MediaTypes,
    IReadOnlySet<string> GenericTypes,
    IReadOnlySet<string> PageTypes);

public enum VerdictKind { Accept, Redirect, Retry, Restart, Fail }

/// <summary>What to do with a server's answer (core/spec/README.md, "Downloads").</summary>
/// <param name="Sniff">Accept: only the first bytes can tell whether it's media.</param>
/// <param name="Length">Accept: bytes in this response, when the server says.</param>
/// <param name="Resumed">Accept: the body continues a partial file.</param>
/// <param name="Location">Redirect: the Location header (possibly relative), checked against the allowlist again.</param>
/// <param name="Seconds">Retry: ask again after this many seconds.</param>
public sealed record ResponseVerdict(
    VerdictKind Kind, bool Sniff = false, long? Length = null, bool Resumed = false,
    string? Location = null, int Seconds = 0, DownloadError? Error = null)
{
    public static ResponseVerdict Fail(DownloadError error) => new(VerdictKind.Fail, Error: error);
}

/// <summary>How to treat the answer to a robots.txt request (RFC 9309, section 2.3.1).</summary>
public enum RobotsFetch { Parse, AllowAll, DisallowAll }

/// <summary>The pure decisions of a download, shared with core/swift and covered by core/spec/tests/downloads.json.</summary>
public static class DownloadRules
{
    public static RobotsFetch RobotsFetchFor(int status) => status switch
    {
        >= 200 and < 300 => RobotsFetch.Parse,
        >= 400 and < 500 => RobotsFetch.AllowAll,
        _ => RobotsFetch.DisallowAll,
    };

    /// <summary>Decides what to do with a response. Header names are matched without regard to case.</summary>
    public static ResponseVerdict Classify(int status, IReadOnlyDictionary<string, string> headers, bool rangeRequested, long maxBytes, DownloadSettings settings)
    {
        var lower = new Dictionary<string, string>();
        foreach (var (key, value) in headers) lower[key.ToLowerInvariant()] = value;
        string? Header(string name) => lower.GetValueOrDefault(name);

        switch (status)
        {
            case 200 or 206:
                if (status == 206 && !rangeRequested) return ResponseVerdict.Fail(DownloadError.Refused);
                var type = (Header("content-type") ?? "").Split(';', 2)[0].Trim().ToLowerInvariant();
                if (settings.PlaylistTypes.Contains(type)) return ResponseVerdict.Fail(DownloadError.From(LinkError.Playlist));
                if (settings.PageTypes.Contains(type)) return ResponseVerdict.Fail(DownloadError.From(LinkError.UnsupportedSite));
                bool sniff;
                if (type.StartsWith("audio/", StringComparison.Ordinal) || type.StartsWith("video/", StringComparison.Ordinal) || settings.MediaTypes.Contains(type)) sniff = false;
                else if (type.Length == 0 || settings.GenericTypes.Contains(type)) sniff = true;
                else return ResponseVerdict.Fail(DownloadError.From(LinkError.NotMedia));
                long? length = long.TryParse(Header("content-length")?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : null;
                if (length > maxBytes) return ResponseVerdict.Fail(DownloadError.TooLarge);
                return new ResponseVerdict(VerdictKind.Accept, sniff, length, status == 206);
            case 301 or 302 or 303 or 307 or 308:
                var location = Header("location")?.Trim() ?? "";
                return location.Length == 0 ? ResponseVerdict.Fail(DownloadError.Refused) : new ResponseVerdict(VerdictKind.Redirect, Location: location);
            case 401 or 403 or 407: return ResponseVerdict.Fail(DownloadError.AccessDenied);
            case 404 or 410: return ResponseVerdict.Fail(DownloadError.NotFound);
            case 416: return rangeRequested ? new ResponseVerdict(VerdictKind.Restart) : ResponseVerdict.Fail(DownloadError.Refused);
            case 451: return ResponseVerdict.Fail(DownloadError.UnavailableForLegalReasons);
            case 429:
            {
                var seconds = RetryAfter(Header("retry-after"), Header("date"), DateTimeOffset.UtcNow) ?? settings.DefaultRetrySeconds;
                return seconds <= settings.MaxRetrySeconds ? new ResponseVerdict(VerdictKind.Retry, Seconds: seconds) : ResponseVerdict.Fail(DownloadError.RateLimited);
            }
            case 503:
            {
                if (RetryAfter(Header("retry-after"), Header("date"), DateTimeOffset.UtcNow) is not { } seconds) return ResponseVerdict.Fail(DownloadError.ServerError);
                return seconds <= settings.MaxRetrySeconds ? new ResponseVerdict(VerdictKind.Retry, Seconds: seconds) : ResponseVerdict.Fail(DownloadError.ServerError);
            }
            case >= 500 and < 600: return ResponseVerdict.Fail(DownloadError.ServerError);
            default: return ResponseVerdict.Fail(DownloadError.Refused);
        }
    }

    /// <summary>Retry-After in seconds: a number, or an HTTP date measured from the response's Date (or now).</summary>
    internal static int? RetryAfter(string? value, string? date, DateTimeOffset now)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.All(char.IsAsciiDigit)) return value.Length > 9 ? int.MaxValue : int.Parse(value, CultureInfo.InvariantCulture);
        if (HttpDate(value) is not { } target) return null;
        var reference = (date is null ? null : HttpDate(date)) ?? now;
        return Math.Max(0, (int)Math.Ceiling((target - reference).TotalSeconds));
    }

    private static DateTimeOffset? HttpDate(string text) =>
        DateTimeOffset.TryParseExact(text.Trim(), "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>Checks the first bytes of a body the server didn't label as media: the media format id, or why not.</summary>
    public static (string? FormatId, DownloadError? Error) Sniff(ReadOnlySpan<byte> header, string fileName, FormatRegistry? formats = null)
    {
        formats ??= FormatRegistry.Shared;
        var bytes = header[..Math.Min(header.Length, formats.HeaderSize)];
        if (bytes.IsEmpty) return (null, DownloadError.From(LinkError.NotMedia));
        var detection = formats.Detect(bytes, fileName);
        if (detection.Format is { } format)
        {
            if (format.Category is FormatCategory.Audio or FormatCategory.Video) return (format.Id, null);
            return (null, DownloadError.From(format.IsText ? LinkError.UnsupportedSite : LinkError.NotMedia));
        }
        // Text that isn't a known format is almost always an error or sign-in page.
        return (null, DownloadError.From(FormatRegistry.LooksLikeText(bytes) ? LinkError.UnsupportedSite : LinkError.NotMedia));
    }

    /// <summary>
    /// The name to save a download under (before the extension is checked against the detected format):
    /// filename* (RFC 8187), then filename from Content-Disposition, then the link's file name, then the host.
    /// </summary>
    public static string FileName(string? contentDisposition, LinkParts link, int maxLength)
    {
        if (contentDisposition is not null)
        {
            var parameters = DispositionParameters(contentDisposition);
            if (parameters.TryGetValue("filename*", out var ext) && DecodeExtended(ext) is { } extended && Sanitize(LastComponent(extended), maxLength) is { } a) return a;
            if (parameters.TryGetValue("filename", out var plain) && Sanitize(LastComponent(plain), maxLength) is { } b) return b;
        }
        return Sanitize(link.FileName, maxLength) ?? link.Host;
    }

    /// <summary>Parameters after the disposition type, names lowercased, quoted strings unescaped. The first of a name wins.</summary>
    internal static Dictionary<string, string> DispositionParameters(string header)
    {
        var result = new Dictionary<string, string>();
        var i = header.IndexOf(';');
        if (i < 0) return result;
        while (i < header.Length)
        {
            while (i < header.Length && (header[i] == ';' || char.IsWhiteSpace(header[i]))) i++;
            var name = new StringBuilder();
            while (i < header.Length && header[i] != '=' && header[i] != ';') name.Append(header[i++]);
            if (i >= header.Length || header[i] != '=') continue;
            i++;
            while (i < header.Length && header[i] is ' ' or '\t') i++;
            var value = new StringBuilder();
            if (i < header.Length && header[i] == '"')
            {
                i++;
                while (i < header.Length && header[i] != '"')
                {
                    if (header[i] == '\\' && i + 1 < header.Length) i++;
                    value.Append(header[i++]);
                }
                i++;
            }
            else
            {
                while (i < header.Length && header[i] != ';') value.Append(header[i++]);
                value = new StringBuilder(value.ToString().Trim(' ', '\t'));
            }
            var key = name.ToString().Trim(' ', '\t').ToLowerInvariant();
            if (key.Length > 0) result.TryAdd(key, value.ToString());
        }
        return result;
    }

    /// <summary>charset'language'percent-encoded with UTF-8 or ISO-8859-1.</summary>
    internal static string? DecodeExtended(string value)
    {
        var parts = value.Split('\'', 3);
        if (parts.Length != 3) return null;
        var encoded = Encoding.UTF8.GetBytes(parts[2]);
        var bytes = new List<byte>();
        for (var i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '%' && i + 2 < encoded.Length &&
                byte.TryParse(Encoding.ASCII.GetString(encoded, i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else bytes.Add(encoded[i]);
        }
        return parts[0].ToLowerInvariant() switch
        {
            "utf-8" => Encoding.UTF8.GetString(bytes.ToArray()),
            "iso-8859-1" => Encoding.Latin1.GetString(bytes.ToArray()),
            _ => null,
        };
    }

    private static string LastComponent(string name) => name[(name.LastIndexOfAny(['/', '\\']) + 1)..];

    private const string ReservedCharacters = "<>:\"/\\|?*";
    private static readonly HashSet<string> ReservedNames =
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })];

    /// <summary>A file name that's safe on macOS and Windows, or null when nothing usable is left.</summary>
    internal static string? Sanitize(string name, int maxLength)
    {
        var runes = new List<Rune>();
        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Value < 0x20 || rune.Value == 0x7F) continue;
            runes.Add(rune.IsAscii && ReservedCharacters.Contains((char)rune.Value) ? new Rune('_') : rune);
        }
        static bool Trimmable(Rune r) => r.Value == '.' || Rune.IsWhiteSpace(r);
        while (runes.Count > 0 && Trimmable(runes[0])) runes.RemoveAt(0);
        while (runes.Count > 0 && Trimmable(runes[^1])) runes.RemoveAt(runes.Count - 1);
        if (runes.Count == 0) return null;

        var stem = string.Concat(runes.TakeWhile(r => r.Value != '.').Select(r => r.ToString()));
        if (ReservedNames.Contains(stem.ToUpperInvariant())) runes.Insert(0, new Rune('_'));

        if (runes.Count > maxLength)
        {
            var ext = new List<Rune>();
            var dot = runes.FindLastIndex(r => r.Value == '.');
            if (dot > 0 && runes.Count - dot <= 11) ext = runes.GetRange(dot, runes.Count - dot);
            var head = runes.Take(maxLength - ext.Count).ToList();
            while (head.Count > 0 && Trimmable(head[^1])) head.RemoveAt(head.Count - 1);
            runes = [.. head, .. ext];
        }
        return string.Concat(runes.Select(r => r.ToString()));
    }
}

/// <summary>robots.txt rules (RFC 9309) for one site, matched against Potion Dropper's product token.</summary>
public sealed class RobotsRules
{
    private sealed record Rule(bool Allow, byte[] Pattern);
    private sealed class Group { public List<string> Agents { get; } = []; public List<Rule> Rules { get; } = []; }

    private readonly List<Group>? _groups;
    private readonly bool _allowWithoutRules;

    public static RobotsRules AllowAll { get; } = new(null, true);
    public static RobotsRules DisallowAll { get; } = new(null, false);

    private RobotsRules(List<Group>? groups, bool allow) { _groups = groups; _allowWithoutRules = allow; }

    public RobotsRules(string text)
    {
        var groups = new List<Group>();
        var lastWasAgent = false;
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Split('#', 2)[0];
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "user-agent":
                    if (!lastWasAgent) groups.Add(new Group());
                    groups[^1].Agents.Add(AgentToken(value));
                    lastWasAgent = true;
                    break;
                case "allow" or "disallow":
                    lastWasAgent = false;
                    if (groups.Count == 0 || value.Length == 0) continue;
                    groups[^1].Rules.Add(new Rule(key == "allow", Encoding.UTF8.GetBytes(LinkSyntax.PercentEncode(value, allowQuestionMark: true))));
                    break;
            }
        }
        _groups = groups;
        _allowWithoutRules = true;
    }

    /// <summary>"*" or the product token at the start of a user-agent line, lowercased ("PotionDropper/2.0" → "potiondropper").</summary>
    private static string AgentToken(string value) =>
        value == "*" ? "*" : new string(value.TakeWhile(c => char.IsAsciiLetter(c) || c is '-' or '_').ToArray()).ToLowerInvariant();

    /// <summary>Whether path (a normalized path with its query) may be fetched by agent.</summary>
    public bool IsAllowed(string path, string agent)
    {
        if (path == "/robots.txt") return true;
        if (_groups is null) return _allowWithoutRules;
        var token = agent.ToLowerInvariant();
        var selected = _groups.Where(g => g.Agents.Contains(token)).ToList();
        if (selected.Count == 0) selected = _groups.Where(g => g.Agents.Contains("*")).ToList();
        var target = Encoding.UTF8.GetBytes(path);
        Rule? best = null;
        foreach (var rule in selected.SelectMany(g => g.Rules).Where(r => Matches(r.Pattern, target)))
        {
            if (best is null || rule.Pattern.Length > best.Pattern.Length || (rule.Pattern.Length == best.Pattern.Length && rule.Allow)) best = rule;
        }
        return best?.Allow ?? true;
    }

    /// <summary>"*" matches any run of bytes; a final "$" anchors the end; otherwise the pattern is a prefix.</summary>
    internal static bool Matches(byte[] pattern, byte[] path)
    {
        var p = new List<byte>(pattern);
        var anchored = p.Count > 0 && p[^1] == '$';
        if (anchored) p.RemoveAt(p.Count - 1); else p.Add((byte)'*');
        int pi = 0, si = 0, starAt = -1, matchAt = 0;
        while (si < path.Length)
        {
            if (pi < p.Count && p[pi] != '*' && p[pi] == path[si]) { pi++; si++; }
            else if (pi < p.Count && p[pi] == '*') { starAt = pi; matchAt = si; pi++; }
            else if (starAt >= 0) { pi = starAt + 1; matchAt++; si = matchAt; }
            else return false;
        }
        while (pi < p.Count && p[pi] == '*') pi++;
        return pi == p.Count;
    }
}
