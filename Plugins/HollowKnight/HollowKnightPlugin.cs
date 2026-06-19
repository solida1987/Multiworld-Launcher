using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.HollowKnight;

// ═══════════════════════════════════════════════════════════════════════════════
// HollowKnightPlugin — install / launch for "Hollow Knight" (Team Cherry, 2017)
// played through the Archipelago.HollowKnight MOD, the in-game Archipelago client
// for Hollow Knight. This is a NATIVE "ConnectsItself" integration in the same
// family as Ship of Harkinian, APDOOM and the ArchipelaGOAL/Jak plugin — the
// game itself speaks to the AP server (no emulator, no Lua bridge, no launcher-
// held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// Hollow Knight (Steam appid 367520; GOG and Xbox Game Pass also work per the AP
// setup guide), and Archipelago is a MOD added on top. The honest integration
// ceiling — exactly like the shipped Jak plugin — is "automate what is possible,
// guide the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Hollow Knight" (verified against
//     worlds/hk/__init__.py: `game = "Hollow Knight"`). GameId = "hk".
//
//   * THE MOD repo is ArchipelagoMW-HollowKnight/Archipelago.HollowKnight
//     (verified live 2026-06-14 — the older Ijwu/Archipelago.HollowKnight URL in
//     the apworld's bug_report_page REDIRECTS here; the bare
//     "ArchipelagoMW/Archipelago.HollowKnight" name in the task brief 404s and is
//     NOT the real repo). Latest release v0.12.0 (2026-04-20) ships a SINGLE
//     asset, "Archipelago.zip" (~473 KB), whose verified contents are FLAT:
//         Archipelago.HollowKnight.dll      (the mod)
//         Archipelago.MultiClient.Net.dll   (AP network client, bundled)
//         Archipelago.Gifting.Net.dll       (AP gifting/deathlink net, bundled)
//         Archipelago.HollowKnight.pdb, README.md
//     These drop into <HK>/hollow_knight_Data/Managed/Mods/Archipelago/.
//
//   * CRITICAL HONESTY — THE ZIP IS NOT SELF-SUFFICIENT. The mod's own README
//     states plainly: "There are several mods that are needed for Archipelago to
//     run. They are installed automatically [by Lumafly]." The release zip bundles
//     ONLY the AP networking DLLs — it does NOT contain the Hollow Knight Modding
//     API itself nor the framework mods Archipelago depends on (ItemChanger,
//     MenuChanger, etc.). Those are resolved by the LUMAFLY mod manager (formerly
//     Scarab). Therefore the OFFICIAL and RECOMMENDED install route is Lumafly's
//     one-click deep link (which installs Archipelago + all dependencies), and the
//     direct-zip drop this plugin can perform is a PARTIAL, best-effort fallback
//     that still needs the modding API present. The plugin says exactly this and
//     leads the user to Lumafly first — faking a one-click "fully installed" that
//     cannot exist would be dishonest theatre.
//
//   * CONNECTION is made IN-GAME: after the mod is installed, "Archipelago"
//     appears on the main menu; you start a NEW save, pick the Archipelago game
//     mode, and type the server / slot / password into the IN-GAME menu fields.
//     There is NO command-line arg and NO config file this launcher can pre-write
//     (verified against the setup guide + README). So this plugin does NOT attempt
//     a connection prefill — the post-install note and settings panel state this.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Hollow Knight install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Hollow Knight via appmanifest_367520.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain hollow_knight.exe /
//      hollow_knight_Data) and persisted in this plugin's OWN sidecar
//      (Games/ROMs/hk/hk_launcher.json) — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort) = download the mod's "Archipelago.zip" from
//      the real release, and extract it into <HK>/.../Mods/Archipelago/. Because
//      the zip does not carry the dependency mods, the plugin ALSO presents
//      clear, numbered, Jak-style guided steps + links (Lumafly one-click deep
//      link, the mod repo, the official HK setup guide, archipelago.gg) so the
//      user can complete the dependency install via Lumafly — the recommended
//      route. Never a fake one-click.
//   3. LAUNCH = run hollow_knight.exe from the detected/override install; if the
//      exe cannot be found but Steam is present, fall back to
//      steam://rungameid/367520. ConnectsItself = true (the mod owns the slot —
//      the launcher must NOT hold its own ApClient on it). SupportsStandalone =
//      true (plain Hollow Knight runs perfectly without AP). No prefill (in-game
//      menu), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", SoH/Jak-style) ────────────
//   * "Installed" is judged by the presence of Archipelago.HollowKnight.dll under
//     a detected/override HK install's Mods tree (case-insensitive, recursive) —
//     NOT by an OUR-OWN version stamp, because the user may instead install via
//     Lumafly (the recommended route), which this launcher should honor. If no HK
//     install is detected, the tile simply reads "not installed".
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "Hollow Knight not
//     found" rather than throwing.
//   * No plaintext AP password is ever written by this plugin (connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class HollowKnightPlugin : IGamePlugin
{
    // ── Constants — the AP HK mod (real repo, verified 2026-06-14) ─────────────
    private const string MOD_OWNER = "ArchipelagoMW-HollowKnight";
    private const string MOD_REPO  = "Archipelago.HollowKnight";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // Lumafly (formerly Scarab) — the RECOMMENDED installer, pulls the mod plus
    // every dependency the release zip does not carry. The deep link installs
    // "Archipelago" and its dependencies in one click.
    private const string LumaflySite        = "https://themulhima.github.io/Lumafly/";
    private const string LumaflyDownloadUrl = "https://themulhima.github.io/Lumafly?download";
    private const string LumaflyInstallApUrl =
        "https://themulhima.github.io/Lumafly/commands/download/?mods=Archipelago";

    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Hollow%20Knight/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Hollow Knight appid 367520.
    private const string HkSteamAppId = "367520";
    private static readonly string SteamRunUrl = $"steam://rungameid/{HkSteamAppId}";

    /// Pinned fallback for the mod when the GitHub API is unreachable. v0.12.0
    /// verified live 2026-06-14; the single asset is "Archipelago.zip".
    private const string FallbackVersion = "0.12.0";
    private const string FallbackZipName = "Archipelago.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "hk";
    public string DisplayName => "Hollow Knight";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/hk/__init__.py (`game =
    /// "Hollow Knight"`).
    public string ApWorldName => "Hollow Knight";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "hk.png");

    public string ThemeAccentColor => "#2A3550";   // pale-blue Hallownest dusk
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Hollow Knight, Team Cherry's 2017 Metroidvania, played through the " +
        "Archipelago.HollowKnight mod — an in-game Archipelago client. Charms, " +
        "abilities, keys, Geo and more are shuffled into the multiworld, and the " +
        "game connects to the Archipelago server itself (no emulator, no bridge). " +
        "You bring your own copy of Hollow Knight (Steam, GOG, or Xbox Game Pass), " +
        "and the Archipelago mod is added on top with the Lumafly mod manager, " +
        "which also installs the mods Archipelago depends on. The launcher detects " +
        "your Steam install, can stage the Archipelago mod files, and guides the " +
        "rest. You connect to your server from the in-game Archipelago menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP HK mod DLL is present in a detected/override HK
    /// install's Mods tree. (We do NOT gate on our own stamp — the user may have
    /// installed via Lumafly, the recommended route, which we honor.)
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod zip) and any working files. The
    /// actual mod is extracted INTO the Hollow Knight install's Mods folder, not
    /// here. This is exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "HollowKnight");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Doom/Jak). Per the
    /// brief, lives under Games/ROMs/hk/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "hk_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Archipelago.HollowKnight mod reports checks/items/goal to the AP server
    // itself — the launcher relays nothing. These exist for interface
    // compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            // Best-effort: read the version we stamped next to a direct-zip install
            // if present; otherwise report "installed" when the mod DLL exists.
            InstalledVersion = FindInstalledModDll() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _) = await ResolveLatestModAsync(ct);
            AvailableVersion = version;
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. We need a Hollow Knight install to drop the mod into. Prefer an
        //    explicit override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Hollow Knight installation..."));
        string? hkDir = ResolveHollowKnightDir();
        if (hkDir == null)
            throw new InvalidOperationException(
                "Could not find a Hollow Knight installation. Open this game's " +
                "Settings and pick your Hollow Knight folder (the one containing " +
                "hollow_knight.exe), or install Hollow Knight via Steam first. " +
                "The Archipelago mod is added on top of your own copy of the game.");

        string modsDir = Path.Combine(hkDir, "hollow_knight_Data", "Managed", "Mods");
        string apModDir = Path.Combine(modsDir, "Archipelago");

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Archipelago Hollow Knight mod download on GitHub. " +
                "Check your internet connection, or install the mod with Lumafly " +
                "(recommended) from " + LumaflySite + " — see Settings for the guided " +
                "steps. The mod repo is " + ModRepoUrl + ".");

        // 2. Download + extract the mod zip INTO <HK>/.../Mods/Archipelago/.
        //    HONEST: this stages the mod + its bundled AP networking DLLs, but the
        //    framework mods Archipelago depends on (the HK Modding API, ItemChanger,
        //    MenuChanger, ...) are NOT in this zip and must be provided by Lumafly.
        await DownloadAndExtractModAsync(zipUrl, version, apModDir, progress, ct);

        // 3. Stamp the version next to our sidecar so the tile can show it. (This
        //    is informational only — IsInstalled is judged by the DLL's presence.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Staged the Archipelago mod {version} into your Hollow Knight Mods folder. " +
            "IMPORTANT: Archipelago needs several dependency mods that this download " +
            "does NOT include. The recommended way to finish is the Lumafly mod " +
            "manager (one click installs Archipelago + all dependencies) — open " +
            "Settings for the guided steps and links. To play: launch the game, start " +
            "a NEW save, choose the Archipelago mode, and enter your server details in " +
            "the in-game menu."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP server connection for Hollow Knight is entered in the
        // IN-GAME Archipelago menu (new save -> Archipelago mode -> server/slot/
        // password fields). There is no command-line / config prefill we can apply
        // (verified — see header). So launching from this tile just starts the
        // game; the user connects in-game with the session credentials.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartHollowKnight();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Hollow Knight runs perfectly well.
    public bool SupportsStandalone => true;

    /// The Archipelago.HollowKnight mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartHollowKnight();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started Hollow Knight from here. Kill what we launched.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in-game), so there is nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Archipelago.HollowKnight mod receives items from the AP server
        // directly; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Hollow Knight folder contains
    /// hollow_knight.exe and/or the hollow_knight_Data folder. Return null when
    /// acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Hollow Knight install folder.";

        if (LooksLikeHollowKnightDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, "Hollow Knight");
            if (LooksLikeHollowKnightDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Hollow Knight installation. Pick the folder " +
               "that contains hollow_knight.exe (the hollow_knight_Data folder is next " +
               "to it).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Hollow Knight is your own game (Steam / GOG / Xbox Game Pass) with " +
                   "the Archipelago mod added on top. The launcher detects your Steam " +
                   "install and can stage the Archipelago mod files, but the mod needs " +
                   "several dependency mods it does not bundle — the recommended way to " +
                   "install everything in one click is the Lumafly mod manager (see the " +
                   "guided steps below). You connect to your server from the in-game " +
                   "Archipelago menu. These external steps are not verified by this " +
                   "launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "HOLLOW KNIGHT INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? hkDir   = ResolveHollowKnightDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = hkDir != null
            ? (overrideDir != null
                ? "✓ Using your selected folder: " + hkDir
                : "✓ Detected Steam install: " + hkDir)
            : "Hollow Knight not detected. Pick your install folder below, or install " +
              "Hollow Knight via Steam first.";
        var detectBlock = new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = hkDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        };
        panel.Children.Add(detectBlock);

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "✓ Archipelago mod found: " + modDll
                    : "Archipelago mod not found in the Mods folder yet (use Install on " +
                      "the Play tab, or install it via Lumafly — recommended).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? hkDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Hollow Knight install folder (the one containing " +
                          "hollow_knight.exe). Detected from Steam automatically; set it " +
                          "here to override (GOG / Xbox Game Pass / non-standard Steam " +
                          "library).",
        };
        var dirBtn = new Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Hollow Knight install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? hkDir ?? "")
                                   ? (overrideDir ?? hkDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    MessageBox.Show(bad, "Not a Hollow Knight folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeHollowKnightDir(picked))
                {
                    string nested = Path.Combine(picked, "Hollow Knight");
                    if (LooksLikeHollowKnightDir(nested)) picked = nested;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Steam installs are detected automatically (appid 367520). Use this " +
                   "picker for GOG, Xbox Game Pass, or a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP (recommended: Lumafly mod manager)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Hollow Knight (Steam, GOG, or Xbox Game Pass). Install it if you have not.",
            "2. Download the Lumafly mod manager and run it from a normal folder (not Downloads). " +
                "If it does not auto-detect Hollow Knight, point it at your install folder.",
            "3. In Lumafly, install and enable \"Archipelago\" — its required dependency mods " +
                "are installed automatically. (Optional: also install \"Archipelago Map Mod\" as " +
                "an in-game tracker.) Or use the one-click Lumafly link in Links below.",
            "4. Alternative (advanced): the Install button on the Play tab stages the mod's own " +
                "files into your Mods\\Archipelago folder, but it does NOT include the dependency " +
                "mods — you would still need the modding API + ItemChanger/MenuChanger from Lumafly.",
            "5. Launch Hollow Knight and check that \"Archipelago\" appears at the top-left of the " +
                "main menu.",
            "6. To play: start a NEW save, choose the Archipelago game mode, type your server / " +
                "slot / password into the in-game fields, then Start. (This launcher cannot " +
                "pre-fill the connection — it is entered in-game.)",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Lumafly mod manager (download) ↗",          LumaflyDownloadUrl),
            ("Lumafly: install Archipelago (1-click) ↗",  LumaflyInstallApUrl),
            ("Archipelago.HollowKnight (GitHub) ↗",       ModRepoUrl),
            ("Hollow Knight Setup Guide ↗",               SetupGuideUrl),
            ("Archipelago Official ↗",                    ArchipelagoSite),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Mod releases are the AP-relevant news for this game.
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
            using var doc  = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? NormalizeTag(t.GetString()) ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v0.12.0" → "0.12.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the "Archipelago.zip" download
    /// URL. Falls back to the pinned 0.12.0 direct URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // the .zip named like Archipelago*.zip
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("archipelago"))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / Hollow Knight detection ─────────────────────

    /// The Hollow Knight install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveHollowKnightDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeHollowKnightDir(ov))
            return ov;

        try { return DetectSteamHollowKnightDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Hollow Knight if it has hollow_knight.exe and/or the
    /// hollow_knight_Data folder.
    private static bool LooksLikeHollowKnightDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "hollow_knight.exe"))) return true;
            if (Directory.Exists(Path.Combine(dir, "hollow_knight_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Hollow Knight install: read the Steam root from the
    /// registry, gather all library roots from libraryfolders.vdf, and find the
    /// one whose appmanifest_367520.acf exists → steamapps\common\Hollow Knight.
    private static string? DetectSteamHollowKnightDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{HkSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "Hollow Knight" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeHollowKnightDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, "Hollow Knight");
                    if (LooksLikeHollowKnightDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath). Both are tried; duplicates are harmless.
    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        // Last-ditch conventional location.
        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    /// Steam stores its SteamPath with forward slashes; normalize for Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf. Tolerant text scan (the VDF is a simple
    /// quoted key/value tree; we only need the path values).
    private static IEnumerable<string> SteamLibraryRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        foreach (string path in ExtractVdfPaths(text))
        {
            string norm = path.Replace('/', '\\').TrimEnd('\\');
            if (norm.Length > 0 && seen.Add(norm))
                yield return norm;
        }
    }

    /// Pull every  "path"   "<value>"  pair out of a libraryfolders.vdf body.
    /// Handles the Steam-VDF escaping of backslashes (\\ → \).
    private static IEnumerable<string> ExtractVdfPaths(string text)
    {
        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            // find the opening quote of the value
            int open = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;

            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
            i = close + 1;
        }
    }

    /// Read the "installdir" value from an appmanifest_*.acf (same quoted-pair
    /// format as VDF). Returns null if absent.
    private static string? ReadAcfInstallDir(string acfPath)
    {
        try
        {
            string text = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) return null;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) return null;
            return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
        }
        catch { return null; }
    }

    /// Safe registry string read; null on any failure (key/value missing, etc.).
    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find Archipelago.HollowKnight.dll under the detected/override HK install's
    /// Mods tree (recursive, case-insensitive). Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? hk = ResolveHollowKnightDir();
            if (hk == null) return null;
            string modsDir = Path.Combine(hk, "hollow_knight_Data", "Managed", "Mods");
            if (!Directory.Exists(modsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(modsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dll);
                if (name.Equals("Archipelago.HollowKnight.dll", StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Hollow Knight: prefer the exe in the detected/override install; if
    /// that cannot be found but Steam is present, fall back to the steam:// URL.
    /// Surfaces a clear message rather than failing opaquely.
    private void StartHollowKnight()
    {
        string? hk  = ResolveHollowKnightDir();
        string? exe = hk != null ? Path.Combine(hk, "hollow_knight.exe") : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = hk!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Hollow Knight.");

            _gameProcess = proc;
            IsRunning    = true;
            try
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    IsRunning = false;
                    GameExited?.Invoke(proc.ExitCode);
                };
            }
            catch { /* some processes don't expose Exited — non-fatal */ }
            return;
        }

        // Fall back to Steam if we at least know Steam is installed.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process, so we won't track exit
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find hollow_knight.exe. Open this game's Settings and pick your " +
            "Hollow Knight install folder, or install Hollow Knight via Steam.",
            "hollow_knight.exe");
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's Archipelago.zip and extract its (flat) contents into
    /// <HK>/.../Mods/Archipelago/. Honest scope: this stages the mod + its bundled
    /// AP networking DLLs only; dependency mods come from Lumafly.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string apModDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"hk-archipelago-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((10, $"Downloading Archipelago mod {version}..."));
            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempZip))
            {
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(10 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading Archipelago mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing mod into the Hollow Knight Mods folder..."));
            Directory.CreateDirectory(apModDir);
            // The release zip is flat (verified): extract straight into Mods\Archipelago.
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, apModDir, overwriteFiles: true);

            // Some release zips wrap everything in a single sub-folder. If so,
            // flatten it so the DLLs sit directly in Mods\Archipelago.
            if (!File.Exists(Path.Combine(apModDir, "Archipelago.HollowKnight.dll")))
            {
                string[] subdirs = Directory.GetDirectories(apModDir);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(apModDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    try { Directory.Delete(sub, recursive: true); } catch { }
                }
            }

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as Doom/Jak).

    private sealed class HkSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private HkSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<HkSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(HkSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — setting just won't persist this time */ }
    }

    private string? LoadOverrideDir()
    {
        string? p = LoadSettings().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }
    private void SaveOverrideDir(string p) { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
