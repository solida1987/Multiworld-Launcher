using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LauncherV2.Core.Plugins;

// PendingDeletes — paths an uninstall could not remove because the process
// still held them (a plugin DLL stays locked after Unload; that is scheduling,
// not release). They are finished at the NEXT start, before any plugin loads
// and can lock its files again.
//
// ⚠ Only paths inside the launcher's own directory are ever honoured, and the
// check runs at DELETE time, not at write time: the file sits on disk between
// runs, and a line edited into it must not become a deletion of anything.
public static class PendingDeletes
{
    private static string StorePath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "pending_deletes.json");

    public static void Add(IEnumerable<string> paths)
    {
        try
        {
            var all = Read();
            all.AddRange(paths);
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(
                all.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }
        catch { /* worst case: the file lingers until the next uninstall */ }
    }

    /// Call once at startup, before plugins load.
    public static void Run()
    {
        List<string> all;
        try { all = Read(); } catch { return; }
        if (all.Count == 0) return;

        string root = Path.GetFullPath(AppContext.BaseDirectory)
                          .TrimEnd(Path.DirectorySeparatorChar);
        var left = new List<string>();
        foreach (string raw in all)
        {
            string full;
            try { full = Path.GetFullPath(raw); } catch { continue; }
            // The gate: inside London, and never inside Emulators/.
            if (!full.StartsWith(root + Path.DirectorySeparatorChar,
                                 StringComparison.OrdinalIgnoreCase)) continue;
            if (full.StartsWith(Path.Combine(root, "Emulators") + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
                else if (File.Exists(full)) File.Delete(full);
            }
            catch { left.Add(raw); }   // still locked: try again next start
        }
        try
        {
            if (left.Count == 0) File.Delete(StorePath);
            else File.WriteAllText(StorePath, JsonSerializer.Serialize(left));
        }
        catch { }
    }

    private static List<string> Read()
    {
        if (!File.Exists(StorePath)) return new List<string>();
        return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath))
               ?? new List<string>();
    }
}
