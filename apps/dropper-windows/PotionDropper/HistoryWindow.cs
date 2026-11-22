using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Potion.Core.Dropper;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace PotionDropper;

/// <summary>
/// Download history (spec Part 6.1.4): only what the user can see here, and Clear history forgets all of it. The
/// downloaded files themselves are never touched.
/// </summary>
internal sealed class HistoryWindow : Window
{
    private static HistoryWindow? _open;
    private readonly DropHistory _history;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(8, 0, 8, 0) };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _empty = new()
    {
        Text = "Downloads you finish appear here.", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Button _clear = new() { Content = "Clear history…" };

    public static void ShowFor(DropHistory history)
    {
        if (_open is not null) { _open.Refresh(); _open.Activate(); return; }
        _open = new HistoryWindow(history);
        _open.Closed += (_, _) => _open = null;
        _open.Activate();
    }

    /// <summary>Called when a download is recorded, so an open window stays current.</summary>
    public static void RefreshIfOpen() => _open?.Refresh();

    private HistoryWindow(DropHistory history)
    {
        _history = history;
        Title = "Download history";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon("Assets/PotionDropper.ico");
        AppWindow.Resize(new SizeInt32(720, 520));

        _count.Style = (Style)Ui.Resource("CaptionTextBlockStyle");
        _count.Foreground = (Brush)Ui.Resource("PotionTextSecondaryBrush");
        _empty.Foreground = (Brush)Ui.Resource("PotionTextSecondaryBrush");
        _clear.Click += async (_, _) =>
        {
            if (!await SettingsWindow.ConfirmClear(Content.XamlRoot)) return;
            _history.Clear();
            Refresh();
        };
        _list.RightTapped += List_RightTapped;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_list);
        root.Children.Add(_empty);
        var footer = new Grid { Padding = new Thickness(16, 8, 16, 8), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = (Brush)Ui.Resource("PotionBorderBrush") };
        footer.Children.Add(_count);
        _clear.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(_clear);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        Content = root;
        Refresh();
    }

    private void Refresh()
    {
        var entries = _history.Entries;
        _list.Items.Clear();
        foreach (var entry in entries) _list.Items.Add(Row(entry));
        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _count.Text = entries.Count == 1 ? "1 download" : $"{entries.Count} downloads";
        _clear.IsEnabled = entries.Count > 0;
    }

    private static FrameworkElement Row(DropHistory.Entry entry)
    {
        var failed = entry.Error is not null;
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), Tag = entry };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon
        {
            Glyph = failed ? "" : "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center,   // Error / CheckMark
            Foreground = (Brush)Ui.Resource(failed ? "PotionErrorBrush" : "PotionSuccessBrush"),
        });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Ui.Resource("BodyStrongTextBlockStyle") });
        text.Children.Add(new TextBlock
        {
            Text = entry.Error ?? $"{entry.Site} · {entry.Date.LocalDateTime:g}", TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Ui.Resource("CaptionTextBlockStyle"),
            Foreground = (Brush)Ui.Resource(failed ? "PotionErrorBrush" : "PotionTextSecondaryBrush"),
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        ToolTipService.SetToolTip(grid, entry.Link);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, failed ? $"{entry.Name}, failed" : entry.Name);
        return grid;
    }

    private void List_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && (element as FrameworkElement)?.Tag is not DropHistory.Entry) element = VisualTreeHelper.GetParent(element);
        if ((element as FrameworkElement)?.Tag is not DropHistory.Entry entry) return;
        var menu = new MenuFlyout();
        void Add(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        if (entry.Files.FirstOrDefault(File.Exists) is { } file) Add("Show in folder", () => Notifications.ShowInFolder(file));
        Add("Copy link", () =>
        {
            var package = new DataPackage();
            package.SetText(entry.Link);
            Clipboard.SetContent(package);
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Remove from history", () => { _history.Remove(entry.Id); Refresh(); });
        menu.ShowAt(_list, e.GetPosition(_list));
        e.Handled = true;
    }
}
