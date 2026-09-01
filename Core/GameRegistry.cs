using System;
using System.Collections.Generic;
using System.Linq;

namespace LauncherV2.Core;

// GameRegistry — central list of installed game plugins.

// V2.0.0: plugins are compiled in and registered by App.xaml.cs at startup:
// GameRegistry.Register(new DiabloII.D2Plugin { GameDirectory = ...

// Future: Register can scan a Plugins/ directory and load assemblies via
// reflection. The interface stays identical — GameRegistry never changes.

public static class GameRegistry
{
    private static readonly List<IGamePlugin> _plugins = new();

    // --- Registration ---

    // Register a game plugin.
    // Throws if a plugin with the same GameId is already registered.
    public static void Register(IGamePlugin plugin)
    {
        if (_plugins.Any(p => p.GameId == plugin.GameId))
            throw new InvalidOperationException(
                $"A plugin with GameId '{plugin.GameId}' is already registered.");
        _plugins.Add(plugin);
    }

    // --- Lookup ---

    // All registered plugins in registration order.
    public static IReadOnlyList<IGamePlugin> All => _plugins;

    // Find a plugin by its stable GameId.
    public static IGamePlugin? Find(string gameId)
        => _plugins.FirstOrDefault(p => string.Equals(p.GameId, gameId,
               StringComparison.OrdinalIgnoreCase));

    // Currently running plugin (at most one game at a time in V2.0.0).
    public static IGamePlugin? ActivePlugin
        => _plugins.FirstOrDefault(p => p.IsRunning);

    // Find the plugin for an Archipelago world name, forgivingly.
    //
    // Exact (case-insensitive) first. Then FOLDED — case, accents,
    // punctuation and roman numerals normalised — accepted only when exactly
    // one plugin matches. A plugin built before its catalogue name was
    // corrected still carries the old spelling inside it ("StarCraft II"
    // against the world's real "Starcraft 2"), and refusing that match told
    // the player to install a plugin they were looking at.
    public static IGamePlugin? ByWorldName(string? worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName)) return null;
        var exact = _plugins.FirstOrDefault(p => string.Equals(
            p.ApWorldName, worldName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        string want = FoldWorldName(worldName!);
        var near = _plugins.Where(p => FoldWorldName(p.ApWorldName) == want).ToList();
        return near.Count == 1 ? near[0] : null;
    }

    // What a human eye glosses over in a world name: case, accents,
    // punctuation — and roman numerals, because half this hobby's titles
    // write 2 as II. Shared with the YAML builder's template lookup so the
    // two forgive the same set of drifts.
    public static string FoldWorldName(string s)
    {
        s = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        string low = sb.ToString();
        low = System.Text.RegularExpressions.Regex.Replace(
            low, @"\b(viii|vii|vi|ix|iv|iii|ii|x|v|i)\b", m => m.Value switch
            {
                "i" => "1", "ii" => "2", "iii" => "3", "iv" => "4", "v" => "5",
                "vi" => "6", "vii" => "7", "viii" => "8", "ix" => "9", _ => "10",
            });
        return System.Text.RegularExpressions.Regex.Replace(low, "[^a-z0-9]+", "");
    }

    // --- Plugins loaded from disk ---

    private static readonly List<Plugins.LoadedPlugin> _loaded = new();

    // Register every approved plugin in GamePlugins\. Runs AFTER built-ins so
    // a plugin can never take over an existing GameId.
    public static IReadOnlyList<Plugins.LoadedPlugin> LoadedFromDisk => _loaded;

    ///
    /// Register every approved plugin in GamePlugins\. Call once at startup,
    /// AFTER the compiled-in games — a plugin must never be able to take over
    /// a built-in game's id by being loaded first.
    ///
    /// problems:
    /// Why a plugin did not load. Surfaced in the UI: a game that silently
    /// disappears is a support request, a game that says why is not.
    ///
    public static void LoadFromDisk(out IReadOnlyList<string> problems)
    {
        var issues = new List<string>();
        var approved = Plugins.PluginLoader.LoadApproved(out var loadIssues);
        foreach (var lp in approved)
        {
            if (Find(lp.Manifest.GameId) != null)
            {
                issues.Add($"{lp.Manifest.DisplayName}: a game with id "
                         + $"\"{lp.Manifest.GameId}\" is already built into the launcher");
                lp.Unload();
                continue;
            }

            Register(lp.Plugin);
            _loaded.Add(lp);

            // A plugin the player approved is a game they wanted. Keeping it
            // out of the library until they ask again would mean an installed
            // plugin that is nowhere on screen -- which is what a missing
            // library entry looked like: the game page open on the right and
            // "your library is empty" on the left.
            LibraryStore.Add(lp.Manifest.GameId);
        }
        issues.AddRange(loadIssues);
        problems = issues;
    }

    /// Drop a disk plugin from the library — removal, or revoked trust.
    public static bool UnloadFromDisk(string gameId)
    {
        var lp = _loaded.FirstOrDefault(p =>
            string.Equals(p.Manifest.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        if (lp == null) return false;

        _plugins.RemoveAll(p => string.Equals(p.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        _loaded.Remove(lp);
        lp.Unload();
        return true;
    }
}
