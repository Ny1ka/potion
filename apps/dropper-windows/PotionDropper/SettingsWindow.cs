using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Potion.Core.Dropper;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PotionDropper;

/// <summary>
/// Settings (05-layout.md, "Settings"; Windows: one page with grouped rows, no brand color): General, Output and
/// Advanced, the same options as the macOS app. Changes apply to downloads queued afterwards.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    private readonly DropperSettings _settings;
    private readonly DropQueue _queue;
    private readonly DropHistory _history;
    private readonly TextBlock _folderText = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240 };
    private readonly TextBlock _example = new() { TextWrapping = TextWrapping.Wrap };

    public static void ShowFor(DropperSettings settings, DropQueue queue, DropHistory history)
    {
        if (_open is not null) { _open.Activate(); return; }
        _open = new SettingsWindow(settings, queue, history);
        _open.Closed += (_, _) => _open = null;
        _open.Activate();
    }

    private SettingsWindow(DropperSettings settings, DropQueue queue, DropHistory history)
    {
        _settings = settings;
        _queue = queue;
        _history = history;
        Title = "Potion Dropper settings";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon("Assets/PotionDropper.ico");
        AppWindow.Resize(new SizeInt32(560, 720));

        var page = new StackPanel { Spacing = 8, Padding = new Thickness(24), MaxWidth = 640 };
        page.Children.Add(new TextBlock { Text = "Settings", Style = (Style)Ui.Resource("TitleTextBlockStyle"), Margin = new Thickness(0, 0, 0, 8) });

        page.Children.Add(Section("General"));
        page.Children.Add(Toggle("Notify me when downloads finish while Potion Dropper is in the background", settings.Notifications, on => settings.Notifications = on));
        var parallel = new NumberBox
        {
            Minimum = 1, Maximum = DropQueue.ParallelLimit, Value = settings.MaxParallel,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 120,
        };
        parallel.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue)) return;
            settings.MaxParallel = (int)e.NewValue;
            queue.MaxParallel = settings.MaxParallel;
        };
        page.Children.Add(Row("Downloads at once", parallel));
        page.Children.Add(Toggle("Keep a download history", settings.KeepHistory, on => settings.KeepHistory = on,
            "The history stays on this PC and lists only what you see in Download history."));
        var clear = new Button { Content = "Clear history…" };
        clear.Click += async (_, _) =>
        {
            if (await ConfirmClear(Content.XamlRoot)) _history.Clear();
        };
        page.Children.Add(Row("Clear the download history", clear, "Downloaded files stay where they are."));

        page.Children.Add(Section("Output"));
        var choose = new Button { Content = "Choose folder…" };
        choose.Click += async (_, _) => await ChooseFolder();
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        folderRow.Children.Add(_folderText);
        folderRow.Children.Add(choose);
        page.Children.Add(Row("Save downloads to", folderRow));
        RefreshFolder();
        page.Children.Add(Toggle("Keep the original download next to the converted file", settings.KeepSource, on => settings.KeepSource = on));
        var template = new TextBox { Text = settings.NameTemplate, Width = 240 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(template, "File names");
        template.TextChanged += (_, _) =>
        {
            settings.NameTemplate = template.Text;
            RefreshExample();
        };
        page.Children.Add(Row("File names", template, "Use {title}, {site}, {date} and {format}."));
        _example.Foreground = (Brush)Ui.Resource("PotionTextSecondaryBrush");
        _example.Style = (Style)Ui.Resource("CaptionTextBlockStyle");
        page.Children.Add(_example);
        RefreshExample();

        page.Children.Add(Section("Advanced"));
        var size = new ComboBox { MinWidth = 140 };
        foreach (var gb in new[] { 1, 2, 4, 8, 16, 32 }) size.Items.Add(new ComboBoxItem { Content = $"{gb} GB", Tag = gb });
        size.Items.Add(new ComboBoxItem { Content = "No limit", Tag = 0 });
        size.SelectedItem = size.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == settings.MaxSizeGB) ?? size.Items[2];
        size.SelectionChanged += (_, _) => { if (size.SelectedItem is ComboBoxItem { Tag: int gb }) settings.MaxSizeGB = gb; };
        page.Children.Add(Row("Largest download", size));
        var minutes = new NumberBox
        {
            Minimum = 5, Maximum = 240, SmallChange = 5, LargeChange = 30, Value = settings.DurationMinutes,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 140, IsEnabled = settings.LimitDuration,
        };
        minutes.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) settings.DurationMinutes = (int)e.NewValue; };
        page.Children.Add(Toggle("Limit the length of videos and audio", settings.LimitDuration, on =>
        {
            settings.LimitDuration = on;
            minutes.IsEnabled = on;
        }));
        page.Children.Add(Row("Longest (minutes)", minutes, "Longer files are deleted after downloading, since direct links don't say how long they are."));

        Content = new ScrollViewer { Content = page };
    }

    private void RefreshFolder()
    {
        _folderText.Text = Path.GetFileName(_settings.OutputFolder.TrimEnd(Path.DirectorySeparatorChar));
        ToolTipService.SetToolTip(_folderText, _settings.OutputFolder);
    }

    private void RefreshExample()
    {
        var registry = SourceRegistry.Shared;
        var name = DropNaming.Render(_settings.NameTemplate, "Interview.mp4", "www.example.com", DateTime.Now.ToString("yyyy-MM-dd"), "MP3",
            registry.Download.MaxFileNameLength - registry.Naming.ReserveForSuffix, registry.Naming.FallbackName);
        _example.Text = $"Example: {name}.mp3";
    }

    private async Task ChooseFolder()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSingleFolderAsync() is StorageFolder folder)
        {
            _settings.OutputFolder = folder.Path;
            RefreshFolder();
        }
    }

    public static async Task<bool> ConfirmClear(XamlRoot root)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Clear the download history?",
            Content = "The list is deleted from this PC. Downloaded files stay where they are.",
            PrimaryButtonText = "Clear history",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static TextBlock Section(string title) => new()
    {
        Text = title, Style = (Style)Ui.Resource("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 16, 0, 4),
    };

    /// <summary>A settings row: label (and optional description) on the left, control on the right.</summary>
    private static Border Row(string label, FrameworkElement control, string? description = null)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (description is not null)
            text.Children.Add(new TextBlock
            {
                Text = description, TextWrapping = TextWrapping.Wrap, Style = (Style)Ui.Resource("CaptionTextBlockStyle"),
                Foreground = (Brush)Ui.Resource("PotionTextSecondaryBrush"),
            });
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return new Border
        {
            Child = grid, Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(4),
            Background = (Brush)Ui.Resource("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = (Brush)Ui.Resource("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
        };
    }

    private static Border Toggle(string label, bool on, Action<bool> changed, string? description = null)
    {
        var toggle = new ToggleSwitch { IsOn = on, OnContent = "On", OffContent = "Off", MinWidth = 0 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return Row(label, toggle, description);
    }
}
