using System;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// HintEntry — one structured hint received from the AP server.
//
// Hints arrive as PrintJSON packets with type = "Hint".
// The server message looks like:
//   "ReceiverName's ItemName is at LocationName in SenderName's world."
//
// We parse the data array parts (player_id, item_id, location_id) so we can
// show the hint in a rich UI with item classification colours.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class HintEntry
{
    public DateTimeOffset Timestamp    { get; init; } = DateTimeOffset.Now;

    /// Name of the player whose inventory this item belongs to.
    public string ReceiverName { get; init; } = "";

    /// Human-readable AP item name.
    public string ItemName     { get; init; } = "";

    /// Name of the player whose world contains this location.
    public string SenderName   { get; init; } = "";

    /// Location name where the item can be found.
    public string LocationName { get; init; } = "";

    /// True once the hinted location check is completed. Mutable — the data
    /// storage hint backlog (SetReply) flips this live when the finder checks it.
    public bool   IsChecked    { get; set; }

    /// AP item flags bitmask (progression=1, useful=2, trap=4).
    public int    ItemFlags    { get; init; }

    /// Full plain-text version of the hint for fallback display.
    public string RawText      { get; init; } = "";

    /// True if WE are the receiver (this is an item we still need).
    public bool   IsForMe      { get; init; }

    /// True if the item is located in OUR world (we need to find it for someone).
    public bool   IsOurs       { get; init; }

    // ── Numeric identity (data-storage hint backlog only) ─────────────────────
    // PrintJSON "Hint" packets carry display names, not slot/location numbers,
    // so entries parsed from them leave these at 0. The hint backlog fetched
    // via the data storage API ("_read_hints_{team}_{slot}") back-fills them,
    // which is what enables the UpdateHint priority buttons.

    /// Slot whose world contains the hinted location (0 = unknown).
    public int    FindingSlot  { get; set; }

    /// AP location ID of the hinted location (0 = unknown).
    public long   LocationId   { get; set; }

    /// AP HintStatus: 0 unspecified, 10 no-priority, 20 avoid, 30 priority,
    /// 40 found (server-set). Mutable — updated via UpdateHint + SetReply.
    public int    Status       { get; set; }

    // ── AP item classification ─────────────────────────────────────────────────
    public bool IsProgression  => (ItemFlags & 0b001) != 0;
    public bool IsUseful       => (ItemFlags & 0b010) != 0;
    public bool IsTrap         => (ItemFlags & 0b100) != 0;
}
