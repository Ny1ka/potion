using System.Runtime.InteropServices;

namespace Potion;

/// <summary>Small Windows shell helpers: Recycle Bin and taskbar progress (09-platforms.md › Taskbar).</summary>
internal static class NativeShell
{
    // Recycle Bin (SHFileOperation with FOF_ALLOWUNDO), no confirmation and no UI; throws on failure so the original stays.

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004, FofNoConfirmation = 0x0010, FofAllowUndo = 0x0040, FofNoErrorUi = 0x0400;

    public static void MoveToRecycleBin(string path)
    {
        var operation = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = Path.GetFullPath(path) + "\0\0",   // double-null-terminated list
            fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi),
        };
        var result = SHFileOperation(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted) throw new IOException($"Couldn't move {path} to the Recycle Bin ({result}).");
    }

    // Taskbar progress (ITaskbarList3)

    public enum TaskbarState { None = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

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
        void SetProgressState(IntPtr hwnd, TaskbarState state);
    }

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList;

    private static ITaskbarList3? _taskbar;

    private static ITaskbarList3? Taskbar()
    {
        if (_taskbar is not null) return _taskbar;
        try
        {
            var list = (ITaskbarList3)new TaskbarList();
            list.HrInit();
            return _taskbar = list;
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return null; }   // no taskbar (e.g. some server editions)
    }

    public static void SetProgress(IntPtr hwnd, double fraction, TaskbarState state)
    {
        try
        {
            var taskbar = Taskbar();
            if (taskbar is null) return;
            taskbar.SetProgressState(hwnd, state);
            if (state is TaskbarState.Normal or TaskbarState.Error) taskbar.SetProgressValue(hwnd, (ulong)(Math.Clamp(fraction, 0, 1) * 1000), 1000);
        }
        catch (COMException) { /* progress on the taskbar is a convenience */ }
    }
}
