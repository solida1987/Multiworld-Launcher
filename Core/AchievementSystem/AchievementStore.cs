using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LauncherV2.Core.AchievementSystem;

// ═══════════════════════════════════════════════════════════════════════════════
// AchievementStore — persists play sessions and tracks achievement progress.
//
// Storage: <AppDir>/Data/achievements.json          (earned id → UTC date)
//          <AppDir>/Data/sessions.json              (play sessions)
//          <AppDir>/Data/achievement_counters.json  (per-game counters + servers)
//
// Everything is written locally — no server, no telemetry.
//
// Two evaluation paths:
//   • CheckAchievements(session) — the handwritten definitions, evaluated when
//     a session ends (their conditions need the latest session).
//   • EvaluateAll(gameId) — the generated ladders (AchievementLadders.Rules),
//     evaluated after every counter increment and on session end. Counters
//     start at 0 — play from before the feature existed has not counted.
//
// THREAD SAFETY: all public methods are safe to call from any thread.
// Collection access is guarded by locks on _sessions/_earned/_counterLock;
// Save() serializes SNAPSHOTS taken under those locks and writes the files
// under a dedicated save lock, so concurrent EndSession calls (game exit
// racing a manual disconnect) can neither corrupt the JSON nor interleave
// writes. (P2-21: an earlier version of this header claimed a queue +
// background flush timer that never existed, while Save() serialized the
// live collections outside the writers' locks.)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AchievementStore
{
    // ── Paths ─────────────────────────────────────────────────────────────────
    private static string DataDir      => Path.Combine(AppContext.BaseDirectory, "Data");
    private static string SessionsPath => Path.Combine(DataDir, "sessions.json");
    private static string EarnedPath   => Path.Combine(DataDir, "achievements.json");
    private static string CountersPath => Path.Combine(DataDir, "achievement_counters.json");

    // ── In-memory state ───────────────────────────────────────────────────────
    // _sessions and _earned each serve as their own lock object (they are
    // readonly references in practice — only reassigned during construction).
    private List<PlaySession>                  _sessions = new();
    // Earned id → UTC earned date. Legacy files (a bare id array from before
    // dates were stamped) load with DateTimeOffset.MinValue = "date unknown".
    private Dictionary<string, DateTimeOffset> _earned   = new();

    // Per-game cumulative counters ("{gameId|_}:{kind}") + distinct AP servers.
    // Guarded by _counterLock (two collections, one lock).
    private Dictionary<string, long> _counters    = new();
    private HashSet<string>          _servers     = new(StringComparer.OrdinalIgnoreCase);
    private readonly object          _counterLock = new();

    // Serialises the file writes — two racing Save() calls would otherwise
    // interleave WriteAllText on the same paths (IOException → write lost).
    private readonly object             _saveLock = new();

    // ── Events ────────────────────────────────────────────────────────────────

    /// Fired when a new achievement is earned. Argument = the definition.
    /// May fire on ANY thread (session end, plugin pipe threads) — subscribers
    /// must marshal to the UI themselves, and should not block.
    public event Action<AchievementDefinition>? AchievementEarned;

    // ── Singleton ─────────────────────────────────────────────────────────────
    public static AchievementStore Instance { get; } = new();
    private AchievementStore() => Load();

    // ── Load / save ───────────────────────────────────────────────────────────

    /// Counters file layout (single DTO so servers and counters stay together).
    private sealed class CounterFile
    {
        [JsonPropertyName("counters")] public Dictionary<string, long> Counters { get; set; } = new();
        [JsonPropertyName("servers")]  public List<string>             Servers  { get; set; } = new();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(SessionsPath))
            {
                var json = File.ReadAllText(SessionsPath);
                _sessions = JsonSerializer.Deserialize<List<PlaySession>>(json) ?? new();
            }
        }
        catch { _sessions = new(); }

        try
        {
            if (File.Exists(EarnedPath))
            {
                var json = File.ReadAllText(EarnedPath);
                try
                {
                    _earned = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(json) ?? new();
                }
                catch (JsonException)
                {
                    // Legacy format: a plain id array (pre-date stamping).
                    // Earned state is preserved; the date stays unknown.
                    var legacy = JsonSerializer.Deserialize<List<string>>(json) ?? new();
                    _earned = legacy.ToDictionary(id => id, _ => DateTimeOffset.MinValue);
                }
            }
        }
        catch { _earned = new(); }

        try
        {
            if (File.Exists(CountersPath))
            {
                var file = JsonSerializer.Deserialize<CounterFile>(
                    File.ReadAllText(CountersPath));
                if (file != null)
                {
                    _counters = file.Counters ?? new();
                    _servers  = new HashSet<string>(file.Servers ?? new(),
                                                    StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch { _counters = new(); _servers = new(StringComparer.OrdinalIgnoreCase); }
    }

    private void Save()
    {
        try
        {
            // Snapshot under the collection locks (P2-21): serializing the live
            // collections while a writer held the locks threw "collection was
            // modified" inside JsonSerializer and silently lost the write.
            List<PlaySession>                  sessionsSnapshot;
            Dictionary<string, DateTimeOffset> earnedSnapshot;
            lock (_sessions) sessionsSnapshot = new List<PlaySession>(_sessions);
            lock (_earned)   earnedSnapshot   = new Dictionary<string, DateTimeOffset>(_earned);

            var opts = new JsonSerializerOptions { WriteIndented = true };
            string sessionsJson = JsonSerializer.Serialize(sessionsSnapshot, opts);
            string earnedJson   = JsonSerializer.Serialize(earnedSnapshot, opts);

            lock (_saveLock)
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(SessionsPath, sessionsJson);
                File.WriteAllText(EarnedPath,   earnedJson);
            }
        }
        catch { /* non-fatal */ }
    }

    /// Counters change far more often than sessions (every check batch), so
    /// they get their own save — same snapshot + save-lock discipline (P2-21).
    private void SaveCounters()
    {
        try
        {
            CounterFile snapshot;
            lock (_counterLock)
                snapshot = new CounterFile
                {
                    Counters = new Dictionary<string, long>(_counters),
                    Servers  = _servers.ToList(),
                };

            string json = JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { WriteIndented = true });

            lock (_saveLock)
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(CountersPath, json);
            }
        }
        catch { /* non-fatal */ }
    }

    // ── Session tracking ──────────────────────────────────────────────────────

    /// Call when a game session starts. Returns a session token (StartedAt).
    public DateTimeOffset BeginSession(string gameId, string? server, string? slot)
    {
        // Stored when EndSession is called; we only keep the token here.
        return DateTimeOffset.UtcNow;
    }

    /// Call when a game session ends.
    public void EndSession(
        string gameId,
        DateTimeOffset startedAt,
        bool goalReached,
        string? server,
        string? slotName,
        int playerCount)
    {
        var session = new PlaySession
        {
            GameId      = gameId,
            StartedAt   = startedAt,
            EndedAt     = DateTimeOffset.UtcNow,
            GoalReached = goalReached,
            Server      = server,
            SlotName    = slotName,
            PlayerCount = playerCount,
        };

        lock (_sessions) _sessions.Add(session);
        Save();
        CheckAchievements(session);
        EvaluateAll(gameId);   // playtime / session-count ladders + general extras
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    public int TotalSessions(string? gameId = null)
    {
        lock (_sessions)
            return gameId == null ? _sessions.Count
                                  : _sessions.Count(s => s.GameId == gameId);
    }

    public int GoalsReached(string? gameId = null)
    {
        lock (_sessions)
            return gameId == null ? _sessions.Count(s => s.GoalReached)
                                  : _sessions.Count(s => s.GameId == gameId && s.GoalReached);
    }

    public TimeSpan TotalPlaytime(string? gameId = null)
    {
        lock (_sessions)
            return gameId == null
                ? TimeSpan.FromTicks(_sessions.Sum(s => s.Duration.Ticks))
                : TimeSpan.FromTicks(_sessions.Where(s => s.GameId == gameId).Sum(s => s.Duration.Ticks));
    }

    public TimeSpan? FastestGoal(string gameId)
    {
        lock (_sessions)
        {
            var times = _sessions
                .Where(s => s.GameId == gameId && s.GoalReached)
                .Select(s => s.Duration)
                .ToList();
            return times.Count > 0 ? times.Min() : null;
        }
    }

    public int UniqueGamesPlayed()
    {
        lock (_sessions) return _sessions.Select(s => s.GameId).Distinct().Count();
    }

    public int MaxPlayerCount()
    {
        lock (_sessions) return _sessions.Count > 0 ? _sessions.Max(s => s.PlayerCount) : 0;
    }

    /// Longest single recorded session (TimeSpan.Zero when none).
    public TimeSpan LongestSession()
    {
        lock (_sessions)
            return _sessions.Count > 0
                ? TimeSpan.FromTicks(_sessions.Max(s => s.Duration.Ticks))
                : TimeSpan.Zero;
    }

    /// True when any recorded session was running past 2 AM local time —
    /// either it spans a local 02:00 instant or it started in the small hours.
    public bool AnySessionPastTwoAm()
    {
        lock (_sessions)
        {
            foreach (var s in _sessions)
            {
                var start = s.StartedAt.ToLocalTime().DateTime;
                var end   = s.EndedAt.ToLocalTime().DateTime;
                if (end < start) continue;   // clock weirdness — skip, stay honest

                if (start.Hour is >= 2 and < 6) return true;   // started past 2 AM
                for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
                {
                    var twoAm = day.AddHours(2);
                    if (twoAm >= start && twoAm <= end) return true;   // played across 2 AM
                }
            }
            return false;
        }
    }

    public bool IsEarned(string achievementId)
    {
        lock (_earned) return _earned.ContainsKey(achievementId);
    }

    /// UTC date the achievement was earned — null when not earned, or when it
    /// was earned before dates were stamped (legacy file).
    public DateTimeOffset? EarnedAt(string achievementId)
    {
        lock (_earned)
            return _earned.TryGetValue(achievementId, out var at) && at != DateTimeOffset.MinValue
                ? at : null;
    }

    public IReadOnlyList<string> AllEarned()
    {
        lock (_earned) return _earned.Keys.ToList();
    }

    // ── Counters (generated-ladder progress) ──────────────────────────────────

    private static string CounterKey(string? gameId, string kind)
        => $"{gameId ?? "_"}:{kind}";

    /// Cumulative counter for one game (null = launcher-general scope).
    public long GetCounter(string? gameId, string kind)
    {
        lock (_counterLock)
            return _counters.GetValueOrDefault(CounterKey(gameId, kind));
    }

    /// Bump a counter, persist it, and evaluate the generated ladders in this
    /// game's context. Safe from any thread; never throws.
    public void IncrementCounter(string? gameId, string kind, long by = 1)
    {
        if (by <= 0) return;
        lock (_counterLock)
        {
            string key     = CounterKey(gameId, kind);
            _counters[key] = _counters.GetValueOrDefault(key) + by;
        }
        SaveCounters();
        EvaluateAll(gameId);
    }

    /// Number of distinct AP servers this launcher has logged in to.
    public int DistinctServerCount()
    {
        lock (_counterLock) return _servers.Count;
    }

    /// Record a successful AP login: remembers the distinct server and bumps
    /// the game's connect counter (which evaluates the ladders).
    public void RecordApConnected(string? gameId, string? server)
    {
        string normalized = NormalizeServer(server);
        if (normalized.Length > 0)
        {
            bool added;
            lock (_counterLock) added = _servers.Add(normalized);
            if (added) SaveCounters();
        }
        IncrementCounter(gameId, AchievementCounters.Connects);
    }

    /// "wss://Archipelago.gg:38281/" and "archipelago.gg:38281" are one server.
    private static string NormalizeServer(string? server)
    {
        string s = (server ?? "").Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "wss://", "ws://" })
            if (s.StartsWith(prefix, StringComparison.Ordinal)) { s = s[prefix.Length..]; break; }
        return s.TrimEnd('/');
    }

    // ── Achievement checking ───────────────────────────────────────────────────

    /// Evaluate every generated-ladder rule whose scope matches and grant the
    /// ones whose threshold is met. gameId = null evaluates everything (the
    /// launcher-general rules always run). Unlock-once semantics are preserved
    /// by Grant. The rule list puts the completionist tiers last, so one pass
    /// already sees this pass's own grants.
    public void EvaluateAll(string? gameId = null)
    {
        try
        {
            foreach (var rule in AchievementLadders.Rules)
            {
                if (gameId != null && rule.GameId != null && rule.GameId != gameId) continue;
                if (IsEarned(rule.Id)) continue;   // locked read; Grant re-checks under the lock
                if (!rule.IsMet(this)) continue;

                if (AchievementDefinitions.Find(rule.Id) is { } def) Grant(def);
            }
        }
        catch { /* evaluation must never take down a pipe thread */ }
    }

    private void CheckAchievements(PlaySession latestSession)
    {
        foreach (var def in AchievementDefinitions.All)
        {
            if (IsEarned(def.Id)) continue;   // locked read; Grant re-checks under the lock
            if (def.GameId != null && def.GameId != latestSession.GameId) continue;

            if (IsUnlocked(def, latestSession))
                Grant(def);
        }
    }

    private void Grant(AchievementDefinition def)
    {
        lock (_earned)
        {
            if (!_earned.TryAdd(def.Id, DateTimeOffset.UtcNow)) return; // already earned (race check)
        }
        Save();
        AchievementEarned?.Invoke(def);
    }

    private bool IsUnlocked(AchievementDefinition def, PlaySession latest)
    {
        return def.Id switch
        {
            // ── Global ──────────────────────────────────────────────────────
            "first_session"       => TotalSessions() >= 1,
            "first_goal"          => GoalsReached() >= 1,
            "five_goals"          => GoalsReached() >= 5,
            "twenty_goals"        => GoalsReached() >= 20,
            "fifty_goals"         => GoalsReached() >= 50,
            "explorer"            => UniqueGamesPlayed() >= 3,
            "collector"           => UniqueGamesPlayed() >= 7,
            "multiworlder"        => latest.PlayerCount >= 3,
            "big_multiworld"      => MaxPlayerCount() >= 8,
            "marathon_runner"     => TotalPlaytime() >= TimeSpan.FromHours(50),
            "dedicated"           => TotalPlaytime() >= TimeSpan.FromHours(200),
            "speedster"           => latest.GoalReached && latest.Duration <= TimeSpan.FromHours(2),
            "veteran"             => GoalsReached() >= 10,
            "legend"              => GoalsReached() >= 100,
            // ── Session duration ─────────────────────────────────────────────
            "session_1h"          => latest.Duration >= TimeSpan.FromHours(1),
            "session_4h"          => latest.Duration >= TimeSpan.FromHours(4),
            // ── D2-specific ─────────────────────────────────────────────────
            "d2_speed"            => FastestGoal("diablo2_archipelago") is { } t && t <= TimeSpan.FromHours(4),
            _                     => false
        };
    }
}

// ── Achievement definitions registry ─────────────────────────────────────────

public static class AchievementDefinitions
{
    // Handwritten definitions. The per-game basics (first goal / session
    // counts / playtime tiers) moved to the generated ladders — only defs
    // with a personality of their own stay here. (Declared before _all —
    // the lazy combiner below captures this field.)
    private static readonly IReadOnlyList<AchievementDefinition> Handwritten =
        new List<AchievementDefinition>
    {
        // ── Global — first steps ─────────────────────────────────────────────
        new() { Id = "first_session",    Title = "First Steps",         Icon = "🌱", Tier = "bronze",
                Description = "Play your first Archipelago session." },
        new() { Id = "first_goal",       Title = "Goal Achieved",       Icon = "🎯", Tier = "bronze",
                Description = "Complete your first Archipelago goal." },
        new() { Id = "five_goals",       Title = "On a Roll",           Icon = "🔥", Tier = "silver",
                Description = "Complete 5 Archipelago goals." },
        new() { Id = "twenty_goals",     Title = "Regular Rando",       Icon = "⚡", Tier = "gold",
                Description = "Complete 20 Archipelago goals." },
        new() { Id = "fifty_goals",      Title = "Rando Addict",        Icon = "💀", Tier = "gold",
                Description = "Complete 50 Archipelago goals." },
        new() { Id = "veteran",          Title = "Veteran",             Icon = "🛡️", Tier = "silver",
                Description = "Complete 10 Archipelago goals." },
        new() { Id = "legend",           Title = "Legend",              Icon = "👑", Tier = "platinum",
                Description = "Complete 100 Archipelago goals." },

        // ── Global — variety ─────────────────────────────────────────────────
        new() { Id = "explorer",         Title = "Explorer",            Icon = "🗺️", Tier = "bronze",
                Description = "Play 3 different games in the launcher." },
        new() { Id = "collector",        Title = "Collector",           Icon = "📦", Tier = "silver",
                Description = "Play 7 different games in the launcher." },

        // ── Global — social ───────────────────────────────────────────────────
        new() { Id = "multiworlder",     Title = "Multiworlder",        Icon = "🌐", Tier = "bronze",
                Description = "Play in a session with at least 3 players." },
        new() { Id = "big_multiworld",   Title = "Party Time",          Icon = "🎉", Tier = "silver",
                Description = "Play in a session with at least 8 players." },

        // ── Global — playtime ─────────────────────────────────────────────────
        new() { Id = "marathon_runner",  Title = "Marathon Runner",     Icon = "🏃", Tier = "silver",
                Description = "Accumulate 50 hours of play across all games." },
        new() { Id = "dedicated",        Title = "Dedicated",           Icon = "🏅", Tier = "gold",
                Description = "Accumulate 200 hours of play across all games." },
        new() { Id = "speedster",        Title = "Speed Freak",         Icon = "⏱️", Tier = "gold",
                Description = "Complete any goal in under 2 hours." },

        // ── Global — session length ────────────────────────────────────────────
        new() { Id = "session_1h",       Title = "In It for the Long Haul", Icon = "⏳", Tier = "bronze",
                Description = "Play a single session for at least 1 hour." },
        new() { Id = "session_4h",       Title = "All-Nighter",          Icon = "🌙", Tier = "silver",
                Description = "Play a single session for at least 4 hours." },

        // ── Diablo II Archipelago — flavor (the basics live in the ladders) ──
        new() { Id = "d2_speed",         Title = "Speed Demon",         Icon = "🔴", Tier = "gold",
                GameId = "diablo2_archipelago",
                Description = "Complete a Diablo II: Lord of Destruction run in under 4 hours." },
    };

    /// Every achievement the launcher knows: the handwritten set above plus
    /// the generated per-game ladders (AchievementLadders). Built lazily so
    /// the ladders see the full plugin registry (App.xaml.cs registers all
    /// plugins before the first UI access).
    public static IReadOnlyList<AchievementDefinition> All => _all.Value;

    private static readonly Lazy<IReadOnlyList<AchievementDefinition>> _all =
        new(() => Handwritten.Concat(AchievementLadders.Definitions).ToList());

    /// Point value of a tier — the page shows these instead of the tier word.
    /// bronze = 10, silver = 25, gold = 50, platinum = 100.
    public static int AchievementPoints(string tier) => tier switch
    {
        "platinum" => 100,
        "gold"     => 50,
        "silver"   => 25,
        "bronze"   => 10,
        _          => 10,
    };

    /// Point value of a single definition (convenience over AchievementPoints).
    public static int PointsOf(AchievementDefinition def) => AchievementPoints(def.Tier);

    /// O(1) definition lookup by id (null when unknown).
    public static AchievementDefinition? Find(string id)
        => _byId.Value.GetValueOrDefault(id);

    private static readonly Lazy<Dictionary<string, AchievementDefinition>> _byId =
        new(() => All.ToDictionary(d => d.Id));
}
