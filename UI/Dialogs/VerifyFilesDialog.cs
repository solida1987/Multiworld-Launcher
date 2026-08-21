using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.UI.Dialogs;

// VerifyFilesDialog — "is every file still the one we installed?", and then:
// "shall I fix it?"
//
// A tester asked for the check in as many words: the fast check confirms the
// files are THERE and the right length, but not that their contents are
// intact. A half-finished update leaves files that pass every quick check and
// crash the game.
//
// Finding the damage is only half a feature though. A window that lists nine
// broken paths and leaves the player holding them has moved the problem, not
// solved it — so when the files can be fetched again this offers to do it, and
// when they cannot it says what to do instead in words a person can act on.
//
// It reads every byte, so it is a button rather than something that happens on
// every launch.
public sealed class VerifyFilesDialog : Window
{
    private readonly IGamePlugin _plugin;
    private readonly string _gameDir;

    private readonly TextBlock _status;
    private readonly ProgressBar _bar;
    private readonly StackPanel _findings;
    private readonly Button _action;

    private CancellationTokenSource? _cts;

    /// What the button does next. The dialog is a small state machine and
    /// naming the states beats three booleans that can disagree.
    private enum Step { Ready, Checking, Repairable, Repairing, Done }
    private Step _step = Step.Ready;

    /// Files worth asking the plugin to fetch again. Only ever set from a
    /// finished scan, so a repair can never act on a stale list.
    private List<string> _repairable = new();

    public static void ShowFor(Window? owner, IGamePlugin plugin)
        => new VerifyFilesDialog(owner, plugin).ShowDialog();

    private VerifyFilesDialog(Window? owner, IGamePlugin plugin)
    {
        _plugin  = plugin;
        _gameDir = SafeDir(plugin);

        Title  = $"Verify files — {plugin.DisplayName}";
        Width  = 660;
        Height = 560;
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
            Text = $"Reads every file {plugin.DisplayName} installed and compares it, "
                 + "byte for byte, against what was written when it was installed. "
                 + "That finds damage a quick check cannot: a file that is present, "
                 + "the right size, and wrong inside.",
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
            Foreground = Brush("BrushMuted", "#727A99"),
        });
        _bar = new ProgressBar
        {
            Height = 6, Minimum = 0, Maximum = 100,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8),
        };
        head.Children.Add(_bar);
        _status = new TextBlock
        {
            Text = "Takes a few seconds. You can stop it at any point.",
            FontSize = 12.5,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
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
        var close = new Button { Content = "Close", Padding = new Thickness(16, 7, 16, 7),
                                 Margin = new Thickness(0, 0, 8, 0) };
        _action = new Button { Content = "Start checking", Padding = new Thickness(16, 7, 16, 7),
                               IsDefault = true };
        if (Application.Current?.TryFindResource("BtnSecondaryStyle") is Style sec)
            close.Style = sec;
        if (Application.Current?.TryFindResource("BtnPlayStyle") is Style pri)
            _action.Style = pri;
        close.Click   += (_, _) => { _cts?.Cancel(); Close(); };
        _action.Click += async (_, _) => await OnActionAsync();
        buttons.Children.Add(close);
        buttons.Children.Add(_action);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
        Closed += (_, _) => _cts?.Cancel();
    }

    // ----------------------------------------------------------------- flow

    private async Task OnActionAsync()
    {
        switch (_step)
        {
            case Step.Checking:
            case Step.Repairing:
                _cts?.Cancel();
                return;
            case Step.Repairable:
                await RepairAsync();
                return;
            default:
                await CheckAsync();
                return;
        }
    }

    private async Task CheckAsync()
    {
        _step = Step.Checking;
        _action.Content = "Stop";
        _bar.Visibility = Visibility.Visible;
        _bar.Value = 0;
        _findings.Children.Clear();
        Say("Reading files…", "BrushMuted", "#727A99");

        _cts = new CancellationTokenSource();
        var progress = new Progress<(int Pct, string Msg)>(p =>
        {
            _bar.Value = p.Pct;
            _status.Text = p.Msg;
        });

        InstallVerifier.Result result;
        try
        {
            result = await InstallVerifier.VerifyAsync(
                _gameDir, SafeVersion(_plugin), progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Reset("Stopped. Nothing was changed.");
            return;
        }
        catch (Exception ex)
        {
            Reset(null);
            Say("The check could not finish: " + ex.Message, "BrushError", "#D94A4A");
            return;
        }

        _bar.Visibility = Visibility.Collapsed;
        Render(result);
    }

    private async Task RepairAsync()
    {
        var wanted = _repairable.ToList();
        if (wanted.Count == 0) return;

        _step = Step.Repairing;
        _action.Content = "Stop";
        _bar.Visibility = Visibility.Visible;
        _bar.Value = 0;
        _findings.Children.Clear();
        Say($"Fetching {wanted.Count} file(s) again…", "BrushMuted", "#727A99");

        _cts = new CancellationTokenSource();
        var progress = new Progress<(int Pct, string Msg)>(p =>
        {
            _bar.Value = p.Pct;
            if (!string.IsNullOrWhiteSpace(p.Msg)) _status.Text = p.Msg;
        });

        IReadOnlyList<string> restored, unfixable;
        try
        {
            (restored, unfixable) =
                await _plugin.RepairFilesAsync(wanted, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Reset("Stopped. Some files may already have been replaced — run the "
                + "check again to see where it stands.");
            return;
        }
        catch (Exception ex)
        {
            Reset(null);
            Say("The files could not be fetched: " + ex.Message
              + "  Check your internet connection and try again.",
                "BrushError", "#D94A4A");
            return;
        }

        _bar.Visibility = Visibility.Collapsed;
        _findings.Children.Clear();

        if (unfixable.Count > 0)
        {
            // Nothing came back at all = this game cannot fetch single files.
            // Some came back = the rest genuinely are not in the download.
            Say(restored.Count == 0
                    ? $"{_plugin.DisplayName} cannot fetch its files one at a time."
                    : $"Replaced {restored.Count} file(s), but {unfixable.Count} "
                      + "could not be fetched — they are not in the download.",
                "BrushAccent", "#CCA800");
            _findings.Children.Add(Note(HowToFixByHand()));
            if (restored.Count > 0)
                foreach (string f in unfixable.Take(20)) _findings.Children.Add(Path(f));
            _step = Step.Done;
            _action.Content = "Check again";
            return;
        }

        // Confirm rather than claim. A repair that reports success without
        // re-reading the files is exactly how a broken install gets declared
        // healthy.
        Say($"Replaced {restored.Count} file(s). Checking them…", "BrushMuted", "#727A99");
        await CheckAsync();
    }

    // --------------------------------------------------------------- render

    private void Render(InstallVerifier.Result r)
    {
        _findings.Children.Clear();
        _repairable = new List<string>();

        if (r.CouldNotRun != null)
        {
            _step = Step.Done;
            _action.Content = "Check again";
            Say(r.CouldNotRun, "BrushError", "#D94A4A");
            return;
        }

        // A record written for a different build. Nothing measured against it
        // means anything -- and the fix is the same download, so it is offered
        // as one rather than described.
        if (r.StaleRecord != null)
        {
            Say(r.StaleRecord, "BrushAccent", "#CCA800");
            // No repair button here on purpose: a repair works from a list of
            // bad files, and this state means we could not decide which files
            // ARE bad. Reinstalling is what rewrites the note, so that is what
            // is asked for — plainly, with no path or version in sight.
            _findings.Children.Add(Note(
                "Nothing is damaged. The launcher simply has an out-of-date note "
                + "of what this game should look like, so it has nothing reliable "
                + "to compare against.\n\n"
                + $"To put it right: open {_plugin.DisplayName} in the library and "
                + "press Install. It fetches the game's files again and writes a "
                + "fresh note at the same time, after which this check works "
                + "properly. Your saves and characters are not part of that."));
            _step = Step.Done;
            _action.Content = "Check again";
            return;
        }

        if (r.Healthy)
        {
            _step = Step.Done;
            _action.Content = "Check again";
            Say(r.Summary(), "BrushSuccess", "#22C55E");
            _findings.Children.Add(Note(
                "Nothing here is damaged. If the game still misbehaves, the "
                + "installed files are not the reason — use Collect logs and send "
                + "that file instead."));
            if (r.Changed.Count > 0) ListGroup(r.Changed, InstallVerifier.Fault.ChangedSinceInstall);
            return;
        }

        Say(r.Summary(), "BrushError", "#D94A4A");

        // Missing, wrong size and damaged all have the same cure: fetch the
        // file again. Unreadable does not -- something is holding it open, and
        // downloading over it will fail the same way.
        _repairable = r.Damage
            .Where(b => b.Fault != InstallVerifier.Fault.Unreadable)
            .Select(b => b.Path)
            .Distinct()
            .ToList();

        var locked = r.Damage.Where(b => b.Fault == InstallVerifier.Fault.Unreadable).ToList();
        if (locked.Count > 0)
            _findings.Children.Add(Note(
                $"{locked.Count} file(s) could not be read at all. That usually means "
                + "the game is still running, or an antivirus has hold of them. Close "
                + "the game and try again."));

        foreach (var group in r.Damage.GroupBy(b => b.Fault).OrderBy(g => (int)g.Key))
            ListGroup(group.ToList(), group.Key);
        if (r.Changed.Count > 0)
            ListGroup(r.Changed, InstallVerifier.Fault.ChangedSinceInstall);

        OfferRepair(_repairable, $"Repair {_repairable.Count} file(s)");
    }

    /// Offer the fix if the game can fetch its own files; otherwise say what
    /// to do instead — in words, not paths.
    private void OfferRepair(List<string> files, string label)
    {
        _repairable = files;

        // No capability probe. The interface's default and a real
        // implementation answer identically for an empty request, and asking
        // with a real one costs a network round trip just to decide whether to
        // draw a button. So the offer is always made and the ANSWER is told
        // truthfully: a game that cannot fetch its files reports them all as
        // unrepairable, and that is when the by-hand route appears.
        _step = Step.Repairable;
        _action.Content = label;
        _findings.Children.Insert(0, Note(
            "The launcher can fetch these from the game's own download and put "
            + "them back. Your saves and settings are not touched — only the "
            + "files listed here. Press the button below to do it now."));
    }

    private string HowToFixByHand()
        => "This game cannot replace single files on its own. To put it right, "
         + $"open {_plugin.DisplayName} in the library and install it again — that "
         + "replaces the program files. Your saves and characters live elsewhere "
         + "and are not part of what gets replaced.";

    private void ListGroup(IReadOnlyList<InstallVerifier.BadFile> files,
                           InstallVerifier.Fault fault)
    {
        _findings.Children.Add(new TextBlock
        {
            Text = $"{files.Count} {InstallVerifier.Result.Describe(fault)}".ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 14, 0, 6),
            Foreground = Brush("BrushAccent", "#CCA800"),
        });
        foreach (var b in files.Take(25)) _findings.Children.Add(Path(b.Path));
        int rest = files.Count - 25;
        if (rest > 0)
            _findings.Children.Add(new TextBlock
            {
                Text = $"…and {rest} more of the same kind.",
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = Brush("BrushMuted", "#727A99"),
            });
    }

    // ---------------------------------------------------------------- parts

    private UIElement Path(string p) => new TextBlock
    {
        Text = p,
        FontSize = 11,
        FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 3),
        Foreground = Brush("BrushText", "#CCD0E0"),
    };

    private UIElement Note(string text) => new Border
    {
        Background      = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x20)),
        BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        BorderThickness = new Thickness(1),
        CornerRadius    = new CornerRadius(5),
        Padding         = new Thickness(14, 11, 14, 12),
        Margin          = new Thickness(0, 0, 0, 4),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("BrushMuted", "#727A99"),
        },
    };

    private void Say(string text, string key, string fallback)
    {
        _status.Foreground = Brush(key, fallback);
        _status.Text = text;
    }

    private void Reset(string? message)
    {
        _step = Step.Ready;
        _action.Content = "Start checking";
        _bar.Visibility = Visibility.Collapsed;
        if (message != null) Say(message, "BrushMuted", "#727A99");
    }

    private static string SafeDir(IGamePlugin p)
    {
        try { return p.GameDirectory ?? ""; } catch (Exception) { return ""; }
    }

    private static string? SafeVersion(IGamePlugin p)
    {
        try { return p.InstalledVersion; } catch (Exception) { return null; }
    }

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
