using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;
using LauncherV2.UI.Pages;
// (WPF/WinForms disambiguation handled in GlobalUsings.cs)

namespace LauncherV2;

public partial class App : Application
{
    /// <summary>
    /// Why a plugin did not load, if any did not. Read once by the main
    /// window when its log exists, then cleared.
    /// </summary>
    public static IReadOnlyList<string> PluginLoadProblems { get; private set; }
        = Array.Empty<string>();

    /// <summary>Called by the main window once it has reported them.</summary>
    public static void ClearPluginLoadProblems() => PluginLoadProblems = Array.Empty<string>();

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

        // ── Emulator bridges ────────────────────────────────────────────────
        //
        // MUST run before EnsureEmulatorFolders, and before any game can start.
        // The launcher knows no emulator of its own any more: BizHawk, SNI and
        // everything else arrive as extensions, and nothing can launch until
        // this has read them. An empty registry means every game reports "no
        // bridge extension is installed" — correct, but only if we actually
        // looked.
        foreach (string problem in LauncherV2.Core.Extensions.BridgeRegistry.LoadInstalled())
            System.Diagnostics.Trace.WriteLine("[extension] " + problem);

        // Emulators/<backend>/ with a note in each, so a player who wants an
        // emulated game has somewhere obvious to put their own copy. Covers the
        // emulators the installed extensions ask for, which is why it runs
        // after the registry is loaded. The launcher never downloads one.
        LauncherV2.Plugins.Emulated.EmulatorPlugin.EnsureEmulatorFolders();

        // ── Game plugins ────────────────────────────────────────────────────
        //
        // The launcher registers nothing. Every game arrives as a
        // .londonplugin the player downloaded and added themselves — including
        // the ones written by the same person who wrote this launcher.
        //
        // Diablo II used to be registered here, in two channels. It is now
        // built from Diablo-London-Plugin/ and shipped separately; see
        // PLUGIN_API.md.










        // ── Game plugins from disk ───────────────────────────────────────────
        // AFTER the built-in games on purpose: Register refuses a duplicate
        // GameId, so registering plugins last means a plugin can never take
        // over Diablo II's id by loading first.
        //
        // Only plugins the player approved, and whose files still hash to what
        // they approved, come back from here. Everything else arrives as a
        // problem line — a game that silently vanishes is a support request, a
        // game that says why is not.
        // Finish what the last session could not: files an uninstall left
        // behind because the process still held them. Must run BEFORE the
        // plugins load and lock their files again.
        LauncherV2.Core.Plugins.PendingDeletes.Run();

        GameRegistry.LoadFromDisk(out var pluginProblems);

        // Held for the main window, which owns the log. Debug.WriteLine was
        // all this did before -- and Debug.WriteLine does not exist in a
        // Release build, so a plugin that failed to load told the player
        // nothing at all: no error, no log line, just a game missing from the
        // sidebar. The comment on LoadFromDisk already said it should be
        // surfaced; it simply never was.
        PluginLoadProblems = pluginProblems;

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

