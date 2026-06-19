using System.Text.Json.Serialization;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// CatalogTypes — enriched data types for the Browse catalog.
//
// These complement CatalogEntry (in GameCatalog.cs) and are used for:
//   • Install strategy (how to get the game running)
//   • Credits (who made the game and who made the AP world)
//   • Official links (website, Discord, GitHub)
//   • In-launcher install/setup guides
//   • Estimated playtime and player count hints
// ═══════════════════════════════════════════════════════════════════════════════

// ── Install strategy ──────────────────────────────────────────────────────────

/// Describes how this game should be installed / launched.
public enum InstallStrategy
{
    /// All files are downloaded from GitHub/CDN. Fully automated.
    DirectDownload,

    /// The base game must be owned on Steam / GOG; the launcher downloads
    /// only the AP mod files and patches them in automatically.
    ModOnTop,

    /// The base game must already be installed somewhere on the PC.
    /// The user points the launcher to the game executable; the launcher
    /// then downloads and applies the AP mod.
    PointToExisting,

    /// The user must supply a ROM file (emulated console games).
    /// The launcher downloads the AP patch and applies it to the ROM.
    RomRequired,

    /// No automation is possible (DRM, special launcher, etc.).
    /// The launcher shows an install guide only.
    ManualOnly,

    /// The AP integration is provided by an external AP client
    /// (e.g. Minecraft + ap_mc_client). The launcher opens a browser link.
    ExternalClient,

    /// Runs entirely in the browser — no local install needed.
    WebOnly,
}

// ── Install capability ────────────────────────────────────────────────────────

/// What the LAUNCHER can actually do for this game — the honest, user-facing
/// summary of InstallStrategy. Drives the capability label pill on Browse
/// cards, the detail page badge, and the "Available" filter ("available" =
/// the launcher itself can get you playing, i.e. capability != ManualSetup).
public enum InstallCapability
{
    /// Launcher downloads game + mod; click Install and play (DirectDownload).
    AutoInstall,

    /// User owns/installs the base game; launcher installs + applies the mod
    /// automatically (ModOnTop, PointToExisting).
    AutoMod,

    /// Launcher installs emulator + mod; user supplies the ROM (RomRequired).
    RomRequired,

    /// Launcher cannot automate it (yet) — install guide / links only
    /// (ManualOnly, ExternalClient, WebOnly).
    ManualSetup,
}

// ── Credits ───────────────────────────────────────────────────────────────────

/// One credit entry on a game's store page.
public sealed record GameCredit
{
    /// Short role label shown in bold. e.g. "Original game", "AP world", "Sprite patch"
    [JsonPropertyName("role")]   public string  Role  { get; init; } = "";

    /// Person or organisation name. e.g. "Nintendo", "Berserker99"
    [JsonPropertyName("name")]   public string  Name  { get; init; } = "";

    /// Optional link to their GitHub profile, website, or Twitch.
    [JsonPropertyName("url")]    public string? Url   { get; init; }
}

// ── Official links ────────────────────────────────────────────────────────────

/// Collection of official external links for a game.
public sealed record GameLinks
{
    /// Official game homepage (developer website).
    [JsonPropertyName("official_site")]  public string? OfficialSite   { get; init; }

    /// Archipelago-specific game page on archipelago.gg/games/<name>.
    [JsonPropertyName("ap_game_page")]   public string? ApGamePage     { get; init; }

    /// Archipelago Discord invite link (or specific game thread).
    [JsonPropertyName("ap_discord")]     public string? ApDiscord      { get; init; }

    /// Game's own community Discord.
    [JsonPropertyName("game_discord")]   public string? GameDiscord    { get; init; }

    /// AP world GitHub repository.
    [JsonPropertyName("ap_github")]      public string? ApGithub       { get; init; }

    /// ROM / base game purchase link (e.g. GOG, Steam, itch.io).
    [JsonPropertyName("purchase_url")]   public string? PurchaseUrl    { get; init; }
}

// ── Achievement definitions ───────────────────────────────────────────────────

/// One achievement definition (static metadata — not the earned state).
public sealed record AchievementDefinition
{
    public string Id          { get; init; } = "";
    public string Title       { get; init; } = "";
    public string Description { get; init; } = "";
    /// Icon identifier (maps to an Assets/Achievements/*.png or a Unicode emoji).
    public string Icon        { get; init; } = "🏆";
    /// null = global (any game), otherwise locked to one GameId.
    public string? GameId     { get; init; }
    /// Tier: "bronze" | "silver" | "gold" | "platinum"
    public string Tier        { get; init; } = "bronze";
}

// ── Session statistics (tracked per play session) ─────────────────────────────

/// One completed play session — written to disk when the game exits.
public sealed record PlaySession
{
    public string        GameId       { get; init; } = "";
    public DateTimeOffset StartedAt   { get; init; }
    public DateTimeOffset EndedAt     { get; init; }
    public TimeSpan       Duration    => EndedAt - StartedAt;
    public bool           GoalReached { get; init; }
    public string?        Server      { get; init; }
    public string?        SlotName    { get; init; }
    public int            PlayerCount { get; init; }
}
