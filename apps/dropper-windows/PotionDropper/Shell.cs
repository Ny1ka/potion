using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Potion.Core.App;
using Potion.Core.Dropper;

namespace PotionDropper;

/// <summary>Settings (one JSON file in %LOCALAPPDATA%\Potion Dropper, same keys as the macOS app).</summary>
internal sealed class DropperSettings(SettingsStore store)
{
    public static DropperSettings Default { get; } = new(new SettingsStore(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Potion Dropper", "settings.json")));

    public SettingsStore Store { get; } = store;
    private static SourceRegistry Registry => SourceRegistry.Shared;

    /// <summary>Downloads go here: the chosen folder if it still exists, otherwise Downloads.</summary>
    public string OutputFolder
    {
        get => Store.GetString("outputFolder") is { } path && Directory.Exists(path) ? path : DownloadsFolder;
        set => Store.Set("outputFolder", value);
    }

    public static string DownloadsFolder
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(path) ? path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    public string NameTemplate
    {
        get => Store.GetString("nameTemplate") is { } t && !string.IsNullOrWhiteSpace(t) ? t : Registry.Naming.DefaultTemplate;
        set => Store.Set("nameTemplate", value);
    }

    public static int DefaultSizeGB => (int)(Registry.Download.DefaultMaxBytes / 1_073_741_824);
    /// <summary>Gigabytes; 0 means no limit.</summary>
    public int MaxSizeGB { get => Store.GetInt("maxSizeGB", DefaultSizeGB); set => Store.Set("maxSizeGB", value); }
    public long MaxBytes => MaxSizeGB == 0 ? long.MaxValue : MaxSizeGB * 1_073_741_824L;

    public bool LimitDuration { get => Store.GetBool("limitDuration", true); set => Store.Set("limitDuration", value); }
    public int DurationMinutes
    {
        get => Store.GetInt("maxDurationMinutes", Registry.Download.DefaultMaxDurationMinutes);
        set => Store.Set("maxDurationMinutes", value);
    }
    public int? MaxDurationMinutes => LimitDuration ? DurationMinutes : null;

    public int MaxParallel { get => Math.Clamp(Store.GetInt("maxParallel", 2), 1, DropQueue.ParallelLimit); set => Store.Set("maxParallel", value); }
    public bool KeepSource { get => Store.GetBool("keepSource", false); set => Store.Set("keepSource", value); }
    public bool KeepHistory { get => Store.GetBool("keepHistory", true); set => Store.Set("keepHistory", value); }
    public bool Notifications { get => Store.GetBool("notifications", true); set => Store.Set("notifications", value); }
    public int AcknowledgedVersion { get => Store.GetInt("acknowledgedSourcePolicyVersion", 0); set => Store.Set("acknowledgedSourcePolicyVersion", value); }
}

/// <summary>Taskbar progress (09-platforms.md › Taskbar) through ITaskbarList3.</summary>
internal static class Taskbar
{
    public enum State { None = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, State state);
    }

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList;

    private static ITaskbarList3? _list;

    private static ITaskbarList3? List()
    {
        if (_list is not null) return _list;
        try
        {
            var list = (ITaskbarList3)new TaskbarList();
            list.HrInit();
            return _list = list;
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return null; }   // no taskbar (some server editions)
    }

    public static void SetProgress(IntPtr hwnd, double fraction, State state)
    {
        try
        {
            if (List() is not { } list) return;
            list.SetProgressState(hwnd, state);
            if (state is State.Normal or State.Error) list.SetProgressValue(hwnd, (ulong)(Math.Clamp(fraction, 0, 1) * 1000), 1000);
        }
        catch (COMException) { /* progress on the taskbar is a convenience */ }
    }
}

/// <summary>
/// "3 downloads finished" notifications with Show in folder, when a batch ends while Potion Dropper is in the
/// background (06-components.md, "Completion feedback"). Unpackaged apps register with the Windows App SDK at startup.
/// </summary>
internal static class Notifications
{
    private static bool _registered;
    private static IReadOnlyList<string> _lastFiles = [];

    public static void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, _) =>
            {
                if (_lastFiles.FirstOrDefault(File.Exists) is { } first) ShowInFolder(first);
            };
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { _registered = false; }   // notifications are a convenience
    }

    public static void Unregister()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); } catch (Exception e) when (e is not OutOfMemoryException) { }
    }

    public static void BatchFinished(IReadOnlyList<DropQueueItem> done, IReadOnlyList<DropQueueItem> failed)
    {
        if (!_registered) return;
        var finished = done.Count == 1 ? "1 download finished" : $"{done.Count} downloads finished";
        var message = (done.Count > 0, failed.Count > 0) switch
        {
            (true, false) => finished,
            (true, true) => $"{finished}, {failed.Count} failed",
            _ => failed.Count == 1 ? "1 download failed" : $"{failed.Count} downloads failed",
        };
        _lastFiles = done.SelectMany(i => i.Files).ToList();
        try
        {
            var builder = new AppNotificationBuilder().AddText(message);
            if (_lastFiles.Count > 0) builder.AddButton(new AppNotificationButton("Show in folder").AddArgument("action", "show"));
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }

    /// <summary>Opens File Explorer with the file selected (no shell: explorer.exe with an argument list).</summary>
    public static void ShowInFolder(string path)
    {
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add($"/select,{path}");
        Process.Start(info);
    }
}
