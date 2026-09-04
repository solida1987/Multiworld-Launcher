using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LauncherV2.Core.Plugins;

/// One line per plugin London itself put on this machine.
public sealed class InstalledPluginEntry
{
    [JsonPropertyName("version")]      public string Version { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("installed_at")] public string InstalledAt { get; set; } = "";
}

/// What London installed, kept apart from whether it currently loads.
///
/// Three other lists each answer a nearby question and none answers this
/// one. The library (Data/library.json) holds every game the player ever
/// looked at, plugin or not. The trust store (Data/plugins.json) holds what
/// the player approved, and a revoke — the normal outcome of a failed
/// install — deletes the record. The registry of loaded plugins is rebuilt
/// from disk on every start and is empty for anything that did not load.
///
/// So when a plugin stopped loading after a launcher update, nothing
/// remembered that it had been there: the sidebar said "Your library is
/// empty. Add a plugin to put a game here." and the player did exactly
/// that, by hand (Maegis, 5 September, and not for the first time).
///
/// This file is written when London installs a plugin and cleared only when
/// the player removes it. Start-up compares it against what actually loaded;
/// anything missing is restored from the catalogue. See OrphanedPluginRepair.
public static class InstalledPluginRegistry
{
    private static string PathOnDisk => Path.Combine(
        AppContext.BaseDirectory, "Data", "installed_plugins.json");

    private static readonly object Gate = new();

    private static Dictionary<string, InstalledPluginEntry> Load()
    {
        try
        {
            if (File.Exists(PathOnDisk))
                return JsonSerializer.Deserialize<Dictionary<string, InstalledPluginEntry>>(
                           File.ReadAllText(PathOnDisk),
                           new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) { /* unreadable: start from nothing rather than throw */ }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private static void Save(Dictionary<string, InstalledPluginEntry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
        string tmp = PathOnDisk + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, PathOnDisk, overwrite: true);
    }

    public static bool Exists => File.Exists(PathOnDisk);

    /// A plugin London just installed or updated.
    public static void Record(PluginManifest m)
    {
        try
        {
            lock (Gate)
            {
                var all = Load();
                all[m.GameId] = new InstalledPluginEntry
                {
                    Version = m.Version,
                    DisplayName = m.DisplayName,
                    InstalledAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"),
                };
                Save(all);
            }
        }
        catch (Exception) { /* non-fatal: the plugin is installed either way */ }
    }

    /// The player removed it. Only the player: a failed load or a revoked
    /// approval is exactly the situation this file exists to remember.
    public static void Forget(string gameId)
    {
        try
        {
            lock (Gate)
            {
                var all = Load();
                if (all.Remove(gameId)) Save(all);
            }
        }
        catch (Exception) { }
    }

    public static IReadOnlyDictionary<string, InstalledPluginEntry> All()
    {
        lock (Gate) { return Load(); }
    }

    /// First run on a machine that installed plugins before this file
    /// existed: everything the trust store approved was installed by London,
    /// so that is the honest starting point. Runs once; a file that exists,
    /// even empty, is left alone.
    public static void SeedFromTrustStoreIfMissing()
    {
        try
        {
            if (Exists) return;
            var all = new Dictionary<string, InstalledPluginEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (gameId, rec) in PluginTrustStore.All())
                all[gameId] = new InstalledPluginEntry
                {
                    Version = rec.Version ?? "",
                    DisplayName = gameId,
                    InstalledAt = rec.Approved ?? "",
                };
            lock (Gate) { Save(all); }
        }
        catch (Exception) { }
    }
}
