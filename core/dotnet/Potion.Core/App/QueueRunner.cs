using System.Globalization;
using Potion.Core.Engines;

namespace Potion.Core.App;

/// <summary>Converts one queue row into <paramref name="outputDirectory"/>, reporting 0…1 progress. Returns the saved files.</summary>
public delegate Task<IReadOnlyList<string>> ItemConverter(QueueItem item, string outputDirectory, IProgress<double> progress, CancellationToken token);

/// <summary>
/// Converting the queue (same behavior as the macOS runner): up to <see cref="MaxParallel"/> files at once, per-row and
/// overall progress, Stop (running files are cancelled, the rest stay waiting), Retry, and a short summary afterwards.
/// Originals go to the Recycle Bin only after their converted copy is verified and saved. Call from the UI thread.
/// </summary>
public sealed partial class QueueModel
{
    public const int DefaultParallel = 2, ParallelLimit = 4;

    /// <summary>The conversion itself (the Windows app plugs in ConversionService).</summary>
    public ItemConverter? Converter { get; set; }
    /// <summary>Moves an original to the Recycle Bin; throwing keeps the original where it is.</summary>
    public Action<string>? Recycle { get; set; }

    private readonly Dictionary<QueueItem, CancellationTokenSource> _running = [];
    private readonly HashSet<QueueItem> _batch = [];
    private bool _stopping;
    private int _batchTotal, _batchFinished, _batchConverted, _batchFailed;
    private readonly List<string> _batchOutputs = [];
    private string? _completion;
    private IReadOnlyList<string> _completionOutputs = [];
    private int _completionGeneration;

    public bool IsConverting { get; private set; }
    public bool CanConvert => !IsConverting && Items.Any(IsConvertible);
    public int MaxParallel
    {
        get => Math.Clamp(Settings.GetInt("maxParallelConversions", DefaultParallel), 1, ParallelLimit);
        set { Settings.Set("maxParallelConversions", Math.Clamp(value, 1, ParallelLimit)); Changed(); }
    }

    /// <summary>"Converting 3 of 12…" (the first file still in progress).</summary>
    public string ConvertingText => $"Converting {Math.Min(_batchFinished + 1, _batchTotal)} of {_batchTotal}…";
    /// <summary>0…1 across the batch, counting files in progress by how far along they are.</summary>
    public double BatchFraction => _batchTotal == 0 ? 0 : Math.Min((_batchFinished + _running.Keys.Sum(i => i.Progress)) / _batchTotal, 1);
    /// <summary>Files left in the batch (for the taskbar).</summary>
    public int Remaining => _batchTotal - _batchFinished;

    /// <summary>"12 files converted" (or "10 files converted, 2 failed") for a few seconds after a batch.</summary>
    public string? CompletionMessage => _completion;
    public IReadOnlyList<string> CompletionOutputs => _completionOutputs;
    public bool CompletionHasFailures => _batchFailed > 0;

    // Where files go

    /// <summary>Converted files go here; null means next to each original.</summary>
    public string? SaveFolder
    {
        get => Settings.GetString(SettingsKeys.SaveFolder) is { } path && Directory.Exists(path) ? path : null;
        private set { Settings.Set(SettingsKeys.SaveFolder, value); Changed(); }
    }

    public IReadOnlyList<string> RecentFolders => Settings.GetStrings("recentFolders").Where(Directory.Exists).ToList();

    public void UseSaveFolder(string? path)
    {
        SaveFolder = path;
        if (path is not null)
            Settings.Set("recentFolders", new[] { path }.Concat(RecentFolders.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))).Take(5).ToList());
        Changed(nameof(RecentFolders));
    }

    public bool MoveOriginalsToRecycleBin
    {
        get => Settings.GetBool(SettingsKeys.MoveToRecycleBin, false);
        set { Settings.Set(SettingsKeys.MoveToRecycleBin, value); Changed(); }
    }

    private static bool IsConvertible(QueueItem item) =>
        item.Status is QueueStatus.Waiting or QueueStatus.Failed or QueueStatus.Canceled && item.Output is not null;

    // Running

    /// <summary>Converts every row that isn't done yet (waiting, failed or canceled).</summary>
    public void ConvertAll() => Convert(Items.Where(IsConvertible).ToList());

    /// <summary>Converts these rows; while a batch is running they join it. Also used for Retry.</summary>
    public void Convert(IEnumerable<QueueItem> items)
    {
        var chosen = items.Where(i => IsConvertible(i) && !_running.ContainsKey(i)).ToList();
        if (chosen.Count == 0 || Converter is null) return;
        foreach (var item in chosen) { item.Status = QueueStatus.Waiting; item.Failure = null; item.Progress = 0; }
        if (!IsConverting)
        {
            _batch.Clear();
            _batchOutputs.Clear();
            _batchTotal = _batchFinished = _batchConverted = _batchFailed = 0;
            _stopping = false;
            _completion = null;
            _completionGeneration++;
            IsConverting = true;
        }
        foreach (var item in chosen) _batch.Add(item);
        _batchTotal += chosen.Count;
        Pump();
        NotifyRunner();
    }

    public void Stop()
    {
        if (!IsConverting) return;
        _stopping = true;
        foreach (var cts in _running.Values) cts.Cancel();
        if (_running.Count == 0) FinishBatch();
        NotifyRunner();
    }

    private void Pump()
    {
        while (!_stopping && _running.Count < MaxParallel
               && Items.FirstOrDefault(i => _batch.Contains(i) && i.Status == QueueStatus.Waiting && !_running.ContainsKey(i)) is { } next)
        {
            _ = Run(next);
        }
        if (_running.Count == 0 && IsConverting) FinishBatch();
    }

    private async Task Run(QueueItem item)
    {
        var cts = new CancellationTokenSource();
        _running[item] = cts;
        item.Status = QueueStatus.Converting;
        item.Progress = 0;
        var directory = SaveFolder ?? Path.GetDirectoryName(item.Path)!;
        var recycle = MoveOriginalsToRecycleBin;
        var progress = new Progress<double>(f => { if (_running.ContainsKey(item)) { item.Progress = f; NotifyRunner(); } });
        try
        {
            var saved = await Converter!(item, directory, progress, cts.Token);
            if (recycle && saved.Count > 0 && !saved.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
            {
                // The converted copy is verified and saved, so the original can go. A failure keeps it.
                try { Recycle?.Invoke(item.Path); } catch (Exception e) when (e is not OutOfMemoryException) { }
            }
            item.SavedFiles = saved;
            item.Status = QueueStatus.Done;
            _batchOutputs.AddRange(saved);
            _batchConverted++;
        }
        catch (OperationCanceledException)
        {
            item.Status = QueueStatus.Canceled;
        }
        catch (Exception e)
        {
            item.Failure = e is ConversionException ? e.Message : $"Couldn't convert “{item.Name}”.";
            item.Status = QueueStatus.Failed;
            _batchFailed++;
        }
        finally
        {
            _running.Remove(item);
            cts.Dispose();
            item.Progress = 0;
            _batchFinished++;
            Pump();
            NotifyRunner();
        }
    }

    private void FinishBatch()
    {
        IsConverting = false;
        _batch.Clear();
        _stopping = false;
        if (_batchConverted > 0 || _batchFailed > 0)
        {
            var files = _batchConverted == 1 ? "1 file converted" : $"{_batchConverted.ToString(CultureInfo.CurrentCulture)} files converted";
            _completion = _batchFailed == 0 ? files : $"{files}, {_batchFailed} failed";
            _completionOutputs = _batchOutputs.ToList();
            var generation = ++_completionGeneration;
            _ = ClearCompletionLater(generation);
        }
        NotifyRunner();
    }

    private async Task ClearCompletionLater(int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (generation != _completionGeneration) return;
        _completion = null;
        Changed(nameof(CompletionMessage));
    }

    /// <summary>Removing a converting row cancels it.</summary>
    private void CancelRunning(IEnumerable<QueueItem> items)
    {
        foreach (var item in items) if (_running.TryGetValue(item, out var cts)) cts.Cancel();
    }

    private void NotifyRunner()
    {
        Changed(nameof(IsConverting));
        Changed(nameof(CanConvert));
        Changed(nameof(ConvertingText));
        Changed(nameof(BatchFraction));
        Changed(nameof(Remaining));
        Changed(nameof(CompletionMessage));
    }
}
