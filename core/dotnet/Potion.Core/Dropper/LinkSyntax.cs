using System.Text;

namespace Potion.Core.Dropper;

/// <summary>Why a link can't be used. The camelCase names are the ids in core/spec/sources.json "messages".</summary>
public enum LinkError { NotALink, UnsupportedScheme, Credentials, TooLong, InvalidHost, InvalidPort, Playlist, NotMedia, UnsupportedSite }

/// <summary>A link split into normalized parts (core/spec/README.md, "Links"). Source adapters match against these.</summary>
/// <param name="Scheme">"http" or "https".</param>
/// <param name="Host">Lowercase ASCII host (internationalized names in Punycode, IPv6 in brackets).</param>
/// <param name="Port">The port, unless it's the scheme's default.</param>
/// <param name="Path">Percent-encoded path, at least "/".</param>
/// <param name="Query">Percent-encoded query without the "?", or null when there's none.</param>
public sealed record LinkParts(string Scheme, string Host, int? Port, string Path, string? Query)
{
    /// <summary>The normalized link.</summary>
    public override string ToString() =>
        $"{Scheme}://{Host}{(Port is { } p ? $":{p}" : "")}{Path}{(Query is null ? "" : "?" + Query)}";

    /// <summary>The last path segment, percent-decoded ("" when the path ends with "/").</summary>
    public string FileName => LinkSyntax.PercentDecode(Path[(Path.LastIndexOf('/') + 1)..]);

    /// <summary>The file name's extension, lowercased. A name whose only dot is the first character has none.</summary>
    public string? FileExtension
    {
        get
        {
            var name = FileName;
            var dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1) return null;
            return name[(dot + 1)..].ToLowerInvariant();
        }
    }
}

/// <summary>
/// Parses and normalizes links the same way core/swift does, without relying on System.Uri, so both apps accept,
/// reject and de-duplicate exactly the same input.
/// </summary>
internal static class LinkSyntax
{
    public static (LinkParts? Parts, LinkError? Error) Normalize(string raw, int maxLength)
    {
        var text = raw.Trim();
        if (text.Length >= 2 && text[0] == '<' && text[^1] == '>') text = text[1..^1].Trim();
        if (text.Length == 0) return (null, LinkError.NotALink);
        if (Encoding.UTF8.GetByteCount(text) > maxLength * 4) return (null, LinkError.TooLong);

        string scheme, rest;
        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator >= 0 && IsScheme(text[..separator]))
        {
            scheme = text[..separator].ToLowerInvariant();
            rest = text[(separator + 3)..];
        }
        else if (HostInferable(text))
        {
            scheme = "https";
            rest = text;
        }
        else
        {
            var colon = text.IndexOf(':');
            return (null, colon >= 0 && IsScheme(text[..colon]) ? LinkError.UnsupportedScheme : LinkError.NotALink);
        }
        if (scheme is not ("http" or "https")) return (null, LinkError.UnsupportedScheme);

        var authorityEnd = rest.IndexOfAny(['/', '?', '#']);
        if (authorityEnd < 0) authorityEnd = rest.Length;
        var authority = rest[..authorityEnd];
        var remainder = rest[authorityEnd..];
        if (authority.Contains('@')) return (null, LinkError.Credentials);

        string host;
        string? portText;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return (null, LinkError.InvalidHost);
            var literal = authority[1..close];
            if (literal.Length == 0 || !literal.Contains(':') || !literal.All(c => char.IsAsciiHexDigit(c) || c is ':' or '.'))
                return (null, LinkError.InvalidHost);
            host = $"[{literal.ToLowerInvariant()}]";
            var after = authority[(close + 1)..];
            if (after.Length == 0) portText = null;
            else if (after[0] == ':') portText = after[1..];
            else return (null, LinkError.InvalidHost);
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            var hostText = colon >= 0 ? authority[..colon] : authority;
            portText = colon >= 0 ? authority[(colon + 1)..] : null;
            if (NormalizeHost(hostText) is not { } normalized) return (null, LinkError.InvalidHost);
            host = normalized;
        }

        int? port = null;
        if (!string.IsNullOrEmpty(portText))
        {
            if (portText.Length > 5 || !portText.All(char.IsAsciiDigit) || int.Parse(portText) is < 1 or > 65535)
                return (null, LinkError.InvalidPort);
            port = int.Parse(portText);
        }
        if ((scheme == "http" && port == 80) || (scheme == "https" && port == 443)) port = null;

        var hash = remainder.IndexOf('#');
        if (hash >= 0) remainder = remainder[..hash];
        string path;
        string? query = null;
        var mark = remainder.IndexOf('?');
        if (mark >= 0)
        {
            path = PercentEncode(remainder[..mark], allowQuestionMark: false);
            query = PercentEncode(remainder[(mark + 1)..], allowQuestionMark: true);
        }
        else path = PercentEncode(remainder, allowQuestionMark: false);
        if (path.Length == 0) path = "/";
        if (query?.Length == 0) query = null;

        var parts = new LinkParts(scheme, host, port, path, query);
        return parts.ToString().Length > maxLength ? (null, LinkError.TooLong) : (parts, null);
    }

    /// <summary>
    /// Whether a word in pasted text should be treated as a link: it has "://", or it starts with something that
    /// is clearly a host name ("example.com/…", "www.example.org").
    /// </summary>
    public static bool IsCandidate(string token) => token.Contains("://", StringComparison.Ordinal) || HostInferable(token);

    private static bool IsScheme(string text) =>
        text.Length > 0 && char.IsAsciiLetter(text[0]) && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');

    /// <summary>
    /// A link typed without a scheme: an ASCII host with a dot whose last label is letters (a top-level domain), or a
    /// dotted IPv4 address, optionally followed by a numeric port.
    /// </summary>
    private static bool HostInferable(string text)
    {
        var end = text.IndexOfAny(['/', '?', '#']);
        var authority = end >= 0 ? text[..end] : text;
        if (authority.Contains('@')) return false;
        var host = authority;
        var colon = authority.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!authority[(colon + 1)..].All(char.IsAsciiDigit)) return false;
            host = authority[..colon];
        }
        var labels = host.Split('.');
        if (labels.Length < 2 || !labels.All(l => l.Length > 0 && l.All(IsHostCharacter))) return false;
        if (labels.Length == 4 && labels.All(l => l.All(char.IsAsciiDigit))) return true;
        var last = labels[^1];
        return last.Length >= 2 && last.All(char.IsAsciiLetter);
    }

    private static bool IsHostCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    /// <summary>
    /// Lowercase, NFC, Punycode for non-ASCII labels; each label 1–63 host characters without a leading or trailing
    /// hyphen; at most 253 characters. A single trailing dot is removed.
    /// </summary>
    private static string? NormalizeHost(string text)
    {
        if (text.EndsWith('.')) text = text[..^1];
        if (text.Length == 0) return null;
        var labels = new List<string>();
        foreach (var label in text.ToLowerInvariant().Normalize(NormalizationForm.FormC).Split('.'))
        {
            if (label.Length == 0) return null;
            string ascii;
            if (label.All(char.IsAscii)) ascii = label;
            else if (Punycode.Encode(label) is { } encoded) ascii = "xn--" + encoded;
            else return null;
            if (ascii.Length > 63 || ascii[0] == '-' || ascii[^1] == '-' || !ascii.All(IsHostCharacter)) return null;
            labels.Add(ascii);
        }
        var host = string.Join('.', labels);
        return host.Length <= 253 ? host : null;
    }

    private const string Allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~!$&'()*+,;=:@/";
    private const string Hex = "0123456789ABCDEF";

    /// <summary>Keeps valid %XX escapes and characters allowed in a path (or query); encodes everything else as UTF-8 %XX.</summary>
    public static string PercentEncode(string text, bool allowQuestionMark)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var output = new StringBuilder(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b == '%' && i + 2 < bytes.Length && HexValue(bytes[i + 1]) >= 0 && HexValue(bytes[i + 2]) >= 0)
            {
                output.Append('%').Append((char)bytes[i + 1]).Append((char)bytes[i + 2]);
                i += 2;
            }
            else if ((b < 0x80 && Allowed.Contains((char)b)) || (allowQuestionMark && b == '?')) output.Append((char)b);
            else output.Append('%').Append(Hex[b >> 4]).Append(Hex[b & 0x0F]);
        }
        return output.ToString();
    }

    /// <summary>Decodes %XX escapes as UTF-8 (invalid sequences become U+FFFD).</summary>
    public static string PercentDecode(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var output = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '%' && i + 2 < bytes.Length && HexValue(bytes[i + 1]) is var hi and >= 0 && HexValue(bytes[i + 2]) is var lo and >= 0)
            {
                output.Add((byte)(hi << 4 | lo));
                i += 2;
            }
            else output.Add(bytes[i]);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}

/// <summary>RFC 3492 Punycode encoder for internationalized host labels (the part after "xn--").</summary>
internal static class Punycode
{
    private const int Base = 36, TMin = 1, TMax = 26, Skew = 38, Damp = 700;

    public static string? Encode(string label)
    {
        var input = label.EnumerateRunes().Select(r => r.Value).ToList();
        var output = new StringBuilder();
        foreach (var c in input) if (c < 0x80) output.Append((char)c);
        var basicCount = output.Length;
        var handled = basicCount;
        if (basicCount > 0) output.Append('-');

        int n = 0x80, delta = 0, bias = 72;
        while (handled < input.Count)
        {
            var m = input.Where(c => c >= n).DefaultIfEmpty(-1).Min();
            if (m < 0) return null;
            try { delta = checked(delta + (m - n) * (handled + 1)); }
            catch (OverflowException) { return null; }
            n = m;
            foreach (var c in input)
            {
                if (c < n) delta++;
                if (c != n) continue;
                var q = delta;
                for (var k = Base; ; k += Base)
                {
                    var t = k <= bias ? TMin : k >= bias + TMax ? TMax : k - bias;
                    if (q < t) break;
                    output.Append(Digit(t + (q - t) % (Base - t)));
                    q = (q - t) / (Base - t);
                }
                output.Append(Digit(q));
                bias = Adapt(delta, handled + 1, handled == basicCount);
                delta = 0;
                handled++;
            }
            delta++;
            n++;
        }
        return output.ToString();
    }

    private static char Digit(int d) => (char)(d < 26 ? 'a' + d : '0' + d - 26);

    private static int Adapt(int delta, int points, bool first)
    {
        delta = first ? delta / Damp : delta / 2;
        delta += delta / points;
        var k = 0;
        while (delta > (Base - TMin) * TMax / 2)
        {
            delta /= Base - TMin;
            k += Base;
        }
        return k + (Base - TMin + 1) * delta / (delta + Skew);
    }
}
