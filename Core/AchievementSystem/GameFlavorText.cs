using System.Collections.Generic;

namespace LauncherV2.Core.AchievementSystem;

// ═══════════════════════════════════════════════════════════════════════════════
// GameFlavorText — game-specific achievement overrides for the generated ladders.
//
// The AchievementLadders generator produces generic definitions (e.g., "Mission
// Complete — complete your first goal"). For popular games, this table provides
// thematic titles, descriptions, and icons that reference the game's actual
// content. The first-goal achievement (_{prefix}_goal_1) uses these when available.
//
// ADDING ENTRIES
// ──────────────
// Add a line: { "game_id", ("Title", "Description — should mention the game's
//               specific goal or win condition.", "Emoji") }
// Keep descriptions factual: what actually happens in the game to win.
// ═══════════════════════════════════════════════════════════════════════════════

public static class GameFlavorText
{
    /// Returns (Title, Description, Icon) for the first-goal achievement of a game,
    /// or null when no game-specific override exists.
    public static (string Title, string Description, string Icon)? GoalFlavor(string gameId)
        => _goalFlavor.TryGetValue(gameId, out var v) ? v : null;

    private static readonly Dictionary<string, (string, string, string)> _goalFlavor = new()
    {
        // ── Diablo II ────────────────────────────────────────────────────────
        { "diablo2_archipelago",
          ("Lord of Destruction", "Slay Baal and his Prime Evils and bring peace to Sanctuary.", "⚔️") },
        { "diablo_ii_lord_of_destruction",
          ("Lord of Destruction", "Slay Baal and his Prime Evils and bring peace to Sanctuary.", "⚔️") },

        // ── Zelda / Nintendo ─────────────────────────────────────────────────
        { "a_link_to_the_past",
          ("Hero of the Triforce", "Rescue Princess Zelda, gather the Crystals, and defeat Ganon in the Pyramid of Power.", "🏆") },
        { "wind_waker",
          ("King of the Seas", "Defeat Ganondorf atop Hyrule Castle as the Great Sea washes it away.", "🌊") },
        { "super_mario_64",
          ("Star Collector", "Recover all Power Stars, free the castle, and face Bowser at the top.", "⭐") },
        { "kingdom_hearts",
          ("Keyblade Master", "Close every Keyhole and seal the World, ending the Darkness at the End of the World.", "🔑") },
        { "kingdom_hearts_2",
          ("Light's Champion", "Defeat Xemnas in the World That Never Was and return home.", "✨") },

        // ── Metroidvania ──────────────────────────────────────────────────────
        { "super_metroid",
          ("Metroid Slayer", "Defeat Mother Brain and escape Zebes before it explodes.", "🌟") },
        { "hollow_knight",
          ("Hollow Knight", "Seal the Hollow Knight at the Black Egg Temple and save Hallownest.", "🪲") },
        { "blasphemous",
          ("Penitent One", "Ascend the True Throne and end the Miracle's cycle of suffering.", "✝️") },
        { "timespinner",
          ("Anomaly Resolved", "Destroy the Timespinner and collapse the gate that doomed your clan.", "⏰") },

        // ── Action / Combat ───────────────────────────────────────────────────
        { "risk_of_rain_2",
          ("Providence's End", "Defeat Mithrix, the King of Nothing, on the moon of Petrichor V.", "🌧️") },
        { "dark_souls_3",
          ("Lord of Cinder", "Link the Fire — or let it die — and close the Age of Fire.", "🔥") },
        { "sonic_adventure_2_battle",
          ("Last Story", "Witness the Biolizard's defeat and save the Ark with the Ultimate Power.", "💎") },
        { "doom_1993",
          ("Hell on Earth", "Defeat the Spiderdemon atop the starbase and close the Hell gates.", "💥") },
        { "doom_ii",
          ("Icon Slain", "Kill the Icon of Sin and end the demon invasion of Earth.", "☠️") },
        { "aquaria",
          ("Verse Cave Opened", "Defeat the God Fish and uncover the truth behind Aquaria's creators.", "🐟") },
        { "messenger",
          ("The Message Delivered", "Unravel the loop, save both timelines, and deliver the message.", "📜") },
        { "slay_the_spire",
          ("The Heart Defeated", "Slay the Corrupt Heart at the top of the Spire.", "❤️") },

        // ── Exploration / Survival ────────────────────────────────────────────
        { "minecraft",
          ("Bane of the Dragon", "Defeat the Ender Dragon in The End.", "🐉") },
        { "terraria",
          ("Moon Lord Slain", "Defeat the Moon Lord, master of the Celestial Event.", "🌙") },
        { "noita",
          ("Powder Master", "Brew the mixture, purify yourself, and seek the truth beneath the mountain.", "⚗️") },
        { "stardew_valley",
          ("Community Restored", "Complete the Community Center bundles and restore Pelican Town.", "🌱") },
        { "subnautica",
          ("Safe Shallows Left Behind", "Build a Cyclops, find the Precursor facility, and cure the Kharaa bacterium.", "🌊") },

        // ── Roguelike / Card ──────────────────────────────────────────────────
        { "inscryption",
          ("Escaping the Cabin", "Defeat Leshy, unravel the tape's secrets, and escape the cabin.", "🃏") },

        // ── Puzzle / Indie ────────────────────────────────────────────────────
        { "tunic",
          ("True Ending", "Retrieve all three hexagon pieces and face the Heir.", "📖") },
        { "witness",
          ("Endgame Witnessed", "Activate all colored line puzzles and find the island's secret.", "🧩") },
        { "lingo",
          ("Linguist", "Solve the final door and read what lies beyond Lingo's language puzzles.", "🔠") },

        // ── Factory / Automation ──────────────────────────────────────────────
        { "factorio",
          ("Rocket Launched", "Research, build, and launch a rocket into space.", "🚀") },
        { "satisfactory",
          ("Tier 9 Complete", "Ascend through every phase of Ficsit's project and complete Phase 4.", "🏭") },

        // ── Music / Rhythm ────────────────────────────────────────────────────
        { "muse_dash",
          ("Full Combo Champion", "Clear the required songs and reach the AP goal.", "🎵") },

        // ── Other popular ─────────────────────────────────────────────────────
        { "a_hat_in_time",
          ("Time Pieces Restored", "Collect all Time Pieces and seal the rift in Hat Kid's ship.", "🎩") },
        { "undertale",
          ("True Pacifist", "Complete a pacifist route and witness the True Lab's secret.", "❤️") },
    };
}
