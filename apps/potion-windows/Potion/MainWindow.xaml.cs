using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Potion.Core.App;
using Potion.Core.Formats;
using Potion.Core.Windows;
using Potion.Design;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Potion;

public sealed partial class MainWindow : Window
{
    // design-system/tokens/tokens.json › size.window.windows (in effective pixels).
    private const int DefaultWidth = 820, DefaultHeight = 580;
    private const int MinWidth = 600, MinHeight = 440;

    public QueueModel Queue { get; } = new(SettingsStore.Default());

    private DropState _drop = DropState.None;
    private readonly ConversionService _service = ConversionService.WithBundledTools();
    private bool _updatingSaveTo;
    private bool _closeConfirmed;
    private bool _isActive = true;
    private string? _notifiedCompletion;

    public MainWindow()
    {
        InitializeComponent();
        Root.Loaded += async (_, _) => await ShowFirstRunNoticeAsync();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/Potion.ico");

        // AppWindow sizes are in physical pixels; scale the effective-pixel tokens by the window's DPI.
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(DefaultWidth * scale), (int)(DefaultHeight * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinHeight * scale);
        }

        Queue.Converter = (item, directory, progress, token) =>
            _service.ConvertAsync(item.Path, item.Format, item.Output!, directory, item.Settings, progress, token);
        Queue.Recycle = NativeShell.MoveToRecycleBin;
        Queue.PropertyChanged += Queue_PropertyChanged;
        AppWindow.Closing += AppWindow_Closing;
        Activated += (_, e) => _isActive = e.WindowActivationState != WindowActivationState.Deactivated;
        Refresh();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>Files passed on the command line (Open with, File Explorer) and from the pickers end up here.</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths) => await Queue.AddAsync(paths);

    // State → view

    private void Queue_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var empty = Queue.IsEmpty;
        DropZone.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        QueueList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        Footer.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        RefreshFooter();
        ChooseButton.Visibility = Queue.IsAdding ? Visibility.Collapsed : Visibility.Visible;
        AddingRing.Visibility = Queue.IsAdding ? Visibility.Visible : Visibility.Collapsed;
        AddingRing.IsActive = Queue.IsAdding;
        DropZoneCaption.Text = Queue.IncludeSubfolders
            ? "Video, audio, images, documents and PDFs. Folders include their subfolders."
            : "Video, audio, images, documents and PDFs";
        Banner.Message = Queue.BannerMessage ?? "";
        Banner.IsOpen = Queue.BannerMessage is not null;
        ShowDropState();
    }

    /// <summary>Footer states (06-components.md): options and Convert all, progress and Stop, or the summary.</summary>
    private void RefreshFooter()
    {
        var converting = Queue.IsConverting;
        var completion = !converting && Queue.CompletionMessage is not null;
        IdleOptions.Visibility = !converting && !completion ? Visibility.Visible : Visibility.Collapsed;
        ConvertingPanel.Visibility = converting ? Visibility.Visible : Visibility.Collapsed;
        CompletionPanel.Visibility = completion ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.Visibility = converting ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = converting ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = !converting;
        ConvertButton.IsEnabled = Queue.CanConvert;
        if (converting)
        {
            ConvertingText.Text = Queue.ConvertingText;
            OverallProgress.Value = Queue.BatchFraction;
        }
        if (completion)
        {
            // When the batch ends in the background, also send a notification (once per batch).
            if (!_isActive && Queue.Settings.GetBool(SettingsKeys.Notifications, true) && _notifiedCompletion != Queue.CompletionMessage)
            {
                _notifiedCompletion = Queue.CompletionMessage;
                Notifications.Show(Queue.CompletionMessage!, Queue.CompletionOutputs);
            }
            CompletionText.Text = Queue.CompletionMessage!;
            CompletionIcon.Glyph = Queue.CompletionHasFailures ? "\uE783" : "\uE73E";
            CompletionIcon.Foreground = (Brush)Ui.Resource(Queue.CompletionHasFailures ? "PotionErrorBrush" : "PotionSuccessBrush");
        }
        RecycleBox.IsChecked = Queue.MoveOriginalsToRecycleBin;
        var sameKind = Queue.Items.Count > 1 && Queue.Items.Select(i => i.Category).Distinct().Count() == 1
                       && Queue.CommonOutputs(Queue.Items.ToList()).Count > 0;
        ConvertAllToButton.Visibility = sameKind ? Visibility.Visible : Visibility.Collapsed;
        if (sameKind)
        {
            var current = Queue.Items.Select(i => i.OutputName).Distinct().ToList();
            ConvertAllToButton.Content = current.Count == 1 ? $"Convert all to: {current[0]}" : "Convert all to…";
        }
        RefreshSaveTo();
        // Taskbar: progress while converting; the error state after a batch with failures (09-platforms.md).
        NativeShell.SetProgress(Hwnd, converting ? Queue.BatchFraction : 1,
            converting ? NativeShell.TaskbarState.Normal
            : completion && Queue.CompletionHasFailures ? NativeShell.TaskbarState.Error : NativeShell.TaskbarState.None);
    }

    /// <summary>"Same folder as original", recent folders, then "Choose folder…" (Sentence case on Windows).</summary>
    private void RefreshSaveTo()
    {
        _updatingSaveTo = true;
        var folders = Queue.RecentFolders.ToList();
        if (Queue.SaveFolder is { } current && !folders.Contains(current, StringComparer.OrdinalIgnoreCase)) folders.Insert(0, current);
        SaveToBox.Items.Clear();
        SaveToBox.Items.Add(new ComboBoxItem { Content = "Same folder as original", Tag = "" });
        foreach (var folder in folders)
        {
            var entry = new ComboBoxItem { Content = Path.GetFileName(folder) is { Length: > 0 } n ? n : folder, Tag = folder };
            ToolTipService.SetToolTip(entry, folder);
            SaveToBox.Items.Add(entry);
        }
        SaveToBox.Items.Add(new ComboBoxItem { Content = "Choose folder…", Tag = "choose" });
        SaveToBox.SelectedIndex = Queue.SaveFolder is { } selected
            ? 1 + folders.FindIndex(f => string.Equals(f, selected, StringComparison.OrdinalIgnoreCase))
            : 0;
        _updatingSaveTo = false;
    }

    private async void SaveToBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSaveTo || SaveToBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (tag == "choose")
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            if (await picker.PickSingleFolderAsync() is StorageFolder folder) Queue.UseSaveFolder(folder.Path);
            RefreshSaveTo();
        }
        else Queue.UseSaveFolder(tag.Length == 0 ? null : tag);
    }

    private void RecycleBox_Click(object sender, RoutedEventArgs e) => Queue.MoveOriginalsToRecycleBin = RecycleBox.IsChecked == true;

    private void Convert_Click(object sender, RoutedEventArgs e)
    {
        _notifiedCompletion = null;
        Queue.ConvertAll();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsWindow.ShowFor(Queue, _service.Tools is not null);

    private void Stop_Click(object sender, RoutedEventArgs e) => Queue.Stop();

    private void ShowCompletion_Click(object sender, RoutedEventArgs e)
    {
        if (Queue.CompletionOutputs.FirstOrDefault(File.Exists) is { } first) ShowInFolder(first);
    }

    // About and legal documents (spec Part 6.4)

    /// <summary>Spec Part 6.2: the one-time first-run note, also in About.</summary>
    private const string PotionNote = "Potion converts files on this computer. Nothing is uploaded. Converting a file doesn't change who owns it: only convert files you have the right to use. Potion doesn't remove copy protection.";

    private void About_Click(object sender, RoutedEventArgs e) => PotionShared.AboutWindow.ShowFor("Potion", PotionNote, null, "Assets/Potion.ico");
    private void Terms_Click(object sender, RoutedEventArgs e) => PotionShared.Legal.Show(PotionShared.LegalDocument.Terms);
    private void Privacy_Click(object sender, RoutedEventArgs e) => PotionShared.Legal.Show(PotionShared.LegalDocument.Privacy);

    /// <summary>First launch: the spec Part 6.2 note with the Terms of use and Privacy policy.</summary>
    private async Task ShowFirstRunNoticeAsync()
    {
        const string key = "firstRunNoticeVersion";
        if (Queue.Settings.GetInt(key, 0) >= 1) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Welcome to Potion",
            Content = new StackPanel
            {
                Children = { new TextBlock { Text = PotionNote, TextWrapping = TextWrapping.Wrap }, PotionShared.Legal.AgreementLine() },
            },
            PrimaryButtonText = "Continue",
            DefaultButton = ContentDialogButton.Primary,
        };
        await dialog.ShowAsync();
        Queue.Settings.Set(key, 1);
    }

    /// <summary>Asks before closing in the middle of a batch (files that haven't finished won't be saved).</summary>
    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || !Queue.IsConverting) return;
        args.Cancel = true;
        var remaining = Queue.Remaining;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = remaining == 1 ? "Potion is still converting 1 file." : $"Potion is still converting {remaining} files.",
            Content = "If you close Potion now, files that haven't finished won't be saved.",
            PrimaryButtonText = "Keep converting",
            SecondaryButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Secondary)
        {
            Queue.Stop();
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>Drag-over look (06-components.md): accent when it can be added, error when nothing can be converted.</summary>
    private void ShowDropState()
    {
        var targeted = _drop != DropState.None;
        DropZoneTitle.Text = targeted ? _drop.Label : "Drop files or folders";
        DropZoneTitle.Foreground = (Brush)Ui.Resource(_drop.Invalid ? "PotionErrorBrush" : "PotionTextPrimaryBrush");
        if (!targeted)
        {
            DropZoneBorder.Stroke = (Brush)Ui.Resource("PotionBorderStrongBrush");
            DropZoneBorder.StrokeThickness = 1.5;
            DropZoneBorder.StrokeDashArray = new DoubleCollection { 4, 2.67 };
            DropZoneBorder.Fill = null;
        }
        else if (_drop.Invalid)
        {
            DropZoneBorder.Stroke = (Brush)Ui.Resource("PotionErrorBrush");
            DropZoneBorder.StrokeThickness = 1.5;
            DropZoneBorder.StrokeDashArray = new DoubleCollection { 4, 2.67 };
            DropZoneBorder.Fill = null;
        }
        else
        {
            var accent = (Windows.UI.Color)Ui.Resource("SystemAccentColor");
            DropZoneBorder.Stroke = new SolidColorBrush(accent);
            DropZoneBorder.StrokeThickness = 2;
            DropZoneBorder.StrokeDashArray = new DoubleCollection();
            DropZoneBorder.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(20, accent.R, accent.G, accent.B));   // 8%
        }
        QueueList.BorderBrush = targeted && !Queue.IsEmpty ? DropZoneBorder.Stroke : null;
        QueueList.BorderThickness = new Thickness(targeted && !Queue.IsEmpty ? 2 : 0);
    }

    // Drag and drop (the whole window is a drop target)

    private static async Task<List<string>> PathsOf(DataPackageView data)
    {
        if (!data.Contains(StandardDataFormats.StorageItems)) return [];
        var items = await data.GetStorageItemsAsync();
        return items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    private async void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.None; return; }
        var deferral = e.GetDeferral();
        try
        {
            if (_drop == DropState.None)
            {
                var paths = await PathsOf(e.DataView);
                _drop = await Task.Run(() => DropState.Classify(paths));
                ShowDropState();
            }
            e.AcceptedOperation = _drop.Invalid ? DataPackageOperation.None : DataPackageOperation.Copy;
            e.DragUIOverride.Caption = _drop.Label;
        }
        finally { deferral.Complete(); }
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        _drop = DropState.None;
        ShowDropState();
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var invalid = _drop.Invalid;
            _drop = DropState.None;
            ShowDropState();
            if (!invalid) await Queue.AddAsync(await PathsOf(e.DataView));
        }
        finally { deferral.Complete(); }
    }

    // Commands

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 }) await Queue.AddAsync(files.Select(f => f.Path));
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        if (await picker.PickSingleFolderAsync() is StorageFolder folder) await Queue.AddAsync([folder.Path]);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Queue.Clear();

    private void Banner_Closed(InfoBar sender, InfoBarClosedEventArgs args) => Queue.DismissBanner();

    private List<QueueItem> Selected => QueueList.SelectedItems.OfType<QueueItem>().ToList();

    /// <summary>Changing a selected row's format changes every selected row that can take it.</summary>
    private List<QueueItem> TargetsFor(QueueItem item) => Selected.Contains(item) ? Selected : [item];

    private void FormatButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton { DataContext: QueueItem item } button) return;
        var targets = TargetsFor(item);
        button.Flyout = FormatMenu(Queue.CommonOutputs(targets), id => Queue.SetOutput(targets, id));
    }

    private static MenuFlyout FormatMenu(IReadOnlyList<FileFormat> formats, Action<string> choose)
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var entry in FormatMenuItems(formats, choose)) menu.Items.Add(entry);
        return menu;
    }

    /// <summary>Formats grouped by category, each group under its name (06-components.md, "Format popup").
    /// New items every time: a menu item can only belong to one menu.</summary>
    private static List<MenuFlyoutItemBase> FormatMenuItems(IReadOnlyList<FileFormat> formats, Action<string> choose)
    {
        var items = new List<MenuFlyoutItemBase>();
        foreach (var (category, group) in FormatGroups.Of(formats))
        {
            if (items.Count > 0) items.Add(new MenuFlyoutSeparator());
            items.Add(new MenuFlyoutItem { Text = FormatGroups.Label(category), IsEnabled = false });
            foreach (var format in group)
            {
                var entry = new MenuFlyoutItem { Text = format.Name };
                entry.Click += (_, _) => choose(format.Id);
                items.Add(entry);
            }
        }
        return items;
    }

    /// <summary>Double-click a row (or Ctrl+I) to open the convert dialog for it, or for the selection.</summary>
    private void QueueList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not QueueItem item || item.IsConverting) return;
        _ = ConvertDialog.ShowAsync(Root.XamlRoot, Queue, TargetsFor(item).Where(t => !t.IsConverting).ToList());
    }

    private void Options_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var targets = Selected.Where(t => !t.IsConverting).ToList();
        if (targets.Count > 0) _ = ConvertDialog.ShowAsync(Root.XamlRoot, Queue, targets);
        args.Handled = true;
    }

    /// <summary>"Convert all to: MP3" when every row is the same kind of file (05-layout.md).</summary>
    private void ConvertAllTo_Click(object sender, RoutedEventArgs e)
    {
        var all = Queue.Items.ToList();
        ConvertAllToButton.Flyout = FormatMenu(Queue.CommonOutputs(all), id => Queue.SetOutput(all, id));
    }

    private void QueueList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Delete && Selected.Count > 0) { Queue.Remove(Selected); e.Handled = true; }
    }

    private void QueueList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not QueueItem item) return;
        if (!Selected.Contains(item)) QueueList.SelectedItem = item;
        var targets = TargetsFor(item);
        var menu = new MenuFlyout();
        var formats = Queue.CommonOutputs(targets);
        if (formats.Count > 0)
        {
            var change = new MenuFlyoutSubItem { Text = "Change format" };
            foreach (var entry in FormatMenuItems(formats, id => Queue.SetOutput(targets, id))) change.Items.Add(entry);
            menu.Items.Add(change);
        }
        var options = new MenuFlyoutItem { Text = "Options…", IsEnabled = targets.All(t => !t.IsConverting) };
        options.Click += (_, _) => _ = ConvertDialog.ShowAsync(Root.XamlRoot, Queue, targets);
        menu.Items.Insert(0, options);
        var retryable = targets.Where(t => t.Status is QueueStatus.Failed or QueueStatus.Canceled).ToList();
        if (retryable.Count > 0)
        {
            var retry = new MenuFlyoutItem { Text = "Retry" };
            retry.Click += (_, _) => Queue.Convert(retryable);
            menu.Items.Add(retry);
        }
        var show = new MenuFlyoutItem { Text = "Show in folder" };
        // Done rows show the converted file; others the original.
        show.Click += (_, _) => ShowInFolder(targets.First().SavedFiles.FirstOrDefault(File.Exists) ?? targets.First().Path);
        menu.Items.Add(show);
        menu.Items.Add(new MenuFlyoutSeparator());
        var remove = new MenuFlyoutItem { Text = "Remove" };
        remove.Click += (_, _) => Queue.Remove(targets);
        menu.Items.Add(remove);
        menu.ShowAt(QueueList, e.GetPosition(QueueList));
        e.Handled = true;
    }

    /// <summary>Opens File Explorer with the file selected (no shell: explorer.exe with an argument list).</summary>
    public static void ShowInFolder(string path)
    {
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add($"/select,{path}");
        Process.Start(info);
    }
}

/// <summary>x:Bind helpers mapping categories and statuses to design-system brushes and glyphs.</summary>
public static class Ui
{
    public static object Resource(string key) => Application.Current.Resources[key];

    private static PotionCategory Design(FormatCategory c) => c switch
    {
        FormatCategory.Image => PotionCategory.Image, FormatCategory.Audio => PotionCategory.Audio,
        FormatCategory.Video => PotionCategory.Video, _ => PotionCategory.Docs,
    };

    public static Brush TintBrush(FormatCategory c) => (Brush)Resource(Design(c).ResourcePrefix() + "TintBrush");
    public static Brush TextBrush(FormatCategory c) => (Brush)Resource(Design(c).ResourcePrefix() + "TextBrush");
    public static Brush FillBrush(FormatCategory c) => (Brush)Resource(Design(c).ResourcePrefix() + "FillBrush");
    public static string Glyph(FormatCategory c) => Design(c).Glyph();

    /// <summary>Status always comes with an icon plus words (CLAUDE.md, design system).</summary>
    public static string StatusGlyph(QueueStatus s) => s switch
    {
        QueueStatus.Done => "\uE73E",       // CheckMark
        QueueStatus.Failed => "\uE783",     // Error
        _ => "",
    };

    public static Brush StatusBrush(QueueStatus s) => (Brush)Resource(s switch
    {
        QueueStatus.Done => "PotionSuccessBrush",
        QueueStatus.Failed => "PotionErrorBrush",
        QueueStatus.Converting => "PotionTextSecondaryBrush",
        _ => "PotionTextTertiaryBrush",
    });
}
