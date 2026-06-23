using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// LocationTracker — tracks which Archipelago location checks are done.
//
// SETUP FLOW (called from MainWindow event handlers):
//   1. OnConnected   — after Connected packet: builds the ID→checked map
//   2. OnDataPackage — after DataPackage for our game: resolves names + groups
//   3. OnLocationInfo — after LocationScouts reply: enriches unchecked items
//   4. OnLocationsChecked — when our plugin finds items or server sends RoomUpdate
//
// CATEGORIES
// ──────────
// 1. Use DataPackage's location_name_groups if present.
// 2. Fall back to "Prefix: Rest" or "Prefix - Rest" name patterns.
// 3. Anything unclassified goes into "Other".
// ═══════════════════════════════════════════════════════════════════════════════

/// One location / check slot in the tracker.
public sealed class LocationEntry
{
    public long   LocationId   { get; set; }
    public string LocationName { get; set; } = "";
    public string ItemName     { get; set; } = "";      // populated after LocationScouts
    public string ReceiverName { get; set; } = "";      // whose item it is
    public int    ItemFlags    { get; set; }
    public bool   IsChecked    { get; set; }

    // AP item classification from flags bitmask
    public bool IsProgression  => (ItemFlags & 0b001) != 0;
    public bool IsUseful       => (ItemFlags & 0b010) != 0;
    public bool IsTrap         => (ItemFlags & 0b100) != 0;
    public bool IsScouted      => ItemName.Length > 0;
}

/// A named group of locations shown as one progress card (e.g. "Skills", "Zones").
public sealed class LocationCategory
{
    public string              Name    { get; }
    public List<LocationEntry> Entries { get; } = new();

    public int    Total      => Entries.Count;
    public int    Checked    => Entries.Count(e => e.IsChecked);
    public double Progress   => Total == 0 ? 0.0 : (double)Checked / Total;
    public bool   IsComplete => Total > 0 && Checked == Total;

    public LocationCategory(string name) => Name = name;
}

public sealed class LocationTracker
{
    // ── Internal state ────────────────────────────────────────────────────────
    // Every collection below is guarded by _sync (P2-1): the AP receive thread
    // mutates them (OnConnected/OnDataPackage/OnLocationInfo/OnLocationsChecked)
    // while the UI thread enumerates via GetCategories()/GetAll()/the counters —
    // unguarded, that intermittently threw "collection was modified" into the
    // global crash dialog. Queries return snapshots built under the lock.
    // Changed is ALWAYS raised AFTER the lock is released: handlers marshal to
    // the dispatcher, and the UI thread may itself be waiting on _sync.
    private readonly object _sync = new();
    private readonly Dictionary<long,   LocationEntry> _entries     = new();
    private readonly Dictionary<long,   string>        _locNames    = new();
    private readonly Dictionary<long,   string>        _itemNames   = new();
    private readonly Dictionary<int,    string>        _playerNames = new();
    private readonly Dictionary<string, string>        _nameToGroup = new(); // locName → group

    // ── Public aggregates ─────────────────────────────────────────────────────
    public int Total   { get { lock (_sync) return _entries.Count; } }
    public int Checked { get { lock (_sync) return _entries.Values.Count(e => e.IsChecked); } }
    public int Missing
    {
        get
        {
            lock (_sync)
                return _entries.Count - _entries.Values.Count(e => e.IsChecked);
        }
    }

    /// Fires on any change — caller must marshal to UI thread if needed.
    /// Raised outside the internal lock (handlers may block on the dispatcher).
    public event Action? Changed;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// Call after the AP Connected packet.
    public void OnConnected(
        IEnumerable<long>              checkedIds,
        IEnumerable<long>              missingIds,
        IReadOnlyList<ApNetworkPlayer> players)
    {
        lock (_sync)
        {
            _entries.Clear();
            _playerNames.Clear();

            foreach (var p in players)
                _playerNames[p.Slot] = p.Alias.Length > 0 ? p.Alias : p.Name;

            foreach (var id in checkedIds) GetOrAdd(id).IsChecked = true;
            foreach (var id in missingIds) GetOrAdd(id).IsChecked = false;

            ApplyNames();
        }
        Changed?.Invoke();
    }

    /// Call with each DataPackage entry — populates name tables and groups.
    public void OnDataPackage(string game, JsonElement data)
    {
        lock (_sync)
        {
            if (data.TryGetProperty("location_name_to_id", out var locs))
                foreach (var kv in locs.EnumerateObject())
                    if (kv.Value.TryGetInt64(out var id))
                        _locNames[id] = kv.Name;

            if (data.TryGetProperty("item_name_to_id", out var items))
                foreach (var kv in items.EnumerateObject())
                    if (kv.Value.TryGetInt64(out var id))
                        _itemNames[id] = kv.Name;

            // location_name_groups: developer-defined category groupings
            // Format: { "GroupName": ["Loc A", "Loc B", …] }
            if (data.TryGetProperty("location_name_groups", out var groups))
                foreach (var grp in groups.EnumerateObject())
                    foreach (var nameEl in grp.Value.EnumerateArray())
                    {
                        string? n = nameEl.GetString();
                        if (n != null) _nameToGroup[n] = grp.Name;
                    }

            ApplyNames();
        }
        Changed?.Invoke();
    }

    /// Call after LocationScouts reply — fills in scouted item names and flags.
    public void OnLocationInfo(ApNetworkItem[] items)
    {
        lock (_sync)
        {
            foreach (var item in items)
            {
                if (!_entries.TryGetValue(item.LocationId, out var entry)) continue;
                entry.ItemName     = _itemNames.GetValueOrDefault(item.ItemId, $"Item #{item.ItemId}");
                entry.ItemFlags    = item.Flags;
                entry.ReceiverName = _playerNames.GetValueOrDefault(item.Player, $"Slot {item.Player}");
            }
        }
        Changed?.Invoke();
    }

    /// Mark location IDs as checked (from our plugin checks or server RoomUpdate).
    /// <paramref name="addUnknown"/> = true (standalone / no AP server, so the
    /// full location universe was never delivered by OnConnected) adds any
    /// unseen id as a freshly-checked entry, so a solo run's checks still show
    /// up in the tracker instead of being dropped.
    public void OnLocationsChecked(IEnumerable<long> ids, bool addUnknown = false)
    {
        bool any = false;
        lock (_sync)
        {
            // When the location table is loaded (standalone DataPackage / AP), an
            // id that isn't in it is a phantom — e.g. the mod's MISSING/CHECK
            // enumeration emits base+qid+diff*1000 combinations that don't map to a
            // real location (level-milestone ids bake difficulty into the qid). Drop
            // those instead of showing "#42282" rows. Only filters NEW ids; ids
            // already tracked (added by the computed universe) always update.
            bool haveTable = _locNames.Count > 0;
            foreach (var id in ids)
            {
                if (_entries.TryGetValue(id, out var e))
                {
                    if (!e.IsChecked) { e.IsChecked = true; any = true; }
                }
                else if (addUnknown && (!haveTable || _locNames.ContainsKey(id)))
                {
                    GetOrAdd(id).IsChecked = true;
                    any = true;
                }
            }
            if (addUnknown && any) ApplyNames();
        }
        if (any) Changed?.Invoke();
    }

    /// Add location IDs as "missing" (unchecked) without disturbing entries that
    /// already exist — used in STANDALONE, where the game streams its full active
    /// location universe (MISSING:) separately from the checks (CHECK:), in any
    /// order. Gives the tracker per-category totals + the unchecked list, like an
    /// AP session's OnConnected, but merge-friendly.
    public void OnMissingLocations(IEnumerable<long> ids)
    {
        bool any = false;
        lock (_sync)
        {
            // Same phantom guard as OnLocationsChecked: only add ids the table
            // knows about, so a buggy/over-eager MISSING: stream can't inject
            // nameless "#<id>" rows. (The launcher-computed universe is built FROM
            // the table, so it passes cleanly.)
            bool haveTable = _locNames.Count > 0;
            foreach (var id in ids)
            {
                if (haveTable && !_locNames.ContainsKey(id)) continue;
                if (!_entries.ContainsKey(id)) { GetOrAdd(id); any = true; }
            }
            if (any) ApplyNames();
        }
        if (any) Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _locNames.Clear();
            _itemNames.Clear();
            _playerNames.Clear();
            _nameToGroup.Clear();
        }
        Changed?.Invoke();
    }

    // ── Queries (snapshots — safe to enumerate on any thread) ─────────────────

    /// All unchecked location IDs — pass to LocationScoutsAsync.
    public long[] GetMissingIds()
    {
        lock (_sync)
            return _entries.Values.Where(e => !e.IsChecked)
                                  .Select(e => e.LocationId).ToArray();
    }

    /// All entries sorted by name.
    public IReadOnlyList<LocationEntry> GetAll()
    {
        lock (_sync)
            return _entries.Values
                .OrderBy(e => e.LocationName, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// Entries grouped into named categories, ordered largest-first then alphabetically.
    /// The "Other" bucket always goes last. The returned category/list structure
    /// is a snapshot; the LocationEntry objects stay live (scalar fields may
    /// update underneath, which is fine for display).
    public IReadOnlyList<LocationCategory> GetCategories()
    {
        lock (_sync)
        {
            var cats = new Dictionary<string, LocationCategory>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in _entries.Values)
            {
                string catName = InferCategory(e.LocationName);
                if (!cats.TryGetValue(catName, out var cat))
                    cats[catName] = cat = new LocationCategory(catName);
                cat.Entries.Add(e);
            }

            return cats.Values
                .OrderBy(c => c.Name.Equals("Other", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(c => c.Total)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private LocationEntry GetOrAdd(long id)
    {
        if (!_entries.TryGetValue(id, out var e))
            _entries[id] = e = new LocationEntry { LocationId = id };
        return e;
    }

    private void ApplyNames()
    {
        foreach (var e in _entries.Values)
        {
            if (_locNames.TryGetValue(e.LocationId, out var name))
                e.LocationName = name;
            else if (e.LocationName.Length == 0)
                e.LocationName = $"#{e.LocationId}";
        }
    }

    private string InferCategory(string locName)
    {
        // 1. Explicit group from DataPackage (highest priority)
        if (_nameToGroup.TryGetValue(locName, out var group)) return group;

        // 2. "Category: Description" pattern
        int colon = locName.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && colon < 32) return locName[..colon].Trim();

        // 3. "Category - Description" pattern
        int dash = locName.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && dash < 32) return locName[..dash].Trim();

        return "Other";
    }
}
