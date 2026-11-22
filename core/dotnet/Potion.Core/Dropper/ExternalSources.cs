#if DEBUG
using System.Text.Json;
using Potion.Core.Engines;

namespace Potion.Core.Dropper;

// External sources: a generic, Debug-only extension point (Release builds compile none of this, and the Release
// artifact scan fails if any of it is found). Same layout and protocol as core/swift: a folder per source under
// ExternalSources.DefaultFolder with a source.json manifest
//   {"version": 1, "id": "…", "name": "…", "hosts": ["…"], "executable": "…", "arguments": ["…"]}
// Potion Dropper runs `<executable> <arguments…> download <link> <output folder>` (an argument list, never a shell);
// the program prints JSON lines on stdout ({"progress": 0.4}, {"title": "…"}, then {"file": "<path inside the
// output folder>"}) and exits with 0, or non-zero with a message as the last stderr line.

/// <summary>A source added outside this repository (Debug builds only).</summary>
public interface IExternalSourceAdapter
{
    string Id { get; }
    string Name { get; }
    bool Handles(DropLink link);
    Task<ExternalDownload> DownloadAsync(DropLink link, string folder, Action<double>? progress, CancellationToken token);
}

public sealed record ExternalDownload(string File, string? Title);

/// <summary>Why an external source failed, in its own words (shown in the queue row).</summary>
public sealed class ExternalSourceException(string message) : Exception(message);

public static class ExternalSources
{
    /// <summary>%LOCALAPPDATA%\Potion Dropper\External Sources (or the platform equivalent).</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Potion Dropper", "External Sources");

    /// <summary>Every valid source in folder; folders without a readable source.json are skipped.</summary>
    public static IReadOnlyList<IExternalSourceAdapter> Load(string? folder = null)
    {
        folder ??= DefaultFolder;
        if (!Directory.Exists(folder)) return [];
        var sources = new List<IExternalSourceAdapter>();
        foreach (var child in Directory.GetDirectories(folder).Order(StringComparer.Ordinal))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<ProcessSourceAdapter.Manifest>(File.ReadAllText(Path.Combine(child, "source.json")),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                if (manifest is { Version: 1 } && manifest.Hosts is { Count: > 0 } && !string.IsNullOrEmpty(manifest.Executable))
                    sources.Add(new ProcessSourceAdapter(manifest, child));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return sources;
    }
}

/// <summary>An external source that runs a program (see the comment at the top of this file).</summary>
internal sealed class ProcessSourceAdapter(ProcessSourceAdapter.Manifest manifest, string folder) : IExternalSourceAdapter
{
    internal sealed record Manifest(int Version, string Id, string Name, List<string> Hosts, string Executable, List<string>? Arguments);

    public string Id => manifest.Id;
    public string Name => manifest.Name;

    public bool Handles(DropLink link) =>
        manifest.Hosts.Any(h => link.Host == h.ToLowerInvariant() || link.Host.EndsWith("." + h.ToLowerInvariant(), StringComparison.Ordinal));

    /// <summary>Absolute paths as given; a path with a separator is relative to the source's folder; a bare name is found on PATH.</summary>
    private string Executable =>
        Path.IsPathRooted(manifest.Executable) ? manifest.Executable
        : manifest.Executable.IndexOfAny(['/', '\\']) >= 0 ? Path.Combine(folder, manifest.Executable)
        : manifest.Executable;

    public async Task<ExternalDownload> DownloadAsync(DropLink link, string output, Action<double>? progress, CancellationToken token)
    {
        string? title = null, file = null;
        var result = await ProcessRunner.RunAsync(Executable, [.. manifest.Arguments ?? [], "download", link.Text, output], line =>
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (root.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number) progress?.Invoke(Math.Clamp(p.GetDouble(), 0, 1));
                if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString();
                if (root.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String) file = f.GetString();
            }
            catch (JsonException) { }
        }, token).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            var last = result.Stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            var message = string.IsNullOrEmpty(last) ? $"{Name} stopped with an error." : last;
            throw new ExternalSourceException(message.Length > 200 ? message[..200] : message);
        }
        if (file is null) throw new ExternalSourceException($"{Name} didn't name a downloaded file.");
        var full = Path.GetFullPath(file);
        var root = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(full))
            throw new ExternalSourceException($"{Name} didn't save its file in the download folder.");
        return new ExternalDownload(full, title);
    }
}
#endif
