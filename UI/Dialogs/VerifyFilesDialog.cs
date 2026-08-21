using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.UI.Dialogs;

// VerifyFilesDialog — "is every file still the one we installed?"
//
// A tester asked for this in as many words: the existing check confirms the
// files are THERE and the right length, but not that their contents are
// intact. A half-finished update leaves files that pass every fast check and
// crash the game, and until now there was no way to tell that from a bug.
//
// It reads every byte, so it is a button rather than something that happens
// on every launch — a two-second wait before Play is reasonable, a
// fifteen-second one is not.
public sealed class VerifyFilesDialog : Window
{
    private readonly string _gameDir;
    private readonly string _displayName;

    private readonly TextBlock _status;
    private readonly ProgressBar _bar;
    private readonly StackPanel _findings;
    private readonly Button _action;
    private readonly Button _close;

    private CancellationTokenSource? _cts;
    private bool _running;

    public static void ShowFor(Window? owner, string displayName, string gameDirectory)
        => new VerifyFilesDialog(owner, displayName, gameDirectory).ShowDialog();

    private VerifyFilesDialog(Window? owner, string displayName, string gameDirectory)
    {
        _gameDir = gameDirectory;
        _displayName = displayName;

        Title  = $"Verify files — {displayName}";
        Width  = 640;
        Height = 520;
        Owner  = owner;
        WindowStartupLocation = owner == null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        Background = Brush("BrushBackground", "#0D1018");

        var root = new Grid { Margin = new Thickness(22, 18, 22, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel();
        head.Children.Add(new TextBlock
        {
            Text = "Verify game files",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("BrushAccent", "#CCA800"),
        });
        head.Children.Add(new TextBlock
        {
            Text = $"Reads every file {displayName} installed and compares it, byte for "
                 + "byte, against what was written at install time. This finds damage a "
                 + "quick check cannot: a file that is present, the right size, and "
                 + "wrong inside.",
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
            Foreground = Brush("BrushMuted", "#727A99"),
        });
        _bar = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 100,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8),
        };
        head.Children.Add(_bar);
        _status = new TextBlock
        {
            Text = "It takes a few seconds to a minute, depending on the game's size. "
                 + "You can stop it at any point.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = Brush("BrushMuted", "#727A99"),
        };
        head.Children.Add(_status);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        _findings = new StackPanel();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _findings,
            Margin = new Thickness(0, 0, 0, 12),
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _close = new Button { Content = "Close", Padding = new Thickness(16, 7, 16, 7),
                              Margin = new Thickness(0, 0, 8, 0) };
        _action = new Button { Content = "Start checking", Padding = new Thickness(16, 7, 16, 7),
                               IsDefault = true };
        if (Application.Current?.TryFindResource("BtnSecondaryStyle") is Style sec)
            _close.Style = sec;
        if (Application.Current?.TryFindResource("BtnPlayStyle") is Style pri)
            _action.Style = pri;
        _close.Click  += (_, _) => { _cts?.Cancel(); Close(); };
        _action.Click += async (_, _) => await ToggleAsync();
        buttons.Children.Add(_close);
        buttons.Children.Add(_action);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;

        // Cancel a run the player walks away from rather than leaving it
        // reading a gigabyte into a closed window.
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task ToggleAsync()
    {
        if (_running) { _cts?.Cancel(); return; }

        _running = true;
        _action.Content = "Stop";
        _bar.Visibility = Visibility.Visible;
        _bar.Value = 0;
        _findings.Children.Clear();
        _status.Foreground = Brush("BrushMuted", "#727A99");
        _status.Text = "Reading files…";

        _cts = new CancellationTokenSource();
        var progress = new Progress<(int Pct, string Msg)>(p =>
        {
            _bar.Value = p.Pct;
            _status.Text = p.Msg;
        });

        InstallVerifier.Result result;
        try
        {
            result = await InstallVerifier.VerifyAsync(_gameDir, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            _running = false;
            _action.Content = "Start checking";
            _bar.Visibility = Visibility.Collapsed;
            _status.Text = "Stopped. Nothing was changed.";
            return;
        }
        catch (Exception ex)
        {
            _running = false;
            _action.Content = "Start checking";
            _bar.Visibility = Visibility.Collapsed;
            Fail("The check could not finish: " + ex.Message);
            return;
        }

        _running = false;
        _action.Content = "Check again";
        _bar.Visibility = Visibility.Collapsed;
        Render(result);
    }

    private void Render(InstallVerifier.Result r)
    {
        if (r.CouldNotRun != null) { Fail(r.CouldNotRun); return; }

        if (r.Healthy)
        {
            _status.Foreground = Brush("BrushSuccess", "#22C55E");
            _status.Text = r.Summary()
                         + (r.ManifestVersion is { Length: > 0 } v
                            ? $"  (installed version {v})" : "");
            _findings.Children.Add(Note(
                "Nothing here is damaged. If the game still misbehaves, it is not "
                + "the installed files — collect the logs and send those instead."));
            return;
        }

        _status.Foreground = Brush("BrushError", "#D94A4A");
        _status.Text = r.Summary();

        _findings.Children.Add(Note(
            "Re-installing the game replaces these. Your saves and settings live "
            + "outside the files listed here and are not affected."));

        // Grouped by what is wrong: the three faults have three different
        // causes, and a flat list of two hundred paths hides that.
        foreach (var group in r.Bad.GroupBy(b => b.Fault)
                                   .OrderBy(g => (int)g.Key))
        {
            _findings.Children.Add(new TextBlock
            {
                Text = $"{group.Count()} {InstallVerifier.Result.Describe(group.Key)}"
                       .ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 12, 0, 6),
                Foreground = Brush("BrushAccent", "#CCA800"),
            });

            // A cap, said out loud. Two hundred paths in a window helps nobody,
            // and pretending there were only twenty helps less.
            foreach (var b in group.Take(25))
                _findings.Children.Add(new TextBlock
                {
                    Text = $"{b.Path}  —  {b.Detail}",
                    FontSize = 11,
                    FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 3),
                    Foreground = Brush("BrushText", "#CCD0E0"),
                });

            int rest = group.Count() - 25;
            if (rest > 0)
                _findings.Children.Add(new TextBlock
                {
                    Text = $"…and {rest} more of the same kind. The full list is in the "
                         + "launcher log.",
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0),
                    Foreground = Brush("BrushMuted", "#727A99"),
                });
        }
    }

    private void Fail(string message)
    {
        _status.Foreground = Brush("BrushError", "#D94A4A");
        _status.Text = message;
    }

    /// Everything this dialog found, as lines for the launcher log — the copy
    /// that survives the window being closed and ends up in a bug report.
    public static string[] LogLines(string game, InstallVerifier.Result r)
        => new[] { $"[Verify] {game}: {r.Summary()}" }
           .Concat(r.Bad.Select(b =>
               $"[Verify]   {InstallVerifier.Result.Describe(b.Fault)}: {b.Path} ({b.Detail})"))
           .ToArray();

    private UIElement Note(string text) => new Border
    {
        Background      = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x20)),
        BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        BorderThickness = new Thickness(1),
        CornerRadius    = new CornerRadius(5),
        Padding         = new Thickness(14, 11, 14, 12),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("BrushMuted", "#727A99"),
        },
    };

    private static Brush Brush(string key, string fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
        }
        catch (Exception) { }
        return (Brush)new BrushConverter().ConvertFrom(fallback)!;
    }
}
