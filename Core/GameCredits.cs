using System.Collections.Generic;

namespace LauncherV2.Core;

// GameCredits — static registry of game developers and AP world authors.
// Each entry maps a plugin GameId → (GameDev, ApAuthor).
// Leave ApAuthor as null if unknown — the credits block is hidden when empty.

public static class GameCredits
{
    // Returns (GameDev, ApAuthor) for the given gameId, or null if unknown.
    public static (string GameDev, string? ApAuthor)? Get(string gameId)
        => _registry.TryGetValue(gameId, out var c) ? c : null;

    // Extra contributors who help with the Archipelago LOGIC (regions/rules) for
    // a game, shown as a separate credit line.
    public static string? GetApLogic(string gameId)
        => _apLogic.TryGetValue(gameId, out var c) ? c : null;

    private static readonly Dictionary<string, string> _apLogic = new()
    {
        { "diablo2_archipelago",            "ꓘicka & Zoë" },
        { "diablo_ii_lord_of_destruction",  "ꓘicka & Zoë" },
    };

    private static readonly Dictionary<string, (string GameDev, string? ApAuthor)>
        _registry = new()
    {
        { "diablo2_archipelago",              ("Blizzard Entertainment", "solida1987") },
        { "diablo2_archipelago_experimental", ("Blizzard Entertainment", "solida1987") },
        { "diablo_ii_lord_of_destruction",    ("Blizzard Entertainment", "solida1987") },
    };
}
