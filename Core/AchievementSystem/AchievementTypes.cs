using System;
using System.Text.Json.Serialization;

namespace LauncherV2.Core;

// The two records the achievement and playtime systems are built on.
//
// They used to live in CatalogTypes.cs, next to the types that described a
// shipped list of games. That list is gone, and these two had nothing to do
// with it: an achievement and a play session belong to the launcher's own
// bookkeeping, not to a catalogue of games it does not contain.

// --- Achievement definitions ---

// One achievement definition (static metadata — not the earned state).
public sealed record AchievementDefinition
{
    public string Id          { get; init; } = "";
    public string Title       { get; init; } = "";
    public string Description { get; init; } = "";
    // Icon identifier (maps to an Assets/Achievements/*.png or a Unicode emoji).
    public string Icon        { get; init; } = "🏆";
    // null = global (any game), otherwise locked to one GameId.
    public string? GameId     { get; init; }
    // Tier: "bronze" | "silver" | "gold" | "platinum"
    public string Tier        { get; init; } = "bronze";
}

// --- Session statistics (tracked per play session) ---

// One completed play session — written to disk when the game exits.
public sealed record PlaySession
{
    public string        GameId       { get; init; } = "";
    public DateTimeOffset StartedAt   { get; init; }
    public DateTimeOffset EndedAt     { get; init; }
    public TimeSpan       Duration    => EndedAt - StartedAt;
    public bool           GoalReached { get; init; }
    public string?        Server      { get; init; }
    public string?        SlotName    { get; init; }
    public int            PlayerCount { get; init; }
}
