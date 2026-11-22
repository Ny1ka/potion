namespace Potion.Core.Formats;

/// <summary>The four file categories. Each matches one band of the Potion logo and decides a file's tile color and glyph.</summary>
public enum FormatCategory { Image, Audio, Video, Docs }

/// <summary>A file format Potion understands, loaded from core/spec/formats.json.</summary>
/// <param name="Id">Stable lowercase id, e.g. "mp4". Used by the conversion matrix, settings and tests.</param>
/// <param name="Name">Display name for badges and menus, e.g. "MP4" or "Markdown".</param>
/// <param name="Extensions">Lowercase extensions without the dot. The first is used for output files.</param>
/// <param name="IsText">Text formats (TXT, Markdown, HTML, SVG, RTF) are identified mainly by extension.</param>
/// <param name="IsRaw">Camera RAW formats.</param>
/// <param name="Platforms">Platforms that can read and write this format ("macos", "windows"); null means every platform.</param>
public sealed record FileFormat(
    string Id,
    string Name,
    FormatCategory Category,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> MimeTypes,
    bool IsText,
    bool IsRaw,
    IReadOnlyList<string>? Platforms = null)
{
    /// <summary>Whether this platform's app can convert from and to this format.</summary>
    public bool IsAvailableOn(string platform) => Platforms is null || Platforms.Contains(platform);

    /// <summary>Extension for files written in this format.</summary>
    public string PreferredExtension => Extensions[0];
}

public enum DetectionMethod
{
    /// <summary>Identified from the file's bytes.</summary>
    Content,
    /// <summary>Identified from the extension (text formats, or binary files no content rule recognized).</summary>
    Extension,
    /// <summary>Not a supported format.</summary>
    None,
}

/// <summary>The result of identifying a file.</summary>
/// <param name="Format">The detected format, or null when the file isn't supported.</param>
/// <param name="ExtensionFormat">The format the file's extension points to, if any.</param>
/// <param name="ExtensionMismatch">True when a format was found but the file's extension isn't one of its extensions.</param>
public sealed record FormatDetection(
    FileFormat? Format,
    DetectionMethod Method,
    FileFormat? ExtensionFormat,
    bool ExtensionMismatch)
{
    public bool IsSupported => Format is not null;
}
