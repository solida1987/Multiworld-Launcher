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
