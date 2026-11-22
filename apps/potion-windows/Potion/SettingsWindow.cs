using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Potion.Core.App;
using Potion.Core.Formats;
using Windows.Graphics;

namespace Potion;

/// <summary>
/// Settings (05-layout.md, "Settings"; Windows: its own page with grouped rows, no brand color): General, Output and
/// Advanced. Changes apply immediately.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    private readonly QueueModel _queue;
    private readonly bool _toolsInstalled;

    public static void ShowFor(QueueModel queue, bool toolsInstalled)
    {
        if (_open is not null) { _open.Activate(); return; }
        _open = new SettingsWindow(queue, toolsInstalled);
        _open.Closed += (_, _) => _open = null;
        _open.Activate();
    }

    private SettingsWindow(QueueModel queue, bool toolsInstalled)
    {
        _queue = queue;
        _toolsInstalled = toolsInstalled;
        Title = "Potion settings";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon("Assets/Potion.ico");
        var scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;
        AppWindow.Resize(new SizeInt32((int)(560 * scale), (int)(640 * scale)));

        var page = new StackPanel { Spacing = 8, Padding = new Thickness(24), MaxWidth = 640 };
        page.Children.Add(new TextBlock { Text = "Settings", Style = (Style)Ui.Resource("TitleTextBlockStyle"), Margin = new Thickness(0, 0, 0, 8) });

        page.Children.Add(Section("General"));
        page.Children.Add(Toggle("Include subfolders when adding a folder", queue.IncludeSubfolders, on => queue.IncludeSubfolders = on));
        page.Children.Add(Toggle("Notify me when conversions finish while Potion is in the background",
            queue.Settings.GetBool(SettingsKeys.Notifications, true), on => queue.Settings.Set(SettingsKeys.Notifications, on)));
        page.Children.Add(Toggle("Show “Convert with Potion” in File Explorer", ExplorerMenu.IsInstalled, on =>
        {
            try { if (on) ExplorerMenu.Install(); else ExplorerMenu.Uninstall(); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException) { }
            queue.Settings.Set(SettingsKeys.ExplorerMenu, on);
        }, "On Windows 11, it's under “Show more options”."));

        page.Children.Add(Section("Output"));
        page.Children.Add(Row("Save converted files to", new TextBlock
        {
            Text = queue.SaveFolder ?? "Same folder as original",
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260,
        }, "Change this in the main window's footer."));
        page.Children.Add(Toggle("Move originals to Recycle Bin after converting", queue.MoveOriginalsToRecycleBin, on => queue.MoveOriginalsToRecycleBin = on));
        foreach (var category in Enum.GetValues<FormatCategory>()) page.Children.Add(DefaultFormat(category));

        page.Children.Add(Section("Advanced"));
        var parallel = new NumberBox
        {
            Minimum = 1, Maximum = QueueModel.ParallelLimit, Value = queue.MaxParallel, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 120,
        };
        parallel.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) queue.MaxParallel = (int)e.NewValue; };
        page.Children.Add(Row("Files converted at the same time", parallel));
        page.Children.Add(Row("Audio and video tools", new TextBlock
        {
            Text = _toolsInstalled ? "Installed" : "Missing. Reinstall Potion to restore them.",
            Foreground = (Brush)Ui.Resource(_toolsInstalled ? "PotionSuccessBrush" : "PotionErrorBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        }));

        Content = new ScrollViewer { Content = page };
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
            text.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Style = (Style)Ui.Resource("CaptionTextBlockStyle"), Foreground = (Brush)Ui.Resource("PotionTextSecondaryBrush") });
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

    /// <summary>The format new files of a type start with; "Automatic" uses the last one picked.</summary>
    private Border DefaultFormat(FormatCategory category)
    {
        var key = SettingsKeys.LastOutput(category);
        var combo = new ComboBox { MinWidth = 160 };
        combo.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = "" });
        foreach (var format in DefaultFormats.For(category)) combo.Items.Add(new ComboBoxItem { Content = format.Name, Tag = format.Id });
        var current = _queue.Settings.GetString(key) ?? "";
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? combo.Items[0];
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string id }) _queue.Settings.Set(key, id.Length == 0 ? null : id);
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(combo, $"Default format for {FormatGroups.Label(category)}");
        return Row($"Default format for {FormatGroups.Label(category).ToLowerInvariant()} files", combo);
    }
}
