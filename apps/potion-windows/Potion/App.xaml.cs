using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Potion;

public partial class App : Application
{
    private static MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;
        Notifications.Register(MainWindow.ShowInFolder);
        window.Closed += (_, _) => Notifications.Unregister();
        window.Activate();
        // Files passed on the command line (Open with, File Explorer's "Convert with Potion") join the queue.
        AddPaths(Environment.GetCommandLineArgs().Skip(1));
    }

    /// <summary>Another launch handed its files to this instance (Program.Main).</summary>
    public static void ReceiveRedirect(AppActivationArguments activation)
    {
        if (activation.Kind != ExtendedActivationKind.Launch || activation.Data is not ILaunchActivatedEventArgs launch) return;
        var paths = Program.SplitCommandLine(launch.Arguments).ToList();
        if (paths.Count > 0 && paths[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) paths.RemoveAt(0);
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            _window.Activate();
            AddPaths(paths);
        });
    }

    private static void AddPaths(IEnumerable<string> candidates)
    {
        var paths = candidates.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (paths.Count > 0 && _window is not null) _ = _window.AddPathsAsync(paths);
    }
}
