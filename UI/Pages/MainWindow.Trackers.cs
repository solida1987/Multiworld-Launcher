using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LauncherV2.Core;
using LauncherV2.Core.Archipelago;
using LauncherV2.Core.Trackers;
using LauncherV2.UI.Dialogs;

namespace LauncherV2.UI.Pages;

/// The game's own map tracker, opened from the page you launch the game from.
///
/// 147 of the catalogue's 794 games have a PopTracker pack somebody built for
/// them. London installs PopTracker ONCE — it is one 7 MB program, not one per
/// game — then the pack for the game in front of you, then opens it on the
/// right pack with the session already attached.
///
/// The other 647 get Universal Tracker instead: one apworld that gives the
/// Archipelago client a tracking tab for any world at all.
public partial class MainWindow
{
    private TrackerEntry? _overviewTracker;

    /// The engine folder Universal Tracker would go into, when this game has no
    /// pack of its own. Null when it has one, or when no engine is set up.
    private string? _universalTarget;

    private static string? TryCustomWorldsDir()
    {
        try
        {
            var s = SettingsStore.Load();
            var eng = ApEngine.Discover(
                string.IsNullOrWhiteSpace(s.ApEnginePath) ? null : s.ApEnginePath);
            return eng is { Exists: true } ? eng.CustomWorldsDir : null;
        }
        catch (Exception) { return null; }
    }

    /// Show or hide the button for the game now on screen.
    ///
    /// Runs on its own: the catalogue is fetched over the network, and the page
    /// must draw immediately whether or not the answer has arrived. The button
    /// appears a moment later if there is something to offer.
    private async Task RefreshTrackerButtonAsync(IGamePlugin plugin)
    {
        _overviewTracker = null;
        _universalTarget = null;
        BtnOverviewTracker.Visibility = Visibility.Collapsed;

        TrackerEntry? entry;
        try { entry = await TrackerCatalog.ForGameAsync(plugin.GameId); }
        catch (Exception) { return; }

        // The player may have moved on while we were asking.
        if (_selectedPlugin?.GameId != plugin.GameId) return;

        // A no is an answer, not a delay. The buttons go away until the player
        // changes their mind in Settings, rather than asking again every time
        // they press one.
        if (!TrackerConsent.MayOffer) return;

        if (entry == null)
        {
            // No pack for this one — 647 of the 794 are in that boat. Offer
            // Universal Tracker instead, which covers every world at once.
            //
            // ⚠ Only while it is missing. Once the apworld is in the engine
            // there is nothing left for London to do: the tracking happens
            // inside the Archipelago client, and a button that does nothing
            // would be a lie about where the feature lives.
            string? cw = TryCustomWorldsDir();
            if (cw != null && !UniversalTrackerService.IsInstalledIn(cw))
            {
                _universalTarget = cw;
                BtnOverviewTracker.Content = "🗺  Add Universal Tracker";
                BtnOverviewTracker.ToolTip =
                    "Nobody has built a map tracker for this game. Universal Tracker "
                  + "adds a tracking tab to the Archipelago client for any game — "
                  + "London puts it in your engine.";
                BtnOverviewTracker.Visibility = Visibility.Visible;
            }
            return;
        }

        _overviewTracker = entry;
        bool ready = PopTrackerService.IsInstalled
                  && PopTrackerService.IsPackInstalled(entry.PackageUid);

        // The label is the promise. "Open" when it will open; "Get" when a
        // download has to happen first, so nobody is surprised by a progress
        // bar they did not ask for.
        BtnOverviewTracker.Content = ready ? "🗺  Open tracker" : "🗺  Get the tracker";
        BtnOverviewTracker.ToolTip = ready
            ? $"Open {entry.PackName} in PopTracker"
            : $"Download {entry.PackName} by {entry.PackRepo.Split('/')[0]}"
              + (PopTrackerService.IsInstalled ? "" : ", and PopTracker itself")
              + " — then open it";
        BtnOverviewTracker.Visibility = Visibility.Visible;
    }

    private async void BtnOverviewTracker_Click(object sender, RoutedEventArgs e)
    {
        // No pack: the button is the Universal Tracker install.
        if (_overviewTracker == null && _universalTarget is { } target)
        {
            if (!TrackerConsentDialog.AskForUniversal(this))
            {
                AppendLog("[Tracker] No trackers downloaded — you said no. "
                        + "Settings has the switch if you change your mind.");
                if (_selectedPlugin != null) _ = RefreshTrackerButtonAsync(_selectedPlugin);
                return;
            }
            BtnOverviewTracker.IsEnabled = false;
            var p = new Progress<string>(m =>
            {
                AppendLog("[Tracker] " + m);
                BtnOverviewTracker.Content = m;
            });
            string? err = await UniversalTrackerService.InstallAsync(target, p);
            AppendLog("[Tracker] " + (err ?? "Universal Tracker is in your engine — the "
                    + "Archipelago client will show a tracker tab."));
            BtnOverviewTracker.IsEnabled = true;
            if (_selectedPlugin != null) _ = RefreshTrackerButtonAsync(_selectedPlugin);
            return;
        }

        if (_overviewTracker is not { } entry || _selectedPlugin is not { } plugin) return;

        // ⚠ Asked BEFORE anything is fetched, and only the first time. The
        // answer covers every game with a pack from then on.
        if (!TrackerConsentDialog.Ask(this, entry))
        {
            AppendLog("[Tracker] No trackers downloaded — you said no. "
                    + "Settings has the switch if you change your mind.");
            _ = RefreshTrackerButtonAsync(plugin);
            return;
        }

        // If this game is connected right now, hand the tracker the session so
        // it opens live rather than empty. That is the whole point of the
        // button sitting where the game is launched from.
        var live = ApJoinSession.All.FirstOrDefault(s =>
            string.Equals(s.Plugin.GameId, plugin.GameId, StringComparison.OrdinalIgnoreCase));

        await OpenTrackerAsync(entry, BtnOverviewTracker,
                               live?.ServerAddress, live?.SlotName, null);
    }

    // ------------------------------------------------------- the switch

    /// Draw the rail switch from the saved answer.
    ///
    /// ⚠ Three states, not two. "Not asked yet" is shown as ON, because that
    /// is what it behaves like: the button is offered and the question comes
    /// when it is pressed. Showing it off would say the feature is disabled
    /// when it is merely unanswered.
    private bool _trackerSwitchLoading;

    internal void RefreshTrackerSwitch()
    {
        _trackerSwitchLoading = true;
        bool? answer = TrackerConsent.Answer;
        ChkTrackers.IsChecked = answer != false;
        TxtTrackerState.Text = answer switch
        {
            true  => "You said yes — every game with a tracker offers one.",
            false => "You said no — the tracker buttons are hidden.",
            _     => "You have not been asked yet. The question comes the first "
                   + "time you press a tracker button.",
        };
        _trackerSwitchLoading = false;
    }

    private void ChkTrackers_Changed(object sender, RoutedEventArgs e)
    {
        if (_trackerSwitchLoading) return;
        // Turning it ON from the rail is not the same as saying yes to a
        // download: it puts the question back to "not asked", so the next
        // press asks properly and names what is about to be fetched.
        if (ChkTrackers.IsChecked == true)
        {
            var s = SettingsStore.Load();
            s.TrackerConsent = null;
            SettingsStore.Save(s);
        }
        else TrackerConsent.Set(false);

        RefreshTrackerSwitch();
        if (_selectedPlugin != null) _ = RefreshTrackerButtonAsync(_selectedPlugin);
    }

    /// Install what is missing, then open.
    ///
    /// The sequence itself lives in PopTrackerService.OpenAsync, because the
    /// tracker is offered from two places and two copies would drift. This owns
    /// only the label and the log line.
    internal async Task OpenTrackerAsync(TrackerEntry entry, Button? button,
                                         string? host, string? slot, string? password)
    {
        if (button != null) button.IsEnabled = false;

        void Say(string msg)
        {
            AppendLog($"[Tracker] {msg}");
            if (button != null) button.Content = msg.Length > 34 ? msg[..34] + "…" : msg;
        }

        try
        {
            Say(await PopTrackerService.OpenAsync(entry, new Progress<string>(Say),
                                                  host, slot, password));
        }
        finally
        {
            if (button != null)
            {
                button.IsEnabled = true;
                // Re-read the state: the pack may now be installed, which
                // changes the label from "Get" to "Open".
                if (_selectedPlugin != null) _ = RefreshTrackerButtonAsync(_selectedPlugin);
            }
        }
    }
}
