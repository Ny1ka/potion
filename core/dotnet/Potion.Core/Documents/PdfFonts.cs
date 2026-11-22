using PdfSharp.Fonts;

namespace Potion.Core.Documents;

/// <summary>
/// Finds TrueType fonts for PDFsharp in the system font folders (Windows, macOS, Linux). A family the system
/// doesn't have falls back to Arial, or Courier New for monospaced names, so text always renders and stays
/// searchable. PDFsharp embeds only the characters used.
/// </summary>
public sealed class PdfFonts : IFontResolver
{
    private static readonly object Gate = new();
    private static bool _installed;

    /// <summary>Installs the resolver once per process (PDFsharp allows a single global resolver).</summary>
    public static void Install()
    {
        lock (Gate)
        {
            if (_installed) return;
            GlobalFontSettings.FontResolver = new PdfFonts();
            _installed = true;
        }
    }

    private static readonly string[] Folders = BuildFolders();

    private static string[] BuildFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
            "/System/Library/Fonts/Supplemental", "/System/Library/Fonts", "/Library/Fonts", Path.Combine(home, "Library", "Fonts"),
            "/usr/share/fonts/truetype/msttcorefonts", "/usr/share/fonts/truetype", "/usr/share/fonts",
        }.Where(d => d.Length > 0 && Directory.Exists(d)).Distinct().ToArray();
    }

    /// <summary>File names per family and face (regular, bold, italic, bold italic): Windows names first, then macOS.</summary>
    private static readonly Dictionary<string, string[][]> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial"] = [["arial.ttf", "Arial.ttf"], ["arialbd.ttf", "Arial Bold.ttf"], ["ariali.ttf", "Arial Italic.ttf"], ["arialbi.ttf", "Arial Bold Italic.ttf"]],
        ["Courier New"] = [["cour.ttf", "Courier New.ttf"], ["courbd.ttf", "Courier New Bold.ttf"], ["couri.ttf", "Courier New Italic.ttf"], ["courbi.ttf", "Courier New Bold Italic.ttf"]],
        ["Times New Roman"] = [["times.ttf", "Times New Roman.ttf"], ["timesbd.ttf", "Times New Roman Bold.ttf"], ["timesi.ttf", "Times New Roman Italic.ttf"], ["timesbi.ttf", "Times New Roman Bold Italic.ttf"]],
        ["Calibri"] = [["calibri.ttf"], ["calibrib.ttf"], ["calibrii.ttf"], ["calibriz.ttf"]],
        ["Consolas"] = [["consola.ttf"], ["consolab.ttf"], ["consolai.ttf"], ["consolaz.ttf"]],
        ["Segoe UI"] = [["segoeui.ttf"], ["segoeuib.ttf"], ["segoeuii.ttf"], ["segoeuiz.ttf"]],
        ["Georgia"] = [["georgia.ttf", "Georgia.ttf"], ["georgiab.ttf", "Georgia Bold.ttf"], ["georgiai.ttf", "Georgia Italic.ttf"], ["georgiaz.ttf", "Georgia Bold Italic.ttf"]],
        ["Verdana"] = [["verdana.ttf", "Verdana.ttf"], ["verdanab.ttf", "Verdana Bold.ttf"], ["verdanai.ttf", "Verdana Italic.ttf"], ["verdanaz.ttf", "Verdana Bold Italic.ttf"]],
    };

    /// <summary>Families documents often name that map to a font we can find.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Helvetica"] = "Arial", ["Helvetica Neue"] = "Arial", ["sans-serif"] = "Arial", ["Liberation Sans"] = "Arial",
        ["Times"] = "Times New Roman", ["serif"] = "Times New Roman", ["Cambria"] = "Times New Roman",
        ["Courier"] = "Courier New", ["Menlo"] = "Courier New", ["Monaco"] = "Courier New", ["monospace"] = "Courier New",
        ["Consolas"] = "Consolas", ["Calibri"] = "Calibri",
    };

    private readonly Dictionary<string, byte[]> _cache = [];

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var face = (bold ? 1 : 0) + (italic ? 2 : 0);
        foreach (var family in Candidates(familyName))
        {
            if (Find(family, face) is { } path) return new FontResolverInfo(path);
            // The regular face, with PDFsharp simulating bold/italic.
            if (face != 0 && Find(family, 0) is { } regular) return new FontResolverInfo(regular, bold, italic);
        }
        return null;
    }

    public byte[]? GetFont(string faceName)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(faceName, out var bytes)) _cache[faceName] = bytes = File.ReadAllBytes(faceName);
            return bytes;
        }
    }

    private static IEnumerable<string> Candidates(string family)
    {
        yield return family;
        if (Aliases.TryGetValue(family, out var alias)) yield return alias;
        var mono = family.Contains("mono", StringComparison.OrdinalIgnoreCase) || family.Contains("courier", StringComparison.OrdinalIgnoreCase)
                   || family.Contains("code", StringComparison.OrdinalIgnoreCase);
        yield return mono ? "Courier New" : "Arial";
        yield return "Arial";
    }

    private static string? Find(string family, int face)
    {
        var names = Known.TryGetValue(family, out var known) ? known[face]
            : [family + (face switch { 1 => " Bold", 2 => " Italic", 3 => " Bold Italic", _ => "" }) + ".ttf"];
        foreach (var folder in Folders)
            foreach (var name in names)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path)) return path;
            }
        return null;
    }
}
