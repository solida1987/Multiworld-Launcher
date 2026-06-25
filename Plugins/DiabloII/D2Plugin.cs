using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.DiabloII;

// ═══════════════════════════════════════════════════════════════════════════════
// D2Plugin — IGamePlugin for "Diablo II Archipelago"
//
// KEY CHANGE from V1 launcher architecture:
// ──────────────────────────────────────────
// V1: D2Arch_Launcher.exe (standalone C injector) → called by C# launcher
//       → did VirtualAllocEx + CreateRemoteThread from a SEPARATE PROCESS
//       → AV scanners: "small standalone injector" = high-risk heuristic
//
// V2: Injection lives HERE, inside D2Plugin (C# P/Invoke inside the main
//     launcher process). The 154MB WPF app with proper PE metadata and
//     (eventually) a code signature doing CreateRemoteThread is dramatically
//     less suspicious than a 40KB standalone injector blob.
//     D2Arch_Launcher.exe bootstrap is ELIMINATED for V2.
//
// IPC CHANGE:
// ───────────
// V1: D2 DLL ↔ TCP socket ↔ ap_bridge.py ↔ AP server   (Python subprocess)
// V2: D2 DLL ↔ Named pipe ↔ D2Plugin (C# in launcher) ↔ ApClient ↔ AP server
//     (No Python, no subprocess, no TCP port conflict)
//
// NOTE: The D2 DLL (D2Archipelago.dll) must be updated to open the named
// pipe client instead of its current TCP socket to ap_bridge.py.
// That DLL-side change is tracked in TODO_V2_DLL_BRIDGE.md.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class D2Plugin : IGamePlugin
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string GameId      => "diablo2_archipelago";
    public string DisplayName => "Diablo II: Lord of Destruction";
    public string Subtitle    => "Randomiser Mod";
    public string IconPath    => Path.Combine(AppContext.BaseDirectory, "Assets", "diablo2_archipelago.png");

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion  { get; private set; }
    public string? AvailableVersion  { get; private set; }
    public bool    IsInstalled       => InstalledVersion != null;
    public bool    IsRunning         { get; private set; }

    // ── Configuration (set by caller before use) ──────────────────────────────

    private string _gameDirectory = string.Empty;

    /// Root directory of the D2 Archipelago installation.
    /// Setter re-loads the persisted AP item resume index — the index file
    /// lives under <GameDirectory>\Archipelago\, and the directory is assigned
    /// AFTER construction (object initializer in App.xaml.cs), so loading in
    /// the constructor would always read a non-existent relative path and
    /// leave the index at 0 (re-delivering the full item history every run).
    public string GameDirectory
    {
        get => _gameDirectory;
        set
        {
            _gameDirectory = value;
            LoadApIndex();
        }
    }

    /// The user's OWN original Diablo II: LoD installation — used ONLY as the
    /// source to COPY the copyrighted Blizzard data files (the MPQs) from.
    /// It is never modified or installed into. The mod itself lives in
    /// GameDirectory (Games/diablo2_archipelago), kept fully separate so the
    /// player's original game is left untouched.
    public string OriginalD2Directory { get; set; } = string.Empty;

    /// True when OriginalD2Directory points at a valid Classic D2 + LoD folder.
    public bool IsOriginalD2Configured =>
        !string.IsNullOrEmpty(OriginalD2Directory) &&
        ValidateExistingInstall(OriginalD2Directory) == null;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process?                _gameProcess;
    private NamedPipeServerStream?  _pipe;
    private CancellationTokenSource? _pipeCts;

    // Serialises all pipe writes (ITEM: from ReceiveItemsAsync, STATE: from
    // OnApStateChanged) — PipeStream does not guarantee atomic interleaving
    // of concurrent writers, and the DLL frames messages by newline.
    private readonly SemaphoreSlim _pipeWriteLock = new(1, 1);

    // Last AP connection state seen via OnApStateChanged. Used to push the
    // current state to the DLL right after it connects to the pipe (the
    // state may have changed to Connected BEFORE the game was launched, in
    // which case no further OnApStateChanged call would ever inform it).
    private ApConnectionState _lastApState = ApConnectionState.Disconnected;

    // AP item resume index — persisted to disk so we survive restarts
    private int _apItemIndex;

    // ── AP bridge events ──────────────────────────────────────────────────────

    public event Action<long[]>? LocationsChecked;

    /// "RECV:&lt;text&gt;" from the DLL in standalone — a reward notification formatted
    /// "&lt;location&gt;: &lt;reward&gt;". The host appends it to the tracker's Received
    /// tab so a solo run shows what each check granted (no AP server involved).
    public event Action<string>? StandaloneItemReceived;

    /// The game's full active location universe (standalone only) — the DLL
    /// streams it as MISSING: once the pipe connects so the tracker can show
    /// unchecked locations + per-category totals like an AP session. Not on
    /// IGamePlugin (D2-specific); the host wires it via a cast.
    public event Action<long[]>? LocationsMissing;
    public event Action<int>?    GameExited;

    /// Fired when the DLL sends a "GOAL" message (player completed the goal).
    /// Caller should send StatusUpdate(ApClientStatus.Goal) to the AP server.
    public event Action? GoalCompleted;

    /// Invoked when the DLL reports the local player died ("DEATH:<cause>").
    /// Assignment-style (not an event) so the per-launch re-wiring in the UI
    /// layer stays idempotent — a second launch overwrites, never stacks.
    /// Caller forwards to ApClient.SendDeathLinkAsync IF DeathLink is enabled.
    public Action<string>? OnPlayerDied { get; set; }

    // ── UI-layer injections (assignment-style = idempotent re-wiring) ─────────

    /// Resolves an AP player slot to its display name for ITEM v2 messages.
    /// The player map lives in the UI layer; null = plain ITEM:<id> fallback.
    /// IMPORTANT: for the player's OWN items this must return the slot name —
    /// the DLL compares it against [ap] SlotName for self-release detection.
    public Func<int, string>? ResolvePlayerName { get; set; }

    /// Supplies the current AP slot_data (null when not connected). Used to
    /// write ap_settings.dat BEFORE the STATE:CONNECTED push so the DLL never
    /// applies the connected state ahead of the settings file.
    public Func<JsonElement?>? GetSlotData { get; set; }

    /// Supplies the current AP room seed name (null when not connected). The AP
    /// launch path derives a stable per-world seed from this so its data-file
    /// randomization is reproducible across relaunches of the same multiworld.
    public Func<string?>? GetSeedName { get; set; }

    /// Invoked once the game's pipe has attached (post-launch). The host wires
    /// this to ApClient.SyncAsync() so the AP server re-sends the full item
    /// stream from index 0 — re-delivering any items received while the player
    /// was still sitting in the launcher (notably the PRECOLLECTED STARTING
    /// SKILLS at item index 0), which ReceiveItemsAsync had to drop because the
    /// pipe wasn't connected yet. The DLL dedups by item id, so re-delivered
    /// items are harmless. Assignment-style for idempotent per-launch re-wiring.
    public Func<Task>? RequestApResync { get; set; }

    // ── GitHub API ────────────────────────────────────────────────────────────

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
    };

    // Cached latest tag (e.g. "Beta-1.9.13") — resolved once per session via the
    // releases/latest redirect, which is served by GitHub's CDN and is not counted
    // against the REST API rate limit (60 req/hour unauthenticated).
    private string? _cachedLatestTag;

    static D2Plugin()
    {
        // GitHub API requires a User-Agent header; requests without one are rejected
        // or aggressively rate-limited (HTTP 429).
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("Archipelago-Launcher/2.0");
    }

    private const string GITHUB_OWNER    = "solida1987";
    private const string GITHUB_REPO     = "Diablo-II-Archipelago";

    // We resolve the latest tag via the releases/latest REDIRECT (302 → tag URL)
    // instead of the GitHub REST API. This avoids the 60 req/hour unauthenticated
    // rate limit entirely — redirect responses are served by GitHub's CDN and are
    // not counted against the API quota.
    private const string GH_LATEST_PAGE  =
        "https://github.com/solida1987/Diablo-II-Archipelago/releases/latest";
    private const string GH_DOWNLOAD_BASE =
        "https://github.com/solida1987/Diablo-II-Archipelago/releases/download";
    private const string GH_RELEASES_API =
        "https://api.github.com/repos/solida1987/Diablo-II-Archipelago/releases";

    // ── File skip lists (mirrors V1 GameDownloader.cs — must stay in sync) ────
    //
    // 8-script sync invariant: changes here must ALSO land in
    //   _pack_game.py, generate_manifest.py, regen_manifest_from_git.py,
    //   prerelease_check.bat, _audit_manifest.py, MainForm.cs ORIGINAL_D2_FILES,
    //   AND .gitignore  (V1 list — in V2 the launcher replaces GameDownloader.cs)

    // Original Blizzard MPQ data files — NEVER downloaded from GitHub.
    // Exemption: 1.10f loader binaries (Game.exe, D2*.dll, Storm.dll, Fog.dll)
    // MUST ship — they are the mod's foundation (1.9.5–1.9.8 lesson).
    private static readonly HashSet<string> ORIGINAL_D2_FILES =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "D2.LNG", "SmackW32.dll", "binkw32.dll",
        "d2exp.mpq", "d2music.mpq", "d2speech.mpq", "d2video.mpq",
        "d2xmusic.mpq", "d2xtalk.mpq", "d2xvideo.mpq",
        "ijl11.dll", "d2char.mpq", "d2data.mpq", "d2sfx.mpq",
    };

    // User-editable config files: present-is-valid; never clobber even if size differs.
    private static readonly HashSet<string> USER_EDITABLE_FILES =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "d2gl.ini", "D2Hackmap.ini", "d2sigma.ini",
        "d2gl_userkeys.ini", "keymap.ini", "d2arch.ini",
    };

    // Files written exclusively by the launcher after install — skip download
    // AND size-check so a stale manifest entry never resets them.
    private static readonly HashSet<string> LAUNCHER_MANAGED_FILES =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "version.dat",
    };

    // ── Original-game detection ───────────────────────────────────────────────

    /// True when the required original D2 data files are present in GameDirectory.
    /// Used by the UI install flow to know when to show the "select D2 folder" picker.
    public bool HasOriginalGameFiles()
    {
        if (string.IsNullOrEmpty(GameDirectory)) return false;
        foreach (string f in new[] { "d2data.mpq", "d2char.mpq", "d2exp.mpq" })
            if (File.Exists(Path.Combine(GameDirectory, f))) return true;
        return false;
    }

    /// Validate that a folder is the user's ORIGINAL Classic Diablo II + LoD
    /// install (the copy source). Returns null when valid, else a reason.
    /// Rejects Diablo II: Resurrected (binary-incompatible) and an existing AP
    /// install, and requires every Blizzard data file the mod needs.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return "That folder does not exist.";

        if (File.Exists(Path.Combine(folder, "D2R.exe")) ||
            Directory.Exists(Path.Combine(folder, "ClassicMode")))
            return "That is Diablo II: Resurrected. Archipelago needs the CLASSIC " +
                   "Diablo II + Lord of Destruction install instead.";

        if (File.Exists(Path.Combine(folder, "D2Archipelago.dll")))
            return "That folder is an existing Archipelago install, not your original " +
                   "Diablo II. Point to the folder where you originally installed " +
                   "Classic Diablo II + LoD (the one containing d2data.mpq).";

        var missing = ORIGINAL_D2_FILES
            .Where(f => !File.Exists(Path.Combine(folder, f)))
            .ToList();
        if (missing.Count > 0)
            return "That folder is missing required Diablo II files (" +
                   string.Join(", ", missing) + "). Select your Classic Diablo II + " +
                   "Lord of Destruction install folder (typically " +
                   "C:\\Program Files (x86)\\Diablo II).";

        return null;
    }

    /// Copy the original Blizzard data files (the copyrighted MPQs etc.) from the
    /// user's own Diablo II install (OriginalD2Directory) into the mod install
    /// dir (GameDirectory). These are NEVER downloaded from GitHub. Returns the
    /// list of files that could not be copied (empty = all present).
    private List<string> CopyOriginalD2Files()
    {
        var missing = new List<string>();
        if (string.IsNullOrEmpty(OriginalD2Directory) || !Directory.Exists(OriginalD2Directory))
        {
            missing.AddRange(ORIGINAL_D2_FILES);
            return missing;
        }
        Directory.CreateDirectory(GameDirectory);
        foreach (string file in ORIGINAL_D2_FILES)
        {
            string src = Path.Combine(OriginalD2Directory, file);
            string dst = Path.Combine(GameDirectory, file);
            if (!File.Exists(src)) { missing.Add(file); continue; }
            try { File.Copy(src, dst, overwrite: true); }
            catch { missing.Add(file); }
        }
        return missing;
    }

    /// Best-effort auto-detection of the user's Classic Diablo II install via the
    /// registry (Blizzard / GOG / Uninstall keys) and common paths. Returns the
    /// first folder that passes ValidateExistingInstall, or null if none found.
    public string? AutoDetectOriginalD2()
    {
        var candidates = new List<string>();
        void TryReg(RegistryKey root, string sub, string val)
        {
            try
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue(val) is string p && !string.IsNullOrWhiteSpace(p))
                    candidates.Add(p.TrimEnd('\\', '/'));
            }
            catch { }
        }
        TryReg(Registry.CurrentUser,  @"Software\Blizzard Entertainment\Diablo II", "InstallPath");
        TryReg(Registry.LocalMachine, @"SOFTWARE\Blizzard Entertainment\Diablo II", "InstallPath");
        TryReg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Blizzard Entertainment\Diablo II", "InstallPath");
        TryReg(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Diablo II", "InstallLocation");
        TryReg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Diablo II", "InstallLocation");
        TryReg(Registry.LocalMachine, @"SOFTWARE\GOG.com\Games\1435828550", "path");
        TryReg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games\1435828550", "path");
        candidates.Add(@"C:\Program Files (x86)\Diablo II");
        candidates.Add(@"C:\Program Files\Diablo II");
        candidates.Add(@"C:\GOG Games\Diablo II");
        candidates.Add(@"C:\Games\Diablo II");
        candidates.Add(@"D:\Diablo II");

        foreach (string c in candidates)
            if (ValidateExistingInstall(c) == null) return c;
        return null;
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public D2Plugin()
    {
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("D2Arch-Launcher/2.0");
        // AP item resume index is loaded by the GameDirectory setter — the
        // directory is not known yet at construction time.
    }

    // ── Version check ─────────────────────────────────────────────────────────

    /// Resolve the latest release tag via the releases/latest redirect.
    /// Does NOT use the GitHub REST API — immune to the 60 req/hour rate limit.
    private async Task<string?> FetchLatestTagAsync(CancellationToken ct)
    {
        if (_cachedLatestTag != null) return _cachedLatestTag;
        try
        {
            // GitHub redirects /releases/latest → /releases/tag/<tag> (HTTP 302).
            // AllowAutoRedirect=false lets us read the Location header directly.
            using var req = new HttpRequestMessage(HttpMethod.Head, GH_LATEST_PAGE);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client  = new HttpClient(handler, disposeHandler: true);
            client.DefaultRequestHeaders.UserAgent.TryParseAdd("Archipelago-Launcher/2.0");
            client.Timeout = TimeSpan.FromSeconds(15);
            using var resp = await client.SendAsync(req, ct);

            string? location = resp.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(location)) return null;

            // Extract tag from ".../releases/tag/<tag>"
            const string marker = "/releases/tag/";
            int idx = location.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;

            _cachedLatestTag = location[(idx + marker.Length)..].TrimEnd('/');
            return _cachedLatestTag;
        }
        catch { return null; }
    }

    /// Build a direct CDN download URL for a known asset filename.
    private static string DownloadUrl(string tag, string filename)
        => $"{GH_DOWNLOAD_BASE}/{tag}/{filename}";

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            string versionDat = Path.Combine(GameDirectory, "Archipelago", "version.dat");
            InstalledVersion = File.Exists(versionDat)
                ? File.ReadAllText(versionDat).Trim()
                : null;

            AvailableVersion = await FetchLatestTagAsync(ct);
        }
        catch
        {
            AvailableVersion = null;
        }
    }

    // ── Install / update ──────────────────────────────────────────────────────

    /// Full install or incremental update.
    /// • Fresh install: downloads game_package.zip from the GitHub release.
    /// • Update:        downloads only changed files via game_manifest.json.
    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((0, "Fetching release info..."));

        string? tag = await FetchLatestTagAsync(ct)
            ?? throw new InvalidOperationException(
                "Could not reach GitHub: check your internet connection and try again.");

        // Build direct CDN download URLs — no REST API call needed.
        string packageUrl  = DownloadUrl(tag, "game_package.zip");
        string manifestUrl = DownloadUrl(tag, "game_manifest.json");
        string apworldUrl  = DownloadUrl(tag, "diablo2_archipelago.apworld");

        if (!IsInstalled)
        {
            // Fresh install: full ZIP download + extract.
            await InstallFromZipAsync(packageUrl, apworldUrl, tag, progress, ct);
        }
        else
        {
            // Update: manifest tells us WHAT changed; the release ZIP provides the
            // bytes (same reliable source as a fresh install).
            await InstallFromManifestAsync(manifestUrl, packageUrl, apworldUrl, tag, progress, ct);
        }
    }

    // ── Repair (antivirus deletes D2Arch_Launcher.exe) ────────────────────────

    /// Mod binaries that MUST exist for the game to launch. Antivirus — especially
    /// Windows Defender — frequently quarantines D2Arch_Launcher.exe (the 32-bit
    /// injector) as a false positive; the Repair flow restores exactly these.
    private static readonly string[] CriticalModFiles =
    {
        "D2Arch_Launcher.exe",
        "D2Archipelago.dll",
        @"patch\D2Archipelago.dll",
    };

    /// Critical mod files currently missing from the install (paths relative to
    /// GameDirectory). Empty list = nothing to repair. Used by the launch-time
    /// Repair prompt so a quarantined file is a one-click fix, not a dead end.
    public List<string> GetMissingCriticalFiles()
    {
        var missing = new List<string>();
        if (!Directory.Exists(GameDirectory)) return missing;   // not installed → not a "repair" case
        foreach (string rel in CriticalModFiles)
            if (!File.Exists(Path.Combine(GameDirectory, rel)))
                missing.Add(rel);
        return missing;
    }

    /// Re-download the mod package and restore ONLY the missing critical files,
    /// without touching the rest of the install. Returns how many were restored.
    public async Task<int> RepairMissingFilesAsync(
        IProgress<(int Pct, string Msg)> progress, CancellationToken ct = default)
    {
        var missing = GetMissingCriticalFiles();
        if (missing.Count == 0) return 0;

        progress.Report((0, "Checking release..."));
        string? tag = await FetchLatestTagAsync(ct)
            ?? throw new InvalidOperationException(
                "Could not reach GitHub to download the missing files — check your internet connection.");
        string packageUrl = DownloadUrl(tag, "game_package.zip");

        string tempZip = Path.Combine(Path.GetTempPath(), "d2arch_repair.zip");
        try
        {
            progress.Report((5, "Downloading mod files..."));
            await DownloadZipWithRetryAsync(packageUrl, tempZip, 5, 85, progress, ct);
            ct.ThrowIfCancellationRequested();

            progress.Report((88, "Restoring missing files..."));
            var wanted = new HashSet<string>(
                missing.Select(m => m.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);

            int restored = 0;
            using var zip = ZipFile.OpenRead(tempZip);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (!wanted.Contains(entry.FullName.Replace('\\', '/'))) continue;
                string dest = Path.Combine(GameDirectory, entry.FullName.Replace('/', '\\'));
                string? dir = Path.GetDirectoryName(dest);
                if (dir != null) Directory.CreateDirectory(dir);
                entry.ExtractToFile(dest, overwrite: true);
                restored++;
            }
            progress.Report((100, $"Restored {restored} file(s)."));
            return restored;
        }
        finally { try { File.Delete(tempZip); } catch { /* ignore */ } }
    }

    /// Add the install folder to Windows Defender's exclusion list so it stops
    /// flagging D2Arch_Launcher.exe as a false positive (the mod injects into D2,
    /// which Defender treats as suspicious). Requires admin — launches an elevated
    /// PowerShell (UAC prompt). Returns true if the exclusion command succeeded.
    /// Returns false if the user declined UAC or Defender cmdlets aren't available
    /// (e.g. a third-party antivirus is the active provider).
    public bool AddDefenderExclusion()
    {
        try
        {
            string psPath = GameDirectory.Replace("'", "''");
            var psi = new ProcessStartInfo
            {
                FileName  = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command " +
                            $"\"Add-MpPreference -ExclusionPath '{psPath}'; " +
                            "Add-MpPreference -ExclusionProcess 'D2Arch_Launcher.exe'\"",
                UseShellExecute = true,   // required for Verb=runas
                Verb            = "runas",
                CreateNoWindow  = true,
                WindowStyle     = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(30000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task InstallFromZipAsync(
        string packageUrl, string? apworldUrl, string? actualTag,
        IProgress<(int, string)> progress, CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), "d2arch_install.zip");
        try
        {
            progress.Report((5, "Downloading game package..."));
            await DownloadZipWithRetryAsync(packageUrl, tempZip, 5, 80, progress, ct);

            // A cancelled install must THROW, not return — a silent return made
            // the caller report "Installation complete!" over a partial extract
            // and stamp version.dat below (P2-3).
            ct.ThrowIfCancellationRequested();

            progress.Report((82, "Extracting game files..."));
            Directory.CreateDirectory(GameDirectory);

            using var zip = ZipFile.OpenRead(tempZip);
            int total = zip.Entries.Count, done = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) continue;

                string dest = Path.Combine(GameDirectory, entry.FullName.Replace('/', '\\'));
                string? dir = Path.GetDirectoryName(dest);
                if (dir != null) Directory.CreateDirectory(dir);
                entry.ExtractToFile(dest, overwrite: true);

                done++;
                if (done % 20 == 0)
                    progress.Report((82 + done * 15 / total, $"Extracting... ({done}/{total})"));
            }

            if (apworldUrl != null)
            {
                progress.Report((97, "Downloading AP World..."));
                string apworldDir = Path.Combine(GameDirectory, "apworld");
                Directory.CreateDirectory(apworldDir);
                await DownloadSimpleAsync(apworldUrl,
                    Path.Combine(apworldDir, "diablo2_archipelago.apworld"), ct);
            }

            progress.Report((98, "Cleaning data cache..."));
            DeleteBinCache();
            Directory.CreateDirectory(Path.Combine(GameDirectory, "save"));

            // Copy the copyrighted Blizzard data files from the player's own
            // Diablo II install into the mod folder — these are never downloaded.
            // The game cannot run without them, so a failure aborts BEFORE
            // version.dat is written (IsInstalled stays false so a retry works).
            progress.Report((99, "Copying original Diablo II files..."));
            var missingOriginals = CopyOriginalD2Files();
            if (missingOriginals.Count > 0)
                throw new InvalidOperationException(
                    "Could not copy these original Diablo II files from your installation: " +
                    string.Join(", ", missingOriginals) + ".\n\nOpen Settings and point the " +
                    "launcher at your Classic Diablo II + Lord of Destruction folder " +
                    "(the one containing d2data.mpq), then install again.");

            progress.Report((99, "Writing version..."));
            WriteVersionDat(actualTag);

            progress.Report((100, "Installation complete!"));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    private async Task InstallFromManifestAsync(
        string manifestUrl, string packageUrl, string? apworldUrl, string? actualTagHint,
        IProgress<(int, string)> progress, CancellationToken ct)
    {
        progress.Report((5, "Downloading manifest..."));
        string manifestJson = await _http.GetStringAsync(manifestUrl, ct);

        // Cache manifest locally for offline verify
        try
        {
            Directory.CreateDirectory(GameDirectory);
            File.WriteAllText(Path.Combine(GameDirectory, "game_manifest.json"), manifestJson);
        }
        catch { }

        // Extract the version tag from the manifest (more reliable than release tag)
        string? manifestVersion = actualTagHint;
        JsonFindString(manifestJson, "version", ref manifestVersion);

        // NOTE (P2-3): version.dat is NOT stamped up front any more — an early
        // stamp made a failed/cancelled update report the new version forever
        // ("up to date" over stale files). The stamp now lands on each SUCCESS
        // exit: the 0-files-to-download path below (which preserves the V1
        // "stuck at Beta 1.9.4" self-heal) and the all-downloads-OK path.

        using var doc = JsonDocument.Parse(manifestJson);
        var root = doc.RootElement;

        // Build download list and manifest path set (for orphan cleanup)
        var toDownload = new List<(string path, string sha256, long size)>();
        var manifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("files", out var files))
        {
            progress.Report((10, "Comparing local files..."));
            int total = files.GetArrayLength(), checked_ = 0;

            foreach (var entry in files.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();

                string path   = entry.GetProperty("path").GetString() ?? "";
                string sha256 = entry.TryGetProperty("sha256", out var s) ? s.GetString() ?? "" : "";
                long   size   = entry.TryGetProperty("size",   out var z) ? z.GetInt64()        : 0;

                if (path.Length > 0)
                    manifestPaths.Add(path.Replace('\\', '/'));

                string fileName = Path.GetFileName(path);
                if (ORIGINAL_D2_FILES.Contains(fileName))      goto next;
                if (LAUNCHER_MANAGED_FILES.Contains(fileName)) goto next;

                string local = Path.Combine(GameDirectory, path.Replace('/', '\\'));
                bool needsDownload = true;
                if (File.Exists(local) && sha256.Length > 0 &&
                    ComputeSha256(local).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    needsDownload = false;
                }
                if (needsDownload) toDownload.Add((path, sha256, size));

                next:
                checked_++;
                if (checked_ % 10 == 0)
                    progress.Report((10 + checked_ * 20 / total,
                        $"Checking files... ({checked_}/{total})"));
            }
        }

        if (toDownload.Count == 0)
        {
            // Self-heal: even with nothing to download the stamp is refreshed,
            // so a stale version.dat on an otherwise-correct install recovers
            // (the V1 "stuck at Beta 1.9.4" lesson — both exit paths stamp).
            WriteVersionDat(manifestVersion);
            progress.Report((100, "All files up to date!"));
            return;
        }

        // Apply the update from the release ZIP — the SAME source a fresh install
        // uses. The previous per-file path pulled each file from the repo's `main`
        // branch (raw.githubusercontent .../main/game/<path>), but main's game/ tree
        // drifts out of sync with the RELEASED manifest: the binaries are an older
        // build, some data tables differ, and a few manifest files aren't on main at
        // all. So files downloaded fine (HTTP 200) but failed their SHA → "86 of
        // 173 file(s) failed", and the only workaround was deleting the whole game
        // to force the fresh-install ZIP path. The ZIP's bytes ALWAYS match the
        // manifest (built together), and it's ONE download instead of 800 raw
        // requests (no rate-limiting). We still extract only the CHANGED paths, so
        // an update rewrites just what differs.
        progress.Report((30, $"Downloading update ({toDownload.Count} changed file(s))..."));

        string tempZip = Path.Combine(Path.GetTempPath(), "d2arch_update.zip");
        int updated = 0, orphans = 0;
        try
        {
            await DownloadZipWithRetryAsync(packageUrl, tempZip, 30, 85, progress, ct);
            ct.ThrowIfCancellationRequested();

            var wanted = new HashSet<string>(
                toDownload.Select(t => t.path.Replace('\\', '/')),
                StringComparer.OrdinalIgnoreCase);

            progress.Report((86, "Applying update..."));
            using var zip = ZipFile.OpenRead(tempZip);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) continue;

                string rel = entry.FullName.Replace('\\', '/');
                if (!wanted.Contains(rel)) continue;   // unchanged — leave it

                string dest = Path.Combine(GameDirectory, rel.Replace('/', '\\'));
                string? dir = Path.GetDirectoryName(dest);
                if (dir != null) Directory.CreateDirectory(dir);
                entry.ExtractToFile(dest, overwrite: true);
                updated++;

                if (updated % 20 == 0)
                    progress.Report((86 + updated * 8 / Math.Max(1, wanted.Count),
                        $"Applying update... ({updated}/{wanted.Count})"));
            }
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }

        DeleteBinCache();

        // Remove orphan files (present locally but removed from the manifest).
        orphans = DeleteOrphans(manifestPaths);

        if (apworldUrl != null)
        {
            progress.Report((96, "Downloading AP World..."));
            try
            {
                string apworldDir = Path.Combine(GameDirectory, "apworld");
                Directory.CreateDirectory(apworldDir);
                await DownloadSimpleAsync(apworldUrl,
                    Path.Combine(apworldDir, "diablo2_archipelago.apworld"), ct);
            }
            catch { /* non-fatal — apworld update failure doesn't block the game */ }
        }

        // Heal: ensure the original Blizzard data files are present (an update to
        // an existing install should already have them; best-effort re-copy here).
        CopyOriginalD2Files();

        // Only now that the ZIP extracted cleanly do we stamp the new version —
        // a failed ZIP download throws above, so a broken update never stamps
        // (the next check still offers the update). (P2-3 lesson preserved.)
        WriteVersionDat(manifestVersion);

        string msg = $"Updated {updated} file(s).";
        if (orphans > 0) msg += $" Removed {orphans} stale file(s).";

        progress.Report((100, msg));
    }

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        // Fast size-only check, same design as V1's VerifyInstallationAsync.
        // SHA256 was tested in V1 but rejected — 5-15 s for ~355 files.
        string manifestPath = Path.Combine(GameDirectory, "game_manifest.json");
        string? manifestJson = null;

        if (File.Exists(manifestPath))
        {
            try { manifestJson = await File.ReadAllTextAsync(manifestPath, ct); }
            catch { }
        }

        if (manifestJson == null)
        {
            // Fetch from GitHub if no local copy — uses CDN redirect, no API quota.
            try
            {
                string? tag = await FetchLatestTagAsync(ct);
                if (tag != null)
                {
                    string mUrl = DownloadUrl(tag, "game_manifest.json");
                    manifestJson = await _http.GetStringAsync(mUrl, ct);
                    try { await File.WriteAllTextAsync(manifestPath, manifestJson, ct); } catch { }
                }
            }
            catch { return true; } // offline — don't block launch
        }

        if (manifestJson == null) return true;

        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (!doc.RootElement.TryGetProperty("files", out var files)) return true;

            foreach (var entry in files.EnumerateArray())
            {
                string path = entry.GetProperty("path").GetString() ?? "";
                if (path.Length == 0) continue;

                string fileName = Path.GetFileName(path);
                if (ORIGINAL_D2_FILES.Contains(fileName))      continue;
                if (LAUNCHER_MANAGED_FILES.Contains(fileName)) continue;

                string localPath = Path.Combine(GameDirectory, path.Replace('/', '\\'));
                if (!File.Exists(localPath)) return false;

                if (USER_EDITABLE_FILES.Contains(fileName)) continue; // present = valid

                long expectedSize = 0;
                if (entry.TryGetProperty("size", out var sizeEl)) expectedSize = sizeEl.GetInt64();
                if (expectedSize > 0 && new FileInfo(localPath).Length != expectedSize) return false;
            }
            return true;
        }
        catch { return true; }
    }

    // ── Launch ────────────────────────────────────────────────────────────────

    public async Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // 0. Pin D2 save path registry keys so D2 1.10f writes saves to our folder.
        PinSavePathRegistry();

        // Defensive teardown: any pipe left over from a previous session must
        // be fully gone before a new server stream is created.
        DisposePipe();

        // 1. Write AP credentials + the pipe name to d2arch.ini so the DLL can
        //    read them. The DLL discovers the pipe via [ap] PipeName=<name>
        //    (bare name, no \\.\pipe\ prefix — the DLL adds that itself).
        //    Per-launch random suffix: even if an old server stream lingers
        //    (GC-pending after a crash), the new launch never collides with it.
        string pipeName =
            $"d2arch_v2_{Environment.ProcessId}_{Guid.NewGuid().ToString("N")[..8]}";
        WriteApCredentials(session, pipeName);

        // 1.5 — AP parity: apply the same seed-bound txt randomization the standalone
        // path uses (monster / super-unique / shop shuffle + skill/item reqs), driven
        // by the AP slot_data + a stable per-world seed, with the same progress bar.
        // The DLL then skips its own monster/super-unique shuffle (LauncherDataShuffle)
        // but still does the act-boss cosmetic swap. No-op (DLL keeps its runtime
        // shuffle) if slot_data isn't available yet. RestorePristine runs on exit.
        await ApplyApDataTablesAsync(session);

        // 2. Start named pipe server BEFORE launching Game.exe so the DLL
        //    can connect immediately after injection. Byte mode: the DLL
        //    client reads a byte stream and frames messages by '\n', so the
        //    server must NOT use Message transmission mode.
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                       maxNumberOfServerInstances: 1,
                       transmissionMode: PipeTransmissionMode.Byte,
                       options: PipeOptions.Asynchronous);
        _pipe    = pipe;
        _pipeCts = new CancellationTokenSource();

        // 3. Launch Game.exe through the 32-bit bootstrap (D2Arch_Launcher.exe),
        //    which sets DIABLO2_PATCH (so D2.Detours loads the patched DLLs),
        //    launches the game via D2.Detours, and injects D2Archipelago.dll.
        //    The 64-bit launcher cannot inject into the 32-bit game itself.
        //    D2ARCH_AP_MODE keeps ap_settings.dat alive for the slot-data
        //    hand-off (the DLL scrubs it at startup otherwise).
        int launchCompleted = 0;   // 1 once the pipe handshake succeeded
        int exitRaised      = 0;   // GameExited must fire at most once
        var deathCts        = new CancellationTokenSource();
        Process? proc       = null;

        void RaiseExited()
        {
            if (Interlocked.Exchange(ref exitRaised, 1) == 1) return;
            IsRunning = false;
            ScrubApPassword();   // session over — blank the ini password (P3-20)
            if (_apAppliedDataTables)
            {
                _apAppliedDataTables = false;
                // Reset the seed-patched tables (with the "Nulstiller" bar), same as
                // the standalone exit handler — never leave the install patched.
                D2RandomizeProgress.RunRestoreWithProgress(GameDirectory);
            }
            int code = 0;
            try { code = proc?.ExitCode ?? 0; } catch { /* handle already gone */ }
            GameExited?.Invoke(code);
        }

        try
        {
            // Bootstrap inject (up to ~60 s) + pipe handshake, bounded by one
            // linked token: caller cancel + plugin teardown + process death +
            // a 90 s overall ceiling.
            using var launchCts = CancellationTokenSource.CreateLinkedTokenSource(
                ct, _pipeCts.Token, deathCts.Token);
            launchCts.CancelAfter(TimeSpan.FromSeconds(90));

            proc = await RunBootstrapAndFindGameAsync(apMode: true, launchCts.Token);
            _gameProcess = proc;
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                IsRunning = false;
                try { deathCts.Cancel(); } catch (ObjectDisposedException) { }
                if (Volatile.Read(ref launchCompleted) == 1)
                    RaiseExited();
            };

            // Game.exe is modded + running; wait for the DLL to open our pipe.
            await pipe.WaitForConnectionAsync(launchCts.Token);
        }
        catch (Exception ex)
        {
            // The launch is dead either way — never leave a half-modded
            // Game.exe running behind a failed bridge.
            try { proc?.Kill(entireProcessTree: true); } catch { }
            DisposePipe();
            IsRunning = false;

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException("Game launch was cancelled.", ct);

            if (ex is OperationCanceledException)
            {
                if (deathCts.IsCancellationRequested)
                    throw new InvalidOperationException(
                        "The game closed during startup before the mod could connect. " +
                        "Check your antivirus — add D2Archipelago.dll, Game.exe and the " +
                        "launcher to its exclusion list, then try again.");
                throw new InvalidOperationException(
                    "The game started but the mod never connected to the launcher. " +
                    "This usually means antivirus blocked the mod — add D2Archipelago.dll " +
                    "and the launcher to your antivirus exclusions, then try again.");
            }
            throw;   // bootstrap / injection errors already carry actionable text
        }
        finally
        {
            deathCts.Dispose();
        }

        IsRunning = true;
        Volatile.Write(ref launchCompleted, 1);

        // Closes the race where the process died between the pipe handshake
        // and the flag write above (the Exited handler saw launchCompleted=0).
        if (proc is { HasExited: true }) RaiseExited();

        // Push the current AP state immediately: if the AP server connected
        // before the game was up, the DLL would otherwise never receive a
        // STATE: message (OnApStateChanged only fires on transitions).
        if (_lastApState == ApConnectionState.Connected)
        {
            await WritePipeLineAsync("STATE:CONNECTED", ct);

            // The game just attached. Any AP items received while the player was
            // still in the launcher (notably the precollected STARTING SKILLS at
            // index 0) were dropped by ReceiveItemsAsync because the pipe wasn't
            // connected yet. Now that it is, ask the server to resend the full
            // item stream from index 0 so those items reach the DLL.
            //
            // Fire the resync from a BACKGROUND task with a readiness delay, NOT
            // inline: the DLL needs a moment after STATE:CONNECTED to stand up its
            // AP subsystem + skill tree. Resyncing the instant it attaches races
            // that startup and only some precollected skills get applied (testers
            // saw 3/6, 5/6). We re-sync a few times at increasing delays so even a
            // slow startup catches every item; once they've all landed the index
            // makes each repeat a no-op, so extra syncs are harmless.
            if (RequestApResync != null)
            {
                var resyncCt = _pipeCts?.Token ?? CancellationToken.None;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        foreach (int delayMs in new[] { 2000, 3500, 6000 })
                        {
                            await Task.Delay(delayMs, resyncCt);
                            if (_pipe?.IsConnected != true) return; // game gone
                            await RequestApResync();
                        }
                    }
                    catch { /* resync is best-effort; cancellation = game exited */ }
                }, resyncCt);
            }
        }

        _ = Task.Run(() => PipeLoopAsync(_pipeCts.Token));
    }

    public Task StopAsync()
    {
        DisposePipe();
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        ScrubApPassword();   // plaintext credentials die with the session (P3-20)
        return Task.CompletedTask;
    }

    /// Blank the [ap] Password in d2arch.ini once the session ends (P3-20) —
    /// the plaintext room password should not outlive the session on disk.
    /// Safe timing: the DLL reads the ini only at game startup, and every
    /// launch rewrites the credentials first (WriteApCredentials).
    private void ScrubApPassword()
    {
        try
        {
            string ini = Path.Combine(GameDirectory, "Archipelago", "d2arch.ini");
            if (!File.Exists(ini)) return;
            var lines = new List<string>(File.ReadAllLines(ini));
            SetIniSectionValue(lines, "ap", "Password", "");
            File.WriteAllLines(ini, lines);
        }
        catch { /* best effort — the next launch overwrites the file anyway */ }
    }

    /// Tear down the named pipe + its CTS. Safe to call from any state and
    /// more than once — every step is null-swapped and exception-tolerant.
    /// Without this, the single-instance server stream of a finished session
    /// lingers until GC and the next launch's server creation fails
    /// nondeterministically (UnauthorizedAccessException/IOException).
    private void DisposePipe()
    {
        var cts = _pipeCts;
        _pipeCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }

        var pipe = _pipe;
        _pipe = null;
        if (pipe != null)
        {
            try { pipe.Dispose(); } catch { }
        }
    }

    // ── AP data-table parity (mirror the standalone txt randomization) ────────

    /// Set once we've applied seed-patched tables for an AP session, so the exit
    /// handler knows to reset the install (and only then shows the reset bar).
    private bool _apAppliedDataTables;

    /// Run the standalone seed-bound txt randomization for an AP world: read the
    /// toggles from slot_data, derive a stable per-world seed, generate+apply the
    /// tables (with the progress bar), and flag the DLL to skip its own monster/
    /// super-unique shuffle. Best-effort — if slot_data isn't ready (connected after
    /// launch) it does nothing and the DLL keeps doing the shuffle at runtime.
    private async Task ApplyApDataTablesAsync(ApSession session)
    {
        try
        {
            if (GetSlotData?.Invoke() is not JsonElement sd || sd.ValueKind != JsonValueKind.Object)
                return;

            var settings = D2RandomizerSettings.FromSlotData(sd);
            long seed = StableApSeed(session);
            string saveFolder = new D2SeedLibrary(GameDirectory).SeedFolder(seed);
            Directory.CreateDirectory(saveFolder);

            // Tell the DLL the launcher owns the monster/super-unique shuffle this
            // session (it still does the act-boss cosmetic swap itself). Written to
            // d2arch.ini [settings]; the DLL reads it in both AP and offline modes.
            try
            {
                string ini = Path.Combine(GameDirectory, "Archipelago", "d2arch.ini");
                var lines = File.Exists(ini)
                    ? new List<string>(File.ReadAllLines(ini)) : new List<string>();
                SetIniSectionValue(lines, "settings", "LauncherDataShuffle", "1");
                // 2.x one-chest: give the DLL this AP world's per-seed key (the same
                // stable FNV value the data-file randomization uses) + the stash
                // isolation choice from slot_data, so the chest keys per-seed in AP.
                SetIniSectionValue(lines, "settings", "SeedKey", seed.ToString());
                SetIniSectionValue(lines, "settings", "StashIsolated", settings.StashIsolated ? "1" : "0");
                File.WriteAllLines(ini, lines);
            }
            catch { /* non-fatal */ }

            await D2RandomizeProgress.RunApplyAsync(settings, seed, saveFolder, GameDirectory);
            _apAppliedDataTables = true;
        }
        catch { /* non-fatal — fall back to the DLL's runtime shuffle */ }
    }

    /// Stable, reproducible per-world seed for AP data-file randomization: derived
    /// from the AP room seed name (falling back to the server) + the slot name, so
    /// the same multiworld always yields the same local cosmetic randomization.
    private long StableApSeed(ApSession session)
    {
        string seedName = GetSeedName?.Invoke() ?? "";
        string basis = (string.IsNullOrEmpty(seedName) ? session.ServerUri : seedName)
                       + "|" + session.SlotName;
        ulong h = 1469598103934665603UL;          // FNV-1a 64-bit
        foreach (char c in basis) { h ^= c; h *= 1099511628211UL; }
        return (long)(h & 0x7FFFFFFFFFFFFFFFUL);
    }

    // ── Standalone launch (no AP connection) ──────────────────────────────────

    public bool SupportsStandalone => true;

    /// Launch a SOLO randomized run — no Archipelago server. Pops the randomizer
    /// dialog (same options the apworld exposes), writes the choices to
    /// d2arch.ini [settings], then launches the game through the 32-bit
    /// D2Arch_Launcher.exe bootstrap (which injects D2Archipelago.dll — the
    /// 64-bit launcher cannot inject into the 32-bit game itself) WITHOUT a pipe
    /// and WITHOUT D2ARCH_AP_MODE, so the mod runs offline and applies the local
    /// randomization. This is what makes standalone actually randomize.
    public async Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        if (!IsInstalled)
            throw new InvalidOperationException("Diablo II: Lord of Destruction is not installed.");

        string gameExe   = Path.Combine(GameDirectory, "Game.exe");
        string bootstrap = Path.Combine(GameDirectory, "D2Arch_Launcher.exe");
        string dllPath   = Path.Combine(GameDirectory, "D2Archipelago.dll");
        if (!File.Exists(gameExe))
            throw new InvalidOperationException(
                $"Game.exe not found at:\n{gameExe}\n\nVerify your install directory in Settings.");
        if (!File.Exists(bootstrap) || !File.Exists(dllPath))
            throw new InvalidOperationException(
                "The Archipelago mod files are missing from the install folder " +
                "(D2Arch_Launcher.exe / D2Archipelago.dll). Re-install the game " +
                "from the Play tab, then try again.");

        // 1. Randomizer + seed library dialog (UI thread — still synchronous here
        //    before the first await). The user either starts a NEW seed (with the
        //    chosen options) or LOADS a previous one. Cancel aborts quietly.
        var lib = new D2SeedLibrary(GameDirectory);
        var choice = D2StandaloneSettingsDialog.ShowAndGet(
            Application.Current?.MainWindow, ReadStandaloneSettings(), lib);
        if (choice == null)
            throw new OperationCanceledException("Standalone launch cancelled.");

        // 2. Resolve the world: ShuffleSeed must match the seed so the mod
        //    reproduces it; persist the seed's settings + isolate its characters
        //    in its own save folder.
        var settings = choice.Settings;
        settings.Seed = choice.Seed;
        lib.SaveMeta(new D2SeedInfo { Seed = choice.Seed, Settings = settings });
        string saveFolder = lib.SeedFolder(choice.Seed);
        Directory.CreateDirectory(saveFolder);

        // 2.1 — Seed-bound data tables. Generate this seed's complete excel set
        // (skill/item level + stat requirements baked in per its settings, default
        // or not) into its folder, then overlay onto the live install so the game
        // loads the seed's tables. RestorePristine in the Exited handler resets the
        // install afterwards; ApplySeed also restores-then-overlays so a prior crash
        // never leaves the folder patched. The on-screen bar steps through
        // backup → generate → apply → confirm so it's visible the world is randomized.
        await D2RandomizeProgress.RunApplyAsync(settings, choice.Seed, saveFolder, GameDirectory);

        // 3. Persist the chosen options. We write a PipeName (but blank server/
        //    slot/pass) so the mod opens the launcher pipe and streams CHECK:
        //    for the TRACKER — the DLL sends checks whenever the pipe is open,
        //    independent of any AP connection. We never send STATE:CONNECTED, so
        //    the mod stays offline and grants rewards locally (true standalone).
        string pipeName =
            $"d2arch_v2_{Environment.ProcessId}_{Guid.NewGuid().ToString("N")[..8]}";
        WriteStandaloneSettings(settings, pipeName);

        // Open the pipe BEFORE launch so the DLL can connect at init.
        DisposePipe();
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                       maxNumberOfServerInstances: 1,
                       transmissionMode: PipeTransmissionMode.Byte,
                       options: PipeOptions.Asynchronous);
        _pipe    = pipe;
        _pipeCts = new CancellationTokenSource();

        // 4. Launch + inject via the 32-bit bootstrap (which pins the per-seed
        //    save folder via D2ARCH_SAVE_PATH), then attach to Game.exe.
        Process game = await RunBootstrapAndFindGameAsync(apMode: false, ct, saveFolder);
        _gameProcess = game;

        // Capture the local `game` for the exit handler — reading the field
        // would throw if a relaunch replaced it before this process exits.
        game.EnableRaisingEvents = true;
        game.Exited += (_, _) =>
        {
            IsRunning = false;
            DisposePipe();   // tear down the tracker pipe with the game
            // 2.1 — reset seed-patched data tables, with a small "nulstiller" bar
            // (falls back to a silent restore if the app is already shutting down).
            D2RandomizeProgress.RunRestoreWithProgress(GameDirectory);
            int code = 0;
            try { code = game.ExitCode; } catch { /* handle already gone */ }
            GameExited?.Invoke(code);
        };

        IsRunning = true;
        if (game.HasExited) { IsRunning = false; DisposePipe(); return; }

        // 5. Pump the DLL's CHECK: messages to the launcher's tracker. Best-effort
        //    on a background task: a pipe that never connects must NOT break the
        //    already-running game — the tracker just stays empty that session.
        var pipeCts = _pipeCts;
        _ = Task.Run(async () =>
        {
            try
            {
                using var pcts = CancellationTokenSource.CreateLinkedTokenSource(pipeCts.Token);
                pcts.CancelAfter(TimeSpan.FromSeconds(30));
                await pipe.WaitForConnectionAsync(pcts.Token);
                await PipeLoopAsync(pipeCts.Token);
            }
            catch { /* no tracker this session */ }
        });
    }

    // ── AP bridge: items in, checks out ──────────────────────────────────────

    /// Write one protocol line ("ITEM:123", "STATE:CONNECTED", ...) to the
    /// pipe with the '\n' terminator the DLL frames on. All writers go
    /// through here so messages never interleave mid-line. Best-effort:
    /// a broken pipe (game exited) is swallowed — the read loop handles
    /// the disconnect. Returns true only when the line was actually written
    /// AND flushed, so callers (notably ReceiveItemsAsync) can avoid marking an
    /// item delivered when the pipe dropped mid-batch.
    private async Task<bool> WritePipeLineAsync(string line, CancellationToken ct = default)
    {
        var pipe = _pipe;
        if (pipe?.IsConnected != true) return false;

        byte[] msg = Encoding.UTF8.GetBytes(line + "\n");
        await _pipeWriteLock.WaitAsync(ct);
        try
        {
            await pipe.WriteAsync(msg, ct);
            await pipe.FlushAsync(ct);
            return true;
        }
        catch (IOException)              { /* pipe broken — game exited */ }
        catch (ObjectDisposedException)  { /* pipe torn down */ }
        catch (InvalidOperationException){ /* pipe no longer connected */ }
        finally
        {
            _pipeWriteLock.Release();
        }
        return false;
    }

    public async Task ReceiveItemsAsync(ApNetworkItem[] items, int index,
        CancellationToken ct = default)
    {
        // Load the index for THIS room's seed (handles the player having switched
        // seeds since the plugin was constructed) before reading it.
        EnsureApIndexForCurrentSeed();

        // AP resume contract: the server replays the FULL item history from
        // index 0 on every connect/Sync. Skip the prefix the game already
        // received and deliver only the remainder — a blanket
        // "index < _apItemIndex → return" would silently drop never-delivered
        // items riding at the tail of the same catch-up batch.
        int skip = Math.Max(0, _apItemIndex - index);
        if (skip >= items.Length) return;   // every item is an already-seen duplicate

        // Only count items as consumed when the game is actually attached:
        // advancing the index without writing to the pipe would mark them
        // delivered and lose them permanently. Items that arrive while no
        // game runs are re-requested by the post-launch Sync.
        if (_pipe?.IsConnected != true) return;

        // ITEM v2: "ITEM:<id>|<sender>|<locationId>". Sender is a display
        // string (in-game "from <sender>" banners + self-release detect);
        // location is the NUMERIC AP id — the DLL sscanf's %d and uses it
        // for self-release auto-complete. Plain "ITEM:<id>" stays the
        // fallback when no resolver is wired — the DLL accepts both.
        // Deliver in order, counting only what actually reached the pipe. If a
        // write fails mid-batch (the pipe dropped, or the game is still standing
        // up), STOP and DON'T mark the rest delivered — the next Sync/reconnect
        // replays from index 0 and the undelivered tail (e.g. precollected
        // starting skills) is re-sent instead of being silently lost.
        int delivered = 0;
        foreach (var item in items.Skip(skip))
        {
            bool ok;
            if (ResolvePlayerName != null)
            {
                string sender = SanitizePipeField(
                    ResolvePlayerName(item.Player) ?? $"Player {item.Player}");
                ok = await WritePipeLineAsync(
                    $"ITEM:{item.ItemId}|{sender}|{item.LocationId}", ct);
            }
            else
            {
                ok = await WritePipeLineAsync($"ITEM:{item.ItemId}", ct);
            }
            if (!ok) break;
            delivered++;
        }

        if (delivered > 0)
        {
            // Highest contiguous index actually delivered = batch start + the
            // already-seen prefix + what we just wrote.
            _apItemIndex = Math.Max(_apItemIndex, index + skip + delivered);
            PersistApIndex();
        }
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        _lastApState = state;

        // Order matters: the DLL's LoadAPSettings only reads ap_settings.dat
        // once it believes AP is connected — so the file must be on disk
        // BEFORE the STATE:CONNECTED push reaches the game.
        if (state == ApConnectionState.Connected &&
            GetSlotData?.Invoke() is JsonElement sd)
            WriteApSettingsFile(sd);

        // Tell the DLL so it can flip its in-game connection indicator.
        // Protocol only knows CONNECTED/DISCONNECTED — Connecting/Error
        // map to DISCONNECTED (the DLL treats them as "not usable yet").
        _ = WritePipeLineAsync(state == ApConnectionState.Connected
            ? "STATE:CONNECTED"
            : "STATE:DISCONNECTED");

        try
        {
            string flagPath = Path.Combine(GameDirectory, "Archipelago", "ap_connected.flag");
            File.WriteAllText(flagPath, state == ApConnectionState.Connected ? "1" : "0");
        }
        catch { }
    }

    // ── Named pipe receive loop ───────────────────────────────────────────────

    private async Task PipeLoopAsync(CancellationToken ct)
    {
        // Byte-mode pipe: one ReadAsync can deliver a partial message, or
        // several messages back-to-back. Accumulate raw bytes and only act
        // on complete '\n'-terminated lines; whatever trails the last '\n'
        // stays in the buffer for the next read.
        //
        // The pipe instance is captured here: the loop must read from — and
        // finally dispose — ITS OWN pipe even if a relaunch already swapped
        // the _pipe field to a fresh server stream.
        var pipe    = _pipe;
        var buf     = new byte[4096];
        var pending = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested && pipe?.IsConnected == true)
            {
                int n = await pipe.ReadAsync(buf, ct);
                if (n == 0) break;

                pending.Append(Encoding.UTF8.GetString(buf, 0, n));

                while (true)
                {
                    string all = pending.ToString();
                    int nl = all.IndexOf('\n');
                    if (nl < 0) break;

                    string msg = all[..nl].TrimEnd('\r');
                    pending.Clear();
                    pending.Append(all[(nl + 1)..]);

                    if (msg.Length == 0) continue;

                    // A faulting subscriber (UI-layer handler) must never kill
                    // this loop — that would silently drop every further CHECK
                    // and GOAL from the game for the rest of the session.
                    try { HandlePipeMessage(msg); }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[D2Plugin] Pipe message handler failed for '{msg}': {ex}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* clean shutdown */ }
        catch (IOException)                { /* pipe disconnected — game exited */ }
        catch (ObjectDisposedException)    { /* pipe torn down mid-read */ }
        finally
        {
            // Dispose our own stream so the next launch can create a fresh
            // single-instance server without colliding with this one.
            try { pipe?.Dispose(); } catch { }
            if (ReferenceEquals(_pipe, pipe)) _pipe = null;
        }
    }

    /// Dispatch one complete newline-framed message from the DLL.
    private void HandlePipeMessage(string msg)
    {
        // "CHECK:<locationId1>,<locationId2>,..."
        if (msg.StartsWith("CHECK:", StringComparison.Ordinal))
        {
            var ids = new List<long>();
            foreach (string p in msg[6..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (long.TryParse(p.Trim(), out long id))
                    ids.Add(id);
            if (ids.Count > 0)
            {
                LocationsChecked?.Invoke(ids.ToArray());
                foreach (var id in ids) { _mapChecked.Add(id); _mapActive.Add(id); }
                _mapControl?.SetLocations(_mapActive, _mapChecked);   // per-area checklist
            }
        }
        // "MISSING:<id,...>" — the game's FULL active location universe (sent in
        // standalone so the tracker shows unchecked locations + totals like AP).
        else if (msg.StartsWith("MISSING:", StringComparison.Ordinal))
        {
            var ids = new List<long>();
            foreach (string p in msg[8..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (long.TryParse(p.Trim(), out long id))
                    ids.Add(id);
            if (ids.Count > 0)
            {
                LocationsMissing?.Invoke(ids.ToArray());
                foreach (var id in ids) _mapActive.Add(id);
                _mapControl?.SetLocations(_mapActive, _mapChecked);   // per-area checklist universe
            }
        }
        // "RECV:<text>" — standalone reward notification ("<location>: <reward>"),
        // surfaced in the tracker's Received tab so solo runs show what each check gave.
        else if (msg.StartsWith("RECV:", StringComparison.Ordinal))
        {
            string text = msg[5..].Trim();
            if (text.Length > 0) StandaloneItemReceived?.Invoke(text);
        }
        // "POS:<levelId>|<x>|<y>" — live player position for the map tracker dot.
        else if (msg.StartsWith("POS:", StringComparison.Ordinal))
        {
            var parts = msg[4..].Split('|');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out int lvl) &&
                int.TryParse(parts[1], out int x) &&
                int.TryParse(parts[2], out int y))
            {
                _lastMapPos = new D2PlayerPos { LevelId = lvl, X = x, Y = y };
                _mapControl?.SetPlayer(_lastMapPos);   // SetPlayer marshals to the UI thread
            }
        }
        // "GOAL" — player completed the Archipelago goal
        else if (msg == "GOAL")
        {
            // Caller wires GoalCompleted → ApClient.SendStatusUpdateAsync(Goal)
            GoalCompleted?.Invoke();
        }
        // "DEATH:<cause>" — local player died (DeathLink send-side).
        // The UI layer forwards to the AP server only when DeathLink is on.
        else if (msg.StartsWith("DEATH:", StringComparison.Ordinal))
        {
            OnPlayerDied?.Invoke(msg[6..].Trim());
        }
        // "LOG:<text>" — diagnostic from DLL
        // (handled externally by wiring PrintMessage on ApClient)
    }

    // ── DeathLink + slot-data bridge (launcher → game) ───────────────────────

    /// Forward a DeathLink death from another player into the game.
    /// Pipe grammar: "DEATHLINK:<source>|<cause>" — the DLL always shows an
    /// in-game notification; it only kills the player when the user opted in
    /// via d2arch.ini [settings] DeathLinkReceive=1 (ships dark, default off).
    public Task SendDeathLinkToGameAsync(string source, string cause)
        => WritePipeLineAsync(
            $"DEATHLINK:{SanitizePipeField(source)}|{SanitizePipeField(cause)}");

    /// Write AP slot_data to Archipelago/ap_settings.dat in the exact format
    /// the V1 Python bridge used: flat "key=value" lines, no sections, no
    /// whitespace around '=', UTF-8 without BOM (a BOM would break the first
    /// key's column-0 sscanf match), atomic replace. The DLL's LoadAPSettings
    /// prefers this file over the d2arch.ini [settings] fallback — without
    /// it, characters created in a V2 session would freeze LOCAL ini settings
    /// instead of the multiworld seed's.
    public void WriteApSettingsFile(JsonElement slotData)
    {
        try
        {
            if (slotData.ValueKind != JsonValueKind.Object) return;

            var sb = new StringBuilder();
            foreach (var prop in slotData.EnumerateObject())
            {
                // The DLL parses values with sscanf("%d") — every flag must be
                // a decimal integer. JSON bools map to 1/0; nulls are skipped
                // (absent key = DLL default, same as V1 never writing it).
                if (prop.Value.ValueKind == JsonValueKind.Null) continue;
                sb.Append(prop.Name).Append('=')
                  .Append(FormatSlotValue(prop.Value)).Append('\n');
            }

            string archDir = Path.Combine(GameDirectory, "Archipelago");
            Directory.CreateDirectory(archDir);
            string finalPath = Path.Combine(archDir, "ap_settings.dat");
            string tempPath  = finalPath + ".tmp";
            File.WriteAllText(tempPath, sb.ToString());   // UTF-8, no BOM
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch { /* non-fatal — DLL falls back to d2arch.ini [settings] */ }
    }

    /// Slot values as the DLL's integer-only parser expects them:
    /// bools → 1/0, numbers/strings raw (seed's big int stays raw digits).
    private static string FormatSlotValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True   => "1",
        JsonValueKind.False  => "0",
        JsonValueKind.String => value.GetString() ?? "",
        _                    => value.GetRawText(),   // numbers / arrays / objects
    };

    /// Pipe fields are |-separated and newline-framed — strip both from
    /// display strings (plus CR) so a hostile slot name can't break framing.
    private static string SanitizePipeField(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("|", "/").Replace("\n", " ").Replace("\r", " ").Trim();
    }

    // ── Map tracker ────────────────────────────────────────────────────────────

    public bool SupportsMapTracker => true;

    private D2MapTrackerControl? _mapControl;
    private D2PlayerPos?         _lastMapPos;   // last POS seen before the panel existed
    private readonly HashSet<long> _mapChecked = new();   // checked location ids (CHECK:)
    private readonly HashSet<long> _mapActive  = new();   // run's location universe (MISSING:)

    /// The launcher hosts this under the Map tab. Cached so the live data
    /// pipeline (SetWorld/SetPlayer — fed from the DLL's per-area map export +
    /// the POS pipe messages) keeps updating one persistent control.
    public UIElement? CreateMapTrackerPanel()
    {
        if (_mapControl == null)
        {
            _mapControl = new D2MapTrackerControl();
            if (_lastMapPos != null) _mapControl.SetPlayer(_lastMapPos);
        }
        // Seed the checklist with anything already tracked this session.
        _mapControl.SetLocations(_mapActive, _mapChecked);
        // Point it at the DLL's live map-export folder so it fills in (and shows
        // the "you are here" dot) as the player explores.
        if (!string.IsNullOrEmpty(GameDirectory))
            _mapControl.SetMapSource(Path.Combine(GameDirectory, "Archipelago", "map"));
        return _mapControl;
    }

    // ── Settings UI ──────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted  = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg     = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var panel  = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Section: Install directory ─────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "ORIGINAL DIABLO II (your own copy)", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text         = "The folder where your own Classic Diablo II: Lord of Destruction is " +
                           "installed. The launcher copies the original game files from here into " +
                           "its own install folder — your copy is never modified.",
            FontSize     = 11, Foreground = muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow   = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var dirBox   = new TextBox
        {
            Text        = OriginalD2Directory,
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dirBtn = new Button
        {
            Content = "Browse...", Width = 90,
            Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            // Picks the user's OWN Classic Diablo II folder (the copy source) —
            // never the mod install dir. Validated before it is accepted.
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select your Classic Diablo II: Lord of Destruction folder",
                InitialDirectory = Directory.Exists(OriginalD2Directory) ? OriginalD2Directory
                                   : @"C:\Program Files (x86)",
            };
            if (dlg.ShowDialog() == true)
            {
                string? err = ValidateExistingInstall(dlg.FolderName);
                if (err != null)
                {
                    System.Windows.MessageBox.Show(err, "Folder not recognized",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                OriginalD2Directory = dlg.FolderName;
                dirBox.Text         = dlg.FolderName;
                SaveD2Settings();
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        // "Open install folder" shortcut
        var openFolderBtn = new Button
        {
            Content             = "📁  Open Install Folder",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding             = new Thickness(10, 6, 10, 6),
            Background          = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground          = fg,
            BorderBrush         = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            FontSize            = 12,
            Cursor              = System.Windows.Input.Cursors.Hand,
            Margin              = new Thickness(0, 0, 0, 16),
        };
        openFolderBtn.Click += (_, _) =>
        {
            if (Directory.Exists(GameDirectory))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{GameDirectory}\"")
                    { UseShellExecute = true });
        };
        panel.Children.Add(openFolderBtn);

        // ── Section: Version info ──────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "VERSION INFO", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text     = $"Installed: {InstalledVersion ?? "Not installed"}",
            FontSize = 12, Foreground = fg, Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text     = $"Latest available: {AvailableVersion ?? "Unknown (not checked yet)"}",
            FontSize = 12, Foreground = muted, Margin = new Thickness(0, 0, 0, 16),
        });

        // ── Section: Launch options ────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LAUNCH OPTIONS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        var ls = SettingsStore.Load();

        var chkWindowed = new CheckBox
        {
            Content   = "Windowed mode  (-w)",
            IsChecked = ls.D2Windowed,
            Foreground = fg, FontSize = 12,
            Margin    = new Thickness(0, 0, 0, 6),
        };
        chkWindowed.Checked   += (_, _) => { var s = SettingsStore.Load(); s.D2Windowed = true;  SettingsStore.Save(s); };
        chkWindowed.Unchecked += (_, _) => { var s = SettingsStore.Load(); s.D2Windowed = false; SettingsStore.Save(s); };
        panel.Children.Add(chkWindowed);

        var chkNoSound = new CheckBox
        {
            Content   = "Disable music/sound  (-ns)",
            IsChecked = ls.D2NoSound,
            Foreground = fg, FontSize = 12,
            Margin    = new Thickness(0, 0, 0, 16),
        };
        chkNoSound.Checked   += (_, _) => { var s = SettingsStore.Load(); s.D2NoSound = true;  SettingsStore.Save(s); };
        chkNoSound.Unchecked += (_, _) => { var s = SettingsStore.Load(); s.D2NoSound = false; SettingsStore.Save(s); };
        panel.Children.Add(chkNoSound);

        // ── Section: Graphics (D2GL) ───────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GRAPHICS (D2GL)", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Fullscreen, window size, v-sync, FPS caps and more — set them here " +
                   "before launch instead of the in-game Ctrl+O menu.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var gfxBtn = new Button
        {
            Content             = "🖵  Graphics settings…",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding             = new Thickness(10, 6, 10, 6),
            Background          = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground          = fg,
            BorderBrush         = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            FontSize            = 12,
            Cursor              = System.Windows.Input.Cursors.Hand,
            Margin              = new Thickness(0, 0, 0, 16),
        };
        gfxBtn.Click += (_, _) =>
        {
            if (!Directory.Exists(GameDirectory))
            {
                System.Windows.MessageBox.Show(
                    "Install Diablo II from the Play tab first — the graphics config " +
                    "lives in the game folder.", "Not installed yet",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
            D2GLSettingsDialog.ShowFor(Application.Current?.MainWindow, GameDirectory);
        };
        panel.Children.Add(gfxBtn);

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("GitHub Repository ↗",    "https://github.com/solida1987/Diablo-II-Archipelago"),
            ("GitHub Releases ↗",      "https://github.com/solida1987/Diablo-II-Archipelago/releases"),
            ("Archipelago Official ↗", "https://archipelago.gg"),
        })
        {
            var linkBtn = new Button
            {
                Content     = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding     = new Thickness(0, 2, 0, 2),
                Background  = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground  = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                FontSize    = 12,
                Cursor      = System.Windows.Input.Cursors.Hand,
                Margin      = new Thickness(0, 0, 0, 4),
            };
            string capturedUrl = url;
            linkBtn.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(capturedUrl) { UseShellExecute = true }); }
                catch { /* ignore */ }
            };
            panel.Children.Add(linkBtn);
        }

        return panel;
    }

    /// Pin D2 1.10f save path in registry so characters are always saved to
    /// <GameDirectory>\save\ regardless of where a previous install wrote the keys.
    /// D2 1.10f reads HKCU\Software\Blizzard Entertainment\Diablo II\Save Path
    /// and NewSavePath on every launch.
    private void PinSavePathRegistry()
    {
        try
        {
            string savePath = Path.Combine(GameDirectory, "save");
            Directory.CreateDirectory(savePath);

            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Blizzard Entertainment\Diablo II", writable: true);
            key.SetValue("Save Path",    savePath, RegistryValueKind.String);
            key.SetValue("NewSavePath",  savePath, RegistryValueKind.String);
        }
        catch
        {
            // Non-fatal — D2 will fall back to its own default save location.
        }
    }

    /// Build the extra command-line flags from launcher settings (windowed, no-sound).
    private static string BuildExtraLaunchArgs()
    {
        var ls  = SettingsStore.Load();
        var sb  = new StringBuilder();
        if (ls.D2Windowed) sb.Append(" -w");
        if (ls.D2NoSound)  sb.Append(" -ns");
        return sb.ToString();
    }

    private void SaveD2Settings()
    {
        // Write to the launcher's central settings file so the path persists
        // across restarts (App.xaml.cs reads LauncherSettings.DiabloIIPath on startup).
        var ls = SettingsStore.Load();
        ls.DiabloIIPath = OriginalD2Directory;
        SettingsStore.Save(ls);

        // Also keep the per-plugin file for backward-compat / third-party tooling.
        string dir = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "d2_settings.json"),
            JsonSerializer.Serialize(new { game_directory = GameDirectory }));
    }

    // (LoadD2Settings was removed — dead code, P3-14. The launcher's own
    //  SettingsStore.DiabloIIPath is the authoritative load path; the
    //  d2_settings.json written above stays as a one-way export for
    //  third-party tooling.)

    // ── IGamePlugin: catalog + news ───────────────────────────────────────────

    public string Description =>
        "A full Archipelago randomiser mod for Diablo II Lord of Destruction. " +
        "Randomises skills, quests, zones and items across a multiworld. " +
        "Play solo or in a multiworld with friends on any AP server.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls => Array.Empty<string>();
    public string   ApWorldName    => "Diablo II Archipelago";
    public string   ThemeAccentColor => "#7A1010";   // blood-red
    // State-aware (RefreshHeaderBadges rebuilds when requirement state changes):
    // the "Requires D2: LoD" badge disappears once the user has pointed the
    // launcher at a valid original Diablo II: LoD install — the requirement is
    // then satisfied, leaving only the green "✓ Installed" badge.
    public string[] GameBadges =>
        IsOriginalD2Configured ? Array.Empty<string>() : new[] { "Requires D2: LoD" };

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(
                "https://api.github.com/repos/solida1987/Diablo-II-Archipelago/releases", ct);
            using var doc  = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",         out var n) ? n.GetString()! : "",
                    Body:    el.TryGetProperty("body",         out var b) ? b.GetString()! : "",
                    Version: el.TryGetProperty("tag_name",     out var t) ? t.GetString()! : "",
                    Date:    el.TryGetProperty("published_at", out var d)
                                 ? DateTimeOffset.Parse(d.GetString()!)
                                 : DateTimeOffset.MinValue,
                    Url:     el.TryGetProperty("html_url",     out var u) ? u.GetString()  : null
                ));
                if (items.Count >= 20) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Helpers: AP credentials → d2arch.ini ─────────────────────────────────

    private void WriteApCredentials(ApSession session, string pipeName)
    {
        string ini = Path.Combine(GameDirectory, "Archipelago", "d2arch.ini");
        try
        {
            // A missing ini (partial install / manual cleanup) used to silently
            // skip the write — the DLL then had no server/slot/pipe name and
            // the player saw an unexplained in-game "not connected" (P2-14).
            // Create it: SetIniSectionValue appends the [ap] section + keys to
            // an empty file, which is a valid minimal config for the DLL
            // (other sections simply fall back to their defaults).
            List<string> lines;
            if (File.Exists(ini))
            {
                lines = new List<string>(File.ReadAllLines(ini));
            }
            else
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(ini) ?? GameDirectory);
                lines = new List<string>();
            }

            // Strip scheme prefix from server URI for the DLL's TCP parser
            string serverEntry = session.ServerUri.Contains("://")
                ? session.ServerUri.Split("://", 2)[1]
                : session.ServerUri;

            SetIniSectionValue(lines, "ap", "ServerIP", serverEntry);
            SetIniSectionValue(lines, "ap", "SlotName", session.SlotName);
            SetIniSectionValue(lines, "ap", "Password", session.Password ?? "");
            // Pipe discovery for the DLL — bare name, no \\.\pipe\ prefix.
            // V1 DLLs ignore the key; V2 DLLs switch to the pipe transport.
            SetIniSectionValue(lines, "ap", "PipeName", pipeName);

            // Launcher owns the AP connection + randomization → mod hides its
            // in-game title overlay + F1 AP login panel (read from disk, so it
            // works even if the D2ARCH_LAUNCHER env var doesn't propagate).
            SetIniSectionValue(lines, "launcher", "HideInGameUI", "1");

            File.WriteAllLines(ini, lines);
        }
        catch { /* non-fatal — user can set manually in d2arch.ini */ }
    }

    /// Write <key>=<value> into a specific [section] of an ini file (in-memory).
    /// Adds the section and/or key if they don't exist.
    private static void SetIniSectionValue(
        List<string> lines, string section, string key, string value)
    {
        string header  = $"[{section}]";
        string newLine = $"{key}={value}";

        // Find section header
        int sectionIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
            { sectionIdx = i; break; }
        }

        if (sectionIdx < 0)
        {
            // Section missing — append
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");
            lines.Add(header);
            lines.Add(newLine);
            return;
        }

        // Search within section for existing key
        for (int i = sectionIdx + 1; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith('[')) break; // next section

            int eq = t.IndexOf('=');
            if (eq > 0 && t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            { lines[i] = newLine; return; }
        }

        // Key not found — insert right after the section header
        lines.Insert(sectionIdx + 1, newLine);
    }

    /// Read <key> from [section] of an in-memory ini (mirrors the scan in
    /// SetIniSectionValue). Returns the trimmed value, or null if the section
    /// or key is absent.
    private static string? ReadIniSectionValue(IReadOnlyList<string> lines, string section, string key)
    {
        string header = $"[{section}]";
        int sectionIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
            { sectionIdx = i; break; }
        }
        if (sectionIdx < 0) return null;

        for (int i = sectionIdx + 1; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith('[')) break;            // next section
            int eq = t.IndexOf('=');
            if (eq > 0 && t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return t[(eq + 1)..].Trim();
        }
        return null;
    }

    private string D2IniPath => Path.Combine(GameDirectory, "Archipelago", "d2arch.ini");

    /// Load the current standalone randomizer options from d2arch.ini [settings]
    /// so the dialog opens on whatever was last used (or the mod's defaults when
    /// a key — or the whole file — is missing).
    /// Bundled D2 location id↔name table (Game/Archipelago/d2_locations.json,
    /// extracted from the apworld at build time) as an AP-style data package
    /// ({ "location_name_to_id": { … } }). Lets the launcher's tracker name +
    /// categorise standalone checks the same way an AP DataPackage does. Null if
    /// the file is missing (older install) — the tracker then falls back to #id.
    public JsonElement? GetLocationDataPackage()
    {
        try
        {
            string p = Path.Combine(GameDirectory, "Archipelago", "d2_locations.json");
            if (!File.Exists(p)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private D2RandomizerSettings ReadStandaloneSettings()
    {
        try
        {
            string ini = D2IniPath;
            if (!File.Exists(ini)) return new D2RandomizerSettings();
            var lines = File.ReadAllLines(ini);
            return D2RandomizerSettings.FromIni(key => ReadIniSectionValue(lines, "settings", key));
        }
        catch { return new D2RandomizerSettings(); }
    }

    /// The standalone randomizer options currently on disk (d2arch.ini
    /// [settings]) — exactly what the mod reads this session. The host uses these
    /// + GetLocationDataPackage() to derive the standalone tracker's full active
    /// location universe (D2LocationUniverse), since there's no AP server to
    /// deliver one. Read AFTER LaunchStandaloneAsync, so it reflects the just-
    /// written launch settings.
    public D2RandomizerSettings GetStandaloneSettings() => ReadStandaloneSettings();

    /// Persist the chosen randomizer options to d2arch.ini [settings] and blank
    /// the [ap] section. The mod's LoadAPSettings reads [settings] as its single
    /// source of truth whenever AP is offline, so a standalone (DLL-injected,
    /// no-pipe) launch randomizes exactly per these values. Blanking [ap]
    /// guarantees the DLL never tries to reach a stale pipe/server.
    private void WriteStandaloneSettings(D2RandomizerSettings s, string pipeName)
    {
        string ini = D2IniPath;
        List<string> lines;
        if (File.Exists(ini))
        {
            lines = new List<string>(File.ReadAllLines(ini));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ini) ?? GameDirectory);
            lines = new List<string>();
        }

        foreach (var kv in s.ToIniPairs())
            SetIniSectionValue(lines, "settings", kv.Key, kv.Value);

        // Standalone: write the PipeName so the mod streams CHECK: to the
        // launcher's tracker, but blank server/slot/pass so it never tries to
        // reach an AP server (the launcher never sends STATE:CONNECTED either).
        SetIniSectionValue(lines, "ap", "PipeName", pipeName);
        SetIniSectionValue(lines, "ap", "ServerIP", "");
        SetIniSectionValue(lines, "ap", "SlotName", "");
        SetIniSectionValue(lines, "ap", "Password", "");

        // Launcher owns randomization → mod hides its in-game title overlay +
        // F1 AP login panel (read from disk = reliable regardless of env vars).
        SetIniSectionValue(lines, "launcher", "HideInGameUI", "1");

        File.WriteAllLines(ini, lines);
    }

    // ── Launch via the 32-bit bootstrap (the ONLY reliable injector) ──────────
    //
    // The launcher is a 64-bit process; Game.exe is 32-bit. A 64-bit process
    // CANNOT inject a DLL into a 32-bit target via CreateRemoteThread +
    // LoadLibraryA — the kernel32 LoadLibraryA address differs between the
    // 32- and 64-bit views, so the remote thread runs garbage and the DLL is
    // never loaded (the game boots completely unmodded). This is exactly why
    // D2Arch_Launcher.exe exists: it is built x86, so its injection matches the
    // 32-bit Game.exe. The OLD C# launcher invoked it for precisely this reason
    // (see src/d2arch_bootstrap.c header). It sets DIABLO2_PATCH, launches
    // Game.exe via D2.Detours, injects D2Archipelago.dll (with retry), then
    // exits. We run it and then attach to the running Game.exe.

    /// Run D2Arch_Launcher.exe (kills stale Game.exe, sets DIABLO2_PATCH,
    /// launches + injects), wait for it to finish, then return the live
    /// Game.exe process. <paramref name="apMode"/> sets D2ARCH_AP_MODE so the
    /// DLL keeps ap_settings.dat (AP) instead of scrubbing it (standalone).
    private async Task<Process> RunBootstrapAndFindGameAsync(
        bool apMode, CancellationToken ct, string? saveFolder = null)
    {
        string bootstrap = Path.Combine(GameDirectory, "D2Arch_Launcher.exe");
        if (!File.Exists(bootstrap))
            throw new InvalidOperationException(
                "D2Arch_Launcher.exe is missing from the install folder — re-install the " +
                "game from the Play tab, then try again.");

        // Map tracker — wipe the previous session's per-room collision dumps so a
        // new seed (different layout) never renders on top of stale rooms. The DLL
        // recreates the folder + appends rooms as the player explores.
        try
        {
            string mapDir = Path.Combine(GameDirectory, "Archipelago", "map");
            if (Directory.Exists(mapDir)) Directory.Delete(mapDir, recursive: true);
        }
        catch { /* non-fatal — stale rooms at worst */ }

        // -3dfx selects the Glide renderer, which is what D2GL (glide3x.dll)
        // hooks to provide the HD graphics. Without it the game falls back to
        // DirectDraw and D2GL never activates (no HD). START.bat passes it too.
        string extraArgs = BuildExtraLaunchArgs();
        var psi = new ProcessStartInfo
        {
            FileName         = bootstrap,
            Arguments        = $"-3dfx -direct -txt{extraArgs}",
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
            CreateNoWindow   = true,
        };
        if (apMode) psi.Environment["D2ARCH_AP_MODE"] = "1";
        // The bootstrap sets DIABLO2_PATCH itself; set it here too so the
        // dependency is explicit and survives any future refactor of the exe.
        psi.Environment["DIABLO2_PATCH"] = Path.Combine(GameDirectory, "patch");
        // The launcher now owns randomization (it writes d2arch.ini [settings])
        // and the AP connection (the pipe), so tell the mod to hide its in-game
        // title-screen overlay (the old toggles + Server/Slot/Pass/Connect panel).
        psi.Environment["D2ARCH_LAUNCHER"] = "1";
        // Per-seed character isolation: the bootstrap pins D2's save path here,
        // so D2's character-select only shows characters created under this seed.
        if (!string.IsNullOrEmpty(saveFolder))
            psi.Environment["D2ARCH_SAVE_PATH"] = saveFolder;

        var boot = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the mod loader (D2Arch_Launcher.exe).");

        // The bootstrap exits the moment injection completes (or fails). Its own
        // internal waits cap at ~15s for Game.exe + 3 inject retries, so 60s is
        // a safe ceiling that still surfaces a hung AV scan.
        using (var bootCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            bootCts.CancelAfter(TimeSpan.FromSeconds(60));
            try { await boot.WaitForExitAsync(bootCts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { boot.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException(
                    "The mod loader did not finish within 60 seconds — usually antivirus " +
                    "blocking DLL injection. Add the game folder and the launcher to your " +
                    "antivirus exclusions, then try again.");
            }
        }

        if (boot.ExitCode != 0)
            throw new InvalidOperationException(
                $"The randomizer mod could not be injected into the game (loader exit code {boot.ExitCode}). " +
                "This is almost always antivirus blocking DLL injection — add D2Archipelago.dll, " +
                "Game.exe, D2Arch_Launcher.exe and the launcher to your antivirus exclusions, then try again.");

        // Bootstrap succeeded → Game.exe is running + modded. Attach to it.
        int pid = await FindGamePidByImagePathAsync(ct);
        return Process.GetProcessById(pid);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess, int flags, System.Text.StringBuilder exeName, ref int size);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// Find the Game.exe living under GameDirectory. Uses
    /// QueryFullProcessImageName (works cross-bitness, unlike Process.MainModule
    /// which a 64-bit process can't reliably read on a 32-bit target). Retries
    /// briefly because the bootstrap may exit a beat before the OS finishes
    /// surfacing the process.
    private async Task<int> FindGamePidByImagePathAsync(CancellationToken ct, int timeoutMs = 10_000)
    {
        string root;
        try   { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GameDirectory)); }
        catch { root = GameDirectory; }

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            foreach (var p in Process.GetProcessesByName("Game"))
            {
                IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb  = new System.Text.StringBuilder(1024);
                    int cap = sb.Capacity;
                    if (QueryFullProcessImageNameW(h, 0, sb, ref cap))
                    {
                        string full = Path.GetFullPath(sb.ToString());
                        if (full.StartsWith(root + Path.DirectorySeparatorChar,
                                            StringComparison.OrdinalIgnoreCase))
                            return p.Id;
                    }
                }
                catch { /* process vanished — skip */ }
                finally { CloseHandle(h); }
            }
            await Task.Delay(200, ct);
        }
        throw new InvalidOperationException(
            "The game started but could not be located under the install folder. " +
            "If it is running, close it and try again.");
    }

    // ── Helpers: wait for Game.exe process ───────────────────────────────────

    /// Find the Game.exe spawned by OUR DetoursLauncher. Matching by process
    /// name alone injected into ANY process named "Game" — a leftover Game.exe
    /// from a crashed session (or an unrelated tool) got the DLL while the new
    /// instance ran unmodded (P2-4). Primary filter: the process image must
    /// live under GameDirectory. Fallback when the image path is unreadable
    /// (access denied across privilege/bitness boundaries): only accept a
    /// process that started AFTER this launch began — a stale leftover is
    /// minutes old and never qualifies.
    private async Task<int> WaitForGamePidAsync(CancellationToken ct, int timeoutMs = 15_000)
    {
        // Process.StartTime reports LOCAL time; small slack absorbs clock skew
        // between Process.Start and this call.
        var launchedAfter = DateTime.Now - TimeSpan.FromSeconds(3);

        string expectedRoot;
        try   { expectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GameDirectory)); }
        catch { expectedRoot = GameDirectory; }

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            foreach (var p in Process.GetProcessesByName("Game"))
            {
                try
                {
                    string? exePath = null;
                    try { exePath = p.MainModule?.FileName; }
                    catch (System.ComponentModel.Win32Exception) { /* access denied — use fallback */ }
                    catch (InvalidOperationException)            { continue; /* already exited */ }

                    if (exePath != null)
                    {
                        string fullExe = Path.GetFullPath(exePath);
                        if (fullExe.StartsWith(expectedRoot + Path.DirectorySeparatorChar,
                                               StringComparison.OrdinalIgnoreCase))
                            return p.Id;
                        continue;   // some OTHER "Game" process — never inject into it
                    }

                    // Image path unreadable → start-time fallback.
                    if (p.StartTime >= launchedAfter)
                        return p.Id;
                }
                catch { /* process vanished mid-query — skip it */ }
            }
            await Task.Delay(200, ct);
        }
        throw new TimeoutException(
            "Game.exe did not appear under the install folder within 15 seconds.");
    }

    // ── DLL injection via P/Invoke ────────────────────────────────────────────
    //
    // Runs from inside the main 154MB launcher process — much less suspicious
    // to AV heuristics than a tiny standalone injector binary.
    // AV long-term fix: code-sign the launcher exe with an EV certificate.

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddr,
        uint dwSize, uint flAllocType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBase,
        byte[] buf, uint size, out int written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpAttr,
        uint dwStackSize, IntPtr lpStartAddr, IntPtr lpParam,
        uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hObject, uint dwMs);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    private const uint MEM_COMMIT_RESERVE = 0x3000;
    private const uint PAGE_READWRITE     = 0x04;

    private async Task InjectDllAsync(int pid, string dllPath, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
            if (hProcess == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"OpenProcess failed (err {Marshal.GetLastWin32Error()}). " +
                    "Add the launcher to your antivirus exclusion list.");
            try
            {
                byte[] pathBytes = Encoding.ASCII.GetBytes(dllPath + "\0");
                IntPtr remoteMem = VirtualAllocEx(hProcess, IntPtr.Zero,
                    (uint)pathBytes.Length, MEM_COMMIT_RESERVE, PAGE_READWRITE);

                if (remoteMem == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"VirtualAllocEx failed (err {Marshal.GetLastWin32Error()}).");

                WriteProcessMemory(hProcess, remoteMem, pathBytes,
                    (uint)pathBytes.Length, out _);

                IntPtr kernel32 = GetModuleHandleA("kernel32.dll");
                IntPtr loadLib  = GetProcAddress(kernel32, "LoadLibraryA");

                IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0,
                    loadLib, remoteMem, 0, out _);

                if (hThread == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"CreateRemoteThread failed (err {Marshal.GetLastWin32Error()}). " +
                        "AV is blocking injection — add Game.exe and launcher to exclusions.");

                WaitForSingleObject(hThread, 10_000);
                CloseHandle(hThread);
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }, ct);
    }

    // ── Helpers: download ─────────────────────────────────────────────────────

    private async Task DownloadWithProgressAsync(
        string url, string destPath,
        int startPct, int endPct,
        IProgress<(int, string)> progress,
        CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file   = new FileStream(destPath, FileMode.Create);

        var buf = new byte[81920];
        long bytesRead = 0;
        var  lastReport = DateTime.MinValue;

        while (true)
        {
            int read = await stream.ReadAsync(buf, ct);
            if (read <= 0 || ct.IsCancellationRequested) break;
            await file.WriteAsync(buf.AsMemory(0, read), ct);
            bytesRead += read;

            if ((DateTime.Now - lastReport).TotalMilliseconds > 250)
            {
                lastReport = DateTime.Now;
                double frac = total.HasValue ? (double)bytesRead / total.Value : 0;
                int pct = startPct + (int)(frac * (endPct - startPct));
                string sizeStr = total.HasValue
                    ? $"{bytesRead / 1_048_576}MB / {total.Value / 1_048_576}MB"
                    : $"{bytesRead / 1_048_576}MB";
                progress.Report((pct, "Downloading... " + sizeStr));
            }
        }
    }

    private async Task DownloadSimpleAsync(string url, string destPath, CancellationToken ct)
        => await File.WriteAllBytesAsync(destPath, await _http.GetByteArrayAsync(url, ct), ct);

    /// Exponential backoff with jitter: 0.5s, 1s, 2s, 4s, 8s … capped at 10s.
    private static int BackoffMs(int retry)
        => (int)Math.Min(10000, 500 * Math.Pow(2, retry)) + Random.Shared.Next(0, 400);

    /// Download a (large) file to disk with progress, retrying the whole transfer
    /// on a transient failure. Used for the release ZIP on both fresh install and
    /// update, so a dropped connection mid-download self-corrects instead of
    /// failing the install.
    private async Task DownloadZipWithRetryAsync(
        string url, string destPath, int startPct, int endPct,
        IProgress<(int, string)> progress, CancellationToken ct, int attempts = 4)
    {
        for (int retry = 0; ; retry++)
        {
            try
            {
                await DownloadWithProgressAsync(url, destPath, startPct, endPct, progress, ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch when (retry < attempts - 1)
            {
                progress.Report((startPct,
                    $"Download interrupted — retrying ({retry + 1}/{attempts - 1})..."));
                await Task.Delay(BackoffMs(retry), ct);
            }
        }
    }

    // ── Helpers: file operations ──────────────────────────────────────────────

    private static string ComputeSha256(string filePath)
    {
        using var sha  = SHA256.Create();
        using var file = File.OpenRead(filePath);
        return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLower();
    }

    private void DeleteBinCache()
    {
        string excelDir = Path.Combine(GameDirectory, "data", "global", "excel");
        if (!Directory.Exists(excelDir)) return;
        foreach (string f in Directory.GetFiles(excelDir, "*.bin"))
            try { File.Delete(f); } catch { }
    }

    private int DeleteOrphans(HashSet<string> manifestPaths)
    {
        int deleted = 0;
        string[] modDirs = { "patch", "ap_bridge_dist", "apworld" };
        foreach (string dirName in modDirs)
        {
            string fullDir = Path.Combine(GameDirectory, dirName);
            if (!Directory.Exists(fullDir)) continue;
            foreach (string filePath in Directory.GetFiles(fullDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(GameDirectory, filePath).Replace('\\', '/');
                if (!manifestPaths.Contains(rel))
                    try { File.Delete(filePath); deleted++; } catch { }
            }
        }

        // Top-level mod files only (never blind-scan the game root)
        HashSet<string> knownTopFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "D2Archipelago.dll", "D2Archipelago.exp", "D2Archipelago.lib",
            "D2.DetoursLauncher.exe", "Detours.dll",
        };
        foreach (string fileName in knownTopFiles)
        {
            string full = Path.Combine(GameDirectory, fileName);
            if (File.Exists(full) && !manifestPaths.Contains(fileName))
                try { File.Delete(full); deleted++; } catch { }
        }
        return deleted;
    }

    private void WriteVersionDat(string? version)
    {
        if (string.IsNullOrEmpty(version)) return;
        try
        {
            string dir = Path.Combine(GameDirectory, "Archipelago");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "version.dat"), version.Trim());
            InstalledVersion = version.Trim();
        }
        catch { }
    }

    private static void JsonFindString(string json, string key, ref string? result)
    {
        string marker = "\"" + key + "\"";
        int i = json.IndexOf(marker);
        if (i < 0) return;
        i = json.IndexOf('"', i + marker.Length);
        if (i < 0) return;
        i++;
        int end = json.IndexOf('"', i);
        if (end > i) result = json[i..end];
    }

    // ── Helpers: AP index persistence ────────────────────────────────────────

    // The received-items index is PER-SEED (per AP room), not per-install: every
    // seed starts its own item stream at 0. A single global file meant a returning
    // player who started a new seed inherited the previous seed's index, so the
    // new seed's early items — notably the precollected starting skills at index
    // 0 — were all skipped. Scope the file by the room seed name; fall back to the
    // legacy global name only when no seed is known.
    private string? _apIndexSeedKey;   // seed the in-memory _apItemIndex belongs to

    private string ApIndexPath
    {
        get
        {
            string? seed = GetSeedName?.Invoke();
            string suffix = string.IsNullOrEmpty(seed) ? "" : "_" + StableSeedKey(seed!);
            return Path.Combine(GameDirectory, "Archipelago", $"ap_item_index{suffix}.dat");
        }
    }

    /// Filesystem-safe stable key for a seed name (FNV-1a, 8 hex chars).
    private static string StableSeedKey(string seed)
    {
        uint h = 2166136261u;
        foreach (char c in seed) { h ^= c; h *= 16777619u; }
        return h.ToString("x8");
    }

    /// Reload _apItemIndex from the file for the current seed if the seed changed
    /// since we last loaded. Call before reading/advancing the index.
    private void EnsureApIndexForCurrentSeed()
    {
        string key = GetSeedName?.Invoke() ?? "";
        if (_apIndexSeedKey == key) return;
        _apIndexSeedKey = key;
        LoadApIndex();
    }

    private void PersistApIndex()
    {
        try { File.WriteAllText(ApIndexPath, _apItemIndex.ToString()); } catch { }
    }

    private void LoadApIndex()
    {
        // Missing file (fresh install / directory changed / new seed) resets to 0
        // so a stale index can never carry over.
        try
        {
            _apItemIndex = File.Exists(ApIndexPath)
                ? int.Parse(File.ReadAllText(ApIndexPath).Trim())
                : 0;
        }
        catch { _apItemIndex = 0; }
    }
}
