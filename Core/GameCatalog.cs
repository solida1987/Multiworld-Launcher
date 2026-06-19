using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// GameCatalog — the "Browse Games" store listing.
//
// WHAT IT IS
// ──────────
// A JSON feed listing all AP games available to install through the launcher.
// Can include:
//   • Marco's own games (Diablo II Archipelago, OpenTTD, future standalone games)
//   • Third-party AP games (any AP world that has a plugin written for this launcher)
//   • ROM games (e.g. Pokemon — launcher provides a built-in emulator)
//
// WHERE THE DATA COMES FROM
// ─────────────────────────
// A hosted catalog.json — the URL is configured in the launcher settings.
// Default: a GitHub raw URL in a dedicated "launcher-catalog" repo.
//
// FORMAT (catalog.json):
//   {
//     "schema_version": 1,
//     "updated": "2026-06-09",
//     "games": [
//       {
//         "id": "diablo2_archipelago",
//         "display_name": "Diablo II Archipelago",
//         "author": "Marco / Solida Games",
//         "category": "Action RPG",
//         "description": "...",
//         "ap_world_name": "Diablo II Archipelago",
//         "plugin_type": "native",        // "native" | "emulated" | "external"
//         "emulator": null,               // "bizhawk" | "mgba" | null
//         "thumbnail_url": "...",
//         "video_url": "...",
//         "screenshot_urls": ["...", "..."],
//         "install_url": "https://github.com/.../releases/latest",
//         "news_url": "https://api.github.com/repos/.../releases",
//         "tags": ["singleplayer", "multiplayer", "modded"],
//         "min_launcher_version": "2.0.0"
//       }
//     ]
//   }
//
// PLUGIN TYPES
// ────────────
//   native   — game has a compiled plugin in this launcher (D2, OpenTTD, etc.)
//   emulated — ROM game; launcher uses a built-in emulator (BizHawk for GBA/GB/SNES etc.)
//   external — AP world that this launcher can manage but delegates launch to
//              an existing AP client / game executable (for games we don't
//              have full integration for yet)
// ═══════════════════════════════════════════════════════════════════════════════

public enum GamePluginType
{
    Native,    // compiled plugin in the launcher
    Emulated,  // ROM game — launched via built-in emulator
    External   // any AP game — managed by launcher but launched externally
}

public enum EmulatorType
{
    None,
    BizHawk,     // multi-system: GBA, GBC, GB, NES, SNES, N64, Genesis, SMS, Atari2600, ...
    MGba,        // lightweight GBA-only alternative
    Dolphin,     // GameCube, Wii
    Cemu,        // Wii U
    DuckStation, // PlayStation 1/PSX
    PCSX2,       // PlayStation 2
    RPCS3,       // PlayStation 3
    PPSSPP,      // PlayStation Portable
    MelonDS,     // Nintendo DS / NDS
    Lime3DS,     // Nintendo 3DS (Citra fork)
    Pico8,       // PICO-8
}

public sealed record CatalogEntry
{
    [JsonPropertyName("id")]           public string   Id           { get; init; } = "";
    [JsonPropertyName("display_name")] public string   DisplayName  { get; init; } = "";
    [JsonPropertyName("author")]       public string   Author       { get; init; } = "";
    [JsonPropertyName("category")]     public string   Category     { get; init; } = "";
    [JsonPropertyName("description")]  public string   Description  { get; init; } = "";
    [JsonPropertyName("ap_world_name")]public string   ApWorldName  { get; init; } = "";

    [JsonPropertyName("plugin_type")]
    public string PluginTypeRaw { get; init; } = "external";
    public GamePluginType PluginType => PluginTypeRaw switch
    {
        "native"   => GamePluginType.Native,
        "emulated" => GamePluginType.Emulated,
        _          => GamePluginType.External
    };

    [JsonPropertyName("emulator")]
    public string? EmulatorRaw { get; init; }
    public EmulatorType Emulator => EmulatorRaw switch
    {
        "bizhawk"     => EmulatorType.BizHawk,
        "mgba"        => EmulatorType.MGba,
        "dolphin"     => EmulatorType.Dolphin,
        "cemu"        => EmulatorType.Cemu,
        "duckstation" => EmulatorType.DuckStation,
        "pcsx2"       => EmulatorType.PCSX2,
        "rpcs3"       => EmulatorType.RPCS3,
        "ppsspp"      => EmulatorType.PPSSPP,
        "melonds"     => EmulatorType.MelonDS,
        "lime3ds"     => EmulatorType.Lime3DS,
        "pico8"       => EmulatorType.Pico8,
        _             => EmulatorType.None
    };

    // ── New catalog fields (added for full game library) ─────────────────────

    /// Release status. "available" | "coming_soon" | "discord_only"
    [JsonPropertyName("status")]          public string   Status          { get; init; } = "coming_soon";

    /// Target platforms e.g. ["PC"], ["SNES"], ["GBA","GBC"].
    [JsonPropertyName("platforms")]       public string[] Platforms       { get; init; } = Array.Empty<string>();

    /// True = base game is free/open-source (OpenTTD, Cave Story, web games…).
    [JsonPropertyName("free")]            public bool     Free            { get; init; } = false;

    /// True = player must supply a ROM/ISO file (console games).
    [JsonPropertyName("requires_rom")]    public bool     RequiresRom     { get; init; } = false;

    /// True = this is a hint mini-game, not a standard AP world.
    [JsonPropertyName("hint_game")]       public bool     HintGame        { get; init; } = false;

    // ── Enriched fields ────────────────────────────────────────────────────────

    /// How the game is installed. Maps to InstallStrategy enum values as strings.
    /// "direct_download" | "mod_on_top" | "point_to_existing" | "rom_required" |
    /// "manual_only" | "external_client" | "web_only"
    [JsonPropertyName("install_strategy")]
    public string InstallStrategyRaw { get; init; } = "manual_only";

    public InstallStrategy InstallStrategy => InstallStrategyRaw switch
    {
        "direct_download"   => InstallStrategy.DirectDownload,
        "mod_on_top"        => InstallStrategy.ModOnTop,
        "point_to_existing" => InstallStrategy.PointToExisting,
        "rom_required"      => InstallStrategy.RomRequired,
        "manual_only"       => InstallStrategy.ManualOnly,
        "external_client"   => InstallStrategy.ExternalClient,
        "web_only"          => InstallStrategy.WebOnly,
        _                   => InstallStrategy.ManualOnly
    };

    /// Credits list. Each entry has role + name + optional URL.
    [JsonPropertyName("credits")]
    public GameCredit[] Credits { get; init; } = Array.Empty<GameCredit>();

    /// Official external links (website, AP page, Discord, GitHub, purchase URL).
    [JsonPropertyName("links")]
    public GameLinks? Links { get; init; }

    /// Markdown-formatted install/setup guide shown inside the launcher.
    /// null = no guide available.
    [JsonPropertyName("install_guide")]
    public string? InstallGuide { get; init; }

    /// Estimated playtime per run in minutes (0 = unknown).
    [JsonPropertyName("est_playtime_min")]
    public int EstPlaytimeMin { get; init; } = 0;

    /// Typical player count hint. e.g. "1–4" or "1" or "2–8".
    [JsonPropertyName("player_count")]
    public string PlayerCount { get; init; } = "1+";

    /// Steam App ID (if applicable) — used to detect if player owns the game.
    [JsonPropertyName("steam_app_id")]
    public int SteamAppId { get; init; } = 0;

    /// URL opened when "Buy / Get Game" is clicked (Steam, GOG, itch.io, etc.).
    [JsonPropertyName("purchase_url")]
    public string? PurchaseUrl { get; init; }

    /// Subcategory for finer grouping within a category. e.g. "Zelda", "Pokémon"
    [JsonPropertyName("subcategory")]
    public string Subcategory { get; init; } = "";

    // ── Original fields ───────────────────────────────────────────────────────

    [JsonPropertyName("thumbnail_url")]    public string    ThumbnailUrl   { get; init; } = "";
    [JsonPropertyName("video_url")]        public string?   VideoUrl       { get; init; }
    [JsonPropertyName("screenshot_urls")]  public string[]  ScreenshotUrls { get; init; } = Array.Empty<string>();
    [JsonPropertyName("install_url")]      public string?   InstallUrl     { get; init; }
    [JsonPropertyName("news_url")]         public string?   NewsUrl        { get; init; }
    [JsonPropertyName("tags")]             public string[]  Tags           { get; init; } = Array.Empty<string>();
    [JsonPropertyName("min_launcher_version")] public string MinLauncherVersion { get; init; } = "2.0.0";

    /// True if this game is part of the official Archipelago distribution
    /// (from archipelago.gg/datapackage or explicitly marked in the catalog).
    [JsonPropertyName("is_official")]
    public bool IsOfficial { get; init; } = false;

    // ── Derived helpers ───────────────────────────────────────────────────────

    /// What the launcher can actually do for this game, derived from
    /// InstallStrategy. See the InstallCapability enum for the meaning of
    /// each bucket — this is the single source of the capability labels.
    public InstallCapability InstallCapability => InstallStrategy switch
    {
        InstallStrategy.DirectDownload  => InstallCapability.AutoInstall,
        InstallStrategy.ModOnTop        => InstallCapability.AutoMod,
        InstallStrategy.PointToExisting => InstallCapability.AutoMod,
        InstallStrategy.RomRequired     => InstallCapability.RomRequired,
        _                               => InstallCapability.ManualSetup,
    };

    /// True if this game is available in the launcher right now.
    public bool IsAvailable   => Status == "available";

    /// Honest availability: the game is released AND the launcher can get the
    /// user playing on its own (auto-install / auto-mod / emulator+ROM).
    /// Manual-setup games are browsable and library-addable but not "available".
    public bool IsLauncherPlayable => Status == "available"
                                   && InstallCapability != InstallCapability.ManualSetup;

    /// True if this game is already installed and registered in GameRegistry.
    public bool IsInstalled   => GameRegistry.Find(Id) is { IsInstalled: true };

    /// True if this needs the official Archipelago client to launch.
    public bool IsBundledWithAP => Tags.Contains("bundled-ap");

    /// True if sourced from the community Discord list (not the official AP distribution).
    public bool IsCommunityGame => Tags.Contains("unofficial");

    /// Primary platform label for the UI badge (first entry, or "PC").
    public string PrimaryPlatform => Platforms.Length > 0 ? Platforms[0] : "PC";
}

// ── Catalog tool entry (AP ecosystem tools, not games) ───────────────────────

public sealed record CatalogTool
{
    [JsonPropertyName("id")]          public string  Id          { get; init; } = "";
    [JsonPropertyName("name")]        public string  Name        { get; init; } = "";
    [JsonPropertyName("description")] public string  Description { get; init; } = "";
    [JsonPropertyName("url")]         public string? Url         { get; init; }
    /// "tracker" | "utility" | "client" | "social" | "mobile" | "hint"
    [JsonPropertyName("type")]        public string  Type        { get; init; } = "utility";
}

// ── Full catalog result ───────────────────────────────────────────────────────

public sealed record CatalogResult(
    IReadOnlyList<CatalogEntry> Games,
    IReadOnlyList<CatalogTool>  Tools
);

public sealed class GameCatalog
{
    // UA header is set exactly ONCE here (P3-2): the old per-fetch TryParseAdd
    // grew the header on every refresh, and DefaultRequestHeaders mutation is
    // not thread-safe against a fetch already in flight.
    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.TryParseAdd("Archipelago-Launcher/2.0");
        return http;
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────

    /// Download and parse the catalog from the configured URL.
    /// Returns empty lists on network failure (non-fatal — user may be offline).
    public static async Task<CatalogResult> FetchAsync(
        string catalogUrl,
        CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(catalogUrl, ct);
            using var doc = JsonDocument.Parse(json);

            var games = new List<CatalogEntry>();
            if (doc.RootElement.TryGetProperty("games", out var gamesEl))
                foreach (var el in gamesEl.EnumerateArray())
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<CatalogEntry>(el.GetRawText());
                        if (entry != null) games.Add(entry);
                    }
                    catch { /* skip malformed entries */ }
                }

            var tools = new List<CatalogTool>();
            if (doc.RootElement.TryGetProperty("tools", out var toolsEl))
                foreach (var el in toolsEl.EnumerateArray())
                {
                    try
                    {
                        var tool = JsonSerializer.Deserialize<CatalogTool>(el.GetRawText());
                        if (tool != null) tools.Add(tool);
                    }
                    catch { /* skip malformed entries */ }
                }

            return new CatalogResult(games, tools);
        }
        catch
        {
            return new CatalogResult(
                Array.Empty<CatalogEntry>(),
                Array.Empty<CatalogTool>()); // offline — show empty store
        }
    }

    // ── Load from local file (offline fallback) ───────────────────────────────

    /// Read and parse the catalog from a local JSON file.
    /// Used as offline fallback when the hosted URL is unreachable.
    public static async Task<CatalogResult> FetchFromFileAsync(
        string filePath,
        CancellationToken ct = default)
    {
        try
        {
            string json = await File.ReadAllTextAsync(filePath, ct);
            // Reuse the same parse logic as FetchAsync by going through a
            // temporary stream — avoids duplicating the parse code.
            using var doc = JsonDocument.Parse(json);

            var games = new List<CatalogEntry>();
            if (doc.RootElement.TryGetProperty("games", out var gamesEl))
                foreach (var el in gamesEl.EnumerateArray())
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<CatalogEntry>(el.GetRawText());
                        if (entry != null) games.Add(entry);
                    }
                    catch { }
                }

            var tools = new List<CatalogTool>();
            if (doc.RootElement.TryGetProperty("tools", out var toolsEl))
                foreach (var el in toolsEl.EnumerateArray())
                {
                    try
                    {
                        var tool = JsonSerializer.Deserialize<CatalogTool>(el.GetRawText());
                        if (tool != null) tools.Add(tool);
                    }
                    catch { }
                }

            return new CatalogResult(games, tools);
        }
        catch
        {
            return new CatalogResult(
                Array.Empty<CatalogEntry>(),
                Array.Empty<CatalogTool>());
        }
    }

    // ── Filter helpers for the store UI ───────────────────────────────────────

    /// Filter by free-text search across name, author, description, tags.
    /// Platforms match BOTH the raw catalog spelling and the normalized name
    /// ("PSX" entries are found by "ps1") — rare platforms have no filter chip
    /// and rely on this search to stay reachable.
    public static IEnumerable<CatalogEntry> Search(
        IReadOnlyList<CatalogEntry> catalog,
        string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return catalog;
        string q = query.ToLowerInvariant();
        return catalog.Where(e =>
            e.DisplayName.ToLowerInvariant().Contains(q)  ||
            e.Author.ToLowerInvariant().Contains(q)       ||
            e.Description.ToLowerInvariant().Contains(q)  ||
            e.Tags.Any(t => t.ToLowerInvariant().Contains(q)) ||
            e.Platforms.Any(p => p.ToLowerInvariant().Contains(q) ||
                                 NormalizePlatform(p).ToLowerInvariant().Contains(q)));
    }

    /// Filter by category (e.g. "Action RPG", "Strategy", "ROM").
    public static IEnumerable<CatalogEntry> ByCategory(
        IReadOnlyList<CatalogEntry> catalog,
        string category)
        => catalog.Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

    /// Filter by platform (e.g. "SNES", "GBA", "PC").
    public static IEnumerable<CatalogEntry> ByPlatform(
        IReadOnlyList<CatalogEntry> catalog,
        string platform)
        => catalog.Where(e => e.Platforms.Any(p =>
               p.Equals(platform, StringComparison.OrdinalIgnoreCase)));

    /// Only available/coming-soon games (not Discord-only).
    public static IEnumerable<CatalogEntry> PublicOnly(IReadOnlyList<CatalogEntry> catalog)
        => catalog.Where(e => e.Status != "discord_only");

    /// All distinct categories in the catalog — for the filter sidebar.
    public static IReadOnlyList<string> Categories(IReadOnlyList<CatalogEntry> catalog)
        => catalog.Select(e => e.Category)
                  .Where(c => c.Length > 0)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .OrderBy(c => c)
                  .ToList();

    /// All distinct platforms in the catalog — for the platform filter.
    public static IReadOnlyList<string> Platforms(IReadOnlyList<CatalogEntry> catalog)
        => catalog.SelectMany(e => e.Platforms)
                  .Where(p => p.Length > 0)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .OrderBy(p => p)
                  .ToList();

    /// Distinct NORMALIZED platforms carried by at least <paramref name="minGames"/>
    /// catalog entries, most-populated first (ties alphabetical). Drives the
    /// platform chip row in Browse; platforms below the threshold stay reachable
    /// through free-text search (Search() already matches Platforms).
    public static IReadOnlyList<string> CommonPlatforms(
        IReadOnlyList<CatalogEntry> catalog, int minGames)
        => catalog.SelectMany(e => e.Platforms)
                  .Where(p => p.Length > 0)
                  .Select(NormalizePlatform)
                  .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                  .Where(g => g.Count() >= minGames)
                  .OrderByDescending(g => g.Count())
                  .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                  .Select(g => g.Key)
                  .ToList();

    /// True when the entry targets the given normalized platform (any spelling).
    public static bool EntryHasPlatform(CatalogEntry e, string normalizedPlatform)
        => e.Platforms.Any(p => NormalizePlatform(p)
               .Equals(normalizedPlatform, StringComparison.OrdinalIgnoreCase));

    // ── Official AP game list merge ───────────────────────────────────────────

    /// Fetches the official AP game list from archipelago.gg and merges it with
    /// an existing catalog, adding any games that are not already in the list.
    /// New entries get status="coming_soon" and ap_game_page URL generated.
    /// Returns the merged catalog (existing + any new entries from AP official).
    public static async Task<IReadOnlyList<CatalogEntry>> MergeWithOfficialApGamesAsync(
        IReadOnlyList<CatalogEntry> existing,
        CancellationToken ct = default)
    {
        try
        {
            // The AP datapackage lists all registered games (game names are the keys)
            const string dataPkgUrl = "https://archipelago.gg/datapackage";

            string json = await _http.GetStringAsync(dataPkgUrl, ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("games", out var gamesEl))
                return existing;

            // Build a lookup of existing entries by ap_world_name (case-insensitive)
            var existingByName = existing
                .Where(e => e.ApWorldName.Length > 0)
                .ToDictionary(e => e.ApWorldName, e => e, StringComparer.OrdinalIgnoreCase);

            var merged = new List<CatalogEntry>(existing);

            foreach (var prop in gamesEl.EnumerateObject())
            {
                string gameName = prop.Name;
                if (gameName == "Archipelago") continue; // built-in "game"
                if (existingByName.ContainsKey(gameName)) continue; // already have it

                // Build a minimal entry from the AP game name alone
                string id      = ToGameId(gameName);
                string apPage  = $"https://archipelago.gg/games/{Uri.EscapeDataString(gameName)}";

                merged.Add(new CatalogEntry
                {
                    Id                 = id,
                    DisplayName        = gameName,
                    Author             = "Archipelago Community",
                    Category           = "Other",
                    Description        = $"Play {gameName} in a multiworld randomizer via Archipelago.",
                    ApWorldName        = gameName,
                    PluginTypeRaw      = "external",
                    Status             = "coming_soon",
                    InstallStrategyRaw = "manual_only",
                    IsOfficial         = true,
                    Links              = new GameLinks { ApGamePage = apPage },
                });
            }

            return merged;
        }
        catch
        {
            return existing; // offline / AP server down — use existing list
        }
    }

    /// Convert a game name like "Super Metroid" → "super_metroid" (safe ID slug).
    private static string ToGameId(string name)
        => System.Text.RegularExpressions.Regex.Replace(
               name.ToLowerInvariant().Replace(' ', '_'),
               @"[^a-z0-9_]", "");

    // ── Official game list (defines "Official" in Browse) ─────────────────────
    //
    // CatalogRepo/official_games.txt is the authoritative list of games that
    // archipelago.gg's own website lists as official. An entry is "Official"
    // iff its name matches this list — and this OVERRIDES every other signal
    // (the json is_official field, the datapackage merge, the bundled-ap tag).
    //
    // The catch: official_games.txt uses SHORT names ("Ocarina of Time") while
    // the catalog / community list uses LONG canonical names ("The Legend of
    // Zelda: Ocarina of Time"). Matching bridges the two with aggressive
    // normalization plus an explicit alias table for the irreducible cases.

    private static readonly object               _officialLock = new();
    private static HashSet<string>?              _officialKeys;       // norm(official names + aliases)
    private static IReadOnlyList<string>?        _officialNamesRaw;   // raw lines, for diagnostics
    private static IReadOnlyList<string>         _unmatchedOfficial = Array.Empty<string>();

    /// Normalized "The Legend of Zelda" (after the leading "the" is stripped).
    /// Catalog titles carry this as a prefix where official_games.txt does not.
    private const string ZeldaPrefix = "legendofzelda";

    /// Alias table: official_games.txt name → the longer catalog / AP names it
    /// must ALSO match. Pure normalization already bridges most pairs (it drops
    /// punctuation and diacritics), but the names below differ by more than that
    /// and need an explicit bridge. A handful of already-bridged names are listed
    /// too, as documentation of intent.
    private static readonly Dictionary<string, string[]> _officialAliases =
        new(StringComparer.Ordinal)
    {
        ["Castlevania 64"]                   = new[] { "Castlevania" },
        ["Castlevania - Circle of the Moon"] = new[] { "Castlevania: Circle of the Moon" },
        ["Civilization VI"]                  = new[] { "Sid Meier's Civilization VI" },
        ["DLCQuest"]                         = new[] { "DLC Quest" },
        ["Final Fantasy Mystic Quest"]       = new[] { "Final Fantasy: Mystic Quest" },
        ["Kingdom Hearts"]                   = new[] { "KINGDOM HEARTS FINAL MIX" },
        ["Kingdom Hearts 2"]                 = new[] { "KINGDOM HEARTS II FINAL MIX" },
        ["Links Awakening DX"]               = new[] { "Links Awakening DX", "The Legend of Zelda: Link's Awakening DX" },
        ["Lufia II Ancient Cave"]            = new[] { "Lufia II: Rise of the Sinistrals" },
        ["Mario & Luigi Superstar Saga"]     = new[] { "Mario & Luigi: Superstar Saga" },
        ["MegaMan Battle Network 3"]         = new[] { "Mega Man Battle Network 3 Blue", "MegaMan Battle Network 3" },
        ["Ocarina of Time"]                  = new[] { "The Legend of Zelda: Ocarina of Time" },
        ["Paint"]                            = new[] { "JS Paint" },
        ["Pokemon Emerald"]                  = new[] { "Pokémon Emerald" },
        ["Pokemon Red and Blue"]             = new[] { "Pokémon Red and Blue" },
        ["Sonic Adventure 2 Battle"]         = new[] { "Sonic Adventure 2: Battle", "Sonic Adventure 2 Battle" },
        ["Starcraft 2"]                      = new[] { "StarCraft II", "Starcraft 2" },
        ["Super Mario Land 2"]               = new[] { "Super Mario Land 2: 6 Golden Coins" },
        ["The Legend of Zelda"]              = new[] { "The Legend of Zelda" },
        ["The Wind Waker"]                   = new[] { "The Legend of Zelda: The Wind Waker" },
        ["Yoshi's Island"]                   = new[] { "Super Mario World 2: Yoshi's Island" },
        ["Yu-Gi-Oh! 2006"]                   = new[] { "Yu-Gi-Oh! Ultimate Masters: WCT 2006" },
        ["SMZ3"]                             = new[] { "SMZ3" },
        ["Adventure"]                        = new[] { "Adventure" },
    };

    /// Strip a leading "the" (the string is already lowercase + alphanumeric-only).
    private static string StripLeadingThe(string s)
        => (s.Length > 3 && s.StartsWith("the", StringComparison.Ordinal)) ? s[3..] : s;

    /// Normalize a game name for official-list matching: lowercase, strip
    /// diacritics (é→e), drop every non-alphanumeric character, then strip a
    /// leading "the". Equality on this form is the primary match test.
    /// Exposed for the verification harness.
    public static string NormalizeForOfficialMatch(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";

        // Decompose so combining marks separate from their base letter (é → e + ´),
        // then drop the marks and keep only lowercased alphanumerics.
        string decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return StripLeadingThe(sb.ToString());
    }

    /// All candidate match-keys for one name: the normalized form, plus — when
    /// the name carries the full "The Legend of Zelda: <title>" prefix — the
    /// remainder after that prefix (so "The Legend of Zelda: Ocarina of Time"
    /// also lines up with the short official "Ocarina of Time"). Matching is
    /// always by full-string EQUALITY of a key, never Contains.
    private static IEnumerable<string> OfficialMatchKeys(string name)
    {
        string norm = NormalizeForOfficialMatch(name);
        if (norm.Length == 0) yield break;
        yield return norm;

        if (norm.Length > ZeldaPrefix.Length &&
            norm.StartsWith(ZeldaPrefix, StringComparison.Ordinal))
        {
            string rest = StripLeadingThe(norm[ZeldaPrefix.Length..]);
            if (rest.Length > 0) yield return rest;
        }
    }

    /// All match-keys an official name brings to the table: its own keys plus
    /// the keys of every alias mapped to it.
    private static IEnumerable<string> OfficialNameKeys(string officialName)
    {
        foreach (var k in OfficialMatchKeys(officialName)) yield return k;
        if (_officialAliases.TryGetValue(officialName.Trim(), out var aliases))
            foreach (var alias in aliases)
                foreach (var k in OfficialMatchKeys(alias)) yield return k;
    }

    /// Build the lookup set: every match-key of every official name + alias.
    private static HashSet<string> BuildOfficialKeySet(IEnumerable<string> officialNames)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in officialNames)
            foreach (var k in OfficialNameKeys(name))
                set.Add(k);
        return set;
    }

    /// Which official names found NO matching entry in the given catalog.
    private static IReadOnlyList<string> ComputeUnmatched(
        IReadOnlyList<string> officialNames, IReadOnlyList<CatalogEntry> entries)
    {
        var catalogKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            foreach (var k in OfficialMatchKeys(e.DisplayName)) catalogKeys.Add(k);
            foreach (var k in OfficialMatchKeys(e.ApWorldName)) catalogKeys.Add(k);
        }

        var unmatched = new List<string>();
        foreach (var name in officialNames)
            if (!OfficialNameKeys(name).Any(catalogKeys.Contains))
                unmatched.Add(name);
        return unmatched;
    }

    private static bool EntryMatchesOfficial(CatalogEntry e, HashSet<string> set)
    {
        foreach (var k in OfficialMatchKeys(e.DisplayName)) if (set.Contains(k)) return true;
        foreach (var k in OfficialMatchKeys(e.ApWorldName)) if (set.Contains(k)) return true;
        return false;
    }

    /// Load official_games.txt from the output directory (copied there via the
    /// CatalogRepo Content glob in the csproj). Cached after the first call.
    private static void EnsureOfficialLoaded()
    {
        if (_officialKeys != null) return;
        lock (_officialLock)
        {
            if (_officialKeys != null) return;

            var names = new List<string>();
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory,
                    "CatalogRepo", "official_games.txt");
                if (File.Exists(path))
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        string n = raw.Trim();
                        if (n.Length > 0) names.Add(n);
                    }
            }
            catch { /* missing/unreadable → empty official set (nothing flagged) */ }

            _officialNamesRaw = names;
            _officialKeys     = BuildOfficialKeySet(names);
        }
    }

    /// Re-flag IsOfficial on every entry based SOLELY on official_games.txt
    /// (overrides any prior is_official semantics). Returns a new list; the
    /// input records are left untouched. After this call UnmatchedOfficialNames
    /// reports any official names that found no home in <paramref name="entries"/>.
    public static IReadOnlyList<CatalogEntry> ApplyOfficialList(IReadOnlyList<CatalogEntry> entries)
    {
        EnsureOfficialLoaded();
        var set = _officialKeys!;

        var result = new List<CatalogEntry>(entries.Count);
        foreach (var e in entries)
        {
            bool official = EntryMatchesOfficial(e, set);
            result.Add(e.IsOfficial == official ? e : e with { IsOfficial = official });
        }

        _unmatchedOfficial = ComputeUnmatched(_officialNamesRaw ?? Array.Empty<string>(), entries);
        return result;
    }

    /// Official names that had no matching catalog entry at the last
    /// ApplyOfficialList call. Empty = full coverage. (Diagnostics.)
    public static IReadOnlyList<string> UnmatchedOfficialNames => _unmatchedOfficial;

    /// Total number of names loaded from official_games.txt.
    public static int OfficialCount => _officialNamesRaw?.Count ?? 0;

    /// Verification harness entry point: load an official list + a
    /// community_games.txt (and optionally a catalog.json) from explicit paths,
    /// run matching, and report coverage. Does NOT touch the cached production
    /// state, so it is safe to call from tooling. Harmless in the shipped app.
    public static (int total, int matched, IReadOnlyList<string> unmatched) DebugMatchOfficial(
        string officialListPath, string communityGamesPath, string? catalogJsonPath = null)
    {
        var names = File.ReadAllLines(officialListPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var entries = new List<CatalogEntry>();
        if (catalogJsonPath != null && File.Exists(catalogJsonPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(catalogJsonPath));
            if (doc.RootElement.TryGetProperty("games", out var gamesEl))
                foreach (var el in gamesEl.EnumerateArray())
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<CatalogEntry>(el.GetRawText());
                        if (entry != null) entries.Add(entry);
                    }
                    catch { }
                }
        }
        entries.AddRange(ParseCommunityGamesText(File.ReadAllText(communityGamesPath)).Games);

        var unmatched = ComputeUnmatched(names, entries);
        return (names.Count, names.Count - unmatched.Count, unmatched);
    }

    // ── Community games.txt parser ────────────────────────────────────────────

    /// Parse the AP Discord community games list (the games.txt format).
    ///
    /// FILE FORMAT
    /// ───────────
    /// Lines before "Hint Games:" header   → AP ecosystem tools
    /// Lines after  "Hint Games:" header   → community AP games (some are hint-games)
    ///
    /// Each data line is one of:
    ///   Name (Platform): https://...            — game with GitHub/download URL
    ///   Name (Platform): Bundled with Archipelago
    ///   Name (Platform): Discord Thread Only
    ///   ToolName: https://...                   — tool (no platform parens)
    ///
    /// Games whose names contain colons (e.g. "The Legend of Heroes: Trails…") are handled
    /// by searching backwards for the last colon that precedes a known value token.
    public static CatalogResult ParseCommunityGamesText(string text)
    {
        var games = new List<CatalogEntry>();
        var tools = new List<CatalogTool>();

        bool inHintSection = false;
        bool toolsDone     = false;  // true once "Hint Games:" header seen

        foreach (var rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // "Hint Games:" section header
            if (line.TrimEnd(':').Equals("Hint Games", StringComparison.OrdinalIgnoreCase))
            {
                inHintSection = true;
                toolsDone     = true;
                continue;
            }

            // Find the separator colon.
            // Lines in the hint section with no colon are pure game names (no URL).
            int colonIdx = FindValueColon(line);
            if (colonIdx <= 0)
            {
                if (!inHintSection) continue;
                // Treat the whole line as a name-only hint game entry
                string hn = line.Trim();
                if (hn.Length == 0) continue;
                games.Add(new CatalogEntry
                {
                    Id                 = ToGameId(hn),
                    DisplayName        = hn,
                    Author             = "Archipelago Community",
                    Category           = "Hint Game",
                    Description        = $"Play {hn} as an Archipelago multiworld randomizer.",
                    ApWorldName        = hn,
                    PluginTypeRaw      = "external",
                    Status             = "available",
                    HintGame           = true,
                    Platforms          = new[] { "PC" },
                    Free               = true,
                    IsOfficial         = false,
                    InstallStrategyRaw = "manual_only",
                    Tags               = new[] { "unofficial", "hint-game" },
                    Links              = new GameLinks
                    {
                        ApGamePage = $"https://archipelago.gg/games/{Uri.EscapeDataString(hn)}"
                    },
                });
                continue;
            }

            string namePart = line[..colonIdx].Trim();
            string rest     = line[(colonIdx + 1)..].Trim();

            // ── Reversed joke entry ───────────────────────────────────────────
            // The community list contains one line written entirely backwards as
            // an in-joke: "(SENS) yrtnuoC gnoK yeknoD" is "Donkey Kong Country
            // (SNES)" reversed. Left as-is it renders as a garbage card that
            // sorts to the very top of the store. Detect the tell-tale "(SENS)"
            // prefix, char-reverse the name back to normal, and swap the
            // parentheses (a full string reversal flips "(" and ")") so the
            // "(SNES)" platform tag lands at the end and parses like every other
            // entry.
            if (namePart.StartsWith("(SENS)", StringComparison.Ordinal))
                namePart = ReverseJokeName(namePart);

            // Extract "(Platform)" from the name if present
            string displayName = namePart;
            string platform    = "";
            int lp = namePart.LastIndexOf('(');
            int rp = namePart.LastIndexOf(')');
            if (lp > 0 && rp > lp && rp == namePart.Length - 1)
            {
                platform    = namePart[(lp + 1)..rp].Trim();
                displayName = namePart[..lp].Trim();
            }

            // "(Discord)" is a coordination venue, not a real platform
            bool hasPlatform = platform.Length > 0 &&
                               !platform.Equals("Discord", StringComparison.OrdinalIgnoreCase);

            // Determine status + URL
            string url      = "";
            string status   = "available";
            bool   isBundled = false;

            if (rest.StartsWith("Bundled", StringComparison.OrdinalIgnoreCase))
            {
                isBundled = true;
            }
            else if (rest.Equals("Discord Thread Only", StringComparison.OrdinalIgnoreCase) ||
                     (rest.Contains("Discord", StringComparison.OrdinalIgnoreCase)
                      && !rest.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
            {
                status = "discord_only";
                int httpIdx = rest.IndexOf("http", StringComparison.OrdinalIgnoreCase);
                if (httpIdx >= 0) url = rest[httpIdx..].Split(' ')[0].TrimEnd();
            }
            else if (rest.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                url = rest.Split(' ')[0].TrimEnd();
            }

            bool isHintSectionItem = inHintSection && !hasPlatform;

            // ── Tool or game? ─────────────────────────────────────────────────
            if (!toolsDone && !hasPlatform)
            {
                tools.Add(new CatalogTool
                {
                    Id          = ToGameId(displayName),
                    Name        = displayName,
                    Description = "Archipelago ecosystem tool.",
                    Url         = url.Length > 0 ? url : null,
                    Type        = "utility",
                });
                continue;
            }

            // It's a game
            string normPlatform = NormalizePlatform(hasPlatform ? platform : "PC");
            string category     = isHintSectionItem ? "Hint Game" : PlatformToCategory(normPlatform);
            string apPage       = $"https://archipelago.gg/games/{Uri.EscapeDataString(displayName)}";

            var tags = new List<string> { "unofficial" };
            if (isBundled)      tags.Add("bundled-ap");
            if (inHintSection)  tags.Add("hint-game");

            games.Add(new CatalogEntry
            {
                Id                 = ToGameId(displayName),
                DisplayName        = displayName,
                Author             = "Archipelago Community",
                Category           = category,
                Description        = $"Play {displayName} as an Archipelago multiworld randomizer.",
                ApWorldName        = displayName,
                PluginTypeRaw      = "external",
                Status             = status,
                HintGame           = isHintSectionItem,
                Platforms          = new[] { normPlatform },
                Free               = isBundled,
                IsOfficial         = isBundled,
                InstallStrategyRaw = isBundled ? "external_client" : "manual_only",
                Tags               = tags.ToArray(),
                InstallUrl         = url.Length > 0 ? url : null,
                Links              = new GameLinks { ApGamePage = apPage },
            });
        }

        return new CatalogResult(games, tools);
    }

    /// Find the colon that separates the game name from the value (URL / status text).
    /// Searches backwards so that game titles containing colons are handled correctly.
    private static int FindValueColon(string line)
    {
        for (int i = line.Length - 1; i >= 1; i--)
        {
            if (line[i] != ':') continue;
            string after = line[(i + 1)..].TrimStart();
            if (after.StartsWith("http",    StringComparison.OrdinalIgnoreCase) ||
                after.StartsWith("Bundled", StringComparison.OrdinalIgnoreCase) ||
                after.StartsWith("Discord", StringComparison.OrdinalIgnoreCase) ||
                after.Length == 0)
                return i;
        }
        return -1;
    }

    /// Reverse a fully-backwards community entry name and restore parenthesis
    /// orientation: "(SENS) yrtnuoC gnoK yeknoD" → "Donkey Kong Country (SNES)".
    /// A plain char reversal flips "(" and ")", so swap them back afterwards.
    private static string ReverseJokeName(string s)
    {
        var chars = s.ToCharArray();
        Array.Reverse(chars);
        for (int i = 0; i < chars.Length; i++)
        {
            if      (chars[i] == '(') chars[i] = ')';
            else if (chars[i] == ')') chars[i] = '(';
        }
        return new string(chars);
    }

    /// Canonical display name for a platform tag. The catalog json and the
    /// community games list use mixed spellings ("PSX" vs "PS1", "GEN" vs
    /// "Genesis", "NDS" vs "DS") — the platform filter chips and their match
    /// test both go through this so one chip covers every spelling.
    public static string NormalizePlatform(string platform)
        => platform switch
        {
            "2600"                   => "Atari 2600",
            "PSX"                    => "PS1",
            "GEN"                    => "Genesis",
            "NDS"                    => "DS",
            "GC" or "NGC"            => "GameCube",
            "PICO-8" or "PICO8"      => "PICO-8",
            _                        => platform,
        };

    private static string PlatformToCategory(string platform)
        => platform switch
        {
            "PC" or "Mac" or "Linux" => "PC Game",
            "Web" or "Browser"       => "Web Game",
            "Mobile" or "Android"    => "Mobile",
            "VR"                     => "VR Game",
            "PICO-8"                 => "PC Game",
            _                        => "Console Game",
        };

    // ── Default catalog URL ───────────────────────────────────────────────────
    public const string DefaultCatalogUrl =
        "https://raw.githubusercontent.com/solida1987/Multiworld-Launcher/main/CatalogRepo/catalog.json";
}
