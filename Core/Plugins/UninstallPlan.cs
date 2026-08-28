using System;
using System.Collections.Generic;
using System.IO;

namespace LauncherV2.Core.Plugins;

// UninstallPlan — what uninstalling a game may and may not delete.
//
// The rule, in the owner's words: delete what London put in its OWN folders
// for this game; never the player's game files outside London, and never an
// emulator. This class only DECIDES -- it deletes nothing -- so the decision
// can be proved in tools/UninstallProof against paths no test machine has.
public sealed record UninstallItem(string Path, string What, bool IsDirectory);

public static class UninstallPlan
{
    /// Everything London owns for this game. Every entry names what it is,
    /// because the confirmation dialog shows this list verbatim -- the player
    /// approves the actual paths, not a summary.
    public static IReadOnlyList<UninstallItem> Build(
        string gameId, string? gameDirectory, string baseDirectory)
    {
        var plan = new List<UninstallItem>();
        string root = Norm(baseDirectory);

        // 1. The plugin package itself: GamePlugins/<id>/. Always London's.
        string pluginDir = Path.Combine(baseDirectory, "GamePlugins", gameId);
        plan.Add(new UninstallItem(pluginDir, "the game's plugin", true));

        // 2. Cached art. Refetchable from the catalogue at any time.
        plan.Add(new UninstallItem(
            Path.Combine(baseDirectory, "Assets", gameId + ".png"),
            "cached cover art", false));
        plan.Add(new UninstallItem(
            Path.Combine(baseDirectory, "Assets", "Heroes", gameId + "_hero.png"),
            "cached banner art", false));

        // 3. The game directory -- ONLY when it is London's own. A directory
        // outside the launcher is the player's; a directory under Emulators/
        // is shared by every game on that console and is nobody's to delete.
        if (!string.IsNullOrWhiteSpace(gameDirectory))
        {
            string dir = Norm(gameDirectory);
            bool insideLondon  = dir.StartsWith(root + Path.DirectorySeparatorChar,
                                                StringComparison.OrdinalIgnoreCase);
            bool insideEmulators = dir.StartsWith(
                Norm(Path.Combine(baseDirectory, "Emulators")) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
                || string.Equals(dir, Norm(Path.Combine(baseDirectory, "Emulators")),
                                 StringComparison.OrdinalIgnoreCase);
            bool isPluginDirItself = string.Equals(dir, Norm(pluginDir),
                                 StringComparison.OrdinalIgnoreCase);
            if (insideLondon && !insideEmulators && !isPluginDirItself)
                plan.Add(new UninstallItem(gameDirectory!, "game files London installed", true));
        }

        return plan;
    }

    /// What the plan deliberately leaves alone, said out loud so the dialog
    /// can promise it. The promise is part of the feature.
    public static string Keeps(string? gameDirectory, string baseDirectory)
    {
        string root = Norm(baseDirectory);
        bool external = !string.IsNullOrWhiteSpace(gameDirectory)
            && !Norm(gameDirectory!).StartsWith(root + Path.DirectorySeparatorChar,
                                                StringComparison.OrdinalIgnoreCase);
        return external
            ? "Your own game files (they live outside the launcher) and every emulator stay untouched."
            : "Every emulator stays untouched.";
    }

    private static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, '/'); }
        catch { return p; }
    }
}
