using System.Text;
using System.Text.Json;

namespace Potion.Core.Formats;

/// <summary>One content-detection rule from formats.json. See core/spec/README.md for the semantics.</summary>
internal sealed class DetectionRule
{
    public required string Format { get; init; }
    public required IReadOnlyList<Condition> Conditions { get; init; }
    public required IReadOnlyList<string> ExtensionHint { get; init; }

    public bool Matches(ReadOnlySpan<byte> header, string? extensionFormatId)
    {
        foreach (var c in Conditions)
        {
            if (!c.Matches(header, extensionFormatId)) return false;
        }
        return true;
    }

    public static DetectionRule Parse(JsonElement e)
    {
        var format = e.GetProperty("format").GetString() ?? throw new FormatSpecException("Rule without a format");
        var conditions = e.GetProperty("all").EnumerateArray().Select(Condition.Parse).ToList();
        if (conditions.Count == 0) throw new FormatSpecException($"Rule for {format} has no conditions");
        var hint = e.TryGetProperty("extensionHint", out var h)
            ? h.EnumerateArray().Select(x => x.GetString()!).ToList()
            : new List<string>();
        return new DetectionRule { Format = format, Conditions = conditions, ExtensionHint = hint };
    }
}

internal abstract class Condition
{
    public abstract bool Matches(ReadOnlySpan<byte> header, string? extensionFormatId);

    public static Condition Parse(JsonElement e)
    {
        if (e.TryGetProperty("extension", out var ext))
        {
            return new ExtensionCondition(ext.EnumerateArray().Select(x => x.GetString()!).ToList());
        }
        if (e.TryGetProperty("contains", out var contains))
        {
            var ignoreCase = e.TryGetProperty("ignoreCase", out var ic) && ic.GetBoolean();
            return new ContainsCondition(Value(contains), e.GetProperty("within").GetInt32(), ignoreCase);
        }
        var value = Value(e);
        byte[]? mask = null;
        if (e.TryGetProperty("mask", out var m))
        {
            mask = Hex.Bytes(m.GetString()!);
            if (mask is null || mask.Length != value.Length)
                throw new FormatSpecException("Mask must be hex of the same length as the value");
        }
        return new BytesCondition(e.GetProperty("offset").GetInt32(), value, mask);
    }

    private static byte[] Value(JsonElement e)
    {
        if (e.TryGetProperty("hex", out var hex) && Hex.Bytes(hex.GetString()!) is { Length: > 0 } bytes) return bytes;
        if (e.TryGetProperty("ascii", out var ascii) && ascii.GetString() is { Length: > 0 } s && s.All(char.IsAscii))
            return Encoding.ASCII.GetBytes(s);
        throw new FormatSpecException("Condition needs a non-empty hex or ASCII value");
    }
}

internal sealed class BytesCondition(int offset, byte[] value, byte[]? mask) : Condition
{
    public override bool Matches(ReadOnlySpan<byte> header, string? extensionFormatId)
    {
        if (offset < 0 || offset + value.Length > header.Length) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var m = mask?[i] ?? 0xFF;
            if ((header[offset + i] & m) != (value[i] & m)) return false;
        }
        return true;
    }
}

internal sealed class ContainsCondition(byte[] value, int within, bool ignoreCase) : Condition
{
    private readonly byte[] _needle = ignoreCase ? value.Select(Lower).ToArray() : value;

    public override bool Matches(ReadOnlySpan<byte> header, string? extensionFormatId)
    {
        var limit = Math.Min(within, header.Length);
        if (_needle.Length > limit) return false;
        for (var start = 0; start <= limit - _needle.Length; start++)
        {
            var found = true;
            for (var i = 0; i < _needle.Length; i++)
            {
                var b = ignoreCase ? Lower(header[start + i]) : header[start + i];
                if (b != _needle[i]) { found = false; break; }
            }
            if (found) return true;
        }
        return false;
    }

    private static byte Lower(byte b) => b is >= 0x41 and <= 0x5A ? (byte)(b + 0x20) : b;
}

internal sealed class ExtensionCondition(IReadOnlyList<string> ids) : Condition
{
    public override bool Matches(ReadOnlySpan<byte> header, string? extensionFormatId) =>
        extensionFormatId is not null && ids.Contains(extensionFormatId);
}

internal static class Hex
{
    public static byte[]? Bytes(string s)
    {
        if (s.Length % 2 != 0) return null;
        try { return Convert.FromHexString(s); }
        catch (FormatException) { return null; }
    }
}

/// <summary>Thrown when formats.json is malformed or inconsistent.</summary>
public sealed class FormatSpecException(string message) : Exception(message);
