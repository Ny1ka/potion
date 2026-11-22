using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Potion.Core.Dropper;
using Potion.Core.Formats;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace PotionDropper;

/// <summary>
/// The link bar (05-layout.md, "Potion Dropper: main window"). Fetch checks the links against the source allowlist;
/// media info, the output options and the download queue arrive with the M7 interface.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 720, DefaultHeight = 520;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/PotionDropper.ico");
        var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32((int)(DefaultWidth * scale), (int)(DefaultHeight * scale)));
    }

    private void LinkBox_TextChanged(object sender, TextChangedEventArgs e) =>
        FetchButton.IsEnabled = !string.IsNullOrWhiteSpace(LinkBox.Text);

    private void LinkBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(LinkBox.Text)) return;
        e.Handled = true;
        Fetch();
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) return;
        LinkBox.Text = await content.GetTextAsync();
        Fetch();
    }

    private void Fetch_Click(object sender, RoutedEventArgs e) => Fetch();

    private void Fetch()
    {
        var (ok, text) = LinkCheck.For(LinkBox.Text);
        CheckIcon.Glyph = ok ? "" : "";   // CheckMark / Error
        CheckIcon.Foreground = (Brush)Application.Current.Resources[ok ? "PotionTextSecondaryBrush" : "PotionErrorBrush"];
        CheckText.Text = text;
        CheckText.Foreground = (Brush)Application.Current.Resources[ok ? "PotionTextSecondaryBrush" : "PotionErrorBrush"];
        CheckPanel.Visibility = Visibility.Visible;
    }
}

/// <summary>What the link bar says about its text: the first usable link, or why there's none.</summary>
internal static class LinkCheck
{
    public static (bool Ok, string Text) For(string input)
    {
        var registry = SourceRegistry.Shared;
        var list = registry.FindLinks(input);
        if (list.Links.Count > 0)
        {
            var first = list.Links[0];
            var format = first.FormatId is { } id ? FormatRegistry.Shared.FormatById(id)?.Name : null;
            var what = format is null ? $"Link to {first.Host}" : $"{format} from {first.Host}";
            return (true, list.Links.Count == 1 ? $"{what}." : $"{what}, and {list.Links.Count - 1} more.");
        }
        var (_, error) = registry.Parse(input);
        return (false, registry.Message(error ?? list.Problems.FirstOrDefault()?.Error ?? LinkError.NotALink));
    }
}
