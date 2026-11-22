namespace Potion.Core.Engines;

/// <summary>
/// Output file naming and safe saving. Converted files are written to a hidden temporary file in the destination
/// folder, verified, then moved to a name that doesn't exist yet. Nothing is ever overwritten.
/// </summary>
public static class OutputFiles
{
    /// <summary>"Interview.mp3", then "Interview 2.mp3", "Interview 3.mp3"…</summary>
    public static string UniquePath(string directory, string baseName, string extension)
    {
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? baseName : $"{baseName} {n}";
            var path = Path.Combine(directory, $"{name}.{extension}");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
    }

    /// <summary>A hidden temporary file next to where the output will go (same volume, so the final move is cheap).</summary>
    public static string TemporaryPath(string directory, string extension) =>
        Path.Combine(directory, $".potion-{Guid.NewGuid():N}.{extension}");

    /// <summary>Moves a finished temporary file to a unique final name, retrying if another file claims the name first.</summary>
    public static string Finalize(string temporary, string directory, string baseName, string extension)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var target = UniquePath(directory, baseName, extension);
            try
            {
                File.Move(temporary, target, overwrite: false);   // throws instead of replacing an existing file
                if (OperatingSystem.IsWindows()) File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.Hidden);
                return target;
            }
            catch (IOException) when (File.Exists(target)) { }
        }
        throw ConversionException.CannotWrite(Path.GetFileName(directory));
    }

    /// <summary>Deletes a leftover temporary file. Never throws, so cleanup can't hide the real error.</summary>
    public static void RemoveIfPresent(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a stray hidden file is harmless */ }
    }

    /// <summary>Checks that the destination folder exists.</summary>
    public static void CheckWritable(string directory)
    {
        if (!Directory.Exists(directory)) throw ConversionException.CannotWrite(Path.GetFileName(directory));
    }
}
