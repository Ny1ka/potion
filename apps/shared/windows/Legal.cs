using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace PotionShared;

// About window and legal documents, shared by Potion and Potion Dropper for Windows (spec Part 6.4). The documents are
// bundled from legal/ into a Legal folder next to the app (see each .csproj) and shown in the app, never fetched.

public enum LegalDocument { Terms, Privacy, Acknowledgements, License }

public static class Legal
{
    public static string Title(LegalDocument document) => document switch
    {
        LegalDocument.Terms => "Terms of use",
        LegalDocument.Privacy => "Privacy policy",
        LegalDocument.Acknowledgements => "Acknowledgements",
        _ => "License",
    };

    private static string FileName(LegalDocument document) => document switch
    {
        LegalDocument.Terms => "TERMS.md",
        LegalDocument.Privacy => "PRIVACY.md",
        LegalDocument.Acknowledgements => "THIRD_PARTY_NOTICES.md",
        _ => "LICENSE",
    };

    public static string Text(LegalDocument document)
    {
        try { return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Legal", FileName(document))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "This document is missing from the app. It's also at https://github.com/Ny1ka/potion/tree/main/legal";
        }
    }

    private static readonly Dictionary<LegalDocument, Window> Open = [];

    /// <summary>Shows a document in its own window (one per document).</summary>
    public static void Show(LegalDocument document)
    {
        if (Open.TryGetValue(document, out var existing)) { existing.Activate(); return; }
        var monospace = document is LegalDocument.Acknowledgements or LegalDocument.License;
        // A read-only TextBox handles the 1 MB of acknowledgements and keeps every license text exact and selectable.
        var box = new TextBox
        {
            Text = Text(document), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0), Padding = new Thickness(16), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        if (monospace) box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, Title(document));
        var window = new Window { Title = Title(document), Content = box, SystemBackdrop = new MicaBackdrop() };
        window.AppWindow.Resize(new SizeInt32(760, 640));
        window.Closed += (_, _) => Open.Remove(document);
        Open[document] = window;
        window.Activate();
    }

    /// <summary>"By continuing, you agree to the Terms of use and Privacy policy." with links that open them.</summary>
    public static FrameworkElement AgreementLine()
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 12, 0, 0) };
        line.Children.Add(new TextBlock { Text = "By continuing, you agree to the", VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(Link("Terms of use", () => Show(LegalDocument.Terms)));
        line.Children.Add(new TextBlock { Text = "and", VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(Link("Privacy policy", () => Show(LegalDocument.Privacy)));
        return line;
    }

    private static HyperlinkButton Link(string text, Action action)
    {
        var link = new HyperlinkButton { Content = text, Padding = new Thickness(0) };
        link.Click += (_, _) => action();
        return link;
    }

    public static void OpenUri(string uri)
    {
        // explorer.exe with the URL as an argument opens the default browser or mail app (no shell).
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add(uri);
        Process.Start(info);
    }
}

/// <summary>
/// About window (06-components.md, "About window"): mark at 96, product name in the brand face at 28, version, copyright,
/// privacy line, the legal documents, FFmpeg/LGPL attribution, source code and contact (spec Part 6.4).
/// </summary>
public sealed class AboutWindow : Window
{
    private static AboutWindow? _open;

    public static void ShowFor(string productName, string privacyLine, string? note, string iconPath)
    {
        if (_open is not null) { _open.Activate(); return; }
        _open = new AboutWindow(productName, privacyLine, note, iconPath);
        _open.Closed += (_, _) => _open = null;
        _open.Activate();
    }

    private AboutWindow(string productName, string privacyLine, string? note, string iconPath)
    {
        Title = $"About {productName}";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(iconPath);
        AppWindow.Resize(new SizeInt32(520, 640));

        var assembly = Assembly.GetEntryAssembly();
        var version = assembly?.GetName().Version?.ToString(3) ?? "?";
        var copyright = assembly?.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
        var secondary = (Brush)Application.Current.Resources["PotionTextSecondaryBrush"];

        var page = new StackPanel { Spacing = 12, Padding = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460 };
        page.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.targetsize-256.png")), Width = 96, Height = 96,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        page.Children.Add(new TextBlock
        {
            Text = productName, FontSize = 28, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = (FontFamily)Application.Current.Resources["PotionBrandBoldFontFamily"],
        });
        page.Children.Add(Centered($"Version {version}", secondary, "CaptionTextBlockStyle", selectable: true));
        page.Children.Add(Centered(copyright, secondary, "CaptionTextBlockStyle"));
        page.Children.Add(Centered(privacyLine, null, "BodyTextBlockStyle"));
        if (note is not null) page.Children.Add(Centered(note, secondary, "CaptionTextBlockStyle"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var document in new[] { LegalDocument.Terms, LegalDocument.Privacy, LegalDocument.Acknowledgements })
        {
            var button = new Button { Content = Legal.Title(document) };
            button.Click += (_, _) => Legal.Show(document);
            buttons.Children.Add(button);
        }
        page.Children.Add(buttons);

        page.Children.Add(Centered("This app uses FFmpeg, licensed under the LGPL 2.1 or later, run as separate programs.", secondary, "CaptionTextBlockStyle"));
        page.Children.Add(LinkButton("Get the FFmpeg source and build scripts", "https://github.com/Ny1ka/potion/tree/main/scripts/ffmpeg"));
        page.Children.Add(LinkButton("Source code (MIT License)", "https://github.com/Ny1ka/potion"));
        page.Children.Add(LinkButton("nyikabusiness@gmail.com", "mailto:nyikabusiness@gmail.com"));

        Content = new ScrollViewer { Content = page };
    }

    private static TextBlock Centered(string text, Brush? brush, string style, bool selectable = false)
    {
        var block = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)Application.Current.Resources[style], IsTextSelectionEnabled = selectable,
        };
        if (brush is not null) block.Foreground = brush;
        return block;
    }

    private static HyperlinkButton LinkButton(string text, string uri)
    {
        var link = new HyperlinkButton { Content = text, HorizontalAlignment = HorizontalAlignment.Center };
        link.Click += (_, _) => Legal.OpenUri(uri);
        return link;
    }
}
