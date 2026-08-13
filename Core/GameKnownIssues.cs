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

    // The vanishing waypoint menu. Reported repeatedly and mistaken for lost
    // save data every time: every act tab except Act 1 drops out of the
    // waypoint menu at once. The waypoint bits themselves are never lost —
    // what goes stale is the game client's copy of the quest records, which
    // is what the menu gates its act tabs on. Any quest-bearing NPC dialogue
    // makes the game resend them, which is why talking to one or two town
    // NPCs (Jerhyn was just the traditional pick) always brought everything
    // back. As of v3.7.4 / EX-1.1.12 the randomiser resends them itself on
    // every area change and every ten seconds, so the menu should no longer
    // be able to go stale — the card stays as a fallback in case it ever
    // still happens.
    private static readonly KnownIssue WaypointsVanish = new(
        "Your waypoints disappear from the waypoint menu — every act except " +
        "Act 1 goes missing at once, including waypoints you already activated.",
        "Nothing has been lost from your save. The game's menu loses track of " +
        "which acts you have reached; the game version from v3.7.4 / EX-1.1.12 " +
        "onwards refreshes this automatically, so you should rarely see it.",
        "If it still happens: talk to one or two town NPCs — any of them, in " +
        "any act. The menu comes straight back with every act intact.");

    private static readonly Dictionary<string, KnownIssue[]> _registry = new()
    {
        { "diablo2_archipelago",              new[] { WaypointsVanish } },
        { "diablo2_archipelago_experimental", new[] { WaypointsVanish } },
        { "diablo_ii_lord_of_destruction",    new[] { WaypointsVanish } },
    };
}
