using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;
using LauncherV2.Plugins.DiabloII;
using LauncherV2.UI.Pages;
// (WPF/WinForms disambiguation handled in GlobalUsings.cs)

namespace LauncherV2;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Global crash capture ─────────────────────────────────────────────
        // Three nets: UI-thread exceptions (recoverable — handled + logged),
        // non-UI thread exceptions (process is dying — log before it goes),
        // and unobserved Task faults (logged, marked observed so they don't
        // escalate). Everything lands in crash.log next to the exe.
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("UI thread", args.Exception);
            try
            {
                System.Windows.Clipboard.SetText(args.Exception.ToString());
            }
            catch { /* clipboard can be locked by another process */ }
            MessageBox.Show(
                "An unexpected error occurred.\n\n" +
                $"{args.Exception.GetType().Name}: {args.Exception.Message}\n\n" +
                "Details were written to crash.log next to the launcher and " +
                "copied to your clipboard — please paste them in the Discord " +
                "bug-reports channel.",
                "Multiworld Launcher — Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;   // keep the launcher alive when possible
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                WriteCrashLog("background thread (fatal)", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("unobserved task", args.Exception);
            args.SetObserved();
        };

        var settings = SettingsStore.Load();

        // Remove everything the old multi-game catalog left on this machine —
        // installed games, fetched apworlds, cached art. One-shot per version;
        // details and scope in Core/ContentCleanup.cs.
        ContentCleanup.RunOnce();

        // ── Register game plugins ────────────────────────────────────────────
        // This launcher integrates Diablo II only.

        GameRegistry.Register(new D2Plugin
        {
            // The mod installs into Games/diablo2_archipelago next to the launcher
            // — never the user's own Diablo II. DiabloIIPath now records where the
            // player's ORIGINAL Diablo II lives, used only to copy the MPQ data in.
            GameDirectory       = SettingsStore.DefaultGamePath("diablo2_archipelago"),
            OriginalD2Directory = settings.DiabloIIPath,
        });

        // 2.8.2 — EXPERIMENTAL build as a SEPARATE installable entry. Distinct
        // GameId → its own folder (Games/diablo2_archipelago_experimental) and its
        // own GitHub repo (Diablo-II-Archipelago-experimental). Fully isolated from
        // the stable install above; for aggressive testing only.
        GameRegistry.Register(new D2Plugin
        {
            Experimental        = true,
            GameDirectory       = SettingsStore.DefaultGamePath("diablo2_archipelago_experimental"),
            OriginalD2Directory = settings.DiabloIIPath,
        });










        // ── Splash screen ────────────────────────────────────────────────────
        // Show splash, then reveal the main window after a short minimum delay.
        var splash = new SplashWindow();
        splash.Show();

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;

        // Hide main window initially so it doesn't flash under the splash.
        mainWindow.Visibility = Visibility.Hidden;

        // Show for at least 700 ms, then cross-fade into the main window.
        _ = ShowMainWindowAsync(mainWindow, splash);
    }

    private static async Task ShowMainWindowAsync(MainWindow mainWindow, SplashWindow splash)
    {
        // Run the launcher self-update check during the splash dwell time.
        // If a new version is available we download and apply it right here —
        // the user sees "Updating… N%" on the splash, then the batch restarts
        // the launcher automatically. The main window never opens.
        var updater = new LauncherUpdater();
        bool updateApplied = false;
        try
        {
            bool updateFound = await updater.CheckAsync();
            if (updateFound)
            {
                splash.SetUpdateStatus($"Updating to v{updater.LatestVersion}…");
                updater.DownloadProgress += pct =>
                    splash.SetUpdateStatus($"Updating to v{updater.LatestVersion}…  {pct}%");
                await updater.DownloadAndApplyAsync();
                updateApplied = true;   // App.Shutdown() called inside DownloadAndApplyAsync
            }
        }
        catch (Exception ex)
        {
            // Fall through and open the main window on the current version --
            // a failed update must never stop the launcher from starting.
            // But say so somewhere: this used to be a bare catch that
            // discarded the reason, so an update that quietly did nothing
            // was indistinguishable from one that was never offered.
            Core.LauncherUpdater.LogStep(
                $"update abandoned: {ex.GetType().Name}: {ex.Message}");
            splash.SetUpdateStatus("");
        }

        if (updateApplied) return;   // batch script will restart us; nothing else to do

        // Enforce a minimum 700 ms dwell so the splash doesn't flash for fast startups.
        await Task.Delay(700);
        await splash.FadeOutAsync();   // 300 ms fade-out
        mainWindow.Visibility = Visibility.Visible;
        mainWindow.Activate();
    }

    // ── Crash log ────────────────────────────────────────────────────────────

    private static readonly object _crashLogLock = new();

    /// <summary>
    /// Append an exception to crash.log next to the exe. Never throws.
    /// Rotates through crash_0.log / crash_1.log / crash_2.log at 1 MB each,
    /// keeping up to 3 MB of crash history instead of discarding on overflow.
    /// </summary>
    internal static void WriteCrashLog(string source, Exception ex)
    {
        try
        {
            string dir  = AppContext.BaseDirectory;
            string path = Path.Combine(dir, "crash.log");
            lock (_crashLogLock)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 1_048_576)
                {
                    // Rotate: crash_1.log → crash_2.log, crash_0.log → crash_1.log,
                    // crash.log → crash_0.log. Silently drop crash_2 if it exists.
                    string slot2 = Path.Combine(dir, "crash_2.log");
                    string slot1 = Path.Combine(dir, "crash_1.log");
                    string slot0 = Path.Combine(dir, "crash_0.log");
                    if (File.Exists(slot2)) File.Delete(slot2);
                    if (File.Exists(slot1)) File.Move(slot1, slot2);
                    if (File.Exists(slot0)) File.Move(slot0, slot1);
                    File.Move(path, slot0);
                }
                File.AppendAllText(path,
                    $"════════ {DateTime.Now:yyyy-MM-dd HH:mm:ss} · " +
                    $"v{LauncherUpdater.CurrentVersion} · {source} ════════\r\n" +
                    $"{ex}\r\n\r\n");
            }
        }
        catch { /* a crash logger must never crash */ }
    }
}

