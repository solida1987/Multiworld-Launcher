using System.Collections.Generic;

namespace LauncherV2.Core;

// GameKnownIssues — per-game notes about bugs the player will hit that we
// cannot fix from our side, shown as a highlighted card on the Overview page.
//
// WHAT BELONGS HERE
// Only issues that (a) players actually report, and (b) have a workaround the
// player can perform themselves. A bug we are going to fix belongs in the
// tracker, not here — this card is for the ones where the answer is "yes, that
// happens, and here is what you do about it".
//
// Entries are keyed by plugin GameId, so the same note can cover the stable and
// experimental builds of a game without duplicating the text.

public readonly struct KnownIssue
{
    public string Symptom { get; init; }   // what the player sees
    public string Cause   { get; init; }   // why it happens
    public string Fix     { get; init; }   // what the player does — highlighted

    public KnownIssue(string symptom, string cause, string fix)
    {
        Symptom = symptom;
        Cause   = cause;
        Fix     = fix;
    }
}

public static class GameKnownIssues
{
    // Returns the notes for a game, or an empty list when there are none.
    public static IReadOnlyList<KnownIssue> Get(string gameId)
        => _registry.TryGetValue(gameId, out var list) ? list : System.Array.Empty<KnownIssue>();

    // Diablo II 1.10's Act 2 waypoint bug. Reported repeatedly and mistaken for
    // a randomiser fault every time, because it looks exactly like lost save
    // data: the waypoints you already activated stop appearing on the map.
    // It is vanilla behaviour — the game hides the Act 2 waypoint set while
    // Jerhyn's palace dialogue is pending, and talking to him releases them.
    private static readonly KnownIssue WaypointsVanish = new(
        "Your waypoints disappear and cannot be used — the ones you already " +
        "activated stop showing up on the map.",
        "This is a bug in Diablo II 1.10 itself, not in the randomiser. Nothing " +
        "has been lost from your save.",
        "Talk to Jerhyn. He stands outside the palace in Lut Gholein (the Act 2 " +
        "town). Speaking to him brings the waypoints straight back.");

    private static readonly Dictionary<string, KnownIssue[]> _registry = new()
    {
        { "diablo2_archipelago",              new[] { WaypointsVanish } },
        { "diablo2_archipelago_experimental", new[] { WaypointsVanish } },
        { "diablo_ii_lord_of_destruction",    new[] { WaypointsVanish } },
    };
}
