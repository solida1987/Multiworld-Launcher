using System;
using System.Collections.Generic;
using System.Linq;

namespace LauncherV2.Core.AchievementSystem;

// ═══════════════════════════════════════════════════════════════════════════════
// AchievementLadders — programmatically generated achievement ladders
// (owner spec §4: hundreds of definitions instead of a hand-curated handful).
//
// One ladder set per registered plugin (install / first connect / checks /
// goals / playtime / sessions / DeathLink) + a launcher-general group that
// complements — never duplicates — the handwritten definitions in
// AchievementDefinitions.
//
// IDs ARE A PERSISTENCE CONTRACT. Earned achievements are stored by id in
// achievements.json, so a generated id must NEVER change once shipped:
//   • Known games use the frozen alias map below (diablo2_archipelago → "d2").
//   • Every other game derives its prefix from its GameId verbatim — adding a
//     plugin automatically grows the ladder set with stable ids and NO edits
//     here. Do not add aliases for games that have already shipped without one.
//
// HONESTY: every rule reads real tracked state (AchievementStore counters and
// sessions, LibraryStore). Counters start at 0 — play from before this feature
// simply hasn't counted. The completionist tiers exclude themselves from their
// own percentage so they can't self-reference.
// ═══════════════════════════════════════════════════════════════════════════════

/// Counter kinds persisted by AchievementStore (Data/achievement_counters.json).
/// String values are a persistence contract — never change them once shipped.
public static class AchievementCounters
{
    public const string Installs     = "installs";       // completed first-time installs
    public const string Connects     = "connects";       // successful AP logins
    public const string ChecksSent   = "checks";         // location checks sent by the game
    public const string Goals        = "goals";          // goal completions reported by the game
    public const string DeathsShared = "deaths_shared";  // DeathLink deaths we sent out
}

/// One evaluatable unlock rule for a generated achievement.
/// GameId scopes evaluation (null = launcher-general); IsMet reads live state.
public sealed record LadderRule(string Id, string? GameId, Func<AchievementStore, bool> IsMet);

public static class AchievementLadders
{
    // ── Frozen id-prefix aliases ──────────────────────────────────────────────
    // Only for games that shipped WITH an alias. Everything else uses GameId.
    private static readonly Dictionary<string, string> IdAliases = new()
    {
        ["diablo2_archipelago"] = "d2",
    };

    private static string Prefix(string gameId)
        => IdAliases.GetValueOrDefault(gameId, gameId);

    // ── Lazily generated (plugins are registered before first UI access) ─────
    private static readonly Lazy<(IReadOnlyList<AchievementDefinition> Defs,
                                  IReadOnlyList<LadderRule>            Rules)> _generated
        = new(Generate);

    /// All generated definitions (per-game ladders + launcher-general extras).
    public static IReadOnlyList<AchievementDefinition> Definitions => _generated.Value.Defs;

    /// Unlock rules for the generated definitions, evaluation-ordered:
    /// the completionist tiers come LAST so a single evaluation pass sees
    /// every grant the same pass produced.
    public static IReadOnlyList<LadderRule> Rules => _generated.Value.Rules;

    // ── Generator ─────────────────────────────────────────────────────────────

    private static (IReadOnlyList<AchievementDefinition>, IReadOnlyList<LadderRule>) Generate()
    {
        var defs  = new List<AchievementDefinition>();
        var rules = new List<LadderRule>();

        void Add(AchievementDefinition def, string? gameId, Func<AchievementStore, bool> isMet)
        {
            defs.Add(def);
            rules.Add(new LadderRule(def.Id, gameId, isMet));
        }

        // ── Per-game ladders — one set for EVERY registered plugin ───────────
        foreach (var plugin in GameRegistry.All)
        {
            string id   = plugin.GameId;
            string p    = Prefix(id);
            string name = plugin.DisplayName;

            Func<AchievementStore, bool> Counter(string kind, long n)
                => store => store.GetCounter(id, kind) >= n;

            // Install
            Add(new AchievementDefinition
            {
                Id = $"{p}_install", GameId = id, Icon = "🧩", Tier = "bronze",
                Title = "First Steps", Description = $"Install {name} through the launcher.",
            }, id, Counter(AchievementCounters.Installs, 1));

            // First AP connect
            Add(new AchievementDefinition
            {
                Id = $"{p}_connect_1", GameId = id, Icon = "🔗", Tier = "bronze",
                Title = "Hello, Multiworld", Description = $"Connect {name} to an Archipelago server.",
            }, id, Counter(AchievementCounters.Connects, 1));

            // Checks: first + milestones
            Add(new AchievementDefinition
            {
                Id = $"{p}_checks_1", GameId = id, Icon = "🎯", Tier = "bronze",
                Title = "Opening Move", Description = $"Send your first check from {name}.",
            }, id, Counter(AchievementCounters.ChecksSent, 1));

            foreach (var (n, tier, title) in new (int, string, string)[]
            {
                (10,   "bronze",   "Warming Up"),
                (50,   "silver",   "Check Collector"),
                (100,  "silver",   "Triple Digits"),
                (250,  "gold",     "Check Machine"),
                (500,  "gold",     "Five Hundred Strong"),
                (1000, "platinum", "Thousand-Check Club"),
            })
            {
                Add(new AchievementDefinition
                {
                    Id = $"{p}_checks_{n}", GameId = id, Icon = "🎯", Tier = tier,
                    Title = title, Description = $"Send {n:#,##0} checks from {name}.",
                }, id, Counter(AchievementCounters.ChecksSent, n));
            }

            // Goals: first + milestones (game-specific flavor when available)
            var goalFlavor = GameFlavorText.GoalFlavor(id);
            Add(new AchievementDefinition
            {
                Id          = $"{p}_goal_1",
                GameId      = id,
                Icon        = goalFlavor?.Icon ?? "🏁",
                Tier        = "gold",
                Title       = goalFlavor?.Title ?? "Mission Complete",
                Description = goalFlavor?.Description ?? $"Complete your first {name} goal.",
            }, id, Counter(AchievementCounters.Goals, 1));

            foreach (var (n, tier, title) in new (int, string, string)[]
            {
                (3,  "gold",     "Hat Trick"),
                (10, "platinum", "Perfect Ten"),
            })
            {
                Add(new AchievementDefinition
                {
                    Id = $"{p}_goals_{n}", GameId = id, Icon = "🏁", Tier = tier,
                    Title = title, Description = $"Complete {n} {name} goals.",
                }, id, Counter(AchievementCounters.Goals, n));
            }

            // Playtime tiers — evaluated from REAL tracked sessions, no counter.
            foreach (var (hours, tier, title) in new (int, string, string)[]
            {
                (1,   "bronze",   "Just Getting Started"),
                (5,   "bronze",   "Finding the Rhythm"),
                (10,  "silver",   "Settling In"),
                (25,  "silver",   "Quarter Century"),
                (50,  "gold",     "Half Century"),
                (100, "platinum", "Hundred-Hour Hero"),
            })
            {
                int h = hours;
                Add(new AchievementDefinition
                {
                    Id = $"{p}_time_{hours}h", GameId = id, Icon = "⏱️", Tier = tier,
                    Title = title,
                    Description = $"Play {name} for {hours} hour{(hours == 1 ? "" : "s")} in total.",
                }, id, store => store.TotalPlaytime(id) >= TimeSpan.FromHours(h));
            }

            // Session-count tiers — also from real tracked sessions.
            foreach (var (n, tier, title) in new (int, string, string)[]
            {
                (5,   "bronze", "Regular Visitor"),
                (25,  "silver", "Frequent Flyer"),
                (100, "gold",   "Home Away From Home"),
            })
            {
                int c = n;
                Add(new AchievementDefinition
                {
                    Id = $"{p}_sessions_{n}", GameId = id, Icon = "📅", Tier = tier,
                    Title = title, Description = $"Play {n} {name} sessions.",
                }, id, store => store.TotalSessions(id) >= c);
            }

            // DeathLink — D2 only for now (the only plugin with a send path).
            if (id == "diablo2_archipelago")
            {
                Add(new AchievementDefinition
                {
                    Id = $"{p}_deathlink_1", GameId = id, Icon = "💀", Tier = "silver",
                    Title = "Misery Loves Company",
                    Description = "Share a death with the multiworld through DeathLink.",
                }, id, Counter(AchievementCounters.DeathsShared, 1));
            }
        }

        // ── Launcher-general extras (handwritten general defs stay untouched) ─
        Add(new AchievementDefinition
        {
            Id = "gen_socialite_5", Icon = "🛰️", Tier = "silver",
            Title = "Multiworld Socialite",
            Description = "Connect to 5 different Archipelago servers.",
        }, null, store => store.DistinctServerCount() >= 5);

        Add(new AchievementDefinition
        {
            Id = "gen_librarian", Icon = "📚", Tier = "silver",
            Title = "Librarian",
            Description = "Grow your library to 10 games.",
        }, null, _ => LibraryStore.GetSortedGameIds().Count >= 10);

        Add(new AchievementDefinition
        {
            Id = "gen_night_owl", Icon = "🦉", Tier = "bronze",
            Title = "Night Owl",
            Description = "Play a session that runs past 2 AM.",
        }, null, store => store.AnySessionPastTwoAm());

        Add(new AchievementDefinition
        {
            Id = "gen_marathon_8h", Icon = "⏰", Tier = "gold",
            Title = "Ultramarathon",
            Description = "Play for 8 hours in a single session.",
        }, null, store => store.LongestSession() >= TimeSpan.FromHours(8));

        // Completionist tiers LAST (see Rules doc comment). They exclude
        // themselves from the percentage so they can never self-reference.
        foreach (var (pct, tier, title) in new (int, string, string)[]
        {
            (25,  "silver",   "Trophy Hunter"),
            (50,  "gold",     "Trophy Connoisseur"),
            (100, "platinum", "Completionist"),
        })
        {
            int threshold = pct;
            Add(new AchievementDefinition
            {
                Id = $"gen_completionist_{pct}", Icon = pct == 100 ? "💯" : "🎖️", Tier = tier,
                Title = title,
                Description = pct == 100
                    ? "Earn every other achievement in the launcher."
                    : $"Earn {pct}% of all achievements.",
            }, null, store => CompletionPercent(store) >= threshold);
        }

        return (defs, rules);
    }

    // ── Completionist support ─────────────────────────────────────────────────

    private static bool IsCompletionistId(string id) => id.StartsWith("gen_completionist_", StringComparison.Ordinal);

    /// Percentage of all NON-completionist achievements earned (0–100).
    private static double CompletionPercent(AchievementStore store)
    {
        var eligible = AchievementDefinitions.All.Where(d => !IsCompletionistId(d.Id)).ToList();
        if (eligible.Count == 0) return 0;
        int earned = eligible.Count(d => store.IsEarned(d.Id));
        return earned * 100.0 / eligible.Count;
    }
}
