using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Potion.Core.App;
using Potion.Core.Formats;

namespace Potion.Core.Dropper;

/// <summary>Runs one queued link (the app plugs in <see cref="DropJobRunner"/>; tests use a fake).</summary>
public delegate Task<DropOutcome> DropRun(DropLink link, string folder, DropOptions options, ResumeState? resume,
    Action<DropStage> stage, CancellationToken token);

public enum DropItemStatus { Waiting, Downloading, Inspecting, Converting, Saving, Done, Failed, Stopped }

/// <summary>One link in the queue, bindable from XAML. Changes arrive on the thread that created the queue.</summary>
public sealed class DropQueueItem : INotifyPropertyChanged
{
    internal DropQueueItem(DropLink link, string folder, DropOptions options, string title, string? formatId)
    {
        Link = link; Folder = folder; Options = options; Title = title; _formatId = formatId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void AllChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public Guid Id { get; } = Guid.NewGuid();
    public DropLink Link { get; }
    /// <summary>Where the download is saved (fixed when it's queued).</summary>
    public string Folder { get; }
    public DropOptions Options { get; }
    /// <summary>What the row shows: the file name from Fetch, or the link's own file name or site.</summary>
    public string Title { get; }

    private string? _formatId;
    /// <summary>The format the row's tile shows: the downloaded one until the result is known.</summary>
    public string? FormatId { get => _formatId; internal set { _formatId = value; Changed(); Changed(nameof(Category)); } }

    /// <summary>The row's category: the known format's, otherwise the chosen output's, otherwise video.</summary>
    public FormatCategory Category
    {
        get
        {
            var known = _formatId is null ? null : FormatRegistry.Shared.FormatById(_formatId);
            var output = Options.OutputFormatId is null ? null : FormatRegistry.Shared.FormatById(Options.OutputFormatId);
            return (known ?? output)?.Category == FormatCategory.Audio ? FormatCategory.Audio : FormatCategory.Video;
        }
    }

    public DropItemStatus Status { get; private set; } = DropItemStatus.Waiting;
    public DownloadProgress? Download { get; private set; }
    public double Conversion { get; private set; }
    public IReadOnlyList<string> Files { get; private set; } = [];
    public string? Error { get; private set; }
    /// <summary>A failed or stopped download that Retry continues instead of starting over.</summary>
    public bool CanResume { get; private set; }

    public bool IsRunning => Status is DropItemStatus.Downloading or DropItemStatus.Inspecting or DropItemStatus.Converting or DropItemStatus.Saving;
    public bool IsFinished => Status is DropItemStatus.Done or DropItemStatus.Failed or DropItemStatus.Stopped;
    public bool CanRetry => Status is DropItemStatus.Failed or DropItemStatus.Stopped;
    public bool CanStop => Status == DropItemStatus.Waiting || IsRunning;
    public bool IsDone => Status == DropItemStatus.Done;
    public bool IsFailed => Status == DropItemStatus.Failed;

    /// <summary>0…1 for a determinate bar; null while the total isn't known (or nothing is running).</summary>
    public double? Fraction => Status switch
    {
        DropItemStatus.Downloading when Download?.Total is { } total => (double)Download.Bytes / Math.Max(total, 1),
        DropItemStatus.Converting => Conversion,
        _ => null,
    };

    /// <summary>For a progress bar's Value (0 when unknown).</summary>
    public double ProgressValue => Fraction ?? 0;
    /// <summary>True while running without a known total.</summary>
    public bool IsIndeterminate => IsRunning && Fraction is null;

    /// <summary>The row's status line, worded like the macOS app.</summary>
    public string StatusText => Status switch
    {
        DropItemStatus.Waiting => "Waiting",
        DropItemStatus.Downloading => Download is { } p ? $"Downloading · {DropText.Detail(p)}" : "Downloading",
        DropItemStatus.Inspecting => "Checking the file…",
        DropItemStatus.Converting => $"Converting · {Math.Round(Conversion * 100).ToString(CultureInfo.CurrentCulture)}%",
        DropItemStatus.Saving => "Saving…",
        DropItemStatus.Done => Files.Count == 1 ? $"Saved as “{Path.GetFileName(Files[0])}”" : $"Saved {Files.Count} files",
        DropItemStatus.Failed => Error ?? "",
        _ => CanResume ? "Stopped · Retry continues where it stopped" : "Stopped",
    };

    internal void Set(DropItemStatus status, DownloadProgress? download = null, double conversion = 0)
    {
        Status = status;
        if (download is not null) Download = download;
        Conversion = conversion;
        AllChanged();
    }

    internal void Finish(DropItemStatus status, IReadOnlyList<string>? files = null, string? error = null, bool canResume = false)
    {
        Status = status;
        Files = files ?? Files;
        Error = error;
        CanResume = canResume;
        AllChanged();
    }
}

/// <summary>Shared wording for download progress.</summary>
public static class DropText
{
    /// <summary>"12.3 MB of 48 MB · 2.1 MB/s · 17 s left"</summary>
    public static string Detail(DownloadProgress progress)
    {
        var bytes = ByteSize.Format(progress.Bytes);
        var parts = new List<string> { progress.Total is { } total ? $"{bytes} of {ByteSize.Format(total)}" : bytes };
        if (progress.BytesPerSecond > 0)
        {
            parts.Add($"{ByteSize.Format((long)progress.BytesPerSecond)}/s");
            if (progress.Total is { } t && t > progress.Bytes)
            {
                var left = TimeSpan.FromSeconds(Math.Max(1, (t - progress.Bytes) / progress.BytesPerSecond));
                parts.Add(left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} min left"
                    : left.TotalMinutes >= 1 ? $"{(int)left.TotalMinutes} min {left.Seconds} s left"
                    : $"{left.Seconds} s left");
            }
        }
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Potion Dropper's download queue (same behavior as core/swift's DropQueue): links wait their turn, up to
/// <see cref="MaxParallel"/> run at once, each can be stopped, retried (resuming when the server allows) or removed.
/// Call from the UI thread; progress is posted back to it.
/// </summary>
public sealed class DropQueue : INotifyPropertyChanged
{
    public const int ParallelLimit = 4;

    private readonly DropRun _run;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly Dictionary<DropQueueItem, CancellationTokenSource> _running = [];
    private readonly Dictionary<DropQueueItem, ResumeState> _resumes = [];
    private readonly HashSet<DropQueueItem> _stopping = [];
    private readonly List<DropQueueItem> _batch = [];
    private readonly Dictionary<DropQueueItem, DateTime> _lastProgress = [];
    private int _maxParallel;

    public DropQueue(DropRun run, int maxParallel = 2)
    {
        _run = run;
        _maxParallel = Math.Clamp(maxParallel, 1, ParallelLimit);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ObservableCollection<DropQueueItem> Items { get; } = [];

    /// <summary>After each download finishes, fails or stops.</summary>
    public event Action<DropQueueItem>? ItemFinished;
    /// <summary>When nothing is left to run: the batch's finished and failed items (stopped ones are neither).</summary>
    public event Action<IReadOnlyList<DropQueueItem>, IReadOnlyList<DropQueueItem>>? BatchFinished;

    /// <summary>Downloads at once (1–4).</summary>
    public int MaxParallel
    {
        get => _maxParallel;
        set { _maxParallel = Math.Clamp(value, 1, ParallelLimit); Changed(); Pump(); }
    }

    public bool IsBusy => _running.Count > 0 || Items.Any(i => i.Status == DropItemStatus.Waiting);

    /// <summary>The current batch: finished items, all items, and the overall fraction (running downloads count by progress).</summary>
    public (int Finished, int Total, double Fraction) Overall
    {
        get
        {
            var current = Items.Where(_batch.Contains).ToList();
            if (current.Count == 0) return (0, 0, 0);
            var finished = current.Count(i => i.IsFinished);
            var partial = current.Sum(i => i.Status switch
            {
                DropItemStatus.Downloading => (i.Fraction ?? 0) * 0.8,
                DropItemStatus.Inspecting => 0.8,
                DropItemStatus.Converting => 0.8 + i.Conversion * 0.2,
                DropItemStatus.Saving => 0.99,
                _ => 0,
            });
            return (finished, current.Count, (finished + partial) / current.Count);
        }
    }

    /// <summary>Downloads waiting or running (for the taskbar and the close question).</summary>
    public int Remaining => Items.Count(i => i.CanStop);

    /// <summary>Adds a link. A link that is already waiting or downloading isn't added twice; that item is returned instead.</summary>
    public DropQueueItem Add(DropLink link, string folder, DropOptions options, string? title = null, string? formatId = null)
    {
        if (Items.FirstOrDefault(i => i.Link == link && i.CanStop) is { } existing) return existing;
        var name = title ?? (link.Parts.FileName.Length > 0 ? link.Parts.FileName : link.Host);
        var item = new DropQueueItem(link, folder, options, name, formatId);
        Items.Add(item);
        _batch.Add(item);
        Pump();
        Refresh();
        return item;
    }

    public void Stop(DropQueueItem item)
    {
        if (_running.TryGetValue(item, out var cts))
        {
            _stopping.Add(item);
            cts.Cancel();
        }
        else if (item.Status == DropItemStatus.Waiting)
        {
            item.Finish(DropItemStatus.Stopped, canResume: _resumes.ContainsKey(item));
            FinishBatchIfIdle();
            Refresh();
        }
    }

    public void StopAll()
    {
        foreach (var item in Items.Where(i => i.CanStop).ToList()) Stop(item);
    }

    /// <summary>Runs a failed or stopped download again, continuing the partial file when possible.</summary>
    public void Retry(DropQueueItem item)
    {
        if (!item.CanRetry) return;
        item.Set(DropItemStatus.Waiting);
        if (!_batch.Contains(item)) _batch.Add(item);
        Pump();
        Refresh();
    }

    /// <summary>Removes an item that isn't running, and deletes any partial file it kept.</summary>
    public void Remove(DropQueueItem item)
    {
        if (item.IsRunning) return;
        if (_resumes.Remove(item, out var resume)) resume.Discard();
        Items.Remove(item);
        _batch.Remove(item);
        FinishBatchIfIdle();
        Refresh();
    }

    /// <summary>Removes every finished download from the list (the files stay where they were saved).</summary>
    public void ClearFinished()
    {
        foreach (var item in Items.Where(i => i.IsDone).ToList()) Remove(item);
    }

    public void Move(int oldIndex, int newIndex) => Items.Move(oldIndex, newIndex);

    private void Pump()
    {
        while (_running.Count < _maxParallel && Items.FirstOrDefault(i => i.Status == DropItemStatus.Waiting) is { } next) Start(next);
    }

    private void Start(DropQueueItem item)
    {
        item.Set(DropItemStatus.Downloading, new DownloadProgress(0, null, 0));
        _resumes.Remove(item, out var resume);
        var cts = new CancellationTokenSource();
        _running[item] = cts;
        void Stage(DropStage stage) => OnUi(() => Update(item, stage));
        _ = RunAsync(item, resume, Stage, cts.Token);
    }

    private async Task RunAsync(DropQueueItem item, ResumeState? resume, Action<DropStage> stage, CancellationToken token)
    {
        DropOutcome? outcome = null;
        Exception? error = null;
        try { outcome = await Task.Run(() => _run(item.Link, item.Folder, item.Options, resume, stage, token), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) { error = e; }
        OnUi(() => Finish(item, outcome, error));
    }

    private void Update(DropQueueItem item, DropStage stage)
    {
        if (!item.IsRunning || !_running.ContainsKey(item)) return;
        switch (stage.Kind)
        {
            case DropStageKind.Downloading:
                // Progress arrives per network read; ten updates a second per row is plenty.
                var now = DateTime.UtcNow;
                if (_lastProgress.TryGetValue(item, out var last) && (now - last).TotalSeconds < 0.1 && stage.Download?.Bytes != stage.Download?.Total) return;
                _lastProgress[item] = now;
                item.Set(DropItemStatus.Downloading, stage.Download);
                break;
            case DropStageKind.Inspecting: item.Set(DropItemStatus.Inspecting); break;
            case DropStageKind.Converting: item.Set(DropItemStatus.Converting, conversion: stage.Conversion); break;
            default: item.Set(DropItemStatus.Saving); break;
        }
        Refresh();
    }

    private void Finish(DropQueueItem item, DropOutcome? outcome, Exception? error)
    {
        if (_running.Remove(item, out var cts)) cts.Dispose();
        _lastProgress.Remove(item);
        var stopped = _stopping.Remove(item);
        if (outcome is not null)
        {
            item.FormatId = outcome.OutputFormatId;
            item.Finish(DropItemStatus.Done, outcome.Files);
        }
        else
        {
            var resume = (error as DropException)?.Resume;
            if (resume is not null) _resumes[item] = resume;
            var message = error is DropException drop ? drop.Message : SourceRegistry.Shared.Message(DownloadError.Interrupted);
            item.Finish(stopped ? DropItemStatus.Stopped : DropItemStatus.Failed, error: stopped ? null : message, canResume: resume is not null);
        }
        ItemFinished?.Invoke(item);
        Pump();
        FinishBatchIfIdle();
        Refresh();
    }

    private void FinishBatchIfIdle()
    {
        if (_running.Count > 0 || Items.Any(i => i.Status == DropItemStatus.Waiting) || _batch.Count == 0) return;
        var current = Items.Where(_batch.Contains).ToList();
        _batch.Clear();
        var done = current.Where(i => i.IsDone).ToList();
        var failed = current.Where(i => i.IsFailed).ToList();
        if (done.Count > 0 || failed.Count > 0) BatchFinished?.Invoke(done, failed);
    }

    private void Refresh()
    {
        Changed(nameof(IsBusy));
        Changed(nameof(Overall));
        Changed(nameof(Remaining));
    }

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }
}
