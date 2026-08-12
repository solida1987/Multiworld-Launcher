using System.Collections.Generic;

namespace LauncherV2.Core.AchievementSystem;

// GameFlavorText — game-specific achievement overrides for the generated ladders.
// The AchievementLadders generator produces generic definitions (e.g., "Mission
// Complete — complete your first goal"); entries here replace the first-goal
// achievement (_{prefix}_goal_1) with a title/description that matches the
// game's actual win condition.

public static class GameFlavorText
{
    // Returns (Title, Description, Icon) for the first-goal achievement of a game,
    // or null when no game-specific override exists.
    public static (string Title, string Description, string Icon)? GoalFlavor(string gameId)
        => _goalFlavor.TryGetValue(gameId, out var v) ? v : null;

    private static readonly Dictionary<string, (string, string, string)> _goalFlavor = new()
    {
        { "diablo2_archipelago",
          ("Lord of Destruction", "Slay Baal and his Prime Evils and bring peace to Sanctuary.", "⚔️") },
        { "diablo2_archipelago_experimental",
          ("Lord of Destruction", "Slay Baal and his Prime Evils and bring peace to Sanctuary.", "⚔️") },
        { "diablo_ii_lord_of_destruction",
          ("Lord of Destruction", "Slay Baal and his Prime Evils and bring peace to Sanctuary.", "⚔️") },
    };
}
