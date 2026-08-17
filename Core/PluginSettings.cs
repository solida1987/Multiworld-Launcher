using System;
using System.Collections.Generic;
using System.Globalization;

namespace LauncherV2.Core;

// Per-plugin key/value bag inside the launcher's settings file, keyed by
// GameId. The launcher never reads the contents. Migrates the three legacy
// d2_* fields on first use — a floor, never overwriting plugin writes.
public static class PluginSettings
{
    // The three legacy fields, and the plugin/key they became. Applied once,
    // the first time anything asks for a value, so a player who already told
    // the launcher where their Diablo II lives is never asked again.
    //
    // The stable and experimental channels shared these fields, so both get
    // the migrated value — which is what they had before.
    private const string D2Stable = "diablo2_archipelago";
    private const string D2Exp    = "diablo2_archipelago_experimental";

    private static bool _migrated;
    private static readonly object _lock = new();

    private static void MigrateOnce(LauncherSettings s)
    {
        if (_migrated) return;
        _migrated = true;

        bool changed = false;

        void Carry(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (string game in new[] { D2Stable, D2Exp })
            {
                if (!s.PluginValues.TryGetValue(game, out var bag))
                    s.PluginValues[game] = bag = new Dictionary<string, string>();
                // Never overwrite something the plugin has already written --
                // the migration is a floor, not an authority.
                if (!bag.ContainsKey(key)) { bag[key] = value!; changed = true; }
            }
        }

        Carry("original_game_folder", s.DiabloIIPath);
        if (s.D2Windowed) Carry("windowed", "true");
        if (s.D2NoSound)  Carry("no_sound", "true");

        if (changed) SettingsStore.Save(s);
    }

    public static string? Get(string gameId, string key)
    {
        lock (_lock)
        {
            var s = SettingsStore.Load();
            MigrateOnce(s);
            return s.PluginValues.TryGetValue(gameId, out var bag)
                   && bag.TryGetValue(key, out var v) ? v : null;
        }
    }

    public static void Set(string gameId, string key, string? value)
    {
        lock (_lock)
        {
            var s = SettingsStore.Load();
            MigrateOnce(s);

            if (!s.PluginValues.TryGetValue(gameId, out var bag))
                s.PluginValues[gameId] = bag = new Dictionary<string, string>();

            if (value == null) bag.Remove(key);
            else               bag[key] = value;

            SettingsStore.Save(s);
        }
    }

    // Convenience for the common case. A value that is not there, or is not a
    // bool, gives the fallback -- a corrupt setting should not throw at a
    // plugin somewhere far from here.
    public static bool GetBool(string gameId, string key, bool fallback = false)
        => bool.TryParse(Get(gameId, key), out bool b) ? b : fallback;

    public static void SetBool(string gameId, string key, bool value)
        => Set(gameId, key, value ? "true" : "false");

    public static int GetInt(string gameId, string key, int fallback = 0)
        => int.TryParse(Get(gameId, key), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int n) ? n : fallback;

    public static void SetInt(string gameId, string key, int value)
        => Set(gameId, key, value.ToString(CultureInfo.InvariantCulture));
}
