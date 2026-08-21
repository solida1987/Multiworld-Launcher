using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace LauncherV2.Core;

//
// Collects everything needed to diagnose a crash into ONE zip on the Desktop.

// The problem this solves: the game already writes a good crash log
// (d2arch_crash.txt — faulting module, offset, build stamp, recent log tail),
// but nobody knows it exists.
// diagnostics instead, which contains no crash information at all, and the
// round-trip to ask for the right file costs a day.

// So the player is never asked to find anything.
// Desktop with an obvious name, and it is produced automatically when a game
// exits abnormally as well as on demand.
//
public static class ProblemReport
{
    // Per-file cap. Logs from a long session can reach tens of megabytes, and
    // the tail is the part that matters — so oversized files are truncated
    // from the END, keeping what happened just before the crash.
    private const long MaxFileBytes = 4L * 1024 * 1024;

    // Safety net so a folder full of stale dumps cannot produce a zip nobody
    // can upload to Discord.
    private const int MaxFilesPerGame = 40;

    // How far back a log is still worth sending. Older ones are counted in the
    // manifest but not included: a report about today's crash carrying three
    // months of unrelated dumps costs the reader more than it tells them.
    private static readonly TimeSpan Window = TimeSpan.FromDays(14);

    // When this run of the launcher began. Files written since are from the
    // session the player is reporting about, which is nearly always the one
    // that matters -- so they are kept apart rather than mixed in.
    private static readonly DateTime SessionStart = GetSessionStart();

    private static DateTime GetSessionStart()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().StartTime; }
        catch (Exception)
        {
            // Some hosts refuse StartTime. An hour back is a poor guess but a
            // safe one: it over-includes rather than hiding something.
            return DateTime.Now - TimeSpan.FromHours(1);
        }
    }

    // Names we always want, matched case-insensitively anywhere in the game
    // folder tree.
    private static readonly string[] WantedNames =
    {
        "d2arch_crash.txt", "d2arch_log.txt", "d2arch.ini", "version.dat",
        "crash.log", "error.log", "output_log.txt", "player.log",
    };

    // ⚠ COLLECTED, NEVER DELETED.
    //
    // These are worth reading in a bug report -- the settings a game ran with
    // and the version it thought it was -- but they are CONFIGURATION and
    // STATE, not logs. Clearing them would throw away the player's settings
    // and leave the game rebuilding state it had no reason to lose.
    //
    // This list exists because "Clear all log files" was written against
    // WantedNames and would have deleted both of these. The proof did not
    // catch it: its fake install had a settings.json, which is not one of the
    // names this list actually holds.
    private static readonly string[] NeverDelete =
    {
        "d2arch.ini", "version.dat",
    };

    // Extensions worth sweeping up generically, for the other plugins that do
    // not follow the Diablo II naming.
    private static readonly string[] WantedExtensions = { ".dmp", ".crash" };

    // Directories that are never interesting and can be enormous.
    private static readonly string[] SkipDirs =
    {
        "data", "mpq", "save", "screenshots", "cache", "_apbackup",
    };

    //
    // Build the report and return the full path of the zip that was written.
    // Throws only if the Desktop itself is unwritable; individual files that
    // cannot be read are recorded in the manifest instead of aborting.
    //
    public static string Build(string diagnosticsText,
                                 IEnumerable<(string GameId, string Directory, bool Installed)> games,
                                 string? trigger = null,
                                 string? targetPath = null)
    {
        string stamp   = DateTime.Now.ToString("yyyy-MM-dd_HHmm");
        string zipPath;
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            // The player picked the location themselves (the "Collect logs"
            // button). Their choice wins outright, including overwriting —
            // they were already asked to confirm that in the save dialog.
            zipPath = targetPath!;
            var dir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }
        else
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
                desktop = AppContext.BaseDirectory;

            zipPath = Path.Combine(desktop, $"Multiworld-Rapport_{stamp}.zip");
            // Never overwrite a report the player may not have sent yet.
            int dedup = 2;
            while (File.Exists(zipPath))
                zipPath = Path.Combine(desktop, $"Multiworld-Rapport_{stamp}_{dedup++}.zip");
        }

        var manifest = new StringBuilder();
        manifest.AppendLine("=== What is this file? ===");
        manifest.AppendLine();
        manifest.AppendLine("A problem report from the Multiworld Launcher. Send the whole");
        manifest.AppendLine("zip to the developer — everything needed is already inside, so");
        manifest.AppendLine("there is nothing else to look for.");
        manifest.AppendLine();
        manifest.AppendLine($"Created : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        manifest.AppendLine($"Trigger : {trigger ?? "requested by the player"}");
        manifest.AppendLine();
        manifest.AppendLine("It contains the launcher's own diagnostics and, for each");
        manifest.AppendLine("installed game, its crash log, its run log and its settings.");
        manifest.AppendLine("No personal files are collected — only the game and launcher");
        manifest.AppendLine("folders, and only logs and configuration from them.");
        manifest.AppendLine();
        manifest.AppendLine($"Logs are split by when they were written. Anything under");
        manifest.AppendLine($"this-session/ was written after the launcher started at");
        manifest.AppendLine($"{SessionStart:yyyy-MM-dd HH:mm:ss} — that is the run being reported.");
        manifest.AppendLine($"earlier/ is older, kept for context. Files more than");
        manifest.AppendLine($"{Window.TotalDays:F0} days old are not included at all; the count is");
        manifest.AppendLine("listed below so you can ask for them.");
        manifest.AppendLine();
        manifest.AppendLine("=== Files collected ===");
        manifest.AppendLine();

        using (var zip = new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create))
        {
            WriteText(zip, "diagnostics.txt", diagnosticsText);

            // The launcher's own folder — its crash log lives next to the exe.
            AddFolder(zip, AppContext.BaseDirectory, "launcher", manifest, out int launcherCount);
            if (launcherCount == 0)
                manifest.AppendLine("  launcher/ — nothing to collect (no logs written yet)");

            foreach (var (gameId, dir, installed) in games)
            {
                if (!installed || string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    continue;
                AddFolder(zip, dir, $"games/{Sanitize(gameId)}", manifest, out int n);
                if (n == 0)
                    manifest.AppendLine($"  games/{gameId}/ — installed, but no logs found");
            }

            WriteText(zip, "READ ME FIRST.txt", manifest.ToString());
        }

        return zipPath;
    }

    // Copy the interesting files out of one folder tree into the zip.
    private static void AddFolder(ZipArchive zip, string root, string prefix,
                                  StringBuilder manifest, out int added)
    {
        added = 0;
        List<string> all;
        try
        {
            all = FindLogs(root).ToList();
        }
        catch (Exception ex)
        {
            manifest.AppendLine($"  {prefix}/ — could not be read ({ex.GetType().Name})");
            return;
        }

        // Too old to be about anything being reported now. Counted, never
        // silently dropped -- if the answer really is in a two-month-old file,
        // the reader needs to know it exists and can ask for it.
        var cutoff = DateTime.Now - Window;
        int stale = all.Count(f => SafeWriteTime(f) < cutoff);
        var files = all.Where(f => SafeWriteTime(f) >= cutoff)
                       // Newest first, so the per-game cap keeps what is
                       // relevant to the crash that just happened rather
                       // than whatever happens to sort first.
                       .OrderByDescending(f => SafeWriteTime(f))
                       .Take(MaxFilesPerGame)
                       .ToList();

        if (stale > 0)
            manifest.AppendLine($"  {prefix}/ — {stale} file(s) older than "
                              + $"{Window.TotalDays:F0} days were left out. Ask for them "
                              + "if this turns out to be an old problem.");

        foreach (string f in files)
        {
            string rel;
            try { rel = Path.GetRelativePath(root, f).Replace('\\', '/'); }
            catch { rel = Path.GetFileName(f); }

            // The split that makes the zip readable: this run of the launcher,
            // or before it.
            bool thisSession = SafeWriteTime(f) >= SessionStart;
            rel = (thisSession ? "this-session/" : "earlier/") + rel;

            try
            {
                var info = new FileInfo(f);
                bool truncated = info.Length > MaxFileBytes;
                byte[] bytes = truncated ? ReadTail(f, MaxFileBytes) : ReadAllShared(f);

                var entry = zip.CreateEntry($"{prefix}/{rel}", CompressionLevel.Optimal);
                using (var s = entry.Open())
                {
                    if (truncated)
                    {
                        byte[] note = Encoding.UTF8.GetBytes(
                            $"[Only the last {MaxFileBytes / 1024 / 1024} MB of this file are included " +
                            $"— it was {info.Length / 1024 / 1024} MB. The end is the part just " +
                            "before the crash.]\r\n\r\n");
                        s.Write(note, 0, note.Length);
                    }
                    s.Write(bytes, 0, bytes.Length);
                }

                manifest.AppendLine($"  {prefix}/{rel}  ({info.Length:N0} bytes" +
                                    (truncated ? ", truncated)" : ")"));
                added++;
            }
            catch (Exception ex)
            {
                // A file held open by the running game is normal, not a failure.
                manifest.AppendLine($"  {prefix}/{rel} — skipped ({ex.GetType().Name})");
            }
        }
    }

    /// Every file in a tree this report would collect. The ONE definition of
    /// "a log", so clearing can never remove something collecting would not
    /// have taken -- which is what keeps a "clear logs" button away from save
    /// files.
    public static IEnumerable<string> FindLogs(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Array.Empty<string>();
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Where(f => !IsUnderSkippedDir(root, f))
                        .Where(IsInteresting);
    }

    public sealed record ClearResult(int Deleted, int Locked, long BytesFreed)
    {
        public string Summary() =>
            Deleted == 0 && Locked == 0
                ? "There were no log files to clear."
                : $"Cleared {Deleted} log file(s), freeing {BytesFreed / 1024:N0} KB."
                  + (Locked > 0
                     ? $" {Locked} could not be removed — a game holding one open "
                       + "is normal; they go on the next attempt."
                     : "");
    }

    /// Delete the logs, so the next report carries only what happened after.
    ///
    /// Deletes exactly what Build would have collected and nothing else. A
    /// file the running game holds open is skipped rather than fought over --
    /// reporting it is more honest than pretending it went.
    public static ClearResult ClearLogs(
        IEnumerable<(string GameId, string Directory, bool Installed)> games)
    {
        int deleted = 0, locked = 0;
        long freed = 0;

        void Sweep(string root)
        {
            IEnumerable<string> found;
            try
            {
                found = FindLogs(root)
                        .Where(f => !NeverDelete.Contains(Path.GetFileName(f),
                                                          StringComparer.OrdinalIgnoreCase))
                        .ToList();
            }
            catch (Exception) { return; }

            foreach (string f in found)
            {
                try
                {
                    long size = new FileInfo(f).Length;
                    File.Delete(f);
                    deleted++;
                    freed += size;
                }
                catch (Exception)
                {
                    locked++;
                }
            }
        }

        Sweep(AppContext.BaseDirectory);
        foreach (var (_, dir, installed) in games)
            if (installed && !string.IsNullOrWhiteSpace(dir))
                Sweep(dir);

        return new ClearResult(deleted, locked, freed);
    }

    private static bool IsInteresting(string path)
    {
        string name = Path.GetFileName(path);
        if (WantedNames.Any(w => string.Equals(w, name, StringComparison.OrdinalIgnoreCase)))
            return true;
        string ext = Path.GetExtension(path);
        if (WantedExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
            return true;
        // Catch-all for plugin-specific naming, without dragging in the game's
        // own data files.
        if (name.Contains("crash", StringComparison.OrdinalIgnoreCase) &&
            (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
             ext.Equals(".log", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    private static bool IsUnderSkippedDir(string root, string file)
    {
        try
        {
            string rel = Path.GetRelativePath(root, file);
            string first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            return SkipDirs.Any(d => string.Equals(d, first, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static DateTime SafeWriteTime(string f)
    {
        try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; }
    }

    // Read with sharing on — the game is often still running and holding its
    // own log open for append.
    private static byte[] ReadAllShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] ReadTail(string path, long bytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > bytes) fs.Seek(fs.Length - bytes, SeekOrigin.Begin);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        byte[] b = Encoding.UTF8.GetBytes(text ?? "");
        s.Write(b, 0, b.Length);
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    //
    // Did this exit code mean the game died rather than quit?
    // Windows reports a fatal exception as the exception code itself, which is
    // always in the 0xC0000000 range (0xC0000005 = access violation,
    // 0x80000003 = the breakpoint a Diablo II assert raises).
    //
    internal static bool LooksLikeCrash(int exitCode)
    {
        if (exitCode == 0) return false;
        uint u = unchecked((uint)exitCode);
        return (u & 0xF0000000u) == 0xC0000000u || (u & 0xF0000000u) == 0x80000000u;
    }
}
