using System.Collections.Generic;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// GameCredits — static registry of game developers and AP world authors.
//
// ADDING ENTRIES
// ──────────────
// Each entry maps a plugin GameId → (GameDev, ApAuthor).
// GameDev:  original game's developer/publisher (always known).
// ApAuthor: GitHub username or display name of the AP world author.
//           Leave as null if unknown — the credits block is hidden when empty.
//
// HOW TO FILL IN MISSING AUTHORS
// ───────────────────────────────
// 1. Open https://github.com/ArchipelagoMW/Archipelago/tree/main/worlds
// 2. Find the folder for the game — the README or __init__.py credits the author.
// 3. Add a line: { "game_id", ("Developer", "AP-Author-GitHub-Name") }
// ═══════════════════════════════════════════════════════════════════════════════

public static class GameCredits
{
    /// Returns (GameDev, ApAuthor) for the given gameId, or null if unknown.
    public static (string GameDev, string? ApAuthor)? Get(string gameId)
        => _registry.TryGetValue(gameId, out var c) ? c : null;

    private static readonly Dictionary<string, (string GameDev, string? ApAuthor)>
        _registry = new()
    {
        // ── Official Archipelago AP-main worlds ──────────────────────────────
        { "a_link_to_the_past",          ("Nintendo / Capcom",          "Archipelago Team") },
        { "super_metroid",               ("Nintendo / Retro Studios",   "Archipelago Team") },
        { "hollow_knight",               ("Team Cherry",                "Archipelago Team") },
        { "stardew_valley",              ("ConcernedApe",               "Archipelago Team") },
        { "tunic",                       ("Andrew Shouldice / Finji",   "Archipelago Team") },
        { "risk_of_rain_2",              ("Hopoo Games",                "Archipelago Team") },
        { "minecraft",                   ("Mojang Studios",             "Archipelago Team") },
        { "terraria",                    ("Re-Logic",                   "Archipelago Team") },
        { "noita",                       ("Nolla Games",                "Archipelago Team") },
        { "blasphemous",                 ("The Game Kitchen",           "Archipelago Team") },
        { "muse_dash",                   ("PeroPeroGames / XD Inc.",    "Archipelago Team") },
        { "slay_the_spire",              ("Mega Crit",                  "Archipelago Team") },
        { "undertale",                   ("Toby Fox",                   "Archipelago Team") },
        { "meritous",                    ("Lancer-X / asie",            "Archipelago Team") },
        { "aquaria",                     ("Bit Blot",                   "Archipelago Team") },
        { "doom_1993",                   ("id Software",                "Archipelago Team") },
        { "doom_ii",                     ("id Software",                "Archipelago Team") },
        { "heretic",                     ("Raven Software",             "Archipelago Team") },
        { "lingo",                       ("Bryce Vandegrift",           "Archipelago Team") },
        { "shivers",                     ("Sierra On-Line",             "Archipelago Team") },
        { "witness",                     ("Thekla, Inc.",               "Archipelago Team") },
        { "timespinner",                 ("Lunar Ray Games",            "Archipelago Team") },
        { "inscryption",                 ("Daniel Mullins Games",       "Archipelago Team") },
        { "raft",                        ("Redbeet Interactive",        "Archipelago Team") },
        { "dlc_quest",                   ("Going Loud Studios",         "Archipelago Team") },
        { "a_short_hike",                ("adamgryu",                   "Archipelago Team") },
        { "vvvvvv",                      ("Terry Cavanagh",             "Archipelago Team") },
        { "messenger",                   ("Sabotage Studio",            "Archipelago Team") },
        { "a_hat_in_time",               ("Gears for Breakfast",        "Archipelago Team") },
        { "factorio",                    ("Wube Software",              "Archipelago Team") },
        { "starcraft_2",                 ("Blizzard Entertainment",     "Archipelago Team") },
        { "hylics_2",                    ("Mason Lindroth",             "Archipelago Team") },
        { "overcooked_2",                ("Ghost Town Games",           "Archipelago Team") },
        { "satisfactory",                ("Coffee Stain Studios",       "Archipelago Team") },
        { "wargroove",                   ("Chucklefish",                "Archipelago Team") },
        { "shapez",                      ("tobspr Games",               "Archipelago Team") },
        { "celeste_64",                  ("Extremely OK Games",         "Archipelago Team") },
        { "super_mario_64",              ("Nintendo",                   "Archipelago Team") },
        { "paint",                       ("Microsoft",                  "Archipelago Team") },
        { "kingdom_hearts",              ("Square Enix",                "Archipelago Team") },
        { "kingdom_hearts_2",            ("Square Enix",                "Archipelago Team") },
        { "sonic_adventure_2_battle",    ("Sega / Sonic Team",          "Archipelago Team") },
        { "dark_souls_3",                ("FromSoftware",               "Archipelago Team") },
        { "civilization_vi",             ("Firaxis Games",              "Archipelago Team") },
        { "old_school_runescape",        ("Jagex",                      "Archipelago Team") },
        { "wind_waker",                  ("Nintendo",                   "Archipelago Team") },
        { "choo_choo_charles",           ("Two Star Games",             "Archipelago Team") },
        { "celeste_open_world",          ("Extremely OK Games",         "Archipelago Team") },
        { "bomb_rush_cyberfunk",         ("Team Reptile",               "Archipelago Team") },
        { "jak_and_daxter",              ("Naughty Dog",                "Archipelago Team") },
        { "faxanadu",                    ("Hudson Soft",                "Archipelago Team") },

        // ── Diablo II Archipelago (our own mod) ──────────────────────────────
        { "diablo2_archipelago",         ("Blizzard Entertainment",     "solida1987") },
        { "diablo_ii_lord_of_destruction", ("Blizzard Entertainment",  "solida1987") },

        // ── OpenTTD Archipelago ──────────────────────────────────────────────
        { "openttd_archipelago",         ("OpenTTD Team",               "solida1987") },

        // ── Ship of Harkinian ─────────────────────────────────────────────────
        { "ship_of_harkinian",           ("Nintendo (original)",        "Archipelago SoH Team") },

        // ── Community natives ─────────────────────────────────────────────────
        { "hollow_knight_legacy",        ("Team Cherry",                "Archipelago Team") },
        { "pokemon_emerald",             ("Game Freak / Nintendo",      "Archipelago Team") },
        { "a_link_to_the_past_alttpr",   ("Nintendo",                   "Archipelago Team") },
        { "balatro",                     ("LocalThunk",                 null) },
        { "brotato",                     ("Blobfish",                   null) },
        { "celeste",                     ("Extremely OK Games",         null) },
        { "pizza_tower",                 ("Tour De Pizza",              null) },
        { "rain_world",                  ("Videocult",                  null) },
        { "monster_sanctuary",           ("Moi Rai Games",              null) },
        { "outer_wilds",                 ("Mobius Digital",             null) },
        { "hades",                       ("Supergiant Games",           null) },
        { "cuphead",                     ("Studio MDHR",                null) },
        { "deltarune",                   ("Toby Fox",                   null) },
        { "grim_dawn",                   ("Crate Entertainment",        null) },
        { "risk_of_rain",                ("Hopoo Games",                null) },
        { "enter_the_gungeon",           ("Dodge Roll",                 null) },
        { "freedom_planet_2",            ("GalaxyTrail",                null) },
        { "neon_white",                  ("Angel Matrix / Devolver",    null) },
        { "pseudoregalia",               ("rittzler",                   null) },
        { "omori",                       ("OMOCAT",                     null) },
        { "void_stranger",               ("System Erasure",             null) },
        { "ultrakill",                   ("Arsi 'Hakita' Patala",       null) },
        { "cave_story",                  ("Daisuke 'Pixel' Amaya",      null) },
        { "axiom_verge",                 ("Thomas Happ Games",          null) },
        { "crosscode",                   ("Radical Fish Games",         null) },
        { "cobalt_core",                 ("Rocket Rat Games",           null) },
        { "chained_echoes",              ("Matthias Linda",             null) },
        { "crystal_project",             ("James Conway",               null) },
        { "dark_souls_remastered",       ("FromSoftware",               null) },
        { "dark_souls_ii",               ("FromSoftware",               null) },
        { "deaths_door",                 ("Acid Nerve",                 null) },
        { "dicey_dungeons",              ("Terry Cavanagh",             null) },
        { "dome_keeper",                 ("Bippinbits",                 null) },
        { "dredge",                      ("Black Salt Games",           null) },
        { "devil_may_cry_3",             ("Capcom",                     null) },
        { "dungeon_clawler",             ("Stramite",                   null) },
        { "ender_lilies",                ("Binary Haze Interactive",    null) },
        { "everhood_2",                  ("Yellowcloud games",          null) },
        { "getting_over_it",             ("Bennett Foddy",              null) },
        { "hammerwatch",                 ("Crackshell",                 null) },
        { "here_comes_niko",             ("Frog Vibes",                 null) },
        { "hi_fi_rush",                  ("Tango Gameworks",            null) },
        { "buckshot_roulette",           ("Mike Klubnika",              null) },
        { "dont_starve_together",        ("Klei Entertainment",         null) },
        { "sticklands",                  ("Sokpop Collective",          null) },
        { "binding_of_isaac_repentance", ("Nicalis / Edmund McMillen",  null) },
    };
}
