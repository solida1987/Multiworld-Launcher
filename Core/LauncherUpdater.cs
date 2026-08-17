using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// LauncherUpdater — checks for and applies launcher self-updates.

// FLOW
// 1. CheckAsync() — download launcher_version.txt → parse available version
// If available > current → fire UpdateAvailable event.
// 2. DownloadAndApplyAsync() — download launcher_package.zip → verify SHA256
// → extract to temp dir → write _self_update.bat → Process.Start bat → exit.

// The batch script is: wait 2s, copy new exe, restart launcher, delete itself.

// VERSION FILE FORMAT (launcher_version.txt):
// 2.1.0
// <SHA256 hex of launcher_package.zip>

// CURRENT VERSION: read from Assembly at runtime.

public sealed class LauncherUpdater
{
    // --- Step log ---

    // Every stage logs one line — update failures must be diagnosable from a report.
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "multiworld_launcher_update.log");

    private static void Step(string message)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never break the update */ }
    }

    // Public so the caller can record why it gave up.
    public static void LogStep(string message) => Step(message);

    // --- Configuration ---

    // URL of the remote launcher_version.txt (two lines: version + sha256).
    // Lives at the root of the launcher dev repo — bumped as part of each release.
    public string VersionFileUrl { get; set; } =
        "https://raw.githubusercontent.com/solida1987/Multiworld-Launcher/main/launcher_version.txt";

    // URL of the launcher ZIP package to download.
    // Served as a GitHub release asset ("latest" redirect) — raw.githubusercontent
    // has a 100MB cap, release assets don't, and the self-contained zip is large.
    public string PackageUrl { get; set; } =
        "https://github.com/solida1987/Multiworld-Launcher/releases/latest/download/launcher_package.zip";

    // --- Events ---

    // Fires on the calling thread when a newer version is found.
    public event Action<string>? UpdateAvailable;

    // Fires during download with 0–100 progress value.
    public event Action<int>? DownloadProgress;

    // --- Public API ---

    // Current launcher version (from assembly metadata).
    public static Version CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
        ?? new Version(2, 0, 0);

    // Latest version found by the last successful CheckAsync call.
    public Version? LatestVersion { get; private set; }

    // SHA256 hex of the latest package (set by CheckAsync).
    public string? LatestSha256 { get; private set; }

    // --- Check for update ---

    // Check whether a newer launcher version is available.
    // Returns true if an update was found (UpdateAvailable was also fired).
    // Silent on any network or parse error — never throws.
    public async Task<bool> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ArchipelagoLauncher/2");

            string text = await http.GetStringAsync(VersionFileUrl, ct);
            string[] lines = text.Trim().Split('\n');
            if (lines.Length < 2) return false;

            if (!Version.TryParse(lines[0].Trim(), out var remote)) return false;
            string sha = lines[1].Trim();

            LatestVersion = remote;
            LatestSha256  = sha;

            if (remote > CurrentVersion)
            {
                UpdateAvailable?.Invoke(remote.ToString());
                return true;
            }
        }
        catch { /* non-fatal — no network, bad format, etc. */ }

        return false;
    }

    // --- Download and apply ---

    // Download the new launcher package, verify SHA256, write the update script,
    // start the script and exit the application.
    // Calls progress 0→100 during download.
    public async Task DownloadAndApplyAsync(CancellationToken ct = default)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "archipelago_launcher_update");
        Directory.CreateDirectory(tempDir);

        string zipPath = Path.Combine(tempDir, "launcher_package.zip");

        // --- Download ---
        Step($"update {CurrentVersion} -> {LatestVersion}: starting download");

        // HttpClient.Timeout covers getting the response headers, not
        // reading the body, so a connection that stalls mid-download waits
        // forever and the splash sits on a frozen percentage with nothing
        // to cancel it. Give each read its own deadline instead.
        using (var http = new HttpClient())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ArchipelagoLauncher/2");
            using var response = await http.GetAsync(PackageUrl,
                HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            await using var src  = await response.Content.ReadAsStreamAsync(ct);
            await using var dest = File.Create(zipPath);

            byte[] buf      = new byte[81920];
            long   received = 0;
            int    read;
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                try { read = await src.ReadAsync(buf, stall.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Step($"download STALLED after {received:N0} of {total} bytes");
                    throw new IOException(
                        "The download stopped responding. Check your connection "
                        + "and try the update again.");
                }
                if (read <= 0) break;

                await dest.WriteAsync(buf.AsMemory(0, read), ct);
                received += read;
                if (total.HasValue && total.Value > 0)
                    DownloadProgress?.Invoke((int)(received * 100 / total.Value));
            }

            DownloadProgress?.Invoke(100);
            Step($"download complete: {received:N0} bytes");
        }

        // --- SHA256 verify ---
        // A package without a known checksum is never applied — silently
        // skipping verification would let a truncated/tampered download
        // replace the launcher executable.
        if (string.IsNullOrEmpty(LatestSha256))
            throw new InvalidOperationException(
                "No update checksum is available — run the update check again, "
                + "then retry the install.");
        string actualHash = ComputeSha256(zipPath);
        Step("sha256 verified");
        if (!actualHash.Equals(LatestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"SHA256 mismatch — download may be corrupt.\n" +
                $"  Expected: {LatestSha256}\n" +
                $"  Actual:   {actualHash}");

        // --- Extract ---
        string extractDir = Path.Combine(tempDir, "extracted");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        ZipFile.ExtractToDirectory(zipPath, extractDir);
        Step("archive extracted");

        string currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                            ?? Path.Combine(AppContext.BaseDirectory, "Multiworld Launcher.exe");

        // Pick the launcher exe by its expected name — GetFiles order is
        // undefined, so "first exe in the zip" would break the day the
        // package ever ships a second executable.
        string[] exes = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories);
        if (exes.Length == 0)
            throw new InvalidOperationException("No executable found in launcher_package.zip.");

        string expectedName = Path.GetFileName(currentExe);
        string newExe = exes.FirstOrDefault(p => string.Equals(
                            Path.GetFileName(p), expectedName,
                            StringComparison.OrdinalIgnoreCase))
                        ?? exes[0];

        // --- Write updater batch script ---
        string newRoot = Path.GetDirectoryName(newExe)!;
        string curRoot = Path.GetDirectoryName(currentExe)!;

        string batPath = Path.Combine(tempDir, "_self_update.bat");
        string batContent = BuildUpdateScript(currentExe, newExe, newRoot, curRoot, batPath);
        await File.WriteAllTextAsync(batPath, batContent, Encoding.ASCII, ct);

        // --- Launch script and exit ---
        Step($"launching update script: {batPath}");
        try
        {
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = batPath,
                UseShellExecute = true,
                WindowStyle     = System.Diagnostics.ProcessWindowStyle.Minimized,
            });
            Step(proc == null
                ? "update script did NOT start (Process.Start returned null)"
                : $"update script started, pid {proc.Id}");
        }
        catch (Exception ex)
        {
            // Do not shut down over a script that never started -- that
            // would close the launcher and leave the user on the old
            // version with no window and no explanation.
            Step($"update script FAILED to start: {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        // Exit the application — the batch script will restart it.
        // Shutdown() must run on the dispatcher thread; this method may be
        // awaited on a background continuation, so marshal explicitly.
        var app = System.Windows.Application.Current;
        if (app != null)
            app.Dispatcher.Invoke(() => app.Shutdown());
    }

    // --- Private helpers ---

    private static string BuildUpdateScript(string currentExe, string newExe,
                                            string newRoot, string curRoot, string batPath)
    {
        // The updater waits for THIS pid to exit before copying — a fixed sleep
        // relaunched the old exe when shutdown took longer.
        string cur     = EscapeBatchPath(currentExe);
        string src     = EscapeBatchPath(newExe);
        string bat     = EscapeBatchPath(batPath);
        string newDir  = EscapeBatchPath(newRoot);
        string curDir  = EscapeBatchPath(curRoot);
        string exeName = EscapeBatchPath(Path.GetFileName(currentExe));

        // Gate the whole update on the exe copy, because that is the one file
        // held open until the old process exits — its success is the signal
        // that the app has released its files.
        // brings every other packaged file (assets, data, native dlls) up to
        // date.
        
        // /E (copy new+changed, recurse) — NOT /MIR.
        // holds the user's own data: launcher_settings.json, Games\, installed
        // game saves. /MIR would delete everything not in the fresh package,
        // i.e. wipe the user's settings and installed games. /E only adds and
        // overwrites, never deletes — a stale leftover file is harmless, a
        // deleted save is not.
        
        // robocopy exit codes 0-7 are success; 8+ is a real error.
        // the running exe (already copied) and the script itself.
        return
            "@echo off\r\n" +
            "rem Multiworld Launcher self-update\r\n" +
            "set tries=0\r\n" +
            ":copyloop\r\n" +
            "timeout /t 1 /nobreak >nul\r\n" +
            $"copy /y \"{src}\" \"{cur}\" >nul 2>&1\r\n" +
            "if not errorlevel 1 goto copied\r\n" +
            "set /a tries+=1\r\n" +
            "if %tries% lss 60 goto copyloop\r\n" +
            "echo Self-update failed: launcher exe stayed locked for 60 seconds.>\"%TEMP%\\multiworld_launcher_update_failed.log\"\r\n" +
            $"del \"{bat}\"\r\n" +
            "exit /b 1\r\n" +
            ":copied\r\n" +
            $"robocopy \"{newDir}\" \"{curDir}\" /E /NFL /NDL /NJH /NJS /NP /R:2 /W:1 /XF \"{exeName}\" _self_update.bat >nul 2>&1\r\n" +
            "if errorlevel 8 echo Self-update: robocopy reported errors syncing assets.>\"%TEMP%\\multiworld_launcher_update_assets.log\"\r\n" +
            $"start \"\" \"{cur}\"\r\n" +
            $"del \"{bat}\"\r\n";
    }

    // Double every literal % so cmd.exe cannot expand path fragments such as
    // "%USERPROFILE%" — expansion happens even inside double quotes.
    private static string EscapeBatchPath(string path) => path.Replace("%", "%%");

    private static string ComputeSha256(string filePath)
    {
        using var sha  = SHA256.Create();
        using var file = File.OpenRead(filePath);
        byte[] hash    = sha.ComputeHash(file);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
