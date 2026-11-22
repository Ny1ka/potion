using Microsoft.UI.Xaml;

namespace PotionDropper;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        Notifications.Register();
        _window.Closed += (_, _) => Notifications.Unregister();
        _window.Activate();
    }
}
