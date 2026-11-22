using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Potion.Core.App;
using Potion.Core.Documents;
using Potion.Core.Engines;
using Potion.Core.Formats;

namespace Potion;

/// <summary>
/// Convert dialog (05-layout.md, "Convert sheet"; on Windows a ContentDialog with Primary "Convert" and Close "Cancel"):
/// the format, the matrix's limitation notes, Advanced options from presets.json, and the Recycle Bin choice.
/// </summary>
internal sealed class ConvertDialog
{
    private const string KeepEach = "";   // "Each file's current format"
    private readonly QueueModel _queue;
    private readonly List<QueueItem> _items;
    private readonly List<FileFormat> _outputs;
    private readonly int? _pageCount;
    private string _outputId;
    private ConversionSettings _settings;
    private string _pagesText;

    private readonly ContentDialog _dialog;
    private readonly StackPanel _notes = new() { Spacing = 4 };
    private readonly Expander _advanced = new() { Header = "Advanced", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };

    private ConvertDialog(XamlRoot root, QueueModel queue, List<QueueItem> items)
    {
        _queue = queue;
        _items = items;
        var first = items[0];
        _outputs = queue.CommonOutputs(items).ToList();
        var mixed = items.Count > 1 && (items.Select(i => i.OutputId).Distinct().Count() > 1 || _outputs.Count == 0);
        _outputId = mixed ? KeepEach : _outputs.Any(o => o.Id == first.OutputId) ? first.OutputId : _outputs.FirstOrDefault()?.Id ?? first.OutputId;
        _settings = first.Settings;
        _pagesText = first.Settings.Pdf?.Pages is { } pages ? PageSelection.Describe(pages) : "";
        if (items.Count == 1 && first.Format.Id == "pdf")
        {
            try { _pageCount = PdfTools.Inspect(first.Path); } catch (ConversionException) { }
        }
        _summary.Style = (Style)Ui.Resource("CaptionTextBlockStyle");
        _summary.Foreground = (Brush)Ui.Resource("PotionTextSecondaryBrush");
        _advanced.IsExpanded = queue.Settings.GetBool("convertDialog.showAdvanced", false);
        _advanced.Expanding += (_, _) => queue.Settings.Set("convertDialog.showAdvanced", true);
        _advanced.Collapsed += (_, _) => queue.Settings.Set("convertDialog.showAdvanced", false);

        var content = new StackPanel { Spacing = 12, Width = 460 };
        content.Children.Add(Header());
        content.Children.Add(FormatRow());
        content.Children.Add(_notes);
        content.Children.Add(_advanced);
        var recycle = new CheckBox { Content = items.Count == 1 ? "Move original to Recycle Bin" : "Move originals to Recycle Bin", IsChecked = queue.MoveOriginalsToRecycleBin };
        recycle.Click += (_, _) => queue.MoveOriginalsToRecycleBin = recycle.IsChecked == true;
        content.Children.Add(recycle);

        _dialog = new ContentDialog
        {
            XamlRoot = root,
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Convert",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        Rebuild();
    }

    /// <summary>Shows the dialog; on Convert, applies the choices and converts these rows.</summary>
    public static async Task ShowAsync(XamlRoot root, QueueModel queue, List<QueueItem> items)
    {
        if (items.Count == 0) return;
        var dialog = new ConvertDialog(root, queue, items);
        if (await dialog._dialog.ShowAsync() != ContentDialogResult.Primary) return;
        dialog.Apply();
    }

    private void Apply()
    {
        if (_outputId != KeepEach)
        {
            var pages = _pageCount is { } count && OptionsKindNow().UsesPages && _pagesText.Trim().Length > 0
                ? PageSelection.Pages(_pagesText, count) : null;
            var settings = _settings with { Pdf = (_settings.Pdf ?? new PdfOptions()) with { Pages = pages } };
            _queue.Apply(_items, _outputId, settings);
        }
        _queue.Convert(_items);
    }

    private FileFormat? Output => _outputs.FirstOrDefault(o => o.Id == _outputId);
    private OptionsKind OptionsKindNow()
    {
        if (Output is not { } output) return new OptionsKind.None();
        var kind = OptionsKind.For(_items, output);
        // Page ranges only make sense for one PDF.
        return kind is OptionsKind.PdfPages && _pageCount is null ? new OptionsKind.None() : kind;
    }

    // Layout

    private UIElement Header()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var first = _items[0];
        var tile = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(11), Background = Ui.TintBrush(first.Category),
            Child = new FontIcon { Glyph = Ui.Glyph(first.Category), FontSize = 22, Foreground = Ui.TextBrush(first.Category) },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(tile, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        row.Children.Add(tile);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = _items.Count == 1 ? first.Name : $"{_items.Count} files",
            Style = (Style)Ui.Resource("BodyStrongTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 400,
        });
        if (_items.Count == 1)
            text.Children.Add(new TextBlock
            {
                Text = first.Size is { } size ? $"{FormatGroups.Label(first.Category)} · {ByteSize.Format(size)}" : FormatGroups.Label(first.Category),
                Style = (Style)Ui.Resource("CaptionTextBlockStyle"), Foreground = (Brush)Ui.Resource("PotionTextSecondaryBrush"),
            });
        row.Children.Add(text);
        return row;
    }

    private UIElement FormatRow()
    {
        var combo = new ComboBox { MinWidth = 200 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(combo, "Convert to");
        if (_items.Count > 1) combo.Items.Add(new ComboBoxItem { Content = "Each file's current format", Tag = KeepEach });
        foreach (var (category, formats) in FormatGroups.Of(_outputs))
        {
            combo.Items.Add(new ComboBoxItem { Content = FormatGroups.Label(category), IsEnabled = false, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            foreach (var format in formats) combo.Items.Add(new ComboBoxItem { Content = format.Name, Tag = format.Id });
        }
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == _outputId);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string id })
            {
                _outputId = id;
                // A codec chosen for one container may not exist in another.
                if (_settings.Video?.Codec is { } codec && !(VideoPresets.Shared.Formats.TryGetValue(id, out var c) && c.VideoCodecs.Contains(codec)))
                    _settings = _settings with { Video = _settings.Video with { Codec = null } };
                Rebuild();
            }
        };
        return Labeled("Convert to", combo);
    }

    private void Rebuild()
    {
        _notes.Children.Clear();
        if (Output is { } output)
            foreach (var note in OptionsText.Notes(_items, output))
                _notes.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Style = (Style)Ui.Resource("CaptionTextBlockStyle"), Foreground = (Brush)Ui.Resource("PotionTextSecondaryBrush") });
        var kind = OptionsKindNow();
        _advanced.Visibility = kind is OptionsKind.None ? Visibility.Collapsed : Visibility.Visible;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_summary);
        foreach (var control in Controls(kind)) panel.Children.Add(control);
        _advanced.Content = panel;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (Output is { } output) _summary.Text = OptionsText.Summary(OptionsKindNow(), output, _settings, _pagesText);
        _dialog.IsPrimaryButtonEnabled = _outputId == KeepEach || Output is not null && !PagesInvalid;
    }

    private bool PagesInvalid => _pageCount is { } count && OptionsKindNow().UsesPages && OptionsText.PagesHint(_pagesText, count).IsError;

    private static UIElement Labeled(string label, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private ComboBox Combo<T>(string name, IEnumerable<(string Label, T Value)> options, T selected, Action<T> changed)
    {
        var combo = new ComboBox { MinWidth = 200 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(combo, name);
        foreach (var (label, value) in options) combo.Items.Add(new ComboBoxItem { Content = label, Tag = new Box<T>(value) });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(((Box<T>)i.Tag).Value, selected)) ?? combo.Items.FirstOrDefault();
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: Box<T> box }) { changed(box.Value); UpdateSummary(); }
        };
        return combo;
    }

    private sealed record Box<T>(T Value);

    // Advanced controls per kind (06-components.md table), all from presets.json

    private IEnumerable<UIElement> Controls(OptionsKind kind)
    {
        var video = _settings.Video ?? new VideoOptions();
        var audio = _settings.Audio ?? new AudioOptions();
        var image = _settings.Image ?? new ImageOptions();
        var pdf = _settings.Pdf ?? new PdfOptions();
        var vp = VideoPresets.Shared;
        switch (kind)
        {
            case OptionsKind.Video v:
            {
                var bitrate = new NumberBox { Minimum = vp.MinBitrateKbps, Maximum = vp.MaxBitrateKbps, Value = video.BitrateKbps ?? 5000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Header = null };
                var bitrateRow = Labeled("Bitrate (kbps)", bitrate);
                bitrateRow.Visibility = video.BitrateKbps is null ? Visibility.Collapsed : Visibility.Visible;
                bitrate.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) { _settings = _settings with { Video = Video() with { BitrateKbps = (int)e.NewValue } }; UpdateSummary(); } };
                yield return Labeled("Quality", Combo("Quality",
                    vp.Presets.Select(p => (p.Name, p.Id)).Append(("Custom bitrate", "custom")),
                    video.BitrateKbps is null ? video.PresetId ?? vp.DefaultPresetId : "custom",
                    id =>
                    {
                        _settings = _settings with { Video = id == "custom" ? Video() with { BitrateKbps = (int)bitrate.Value } : Video() with { PresetId = id, BitrateKbps = null } };
                        bitrateRow.Visibility = id == "custom" ? Visibility.Visible : Visibility.Collapsed;
                    }));
                yield return bitrateRow;
                yield return Labeled("Resolution", Combo("Resolution",
                    new[] { ("Original", (int?)null) }.Concat(vp.Resolutions.Select(h => (OptionsText.Resolution(h), (int?)h))),
                    video.MaxHeight, h => _settings = _settings with { Video = Video() with { MaxHeight = h } }));
                yield return Labeled("Frame rate", Combo("Frame rate",
                    new[] { ("Original", (double?)null) }.Concat(vp.FrameRates.Select(f => (OptionsText.FrameRate(f), (double?)f))),
                    video.FrameRate, f => _settings = _settings with { Video = Video() with { FrameRate = f } }));
                if (vp.Formats.TryGetValue(v.Container, out var container) && container.VideoCodecs.Count > 1)
                    yield return Labeled("Codec", Combo("Codec",
                        container.VideoCodecs.Select((id, i) => (vp.Codecs[id].Name, i == 0 ? (string?)null : id)),
                        video.Codec, c => _settings = _settings with { Video = Video() with { Codec = c } }));
                yield return Labeled("Aspect ratio", Combo("Aspect ratio",
                    new[] { ("Original", (string?)null) }.Concat(vp.AspectRatios.Select(r => (r, (string?)r))),
                    video.AspectRatio, r => _settings = _settings with { Video = Video() with { AspectRatio = r } }));
                break;
            }
            case OptionsKind.Gif:
                yield return Labeled("Quality", Combo("Quality",
                    vp.Presets.Where(p => vp.Gif.ContainsKey(p.Id)).Select(p => ($"{p.Name} ({OptionsText.FrameRate(vp.Gif[p.Id].Fps)}, up to {vp.Gif[p.Id].MaxWidth} px wide)", p.Id)),
                    video.PresetId ?? vp.DefaultPresetId, id => _settings = _settings with { Video = Video() with { PresetId = id } }));
                break;
            case OptionsKind.Audio a:
            {
                var ap = AudioPresets.Shared;
                var format = ap.Formats.TryGetValue(a.Format, out var f) ? f : null;
                if (!a.Lossless)
                {
                    var bitrates = format?.Bitrates ?? [];
                    var bitrateCombo = Combo("Bitrate", bitrates.Select(b => ($"{b} kbps", b)), audio.BitrateKbps ?? bitrates.FirstOrDefault(),
                        b => _settings = _settings with { Audio = Audio() with { BitrateKbps = b } });
                    var bitrateRow = Labeled("Bitrate", bitrateCombo);
                    bitrateRow.Visibility = audio.BitrateKbps is null ? Visibility.Collapsed : Visibility.Visible;
                    var options = ap.Presets.Select(p => ($"{p.Name} ({p.BitrateKbps} kbps)", p.Id)).ToList();
                    if (bitrates.Count > 0) options.Add(("Custom bitrate", "custom"));
                    yield return Labeled("Quality", Combo("Quality", options, audio.BitrateKbps is null ? audio.PresetId ?? ap.DefaultPresetId : "custom", id =>
                    {
                        _settings = _settings with
                        {
                            Audio = id == "custom" ? Audio() with { BitrateKbps = AudioPresets.Nearest(ap.PresetById(Audio().PresetId).BitrateKbps, bitrates) }
                                                   : Audio() with { PresetId = id, BitrateKbps = null },
                        };
                        bitrateRow.Visibility = id == "custom" ? Visibility.Visible : Visibility.Collapsed;
                    }));
                    yield return bitrateRow;
                }
                yield return Labeled("Sample rate", Combo("Sample rate",
                    new[] { ("Original", (int?)null) }.Concat((format?.SampleRates ?? ap.SampleRates).Select(r => (OptionsText.SampleRate(r), (int?)r))),
                    audio.SampleRate, r => _settings = _settings with { Audio = Audio() with { SampleRate = r } }));
                break;
            }
            case OptionsKind.Image i:
                foreach (var control in SizeControls(image)) yield return control;
                if (i.Lossy)
                {
                    var slider = new Slider { Minimum = 10, Maximum = 100, Value = (image.Quality ?? ImagePresets.Shared.QualityFor(image)) * 100, StepFrequency = 1 };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, "Quality");
                    slider.ValueChanged += (_, e) => { _settings = _settings with { Image = Image() with { Quality = Math.Round(e.NewValue) / 100 } }; UpdateSummary(); };
                    yield return Labeled("Quality", slider);
                }
                yield return RotateControls();
                var flipH = new CheckBox { Content = "Horizontal", IsChecked = image.FlipHorizontal };
                var flipV = new CheckBox { Content = "Vertical", IsChecked = image.FlipVertical };
                flipH.Click += (_, _) => { _settings = _settings with { Image = Image() with { FlipHorizontal = flipH.IsChecked == true } }; UpdateSummary(); };
                flipV.Click += (_, _) => { _settings = _settings with { Image = Image() with { FlipVertical = flipV.IsChecked == true } }; UpdateSummary(); };
                var flips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                flips.Children.Add(flipH);
                flips.Children.Add(flipV);
                yield return Labeled("Flip", flips);
                break;
            case OptionsKind.ImagesToPdf:
                foreach (var control in SizeControls(image)) yield return control;
                break;
            case OptionsKind.PdfToImage p:
                if (_pageCount is not null) yield return PagesField();
                yield return Labeled("Resolution", Combo("Resolution", PdfPresets.Shared.DpiChoices.Select(d => ($"{d:0} dpi", d)),
                    pdf.Dpi ?? PdfPresets.Shared.DefaultDpi, d => _settings = _settings with { Pdf = Pdf() with { Dpi = d } }));
                if (p.Lossy)
                    yield return Labeled("Quality", Combo("Quality", ImagePresets.Shared.Presets.Select(x => (x.Name, x.Id)),
                        pdf.ImagePresetId ?? ImagePresets.Shared.DefaultPresetId, id => _settings = _settings with { Pdf = Pdf() with { ImagePresetId = id } }));
                break;
            case OptionsKind.PdfPages:
                yield return PagesField();
                break;
        }
    }

    private VideoOptions Video() => _settings.Video ?? new VideoOptions();
    private AudioOptions Audio() => _settings.Audio ?? new AudioOptions();
    private ImageOptions Image() => _settings.Image ?? new ImageOptions();
    private PdfOptions Pdf() => _settings.Pdf ?? new PdfOptions();

    private IEnumerable<UIElement> SizeControls(ImageOptions image)
    {
        var width = new NumberBox { Minimum = 1, Maximum = 100_000, PlaceholderText = "Width" };
        var height = new NumberBox { Minimum = 1, Maximum = 100_000, PlaceholderText = "Height" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(width, "Width in pixels");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(height, "Height in pixels");
        var boxes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        boxes.Children.Add(width);
        boxes.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center });
        boxes.Children.Add(height);
        var pixels = Labeled("Pixels", boxes);
        void Show(ImageResize resize)
        {
            (width.Value, height.Value, pixels.Visibility) = resize switch
            {
                ImageResize.Fit f => ((double)(f.Width ?? double.NaN), (double)(f.Height ?? double.NaN), Visibility.Visible),
                ImageResize.Exact e => ((double)e.Width, (double)e.Height, Visibility.Visible),
                _ => (double.NaN, double.NaN, Visibility.Collapsed),
            };
        }
        void Update()
        {
            int? w = double.IsNaN(width.Value) ? null : (int)width.Value, h = double.IsNaN(height.Value) ? null : (int)height.Value;
            ImageResize resize = Image().ResizeOrOriginal is ImageResize.Exact ? new ImageResize.Exact(w ?? 1920, h ?? 1080) : new ImageResize.Fit(w, h);
            _settings = _settings with { Image = Image() with { Resize = resize } };
            UpdateSummary();
        }
        width.ValueChanged += (_, _) => { if (pixels.Visibility == Visibility.Visible) Update(); };
        height.ValueChanged += (_, _) => { if (pixels.Visibility == Visibility.Visible) Update(); };
        var options = new (string, string)[] { ("Original", "original"), ("75%", "75"), ("50%", "50"), ("25%", "25"), ("Fit within…", "fit"), ("Exact size…", "exact") };
        var current = image.ResizeOrOriginal switch { ImageResize.Percent p => $"{p.Value:0}", ImageResize.Fit => "fit", ImageResize.Exact => "exact", _ => "original" };
        Show(image.ResizeOrOriginal);
        yield return Labeled("Size", Combo("Size", options, current, mode =>
        {
            ImageResize resize = mode switch
            {
                "original" => new ImageResize.Original(),
                "fit" => new ImageResize.Fit(1920, 1920),
                "exact" => new ImageResize.Exact(1920, 1080),
                _ => new ImageResize.Percent(double.Parse(mode, System.Globalization.CultureInfo.InvariantCulture)),
            };
            _settings = _settings with { Image = Image() with { Resize = resize } };
            Show(resize);
        }));
        yield return pixels;
    }

    private UIElement RotateControls()
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Ui.Resource("PotionTextSecondaryBrush") };
        void Refresh() => label.Text = Image().Rotation == 0 ? "Not rotated" : $"{Image().Rotation}°";
        Button Rotate(string glyph, string name, int degrees, bool mirrored)
        {
            var icon = new FontIcon { Glyph = glyph, FontSize = 14, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5) };
            if (mirrored) icon.RenderTransform = new ScaleTransform { ScaleX = -1 };
            var button = new Button { Content = icon };
            ToolTipService.SetToolTip(button, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => { _settings = _settings with { Image = Image() with { Rotation = (Image().Rotation + degrees) % 360 } }; Refresh(); UpdateSummary(); };
            return button;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Rotate("\uE7AD", "Rotate left", 270, mirrored: true));
        row.Children.Add(Rotate("\uE7AD", "Rotate right", 90, mirrored: false));
        row.Children.Add(label);
        Refresh();
        return Labeled("Rotate", row);
    }

    private UIElement PagesField()
    {
        var box = new TextBox { Text = _pagesText, PlaceholderText = "All pages", Width = 180 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Pages");
        var hint = new TextBlock { Style = (Style)Ui.Resource("CaptionTextBlockStyle") };
        void Refresh()
        {
            var (text, error) = OptionsText.PagesHint(_pagesText, _pageCount ?? 1);
            hint.Text = text;
            hint.Foreground = (Brush)Ui.Resource(error ? "PotionErrorBrush" : "PotionTextSecondaryBrush");
        }
        box.TextChanged += (_, _) => { _pagesText = box.Text; Refresh(); UpdateSummary(); };
        Refresh();
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(box);
        stack.Children.Add(hint);
        return Labeled("Pages", stack);
    }
}
