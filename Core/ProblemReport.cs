using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace LauncherV2.Core;

// <summary>
// Collects everything needed to diagnose a crash into ONE zip on the Desktop.

// The problem this solves: the game already writes a good crash log
// (d2arch_crash.txt — faulting module, offset, build stamp, recent log tail),
// but nobody knows it exists.
// diagnostics instead, which contains no crash information at all, and the
// round-trip to ask for the right file costs a day.

// So the player is never asked to find anything.
// Desktop with an obvious name, and it is produced automatically when a game
// exits abnormally as well as on demand.
// </summary>
internal static class ProblemReport
{
    // Per-file cap. Logs from a long session can reach tens of megabytes, and
    // the tail is the part that matters — so oversized files are truncated
    // from the END, keeping what happened just before the crash.
    private const long MaxFileBytes = 4L * 1024 * 1024;

    // Safety net so a folder full of stale dumps cannot produce a zip nobody
    // can upload to Discord.
    private const int MaxFilesPerGame = 40;

    // Names we always want, matched case-insensitively anywhere in the game
    // folder tree.
    private static readonly string[] WantedNames =
    {
        "d2arch_crash.txt", "d2arch_log.txt", "d2arch.ini", "version.dat",
        "crash.log", "error.log", "output_log.txt", "player.log",
    };

    // Extensions worth sweeping up generically, for the other plugins that do
    // not follow the Diablo II naming.
    private static readonly string[] WantedExtensions = { ".dmp", ".crash" };

    // Directories that are never interesting and can be enormous.
    private static readonly string[] SkipDirs =
    {
        "data", "mpq", "save", "screenshots", "cache", "_apbackup",
    };

    // <summary>
    // Build the report and return the full path of the zip that was written.
    // Throws only if the Desktop itself is unwritable; individual files that
    // cannot be read are recorded in the manifest instead of aborting.
    // </summary>
    internal static string Build(string diagnosticsText,
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
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                             .Where(f => !IsUnderSkippedDir(root, f))
                             .Where(IsInteresting)
                             // Newest first, so the per-game cap keeps what is
                             // relevant to the crash that just happened rather
                             // than whatever happens to sort first.
                             .OrderByDescending(f => SafeWriteTime(f))
                             .Take(MaxFilesPerGame)
                             .ToList();
        }
        catch (Exception ex)
        {
            manifest.AppendLine($"  {prefix}/ — could not be read ({ex.GetType().Name})");
            return;
        }

        foreach (string f in files)
        {
            string rel;
            try { rel = Path.GetRelativePath(root, f).Replace('\\', '/'); }
            catch { rel = Path.GetFileName(f); }

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

    // <summary>
    // Did this exit code mean the game died rather than quit?
    // Windows reports a fatal exception as the exception code itself, which is
    // always in the 0xC0000000 range (0xC0000005 = access violation,
    // 0x80000003 = the breakpoint a Diablo II assert raises).
    // </summary>
    internal static bool LooksLikeCrash(int exitCode)
    {
        if (exitCode == 0) return false;
        uint u = unchecked((uint)exitCode);
        return (u & 0xF0000000u) == 0xC0000000u || (u & 0xF0000000u) == 0x80000000u;
    }
}
