using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;
using LauncherV2.Core.Archipelago;
using LauncherV2.UI.Controls;

namespace LauncherV2.UI.Pages;

/// Keeping the Archipelago engine's worlds current.
///
/// Updating the game and updating its world are TWO things, and London used to
/// do only the first. A player whose game updated kept generating with last
/// month's world — which is the half that decides what a seed contains.
///
/// ⚠ Still two decisions, deliberately. A world built for a newer release can
/// refuse to generate the seed a group is already playing, so this lights a
/// button and waits rather than fetching behind the player's back.
public partial class MainWindow
{
    /// Show or hide the button for the game now on screen.
    ///
    /// Runs on its own: the catalogue comes over the network and the page must
    /// draw immediately. The button appears a moment later if there is
    /// something to do — and never otherwise, because a lit button that
    /// downloads nothing is worse than no button.
    private async Task RefreshApworldButtonAsync(IGamePlugin plugin)
    {
        BtnOverviewApworld.Visibility = Visibility.Collapsed;

        ApworldStatus status;
        try { status = await ApworldUpdater.CheckAsync(plugin.GameId); }
        catch (Exception) { return; }

        // The player may have moved on while we were asking.
        if (_selectedPlugin?.GameId != plugin.GameId) return;

        if (!status.Actionable) return;

        bool missing = status.State == ApworldState.Missing;
        BtnOverviewApworld.Content = missing
            ? "⬇  Get the AP world"
            : "↑  AP world update";
        BtnOverviewApworld.ToolTip = missing
            ? $"Put {status.Entry!.Asset} into your Archipelago engine "
            + $"— published by {status.Entry.Source}"
            : $"{status.Detail} — from {status.Entry!.Source}";
        BtnOverviewApworld.Visibility = Visibility.Visible;
    }

    private async void BtnOverviewApworld_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlugin is not { } plugin) return;

        BtnOverviewApworld.IsEnabled = false;
        object? was = BtnOverviewApworld.Content;
        try
        {
            var progress = new Progress<string>(m =>
            {
                AppendLog($"[{plugin.DisplayName}] {m}");
                BtnOverviewApworld.Content = m.Length > 28 ? m[..28] + "…" : m;
            });
            string? err = await ApworldUpdater.UpdateAsync(plugin.GameId, progress);
            AppendLog($"[{plugin.DisplayName}] " + (err ?? "The world in your "
                    + "Archipelago engine is now the published one."));
            if (err != null)
                ToastService.Show("AP world not updated", err, ToastKind.Warning);
        }
        finally
        {
            BtnOverviewApworld.Content = was;
            BtnOverviewApworld.IsEnabled = true;
            if (_selectedPlugin != null) _ = RefreshApworldButtonAsync(_selectedPlugin);
        }
    }

    /// The games London itself has installed. The sweep adds to this whatever
    /// the engine already carries — see ApworldUpdater.CandidatesAsync.
    private static List<string> InstalledGameIds()
        => LibraryStore.GetSortedGameIds()
            .Select(id => GameRegistry.All.FirstOrDefault(p => p.GameId == id))
            .Where(p => p is { IsInstalled: true })
            .Select(p => p!.GameId)
            .ToList();

    // ------------------------------------------------------------ the rail

    /// What the rail says about the engine's worlds.
    ///
    /// A sweep button with no number beside it is a button nobody presses:
    /// there is no reason to, because there is no reason to think anything is
    /// behind. The count is the reason.
    internal async Task RefreshApworldRailAsync()
    {
        if (TxtApworldState == null) return;

        string? dir = ApworldUpdater.WorldsDir();
        if (dir == null)
        {
            TxtApworldState.Text = "No Archipelago engine yet — point London at "
                                 + "one under Multiworld and this can work.";
            BtnUpdateAllApworlds.IsEnabled = false;
            return;
        }
        BtnUpdateAllApworlds.IsEnabled = true;
        TxtApworldState.Text = "Checking…";

        int behind = 0, known = 0;
        try
        {
            foreach (string id in await ApworldUpdater.CandidatesAsync(InstalledGameIds()))
            {
                var st = await ApworldUpdater.CheckAsync(id);
                if (st.State == ApworldState.None) continue;
                known++;
                if (st.Actionable) behind++;
            }
        }
        catch (Exception)
        {
            TxtApworldState.Text = "Could not check right now.";
            return;
        }

        TxtApworldState.Text = known == 0
            ? "None of your installed games fetch a world from outside the engine."
            : behind == 0
                ? $"All {known} world(s) match what their authors publish."
                : $"{behind} of {known} world(s) are behind.";

        // And the engine itself, on the same line: the number above is the
        // reason to press, and an engine that is behind is a second one.
        try
        {
            var check = await ApEngineUpdater.CheckAsync();
            if (check.Newer && check.Offer != null)
                TxtApworldState.Text += $" Archipelago {check.Offer.Version} is out — "
                                      + $"you have {check.Engine!.Version}.";
        }
        catch (Exception) { /* the worlds line stands on its own */ }
    }

    // ------------------------------------------------------- all at once

    /// Every installed game whose world is behind, in one press.
    ///
    /// The per-game button is the one a player uses while looking at a game;
    /// this is the one they use after not opening London for a month.
    private async void BtnUpdateAllApworlds_Click(object sender, RoutedEventArgs e)
    {
        BtnUpdateAllApworlds.IsEnabled = false;
        object? was = BtnUpdateAllApworlds.Content;
        try
        {
            if (ApworldUpdater.WorldsDir() == null)
            {
                ToastService.Show("No engine yet",
                    "Point London at an Archipelago install first — the worlds "
                  + "have to go into its own folder.", ToastKind.Warning);
                return;
            }

            // The engine before its worlds — see ApEngineUpdater for why.
            bool engineUpdated = false;
            try { engineUpdated = await UI.Dialogs.ApEngineUpdateDialog.OfferAsync(this, m => AppendLog(m)); }
            catch (Exception ex) { AppendLog("[AP engine] Could not check for an update: " + ex.Message); }
            if (engineUpdated) EnsureTemplatesFresh();

            var ids = await ApworldUpdater.CandidatesAsync(InstalledGameIds());

            var progress = new Progress<string>(m =>
                BtnUpdateAllApworlds.Content = m.Length > 24 ? m[..24] + "…" : m);

            AppendLog($"[AP worlds] Checking {ids.Count} installed game(s)…");
            var lines = await ApworldUpdater.UpdateAllAsync(ids, progress);

            foreach (string line in lines) AppendLog("[AP worlds] " + line);
            AppendLog(lines.Count == 0
                ? "[AP worlds] Every installed game's world is already the "
                + "published one."
                : $"[AP worlds] {lines.Count} world(s) fetched.");
            ToastService.Show(
                lines.Count == 0 ? "AP worlds already current"
                                 : $"{lines.Count} AP world(s) updated",
                lines.Count == 0 ? "Nothing to fetch."
                                 : "The log lists which ones.",
                ToastKind.Success);

            if (_selectedPlugin != null) _ = RefreshApworldButtonAsync(_selectedPlugin);
            _ = RefreshApworldRailAsync();
            _ = CheckPluginUpdatesAsync();
        }
        finally
        {
            BtnUpdateAllApworlds.Content = was;
            BtnUpdateAllApworlds.IsEnabled = true;
        }
    }
}
