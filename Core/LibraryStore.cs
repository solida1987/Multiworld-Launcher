using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LauncherV2.Core;

// LibraryStore — tracks which games the user has added to their library
// and which are marked as favorites.

// Storage: <AppDir>/Data/library.json

// LIBRARY vs REGISTRY
// GameRegistry holds the integrations that are compiled in.
// LibraryStore is the USER's curated list:
// • Any plugin can be removed from view ("Remove from library").
// • Any catalog entry can be pinned even without a plugin yet.
// • Favorites appear at the top of the sidebar.

// On first run, LibraryStore is seeded with all registered plugin IDs so the
// sidebar is not empty out-of-the-box.

public sealed class LibraryEntry
{
    [JsonPropertyName("game_id")]     public string           GameId     { get; set; } = "";
    [JsonPropertyName("is_favorite")] public bool             IsFavorite { get; set; }
    [JsonPropertyName("added_at")]    public DateTimeOffset   AddedAt    { get; set; }
    /// When Play was last pressed for this game. Null = never played through
    /// London. Old library files simply lack the field and read back as null.
    [JsonPropertyName("last_played")] public DateTimeOffset?  LastPlayed { get; set; }
    /// The user's own shelf for this game ("RPGs", "With the kids", ...).
    /// Null = not filed anywhere. Free text, owned entirely by the player.
    [JsonPropertyName("folder")]      public string?          Folder     { get; set; }
}

public static class LibraryStore
{
    private static readonly string _path =
        Path.Combine(AppContext.BaseDirectory, "Data", "library.json");

    private static readonly JsonSerializerOptions _opts =
        new() { WriteIndented = true };

    private static List<LibraryEntry> _entries = new();

    // --- Initialization ---

    static LibraryStore() => Load();

    private static void Load()
    {
        try
        {
            if (File.Exists(_path))
                _entries = JsonSerializer.Deserialize<List<LibraryEntry>>(
                    File.ReadAllText(_path), _opts) ?? new();
        }
        catch { _entries = new(); }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Atomic write (temp + swap) so a crash mid-write can't truncate the
            // library file and silently wipe the user's game list / order.
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, _opts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* non-fatal */ }
    }

    // --- Seeding ---

    //
    // Ensures every supplied gameId appears in the library.
    // Call on startup with GameRegistry.All.Select(p => p.GameId) so the
    // sidebar is not empty on first launch.
    // New entries are inserted at the front (pre-existing order preserved).
    //
    public static void SeedPlugins(IEnumerable<string> gameIds)
    {
        bool changed = false;
        // Insert in REVERSE so the first element ends up at index 0
        foreach (var id in gameIds.Reverse())
        {
            if (!IsInLibrary(id))
            {
                _entries.Insert(0, new LibraryEntry
                {
                    GameId  = id,
                    AddedAt = DateTimeOffset.UtcNow,
                });
                changed = true;
            }
        }
        if (changed) Save();
    }

    // --- Queries ---

    public static bool IsInLibrary(string gameId)
        => _entries.Any(e => e.GameId == gameId);

    public static bool IsFavorite(string gameId)
        => _entries.FirstOrDefault(e => e.GameId == gameId)?.IsFavorite ?? false;

    // Returns game IDs ordered: favorites first (in list order), then non-favorites (in list order).
    // List order is preserved as-is, respecting any drag-to-reorder the user has done.
    public static IReadOnlyList<string> GetSortedGameIds()
    {
        var favorites    = _entries.Where(e => e.IsFavorite).Select(e => e.GameId);
        var nonFavorites = _entries.Where(e => !e.IsFavorite).Select(e => e.GameId);
        return favorites.Concat(nonFavorites).ToList();
    }

    // --- Mutations ---

    // Add a game to the library (no-op if already present).
    public static void Add(string gameId)
    {
        if (IsInLibrary(gameId)) return;
        _entries.Add(new LibraryEntry
        {
            GameId  = gameId,
            AddedAt = DateTimeOffset.UtcNow,
        });
        Save();
    }

    // Remove a game from the library.
    public static void Remove(string gameId)
    {
        _entries.RemoveAll(e => e.GameId == gameId);
        Save();
    }

    // Returns the raw list order (game IDs in their stored sequence, without
    // the favorites-first resorting that GetSortedGameIds applies).
    // take a snapshot before a drag-reorder so it can be undone.
    public static IReadOnlyList<string> GetRawOrder()
        => _entries.Select(e => e.GameId).ToList();

    // Restore a previously captured order snapshot (e.g.
    // IDs not currently in the library are silently ignored.
    public static void RestoreOrder(IReadOnlyList<string> order)
    {
        var map = _entries.ToDictionary(e => e.GameId, e => e);
        var restored = new List<LibraryEntry>();
        foreach (var id in order)
            if (map.TryGetValue(id, out var entry)) { restored.Add(entry); map.Remove(id); }
        // Any entries not in the snapshot (added after the snapshot) go at the end.
        restored.AddRange(map.Values);
        _entries = restored;
        Save();
    }

    // Move movedId to immediately before targetId in the list.
    // Reorders within the same favorites/non-favorites group.
    // No-op if either ID is missing or they are the same entry.
    public static void MoveBeforeId(string movedId, string targetId)
    {
        var moved  = _entries.FirstOrDefault(e => e.GameId == movedId);
        var target = _entries.FirstOrDefault(e => e.GameId == targetId);
        if (moved == null || target == null || moved == target) return;

        _entries.Remove(moved);
        int idx = _entries.IndexOf(target);
        _entries.Insert(Math.Max(0, idx), moved);
        Save();
    }

    /// Stamp "played now". Called from the Play button and nowhere else, so
    /// the sidebar's Last played sort reflects launches, not browsing.
    public static void TouchPlayed(string gameId)
    {
        var entry = _entries.FirstOrDefault(e => e.GameId == gameId);
        if (entry == null) return;
        entry.LastPlayed = DateTimeOffset.UtcNow;
        Save();
    }

    public static DateTimeOffset? GetLastPlayed(string gameId)
        => _entries.FirstOrDefault(e => e.GameId == gameId)?.LastPlayed;

    public static DateTimeOffset GetAddedAt(string gameId)
        => _entries.FirstOrDefault(e => e.GameId == gameId)?.AddedAt ?? DateTimeOffset.MinValue;

    public static string? GetFolder(string gameId)
        => _entries.FirstOrDefault(e => e.GameId == gameId)?.Folder;

    /// File a game on a shelf; null takes it off again. Unknown ids are a
    /// quiet no-op -- the menu can only offer games that exist.
    public static void SetFolder(string gameId, string? folder)
    {
        var entry = _entries.FirstOrDefault(e => e.GameId == gameId);
        if (entry == null) return;
        entry.Folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        Save();
    }

    /// Every shelf that currently holds at least one game, alphabetically.
    /// Shelves have no life of their own -- empty ones simply stop existing,
    /// so there is nothing to manage and nothing to leak.
    public static IReadOnlyList<string> AllFolders()
        => _entries.Where(e => !string.IsNullOrWhiteSpace(e.Folder))
                   .Select(e => e.Folder!)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                   .ToList();

    // Toggle or set the favorite flag for a game.
    // If the game is not in the library it is added first.
    public static void SetFavorite(string gameId, bool value)
    {
        var entry = _entries.FirstOrDefault(e => e.GameId == gameId);
        if (entry == null)
        {
            Add(gameId);
            entry = _entries.Last();
        }
        entry.IsFavorite = value;
        Save();
    }
}
