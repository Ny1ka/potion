using Potion.Core.Dropper;
using Potion.Core.Tests;
using Xunit;

/// <summary>Jobs that wait until the test finishes them, so the queue's scheduling can be checked step by step.</summary>
internal sealed class ControlledRuns
{
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskCompletionSource<DropOutcome>> _waiting = [];
    private readonly List<string> _started = [];
    private readonly Dictionary<string, ResumeState?> _resumes = [];
    public ResumeState? ResumeOnCancel { get; set; }

    public IReadOnlyList<string> Started { get { lock (_lock) return [.. _started]; } }
    public int Running { get { lock (_lock) return _waiting.Count; } }
    public ResumeState? ResumeSeen(string link) { lock (_lock) return _resumes.GetValueOrDefault(link); }

    public Task<DropOutcome> Run(DropLink link, string folder, DropOptions options, ResumeState? resume, Action<DropStage> stage, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<DropOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _started.Add(link.Text);
            _resumes[link.Text] = resume;
            _waiting[link.Text] = tcs;
        }
        var onCancel = ResumeOnCancel;
        token.Register(() =>
        {
            lock (_lock) _waiting.Remove(link.Text);
            tcs.TrySetException(DropException.FromDownload(DownloadError.Interrupted, onCancel, SourceRegistry.Shared));
        });
        return tcs.Task;
    }

    private TaskCompletionSource<DropOutcome>? Take(string link)
    {
        lock (_lock) return _waiting.Remove(link, out var tcs) ? tcs : null;
    }

    public void Succeed(string link) =>
        Take(link)?.TrySetResult(new DropOutcome([Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp4")], "mp4", "mp4", 10, SourceRegistry.Shared.Parse(link).Link!));

    public void Fail(string link, DownloadError error) =>
        Take(link)?.TrySetException(DropException.FromDownload(error, null, SourceRegistry.Shared));
}

public class DropQueueTests
{
    private static DropLink Link(int n) => SourceRegistry.Shared.Parse($"https://example.com/{n}.mp4").Link!;
    private static readonly string Folder = Path.GetTempPath();

    /// <summary>Waits (briefly) for queue work posted to the UI thread to settle.</summary>
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "timed out");
    }

    [Fact]
    public Task RunsAtMostMaxParallelInOrder() => UiThread.Run(async () =>
    {
        var runs = new ControlledRuns();
        var queue = new DropQueue(runs.Run);
        var items = Enumerable.Range(1, 4).Select(n => queue.Add(Link(n), Folder, new DropOptions())).ToList();
        await Until(() => runs.Running == 2);
        Assert.Equal([Link(1).Text, Link(2).Text], runs.Started);
        Assert.Equal(DropItemStatus.Waiting, items[2].Status);

        runs.Succeed(Link(1).Text);
        await Until(() => runs.Started.Count == 3);
        Assert.Equal(Link(3).Text, runs.Started[^1]);
        Assert.Equal(DropItemStatus.Done, items[0].Status);

        queue.MaxParallel = 3;
        await Until(() => runs.Started.Count == 4);
    });

    [Fact]
    public Task TheSameLinkIsNotQueuedTwice() => UiThread.Run(() =>
    {
        var queue = new DropQueue(new ControlledRuns().Run);
        Assert.Same(queue.Add(Link(1), Folder, new DropOptions()), queue.Add(Link(1), Folder, new DropOptions()));
        Assert.Single(queue.Items);
        return Task.CompletedTask;
    });

    [Fact]
    public Task StopKeepsTheResumeStateAndRetryUsesIt() => UiThread.Run(async () =>
    {
        var resume = new ResumeState(Path.Combine(Folder, "part"), 500, "\"v\"", Link(1));
        var runs = new ControlledRuns { ResumeOnCancel = resume };
        var queue = new DropQueue(runs.Run);
        var item = queue.Add(Link(1), Folder, new DropOptions());
        await Until(() => runs.Running == 1);
        queue.Stop(item);
        await Until(() => item.Status == DropItemStatus.Stopped);
        Assert.True(item.CanResume);
        Assert.Equal("Stopped · Retry continues where it stopped", item.StatusText);

        queue.Retry(item);
        await Until(() => runs.Started.Count == 2);
        Assert.Equal(resume, runs.ResumeSeen(Link(1).Text));
    });

    [Fact]
    public Task FailureShowsTheMessageAndStoppedWaitingItemsNeverStart() => UiThread.Run(async () =>
    {
        var runs = new ControlledRuns();
        var queue = new DropQueue(runs.Run, maxParallel: 1);
        var first = queue.Add(Link(1), Folder, new DropOptions());
        var second = queue.Add(Link(2), Folder, new DropOptions());
        await Until(() => runs.Running == 1);
        queue.Stop(second);
        Assert.Equal(DropItemStatus.Stopped, second.Status);
        runs.Fail(Link(1).Text, DownloadError.NotFound);
        await Until(() => first.Status == DropItemStatus.Failed);
        Assert.Equal("This file isn't on the site anymore. Check the link.", first.StatusText);
        Assert.Equal([Link(1).Text], runs.Started);
    });

    [Fact]
    public Task BatchEventAndOverallProgress() => UiThread.Run(async () =>
    {
        var runs = new ControlledRuns();
        var queue = new DropQueue(runs.Run);
        var batches = new List<(int, int)>();
        var finished = new List<DropQueueItem>();
        queue.BatchFinished += (done, failed) => batches.Add((done.Count, failed.Count));
        queue.ItemFinished += finished.Add;
        var a = queue.Add(Link(1), Folder, new DropOptions());
        var b = queue.Add(Link(2), Folder, new DropOptions());
        await Until(() => runs.Running == 2);
        Assert.Equal(2, queue.Overall.Total);
        Assert.Equal(2, queue.Remaining);
        runs.Succeed(Link(1).Text);
        await Until(() => queue.Overall.Finished == 1);
        runs.Fail(Link(2).Text, DownloadError.NotFound);
        await Until(() => batches.Count == 1);
        Assert.Equal((1, 1), batches[0]);
        Assert.Equal([a, b], finished);
        Assert.False(queue.IsBusy);
    });

    [Fact]
    public Task RemoveAndClearFinished() => UiThread.Run(async () =>
    {
        var runs = new ControlledRuns();
        var queue = new DropQueue(runs.Run);
        var a = queue.Add(Link(1), Folder, new DropOptions());
        var b = queue.Add(Link(2), Folder, new DropOptions());
        await Until(() => runs.Running == 2);
        queue.Remove(a);
        Assert.Equal(2, queue.Items.Count);
        runs.Succeed(Link(1).Text);
        runs.Fail(Link(2).Text, DownloadError.NotFound);
        await Until(() => !queue.IsBusy);
        queue.ClearFinished();
        Assert.Equal([b], queue.Items);
        queue.Remove(b);
        Assert.Empty(queue.Items);
    });

    [Fact]
    public void DetailText()
    {
        Assert.Equal("1.0 MB of 4.0 MB · 1.0 MB/s · 3 s left", DropText.Detail(new DownloadProgress(1L << 20, 4L << 20, 1 << 20)));
        Assert.Equal("10 KB", DropText.Detail(new DownloadProgress(10_240, null, 0)));
    }
}
