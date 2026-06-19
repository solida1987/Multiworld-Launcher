using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// IGamePlugin — the central contract for every game the launcher supports.
//
// DESIGN INTENT
// ─────────────
// The launcher core knows NOTHING about specific games. It only knows IGamePlugin.
// Each game (Diablo II, future games) ships as a plugin that implements this
// interface. The launcher calls the methods; the plugin fires events back.
//
// THREADING
// ─────────
// All async methods are fire-and-forget from the UI thread (via Task.Run).
// Events fire on arbitrary threads — handlers must marshal to UI if needed:
//   Application.Current.Dispatcher.Invoke(() => { ... });
//
// PLUGIN DISCOVERY (V2.0.0 → future)
// ──────────────────────────────────
// V2.0.0: plugins are compiled into the launcher and registered in App.xaml.cs.
// Future: GameRegistry scans a Plugins/ folder, loads assemblies via reflection.
// The interface is designed so the discovery mechanism is transparent to plugins.
// ═══════════════════════════════════════════════════════════════════════════════

public interface IGamePlugin
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// Stable, never-changing key. Used in manifests, settings files, update
    /// checks. Must be lowercase ASCII with no spaces. e.g. "diablo2_archipelago"
    string GameId { get; }

    /// Human-readable name shown in the game library. e.g. "Diablo II Archipelago"
    string DisplayName { get; }

    /// Short subtitle / genre tag shown under DisplayName. e.g. "Randomiser Mod"
    string Subtitle { get; }

    /// Absolute path to a 256×256 PNG icon (shipped with the launcher assets).
    string IconPath { get; }

    // ── Version state ─────────────────────────────────────────────────────────
    // Updated by CheckForUpdateAsync and InstallOrUpdateAsync.

    /// The currently installed mod/game version. null = not installed.
    /// e.g. "1.9.13"  (matches the GitHub release tag format used by this mod)
    string? InstalledVersion { get; }

    /// Absolute path to the directory where this game is installed.
    /// Empty string when the game is not installed.
    string GameDirectory { get; }

    /// The latest available version found on GitHub. null = not yet checked.
    string? AvailableVersion { get; }

    /// Convenience: InstalledVersion != null.
    bool IsInstalled { get; }

    /// True while Game.exe (or equivalent) is running.
    bool IsRunning { get; }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// Poll GitHub for the latest release. Sets AvailableVersion on success.
    /// Should not throw on network failure — set AvailableVersion to null and return.
    Task CheckForUpdateAsync(CancellationToken ct = default);

    /// Download and install (or update to) the latest release from GitHub.
    /// progress: (percent 0–100, status message)
    /// Must be idempotent — safe to call even if already up-to-date.
    Task InstallOrUpdateAsync(IProgress<(int Pct, string Msg)> progress,
                              CancellationToken ct = default);

    /// Verify the local install is intact (size/SHA check against manifest).
    /// Returns true = OK, false = damaged/missing files detected.
    /// Does NOT repair — caller decides whether to trigger InstallOrUpdateAsync.
    Task<bool> VerifyInstallAsync(CancellationToken ct = default);

    /// AutoMod games only (§10): the user located their existing install of
    /// the ORIGINAL game via the launcher's folder picker — check that
    /// `folder` actually looks like that game (right exe, right version, …).
    /// Return null when the folder is acceptable; otherwise a short,
    /// human-readable reason that is shown to the user so they can pick again.
    /// Default accepts any folder — only plugins with real requirements
    /// override this. The launcher stores the accepted location and never
    /// modifies the original install (§11).
    string? ValidateExistingInstall(string folder) => null;

    /// Launch the game. The launcher passes an already-configured ApSession
    /// so the plugin knows which AP server/slot to bridge to.
    /// The launcher's ApClient for this session is already connected before
    /// LaunchAsync is called; the plugin just needs to open the IPC channel to
    /// the in-game side and start forwarding messages.
    Task LaunchAsync(ApSession session, CancellationToken ct = default);

    /// True if this game can be launched without an Archipelago connection.
    /// When true, a "Launch Standalone" button is shown in the game header.
    bool SupportsStandalone => false;

    /// True when the game ships its OWN native AP client and connects to the
    /// slot itself (e.g. the OpenTTD Archipelago fork). AP servers allow one
    /// connection per slot and kick the older one — so for these games the
    /// launcher must NOT hold an ApClient session on the same slot while the
    /// game runs (launcher and game would endlessly kick each other off).
    /// The launcher launches with credential prefill only and suppresses its
    /// auto-reconnect + "connection lost" toast while the game is running.
    bool ConnectsItself => false;

    /// Upstream-update detection (§15): the AP datapackage checksum this
    /// plugin's game integration (RAM map / patch logic) was derived from.
    /// The server announces current checksums in RoomInfo — a mismatch means
    /// the apworld changed upstream and the integration may be stale, which
    /// the launcher surfaces as a warning instead of silently missing checks.
    /// Null = nothing to verify (no checksum-coupled integration).
    string? BuiltAgainstDataPackageChecksum => null;

    /// Launch the game directly without connecting to Archipelago.
    /// Called only when SupportsStandalone is true and the user clicks
    /// "Launch Standalone". Default implementation is a no-op.
    Task LaunchStandaloneAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// Gracefully stop the game. Plugin closes IPC, sends AP goal/disconnect
    /// if needed, then terminates the process (or waits for clean exit).
    Task StopAsync();

    // ── AP bridge — plugin side ───────────────────────────────────────────────

    /// Fired by the plugin when the game completes one or more location checks.
    /// The launcher's ApClient forwards these to the AP server automatically.
    /// long[] = AP location IDs (numeric, not names).
    event Action<long[]>? LocationsChecked;

    /// Fired when the game process exits (cleanly or crashed).
    /// int = process exit code.
    event Action<int>? GameExited;

    /// Fired when the game signals that the Archipelago goal is complete.
    /// The launcher wires this to SendStatusUpdateAsync(ApClientStatus.Goal).
    event Action? GoalCompleted;

    /// Called by the launcher when the AP server delivers items to this game slot.
    /// index: AP resume index. The plugin must track this to avoid re-delivering
    ///        items already processed in a previous session (AP dedup contract).
    Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default);

    /// Called when the AP connection state changes.
    /// Plugins can use this to show an in-game HUD indicator (connected/disconnected).
    void OnApStateChanged(ApConnectionState state);

    // ── Settings UI ──────────────────────────────────────────────────────────

    /// Return a WPF UIElement shown in the launcher's Settings tab for this game.
    /// May return null if the game has no launcher-side settings.
    /// Called on the UI thread.
    UIElement? CreateSettingsPanel();

    // ── Game store / catalog ──────────────────────────────────────────────────
    // Used by the "Browse Games" store page to show what the game is about.

    /// One-paragraph description shown in the store listing.
    string Description { get; }

    /// URL of a YouTube or direct video link previewed in the store page.
    /// null if no video is available.
    string? VideoPreviewUrl { get; }

    /// URLs of screenshot images shown in the store carousel.
    string[] ScreenshotUrls { get; }

    /// The AP world name as registered in Archipelago (used for DataPackage lookup).
    /// e.g. "Diablo II Archipelago"  or  "OpenTTD"
    string ApWorldName { get; }

    // ── Visual identity ───────────────────────────────────────────────────────
    // Used by the launcher UI to theme the game's detail header and sidebar card.

    /// Hex accent color blended into the game header background. e.g. "#7A1010".
    /// The launcher mixes this with the neutral base at ~25% strength.
    string ThemeAccentColor { get; }

    /// Short requirement/info badges shown in the header next to version badges.
    /// e.g. ["Requires D2"] or []. Keep to 1–2 items maximum.
    string[] GameBadges { get; }

    // ── News feed ────────────────────────────────────────────────────────────
    // Shown on the game's news/patch notes page.

    /// Fetch the latest news items for this game (patch notes, announcements).
    /// Returns at most 20 items, newest first.
    /// Implementations typically parse GitHub releases or a hosted news.json.
    Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default);
}

// ── Supporting types for store and news ──────────────────────────────────────

/// One news / patch-note entry shown on a game's news page.
public sealed record NewsItem(
    string Title,        // e.g. "Beta 1.9.14 released"
    string Body,         // markdown body
    string Version,      // e.g. "1.9.14"
    DateTimeOffset Date, // publish date
    string? Url          // link to full release page, or null
);

// ── Supporting types shared across Core + Plugins ─────────────────────────────

public enum ApConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

/// Describes one AP session (connection credentials for one game slot).
public sealed record ApSession(
    string ServerUri,   // e.g. "archipelago.gg:38281" or "ws://localhost:38281"
    string SlotName,    // AP player slot name
    string Password,    // "" if no password
    string Game,        // AP game name e.g. "Diablo II Archipelago"
    string? Uuid = null // auto-generated if null
);

/// One item received from the AP server for this game slot.
public sealed record ApNetworkItem(
    long ItemId,      // AP item ID (numeric)
    long LocationId,  // AP location ID where this item was found
    int  Player,      // AP player slot that found it
    int  Flags        // AP item classification flags
);
