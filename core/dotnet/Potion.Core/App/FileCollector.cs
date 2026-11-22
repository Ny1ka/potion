using Potion.Core.Formats;

namespace Potion.Core.App;

/// <summary>
/// Turns files and folders the user dropped or chose into a list of convertible files (same rules as the Swift
/// FileCollector): folders are expanded (optionally with subfolders); hidden files, app bundles and Potion's own
/// temporary files are skipped; duplicates are dropped; folder contents come in natural name order.
/// </summary>
public sealed class FileCollector(FormatRegistry? registry = null, ConversionMatrix? matrix = null, int limit = 5_000)
{
    private readonly FormatRegistry _registry = registry ?? FormatRegistry.Shared;
    private readonly ConversionMatrix _matrix = matrix ?? ConversionMatrix.Shared;

    public sealed record File(string Path, FileFormat Format, long? Size);

    public sealed record Result(IReadOnlyList<File> Files, IReadOnlyList<string> Unsupported, bool Truncated);

    public int Limit => limit;

    public Result Collect(IEnumerable<string> paths, bool includeSubfolders, IEnumerable<string>? excluding = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>((excluding ?? []).Select(Normalize), comparer);
        var files = new List<File>();
        var unsupported = new List<string>();
        foreach (var path in paths)
        {
            foreach (var file in Expand(Normalize(path), includeSubfolders))
            {
                if (!seen.Add(file)) continue;
                if (files.Count >= limit) return new Result(files, unsupported, Truncated: true);
                FileFormat? format = null;
                try { format = _registry.Detect(file).Format; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                if (format is not null && _matrix.Outputs(format.Id).Count > 0)
                    files.Add(new File(file, format, SizeOf(file)));
                else
                    unsupported.Add(file);
            }
        }
        return new Result(files, unsupported, Truncated: false);
    }

    private static string Normalize(string path) => System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);

    private static long? SizeOf(string path)
    {
        try { return new FileInfo(path).Length; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsSkipped(FileSystemInfo info) =>
        info.Name.StartsWith('.') || info.Attributes.HasFlag(FileAttributes.Hidden) || info.Attributes.HasFlag(FileAttributes.System)
        || info.Name.StartsWith(".potion-", StringComparison.Ordinal);

    /// <summary>macOS app bundles and similar packages are folders that behave like files; never look inside them.</summary>
    private static bool IsPackage(DirectoryInfo dir) =>
        dir.Extension.ToLowerInvariant() is ".app" or ".bundle" or ".framework" or ".rtfd" or ".pages" or ".key" or ".numbers" or ".photoslibrary";

    private static IEnumerable<string> Expand(string path, bool includeSubfolders)
    {
        if (System.IO.File.Exists(path))
        {
            var info = new FileInfo(path);
            if (!info.Name.StartsWith(".potion-", StringComparison.Ordinal)) yield return path;
            yield break;
        }
        var dir = new DirectoryInfo(path);
        if (!dir.Exists || IsPackage(dir)) yield break;
        var found = new List<string>();
        void Walk(DirectoryInfo d)
        {
            IEnumerable<FileSystemInfo> entries;
            try { entries = d.EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var entry in entries)
            {
                if (IsSkipped(entry)) continue;
                if (entry is FileInfo f) found.Add(f.FullName);
                else if (entry is DirectoryInfo sub && includeSubfolders && !IsPackage(sub)) Walk(sub);
            }
        }
        Walk(dir);
        found.Sort(NaturalComparer.Instance);
        foreach (var file in found) yield return file;
    }

    /// <summary>"Track 2" before "Track 10", like File Explorer and Finder.</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            if (a is null || b is null) return string.CompareOrdinal(a, b);
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                    while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                    var na = a[si..i].TrimStart('0');
                    var nb = b[sj..j].TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    var c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
