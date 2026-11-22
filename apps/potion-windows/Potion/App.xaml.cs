using Microsoft.UI.Xaml;

namespace Potion;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;
        window.Activate();
        // Files passed on the command line (Open with, drag onto the exe, File Explorer later) join the queue.
        var paths = Environment.GetCommandLineArgs().Skip(1).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (paths.Count > 0) _ = window.AddPathsAsync(paths);
    }
}
