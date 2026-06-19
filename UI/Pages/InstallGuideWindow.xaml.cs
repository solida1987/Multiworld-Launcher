using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
// (WPF/WinForms disambiguation handled in GlobalUsings.cs)

namespace LauncherV2.UI.Pages;

/// <summary>
/// Floating window that renders a plain-text or light-Markdown install guide.
/// Supports:  ## Headings,  ### Sub-headings,  - Bullet lists,  **Bold** text,
///            blank lines as spacers, and https:// URLs (rendered as hyperlinks).
/// </summary>
public partial class InstallGuideWindow : Window
{
    public InstallGuideWindow(string title, string markdownText)
    {
        InitializeComponent();
        TxtTitle.Text = title;
        RenderGuide(markdownText);
    }

    // ── Lightweight markdown renderer ─────────────────────────────────────────

    private void RenderGuide(string text)
    {
        var textBrush  = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var mutedBrush = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var accentBrush= new SolidColorBrush(Color.FromRgb(0xF0, 0xC0, 0x40));

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.TrimEnd();

            if (line.StartsWith("## "))
            {
                // H2 heading
                ContentPanel.Children.Add(new TextBlock
                {
                    Text       = line[3..],
                    FontSize   = 16,
                    FontWeight = FontWeights.Bold,
                    Foreground = accentBrush,
                    Margin     = new Thickness(0, 18, 0, 6),
                });
                ContentPanel.Children.Add(new Border
                {
                    Height          = 1,
                    Background      = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
                    Margin          = new Thickness(0, 0, 0, 10),
                });
            }
            else if (line.StartsWith("### "))
            {
                // H3 sub-heading
                ContentPanel.Children.Add(new TextBlock
                {
                    Text       = line[4..],
                    FontSize   = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = textBrush,
                    Margin     = new Thickness(0, 12, 0, 4),
                });
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                // Bullet item — supports **bold** and URLs
                var tb = new TextBlock
                {
                    Foreground   = textBrush,
                    FontSize     = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(12, 2, 0, 2),
                };
                tb.Inlines.Add(new Run("• ") { Foreground = mutedBrush });
                AddInlines(tb, line[2..], textBrush, accentBrush);
                ContentPanel.Children.Add(tb);
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                ContentPanel.Children.Add(new Border { Height = 6 });
            }
            else
            {
                // Regular paragraph line
                var tb = new TextBlock
                {
                    Foreground   = textBrush,
                    FontSize     = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 2, 0, 2),
                };
                AddInlines(tb, line, textBrush, accentBrush);
                ContentPanel.Children.Add(tb);
            }
        }
    }

    /// Parses **bold** markers and https:// URLs inside a text string into Inlines.
    private static void AddInlines(TextBlock tb, string text, Brush textBr, Brush accentBr)
    {
        // Simple state machine: scan character by character
        int pos = 0;
        while (pos < text.Length)
        {
            // Check for **bold**
            if (pos + 1 < text.Length && text[pos] == '*' && text[pos + 1] == '*')
            {
                int end = text.IndexOf("**", pos + 2);
                if (end >= 0)
                {
                    string bold = text[(pos + 2)..end];
                    tb.Inlines.Add(new Bold(new Run(bold)) { Foreground = accentBr });
                    pos = end + 2;
                    continue;
                }
            }

            // Check for URL
            int urlStart = text.IndexOfAny(new[] { 'h' }, pos);
            bool foundUrl = false;
            if (urlStart == pos && text[pos..].StartsWith("https://", StringComparison.Ordinal))
            {
                int urlEnd = text.IndexOfAny(new[] { ' ', '\t' }, pos);
                string url = urlEnd < 0 ? text[pos..] : text[pos..urlEnd];

                // The guide text is remote catalog content — a malformed token
                // (e.g. a bare "https://" before a parenthesis) used to throw
                // UriFormatException out of the window constructor and crash
                // the app (P2-19). Bad links render as plain text instead;
                // only web schemes are ever opened (P2-12).
                if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) &&
                    (parsedUri.Scheme == Uri.UriSchemeHttp ||
                     parsedUri.Scheme == Uri.UriSchemeHttps))
                {
                    var link = new Hyperlink(new Run(url))
                    {
                        Foreground          = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                        TextDecorations     = TextDecorations.Underline,
                        NavigateUri         = parsedUri,
                    };
                    link.RequestNavigate += (s, e) =>
                    {
                        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                        catch { /* ignore */ }
                    };
                    tb.Inlines.Add(link);
                }
                else
                {
                    tb.Inlines.Add(new Run(url) { Foreground = textBr });
                }
                pos += url.Length;
                foundUrl = true;
            }

            if (!foundUrl)
            {
                // Find the next ** or https:// and emit plain text up to it
                int next = int.MaxValue;
                int boldIdx = text.IndexOf("**", pos);
                int urlIdx  = text.IndexOf("https://", pos);
                if (boldIdx >= 0) next = Math.Min(next, boldIdx);
                if (urlIdx  >= 0) next = Math.Min(next, urlIdx);

                string plain = next < int.MaxValue ? text[pos..next] : text[pos..];
                if (plain.Length > 0)
                    tb.Inlines.Add(new Run(plain) { Foreground = textBr });
                pos += plain.Length > 0 ? plain.Length : 1;
            }
        }
    }

    // ── Window chrome ─────────────────────────────────────────────────────────
    private void TitleBar_Drag(object sender, MouseButtonEventArgs e) => DragMove();
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
