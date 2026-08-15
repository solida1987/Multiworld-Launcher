using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LauncherV2.Core;

// ApworldSync — mirrors the .apworld files the launcher installs into a folder
// the user nominates (normally Archipelago's custom_worlds).

// Why this exists: several plugins ship an .apworld alongside the game, but it
// lands inside the launcher's own install tree.
// from its custom_worlds folder, so every update meant hand-copying a file.
// Point this at that folder once and the copy happens on every install/update.

// OPT-IN. LauncherSettings.ApworldSyncDir is empty by default and nothing runs
// until the user fills it in.

// SAFETY: this only ever ADDS or OVERWRITES files it recognises as ours (an
// .apworld of the same name).
// never touches anything else in the target folder — that folder belongs to the
// user's Archipelago install and may hold dozens of unrelated community worlds.
// A failure here is always non-fatal: an install must never break because a
// USB drive got unplugged or a path was mistyped.

public sealed record ApworldSyncResult(int Copied, int UpToDate, int Failed, string? Error)
{
    public bool DidSomething => Copied > 0 || UpToDate > 0 || Failed > 0;

    // One-line summary for the log.
    public string? Summary()
    {
        if (Error != null) return $"AP World sync skipped — {Error}";
        if (!DidSomething) return null;
        var bits = new List<string>();
        if (Copied   > 0) bits.Add($"{Copied} copied");
        if (UpToDate > 0) bits.Add($"{UpToDate} already up to date");
        if (Failed   > 0) bits.Add($"{Failed} failed");
        return "AP World sync — " + string.Join(", ", bits);
    }
}

public static class ApworldSync
{
    // Every root a plugin might drop an .apworld into.
    // in this codebase: the game's own install folder, and the shared
    // Games/ROMs/<gameId> library used by plugins that don't install a game
    // tree of their own. Both are checked; neither is required to exist.
    private static IEnumerable<string> SourceRoots(IGamePlugin plugin)
    {
        string? dir = null;
        try { dir = plugin.GameDirectory; } catch { /* plugin not configured */ }
        if (!string.IsNullOrWhiteSpace(dir)) yield return dir!;

        string roms = Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", plugin.GameId);
        if (!string.Equals(roms, dir, StringComparison.OrdinalIgnoreCase)) yield return roms;
    }

    // All .apworld files this plugin has on disk right now.
    public static List<string> FindApworlds(IGamePlugin plugin)
    {
        var found = new List<string>();
        foreach (string root in SourceRoots(plugin))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                found.AddRange(Directory.EnumerateFiles(
                    root, "*.apworld", SearchOption.AllDirectories));
            }
            catch (Exception)
            {
                // Unreadable subtree (permissions, a junction loop) — the rest
                // of the roots still get scanned.
            }
        }
        return found;
    }

    // Copy one plugin's .apworld files into targetDir.
    // Never throws.
    public static ApworldSyncResult Sync(IGamePlugin plugin, string? targetDir)
        => Sync(new[] { plugin }, targetDir);

    // Copy several plugins' .apworld files into targetDir.
    // Never throws.
    public static ApworldSyncResult Sync(IEnumerable<IGamePlugin> plugins, string? targetDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
            return new ApworldSyncResult(0, 0, 0, null);   // opt-in: not configured

        string dest = targetDir!.Trim();
        try
        {
            // Created rather than rejected: the Browse button is the normal way
            // in, so a missing folder almost always means the user typed a path
            // for a folder they intend to use.
            // path either way, so a typo is visible instead of silent.
            Directory.CreateDirectory(dest);
        }
        catch (Exception ex)
        {
            return new ApworldSyncResult(0, 0, 0, $"cannot use \"{dest}\": {ex.Message}");
        }

        return CopyInto(plugins.SelectMany(FindApworlds), dest);
    }

    // The copy half, split out from plugin discovery so the semantics that
    // actually matter — overwrite, skip-identical, self-copy, per-file failure
    // — can be exercised directly without standing up a game plugin.
    // Assumes dest already exists.
    public static ApworldSyncResult CopyInto(IEnumerable<string> sources, string dest)
    {
        int copied = 0, upToDate = 0, failed = 0;
        foreach (string src in sources)
        {
            string dst;
            try { dst = Path.Combine(dest, Path.GetFileName(src)); }
            catch (Exception) { failed++; continue; }

            // The user is allowed to point this straight at a folder we already
            // write into. Copying a file onto itself throws, so treat that as
            // "already up to date".
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst),
                              StringComparison.OrdinalIgnoreCase))
            {
                upToDate++;
                continue;
            }

            try
            {
                if (IsSameFile(src, dst)) { upToDate++; continue; }
                File.Copy(src, dst, overwrite: true);
                copied++;
            }
            catch (Exception)
            {
                // File locked by a running Archipelago, read-only target, drive
                // disconnected — counted and reported, never thrown.
                failed++;
            }
        }
        return new ApworldSyncResult(copied, upToDate, failed, null);
    }

    // Cheap "is the destination already this exact build?" test.
    // last-write time is enough here: .apworld files are rebuilt by our own
    // release pipeline, so a new build always differs in one or the other.
    // Hashing every file on every install would cost more than the copy.
    private static bool IsSameFile(string src, string dst)
    {
        if (!File.Exists(dst)) return false;
        var a = new FileInfo(src);
        var b = new FileInfo(dst);
        return a.Length == b.Length &&
               a.LastWriteTimeUtc == b.LastWriteTimeUtc;
    }
}
