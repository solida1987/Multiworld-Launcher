using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core.Archipelago;

namespace LauncherV2.UI.Dialogs;

// ApEngineUpdateDialog — "Archipelago 0.6.8 is out; install it?" and the wait
// while it goes in.
//
// Every update button runs through OfferAsync before its own work: the worlds
// are only as current as the engine that loads them. The offer is one plain
// question with the version, the folder and what will happen in it; a no is
// kept for the rest of this run so the same question is not asked by the next
// button pressed a minute later, and is forgotten on restart so a new release
// — or a change of mind — gets asked again.
public sealed class ApEngineUpdateDialog : Window
{
    private static string? _declinedThisRun;

    private readonly TextBlock _status;
    private readonly ProgressBar _bar;
    private ApEngineUpdater.Result? _result;

    /// Look, and if there is a newer Archipelago, ask and install.
    /// Returns true only when the engine on disk actually changed version.
    ///
    /// Quiet about a missing engine: the buttons that call this have their own,
    /// better-placed way of asking where Archipelago is.
    public static async Task<bool> OfferAsync(Window? owner, Action<string> log)
    {
        ApEngineUpdater.Check check;
        try { check = await ApEngineUpdater.CheckAsync(); }
        catch (Exception) { return false; }

        if (!check.HasEngine) return false;
        var engine = check.Engine!;

        if (check.Offer == null || check.Latest == null)
        {
            log("[AP engine] Archipelago's release page could not be read — the "
              + "engine was not checked this time.");
            return false;
        }
        if (!check.Newer)
        {
            log($"[AP engine] Archipelago {engine.Version} is the newest release.");
            return false;
        }
        if (string.Equals(_declinedThisRun, check.Offer.Version, StringComparison.OrdinalIgnoreCase))
        {
            log($"[AP engine] Archipelago {check.Offer.Version} is out — you chose not to "
              + "install it this time.");
            return false;
        }

        string root = engine.Root;
        if (!check.CanInstallInPlace)
        {
            log($"[AP engine] Archipelago {check.Offer.Version} is out, but the copy at "
              + $"{root} was not made by Archipelago's installer, so London cannot "
              + "update it in place.");
            MessageBox.Show(owner,
                $"Archipelago {check.Offer.Version} is out and you have {engine.Version}.\n\n"
              + $"The copy at {root} was not put there by Archipelago's installer, so "
              + "London cannot bring it forward for you. Install the new version by "
              + $"hand from\n{ApEngineSource.ProjectPage}\n\nand point London at it "
              + "under Settings if it goes somewhere new.",
                "Archipelago update", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var busy = ApEngineUpdater.Busy();
        if (busy.Count > 0)
        {
            log($"[AP engine] Archipelago {check.Offer.Version} is out, but "
              + $"{string.Join(", ", busy)} is running — close it and press again.");
            MessageBox.Show(owner,
                $"Archipelago {check.Offer.Version} is out and you have {engine.Version}.\n\n"
              + $"{string.Join(", ", busy)} is running right now, and the installer "
              + "cannot replace files that are in use. Close it and press the button "
              + "again to install the update.",
                "Archipelago update", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        double mb = check.Offer.Size / 1_000_000.0;
        var ask = MessageBox.Show(owner,
            $"Archipelago {check.Offer.Version} is out — you have {engine.Version}.\n\n"
          + $"Install it now into {root}?\n\n"
          + $"London downloads the official installer ({check.Offer.AssetName}, {mb:F0} MB) "
          + "from the project's own release page, checks it against the hash the "
          + "release publishes, and runs it silently into the same folder. Your "
          + "custom_worlds and Players folders stay where they are. Windows may ask "
          + "for permission to run it.\n\n"
          + $"Written by {ApEngineSource.Author}, licensed {ApEngineSource.Licence}.",
            "Archipelago update", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes)
        {
            _declinedThisRun = check.Offer.Version;
            log($"[AP engine] Archipelago {check.Offer.Version} not installed — you said no. "
              + "Nothing was changed.");
            return false;
        }

        log($"[AP engine] Installing Archipelago {check.Offer.Version} into {root}…");
        var dlg = new ApEngineUpdateDialog(owner, check);
        dlg.ShowDialog();
        var r = dlg._result ?? new ApEngineUpdater.Result(false, "The update did not run.", engine.Version);

        log("[AP engine] " + r.Message);
        MessageBox.Show(owner, r.Message, "Archipelago update", MessageBoxButton.OK,
                        r.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        return r.Ok;
    }

    private ApEngineUpdateDialog(Window? owner, ApEngineUpdater.Check check)
    {
        Title = "Archipelago update";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        Owner = owner;
        WindowStartupLocation = owner == null
            ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("BrushBackground", "#0D1018");

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(new TextBlock
        {
            Text = $"Installing Archipelago {check.Offer!.Version}",
            FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = Brush("BrushAccent", "#CCA800"),
        });
        _status = new TextBlock
        {
            Text = "Starting…",
            FontSize = 12.5, LineHeight = 19, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 10),
            Foreground = Brush("BrushMuted", "#727A99"),
        };
        root.Children.Add(_status);
        _bar = new ProgressBar { Height = 6, IsIndeterminate = true };
        root.Children.Add(_bar);
        root.Children.Add(new TextBlock
        {
            Text = "This window closes on its own when the installer has finished.",
            FontSize = 11, Margin = new Thickness(0, 10, 0, 0),
            Foreground = Brush("BrushMuted", "#727A99"),
        });
        Content = root;

        // Not closable by hand while the installer runs: closing the window
        // would not stop the installer, only hide what it is doing.
        Closing += (_, e) => { if (_result == null) e.Cancel = true; };
        Loaded += async (_, _) =>
        {
            try
            {
                _result = await ApEngineUpdater.InstallAsync(check,
                    new Progress<string>(m => _status.Text = m), CancellationToken.None);
            }
            catch (Exception e)
            {
                _result = new ApEngineUpdater.Result(false,
                    "The update stopped: " + e.Message, check.Engine?.Version);
            }
            Close();
        };
    }

    private static System.Windows.Media.Brush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
           ?? new SolidColorBrush((Color)System.Windows.Media.ColorConverter
                                             .ConvertFromString(fallback));
}
