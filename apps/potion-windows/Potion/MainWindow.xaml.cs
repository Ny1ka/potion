using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Potion.Design;
using Windows.Graphics;

namespace Potion;

public sealed partial class MainWindow : Window
{
    // design-system/tokens/tokens.json › size.window.windows (in effective pixels).
    private const int DefaultWidth = 820, DefaultHeight = 580;
    private const int MinWidth = 600, MinHeight = 440;

    public MainWindow()
    {
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/Potion.ico");

        ContentRoot.Margin = new Thickness(PotionSpacing.X6, 0, PotionSpacing.X6, PotionSpacing.X6);

        // AppWindow sizes are in physical pixels; scale the effective-pixel tokens by the window's DPI.
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(DefaultWidth * scale), (int)(DefaultHeight * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinHeight * scale);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
