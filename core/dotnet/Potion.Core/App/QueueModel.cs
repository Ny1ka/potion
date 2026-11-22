using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Potion.Core.Formats;

namespace Potion.Core.App;

/// <summary>A small settings store: one JSON file (Windows apps run unpackaged, so no ApplicationData).</summary>
public sealed class SettingsStore(string path)
{
    private readonly Dictionary<string, JsonElement> _values = Load(path);

    /// <summary>%LOCALAPPDATA%\Potion\settings.json (or the platform equivalent).</summary>
    public static SettingsStore Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Potion", "settings.json"));

    private static Dictionary<string, JsonElement> Load(string path)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public string? GetString(string key) => _values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public bool GetBool(string key, bool fallback) => _values.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
    public IReadOnlyList<string> GetStrings(string key) =>
        _values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
    public int GetInt(string key, int fallback) => _values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    public void Set(string key, object? value)
    {
        if (value is null) _values.Remove(key);
        else _values[key] = JsonSerializer.SerializeToElement(value);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* settings are a convenience */ }
    }
}

public static class SettingsKeys
{
    public const string IncludeSubfolders = "includeSubfolders";
    public const string SaveFolder = "saveFolder";
    public const string MoveToRecycleBin = "moveOriginalsToRecycleBin";
    public static string LastOutput(FormatCategory category) => "lastOutput." + category.ToString().ToLowerInvariant();
}

public enum QueueStatus { Waiting, Converting, Done, Failed, Canceled }

/// <summary>One file in the queue, bindable from XAML.</summary>
public sealed class QueueItem : INotifyPropertyChanged
{
    public QueueItem(string path, FileFormat format, long? size, IReadOnlyList<FileFormat> outputs, string outputId)
    {
        Path = path; Format = format; Size = size; Outputs = outputs; _outputId = outputId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public FileFormat Format { get; }
    public long? Size { get; }
    /// <summary>What this file can become (the input's own category first).</summary>
    public IReadOnlyList<FileFormat> Outputs { get; }
    public FormatCategory Category => Format.Category;

    private string _outputId;
    public string OutputId
    {
        get => _outputId;
        set { if (_outputId != value) { _outputId = value; Changed(); Changed(nameof(Output)); Changed(nameof(OutputName)); } }
    }
    public FileFormat? Output => Outputs.FirstOrDefault(o => o.Id == _outputId);
    public string OutputName => Output?.Name ?? "";

    private QueueStatus _status;
    public QueueStatus Status
    {
        get => _status;
        set { if (_status != value) { _status = value; Changed(); Changed(nameof(StatusText)); Changed(nameof(SecondaryText)); Changed(nameof(IsConverting)); } }
    }
    private double _progress;
    /// <summary>0…1 while converting.</summary>
    public double Progress
    {
        get => _progress;
        set { _progress = value; Changed(); Changed(nameof(StatusText)); }
    }
    private string? _failure;
    public string? Failure
    {
        get => _failure;
        set { _failure = value; Changed(); Changed(nameof(SecondaryText)); }
    }
    public IReadOnlyList<string> SavedFiles { get; set; } = [];

    public bool IsConverting => _status == QueueStatus.Converting;

    /// <summary>Right-hand status column (icon added by the view): "Waiting", "62%", "Done", "Failed", "Canceled".</summary>
    public string StatusText => _status switch
    {
        QueueStatus.Converting => _progress.ToString("P0", CultureInfo.CurrentCulture),
        QueueStatus.Done => "Done",
        QueueStatus.Failed => "Failed",
        QueueStatus.Canceled => "Canceled",
        _ => "Waiting",
    };

    /// <summary>"MOV · 48.2 MB", or the reason it failed.</summary>
    public string SecondaryText => _status == QueueStatus.Failed && _failure is not null
        ? _failure
        : Size is { } s ? $"{Format.Name} · {ByteSize.Format(s)}" : Format.Name;

    /// <summary>The output formats grouped by category, for the format menu (06-components.md).</summary>
    public IReadOnlyList<(FormatCategory Category, IReadOnlyList<FileFormat> Formats)> OutputGroups => FormatGroups.Of(Outputs);
}

public static class FormatGroups
{
    /// <summary>Keeps the matrix order (own category first) and groups by category.</summary>
    public static IReadOnlyList<(FormatCategory Category, IReadOnlyList<FileFormat> Formats)> Of(IEnumerable<FileFormat> formats) =>
        formats.GroupBy(f => f.Category).Select(g => (g.Key, (IReadOnlyList<FileFormat>)g.ToList())).ToList();

    public static string Label(FormatCategory category) => category.ToString();
}

/// <summary>File sizes as File Explorer shows them: "6 bytes", "105 KB", "48.2 MB", "1.2 GB".</summary>
public static class ByteSize
{
    public static string Format(long bytes)
    {
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;
        do { value /= 1024; unit++; } while (value >= 1024 && unit < units.Length - 1);
        return unit == 0 ? $"{Math.Ceiling(value):0} KB" : $"{value.ToString(value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}

/// <summary>What a drag over the window would do (06-components.md, "Drop zone").</summary>
public sealed record DropState(int Files, int Folders, bool Invalid)
{
    public static readonly DropState None = new(0, 0, false);

    public string Label => Invalid ? "These files can't be converted"
        : Folders == 0 ? (Files == 1 ? "Drop to add 1 file" : $"Drop to add {Files} files")
        : Files == 0 ? (Folders == 1 ? "Drop to add 1 folder" : $"Drop to add {Folders} folders")
        : $"Drop to add {Files + Folders} items";

    /// <summary>Classifies dragged paths, reading at most 200 file headers so the drag stays responsive.</summary>
    public static DropState Classify(IEnumerable<string> paths, FormatRegistry? registry = null, ConversionMatrix? matrix = null)
    {
        registry ??= FormatRegistry.Shared;
        matrix ??= ConversionMatrix.Shared;
        int files = 0, folders = 0, checkedFiles = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) { folders++; continue; }
            checkedFiles++;
            if (checkedFiles > 200) { files++; continue; }
            try
            {
                if (registry.Detect(path).Format is { } f && matrix.Outputs(f.Id).Count > 0) files++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return files + folders == 0 ? new DropState(0, 0, true) : new DropState(files, folders, false);
    }
}

/// <summary>The main window's file list (UI-framework neutral; the WinUI view binds to it).</summary>
public sealed partial class QueueModel(SettingsStore settings, ConversionMatrix? matrix = null) : INotifyPropertyChanged
{
    private readonly ConversionMatrix _matrix = matrix ?? ConversionMatrix.Shared;

    public ObservableCollection<QueueItem> Items { get; } = [];
    public SettingsStore Settings => settings;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private IReadOnlyList<string> _unsupported = [];
    private int? _truncatedCount;
    private bool _isAdding;

    public bool IncludeSubfolders
    {
        get => settings.GetBool(SettingsKeys.IncludeSubfolders, true);
        set { settings.Set(SettingsKeys.IncludeSubfolders, value); Changed(); }
    }
    public bool IsAdding { get => _isAdding; private set { _isAdding = value; Changed(); } }
    public bool IsEmpty => Items.Count == 0;
    public string CountText => Items.Count == 1 ? "1 file" : $"{Items.Count} files";

    /// <summary>The banner text for the last add, or null (same wording as macOS).</summary>
    public string? BannerMessage
    {
        get
        {
            if (_truncatedCount is { } count) return $"Added the first {count.ToString("N0", CultureInfo.CurrentCulture)} files. Add the rest in another batch.";
            var names = _unsupported.Select(System.IO.Path.GetFileName).ToList();
            return names.Count switch
            {
                0 => null,
                1 => $"“{names[0]}” can't be converted.",
                2 => $"2 files can't be converted: “{names[0]}” and “{names[1]}”.",
                3 => $"3 files can't be converted: “{names[0]}”, “{names[1]}” and “{names[2]}”.",
                _ => $"{names.Count} files can't be converted: “{names[0]}”, “{names[1]}” and {names.Count - 2} more.",
            };
        }
    }

    public void DismissBanner()
    {
        _unsupported = [];
        _truncatedCount = null;
        Changed(nameof(BannerMessage));
    }

    /// <summary>Adds files and folders; detection runs off the calling (UI) thread.</summary>
    public async Task<IReadOnlyList<QueueItem>> AddAsync(IEnumerable<string> paths, int limit = 5_000)
    {
        var list = paths.ToList();
        if (list.Count == 0) return [];
        IsAdding = true;
        var existing = Items.Select(i => i.Path).ToList();
        var subfolders = IncludeSubfolders;
        var result = await Task.Run(() => new FileCollector(matrix: _matrix, limit: limit).Collect(list, subfolders, existing));
        var added = new List<QueueItem>();
        foreach (var file in result.Files)
        {
            var outputs = _matrix.Outputs(file.Format.Id).Select(o => o.Output).ToList();
            var item = new QueueItem(file.Path, file.Format, file.Size, outputs, DefaultOutput(file.Format, outputs));
            Items.Add(item);
            added.Add(item);
        }
        _unsupported = result.Unsupported;
        _truncatedCount = result.Truncated ? result.Files.Count : null;
        IsAdding = false;
        NotifyListChanged();
        Changed(nameof(BannerMessage));
        return added;
    }

    /// <summary>The last format chosen for this category, if this file can become it; otherwise the first output in the
    /// file's own category that isn't its current format.</summary>
    private string DefaultOutput(FileFormat format, IReadOnlyList<FileFormat> outputs)
    {
        var last = settings.GetString(SettingsKeys.LastOutput(format.Category));
        if (last is not null && last != format.Id && outputs.Any(o => o.Id == last)) return last;
        return (outputs.FirstOrDefault(o => o.Id != format.Id) ?? outputs[0]).Id;
    }

    /// <summary>Sets the format for these rows (only those that can take it) and remembers it per category.</summary>
    public void SetOutput(IEnumerable<QueueItem> items, string outputId)
    {
        foreach (var item in items.Where(i => !i.IsConverting && i.Outputs.Any(o => o.Id == outputId)))
        {
            item.OutputId = outputId;
            item.Status = QueueStatus.Waiting;
            settings.Set(SettingsKeys.LastOutput(item.Category), outputId);
        }
    }

    /// <summary>Formats every one of these rows can become.</summary>
    public IReadOnlyList<FileFormat> CommonOutputs(IReadOnlyCollection<QueueItem> items)
    {
        var first = items.FirstOrDefault();
        if (first is null) return [];
        return first.Outputs.Where(o => items.All(i => i.Outputs.Any(x => x.Id == o.Id))).ToList();
    }

    public void Remove(IEnumerable<QueueItem> items)
    {
        var list = items.ToList();
        CancelRunning(list);
        foreach (var item in list) Items.Remove(item);
        NotifyListChanged();
    }

    public void Clear()
    {
        if (IsConverting) return;
        Items.Clear();
        DismissBanner();
        NotifyListChanged();
    }

    private void NotifyListChanged()
    {
        Changed(nameof(IsEmpty));
        Changed(nameof(CountText));
    }
}
