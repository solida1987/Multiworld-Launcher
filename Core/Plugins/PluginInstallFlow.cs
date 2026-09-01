using System;
using System.Windows;

namespace LauncherV2.Core.Plugins;

// "Add plugin" from start to finish, in the one order that is safe:
//
//   inspect (no code runs)  →  ask  →  install  →  record the hash  →  load
//
// Kept out of MainWindow so the sequence lives in one readable place. Getting
// it wrong is not a visual bug — installing before asking would mean a package
// the player declined had already written to disk.

public static class PluginInstallFlow
{
    public sealed record Outcome(bool Added, string? Message, LoadedPlugin? Plugin);

    ///
    /// Run the whole flow for a chosen .londonplugin file.
    /// Never throws; the message is written for the player.
    ///
    public static Outcome AddFromFile(Window? owner, string path)
    {
        var candidate = PluginPackage.Inspect(path);
        if (!candidate.IsUsable)
            return new Outcome(false, "This is not a plugin the launcher can use.\n\n"
                                    + candidate.Error, null);

        var m = candidate.Manifest!;

        // Replacing a built-in game would be a takeover, not an addition.
        if (GameRegistry.Find(m.GameId) != null && GameRegistry.LoadedFromDisk
                .All(p => !string.Equals(p.Manifest.GameId, m.GameId, StringComparison.OrdinalIgnoreCase)))
            return new Outcome(false,
                $"\"{m.GameId}\" is a game built into the launcher. A plugin cannot replace it.", null);

        if (!PluginConsentDialog.Ask(owner, candidate))
            return new Outcome(false, null, null);          // cancelled: say nothing

        return Install(candidate);
    }

    ///
    /// The post-consent half: unpack, record the hash, load, register.
    /// Reached from AddFromFile (the player just said yes) and from
    /// AutoApplyAsync (a first-party update, where the launcher's own
    /// authorship stands in for the dialog).
    ///
    private static Outcome Install(PluginCandidate candidate)
    {
        var m = candidate.Manifest!;

        // Only now does anything reach the disk.
        GameRegistry.UnloadFromDisk(m.GameId);               // replacing an older copy
        string? err = PluginPackage.Install(candidate);
        if (err != null)
            return new Outcome(false, "The plugin could not be unpacked.\n\n" + err, null);

        string dir = PluginPackage.DirectoryFor(m.GameId);

        // The hash recorded is of the INSTALLED folder, not of the package.
        // That is what later start-ups can re-check; a package file the player
        // may well delete is not something we can compare against.
        try
        {
            PluginTrustStore.Approve(m.GameId, PluginPackage.HashDirectory(dir), m.Version, m.Author);
        }
        catch (Exception ex)
        {
            return new Outcome(false, "The plugin was unpacked but could not be approved.\n\n"
                                    + ex.Message, null);
        }

        var loaded = PluginLoader.Load(dir, m, out string loadErr);
        if (loaded == null)
        {
            // Approved but unloadable: revoke, so a later start does not treat
            // a broken folder as trusted.
            PluginTrustStore.Revoke(m.GameId);
            return new Outcome(false, $"{m.DisplayName} could not be started.\n\n{loadErr}", null);
        }

        GameRegistry.Register(loaded.Plugin);

        // Into the library, so it appears in the sidebar the moment the player
        // approves it. Adding a plugin IS the act of putting a game there;
        // asking them to add it a second time in a different place would be a
        // step that exists only because the code was written in two halves.
        LibraryStore.Add(m.GameId);

        return new Outcome(true, $"{m.DisplayName} was added.", loaded);
    }

    ///
    /// May this update be applied without asking?
    ///
    /// Yes only when every one of these holds:
    ///   * the plugin is FIRST-PARTY — written by the launcher's own developer,
    ///     decided by the launcher's own list (PluginProvenance), never by the
    ///     package. Auto-installing it is the same act of trust as installing
    ///     a launcher update: the same author's code, from the same releases.
    ///   * the player already approved this plugin — automatic updates keep a
    ///     choice current, they never make the choice.
    /// Everything third-party keeps the two-prompt offer: somebody else's code
    /// never changes on this machine without the player saying so.
    ///
    public static bool MayAutoApply(PluginUpdater.Available update)
        => (FirstParty.For(update.GameId).IsFirstPartyPlugin
            || PluginAutoUpdatePolicy.OursByReleaseOwner(update.Source))
        && PluginTrustStore.Get(update.GameId) != null;

    ///
    /// Apply a first-party update with no dialogs: download, verify against
    /// the published checksum, install, record the new hash, reload.
    ///
    /// Exists because the yaml dialog, the mission boards and every other
    /// piece of per-game UI live in the PLUGIN — a player who updates the GAME
    /// and declines (or never sees) the separate plugin offer keeps last
    /// month's dialogs forever, and reports "it does not update". Measured on
    /// Diablo II: game on v3.9.6, Create YAML still the old plugin's.
    ///
    /// Never throws; a failed auto-update must degrade to "still on the old
    /// version", which is exactly the state the player was already in.
    ///
    public static async System.Threading.Tasks.Task<Outcome> AutoApplyAsync(
        PluginUpdater.Available update)
    {
        if (!MayAutoApply(update))
            return new Outcome(false, null, null);
        string? pkg = null;
        try
        {
            // No ConfigureAwait(false): Install() loads and registers the
            // plugin, and callers sit on the UI thread — stay there.
            pkg = await PluginUpdater.DownloadAsync(update, progress: null);

            var candidate = PluginPackage.Inspect(pkg);
            if (!candidate.IsUsable)
                return new Outcome(false,
                    $"{update.DisplayName}: the update package is not usable — "
                    + candidate.Error, null);

            // The feed and the package must agree about WHICH plugin this is.
            // A feed that starts naming a different game id must not replace
            // anything silently, whatever the checksum says.
            if (!string.Equals(candidate.Manifest!.GameId, update.GameId,
                               StringComparison.OrdinalIgnoreCase))
                return new Outcome(false,
                    $"{update.DisplayName}: the update names a different game "
                    + $"id (\"{candidate.Manifest.GameId}\") — not applied.", null);

            var outcome = Install(candidate);
            return outcome.Added
                ? new Outcome(true,
                    $"{update.DisplayName} was updated to {update.NewVersion}.",
                    outcome.Plugin)
                : outcome;
        }
        catch (Exception ex)
        {
            return new Outcome(false,
                $"{update.DisplayName}: the update could not be applied — "
                + ex.Message, null);
        }
        finally
        {
            if (pkg != null) try { System.IO.File.Delete(pkg); } catch { }
        }
    }

    ///
    /// Offer one update, and apply it if the player says yes twice.
    ///
    /// The second yes is the ordinary consent dialog, reached through
    /// AddFromFile: an update installs somebody's code exactly as the first
    /// install did, so it goes through the same door rather than a quieter one
    /// built beside it.
    ///
    public static Outcome OfferUpdate(Window? owner, PluginUpdater.Available update)
    {
        string? pkg = PluginUpdateDialog.Ask(owner, update);
        if (pkg == null)
            return new Outcome(false, null, null);      // declined or failed: already shown

        var outcome = AddFromFile(owner, pkg);

        // The temp copy has served its purpose either way. Leaving it behind
        // would accumulate one package per update in the user's temp folder.
        try { System.IO.File.Delete(pkg); } catch { }

        return outcome.Added
            ? new Outcome(true, $"{update.DisplayName} was updated to {update.NewVersion}.",
                          outcome.Plugin)
            : outcome;
    }

    /// Remove a plugin: out of the library, off the disk, out of the trust file.
    public static string? Remove(string gameId)
    {
        GameRegistry.UnloadFromDisk(gameId);
        PluginTrustStore.Revoke(gameId);
        try
        {
            string dir = PluginPackage.DirectoryFor(gameId);
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
            return null;
        }
        catch (Exception ex)
        {
            // Windows keeps a loaded assembly's file locked until the context
            // is collected, which can lag behind Unload(). The plugin is
            // already out of the library, so this is untidy, not broken.
            return "The plugin was removed from the library, but its files could "
                 + "not be deleted yet. They will go on the next start.\n\n" + ex.Message;
        }
    }
}
