using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Potion;

/// <summary>
/// "Convert with Potion" in File Explorer's classic context menu (per-user registry, no admin rights). On Windows 11 it
/// appears under "Show more options"; the top-level Windows 11 menu needs package identity (MSIX or a sparse package,
/// which needs signing: M9). Selected files reach the running window through single-instance redirection (Program.cs).
/// </summary>
internal static class ExplorerMenu
{
    private const string VerbKey = "PotionConvert";
    private static readonly string[] Targets = [@"Software\Classes\*\shell", @"Software\Classes\Directory\shell"];

    public static bool IsInstalled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{Targets[0]}\{VerbKey}\command");
            return key?.GetValue(null) is string command && command.Contains(Environment.ProcessPath ?? "Potion.exe", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Install()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find Potion.exe");
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Potion.ico");
        foreach (var target in Targets)
        {
            using var verb = Registry.CurrentUser.CreateSubKey($@"{target}\{VerbKey}");
            verb.SetValue(null, "Convert with Potion");
            verb.SetValue("Icon", File.Exists(icon) ? icon : exe);
            verb.SetValue("MultiSelectModel", "Player");   // one launch for a multi-file selection where the shell allows it
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{exe}\" \"%1\"");
        }
    }

    public static void Uninstall()
    {
        foreach (var target in Targets) Registry.CurrentUser.DeleteSubKeyTree($@"{target}\{VerbKey}", throwOnMissingSubKey: false);
    }
}

/// <summary>
/// "12 files converted" notifications with Show in folder, when a batch finishes while Potion is in the background
/// (06-components.md, "Completion feedback"). Unpackaged apps register with the Windows App SDK at startup.
/// </summary>
internal static class Notifications
{
    private static bool _registered;
    private static IReadOnlyList<string> _lastOutputs = [];

    public static void Register(Action<string> showInFolder)
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, args) =>
            {
                if (_lastOutputs.FirstOrDefault(File.Exists) is { } first) showInFolder(first);
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

    public static void Show(string message, IReadOnlyList<string> outputs)
    {
        if (!_registered) return;
        _lastOutputs = outputs;
        try
        {
            var builder = new AppNotificationBuilder().AddText(message);
            if (outputs.Count > 0) builder.AddButton(new AppNotificationButton("Show in folder").AddArgument("action", "show"));
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }
}
