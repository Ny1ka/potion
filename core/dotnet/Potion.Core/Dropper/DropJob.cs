using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Potion.Core.Engines;
using Potion.Core.Formats;

namespace Potion.Core.Dropper;

/// <summary>Inspects and converts downloaded media. <see cref="FFmpegMediaConverter"/> is the real one; tests use a fake.</summary>
public interface IMediaConverter
{
    Task<MediaInfo> ProbeAsync(string file, CancellationToken token);
    Task<IReadOnlyList<string>> ConvertAsync(string input, FileFormat inputFormat, FileFormat outputFormat, string outputDirectory,
        ConversionSettings? settings, IProgress<double>? progress, CancellationToken token);
}

/// <summary>Audio and video conversions with the bundled FFmpeg (all Potion Dropper needs; no WIC or PDF engines).</summary>
public sealed class FFmpegMediaConverter(FFmpegTools? tools, ConversionMatrix? matrix = null) : IMediaConverter
{
    private readonly ConversionMatrix _matrix = matrix ?? ConversionMatrix.Shared;

    public Task<MediaInfo> ProbeAsync(string file, CancellationToken token) =>
        MediaProbe.ProbeAsync(file, tools ?? throw ConversionException.ToolsMissing(), token);

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, FileFormat inputFormat, FileFormat outputFormat, string outputDirectory,
        ConversionSettings? settings, IProgress<double>? progress, CancellationToken token)
    {
        settings ??= new ConversionSettings();
        var conversion = _matrix.Conversion(inputFormat.Id, outputFormat.Id) ?? throw ConversionException.Unsupported(inputFormat.Name, outputFormat.Name);
        var ffmpeg = tools ?? throw ConversionException.ToolsMissing();
        string output = conversion.Operation switch
        {
            ConversionOperation.TranscodeVideo or ConversionOperation.VideoToGif =>
                await new VideoEngine(ffmpeg, matrix: _matrix).ConvertAsync(input, inputFormat, outputFormat, outputDirectory, settings.Video, progress, token).ConfigureAwait(false),
            ConversionOperation.TranscodeAudio or ConversionOperation.ExtractAudio =>
                await new AudioEngine(ffmpeg, matrix: _matrix).ConvertAsync(input, inputFormat, outputFormat, outputDirectory, settings.Audio, progress, token).ConfigureAwait(false),
            _ => throw ConversionException.Unsupported(inputFormat.Name, outputFormat.Name),
        };
        progress?.Report(1);
        return [output];
    }
}

/// <summary>File names for finished downloads (core/spec/README.md, "Naming").</summary>
public static class DropNaming
{
    /// <summary>
    /// Renders a naming template: {title}, {site}, {date} (YYYY-MM-DD) and {format}, case-insensitive; other {…} text
    /// stays as typed. The result is cleaned like any file name; if nothing usable is left, the title is used, then fallback.
    /// </summary>
    public static string Render(string template, string fileName, string host, string date, string formatName, int maxLength, string fallback,
        FormatRegistry? formats = null)
    {
        var title = Stem(fileName, formats ?? FormatRegistry.Shared);
        var lowerHost = host.ToLowerInvariant();
        var site = lowerHost.StartsWith("www.", StringComparison.Ordinal) ? lowerHost[4..] : lowerHost;
        var values = new Dictionary<string, string> { ["title"] = title, ["site"] = site, ["date"] = date, ["format"] = formatName };
        var output = new System.Text.StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0) break;
            output.Append(template, i, open - i);
            var close = template.IndexOf('}', open);
            if (close < 0) { i = open; break; }
            var token = template[(open + 1)..close];
            output.Append(values.TryGetValue(token.ToLowerInvariant(), out var value) ? value : template[open..(close + 1)]);
            i = close + 1;
        }
        output.Append(template[i..]);
        return DownloadRules.Sanitize(output.ToString(), maxLength) ?? DownloadRules.Sanitize(title, maxLength) ?? fallback;
    }

    /// <summary>The file name without an extension that names a known format ("clip.mp4" → "clip", "My.Talk" stays).</summary>
    internal static string Stem(string fileName, FormatRegistry formats)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0) return fileName;
        var ext = fileName[(dot + 1)..];
        return ext.Length > 0 && formats.FormatForExtension(ext) is not null ? fileName[..dot] : fileName;
    }
}

/// <summary>What to do with one link.</summary>
/// <param name="OutputFormatId">The format to convert to, or null to keep the downloaded format.</param>
/// <param name="MaxDurationMinutes">Null means no limit.</param>
/// <param name="KeepSource">Keep the downloaded original next to the converted file.</param>
public sealed record DropOptions(
    string? OutputFormatId = null,
    ConversionSettings? Settings = null,
    string? NameTemplate = null,
    long? MaxBytes = null,
    int? MaxDurationMinutes = -1,
    bool KeepSource = false)
{
    /// <summary>The duration limit with "-1" meaning the default from sources.json.</summary>
    internal int? DurationLimit(SourceRegistry registry) => MaxDurationMinutes == -1 ? registry.Download.DefaultMaxDurationMinutes : MaxDurationMinutes;
}

public enum DropStageKind { Downloading, Inspecting, Converting, Saving }

/// <summary>Where a job is: download progress, or conversion progress (0–1).</summary>
public sealed record DropStage(DropStageKind Kind, DownloadProgress? Download = null, double Conversion = 0);

/// <summary>Why a job failed: a download error (with resume state when possible), the duration limit, or a conversion error.</summary>
public sealed class DropException : Exception
{
    public DownloadError? Download { get; }
    public ResumeState? Resume { get; }
    public ConversionException? Conversion { get; }
    public int Minutes { get; }
    public int Limit { get; }
    public bool TooLong => Download is null && Conversion is null && Minutes >= 0;

    private DropException(string message, DownloadError? download = null, ResumeState? resume = null, ConversionException? conversion = null,
        int minutes = 0, int limit = 0) : base(message)
    {
        Download = download; Resume = resume; Conversion = conversion; Minutes = minutes; Limit = limit;
    }

    public static DropException FromDownload(DownloadError error, ResumeState? resume, SourceRegistry registry) =>
        new(registry.Message(error), download: error, resume: resume);

#if DEBUG
    /// <summary>An external source (Debug builds only) failed; its own message.</summary>
    public static DropException External(string message) => new(message, minutes: -1);
#endif

    public static DropException FromConversion(ConversionException error) => new(error.MessageFor("Potion Dropper"), conversion: error);

    public static DropException DurationLimit(int minutes, int limit, SourceRegistry registry) =>
        new(registry.Message("tooLong").Replace("{minutes}", minutes.ToString(CultureInfo.InvariantCulture)).Replace("{limit}", limit.ToString(CultureInfo.InvariantCulture)),
            minutes: minutes, limit: limit);
}

/// <summary>A finished job: the converted file (or the download itself), plus the original when it was kept.</summary>
public sealed record DropOutcome(IReadOnlyList<string> Files, string SourceFormatId, string OutputFormatId, long Bytes, DropLink FinalLink);

/// <summary>
/// Runs one link through download → inspect (duration limit, copy protection) → convert → save (core/spec/README.md,
/// "Jobs"). Work happens in a hidden folder inside the output folder, so the final move is on the same volume; nothing
/// is overwritten. core/swift's DropJobRunner behaves the same.
/// </summary>
public sealed class DropJobRunner(Downloader downloader, IMediaConverter converter, SourceRegistry? registry = null, Func<string>? today = null)
{
    private readonly SourceRegistry _registry = registry ?? SourceRegistry.Shared;
#if DEBUG
    /// <summary>Debug builds only: links an external source handles are downloaded by it instead of the Downloader.</summary>
    public IReadOnlyList<IExternalSourceAdapter> ExternalSources { get; init; } = [];

    private async Task<DownloadResult> ExternalDownloadAsync(IExternalSourceAdapter source, DropLink link, string work, Action<DropStage>? stage,
        CancellationToken token)
    {
        stage?.Invoke(new DropStage(DropStageKind.Downloading, new DownloadProgress(0, null, 0)));
        ExternalDownload result;
        try { result = await source.DownloadAsync(link, work, null, token).ConfigureAwait(false); }
        catch (ExternalSourceException e) { throw DropException.External(e.Message); }
        catch (OperationCanceledException) { throw DropException.FromDownload(DownloadError.Interrupted, null, _registry); }
        var format = _registry.Formats.Detect(result.File).Format;
        if (format is null || format.Category is not (FormatCategory.Audio or FormatCategory.Video))
            throw DropException.FromDownload(DownloadError.From(LinkError.NotMedia), null, _registry);
        var name = result.Title is { } title && DownloadRules.Sanitize(title, _registry.Download.MaxFileNameLength) is { } clean
            ? $"{clean}.{format.PreferredExtension}" : Path.GetFileName(result.File);
        return new DownloadResult(result.File, format.Id, name, new FileInfo(result.File).Length, link);
    }
#endif
    private readonly Func<string> _today = today ?? (() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public async Task<DropOutcome> RunAsync(DropLink link, string folder, DropOptions options, ResumeState? resume = null,
        Action<DropStage>? stage = null, CancellationToken cancellationToken = default)
    {
        var work = resume is not null ? Path.GetDirectoryName(resume.File)! : Path.Combine(folder, $".potion-dropper-{Guid.NewGuid():N}");
        try
        {
            var created = Directory.CreateDirectory(work);
            if (OperatingSystem.IsWindows()) created.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw DropException.FromConversion(ConversionException.CannotWrite(Path.GetFileName(folder)));
        }
        var keepWork = false;
        try
        {
            DownloadResult download;
#if DEBUG
            if (ExternalSources.FirstOrDefault(s => s.Handles(link)) is { } external)
                download = await ExternalDownloadAsync(external, link, work, stage, cancellationToken).ConfigureAwait(false);
            else
#endif
            try
            {
                download = await downloader.DownloadAsync(link, work, options.MaxBytes ?? _registry.Download.DefaultMaxBytes, resume,
                    p => stage?.Invoke(new DropStage(DropStageKind.Downloading, p)), cancellationToken).ConfigureAwait(false);
            }
            catch (DownloadFailureException e)
            {
                keepWork = e.Resume is not null;
                throw DropException.FromDownload(e.Error, e.Resume, _registry);
            }

            var formats = _registry.Formats;
            var source = formats.FormatById(download.FormatId) ?? throw DropException.FromDownload(DownloadError.From(LinkError.NotMedia), null, _registry);
            var target = (options.OutputFormatId is { } id ? formats.FormatById(id) : null) ?? source;
            stage?.Invoke(new DropStage(DropStageKind.Inspecting));
            try
            {
                var info = await converter.ProbeAsync(download.File, cancellationToken).ConfigureAwait(false);
                if (info.IsCopyProtected) throw ConversionException.CopyProtected();
                if (options.DurationLimit(_registry) is { } limit && info.Duration is { } seconds && seconds > limit * 60)
                    throw DropException.DurationLimit((int)Math.Ceiling(seconds / 60), limit, _registry);

                var baseName = DropNaming.Render(options.NameTemplate ?? _registry.Naming.DefaultTemplate, download.FileName, download.FinalLink.Host,
                    _today(), target.Name, _registry.Download.MaxFileNameLength - _registry.Naming.ReserveForSuffix, _registry.Naming.FallbackName, formats);
                if (target.Id == source.Id)
                {
                    stage?.Invoke(new DropStage(DropStageKind.Saving));
                    var saved = OutputFiles.Finalize(download.File, folder, baseName, source.PreferredExtension);
                    return new DropOutcome([saved], source.Id, source.Id, download.Bytes, download.FinalLink);
                }
                // The engines name their output after the input, so the download gets its final name first.
                var named = Path.Combine(work, $"{baseName}.{source.PreferredExtension}");
                File.Move(download.File, named);
                stage?.Invoke(new DropStage(DropStageKind.Converting));
                var files = (await converter.ConvertAsync(named, source, target, folder, options.Settings,
                    new InlineProgress<double>(p => stage?.Invoke(new DropStage(DropStageKind.Converting, Conversion: p))), cancellationToken).ConfigureAwait(false)).ToList();
                if (options.KeepSource)
                {
                    stage?.Invoke(new DropStage(DropStageKind.Saving));
                    files.Add(OutputFiles.Finalize(named, folder, baseName, source.PreferredExtension));
                }
                return new DropOutcome(files, source.Id, target.Id, download.Bytes, download.FinalLink);
            }
            catch (ConversionException e) { throw DropException.FromConversion(e); }
            catch (OperationCanceledException) { throw DropException.FromDownload(DownloadError.Interrupted, null, _registry); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw DropException.FromConversion(ConversionException.CannotWrite(Path.GetFileName(folder)));
            }
        }
        finally
        {
            if (!keepWork)
            {
                try { Directory.Delete(work, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}

/// <summary>Download history the user can see and clear (spec Part 6.1.4: nothing kept beyond that). Stored as JSON.</summary>
public sealed class DropHistory
{
    /// <param name="Error">Null when the download succeeded; otherwise the error id.</param>
    public sealed record Entry(Guid Id, DateTimeOffset Date, string Link, string Site, string Name, IReadOnlyList<string> Files,
        string? FormatId = null, long? Bytes = null, string? Error = null)
    {
        public static Entry Create(string link, string site, string name, IReadOnlyList<string>? files = null, string? formatId = null,
            long? bytes = null, string? error = null) =>
            new(Guid.NewGuid(), DateTimeOffset.Now, link, site, name, files ?? [], formatId, bytes, error);
    }

    private sealed record FileContents(int Version, List<Entry> Entries);

    public const int MaxEntries = 500;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _path;
    private readonly object _lock = new();
    private List<Entry> _entries;

    /// <summary>Loads the history at path (a missing or unreadable file is an empty history).</summary>
    public DropHistory(string path)
    {
        _path = path;
        try { _entries = JsonSerializer.Deserialize<FileContents>(File.ReadAllText(path), Json)?.Entries ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { _entries = []; }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<Entry> Entries { get { lock (_lock) return [.. _entries]; } }

    public void Add(Entry entry)
    {
        lock (_lock)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            Save();
        }
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Id == id);
            Save();
        }
    }

    /// <summary>Forgets everything and deletes the file. Downloaded files are never touched.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries = [];
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new FileContents(1, _entries), Json));
        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>Video or audio output (the segmented control on the media card).</summary>
public enum DropMode { Video, Audio }

/// <summary>What the media card offers for a downloaded format (core/spec/README.md, "Output choices").</summary>
public static class DropChoices
{
    private static FormatCategory CategoryOf(DropMode mode) => mode == DropMode.Video ? FormatCategory.Video : FormatCategory.Audio;

    /// <summary>Video sources can be saved as video or audio; audio sources only as audio.</summary>
    public static IReadOnlyList<DropMode> Modes(FileFormat source) => source.Category switch
    {
        FormatCategory.Video => [DropMode.Video, DropMode.Audio],
        FormatCategory.Audio => [DropMode.Audio],
        _ => [],
    };

    /// <summary>
    /// Formats offered in a mode: the source's own format first when it fits the mode ("keep as downloaded"), then every
    /// format of that kind the matrix can convert it to, in formats.json order.
    /// </summary>
    public static IReadOnlyList<FileFormat> Formats(FileFormat source, DropMode mode, ConversionMatrix? matrix = null)
    {
        if (!Modes(source).Contains(mode)) return [];
        var category = CategoryOf(mode);
        var converted = (matrix ?? ConversionMatrix.Shared).Outputs(source.Id).Select(o => o.Output).Where(f => f.Category == category && f.Id != source.Id);
        return [.. (source.Category == category ? new[] { source } : []), .. converted];
    }

    /// <summary>The preselected format: the source's own when it fits, otherwise MP4 (video) or MP3 (audio) when offered, otherwise the first.</summary>
    public static FileFormat? DefaultFormat(FileFormat source, DropMode mode, ConversionMatrix? matrix = null)
    {
        var offered = Formats(source, mode, matrix);
        if (offered.Count > 0 && offered[0].Id == source.Id) return offered[0];
        var preferred = mode == DropMode.Video ? "mp4" : "mp3";
        return offered.FirstOrDefault(f => f.Id == preferred) ?? offered.FirstOrDefault();
    }
}
