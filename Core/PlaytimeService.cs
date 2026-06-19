using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// PlaytimeService — read-only queries over the persisted play sessions
// (Data/sessions.json, written by AchievementStore on every session end).
//
// Surfaces "⏱ 42h 15m played · last played 2 days ago" in the game header and
// the sidebar card tooltips. The file is tiny; a write-timestamp-checked cache
// keeps repeated queries cheap. Everything stays local — no telemetry.
// ═══════════════════════════════════════════════════════════════════════════════

public static class PlaytimeService
{
    private static string SessionsPath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "sessions.json");

    private static List<PlaySession> _cache      = new();
    private static DateTime          _cacheStamp = DateTime.MinValue;
    private static readonly object   _lock       = new();

    private static IReadOnlyList<PlaySession> Sessions
    {
        get
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(SessionsPath)) return Array.Empty<PlaySession>();
                    var stamp = File.GetLastWriteTimeUtc(SessionsPath);
                    if (stamp != _cacheStamp)
                    {
                        _cache = JsonSerializer.Deserialize<List<PlaySession>>(
                            File.ReadAllText(SessionsPath)) ?? new();
                        _cacheStamp = stamp;
                    }
                }
                catch { /* mid-write or corrupt — serve the last good copy */ }
                return _cache;
            }
        }
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    public static int TotalSessions(string gameId)
        => Sessions.Count(s => s.GameId == gameId);

    public static TimeSpan TotalPlaytime(string gameId)
        => TimeSpan.FromTicks(Sessions.Where(s => s.GameId == gameId)
                                      .Sum(s => s.Duration.Ticks));

    /// Most recent session end for this game, or null when never played.
    public static DateTimeOffset? LastPlayed(string gameId)
    {
        var ended = Sessions.Where(s => s.GameId == gameId)
                            .Select(s => s.EndedAt)
                            .ToList();
        return ended.Count > 0 ? ended.Max() : null;
    }

    // ── Humanizers ────────────────────────────────────────────────────────────

    /// "42h 15m" · "15m" · "<1m"
    public static string FormatPlaytime(TimeSpan t)
    {
        if (t.TotalHours   >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return "<1m";
    }

    /// "today" · "yesterday" · "5 days ago" · "12 Mar 2026"
    public static string FormatRelativeDate(DateTimeOffset when)
    {
        var localDate = when.ToLocalTime().Date;
        int days = (DateTime.Now.Date - localDate).Days;
        return days switch
        {
            <= 0 => "today",
            1    => "yesterday",
            < 31 => $"{days} days ago",
            _    => localDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        };
    }
}
