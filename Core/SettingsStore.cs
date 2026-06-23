using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// SettingsStore — persists launcher-level settings to launcher_settings.json.
//
// Game-specific settings (AP credentials, toggles) live in the game plugin's
// own settings file (e.g. d2arch.ini for D2). This file only holds settings
// that belong to the launcher itself (window position, which directory each
// game is installed in, default AP server, etc.).
// ═══════════════════════════════════════════════════════════════════════════════

/// One remembered AP connection (most-recent first). Passwords are NEVER stored.
public sealed record RecentConnection
{
    [JsonPropertyName("server")] public string Server { get; init; } = "";
    [JsonPropertyName("slot")]   public string Slot   { get; init; } = "";
}

public sealed class LauncherSettings
{
    /// Absolute path to the user's OWN original Classic Diablo II + LoD install.
    /// Used only as the source to copy the Blizzard MPQ data files from; the mod
    /// itself installs into Games/diablo2_archipelago. Empty until located.
    [JsonPropertyName("diablo2_path")]
    public string DiabloIIPath { get; set; } = string.Empty;

    /// Last-used AP server URI (shared default across all games).
    [JsonPropertyName("default_ap_server")]
    public string DefaultApServer { get; set; } = "";

    /// Override URL for the game catalog JSON feed.
    /// If null, uses GameCatalog.DefaultCatalogUrl (the public catalog repo).
    [JsonPropertyName("catalog_url")]
    public string? CatalogUrl { get; set; } = null;

    /// Launch Diablo II in windowed mode (-w flag).
    [JsonPropertyName("d2_windowed")]
    public bool D2Windowed { get; set; } = false;

    /// Disable Diablo II in-game music/sound on launch (-ns flag).
    [JsonPropertyName("d2_no_sound")]
    public bool D2NoSound { get; set; } = false;

    /// One-time: the launcher + game folders have been added to (or already exist
    /// in) Windows Defender's exclusions. Re-checked at every startup until true, so
    /// the prompt keeps reappearing until the user accepts (Defender false-positives
    /// the mod injector). Set once the exclusions are confirmed present.
    [JsonPropertyName("defender_exclusions_done")]
    public bool DefenderExclusionsDone { get; set; } = false;

    /// Launch OpenTTD fullscreen (§12). Applied by writing "fullscreen = …"
    /// into the [misc] section of the install's own data\openttd.cfg before
    /// each launch — OpenTTD has no Windows command-line fullscreen switch
    /// (-f is Unix-only dedicated-server forking).
    [JsonPropertyName("openttd_fullscreen")]
    public bool OpenTtdFullscreen { get; set; } = false;

    /// Launch Ship of Harkinian fullscreen. Applied by writing the relevant
    /// CVar into the install's own shipofharkinian.json before each launch
    /// (the JSON CVar store next to soh.exe). Best effort — a write failure
    /// never blocks a launch.
    [JsonPropertyName("soh_fullscreen")]
    public bool SohFullscreen { get; set; } = false;

    /// Absolute path to the user's Ocarina of Time ROM, pre-staged next to
    /// soh.exe so Ship of Harkinian's first-run OTR generator can find it.
    /// The launcher copies the file into its own install tree — the user's
    /// original ROM is never modified (§11). null = not yet provided.
    [JsonPropertyName("soh_rom_path")]
    public string? SohRomPath { get; set; } = null;

    /// Launcher window position.
    [JsonPropertyName("window_left")]   public double WindowLeft   { get; set; } = 100;
    [JsonPropertyName("window_top")]    public double WindowTop    { get; set; } = 100;

    /// Launcher window size + maximized state (restored on startup, clamped to
    /// the current virtual screen so an unplugged monitor can't strand the window).
    [JsonPropertyName("window_width")]     public double WindowWidth     { get; set; } = 1280;
    [JsonPropertyName("window_height")]    public double WindowHeight    { get; set; } = 800;
    [JsonPropertyName("window_maximized")] public bool   WindowMaximized { get; set; } = false;

    /// Most-recently-used AP connections (server + slot only — never passwords).
    /// Front of the list = most recent. Capped at MaxRecentConnections entries.
    [JsonPropertyName("recent_connections")]
    public List<RecentConnection> RecentConnections { get; set; } = new();

    /// AutoMod games (§10): where the user's ORIGINAL game installs live,
    /// keyed by plugin GameId. Filled by the "Find original game…" folder
    /// picker after the plugin validated the folder. The launcher only reads
    /// from these folders — it never modifies the original install (§11).
    [JsonPropertyName("original_game_locations")]
    public Dictionary<string, string> OriginalGameLocations { get; set; } = new();

    /// First-run checklist: true once any game has ever been selected in the library.
    [JsonPropertyName("has_selected_game_once")]
    public bool HasSelectedGameOnce { get; set; } = false;

    /// Items tracker view mode: "list" (DataGrid) or "grid" (icon tiles).
    [JsonPropertyName("tracker_view_mode")]
    public string TrackerViewMode { get; set; } = "list";

    /// DeathLink opt-in. When true the launcher primes the "DeathLink" tag on
    /// every AP connect, so deaths are shared with other DeathLink players.
    /// Off by default; toggled from the AP commands row on the Play tab.
    [JsonPropertyName("deathlink_enabled")]
    public bool DeathLinkEnabled { get; set; } = false;

    /// Master toggle for notification sounds (progression items, traps,
    /// DeathLink deaths, goal completion). System sounds — no audio assets.
    [JsonPropertyName("sound_notifications")]
    public bool SoundNotifications { get; set; } = true;

    /// Automatically reconnect to the AP server after an unexpected drop
    /// (backoff: 5/10/30/60/60s, then gives up until the next manual connect).
    [JsonPropertyName("auto_reconnect")]
    public bool AutoReconnect { get; set; } = true;

    private const int MaxRecentConnections = 8;

    /// Upsert a connection to the front of the MRU list (deduped, capped).
    /// Call only after a SUCCESSFUL connect. Passwords are never recorded.
    public void AddRecentConnection(string server, string slot)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(slot)) return;
        server = server.Trim();
        slot   = slot.Trim();
        RecentConnections.RemoveAll(c =>
            string.Equals(c.Server, server, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Slot,   slot,   StringComparison.OrdinalIgnoreCase));
        RecentConnections.Insert(0, new RecentConnection { Server = server, Slot = slot });
        if (RecentConnections.Count > MaxRecentConnections)
            RecentConnections.RemoveRange(MaxRecentConnections,
                RecentConnections.Count - MaxRecentConnections);
    }

    private static string DefaultD2Path() =>
        SettingsStore.DefaultGamePath("diablo2_archipelago");
}

public static class SettingsStore
{
    private static readonly string _path = Path.Combine(
        AppContext.BaseDirectory, "launcher_settings.json");

    /// Returns the default installation directory for a given game.
    /// All games default to Games/<gameId>/ next to the launcher executable.
    public static string DefaultGamePath(string gameId)
        => Path.Combine(AppContext.BaseDirectory, "Games", gameId);

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true
    };

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<LauncherSettings>(
                    File.ReadAllText(_path), _opts) ?? new();
        }
        catch { /* corrupt file — return defaults */ }
        return new();
    }

    public static void Save(LauncherSettings settings)
    {
        try
        {
            // Atomic write: serialize to a temp file then swap into place, so a
            // crash / power loss mid-write can never truncate the live settings
            // file (Load() would silently fall back to defaults = lost settings).
            string json = JsonSerializer.Serialize(settings, _opts);
            string tmp  = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* non-fatal */ }
    }
}
