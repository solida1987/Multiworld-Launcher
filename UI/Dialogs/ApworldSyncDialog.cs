using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;
using LauncherV2.Core.Archipelago;

namespace LauncherV2.UI.Dialogs;

// ApworldSyncDialog — put the newest copy of EVERY world into the generator.
//
// WHY THIS EXISTS
//
// Hosting a seed for other people fails in a particular, maddening way: the
// generator has an older copy of somebody's world than the player who wrote
// the YAML, and the error names an option rather than the version behind it.
// The per-game update button on a store card fixes one game, and only a game
// you have installed — which is no use to a host who installs nothing and
// generates for eight people.
//
// So: one button, the whole catalogue, installed or not.
//
// ⚠ It asks first, and it says the number out loud. This downloads hundreds of
// files from hundreds of strangers' release pages; that is not something to
// start because somebody brushed a button in the title bar.
public sealed class ApworldSyncDialog : Window
{
    private readonly TextBlock _head;
    private readonly TextBlock _status;
    private readonly ProgressBar _bar;
    private readonly StackPanel _findings;
    private readonly ScrollViewer _scroll;
    private readonly Button _action;
    private readonly Button _close;

    private CancellationTokenSource? _cts;

    /// Ids to work through, filled once the catalogue answers. Only ever set
    /// from a successful fetch, so the run can never act on a stale list.
    private List<string> _ids = new();

    private enum Step { Asking, Running, Done, Unavailable }
    private Step _step = Step.Asking;

    public static void ShowFor(Window? owner)
        => new ApworldSyncDialog(owner).ShowDialog();

    private ApworldSyncDialog(Window? owner)
    {
        Title  = "Update AP worlds";
        Width  = 660;
        // Short while it is only a question: the list underneath is empty
        // until the run starts, and a half-empty window reads as something
        // that failed to load. RunAsync grows it when there is something to
        // put there.
        Height = 300;
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
            Text = "Update every AP world",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("BrushAccent", "#CCA800"),
        });
        _head = new TextBlock
        {
            Text = "Reading the catalogue…",
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
            Foreground = Brush("BrushMuted", "#727A99"),
        };
        head.Children.Add(_head);
        _bar = new ProgressBar
        {
            Height = 6, Minimum = 0, Maximum = 100,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8),
        };
        head.Children.Add(_bar);
        _status = new TextBlock
        {
            Text = "",
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
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _findings,
            Margin = new Thickness(0, 0, 0, 12),
        };
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _close  = new Button { Content = "No, cancel", Padding = new Thickness(16, 7, 16, 7),
                               Margin = new Thickness(0, 0, 8, 0) };
        _action = new Button { Content = "…", Padding = new Thickness(16, 7, 16, 7),
                               IsDefault = true, IsEnabled = false };
        if (Application.Current?.TryFindResource("BtnSecondaryStyle") is Style sec)
            _close.Style = sec;
        if (Application.Current?.TryFindResource("BtnPlayStyle") is Style pri)
            _action.Style = pri;
        _close.Click  += (_, _) => { _cts?.Cancel(); Close(); };
        _action.Click += async (_, _) => await OnActionAsync();
        buttons.Children.Add(_close);
        buttons.Children.Add(_action);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
        Closed += (_, _) => _cts?.Cancel();
        Loaded += async (_, _) => await AskAsync();
    }

    // ------------------------------------------------------------- the ask

    /// Count first, then ask. The question has to carry the real number:
    /// "update everything" and "download 656 files from 400 strangers" are
    /// the same action, and only one of them is honest about it.
    private async Task AskAsync()
    {
        string? worlds = ApworldUpdater.WorldsDir();
        if (worlds == null)
        {
            // Nowhere to put the worlds. The old wording sent the player off to
            // "point London at your install" in a panel where nothing could be
            // pointed at — so ask here, where the problem actually is, and
            // carry on the moment they answer.
            var located = ApEngineFolderDialog.Ask(this,
                "This button fills your Archipelago install's custom_worlds folder, "
                + "so London needs to know where that install is.");
            if (located is { Usable: true })
            {
                worlds = ApworldUpdater.WorldsDir();
            }
            if (worlds == null)
            {
                _step = Step.Unavailable;
                _head.Text = "London has no Archipelago generator to put worlds into yet.";
                _status.Text = "Nothing was changed. You can point London at your "
                             + "Archipelago folder at any time under Settings.";
                _action.Content = "Close";
                _action.IsEnabled = true;
                _close.Visibility = Visibility.Collapsed;
                return;
            }
        }

        ApworldIndex? index;
        try { index = await ApworldCatalog.FetchAsync(); }
        catch (Exception) { index = null; }

        if (index?.Games is not { Count: > 0 } games)
        {
            _step = Step.Unavailable;
            _head.Text = "The catalogue could not be read.";
            _status.Text = "London could not reach the list of worlds. Check your "
                         + "connection and try again.";
            _action.Content = "Close";
            _action.IsEnabled = true;
            _close.Visibility = Visibility.Collapsed;
            return;
        }

        _ids = games.Keys.ToList();
        _head.Text =
            $"Are you sure you want to download all of these worlds? There are "
          + $"{_ids.Count} of them, and every one will be brought to its newest "
          + "published version.";
        _status.Text =
            $"They go into {worlds}. Worlds already at the newest version are "
          + "left alone, so running this again is cheap. Games you have not "
          + "installed are included on purpose — a host generates for other "
          + "people's games.";
        _action.Content = $"Yes, update all {_ids.Count}";
        _action.IsEnabled = true;
    }

    // ------------------------------------------------------------- the run

    private async Task OnActionAsync()
    {
        switch (_step)
        {
            case Step.Unavailable:
            case Step.Done:
                Close();
                return;

            case Step.Running:
                _cts?.Cancel();
                return;

            case Step.Asking:
                await RunAsync();
                return;
        }
    }

    private async Task RunAsync()
    {
        _step = Step.Running;
        _cts  = new CancellationTokenSource();
        var ct = _cts.Token;

        // Room for the list of anything that would not come down.
        Height = 560;
        _action.Content = "Stop";
        _close.IsEnabled = false;
        _bar.Visibility = Visibility.Visible;
        _bar.Minimum = 0;
        _bar.Maximum = _ids.Count;
        _bar.Value = 0;
        _findings.Children.Clear();

        int updated = 0, current = 0, alreadyNewest = 0;
        var problems = new List<string>();

        foreach (string id in _ids)
        {
            if (ct.IsCancellationRequested) break;
            current++;

            ApworldStatus status;
            try { status = await ApworldUpdater.CheckAsync(id, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                problems.Add($"{id}: {e.Message}");
                _bar.Value = current;
                continue;
            }

            string label = status.Entry?.Game is { Length: > 0 } g ? g : id;
            _status.Text = $"{current} of {_ids.Count} — {label}";
            _bar.Value = current;

            if (!status.Actionable)
            {
                alreadyNewest++;
                continue;
            }

            string? err;
            try { err = await ApworldUpdater.UpdateAsync(id, null, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { err = e.Message; }

            if (err == null) updated++;
            else
            {
                problems.Add($"{label}: {err}");
                AddLine(label, err);
            }

            // Let the window paint between files. Without this the bar only
            // moves when the loop finishes, which is the opposite of the
            // point of having one.
            await Task.Yield();
        }

        _step = Step.Done;
        _bar.Visibility = Visibility.Collapsed;
        _close.IsEnabled = true;
        _close.Visibility = Visibility.Collapsed;
        _action.Content = "Close";

        bool stopped = ct.IsCancellationRequested;
        _head.Text = stopped ? "Stopped." : "Done.";
        _status.Text =
            $"{updated} updated · {alreadyNewest} already newest"
          + (problems.Count > 0 ? $" · {problems.Count} could not be fetched" : "")
          + (stopped ? $" · stopped after {current} of {_ids.Count}" : "");

        if (problems.Count == 0 && !stopped)
            AddLine("Every world in the catalogue is now at its newest version.", "");
    }

    private void AddLine(string what, string detail)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        p.Children.Add(new TextBlock
        {
            Text = what,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("BrushText", "#E6E9F5"),
        });
        if (detail.Length > 0)
            p.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11.5,
                LineHeight = 17,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("BrushMuted", "#727A99"),
            });
        _findings.Children.Add(p);
        _scroll.ScrollToEnd();
    }

    // ⚠ Fully qualified: System.Drawing is also in scope here, and both
    // assemblies export a ColorConverter.
    private static Brush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key) as Brush
           ?? new SolidColorBrush((Color)System.Windows.Media.ColorConverter
                                             .ConvertFromString(fallback));
}
