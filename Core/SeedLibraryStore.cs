using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// SeedLibraryStore — registry of patched ROMs the launcher has produced.
//
// Each emulated game (e.g. Pokémon Emerald) generates a separate patched ROM per
// multiworld seed; those files accumulate under Games/ROMs/<gameId>/ and need a
// managed list so the player can see, attribute playtime to, and delete them.
//
// Storage: <AppDir>/Data/seed_library.json — written locally, no telemetry.
//
// IDENTITY: a SeedEntry is keyed by PatchedRomPath (the absolute .gba path). Two
// different generations can reuse a seed NUMBER but never the same patched-file
// path (the path embeds rom + patch name), so the path is the stable identity.
//
// THREAD SAFETY: all public methods are safe to call from any thread. Mutators
// take the collection lock, snapshot under it, then write the file under a
// dedicated save lock — mirroring AchievementStore's P2-21 discipline so two
// racing saves (a session end racing a fresh registration) can neither corrupt
// the JSON nor interleave WriteAllText.
// ═══════════════════════════════════════════════════════════════════════════════

/// One patched ROM in the library.
public sealed record SeedEntry
{
    [JsonPropertyName("gameId")]         public string GameId         { get; init; } = "";
    [JsonPropertyName("patchedRomPath")] public string PatchedRomPath { get; init; } = "";
    /// The .apemerald this ROM was applied from. May be gone later (the player
    /// can delete the patch independently) — kept only for reference.
    [JsonPropertyName("patchPath")]      public string? PatchPath     { get; init; }
    [JsonPropertyName("seedName")]       public string SeedName       { get; init; } = "unknown";
    [JsonPropertyName("slotName")]       public string SlotName       { get; init; } = "";
    [JsonPropertyName("createdUtc")]     public DateTimeOffset  CreatedUtc    { get; init; }
    [JsonPropertyName("lastPlayedUtc")]  public DateTimeOffset? LastPlayedUtc { get; init; }
    /// Accumulated wall-clock seconds played on THIS seed.
    [JsonPropertyName("playSeconds")]    public long PlaySeconds      { get; init; }
    /// "new" | "in_progress" | "complete".
    [JsonPropertyName("status")]         public string Status         { get; init; } = "new";
}

public sealed class SeedLibraryStore
{
    // ── Status constants ───────────────────────────────────────────────────────
    public const string StatusNew        = "new";
    public const string StatusInProgress = "in_progress";
    public const string StatusComplete   = "complete";

    // ── Paths ──────────────────────────────────────────────────────────────────
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "Data");
    private static string FilePath => Path.Combine(DataDir, "seed_library.json");

    // ── State ──────────────────────────────────────────────────────────────────
    // Keyed by PatchedRomPath (OrdinalIgnoreCase — Windows paths). Guarded by
    // _lock; Save() snapshots under it and writes under _saveLock.
    private readonly Dictionary<string, SeedEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock     = new();
    private readonly object _saveLock = new();

    // ── Singleton ──────────────────────────────────────────────────────────────
    public static SeedLibraryStore Instance { get; } = new();
    private SeedLibraryStore() => Load();

    // ── Load / save ────────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var list = JsonSerializer.Deserialize<List<SeedEntry>>(
                File.ReadAllText(FilePath));
            if (list == null) return;
            lock (_lock)
            {
                _entries.Clear();
                foreach (var e in list)
                    if (!string.IsNullOrEmpty(e.PatchedRomPath))
                        _entries[e.PatchedRomPath] = e;
            }
        }
        catch { /* a corrupt registry starts empty rather than crashing the app */ }
    }

    private void Save()
    {
        try
        {
            List<SeedEntry> snapshot;
            lock (_lock) snapshot = _entries.Values.ToList();

            string json = JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { WriteIndented = true });

            lock (_saveLock)
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(FilePath, json);
            }
        }
        catch { /* non-fatal */ }
    }

    // ── Mutators ───────────────────────────────────────────────────────────────

    /// Register (or refresh) a patched ROM. New rows start "new"; an existing
    /// row keeps its status (never downgraded — a "complete"/"in_progress" seed
    /// re-launched stays where it was) and its play accounting, but refreshes
    /// the seed/slot/patch metadata in case the resolver picked differently.
    public void Register(SeedEntry entry)
    {
        if (string.IsNullOrEmpty(entry.PatchedRomPath)) return;

        lock (_lock)
        {
            if (_entries.TryGetValue(entry.PatchedRomPath, out var existing))
            {
                _entries[entry.PatchedRomPath] = existing with
                {
                    GameId    = string.IsNullOrEmpty(entry.GameId) ? existing.GameId : entry.GameId,
                    PatchPath = entry.PatchPath ?? existing.PatchPath,
                    SeedName  = string.IsNullOrEmpty(entry.SeedName) || entry.SeedName == "unknown"
                                    ? existing.SeedName : entry.SeedName,
                    SlotName  = string.IsNullOrEmpty(entry.SlotName) ? existing.SlotName : entry.SlotName,
                    // CreatedUtc, LastPlayedUtc, PlaySeconds, Status: preserved.
                };
            }
            else
            {
                _entries[entry.PatchedRomPath] = entry with
                {
                    CreatedUtc = entry.CreatedUtc == default ? DateTimeOffset.UtcNow : entry.CreatedUtc,
                    Status     = StatusNew,
                };
            }
        }
        Save();
    }

    /// Record a finished play session for this seed: stamp LastPlayed=now, add
    /// the seconds, and promote "new" → "in_progress". "complete" is left alone.
    public void MarkPlayed(string gameId, string romPath, long sessionSeconds)
    {
        if (string.IsNullOrEmpty(romPath)) return;

        lock (_lock)
        {
            if (!_entries.TryGetValue(romPath, out var e)) return;
            _entries[romPath] = e with
            {
                LastPlayedUtc = DateTimeOffset.UtcNow,
                PlaySeconds   = e.PlaySeconds + (sessionSeconds > 0 ? sessionSeconds : 0),
                Status        = e.Status == StatusComplete ? StatusComplete : StatusInProgress,
            };
        }
        Save();
    }

    /// Mark this seed's goal reached.
    public void MarkComplete(string gameId, string romPath)
    {
        if (string.IsNullOrEmpty(romPath)) return;

        lock (_lock)
        {
            if (!_entries.TryGetValue(romPath, out var e)) return;
            if (e.Status == StatusComplete) return;
            _entries[romPath] = e with { Status = StatusComplete };
        }
        Save();
    }

    /// Drop the registry row for a patched ROM. File deletion is the caller's
    /// responsibility (see EmulatorPlugin.DeletePatchedRom).
    public void Remove(string gameId, string romPath)
    {
        if (string.IsNullOrEmpty(romPath)) return;
        bool removed;
        lock (_lock) removed = _entries.Remove(romPath);
        if (removed) Save();
    }

    // ── Queries ────────────────────────────────────────────────────────────────

    /// Every seed for one game, newest first (last played, falling back to
    /// created date for never-played seeds).
    public IReadOnlyList<SeedEntry> GetForGame(string gameId)
    {
        lock (_lock)
            return _entries.Values
                .Where(e => string.Equals(e.GameId, gameId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.LastPlayedUtc ?? e.CreatedUtc)
                .ToList();
    }
}
