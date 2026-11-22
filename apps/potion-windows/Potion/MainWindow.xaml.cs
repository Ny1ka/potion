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

    public MainWindow()
    {
        InitializeComponent();

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

        Queue.PropertyChanged += Queue_PropertyChanged;
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
        CountText.Text = Queue.CountText;
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
        var show = new MenuFlyoutItem { Text = "Show in folder" };
        show.Click += (_, _) => ShowInFolder(targets.First().Path);
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
        QueueStatus.Done => "",       // CheckMark
        QueueStatus.Failed => "",     // Error
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
