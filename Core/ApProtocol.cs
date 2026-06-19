using System.Text.Json;
using System.Text.Json.Serialization;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// ApProtocol — Archipelago WebSocket protocol message types.
//
// Reference: https://github.com/ArchipelagoMW/Archipelago/blob/main/docs/network%20protocol.md
//
// AP messages are always a JSON ARRAY of command objects:
//   [{"cmd": "RoomInfo", ...}, {"cmd": "PrintJSON", ...}]
//
// Inbound  = server → client  (we parse these)
// Outbound = client → server  (we serialize these)
// ═══════════════════════════════════════════════════════════════════════════════

// ── AP version triple ─────────────────────────────────────────────────────────

public sealed record ApVersion(
    [property: JsonPropertyName("major")] int Major,
    [property: JsonPropertyName("minor")] int Minor,
    [property: JsonPropertyName("build")] int Build)
{
    [JsonPropertyName("class")]
    public string Class => "Version";

    /// The version we claim when connecting. AP servers accept older clients
    /// if the protocol is compatible; 0.5.0 works with all current AP servers.
    public static ApVersion ClientVersion => new(0, 5, 0);
}

// ── items_handling bitmask ────────────────────────────────────────────────────

public static class ApItemsHandling
{
    /// Receive items found in OTHER players' worlds for our slot.
    public const int OtherWorlds    = 0b001;
    /// Receive items found in OUR world that go to other players.
    public const int OwnWorldSent   = 0b010;
    /// Receive items that were in our starting inventory.
    public const int StartInventory = 0b100;
    /// Receive everything — the correct value for a full AP client.
    public const int All            = 0b111;
}

// ── AP client status codes (sent via StatusUpdate) ────────────────────────────

public static class ApClientStatus
{
    public const int Unknown   = 0;
    public const int Connected = 5;   // socket open but not playing yet
    public const int Ready     = 10;  // ready to start
    public const int Playing   = 20;  // actively in-game
    public const int Goal      = 30;  // goal completed
}

/// Typed equivalent of the ApClientStatus codes, for ApClient.SetStatusAsync.
/// Intended lifecycle: Connected while sitting in the launcher → Ready on a
/// ready-check → Playing once the game process runs → Goal when the seed is won.
public enum ClientStatus
{
    Unknown   = 0,
    Connected = 5,
    Ready     = 10,
    Playing   = 20,
    Goal      = 30,
}

// ── NetworkPlayer (in Connected + RoomUpdate) ─────────────────────────────────

public sealed record ApNetworkPlayer(
    [property: JsonPropertyName("team")]  int    Team,
    [property: JsonPropertyName("slot")]  int    Slot,
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("game")]  string Game);

// ════════════════════════════════════════════════════════════════════════════════
// INBOUND commands  (server → client)
//
// REFERENCE ONLY: ApClient parses inbound packets field-by-field with
// JsonDocument (tolerates missing/extra fields on old/new servers) and does
// NOT deserialize into these records. They document the wire shape — keep
// them in sync with the protocol doc, but never assume code depends on them.
// ════════════════════════════════════════════════════════════════════════════════

/// First message sent by the server after the WebSocket handshake.
/// We respond with a Connect command.
/// NOTE: hint_cost is a PERCENTAGE of the slot's total location count, not an
/// absolute point price — see ApClient.HintCostPoints for the real cost.
public sealed record ApRoomInfo(
    [property: JsonPropertyName("version")]    ApVersion       Version,
    [property: JsonPropertyName("tags")]       string[]        Tags,
    [property: JsonPropertyName("password")]   bool            HasPassword,
    [property: JsonPropertyName("games")]      string[]        Games,
    [property: JsonPropertyName("hint_cost")]  int             HintCost,
    [property: JsonPropertyName("datapackage_checksums")]
    JsonElement DataPackageChecksums);

/// Sent when the Connect command succeeds.
public sealed record ApConnected(
    [property: JsonPropertyName("team")]              int               Team,
    [property: JsonPropertyName("slot")]              int               Slot,
    [property: JsonPropertyName("players")]           ApNetworkPlayer[] Players,
    [property: JsonPropertyName("missing_locations")] long[]            MissingLocations,
    [property: JsonPropertyName("checked_locations")]  long[]            CheckedLocations,
    [property: JsonPropertyName("slot_data")]         JsonElement       SlotData,
    [property: JsonPropertyName("slot_info")]         JsonElement       SlotInfo,
    [property: JsonPropertyName("hint_points")]       int               HintPoints);

/// Sent when Connect fails (wrong game name, bad password, etc.).
public sealed record ApConnectionRefused(
    [property: JsonPropertyName("errors")] string[] Errors);

/// Delivers items to our slot (on connect for missed items, and live as checks happen).
public sealed record ApReceivedItems(
    [property: JsonPropertyName("index")] int               Index,
    [property: JsonPropertyName("items")] ApNetworkItem[]   Items);

/// Chat message or game event notification (item sent, hint, etc.).
/// data is an array of styled text parts; we decode it to plain text for the log.
public sealed record ApPrintJson(
    [property: JsonPropertyName("data")]  JsonElement[] Data,
    [property: JsonPropertyName("type")]  string?       Type,
    [property: JsonPropertyName("item")]  JsonElement?  Item,
    [property: JsonPropertyName("found")] bool?         Found);

/// Relay of a Bounce packet to matching clients (by game / slot / tag).
/// DeathLink deaths arrive with tags = ["DeathLink"] and
/// data = { time: unix seconds (double), source: slot name, cause: text }.
public sealed record ApBounced(
    [property: JsonPropertyName("games")] string[]?   Games,
    [property: JsonPropertyName("slots")] int[]?      Slots,
    [property: JsonPropertyName("tags")]  string[]?   Tags,
    [property: JsonPropertyName("data")]  JsonElement Data);

/// Reply to a Get command — keys maps each requested data-storage key to its
/// stored value (e.g. "_read_hints_{team}_{slot}" → array of hint objects).
public sealed record ApRetrieved(
    [property: JsonPropertyName("keys")] JsonElement Keys);

/// Data-storage change notification for a key subscribed via SetNotify.
public sealed record ApSetReply(
    [property: JsonPropertyName("key")]            string      Key,
    [property: JsonPropertyName("value")]          JsonElement Value,
    [property: JsonPropertyName("original_value")] JsonElement OriginalValue);

// ════════════════════════════════════════════════════════════════════════════════
// OUTBOUND commands  (client → server)
// We serialize these as anonymous objects for brevity.
// See ApClient.SendXxxAsync for the actual serialization.
// ════════════════════════════════════════════════════════════════════════════════

/// Reply to our LocationScouts command. Contains item details for each location.
public sealed record ApLocationInfo(
    [property: JsonPropertyName("locations")] ApNetworkItem[] Locations);

/// Server-side state update (hint points, new checked locations, player updates, etc.).
/// Only fields present in the JSON are considered changed.
public sealed record ApRoomUpdate(
    [property: JsonPropertyName("hint_points")]        int?    HintPoints,
    [property: JsonPropertyName("checked_locations")]  long[]? CheckedLocations);

// (Outbound commands are defined inline in ApClient.cs as anonymous objects — no separate
//  classes needed since we never deserialize them. Keeping them as anonymous objects
//  avoids JsonPropertyName boilerplate for the write path.)
