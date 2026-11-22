using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Potion.Core.App;
using Potion.Core.Dropper;
using Potion.Core.Engines;
using Potion.Core.Formats;
using Potion.Design;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace PotionDropper;

/// <summary>
/// The main window (05-layout.md, "Potion Dropper: main window"): link bar, media card (or batch card) with output
/// options, then the download queue. Same behavior and wording as the macOS app, in Windows terms.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 720, DefaultHeight = 520;
    private readonly SourceRegistry _registry = SourceRegistry.Shared;
    private readonly Downloader _downloader;
    private readonly DropHistory _history;
    internal SettingsStore Settings { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Potion Dropper", "settings.json"));

    public DropQueue Queue { get; }

    private CancellationTokenSource? _fetch;
    private DropLink? _link;
    private LinkPreview? _preview;
    private FileFormat? _source;
    private IReadOnlyList<DropLink> _batch = [];
    private DropMode _mode = DropMode.Video;

    public MainWindow()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _downloader = new Downloader(new HttpClientTransport(), version);
        FFmpegTools? tools = null;
        try { tools = FFmpegTools.Locate(); } catch (ConversionException) { }
        var runner = new DropJobRunner(_downloader, new FFmpegMediaConverter(tools));
        Queue = new DropQueue((link, folder, options, resume, stage, token) => runner.RunAsync(link, folder, options, resume, stage, token));
        _history = new DropHistory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Potion Dropper", "history.json"));

        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/PotionDropper.ico");
        var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32((int)(DefaultWidth * scale), (int)(DefaultHeight * scale)));

        Queue.PropertyChanged += (_, _) => UpdateQueueHeader();
        Queue.Items.CollectionChanged += (_, _) => UpdateQueueHeader();
        Queue.ItemFinished += Record;
        SavesTo.Text = $"Saves to {Path.GetFileName(OutputFolder.TrimEnd(Path.DirectorySeparatorChar))}";
        UpdateQueueHeader();
    }

    /// <summary>Downloads go here (Settings arrive in M7.2).</summary>
    internal string OutputFolder =>
        Settings.GetString("outputFolder") is { } path && Directory.Exists(path) ? path : KnownFolders.Downloads;

    // First-run acknowledgment

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        LinkBox.Focus(FocusState.Programmatic);
        if (Settings.GetInt(Acknowledgment.SettingsKey, 0) >= Acknowledgment.Version) return;
        if (await Acknowledgment.ShowAsync(Root.XamlRoot)) Settings.Set(Acknowledgment.SettingsKey, Acknowledgment.Version);
        else Application.Current.Exit();
    }

    // Link bar

    private void LinkBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FetchButton.IsEnabled = !string.IsNullOrWhiteSpace(LinkBox.Text);
        PasteButton.Visibility = Visibility.Collapsed;
    }

    private void LinkBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(LinkBox.Text)) return;
        e.Handled = true;
        Fetch();
    }

    /// <summary>On focus, if the clipboard holds a link, offer "Paste link" inside the field (06-components.md).</summary>
    private async void LinkBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (LinkBox.Text.Length > 0) return;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            PasteButton.Visibility = LinkBox.Text.Length == 0 && _registry.FindLinks(text).Links.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            LinkBox.Text = (await content.GetTextAsync()).Trim();
            Fetch();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
    }

    private void Fetch_Click(object sender, RoutedEventArgs e) => Fetch();

    private async void Fetch()
    {
        var list = _registry.FindLinks(LinkBox.Text);
        _fetch?.Cancel();
        ShowError(null);
        _link = null; _preview = null; _source = null; _batch = [];
        if (list.Links.Count == 0)
        {
            var (_, error) = _registry.Parse(LinkBox.Text);
            ShowCard(CardState.None);
            ShowError(_registry.Message(error ?? list.Problems.FirstOrDefault()?.Error ?? LinkError.NotALink));
            return;
        }
        if (list.Links.Count > 1)
        {
            _batch = list.Links;
            ShowCard(CardState.Batch);
            return;
        }

        var link = list.Links[0];
        var cts = _fetch = new CancellationTokenSource();
        ShowCard(CardState.Loading);
        FetchButton.IsEnabled = false;
        try
        {
            var preview = await _downloader.InspectAsync(link, MaxBytes, cts.Token);
            if (cts.IsCancellationRequested) return;
            _link = link;
            _preview = preview;
            _source = FormatRegistry.Shared.FormatById(preview.FormatId);
            _mode = _source?.Category == FormatCategory.Audio ? DropMode.Audio : DropMode.Video;
            ShowCard(CardState.Media);
        }
        catch (DownloadFailureException failure)
        {
            if (cts.IsCancellationRequested) return;
            ShowCard(CardState.None);
            ShowError(_registry.Message(failure.Error));
        }
        finally
        {
            FetchButton.IsEnabled = !string.IsNullOrWhiteSpace(LinkBox.Text);
        }
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorPanel.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        LinkWell.BorderBrush = (Brush)Ui.Resource(message is null ? "PotionBorderStrongBrush" : "PotionErrorBrush");
    }

    // Card and options

    private enum CardState { None, Loading, Media, Batch }

    private void ShowCard(CardState state)
    {
        Skeleton.Visibility = state == CardState.Loading ? Visibility.Visible : Visibility.Collapsed;
        var showCard = state is CardState.Media or CardState.Batch;
        Card.Visibility = Options.Visibility = DownloadRow.Visibility = showCard ? Visibility.Visible : Visibility.Collapsed;
        if (!showCard) return;

        if (state == CardState.Media && _preview is not null && _source is not null)
        {
            var category = _source.Category;
            CardTile.Background = Ui.TintBrush(category);
            CardGlyph.Glyph = Ui.Glyph(category);
            CardGlyph.Foreground = Ui.TextBrush(category);
            CardTitle.Text = _preview.FileName;
            ToolTipService.SetToolTip(CardTitle, _preview.FileName);
            var size = _preview.Size is { } bytes ? ByteSize.Format(bytes) : "Size unknown";
            CardMeta.Text = $"{_source.Name} · {size} · Direct link from {_preview.FinalLink.Host}";
            DownloadButton.Content = "Download";
            AudioMode.IsEnabled = true;
            VideoMode.IsEnabled = DropChoices.Modes(_source).Contains(DropMode.Video);
        }
        else
        {
            CardTile.Background = (Brush)Ui.Resource("PotionSurfaceRaisedBrush");
            CardGlyph.Glyph = "";   // Link
            CardGlyph.Foreground = (Brush)Ui.Resource("PotionTextTertiaryBrush");
            CardTitle.Text = $"{_batch.Count} links";
            CardMeta.Text = "Each link is checked when its download starts.";
            DownloadButton.Content = "Download all";
            VideoMode.IsEnabled = AudioMode.IsEnabled = true;
        }
        SetMode(_mode);
    }

    private void Mode_Click(object sender, RoutedEventArgs e) => SetMode(sender == AudioMode ? DropMode.Audio : DropMode.Video);

    /// <summary>The two toggle buttons act as one segmented control; the format list follows the mode.</summary>
    private void SetMode(DropMode mode)
    {
        _mode = mode;
        VideoMode.IsChecked = mode == DropMode.Video;
        AudioMode.IsChecked = mode == DropMode.Audio;

        FormatBox.Items.Clear();
        if (_source is not null)
        {
            foreach (var format in DropChoices.Formats(_source, mode))
                FormatBox.Items.Add(new ComboBoxItem { Content = format.Id == _source.Id ? $"{format.Name} (as downloaded)" : format.Name, Tag = format.Id });
            var chosen = DropChoices.DefaultFormat(_source, mode)?.Id;
            FormatBox.SelectedItem = FormatBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == chosen);
        }
        else
        {
            FormatBox.Items.Add(new ComboBoxItem { Content = "As downloaded", Tag = "" });
            var category = mode == DropMode.Video ? FormatCategory.Video : FormatCategory.Audio;
            foreach (var format in FormatRegistry.Shared.Formats.Where(f => f.Category == category && f.IsAvailableOn(ConversionMatrix.CurrentPlatform)))
                FormatBox.Items.Add(new ComboBoxItem { Content = format.Name, Tag = format.Id });
            FormatBox.SelectedIndex = mode == DropMode.Audio ? FormatBox.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == "mp3") : 0;
        }

        QualityBox.Header = mode == DropMode.Video ? "Quality" : "Bitrate";
        QualityBox.Items.Clear();
        if (mode == DropMode.Video)
            foreach (var preset in VideoPresets.Shared.Presets) QualityBox.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset.Id });
        else
            foreach (var preset in AudioPresets.Shared.Presets) QualityBox.Items.Add(new ComboBoxItem { Content = $"{preset.Name} ({preset.BitrateKbps} kbps)", Tag = preset.Id });
        var defaultPreset = mode == DropMode.Video ? "original" : AudioPresets.Shared.DefaultPresetId;
        QualityBox.SelectedItem = QualityBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == defaultPreset);
        UpdateQualityEnabled();
    }

    private void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateQualityEnabled();

    private string? SelectedFormat => FormatBox.SelectedItem is ComboBoxItem { Tag: string id } && id.Length > 0 ? id : null;

    /// <summary>Saving the downloaded format as is has no quality choice.</summary>
    private void UpdateQualityEnabled() =>
        QualityBox.IsEnabled = !(_source is not null && SelectedFormat == _source.Id) && !(_source is null && SelectedFormat is null);

    // Download

    private long MaxBytes => _registry.Download.DefaultMaxBytes;

    private DropOptions OptionsFor(string? format)
    {
        var preset = QualityBox.SelectedItem is ComboBoxItem { Tag: string id } ? id : null;
        var settings = _mode == DropMode.Video
            ? new ConversionSettings(Video: new VideoOptions(PresetId: preset))
            : new ConversionSettings(Audio: new AudioOptions(PresetId: preset));
        return new DropOptions(format, settings, MaxBytes: MaxBytes);
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        var folder = OutputFolder;
        if (_link is not null && _preview is not null && _source is not null)
        {
            var format = SelectedFormat;
            Queue.Add(_link, folder, OptionsFor(format == _source.Id ? null : format), _preview.FileName, _source.Id);
        }
        else if (_batch.Count > 0)
        {
            var options = OptionsFor(SelectedFormat);
            foreach (var link in _batch) Queue.Add(link, folder, options);
        }
        else return;
        LinkBox.Text = "";
        _link = null; _preview = null; _source = null; _batch = [];
        ShowCard(CardState.None);
        LinkBox.Focus(FocusState.Programmatic);
    }

    // Queue

    private void UpdateQueueHeader()
    {
        QueueHeader.Visibility = Queue.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var (finished, total, fraction) = Queue.Overall;
        var busy = Queue.IsBusy;
        Summary.Text = busy && total > 0 ? $"{finished} of {total} · {Math.Round(fraction * 100)}%" : "";
        OverallProgress.Visibility = StopAllButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OverallProgress.Value = fraction;
        ClearButton.IsEnabled = Queue.Items.Any(i => i.IsDone);
    }

    private void StopAll_Click(object sender, RoutedEventArgs e) => Queue.StopAll();
    private void ClearFinished_Click(object sender, RoutedEventArgs e) => Queue.ClearFinished();

    private void RowStop_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is DropQueueItem item) Queue.Stop(item); }
    private void RowRetry_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is DropQueueItem item) Queue.Retry(item); }
    private void RowShow_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is DropQueueItem item) ShowInFolder(item); }

    private void QueueList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not DropQueueItem item) return;
        var menu = new MenuFlyout();
        void Add(string text, Action action)
        {
            var entry = new MenuFlyoutItem { Text = text };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
        if (item.CanStop) Add("Stop", () => Queue.Stop(item));
        if (item.CanRetry) Add("Retry", () => Queue.Retry(item));
        if (item.IsDone) Add("Show in folder", () => ShowInFolder(item));
        Add("Copy link", () =>
        {
            var package = new DataPackage();
            package.SetText(item.Link.Text);
            Clipboard.SetContent(package);
        });
        if (!item.IsRunning)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Remove", () => Queue.Remove(item));
        }
        menu.ShowAt(QueueList, e.GetPosition(QueueList));
        e.Handled = true;
    }

    private static void ShowInFolder(DropQueueItem item)
    {
        if (item.Files.FirstOrDefault(File.Exists) is not { } path) return;
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add($"/select,{path}");
        Process.Start(info);
    }

    /// <summary>History keeps what the user can see and clear: finished and failed downloads (spec Part 6.1.4).</summary>
    private void Record(DropQueueItem item)
    {
        try
        {
            if (item.IsDone)
                _history.Add(DropHistory.Entry.Create(item.Link.Text, item.Link.Host, Path.GetFileName(item.Files.FirstOrDefault() ?? item.Title),
                    item.Files, item.FormatId));
            else if (item.IsFailed)
                _history.Add(DropHistory.Entry.Create(item.Link.Text, item.Link.Host, item.Title, error: item.Error));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* history is a convenience */ }
    }
}

/// <summary>The first-run acknowledgment (spec Part 6.3.5, word for word), shown before anything can be fetched.</summary>
internal static class Acknowledgment
{
    public const int Version = 1;
    public const string SettingsKey = "acknowledgedSourcePolicyVersion";

    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        var check = new CheckBox { Content = "I understand", Margin = new Thickness(0, 12, 0, 0) };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Before you download",
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "Only download media you own, media licensed for download (such as Creative Commons or public domain), or media the website's terms allow you to download. Many sites prohibit downloading. You're responsible for what you download and how you use it.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    check,
                },
            },
            PrimaryButtonText = "Continue",
            CloseButtonText = "Quit",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        check.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        check.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}

/// <summary>The user's Downloads folder (SHGetKnownFolderPath via the environment's user profile).</summary>
internal static class KnownFolders
{
    public static string Downloads
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(path) ? path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }
}

/// <summary>x:Bind helpers mapping formats and statuses to design-system brushes and glyphs.</summary>
public static class Ui
{
    public static object Resource(string key) => Application.Current.Resources[key];

    private static PotionCategory Design(FormatCategory c) => c switch
    {
        FormatCategory.Image => PotionCategory.Image, FormatCategory.Audio => PotionCategory.Audio,
        FormatCategory.Video => PotionCategory.Video, _ => PotionCategory.Docs,
    };

    /// <summary>The row's category: the known format's, otherwise the chosen output's, otherwise video.</summary>
    public static FormatCategory CategoryOf(string? formatId, DropOptions options)
    {
        var known = formatId is null ? null : FormatRegistry.Shared.FormatById(formatId);
        var output = options.OutputFormatId is null ? null : FormatRegistry.Shared.FormatById(options.OutputFormatId);
        return (known ?? output)?.Category == FormatCategory.Audio ? FormatCategory.Audio : FormatCategory.Video;
    }

    public static Brush TintBrush(FormatCategory c) => (Brush)Resource(Design(c).ResourcePrefix() + "TintBrush");
    public static Brush TextBrush(FormatCategory c) => (Brush)Resource(Design(c).ResourcePrefix() + "TextBrush");
    public static Brush FillBrush(FormatCategory c) => (Brush)Resource(Design(c).ResourcePrefix() + "FillBrush");
    public static string Glyph(FormatCategory c) => Design(c).Glyph();

    public static Brush TintBrush(string? formatId, DropOptions options) => TintBrush(CategoryOf(formatId, options));
    public static Brush TextBrush(string? formatId, DropOptions options) => TextBrush(CategoryOf(formatId, options));
    public static Brush FillBrush(string? formatId, DropOptions options) => FillBrush(CategoryOf(formatId, options));
    public static string Glyph(string? formatId, DropOptions options) => Glyph(CategoryOf(formatId, options));

    public static Brush StatusBrush(bool failed) => (Brush)Resource(failed ? "PotionErrorBrush" : "PotionTextSecondaryBrush");
    public static double Value(double? fraction) => fraction ?? 0;
    public static bool Indeterminate(double? fraction) => fraction is null;
}
