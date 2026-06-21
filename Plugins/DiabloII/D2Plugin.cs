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

    /// Validate that a folder looks like a Diablo II installation.
    /// Returns null when valid, or a human-readable reason when not.
    public string? ValidateExistingInstall(string folder)
    {
        foreach (string f in new[] { "d2data.mpq", "d2char.mpq" })
            if (File.Exists(Path.Combine(folder, f))) return null;
        return "That folder doesn't appear to contain Diablo II game data " +
               "(expected d2data.mpq / d2char.mpq). " +
               "Select the folder where Diablo II: Lord of Destruction is installed — " +
               "typically C:\\Program Files (x86)\\Diablo II or similar.";
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
            // Update: manifest-based incremental file sync.
            await InstallFromManifestAsync(manifestUrl, apworldUrl, tag, progress, ct);
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
            await DownloadWithProgressAsync(packageUrl, tempZip, 5, 80, progress, ct);

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
        string manifestUrl, string? apworldUrl, string? actualTagHint,
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

        string baseUrl = $"https://raw.githubusercontent.com/{GITHUB_OWNER}/{GITHUB_REPO}/main/game/";
        int downloaded = 0, failed = 0;

        progress.Report((30, $"Downloading {toDownload.Count} files..."));

        foreach (var (path, sha, _) in toDownload)
        {
            // A cancelled update must throw — falling through used to stamp
            // version.dat over a half-updated install (P2-3).
            ct.ThrowIfCancellationRequested();

            string url   = baseUrl + path;
            string local = Path.Combine(GameDirectory, path.Replace('/', '\\'));
            string? dir  = Path.GetDirectoryName(local);
            if (dir != null) Directory.CreateDirectory(dir);

            int pct = 30 + downloaded * 65 / toDownload.Count;
            progress.Report((pct,
                $"Downloading {Path.GetFileName(path)} ({downloaded + 1}/{toDownload.Count})"));

            bool ok = false;
            for (int retry = 0; retry < 3 && !ok; retry++)
            {
                try
                {
                    await DownloadSimpleAsync(url, local, ct);
                    if (sha.Length > 0)
                    {
                        ok = ComputeSha256(local).Equals(sha, StringComparison.OrdinalIgnoreCase);
                        if (!ok) try { File.Delete(local); } catch { }
                    }
                    else ok = true;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* network hiccup — retry */ }
            }

            // Count EVERY exhausted file — SHA mismatches included. They used
            // to vanish from the count, so corrupted downloads still reported
            // success and ran orphan cleanup (P2-3).
            if (ok) downloaded++;
            else    failed++;
        }

        DeleteBinCache();

        // Remove orphan files (present locally but removed from manifest)
        int orphans = 0;
        if (failed == 0) orphans = DeleteOrphans(manifestPaths);

        if (apworldUrl != null)
        {
            progress.Report((98, "Downloading AP World..."));
            try
            {
                string apworldDir = Path.Combine(GameDirectory, "apworld");
                Directory.CreateDirectory(apworldDir);
                await DownloadSimpleAsync(apworldUrl,
                    Path.Combine(apworldDir, "diablo2_archipelago.apworld"), ct);
            }
            catch { /* non-fatal — apworld update failure doesn't block the game */ }
        }

        // Failures are an install FAILURE, not a success footnote: without the
        // new version stamp the next check still says "update available", so
        // a retry can finish the job (stamping here used to mark stale files
        // as up to date forever — P2-3).
        if (failed > 0)
            throw new InvalidOperationException(
                $"{failed} of {toDownload.Count} file(s) failed to download " +
                $"({downloaded} succeeded). The install may be incomplete — " +
                "check your connection and try again.");

        WriteVersionDat(manifestVersion);

        string msg = $"Updated {downloaded} files.";
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

        // 3. Start Game.exe via D2.DetoursLauncher
        string detours   = Path.Combine(GameDirectory, "D2.DetoursLauncher.exe");
        string gameExe   = Path.Combine(GameDirectory, "Game.exe");
        string extraArgs = BuildExtraLaunchArgs();
        var launchPsi = new ProcessStartInfo
        {
            FileName         = detours,
            Arguments        = $"\"{gameExe}\" -- -direct -txt{extraArgs}",
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        };
        // The DLL scrubs ap_settings.dat at startup unless this is set
        // (d2arch_main.c) — without it our slot-data hand-off gets deleted
        // the moment the game boots. Inherited by Game.exe via DetoursLauncher.
        launchPsi.Environment["D2ARCH_AP_MODE"] = "1";
        var proc = Process.Start(launchPsi)
            ?? throw new InvalidOperationException("Failed to start Game.exe.");
        _gameProcess = proc;

        // Exit monitoring is wired BEFORE the bridge handshake so a process
        // that crashes during boot aborts the wait below instead of leaving
        // the launch hanging forever. The handler captures the local `proc` —
        // never the field, which a relaunch may have replaced by exit time.
        int launchCompleted = 0;   // 1 once the pipe handshake succeeded
        int exitRaised      = 0;   // GameExited must fire at most once
        var deathCts        = new CancellationTokenSource();

        void RaiseExited()
        {
            if (Interlocked.Exchange(ref exitRaised, 1) == 1) return;
            IsRunning = false;
            ScrubApPassword();   // session over — blank the ini password (P3-20)
            int code = 0;
            try { code = proc.ExitCode; } catch { /* handle already gone */ }
            GameExited?.Invoke(code);
        }

        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            try { deathCts.Cancel(); } catch (ObjectDisposedException) { }
            if (Volatile.Read(ref launchCompleted) == 1)
                RaiseExited();
        };

        try
        {
            // 4+5. PID wait → DLL injection → pipe handshake, all bounded by
            // one linked token: caller cancel (Stop/cleanup) + plugin teardown
            // + process death + a 30 s overall timeout. Without the timeout a
            // DLL that never opens the pipe (AV blocked the injection, or a
            // V1 file-bridge DLL is deployed) would hang the launch forever.
            using var launchCts = CancellationTokenSource.CreateLinkedTokenSource(
                ct, _pipeCts.Token, deathCts.Token);
            launchCts.CancelAfter(TimeSpan.FromSeconds(30));

            string dllPath = Path.Combine(GameDirectory, "D2Archipelago.dll");
            int    gamePid = await WaitForGamePidAsync(launchCts.Token);
            await InjectDllAsync(gamePid, dllPath, launchCts.Token);
            await pipe.WaitForConnectionAsync(launchCts.Token);
        }
        catch (Exception ex)
        {
            // The launch is dead either way — never leave a half-modded
            // Game.exe running behind a failed bridge.
            try { proc.Kill(entireProcessTree: true); } catch { }
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
                    "The game started but the mod never connected (waited 30 seconds). " +
                    "This usually means antivirus blocked the mod — add D2Archipelago.dll " +
                    "and the launcher to your antivirus exclusions, then try again.");
            }
            throw;   // PID-wait / injection errors already carry actionable text
        }
        finally
        {
            deathCts.Dispose();
        }

        IsRunning = true;
        Volatile.Write(ref launchCompleted, 1);

        // Closes the race where the process died between the pipe handshake
        // and the flag write above (the Exited handler saw launchCompleted=0).
        if (proc.HasExited) RaiseExited();

        // Push the current AP state immediately: if the AP server connected
        // before the game was up, the DLL would otherwise never receive a
        // STATE: message (OnApStateChanged only fires on transitions).
        if (_lastApState == ApConnectionState.Connected)
            await WritePipeLineAsync("STATE:CONNECTED", ct);

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

    // ── Standalone launch (no AP connection) ──────────────────────────────────

    public bool SupportsStandalone => true;

    /// Launch Game.exe directly without DLL injection or an AP connection.
    /// Lets the user play Diablo II without an Archipelago session.
    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        if (!IsInstalled)
            throw new InvalidOperationException("Diablo II: Lord of Destruction is not installed.");

        string gameExe = Path.Combine(GameDirectory, "Game.exe");
        if (!File.Exists(gameExe))
            throw new InvalidOperationException(
                $"Game.exe not found at:\n{gameExe}\n\nVerify your install directory in Settings.");

        // Pin save path so D2 1.10f saves to our folder, not wherever the stale registry points.
        PinSavePathRegistry();

        string extraArgs = BuildExtraLaunchArgs();
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = gameExe,
            Arguments        = $"-direct -txt{extraArgs}",
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        });

        if (proc == null)
            throw new InvalidOperationException(
                "Process.Start returned null — check that Game.exe is not blocked by antivirus.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        // Capture the local `proc` — reading the field here would throw if a
        // relaunch replaced it before this (older) process exited.
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            int code = 0;
            try { code = proc.ExitCode; } catch { /* handle already gone */ }
            GameExited?.Invoke(code);
        };

        return Task.CompletedTask;
    }

    // ── AP bridge: items in, checks out ──────────────────────────────────────

    /// Write one protocol line ("ITEM:123", "STATE:CONNECTED", ...) to the
    /// pipe with the '\n' terminator the DLL frames on. All writers go
    /// through here so messages never interleave mid-line. Best-effort:
    /// a broken pipe (game exited) is swallowed — the read loop handles
    /// the disconnect.
    private async Task WritePipeLineAsync(string line, CancellationToken ct = default)
    {
        var pipe = _pipe;
        if (pipe?.IsConnected != true) return;

        byte[] msg = Encoding.UTF8.GetBytes(line + "\n");
        await _pipeWriteLock.WaitAsync(ct);
        try
        {
            await pipe.WriteAsync(msg, ct);
            await pipe.FlushAsync(ct);
        }
        catch (IOException)              { /* pipe broken — game exited */ }
        catch (ObjectDisposedException)  { /* pipe torn down */ }
        catch (InvalidOperationException){ /* pipe no longer connected */ }
        finally
        {
            _pipeWriteLock.Release();
        }
    }

    public async Task ReceiveItemsAsync(ApNetworkItem[] items, int index,
        CancellationToken ct = default)
    {
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
        foreach (var item in items.Skip(skip))
        {
            if (ResolvePlayerName != null)
            {
                string sender = SanitizePipeField(
                    ResolvePlayerName(item.Player) ?? $"Player {item.Player}");
                await WritePipeLineAsync(
                    $"ITEM:{item.ItemId}|{sender}|{item.LocationId}", ct);
            }
            else
            {
                await WritePipeLineAsync($"ITEM:{item.ItemId}", ct);
            }
        }

        _apItemIndex = Math.Max(_apItemIndex, index + items.Length);
        PersistApIndex();
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
            if (ids.Count > 0) LocationsChecked?.Invoke(ids.ToArray());
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

    // ── Settings UI ──────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted  = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg     = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var panel  = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Section: Install directory ─────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "INSTALL DIRECTORY", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text         = "The folder where Diablo II: Lord of Destruction is installed.",
            FontSize     = 11, Foreground = muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow   = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var dirBox   = new TextBox
        {
            Text        = GameDirectory,
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
            // Real folder picker (P3-16) — .NET 8 WPF ships OpenFolderDialog,
            // replacing the old "OpenFileDialog with FileName='Select Folder'"
            // hack (which confused users into typing a file name).
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select the Diablo II: Lord of Destruction install folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
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
        ls.DiabloIIPath = GameDirectory;
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
    public string[] GameBadges       => new[] { "Requires D2: LoD" };

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

    private string ApIndexPath
        => Path.Combine(GameDirectory, "Archipelago", "ap_item_index.dat");

    private void PersistApIndex()
    {
        try { File.WriteAllText(ApIndexPath, _apItemIndex.ToString()); } catch { }
    }

    private void LoadApIndex()
    {
        // Missing file (fresh install / directory changed) resets to 0 so a
        // stale index from a previous GameDirectory can never carry over.
        try
        {
            _apItemIndex = File.Exists(ApIndexPath)
                ? int.Parse(File.ReadAllText(ApIndexPath).Trim())
                : 0;
        }
        catch { _apItemIndex = 0; }
    }
}
