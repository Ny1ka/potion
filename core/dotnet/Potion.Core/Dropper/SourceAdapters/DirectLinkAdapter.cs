using Potion.Core.Formats;

namespace Potion.Core.Dropper;

/// <summary>
/// The public source adapters, in no particular order (sources.json decides the matching order).
/// Adding one needs an entry in core/spec/sources.json and in docs/SOURCES.md.
/// </summary>
public static class SourceAdapters
{
    public static IReadOnlyList<ISourceAdapter> BuiltIn { get; } = [new DirectLinkAdapter()];
}

/// <summary>
/// Direct links to media files (docs/SOURCES.md, "Direct media links"): the link itself is an audio or video file,
/// served without a login or copy protection. It accepts any web link the adapters before it didn't claim:
/// a media extension in the file name ("clip.mp4") is accepted as that format; a non-media format ("photo.jpg",
/// "manual.pdf") is rejected; anything else ("/download?id=4", a page) needs a check: the download manager accepts it
/// only if the server answers with an audio or video file, and otherwise reports that Potion Dropper can't download
/// from the site.
/// </summary>
public sealed class DirectLinkAdapter : ISourceAdapter
{
    public string Id => "direct";

    public SourceMatch? Match(LinkParts link, SourceInfo source, FormatRegistry formats)
    {
        if (link.FileExtension is not { } ext || formats.FormatForExtension(ext) is not { } format)
            return SourceMatch.Accept(null, needsCheck: true);
        if (source.Categories.Contains(format.Category)) return SourceMatch.Accept(format.Id, needsCheck: false);
        // Text extensions (.html, .txt) usually name a page, not a file: let the server's answer decide.
        return format.IsText ? SourceMatch.Accept(null, needsCheck: true) : SourceMatch.Reject(LinkError.NotMedia);
    }
}
