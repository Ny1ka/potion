using System.Collections.Concurrent;
using Potion.Core.App;
using Potion.Core.Engines;
using Xunit;

namespace Potion.Core.Tests;

/// <summary>The queue runner with a fake converter, on a single-threaded context like the app's UI thread.</summary>
public sealed class QueueRunnerTests : EngineTestBase
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[24]];

    private async Task<QueueModel> Queue(int files)
    {
        var queue = new QueueModel(new SettingsStore(Path.Combine(Dir, "settings.json")));
        var paths = Enumerable.Range(1, files).Select(i => { var p = Path.Combine(Dir, $"img{i}.png"); File.WriteAllBytes(p, Png); return p; });
        await queue.AddAsync(paths);
        return queue;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "timed out");
    }

    [Fact]
    public Task ConvertsEverythingTwoAtATime() => UiThread.Run(async () =>
    {
        var queue = await Queue(5);
        int running = 0, peak = 0;
        queue.Converter = async (item, dir, progress, token) =>
        {
            peak = Math.Max(peak, ++running);
            progress.Report(0.5);
            await Task.Delay(30, token);
            running--;
            return [Path.Combine(dir, item.Name + ".out")];
        };
        Assert.True(queue.CanConvert);
        queue.ConvertAll();
        Assert.True(queue.IsConverting);
        Assert.StartsWith("Converting 1 of 5", queue.ConvertingText);
        await Until(() => !queue.IsConverting);
        Assert.Equal(2, peak);
        Assert.All(queue.Items, i => Assert.Equal(QueueStatus.Done, i.Status));
        Assert.Equal("5 files converted", queue.CompletionMessage);
        Assert.Equal(5, queue.CompletionOutputs.Count);
        Assert.False(queue.CanConvert);
    });

    [Fact]
    public Task FailuresShowTheReasonAndCanBeRetried() => UiThread.Run(async () =>
    {
        var queue = await Queue(2);
        var failFirst = true;
        queue.Converter = (item, dir, progress, token) =>
        {
            if (item.Name == "img1.png" && failFirst) throw ConversionException.Unreadable(item.Name);
            return Task.FromResult<IReadOnlyList<string>>([Path.Combine(dir, "out")]);
        };
        queue.ConvertAll();
        await Until(() => !queue.IsConverting);
        var bad = queue.Items.Single(i => i.Name == "img1.png");
        Assert.Equal(QueueStatus.Failed, bad.Status);
        Assert.Equal("Can't read “img1.png”. The file may be damaged.", bad.SecondaryText);
        Assert.Equal("1 file converted, 1 failed", queue.CompletionMessage);

        failFirst = false;
        queue.Convert([bad]);
        await Until(() => !queue.IsConverting);
        Assert.Equal(QueueStatus.Done, bad.Status);
    });

    [Fact]
    public Task StopCancelsRunningFilesAndLeavesTheRestWaiting() => UiThread.Run(async () =>
    {
        var queue = await Queue(4);
        queue.MaxParallel = 1;
        queue.Converter = async (item, dir, progress, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return [];
        };
        queue.ConvertAll();
        await Until(() => queue.Items.Any(i => i.IsConverting));
        queue.Stop();
        await Until(() => !queue.IsConverting);
        Assert.Equal(QueueStatus.Canceled, queue.Items[0].Status);
        Assert.All(queue.Items.Skip(1), i => Assert.Equal(QueueStatus.Waiting, i.Status));
        Assert.Null(queue.CompletionMessage);   // nothing converted, nothing to report
    });

    [Fact]
    public Task OriginalsGoToTheRecycleBinOnlyAfterASuccessfulSave() => UiThread.Run(async () =>
    {
        var queue = await Queue(2);
        var recycled = new ConcurrentBag<string>();
        queue.Recycle = recycled.Add;
        queue.MoveOriginalsToRecycleBin = true;
        queue.Converter = (item, dir, progress, token) => item.Name == "img2.png"
            ? throw ConversionException.Unreadable(item.Name)
            : Task.FromResult<IReadOnlyList<string>>([Path.Combine(dir, "img1.jpg")]);
        queue.ConvertAll();
        await Until(() => !queue.IsConverting);
        Assert.Equal(["img1.png"], recycled.Select(Path.GetFileName));
    });

    [Fact]
    public Task SaveFolderAndRemovingWhileConverting() => UiThread.Run(async () =>
    {
        var queue = await Queue(1);
        var target = Directory.CreateDirectory(Path.Combine(Dir, "out")).FullName;
        queue.UseSaveFolder(target);
        Assert.Equal(target, queue.SaveFolder);
        Assert.Equal([target], queue.RecentFolders);
        string? usedDirectory = null;
        var cancelled = false;
        queue.Converter = async (item, dir, progress, token) =>
        {
            usedDirectory = dir;
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { cancelled = true; throw; }
            return [];
        };
        queue.ConvertAll();
        await Until(() => usedDirectory is not null);
        Assert.Equal(target, usedDirectory);
        queue.Remove(queue.Items.ToList());
        await Until(() => !queue.IsConverting);
        Assert.True(cancelled);
    });
}

/// <summary>Runs async code on one thread, like a UI dispatcher, so the queue sees its usual threading.</summary>
internal static class UiThread
{
    public static Task Run(Func<Task> body)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            var context = new Context();
            SynchronizationContext.SetSynchronizationContext(context);
            context.Post(async _ =>
            {
                try { await body(); done.SetResult(); }
                catch (Exception e) { done.SetException(e); }
                finally { context.Complete(); }
            }, null);
            context.RunLoop();
        });
        thread.Start();
        return done.Task;
    }

    private sealed class Context : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = [];
        public override void Post(SendOrPostCallback d, object? state) { if (!_queue.IsAddingCompleted) _queue.Add((d, state)); }
        public void Complete() => _queue.CompleteAdding();
        public void RunLoop() { foreach (var (d, state) in _queue.GetConsumingEnumerable()) d(state); }
    }
}
