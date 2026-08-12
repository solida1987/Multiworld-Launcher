using System;
using System.IO;
using System.Linq;

namespace LauncherV2.Core;

// ContentCleanup — one-shot removal of every non-Diablo game this launcher
// previously installed or cached on the user's machine.
//
// WHY THIS EXISTS
// The launcher used to carry a large game catalog. That catalog is gone: from
// this version on, only the two Diablo II channels remain. Anything the old
// catalog put on disk — installed game folders, fetched apworlds, cached
// thumbnails and hero art — must not linger on users' machines, so the first
// run of this version sweeps it.
//
// SCOPE — deliberately narrow. Only directories THIS launcher owns are
// touched: Games\, Assets caches, and the CatalogRepo mirror next to the exe.
// Nothing outside the launcher's own folder is ever deleted, and the two
// Diablo channels (installs, saves, seeds) are explicitly kept.
public static class ContentCleanup
{
    private static readonly string[] KeepGameIds =
    {
        "diablo2_archipelago",
        "diablo2_archipelago_experimental",
    };

    // Stamp file so the sweep runs once per stamp version, not on every start.
    private const string StampName = "content_cleanup.stamp";
    // -2: the first sweep cleared the Heroes/Thumbs caches but missed the
    // loose game icons in Assets\ itself; bumping re-runs the (idempotent)
    // sweep with the per-file art pass on machines that already ran -1.
    private const string StampValue = "diablo-only-2";

    public static void RunOnce()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string stamp = Path.Combine(baseDir, "Data", StampName);
            if (File.Exists(stamp) &&
                File.ReadAllText(stamp).Trim() == StampValue)
                return;

            int removed = 0;

            // 1) Installed game folders for anything that is not Diablo II.
            string games = Path.Combine(baseDir, "Games");
            if (Directory.Exists(games))
            {
                foreach (string dir in Directory.GetDirectories(games))
                {
                    string name = Path.GetFileName(dir);
                    if (KeepGameIds.Contains(name, StringComparer.OrdinalIgnoreCase))
                        continue;
                    removed += TryDeleteTree(dir);
                }
            }

            // 2) Art for the old catalog: loose game icons plus the Heroes and
            //    Thumbs caches. Delete per file so the Diablo art and the
            //    _generic/placeholder fallbacks survive.
            foreach (string sub in new[] { "Assets", @"Assets\Thumbs",
                                           @"Assets\Heroes" })
                removed += DeleteForeignArt(Path.Combine(baseDir, sub));
            removed += TryDeleteTree(Path.Combine(baseDir, @"Assets\_generation"));

            // 3) The local catalog mirror; it re-syncs from the repo, which
            //    now only carries Diablo II.
            string cat = Path.Combine(baseDir, "CatalogRepo", "thumbnails");
            removed += TryDeleteTree(cat);

            // 4) Diablo II keeps its save path in the registry, and launching
            //    the mod points it at the mod's own save folder. Left behind,
            //    it sends a plain Diablo II install looking for saves inside a
            //    folder that may no longer exist, and the game fails to start.
            removed += ClearD2RegistryResidue();

            Directory.CreateDirectory(Path.GetDirectoryName(stamp)!);
            File.WriteAllText(stamp, StampValue);
            System.Diagnostics.Debug.WriteLine(
                $"ContentCleanup: removed {removed} item(s)");
        }
        catch
        {
            // A failed sweep must never stop the launcher; it retries on the
            // next start because the stamp was not written.
        }
    }

    // Points Diablo II's registry save path back at its own installation and
    // drops the launcher's own key, but only when the recorded path leads into
    // this launcher's folder — a player who set their own save path keeps it.
    private static int ClearD2RegistryResidue()
    {
        const string sub = @"SOFTWARE\Blizzard Entertainment\Diablo II";
        int changed = 0;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(sub, writable: true);
            if (key == null) return 0;

            string baseDir = AppContext.BaseDirectory.TrimEnd('\\');
            string? install = key.GetValue("InstallPath") as string;

            foreach (string name in new[] { "Save Path", "NewSavePath" })
            {
                if (key.GetValue(name) is not string v || v.Length == 0) continue;
                if (!v.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(install)) { key.DeleteValue(name, false); }
                else { key.SetValue(name, Path.Combine(install, "save") + "\\"); }
                changed++;
            }

            if (key.GetValue("LauncherPath") != null)
            {
                key.DeleteValue("LauncherPath", false);
                changed++;
            }
        }
        catch { }
        return changed;
    }

    // Deletes .png files in ONE directory (not subdirectories) whose name does
    // not belong to a kept game or the generic fallbacks.
    private static int DeleteForeignArt(string dir)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (string f in Directory.EnumerateFiles(dir, "*.png",
                         SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                bool keep =
                    name.StartsWith("diablo2_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("_generic", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("placeholder", StringComparison.OrdinalIgnoreCase);
                if (keep) continue;
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                    removed++;
                }
                catch { }
            }
        }
        catch { }
        return removed;
    }

    private static int TryDeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            // Read-only files (git objects, some game files) make a bare
            // Directory.Delete throw half-way; clear the bit first.
            foreach (var f in Directory.EnumerateFiles(
                         path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); }
                catch { }
            }
            Directory.Delete(path, recursive: true);
            return 1;
        }
        catch
        {
            return 0;   // locked file etc. — retried next start via the stamp
        }
    }
}
