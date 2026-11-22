namespace Potion.Core.Engines;

/// <summary>
/// Page selections typed by the user, such as "1-3, 5" or "4-". Same rules as the Swift core, checked by the shared
/// cases in core/spec/tests/pages.json: items separated by commas or semicolons, spaces ignored, empty items skipped;
/// an item is a page, a range ("1-3", also with an en dash), "4-" (to the end) or "-2" (from page 1); pages come back
/// 1-based in the order typed without duplicates; an empty selection means every page.
/// </summary>
public static class PageSelection
{
    public static IReadOnlyList<int> Pages(string selection, int pageCount)
    {
        var compact = new string(selection.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('–', '-');
        if (compact.Length == 0) return Enumerable.Range(1, Math.Max(pageCount, 0)).ToList();

        var pages = new List<int>();
        var seen = new HashSet<int>();
        foreach (var item in compact.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            int first, last;
            var dash = item.IndexOf('-');
            if (dash >= 0)
            {
                var start = item[..dash];
                var end = item[(dash + 1)..];
                if ((start.Length == 0 && end.Length == 0) || end.Contains('-')) throw Invalid(selection, pageCount);
                first = start.Length == 0 ? 1 : Number(start) ?? throw Invalid(selection, pageCount);
                last = end.Length == 0 ? pageCount : Number(end) ?? throw Invalid(selection, pageCount);
                if (first > last) throw Invalid(selection, pageCount);
            }
            else
            {
                first = last = Number(item) ?? throw Invalid(selection, pageCount);
            }
            if (first < 1 || last > pageCount) throw Invalid(selection, pageCount);
            for (var page = first; page <= last; page++) if (seen.Add(page)) pages.Add(page);
        }
        if (pages.Count == 0) throw Invalid(selection, pageCount);
        return pages;
    }

    private static int? Number(string text) =>
        text.Length > 0 && text.All(char.IsAsciiDigit) && int.TryParse(text, out var n) ? n : null;

    private static ConversionException Invalid(string selection, int pageCount) => ConversionException.InvalidPages(selection, pageCount);

    /// <summary>A short description for file names: [2,3,4] → "2–4", [1,3] → "1, 3".</summary>
    public static string Describe(IReadOnlyList<int> pages)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < pages.Count)
        {
            var end = i;
            while (end + 1 < pages.Count && pages[end + 1] == pages[end] + 1) end++;
            parts.Add(end > i ? $"{pages[i]}–{pages[end]}" : $"{pages[i]}");
            i = end + 1;
        }
        return string.Join(", ", parts);
    }
}
