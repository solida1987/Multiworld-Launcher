using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LauncherV2.Core;

namespace LauncherV2;

// ─────────────────────────────────────────────────────────────────────────────
// SelfTest — invoked via  MultiworldLauncher.exe --self-test
//
// Runs after all GameRegistry.Register() calls in App.xaml.cs so it sees the
// full plugin list. Writes self_test.log next to the exe and exits:
//   exit 0  — PASS or PASS WITH WARNINGS
//   exit 1  — FAIL (one or more hard errors)
//
// Use in CI / prerelease check:
//   MultiworldLauncher.exe --self-test && echo PASS || echo FAIL
// ─────────────────────────────────────────────────────────────────────────────

internal static class SelfTest
{
    internal static void Run()
    {
        var lines    = new List<string>();
        int errors   = 0;
        int warnings = 0;

        void OK  (string msg) { lines.Add($"[OK]   {msg}"); }
        void WARN(string msg) { lines.Add($"[WARN] {msg}"); warnings++; }
        void ERR (string msg) { lines.Add($"[ERR]  {msg}"); errors++; }
        void HDR (string msg) { lines.Add(""); lines.Add(msg); }

        lines.Add("Multiworld Launcher — Self-Test Report");
        lines.Add($"Run at: {DateTime.UtcNow:u}");
        lines.Add(new string('─', 72));

        // ── 1. Plugin registration count ──────────────────────────────────
        var plugins = GameRegistry.All;
        HDR($"[1] PLUGIN REGISTRATION");

        if (plugins.Count == 0)
            ERR("No plugins registered — App.xaml.cs registration block missing?");
        else
            OK($"{plugins.Count} plugins registered");

        // ── 2. Per-plugin validity ─────────────────────────────────────────
        HDR($"[2] PLUGIN VALIDITY");

        var seenIds    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int iconMissing = 0;

        foreach (var p in plugins)
        {
            if (string.IsNullOrWhiteSpace(p.GameId))
            {
                ERR($"  {p.GetType().Name}: GameId is null/empty");
                continue;
            }

            if (!seenIds.Add(p.GameId))
                ERR($"  Duplicate GameId: '{p.GameId}' ({p.GetType().Name})");

            if (string.IsNullOrWhiteSpace(p.DisplayName))
                WARN($"  {p.GameId}: DisplayName is empty");

            if (string.IsNullOrWhiteSpace(p.ApWorldName))
                WARN($"  {p.GameId}: ApWorldName is empty");

            if (!string.IsNullOrEmpty(p.IconPath) && !File.Exists(p.IconPath))
            {
                WARN($"  {p.GameId}: icon not found ({Path.GetFileName(p.IconPath)})");
                iconMissing++;
            }
        }

        if (errors == 0)
            OK($"All {plugins.Count} GameIds are unique and non-empty");

        if (iconMissing == 0)
            OK("All icon paths resolve to existing files");
        else
            lines.Add($"       ({iconMissing} missing icon(s) — warnings above)");

        // ── 3. Catalog cross-check ─────────────────────────────────────────
        HDR($"[3] CATALOG CROSS-CHECK");

        string? catalogPath = FindCatalogPath();
        if (catalogPath == null)
        {
            WARN("catalog.json not found — skipping cross-check");
            WARN("  (run from the dev tree or place catalog.json next to the exe)");
        }
        else
        {
            try
            {
                using var doc     = JsonDocument.Parse(File.ReadAllText(catalogPath));
                var gamesEl       = doc.RootElement.GetProperty("games");
                var catalogIds    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var g in gamesEl.EnumerateArray())
                    if (g.TryGetProperty("id", out var idEl))
                    {
                        string? id = idEl.GetString();
                        if (!string.IsNullOrEmpty(id)) catalogIds.Add(id);
                    }

                OK($"catalog.json loaded: {catalogIds.Count} entries");
                lines.Add($"       (from {catalogPath})");

                // Catalog IDs with no matching plugin
                var orphanCatalog = catalogIds
                    .Where(id => GameRegistry.Find(id) == null)
                    .OrderBy(s => s)
                    .ToList();

                if (orphanCatalog.Count == 0)
                    OK("All catalog IDs have a matching plugin");
                else
                    foreach (string id in orphanCatalog)
                        WARN($"  catalog entry '{id}' has no matching plugin");

                // Plugins with no catalog entry
                var orphanPlugins = plugins
                    .Where(p => !catalogIds.Contains(p.GameId))
                    .ToList();

                if (orphanPlugins.Count == 0)
                    OK("All plugins have a matching catalog entry");
                else
                    foreach (var p in orphanPlugins)
                        WARN($"  plugin '{p.GameId}' not in catalog");
            }
            catch (Exception ex)
            {
                ERR($"Failed to parse catalog.json: {ex.Message}");
            }
        }

        // ── Summary ────────────────────────────────────────────────────────
        lines.Add("");
        lines.Add(new string('─', 72));
        string result = errors > 0 ? "FAIL"
                      : warnings > 0 ? "PASS WITH WARNINGS"
                      : "PASS";
        lines.Add($"RESULT: {result}  ({errors} error(s), {warnings} warning(s))");

        // Write to self_test.log next to the exe
        string logPath = Path.Combine(AppContext.BaseDirectory, "self_test.log");
        try { File.WriteAllLines(logPath, lines); }
        catch { /* log write failure must not mask the result */ }

        // Also write to stderr so it surfaces in a console or CI log
        foreach (string line in lines)
            Console.Error.WriteLine(line);

        Environment.Exit(errors > 0 ? 1 : 0);
    }

    // ── Locate catalog.json in dev or release tree ─────────────────────────

    private static string? FindCatalogPath()
    {
        string exeDir = AppContext.BaseDirectory;

        // In the dev tree the exe lands at  bin/Release/net8.0-windows/win-x64/
        // and the project root is 4 levels up.  Try a few depths.
        string[] candidates =
        {
            Path.Combine(exeDir, "CatalogRepo", "catalog.json"),
            Path.GetFullPath(Path.Combine(exeDir, "..", "CatalogRepo", "catalog.json")),
            Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "CatalogRepo", "catalog.json")),
            Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "..", "CatalogRepo", "catalog.json")),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
