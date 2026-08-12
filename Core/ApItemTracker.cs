using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// ApItemTracker — real-time item history for the session.

// WHAT IT DOES
// Records every item sent and received during the AP session with full
// metadata (item name, sender, receiver, location, timestamp).
// filtered views so the UI can show:

// • "All items" — everything that happened in the multiworld
// • "What I received" — items sent to my slot from any player
// • "What I sent" — items I found that went to other players
// • "Player X" — everything involving a specific player slot
// • "From player X" — only items I found for player X
// • Search by item name — free-text filter across all columns

// ITEM NAME RESOLUTION
// AP sends numeric item IDs.
// names using the AP DataPackage (game → item_name_to_id mapping).
// ApClient requests the DataPackage right after Connected; the tracker
// populates its lookup tables when it arrives.

public sealed class TrackedItem
{
    // Sequential index within this session (1-based for display).
    public int      Index        { get; init; }
    // AP item ID (numeric).
    public long     ItemId       { get; init; }
    // Resolved item name, e.g.
    public string   ItemName     { get; set; } = "";
    // AP location ID where this item was sitting.
    public long     LocationId   { get; init; }
    // Resolved location name, e.g.
    public string   LocationName { get; set; } = "";
    // Slot number of the player who SENT this item (found it in their world).
    public int      SenderSlot   { get; init; }
    // Display name of the sender, e.g.
    public string   SenderName   { get; set; } = "";
    // Slot number of the player who RECEIVES this item.
    public int      ReceiverSlot { get; init; }
    // Display name of the receiver.
    public string   ReceiverName { get; set; } = "";
    // When this entry was recorded.
    public DateTimeOffset Timestamp { get; init; }
    // True = item goes to my slot (the local player).
    public bool     IsForMe      { get; init; }
    // True = I found this item (it goes to someone else or myself).
    public bool     IFoundIt     { get; init; }
    // AP item classification flags (same bitmask as AP NetworkItem.flags).
    public int      ItemFlags    { get; init; }
    // Human-readable classification: Progression / Useful / Trap / Filler.
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
    // identity of every AP item already recorded (locationId, sender, receiver),
    // so the server's full-history replay on reconnect and the D2 plugin's resyncs
    // can't list the same item several times in the Received tab.
    private readonly HashSet<(long, int, int, long)> _seenKeys = new();
    private int _mySlot;
    private IReadOnlyList<ApNetworkPlayer> _players = Array.Empty<ApNetworkPlayer>();

    // Flat item/location ID → name lookup for OUR OWN game (fallback path).
    private readonly Dictionary<long, string>   _itemNames     = new();
    private readonly Dictionary<long, string>   _locationNames = new();

    // Cross-game name resolution.
    // GAME: an item id must be resolved through the RECEIVER's game and a
    // location id through the SENDER's game, or a 300-game async renders
    // "Item #6952" rows. Keyed by game name; slot→game from the player list.
    private readonly Dictionary<string, Dictionary<long, string>> _gameItemNames =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<long, string>> _gameLocNames  =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _slotGames = new();

    private string ResolveItemName(long itemId, int receiverSlot)
    {
        if (_slotGames.TryGetValue(receiverSlot, out var g) &&
            _gameItemNames.TryGetValue(g, out var map) &&
            map.TryGetValue(itemId, out var n)) return n;
        return _itemNames.GetValueOrDefault(itemId, $"#{itemId}");
    }

    private string ResolveLocationName(long locId, int senderSlot)
    {
        if (_slotGames.TryGetValue(senderSlot, out var g) &&
            _gameLocNames.TryGetValue(g, out var map) &&
            map.TryGetValue(locId, out var n)) return n;
        return _locationNames.GetValueOrDefault(locId, $"#{locId}");
    }

    // --- Observable collection for UI binding ---

    // Full unfiltered history — bind UI lists to ApplyFilter() result, not this.
    public IReadOnlyList<TrackedItem> All
    {
        get { lock (_lock) return _all.ToList(); }
    }

    // --- Events ---

    // Fires ONCE per RecordItems call with every entry that call added, on
    // the thread that recorded them (marshal to UI).
    // (P2-17): a per-item event made a 1,000-item AP catch-up packet trigger
    // 1,000 synchronous full UI refreshes — O(N²) collection churn with the
    // receive loop blocked on each dispatcher hop.
    public event Action<IReadOnlyList<TrackedItem>>? ItemsAdded;

    // --- Session setup ---

    // Call when ApClient receives Connected.
    public void OnConnected(int mySlot, IReadOnlyList<ApNetworkPlayer> players)
    {
        _mySlot  = mySlot;
        UpdatePlayers(players);
    }

    // Refresh the player list mid-session (RoomUpdate "players" — joins,
    // alias changes). Unlike OnConnected this is safe to call at any time:
    // it also back-fills "Slot N" placeholder names on already-recorded
    // entries so late joiners get their alias retroactively.
    public void UpdatePlayers(IReadOnlyList<ApNetworkPlayer> players)
    {
        lock (_lock)
        {
            _players = players;
            foreach (var p in players)
                if (!string.IsNullOrEmpty(p.Game)) _slotGames[p.Slot] = p.Game;

            foreach (var item in _all)
            {
                if (item.SenderName.StartsWith("Slot ", StringComparison.Ordinal))
                    item.SenderName = ResolvePlayerName(item.SenderSlot);
                if (item.ReceiverName.StartsWith("Slot ", StringComparison.Ordinal))
                    item.ReceiverName = ResolvePlayerName(item.ReceiverSlot);
            }
        }
    }

    // Call when ApClient receives DataPackage.
    // gameKey = the AP game name for the DataPackage game entry.
    // isOwnGame: feed the flat fallback maps too (our game only — flat maps
    // would silently collide across games, ids are only unique per game).
    public void OnDataPackage(string gameKey, JsonElement dataPackageElement, bool isOwnGame = true)
    {
        lock (_lock)
        {
            var gi = _gameItemNames.TryGetValue(gameKey, out var egi) ? egi : (_gameItemNames[gameKey] = new());
            var gl = _gameLocNames.TryGetValue(gameKey, out var egl) ? egl : (_gameLocNames[gameKey]  = new());

            // dataPackageElement is the value of games[gameKey] in the DataPackage
            if (dataPackageElement.TryGetProperty("item_name_to_id", out var itemMap))
                foreach (var kv in itemMap.EnumerateObject())
                {
                    long id = kv.Value.GetInt64();
                    gi[id] = kv.Name;
                    if (isOwnGame) _itemNames[id] = kv.Name;
                }

            if (dataPackageElement.TryGetProperty("location_name_to_id", out var locMap))
                foreach (var kv in locMap.EnumerateObject())
                {
                    long id = kv.Value.GetInt64();
                    gl[id] = kv.Name;
                    if (isOwnGame) _locationNames[id] = kv.Name;
                }

            // Back-fill entries recorded before this game's names arrived —
            // both empty names and "#id" placeholders from Resolve* fallbacks.
            foreach (var item in _all)
            {
                if (item.ItemName == "" || item.ItemName.StartsWith("#", StringComparison.Ordinal))
                    item.ItemName = ResolveItemName(item.ItemId, item.ReceiverSlot);
                if (item.LocationName == "" || item.LocationName.StartsWith("#", StringComparison.Ordinal))
                    item.LocationName = ResolveLocationName(item.LocationId, item.SenderSlot);
            }
        }
    }

    // --- Record incoming items ---

    // Called by ApClient when ReceivedItems arrives.
    // receiverSlot = the slot that RECEIVES the item (may not be mySlot —
    // in text-only mode the server sends items for everyone to the log).
    public void RecordItems(ApNetworkItem[] items, int receiverSlot)
    {
        if (items.Length == 0) return;

        var added = new List<TrackedItem>(items.Length);   // pre-sized — no regrowth
        lock (_lock)
        {
            foreach (var raw in items)
            {
                // de-duplicate. The AP server replays the FULL item history on
                // every (re)connect, and the D2 plugin additionally issues up to three
                // resyncs per session, so the Received tab used to list the same items
                // 4-5 times over (cpuff).
                // originated items share a sentinel LocationId (-2 precollected,
                // -1 !getitem) with Player=0, so a location-only key collapsed all 6
                // precollected starting skills into one visible row.
                // included, replays still dedup (same item, same sentinel) while
                // distinct server-granted items stay distinct.
                var key = (raw.LocationId, raw.Player, receiverSlot, raw.ItemId);
                if (!_seenKeys.Add(key)) continue;

                var entry = new TrackedItem
                {
                    Index        = _nextIndex++,
                    ItemId       = raw.ItemId,
                    ItemName     = ResolveItemName(raw.ItemId, receiverSlot),
                    LocationId   = raw.LocationId,
                    LocationName = ResolveLocationName(raw.LocationId, raw.Player),
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
            TrimCapLocked();
        }

        // One event per batch, raised OUTSIDE the lock — a handler hopping to
        // the UI thread must never hold up (or deadlock against) other readers.
        ItemsAdded?.Invoke(added);
    }

    // Record a STANDALONE reward (no AP server).
    // reward as "&lt;location&gt;: &lt;reward&gt;" over the pipe; the caller splits it
    // and we append an entry attributed to the local player so it shows up in the
    // Received tab exactly like an AP item.
    // Standalone rewards the tracker has already seen — the game re-reports a
    // check's reward on reload, and (historically) the event handler could be
    // wired more than once; without this every relog duplicated the whole list.
    private readonly HashSet<(string, string)> _seenStandalone = new();

    public void RecordStandalone(string locationName, string rewardName)
    {
        TrackedItem entry;
        lock (_lock)
        {
            if (!_seenStandalone.Add((locationName, rewardName))) return;
            entry = new TrackedItem
            {
                Index        = _nextIndex++,
                ItemId       = 0,
                ItemName     = rewardName,
                LocationId   = 0,
                LocationName = locationName,
                SenderSlot   = _mySlot,
                SenderName   = "Me",
                ReceiverSlot = _mySlot,
                ReceiverName = "Me",
                Timestamp    = DateTimeOffset.Now,
                IsForMe      = true,
                IFoundIt     = true,
                ItemFlags    = 0,
            };
            _all.Add(entry);
            TrimCapLocked();
        }
        ItemsAdded?.Invoke(new[] { entry });
    }

    // Enforce the entry cap.
    // ItemSend traffic, and in a 300-game async foreign A→B rows alone can
    // blow past the cap — a plain oldest-first trim would then evict exactly
    // the rows the page exists for (our replayed Received + synthesized Sent
    // history sit at the OLD end right after connect).
    // rows first; our own rows only go when they alone exceed the cap.
    private void TrimCapLocked()
    {
        int over = _all.Count - LauncherConstants.ItemTrackerMaxEntries;
        if (over <= 0) return;

        var evict = new bool[_all.Count];
        int toEvict = over;
        for (int i = 0; i < _all.Count && toEvict > 0; i++)
            if (!_all[i].IsForMe && !_all[i].IFoundIt) { evict[i] = true; toEvict--; }
        for (int i = 0; i < _all.Count && toEvict > 0; i++)
            if (!evict[i]) { evict[i] = true; toEvict--; }

        var keep = new List<TrackedItem>(_all.Count - over);
        for (int i = 0; i < _all.Count; i++)
            if (!evict[i]) keep.Add(_all[i]);
        _all.Clear();
        _all.AddRange(keep);
    }

    // --- Filter API (for UI filter dropdowns) ---

    public enum FilterMode
    {
        All,            // everything
        ReceivedByMe,   // items sent to my slot
        SentByMe,       // items I found (going to any slot)
        ByPlayer,       // everything involving a specific player
        ReceivedByPlayer,  // items a specific player received
        SentByPlayer,   // items a specific player sent
    }

    // Apply a filter and optional text search to the full history.
    // Caller provides playerSlot for ByPlayer / ReceivedByPlayer / SentByPlayer.
    // textSearch: matched against ItemName, LocationName, SenderName, ReceiverName.
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

    // All players in the session — for populating the "filter by player" dropdown.
    public IReadOnlyList<ApNetworkPlayer> Players => _players;

    // --- Clear on session end ---

    public void Clear()
    {
        lock (_lock)
        {
            _all.Clear();
            _nextIndex = 1;
            _seenKeys.Clear();   // a new session must be able to list its items again
            _seenStandalone.Clear();
            _itemNames.Clear();
            _locationNames.Clear();
            _gameItemNames.Clear();
            _gameLocNames.Clear();
            _slotGames.Clear();
        }
    }

    // --- Private ---

    private string ResolvePlayerName(int slot)
        => _players.FirstOrDefault(p => p.Slot == slot)?.Alias ?? $"Slot {slot}";
}
