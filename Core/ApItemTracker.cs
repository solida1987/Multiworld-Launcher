using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// ApItemTracker — real-time item history for the session.
//
// WHAT IT DOES
// ────────────
// Records every item sent and received during the AP session with full
// metadata (item name, sender, receiver, location, timestamp). Exposes
// filtered views so the UI can show:
//
//   • "All items"         — everything that happened in the multiworld
//   • "What I received"   — items sent to my slot from any player
//   • "What I sent"       — items I found that went to other players
//   • "Player X"          — everything involving a specific player slot
//   • "From player X"     — only items I found for player X
//   • Search by item name — free-text filter across all columns
//
// ITEM NAME RESOLUTION
// ────────────────────
// AP sends numeric item IDs. The tracker resolves these to human-readable
// names using the AP DataPackage (game → item_name_to_id mapping).
// ApClient requests the DataPackage right after Connected; the tracker
// populates its lookup tables when it arrives.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class TrackedItem
{
    /// Sequential index within this session (1-based for display).
    public int      Index        { get; init; }
    /// AP item ID (numeric).
    public long     ItemId       { get; init; }
    /// Resolved item name, e.g. "Progressive Sword".
    public string   ItemName     { get; set; } = "";
    /// AP location ID where this item was sitting.
    public long     LocationId   { get; init; }
    /// Resolved location name, e.g. "Hyrule Castle - Big Key Chest".
    public string   LocationName { get; set; } = "";
    /// Slot number of the player who SENT this item (found it in their world).
    public int      SenderSlot   { get; init; }
    /// Display name of the sender, e.g. "Marco".
    public string   SenderName   { get; set; } = "";
    /// Slot number of the player who RECEIVES this item.
    public int      ReceiverSlot { get; init; }
    /// Display name of the receiver.
    public string   ReceiverName { get; set; } = "";
    /// When this entry was recorded.
    public DateTimeOffset Timestamp { get; init; }
    /// True = item goes to my slot (the local player).
    public bool     IsForMe      { get; init; }
    /// True = I found this item (it goes to someone else or myself).
    public bool     IFoundIt     { get; init; }
    /// AP item classification flags (same bitmask as AP NetworkItem.flags).
    public int      ItemFlags    { get; init; }
    /// Human-readable classification: Progression / Useful / Trap / Filler.
    public string   TypeLabel    => (ItemFlags & 0b001) != 0 ? "Prog"
                                  : (ItemFlags & 0b010) != 0 ? "Useful"
                                  : (ItemFlags & 0b100) != 0 ? "Trap"
                                  : "Filler";
}

public sealed class ApItemTracker
{
    private readonly object _lock = new();
    private readonly List<TrackedItem> _all = new();
    private int _nextIndex = 1;   // monotonic display index; survives trimming
    private int _mySlot;
    private IReadOnlyList<ApNetworkPlayer> _players = Array.Empty<ApNetworkPlayer>();

    // Per-game item ID → name lookup (from DataPackage)
    private readonly Dictionary<long, string>   _itemNames     = new();
    private readonly Dictionary<long, string>   _locationNames = new();

    // ── Observable collection for UI binding ─────────────────────────────────

    /// Full unfiltered history — bind UI lists to ApplyFilter() result, not this.
    public IReadOnlyList<TrackedItem> All
    {
        get { lock (_lock) return _all.ToList(); }
    }

    // ── Events ────────────────────────────────────────────────────────────────

    /// Fires ONCE per RecordItems call with every entry that call added, on
    /// the thread that recorded them (marshal to UI). Batch-level by design
    /// (P2-17): a per-item event made a 1,000-item AP catch-up packet trigger
    /// 1,000 synchronous full UI refreshes — O(N²) collection churn with the
    /// receive loop blocked on each dispatcher hop. Raised outside the lock.
    public event Action<IReadOnlyList<TrackedItem>>? ItemsAdded;

    // ── Session setup ─────────────────────────────────────────────────────────

    /// Call when ApClient receives Connected. Sets my slot + player list.
    public void OnConnected(int mySlot, IReadOnlyList<ApNetworkPlayer> players)
    {
        _mySlot  = mySlot;
        _players = players;
    }

    /// Call when ApClient receives DataPackage. Populates name lookup tables.
    /// gameKey = the AP game name for the DataPackage game entry.
    public void OnDataPackage(string gameKey, JsonElement dataPackageElement)
    {
        lock (_lock)
        {
            // dataPackageElement is the value of games[gameKey] in the DataPackage
            if (dataPackageElement.TryGetProperty("item_name_to_id", out var itemMap))
                foreach (var kv in itemMap.EnumerateObject())
                    _itemNames[kv.Value.GetInt64()] = kv.Name;

            if (dataPackageElement.TryGetProperty("location_name_to_id", out var locMap))
                foreach (var kv in locMap.EnumerateObject())
                    _locationNames[kv.Value.GetInt64()] = kv.Name;

            // Back-fill any items already recorded without names
            foreach (var item in _all)
            {
                if (item.ItemName     == "" && _itemNames.TryGetValue(item.ItemId, out var iName))
                    item.ItemName = iName;
                if (item.LocationName == "" && _locationNames.TryGetValue(item.LocationId, out var lName))
                    item.LocationName = lName;
            }
        }
    }

    // ── Record incoming items ─────────────────────────────────────────────────

    /// Called by ApClient when ReceivedItems arrives.
    /// receiverSlot = the slot that RECEIVES the item (may not be mySlot —
    /// in text-only mode the server sends items for everyone to the log).
    public void RecordItems(ApNetworkItem[] items, int receiverSlot)
    {
        if (items.Length == 0) return;

        var added = new List<TrackedItem>(items.Length);   // pre-sized — no regrowth
        lock (_lock)
        {
            foreach (var raw in items)
            {
                var entry = new TrackedItem
                {
                    Index        = _nextIndex++,
                    ItemId       = raw.ItemId,
                    ItemName     = _itemNames.GetValueOrDefault(raw.ItemId, $"#{raw.ItemId}"),
                    LocationId   = raw.LocationId,
                    LocationName = _locationNames.GetValueOrDefault(raw.LocationId, $"#{raw.LocationId}"),
                    SenderSlot   = raw.Player,
                    SenderName   = ResolvePlayerName(raw.Player),
                    ReceiverSlot = receiverSlot,
                    ReceiverName = ResolvePlayerName(receiverSlot),
                    Timestamp    = DateTimeOffset.Now,
                    IsForMe      = receiverSlot == _mySlot,
                    IFoundIt     = raw.Player   == _mySlot,
                    ItemFlags    = raw.Flags,
                };
                _all.Add(entry);
                added.Add(entry);
            }
            if (_all.Count > LauncherConstants.ItemTrackerMaxEntries)
                _all.RemoveRange(0, _all.Count - LauncherConstants.ItemTrackerMaxEntries);
        }

        // One event per batch, raised OUTSIDE the lock — a handler hopping to
        // the UI thread must never hold up (or deadlock against) other readers.
        ItemsAdded?.Invoke(added);
    }

    // ── Filter API (for UI filter dropdowns) ─────────────────────────────────

    public enum FilterMode
    {
        All,            // everything
        ReceivedByMe,   // items sent to my slot
        SentByMe,       // items I found (going to any slot)
        ByPlayer,       // everything involving a specific player
        ReceivedByPlayer,  // items a specific player received
        SentByPlayer,   // items a specific player sent
    }

    /// Apply a filter and optional text search to the full history.
    /// Caller provides playerSlot for ByPlayer / ReceivedByPlayer / SentByPlayer.
    /// textSearch: matched against ItemName, LocationName, SenderName, ReceiverName.
    public IReadOnlyList<TrackedItem> ApplyFilter(
        FilterMode mode,
        int playerSlot = 0,
        string textSearch = "")
    {
        lock (_lock)
        {
            IEnumerable<TrackedItem> result = _all;

            result = mode switch
            {
                FilterMode.ReceivedByMe     => result.Where(i => i.ReceiverSlot == _mySlot),
                FilterMode.SentByMe         => result.Where(i => i.SenderSlot   == _mySlot),
                FilterMode.ByPlayer         => result.Where(i => i.ReceiverSlot == playerSlot || i.SenderSlot == playerSlot),
                FilterMode.ReceivedByPlayer => result.Where(i => i.ReceiverSlot == playerSlot),
                FilterMode.SentByPlayer     => result.Where(i => i.SenderSlot   == playerSlot),
                _                           => result  // FilterMode.All
            };

            if (!string.IsNullOrWhiteSpace(textSearch))
            {
                string q = textSearch.ToLowerInvariant();
                result = result.Where(i =>
                    i.ItemName.ToLowerInvariant().Contains(q)     ||
                    i.LocationName.ToLowerInvariant().Contains(q)  ||
                    i.SenderName.ToLowerInvariant().Contains(q)   ||
                    i.ReceiverName.ToLowerInvariant().Contains(q));
            }

            return result.ToList();
        }
    }

    /// All players in the session — for populating the "filter by player" dropdown.
    public IReadOnlyList<ApNetworkPlayer> Players => _players;

    // ── Clear on session end ──────────────────────────────────────────────────

    public void Clear()
    {
        lock (_lock)
        {
            _all.Clear();
            _nextIndex = 1;
            _itemNames.Clear();
            _locationNames.Clear();
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private string ResolvePlayerName(int slot)
        => _players.FirstOrDefault(p => p.Slot == slot)?.Alias ?? $"Slot {slot}";
}
