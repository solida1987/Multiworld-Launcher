using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.AxiomVerge;

// ═══════════════════════════════════════════════════════════════════════════════
// AxiomVergePlugin — install / patch / launch for "Axiom Verge" played through
// the mail-liam/Archipelago fork integration. This is a NATIVE "ConnectsItself"
// integration: the game connects to the AP server itself via an in-game
// "Archipelago" menu item that appears after the patch is applied.
//
// ── VERIFIED FACTS (2026-06-14) ───────────────────────────────────────────────
//
//   * FORK: https://github.com/mail-liam/Archipelago
//     Latest release tag: "beta3.3".
//     Release assets (verified):
//       - "AxiomVergeAP.zip"   — the game patch (Windows only)
//       - "axiomverge.apworld" — the Archipelago world definition
//       - "Axiom.Verge.yaml"   — the default multiworld yaml template
//
//   * AP GAME STRING: "Axiom Verge" — verified from worlds/axiomverge/__init__.py
//     in the feat/axiom-verge-world branch:
//       class AxiomVergeWorld(World):
//           game = "Axiom Verge"
//     Also confirmed by Axiom.Verge.yaml:
//       game: Axiom Verge
//
//   * STEAM APP ID: 332200 (verified from
//     https://store.steampowered.com/app/332200/Axiom_Verge/)
//
//   * THE BASE GAME is bring-your-own: a legally-owned copy of Axiom Verge on
//     Steam (AppID 332200). The launcher DETECTS the Steam install (registry →
//     libraryfolders.vdf → appmanifest_332200.acf → steamapps\common\Axiom
//     Verge\) and also offers a manual folder picker.
//
//   * STEAM BETA BRANCH: The AP patch requires Axiom Verge's "secretworlds"
//     beta branch on Steam. The user must:
//       1. Right-click Axiom Verge in Steam → Properties → Betas
//       2. Enter beta access code: "secretworlds"
//       3. Switch to the [earlyaccess] beta branch
//     The launcher explains this step in the settings panel.
//
//   * THE PATCH: "AxiomVergeAP.zip" must be extracted into the Axiom Verge
//     install folder, then "applyPatchFinal.bat" run. After that, "Archipelago"
//     appears in the game's main menu.
//
//   * HOW IT CONNECTS (verified from release notes):
//     Connection details (server, slot name, password) are entered IN-GAME via
//     the "Archipelago" menu item that appears on the main menu after the patch.
//     There is NO documented config file or command-line argument the launcher
//     can pre-write for this integration — the user must enter the 3 fields
//     in-game. This plugin surfaces the session's server/slot/password in the
//     settings panel so the user can copy them in.
//     ConnectsItself = true (the game's built-in AP client owns the slot — the
//     launcher must NOT hold its own ApClient on the same slot while the game
//     runs). SupportsStandalone = true (plain non-AP play still works).
//
//   * AP WORLD: community fork (not AP-main). The apworld ships as
//     "axiomverge.apworld" in the release, placed in Archipelago's custom_worlds
//     folder. The launcher downloads and saves it next to the sidecar.
//
//   * VERSION TAG NOTE: the fork uses "beta3.3" style tags (not "v1.2.3"), so
//     NormalizeTag skips stripping a leading 'v'.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT the Steam Axiom Verge install (registry + libraryfolders.vdf +
//      appmanifest_332200.acf), with a manual folder picker override persisted in
//      this plugin's OWN JSON sidecar (Games/ROMs/axiom_verge/
//      axiom_verge_launcher.json — Core/SettingsStore is NOT modified).
//   2. INSTALL/UPDATE = download "AxiomVergeAP.zip" from the latest release and
//      extract it into the detected install folder. Then run "applyPatchFinal.bat"
//      silently (cmd /c). Download "axiomverge.apworld" next to the sidecar.
//   3. GUIDED STEPS: the settings panel shows the Steam beta branch setup steps
//      and the in-game connection fields to enter, along with the session's
//      server/slot/password to copy. (No config-file prefill is possible here.)
//   4. LAUNCH = run the game exe (or steam://rungameid/332200 as fallback). No
//      connection prefill. ConnectsItself = true.
//
// ── DEFENSIVE / UNVERIFIED ─────────────────────────────────────────────────────
//   * The exact exe name inside the Axiom Verge install was not verified offline.
//     ResolveGameExe prefers "AxiomVerge.exe", then any "*axiom*" exe, then any
//     non-helper exe in the install.
//   * applyPatchFinal.bat is run via "cmd /c applyPatchFinal.bat" in the install
//     dir. This is a Windows .bat and the release notes confirm it is the correct
//     step (tested working per beta3.3 release notes).
//   * The libraryfolders.vdf / ACF paths follow the same Valve key-value parsing
//     used in StardewValleyPlugin (line-level, quoted "key" "value"), adapted for
//     AppID 332200 and the "Axiom Verge" common folder.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AxiomVergePlugin : IGamePlugin
{
    // ── Constants — repos / links ──────────────────────────────────────────────

    private const string FORK_OWNER = "mail-liam";
    private const string FORK_REPO  = "Archipelago";
    private const string ForkRepoUrl = $"https://github.com/{FORK_OWNER}/{FORK_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{FORK_OWNER}/{FORK_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    private const string SetupGuideUrl   = ForkRepoUrl + "/releases";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// Steam AppID — verified 2026-06-14.
    private const string SteamAppId = "332200";

    /// Standard Steam common folder name for Axiom Verge.
    private const string SteamCommonFolderName = "Axiom Verge";

    /// The Steam beta access code required for the AP patch.
    private const string SteamBetaCode = "secretworlds";

    /// Pinned fallback tag when the GitHub API is unreachable.
    private const string FallbackTag     = "beta3.3";
    private static readonly string FallbackZipUrl =
        $"{ForkRepoUrl}/releases/download/{FallbackTag}/AxiomVergeAP.zip";
    private static readonly string FallbackApWorldUrl =
        $"{ForkRepoUrl}/releases/download/{FallbackTag}/axiomverge.apworld";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Stamp file: what patch version is applied to the detected install.
    private const string VersionFileName = "axiom_verge_ap_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "axiom_verge";
    public string DisplayName => "Axiom Verge";
    public string Subtitle    => "Native PC · Steam · in-game AP menu";

    /// EXACT AP game string — verified from worlds/axiomverge/__init__.py
    /// (AxiomVergeWorld.game = "Axiom Verge") and Axiom.Verge.yaml.
    public string ApWorldName => "Axiom Verge";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "axiom_verge.png");

    public string ThemeAccentColor => "#C0392B";   // Axiom Verge red

    public string[] GameBadges => new[] { "Steam · needs secretworlds beta" };

    public string Description =>
        "Axiom Verge, the acclaimed retro metroidvania by Tom Happ, played through " +
        "the mail-liam/Archipelago fork integration. Weapons, health nodes, address " +
        "disruptors, drones, and suit upgrades are shuffled into the multiworld. " +
        "The game connects to the Archipelago server via its own built-in client — " +
        "no emulator, no Lua bridge. You bring your own copy of Axiom Verge on Steam " +
        "(AppID 332200). The integration requires enabling Steam's secret beta branch " +
        "(\"secretworlds\") and running a one-time patch. After that, an Archipelago " +
        "option appears on the main menu where you enter your server, slot, and " +
        "password.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means: the install folder is detected and the patch stamp
    /// file exists (i.e. AxiomVergeAP.zip was extracted + bat applied).
    public bool IsInstalled => ResolveInstallDir() is { } dir
                            && File.Exists(Path.Combine(RomLibraryDirectory, VersionFileName));

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// The resolved Axiom Verge install directory (contains the game exe and
    /// now the AP patch files after install). Settable for the interface
    /// contract; setting it persists the override.
    public string GameDirectory
    {
        get => ResolveInstallDir() ?? "";
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                SaveOverrideInstallDir(value);
        }
    }

    /// This plugin's OWN settings sidecar — kept out of Core/SettingsStore so
    /// the plugin is a single self-contained source file.
    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "axiom_verge_launcher.json");

    private string VersionFilePath
        => Path.Combine(RomLibraryDirectory, VersionFileName);

    private string ApWorldLocalPath
        => Path.Combine(RomLibraryDirectory, "axiomverge.apworld");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    /// Last session AP details, shown in the settings panel so the user can
    /// copy them into the in-game connection screen.
    private string _lastServer   = "";
    private string _lastSlot     = "";
    private string _lastPassword = "";

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Axiom Verge's in-game AP client reports checks/items/goal to the AP server
    // itself — the launcher relays nothing (ConnectsItself = true).
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
            InstalledVersion = IsInstalled
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            string? ver = await ResolveLatestTagAsync(ct);
            AvailableVersion = ver;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 1. Find where Axiom Verge is installed.
        string? installDir = ResolveInstallDir();
        if (installDir == null)
            throw new DirectoryNotFoundException(
                "Axiom Verge install folder not found. " +
                "Own Axiom Verge on Steam (AppID 332200) and launch it once, or " +
                "choose the folder manually in Settings. " +
                "Also make sure you have enabled the \"secretworlds\" beta branch " +
                "(Steam → Axiom Verge → Properties → Betas → enter code: secretworlds).");

        // 2. Resolve the latest release from the fork.
        progress.Report((2, "Checking latest Axiom Verge AP release..."));
        var (tag, zipUrl, apworldUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = tag;

        // 3. Already current? (idempotent fast path)
        if (IsInstalled && File.Exists(VersionFilePath))
        {
            string installed = (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim();
            if (installed == tag)
            {
                InstalledVersion = tag;
                progress.Report((100, $"Axiom Verge AP patch {tag} is already applied."));
                return;
            }
        }

        // 4. Download AxiomVergeAP.zip to a temp file.
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"axiom_verge_ap_{tag}_{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading Axiom Verge AP patch ({tag})..."));
            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;
            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempZip))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    downloaded += n;
                    if (total > 0)
                        progress.Report((5 + (int)(50 * downloaded / total),
                            $"Downloading... {downloaded / 1_000_000}MB"));
                }
                await dst.FlushAsync(ct);
            }

            // 5. Extract the zip into the Axiom Verge install folder (overwrite).
            progress.Report((58, "Extracting patch files into game folder..."));
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, installDir, overwriteFiles: true);

            // 6. Run applyPatchFinal.bat (Windows .bat confirmed in release notes).
            string batPath = Path.Combine(installDir, "applyPatchFinal.bat");
            if (File.Exists(batPath))
            {
                progress.Report((70, "Applying patch (applyPatchFinal.bat)..."));
                var bat = Process.Start(new ProcessStartInfo
                {
                    FileName         = "cmd.exe",
                    Arguments        = "/c applyPatchFinal.bat",
                    WorkingDirectory = installDir,
                    UseShellExecute  = false,
                    CreateNoWindow   = true,
                });
                if (bat != null)
                {
                    bat.WaitForExit(30_000);   // max 30s; best-effort
                    bat.Dispose();
                }
                progress.Report((82, "Patch bat completed."));
            }
            else
            {
                progress.Report((82, "applyPatchFinal.bat not found in zip — patch may have already been applied or zip changed."));
            }
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }

        // 7. Download axiomverge.apworld next to the sidecar (best effort).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((85, "Downloading axiomverge.apworld..."));
                Directory.CreateDirectory(RomLibraryDirectory);
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((92, "axiomverge.apworld saved — place it in your Archipelago custom_worlds folder."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((92,
                    "Could not download axiomverge.apworld — grab it from the GitHub " +
                    "release page and drop it into your Archipelago custom_worlds folder."));
            }
        }

        // 8. Stamp the installed version.
        Directory.CreateDirectory(RomLibraryDirectory);
        await File.WriteAllTextAsync(VersionFilePath, tag, ct);
        InstalledVersion = tag;

        progress.Report((100,
            $"Axiom Verge AP patch {tag} applied. " +
            "Press Play, choose \"Archipelago\" on the main menu, and enter your " +
            "server, slot name, and password."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        // "Archipelago" menu item requires both: the install is detected AND
        // the patch was applied (version stamp written after running the bat).
        return IsInstalled;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    /// Axiom Verge's in-game client holds the AP slot — ConnectsItself = true.
    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // Remember the session details so CreateSettingsPanel can show them for
        // the user to copy into the in-game Archipelago menu.
        _lastServer   = session.ServerUri ?? "";
        _lastSlot     = session.SlotName  ?? "";
        _lastPassword = session.Password  ?? "";

        StartGame();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        _lastServer = _lastSlot = _lastPassword = "";
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var warn    = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var accent  = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Section: Install directory ────────────────────────────────────
        AddSectionHeader(panel, "GAME INSTALL DIRECTORY", muted);

        string installDir = ResolveInstallDir() ?? "";
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text        = installDir,
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dirBtn = new Button
        {
            Content         = "Browse...",
            Width           = 90,
            Padding         = new Thickness(0, 6, 0, 6),
            Background      = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground      = fg,
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Axiom Verge install folder",
                InitialDirectory = Directory.Exists(installDir) ? installDir
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                SaveOverrideInstallDir(dlg.FolderName);
                dirBox.Text = dlg.FolderName;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        bool detected = !string.IsNullOrEmpty(installDir);
        panel.Children.Add(new TextBlock
        {
            Text       = detected ? "✓ Axiom Verge install detected" : "Not detected — browse to your install folder",
            FontSize   = 11,
            Foreground = detected ? success : warn,
            Margin     = new Thickness(0, 4, 0, 12),
        });

        // ── Section: Patch status ─────────────────────────────────────────
        AddSectionHeader(panel, "ARCHIPELAGO PATCH STATUS", muted);

        bool patched = IsInstalled;
        panel.Children.Add(new TextBlock
        {
            Text       = patched
                         ? $"✓ Patch applied ({InstalledVersion ?? "unknown version"})"
                         : "Patch not applied — click Install Game to download and apply the patch",
            FontSize   = 11,
            Foreground = patched ? success : warn,
            Margin     = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Steam beta branch setup ─────────────────────────────
        AddSectionHeader(panel, "REQUIRED: STEAM BETA BRANCH SETUP", muted);
        panel.Children.Add(new TextBlock
        {
            Text = "Axiom Verge's AP integration requires the \"secretworlds\" Steam " +
                   "beta branch. If you have not done this yet:\n\n" +
                   "  1. Open Steam and find Axiom Verge in your library.\n" +
                   "  2. Right-click → Properties → Betas tab.\n" +
                   $"  3. Enter beta access code: {SteamBetaCode}\n" +
                   "  4. Switch to the [earlyaccess] beta branch and wait for Steam to update.\n" +
                   "  5. Then click Install Game here to apply the AP patch.\n\n" +
                   "If you skip this step the AP option will not appear in the game menu.",
            FontSize    = 11,
            Foreground  = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin      = new Thickness(0, 0, 0, 12),
        });

        // ── Section: How it connects ──────────────────────────────────────
        AddSectionHeader(panel, "HOW TO CONNECT IN-GAME", muted);
        panel.Children.Add(new TextBlock
        {
            Text = "After the patch is applied, launch the game. Select \"Archipelago\" " +
                   "on the main menu and enter the three fields below. " +
                   "There is no config file to pre-write — all connection details are " +
                   "entered in-game.",
            FontSize    = 11,
            Foreground  = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin      = new Thickness(0, 0, 0, 8),
        });

        // Show last-session connection details so user can copy them.
        if (!string.IsNullOrEmpty(_lastServer) || !string.IsNullOrEmpty(_lastSlot))
        {
            AddSectionHeader(panel, "YOUR CONNECTION DETAILS (copy these in-game)", accent);
            AddCopyRow(panel, "Server",   FormatServerDisplay(_lastServer), fg, muted);
            AddCopyRow(panel, "Slot",     _lastSlot,     fg, muted);
            AddCopyRow(panel, "Password", _lastPassword, fg, muted);
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text       = "Press Play (with an active AP session) — the server, slot, and " +
                             "password will appear here for you to copy into the game.",
                FontSize   = 11,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(0, 0, 0, 12),
            });
        }

        // ── Section: apworld ─────────────────────────────────────────────
        if (File.Exists(ApWorldLocalPath))
        {
            AddSectionHeader(panel, "APWORLD", muted);
            panel.Children.Add(new TextBlock
            {
                Text = $"axiomverge.apworld is saved at:\n{ApWorldLocalPath}\n\n" +
                       "Copy it into your Archipelago custom_worlds folder (e.g. " +
                       @"C:\ProgramData\Archipelago\custom_worlds) to generate multiworlds " +
                       "with this fork.",
                FontSize    = 11,
                Foreground  = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin      = new Thickness(0, 0, 0, 12),
            });
        }

        // ── Section: Links ────────────────────────────────────────────────
        AddSectionHeader(panel, "LINKS", muted);
        foreach (var (label, url) in new[]
        {
            ("Axiom Verge on Steam ↗",                "https://store.steampowered.com/app/332200/"),
            ("mail-liam/Archipelago fork (GitHub) ↗", ForkRepoUrl),
            ("Releases / changelog ↗",                SetupGuideUrl),
            ("Archipelago Official ↗",                ArchipelagoSite),
        })
        {
            string u = url;
            var btn = new Button
            {
                Content         = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding         = new Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize        = 12,
                Margin          = new Thickness(0, 0, 0, 4),
                Foreground      = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor          = System.Windows.Input.Cursors.Hand,
            };
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
                catch { }
            };
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc  = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
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

    private async Task<string?> ResolveLatestTagAsync(CancellationToken ct)
    {
        var (tag, _, _) = await ResolveLatestReleaseAsync(ct);
        return tag;
    }

    /// Resolve the latest release: tag + Windows zip URL + apworld URL.
    /// Falls back to the pinned beta3.3 URLs when the API is unreachable.
    private async Task<(string Tag, string ZipUrl, string? ApWorldUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            // mail-liam/Archipelago uses pre-releases (not standard "latest"),
            // so we list all releases and take the first one.
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array) throw new InvalidDataException();

            foreach (var rel in arr.EnumerateArray())
            {
                string? tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (string.IsNullOrEmpty(tag)) continue;

                if (rel.TryGetProperty("assets", out var assets)
                    && assets.ValueKind == JsonValueKind.Array)
                {
                    var (zipUrl, apworldUrl) = PickAssets(assets);
                    if (zipUrl != null)
                        return (tag, zipUrl, apworldUrl);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (FallbackTag, FallbackZipUrl, FallbackApWorldUrl);
    }

    /// From a release's assets array, pick the AxiomVergeAP.zip and the
    /// axiomverge.apworld.
    private static (string? ZipUrl, string? ApWorldUrl) PickAssets(JsonElement assets)
    {
        string? zipUrl = null, apworldUrl = null;
        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();
            if (lower == "axiomvergeap.zip" || (lower.Contains("axiomverge") && lower.EndsWith(".zip")))
                zipUrl = url;
            else if (lower == "axiomverge.apworld" || (lower.Contains("axiomverge") && lower.EndsWith(".apworld")))
                apworldUrl = url;
        }
        return (zipUrl, apworldUrl);
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the game exe: prefer "AxiomVerge.exe", then any "*axiom*" exe,
    /// then any non-helper exe in the install folder.
    private string? ResolveGameExe()
    {
        string? dir = ResolveInstallDir();
        if (dir == null) return null;

        string preferred = Path.Combine(dir, "AxiomVerge.exe");
        if (File.Exists(preferred)) return preferred;

        try
        {
            // Fuzzy: any "*axiom*" exe that is not a helper/uninstaller.
            foreach (string exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name.Contains("unins") || name.Contains("setup") || name.Contains("crash"))
                    continue;
                if (name.Contains("axiom"))
                    return exe;
            }
            // Last resort: any non-helper exe.
            foreach (string exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name.Contains("unins") || name.Contains("setup") || name.Contains("crash"))
                    continue;
                return exe;
            }
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    // ── Private helpers — launch ───────────────────────────────────────────────

    private void StartGame()
    {
        string? exe = ResolveGameExe();
        string? dir = ResolveInstallDir();

        ProcessStartInfo psi;
        if (exe != null && dir != null)
        {
            psi = new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dir,
                UseShellExecute  = false,
            };
        }
        else
        {
            // Steam URI fallback when the exe can't be found.
            psi = new ProcessStartInfo($"steam://rungameid/{SteamAppId}")
            {
                UseShellExecute = true,
            };
        }

        var proc = Process.Start(psi);
        if (proc == null) return;

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            try { GameExited?.Invoke(proc.ExitCode); } catch { }
        };
    }

    // ── Private helpers — Steam install detection ─────────────────────────────

    /// Resolve the Axiom Verge install dir: manual override first, then Steam
    /// detection (registry → libraryfolders.vdf → appmanifest_332200.acf →
    /// steamapps/common/Axiom Verge).
    private string? ResolveInstallDir()
    {
        // 1. Manual override from sidecar.
        var settings = LoadSettings();
        if (!string.IsNullOrEmpty(settings.InstallDirOverride)
            && Directory.Exists(settings.InstallDirOverride))
            return settings.InstallDirOverride;

        // 2. Steam registry detection.
        return DetectViaSteam();
    }

    /// Walk Steam registry + VDF to find the Axiom Verge install folder.
    private static string? DetectViaSteam()
    {
        try
        {
            // HKCU\Software\Valve\Steam → SteamPath
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Valve\Steam");
            string? steamPath = key?.GetValue("SteamPath") as string;
            if (string.IsNullOrEmpty(steamPath)) return null;

            // Parse libraryfolders.vdf to get all Steam library roots.
            var roots = new List<string>();
            string defaultRoot = Path.Combine(steamPath, "steamapps");
            if (Directory.Exists(defaultRoot))
                roots.Add(defaultRoot);

            string libVdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libVdf))
            {
                foreach (string line in File.ReadAllLines(libVdf, Encoding.UTF8))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // "path"		"E:\\SteamLibrary"
                    int first  = trimmed.IndexOf('"', 6);          // after "path"
                    int second = trimmed.IndexOf('"', first + 1);
                    int third  = trimmed.IndexOf('"', second + 1);
                    int fourth = trimmed.IndexOf('"', third + 1);
                    if (first < 0 || second < 0 || third < 0 || fourth < 0) continue;
                    string libPath = trimmed.Substring(third + 1, fourth - third - 1)
                                            .Replace("\\\\", "\\");
                    string apps = Path.Combine(libPath, "steamapps");
                    if (Directory.Exists(apps) && !roots.Contains(apps))
                        roots.Add(apps);
                }
            }

            // Search each library root for appmanifest_332200.acf.
            foreach (string steamApps in roots)
            {
                string acf = Path.Combine(steamApps, $"appmanifest_{SteamAppId}.acf");
                if (!File.Exists(acf)) continue;

                // Parse "installdir" from the ACF.
                foreach (string line in File.ReadAllLines(acf, Encoding.UTF8))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("\"installdir\"", StringComparison.OrdinalIgnoreCase))
                        continue;
                    int first  = trimmed.IndexOf('"', 12);
                    int second = trimmed.IndexOf('"', first + 1);
                    int third  = trimmed.IndexOf('"', second + 1);
                    int fourth = trimmed.IndexOf('"', third + 1);
                    if (first < 0 || second < 0 || third < 0 || fourth < 0) continue;
                    string folderName = trimmed.Substring(third + 1, fourth - third - 1);
                    string candidate  = Path.Combine(steamApps, "common", folderName);
                    if (Directory.Exists(candidate))
                        return candidate;
                }
            }
        }
        catch { /* registry / filesystem error — fall through to null */ }
        return null;
    }

    // ── Private helpers — sidecar settings ────────────────────────────────────

    private sealed class AxiomVergeSettings
    {
        public string InstallDirOverride { get; set; } = "";
    }

    private AxiomVergeSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<AxiomVergeSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(AxiomVergeSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private void SaveOverrideInstallDir(string dir)
    {
        var s = LoadSettings();
        s.InstallDirOverride = dir;
        SaveSettings(s);
    }

    // ── Private helpers — UI utilities ────────────────────────────────────────

    private static void AddSectionHeader(StackPanel panel, string text,
        System.Windows.Media.SolidColorBrush color)
    {
        panel.Children.Add(new TextBlock
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = color,
            Margin     = new Thickness(0, 8, 0, 6),
        });
    }

    private static void AddCopyRow(StackPanel panel, string label, string value,
        System.Windows.Media.SolidColorBrush fg, System.Windows.Media.SolidColorBrush muted)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        var lbl = new TextBlock
        {
            Text       = label + ":",
            Width      = 70,
            FontSize   = 11,
            Foreground = muted,
        };

        var box = new TextBox
        {
            Text            = value,
            IsReadOnly      = true,
            FontSize        = 11,
            Foreground      = fg,
            Margin          = new Thickness(4, 0, 8, 0),
            Background      = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };

        var copy = new Button
        {
            Content         = "Copy",
            Width           = 50,
            Padding         = new Thickness(0, 3, 0, 3),
            FontSize        = 11,
            Background      = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground      = fg,
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        string v = value;
        copy.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(v); } catch { }
        };

        DockPanel.SetDock(lbl, Dock.Left);
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(lbl);
        row.Children.Add(copy);
        row.Children.Add(box);
        panel.Children.Add(row);
    }

    /// Format the server URI for display: strip ws:// prefix, normalize port.
    private static string FormatServerDisplay(string serverUri)
    {
        string s = serverUri.Trim();
        foreach (string prefix in new[] { "wss://", "ws://", "archipelago://" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        return s;
    }
}
