namespace Potion.Core.Documents;

/// <summary>
/// A format-neutral document: what the Windows document readers produce and the writers consume (macOS uses the
/// system's attributed strings instead). It keeps what the spec asks to preserve: text formatting, headings,
/// lists, tables, images, links and page size.
/// </summary>
public sealed class Document
{
    public List<Block> Blocks { get; } = [];
    public string? Title { get; set; }
    /// <summary>Page size and margins in points, when the source has them (DOCX, ODT, RTF).</summary>
    public PageSetup? Page { get; set; }

    /// <summary>All text, without formatting (for checks and plain-text output).</summary>
    public string PlainText => string.Join("\n", Blocks.Select(b => b.PlainText));
}

public sealed record PageSetup(double Width, double Height, double Top, double Right, double Bottom, double Left)
{
    public static readonly PageSetup Letter = new(612, 792, 72, 72, 72, 72);
    public static readonly PageSetup A4 = new(595.28, 841.89, 72, 72, 72, 72);
}

public enum Alignment { Start, Center, End, Justify }

public abstract record Block
{
    public abstract string PlainText { get; }
}

/// <param name="HeadingLevel">1–6 for headings, 0 for body text.</param>
/// <param name="Quote">Indented as a block quote.</param>
public sealed record Paragraph(List<Inline> Inlines, int HeadingLevel = 0, Alignment Alignment = Alignment.Start, bool Quote = false) : Block
{
    public override string PlainText => Inline.TextOf(Inlines);
}

/// <param name="Depth">0 for top level.</param>
/// <param name="ListId">Items with the same id belong to one list (numbering restarts per list).</param>
public sealed record ListItem(List<Inline> Inlines, int Depth, bool Ordered, int Number, int ListId) : Block
{
    public override string PlainText => Inline.TextOf(Inlines);
}

/// <summary>Rows of cells; the first row is the header when <see cref="HasHeader"/>.</summary>
public sealed record Table(List<List<TableCell>> Rows, bool HasHeader) : Block
{
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
    public override string PlainText => string.Join("\n", Rows.Select(r => string.Join("\t", r.Select(c => c.PlainText))));
}

public sealed record TableCell(List<Block> Blocks)
{
    public string PlainText => string.Join(" ", Blocks.Select(b => b.PlainText));
}

public sealed record CodeBlock(string Code) : Block
{
    public override string PlainText => Code;
}

public sealed record HorizontalRule : Block
{
    public override string PlainText => "";
}

public sealed record PageBreak : Block
{
    public override string PlainText => "";
}

public abstract record Inline
{
    public static string TextOf(IEnumerable<Inline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        TextRun t => t.Text,
        LineBreak => "\n",
        _ => "",
    }));
}

public sealed record TextStyle
{
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strike { get; init; }
    public bool Code { get; init; }
    public bool Superscript { get; init; }
    public bool Subscript { get; init; }
    public string? FontFamily { get; init; }
    /// <summary>Points.</summary>
    public double? FontSize { get; init; }
    /// <summary>"RRGGBB".</summary>
    public string? Color { get; init; }
    public string? Link { get; init; }

    public static readonly TextStyle Plain = new();
}

public sealed record TextRun(string Text, TextStyle Style) : Inline;

public sealed record LineBreak : Inline;

/// <summary>An image in the text. Size in points (96 px = 72 pt).</summary>
public sealed record ImageRun(byte[] Data, string MimeType, double Width, double Height, string? Alt = null) : Inline
{
    public string Extension => MimeType switch
    {
        "image/jpeg" => "jpg", "image/gif" => "gif", "image/bmp" => "bmp", "image/webp" => "webp", "image/svg+xml" => "svg", _ => "png",
    };
}
