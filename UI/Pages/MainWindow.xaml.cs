using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LauncherV2.Core;
using LauncherV2.Core.AchievementSystem;
using LauncherV2.UI.Controls;

// (WPF/WinForms disambiguation handled in GlobalUsings.cs)

namespace LauncherV2.UI.Pages;

public partial class MainWindow : Window
{
    // ── Session state ─────────────────────────────────────────────────────────
    private IGamePlugin?              _selectedPlugin;
    private ApClient?                 _apClient;
    private CancellationTokenSource?  _playCts;

    // ── Item tracker ──────────────────────────────────────────────────────────
    private readonly ApItemTracker                        _tracker     = new();
    private readonly ObservableCollection<TrackedItem>   _trackerView = new();

    // ── Location tracker (AP check progress) ─────────────────────────────────
    private readonly LocationTracker    _locationTracker = new();
    private readonly List<HintEntry>    _hints           = new();
    /// Deduplication guard: key = ReceiverName\0ItemName\0LocationName\0SenderName
    private readonly HashSet<string>    _hintKeys        = new();
    private string                      _locCategory     = ""; // currently expanded location category
    private readonly Dictionary<string, int> _locPage   = new(); // current page per expanded category
    private const int LocPageSize = LauncherV2.Core.LauncherConstants.LocPageSize;
    private string                      _hintsFilter     = "all"; // "all" | "for_me" | "in_my_world"

    // ── Browse / catalog ──────────────────────────────────────────────────────
    private IReadOnlyList<CatalogEntry>? _catalogEntries;
    private IReadOnlyList<CatalogTool>?  _catalogTools;
    private string _catalogMainFilter     = "all";  // "all" | "available" | "official" | "unofficial"
    private string _catalogPlatformFilter = "all";  // "all" or a normalized platform name
    private string _catalogCategoryFilter = "all";  // "all" or a category name
    /// Platforms below this many games get no chip — they stay reachable via text search.
    private const int PlatformChipMinGames = LauncherV2.Core.LauncherConstants.PlatformChipMinGames;

    // ── Sidebar card references (for active-state highlighting) ───────────────
    private readonly Dictionary<string, Border> _gameCards = new();

    // ── Slot→game suggestion (shown after AP connects if game differs) ─────────
    private IGamePlugin? _suggestedPlugin;

    // ── Active AP session info (set by global connect, used by LaunchGameAsync) ─
    private ApSession?   _currentSession;

    // ── THE plugin of the live AP game session (P2-6): set when LaunchGameAsync
    //    wires it, cleared only once its game stops running. Session accounting,
    //    DeathLink forwarding and the D2 slot-data refresh bind to THIS — never
    //    to _selectedPlugin, which merely tracks what the sidebar displays
    //    (browsing another game mid-session used to corrupt all three).
    //    Handler lifetime: plugins are app-lifetime singletons, so the named
    //    AP-bridge handlers MUST be unsubscribed when the game session ends —
    //    stacked lambdas would fire once per past launch. They stay wired
    //    across a mid-game AP teardown (manual sidebar disconnect): the game
    //    keeps running, and its checks must keep landing in the replay buffer
    //    below (P2-8). ─────────────────────────────────────────────────────────
    private IGamePlugin? _runningPlugin;

    // ── Check replay buffer (P2-8): every location ID the game reports this
    //    session. ApClient keeps its own replay set, but the reconnect loop
    //    builds a NEW client — checks fired while _apClient was null (or while
    //    the old socket was dying) would vanish with the old instance. Flushed
    //    to the server on every successful (re)connect; the server ignores
    //    duplicates. Written on plugin pipe threads, read on connect threads. ──
    private readonly HashSet<long> _pendingChecks     = new();
    private readonly object        _pendingChecksLock = new();

    // ── Standalone (no-AP) session: exit watcher + playtime accounting ────────
    private IGamePlugin?   _standalonePlugin;   // null = no standalone session
    private DateTimeOffset _standaloneStart;

    // ── Achievements ──────────────────────────────────────────────────────────
    private DateTimeOffset _sessionStart;
    private bool           _goalReachedThisSession;
    private CancellationTokenSource?  _newsCts;

    // ── Launcher self-updater ─────────────────────────────────────────────────
    private readonly LauncherUpdater    _launcherUpdater = new();

    // ── Sidebar drag-to-reorder state ─────────────────────────────────────────
    private System.Windows.Point        _dragStartPoint;
    // Ring buffer of up to 10 order snapshots for Ctrl+Z undo (UX-7).
    private const int MaxOrderHistory = 10;
    private readonly List<IReadOnlyList<string>> _orderHistory = new();

    // ── System tray icon (shown while a game is running) ──────────────────────
    private readonly SystemTrayManager  _trayIcon = new();

    // ── Current tab ──────────────────────────────────────────────────────────
    private enum PageTab { Overview, Play, Progression, Tracker, Map, News, Roms, Settings }
    private PageTab _currentTab = PageTab.Overview;

    // ── Download ETA (rolling window over install progress reports) ──────────
    private readonly Queue<(DateTime Time, int Pct)> _progressSamples = new();
    private DateTime _etaLastUpdate = DateTime.MinValue;
    private string   _etaSuffix     = "";

    // ── Install lifecycle (P2-11): non-null while an install runs. Doubles as
    //    the single-install guard — installers share %TEMP% files, so two
    //    concurrent installs would corrupt each other. ──────────────────────
    private CancellationTokenSource? _installCts;

    // ── Item tracker view mode ("list" = DataGrid, "grid" = icon tiles) ──────
    private string _trackerViewMode = "list";

    // ── First-run checklist state ─────────────────────────────────────────────
    private bool         _hasSelectedGameOnce;
    private IGamePlugin? _homeHeroPlugin;

    // ── One-game-at-a-time (P2-5): true while RefreshButtons has disabled the
    //    launch buttons because a DIFFERENT game is running. Lets the no-game
    //    branch re-enable exactly what this rule disabled — and nothing else
    //    (e.g. a launch-in-progress disabled state). ──────────────────────────
    private bool _playDisabledForOtherGame;

    // ── Sidebar "Not Installed" section toggle ────────────────────────────────
    private bool _showNotInstalled = true;

    // ── Window-lifetime cancellation for background tasks ──────────────────────
    private readonly CancellationTokenSource _mainWindowCts = new();

    // ── AP drop detection (toast on unexpected disconnect mid-session) ────────
    private ApConnectionState _lastApState = ApConnectionState.Disconnected;
    private bool _apTeardownExpected;

    // ── AP auto-reconnect ─────────────────────────────────────────────────────
    private CancellationTokenSource? _reconnectCts;
    private int          _reconnectAttempt;
    private IGamePlugin? _reconnectPlugin;
    private bool         _reconnectInProgress;
    private static readonly int[] ReconnectDelays = { 5, 10, 30, 60, 60 };

    // ── Diagnostics ring buffer (fed by AppendLog, drained by Copy Diagnostics) ─
    private static readonly Queue<string> _diagLogBuffer = new();

    // ── Notification sounds (cached — flipped by the Settings checkbox) ──────
    internal static bool SoundsEnabled = SettingsStore.Load().SoundNotifications;
    private static DateTime _lastSoundAt = DateTime.MinValue;

    // ── AP ecosystem features (DeathLink / Ready / countdown / send toasts) ──
    private bool         _apReadySent;          // Ready(10) sent, not yet cleared
    private int          _countdownSeq;         // invalidates stale overlay fade-outs
    private JsonElement? _pendingHintBacklog;   // backlog parked until names resolve
    private bool         _dpNamesReady;         // DataPackage name maps populated
    /// DataPackage id→name maps for toast + hint-backlog name resolution.
    /// Same source the trackers use (our game's DataPackage); UI thread only.
    private readonly Dictionary<long, string> _dpItemNames     = new();
    private readonly Dictionary<long, string> _dpLocationNames = new();
    // Item-send toast rate limit: timestamps of recently shown toasts; bursts
    // beyond 3-in-2s collapse into one "N items received" summary.
    private readonly Queue<DateTime> _itemToastTimes = new();
    private int              _itemToastSuppressed;
    private DispatcherTimer? _itemToastBurstTimer;

    // ── Quick switcher (Ctrl+K) entry: label + sub + right tag + action ───────
    private sealed record QuickSwitchEntry(string Label, string Sub, string Tag, Action Activate);
    private List<QuickSwitchEntry> _quickSwitchEntries = new();

    // ─────────────────────────────────────────────────────────────────────────

    // ── AP log document (UX-12) ───────────────────────────────────────────────
    // TxtLog is a read-only RichTextBox so log text is selectable/copyable;
    // every AppendLog run lands in this single paragraph.
    private readonly Paragraph _logParagraph = new() { Margin = new Thickness(0) };

    public MainWindow()
    {
        InitializeComponent();

        // Log document: one paragraph, padded like the old TextBlock had.
        TxtLog.Document = new FlowDocument(_logParagraph)
        {
            PagePadding = new Thickness(10),
            FontFamily  = TxtLog.FontFamily,
            FontSize    = TxtLog.FontSize,
        };

        // Restore persisted window size/position/maximized state before the
        // first render so the window never visibly jumps.
        RestoreWindowBounds(SettingsStore.Load());

        // Host panel for generic toasts (the achievement toast stays separate)
        ToastService.Initialize(ToastHost);

        // The "another game is running" hint must be readable while the launch
        // buttons are disabled (WPF hides tooltips on disabled elements by default).
        ToolTipService.SetShowOnDisabled(BtnPlay, true);
        ToolTipService.SetShowOnDisabled(BtnStandalone, true);
        ToolTipService.SetShowOnDisabled(BtnOverviewPlay, true);
        ToolTipService.SetShowOnDisabled(BtnOverviewStandalone, true);

        Loaded += OnLoaded;

        // Tray icon callbacks — safe to wire before Show() is called.
        // "Stop Game" stops the RUNNING game (P2-6) — the sidebar selection
        // may be on a different game entirely while one is playing. It runs
        // through the SAME confirm as the Play-button Stop path (UX-8): the
        // tray menu used to kill the process tree with no confirmation at all.
        _trayIcon.OpenRequested    += () => Dispatcher.Invoke(RestoreFromTray);
        _trayIcon.StopGameRequested += () => Dispatcher.Invoke(() =>
        {
            RestoreFromTray();
            var running = GameRegistry.ActivePlugin;
            if (running != null && ConfirmStopGame(running))
                _ = running.StopAsync();
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Startup
    // ══════════════════════════════════════════════════════════════════════════

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Bind the DataGrid to the observable collection
        TrackerGrid.ItemsSource = _trackerView;

        // Restore last-used AP server from persisted settings
        // (window bounds were already restored in the constructor)
        var startup = SettingsStore.Load();
        if (!string.IsNullOrEmpty(startup.DefaultApServer))
            TxtServer.Text = startup.DefaultApServer;

        // Restore persisted UI state: tracker view mode, MRU connections,
        // first-run checklist progress
        _hasSelectedGameOnce = startup.HasSelectedGameOnce;
        SetTrackerViewMode(startup.TrackerViewMode, persist: false);
        RefreshRecentConnButton();
        RefreshHomePage();

        // Wire achievement notifications. BeginInvoke, not Invoke — ladder
        // grants can fire on plugin pipe threads (check counters), and a
        // synchronous hop would park the pipe's read loop on the UI thread.
        AchievementStore.Instance.AchievementEarned += def =>
            Dispatcher.BeginInvoke(() => ShowAchievementToast(def));

        // Re-apply filter when new items arrive (tracker fires on any thread).
        // Batched + debounced (P2-17): one non-blocking hop per ReceivedItems
        // packet, coalesced through a 250 ms timer so a multi-packet catch-up
        // burst triggers ONE rebuild instead of one per item — the old per-item
        // synchronous Invoke froze the UI (and the WS receive loop) on big
        // multiworld catch-ups.
        _tracker.ItemsAdded += _ => Dispatcher.BeginInvoke(ScheduleTrackerRefresh);

        SetStatus("Checking for updates...");

        // First run (and every run until accepted): make sure the launcher + game
        // folders are in Windows Defender's exclusions so the mod isn't blocked or
        // deleted. Fire-and-forget — it waits for the UI to settle, never blocks.
        _ = EnsureDefenderExclusionsAtStartupAsync();

        // Build sidebar respecting library order (favorites first)
        RebuildGameList();

        // Auto-select the first library game — EXCEPT on a true first run.
        // SelectGame collapses PanelEmpty, so unconditional auto-select made
        // the Getting Started checklist unreachable (P2-16): a brand-new user
        // landed straight on the D2 Play tab. First run keeps the checklist
        // visible; it disappears forever once the user picks any game
        // (SelectGame persists HasSelectedGameOnce).
        if (_hasSelectedGameOnce)
        {
            var firstId = LibraryStore.GetSortedGameIds().FirstOrDefault();
            var firstPlugin = firstId != null
                ? GameRegistry.All.FirstOrDefault(p => p.GameId == firstId)
                : GameRegistry.All.FirstOrDefault();
            if (firstPlugin != null)
                SelectGame(firstPlugin);
        }

        // Background: check for updates only on library plugins — not all 382.
        // Running all plugins in parallel would fire hundreds of network calls at
        // once and immediately exhaust the unauthenticated GitHub rate limit
        // (60/hour). Library plugins are the only ones the user cares about now.
        // A concurrency gate caps the simultaneous in-flight checks so the launcher
        // never fires a burst — this protects every backend (GitHub, Thunderstore,
        // Codeberg, …) regardless of whether a given plugin uses the CDN helper.
        var libraryIds  = new HashSet<string>(LibraryStore.GetSortedGameIds(), StringComparer.OrdinalIgnoreCase);
        using var updateGate = new SemaphoreSlim(6);
        var updateChecks = GameRegistry.All
            .Where(p => libraryIds.Contains(p.GameId))
            .Select(async plugin =>
        {
            await updateGate.WaitAsync();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                // WaitAsync abandons a check whose HttpClient ignores the token.
                await plugin.CheckForUpdateAsync(cts.Token).WaitAsync(cts.Token);
            }
            catch { /* timeout / offline — the version badge just stays as-is */ }
            finally { updateGate.Release(); }

            // Continuations resume on the UI context (started from OnLoaded).
            // The install/version check just learned InstalledVersion (read from
            // disk), so the whole selected-game UI must refresh — not just the
            // version line. Without RefreshButtons/Overview the Play button stayed
            // "Install" and the requirement badge stale until the user clicked.
            if (_selectedPlugin == plugin)
            {
                RefreshVersionBadges(plugin);
                RefreshHeaderBadges(plugin);
                RefreshButtons(plugin);
                SyncOverviewPlayButton();
                RefreshOverview(plugin);
            }

            // Surface updates for installed games as a toast — the user may be
            // on another tab (or another game) when the startup check lands.
            if (plugin.IsInstalled && UpdateAvailable(plugin))
            {
                ToastService.Show($"Update available — {plugin.DisplayName}",
                    $"Version {plugin.AvailableVersion} is ready to install.",
                    ToastKind.Info);
            }
        }).ToList();
        await Task.WhenAll(updateChecks);

        // Every library game's InstalledVersion is now known — rebuild the sidebar
        // so install pills + the favorites/installed/not-installed grouping are
        // correct. RebuildGameList ran once at startup BEFORE these checks, when
        // every game still looked uninstalled (hence "Not installed" stuck on the
        // sidebar until the user clicked the game). It re-highlights the selection.
        RebuildGameList();

        SetStatus("Ready.");

        // Background: check for launcher self-update (silent, non-blocking).
        // Progress is wired here exactly once (P3-13) — subscribing inside the
        // Install-Update click duplicated log lines after a failed retry.
        _launcherUpdater.DownloadProgress += pct =>
            Dispatcher.Invoke(() => AppendLog($"[Update] {pct}%"));
        _launcherUpdater.UpdateAvailable += version => Dispatcher.Invoke(() =>
        {
            AppendLog($"[Update] Launcher update available: v{version} — open Settings to install.");
            BannerLauncherUpdate.Visibility = Visibility.Visible;
            TxtLauncherUpdateVersion.Text   = $"Launcher v{version} available";
            ToastService.Show("Launcher update available",
                $"Version {version} is ready — use the banner above to install.",
                ToastKind.Info);
        });
        _ = _launcherUpdater.CheckAsync();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Library — sidebar rebuild
    // ══════════════════════════════════════════════════════════════════════════

    /// Rebuilds the sidebar game list from the library (respects favorites + order).
    private void RebuildGameList()
    {
        if (!CheckAccess()) { Dispatcher.Invoke(RebuildGameList); return; }

        GameListPanel.Children.Clear();
        _gameCards.Clear();

        var sortedIds = LibraryStore.GetSortedGameIds();
        var plugins   = sortedIds
            .Select(id => GameRegistry.All.FirstOrDefault(p => p.GameId == id))
            .Where(p => p != null)
            .Where(p => !(p is Plugins.Emulated.EmulatorPlugin ep && !ep.ChecksImplemented))
            .ToList();

        var favorites    = plugins.Where(p => LibraryStore.IsFavorite(p!.GameId)).ToList();
        var installed    = plugins.Where(p => !LibraryStore.IsFavorite(p!.GameId) && p!.IsInstalled).ToList();
        var notInstalled = plugins.Where(p => !LibraryStore.IsFavorite(p!.GameId) && !p!.IsInstalled).ToList();

        if (favorites.Count > 0)
        {
            GameListPanel.Children.Add(BuildSidebarSectionHeader("FAVORITES"));
            foreach (var p in favorites)
                GameListPanel.Children.Add(BuildGameCard(p!));
        }

        if (installed.Count > 0)
        {
            GameListPanel.Children.Add(BuildSidebarSectionHeader("INSTALLED"));
            foreach (var p in installed)
                GameListPanel.Children.Add(BuildGameCard(p!));
        }

        if (notInstalled.Count > 0)
        {
            GameListPanel.Children.Add(BuildNotInstalledSectionHeader(notInstalled.Count));
            if (_showNotInstalled)
                foreach (var p in notInstalled)
                    GameListPanel.Children.Add(BuildGameCard(p!));
        }

        if (!plugins.Any())
        {
            var emptyMsg = new TextBlock
            {
                Text         = "Your library is empty.\nBrowse games to add some.",
                FontSize     = 11,
                Foreground   = (Brush)FindResource("BrushMuted"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin       = new Thickness(12, 20, 12, 0),
                Opacity      = 0.7,
            };
            GameListPanel.Children.Add(emptyMsg);
        }

        // Re-highlight the currently selected game if it's still in the list
        if (_selectedPlugin != null && _gameCards.ContainsKey(_selectedPlugin.GameId))
            HighlightGameCard(_selectedPlugin);
    }

    private static TextBlock BuildSidebarSectionHeader(string label)
        => new TextBlock
        {
            Text       = label,
            FontSize   = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x5C, 0x7A)),
            Margin     = new Thickness(12, 10, 0, 4),
        };

    private UIElement BuildNotInstalledSectionHeader(int count)
    {
        var muted = new SolidColorBrush(Color.FromRgb(0x55, 0x5C, 0x7A));
        var row   = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text              = $"NOT INSTALLED  ({count})",
            FontSize          = 9,
            FontWeight        = FontWeights.SemiBold,
            Foreground        = muted,
            Margin            = new Thickness(12, 10, 0, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);

        var chevron = new TextBlock
        {
            Text       = _showNotInstalled ? "▲" : "▼",
            FontSize   = 8,
            Foreground = muted,
            Margin     = new Thickness(0, 10, 12, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor     = System.Windows.Input.Cursors.Hand,
        };
        Grid.SetColumn(chevron, 1);

        row.Children.Add(label);
        row.Children.Add(chevron);
        row.Cursor = System.Windows.Input.Cursors.Hand;
        row.MouseLeftButtonDown += (_, _) =>
        {
            _showNotInstalled = !_showNotInstalled;
            RebuildGameList();
        };
        return row;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Game selection
    // ══════════════════════════════════════════════════════════════════════════

    private void SelectGame(IGamePlugin plugin)
    {
        _selectedPlugin = plugin;

        PanelEmpty.Visibility        = Visibility.Collapsed;
        PanelBrowse.Visibility       = Visibility.Collapsed;
        PanelAchievements.Visibility = Visibility.Collapsed;
        PanelGame.Visibility         = Visibility.Visible;

        TxtGameName.Text     = plugin.DisplayName;
        TxtGameSubtitle.Text = plugin.Subtitle;

        ImgGameIcon.Source = LoadCachedBitmap(plugin.IconPath);

        // ── Per-game header art / theme color ─────────────────────────────────
        // Prefer the generated hero banner (Assets/Heroes/<id>_hero.png);
        // fall back to a subtle accent-tinted solid when no art exists.
        try
        {
            string heroPath = Path.Combine(AppContext.BaseDirectory,
                "Assets", "Heroes", $"{plugin.GameId}_hero.png");
            if (!File.Exists(heroPath))
                heroPath = Path.Combine(AppContext.BaseDirectory,
                    "Assets", "Heroes", "_generic_hero.png");

            if (LoadCachedBitmap(heroPath) is { } heroBmp)
            {
                GameHeader.Background = new ImageBrush(heroBmp)
                {
                    Stretch    = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Center,
                };
            }
            else
            {
                var accent = ParseHexColor(plugin.ThemeAccentColor);
                var baseBg = Color.FromRgb(0x14, 0x17, 0x20);
                // Blend: 75% base + 25% accent for a subtle dark tint
                GameHeader.Background = new SolidColorBrush(Color.FromRgb(
                    (byte)(baseBg.R * 0.75 + accent.R * 0.25),
                    (byte)(baseBg.G * 0.75 + accent.G * 0.25),
                    (byte)(baseBg.B * 0.75 + accent.B * 0.25)));
            }
        }
        catch
        {
            GameHeader.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x20));
        }

        // ── Game requirement badges (e.g. "Requires D2") ─────────────────────
        RefreshHeaderBadges(plugin);

        RefreshVersionBadges(plugin);
        RefreshButtons(plugin);
        RefreshPlaytimeBadge(plugin);

        // ── First-run flag continues below ───────────────────────────────────

        // First-run checklist: remember that a game has been selected at least once
        if (!_hasSelectedGameOnce)
        {
            _hasSelectedGameOnce = true;
            var s = SettingsStore.Load();
            if (!s.HasSelectedGameOnce)
            {
                s.HasSelectedGameOnce = true;
                SettingsStore.Save(s);
            }
        }

        // "Launch Standalone" visibility lives in RefreshButtons (called above)
        // — it shows on the Overview action bar only (§9 one install surface).

        RefreshGameNotice(plugin);

        // ── ROMs tab: emulated games only (D2 / OpenTTD have no patched-ROM
        //    library). Toggle the tab button + populate its list. ─────────────
        if (plugin is Plugins.Emulated.EmulatorPlugin romPlugin)
        {
            TabRoms.Visibility = Visibility.Visible;
            RefreshRomsTab(romPlugin);
        }
        else
        {
            TabRoms.Visibility = Visibility.Collapsed;
            RomsPanel.Children.Clear();
        }

        // Reset news + settings panels — fresh load on next visit
        NewsPanel.Children.Clear();
        SettingsPanel.Children.Clear();

        // Cancel any in-flight news load for the previous plugin
        _newsCts?.Cancel();
        _newsCts?.Dispose();
        _newsCts = null;

        // Reset progression category so the new game opens on Summary
        _progressionCategory = "summary";

        // Always land on the Overview front page when switching games (§6) —
        // SwitchTab triggers RefreshOverview for the freshly selected plugin.
        SwitchTab(PageTab.Overview);
        HighlightGameCard(plugin);

        // Update slot→game suggestion banner (may appear if this game differs from AP connection)
        UpdateGameSuggestionBanner();
    }

    /// Persistent Play-tab banner for games with a launch-time gotcha the
    /// player must know BEFORE sitting in a session wondering what is wrong:
    ///  · ConnectsItself games (OpenTTD fork) are joined from inside the game —
    ///    the launcher only pre-fills credentials (UX-7).
    ///  · Emulated games whose RAM address map is unverified run fine but can
    ///    never send a check (UX-6) — the launch-time toast alone was missable.
    private void RefreshGameNotice(IGamePlugin plugin)
    {
        if (plugin.ConnectsItself)
        {
            TxtGameNoticeIcon.Text       = "ⓘ";
            TxtGameNoticeIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0xAC, 0xDA));
            PanelGameNotice.Background   = new SolidColorBrush(Color.FromRgb(0x0B, 0x16, 0x22));
            PanelGameNotice.BorderBrush  = new SolidColorBrush(Color.FromRgb(0x1E, 0x30, 0x50));
            TxtGameNotice.Text =
                $"{plugin.DisplayName} connects to Archipelago from its main menu — " +
                "your details are pre-filled. The launcher does not hold the slot " +
                "while the game runs.";
            PanelGameNotice.Visibility = Visibility.Visible;
        }
        else if (plugin is Plugins.Emulated.EmulatorPlugin { ChecksImplemented: false })
        {
            TxtGameNoticeIcon.Text       = "⚠";
            TxtGameNoticeIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            PanelGameNotice.Background   = new SolidColorBrush(Color.FromRgb(0x1C, 0x16, 0x08));
            PanelGameNotice.BorderBrush  = new SolidColorBrush(Color.FromRgb(0x3A, 0x30, 0x10));
            TxtGameNotice.Text =
                "Check detection for this game is not yet implemented — it launches " +
                "and plays, but location checks will not reach the multiworld yet.";
            PanelGameNotice.Visibility = Visibility.Visible;
        }
        else
        {
            PanelGameNotice.Visibility = Visibility.Collapsed;
        }
    }

    // ── Version comparison (P2-18) ────────────────────────────────────────────
    // version.dat (V1 installs wrote "Beta 1.9.13"-style content), the GitHub
    // release tag ("Beta-1.9.13") and the manifest "version" field are three
    // independently formatted sources. Raw string equality made any format
    // drift a PERMANENT update badge — every Play became "Update + Play" with
    // nothing to actually download. All update checks compare normalized.

    /// True when the plugin has a known available version that differs from
    /// the installed one after normalization.
    private static bool UpdateAvailable(IGamePlugin plugin)
    {
        // No known available version means we can't claim an update. This also
        // catches NormalizeTag(null) == "" (network failure / repo has no
        // releases), which would otherwise render an empty "↑ UPDATE " badge.
        if (string.IsNullOrEmpty(plugin.AvailableVersion)) return false;

        string installed = NormalizeVersion(plugin.InstalledVersion);

        // "installed" is a sentinel used by manual / ConnectsItself plugins that
        // open a releases page and cannot report a real version. They can never
        // know if an update exists, so suppress the (permanently-true) badge
        // instead of nagging the user on every launch.
        if (installed.Length == 0 ||
            string.Equals(installed, "installed", StringComparison.OrdinalIgnoreCase))
            return false;

        string available = NormalizeVersion(plugin.AvailableVersion);

        // When BOTH versions parse to numeric x.y.z tuples, an update only exists
        // when the published version is actually NEWER. An installed build that is
        // AHEAD of the latest release (e.g. local Stable-2.0.0 vs published
        // Beta-1.9.13) must NOT show an update badge.
        if (TryParseVersion(installed, out var iv) && TryParseVersion(available, out var av))
            return CompareVersion(av, iv) > 0;

        // Non-numeric / unparseable versions: fall back to "differs = update".
        return !string.Equals(installed, available, StringComparison.OrdinalIgnoreCase);
    }

    /// Extract the leading dotted numeric groups from a normalized version
    /// (e.g. "1.9.13" → [1,9,13], "2.0.0" → [2,0,0]). Returns false if none.
    private static bool TryParseVersion(string s, out int[] parts)
    {
        var nums = new List<int>();
        foreach (var seg in s.Split('.', '-', '_', ' '))
        {
            int i = 0;
            while (i < seg.Length && char.IsDigit(seg[i])) i++;
            if (i == 0) { if (nums.Count > 0) break; else continue; }
            if (int.TryParse(seg[..i], out int v)) nums.Add(v);
        }
        parts = nums.ToArray();
        return parts.Length > 0;
    }

    /// Semver-ish compare: >0 if a is newer than b, <0 older, 0 equal.
    private static int CompareVersion(int[] a, int[] b)
    {
        int n = Math.Max(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y) return x - y;
        }
        return 0;
    }

    /// "Beta-1.9.13", "Beta 1.9.13", "beta1.9.13", "v1.9.13" → "1.9.13".
    /// Unknown formats pass through trimmed, so two equal odd strings still match.
    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        string s = version.Trim();
        if (s.StartsWith("beta", StringComparison.OrdinalIgnoreCase))
            s = s[4..];
        else if (s.Length > 1 &&
                 (s[0] == 'v' || s[0] == 'V') && char.IsDigit(s[1]))
            s = s[1..];
        return s.Trim(' ', '-', '_', '.');
    }

    /// Header requirement badges — rebuilt on select AND whenever state that
    /// satisfies a requirement changes (ROM import, install), because
    /// GameBadges is state-aware: satisfied requirements disappear.
    private void RefreshHeaderBadges(IGamePlugin plugin)
    {
        if (!CheckAccess()) { Dispatcher.Invoke(() => RefreshHeaderBadges(plugin)); return; }
        PanelGameBadges.Children.Clear();
        foreach (var label in plugin.GameBadges)
        {
            PanelGameBadges.Children.Add(new Border
            {
                Background        = new SolidColorBrush(Color.FromArgb(0x28, 0xF5, 0x9E, 0x0B)),
                CornerRadius      = new CornerRadius(2),
                Padding           = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text       = label,
                    FontSize   = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                }
            });
        }
    }

    private void RefreshVersionBadges(IGamePlugin plugin)
    {
        if (!CheckAccess()) { Dispatcher.Invoke(() => RefreshVersionBadges(plugin)); return; }

        TxtInstalledVer.Text = plugin.IsInstalled
            ? $"Installed: {plugin.InstalledVersion}"
            : "Not installed";

        if (UpdateAvailable(plugin))
        {
            TxtAvailableVer.Text   = $"↑ {plugin.AvailableVersion}";
            BadgeUpdate.Visibility = Visibility.Visible;
        }
        else
        {
            BadgeUpdate.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshButtons(IGamePlugin plugin)
    {
        if (!CheckAccess()) { Dispatcher.Invoke(() => RefreshButtons(plugin)); return; }

        // §9 one install surface: the header buttons stay permanently hidden
        // (pinned Collapsed in XAML) — this method writes their label/enabled
        // state as the single source of truth, and SyncOverviewPlayButton
        // mirrors it onto the Overview action bar, the only visible surface.
        // BtnOverviewStandalone visibility is decided here too, since this is
        // the per-state authority the old header BtnStandalone used.
        if (!plugin.IsInstalled)
        {
            BtnOverviewStandalone.Visibility = Visibility.Collapsed;
            BtnOverviewUpdate.Visibility     = Visibility.Collapsed;
            BtnPlay.Content                  = NotInstalledActionLabel(plugin);
        }
        else if (plugin.IsRunning)
        {
            // The displayed game owns the live session, so the Play button IS
            // its Stop button — say so. (It already executed the Stop path,
            // but the label used to reset to "Play" after a manual sidebar
            // AP disconnect while the game kept running — P2-5.)
            BtnOverviewStandalone.Visibility = Visibility.Collapsed;
            BtnOverviewUpdate.Visibility     = Visibility.Collapsed;
            BtnPlay.Content                  = "Stop";
        }
        else
        {
            // Installed + idle. Play ALWAYS just launches AP ("AP Play") — it
            // never auto-updates. An available update is an OPTIONAL separate
            // button that only lights up when a NEWER version is published; the
            // launcher itself is the only thing that self-updates as a must.
            BtnOverviewStandalone.Visibility = plugin.SupportsStandalone
                ? Visibility.Visible : Visibility.Collapsed;
            BtnOverviewUpdate.Visibility = UpdateAvailable(plugin)
                ? Visibility.Visible : Visibility.Collapsed;
            BtnPlay.Content = "AP Play";
        }

        // ── One game at a time (P2-5) ────────────────────────────────────────
        // While ANOTHER game runs, this game's launch buttons go dark with an
        // explanation — a second launch would corrupt the live session/ApClient.
        // Only re-enable what THIS branch disabled (_playDisabledForOtherGame),
        // so a launch-in-progress disabled state is never overridden.
        var running = GameRegistry.ActivePlugin;
        bool otherGameRunning = running != null && !ReferenceEquals(running, plugin);
        if (otherGameRunning)
        {
            BtnPlay.IsEnabled       = false;
            BtnStandalone.IsEnabled = false;
            string hint = $"{running!.DisplayName} is running — stop it before starting another game.";
            BtnPlay.ToolTip       = hint;
            BtnStandalone.ToolTip = hint;
            _playDisabledForOtherGame = true;
        }
        else
        {
            BtnPlay.ToolTip       = null;
            BtnStandalone.ToolTip = null;
            if (_playDisabledForOtherGame)
            {
                _playDisabledForOtherGame = false;
                BtnPlay.IsEnabled       = true;
                BtnStandalone.IsEnabled = true;
            }
        }

        // The Overview tab's floating Play button mirrors the header button —
        // every state change funnelled through here keeps the two in lockstep.
        SyncOverviewPlayButton();
    }

    // ── §10 capability-labeled actions (not-installed games) ─────────────────

    /// Honest primary-action label for a game that is not installed yet:
    ///   AutoInstall → "Install"            (launcher downloads everything)
    ///   RomRequired → "Install ROM…"       (install ends in the ROM picker)
    ///   AutoMod     → "Find original game…" until the user located their own
    ///                 install, then "Install mod"
    /// The label is a promise about what the click does — BtnPlay_Click routes
    /// the AutoMod locate step accordingly.
    private string NotInstalledActionLabel(IGamePlugin plugin)
        => EffectiveInstallCapability(plugin) switch
        {
            InstallCapability.RomRequired => "Install ROM…",
            InstallCapability.AutoMod     => OriginalInstallRegistered(plugin)
                                                 ? "Install mod"
                                                 : "Find original game…",
            _                             => "Install",
        };

    /// Capability from the catalog entry when one matches this plugin;
    /// otherwise fall back to the plugin type (emulated games need a ROM,
    /// everything else with a plugin installs itself). A first-party plugin
    /// can ALWAYS be installed by the launcher, so ManualSetup never applies
    /// here — map anything unknown to AutoInstall rather than dead-ending.
    private InstallCapability EffectiveInstallCapability(IGamePlugin plugin)
    {
        var entry = FindCatalogEntryForPlugin(plugin);
        if (entry != null && entry.InstallCapability != InstallCapability.ManualSetup)
            return entry.InstallCapability;
        return plugin is Plugins.Emulated.EmulatorPlugin
            ? InstallCapability.RomRequired
            : InstallCapability.AutoInstall;
    }

    /// True when the user already located a valid original install for this
    /// AutoMod game (and the folder still exists).
    private static bool OriginalInstallRegistered(IGamePlugin plugin)
        => SettingsStore.Load().OriginalGameLocations
               .TryGetValue(plugin.GameId, out var folder) &&
           !string.IsNullOrEmpty(folder) && Directory.Exists(folder);

    /// §10 AutoMod locate step: folder picker → plugin validation → persist.
    /// Returns true when a folder was registered. Validation failure shows the
    /// plugin's reason and leaves the button on "Find original game…" so the
    /// user can simply try again — this flow can never dead-end.
    private bool PromptForOriginalGameFolder(IGamePlugin plugin)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Locate your {plugin.DisplayName} install folder",
        };
        if (dlg.ShowDialog(this) != true) return false;

        string folder = dlg.FolderName;
        string? error = plugin.ValidateExistingInstall(folder);
        if (error != null)
        {
            ConfirmDialog.ShowInfo(this, "That folder doesn't look right", error);
            return false;
        }

        var s = SettingsStore.Load();
        s.OriginalGameLocations[plugin.GameId] = folder;
        SettingsStore.Save(s);

        AppendLog($"[Install] Original {plugin.DisplayName} install registered: {folder}");
        ToastService.Show("Original game located",
            $"{plugin.DisplayName} found — click \"Install mod\" to add Archipelago. " +
            "Your original install is never modified.", ToastKind.Success);
        RefreshButtons(plugin);   // label flips to "Install mod"
        return true;
    }

    /// ROM safety net: the launcher couldn't find/verify the ROM an emulated
    /// game needs. Tell the player EXACTLY which version (and MD5 when known)
    /// and let them point to it. Loops until they provide the right file or
    /// cancel. Returns true once a usable ROM is imported.
    private bool EnsureEmulatorRom(
        Plugins.Emulated.EmulatorPlugin emu, Plugins.Emulated.RomRequirement req)
    {
        while (true)
        {
            string head = req.WrongVersionPresent
                ? $"The {req.GameName} ROM in your library is the wrong version for this multiworld."
                : $"I couldn't find your {req.GameName} game file.";

            string body =
                head + "\n\n" +
                $"I need: {req.VersionLabel}" +
                (req.RequiredMd5 != null
                    ? $"\nFile fingerprint (MD5): {req.RequiredMd5}"
                    : "") +
                "\n\nClick “Locate game file…” and point me to it. " +
                "Your original file is never modified — the launcher keeps its own copy.";

            if (!ConfirmDialog.Show(this, "Game file needed", body,
                                    "Locate game file…", "Cancel"))
                return false;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = $"Select your {req.GameName} ROM",
                Filter = req.FileFilter,
            };
            if (dlg.ShowDialog(this) != true)
                continue;   // back to the explanation (Cancel there exits)

            string? error = emu.TryImportLocatedRom(dlg.FileName, req);
            if (error == null)
            {
                AppendLog($"[Launch] {req.GameName} game file located and imported.");
                if (emu == _selectedPlugin) RefreshOverview(emu);
                return true;
            }
            ConfirmDialog.ShowInfo(this, "That's not the right file", error);
            // loop and ask again
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Playtime display (header badge + sidebar card tooltip)
    // ══════════════════════════════════════════════════════════════════════════

    /// Show total playtime + last-played in the game header.
    /// Numbers are REAL tracked sessions (PlaytimeService) — never estimates.
    /// Never-played games keep the badge hidden rather than showing "Never
    /// played": the header's job is the Play call-to-action sitting right next
    /// to it, and on a fresh install every game would carry the same gray pill
    /// — pure noise with no information the Play button doesn't already convey.
    /// The never-played state IS stated explicitly where users look for info:
    /// the sidebar card tooltip (BuildGameCardToolTip).
    private void RefreshPlaytimeBadge(IGamePlugin plugin)
    {
        if (!CheckAccess()) { Dispatcher.Invoke(() => RefreshPlaytimeBadge(plugin)); return; }

        var last = PlaytimeService.LastPlayed(plugin.GameId);
        if (last == null)
        {
            BadgePlaytime.Visibility = Visibility.Collapsed;
            return;
        }

        var total = PlaytimeService.TotalPlaytime(plugin.GameId);
        TxtPlaytime.Text = $"⏱ {PlaytimeService.FormatPlaytime(total)} played · " +
                           $"Last played {PlaytimeService.FormatRelativeDate(last.Value)}";
        BadgePlaytime.Visibility = Visibility.Visible;
    }

    /// Sidebar card tooltip: game name + real tracked playtime. Games with no
    /// recorded sessions say "Never played" explicitly — never a fake number,
    /// never a silent omission. (Sidebar search is unaffected: the card's Tag
    /// carries the display name it matches on.)
    private object BuildGameCardToolTip(IGamePlugin plugin)
    {
        var tip = new StackPanel();
        tip.Children.Add(new TextBlock
        {
            Text       = plugin.DisplayName,
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
        });

        var last = PlaytimeService.LastPlayed(plugin.GameId);
        if (last == null)
        {
            tip.Children.Add(new TextBlock
            {
                Text     = "Never played",
                FontSize = 11,
                Opacity  = 0.8,
                Margin   = new Thickness(0, 3, 0, 0),
            });
        }
        else
        {
            tip.Children.Add(new TextBlock
            {
                Text     = $"⏱ {PlaytimeService.FormatPlaytime(PlaytimeService.TotalPlaytime(plugin.GameId))} played",
                FontSize = 11,
                Opacity  = 0.8,
                Margin   = new Thickness(0, 3, 0, 0),
            });
            tip.Children.Add(new TextBlock
            {
                Text     = $"Last played {PlaytimeService.FormatRelativeDate(last.Value)}",
                FontSize = 11,
                Opacity  = 0.8,
            });
        }

        return new System.Windows.Controls.ToolTip
        {
            Background  = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            Content     = tip,
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Tab navigation
    // ══════════════════════════════════════════════════════════════════════════

    private void TabOverview_Click(object sender, MouseButtonEventArgs e)
        => SwitchTab(PageTab.Overview);

    private void TabPlay_Click(object sender, MouseButtonEventArgs e)
        => SwitchTab(PageTab.Play);

    private void TabProgression_Click(object sender, MouseButtonEventArgs e)
        => SwitchTab(PageTab.Progression);

    private void TabTracker_Click(object sender, MouseButtonEventArgs e)
        => SwitchTab(PageTab.Tracker);

    private void TabMap_Click(object sender, MouseButtonEventArgs e)
        => SwitchTab(PageTab.Map);

    private void TabNews_Click(object sender, MouseButtonEventArgs e)
        => SwitchTab(PageTab.News);

    private void TabRoms_Click(object sender, MouseButtonEventArgs e)
        => SwitchTab(PageTab.Roms);

    private void TabSettings_Click(object sender, MouseButtonEventArgs e)
        => SwitchTab(PageTab.Settings);

    /// <summary>
    /// Fade a page in from opacity 0 → 1 over 130 ms.
    /// The page must already be <see cref="Visibility.Visible"/> before calling.
    /// </summary>
    private static void FadeInPage(UIElement page)
    {
        page.Opacity    = 0;
        page.Visibility = Visibility.Visible;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(
            0, 1,
            new System.Windows.Duration(TimeSpan.FromMilliseconds(130)))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        page.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void SwitchTab(PageTab tab)
    {
        _currentTab = tab;

        // Hide all pages instantly, then fade-in only the new one
        PageOverview.Visibility    = Visibility.Collapsed;
        PagePlay.Visibility        = Visibility.Collapsed;
        PageProgression.Visibility = Visibility.Collapsed;
        PageTracker.Visibility     = Visibility.Collapsed;
        PageMap.Visibility         = Visibility.Collapsed;
        PageNews.Visibility        = Visibility.Collapsed;
        PageRoms.Visibility        = Visibility.Collapsed;
        PageSettings.Visibility    = Visibility.Collapsed;

        UIElement newPage = tab switch
        {
            PageTab.Overview    => PageOverview,
            PageTab.Play        => PagePlay,
            PageTab.Progression => PageProgression,
            PageTab.Tracker     => PageTracker,
            PageTab.Map         => PageMap,
            PageTab.News        => PageNews,
            PageTab.Roms        => PageRoms,
            PageTab.Settings    => PageSettings,
            _                   => PagePlay,
        };
        FadeInPage(newPage);

        var gold  = (Brush)FindResource("BrushAccent");
        var muted = (Brush)FindResource("BrushMuted");

        TabOverview.BorderBrush    = tab == PageTab.Overview    ? gold : Brushes.Transparent;
        TabPlay.BorderBrush        = tab == PageTab.Play        ? gold : Brushes.Transparent;
        TabProgression.BorderBrush = tab == PageTab.Progression ? gold : Brushes.Transparent;
        TabTracker.BorderBrush     = tab == PageTab.Tracker     ? gold : Brushes.Transparent;
        TabMap.BorderBrush         = tab == PageTab.Map         ? gold : Brushes.Transparent;
        TabNews.BorderBrush        = tab == PageTab.News        ? gold : Brushes.Transparent;
        TabRoms.BorderBrush        = tab == PageTab.Roms        ? gold : Brushes.Transparent;
        TabSettings.BorderBrush    = tab == PageTab.Settings    ? gold : Brushes.Transparent;

        TabOverviewText.Foreground    = tab == PageTab.Overview    ? gold : muted;
        TabPlayText.Foreground        = tab == PageTab.Play        ? gold : muted;
        TabProgressionText.Foreground = tab == PageTab.Progression ? gold : muted;
        TabTrackerText.Foreground     = tab == PageTab.Tracker     ? gold : muted;
        TabMapText.Foreground         = tab == PageTab.Map         ? gold : muted;
        TabNewsText.Foreground        = tab == PageTab.News        ? gold : muted;
        TabRomsText.Foreground        = tab == PageTab.Roms        ? gold : muted;
        TabSettingsText.Foreground    = tab == PageTab.Settings    ? gold : muted;

        // Overview renders instantly from cached/local data on every visit
        // (only its news teaser is async — and that is per-session cached).
        if (tab == PageTab.Overview && _selectedPlugin != null)
            RefreshOverview(_selectedPlugin);

        // Lazy-load news on first visit
        if (tab == PageTab.News &&
            NewsPanel.Children.Count == 0 &&
            _selectedPlugin != null)
        {
            _ = LoadNewsAsync(_selectedPlugin);
        }

        // Refresh progression whenever tab is opened
        if (tab == PageTab.Progression)
            RefreshProgressionPanel();

        // Lazy-populate settings on first visit for this plugin
        if (tab == PageTab.Settings && _selectedPlugin != null)
            PopulateSettingsPanel(_selectedPlugin);

        // Refresh filter view whenever tracker becomes visible
        if (tab == PageTab.Tracker)
            ApplyTrackerFilter();

        // Populate the Map tab with the plugin's own map UI (or a placeholder)
        if (tab == PageTab.Map)
            PopulateMapTab();

        // Rebuild the ROM library list whenever the tab is opened (cheap, local)
        if (tab == PageTab.Roms && _selectedPlugin is Plugins.Emulated.EmulatorPlugin romPlugin)
            RefreshRomsTab(romPlugin);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Map tab — hosts the selected game's own graphical map tracker
    //
    // The launcher core stays game-agnostic: a plugin that sets
    // SupportsMapTracker=true returns its own WPF map control from
    // CreateMapTrackerPanel(); every other game shows a "not available yet"
    // placeholder. The control is cached by game id so its live state (player
    // position, checks) survives tab switches.
    // ══════════════════════════════════════════════════════════════════════════

    private string? _mapHostPluginId;

    private void PopulateMapTab()
    {
        if (_selectedPlugin is { SupportsMapTracker: true } p)
        {
            if (_mapHostPluginId != p.GameId || MapHost.Content == null)
            {
                MapHost.Content  = p.CreateMapTrackerPanel() ?? BuildMapPlaceholder(loading: true);
                _mapHostPluginId = p.GameId;
            }
        }
        else
        {
            MapHost.Content  = BuildMapPlaceholder(loading: false);
            _mapHostPluginId = null;
        }
    }

    /// Centered placeholder shown when a game has no map tracker (or its panel
    /// is not built yet). loading=true softens the wording.
    private UIElement BuildMapPlaceholder(bool loading)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        stack.Children.Add(new TextBlock
        {
            Text                = "🗺",
            FontSize            = 48,
            Opacity             = 0.45,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 0, 0, 12),
        });
        stack.Children.Add(new TextBlock
        {
            Text                = loading
                ? "Loading the map tracker…"
                : "Map tracker is not available for this game yet.",
            FontSize            = 15,
            Foreground          = (Brush)FindResource("BrushMuted"),
            TextWrapping        = TextWrapping.Wrap,
            TextAlignment       = TextAlignment.Center,
            MaxWidth            = 440,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return new Grid { Children = { stack } };
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ROMs tab — the per-seed patched-ROM library (emulated games only)
    //
    // Each multiworld seed produces a separate patched ROM under
    // Games/ROMs/<gameId>/. SeedLibraryStore tracks them; this page lists them
    // newest-first with status, last-played, accumulated playtime and a delete
    // action. Renders instantly from local data — safe to call on game select,
    // after a launch (a new seed may have registered) and after a session ends.
    // ══════════════════════════════════════════════════════════════════════════

    /// Rebuild the ROM library list for an emulated game. Cheap + local.
    private void RefreshRomsTab(Plugins.Emulated.EmulatorPlugin plugin)
    {
        if (!CheckAccess()) { Dispatcher.Invoke(() => RefreshRomsTab(plugin)); return; }

        RomsPanel.Children.Clear();

        var muted = (Brush)FindResource("BrushMuted");
        var entries = SeedLibraryStore.Instance.GetForGame(plugin.GameId);

        if (entries.Count == 0)
        {
            RomsPanel.Children.Add(new TextBlock
            {
                Text = "No patched ROMs yet — connect to a multiworld and press " +
                       "Play to generate one.",
                Foreground = muted,
                FontSize   = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(2, 8, 2, 0),
            });
            return;
        }

        foreach (var entry in entries)
            RomsPanel.Children.Add(BuildRomRow(plugin, entry));
    }

    /// One ROM card: seed name (big), slot, status pill, last-played, playtime,
    /// and a red Delete button.
    private Border BuildRomRow(Plugins.Emulated.EmulatorPlugin plugin, SeedEntry entry)
    {
        var muted = (Brush)FindResource("BrushMuted");
        var fg    = (Brush)FindResource("BrushText");

        var card = new Border
        {
            Background       = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x20)),
            BorderBrush      = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            BorderThickness  = new Thickness(1),
            CornerRadius     = new CornerRadius(6),
            Padding          = new Thickness(16, 12, 16, 12),
            Margin           = new Thickness(0, 0, 0, 10),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ── Left: details ────────────────────────────────────────────────────
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // Seed name + status pill on one line
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text       = entry.SeedName == "unknown" ? "Unknown seed" : $"Seed {entry.SeedName}",
            FontSize   = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleRow.Children.Add(BuildStatusPill(entry.Status));
        left.Children.Add(titleRow);

        // Slot name
        if (!string.IsNullOrWhiteSpace(entry.SlotName))
            left.Children.Add(new TextBlock
            {
                Text       = $"Slot: {entry.SlotName}",
                FontSize   = 12,
                Foreground = muted,
                Margin     = new Thickness(0, 4, 0, 0),
            });

        // Last played + accumulated playtime
        string lastPlayed = entry.LastPlayedUtc is { } lp
            ? $"Last played {PlaytimeService.FormatRelativeDate(lp)}"
            : "Never played";
        string playtime = PlaytimeService.FormatPlaytime(TimeSpan.FromSeconds(entry.PlaySeconds));
        left.Children.Add(new TextBlock
        {
            Text       = $"{lastPlayed}  ·  {playtime} played",
            FontSize   = 12,
            Foreground = muted,
            Margin     = new Thickness(0, 4, 0, 0),
        });

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // ── Right: Delete button ─────────────────────────────────────────────
        var del = new Button
        {
            Content           = "Delete",
            Foreground        = Brushes.White,
            Background        = new SolidColorBrush(Color.FromRgb(0xD9, 0x4A, 0x4A)),
            BorderThickness   = new Thickness(0),
            Padding           = new Thickness(14, 6, 14, 6),
            FontSize          = 12,
            FontWeight        = FontWeights.SemiBold,
            Cursor            = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        del.Click += (_, _) => DeleteRom(plugin, entry);
        Grid.SetColumn(del, 1);
        grid.Children.Add(del);

        card.Child = grid;
        return card;
    }

    /// Status pill: NEW (gray) / IN PROGRESS (gold) / COMPLETE (green).
    private Border BuildStatusPill(string status)
    {
        var muted = (Brush)FindResource("BrushMuted");
        var gold  = (Brush)FindResource("BrushAccent");

        (string label, Brush brush) = status switch
        {
            SeedLibraryStore.StatusComplete   => ("COMPLETE",    (Brush)FindResource("BrushSuccess")),
            SeedLibraryStore.StatusInProgress => ("IN PROGRESS", gold),
            _                                  => ("NEW",         muted),
        };

        return new Border
        {
            Background        = new SolidColorBrush(Color.FromRgb(0x0D, 0x10, 0x18)),
            BorderBrush       = brush,
            BorderThickness   = new Thickness(1),
            CornerRadius      = new CornerRadius(3),
            Padding           = new Thickness(8, 2, 8, 2),
            Margin            = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text       = label,
                FontSize   = 10,
                FontWeight = FontWeights.Bold,
                Foreground = brush,
            },
        };
    }

    /// Confirm + delete a patched ROM, then re-render the list.
    private void DeleteRom(Plugins.Emulated.EmulatorPlugin plugin, SeedEntry entry)
    {
        string seedLabel = entry.SeedName == "unknown" ? "this seed" : $"Seed {entry.SeedName}";
        string slotLabel = string.IsNullOrWhiteSpace(entry.SlotName) ? "your slot" : entry.SlotName;

        bool confirmed = LauncherV2.UI.Controls.ConfirmDialog.Show(
            this,
            "Delete this ROM?",
            $"{seedLabel} for {slotLabel} — its patched ROM file will be removed from " +
            "the launcher library. Your original game ROM and any save data are " +
            "untouched. This cannot be undone.",
            "Delete", "Cancel", danger: true);
        if (!confirmed) return;

        string? error = Plugins.Emulated.EmulatorPlugin.DeletePatchedRom(entry);
        if (error == null)
            LauncherV2.UI.Controls.ToastService.Show(
                "ROM deleted", $"{seedLabel} removed from the library.",
                LauncherV2.UI.Controls.ToastKind.Success);
        else
            LauncherV2.UI.Controls.ToastService.Show(
                "Could not delete ROM", error,
                LauncherV2.UI.Controls.ToastKind.Error);

        RefreshRomsTab(plugin);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Overview tab — the per-game front page (owner spec §6)
    //
    // Renders instantly from cached/local data (hero art, playtime, sessions,
    // achievements, catalog metadata). The ONLY async piece is the news teaser,
    // which loads lazily and is cached per plugin for the rest of the session.
    // ══════════════════════════════════════════════════════════════════════════

    /// News teaser cache: GameId → newest item. Populated once per game per
    /// session so re-entering Overview never refetches. Only successful,
    /// non-empty feeds are cached — failures retry on the next refresh.
    private readonly Dictionary<string, NewsItem> _overviewNewsCache = new();
    /// GameIds with a teaser fetch in flight (dedupe guard).
    private readonly HashSet<string> _overviewNewsLoading = new();

    /// Catalog metadata for the Overview ("Get the game" link, richer About
    /// text, capability badge). When Browse has not populated _catalogEntries
    /// yet, the bundled local catalog is loaded ONCE in the background and the
    /// page re-renders when it arrives.
    private IReadOnlyList<CatalogEntry>? _overviewCatalog;
    private bool _overviewCatalogLoading;

    /// Link targets for the Overview action bar (set by RefreshOverview).
    private string? _overviewGetGameUrl;
    private string? _overviewWebsiteUrl;

    /// Rebuild the Overview page for <paramref name="plugin"/>. Cheap — safe
    /// to call on every visit and after installs / session ends. No-ops when
    /// the plugin is not the one on screen (e.g. a background install for
    /// another game finishing while the user browses elsewhere).
    private void RefreshOverview(IGamePlugin plugin)
    {
        if (!CheckAccess()) { Dispatcher.Invoke(() => RefreshOverview(plugin)); return; }
        if (!ReferenceEquals(plugin, _selectedPlugin)) return;

        var gold    = (Brush)FindResource("BrushAccent");
        var muted   = (Brush)FindResource("BrushMuted");
        var fg      = (Brush)FindResource("BrushText");
        var success = (Brush)FindResource("BrushSuccess");

        // ── Hero banner ───────────────────────────────────────────────────────
        TxtOverviewTitle.Text      = plugin.DisplayName;
        TxtOverviewSubtitle.Text   = plugin.Subtitle;
        OverviewHeroArt.Background = BuildOverviewHeroBrush(plugin);

        var entry = FindCatalogEntryForPlugin(plugin);

        // Header badges share the same state-aware source — keep them in step
        // with the Overview (ROM import / install happen while both are visible).
        if (plugin == _selectedPlugin) RefreshHeaderBadges(plugin);

        // Badges over the hero: installed state, update, capability, requirements
        OverviewBadgesPanel.Children.Clear();
        void AddOverviewBadge(string label, Color tint, string? tooltip = null)
        {
            OverviewBadgesPanel.Children.Add(new Border
            {
                Background   = new SolidColorBrush(Color.FromArgb(0xB8, 0x10, 0x13, 0x1E)),
                BorderBrush  = new SolidColorBrush(Color.FromArgb(0x70, tint.R, tint.G, tint.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding      = new Thickness(8, 3, 8, 3),
                Margin       = new Thickness(0, 0, 6, 0),
                ToolTip      = tooltip,
                Child = new TextBlock
                {
                    Text       = label,
                    FontSize   = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(tint),
                }
            });
        }

        if (plugin.IsInstalled)
            AddOverviewBadge($"✓ INSTALLED · {plugin.InstalledVersion}",
                Color.FromRgb(0x22, 0xC5, 0x5E));
        else
            AddOverviewBadge("NOT INSTALLED", Color.FromRgb(0x8A, 0x90, 0xA8));

        if (UpdateAvailable(plugin))
            AddOverviewBadge($"↑ UPDATE {plugin.AvailableVersion}",
                Color.FromRgb(0xE8, 0xA0, 0x18),
                "A newer version is available — Play installs it first.");

        // ── Capability/requirement badges are STATE-AWARE: once everything a
        //    game needs is in place, the nagging amber pills collapse into one
        //    green "READY TO PLAY" instead of contradicting the installed
        //    badge ("Installed" next to "ROM needed" next to "Bring your own
        //    ROM" said the same unmet thing three times after it WAS met). ────
        bool romReady = plugin is not Plugins.Emulated.EmulatorPlugin emuRp ||
                        (emuRp.RomPath != null && File.Exists(emuRp.RomPath));
        bool requirementsMet = plugin.IsInstalled && romReady &&
                               plugin.GameBadges.Length == 0;

        if (requirementsMet)
        {
            AddOverviewBadge("✓ READY TO PLAY", Color.FromRgb(0x22, 0xC5, 0x5E),
                "Everything is in place — press Play.");
        }
        else
        {
            // Capability pill only while it still tells the user something to
            // DO; suppress the duplicate "ROM needed" badge when the pill
            // already says "Bring your own ROM".
            bool capShown = false;
            if (entry != null && !plugin.IsInstalled || (entry != null && !romReady))
            {
                var (capLabel, capTip, capTint) = CapabilityPresentation(entry!);
                AddOverviewBadge(capLabel.ToUpperInvariant(), capTint, capTip);
                capShown = entry!.InstallCapability == InstallCapability.RomRequired;
            }
            foreach (var badge in plugin.GameBadges)
            {
                if (capShown && badge.Equals("ROM needed", StringComparison.OrdinalIgnoreCase))
                    continue;   // the capability pill already says it
                AddOverviewBadge(badge.ToUpperInvariant(), Color.FromRgb(0xF5, 0x9E, 0x0B));
            }
        }

        // ── Action bar ────────────────────────────────────────────────────────
        SyncOverviewPlayButton();

        // "Get the game ↗" — only while NOT installed and the catalog knows
        // where to get it (purchase link preferred, official site fallback).
        _overviewGetGameUrl = !plugin.IsInstalled
            ? FirstNonEmpty(entry?.PurchaseUrl, entry?.Links?.PurchaseUrl,
                            entry?.Links?.OfficialSite)
            : null;
        BtnOverviewGetGame.Content    = entry?.Free == true ? "Free — get it ↗" : "Get the game ↗";
        BtnOverviewGetGame.ToolTip    = _overviewGetGameUrl;
        BtnOverviewGetGame.Visibility = _overviewGetGameUrl != null
            ? Visibility.Visible : Visibility.Collapsed;

        // "Website ↗" — official site (or AP game page), hidden when it would
        // duplicate the Get-the-game target.
        _overviewWebsiteUrl = FirstNonEmpty(entry?.Links?.OfficialSite,
                                            entry?.Links?.ApGamePage);
        if (_overviewWebsiteUrl != null && _overviewWebsiteUrl == _overviewGetGameUrl)
            _overviewWebsiteUrl = null;
        BtnOverviewWebsite.ToolTip    = _overviewWebsiteUrl;
        BtnOverviewWebsite.Visibility = _overviewWebsiteUrl != null
            ? Visibility.Visible : Visibility.Collapsed;

        // ── Stats strip — real tracked numbers only, never estimates ─────────
        OverviewStatsPanel.Children.Clear();
        var last  = PlaytimeService.LastPlayed(plugin.GameId);
        var total = PlaytimeService.TotalPlaytime(plugin.GameId);
        OverviewStatsPanel.Children.Add(BuildOverviewStatTile(
            last == null ? "Never played" : PlaytimeService.FormatPlaytime(total),
            "Playtime", last == null ? muted : fg, muted,
            "Real tracked playtime in this launcher"));
        OverviewStatsPanel.Children.Add(BuildOverviewStatTile(
            last == null ? "—" : PlaytimeService.FormatRelativeDate(last.Value),
            "Last played", last == null ? muted : fg, muted));
        int sessions = PlaytimeService.TotalSessions(plugin.GameId);
        OverviewStatsPanel.Children.Add(BuildOverviewStatTile(
            sessions.ToString(), sessions == 1 ? "Session" : "Sessions",
            sessions == 0 ? muted : fg, muted));
        var gameDefs   = AchievementDefinitions.All
            .Where(d => d.GameId == plugin.GameId).ToList();
        int gameEarned = gameDefs.Count(d => AchievementStore.Instance.IsEarned(d.Id));
        OverviewStatsPanel.Children.Add(BuildOverviewStatTile(
            gameDefs.Count == 0 ? "—" : $"{gameEarned} of {gameDefs.Count}",
            "Achievements",
            gameDefs.Count == 0 || gameEarned == 0 ? muted
                : gameEarned == gameDefs.Count ? success : gold,
            muted,
            gameDefs.Count == 0
                ? "No achievements defined for this game yet"
                : $"Achievements earned in {plugin.DisplayName}"));

        // ── About card ────────────────────────────────────────────────────────
        string about = plugin.Description.Trim();
        TxtOverviewAbout.Text       = about;
        TxtOverviewAbout.Visibility = about.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;

        // Append the catalog's richer description when it adds something.
        string? extra = null;
        if (!string.IsNullOrWhiteSpace(entry?.Description))
        {
            string catalogDesc = entry!.Description.Trim();
            if (catalogDesc.Length > about.Length &&
                !string.Equals(catalogDesc, about, StringComparison.Ordinal))
                extra = catalogDesc;
        }
        TxtOverviewAboutMore.Text       = extra ?? "";
        TxtOverviewAboutMore.Visibility = extra != null
            ? Visibility.Visible : Visibility.Collapsed;

        TxtOverviewApWorld.Text = $"Archipelago world: {plugin.ApWorldName}";

        // ── Credits ───────────────────────────────────────────────────────────
        var credits = LauncherV2.Core.GameCredits.Get(plugin.GameId);
        if (credits.HasValue)
        {
            TxtOverviewCreditsGameDev.Text       = $"Game by: {credits.Value.GameDev}";
            TxtOverviewCreditsGameDev.Visibility = Visibility.Visible;
            if (credits.Value.ApAuthor != null)
            {
                TxtOverviewCreditsApAuthor.Text       = $"AP integration by: {credits.Value.ApAuthor}";
                TxtOverviewCreditsApAuthor.Visibility = Visibility.Visible;
            }
            else
            {
                TxtOverviewCreditsApAuthor.Visibility = Visibility.Collapsed;
            }

            string? apLogic = LauncherV2.Core.GameCredits.GetApLogic(plugin.GameId);
            if (apLogic != null)
            {
                TxtOverviewCreditsApLogic.Text       = $"AP logic by: {apLogic}";
                TxtOverviewCreditsApLogic.Visibility = Visibility.Visible;
            }
            else
            {
                TxtOverviewCreditsApLogic.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            TxtOverviewCreditsGameDev.Visibility  = Visibility.Collapsed;
            TxtOverviewCreditsApAuthor.Visibility = Visibility.Collapsed;
            TxtOverviewCreditsApLogic.Visibility  = Visibility.Collapsed;
        }

        // ── Teasers ───────────────────────────────────────────────────────────
        RefreshOverviewAchievements(plugin, fg, muted, gold);
        RefreshOverviewNewsTeaser(plugin);
    }

    /// First non-null, non-empty string of the candidates (action-bar links).
    private static string? FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    /// Hero art for the Overview banner. Same fallback chain as the game
    /// header: per-game hero PNG → generic hero PNG → accent-tinted gradient.
    private static Brush BuildOverviewHeroBrush(IGamePlugin plugin)
    {
        try
        {
            string heroPath = Path.Combine(AppContext.BaseDirectory,
                "Assets", "Heroes", $"{plugin.GameId}_hero.png");
            if (!File.Exists(heroPath))
                heroPath = Path.Combine(AppContext.BaseDirectory,
                    "Assets", "Heroes", "_generic_hero.png");

            if (LoadCachedBitmap(heroPath) is { } heroBmp)
                return new ImageBrush(heroBmp)
                {
                    Stretch    = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Top,
                };
        }
        catch { /* fall through to the gradient */ }

        var accent = ParseHexColor(plugin.ThemeAccentColor);
        var baseBg = Color.FromRgb(0x14, 0x17, 0x20);
        var tinted = Color.FromRgb(
            (byte)(baseBg.R * 0.6 + accent.R * 0.4),
            (byte)(baseBg.G * 0.6 + accent.G * 0.4),
            (byte)(baseBg.B * 0.6 + accent.B * 0.4));
        return new LinearGradientBrush(tinted, baseBg, 90.0);
    }

    /// Mirror the hidden header BtnPlay/BtnStandalone onto the Overview's
    /// floating action buttons — label, enabled state and tooltip. The header
    /// buttons are the state machine (§9); these are the only visible surface.
    /// Called from RefreshButtons (the single button-semantics authority),
    /// from RefreshOverview, and after every direct BtnPlay mutation on the
    /// launch/stop paths.
    private void SyncOverviewPlayButton()
    {
        string label = BtnPlay.Content as string ?? "Play";
        // "Find original game…" (§10 AutoMod) is a locate step, not a launch —
        // no ▶ glyph on that one.
        BtnOverviewPlay.Content =
              label.StartsWith("Stop", StringComparison.Ordinal) ? $"■  {label}"
            : label.StartsWith("Find", StringComparison.Ordinal) ? label
            : $"▶  {label}";
        BtnOverviewPlay.IsEnabled = BtnPlay.IsEnabled;
        BtnOverviewPlay.ToolTip   = BtnPlay.ToolTip;

        BtnOverviewStandalone.IsEnabled = BtnStandalone.IsEnabled;
        BtnOverviewStandalone.ToolTip   = BtnStandalone.ToolTip
            ?? "Launch the game without an Archipelago connection";
    }

    /// The Overview Play button is a front for the header BtnPlay — same
    /// state machine, zero duplicated launch logic. It jumps to the Play tab
    /// (the launch surface with the AP log and install progress) and re-raises
    /// Click on the real button so install/update/launch/stop semantics stay
    /// in ONE place.
    private void BtnOverviewPlay_Click(object sender, RoutedEventArgs e)
    {
        SwitchTab(PageTab.Play);
        if (BtnPlay.IsEnabled)
            BtnPlay.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    /// Same front pattern for standalone launches (§9): the hidden header
    /// BtnStandalone keeps the guard/launch logic in one place.
    private void BtnOverviewStandalone_Click(object sender, RoutedEventArgs e)
    {
        SwitchTab(PageTab.Play);
        if (BtnStandalone.IsEnabled)
            BtnStandalone.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    /// Opt-in "Update available" button — downloads + installs the newest
    /// published version WITHOUT launching (Play is now AP-launch only). Only
    /// visible when a strictly-newer version exists. Updating a game is never
    /// required; only the launcher itself self-updates as a must.
    private async void BtnOverviewUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlugin == null) return;
        SwitchTab(PageTab.Play);
        await RunInstallAsync(_selectedPlugin);
    }

    private void BtnOverviewGetGame_Click(object sender, RoutedEventArgs e)
    {
        if (_overviewGetGameUrl != null) OpenUrl(_overviewGetGameUrl);
    }

    private void BtnOverviewWebsite_Click(object sender, RoutedEventArgs e)
    {
        if (_overviewWebsiteUrl != null) OpenUrl(_overviewWebsiteUrl);
    }

    /// One tile in the Overview stats strip: big value + small caption.
    private static UIElement BuildOverviewStatTile(
        string value, string label, Brush valueBrush, Brush labelBrush,
        string? tooltip = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text         = value,
            FontSize     = 17,
            FontWeight   = FontWeights.Bold,
            Foreground   = valueBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(new TextBlock
        {
            Text       = label,
            FontSize   = 10,
            Foreground = labelBrush,
            Margin     = new Thickness(0, 3, 0, 0),
        });
        return new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x20)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(14, 12, 14, 12),
            Margin          = new Thickness(4, 0, 4, 0),
            ToolTip         = tooltip,
            Child           = stack,
        };
    }

    // ── Achievements teaser ───────────────────────────────────────────────────

    /// Up to 4 of THIS game's achievements: earned first (most impressive tier
    /// first), then the nearest unearned (easiest tier first). Inside a game's
    /// pages only that game's achievements may appear — never the global list.
    private void RefreshOverviewAchievements(
        IGamePlugin plugin, Brush fg, Brush muted, Brush gold)
    {
        OverviewAchHost.Children.Clear();

        var defs = AchievementDefinitions.All
            .Where(d => d.GameId == plugin.GameId).ToList();
        if (defs.Count == 0)
        {
            OverviewAchHost.Children.Add(new TextBlock
            {
                Text       = "No achievements for this game yet.",
                FontSize   = 11,
                Foreground = muted,
                Opacity    = 0.7,
                Margin     = new Thickness(0, 4, 0, 0),
            });
            return;
        }

        var tierRank = new Dictionary<string, int>
            { ["bronze"] = 0, ["silver"] = 1, ["gold"] = 2, ["platinum"] = 3 };
        var tierColors = new Dictionary<string, Brush>
        {
            ["platinum"] = new SolidColorBrush(Color.FromRgb(0xB0, 0xE0, 0xFF)),
            ["gold"]     = gold,
            ["silver"]   = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xD0)),
            ["bronze"]   = new SolidColorBrush(Color.FromRgb(0xCD, 0x7F, 0x32)),
        };

        var store  = AchievementStore.Instance;
        var picked = defs
            .OrderBy(d => store.IsEarned(d.Id) ? 0 : 1)
            .ThenBy(d => store.IsEarned(d.Id)
                ? -tierRank.GetValueOrDefault(d.Tier, 0)   // earned: highest tier first
                :  tierRank.GetValueOrDefault(d.Tier, 99)) // unearned: nearest first
            .Take(4);

        foreach (var def in picked)
        {
            bool earned = store.IsEarned(def.Id);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock
            {
                Text              = def.Icon,
                FontSize          = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 9, 0),
            };
            Grid.SetColumn(icon, 0);

            var title = new TextBlock
            {
                Text              = def.Title,
                FontSize          = 11,
                FontWeight        = FontWeights.SemiBold,
                Foreground        = fg,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(title, 1);

            var tier = new TextBlock
            {
                Text              = $"{AchievementDefinitions.AchievementPoints(def.Tier)} pts",
                FontSize          = 9,
                Foreground        = tierColors.GetValueOrDefault(def.Tier, muted),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(tier, 2);

            row.Children.Add(icon);
            row.Children.Add(title);
            row.Children.Add(tier);

            OverviewAchHost.Children.Add(new Border
            {
                Background   = new SolidColorBrush(Color.FromArgb(
                    earned ? (byte)0x28 : (byte)0x10, 0x1E, 0x22, 0x33)),
                CornerRadius = new CornerRadius(3),
                Padding      = new Thickness(10, 7, 10, 7),
                Margin       = new Thickness(0, 0, 0, 6),
                Opacity      = earned ? 1.0 : 0.5,
                ToolTip      = def.Description,
                Child        = row,
            });
        }
    }

    private void OverviewAllAchievements_Click(object sender, MouseButtonEventArgs e)
    {
        // Opens the GLOBAL achievements page scrolled to this game's group —
        // the teaser above the link stays the only in-game surface (spec §4).
        ShowAchievementsPage(_selectedPlugin?.GameId);
    }

    // ── News teaser (the page's only async piece) ─────────────────────────────

    private void RefreshOverviewNewsTeaser(IGamePlugin plugin)
    {
        if (_overviewNewsCache.TryGetValue(plugin.GameId, out var cached))
        {
            RenderOverviewNewsTeaser(cached);
            return;
        }

        OverviewNewsHost.Children.Clear();
        OverviewNewsHost.Children.Add(new TextBlock
        {
            Text       = "Loading latest news...",
            FontSize   = 11,
            Foreground = (Brush)FindResource("BrushMuted"),
            Opacity    = 0.7,
            Margin     = new Thickness(0, 4, 0, 0),
        });

        if (!_overviewNewsLoading.Add(plugin.GameId)) return; // fetch in flight
        _ = LoadOverviewNewsTeaserAsync(plugin);
    }

    private async Task LoadOverviewNewsTeaserAsync(IGamePlugin plugin)
    {
        NewsItem? newest = null;
        try
        {
            var items = await plugin.GetNewsAsync();
            newest = items.OrderByDescending(n => n.Date).FirstOrDefault();
        }
        catch { /* offline — handled below */ }

        await Dispatcher.InvokeAsync(() =>
        {
            _overviewNewsLoading.Remove(plugin.GameId);
            // Only successful, non-empty feeds are cached — an offline fetch
            // retries the next time the Overview refreshes.
            if (newest != null) _overviewNewsCache[plugin.GameId] = newest;
            if (ReferenceEquals(plugin, _selectedPlugin))
                RenderOverviewNewsTeaser(newest);
        });
    }

    private void RenderOverviewNewsTeaser(NewsItem? item)
    {
        OverviewNewsHost.Children.Clear();
        var muted = (Brush)FindResource("BrushMuted");

        if (item == null)
        {
            OverviewNewsHost.Children.Add(new TextBlock
            {
                Text       = "No news available.",
                FontSize   = 11,
                Foreground = muted,
                Opacity    = 0.7,
                Margin     = new Thickness(0, 4, 0, 0),
            });
            return;
        }

        OverviewNewsHost.Children.Add(new TextBlock
        {
            Text         = item.Title,
            FontSize     = 13,
            FontWeight   = FontWeights.SemiBold,
            Foreground   = (Brush)FindResource("BrushText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        OverviewNewsHost.Children.Add(new TextBlock
        {
            Text       = string.IsNullOrEmpty(item.Version)
                ? item.Date.ToString("MMM dd, yyyy")
                : $"{item.Version} · {item.Date:MMM dd, yyyy}",
            FontSize   = 10,
            Foreground = muted,
            Margin     = new Thickness(0, 2, 0, 6),
        });
        // Two-line body clamp: wrapped text inside a fixed two-line height
        // with character ellipsis on overflow.
        OverviewNewsHost.Children.Add(new TextBlock
        {
            Text                 = FlattenNewsBody(item.Body),
            FontSize             = 11,
            Foreground           = muted,
            TextWrapping         = TextWrapping.Wrap,
            TextTrimming         = TextTrimming.CharacterEllipsis,
            LineHeight           = 16,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight            = 32,
            Opacity              = 0.9,
        });
    }

    /// Collapse a markdown news body into one plain-text line for the teaser:
    /// headers/list markers/bold stripped, lines joined, capped well past the
    /// two visible lines.
    private static string FlattenNewsBody(string body)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var raw in body.Split('\n'))
        {
            string line = raw.Trim().TrimStart('#', '-', '*', '•', '>', ' ').Trim();
            if (line.Length == 0) continue;
            line = line.Replace("**", "").Replace("`", "");
            if (sb.Length > 0) sb.Append("  ");
            sb.Append(line);
            if (sb.Length > 300) break;
        }
        return sb.ToString();
    }

    private void OverviewReadMoreNews_Click(object sender, MouseButtonEventArgs e)
        => SwitchTab(PageTab.News);

    // ── Catalog metadata lookup ───────────────────────────────────────────────

    /// Match this plugin to its Browse catalog entry (first-party games share
    /// ids between the two). Prefers the live Browse catalog; falls back to a
    /// lazily-loaded bundled copy. Returns null until either source is loaded
    /// — RefreshOverview re-runs when the bundled copy arrives.
    private CatalogEntry? FindCatalogEntryForPlugin(IGamePlugin plugin)
    {
        var source = _catalogEntries ?? _overviewCatalog;
        if (source == null)
        {
            EnsureOverviewCatalogLoaded();
            return null;
        }
        return source.FirstOrDefault(e =>
            string.Equals(e.Id, plugin.GameId, StringComparison.OrdinalIgnoreCase));
    }

    /// One-shot background load of the bundled catalog (local file — no
    /// network). On completion the Overview re-renders if it is still showing.
    private void EnsureOverviewCatalogLoaded()
    {
        if (_overviewCatalogLoading) return;
        _overviewCatalogLoading = true;

        var wCts = _mainWindowCts;
        _ = Task.Run(async () =>
        {
            string localPath = Path.Combine(AppContext.BaseDirectory,
                "CatalogRepo", "catalog.json");
            if (!File.Exists(localPath)) return;

            var result = await GameCatalog.FetchFromFileAsync(localPath);
            if (result.Games.Count == 0 || wCts.IsCancellationRequested) return;

            await Dispatcher.InvokeAsync(() =>
            {
                _overviewCatalog = result.Games;
                if (_currentTab == PageTab.Overview && _selectedPlugin != null)
                    RefreshOverview(_selectedPlugin);
            });
        }, wCts.Token);
    }

    private void BtnSession_Click(object sender, RoutedEventArgs e)
    {
        // Expand the sidebar connect panel and focus the server field.
        PanelConnInputs.Visibility = Visibility.Visible;
        BtnConnToggle.Content      = "Cancel";
        if (_selectedPlugin != null)
        {
            PanelBrowse.Visibility       = Visibility.Collapsed;
            PanelAchievements.Visibility = Visibility.Collapsed;
            PanelGame.Visibility         = Visibility.Visible;
            PanelEmpty.Visibility        = Visibility.Collapsed;
            SwitchTab(PageTab.Play);
        }
        TxtServer.Focus();
    }

    // ── Sidebar AP connect toggle ──────────────────────────────────────────────

    private void BtnConnToggle_Click(object sender, RoutedEventArgs e)
    {
        // Any manual click means the user is taking over — stop auto-reconnect.
        CancelAutoReconnect(resetAttempts: true);

        bool isConnected = _apClient?.State == ApConnectionState.Connected;

        if (isConnected)
        {
            // Disconnect
            _ = CleanupSessionAsync();
            _currentSession = null;
        }
        else
        {
            // Toggle the input panel open/closed
            bool isOpen = PanelConnInputs.Visibility == Visibility.Visible;
            PanelConnInputs.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
            BtnConnToggle.Content      = isOpen ? "Connect" : "Cancel";
            if (!isOpen) TxtServer.Focus();
        }
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlugin == null)
        {
            ConfirmDialog.ShowInfo(this, "No game selected",
                "Pick a game from the library on the left first — the " +
                "connection needs to know which game your slot is for.");
            return;
        }
        await ConnectApGlobalAsync(_selectedPlugin);
    }

    private void ConnectHint_Click(object sender, MouseButtonEventArgs e)
    {
        // Expand the sidebar connect panel when user clicks the hint link in Play tab
        PanelConnInputs.Visibility = Visibility.Visible;
        BtnConnToggle.Content      = "Cancel";
        TxtServer.Focus();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Zero-friction AP patch acquisition (drag-drop + room-link download).
    //
    // A `.ap<game>` patch is a ZIP whose archipelago.json carries player_name,
    // game (the apworld name), and server (non-empty only for website-generated
    // seeds). We route by `game` to the matching plugin, copy the patch into the
    // plugin's own ROM library (never moving the original), set it as the
    // explicit patch, and pre-fill the connect card when we know the server.
    // ══════════════════════════════════════════════════════════════════════════

    /// True only for a drop of exactly one file whose extension starts with ".ap"
    /// (.apemerald, .apsm, …). Anything else falls through so the game-card
    /// drag-to-reorder (which drags a plain string, not FileDrop) is unaffected.
    private static bool IsSingleApPatchDrop(System.Windows.IDataObject data, out string path)
    {
        path = "";
        if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return false;
        if (data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files || files.Length != 1)
            return false;
        string ext = Path.GetExtension(files[0]);
        if (!ext.StartsWith(".ap", StringComparison.OrdinalIgnoreCase)) return false;
        path = files[0];
        return true;
    }

    private void Window_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (IsSingleApPatchDrop(e.Data, out _))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;   // claim the drag so child reorder handlers ignore it
        }
        // else: leave untouched — game-card reorder & other drags handle themselves
    }

    private void Window_PreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!IsSingleApPatchDrop(e.Data, out string path)) return;   // not ours
        e.Handled = true;
        ImportPatchFile(path);
    }

    /// Read the archipelago.json metadata from a patch ZIP. Returns null when the
    /// file is not a valid AP patch container.
    private static (string game, string? playerName, string? server)? ReadPatchManifest(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("archipelago.json");
            if (entry == null) return null;
            using var s   = entry.Open();
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (!root.TryGetProperty("game", out var g) || g.GetString() is not string game
                || string.IsNullOrWhiteSpace(game))
                return null;
            string? player = root.TryGetProperty("player_name", out var p) ? p.GetString() : null;
            string? server = root.TryGetProperty("server", out var sv) ? sv.GetString() : null;
            return (game, player, server);
        }
        catch { return null; }
    }

    /// Copy `sourcePath` into Games/ROMs/<gameId>/patches/ and return the copy's
    /// path. The source is only ever READ. Re-importing the same file (same name
    /// + same size) reuses the existing copy; a same-name-different-size file is
    /// kept alongside as _2, _3, … so nothing is ever overwritten.
    private static string CopyPatchIntoLibrary(string sourcePath, string gameId)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", gameId, "patches");
        Directory.CreateDirectory(dir);

        string source = Path.GetFullPath(sourcePath);
        string dest   = Path.Combine(dir, Path.GetFileName(source));

        if (string.Equals(source, Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            return dest;   // already in the library

        long srcLen = new FileInfo(source).Length;
        if (File.Exists(dest))
        {
            if (new FileInfo(dest).Length == srcLen) return dest;   // same file → reuse

            string stem = Path.GetFileNameWithoutExtension(dest);
            string ext  = Path.GetExtension(dest);
            for (int i = 2; ; i++)
            {
                string candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
                if (File.Exists(candidate))
                {
                    if (new FileInfo(candidate).Length == srcLen) return candidate;
                    continue;
                }
                dest = candidate;
                break;
            }
        }

        File.Copy(source, dest, overwrite: false);
        return dest;
    }

    /// Import a `.ap*` patch: validate it, route by `game` to the matching
    /// plugin, copy it into that plugin's library, set it as the explicit patch,
    /// select the game, and pre-fill the connect card. The original file is never
    /// moved. Shared by drag-drop, the Settings "Import patch file…" button, and
    /// the room-link download.
    public void ImportPatchFile(string path, string? serverOverride = null)
    {
        if (!File.Exists(path))
        {
            ToastService.Show("Patch not found", $"Could not read {Path.GetFileName(path)}.",
                              ToastKind.Error);
            return;
        }

        var manifest = ReadPatchManifest(path);
        if (manifest == null)
        {
            ToastService.Show("Not an Archipelago patch",
                $"{Path.GetFileName(path)} isn't a valid AP patch (no archipelago.json inside).",
                ToastKind.Warning);
            return;
        }

        string  game       = manifest.Value.game;
        string? playerName = manifest.Value.playerName;
        string? server     = manifest.Value.server;

        // Route by the apworld name to the owning plugin. Only EmulatorPlugins
        // with explicit-patch support (SetExplicitPatch) can store a patch.
        var plugin = GameRegistry.All.FirstOrDefault(p =>
            string.Equals(p.ApWorldName, game, StringComparison.OrdinalIgnoreCase));

        if (plugin is not LauncherV2.Plugins.Emulated.Games.PokemonEmeraldPlugin emu)
        {
            ToastService.Show($"No integration for {game} yet",
                "This launcher can't apply patches for that game — nothing was saved.",
                ToastKind.Warning);
            return;
        }

        string stored;
        try
        {
            stored = CopyPatchIntoLibrary(path, emu.GameId);
        }
        catch (Exception ex)
        {
            ToastService.Show("Could not save patch", ex.Message, ToastKind.Error);
            return;
        }

        emu.SetExplicitPatch(stored);
        SelectGame(emu);

        // A room-link download knows the LIVE host:port from the room page —
        // that wins over the manifest's `server` (which may be stale or empty).
        string? effectiveServer = !string.IsNullOrWhiteSpace(serverOverride)
            ? serverOverride : server;

        if (!string.IsNullOrWhiteSpace(playerName)) TxtSlotName.Text = playerName!.Trim();

        if (!string.IsNullOrWhiteSpace(effectiveServer))
        {
            TxtServer.Text = effectiveServer!.Trim();
            PanelConnInputs.Visibility = Visibility.Visible;
            ToastService.Show("Patch imported",
                $"{emu.DisplayName} — server and slot pre-filled, press Connect.",
                ToastKind.Success);
        }
        else
        {
            ToastService.Show("Patch imported",
                $"Patch imported for {game} — connect to your room, then press Play.",
                ToastKind.Success);
        }
    }

    // ── Room-link patch download (archipelago.gg) ─────────────────────────────
    //
    // The room page (https://archipelago.gg/room/<id>) renders a slots table.
    // Each row carries the player_name, the game, and a download anchor whose
    // href is either /dl_patch/<room>/<patch_id> (most games) or
    // /slot_file/<room>/<player_id> (VVVVVV/SM64/Factorio). The live host:port
    // appears as "archipelago.gg:<port>" in the page text. We parse defensively
    // with regex so a template tweak that keeps these hrefs still works.

    private const string ApHost = "archipelago.gg";

    private static readonly HttpClient _roomHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// One downloadable patch on a room page.
    private sealed record RoomPatchLink(string Url, string SlotName, string Game);

    private void RoomLinkToggle_Click(object sender, RoutedEventArgs e)
    {
        PanelConnInputs.Visibility = Visibility.Visible;
        PanelRoomLink.Visibility =
            PanelRoomLink.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        if (PanelRoomLink.Visibility == Visibility.Visible) TxtRoomLink.Focus();
    }

    private async void BtnRoomLinkFetch_Click(object sender, RoutedEventArgs e)
    {
        string raw = TxtRoomLink.Text.Trim();
        if (string.IsNullOrEmpty(raw)) { TxtRoomLink.Focus(); return; }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ToastService.Show("That doesn't look like a link",
                "Paste your full room URL, e.g. https://archipelago.gg/room/AbCdEf12.",
                ToastKind.Warning);
            return;
        }
        if (!uri.Host.Equals(ApHost, StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith("." + ApHost, StringComparison.OrdinalIgnoreCase))
        {
            ToastService.Show("Only archipelago.gg room links",
                $"This launcher only downloads patches from {ApHost} room pages.",
                ToastKind.Warning);
            return;
        }

        BtnRoomLinkFetch.IsEnabled = false;
        BtnRoomLinkFetch.Content   = "...";
        try
        {
            await FetchRoomPatchAsync(uri);
        }
        catch (OperationCanceledException)
        {
            ToastService.Show("Room page timed out",
                "The room page took too long to respond — check the link and try again.",
                ToastKind.Error);
        }
        catch (HttpRequestException ex)
        {
            ToastService.Show("Could not reach the room page",
                $"Network error: {ex.Message}", ToastKind.Error);
        }
        catch (Exception ex)
        {
            ToastService.Show("Room link failed", ex.Message, ToastKind.Error);
        }
        finally
        {
            BtnRoomLinkFetch.IsEnabled = true;
            BtnRoomLinkFetch.Content   = "Fetch";
        }
    }

    private async Task FetchRoomPatchAsync(Uri roomUri)
    {
        string html;
        using (var resp = await _roomHttp.GetAsync(roomUri))
        {
            if (!resp.IsSuccessStatusCode)
            {
                ToastService.Show("Room page not found",
                    $"The room page returned {(int)resp.StatusCode}. Make sure the " +
                    "room link is correct and the room still exists.", ToastKind.Error);
                return;
            }
            html = await resp.Content.ReadAsStringAsync();
        }

        var links = ParseRoomPatchLinks(html, roomUri);
        if (links.Count == 0)
        {
            ToastService.Show("No patches on that page",
                "Couldn't find a downloadable patch on that room page. Is it the room " +
                "page (archipelago.gg/room/...) and does your game produce a patch file?",
                ToastKind.Warning);
            return;
        }

        string? host = ParseRoomHostPort(html);

        // Pick: exactly one → it; else prefer the one whose slot name matches a
        // typed slot; otherwise ask via a simple chooser.
        RoomPatchLink chosen;
        if (links.Count == 1)
        {
            chosen = links[0];
        }
        else
        {
            string typedSlot = TxtSlotName.Text.Trim();
            var slotMatch = !string.IsNullOrEmpty(typedSlot)
                ? links.FirstOrDefault(l =>
                    string.Equals(l.SlotName, typedSlot, StringComparison.OrdinalIgnoreCase))
                : null;

            if (slotMatch != null)
            {
                chosen = slotMatch;
            }
            else
            {
                int pick = ChoosePatchLink(links);
                if (pick < 0) return;   // user cancelled
                chosen = links[pick];
            }
        }

        await DownloadAndImportPatchAsync(chosen, host);
    }

    /// Collect download anchors from a room page. Defensive: any href containing
    /// "dl_patch" or "slot_file" counts; the nearby player name and game come
    /// from the same table row when present.
    private static List<RoomPatchLink> ParseRoomPatchLinks(string html, Uri roomUri)
    {
        var result = new List<RoomPatchLink>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Prefer row-aware parsing so we can attach slot name + game.
        foreach (Match row in Regex.Matches(html, "<tr.*?</tr>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var href = Regex.Match(row.Value,
                @"href\s*=\s*[""']([^""']*(?:dl_patch|slot_file)[^""']*)[""']",
                RegexOptions.IgnoreCase);
            if (!href.Success) continue;

            string abs = ToAbsolute(href.Groups[1].Value, roomUri);
            if (!seen.Add(abs)) continue;

            // Cells: <td>id</td><td ...><a ...>NAME</a></td><td>GAME</td>...
            var cells = Regex.Matches(row.Value, "<td.*?>(.*?)</td>",
                            RegexOptions.Singleline | RegexOptions.IgnoreCase);
            string slot = cells.Count > 1 ? StripTags(cells[1].Groups[1].Value) : "";
            string game = cells.Count > 2 ? StripTags(cells[2].Groups[1].Value) : "";
            result.Add(new RoomPatchLink(abs, slot, game));
        }

        // Fallback: no table rows matched — scan all anchors on the page.
        if (result.Count == 0)
        {
            foreach (Match m in Regex.Matches(html,
                         @"href\s*=\s*[""']([^""']*(?:dl_patch|slot_file)[^""']*)[""']",
                         RegexOptions.IgnoreCase))
            {
                string abs = ToAbsolute(m.Groups[1].Value, roomUri);
                if (seen.Add(abs)) result.Add(new RoomPatchLink(abs, "", ""));
            }
        }

        return result;
    }

    /// The live "archipelago.gg:<port>" shown on the room page, or null.
    private static string? ParseRoomHostPort(string html)
    {
        var m = Regex.Match(html,
            Regex.Escape(ApHost) + @":(\d{2,5})", RegexOptions.IgnoreCase);
        return m.Success ? $"{ApHost}:{m.Groups[1].Value}" : null;
    }

    private static string ToAbsolute(string href, Uri baseUri)
        => Uri.TryCreate(baseUri, System.Net.WebUtility.HtmlDecode(href), out var abs)
            ? abs.ToString() : href;

    private static string StripTags(string s)
        => System.Net.WebUtility.HtmlDecode(
               Regex.Replace(s, "<.*?>", "", RegexOptions.Singleline)).Trim();

    /// Themed chooser listing slot names; returns the chosen index or -1.
    private int ChoosePatchLink(IReadOnlyList<RoomPatchLink> links)
    {
        var lines = links.Select((l, i) =>
        {
            string who  = string.IsNullOrEmpty(l.SlotName) ? $"Patch {i + 1}" : l.SlotName;
            string game = string.IsNullOrEmpty(l.Game) ? "" : $"  ({l.Game})";
            return $"{i + 1}.  {who}{game}";
        });
        string body = "This room has several patches. Type the NUMBER of yours:\n\n"
                    + string.Join("\n", lines);

        // Reuse the themed input-less dialog set: a numbered confirm. With more
        // than a handful we still keep it text-driven to stay lean.
        for (int attempt = 0; attempt < 1; attempt++)
        {
            string? pick = PromptForLine("Choose your patch", body,
                                         $"1-{links.Count}");
            if (pick == null) return -1;
            if (int.TryParse(pick.Trim(), out int n) && n >= 1 && n <= links.Count)
                return n - 1;
            ToastService.Show("Not a valid choice",
                $"Enter a number between 1 and {links.Count}.", ToastKind.Warning);
        }
        return -1;
    }

    private async Task DownloadAndImportPatchAsync(RoomPatchLink link, string? host)
    {
        string tmp = Path.Combine(Path.GetTempPath(),
            $"ap_room_{Guid.NewGuid():N}.appatch");
        try
        {
            using (var resp = await _roomHttp.GetAsync(link.Url))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    ToastService.Show("Download failed",
                        $"The patch download returned {(int)resp.StatusCode}.",
                        ToastKind.Error);
                    return;
                }
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs);
            }

            // VALIDATE: must be a zip carrying archipelago.json. We never open or
            // execute downloaded content beyond reading that one manifest entry.
            var manifest = ReadPatchManifest(tmp);
            if (manifest == null)
            {
                ToastService.Show("Downloaded file isn't a patch",
                    "The downloaded file is not a valid Archipelago patch — discarded.",
                    ToastKind.Error);
                return;
            }

            // Give the stored copy a sensible name + the real patch extension when
            // the plugin advertises one, so it matches manually-dropped patches.
            string ext  = PatchExtensionFor(manifest.Value.game);
            string baseName = !string.IsNullOrWhiteSpace(link.SlotName)
                ? SanitizeFileStem(link.SlotName!) : "room_patch";
            string named = Path.Combine(Path.GetTempPath(), $"{baseName}{ext}");
            try { if (File.Exists(named)) File.Delete(named); File.Move(tmp, named); tmp = named; }
            catch { /* keep tmp name if rename clashes — import still works */ }

            ImportPatchFile(tmp, serverOverride: host);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// The on-disk patch extension a plugin uses for a given game (so room
    /// downloads land with the same suffix as a generator's output), or ".zip".
    private static string PatchExtensionFor(string game)
    {
        var plugin = GameRegistry.All.FirstOrDefault(p =>
            string.Equals(p.ApWorldName, game, StringComparison.OrdinalIgnoreCase));
        // Known mapping for the integrated game; fall back to a neutral ".appatch".
        if (plugin is LauncherV2.Plugins.Emulated.Games.PokemonEmeraldPlugin)
            return ".apemerald";
        return ".appatch";
    }

    private static string SanitizeFileStem(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "patch" : s;
    }

    /// Minimal themed single-line prompt (no dedicated XAML control needed).
    /// Returns the typed text, or null on Cancel / Esc / close.
    private string? PromptForLine(string title, string message, string watermark)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF2)),
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xB0, 0xC8)),
            Margin = new Thickness(0, 0, 0, 10),
        });
        var box = new TextBox
        {
            FontSize = 13, Padding = new Thickness(7, 5, 7, 5),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            ToolTip = watermark,
        };
        panel.Children.Add(box);

        string? result = null;
        var win = new Window
        {
            Title = title, SizeToContent = SizeToContent.Height, Width = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x24)),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button
        {
            Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(0, 6, 0, 6), IsDefault = true,
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x5E)),
            Foreground  = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF2)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x40, 0x80)),
        };
        var cancel = new Button
        {
            Content = "Cancel", Width = 80, Padding = new Thickness(0, 6, 0, 6),
            IsCancel = true,
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        ok.Click     += (_, _) => { result = box.Text; win.DialogResult = true; };
        cancel.Click += (_, _) => { win.DialogResult = false; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        win.Content = panel;
        win.Loaded += (_, _) => box.Focus();
        return win.ShowDialog() == true ? result : null;
    }

    // ── Global AP connect (sidebar — does NOT launch game) ────────────────────

    private async Task ConnectApGlobalAsync(IGamePlugin plugin)
    {
        // A manual connect supersedes any auto-reconnect in flight. The
        // reconnect loop itself sets _reconnectInProgress so its own calls
        // here don't cancel the loop they run inside.
        if (!_reconnectInProgress) CancelAutoReconnect(resetAttempts: true);
        _reconnectPlugin = plugin;

        string server   = TxtServer.Text.Trim();
        string slot     = TxtSlotName.Text.Trim();
        string password = TxtPassword.Password.Trim();

        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(slot))
        {
            ConfirmDialog.ShowInfo(this, "Missing connection details",
                "Enter the server address and your slot name — both are shown " +
                "on your room page at archipelago.gg.");
            PanelConnInputs.Visibility = Visibility.Visible;
            // Put the caret where the missing value goes (UX-3)
            if (string.IsNullOrEmpty(server)) TxtServer.Focus();
            else                              TxtSlotName.Focus();
            return;
        }

        // Collapse inputs, show pending state
        PanelConnInputs.Visibility = Visibility.Collapsed;
        BtnConnToggle.Content      = "...";
        BtnConnToggle.IsEnabled    = false;

        // A client left over from an unexpected drop is replaced here — it was
        // never disposed, so its socket + receive loop lingered until GC
        // (P3-5). Null the field first so late events hit the stale-guard.
        if (_apClient != null)
        {
            var stale = _apClient;
            _apClient = null;
            try { await stale.DisposeAsync(); } catch { /* already dead */ }
        }
        _playCts?.Cancel();
        _playCts?.Dispose();   // previous attempt's CTS (P3-4)

        _playCts        = new CancellationTokenSource();
        _currentSession = new ApSession(server, slot, password, plugin.ApWorldName);
        _apClient       = new ApClient(_currentSession, plugin);

        // ── Wire global AP events ────────────────────────────────────────────
        _apClient.StateChanged     += state => Dispatcher.Invoke(() => UpdateApStatus(state));
        _apClient.PrintMessage     += msg   => Dispatcher.Invoke(() =>
        {
            AppendLog(msg);
            if (msg.StartsWith("[AP] Server rejected packet", StringComparison.Ordinal))
                ToastService.Show("AP Protocol Error", msg, ToastKind.Warning);
        });
        _apClient.SessionConnected += (mySlot, players) =>
        {
            // Runs on the AP receive thread — capture the raising client so a
            // concurrent sidebar Disconnect (which nulls/replaces + disposes
            // _apClient) can't NRE us between these field reads.
            var ap = _apClient;
            if (ap == null) return;
            _tracker.OnConnected(mySlot, players);
            _locationTracker.OnConnected(ap.ConnectedChecked,
                                          ap.ConnectedMissing, players);
            // Achievement ladder: first-connect + distinct-server tracking.
            AchievementStore.Instance.RecordApConnected(plugin.GameId, server);
            Dispatcher.Invoke(() =>
            {
                RefreshPlayerDropdown();
                RefreshProgressionPanel();
                UpdateGameSuggestionBanner();
            });
            _ = ap.GetDataPackageAsync(new[] { plugin.ApWorldName });
        };
        _apClient.DataPackageReceived += (gameKey, data) =>
        {
            _tracker.OnDataPackage(gameKey, data);
            _locationTracker.OnDataPackage(gameKey, data);
            // After data package arrives, silently scout all unchecked locations
            if (string.Equals(gameKey, plugin.ApWorldName, StringComparison.OrdinalIgnoreCase))
            {
                var missing = _locationTracker.GetMissingIds();
                if (missing.Length > 0)
                    _ = _apClient!.LocationScoutsAsync(missing, createAsHint: 0);
            }
        };
        _apClient.ItemsReceived += (items, receiverSlot) =>
            _tracker.RecordItems(items, receiverSlot);
        _apClient.LocationInfoReceived += items =>
            _locationTracker.OnLocationInfo(items);
        _apClient.ServerCheckedLocations += ids =>
            _locationTracker.OnLocationsChecked(ids);
        // -= first: only CleanupSessionAsync removes ONE subscription, but the
        // reconnect loop re-runs this method per attempt — duplicates meant N×
        // RefreshProgressionPanel per change (P3-6).
        _locationTracker.Changed -= OnLocationTrackerChanged;
        _locationTracker.Changed += OnLocationTrackerChanged;
        _apClient.HintEntryReceived += hint =>
            Dispatcher.Invoke(() => AddHintEntryToPanel(hint));
        _apClient.HintPointsChanged += pts => Dispatcher.Invoke(() =>
        {
            TxtHintCount.Text       = FormatHintPoints(pts);
            TxtHintCount.Visibility = Visibility.Visible;
            if (_currentTab == PageTab.Progression)
                RefreshProgressionPanel();
        });
        _apClient.HintReceived += _ => { }; // plain text still fires HintEntryReceived above
        WireApFeatureEvents(_apClient);     // DeathLink / countdown / toasts / hint backlog

        // Persisted DeathLink opt-in: prime the tag before the socket opens so
        // the Connect handshake itself carries it (nothing is sent yet — the
        // ConnectUpdate no-ops while the WebSocket is closed).
        if (SettingsStore.Load().DeathLinkEnabled)
            await _apClient.SetDeathLinkAsync(true);

        try
        {
            SetStatus("Connecting to Archipelago...");
            var handshake = ArmHandshakeWatch(_apClient);
            await _apClient.ConnectAsync(_playCts.Token);

            // Socket is open — now wait for the server's login verdict.
            // Declaring success here used to show "Connected", save a typo'd
            // slot to the MRU, and hide the inputs while "AP: Error" sat in
            // the corner (P1-6).
            SetStatus("Logging in to the multiworld...");
            string[]? refusal = await AwaitHandshakeVerdictAsync(handshake);
            if (refusal != null)
                throw new InvalidOperationException(
                    TranslateApRefusal(refusal, slot, plugin));

            // Persist the server as default + remember this connection (MRU)
            // — only now that the server actually accepted the login.
            var savedSettings = SettingsStore.Load();
            savedSettings.DefaultApServer = server;
            savedSettings.AddRecentConnection(server, slot);
            SettingsStore.Save(savedSettings);
            RefreshRecentConnButton();
            RefreshWelcomeChecklist();

            BtnConnToggle.Content   = "Disconnect";
            BtnConnToggle.IsEnabled = true;
            SetStatus($"Connected — {server}");
        }
        catch (Exception ex)
        {
            // Raw socket text ("No such host is known") means nothing to a
            // first-time player — translate the common failures into the fix
            // they actually need (UX-3). Refusal messages pass through (they
            // were already translated by TranslateApRefusal).
            string friendly = TranslateConnectError(ex, server);
            AppendLog($"[Error] Connection failed: {friendly}");
            SetStatus("Connection failed.");
            // Auto-reconnect attempts have their own end-of-loop toast — only
            // a user-initiated connect surfaces the failure as a toast.
            if (!_reconnectInProgress)
                ToastService.Show("Connection failed", friendly, ToastKind.Error);
            BtnConnToggle.Content   = "Connect";
            BtnConnToggle.IsEnabled = true;
            // Re-show inputs, caret on the field most likely wrong (UX-3) —
            // but never steal focus during a background reconnect attempt.
            PanelConnInputs.Visibility = Visibility.Visible;
            if (!_reconnectInProgress) FocusConnectField(friendly);
            await CleanupSessionAsync();
            _currentSession = null;
        }
    }

    /// Map the common connect failures to plain guidance (UX-3). Anything the
    /// handshake path already translated (InvalidOperationException from
    /// TranslateApRefusal) and unknown errors pass through unchanged.
    private static string TranslateConnectError(Exception ex, string server)
    {
        if (ex is InvalidOperationException) return ex.Message;
        if (ex is UriFormatException)
            return $"'{server}' does not look like a server address — it " +
                   "should look like archipelago.gg:38281.";

        // Dig out the socket error behind WebSocketException/HttpRequestException
        var sock = FindInner<System.Net.Sockets.SocketException>(ex);
        if (sock != null)
        {
            return sock.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.HostNotFound or
                System.Net.Sockets.SocketError.NoData =>
                    $"Could not find a server at '{server}' — check the " +
                    "address (it should look like archipelago.gg:38281).",
                System.Net.Sockets.SocketError.ConnectionRefused =>
                    "Nothing is answering on that port — check the port number " +
                    "on your room page (rooms get a new port when they restart).",
                System.Net.Sockets.SocketError.TimedOut =>
                    "The server did not respond — check the address and your " +
                    "internet connection, then try again." + LocalHostHint(server),
                _ => ex.Message,
            };
        }
        return ex.Message;
    }

    /// When a public-IP connect times out and that IP could be this very
    /// machine, most routers can't loop LAN traffic back through their own
    /// public address (NAT hairpin) — hosting locally means connecting via
    /// localhost instead. Appended to the timeout guidance only when the
    /// target is a raw public IP (hostnames like archipelago.gg are fine).
    private static string LocalHostHint(string server)
    {
        string host = server.Split(':')[0].Trim();
        if (!System.Net.IPAddress.TryParse(host, out var ip)) return "";
        byte[] b = ip.GetAddressBytes();
        bool privateOrLoopback = b.Length == 4 &&
            (b[0] == 10 || b[0] == 127 ||
             (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
             (b[0] == 192 && b[1] == 168));
        if (privateOrLoopback) return "";
        string port = server.Contains(':') ? server[(server.IndexOf(':') + 1)..] : "38281";
        return $" Hosting the server on this PC? Use localhost:{port} — " +
               "your own public IP usually doesn't work from inside your network.";
    }

    private static T? FindInner<T>(Exception ex) where T : Exception
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is T match) return match;
        return null;
    }

    /// Focus the connect field the error most plausibly points at.
    private void FocusConnectField(string friendlyError)
    {
        if (friendlyError.Contains("slot", StringComparison.OrdinalIgnoreCase))
            TxtSlotName.Focus();
        else if (friendlyError.Contains("password", StringComparison.OrdinalIgnoreCase))
            TxtPassword.Focus();
        else
            TxtServer.Focus();
    }

    // ── AP login handshake (socket-open ≠ logged-in) ──────────────────────────
    // ApClient.ConnectAsync returns when the WebSocket opens; the server
    // validates slot/password asynchronously (RoomInfo → Connect → Connected /
    // ConnectionRefused). Both connect flows arm a watch BEFORE the socket
    // opens and only declare success once the server's verdict arrives.

    /// Subscribe handshake-verdict watchers on a fresh ApClient. Must be called
    /// BEFORE ConnectAsync — subscribing afterwards races a fast server.
    /// Resolves: null = Connected, refusal codes = ConnectionRefused, synthetic
    /// "ConnectionClosed" = socket died before any verdict. The subscriptions
    /// die with the per-session client; late TrySetResult calls are no-ops.
    private static TaskCompletionSource<string[]?> ArmHandshakeWatch(ApClient ap)
    {
        var tcs = new TaskCompletionSource<string[]?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ap.SessionConnected          += (_, _) => tcs.TrySetResult(null);
        ap.ConnectionRefusedReceived += errors => tcs.TrySetResult(errors);
        ap.StateChanged += s =>
        {
            if (s == ApConnectionState.Disconnected)
                tcs.TrySetResult(new[] { "ConnectionClosed" });
        };
        return tcs;
    }

    /// Wait up to 15 s for the armed verdict. Returns null on success, the
    /// refusal codes on rejection, or a synthetic "Timeout" code.
    private static async Task<string[]?> AwaitHandshakeVerdictAsync(
        TaskCompletionSource<string[]?> handshake)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(15));
        var winner  = await Task.WhenAny(handshake.Task, timeout);
        return winner == timeout ? new[] { "Timeout" } : await handshake.Task;
    }

    /// Translate AP ConnectionRefused codes (raw protocol jargon) into the
    /// plain-English guidance a first-time player actually needs.
    private static string TranslateApRefusal(
        string[] errors, string slotName, IGamePlugin plugin)
    {
        string code = errors.Length > 0 ? errors[0] : "Unknown";
        return code switch
        {
            "InvalidSlot" =>
                $"No slot named '{slotName}' on this server — check the spelling " +
                "on your room page.",
            "InvalidPassword" =>
                "Wrong server password.",
            "InvalidGame" =>
                $"This slot is for a different game, not {plugin.ApWorldName} — " +
                "pick the slot's game in the library, then connect again.",
            "IncompatibleVersion" =>
                "The server rejected this client version — the launcher may need an update.",
            "Timeout" =>
                "The server accepted the connection but never answered the login " +
                "(waited 15 seconds) — check the address and port, then try again.",
            "ConnectionClosed" =>
                "The server closed the connection during login — check the address " +
                "and port, then try again.",
            _ => $"The server refused the connection ({code}).",
        };
    }

    // ── Slot→game suggestion banner ───────────────────────────────────────────

    private void UpdateGameSuggestionBanner()
    {
        if (!CheckAccess()) { Dispatcher.Invoke(UpdateGameSuggestionBanner); return; }

        // Toggle "not connected" hint in the Play tab
        bool connected = _apClient?.State == ApConnectionState.Connected;
        if (PanelNotConnectedHint != null)
            PanelNotConnectedHint.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;

        // Determine if the active connection belongs to a different game than currently shown
        string? connGame = _apClient?.ConnectedGame;
        if (!connected || string.IsNullOrEmpty(connGame) || _selectedPlugin == null)
        {
            PanelGameSuggest.Visibility = Visibility.Collapsed;
            _suggestedPlugin            = null;
            return;
        }

        _suggestedPlugin = GameRegistry.All.FirstOrDefault(
            p => string.Equals(p.ApWorldName, connGame, StringComparison.OrdinalIgnoreCase));

        if (_suggestedPlugin == null ||
            string.Equals(_suggestedPlugin.GameId, _selectedPlugin.GameId, StringComparison.OrdinalIgnoreCase))
        {
            PanelGameSuggest.Visibility = Visibility.Collapsed;
            _suggestedPlugin            = null;
            return;
        }

        bool installed = _suggestedPlugin.IsInstalled;
        TxtGameSuggest.Text = installed
            ? $"Your slot is for {connGame}. Switch to it?"
            : $"Your slot is for {connGame} — not yet installed.";
        BtnSuggestSwitch.Content     = "Switch";
        BtnSuggestInstall.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        PanelGameSuggest.Visibility  = Visibility.Visible;
    }

    private void BtnSuggestSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_suggestedPlugin != null)
            SelectGame(_suggestedPlugin);
        PanelGameSuggest.Visibility = Visibility.Collapsed;
    }

    private async void BtnSuggestInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_suggestedPlugin == null) return;
        PanelGameSuggest.Visibility = Visibility.Collapsed;
        // Add to library first if not already there
        LibraryStore.Add(_suggestedPlugin.GameId);
        RebuildGameList();
        SelectGame(_suggestedPlugin);
        await RunInstallAsync(_suggestedPlugin);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Browse / Catalog
    // ══════════════════════════════════════════════════════════════════════════

    private async void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        PanelGame.Visibility         = Visibility.Collapsed;
        PanelEmpty.Visibility        = Visibility.Collapsed;
        PanelAchievements.Visibility = Visibility.Collapsed;
        PanelBrowse.Visibility       = Visibility.Visible;

        if (_catalogEntries == null)
            await LoadCatalogAsync();
        else
            RenderCatalog(_catalogEntries, TxtCatalogSearch.Text);
    }

    private void BtnBrowseBack_Click(object sender, RoutedEventArgs e)
    {
        PanelBrowse.Visibility       = Visibility.Collapsed;
        PanelAchievements.Visibility = Visibility.Collapsed;
        if (_selectedPlugin != null)
        {
            PanelGame.Visibility  = Visibility.Visible;
            PanelEmpty.Visibility = Visibility.Collapsed;
        }
        else
        {
            PanelEmpty.Visibility = Visibility.Visible;
            PanelGame.Visibility  = Visibility.Collapsed;
            RefreshHomePage();
        }
    }

    private async Task LoadCatalogAsync()
    {
        CatalogLoadingBadge.Visibility = Visibility.Visible;
        CatalogPanel.Children.Clear();

        // Show skeleton cards while the catalog fetches
        for (int i = 0; i < 8; i++)
            CatalogPanel.Children.Add(BuildSkeletonCatalogCard());

        try
        {
            var settings = SettingsStore.Load();
            string url = settings.CatalogUrl ?? GameCatalog.DefaultCatalogUrl;

            // Try hosted catalog first; fall back to the bundled local copy.
            var result = await GameCatalog.FetchAsync(url);

            if (result.Games.Count == 0)
            {
                // Network failed or catalog repo doesn't exist yet — load local bundle.
                string localPath = Path.Combine(AppContext.BaseDirectory,
                    "CatalogRepo", "catalog.json");
                if (File.Exists(localPath))
                {
                    result = await GameCatalog.FetchFromFileAsync(localPath);
                    AppendLog("[Catalog] Loaded from local bundle (offline mode).");
                }
            }

            // Re-flag IsOfficial from CatalogRepo/official_games.txt — this is the
            // single source of truth for the Browse "Official" filter (overrides
            // every other is_official signal).
            _catalogEntries = GameCatalog.ApplyOfficialList(result.Games);
            _catalogTools   = result.Tools;
            LogOfficialCoverage();

            if (_catalogEntries.Count == 0)
            {
                CatalogPanel.Children.Add(new TextBlock
                {
                    Text          = "Could not load the game catalog.\nCheck your internet connection.",
                    FontSize      = 14,
                    Foreground    = (Brush)FindResource("BrushMuted"),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping  = TextWrapping.Wrap,
                    Margin        = new Thickness(40)
                });
            }
            else
            {
                RenderCatalog(_catalogEntries, TxtCatalogSearch.Text);

                // Background: merge in official AP games + community_games.txt.
                // Non-blocking — catalog updates in-place when the fetches complete.
                var bgCts = _mainWindowCts;
                _ = Task.Run(async () =>
                {
                    // Step 1: official AP.gg games
                    var merged = await GameCatalog.MergeWithOfficialApGamesAsync(_catalogEntries);

                    // Step 2: community games from the bundled games.txt
                    string communityPath = Path.Combine(AppContext.BaseDirectory,
                        "CatalogRepo", "community_games.txt");
                    if (File.Exists(communityPath))
                    {
                        try
                        {
                            string text        = await File.ReadAllTextAsync(communityPath);
                            var communityResult = GameCatalog.ParseCommunityGamesText(text);

                            // Merge: community entries not already present.
                            // Dedup on DisplayName, ApWorldName, AND a normalized slug of each
                            // (lowercase + alphanumeric-only) so that punctuation/accent
                            // differences ("Diablo II Archipelago" vs "Diablo II: Lord of
                            // Destruction", "Pokémon" vs "Pokemon") don't produce duplicates.
                            static string Slug(string s) => System.Text.RegularExpressions.Regex
                                .Replace(s.ToLowerInvariant().Normalize(
                                    System.Text.NormalizationForm.FormD), @"[^a-z0-9]", "");

                            var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var existingSlugs = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var e in merged)
                            {
                                if (!string.IsNullOrEmpty(e.ApWorldName)) { existingNames.Add(e.ApWorldName); existingSlugs.Add(Slug(e.ApWorldName)); }
                                if (!string.IsNullOrEmpty(e.DisplayName))  { existingNames.Add(e.DisplayName);  existingSlugs.Add(Slug(e.DisplayName)); }
                            }
                            var newEntries = communityResult.Games
                                .Where(e => !existingNames.Contains(e.ApWorldName ?? "")
                                         && !existingNames.Contains(e.DisplayName ?? "")
                                         && !existingSlugs.Contains(Slug(e.ApWorldName ?? ""))
                                         && !existingSlugs.Contains(Slug(e.DisplayName ?? "")))
                                .ToList();
                            if (newEntries.Count > 0)
                                merged = merged.Concat(newEntries)
                                    .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                        }
                        catch { /* community_games.txt parse failure is non-fatal */ }
                    }

                    if (merged.Count > _catalogEntries.Count && !bgCts.IsCancellationRequested)
                    {
                        // Re-apply the official list over the freshly merged set
                        // (the AP datapackage + community merges set their own
                        // is_official, which official_games.txt must override).
                        var flagged = GameCatalog.ApplyOfficialList(merged);
                        Dispatcher.Invoke(() =>
                        {
                            _catalogEntries = flagged;
                            LogOfficialCoverage();
                            RenderCatalog(_catalogEntries, TxtCatalogSearch.Text);
                        });
                    }
                }, bgCts.Token);
            }
        }
        catch (Exception ex)
        {
            // BtnBrowse_Click is async void — an unhandled throw from the catalog
            // fetch/parse/render would crash the process. Surface it as a card.
            AppendLog($"[Catalog] Load failed: {ex.Message}");
            CatalogPanel.Children.Clear();
            CatalogPanel.Children.Add(new TextBlock
            {
                Text          = "Could not load the game catalog.\nCheck your internet connection and try again.",
                FontSize      = 14,
                Foreground    = (Brush)FindResource("BrushMuted"),
                TextAlignment = TextAlignment.Center,
                TextWrapping  = TextWrapping.Wrap,
                Margin        = new Thickness(40)
            });
        }
        finally
        {
            // Always collapse the loading badge — even if an exception occurs.
            CatalogLoadingBadge.Visibility = Visibility.Collapsed;
        }
    }

    /// Log how many official_games.txt names found a matching catalog entry.
    /// Genuinely useful diagnostics: a non-empty "unmatched" list means a name
    /// in official_games.txt has no card to flag (typo, or the catalog hasn't
    /// caught up). Should read "<total>/<total> matched".
    private void LogOfficialCoverage()
    {
        var unmatched = GameCatalog.UnmatchedOfficialNames;
        int total     = GameCatalog.OfficialCount;
        int matched   = total - unmatched.Count;
        string line   = $"[Catalog] Official: {matched}/{total} matched";
        if (unmatched.Count > 0)
            line += $"; unmatched: {string.Join(", ", unmatched)}";
        AppendLog(line);
    }

    private void TxtCatalogSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_catalogEntries != null)
            RenderCatalog(_catalogEntries, TxtCatalogSearch.Text);
    }

    private void RenderCatalog(IReadOnlyList<CatalogEntry> all, string query)
    {
        CatalogPanel.Children.Clear();

        // ── Filter chip bar (always at top) ───────────────────────────────────
        CatalogPanel.Children.Add(BuildCatalogFilterBar(all));

        // ── Apply text + main + platform + category filters (all AND) ─────────
        IEnumerable<CatalogEntry> entries = string.IsNullOrWhiteSpace(query)
            ? all
            : GameCatalog.Search(all, query);

        // Main filter: All / Available / Official / Unofficial (mutually exclusive).
        // "Available" means the launcher itself can get you playing: released AND
        // automatable (capability != ManualSetup) — see IsLauncherPlayable.
        // Official-ness comes straight from official_games.txt (see ApplyOfficialList).
        switch (_catalogMainFilter)
        {
            case "available":  entries = entries.Where(e => e.IsLauncherPlayable); break;
            case "official":   entries = entries.Where(e => e.IsOfficial);         break;
            case "unofficial": entries = entries.Where(e => !e.IsOfficial);        break;
        }

        // Platform filter composes (AND) with the main filter.
        if (_catalogPlatformFilter != "all")
            entries = entries.Where(e =>
                GameCatalog.EntryHasPlatform(e, _catalogPlatformFilter));

        // Category filter composes (AND) with both.
        if (_catalogCategoryFilter != "all")
            entries = entries.Where(e => e.Category.Equals(
                _catalogCategoryFilter, StringComparison.OrdinalIgnoreCase));

        var list = entries.ToList();

        // ── Section split (honest install-capability semantics) ──────────────
        //   Available Now — the launcher can actually get you playing.
        //   Coming Soon   — plugin-gated integrations we are actively automating.
        //   Manual setup  — everything else (incl. Discord-only entries).
        var availableNow = new List<CatalogEntry>();
        var comingSoon   = new List<CatalogEntry>();
        var manualSetup  = new List<CatalogEntry>();
        foreach (var e in list)
        {
            if      (e.IsLauncherPlayable)      availableNow.Add(e);
            else if (IsPluginGatedComingSoon(e)) comingSoon.Add(e);
            else                                 manualSetup.Add(e);
        }

        if (availableNow.Count > 0)
        {
            CatalogPanel.Children.Add(BuildSectionHeader($"Available Now  ({availableNow.Count})"));
            foreach (var e in availableNow)
                CatalogPanel.Children.Add(BuildCatalogCard(e));
        }

        if (comingSoon.Count > 0)
        {
            CatalogPanel.Children.Add(BuildSectionHeader($"Coming Soon  ({comingSoon.Count})"));
            foreach (var e in comingSoon)
                CatalogPanel.Children.Add(BuildCatalogCard(e));
        }

        if (manualSetup.Count > 0)
        {
            CatalogPanel.Children.Add(BuildSectionHeader($"Manual setup  ({manualSetup.Count})"));
            foreach (var e in manualSetup)
                CatalogPanel.Children.Add(BuildCatalogCard(e));
        }

        // ── AP Ecosystem Tools ────────────────────────────────────────────────
        if (_catalogTools?.Count > 0)
        {
            IEnumerable<CatalogTool> tools = string.IsNullOrWhiteSpace(query)
                ? (IEnumerable<CatalogTool>)_catalogTools
                : _catalogTools.Where(t =>
                    t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    t.Description.Contains(query, StringComparison.OrdinalIgnoreCase));

            var toolList = tools.ToList();
            if (toolList.Count > 0)
            {
                CatalogPanel.Children.Add(BuildSectionHeader($"AP Ecosystem Tools  ({toolList.Count})"));
                foreach (var tool in toolList)
                    CatalogPanel.Children.Add(BuildToolCard(tool));
            }
        }
    }

    // ── Capability presentation (shared by Browse cards + detail page) ────────

    /// "COMING SOON" is reserved for integrations we are actively automating:
    /// a gated catalog status AND a compiled plugin in this launcher (the
    /// emulated trio). Every other unfinished entry is plain "Manual setup" —
    /// never a misleading coming-soon.
    private static bool IsPluginGatedComingSoon(CatalogEntry e)
        => e.Status == "coming_soon" && GameRegistry.Find(e.Id) != null;

    /// Capability label + one-sentence tooltip + tint color. Single source of
    /// truth for capability presentation — the Browse card pill and the detail
    /// page badge both render from this.
    private static (string Label, string Tip, Color Tint) CapabilityPresentation(CatalogEntry entry)
        => entry.InstallCapability switch
        {
            InstallCapability.AutoInstall => (
                "Auto-install",
                "The launcher downloads and installs everything — click Install and play.",
                Color.FromRgb(0x22, 0xC5, 0x5E)),
            InstallCapability.AutoMod => (
                "Auto-mod · you own the game",
                "You own or install the base game — the launcher downloads the Archipelago mod and applies it automatically.",
                Color.FromRgb(0xCC, 0xA8, 0x00)),
            InstallCapability.RomRequired => (
                "Bring your own ROM",
                "The launcher installs the emulator and the Archipelago mod — you supply the game ROM.",
                Color.FromRgb(0xF5, 0x9E, 0x0B)),
            _ => (
                "Manual setup required",
                "The launcher cannot automate this game yet — use the install guide and links to set it up yourself.",
                Color.FromRgb(0x8A, 0x90, 0xA8)),
        };

    /// Steam-like Browse card: art + name + platform/capability/FREE badges
    /// and a compact library toggle. Nothing else — every detail (description,
    /// credits, links, guides, actions) lives one click in, on the detail page.
    /// The WHOLE card is clickable and opens ShowCatalogDetail.
    private UIElement BuildCatalogCard(CatalogEntry entry)
    {
        var gold    = (Brush)FindResource("BrushAccent");
        var muted   = (Brush)FindResource("BrushMuted");
        var success = (Brush)FindResource("BrushSuccess");

        bool pluginGated = IsPluginGatedComingSoon(entry);

        // Emphasis follows the section semantics: full strength when the
        // launcher can get you playing, dimmed when gated, slightly muted for
        // manual-setup entries (Discord-only dimmest — no download link at all).
        double cardOpacity =
            entry.Status == "discord_only" ? 0.60 :
            pluginGated                    ? 0.75 :
            entry.IsLauncherPlayable       ? 1.0  :
            0.85;

        var normalBg     = Color.FromRgb(0x14, 0x17, 0x20);
        var normalBorder = Color.FromRgb(0x1E, 0x22, 0x33);
        var hoverBg      = Color.FromRgb(0x1C, 0x20, 0x30);        // sidebar-card hover feel
        var hoverBorder  = Color.FromArgb(0x66, 0xCC, 0xA8, 0x00); // accent @ ~40%

        var border = new Border
        {
            Width           = 244,
            Margin          = new Thickness(0, 0, 12, 12),
            Background      = new SolidColorBrush(normalBg),
            BorderBrush     = new SolidColorBrush(normalBorder),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Cursor          = Cursors.Hand,
            Opacity         = cardOpacity,
        };

        // Whole card opens the detail page. MouseLeftButtonDown matches the
        // filter chips elsewhere; the library toggle marks its press Handled
        // before it bubbles here, so toggling never opens the page.
        var entryCapture = entry;
        border.MouseLeftButtonDown += (_, _) => ShowCatalogDetail(entryCapture);
        border.MouseEnter += (_, _) =>
        {
            border.BorderBrush = new SolidColorBrush(hoverBorder);
            border.Background  = new SolidColorBrush(hoverBg);
        };
        border.MouseLeave += (_, _) =>
        {
            border.BorderBrush = new SolidColorBrush(normalBorder);
            border.Background  = new SolidColorBrush(normalBg);
        };

        var outer = new StackPanel();

        // ── Thumbnail strip (edge-to-edge at the top of the card) ─────────────
        // Source priority: explicit catalog URL → locally generated art probe
        // (Assets/Thumbs/<id>_thumb.png) so newly generated thumbnails light
        // up without touching catalog.json. Entries with no art get a uniform
        // monogram placeholder so the WrapPanel rows keep a coherent height.
        string thumbSource = entry.ThumbnailUrl;
        if (string.IsNullOrEmpty(thumbSource))
        {
            string probe = Path.Combine(AppContext.BaseDirectory,
                "Assets", "Thumbs", $"{entry.Id}_thumb.png");
            if (File.Exists(probe)) thumbSource = probe;
        }

        var thumbBorder = new Border
        {
            Height       = 128,
            Background   = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            ClipToBounds = true,
        };
        bool thumbLoaded = false;
        if (!string.IsNullOrEmpty(thumbSource))
        {
            try
            {
                // Local thumbnails go through the decode-once cache (P3-3);
                // http URLs keep WPF's async download path.
                BitmapImage? thumbBmp = thumbSource.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new BitmapImage(new Uri(thumbSource, UriKind.Absolute))
                    : LoadCachedBitmap(Path.IsPathRooted(thumbSource)
                        ? thumbSource
                        : Path.Combine(AppContext.BaseDirectory, thumbSource));
                if (thumbBmp != null)
                {
                    var img = new Image
                    {
                        Stretch           = Stretch.UniformToFill,
                        VerticalAlignment = VerticalAlignment.Center,
                        Source            = thumbBmp,
                    };
                    img.ImageFailed += (_, _) =>
                        thumbBorder.Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20));
                    thumbBorder.Child = img;
                    thumbLoaded = true;
                }
            }
            catch { /* fall through to the monogram placeholder */ }
        }
        if (!thumbLoaded)
        {
            thumbBorder.Child = new TextBlock
            {
                Text                = entry.DisplayName.Length > 0
                                          ? entry.DisplayName[..1].ToUpperInvariant()
                                          : "?",
                FontSize            = 44,
                FontWeight          = FontWeights.Bold,
                Foreground          = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
        }
        outer.Children.Add(thumbBorder);

        var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };

        // ── Title row: game name + compact library toggle (top-right) ─────────
        var titleRow = new DockPanel { LastChildFill = true };

        string entryId = entry.Id;
        bool   inLib   = LibraryStore.IsInLibrary(entryId);

        // Stubs (emulated games without check detection) and Discord-only games
        // are not addable to the library — show a distinctive label instead.
        var   entryPlugin   = GameRegistry.Find(entryId);
        bool  checksStub    = entryPlugin is Plugins.Emulated.EmulatorPlugin eStub && !eStub.ChecksImplemented;
        bool  discordOnly   = entry.Status == "discord_only";
        bool  notAddable    = checksStub || discordOnly;

        var libToggleText = new TextBlock
        {
            Text                = discordOnly ? "📢" : (checksStub ? "Soon" : (inLib ? "✓" : "+")),
            FontSize            = (checksStub && !discordOnly) ? 8 : 11,
            FontWeight          = FontWeights.SemiBold,
            Foreground          = notAddable ? new SolidColorBrush(Color.FromRgb(0x55, 0x5C, 0x7A))
                                             : (inLib ? success : muted),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        var libToggle = new Border
        {
            Width             = 24,
            Height            = 24,
            CornerRadius      = new CornerRadius(3),
            Background        = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            BorderBrush       = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x60)),
            BorderThickness   = new Thickness(1),
            Margin            = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Cursor            = notAddable ? Cursors.Arrow : Cursors.Hand,
            ToolTip           = discordOnly ? "Discord only — not available in the launcher"
                                           : (checksStub ? "Check detection not yet implemented for this game"
                                           : (inLib ? "Remove from Library" : "Add to Library")),
            Child             = libToggleText,
            Opacity           = notAddable ? 0.5 : 1.0,
        };
        // Swallow press AND release so the card click never fires from here.
        libToggle.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (notAddable) return;   // stubs and discord-only are not library-addable
            if (LibraryStore.IsInLibrary(entryId))
            {
                LibraryStore.Remove(entryId);
                libToggleText.Text       = "+";
                libToggleText.Foreground = muted;
                libToggle.ToolTip        = "Add to Library";
            }
            else
            {
                LibraryStore.Add(entryId);
                AchievementStore.Instance.EvaluateAll();   // librarian ladder
                libToggleText.Text       = "✓";
                libToggleText.Foreground = success;
                libToggle.ToolTip        = "Remove from Library";
            }
            RebuildGameList();
        };
        libToggle.MouseLeftButtonUp += (_, e) => e.Handled = true;
        if (!checksStub)
        {
            libToggle.MouseEnter += (_, _) => libToggle.BorderBrush = gold;
            libToggle.MouseLeave += (_, _) =>
                libToggle.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x60));
        }
        DockPanel.SetDock(libToggle, Dock.Right);
        titleRow.Children.Add(libToggle);

        titleRow.Children.Add(new TextBlock
        {
            Text         = entry.DisplayName,
            FontSize     = 14,
            FontWeight   = FontWeights.SemiBold,
            Foreground   = gold,
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(titleRow);

        // ── Badge row: platform + capability pill (+ COMING SOON / FREE) ──────
        // The capability pill is on EVERY card so the user always knows what
        // happens when they try to install; COMING SOON rides along only for
        // plugin-gated integrations — see IsPluginGatedComingSoon.
        var (capLabel, capTip, capTint) = CapabilityPresentation(entry);
        var badges = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

        Border MakeBadge(string text, Color tint, bool tintedBg, string? tooltip = null) => new()
        {
            Background        = tintedBg
                ? new SolidColorBrush(Color.FromArgb(0x25, tint.R, tint.G, tint.B))
                : new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            CornerRadius      = new CornerRadius(2),
            Padding           = new Thickness(5, 2, 5, 2),
            Margin            = new Thickness(0, 0, 4, 4),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = tooltip,
            Child = new TextBlock
            {
                Text       = text,
                FontSize   = 9,
                FontWeight = tintedBg ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = tintedBg ? new SolidColorBrush(tint) : muted,
            },
        };

        badges.Children.Add(MakeBadge(entry.PrimaryPlatform.ToUpperInvariant(),
                                      default, tintedBg: false));
        badges.Children.Add(MakeBadge(capLabel, capTint, tintedBg: true, capTip));
        if (pluginGated)
            badges.Children.Add(MakeBadge("COMING SOON",
                                          Color.FromRgb(0xF5, 0x9E, 0x0B), tintedBg: true));
        if (entry.Free)
            badges.Children.Add(MakeBadge("FREE",
                                          Color.FromRgb(0x22, 0xC5, 0x5E), tintedBg: true));

        stack.Children.Add(badges);
        outer.Children.Add(stack);

        border.Child        = outer;
        border.ClipToBounds = true;
        return border;
    }

    private static Border MakePill(string text, Brush fg)
        => new()
        {
            Background      = new SolidColorBrush(Color.FromArgb(0x18, 0x1E, 0x22, 0x33)),
            CornerRadius    = new CornerRadius(2),
            Padding         = new Thickness(5, 2, 5, 2),
            Margin          = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 9, Foreground = fg }
        };

    // ═══════════════════════════════════════════════════════════════════════════
    // Catalog game detail page
    // ═══════════════════════════════════════════════════════════════════════════

    private void BtnDetailBack_Click(object sender, RoutedEventArgs e)
    {
        PanelCatalogDetail.Visibility = Visibility.Collapsed;
        DetailContentPanel.Children.Clear();
    }

    private void ShowCatalogDetail(CatalogEntry entry)
    {
        var gold    = (Brush)FindResource("BrushAccent");
        var muted   = (Brush)FindResource("BrushMuted");
        var textBr  = (Brush)FindResource("BrushText");
        var success = (Brush)FindResource("BrushSuccess");

        TxtDetailTitle.Text = entry.DisplayName;
        DetailContentPanel.Children.Clear();

        // ── Hero area: title block + badges ──────────────────────────────────
        var heroPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        var titleRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        titleRow.Children.Add(new TextBlock
        {
            Text         = entry.DisplayName,
            FontSize     = 26,
            FontWeight   = FontWeights.Bold,
            Foreground   = gold,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 12, 0),
        });
        heroPanel.Children.Add(titleRow);

        // Author
        heroPanel.Children.Add(new TextBlock
        {
            Text       = $"by {entry.Author}",
            FontSize   = 13,
            Foreground = muted,
            Margin     = new Thickness(0, 0, 0, 10),
        });

        // Badge row: status, capability, type, platform, free
        var badgeRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };

        void AddBadge(string text, Brush fg, Brush? bg = null, string? tooltip = null)
        {
            badgeRow.Children.Add(new Border
            {
                Background   = bg ?? new SolidColorBrush(Color.FromArgb(0x30, 0x1E, 0x22, 0x33)),
                CornerRadius = new CornerRadius(3),
                Padding      = new Thickness(8, 3, 8, 3),
                Margin       = new Thickness(0, 0, 6, 6),
                ToolTip      = tooltip,
                Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = fg }
            });
        }

        // Status badge — honest semantics: AVAILABLE only when the launcher
        // can actually get you playing; COMING SOON only when plugin-gated.
        // Everything else lets the capability badge tell the story alone.
        if (entry.IsLauncherPlayable)
            AddBadge("AVAILABLE", success,
                     new SolidColorBrush(Color.FromArgb(0x25, 0x22, 0xC5, 0x5E)));
        else if (IsPluginGatedComingSoon(entry))
            AddBadge("COMING SOON", new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                     new SolidColorBrush(Color.FromArgb(0x25, 0xF5, 0x9E, 0x0B)));

        // Capability badge — same label/tooltip/tint as the Browse card pill.
        var (capLabel, capTip, capTint) = CapabilityPresentation(entry);
        AddBadge(capLabel.ToUpperInvariant(),
                 new SolidColorBrush(capTint),
                 new SolidColorBrush(Color.FromArgb(0x25, capTint.R, capTint.G, capTint.B)),
                 capTip);

        if (entry.PluginType == GamePluginType.Native)
            AddBadge("NATIVE PLUGIN", new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
                     new SolidColorBrush(Color.FromArgb(0x25, 0x22, 0xC5, 0x5E)));

        if (entry.Platforms.Length > 0)
            foreach (var p in entry.Platforms)
                AddBadge(p.ToUpperInvariant(), muted);

        if (entry.Free)
            AddBadge("FREE", new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
                     new SolidColorBrush(Color.FromArgb(0x25, 0x22, 0xC5, 0x5E)));

        if (entry.HintGame)
            AddBadge("HINT GAME", new SolidColorBrush(Color.FromRgb(0x77, 0x8B, 0xFF)),
                     new SolidColorBrush(Color.FromArgb(0x25, 0x77, 0x8B, 0xFF)));

        heroPanel.Children.Add(badgeRow);

        // ── "Get the game" action row — the page's primary actions ────────────
        // Purchase / free acquisition, the page's single library toggle, and
        // the installed chip when a registered plugin reports installed.
        var actionRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };

        if (entry.Free)
        {
            // Free game: green-tinted label-button. Clickable when we have a
            // destination (official site preferred, else the purchase link).
            string? freeTarget = !string.IsNullOrEmpty(entry.Links?.OfficialSite)
                ? entry.Links!.OfficialSite
                : entry.PurchaseUrl;
            var freeChip = new Border
            {
                Background        = new SolidColorBrush(Color.FromArgb(0x28, 0x22, 0xC5, 0x5E)),
                BorderBrush       = new SolidColorBrush(Color.FromArgb(0x60, 0x22, 0xC5, 0x5E)),
                BorderThickness   = new Thickness(1),
                CornerRadius      = new CornerRadius(3),
                Padding           = new Thickness(14, 7, 14, 7),
                Margin            = new Thickness(0, 0, 8, 8),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text       = string.IsNullOrEmpty(freeTarget) ? "Free game" : "Free — get it ↗",
                    FontSize   = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = success,
                },
            };
            if (!string.IsNullOrEmpty(freeTarget))
            {
                string ft = freeTarget!;
                freeChip.Cursor  = Cursors.Hand;
                freeChip.ToolTip = ft;
                freeChip.MouseLeftButtonUp += (_, _) => OpenUrl(ft);
            }
            actionRow.Children.Add(freeChip);
        }
        else if (!string.IsNullOrEmpty(entry.PurchaseUrl))
        {
            var buyBtn = new Button
            {
                Content           = "Get the game ↗",
                Style             = (Style)FindResource("BtnPlayStyle"),
                Padding           = new Thickness(18, 7, 18, 7),
                Margin            = new Thickness(0, 0, 8, 8),
                VerticalAlignment = VerticalAlignment.Center,
            };
            string purchaseUrl = entry.PurchaseUrl;
            buyBtn.Click += (_, _) => OpenUrl(purchaseUrl);
            actionRow.Children.Add(buyBtn);
        }

        // Library toggle — the only Add/Remove control on this page.
        bool isInLib = LibraryStore.IsInLibrary(entry.Id);
        var detailLibBtn = new Button
        {
            Content           = isInLib ? "✓ In Library" : "+ Add to Library",
            Style             = (Style)FindResource("BtnSecondaryStyle"),
            Padding           = new Thickness(14, 6, 14, 6),
            Margin            = new Thickness(0, 0, 8, 8),
            Foreground        = isInLib ? success : textBr,
            Cursor            = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        string detailId = entry.Id;
        detailLibBtn.Click += (_, _) =>
        {
            if (LibraryStore.IsInLibrary(detailId))
            {
                LibraryStore.Remove(detailId);
                detailLibBtn.Content    = "+ Add to Library";
                detailLibBtn.Foreground = textBr;
            }
            else
            {
                LibraryStore.Add(detailId);
                AchievementStore.Instance.EvaluateAll();   // librarian ladder
                detailLibBtn.Content    = "✓ In Library";
                detailLibBtn.Foreground = success;
            }
            RebuildGameList();
        };
        actionRow.Children.Add(detailLibBtn);

        if (entry.IsInstalled)
        {
            actionRow.Children.Add(new Border
            {
                Background        = new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0xAF, 0x50)),
                CornerRadius      = new CornerRadius(3),
                Padding           = new Thickness(12, 6, 12, 6),
                Margin            = new Thickness(0, 0, 8, 8),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "✓ Installed", FontSize = 12, Foreground = success },
            });
        }

        heroPanel.Children.Add(actionRow);

        DetailContentPanel.Children.Add(heroPanel);

        // ── Divider ───────────────────────────────────────────────────────────
        DetailContentPanel.Children.Add(new Border
        {
            Height          = 1,
            Background      = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            Margin          = new Thickness(0, 0, 0, 20),
        });

        // ── About (description + which AP world powers it) ────────────────────
        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            bool hasApWorldLine = !string.IsNullOrEmpty(entry.ApWorldName);
            DetailContentPanel.Children.Add(BuildDetailSectionLabel("About"));
            DetailContentPanel.Children.Add(new TextBlock
            {
                Text         = entry.Description,
                FontSize     = 13,
                Foreground   = textBr,
                TextWrapping = TextWrapping.Wrap,
                LineHeight   = 20,
                Margin       = new Thickness(0, 4, 0, hasApWorldLine ? 6 : 20),
            });
            if (hasApWorldLine)
            {
                DetailContentPanel.Children.Add(new TextBlock
                {
                    Text         = $"Archipelago world: {entry.ApWorldName}",
                    FontSize     = 11,
                    Foreground   = muted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 0, 0, 20),
                });
            }
        }

        // ── Capability explanation box ─────────────────────────────────────────
        // Spells out exactly what clicking Install will and won't do for THIS
        // entry — same sentences as the capability tooltip, plus the legal-ROM
        // reminder for emulated games.
        {
            string explain = capTip;
            if (entry.InstallCapability == InstallCapability.RomRequired)
                explain += " You must provide your own legally-obtained ROM file.";

            var capStack = new StackPanel();
            capStack.Children.Add(new TextBlock
            {
                Text       = "INSTALLATION",
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(capTint),
                Margin     = new Thickness(0, 0, 0, 4),
            });
            capStack.Children.Add(new TextBlock
            {
                Text         = explain,
                FontSize     = 12,
                Foreground   = textBr,
                TextWrapping = TextWrapping.Wrap,
                LineHeight   = 18,
            });
            DetailContentPanel.Children.Add(new Border
            {
                Background      = new SolidColorBrush(Color.FromArgb(0x12, capTint.R, capTint.G, capTint.B)),
                BorderBrush     = new SolidColorBrush(Color.FromArgb(0x55, capTint.R, capTint.G, capTint.B)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(14, 10, 14, 10),
                Margin          = new Thickness(0, 0, 0, 20),
                Child           = capStack,
            });
        }

        // ── Credits (the AP-world / mod author renders prominently) ───────────
        if (entry.Credits.Length > 0)
        {
            DetailContentPanel.Children.Add(BuildDetailSectionLabel("Credits"));
            var creditsPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 20) };
            foreach (var credit in entry.Credits)
            {
                bool isModCredit =
                    credit.Role.Contains("AP world", StringComparison.OrdinalIgnoreCase) ||
                    credit.Role.Contains("mod",      StringComparison.OrdinalIgnoreCase);

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(0, 0, 0, 4),
                };
                row.Children.Add(new TextBlock
                {
                    Text       = credit.Role + ": ",
                    FontSize   = 12,
                    Foreground = muted,
                    MinWidth   = 130,
                });
                if (!string.IsNullOrEmpty(credit.Url))
                {
                    var link = new TextBlock
                    {
                        Text            = credit.Name,
                        FontSize        = 12,
                        FontWeight      = isModCredit ? FontWeights.Bold : FontWeights.Normal,
                        Foreground      = gold,
                        Cursor          = Cursors.Hand,
                        TextDecorations = TextDecorations.Underline,
                    };
                    string creditUrl = credit.Url;
                    link.MouseLeftButtonUp += (_, _) => OpenUrl(creditUrl);
                    row.Children.Add(link);
                }
                else
                {
                    row.Children.Add(new TextBlock
                    {
                        Text       = credit.Name,
                        FontSize   = 12,
                        FontWeight = isModCredit ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = textBr,
                    });
                }
                creditsPanel.Children.Add(row);
            }
            DetailContentPanel.Children.Add(creditsPanel);
        }
        else if (entry.IsCommunityGame && !string.IsNullOrEmpty(entry.InstallUrl))
        {
            // Community-list entry with no structured credits — point at the
            // project page instead of pretending we know the author.
            DetailContentPanel.Children.Add(BuildDetailSectionLabel("Credits"));
            var fallbackRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 6, 0, 20),
            };
            fallbackRow.Children.Add(new TextBlock
            {
                Text       = "Mod author: ",
                FontSize   = 12,
                Foreground = muted,
            });
            var projectLink = new TextBlock
            {
                Text            = "see the project page",
                FontSize        = 12,
                Foreground      = gold,
                Cursor          = Cursors.Hand,
                TextDecorations = TextDecorations.Underline,
            };
            string projectUrl = entry.InstallUrl!;
            projectLink.MouseLeftButtonUp += (_, _) => OpenUrl(projectUrl);
            fallbackRow.Children.Add(projectLink);
            DetailContentPanel.Children.Add(fallbackRow);
        }

        // ── Details meta: typical run length, players, category ───────────────
        // "Typical run length" is a property of the GAME, clearly labeled so it
        // can never read as the user's own playtime (which is tracked for real).
        var metaRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 20) };
        if (entry.EstPlaytimeMin > 0)
        {
            string t = entry.EstPlaytimeMin >= 60
                ? $"Typical run length: ~{entry.EstPlaytimeMin / 60}h"
                : $"Typical run length: ~{entry.EstPlaytimeMin}m";
            metaRow.Children.Add(MakePill(t, muted));
        }
        if (!string.IsNullOrEmpty(entry.PlayerCount) && entry.PlayerCount != "1+")
            metaRow.Children.Add(MakePill($"👥 {entry.PlayerCount} players", muted));
        if (!string.IsNullOrEmpty(entry.Category))
            metaRow.Children.Add(MakePill(entry.Category, muted));
        if (metaRow.Children.Count > 0)
        {
            DetailContentPanel.Children.Add(BuildDetailSectionLabel("Details"));
            DetailContentPanel.Children.Add(metaRow);
        }

        // ── Links ─────────────────────────────────────────────────────────────
        // No purchase link here — getting the game is the action row's job
        // (top of the page), and duplicating it would just add noise.
        var links = entry.Links;
        bool hasAnyLink = links != null && (
            !string.IsNullOrEmpty(links.ApGamePage) ||
            !string.IsNullOrEmpty(links.OfficialSite) ||
            !string.IsNullOrEmpty(links.ApGithub) ||
            !string.IsNullOrEmpty(links.ApDiscord) ||
            !string.IsNullOrEmpty(links.GameDiscord)) ||
            !string.IsNullOrEmpty(entry.InstallUrl) ||
            !string.IsNullOrEmpty(entry.VideoUrl);

        if (hasAnyLink)
        {
            DetailContentPanel.Children.Add(BuildDetailSectionLabel("Links"));
            var linksPanel = new WrapPanel { Margin = new Thickness(0, 8, 0, 20) };

            void AddLink(string label, string url)
            {
                var btn = new Button
                {
                    Content = label,
                    Style   = (Style)FindResource("BtnSecondaryStyle"),
                    Padding = new Thickness(12, 5, 12, 5),
                    Margin  = new Thickness(0, 0, 8, 8),
                    Cursor  = Cursors.Hand,
                };
                btn.Click += (_, _) => OpenUrl(url);
                linksPanel.Children.Add(btn);
            }

            if (!string.IsNullOrEmpty(links?.ApGamePage))       AddLink("AP Page ↗",        links.ApGamePage!);
            if (!string.IsNullOrEmpty(links?.OfficialSite))     AddLink("Official Site ↗",   links.OfficialSite!);
            if (!string.IsNullOrEmpty(links?.ApGithub))         AddLink("GitHub ↗",          links.ApGithub!);
            if (!string.IsNullOrEmpty(links?.ApDiscord))        AddLink("AP Discord ↗",      links.ApDiscord!);
            if (!string.IsNullOrEmpty(links?.GameDiscord))      AddLink("Game Discord ↗",    links.GameDiscord!);
            if (!string.IsNullOrEmpty(entry.InstallUrl))        AddLink("Download / Install ↗", entry.InstallUrl!);
            if (!string.IsNullOrEmpty(entry.VideoUrl))          AddLink("▶ Trailer", entry.VideoUrl!);

            DetailContentPanel.Children.Add(linksPanel);
        }

        // ── Screenshots ───────────────────────────────────────────────────────
        if (entry.ScreenshotUrls.Length > 0)
        {
            DetailContentPanel.Children.Add(BuildDetailSectionLabel("Screenshots"));
            var screenshotRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 20),
            };
            foreach (var screenshotUrl in entry.ScreenshotUrls.Take(4))
            {
                var frame = new Border
                {
                    Width        = 180,
                    Height       = 101,
                    Background   = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
                    CornerRadius = new CornerRadius(4),
                    Margin       = new Thickness(0, 0, 10, 0),
                    ClipToBounds = true,
                };
                try
                {
                    var img = new Image
                    {
                        Stretch = Stretch.UniformToFill,
                        Source  = new BitmapImage(new Uri(screenshotUrl, UriKind.Absolute)),
                        Cursor  = Cursors.Hand,
                    };
                    string urlCapture = screenshotUrl;
                    img.MouseLeftButtonUp += (_, _) => OpenUrl(urlCapture);
                    img.ImageFailed += (_, _) => frame.Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20));
                    frame.Child = img;
                }
                catch { /* leave placeholder */ }
                screenshotRow.Children.Add(frame);
            }
            var screenshotScroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 8, 0, 20),
            };
            screenshotScroller.Content = screenshotRow;
            DetailContentPanel.Children.Add(screenshotScroller);
        }

        // ── Tags ──────────────────────────────────────────────────────────────
        if (entry.Tags.Length > 0)
        {
            DetailContentPanel.Children.Add(BuildDetailSectionLabel("Tags"));
            var tagWrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 20) };
            foreach (var tag in entry.Tags)
            {
                tagWrap.Children.Add(new Border
                {
                    Background   = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x20)),
                    BorderBrush  = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding      = new Thickness(8, 3, 8, 3),
                    Margin       = new Thickness(0, 0, 6, 6),
                    Child = new TextBlock { Text = tag, FontSize = 11, Foreground = muted }
                });
            }
            DetailContentPanel.Children.Add(tagWrap);
        }

        // ── Install guide ─────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(entry.InstallGuide))
        {
            DetailContentPanel.Children.Add(BuildDetailSectionLabel("Install Guide"));
            var guideBtn = new Button
            {
                Content = "📖 Open Install Guide",
                Style   = (Style)FindResource("BtnSecondaryStyle"),
                Padding = new Thickness(14, 6, 14, 6),
                Margin  = new Thickness(0, 8, 0, 20),
                Cursor  = Cursors.Hand,
            };
            string guideName  = entry.DisplayName;
            string guideText  = entry.InstallGuide;
            guideBtn.Click += (_, _) => OpenInstallGuide($"{guideName} — Install Guide", guideText);
            DetailContentPanel.Children.Add(guideBtn);
        }

        // (No bottom Library section: the page's single Add/Remove toggle
        //  lives in the action row at the top — see actionRow above.)

        // Show the panel with a quick fade-in
        FadeInPage(PanelCatalogDetail);
    }

    private static TextBlock BuildDetailSectionLabel(string text)
        => new()
        {
            Text       = text.ToUpperInvariant(),
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0x58, 0x78)),
            Margin     = new Thickness(0, 0, 0, 2),
        };

    /// <summary>
    /// Full-width section divider for use inside the CatalogPanel WrapPanel.
    /// Width=4000 forces the element onto its own row regardless of panel width.
    /// </summary>
    private UIElement BuildSectionHeader(string title)
    {
        var muted = (Brush)FindResource("BrushMuted");

        return new Border
        {
            // Force a new row in the WrapPanel — any width larger than the viewport works.
            Width           = 4000,
            Margin          = new Thickness(0, 20, 0, 6),
            Padding         = new Thickness(2, 0, 0, 8),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new TextBlock
            {
                Text       = title.ToUpperInvariant(),
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = muted,
            }
        };
    }

    /// <summary>
    /// Full-width filter chip bar at the top of the catalog panel.
    /// Width=4000 forces it onto its own row in the WrapPanel (same trick as section headers).
    /// Clicking a chip updates the active status/category filter and immediately re-renders.
    /// </summary>
    private UIElement BuildCatalogFilterBar(IReadOnlyList<CatalogEntry> all)
    {
        var muted = (Brush)FindResource("BrushMuted");

        var container = new Border
        {
            Width  = 4000,
            Margin = new Thickness(0, 0, 0, 4),
        };

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };

        // ── Main filter chips (All / Available / Official / Unofficial) ───────
        // Mutually exclusive. "Official" / "Unofficial" are driven by
        // official_games.txt (see GameCatalog.ApplyOfficialList); they show
        // entries regardless of release status. Category chips compose on top.
        void AddMainChip(string label, string value, byte r, byte g, byte b)
        {
            bool active = _catalogMainFilter == value;
            var chip = new Border
            {
                Margin          = new Thickness(0, 0, 6, 6),
                Padding         = new Thickness(10, 4, 10, 4),
                CornerRadius    = new CornerRadius(12),
                Background      = active
                    ? new SolidColorBrush(Color.FromArgb(0x30, r, g, b))
                    : new SolidColorBrush(Color.FromArgb(0x10, 0x1E, 0x22, 0x33)),
                BorderBrush     = active
                    ? (Brush)new SolidColorBrush(Color.FromRgb(r, g, b))
                    : new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x45)),
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand,
            };
            chip.Child = new TextBlock
            {
                Text       = label,
                FontSize   = 11,
                Foreground = active
                    ? (Brush)new SolidColorBrush(Color.FromRgb(r, g, b))
                    : muted,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            };
            string v = value;
            chip.MouseLeftButtonDown += (_, _) =>
            {
                _catalogMainFilter = v;
                RenderCatalog(_catalogEntries!, TxtCatalogSearch.Text);
            };
            wrap.Children.Add(chip);
        }

        AddMainChip("All",        "all",        0xB0, 0xB8, 0xFF);
        AddMainChip("Available",  "available",  0x22, 0xC5, 0x5E);
        AddMainChip("Official",   "official",   0xF5, 0xC8, 0x42);
        AddMainChip("Unofficial", "unofficial", 0x9B, 0x8C, 0xF5);

        void AddDivider() => wrap.Children.Add(new Border
        {
            Width             = 1,
            Height            = 22,
            Background        = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x45)),
            Margin            = new Thickness(4, 0, 10, 6),
            VerticalAlignment = VerticalAlignment.Bottom,
        });

        AddDivider();

        // ── Platform chips (normalized, ≥ PlatformChipMinGames games) ────────
        // Composes (AND) with the main filter and the category filter. Rarer
        // platforms get no chip — the text search box matches Platforms too.
        void AddPlatformChip(string label, string value)
        {
            bool active = _catalogPlatformFilter.Equals(value, StringComparison.OrdinalIgnoreCase);
            var chip = new Border
            {
                Margin          = new Thickness(0, 0, 6, 6),
                Padding         = new Thickness(10, 4, 10, 4),
                CornerRadius    = new CornerRadius(12),
                Background      = active
                    ? new SolidColorBrush(Color.FromArgb(0x30, 0x2D, 0xD4, 0xBF))
                    : new SolidColorBrush(Color.FromArgb(0x10, 0x1E, 0x22, 0x33)),
                BorderBrush     = active
                    ? (Brush)new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF))
                    : new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x45)),
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand,
            };
            chip.Child = new TextBlock
            {
                Text       = label,
                FontSize   = 11,
                Foreground = active
                    ? (Brush)new SolidColorBrush(Color.FromRgb(0x5E, 0xEA, 0xD4))
                    : muted,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            };
            string v = value;
            chip.MouseLeftButtonDown += (_, _) =>
            {
                _catalogPlatformFilter = v;
                RenderCatalog(_catalogEntries!, TxtCatalogSearch.Text);
            };
            wrap.Children.Add(chip);
        }

        var platforms = GameCatalog.CommonPlatforms(all, PlatformChipMinGames);
        AddPlatformChip("All", "all");
        foreach (var p in platforms)
            AddPlatformChip(p, p);

        // An active filter for a platform that fell below the chip threshold
        // (e.g. reached via an earlier, larger catalog) must stay visible so
        // the user can see it and clear it.
        if (_catalogPlatformFilter != "all" &&
            !platforms.Contains(_catalogPlatformFilter, StringComparer.OrdinalIgnoreCase))
            AddPlatformChip(_catalogPlatformFilter, _catalogPlatformFilter);

        AddDivider();

        // ── Category chips (top 10, alphabetical) ────────────────────────────
        void AddCategoryChip(string label, string value)
        {
            bool active = _catalogCategoryFilter == value;
            var chip = new Border
            {
                Margin          = new Thickness(0, 0, 6, 6),
                Padding         = new Thickness(10, 4, 10, 4),
                CornerRadius    = new CornerRadius(12),
                Background      = active
                    ? new SolidColorBrush(Color.FromArgb(0x30, 0x63, 0x66, 0xF1))
                    : new SolidColorBrush(Color.FromArgb(0x10, 0x1E, 0x22, 0x33)),
                BorderBrush     = active
                    ? (Brush)new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1))
                    : new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x45)),
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand,
            };
            chip.Child = new TextBlock
            {
                Text       = label,
                FontSize   = 11,
                Foreground = active
                    ? (Brush)new SolidColorBrush(Color.FromRgb(0x9B, 0x9E, 0xFF))
                    : muted,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            };
            string v = value;
            chip.MouseLeftButtonDown += (_, _) =>
            {
                _catalogCategoryFilter = v;
                RenderCatalog(_catalogEntries!, TxtCatalogSearch.Text);
            };
            wrap.Children.Add(chip);
        }

        AddCategoryChip("All", "all");
        foreach (var cat in GameCatalog.Categories(all).Take(10))
            AddCategoryChip(cat, cat);

        container.Child = wrap;
        return container;
    }

    /// <summary>
    /// Catalog card for an AP ecosystem tool (tracker, client, utility, etc.).
    /// Same width as a game card so it fits naturally in the WrapPanel grid.
    /// </summary>
    private UIElement BuildToolCard(CatalogTool tool)
    {
        var muted  = (Brush)FindResource("BrushMuted");
        var textBr = (Brush)FindResource("BrushText");

        // Badge color by tool type
        Brush typeBadgeBrush = tool.Type switch
        {
            "tracker" => new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1)), // indigo
            "client"  => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)), // green
            "social"  => new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7)), // purple
            "mobile"  => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), // amber
            "hint"    => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), // red
            _         => muted,                                                  // utility / default
        };

        var border = new Border
        {
            Width           = 244,
            Margin          = new Thickness(0, 0, 12, 12),
            Background      = new SolidColorBrush(Color.FromRgb(0x10, 0x13, 0x1C)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
        };

        var stack = new StackPanel { Margin = new Thickness(14) };

        // Type badge
        var typeBadge = new Border
        {
            Background      = new SolidColorBrush(Color.FromArgb(0x20, 0x63, 0x66, 0xF1)),
            CornerRadius    = new CornerRadius(2),
            Padding         = new Thickness(6, 2, 6, 2),
            Margin          = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        typeBadge.Child = new TextBlock
        {
            Text       = tool.Type.ToUpperInvariant(),
            FontSize   = 9,
            Foreground = typeBadgeBrush,
            FontWeight = FontWeights.SemiBold,
        };
        stack.Children.Add(typeBadge);

        // Name
        stack.Children.Add(new TextBlock
        {
            Text         = tool.Name,
            FontSize     = 13,
            FontWeight   = FontWeights.SemiBold,
            Foreground   = textBr,
            TextWrapping = TextWrapping.Wrap,
        });

        // Description (truncated)
        string descText = tool.Description.Length > 110
            ? tool.Description[..107] + "..."
            : tool.Description;
        stack.Children.Add(new TextBlock
        {
            Text         = descText,
            FontSize     = 11,
            Foreground   = textBr,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 6, 0, 10),
            Opacity      = 0.7,
        });

        // URL button (if available)
        if (!string.IsNullOrEmpty(tool.Url))
        {
            var btn = new Button
            {
                Content = "Visit →",
                Style   = (Style)FindResource("BtnSecondaryStyle"),
                Padding = new Thickness(12, 5, 12, 5),
            };
            string url = tool.Url;
            btn.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(url)
                        { UseShellExecute = true });
                }
                catch { /* ignore */ }
            };
            stack.Children.Add(btn);
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text       = "Discord / community link",
                FontSize   = 10,
                Foreground = muted,
                Opacity    = 0.6,
            });
        }

        border.Child = stack;
        return border;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // News feed
    // ══════════════════════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════════════════════
    // Progression tab
    // ══════════════════════════════════════════════════════════════════════════

    private string _progressionCategory = "summary"; // "summary" | "locations" | "received" | "sent" | "hints" | "player:N"

    private void RefreshProgressionPanel()
    {
        if (!CheckAccess()) { Dispatcher.Invoke(RefreshProgressionPanel); return; }

        var all      = _tracker.All;
        var players  = _apClient?.Players ?? Array.Empty<ApNetworkPlayer>();
        var muted    = (Brush)FindResource("BrushMuted");
        var gold     = (Brush)FindResource("BrushAccent");
        var success  = (Brush)FindResource("BrushSuccess");
        var fg       = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));

        // ── Category sidebar ─────────────────────────────────────────────────
        // (No achievements category — the global Achievements page owns the
        //  full list now; per spec §4 a game's pages only keep the Overview
        //  teaser.)
        ProgressionCategoryPanel.Children.Clear();
        string locLabel = _locationTracker.Total > 0
            ? $"📍 Locations  ({_locationTracker.Checked}/{_locationTracker.Total})"
            : "📍 Locations";
        string hintLabel = _hints.Count > 0
            ? $"💡 Hints  ({_hints.Count})"
            : "💡 Hints";
        var categories = new[]
        {
            ("summary",      "📊 Summary"),
            ("locations",    locLabel),
            ("received",     $"📥 Received  ({all.Count(x => x.IsForMe)})"),
            ("sent",         $"📤 Sent  ({all.Count(x => x.IFoundIt && !x.IsForMe)})"),
            ("hints",        hintLabel),
        };
        foreach (var (key, label) in categories)
        {
            bool active = _progressionCategory == key;
            var item = new Border
            {
                Padding      = new Thickness(16, 8, 16, 8),
                Background   = active
                    ? new SolidColorBrush(Color.FromArgb(0x20, 0xE8, 0xA0, 0x18))
                    : Brushes.Transparent,
                Cursor       = Cursors.Hand,
            };
            item.Child = new TextBlock
            {
                Text       = label,
                FontSize   = 12,
                Foreground = active ? gold : muted,
            };
            string capturedKey = key;
            item.MouseLeftButtonDown += (_, _) =>
            {
                _progressionCategory = capturedKey;
                RefreshProgressionPanel();
            };
            ProgressionCategoryPanel.Children.Add(item);
        }

        // ── Players sidebar ──────────────────────────────────────────────────
        ProgressionPlayersPanel.Children.Clear();
        foreach (var p in players.OrderBy(p => p.Slot))
        {
            string playerKey  = $"player:{p.Slot}";
            bool   isSelected = _progressionCategory == playerKey;
            int    received   = all.Count(x => x.ReceiverSlot == p.Slot);
            int    sent       = all.Count(x => x.SenderSlot   == p.Slot && x.ReceiverSlot != p.Slot);

            var playerCard = new Border
            {
                Padding    = new Thickness(16, 6, 16, 6),
                Background = isSelected
                    ? new SolidColorBrush(Color.FromArgb(0x20, 0xE8, 0xA0, 0x18))
                    : Brushes.Transparent,
                Cursor     = Cursors.Hand,
            };
            var pstack = new StackPanel();
            pstack.Children.Add(new TextBlock
            {
                Text       = p.Alias.Length > 0 ? p.Alias : p.Name,
                FontSize   = 12,
                Foreground = isSelected ? gold : fg,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
            });
            var pSub = new StackPanel { Orientation = Orientation.Horizontal };
            pSub.Children.Add(new TextBlock
                { Text = p.Game, FontSize = 9, Foreground = muted });
            if (received > 0 || sent > 0)
                pSub.Children.Add(new TextBlock
                    { Text = $"  {received}↓ {sent}↑", FontSize = 9, Foreground = muted, Opacity = 0.7 });
            pstack.Children.Add(pSub);
            playerCard.Child = pstack;

            int capturedSlot = p.Slot;
            playerCard.MouseLeftButtonDown += (_, _) =>
            {
                _progressionCategory = isSelected ? "summary" : $"player:{capturedSlot}";
                RefreshProgressionPanel();
            };
            ProgressionPlayersPanel.Children.Add(playerCard);
        }

        // ── Main content area ─────────────────────────────────────────────────
        ProgressionContentPanel.Children.Clear();

        bool connected = _apClient?.State == ApConnectionState.Connected;

        // Location/hint views work regardless of connection
        // (they show appropriate empty states when offline)
        if (!connected &&
            _progressionCategory is not "locations" and
                                    not "hints"     &&
            !_progressionCategory.StartsWith("player:"))
        {
            if (_progressionCategory == "summary")
            {
                // Show all-time stats even offline
                RenderProgressionSummary(all, players, fg, muted, gold, success);
            }
            else
            {
                ProgressionContentPanel.Children.Add(new TextBlock
                {
                    Text         = "Connect to an Archipelago server and start a session to see this data.",
                    FontSize     = 13,
                    Foreground   = muted,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity      = 0.7,
                });
            }
            return;
        }

        switch (_progressionCategory)
        {
            case "summary":
                RenderProgressionSummary(all, players, fg, muted, gold, success);
                break;
            case "locations":
                RenderLocationTracker(fg, muted, gold, success);
                break;
            case "received":
                RenderProgressionItemList(
                    all.Where(x => x.IsForMe).ToList(), "Items Received", fg, muted);
                break;
            case "sent":
                RenderProgressionItemList(
                    all.Where(x => x.IFoundIt && !x.IsForMe).ToList(), "Items Sent to Others", fg, muted);
                break;
            case "hints":
                RenderHints(fg, muted, gold, success);
                break;
            default:
                // "player:N" — per-player session overview
                if (_progressionCategory.StartsWith("player:") &&
                    int.TryParse(_progressionCategory[7..], out int slot))
                {
                    var player = players.FirstOrDefault(p => p.Slot == slot);
                    string playerName = player?.Alias is { Length: > 0 } a ? a
                                     : player?.Name ?? $"Slot {slot}";
                    RenderPlayerSessionView(slot, playerName, all, fg, muted, gold, success);
                }
                break;
        }
    }

    private void RenderPlayerSessionView(
        int slot, string playerName,
        IReadOnlyList<TrackedItem> all,
        Brush fg, Brush muted, Brush gold, Brush success)
    {
        // Header
        ProgressionContentPanel.Children.Add(new TextBlock
        {
            Text       = $"👤  {playerName}",
            FontSize   = 16, FontWeight = FontWeights.SemiBold,
            Foreground = fg, Margin = new Thickness(0, 0, 0, 16),
        });

        // Stat tiles: received, sent, progression items
        var received = all.Where(x => x.ReceiverSlot == slot).ToList();
        var sent     = all.Where(x => x.SenderSlot   == slot && x.ReceiverSlot != slot).ToList();
        int progCount = received.Count(x => (x.ItemFlags & 0b001) != 0);

        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 20) };
        foreach (var (icon, label, val) in new[]
        {
            ("📥", "Received",    received.Count.ToString()),
            ("📤", "Sent",        sent.Count.ToString()),
            ("⭐", "Progression", progCount.ToString()),
        })
        {
            var tile = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0x1E, 0x22, 0x33)),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12, 10, 12, 10),
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = $"{icon}  {label}", FontSize = 10, Foreground = muted });
            sp.Children.Add(new TextBlock
                { Text = val, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = gold, Margin = new Thickness(0, 4, 0, 0) });
            tile.Child = sp;
            grid.Children.Add(tile);
        }
        ProgressionContentPanel.Children.Add(grid);

        // Items received by this player (most recent first, up to 40)
        if (received.Count > 0)
        {
            ProgressionContentPanel.Children.Add(new TextBlock
            {
                Text = "ITEMS RECEIVED", FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
            });
            foreach (var item in received.OrderByDescending(x => x.Timestamp).Take(40))
            {
                Brush itemFg = (item.ItemFlags & 0b001) != 0 ? gold
                             : (item.ItemFlags & 0b010) != 0 ? new SolidColorBrush(Color.FromRgb(0x77, 0x8B, 0xFF))
                             : (item.ItemFlags & 0b100) != 0 ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
                             : muted;
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock
                    { Text = item.ItemName, FontSize = 11, Foreground = itemFg });
                var locTb = new TextBlock
                    { Text = $"  ← {item.SenderName}", FontSize = 10, Foreground = muted };
                DockPanel.SetDock(locTb, Dock.Right);
                row.Children.Add(locTb);
                ProgressionContentPanel.Children.Add(row);
            }
            if (received.Count > 40)
                ProgressionContentPanel.Children.Add(new TextBlock
                    { Text = $"  … and {received.Count - 40} more", FontSize = 10, Foreground = muted, Margin = new Thickness(0, 4, 0, 0) });
        }
    }

    private void RenderProgressionSummary(
        IReadOnlyList<TrackedItem> all,
        IReadOnlyList<ApNetworkPlayer> players,
        Brush fg, Brush muted, Brush gold, Brush success)
    {
        var received   = all.Count(x => x.IsForMe);
        var sent       = all.Count(x => x.IFoundIt && !x.IsForMe);
        var sessionAge = _sessionStart == default
            ? TimeSpan.Zero
            : DateTimeOffset.Now - _sessionStart;

        string FormatDuration(TimeSpan t) => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes}m"
            : $"{t.Minutes}m {t.Seconds}s";

        // Stat tiles
        var stats = new[]
        {
            ("📥", "Items received", received.ToString()),
            ("📤", "Items sent",     sent.ToString()),
            ("👥", "Players",        players.Count.ToString()),
            ("⏱",  "Session time",  FormatDuration(sessionAge)),
        };

        var tileGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 24) };
        foreach (var (icon, label, value) in stats)
        {
            var tile = new Border
            {
                Background   = new SolidColorBrush(Color.FromArgb(0x18, 0x1E, 0x22, 0x33)),
                CornerRadius = new CornerRadius(4),
                Margin       = new Thickness(0, 0, 8, 8),
                Padding      = new Thickness(14, 10, 14, 10),
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
                { Text = $"{icon}  {label}", FontSize = 10, Foreground = muted });
            sp.Children.Add(new TextBlock
                { Text = value, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = gold, Margin = new Thickness(0, 4, 0, 0) });
            tile.Child = sp;
            tileGrid.Children.Add(tile);
        }
        ProgressionContentPanel.Children.Add(tileGrid);

        // Per-player item exchange breakdown
        if (players.Count > 1)
        {
            ProgressionContentPanel.Children.Add(new TextBlock
            {
                Text = "PLAYER ACTIVITY", FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = muted, Margin = new Thickness(0, 0, 0, 10),
            });

            foreach (var p in players.OrderBy(x => x.Slot))
            {
                int theyReceived = all.Count(x => x.ReceiverSlot == p.Slot);
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameTb = new TextBlock
                {
                    Text              = p.Alias.Length > 0 ? p.Alias : p.Name,
                    FontSize          = 12,
                    Foreground        = fg,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(nameTb, 0);

                var bar = new Border
                {
                    Background      = new SolidColorBrush(Color.FromArgb(0x30, 0x1E, 0x22, 0x33)),
                    Height          = 6,
                    CornerRadius    = new CornerRadius(3),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                int maxReceived = players.Max(pl => all.Count(x => x.ReceiverSlot == pl.Slot));
                if (maxReceived > 0)
                {
                    double pct = (double)theyReceived / maxReceived;
                    var fill = new Border
                    {
                        Background      = new SolidColorBrush(Color.FromRgb(0x44, 0x88, 0xFF)),
                        CornerRadius    = new CornerRadius(3),
                        Width           = double.NaN,
                        HorizontalAlignment = HorizontalAlignment.Left,
                    };
                    fill.Loaded += (_, _) =>
                    {
                        double parentWidth = bar.ActualWidth > 0 ? bar.ActualWidth : 200;
                        fill.Width = parentWidth * pct;
                    };
                    bar.Child = fill;
                }
                Grid.SetColumn(bar, 1);

                var countTb = new TextBlock
                {
                    Text              = theyReceived.ToString(),
                    FontSize          = 11,
                    Foreground        = muted,
                    Margin            = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(countTb, 2);

                row.Children.Add(nameTb);
                row.Children.Add(bar);
                row.Children.Add(countTb);
                ProgressionContentPanel.Children.Add(row);
            }
        }
    }

    private void RenderProgressionItemList(
        List<TrackedItem> items, string header,
        Brush fg, Brush muted)
    {
        if (items.Count == 0)
        {
            ProgressionContentPanel.Children.Add(new TextBlock
            {
                Text = "No items yet.", FontSize = 12, Foreground = muted,
            });
            return;
        }

        ProgressionContentPanel.Children.Add(new TextBlock
        {
            Text = header.ToUpperInvariant(), FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 0, 0, 10),
        });

        foreach (var item in items.OrderByDescending(x => x.Timestamp).Take(100))
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock
            {
                Text = item.ItemName.Length > 0 ? item.ItemName : $"#{item.ItemId}",
                FontSize = 12, Foreground = fg,
            });
            row.Children.Add(new TextBlock
            {
                Text = item.IsForMe
                    ? $"from {item.SenderName}  ·  {item.LocationName}"
                    : $"→ {item.ReceiverName}  ·  {item.LocationName}",
                FontSize = 10, Foreground = muted,
            });
            ProgressionContentPanel.Children.Add(row);
        }

        if (items.Count > 100)
        {
            ProgressionContentPanel.Children.Add(new TextBlock
            {
                Text = $"… and {items.Count - 100} more.",
                FontSize = 11, Foreground = muted, Margin = new Thickness(0, 8, 0, 0),
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Location tracker
    // ══════════════════════════════════════════════════════════════════════════

    /// Named handler so we can -= it in CleanupSessionAsync and avoid accumulation.
    private void OnLocationTrackerChanged()
        => Dispatcher.Invoke(() =>
        {
            if (_currentTab == PageTab.Progression &&
                (_progressionCategory == "locations" || _progressionCategory == "summary"))
                RefreshProgressionPanel();
        });

    private void RenderLocationTracker(Brush fg, Brush muted, Brush gold, Brush success)
    {
        bool connected = _apClient?.State == ApConnectionState.Connected;

        if (_locationTracker.Total == 0)
        {
            ProgressionContentPanel.Children.Add(new TextBlock
            {
                Text         = connected
                    ? "Waiting for location data from the Archipelago server..."
                    : "Connect to an Archipelago server to track your check progress.",
                FontSize     = 13,
                Foreground   = muted,
                TextWrapping = TextWrapping.Wrap,
                Opacity      = 0.7,
            });
            return;
        }

        // ── Hint points strip ────────────────────────────────────────────────
        if (connected && _apClient != null)
            ProgressionContentPanel.Children.Add(BuildHintPointsBar(_apClient, muted, gold));

        // ── Overall progress bar ─────────────────────────────────────────────
        ProgressionContentPanel.Children.Add(
            BuildOverallProgressBar(_locationTracker.Checked, _locationTracker.Total,
                                    fg, muted, gold, success));

        // ── Per-category cards ───────────────────────────────────────────────
        foreach (var cat in _locationTracker.GetCategories())
            ProgressionContentPanel.Children.Add(BuildLocationCategoryCard(cat, fg, muted, gold, success));
    }

    private UIElement BuildHintPointsBar(ApClient ap, Brush muted, Brush gold)
    {
        var panel = new Border
        {
            Background   = new SolidColorBrush(Color.FromArgb(0x18, 0x1E, 0x22, 0x33)),
            CornerRadius = new CornerRadius(4),
            Padding      = new Thickness(14, 8, 14, 8),
            Margin       = new Thickness(0, 0, 0, 12),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
            { Text = "💡 ", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock
        {
            Text = ap.HintPoints.ToString(),
            FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = gold, VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text       = $"  hint point{(ap.HintPoints == 1 ? "" : "s")}",
            FontSize   = 11, Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (ap.HintCostPoints > 0)
        {
            // HintCost is a raw PERCENTAGE of total locations — always show the
            // actual point price (HintCostPoints), never the percentage.
            row.Children.Add(new TextBlock
            {
                Text       = $"  ·  Cost: {ap.HintCostPoints} pts per hint",
                FontSize   = 10, Foreground = muted, Opacity = 0.65,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        panel.Child = row;
        return panel;
    }

    private UIElement BuildOverallProgressBar(int done, int total,
        Brush fg, Brush muted, Brush gold, Brush success)
    {
        double pct = total == 0 ? 0.0 : (double)done / total;

        var container = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        // Label row
        var labelRow = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        labelRow.Children.Add(new TextBlock
        {
            Text = "OVERALL PROGRESS", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
        });
        Brush countBr = done == total && total > 0 ? success : gold;
        var countTb = new TextBlock
        {
            Text = $"{done} / {total}",
            FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = countBr,
        };
        DockPanel.SetDock(countTb, Dock.Right);
        labelRow.Children.Add(countTb);
        container.Children.Add(labelRow);

        // Bar track
        var track = new Border
        {
            Height = 6, CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x1E, 0x22, 0x33)),
        };
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background   = done == total && total > 0
                ? success
                : new SolidColorBrush(Color.FromRgb(0x44, 0x88, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        double capturedPct = pct;
        fill.Loaded += (_, _) => fill.Width = Math.Max(0, track.ActualWidth * capturedPct);
        track.Child = fill;
        container.Children.Add(track);

        return container;
    }

    private UIElement BuildLocationCategoryCard(LocationCategory cat,
        Brush fg, Brush muted, Brush gold, Brush success)
    {
        bool isExpanded = _locCategory.Equals(cat.Name, StringComparison.OrdinalIgnoreCase);

        var card = new Border
        {
            Background      = new SolidColorBrush(Color.FromArgb(0x15, 0x1E, 0x22, 0x33)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Margin          = new Thickness(0, 0, 0, 6),
        };

        var stack = new StackPanel();

        // ── Header row (always visible) ───────────────────────────────────────
        var header = new DockPanel
        {
            Cursor = Cursors.Hand,
            Margin = new Thickness(12, 8, 12, 0),
        };

        // Expand icon
        var expandIcon = new TextBlock
        {
            Text = isExpanded ? "▼" : "▶",
            FontSize = 8, Foreground = muted,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(expandIcon, Dock.Left);
        header.Children.Add(expandIcon);

        // Count badge (right-aligned)
        Brush countColor = cat.IsComplete ? success : muted;
        var countBadge = new TextBlock
        {
            Text = $"{cat.Checked}/{cat.Total}",
            FontSize = 11, Foreground = countColor,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(countBadge, Dock.Right);
        header.Children.Add(countBadge);

        // Category name
        header.Children.Add(new TextBlock
        {
            Text       = cat.Name,
            FontSize   = 12, FontWeight = FontWeights.SemiBold,
            Foreground = cat.IsComplete ? success : fg,
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(header);

        // ── Mini progress bar ─────────────────────────────────────────────────
        var track = new Border
        {
            Height = 3, Margin = new Thickness(12, 5, 12, 8),
            Background   = new SolidColorBrush(Color.FromArgb(0x25, 0x1E, 0x22, 0x33)),
            CornerRadius = new CornerRadius(2),
        };
        var fill = new Border
        {
            CornerRadius        = new CornerRadius(2),
            Background          = cat.IsComplete
                ? success
                : new SolidColorBrush(Color.FromRgb(0x44, 0x88, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        double capturedPct = cat.Progress;
        fill.Loaded += (_, _) => fill.Width = Math.Max(0, track.ActualWidth * capturedPct);
        track.Child = fill;
        stack.Children.Add(track);

        // ── Expanded location list (paginated) ───────────────────────────────
        if (isExpanded)
        {
            bool connected = _apClient?.State == ApConnectionState.Connected;

            var sorted = cat.Entries
                .OrderBy(e => e.IsChecked ? 1 : 0)
                .ThenBy(e => e.LocationName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int page    = _locPage.TryGetValue(cat.Name, out int p) ? p : 0;
            int total   = sorted.Count;
            int pages   = (total + LocPageSize - 1) / LocPageSize;
            page        = Math.Clamp(page, 0, Math.Max(0, pages - 1));

            var locList = new StackPanel { Margin = new Thickness(12, 0, 12, 10) };

            foreach (var entry in sorted.Skip(page * LocPageSize).Take(LocPageSize))
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };

                // Checked tick (left)
                var tickTb = new TextBlock
                {
                    Text              = entry.IsChecked ? "✓" : "○",
                    FontSize          = 10,
                    Foreground        = entry.IsChecked ? success : muted,
                    Margin            = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(tickTb, Dock.Left);
                row.Children.Add(tickTb);

                // "Hint" button for unchecked locations (right, only when connected)
                if (!entry.IsChecked && connected)
                {
                    string tooltip = entry.IsScouted
                        ? $"Buy hint for: {entry.ItemName}"
                        : "Buy hint for this location";
                    var hintBtn = new Button
                    {
                        Content         = "🔍",
                        ToolTip         = tooltip,
                        FontSize        = 10,
                        Padding         = new Thickness(5, 1, 5, 1),
                        Margin          = new Thickness(4, 0, 0, 0),
                        Background      = new SolidColorBrush(Color.FromArgb(0x20, 0x44, 0x88, 0xFF)),
                        Foreground      = new SolidColorBrush(Color.FromRgb(0x7A, 0xAC, 0xDA)),
                        BorderBrush     = new SolidColorBrush(Color.FromArgb(0x40, 0x44, 0x88, 0xFF)),
                        BorderThickness = new Thickness(1),
                        Cursor          = Cursors.Hand,
                    };
                    DockPanel.SetDock(hintBtn, Dock.Right);
                    long locId = entry.LocationId;
                    hintBtn.Click += (_, _) => _ = BuyHintAsync(locId);
                    row.Children.Add(hintBtn);
                }

                // Location name + scouted item
                var textCol = new StackPanel();
                textCol.Children.Add(new TextBlock
                {
                    Text            = entry.LocationName,
                    FontSize        = 11,
                    Foreground      = entry.IsChecked ? muted : fg,
                    TextDecorations = entry.IsChecked ? TextDecorations.Strikethrough : null,
                    TextTrimming    = TextTrimming.CharacterEllipsis,
                });
                if (entry.IsScouted && !entry.IsChecked)
                {
                    Brush itemFg = entry.IsProgression
                        ? gold
                        : entry.IsTrap
                            ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
                            : entry.IsUseful
                                ? new SolidColorBrush(Color.FromRgb(0x77, 0x8B, 0xFF))
                                : muted;
                    string receiver = entry.ReceiverName.Length > 0
                        ? $"  ({entry.ReceiverName})"
                        : "";
                    textCol.Children.Add(new TextBlock
                    {
                        Text     = $"  → {entry.ItemName}{receiver}",
                        FontSize = 10, Foreground = itemFg,
                    });
                }
                row.Children.Add(textCol);
                locList.Children.Add(row);
            }

            stack.Children.Add(locList);

            // ── Pagination bar (only when there are multiple pages) ───────────
            if (pages > 1)
            {
                var pagBar = new DockPanel { Margin = new Thickness(12, 4, 12, 8) };

                // Page indicator
                var pageLabel = new TextBlock
                {
                    Text       = $"Page {page + 1} of {pages}  ({total} locations)",
                    FontSize   = 10,
                    Foreground = muted,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                pagBar.Children.Add(pageLabel);

                // Prev / Next buttons (docked right)
                int capturedPage = page;
                string capturedCat = cat.Name;

                if (page < pages - 1)
                {
                    var nextBtn = new Button
                    {
                        Content         = "Next →",
                        FontSize        = 10,
                        Padding         = new Thickness(8, 3, 8, 3),
                        Background      = new SolidColorBrush(Color.FromArgb(0x18, 0x44, 0x88, 0xFF)),
                        Foreground      = new SolidColorBrush(Color.FromRgb(0x7A, 0xAC, 0xDA)),
                        BorderBrush     = new SolidColorBrush(Color.FromArgb(0x30, 0x44, 0x88, 0xFF)),
                        BorderThickness = new Thickness(1),
                        Cursor          = Cursors.Hand,
                    };
                    DockPanel.SetDock(nextBtn, Dock.Right);
                    nextBtn.Click += (_, _) =>
                    {
                        _locPage[capturedCat] = capturedPage + 1;
                        RefreshProgressionPanel();
                    };
                    pagBar.Children.Add(nextBtn);
                }

                if (page > 0)
                {
                    var prevBtn = new Button
                    {
                        Content         = "← Prev",
                        FontSize        = 10,
                        Padding         = new Thickness(8, 3, 8, 3),
                        Margin          = new Thickness(0, 0, 6, 0),
                        Background      = new SolidColorBrush(Color.FromArgb(0x18, 0x44, 0x88, 0xFF)),
                        Foreground      = new SolidColorBrush(Color.FromRgb(0x7A, 0xAC, 0xDA)),
                        BorderBrush     = new SolidColorBrush(Color.FromArgb(0x30, 0x44, 0x88, 0xFF)),
                        BorderThickness = new Thickness(1),
                        Cursor          = Cursors.Hand,
                    };
                    DockPanel.SetDock(prevBtn, Dock.Right);
                    prevBtn.Click += (_, _) =>
                    {
                        _locPage[capturedCat] = capturedPage - 1;
                        RefreshProgressionPanel();
                    };
                    pagBar.Children.Add(prevBtn);
                }

                stack.Children.Add(pagBar);
            }
        }

        card.Child = stack;

        // Click header to expand/collapse; reset page to 0 on fresh expand
        string catName = cat.Name;
        header.MouseLeftButtonDown += (_, _) =>
        {
            bool collapsing = _locCategory.Equals(catName, StringComparison.OrdinalIgnoreCase);
            _locCategory = collapsing ? "" : catName;
            if (!collapsing) _locPage[catName] = 0;  // reset to first page on expand
            RefreshProgressionPanel();
        };

        return card;
    }

    private async Task BuyHintAsync(long locationId)
    {
        if (_apClient?.State != ApConnectionState.Connected) return;

        // Hints spend a shared resource instantly — confirm with the actual
        // point price up front (UX-9). HintCostPoints is the real per-hint
        // price; the raw hint_cost field is a PERCENTAGE of total locations.
        int cost = _apClient.HintCostPoints;
        int pts  = _apClient.HintPoints;
        string body = cost > 0
            ? "A hint reveals which item is waiting at this location and shows " +
              $"it to all players. Costs {cost} pts — you have {pts} pts."
            : "A hint reveals which item is waiting at this location and shows " +
              "it to all players. Hints are free on this server.";
        if (!ConfirmDialog.Show(this, "Buy a hint?", body, "Buy hint", "Cancel"))
            return;

        try
        {
            // create_as_hint=2: create the hint, broadcast only if new
            await _apClient.LocationScoutsAsync(new[] { locationId }, createAsHint: 2);
            AppendLog("[Hint] Hint requested — check your hint points balance.");
        }
        catch (Exception ex)
        {
            AppendLog($"[Hint] Request failed: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Hints panel
    // ══════════════════════════════════════════════════════════════════════════

    private void RenderHints(Brush fg, Brush muted, Brush gold, Brush success)
    {
        bool connected = _apClient?.State == ApConnectionState.Connected;

        // ── Hint points strip ────────────────────────────────────────────────
        if (connected && _apClient != null)
        {
            ProgressionContentPanel.Children.Add(BuildHintPointsBar(_apClient, muted, gold));
            ProgressionContentPanel.Children.Add(new TextBlock
            {
                Text         = "Buy hints from the Locations tab — click 🔍 next to any unchecked location.",
                FontSize     = 11, Foreground = muted, Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 14),
            });
        }

        // ── Filter chips (All / For Me / In My World) ─────────────────────────
        if (_hints.Count > 0)
        {
            var chipRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };

            void AddHintChip(string label, string value)
            {
                bool active = _hintsFilter == value;
                var chip = new Border
                {
                    Margin          = new Thickness(0, 0, 6, 0),
                    Padding         = new Thickness(10, 4, 10, 4),
                    CornerRadius    = new CornerRadius(12),
                    Background      = active
                        ? new SolidColorBrush(Color.FromArgb(0x30, 0xE8, 0xA0, 0x18))
                        : new SolidColorBrush(Color.FromArgb(0x10, 0x1E, 0x22, 0x33)),
                    BorderBrush     = active
                        ? (Brush)new SolidColorBrush(Color.FromRgb(0xE8, 0xA0, 0x18))
                        : new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x45)),
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand,
                };
                int forMe      = _hints.Count(h => h.IsForMe);
                int inMyWorld  = _hints.Count(h => h.IsOurs && !h.IsForMe);
                string countedLabel = value switch
                {
                    "for_me"      => $"{label}  ({forMe})",
                    "in_my_world" => $"{label}  ({inMyWorld})",
                    _             => $"{label}  ({_hints.Count})",
                };
                chip.Child = new TextBlock
                {
                    Text       = countedLabel,
                    FontSize   = 11,
                    Foreground = active ? gold : muted,
                    FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                };
                string v = value;
                chip.MouseLeftButtonDown += (_, _) =>
                {
                    _hintsFilter = v;
                    RefreshProgressionPanel();
                };
                chipRow.Children.Add(chip);
            }

            AddHintChip("All",          "all");
            AddHintChip("For Me",       "for_me");
            AddHintChip("In My World",  "in_my_world");
            ProgressionContentPanel.Children.Add(chipRow);
        }

        // ── Apply filter ──────────────────────────────────────────────────────
        IEnumerable<HintEntry> visible = _hintsFilter switch
        {
            "for_me"      => _hints.Where(h => h.IsForMe),
            "in_my_world" => _hints.Where(h => h.IsOurs && !h.IsForMe),
            _             => _hints,
        };

        // ── Hint list ─────────────────────────────────────────────────────────
        var visibleList = visible
            .OrderBy(h => h.IsChecked ? 1 : 0)
            .ThenByDescending(h => h.IsProgression ? 1 : 0)
            .ThenBy(h => h.Timestamp)
            .ToList();

        if (visibleList.Count == 0)
        {
            string empty = _hints.Count == 0
                ? (connected ? "No hints received yet." : "Connect to an Archipelago server to track hints.")
                : _hintsFilter == "for_me"
                    ? "No hints about your items yet."
                    : "No hints about items in your world yet.";
            ProgressionContentPanel.Children.Add(new TextBlock
            {
                Text         = empty,
                FontSize     = 13, Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                Opacity      = 0.7,
            });
            return;
        }

        string sectionLabel = _hintsFilter switch
        {
            "for_me"      => "ITEMS YOU ARE LOOKING FOR",
            "in_my_world" => "ITEMS OTHERS NEED FROM YOUR WORLD",
            _             => "ALL HINTS",
        };
        ProgressionContentPanel.Children.Add(new TextBlock
        {
            Text       = sectionLabel,
            FontSize   = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 10),
        });

        foreach (var hint in visibleList)
        {
            Brush itemFg = hint.IsProgression
                ? gold
                : hint.IsTrap
                    ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
                    : hint.IsUseful
                        ? new SolidColorBrush(Color.FromRgb(0x77, 0x8B, 0xFF))
                        : fg;

            var card = new Border
            {
                Background      = new SolidColorBrush(Color.FromArgb(0x12, 0x1E, 0x22, 0x33)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Padding         = new Thickness(12, 8, 12, 8),
                Margin          = new Thickness(0, 0, 0, 6),
                Opacity         = hint.IsChecked ? 0.6 : 1.0,
            };

            var inner = new StackPanel();

            // Top row: checked indicator + item name
            var topRow = new DockPanel();
            var hintTickTb = new TextBlock
            {
                Text              = hint.IsChecked ? "✓" : "○",
                FontSize          = 10, Foreground = hint.IsChecked ? success : muted,
                Margin            = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(hintTickTb, Dock.Left);
            topRow.Children.Add(hintTickTb);

            // ── Hint status chip (server-tracked PRIORITY / AVOID) ───────────
            if (hint.Status is ApClient.HintStatusPriority or ApClient.HintStatusAvoid)
            {
                bool prio = hint.Status == ApClient.HintStatusPriority;
                var statusChip = new Border
                {
                    CornerRadius      = new CornerRadius(8),
                    Padding           = new Thickness(7, 2, 7, 2),
                    Margin            = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background        = prio
                        ? new SolidColorBrush(Color.FromArgb(0x28, 0xCC, 0xA8, 0x00))
                        : new SolidColorBrush(Color.FromArgb(0x28, 0xD9, 0x4A, 0x4A)),
                    Child = new TextBlock
                    {
                        Text       = prio ? "PRIORITY" : "AVOID",
                        FontSize   = 9, FontWeight = FontWeights.Bold,
                        Foreground = prio ? gold : (Brush)FindResource("BrushError"),
                    },
                };
                DockPanel.SetDock(statusChip, Dock.Right);
                topRow.Children.Add(statusChip);
            }

            // ── ★/✕ set-status buttons — only for hints WE must receive, still
            //    unfound, with a known numeric identity (back-filled from the
            //    hint backlog; UpdateHint needs finding player + location id) ──
            if (connected && hint.IsForMe && !hint.IsChecked &&
                hint.FindingSlot > 0 && hint.LocationId != 0)
            {
                var captured = hint;
                Button MiniStatusButton(string glyph, string tip, Brush glyphFg, int status)
                {
                    var b = new Button
                    {
                        Content           = glyph,
                        FontSize          = 11,
                        Padding           = new Thickness(6, 1, 6, 1),
                        Margin            = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip           = tip,
                        Cursor            = Cursors.Hand,
                        Background        = new SolidColorBrush(Color.FromArgb(0x14, 0x1E, 0x22, 0x33)),
                        Foreground        = glyphFg,
                        BorderBrush       = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x45)),
                        BorderThickness   = new Thickness(1),
                    };
                    b.Click += async (_, _) => await SetHintStatusAsync(captured, status);
                    DockPanel.SetDock(b, Dock.Right);
                    return b;
                }
                // Right-docked: first added sits rightmost → visual order ★ ✕
                topRow.Children.Add(MiniStatusButton("✕",
                    "Mark AVOID — tell the finding player to skip this",
                    (Brush)FindResource("BrushError"), ApClient.HintStatusAvoid));
                topRow.Children.Add(MiniStatusButton("★",
                    "Mark PRIORITY — tell the finding player to go for this",
                    gold, ApClient.HintStatusPriority));
            }

            topRow.Children.Add(new TextBlock
            {
                Text       = hint.ItemName.Length > 0 ? hint.ItemName : hint.RawText,
                FontSize   = 13, FontWeight = FontWeights.SemiBold,
                Foreground = itemFg,
            });
            inner.Children.Add(topRow);

            // Detail line
            string detail = hint.ReceiverName.Length > 0 && hint.SenderName.Length > 0
                ? $"{hint.ReceiverName}'s item  ·  at {hint.LocationName}  ·  in {hint.SenderName}'s world"
                : hint.RawText;

            if (detail.Length > 0 && detail != hint.ItemName)
            {
                inner.Children.Add(new TextBlock
                {
                    Text         = detail,
                    FontSize     = 10, Foreground = muted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(18, 3, 0, 0),
                });
            }

            card.Child = inner;
            ProgressionContentPanel.Children.Add(card);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Global Achievements page (owner spec §4)
    //
    // Owns the FULL achievement list — launcher group first, then one group per
    // game. The per-game Progression sidebar no longer has an achievements
    // category; inside a game's pages only the Overview teaser remains.
    // ══════════════════════════════════════════════════════════════════════════

    private string _achFilter = "all";   // "all" | "earned" | "locked"

    // Selected category in the WoW-style sidebar: null = "General" (the
    // launcher-wide GameId==null achievements), otherwise a game's GameId.
    private string? _achCategory;
    private bool    _achCategoryInitialized;

    // Category card borders, keyed by category id ("" = General), so the
    // selection highlight can be re-applied without rebuilding the list.
    private readonly Dictionary<string, Border> _achCategoryCards = new();

    private void BtnAchievements_Click(object sender, RoutedEventArgs e)
        => ShowAchievementsPage();

    /// Bring the global achievements page to the front (over the game page,
    /// Browse and the empty state). focusGameId scrolls that game's group
    /// into view — used by the Overview "All achievements →" teaser link.
    private void ShowAchievementsPage(string? focusGameId = null)
    {
        PanelGame.Visibility         = Visibility.Collapsed;
        PanelEmpty.Visibility        = Visibility.Collapsed;
        PanelBrowse.Visibility       = Visibility.Collapsed;
        PanelAchievements.Visibility = Visibility.Visible;

        // Deep-link: pre-select that game's category. Otherwise default to
        // "General" the first time, and keep the last pick on revisits.
        if (focusGameId != null && GameRegistry.Find(focusGameId) != null)
        {
            _achCategory            = focusGameId;
            _achCategoryInitialized = true;
        }
        else if (!_achCategoryInitialized)
        {
            _achCategory            = null;   // General
            _achCategoryInitialized = true;
        }

        RenderAchievementsPage();
        AchScrollViewer.ScrollToTop();
    }

    /// Same return semantics as the Browse back button: game page when a game
    /// is selected, otherwise the getting-started checklist.
    private void BtnAchievementsBack_Click(object sender, RoutedEventArgs e)
    {
        PanelAchievements.Visibility = Visibility.Collapsed;
        if (_selectedPlugin != null)
        {
            PanelGame.Visibility  = Visibility.Visible;
            PanelEmpty.Visibility = Visibility.Collapsed;
        }
        else
        {
            PanelEmpty.Visibility = Visibility.Visible;
            PanelGame.Visibility  = Visibility.Collapsed;
            RefreshHomePage();
        }
    }

    private void BtnAchFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string filter } && _achFilter != filter)
        {
            _achFilter = filter;
            RenderAchievementsPage();
        }
    }

    /// Tier chip / tier count colors, shared across the page.
    private Brush AchTierBrush(string tier) => tier switch
    {
        "platinum" => new SolidColorBrush(Color.FromRgb(0xB0, 0xE0, 0xFF)),
        "gold"     => (Brush)FindResource("BrushAccent"),
        "silver"   => new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xD0)),
        "bronze"   => new SolidColorBrush(Color.FromRgb(0xCD, 0x7F, 0x32)),
        _          => (Brush)FindResource("BrushMuted"),
    };

    /// Points chip: "<n> pts" tinted with the tier colour (so platinum still
    /// reads as special) but the TEXT is the point value, not the tier word.
    private Border BuildPointsChip(string tier)
    {
        var tierBrush = AchTierBrush(tier);
        var tc        = tierBrush is SolidColorBrush scb ? scb.Color : Colors.Gray;
        return new Border
        {
            Background         = new SolidColorBrush(Color.FromArgb(0x22, 0x1E, 0x22, 0x33)),
            BorderBrush        = new SolidColorBrush(Color.FromArgb(0x55, tc.R, tc.G, tc.B)),
            BorderThickness    = new Thickness(1),
            CornerRadius       = new CornerRadius(3),
            Padding            = new Thickness(7, 2, 7, 2),
            VerticalAlignment  = VerticalAlignment.Center,
            Margin             = new Thickness(10, 0, 0, 0),
            Child = new TextBlock
            {
                Text       = $"{AchievementDefinitions.AchievementPoints(tier)} pts",
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = tierBrush,
            },
        };
    }

    /// Slim proportional progress bar (star-sized so it stretches with layout).
    private static UIElement BuildAchProgressBar(double fraction, Brush fill, double height)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var track = new Grid { Height = height };
        track.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(fraction, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(1 - fraction, GridUnitType.Star) });
        var filled = new Border { Background = fill, CornerRadius = new CornerRadius(height / 2) };
        Grid.SetColumn(filled, 0);
        track.Children.Add(filled);
        return new Border
        {
            Background   = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            CornerRadius = new CornerRadius(height / 2),
            Child        = track,
        };
    }

    private void RenderAchievementsPage()
    {
        var fg      = (Brush)FindResource("BrushText");
        var muted   = (Brush)FindResource("BrushMuted");
        var gold    = (Brush)FindResource("BrushAccent");
        var success = (Brush)FindResource("BrushSuccess");
        var store   = AchievementStore.Instance;
        var defs    = AchievementDefinitions.All;

        // ── Filter chips: active = gold text/border ───────────────────────────
        var inactiveBorder = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50));
        foreach (var (btn, key) in new[]
        {
            (BtnAchFilterAll, "all"), (BtnAchFilterEarned, "earned"), (BtnAchFilterLocked, "locked"),
        })
        {
            bool active     = _achFilter == key;
            btn.Foreground  = active ? gold : muted;
            btn.BorderBrush = active ? gold : inactiveBorder;
        }

        // ── Summary strip: POINTS headline, then achievement count + per-tier ──
        int total        = defs.Count;
        int totalEarned  = defs.Count(d => store.IsEarned(d.Id));
        double percent   = total > 0 ? totalEarned * 100.0 / total : 0;
        int totalPoints  = defs.Sum(d => AchievementDefinitions.AchievementPoints(d.Tier));
        int earnedPoints = defs.Where(d => store.IsEarned(d.Id))
                               .Sum(d => AchievementDefinitions.AchievementPoints(d.Tier));
        double ptFraction = totalPoints > 0 ? (double)earnedPoints / totalPoints : 0;

        var summary = new Grid();
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Headline leads with POINTS; achievement count is the secondary line.
        var totals = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var headline = new StackPanel { Orientation = Orientation.Horizontal };
        headline.Children.Add(new TextBlock
        {
            Text       = $"{earnedPoints} / {totalPoints}",
            FontSize   = 24,
            FontWeight = FontWeights.Bold,
            Foreground = earnedPoints > 0 ? gold : muted,
        });
        headline.Children.Add(new TextBlock
        {
            Text              = "points",
            FontSize          = 12,
            FontWeight        = FontWeights.SemiBold,
            Foreground        = muted,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin            = new Thickness(7, 0, 0, 3),
        });
        totals.Children.Add(headline);
        totals.Children.Add(new TextBlock
        {
            Text       = $"{totalEarned} of {total} achievements · {percent:0}%",
            FontSize   = 11,
            Foreground = muted,
            Margin     = new Thickness(0, 3, 0, 0),
        });
        Grid.SetColumn(totals, 0);
        summary.Children.Add(totals);

        var right = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(28, 0, 0, 0),
        };
        // Progress bar is driven by POINTS (nicer — a platinum moves it more).
        right.Children.Add(BuildAchProgressBar(ptFraction, gold, 7));
        var tierRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 10, 0, 0),
        };
        foreach (string tier in new[] { "bronze", "silver", "gold", "platinum" })
        {
            int tierTotal  = defs.Count(d => d.Tier == tier);
            int tierEarned = defs.Count(d => d.Tier == tier && store.IsEarned(d.Id));
            tierRow.Children.Add(new TextBlock
            {
                Text       = "●",
                FontSize   = 9,
                Foreground = AchTierBrush(tier),
                Margin     = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            tierRow.Children.Add(new TextBlock
            {
                Text       = $"{AchievementDefinitions.AchievementPoints(tier)} pts  {tierEarned}/{tierTotal}",
                FontSize   = 10,
                Foreground = muted,
                Margin     = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        right.Children.Add(tierRow);
        Grid.SetColumn(right, 1);
        summary.Children.Add(right);
        AchSummaryHost.Child = summary;

        // ── Left pane: category list (General, then every registered game) ────
        BuildAchCategoryList(defs, store);

        // ── Right pane: rows for the SELECTED category, within the filter ─────
        RenderAchCategoryRows(defs, store);
    }

    /// Builds the WoW-style category sidebar: "General" (launcher-wide defs)
    /// on top, a divider, then EVERY registered game in registry order — each
    /// with its icon, name and an earned/total · points sub-count. Selecting a
    /// card sets _achCategory and re-renders the right pane.
    private void BuildAchCategoryList(IReadOnlyList<AchievementDefinition> defs, AchievementStore store)
    {
        AchCategoryPanel.Children.Clear();
        _achCategoryCards.Clear();

        // "General" — the launcher-wide achievements (GameId == null).
        var generalDefs = defs.Where(d => d.GameId == null).ToList();
        AchCategoryPanel.Children.Add(BuildAchCategoryCard(
            categoryId: "", isGeneral: true,
            displayName: "General", iconPath: null,
            defsInCat: generalDefs, store: store));

        // Thin divider between General and the per-game list.
        AchCategoryPanel.Children.Add(new Border
        {
            Height     = 1,
            Background  = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            Margin      = new Thickness(10, 6, 10, 6),
        });

        // One entry per registered game — even games with zero definitions.
        foreach (var plugin in GameRegistry.All)
        {
            var catDefs = defs.Where(d => d.GameId == plugin.GameId).ToList();
            AchCategoryPanel.Children.Add(BuildAchCategoryCard(
                categoryId: plugin.GameId, isGeneral: false,
                displayName: plugin.DisplayName, iconPath: plugin.IconPath,
                defsInCat: catDefs, store: store));
        }

        HighlightAchCategory();
    }

    /// One selectable category card (General or a game).
    private Border BuildAchCategoryCard(
        string categoryId, bool isGeneral, string displayName, string? iconPath,
        IReadOnlyList<AchievementDefinition> defsInCat, AchievementStore store)
    {
        var fg      = (Brush)FindResource("BrushText");
        var muted   = (Brush)FindResource("BrushMuted");
        var success = (Brush)FindResource("BrushSuccess");

        int catTotal  = defsInCat.Count;
        int catEarned = defsInCat.Count(d => store.IsEarned(d.Id));
        int catPoints = defsInCat.Where(d => store.IsEarned(d.Id))
                                 .Sum(d => AchievementDefinitions.AchievementPoints(d.Tier));

        var row = new DockPanel { LastChildFill = true };

        // Icon: game thumbnail (28×28) or a trophy glyph for General.
        var iconHost = new Border
        {
            Width             = 28,
            Height            = 28,
            CornerRadius      = new CornerRadius(4),
            Background        = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Margin            = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds      = true,
        };
        if (!isGeneral && LoadCachedBitmap(iconPath) is { } iconBmp)
        {
            var img = new Image { Source = iconBmp, Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            iconHost.Child = img;
        }
        else
        {
            iconHost.Child = new TextBlock
            {
                Text                = isGeneral ? "🏆" : "🎮",
                FontSize            = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
        }
        DockPanel.SetDock(iconHost, Dock.Left);
        row.Children.Add(iconHost);

        // Name + sub-count.
        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text         = displayName,
            FontSize     = 12,
            FontWeight   = FontWeights.SemiBold,
            Foreground   = fg,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        textStack.Children.Add(new TextBlock
        {
            Text       = catTotal == 0
                ? "No achievements"
                : $"{catEarned}/{catTotal} · {catPoints} pts",
            FontSize   = 10,
            Foreground = catTotal > 0 && catEarned == catTotal ? success : muted,
            Margin     = new Thickness(0, 1, 0, 0),
        });
        row.Children.Add(textStack);

        var card = new Border
        {
            Tag          = categoryId,
            Cursor       = Cursors.Hand,
            CornerRadius = new CornerRadius(4),
            Padding      = new Thickness(10, 8, 10, 8),
            Margin       = new Thickness(6, 1, 6, 1),
            Child        = row,
        };
        card.MouseLeftButtonDown += (_, _) =>
        {
            // categoryId "" = General → null selection.
            _achCategory            = string.IsNullOrEmpty(categoryId) ? null : categoryId;
            _achCategoryInitialized = true;
            HighlightAchCategory();
            RenderAchCategoryRows(AchievementDefinitions.All, AchievementStore.Instance);
            AchScrollViewer.ScrollToTop();
        };

        _achCategoryCards[categoryId] = card;
        return card;
    }

    /// Applies the selected-category highlight (accent left-border + lifted
    /// background), mirroring the sidebar game-card selection style.
    private void HighlightAchCategory()
    {
        var gold  = (Brush)FindResource("BrushAccent");
        var actBg = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x30));
        string selKey = _achCategory ?? "";   // "" = General

        foreach (var (key, card) in _achCategoryCards)
        {
            bool active = key == selKey;
            if (active)
            {
                card.Background      = actBg;
                card.BorderBrush     = gold;
                card.BorderThickness = new Thickness(3, 0, 0, 0);
                card.Padding         = new Thickness(7, 8, 10, 8);
            }
            else
            {
                card.Background      = Brushes.Transparent;
                card.BorderBrush     = Brushes.Transparent;
                card.BorderThickness = new Thickness(0);
                card.Padding         = new Thickness(10, 8, 10, 8);
            }
        }
    }

    /// Renders the right pane: the achievement rows for _achCategory only,
    /// honouring the All/Earned/Locked filter. Empty categories and empty
    /// filter results show a muted placeholder.
    private void RenderAchCategoryRows(IReadOnlyList<AchievementDefinition> defs, AchievementStore store)
    {
        var fg      = (Brush)FindResource("BrushText");
        var muted   = (Brush)FindResource("BrushMuted");
        var gold    = (Brush)FindResource("BrushAccent");
        var success = (Brush)FindResource("BrushSuccess");

        AchGroupsPanel.Children.Clear();

        var catDefs = defs.Where(d => d.GameId == _achCategory).ToList();

        // Category header: name — "x of y" — slim progress bar.
        string title = _achCategory == null
            ? "GENERAL"
            : (GameRegistry.Find(_achCategory)?.DisplayName ?? _achCategory).ToUpperInvariant();
        int catEarned = catDefs.Count(d => store.IsEarned(d.Id));

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerTitle = new TextBlock
        {
            Text       = title,
            FontSize   = 12,
            FontWeight = FontWeights.Bold,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(headerTitle, 0);
        header.Children.Add(headerTitle);

        if (catDefs.Count > 0)
        {
            var headerCount = new TextBlock
            {
                Text       = $"{catEarned} of {catDefs.Count}",
                FontSize   = 10,
                Foreground = catEarned == catDefs.Count ? success : muted,
                Margin     = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(headerCount, 1);
            header.Children.Add(headerCount);

            var headerBar = new Border
            {
                Width             = 150,
                VerticalAlignment = VerticalAlignment.Center,
                Child = (Border)BuildAchProgressBar(
                    (double)catEarned / catDefs.Count,
                    catEarned == catDefs.Count ? success : gold, 5),
            };
            Grid.SetColumn(headerBar, 2);
            header.Children.Add(headerBar);
        }
        AchGroupsPanel.Children.Add(header);

        // Empty category (a game with no definitions yet).
        if (catDefs.Count == 0)
        {
            AchGroupsPanel.Children.Add(new TextBlock
            {
                Text       = "No achievements for this game yet.",
                FontSize   = 12,
                Foreground = muted,
                Opacity    = 0.8,
                Margin     = new Thickness(2, 6, 0, 0),
            });
            return;
        }

        var visible = _achFilter switch
        {
            "earned" => catDefs.Where(d => store.IsEarned(d.Id)).ToList(),
            "locked" => catDefs.Where(d => !store.IsEarned(d.Id)).ToList(),
            _        => catDefs,
        };

        // Empty filter result within a non-empty category.
        if (visible.Count == 0)
        {
            AchGroupsPanel.Children.Add(new TextBlock
            {
                Text = _achFilter == "earned"
                    ? "Nothing earned here yet — play a session and the trophies will follow."
                    : "Nothing locked — you have earned everything here. Impressive.",
                FontSize   = 12,
                Foreground = muted,
                Opacity    = 0.8,
                Margin     = new Thickness(2, 6, 0, 0),
            });
            return;
        }

        foreach (var def in visible)
            AchGroupsPanel.Children.Add(BuildAchievementRow(def, store));
    }

    /// One achievement row card (icon, title, description, points chip, earned
    /// date / locked state). Earned = full opacity, locked = dimmed.
    private Border BuildAchievementRow(AchievementDefinition def, AchievementStore store)
    {
        var fg      = (Brush)FindResource("BrushText");
        var muted   = (Brush)FindResource("BrushMuted");
        var success = (Brush)FindResource("BrushSuccess");

        bool earned   = store.IsEarned(def.Id);
        var  earnedAt = store.EarnedAt(def.Id);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text              = def.Icon,
            FontSize          = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(new TextBlock
        {
            Text       = def.Title,
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
        });
        textPanel.Children.Add(new TextBlock
        {
            Text         = def.Description,
            FontSize     = 10,
            Foreground   = muted,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(textPanel, 1);
        row.Children.Add(textPanel);

        var pointsChip = BuildPointsChip(def.Tier);
        Grid.SetColumn(pointsChip, 2);
        row.Children.Add(pointsChip);

        var state = new TextBlock
        {
            FontSize          = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(12, 0, 0, 0),
            TextAlignment     = TextAlignment.Right,
            MinWidth          = 92,
        };
        if (earned)
        {
            state.Text = earnedAt is { } at
                ? "✓ " + at.ToLocalTime().ToString("d MMM yyyy",
                      System.Globalization.CultureInfo.InvariantCulture)
                : "✓ Earned";
            state.Foreground = success;
        }
        else
        {
            state.Text       = "🔒 Locked";
            state.Foreground = muted;
            state.Opacity    = 0.8;
        }
        Grid.SetColumn(state, 3);
        row.Children.Add(state);

        return new Border
        {
            Background   = new SolidColorBrush(Color.FromArgb(
                earned ? (byte)0x25 : (byte)0x10, 0x1E, 0x22, 0x33)),
            CornerRadius = new CornerRadius(4),
            Padding      = new Thickness(12, 9, 12, 9),
            Margin       = new Thickness(0, 0, 0, 5),
            Opacity      = earned ? 1.0 : 0.5,
            Child        = row,
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Settings tab
    // ══════════════════════════════════════════════════════════════════════════

    private void PopulateSettingsPanel(IGamePlugin plugin)
    {
        SettingsPanel.Children.Clear();

        var muted = (Brush)FindResource("BrushMuted");
        var fg    = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));

        // ── Global launcher settings ──────────────────────────────────────────
        SettingsPanel.Children.Add(new TextBlock
        {
            Text       = "LAUNCHER",
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new Thickness(0, 0, 0, 8),
        });

        var settings = SettingsStore.Load();

        // Catalog URL row
        var urlRow   = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var urlLabel = new TextBlock
        {
            Text              = "Catalog URL",
            FontSize          = 11,
            Foreground        = fg,
            VerticalAlignment = VerticalAlignment.Center,
            Width             = 90,
            Margin            = new Thickness(0, 0, 8, 0),
        };
        var saveBtn = new Button
        {
            Content     = "Save",
            Width       = 55,
            Padding     = new Thickness(0, 5, 0, 5),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            Margin      = new Thickness(6, 0, 0, 0),
        };
        var urlBox = new TextBox
        {
            Text        = settings.CatalogUrl ?? GameCatalog.DefaultCatalogUrl,
            FontSize    = 11,
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            Padding     = new Thickness(6, 5, 6, 5),
        };
        saveBtn.Click += (_, _) =>
        {
            var s = SettingsStore.Load();
            s.CatalogUrl     = urlBox.Text.Trim();
            SettingsStore.Save(s);
            _catalogEntries  = null;   // force reload next time Browse is opened
            AppendLog("[Settings] Catalog URL saved — reopen Browse to refresh.");
        };
        DockPanel.SetDock(urlLabel, Dock.Left);
        DockPanel.SetDock(saveBtn,  Dock.Right);
        urlRow.Children.Add(urlLabel);
        urlRow.Children.Add(saveBtn);
        urlRow.Children.Add(urlBox);
        SettingsPanel.Children.Add(urlRow);

        // Default AP Server row
        SettingsPanel.Children.Add(new TextBlock
        {
            Text = "Default AP Server", FontSize = 11, Foreground = muted,
            Margin = new Thickness(0, 10, 0, 4),
        });
        var serverRow  = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var serverBox  = new TextBox
        {
            Text        = settings.DefaultApServer,
            FontSize    = 11, Padding = new Thickness(6, 5, 6, 5),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg, BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "e.g. archipelago.gg:38281 — pre-filled into the connect panel",
        };
        var serverSave = new Button
        {
            Content     = "Save", Width = 55, Padding = new Thickness(0, 5, 0, 5),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg, BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            Margin      = new Thickness(6, 0, 0, 0),
        };
        serverSave.Click += (_, _) =>
        {
            var s = SettingsStore.Load();
            s.DefaultApServer = serverBox.Text.Trim();
            SettingsStore.Save(s);
            // Also pre-fill the sidebar server box if it's empty
            if (string.IsNullOrWhiteSpace(TxtServer.Text))
                TxtServer.Text = s.DefaultApServer;
            AppendLog("[Settings] Default AP server saved.");
        };
        DockPanel.SetDock(serverSave, Dock.Right);
        serverRow.Children.Add(serverSave);
        serverRow.Children.Add(serverBox);
        SettingsPanel.Children.Add(serverRow);

        // ── Notification + connection toggles ─────────────────────────────────
        var soundCheck = new CheckBox
        {
            Content   = "Notification sounds (progression items, traps, DeathLink, goal)",
            FontSize  = 11,
            Foreground = fg,
            IsChecked = settings.SoundNotifications,
            Margin    = new Thickness(0, 14, 0, 0),
            Cursor    = Cursors.Hand,
        };
        soundCheck.Click += (_, _) =>
        {
            var s = SettingsStore.Load();
            s.SoundNotifications = soundCheck.IsChecked == true;
            SettingsStore.Save(s);
            SoundsEnabled = s.SoundNotifications;
        };
        SettingsPanel.Children.Add(soundCheck);

        var reconnectCheck = new CheckBox
        {
            Content   = "Auto-reconnect to the AP server after connection drops",
            FontSize  = 11,
            Foreground = fg,
            IsChecked = settings.AutoReconnect,
            Margin    = new Thickness(0, 8, 0, 0),
            Cursor    = Cursors.Hand,
        };
        reconnectCheck.Click += (_, _) =>
        {
            var s = SettingsStore.Load();
            s.AutoReconnect = reconnectCheck.IsChecked == true;
            SettingsStore.Save(s);
            if (!s.AutoReconnect) CancelAutoReconnect(resetAttempts: true);
        };
        SettingsPanel.Children.Add(reconnectCheck);

        // ── Copy Diagnostics — one-click Discord bug report payload ───────────
        var diagBtn = new Button
        {
            Content = "📋 Copy Diagnostics",
            Padding = new Thickness(14, 6, 14, 6),
            Margin  = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            Cursor  = Cursors.Hand,
            ToolTip = "Copies launcher version, install state and recent log lines — paste into a Discord bug report",
        };
        diagBtn.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(BuildDiagnosticsText());
                ToastService.Show("Diagnostics copied",
                    "Paste into the Discord bug-reports channel.", ToastKind.Success);
            }
            catch
            {
                AppendLog("[Settings] Could not access the clipboard — try again.");
            }
        };
        SettingsPanel.Children.Add(diagBtn);

        // Divider
        SettingsPanel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            Margin     = new Thickness(0, 16, 0, 16),
        });

        // ── Per-game settings ─────────────────────────────────────────────────
        SettingsPanel.Children.Add(new TextBlock
        {
            Text       = plugin.DisplayName.ToUpperInvariant(),
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new Thickness(0, 0, 0, 8),
        });

        var panel = plugin.CreateSettingsPanel();
        if (panel != null)
        {
            SettingsPanel.Children.Add(panel);
        }
        else
        {
            SettingsPanel.Children.Add(new TextBlock
            {
                Text         = $"{plugin.DisplayName} has no configurable settings.",
                FontSize     = 12,
                Foreground   = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 8, 0, 0),
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Skeleton / loading placeholder cards
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a single skeleton "shimmer" bar of a given width (0–1 = fraction of panel).
    /// Used to build placeholder cards while data loads.
    /// </summary>
    private static Border SkeletonBar(double widthFraction, double height, double topMargin = 0)
        => new()
        {
            Height          = height,
            Width           = double.NaN,             // stretch to parent
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin          = new Thickness(0, topMargin, (1 - widthFraction) * 200, 0),
            Background      = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x2E)),
            CornerRadius    = new CornerRadius(2),
            Opacity         = 0.65,
        };

    /// <summary>Skeleton placeholder that matches a news card.</summary>
    private UIElement BuildSkeletonNewsCard()
    {
        var card = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x20)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(18, 14, 18, 14),
            Margin          = new Thickness(0, 0, 0, 10),
        };
        var sp = new StackPanel();
        // Title placeholder
        sp.Children.Add(SkeletonBar(0.55, 14));
        // Subtitle row
        sp.Children.Add(SkeletonBar(0.30, 10, 8));
        // Body lines
        sp.Children.Add(SkeletonBar(0.90, 9, 14));
        sp.Children.Add(SkeletonBar(0.85, 9, 6));
        sp.Children.Add(SkeletonBar(0.75, 9, 6));
        sp.Children.Add(SkeletonBar(0.50, 9, 6));
        card.Child = sp;
        return card;
    }

    /// <summary>Skeleton placeholder that matches a catalog card.</summary>
    private UIElement BuildSkeletonCatalogCard()
    {
        var card = new Border
        {
            Width           = 244,
            Margin          = new Thickness(0, 0, 12, 12),
            Background      = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x20)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
        };
        // Matches the slim Steam-like card: edge-to-edge thumb + title + badges.
        var outer = new StackPanel();
        outer.Children.Add(new Border
        {
            Height       = 128,
            Background   = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
        });
        var sp = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
        sp.Children.Add(SkeletonBar(0.70, 13));
        sp.Children.Add(SkeletonBar(0.50, 9, 10));
        outer.Children.Add(sp);
        card.Child        = outer;
        card.ClipToBounds = true;
        return card;
    }

    private async Task LoadNewsAsync(IGamePlugin plugin)
    {
        // Cancel any in-flight load so a previous game's news can't overwrite the new one.
        _newsCts?.Cancel();
        _newsCts = new CancellationTokenSource();
        var cts = _newsCts;

        NewsPanel.Children.Clear();
        NewsLoadingBadge.Visibility = Visibility.Visible;

        // Show skeleton cards while fetching so the panel isn't blank
        for (int i = 0; i < 4; i++)
            NewsPanel.Children.Add(BuildSkeletonNewsCard());

        NewsItem[] news;
        try   { news = await plugin.GetNewsAsync(cts.Token); }
        catch { news = Array.Empty<NewsItem>(); }

        // User switched game while we were fetching — discard stale results.
        if (cts.IsCancellationRequested || _selectedPlugin != plugin) return;

        if (!CheckAccess()) { Dispatcher.Invoke(() => RenderNews(plugin, news)); return; }
        RenderNews(plugin, news);
    }

    private void RenderNews(IGamePlugin plugin, NewsItem[] news)
    {
        NewsLoadingBadge.Visibility = Visibility.Collapsed;
        NewsPanel.Children.Clear();

        if (news.Length == 0)
        {
            var muted = (Brush)FindResource("BrushMuted");
            var text  = (Brush)FindResource("BrushText");
            var gold  = (Brush)FindResource("BrushAccent");

            var card = new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x20)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Padding         = new Thickness(18, 16, 18, 16),
                Margin          = new Thickness(0, 0, 0, 10),
            };
            var stack = new StackPanel { Orientation = Orientation.Vertical };

            stack.Children.Add(new TextBlock
            {
                Text         = plugin.DisplayName,
                FontSize     = 14,
                FontWeight   = FontWeights.SemiBold,
                Foreground   = text,
                Margin       = new Thickness(0, 0, 0, 8),
            });

            if (!string.IsNullOrWhiteSpace(plugin.Description))
            {
                stack.Children.Add(new TextBlock
                {
                    Text         = plugin.Description,
                    FontSize     = 12,
                    Foreground   = muted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 0, 0, 12),
                });
            }

            stack.Children.Add(new TextBlock
            {
                Text         = "No release notes are published yet. Check the Archipelago Discord or the game's GitHub page for the latest updates.",
                FontSize     = 11,
                Foreground   = muted,
                TextWrapping = TextWrapping.Wrap,
                Opacity      = 0.7,
            });

            card.Child = stack;
            NewsPanel.Children.Add(card);
            return;
        }

        foreach (var item in news)
            NewsPanel.Children.Add(BuildNewsCard(item));
    }

    private UIElement BuildNewsCard(NewsItem item)
    {
        var gold  = (Brush)FindResource("BrushAccent");
        var muted = (Brush)FindResource("BrushMuted");
        var text  = (Brush)FindResource("BrushText");

        var border = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x20)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(18, 14, 18, 14),
            Margin          = new Thickness(0, 0, 0, 10)
        };

        var stack = new StackPanel();

        // Header row: version badge | title | date
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var verBadge = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x38)),
            CornerRadius    = new CornerRadius(2),
            Padding         = new Thickness(7, 2, 7, 2),
            Margin          = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        verBadge.Child = new TextBlock
        {
            Text       = item.Version,
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = gold
        };

        var titleTb = new TextBlock
        {
            Text              = item.Title,
            FontSize          = 14,
            FontWeight        = FontWeights.SemiBold,
            Foreground        = text,
            VerticalAlignment = VerticalAlignment.Center
        };

        var dateTb = new TextBlock
        {
            Text              = item.Date.ToString("MMM dd, yyyy"),
            FontSize          = 11,
            Foreground        = muted,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(verBadge, 0);
        Grid.SetColumn(titleTb,  1);
        Grid.SetColumn(dateTb,   2);
        header.Children.Add(verBadge);
        header.Children.Add(titleTb);
        header.Children.Add(dateTb);
        stack.Children.Add(header);

        // Body — rendered with lightweight markdown support (bold, headers, lists, links)
        var bodyPanel = BuildMarkdownBody(item.Body, muted, text, gold);
        bodyPanel.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(bodyPanel);

        // "View full release" link
        if (!string.IsNullOrEmpty(item.Url))
        {
            var link = new TextBlock
            {
                Text       = "View full release →",
                FontSize   = 11,
                Foreground = gold,
                Cursor     = Cursors.Hand,
                Margin     = new Thickness(0, 6, 0, 0)
            };
            string url = item.Url;
            link.MouseLeftButtonDown += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(url)
                        { UseShellExecute = true });
                }
                catch { /* ignore */ }
            };
            stack.Children.Add(link);
        }

        border.Child = stack;
        return border;
    }

    /// Lightweight markdown-to-WPF renderer.  Handles: ## headers, - list items,
    /// **bold**, [link text](url).  Falls back to plain text for anything else.
    private StackPanel BuildMarkdownBody(string rawBody, Brush muted, Brush fg, Brush accent)
    {
        var panel = new StackPanel { Opacity = 0.85 };

        // Truncate at 600 chars worth of rendered lines to keep cards compact.
        string[] lines = rawBody.Replace("\r\n", "\n").Split('\n');
        int charCount  = 0;
        const int limit = 600;
        bool truncated  = false;

        foreach (string rawLine in lines)
        {
            if (truncated) break;
            string line = rawLine.TrimEnd();
            charCount += line.Length + 1;
            if (charCount > limit) { truncated = true; line = "..."; }

            // Empty line → small spacer
            if (line.Length == 0)
            {
                panel.Children.Add(new Border { Height = 4 });
                continue;
            }

            // Heading: ## or ###
            if (line.StartsWith("## ") || line.StartsWith("### "))
            {
                string txt = line.TrimStart('#').Trim();
                panel.Children.Add(new TextBlock
                {
                    Text = txt, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = fg, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 2),
                });
                continue;
            }

            // List item: - or *
            string content = line;
            string prefix  = "";
            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                prefix  = "• ";
                content = line[2..];
            }

            // Build a TextBlock with inline markdown (bold, links)
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize     = 11,
                Margin       = new Thickness(0, 1, 0, 1),
            };
            if (prefix.Length > 0)
                tb.Inlines.Add(new Run(prefix) { Foreground = muted });

            // Parse inline: **bold** and [text](url)
            int pos = 0;
            while (pos < content.Length)
            {
                // Bold: **text**
                if (pos < content.Length - 3 && content[pos] == '*' && content[pos + 1] == '*')
                {
                    int end = content.IndexOf("**", pos + 2);
                    if (end > pos + 2)
                    {
                        string bold = content[(pos + 2)..end];
                        tb.Inlines.Add(new Bold(new Run(bold) { Foreground = fg }));
                        pos = end + 2;
                        continue;
                    }
                }
                // Link: [text](url)
                if (content[pos] == '[')
                {
                    int textEnd = content.IndexOf("](", pos + 1);
                    int urlEnd  = textEnd > 0 ? content.IndexOf(')', textEnd + 2) : -1;
                    if (textEnd > pos && urlEnd > textEnd)
                    {
                        string linkText = content[(pos + 1)..textEnd];
                        string linkUrl  = content[(textEnd + 2)..urlEnd];
                        var hl = new Hyperlink(new Run(linkText))
                        {
                            Foreground          = accent,
                            TextDecorations     = TextDecorations.Underline,
                            NavigateUri         = Uri.TryCreate(linkUrl, UriKind.Absolute, out var uri) ? uri : null,
                            Cursor              = Cursors.Hand,
                        };
                        string capturedUrl = linkUrl;
                        hl.Click += (_, _) => OpenUrl(capturedUrl);
                        tb.Inlines.Add(hl);
                        pos = urlEnd + 1;
                        continue;
                    }
                }
                // Plain text — accumulate until next special char
                int next = content.IndexOfAny(new[] { '*', '[' }, pos + 1);
                if (next < 0) next = content.Length;
                string plain = content[pos..next];
                tb.Inlines.Add(new Run(plain) { Foreground = muted });
                pos = next;
            }

            panel.Children.Add(tb);
        }

        return panel;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Item Tracker filter
    // ══════════════════════════════════════════════════════════════════════════

    private void CboFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Guard: WPF can fire SelectionChanged during InitializeComponent() while
        // x:Name fields for later elements (CboPlayer, TxtTrackerCount) are still null.
        if (CboPlayer is null || TxtTrackerCount is null) return;

        // Show the player selector for modes that target a specific player
        int mode = CboFilter.SelectedIndex;
        CboPlayer.Visibility = mode >= 3 ? Visibility.Visible : Visibility.Collapsed;
        ApplyTrackerFilter();
    }

    private void TxtTrackerSearch_Changed(object sender, TextChangedEventArgs e)
        => ApplyTrackerFilter();

    // ── Debounced tracker refresh (P2-17) ─────────────────────────────────────
    // Item ingest can arrive as a burst of ReceivedItems packets; rebuilding
    // the filtered view once per packet is still wasteful during a catch-up.
    // The timer coalesces every request landing within 250 ms into one rebuild.

    private DispatcherTimer? _trackerRefreshTimer;

    /// Request a (debounced) rebuild of the items view + progression panel.
    /// UI thread only.
    private void ScheduleTrackerRefresh()
    {
        if (_trackerRefreshTimer == null)
        {
            _trackerRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _trackerRefreshTimer.Tick += (_, _) =>
            {
                _trackerRefreshTimer!.Stop();
                ApplyTrackerFilter();
                if (_currentTab == PageTab.Progression)
                    RefreshProgressionPanel();
            };
        }
        // Restart: the rebuild fires 250 ms after the burst quiets down.
        _trackerRefreshTimer.Stop();
        _trackerRefreshTimer.Start();
    }

    private void ApplyTrackerFilter()
    {
        // Guard for the same early-init race as CboFilter_Changed.
        if (CboFilter is null || TxtTrackerCount is null) return;

        var mode = (ApItemTracker.FilterMode)Math.Max(0, CboFilter.SelectedIndex);
        int playerSlot = CboPlayer.SelectedItem is ApNetworkPlayer p ? p.Slot : 0;
        string search  = TxtTrackerSearch.Text ?? "";

        var filtered = _tracker.ApplyFilter(mode, playerSlot, search);

        // Preserve current sort so it survives collection refresh
        var savedSorts = TrackerGrid.Items.SortDescriptions
            .Select(sd => new System.ComponentModel.SortDescription(sd.PropertyName, sd.Direction))
            .ToList();

        _trackerView.Clear();
        foreach (var item in filtered)
            _trackerView.Add(item);

        // Re-apply saved sort (SortDescriptions is cleared by WPF on collection change)
        if (savedSorts.Count > 0)
        {
            TrackerGrid.Items.SortDescriptions.Clear();
            foreach (var sd in savedSorts)
                TrackerGrid.Items.SortDescriptions.Add(sd);
        }

        TxtTrackerCount.Text = $"{filtered.Count} item{(filtered.Count == 1 ? "" : "s")}";

        // Grid view mirrors the same filtered set as aggregated tiles.
        // Only rendered while the Items tab is showing — SwitchTab(Tracker)
        // re-applies the filter, so a hidden grid can never go stale.
        if (_trackerViewMode == "grid" && _currentTab == PageTab.Tracker)
            RenderTrackerTiles(filtered);
    }

    private void RefreshPlayerDropdown()
    {
        int prevSlot = CboPlayer.SelectedItem is ApNetworkPlayer prev ? prev.Slot : -1;
        CboPlayer.Items.Clear();
        foreach (var player in _tracker.Players)
            CboPlayer.Items.Add(player);
        // Restore previous selection if possible
        for (int i = 0; i < CboPlayer.Items.Count; i++)
        {
            if (CboPlayer.Items[i] is ApNetworkPlayer q && q.Slot == prevSlot)
            { CboPlayer.SelectedIndex = i; break; }
        }
        if (CboPlayer.SelectedIndex < 0 && CboPlayer.Items.Count > 0)
            CboPlayer.SelectedIndex = 0;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Install
    // ══════════════════════════════════════════════════════════════════════════

    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlugin == null) return;
        await RunInstallAsync(_selectedPlugin);
    }

    /// Run a full install/update for one plugin. Returns true only when the
    /// install actually completed — false on failure, cancellation, or when
    /// another install is already running (P2-11 single-install guard).
    private async Task<bool> RunInstallAsync(IGamePlugin plugin)
    {
        // Single-install guard: installers share %TEMP% staging files, so a
        // second concurrent install (same or another game) corrupts the first.
        if (_installCts != null)
        {
            ToastService.Show("Install already running",
                "Another install is in progress — wait for it to finish " +
                "(or cancel it from its progress row) first.", ToastKind.Warning);
            return false;
        }

        // D2Plugin: the mod installs into its OWN folder (Games/diablo2_archipelago)
        // — never the user's Diablo II. We only need to know where the player's own
        // Classic Diablo II lives so we can COPY the original Blizzard data files
        // (MPQs) from it; the original is never modified. Auto-detect first, and
        // only prompt the user if that fails.
        if (plugin is Plugins.DiabloII.D2Plugin d2pre
            && !d2pre.IsInstalled
            && !d2pre.IsOriginalD2Configured)
        {
            string? detected = d2pre.AutoDetectOriginalD2();
            if (detected != null)
            {
                d2pre.OriginalD2Directory = detected;
            }
            else
            {
                bool pick = ConfirmDialog.Show(this,
                    "Locate your original Diablo II",
                    "Diablo II Archipelago is built from your own copy of Diablo II: " +
                    "Lord of Destruction. Select the folder where Classic Diablo II is " +
                    "installed — the launcher copies the needed game files from there " +
                    "into its own folder and never modifies your original install.",
                    "Select folder…", "Cancel");
                if (!pick) return false;

                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title            = "Select your Classic Diablo II: Lord of Destruction folder",
                    InitialDirectory = @"C:\Program Files (x86)",
                };
                if (dlg.ShowDialog(this) != true) return false;

                string? err = d2pre.ValidateExistingInstall(dlg.FolderName);
                if (err != null)
                {
                    ConfirmDialog.ShowInfo(this, "Folder not recognized", err);
                    return false;
                }
                d2pre.OriginalD2Directory = dlg.FolderName;
            }

            var ls = SettingsStore.Load();
            ls.DiabloIIPath = d2pre.OriginalD2Directory;
            SettingsStore.Save(ls);
        }

        var installCts = new CancellationTokenSource();
        _installCts    = installCts;

        BtnPlay.IsEnabled          = false;
        BtnInstall.IsEnabled       = false;
        BtnCancelInstall.IsEnabled = true;
        SyncOverviewPlayButton();
        ProgressArea.Visibility    = Visibility.Visible;
        bool wasInstalled = plugin.IsInstalled;
        bool success      = false;
        ResetDownloadEta();

        try
        {
            // Progress text names the game (UX-4): the progress row lives in
            // the Play tab of WHATEVER game is selected, so browsing another
            // game mid-install used to show anonymous progress on its page.
            var progress = new Progress<(int Pct, string Msg)>(p =>
            {
                ProgressBar.Value = p.Pct;
                TxtProgress.Text  = $"{plugin.DisplayName} — {p.Msg}{ComputeEtaSuffix(p.Pct)}";
                SetStatus($"{plugin.DisplayName}: {p.Msg}");
            });

            await plugin.InstallOrUpdateAsync(progress, installCts.Token);
            await plugin.CheckForUpdateAsync();

            RefreshVersionBadges(plugin);
            RefreshButtons(plugin);
            RefreshWelcomeChecklist();
            RefreshOverview(plugin);   // installed-state badges + action row
            SetStatus("Install complete.");
            ToastService.Show(
                wasInstalled
                    ? $"Game updated to {plugin.InstalledVersion ?? "latest"}"
                    : $"Game installed — {plugin.InstalledVersion ?? "latest"}",
                plugin.DisplayName, ToastKind.Success);
            success = true;

            // Add to library on first install (not on updates).
            // Games are ONLY auto-added to the library when the user installs them —
            // not on startup (startup auto-add was removed to prevent SC2/etc. ghost entries).
            if (!wasInstalled)
            {
                LibraryStore.Add(plugin.GameId);
                RebuildGameList();
            }

            // Achievement ladder: first-time installs only — an update of an
            // already-installed game is not an install.
            if (!wasInstalled)
                AchievementStore.Instance.IncrementCounter(
                    plugin.GameId, AchievementCounters.Installs);

            // Emulated games: BizHawk is now in place, but the header still
            // says "Not installed" until the player points the launcher at a
            // ROM — a step that used to hide in Settings with no pointer
            // (UX-5). Offer the picker right here, while they're looking.
            if (plugin is Plugins.Emulated.EmulatorPlugin emu &&
                (emu.RomPath == null || !File.Exists(emu.RomPath)))
            {
                bool pick = ConfirmDialog.Show(this,
                    "One step left — select your ROM",
                    $"The emulator is ready. To finish setting up {plugin.DisplayName}, " +
                    "point the launcher at your own ROM file (the launcher never " +
                    "ships ROMs). You can also do this later in Settings.",
                    "Select ROM...", "Later");
                if (pick && emu.PromptForRomFile())
                {
                    AppendLog($"[Install] ROM copied into the launcher library: {emu.RomPath}");
                    ToastService.Show("ROM selected",
                        $"{plugin.DisplayName} is ready to play. Your ROM was copied " +
                        "into the launcher library — the original file is untouched.",
                        ToastKind.Success);
                }

                RefreshVersionBadges(plugin);
                RefreshButtons(plugin);
                RefreshWelcomeChecklist();
                RefreshOverview(plugin);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("[Install] Cancelled.");
            SetStatus("Install cancelled.");
            ToastService.Show("Install cancelled",
                $"{plugin.DisplayName} — nothing was marked installed. " +
                "Run the install again to finish.", ToastKind.Info);
        }
        catch (Exception ex)
        {
            string friendly = FriendlyInstallError(ex);
            AppendLog($"[Error] Install failed: {ex.Message}");
            SetStatus("Install failed.");
            ToastService.Show("Install failed", friendly, ToastKind.Error);
        }
        finally
        {
            _installCts = null;
            installCts.Dispose();
            ResetDownloadEta();
            ProgressArea.Visibility = Visibility.Collapsed;
            BtnPlay.IsEnabled       = true;
            BtnInstall.IsEnabled    = true;
            // Re-apply button semantics for the game on screen (it may differ
            // from `plugin`, and the one-game-at-a-time rule may apply).
            if (_selectedPlugin != null)
                RefreshButtons(_selectedPlugin);
        }
        return success;
    }

    /// Converts a raw install exception into an actionable one-liner for the error toast.
    private static string FriendlyInstallError(Exception ex)
    {
        // Walk the exception chain looking for well-known network failure types.
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is System.Net.Sockets.SocketException sock)
                return sock.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut
                    ? "The server took too long to respond. Check your internet connection and try again."
                    : "Could not reach the server. Check your internet connection and try again.";

            if (cur is System.Net.Http.HttpRequestException http)
            {
                if (http.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return "The download URL was not found (404). The release may have been removed.";
                if (http.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                    http.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return "Access denied (HTTP 403/401). GitHub rate-limit or private release.";
                return "Network error: check your internet connection and try again.";
            }

            if (cur is TaskCanceledException or TimeoutException)
                return "The download timed out. Check your internet connection and try again.";

            if (cur is IOException)
                return "Could not write to disk. Check available disk space.";
        }
        return ex.Message;
    }

    /// Cancel button on the install progress row — cancels the in-flight
    /// install's CTS. The install path stamps nothing on cancellation, so a
    /// cancelled install can always be re-run cleanly.
    private void BtnCancelInstall_Click(object sender, RoutedEventArgs e)
    {
        BtnCancelInstall.IsEnabled = false;   // single shot; re-armed next install
        try { _installCts?.Cancel(); } catch (ObjectDisposedException) { }
        SetStatus("Cancelling install...");
        AppendLog("[Install] Cancel requested...");
    }

    // ── Download ETA (rolling window over install progress reports) ───────────

    private void ResetDownloadEta()
    {
        _progressSamples.Clear();
        _etaLastUpdate = DateTime.MinValue;
        _etaSuffix     = "";
    }

    /// <summary>
    /// Returns " · ~3m 20s remaining" while the percentage is advancing, or ""
    /// when stalled/complete. The displayed estimate is recomputed at most once
    /// per second so the text never flickers.
    /// </summary>
    private string ComputeEtaSuffix(int pct)
    {
        var now = DateTime.UtcNow;

        if (pct <= 0 || pct >= 100)
        {
            ResetDownloadEta();
            return "";
        }

        // Rolling window: keep samples from the last ~20 s (hard cap guards
        // against pathological report floods).
        _progressSamples.Enqueue((now, pct));
        while (_progressSamples.Count > 64 ||
               (_progressSamples.Count > 1 &&
                (now - _progressSamples.Peek().Time).TotalSeconds > 20))
            _progressSamples.Dequeue();

        // Throttle: refresh the visible estimate at most once per second
        if ((now - _etaLastUpdate).TotalSeconds < 1.0)
            return _etaSuffix;
        _etaLastUpdate = now;

        var (t0, p0)   = _progressSamples.Peek();
        double seconds = (now - t0).TotalSeconds;
        int    gained  = pct - p0;
        if (seconds < 0.5 || gained <= 0)          // stalled or not enough data yet
        {
            _etaSuffix = "";
            return _etaSuffix;
        }

        double remaining = (100 - pct) * (seconds / gained);
        _etaSuffix = remaining < 1
            ? ""
            : $" · ~{FormatEta(TimeSpan.FromSeconds(remaining))} remaining";
        return _etaSuffix;
    }

    private static string FormatEta(TimeSpan t) =>
        t.TotalHours   >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
      : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
      :                       $"{Math.Max(1, (int)t.TotalSeconds)}s";

    // ══════════════════════════════════════════════════════════════════════════
    // Play / Launch
    // ══════════════════════════════════════════════════════════════════════════

    /// ONE confirm for every stop-the-game path — Play-button Stop, tray menu
    /// (UX-8). Wording covers what is actually at risk: the process is killed,
    /// so anything the game has not saved yet is gone.
    private bool ConfirmStopGame(IGamePlugin plugin)
    {
        bool apLive = _apClient?.State == ApConnectionState.Connected;
        string body = "The game process will be closed" +
                      (apLive ? " and your Archipelago session ends." : ".") +
                      " Unsaved in-game progress may be lost.";
        return ConfirmDialog.Show(this, $"Stop {plugin.DisplayName}?", body,
                                  "Stop game", "Cancel", danger: true);
    }

    private async void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlugin == null) return;

        // One game at a time (P2-5): while a DIFFERENT game runs, this Play
        // button is inert — a second launch would fight the live session.
        // RefreshButtons normally disables it, but guard re-entrantly anyway
        // (the click can race the running state).
        var activeOther = GameRegistry.ActivePlugin;
        if (activeOther != null && !ReferenceEquals(activeOther, _selectedPlugin))
        {
            ToastService.Show("Another game is running",
                $"{activeOther.DisplayName} is still running — stop it before " +
                $"starting {_selectedPlugin.DisplayName}.", ToastKind.Warning);
            return;
        }

        // If a game is currently running — this is the Stop button.
        if (_selectedPlugin.IsRunning)
        {
            if (!ConfirmStopGame(_selectedPlugin)) return;

            BtnPlay.IsEnabled = false;
            BtnPlay.Content   = "Stopping...";
            SyncOverviewPlayButton();
            await _selectedPlugin.StopAsync();
            // CleanupSessionAsync is triggered by GameExited event,
            // but if the plugin exits synchronously fall back here.
            if (_apClient != null) await CleanupSessionAsync();
            return;
        }

        // §10 AutoMod: before anything can be installed the user must point
        // the launcher at their existing original-game install. The button
        // says "Find original game…" in this state — honour exactly that and
        // stop; the next click (now "Install mod") runs the normal install.
        if (!_selectedPlugin.IsInstalled &&
            EffectiveInstallCapability(_selectedPlugin) == InstallCapability.AutoMod &&
            !OriginalInstallRegistered(_selectedPlugin))
        {
            PromptForOriginalGameFolder(_selectedPlugin);
            return;
        }

        // Install if not installed (required). Play NO LONGER auto-updates an
        // already-installed game — updating is opt-in via the separate Update
        // button (BtnOverviewUpdate), so "AP Play" just launches what's there.
        // A failed/cancelled install must not fall through to launch (P2-11/P2-3).
        if (!_selectedPlugin.IsInstalled)
        {
            bool installed = await RunInstallAsync(_selectedPlugin);
            if (!installed || !_selectedPlugin.IsInstalled) return;
        }

        // D2: the install dir must contain the copied original MPQs before launch.
        // If they're missing (copy failed, or the original D2 source moved), make
        // sure we know where the player's own Diablo II is, then re-run the install
        // so the copy step runs again. The original install is never modified.
        if (_selectedPlugin is Plugins.DiabloII.D2Plugin d2launch
            && !d2launch.HasOriginalGameFiles())
        {
            if (!d2launch.IsOriginalD2Configured)
            {
                string? detected2 = d2launch.AutoDetectOriginalD2();
                if (detected2 != null)
                {
                    d2launch.OriginalD2Directory = detected2;
                }
                else
                {
                    bool pick = ConfirmDialog.Show(this,
                        "Locate your original Diablo II",
                        "The launcher needs your Classic Diablo II: Lord of Destruction " +
                        "folder to copy the original game files from. Your original " +
                        "install is never modified.",
                        "Select folder…", "Cancel");
                    if (!pick) return;

                    var dlg2 = new Microsoft.Win32.OpenFolderDialog
                    {
                        Title            = "Select your Classic Diablo II: Lord of Destruction folder",
                        InitialDirectory = @"C:\Program Files (x86)",
                    };
                    if (dlg2.ShowDialog(this) != true) return;

                    string? err2 = d2launch.ValidateExistingInstall(dlg2.FolderName);
                    if (err2 != null)
                    {
                        ConfirmDialog.ShowInfo(this, "Folder not recognized", err2);
                        return;
                    }
                    d2launch.OriginalD2Directory = dlg2.FolderName;
                }

                var ls2 = SettingsStore.Load();
                ls2.DiabloIIPath = d2launch.OriginalD2Directory;
                SettingsStore.Save(ls2);
            }

            // Re-run install so the original files get copied into the mod folder.
            bool reinstalled = await RunInstallAsync(d2launch);
            if (!reinstalled || !d2launch.IsInstalled) return;
        }

        // Switch to Play tab so the log is visible
        SwitchTab(PageTab.Play);
        await LaunchGameAsync(_selectedPlugin);
    }

    // ── Launch Standalone (no AP) ─────────────────────────────────────────────

    private async void BtnStandalone_Click(object sender, RoutedEventArgs e)
    {
        var plugin = _selectedPlugin;
        if (plugin == null || !plugin.SupportsStandalone) return;

        if (!plugin.IsInstalled)
        {
            ConfirmDialog.ShowInfo(this, "Not installed",
                "The game is not installed yet — use the Play button to " +
                "install it first.");
            return;
        }

        if (plugin.IsRunning)
        {
            ConfirmDialog.ShowInfo(this, "Already running",
                "The game is already running.");
            return;
        }

        // One game at a time (P2-5) — also applies to standalone launches.
        var activeOther = GameRegistry.ActivePlugin;
        if (activeOther != null && !ReferenceEquals(activeOther, plugin))
        {
            ConfirmDialog.ShowInfo(this, "Another game is running",
                $"{activeOther.DisplayName} is still running — stop it before " +
                "starting another game.");
            return;
        }

        // Antivirus often deletes D2Arch_Launcher.exe — offer a one-click repair
        // before dead-ending at launch (buttons not yet disabled, so cancel is clean).
        if (!await EnsureD2ModFilesAsync(plugin)) return;

        BtnStandalone.IsEnabled = false;
        BtnPlay.IsEnabled       = false;
        SyncOverviewPlayButton();
        SwitchTab(PageTab.Play);
        SetStatus("Launching standalone...");
        AppendLog("[Standalone] Starting game without Archipelago connection...");

        try
        {
            SetStatus("Verifying install...");
            AppendLog("[Verify] Checking install integrity...");
            bool vOk = await plugin.VerifyInstallAsync();
            AppendLog(vOk ? "[Verify] OK." : "[Verify] WARNING: some files may be missing. Consider re-installing.");

            await plugin.LaunchStandaloneAsync();

            // Exit watcher: standalone sessions never go through LaunchGameAsync,
            // so without this nothing would ever re-enable the two buttons (a
            // fresh launcher run that only used Launch Standalone dead-ended
            // them until restart). Unsubscribe-then-subscribe = idempotent.
            _standalonePlugin  = plugin;
            _standaloneStart   = AchievementStore.Instance.BeginSession(
                plugin.GameId, server: null, slot: null);
            plugin.GameExited -= OnStandaloneGameExited;
            plugin.GameExited += OnStandaloneGameExited;

            // Tracker for standalone: the plugin streams CHECK: over its pipe even
            // with no AP server, so wire the same LocationsChecked → tracker path
            // the AP launch uses. _runningPlugin lets OnPluginLocationsChecked
            // attribute checks; the Changed subscription refreshes the tabs.
            _runningPlugin = plugin;
            _locationTracker.Clear();
            // Feed the bundled location id→name table (no AP server to deliver a
            // DataPackage), so standalone checks show real names + categories.
            if (plugin is Plugins.DiabloII.D2Plugin d2track
                && d2track.GetLocationDataPackage() is { } d2data)
            {
                _locationTracker.OnDataPackage(plugin.ApWorldName, d2data);
                // No AP server → derive the FULL active location universe ourselves
                // from the [settings] we just wrote + that table, so the tracker
                // shows every UNCHECKED location + per-category totals (not only the
                // checks that have fired) — exactly like an AP session. Standalone
                // runs all difficulties (g_apMode=FALSE in the mod), so the universe
                // is purely a function of the settings.
                long[] universe = Plugins.DiabloII.D2LocationUniverse.ComputeActiveIds(
                    d2track.GetStandaloneSettings(), d2data);
                if (universe.Length > 0) _locationTracker.OnMissingLocations(universe);
            }
            _locationTracker.Changed -= OnLocationTrackerChanged;
            _locationTracker.Changed += OnLocationTrackerChanged;
            plugin.LocationsChecked  -= OnPluginLocationsChecked;
            plugin.LocationsChecked  += OnPluginLocationsChecked;
            if (plugin is Plugins.DiabloII.D2Plugin d2miss)
            {
                d2miss.LocationsMissing -= OnPluginLocationsMissing;
                d2miss.LocationsMissing += OnPluginLocationsMissing;
            }
            OnLocationTrackerChanged();   // reset the tab counters to 0 for the new run

            _trayIcon.Show($"Playing {plugin.DisplayName} (standalone)");
            SetStatus("Game running (standalone).");
            AppendLog("[Standalone] Game launched. No AP connection active.");
        }
        catch (OperationCanceledException)
        {
            // The user closed the pre-launch options dialog (e.g. D2's
            // randomizer settings) — abort quietly, no error popup.
            SetStatus("Standalone launch cancelled.");
            AppendLog("[Standalone] Cancelled before launch.");
            BtnStandalone.IsEnabled = true;
            BtnPlay.IsEnabled       = true;
            SyncOverviewPlayButton();
        }
        catch (Exception ex)
        {
            SetStatus($"Standalone launch failed: {ex.Message}");
            AppendLog($"[Error] {ex.Message}");
            // If Windows Defender blocked the mod exe, offer to add a Defender
            // exclusion instead of just showing the dead-end error.
            if (!await TryOfferDefenderExclusionAsync(plugin, ex))
                ConfirmDialog.ShowInfo(this, "Could not launch the game", ex.Message);
            BtnStandalone.IsEnabled = true;
            BtnPlay.IsEnabled       = true;
            SyncOverviewPlayButton();
        }
    }

    /// Standalone session ended — record playtime, restore the Play/Standalone
    /// buttons, drop the tray icon. Fires on the process-exit thread.
    private void OnStandaloneGameExited(int exitCode) => Dispatcher.Invoke(() =>
    {
        var plugin = _standalonePlugin;
        if (plugin == null) return;
        _standalonePlugin  = null;
        plugin.GameExited -= OnStandaloneGameExited;

        // Unwire the standalone tracker feed (the tracker keeps its final state
        // on screen until the next session clears it).
        plugin.LocationsChecked -= OnPluginLocationsChecked;
        if (plugin is Plugins.DiabloII.D2Plugin d2miss)
            d2miss.LocationsMissing -= OnPluginLocationsMissing;
        if (ReferenceEquals(_runningPlugin, plugin)) _runningPlugin = null;

        // Playtime accounting — standalone sessions count too (no AP server,
        // no slot, goal never reached).
        if (_standaloneStart != default)
        {
            AchievementStore.Instance.EndSession(
                plugin.GameId, _standaloneStart, goalReached: false,
                server: null, slotName: null, playerCount: 1);
            _standaloneStart = default;
        }

        _trayIcon.Hide();
        SetStatus($"Game exited (code {exitCode}).");
        AppendLog("[Standalone] Game exited.");

        BtnPlay.IsEnabled       = true;
        BtnStandalone.IsEnabled = true;
        if (_selectedPlugin != null)
        {
            RefreshButtons(_selectedPlugin);
            RefreshPlaytimeBadge(_selectedPlugin);
            RefreshOverview(_selectedPlugin);   // standalone sessions count too
        }
    });

    // ── Canonical Archipelago links — sidebar bottom row (§13) ────────────────
    // THE home of these three links in the launcher; nothing else hardcodes them.

    private void BtnLinkWebsite_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://archipelago.gg");

    private void BtnLinkDiscord_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://discord.gg/8Z65BR2");

    private void BtnLinkWiki_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://archipelago.miraheze.org/");

    /// "Find these on your room page at archipelago.gg" helper link in the
    /// connect panel (UX-2) — the room page is where server/slot/password live.
    private void RoomPageLink_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://archipelago.gg");

    // ── Launcher self-update banner ───────────────────────────────────────────

    private async void BtnInstallLauncherUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnInstallLauncherUpdate.IsEnabled = false;
        BtnInstallLauncherUpdate.Content   = "Downloading...";
        AppendLog("[Update] Downloading launcher update...");

        // DownloadProgress is subscribed ONCE in OnLoaded (P3-13) — a per-click
        // subscription duplicated every progress line after a failed retry.

        try
        {
            await _launcherUpdater.DownloadAndApplyAsync();
            // If we get here the shutdown didn't fire (unlikely) — just report
        }
        catch (Exception ex)
        {
            AppendLog($"[Update] Failed: {ex.Message}");
            BtnInstallLauncherUpdate.IsEnabled = true;
            BtnInstallLauncherUpdate.Content   = "Install Update";
        }
    }

    private void BtnDismissLauncherBanner_Click(object sender, RoutedEventArgs e)
        => BannerLauncherUpdate.Visibility = Visibility.Collapsed;

    // ── AP end-of-game command buttons ────────────────────────────────────────

    // Release/Collect are ONE-CLICK IRREVERSIBLE multiworld actions — a
    // misclick used to dump a player's entire remaining world with no way
    // back (UX-9), so both now confirm through the themed dialog.

    private async void BtnRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_apClient == null) return;
        if (!ConfirmDialog.Show(this,
                "Release your remaining items?",
                "This sends ALL items still hidden in your world to their owners. " +
                "It cannot be undone — players normally only release after " +
                "finishing (or abandoning) their run.",
                "Release items", "Cancel", danger: true))
            return;
        try { await _apClient.ReleaseAsync(); AppendLog("[AP] !release sent."); }
        catch (Exception ex) { AppendLog($"[Error] Release failed: {ex.Message}"); }
    }

    private async void BtnCollect_Click(object sender, RoutedEventArgs e)
    {
        if (_apClient == null) return;
        if (!ConfirmDialog.Show(this,
                "Collect your items?",
                "This pulls every item that belongs to you out of all other " +
                "players' worlds at once. It cannot be undone.",
                "Collect items", "Cancel"))
            return;
        try { await _apClient.CollectAsync(); AppendLog("[AP] !collect sent."); }
        catch (Exception ex) { AppendLog($"[Error] Collect failed: {ex.Message}"); }
    }

    // (The Forfeit button was removed — P2-20: the server renamed !forfeit to
    //  !release, so it duplicated Release. ApClient.ForfeitAsync stays as a
    //  compatibility alias for any external callers.)

    // ══════════════════════════════════════════════════════════════════════════
    // AP ecosystem features — DeathLink / Ready / countdown / item-send toasts /
    // goal banners / hint backlog. Wired via WireApFeatureEvents at BOTH
    // ApClient subscription sites (global sidebar connect + Play-button path).
    // ══════════════════════════════════════════════════════════════════════════

    /// Wire the AP ecosystem feature events on a freshly created ApClient.
    /// Every handler marshals to the UI thread; the "_apClient != ap" guard
    /// drops late events from a previous session's client during teardown.
    private void WireApFeatureEvents(ApClient ap)
    {
        // ── Check replay (P2-8): flush the session's pending-check buffer on
        //    every successful login. This client may be a reconnect replacement
        //    that never saw the checks recorded under its predecessor — without
        //    the flush those checks are permanently lost (the new client's Sync
        //    only replays its OWN set). Runs on the receive thread; the server
        //    ignores duplicate checks. ───────────────────────────────────────
        ap.SessionConnected += (_, _) =>
        {
            if (_apClient != ap) return;
            long[] replay;
            lock (_pendingChecksLock) replay = _pendingChecks.ToArray();
            if (replay.Length > 0)
                _ = ap.SendLocationsCheckedAsync(replay);

            // Mid-game reconnect: the fresh client's handshake just reported
            // Connected(5), silently downgrading a LIVE session from
            // Playing(20) until the next launch (P3-17). Re-assert Playing
            // whenever a game session is running.
            if (GameRegistry.ActivePlugin != null)
                _ = ap.SetStatusAsync(ClientStatus.Playing);
        };

        // ── DeathLink deaths from other players ──────────────────────────────
        ap.DeathLinkReceived += (source, cause) => Dispatcher.Invoke(() =>
        {
            if (_apClient != ap) return;
            bool noCause = string.IsNullOrWhiteSpace(cause);
            AppendLog(noCause ? $"[DeathLink] {source} died"
                              : $"[DeathLink] {source}: {cause}",
                      (Brush)FindResource("BrushError"));
            ToastService.Show("DeathLink", noCause ? $"{source} died" : cause,
                              ToastKind.Warning);
            PlayNotifySound("death");

            // Forward into the game (DEATHLINK: pipe message). The DLL always
            // shows an in-game notification; the actual kill ships dark behind
            // d2arch.ini [settings] DeathLinkReceive=1. Target = the RUNNING
            // session's plugin (P2-6) — browsing another sidebar game must not
            // stop deaths from reaching the game.
            if (_runningPlugin is Plugins.DiabloII.D2Plugin d2Run && d2Run.IsRunning)
                _ = d2Run.SendDeathLinkToGameAsync(source, cause);
        });

        // ── Server countdown — one tick per second, 0 = GO ───────────────────
        ap.CountdownTick += n => Dispatcher.Invoke(() =>
        {
            if (_apClient != ap) return;
            ShowCountdownTick(n);
        });

        // ── ItemSend routing: trap warnings + progression feed for OUR slot.
        //    PrintMessage already logs the full server text — no plain re-log. ──
        ap.ItemSendReceived += (receivingSlot, sendingSlot, itemId, itemFlags, locationId)
            => Dispatcher.Invoke(() =>
        {
            if (_apClient != ap || receivingSlot != ap.Slot) return;

            string item = _dpItemNames.GetValueOrDefault(itemId, $"Item #{itemId}");
            string from = ResolveApPlayerName(sendingSlot);

            if ((itemFlags & 0b100) != 0)            // trap
            {
                if (TryBeginItemToast())
                    ToastService.Show("Trap incoming!", $"{item} from {from}",
                                      ToastKind.Error);
                PlayNotifySound("trap");
            }
            else if ((itemFlags & 0b001) != 0)       // progression
            {
                AppendLog($"[Progression] {item} from {from}",
                          (Brush)FindResource("BrushAccent"));
                PlayNotifySound("progression");
            }
        });

        // ── Goal / Release / Collect announcements ───────────────────────────
        // (Our own goal handling — SendStatusUpdateAsync(Goal) — stays untouched;
        //  these banners are purely additive.)
        ap.GoalAnnounced += slot => Dispatcher.Invoke(() =>
        {
            if (_apClient != ap) return;
            if (slot == ap.Slot)
            {
                ToastService.Show("Goal complete!",
                    "Your goal is complete — congratulations!", ToastKind.Success);
                PlayNotifySound("goal");
            }
            else
                ToastService.Show("Goal reached",
                    $"{ResolveApPlayerName(slot)} reached their goal", ToastKind.Info);
        });
        ap.ReleaseAnnounced += slot => Dispatcher.Invoke(() =>
        {
            if (_apClient != ap) return;
            AppendLog($"[Release] {ResolveApPlayerName(slot)} released their remaining items");
        });
        ap.CollectAnnounced += slot => Dispatcher.Invoke(() =>
        {
            if (_apClient != ap) return;
            AppendLog($"[Collect] {ResolveApPlayerName(slot)} collected their items");
        });

        // ── Hint backlog (data storage Retrieved + live SetReply updates).
        //    The JsonElement is a clone (safe to hold) — park it until our
        //    game's DataPackage name maps have arrived. ──────────────────────
        ap.HintsRetrieved += hints => Dispatcher.Invoke(() =>
        {
            if (_apClient != ap) return;
            if (_dpNamesReady) IngestHintBacklog(hints);
            else               _pendingHintBacklog = hints;
        });

        // ── D2 slot-data hand-off: whenever a session authenticates while D2
        //    is the running game (or, with no game running, the selected one),
        //    refresh ap_settings.dat. Covers the connect-AFTER-launch order and
        //    mid-session reconnects (the pre-launch write in LaunchGameAsync
        //    covers the launch order). Binding to the RUNNING plugin keeps the
        //    refresh alive while the user browses other games (P2-6). ─────────
        ap.SessionConnected += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_apClient != ap) return;
            if ((_runningPlugin ?? _selectedPlugin) is Plugins.DiabloII.D2Plugin d2Sd &&
                ap.SlotData is JsonElement sd)
                d2Sd.WriteApSettingsFile(sd);

            // ── Upstream-update detector (§15): warn when the server's
            //    datapackage checksum for this game differs from the one our
            //    integration was built against — the apworld changed upstream
            //    and the RAM map / patch logic may be stale. ─────────────────
            var stampPlugin = _runningPlugin ?? _selectedPlugin;
            string? builtAgainst = stampPlugin?.BuiltAgainstDataPackageChecksum;
            if (stampPlugin != null && builtAgainst != null &&
                ap.DataPackageChecksums.TryGetValue(stampPlugin.ApWorldName, out var serverSum) &&
                !string.Equals(serverSum, builtAgainst, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"[Update] {stampPlugin.DisplayName}'s world data has " +
                          $"been updated upstream (server {serverSum[..8]}…, " +
                          $"built against {builtAgainst[..8]}…).",
                          (Brush)FindResource("BrushError"));
                ToastService.Show("Game data updated upstream",
                    $"{stampPlugin.DisplayName}'s Archipelago world has changed " +
                    "since this launcher version — check detection may be " +
                    "incomplete until the launcher updates.", ToastKind.Warning);
            }
        });

        // ── DataPackage id→name maps (same source the trackers use) ──────────
        ap.DataPackageReceived += (gameKey, data) =>
        {
            // Extract on the receive thread while the JsonElement is alive
            // (ApClient does not clone it for this event)…
            string? ourGame = ap.ConnectedGame;
            if (ourGame != null &&
                !string.Equals(gameKey, ourGame, StringComparison.OrdinalIgnoreCase))
                return;

            var items = new Dictionary<long, string>();
            var locs  = new Dictionary<long, string>();
            if (data.TryGetProperty("item_name_to_id", out var itemMap))
                foreach (var kv in itemMap.EnumerateObject())
                    items[kv.Value.GetInt64()] = kv.Name;
            if (data.TryGetProperty("location_name_to_id", out var locMap))
                foreach (var kv in locMap.EnumerateObject())
                    locs[kv.Value.GetInt64()] = kv.Name;

            // …then publish on the UI thread (the maps are UI-thread-only).
            Dispatcher.Invoke(() =>
            {
                if (_apClient != ap) return;
                foreach (var kv in items) _dpItemNames[kv.Key]     = kv.Value;
                foreach (var kv in locs)  _dpLocationNames[kv.Key] = kv.Value;
                _dpNamesReady = true;
                RenormalizeHints();                  // hints that arrived early
                if (_pendingHintBacklog is JsonElement parked)
                {
                    _pendingHintBacklog = null;
                    IngestHintBacklog(parked);
                }
            });
        };
    }

    // ── DeathLink toggle ──────────────────────────────────────────────────────

    private async void BtnDeathLink_Click(object sender, RoutedEventArgs e)
    {
        if (_apClient == null) return;
        bool enable = !_apClient.DeathLinkEnabled;
        BtnDeathLink.IsEnabled = false;
        try
        {
            await _apClient.SetDeathLinkAsync(enable);
            // Remember the explicit choice — the next connect primes the
            // "DeathLink" tag from this flag before the handshake.
            var s = SettingsStore.Load();
            s.DeathLinkEnabled = enable;
            SettingsStore.Save(s);
            AppendLog(enable
                ? "[DeathLink] ON — deaths are now shared with other DeathLink players."
                : "[DeathLink] OFF — deaths are no longer shared.");
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] DeathLink toggle failed: {ex.Message}");
        }
        finally
        {
            BtnDeathLink.IsEnabled = true;
            RefreshDeathLinkButton();   // reflect the post-call client state
        }
    }

    /// Sync the DeathLink button visual to the client state — red-tinted + bold
    /// while ON, standard muted secondary look while OFF. Session-scoped: the
    /// state lives on the ApClient, so a disconnect resets the button to OFF.
    private void RefreshDeathLinkButton()
    {
        bool on = _apClient?.DeathLinkEnabled == true;
        BtnDeathLink.FontWeight = on ? FontWeights.Bold : FontWeights.SemiBold;
        BtnDeathLink.Background = on
            ? new SolidColorBrush(Color.FromArgb(0x30, 0xD9, 0x4A, 0x4A))
            : new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30));
        BtnDeathLink.BorderBrush = on
            ? (Brush)FindResource("BrushError")
            : new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x60));
        BtnDeathLink.Foreground = on
            ? (Brush)FindResource("BrushError")
            : (Brush)FindResource("BrushMuted");
        BtnDeathLink.ToolTip = on
            ? "DeathLink is ON — your deaths are shared and other players' deaths reach you. Click to turn off."
            : "DeathLink is OFF — click to share deaths with other DeathLink players.";
    }

    // ── Ready toggle (StatusUpdate: Ready ↔ Connected) ────────────────────────

    private async void BtnReady_Click(object sender, RoutedEventArgs e)
    {
        if (_apClient?.State != ApConnectionState.Connected) return;
        bool ready = !_apReadySent;
        BtnReady.IsEnabled = false;
        try
        {
            await _apClient.SetStatusAsync(ready ? ClientStatus.Ready
                                                 : ClientStatus.Connected);
            _apReadySent = ready;
            AppendLog(ready ? "[AP] Status: Ready — waiting for the other players."
                            : "[AP] Status: Connected — ready state cleared.");
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Ready toggle failed: {ex.Message}");
        }
        finally
        {
            BtnReady.IsEnabled = true;
            RefreshReadyButton();
        }
    }

    /// Sync the Ready row + button. Visible only while connected with no game
    /// session running (the launch flow owns the status from then on); pressed
    /// state = gold border + bold while Ready.
    private void RefreshReadyButton()
    {
        bool connected = _apClient?.State == ApConnectionState.Connected;
        // "In session" means ANY game is running (P2-6) — the launch flow owns
        // the AP status from then on, regardless of which game the sidebar shows.
        bool inSession = GameRegistry.ActivePlugin != null;
        PanelReadyRow.Visibility = connected && !inSession
            ? Visibility.Visible : Visibility.Collapsed;

        BtnReady.Content    = _apReadySent ? "✔ Ready" : "Ready up";
        BtnReady.FontWeight = _apReadySent ? FontWeights.Bold : FontWeights.SemiBold;
        BtnReady.BorderBrush = _apReadySent
            ? (Brush)FindResource("BrushAccent")
            : new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x60));
        BtnReady.Foreground = _apReadySent
            ? (Brush)FindResource("BrushAccent")
            : (Brush)FindResource("BrushText");
        BtnReady.ToolTip = _apReadySent
            ? "You are marked Ready (StatusUpdate: Ready) — click to go back to Connected."
            : "Tell the multiworld you are ready to start (StatusUpdate: Ready)";
    }

    // ── Countdown ─────────────────────────────────────────────────────────────

    private void BtnCountdown_Click(object sender, RoutedEventArgs e)
        => SendChatText("!countdown 10");

    /// One tick of a server countdown: re-fade the big overlay number. The log
    /// only gets the start value and GO — the overlay carries the per-second
    /// display (the raw server text lines still flow through PrintMessage).
    private void ShowCountdownTick(int n)
    {
        int seq = ++_countdownSeq;

        if (CountdownOverlay.Visibility != Visibility.Visible && n > 0)
            AppendLog($"[Countdown] {n}");
        if (n == 0)
            AppendLog("[Countdown] GO!");

        TxtCountdownOverlay.Text       = n > 0 ? n.ToString() : "GO!";
        TxtCountdownOverlay.Foreground =
            (Brush)FindResource(n == 0 ? "BrushSuccess" : "BrushAccent");
        CountdownOverlay.Visibility    = Visibility.Visible;

        // Pop in at full opacity, hold past the next server tick (ticks arrive
        // ~1 s apart — the 900 ms hold + 300 ms fade leaves jitter headroom),
        // then fade 300 ms.
        TxtCountdownOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        TxtCountdownOverlay.Opacity = 1.0;
        var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(300))
        {
            BeginTime = TimeSpan.FromMilliseconds(900),
        };
        fade.Completed += (_, _) =>
        {
            if (seq == _countdownSeq)   // stale fade must not hide a newer tick
                CountdownOverlay.Visibility = Visibility.Collapsed;
        };
        TxtCountdownOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ── Item-send toast rate limit ────────────────────────────────────────────

    /// Max 3 item toasts in any rolling 2 s window — returns false when the
    /// toast should be suppressed. Suppressed toasts collapse into a single
    /// "N more items" summary once the burst quiets down (a release dumping
    /// dozens of traps would otherwise churn the whole toast stack).
    private bool TryBeginItemToast()
    {
        var now = DateTime.UtcNow;
        while (_itemToastTimes.Count > 0 &&
               now - _itemToastTimes.Peek() > TimeSpan.FromSeconds(2))
            _itemToastTimes.Dequeue();

        if (_itemToastTimes.Count < 3)
        {
            _itemToastTimes.Enqueue(now);
            return true;
        }

        _itemToastSuppressed++;
        if (_itemToastBurstTimer == null)
        {
            _itemToastBurstTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _itemToastBurstTimer.Tick += (_, _) =>
            {
                _itemToastBurstTimer!.Stop();
                if (_itemToastSuppressed <= 0) return;
                ToastService.Show("Items incoming",
                    $"{_itemToastSuppressed} more item{(_itemToastSuppressed == 1 ? "" : "s")} arrived for you.",
                    ToastKind.Info);
                _itemToastSuppressed = 0;
            };
        }
        _itemToastBurstTimer.Stop();
        _itemToastBurstTimer.Start();
        return false;
    }

    // ── Shared name resolution ────────────────────────────────────────────────

    /// Player display name for toasts/log lines — alias first, then real name,
    /// then a generic slot label (matches how the trackers resolve players).
    private string ResolveApPlayerName(int slot)
    {
        var p = _apClient?.Players.FirstOrDefault(pl => pl.Slot == slot);
        return p?.Alias is { Length: > 0 } alias ? alias
             : p?.Name  is { Length: > 0 } name  ? name
             : $"Slot {slot}";
    }

    /// Status-bar 💡 hint text. The cost shown is ApClient.HintCostPoints —
    /// the ACTUAL point price of one hint — never the raw hint_cost percentage.
    private string FormatHintPoints(int pts)
    {
        int cost = _apClient?.HintCostPoints ?? 0;
        if (cost <= 0) return $"💡 Hints: free · {pts} pts";
        int available = pts / cost;
        return $"💡 Hints: {available} available · {pts}/{cost} pts";
    }

    // ── Hint pipeline (live PrintJSON + data-storage backlog) ─────────────────

    /// Dedup key shared by live hints and the backlog.
    private static string HintKey(HintEntry h)
        => $"{h.ReceiverName}\x00{h.ItemName}\x00{h.LocationName}\x00{h.SenderName}";

    /// Shared live-hint ingest used by BOTH ApClient subscription sites.
    /// Normalizes raw numeric tokens to display names first so live entries
    /// land on the same dedup keys as backlog entries. UI thread only.
    private void AddHintEntryToPanel(HintEntry hint)
    {
        var entry = NormalizeHintEntry(hint);
        if (!_hintKeys.Add(HintKey(entry))) return;
        _hints.Add(entry);
        if (_currentTab == PageTab.Progression)
            RefreshProgressionPanel();
    }

    /// Live PrintJSON hints carry raw numeric ids as display text (the AP
    /// server sends "7000"-style tokens — clients resolve names from the
    /// DataPackage). Rebuild the entry with resolved names where possible.
    private HintEntry NormalizeHintEntry(HintEntry raw)
    {
        string item     = ResolveIdToken(raw.ItemName,     _dpItemNames,     "Item");
        string location = ResolveIdToken(raw.LocationName, _dpLocationNames, "Location");
        string receiver = ResolveSlotToken(raw.ReceiverName);
        string sender   = ResolveSlotToken(raw.SenderName);

        if (item == raw.ItemName && location == raw.LocationName &&
            receiver == raw.ReceiverName && sender == raw.SenderName)
            return raw;                                   // nothing to resolve

        return new HintEntry
        {
            Timestamp    = raw.Timestamp,
            ReceiverName = receiver,
            ItemName     = item,
            SenderName   = sender,
            LocationName = location,
            IsChecked    = raw.IsChecked,
            ItemFlags    = raw.ItemFlags,
            RawText      = raw.RawText,
            IsForMe      = raw.IsForMe,
            IsOurs       = raw.IsOurs,
            FindingSlot  = raw.FindingSlot,
            LocationId   = raw.LocationId,
            Status       = raw.Status,
        };
    }

    /// "7000" or "Item #7000" → DataPackage name when known; otherwise as-is.
    private static string ResolveIdToken(string token, Dictionary<long, string> names,
                                         string kind)
    {
        string digits = token;
        if (token.StartsWith(kind + " #", StringComparison.Ordinal))
            digits = token[(kind.Length + 2)..];
        return long.TryParse(digits, out long id) && names.TryGetValue(id, out var name)
            ? name : token;
    }

    /// "3" (raw slot number from a player_id part) → player alias when known.
    private string ResolveSlotToken(string token)
        => int.TryParse(token, out int slot) ? ResolveApPlayerName(slot) : token;

    /// Re-resolve numeric tokens in already-ingested hints (live hints can
    /// arrive before the DataPackage). Rebuilds the dedup key set; entries
    /// that resolve onto the same key collapse into one.
    private void RenormalizeHints()
    {
        if (_hints.Count == 0) return;
        var old = _hints.ToList();
        _hints.Clear();
        _hintKeys.Clear();
        bool changed = false;
        foreach (var h in old)
        {
            var entry = NormalizeHintEntry(h);
            changed |= !ReferenceEquals(entry, h);
            if (_hintKeys.Add(HintKey(entry))) _hints.Add(entry);
            else changed = true;                 // collapsed a duplicate
        }
        if (changed && _currentTab == PageTab.Progression)
            RefreshProgressionPanel();
    }

    /// Ingest the slot's full hint list from the data storage API (connect-time
    /// Retrieved + every live SetReply). New hints join the panel through the
    /// same key dedup as live PrintJSON hints; known hints get their mutable
    /// state (found/status) re-synced and their numeric identity back-filled,
    /// which is what lights up the ★/✕ priority buttons. UI thread only.
    private void IngestHintBacklog(JsonElement hintsArray)
    {
        if (_apClient == null || hintsArray.ValueKind != JsonValueKind.Array) return;

        var byKey = _hints.ToDictionary(HintKey, h => h);
        bool dirty = false;

        foreach (var h in hintsArray.EnumerateArray())
        {
            try
            {
                int  receivingPlayer = h.TryGetProperty("receiving_player", out var rp) ? rp.GetInt32() : 0;
                int  findingPlayer   = h.TryGetProperty("finding_player",   out var fp) ? fp.GetInt32() : 0;
                long locationId      = h.TryGetProperty("location",   out var lo) ? lo.GetInt64()  : 0;
                long itemId          = h.TryGetProperty("item",       out var it) ? it.GetInt64()  : 0;
                bool found           = h.TryGetProperty("found",      out var fo) && fo.GetBoolean();
                int  itemFlags       = h.TryGetProperty("item_flags", out var fl) ? fl.GetInt32()  : 0;
                int  status          = h.TryGetProperty("status",     out var st) ? st.GetInt32()  : 0;
                string entrance      = h.TryGetProperty("entrance",   out var en)
                                       ? en.GetString() ?? "" : "";

                string receiverName = ResolveApPlayerName(receivingPlayer);
                string senderName   = ResolveApPlayerName(findingPlayer);
                string itemName     = _dpItemNames.GetValueOrDefault(itemId, $"Item #{itemId}");
                string locationName = _dpLocationNames.GetValueOrDefault(locationId, $"Location #{locationId}");

                string key = $"{receiverName}\x00{itemName}\x00{locationName}\x00{senderName}";

                if (_hintKeys.Add(key))
                {
                    var entry = new HintEntry
                    {
                        ReceiverName = receiverName,
                        ItemName     = itemName,
                        SenderName   = senderName,
                        LocationName = locationName,
                        IsChecked    = found,
                        ItemFlags    = itemFlags,
                        RawText      = entrance.Length > 0
                            ? $"{receiverName}'s {itemName} is at {locationName} in {senderName}'s world (entrance: {entrance})."
                            : $"{receiverName}'s {itemName} is at {locationName} in {senderName}'s world.",
                        IsForMe      = receivingPlayer == _apClient.Slot,
                        IsOurs       = findingPlayer   == _apClient.Slot,
                        FindingSlot  = findingPlayer,
                        LocationId   = locationId,
                        Status       = status,
                    };
                    _hints.Add(entry);
                    byKey[key] = entry;
                    dirty = true;
                }
                else if (byKey.TryGetValue(key, out var existing))
                {
                    if (existing.IsChecked != found)  { existing.IsChecked = found;  dirty = true; }
                    if (existing.Status    != status) { existing.Status    = status; dirty = true; }
                    if (existing.LocationId == 0)     // back-fill numeric identity
                    {
                        existing.FindingSlot = findingPlayer;
                        existing.LocationId  = locationId;
                        dirty = true;
                    }
                }
            }
            catch { /* malformed hint object — skip it */ }
        }

        if (dirty && _currentTab == PageTab.Progression)
            RefreshProgressionPanel();
    }

    /// Send an UpdateHint for one of OUR hints. Optimistic local update — the
    /// server's SetReply for the hints key re-syncs authoritatively.
    private async Task SetHintStatusAsync(HintEntry hint, int status)
    {
        if (_apClient?.State != ApConnectionState.Connected) return;
        try
        {
            await _apClient.UpdateHintStatusAsync(hint.FindingSlot, hint.LocationId, status);
            hint.Status = status;
            AppendLog($"[Hint] {hint.ItemName}: marked " +
                      (status == ApClient.HintStatusPriority ? "PRIORITY." : "AVOID."));
            if (_currentTab == PageTab.Progression)
                RefreshProgressionPanel();
        }
        catch (Exception ex)
        {
            AppendLog($"[Hint] Status update failed: {ex.Message}");
        }
    }

    // ── Sidebar library search ────────────────────────────────────────────────

    private void TxtSidebarSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Placeholder overlay (P3-7) — the box used to be an unexplained dark
        // rectangle (its Tag-based hint was never rendered by anything).
        if (TxtSidebarSearchHint != null)
            TxtSidebarSearchHint.Visibility = string.IsNullOrEmpty(TxtSidebarSearch.Text)
                ? Visibility.Visible : Visibility.Collapsed;

        string query = TxtSidebarSearch.Text.Trim().ToLowerInvariant();
        foreach (UIElement child in GameListPanel.Children)
        {
            // Each sidebar game card is a Border with Tag = gameId (string)
            if (child is FrameworkElement fe)
            {
                bool visible = string.IsNullOrEmpty(query)
                    || (fe.Tag is string tag && tag.ToLowerInvariant().Contains(query))
                    || (fe.ToolTip is string tip && tip.ToLowerInvariant().Contains(query));
                fe.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    // ── Plugin AP-bridge handlers (named so they can be unsubscribed) ─────────
    // These run on the plugin's pipe/process threads — they must never throw.
    // _apClient is read null-safely: a null client (manual disconnect while the
    // game runs) downgrades to "buffered in _pendingChecks, flushed by the next
    // successful (re)connect" rather than an NRE that kills the plugin's read
    // loop (P2-8).

    private void OnPluginLocationsChecked(long[] ids)
    {
        // Record FIRST, send second — the send can silently die on a dying
        // socket, and a reconnect builds a new ApClient that never saw the
        // old instance's replay set. The buffer closes both holes.
        int newChecks = 0;
        lock (_pendingChecksLock)
            foreach (long id in ids)
                if (_pendingChecks.Add(id)) newChecks++;

        _ = _apClient?.SendLocationsCheckedAsync(ids);
        // Standalone (no AP server delivered the location universe) → add unseen
        // ids on the fly so a solo run's checks still populate the tracker. The
        // bundled D2 location table (fed via OnDataPackage at session start) gives
        // them real names + categories.
        _locationTracker.OnLocationsChecked(ids, addUnknown: _standalonePlugin != null);

        // Achievement ladder: count only ids NEW to this session (the game can
        // re-report on reload). Credited to the RUNNING game (P2-6) — the
        // store call is thread-safe and never throws on this pipe thread.
        if (newChecks > 0 && _runningPlugin is { } checkedPlugin)
            AchievementStore.Instance.IncrementCounter(
                checkedPlugin.GameId, AchievementCounters.ChecksSent, newChecks);
    }

    /// Standalone only: the game streamed its full active location universe so
    /// the tracker can show unchecked locations + per-category totals like AP.
    private void OnPluginLocationsMissing(long[] ids)
        => _locationTracker.OnMissingLocations(ids);

    private void OnPluginGameExited(int code)
        => Dispatcher.Invoke(() => OnGameExited(code));

    private void OnPluginGoalCompleted()
    {
        // Achievement ladder: one goal credit per session (the flag below
        // dedupes a re-fired GOAL message), booked on the RUNNING game.
        if (!_goalReachedThisSession && _runningPlugin is { } goalPlugin)
        {
            AchievementStore.Instance.IncrementCounter(
                goalPlugin.GameId, AchievementCounters.Goals);

            // Mark this seed's ROM complete in the seed library.
            if (goalPlugin is Plugins.Emulated.EmulatorPlugin
                    { ActivePatchedRomPath: { } seedRom })
                SeedLibraryStore.Instance.MarkComplete(goalPlugin.GameId, seedRom);
        }

        _goalReachedThisSession = true;
        _ = _apClient?.SendStatusUpdateAsync(ApClientStatus.Goal);
    }

    /// Remove the named AP-bridge handlers from a plugin. Safe to call when
    /// nothing is subscribed (event -= on a non-subscriber is a no-op).
    private void UnwirePluginEvents(IGamePlugin plugin)
    {
        plugin.LocationsChecked -= OnPluginLocationsChecked;
        plugin.GameExited       -= OnPluginGameExited;
        plugin.GoalCompleted    -= OnPluginGoalCompleted;
    }

    /// 2.1 — Before launching Diablo II, make sure the antivirus-prone mod files
    /// exist. Antivirus (especially Windows Defender) routinely quarantines
    /// D2Arch_Launcher.exe; rather than dead-ending at launch, offer a one-click
    /// repair that re-downloads + restores just the missing files. Returns true when
    /// it's safe to launch (nothing missing, or repair succeeded), false otherwise.
    private async Task<bool> EnsureD2ModFilesAsync(IGamePlugin plugin)
    {
        if (plugin is not Plugins.DiabloII.D2Plugin d2) return true;
        var missing = d2.GetMissingCriticalFiles();
        if (missing.Count == 0) return true;

        string list = string.Join("\n", missing.Select(m => "   • " + m));
        var ask = System.Windows.MessageBox.Show(this,
            "Some Diablo II mod files are missing:\n\n" + list +
            "\n\nThis is almost always your antivirus (especially Windows Defender) " +
            "removing D2Arch_Launcher.exe as a false positive.\n\n" +
            "Download and restore them now?",
            "Mod files missing — repair?",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (ask != System.Windows.MessageBoxResult.Yes) return false;

        try
        {
            SwitchTab(PageTab.Play);
            SetStatus("Repairing — downloading missing mod files...");
            var progress = new Progress<(int Pct, string Msg)>(p =>
            {
                ProgressBar.Value = p.Pct;
                SetStatus("Repairing — " + p.Msg);
            });
            int restored = await d2.RepairMissingFilesAsync(progress);
            AppendLog($"[Repair] Restored {restored} file(s).");

            var still = d2.GetMissingCriticalFiles();
            if (still.Count > 0)
            {
                System.Windows.MessageBox.Show(this,
                    "The files downloaded but are still missing — your antivirus is " +
                    "deleting them again as they're written. Add the game's install folder " +
                    "to your antivirus exclusions, then try launching again.",
                    "Repair blocked by antivirus",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                SetStatus("Repair blocked by antivirus.");
                return false;
            }
            SetStatus("Repair complete.");
            AppendLog("[Repair] All required files restored.");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("[Repair] Failed: " + ex.Message);
            System.Windows.MessageBox.Show(this,
                "Could not download the missing files:\n\n" + ex.Message,
                "Repair failed", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            SetStatus("Repair failed.");
            return false;
        }
    }

    /// True when a launch exception means Windows Defender (or another AV) blocked
    /// the mod exe as a virus/PUA false positive — Win32 error 225 (ERROR_VIRUS_
    /// INFECTED) or a localized "virus / unwanted software" message.
    private static bool IsAntivirusBlock(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is System.ComponentModel.Win32Exception w32 && w32.NativeErrorCode == 225)
                return true;
            string m = (e.Message ?? "").ToLowerInvariant();
            if (m.Contains("virus") || m.Contains("potentially unwanted")
                || m.Contains("unwanted software") || m.Contains("uønsket"))
                return true;
        }
        return false;
    }

    /// When a launch failed because Defender blocked the mod exe, offer a one-click
    /// fix: add the game folder to Windows Defender's exclusion list (one admin/UAC
    /// click). Returns true if it handled the error (showed its own UI), false to
    /// fall through to the generic "could not launch" dialog.
    private async Task<bool> TryOfferDefenderExclusionAsync(IGamePlugin plugin, Exception ex)
    {
        if (plugin is not Plugins.DiabloII.D2Plugin d2 || !IsAntivirusBlock(ex)) return false;

        string gameDir = d2.GameDirectory;
        var ask = System.Windows.MessageBox.Show(this,
            "Windows Defender blocked the mod from starting (false positive):\n\n" +
            ex.Message +
            "\n\nThe mod injects into Diablo II, which Defender flags as suspicious. " +
            "I can add the game folder to Defender's exclusion list so it stops:\n\n" +
            gameDir +
            "\n\nWindows will ask for administrator permission. Add the exclusion now?",
            "Add Windows Defender exclusion?",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (ask != System.Windows.MessageBoxResult.Yes) return true;   // handled — user declined the fix

        SetStatus("Adding Windows Defender exclusion...");
        bool ok = await Task.Run(() => d2.AddDefenderExclusion());
        if (ok)
        {
            // Defender may also have quarantined the exe — restore it if so.
            try { if (d2.GetMissingCriticalFiles().Count > 0) await EnsureD2ModFilesAsync(plugin); }
            catch { /* best effort */ }
            AppendLog("[Defender] Exclusion added for " + gameDir);
            System.Windows.MessageBox.Show(this,
                "Added to Windows Defender's exclusions. Click Play / Launch again to start the game.",
                "Exclusion added", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        else
        {
            System.Windows.MessageBox.Show(this,
                "Couldn't add the exclusion automatically (the admin prompt may have been " +
                "declined, or a third-party antivirus is active). Add it manually:\n\n" +
                "Windows Security → Virus & threat protection → Manage settings → " +
                "Exclusions → Add an exclusion → Folder:\n\n" + gameDir,
                "Add the exclusion manually", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        return true;
    }

    /// At startup, make sure the launcher's own folder + the Diablo II install are in
    /// Windows Defender's exclusion list (Defender false-positives the mod injector and
    /// blocks/deletes it). Re-prompts every startup until the user accepts. Best-effort —
    /// never blocks startup, and silently no-ops when Defender isn't the active provider.
    private async Task EnsureDefenderExclusionsAtStartupAsync()
    {
        try
        {
            var s = SettingsStore.Load();
            if (s.DefenderExclusionsDone) return;

            await Task.Delay(1500);   // let the main window finish rendering first

            var paths = new List<string>();
            try { paths.Add(AppContext.BaseDirectory.TrimEnd('\\')); } catch { }
            try
            {
                string d2dir = SettingsStore.DefaultGamePath("diablo2_archipelago").TrimEnd('\\');
                if (Directory.Exists(d2dir)) paths.Add(d2dir);
            }
            catch { }
            if (paths.Count == 0) return;

            // Already excluded? (read-only query — no admin needed)
            var existing = await Task.Run(GetDefenderExclusionPaths);
            var missing = paths.Where(p => !existing.Any(e =>
                string.Equals(e.TrimEnd('\\'), p, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count == 0)
            {
                s.DefenderExclusionsDone = true; SettingsStore.Save(s);
                return;
            }

            string list = string.Join("\n", missing.Select(m => "   • " + m));
            var ask = System.Windows.MessageBox.Show(this,
                "To stop Windows Defender from blocking or deleting the Diablo II mod " +
                "(it false-positives the injector), these folders should be added to " +
                "Defender's exclusion list:\n\n" + list +
                "\n\nWindows will ask for administrator permission once. Add them now?",
                "Allow the game in Windows Defender?",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (ask != System.Windows.MessageBoxResult.Yes) return;  // re-prompt next startup

            bool ok = await Task.Run(() => AddDefenderExclusionPaths(missing));
            if (ok)
            {
                s.DefenderExclusionsDone = true; SettingsStore.Save(s);
                AppendLog("[Defender] Exclusions added: " + string.Join(", ", missing));
                System.Windows.MessageBox.Show(this,
                    "Done — the launcher and game folders are now allowed in Windows Defender.",
                    "Exclusions added", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
            else
            {
                AppendLog("[Defender] Startup exclusion add was declined or failed.");
                // Flag stays false → we ask again next startup.
            }
        }
        catch (Exception ex)
        {
            AppendLog("[Defender] Startup check failed: " + ex.Message);
        }
    }

    /// Read Windows Defender's current ExclusionPath list (no admin). Empty list when
    /// Defender isn't the active provider or the query fails.
    private static List<string> GetDefenderExclusionPaths()
    {
        var result = new List<string>();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName  = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"(Get-MpPreference).ExclusionPath\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return result;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            foreach (var line in outp.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) result.Add(t);
            }
        }
        catch { /* Defender absent / query blocked — treat as none excluded */ }
        return result;
    }

    /// Add multiple paths to Windows Defender's exclusions in one elevated call (UAC).
    private static bool AddDefenderExclusionPaths(List<string> paths)
    {
        try
        {
            string arr = string.Join(",", paths.Select(p => "'" + p.Replace("'", "''") + "'"));
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName  = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command " +
                            $"\"Add-MpPreference -ExclusionPath {arr}\"",
                UseShellExecute = true,
                Verb            = "runas",
                CreateNoWindow  = true,
                WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(30000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private async Task LaunchGameAsync(IGamePlugin plugin)
    {
        // Antivirus often quarantines D2Arch_Launcher.exe — offer repair, not a dead end.
        if (!await EnsureD2ModFilesAsync(plugin)) return;

        bool alreadyConnected = _apClient?.State == ApConnectionState.Connected;

        // Native-AP games (ConnectsItself, e.g. the OpenTTD fork) hold the slot
        // with their own in-game client. AP servers allow ONE connection per
        // slot and drop the older one — a launcher ApClient on the same slot
        // plus auto-reconnect becomes an endless kick-war (P1-8). These games
        // launch with credential prefill only: no launcher AP session required,
        // none created, and any live one is released first.
        bool nativeAp = plugin.ConnectsItself;

        if (!alreadyConnected && !nativeAp)
        {
            // No active connection — check that the user has filled in credentials
            string server   = TxtServer.Text.Trim();
            string slot     = TxtSlotName.Text.Trim();

            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(slot))
            {
                // Prompt user to connect via the sidebar panel
                PanelConnInputs.Visibility = Visibility.Visible;
                BtnConnToggle.Content      = "Cancel";
                TxtServer.Focus();
                AppendLog("[Info] Enter server and slot name in the left panel, then connect before launching.");
                return;
            }
        }

        // Use the session from the global connect if available, otherwise build one from the text fields
        var session = _currentSession
            ?? new ApSession(TxtServer.Text.Trim(), TxtSlotName.Text.Trim(),
                             TxtPassword.Password.Trim(), plugin.ApWorldName);

        // Release a live launcher session before a native-AP game starts — the
        // server would kick it the moment the game connects anyway. (The
        // session record above was captured first; it feeds the prefill.)
        if (nativeAp && _apClient != null)
        {
            AppendLog($"[Info] {plugin.DisplayName} connects to Archipelago from inside the game — " +
                      "the launcher connection was released so the two don't fight over the slot.");
            await CleanupSessionAsync();
        }

        BtnPlay.IsEnabled = false;
        BtnPlay.Content   = alreadyConnected || nativeAp ? "Launching..." : "Connecting...";
        SyncOverviewPlayButton();

        if (!alreadyConnected && !nativeAp)
        {
            // Connect now (full connect+launch path — backward compat for Play button)
            _playCts  = new CancellationTokenSource();
            _apClient = new ApClient(session, plugin);

            _apClient.StateChanged     += state => Dispatcher.Invoke(() => UpdateApStatus(state));
            _apClient.PrintMessage     += msg   => Dispatcher.Invoke(() =>
            {
                AppendLog(msg);
                if (msg.StartsWith("[AP] Server rejected packet", StringComparison.Ordinal))
                    ToastService.Show("AP Protocol Error", msg, ToastKind.Warning);
            });
            _apClient.SessionConnected += (mySlot, players) =>
            {
                // Runs on the AP receive thread — capture the raising client so a
                // concurrent sidebar Disconnect (which nulls/replaces + disposes
                // _apClient) can't NRE us between these field reads.
                var ap = _apClient;
                if (ap == null) return;
                _tracker.OnConnected(mySlot, players);
                _locationTracker.OnConnected(ap.ConnectedChecked,
                                              ap.ConnectedMissing, players);
                // Achievement ladder: first-connect + distinct-server tracking.
                AchievementStore.Instance.RecordApConnected(
                    plugin.GameId, session.ServerUri);
                Dispatcher.Invoke(() =>
                {
                    RefreshPlayerDropdown();
                    RefreshProgressionPanel();
                    UpdateGameSuggestionBanner();
                });
                _ = ap.GetDataPackageAsync(new[] { plugin.ApWorldName });
            };
            _apClient.DataPackageReceived += (gameKey, data) =>
            {
                _tracker.OnDataPackage(gameKey, data);
                _locationTracker.OnDataPackage(gameKey, data);
                if (string.Equals(gameKey, plugin.ApWorldName, StringComparison.OrdinalIgnoreCase))
                {
                    var missing = _locationTracker.GetMissingIds();
                    if (missing.Length > 0)
                        _ = _apClient!.LocationScoutsAsync(missing, createAsHint: 0);
                }
            };
            _apClient.ItemsReceived += (items, receiverSlot) =>
                _tracker.RecordItems(items, receiverSlot);
            _apClient.LocationInfoReceived += items =>
                _locationTracker.OnLocationInfo(items);
            _apClient.ServerCheckedLocations += ids =>
                _locationTracker.OnLocationsChecked(ids);
            _locationTracker.Changed -= OnLocationTrackerChanged;   // never stack (P3-6)
            _locationTracker.Changed += OnLocationTrackerChanged;
            // Same ingest pipeline as the global-connect path + the hint backlog
            _apClient.HintEntryReceived += hint =>
                Dispatcher.Invoke(() => AddHintEntryToPanel(hint));
            _apClient.HintPointsChanged += pts => Dispatcher.Invoke(() =>
            {
                TxtHintCount.Text       = FormatHintPoints(pts);
                TxtHintCount.Visibility = Visibility.Visible;
                if (_currentTab == PageTab.Progression)
                    RefreshProgressionPanel();
            });
            _apClient.HintReceived += _ => { };
            WireApFeatureEvents(_apClient);   // DeathLink / countdown / toasts / hint backlog

            // Persisted DeathLink opt-in — same priming as the global connect path
            if (SettingsStore.Load().DeathLinkEnabled)
                await _apClient.SetDeathLinkAsync(true);
        }

        // Fresh game session — checks from a previous session must not leak
        // into this slot's replay buffer (P2-8).
        lock (_pendingChecksLock) _pendingChecks.Clear();

        // Wire per-game events. NAMED handlers, unsubscribed before every
        // subscribe and again in CleanupSessionAsync — plugins are app-lifetime
        // singletons, so a re-wire per launch would otherwise stack and fire
        // once per past launch. The handlers null-check _apClient (no `!`):
        // a manual sidebar Disconnect while the game runs nulls the field, and
        // an NRE here would land on the plugin's pipe thread and kill its read
        // loop (all further CHECK/GOAL messages silently lost).
        UnwirePluginEvents(plugin);
        plugin.LocationsChecked += OnPluginLocationsChecked;
        plugin.GameExited       += OnPluginGameExited;
        plugin.GoalCompleted    += OnPluginGoalCompleted;
        _runningPlugin = plugin;

        // ── D2-specific pipe v2 extensions (assignment-style = idempotent) ───
        if (plugin is Plugins.DiabloII.D2Plugin d2)
        {
            // ITEM v2 sender names — the player map is UI-thread-only, so
            // resolve via the dispatcher (items arrive on the AP receive loop).
            d2.ResolvePlayerName = slot => Dispatcher.Invoke(() => ResolveApPlayerName(slot));

            // Slot-data supplier — lets the plugin write ap_settings.dat
            // BEFORE pushing STATE:CONNECTED (LoadAPSettings ordering).
            d2.GetSlotData = () => _apClient?.SlotData;

            // Seed-name supplier — the AP launch path derives a stable per-world
            // seed from it for the data-file randomization (same-world reproducible).
            d2.GetSeedName = () => _apClient?.SeedName;

            // Post-attach resync — once the game's pipe connects, ask the AP
            // server to resend the full item stream from index 0 so items the
            // player received while still in the launcher (notably the
            // precollected STARTING SKILLS) reach the DLL. They were dropped
            // earlier because the pipe wasn't connected yet.
            d2.RequestApResync = () => _apClient?.SyncAsync() ?? Task.CompletedTask;

            // Standalone "Received" — the mod forwards each check's reward as
            // "<location>: <reward>"; split it and append to the item tracker so a
            // solo run's Received tab lists what every check granted (no AP server).
            d2.StandaloneItemReceived += text =>
            {
                int sep = text.IndexOf(": ", StringComparison.Ordinal);
                string loc    = sep > 0 ? text[..sep]       : "Check";
                string reward = sep > 0 ? text[(sep + 2)..] : text;
                _tracker.RecordStandalone(loc, reward);
            };

            // DeathLink send-side: in-game death → AP Bounce, only when opted in.
            d2.OnPlayerDied = cause =>
            {
                if (_apClient?.DeathLinkEnabled == true)
                {
                    _ = _apClient.SendDeathLinkAsync(
                        string.IsNullOrWhiteSpace(cause) ? "died" : cause);
                    // Achievement ladder: a death actually shared with the pack.
                    AchievementStore.Instance.IncrementCounter(
                        plugin.GameId, AchievementCounters.DeathsShared);
                }
            };
        }

        // ── Emulated-game bridge suppliers (assignment-style = idempotent) ───
        // The Lua game modules need multiworld context the pipe protocol
        // cannot carry by itself: the slot's full server-location set (their
        // check filter), the seed's slot_data (goal/remote_items/dexsanity)
        // and our own slot number (own-item filtering). WriteApConfig pulls
        // these at launch time — after the AP handshake below has completed.
        if (plugin is Plugins.Emulated.EmulatorPlugin emuBridge)
        {
            emuBridge.GetSlotData = () => _apClient?.SlotData;
            emuBridge.GetOwnSlot  = () => _apClient?.Slot ?? 0;
            // Seed of the connected room — lets the patch resolver pick the
            // .ap<game> file that belongs to THIS multiworld, not just the
            // newest one on disk.
            emuBridge.GetSeedName = () => _apClient?.SeedName;
            emuBridge.GetServerLocations = () =>
            {
                var ap = _apClient;
                if (ap == null) return null;
                var all = new List<long>(ap.ConnectedChecked.Count + ap.ConnectedMissing.Count);
                all.AddRange(ap.ConnectedChecked);
                all.AddRange(ap.ConnectedMissing);
                return all.ToArray();
            };
        }

        try
        {
            if (!alreadyConnected && !nativeAp)
            {
                SetStatus("Connecting to Archipelago...");
                var handshake = ArmHandshakeWatch(_apClient!);
                await _apClient!.ConnectAsync(_playCts!.Token);

                // Same handshake gate as the sidebar connect (P1-6): never
                // launch the game on a login the server is about to refuse.
                SetStatus("Logging in to the multiworld...");
                string[]? refusal = await AwaitHandshakeVerdictAsync(handshake);
                if (refusal != null)
                    throw new InvalidOperationException(
                        TranslateApRefusal(refusal, session.SlotName, plugin));

                // Persist the server address + remember this connection (MRU)
                var savedSettings = SettingsStore.Load();
                savedSettings.DefaultApServer = session.ServerUri;
                savedSettings.AddRecentConnection(session.ServerUri, session.SlotName);
                SettingsStore.Save(savedSettings);
                RefreshRecentConnButton();
                RefreshWelcomeChecklist();

                // Once connected via Play button, update the toggle button too
                BtnConnToggle.Content = "Disconnect";
                _currentSession       = session;
            }

            // Pre-launch integrity check (fast size-based, offline-safe).
            // No throwaway CTSes (P3-4): when no play CTS exists (nativeAp /
            // already-connected paths) the steps are simply uncancellable.
            var playToken = _playCts?.Token ?? CancellationToken.None;
            SetStatus("Verifying install...");
            AppendLog("[Verify] Checking install integrity...");
            bool ok = await plugin.VerifyInstallAsync(playToken);
            AppendLog(ok ? "[Verify] OK — all files present and correct size."
                        : "[Verify] WARNING: some files may be missing or corrupted. Consider re-installing.");

            // Slot-data hand-off: write ap_settings.dat BEFORE the game starts
            // so characters created this session bake the multiworld seed's
            // settings, not the local d2arch.ini fallback.
            if (plugin is Plugins.DiabloII.D2Plugin d2Pre &&
                _apClient?.SlotData is JsonElement preSd)
                d2Pre.WriteApSettingsFile(preSd);

            // ── ROM safety net ──────────────────────────────────────────────
            // If an emulated game can't find the correct ROM (none imported, or
            // the wrong version for this seed's patch), ask the player to point
            // to it BEFORE launching — with the exact version + fingerprint —
            // instead of failing or silently patching the wrong dump.
            if (plugin is Plugins.Emulated.EmulatorPlugin emuRom)
            {
                var req = emuRom.GetUnmetRomRequirement();
                if (req != null && !EnsureEmulatorRom(emuRom, req))
                {
                    AppendLog($"[Launch] Cancelled — {req.GameName} game file not provided.");
                    SetStatus("Launch cancelled — game file needed.");
                    RefreshButtons(plugin);     // restore Play (connection stays up)
                    return;
                }
            }

            SetStatus("Launching game...");
            await plugin.LaunchAsync(session, playToken);
            RebuildGameList();   // show "Running" badge on the sidebar card

            // Emulated-game launch degradations — surfaced loudly instead of
            // letting a "Playing" session sit there with zero AP traffic:
            //  1. The Lua connector never attached (60 s timeout in
            //     LaunchAsync) — NOTHING flows, items included (P2-10).
            //  2. Connector fine, but the game's RAM address map is not
            //     verified yet — checks can never be detected (P1-9).
            if (plugin is Plugins.Emulated.EmulatorPlugin emu)
            {
                // ROM-preparation outcome (AP patch applied / vanilla
                // fallback / patch failure) — always worth a log line.
                if (!string.IsNullOrEmpty(emu.SessionRomNote))
                    AppendLog(emu.SessionRomNote);

                // A fresh seed may have just registered in the library — keep
                // the ROMs tab current for the on-screen game.
                if (ReferenceEquals(_selectedPlugin, emu))
                    RefreshRomsTab(emu);

                if (!emu.ConnectorAttached)
                {
                    AppendLog("[Warning] BizHawk is running but the AP connector script never " +
                              "attached (waited 60 seconds) — no checks or items will sync. " +
                              "Open the Lua Console inside BizHawk to see the script error, " +
                              "then restart the game from the launcher.");
                    ToastService.Show("AP connector not attached",
                        $"{plugin.DisplayName} is running WITHOUT Archipelago sync — the " +
                        "connector script didn't start. Check BizHawk's Lua Console for " +
                        "errors, then stop and relaunch.",
                        ToastKind.Warning);
                }
                else if (!emu.ChecksImplemented)
                {
                    AppendLog("[Warning] Check detection for this game is still in development — " +
                              "location checks will NOT be sent to the multiworld yet.");
                    ToastService.Show("Check detection in development",
                        $"{plugin.DisplayName} launches and runs, but in-game checks are not " +
                        "detected yet — they will not reach the multiworld.",
                        ToastKind.Warning);
                }
            }

            _sessionStart           = AchievementStore.Instance.BeginSession(
                plugin.GameId, session.ServerUri, session.SlotName);
            _goalReachedThisSession = false;

            // The launched game may no longer be the one on screen (the user
            // can browse the sidebar during the connect/launch) — only paint
            // "Stop" when it is; otherwise refresh the displayed game so its
            // buttons pick up the one-game-at-a-time disabled state (P2-5).
            BtnPlay.IsEnabled = true;
            if (ReferenceEquals(_selectedPlugin, plugin))
                BtnPlay.Content = "Stop";
            else if (_selectedPlugin != null)
                RefreshButtons(_selectedPlugin);
            SyncOverviewPlayButton();
            SetStatus($"Playing — {plugin.DisplayName}");

            // AP lifecycle: the game session is live → report Playing (20).
            // Connected (5) was sent by ApClient at the handshake; Goal (30)
            // stays wired to plugin.GoalCompleted — nothing else sends 20.
            if (_apClient?.State == ApConnectionState.Connected)
            {
                try { await _apClient.SetStatusAsync(ClientStatus.Playing); }
                catch { /* status is cosmetic — never block a successful launch */ }
            }

            // A running session retires the Ready toggle until the next idle connect
            _apReadySent = false;
            RefreshReadyButton();

            // Show tray icon so user can restore/stop the launcher while D2 runs
            _trayIcon.Show($"Playing {plugin.DisplayName}");
        }
        catch (Exception ex)
        {
            // Same plain-language mapping as the sidebar connect (UX-3) —
            // launch-specific errors (AV guidance etc.) pass through unchanged.
            AppendLog($"[Error] {TranslateConnectError(ex, session.ServerUri)}");
            SetStatus("Launch failed.");
            // If Windows Defender blocked the mod exe, offer to add an exclusion.
            await TryOfferDefenderExclusionAsync(plugin, ex);
            await CleanupSessionAsync();
            _currentSession = null;
        }
    }

    private async void OnGameExited(int exitCode)
    {
        // async void on a plugin event that fires on an arbitrary thread: an
        // unhandled throw here would be unobserved and crash the process exactly
        // when a game closes. Guard the whole teardown.
        try
        {
            SetStatus($"Game exited (code {exitCode}).");
            await CleanupSessionAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Cleanup after game exit failed: {ex.Message}");
        }
    }

    private async Task CleanupSessionAsync()
    {
        // Mark the teardown as deliberate so UpdateApStatus doesn't toast
        // "connection lost" for state changes we cause ourselves.
        _apTeardownExpected = true;
        try
        {
            // The session belongs to the plugin that was LAUNCHED, never to
            // whatever game the sidebar happens to display (P2-6). A manual
            // AP disconnect can run this while that game is still going.
            var  sessionPlugin    = _runningPlugin ?? _selectedPlugin;
            bool gameStillRunning = _runningPlugin?.IsRunning == true;

            // Hide the tray icon only when the game really stopped — after a
            // mid-game AP disconnect it keeps saying "Playing <game>".
            if (!gameStillRunning) _trayIcon.Hide();

            // Record session for achievement tracking — credited to the
            // session's own game (browsing another game mid-session used to
            // book the playtime on the wrong title).
            if (sessionPlugin != null && _sessionStart != default)
            {
                int playerCount = _tracker.Players.Count > 0 ? _tracker.Players.Count : 1;
                AchievementStore.Instance.EndSession(
                    sessionPlugin.GameId,
                    _sessionStart,
                    _goalReachedThisSession,
                    _currentSession?.ServerUri ?? TxtServer.Text.Trim(),
                    _currentSession?.SlotName  ?? TxtSlotName.Text.Trim(),
                    playerCount);

                // Attribute this session's playtime to the per-seed ROM in the
                // seed library (same duration the achievement session just used).
                if (sessionPlugin is Plugins.Emulated.EmulatorPlugin
                        { ActivePatchedRomPath: { } seedRom })
                {
                    long sessionSeconds =
                        (long)Math.Max(0, (DateTimeOffset.UtcNow - _sessionStart).TotalSeconds);
                    SeedLibraryStore.Instance.MarkPlayed(
                        sessionPlugin.GameId, seedRom, sessionSeconds);
                }

                _sessionStart           = default;
                _goalReachedThisSession = false;
            }

            if (_apClient != null)
            {
                await _apClient.DisposeAsync();
                _apClient = null;
            }
            _playCts?.Cancel();
            _playCts?.Dispose();   // cancelled-but-never-disposed leaked timers (P3-4)
            _playCts        = null;
            _currentSession = null;

            // Unsubscribe named handler before Clear() so it doesn't fire the UI refresh
            // while we're already tearing down the session.
            _locationTracker.Changed -= OnLocationTrackerChanged;

            // Unwire the plugin's AP-bridge handlers — but ONLY once its game
            // is no longer running. After a manual sidebar disconnect the game
            // keeps going: the handlers must stay attached so its checks keep
            // landing in the replay buffer (P2-8) and the eventual game exit
            // still triggers this cleanup. The next launch unwires-then-wires,
            // so handlers can never stack either way.
            if (_runningPlugin != null && !gameStillRunning)
            {
                UnwirePluginEvents(_runningPlugin);
                _runningPlugin = null;
            }

            // The check replay buffer survives an AP teardown while the game
            // runs — that reconnect window is exactly what it protects (P2-8).
            // It dies with the game session.
            if (!gameStillRunning)
                lock (_pendingChecksLock) _pendingChecks.Clear();

            _tracker.Clear();
            _locationTracker.Clear();
            _hints.Clear();
            _hintKeys.Clear();
            _locCategory  = "";
            _locPage.Clear();
            _hintsFilter  = "all";

            // AP ecosystem feature state dies with the session
            _dpItemNames.Clear();
            _dpLocationNames.Clear();
            _dpNamesReady       = false;
            _pendingHintBacklog = null;
            _apReadySent        = false;
            _itemToastTimes.Clear();
            _itemToastSuppressed = 0;
            _itemToastBurstTimer?.Stop();
            Dispatcher.Invoke(() =>
            {
                _trackerRefreshTimer?.Stop();   // drop any pending debounced rebuild
                _trackerView.Clear();
                TrackerTilePanel.Children.Clear();
                TxtTrackerCount.Text    = "";
                TxtHintCount.Visibility = Visibility.Collapsed;
                TxtHintCount.Text       = "";
                // Kill any in-flight countdown overlay (seq bump invalidates
                // the pending fade-out so it cannot re-hide a future one)
                _countdownSeq++;
                CountdownOverlay.Visibility = Visibility.Collapsed;
                // Reset the sidebar toggle button and suggestion banner
                BtnConnToggle.Content   = "Connect";
                BtnConnToggle.IsEnabled = true;
                PanelGameSuggest.Visibility = Visibility.Collapsed;
                UpdateGameSuggestionBanner();
                // Session just ended — playtime totals have changed
                if (_selectedPlugin != null)
                {
                    RefreshPlaytimeBadge(_selectedPlugin);
                    RefreshOverview(_selectedPlugin);   // stats strip + achievements
                    // The seed's status / play time was just updated.
                    if (_selectedPlugin is Plugins.Emulated.EmulatorPlugin endedEmu)
                        RefreshRomsTab(endedEmu);
                }
            });

            if (_selectedPlugin != null)
            {
                BtnPlay.IsEnabled       = true;
                BtnStandalone.IsEnabled = true;   // a standalone launch disables both
                RefreshButtons(_selectedPlugin);
            }

            RebuildGameList();   // clear "Running" badge from the sidebar card
        UpdateApStatus(ApConnectionState.Disconnected);
        }
        finally
        {
            _apTeardownExpected = false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Sidebar card builder
    // ══════════════════════════════════════════════════════════════════════════

    private UIElement BuildGameCard(IGamePlugin plugin)
    {
        var muted   = (Brush)FindResource("BrushMuted");
        var success = (Brush)FindResource("BrushSuccess");

        var border = new Border
        {
            Style   = (Style)FindResource("GameCardStyle"),
            Cursor  = Cursors.Hand,
            // Tag carries the display name for sidebar search filtering.
            Tag     = plugin.DisplayName,
            ToolTip = BuildGameCardToolTip(plugin),
        };

        // Layout: icon on the left, text+badges in the middle, status dot on the right
        var row = new DockPanel { LastChildFill = true };

        // ── Status dot (right side, small) ────────────────────────────────────
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width             = 6,
            Height            = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
            Fill              = plugin.IsRunning
                ? (Brush)FindResource("BrushSuccess")
                : plugin.IsInstalled
                    ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x55))
                    : new SolidColorBrush(Color.FromRgb(0x28, 0x2C, 0x40)),
        };
        DockPanel.SetDock(dot, Dock.Right);
        row.Children.Add(dot);

        // ── Favorite star toggle (right side, before dot) ─────────────────────
        bool fav = LibraryStore.IsFavorite(plugin.GameId);
        var starBtn = new TextBlock
        {
            Text              = fav ? "★" : "☆",
            FontSize          = 12,
            Foreground        = fav
                ? new SolidColorBrush(Color.FromRgb(0xF5, 0xC5, 0x18))
                : new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x55)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(2, 0, 4, 0),
            Cursor            = Cursors.Hand,
            ToolTip           = fav ? "Unpin from top" : "Pin to top",
        };
        DockPanel.SetDock(starBtn, Dock.Right);
        string capturedId = plugin.GameId;
        starBtn.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;   // don't propagate to card's SelectGame handler
            bool cur = LibraryStore.IsFavorite(capturedId);
            LibraryStore.SetFavorite(capturedId, !cur);
            RebuildGameList();
            // Re-select the same game so the header stays correct
            if (_selectedPlugin?.GameId == capturedId)
                SelectGame(_selectedPlugin);
        };
        row.Children.Add(starBtn);

        // ── Game icon (36×36) ─────────────────────────────────────────────────
        var imgBorder = new Border
        {
            Width             = 36,
            Height            = 36,
            CornerRadius      = new CornerRadius(4),
            Background        = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Margin            = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds      = true,
        };
        if (LoadCachedBitmap(plugin.IconPath) is { } iconBmp)
        {
            var img = new Image
            {
                Source  = iconBmp,
                Stretch = System.Windows.Media.Stretch.UniformToFill,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            imgBorder.Child = img;
        }
        DockPanel.SetDock(imgBorder, Dock.Left);
        row.Children.Add(imgBorder);

        // ── Text block (name + subtitle + badge) ──────────────────────────────
        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text         = plugin.DisplayName,
            FontWeight   = FontWeights.SemiBold,
            FontSize     = 12,
            Foreground   = (Brush)FindResource("BrushText"),
            TextWrapping = TextWrapping.NoWrap,
        });
        textStack.Children.Add(new TextBlock
        {
            Text       = plugin.Subtitle,
            FontSize   = 10,
            Foreground = muted,
            Margin     = new Thickness(0, 2, 0, 0),
        });

        // Status badge pill (Running / Installed / Not installed). "Running"
        // not "In Session" — that used to collide with the header badge, which
        // means "AP connected", a different thing entirely (P3-10).
        string statusLabel = plugin.IsRunning   ? "Running"
                           : plugin.IsInstalled ? "Installed"
                                                : "Not installed";
        var statusBr = plugin.IsRunning   ? success
                     : plugin.IsInstalled ? new SolidColorBrush(Color.FromRgb(0x3A, 0x55, 0x3A))
                                          : muted;
        textStack.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Thickness(0, 4, 0, 0),
            Padding             = new Thickness(5, 1, 5, 1),
            CornerRadius        = new CornerRadius(2),
            Background          = new SolidColorBrush(Color.FromArgb(0x20, 0x20, 0x30, 0x20)),
            Child = new TextBlock
            {
                Text       = statusLabel,
                FontSize   = 9,
                Foreground = statusBr,
            }
        });

        row.Children.Add(textStack);

        border.Child = row;
        border.MouseLeftButtonDown += (_, e) =>
        {
            _dragStartPoint = e.GetPosition(null);
            SelectGame(plugin);
        };

        // ── Drag-to-reorder ──────────────────────────────────────────────────────
        border.AllowDrop = true;

        border.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var pos  = e.GetPosition(null);
            var diff = pos - _dragStartPoint;
            // Only start a drag after a small movement threshold (avoids accidental drags)
            if (Math.Abs(diff.Y) < 8 && Math.Abs(diff.X) < 8) return;

            DragDrop.DoDragDrop(border, plugin.GameId, DragDropEffects.Move);
        };

        border.DragEnter += (_, e) =>
        {
            e.Effects = (e.Data.GetData(typeof(string)) is string id && id != plugin.GameId)
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };

        border.DragOver += (_, e) =>
        {
            e.Effects = (e.Data.GetData(typeof(string)) is string id && id != plugin.GameId)
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };

        border.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(string)) is string draggedId &&
                draggedId != plugin.GameId)
            {
                // Snapshot before mutation so Ctrl+Z can revert (UX-7).
                _orderHistory.Add(LibraryStore.GetRawOrder());
                if (_orderHistory.Count > MaxOrderHistory) _orderHistory.RemoveAt(0);

                LibraryStore.MoveBeforeId(draggedId, plugin.GameId);
                RebuildGameList();
                // Keep the dragged game selected if it was the active one
                if (_selectedPlugin?.GameId == draggedId)
                    HighlightGameCard(_selectedPlugin);
            }
            e.Handled = true;
        };

        // ── Right-click context menu ─────────────────────────────────────────────
        border.ContextMenu = BuildGameCardContextMenu(plugin);

        _gameCards[plugin.GameId] = border;
        return border;
    }

    private ContextMenu BuildGameCardContextMenu(IGamePlugin plugin)
    {
        var menu = new ContextMenu
        {
            Background   = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x24)),
            BorderBrush  = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            Foreground   = (Brush)FindResource("BrushText"),
        };

        bool isFav = LibraryStore.IsFavorite(plugin.GameId);
        var favItem = new MenuItem
        {
            Header     = isFav ? "★  Remove from Favorites" : "☆  Add to Favorites",
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("BrushText"),
        };
        favItem.Click += (_, _) =>
        {
            LibraryStore.SetFavorite(plugin.GameId, !LibraryStore.IsFavorite(plugin.GameId));
            RebuildGameList();
        };
        menu.Items.Add(favItem);

        var removeItem = new MenuItem
        {
            Header     = "Remove from Library",
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0x60, 0x60)),
        };
        removeItem.Click += (_, _) =>
        {
            bool remove = ConfirmDialog.Show(this,
                $"Remove {plugin.DisplayName} from your library?",
                "The game stays installed — this only hides it from the " +
                "sidebar. You can add it back any time from Browse.",
                "Remove", "Cancel");
            if (!remove) return;

            LibraryStore.Remove(plugin.GameId);
            RebuildGameList();

            // If this was the selected game, fall back to the first remaining game
            if (_selectedPlugin == plugin)
            {
                var firstId = LibraryStore.GetSortedGameIds().FirstOrDefault();
                var next    = firstId != null
                    ? GameRegistry.All.FirstOrDefault(p => p.GameId == firstId)
                    : null;
                if (next != null)
                    SelectGame(next);
                else
                {
                    PanelGame.Visibility  = Visibility.Collapsed;
                    PanelEmpty.Visibility = Visibility.Visible;
                    _selectedPlugin       = null;
                    RefreshHomePage();
                }
            }
        };
        menu.Items.Add(removeItem);

        if (plugin.IsInstalled && !string.IsNullOrEmpty(plugin.GameDirectory) &&
            Directory.Exists(plugin.GameDirectory))
        {
            menu.Items.Add(new Separator());
            var openFolder = new MenuItem
            {
                Header     = "Open Game Folder",
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushText"),
            };
            string dir = plugin.GameDirectory;
            openFolder.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true }); }
                catch { /* ignore */ }
            };
            menu.Items.Add(openFolder);
        }

        return menu;
    }

    private void HighlightGameCard(IGamePlugin plugin)
    {
        var gold  = (Brush)FindResource("BrushAccent");
        var divBr = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33));
        var actBg = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x30));

        foreach (var (id, card) in _gameCards)
        {
            bool active = id == plugin.GameId;
            if (active)
            {
                card.Background      = actBg;
                card.BorderBrush     = gold;
                card.BorderThickness = new Thickness(3, 0, 0, 0);
                card.Padding         = new Thickness(11, 11, 14, 11);
            }
            else
            {
                card.Background      = Brushes.Transparent;
                card.BorderBrush     = divBr;
                card.BorderThickness = new Thickness(0, 0, 0, 1);
                card.Padding         = new Thickness(14, 11, 14, 11);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════════

    // ── Bitmap cache (P3-3) ───────────────────────────────────────────────────
    // Every SelectGame / sidebar rebuild / catalog render used to re-decode the
    // same PNGs straight from disk — and the default lazy BitmapImage kept the
    // files locked. Decode once (OnLoad releases the file), Freeze (shareable),
    // cache by path. Local files only; http thumbnails keep WPF's own
    // async-download path.
    private static readonly Dictionary<string, BitmapImage> _bitmapCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// Cached, frozen bitmap for a LOCAL image path. Null when the file is
    /// missing or fails to decode (callers already tolerate a null Source).
    private static BitmapImage? LoadCachedBitmap(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_bitmapCache.TryGetValue(path, out var hit)) return hit;
        if (!File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource   = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // decode now, release the file
            bmp.EndInit();
            bmp.Freeze();
            _bitmapCache[path] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    /// Parse a 6-digit HTML hex color string like "#7A1010" into a WPF Color.
    /// Falls back to the neutral base color on any parse error.
    private static Color ParseHexColor(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
                return Color.FromRgb(
                    Convert.ToByte(hex[0..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
        }
        catch { /* fall through */ }
        return Color.FromRgb(0x14, 0x17, 0x20); // neutral base
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Achievements
    // ══════════════════════════════════════════════════════════════════════════

    private void ShowAchievementToast(AchievementDefinition def)
    {
        if (!CheckAccess()) { Dispatcher.Invoke(() => ShowAchievementToast(def)); return; }
        var toast = new AchievementToast(def) { Owner = this };
        toast.Show();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Install Guide viewer
    // ══════════════════════════════════════════════════════════════════════════

    private void OpenInstallGuide(string title, string guide)
    {
        var win = new InstallGuideWindow(title, guide) { Owner = this };
        win.ShowDialog();
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        // Remote-controlled strings (catalog JSON, news markdown) reach this —
        // ShellExecute would happily RUN a file:///C:/...exe or \\host\share\x.exe,
        // so only web schemes are allowed (P2-12).
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    /// Append one log line. colour overrides the default log green when given
    /// (DeathLink deaths log red, incoming progression items log gold).
    private void AppendLog(string text, Brush? colour = null)
    {
        if (!CheckAccess()) { Dispatcher.Invoke(() => AppendLog(text, colour)); return; }
        // Mirror into the diagnostics ring buffer (UI thread only — no lock needed)
        _diagLogBuffer.Enqueue($"{DateTime.Now:HH:mm:ss} {text}");
        while (_diagLogBuffer.Count > 200) _diagLogBuffer.Dequeue();

        // Autoscroll ONLY when the view is already at the bottom — a user who
        // scrolled up to read (or select/copy) history must not be yanked back
        // down by every new line (UX-12).
        bool atBottom = TxtLog.VerticalOffset + TxtLog.ViewportHeight
                        >= TxtLog.ExtentHeight - 12;

        var run = new Run(text + "\n");
        if (colour != null) run.Foreground = colour;
        _logParagraph.Inlines.Add(run);

        // Trim to the last 300 entries (one Run per call) — prevents unbounded
        // memory growth in long sessions.
        while (_logParagraph.Inlines.Count > 300)
            _logParagraph.Inlines.Remove(_logParagraph.Inlines.FirstInline);

        if (atBottom) TxtLog.ScrollToEnd();
    }

    private void BtnSendChat_Click(object sender, RoutedEventArgs e)
        => SendChatMessage();

    private void TxtChat_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            SendChatMessage();
            e.Handled = true;
        }
    }

    private void SendChatMessage()
    {
        if (_apClient == null) return;
        string text = TxtChat.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        TxtChat.Clear();
        SendChatText(text);
    }

    /// Shared chat-send path — used by the chat box and the ⏱ Countdown button.
    private void SendChatText(string text)
    {
        if (_apClient == null) return;
        AppendLog($"[You] {text}");
        _ = _apClient.SendSayAsync(text).ContinueWith(t =>
        {
            if (t.IsFaulted)
                Dispatcher.Invoke(() =>
                    AppendLog($"[Chat] Send failed: {t.Exception?.InnerException?.Message ?? "error"}"));
        }, TaskScheduler.Default);
    }

    private void SetStatus(string text)
    {
        if (!CheckAccess()) { Dispatcher.Invoke(() => SetStatus(text)); return; }
        TxtStatus.Text = text;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // AP auto-reconnect — backoff loop after an unexpected connection drop.
    // Started from UpdateApStatus; cancelled by any manual connect/disconnect,
    // by a successful reconnect, or by the Settings toggle.
    // ══════════════════════════════════════════════════════════════════════════

    private void TryStartAutoReconnect()
    {
        if (_reconnectCts != null) return;                        // already running
        if (_reconnectAttempt >= ReconnectDelays.Length) return;  // gave up this outage
        if (_reconnectPlugin == null) return;                     // never connected
        if (!SettingsStore.Load().AutoReconnect) return;
        // A running ConnectsItself game owns the slot — reconnecting the
        // launcher would get the player kicked out of their own game (P1-8).
        if (GameRegistry.ActivePlugin?.ConnectsItself == true) return;
        if (string.IsNullOrWhiteSpace(TxtServer.Text) ||
            string.IsNullOrWhiteSpace(TxtSlotName.Text)) return;

        _reconnectCts = new CancellationTokenSource();
        _ = ReconnectLoopAsync(_reconnectCts.Token);
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        try
        {
            while (_reconnectAttempt < ReconnectDelays.Length && !ct.IsCancellationRequested)
            {
                int delay = ReconnectDelays[_reconnectAttempt];
                _reconnectAttempt++;

                for (int s = delay; s > 0 && !ct.IsCancellationRequested; s--)
                {
                    SetStatus($"AP connection lost — reconnecting in {s}s " +
                              $"(attempt {_reconnectAttempt}/{ReconnectDelays.Length})...");
                    await Task.Delay(1000, ct);
                }
                if (ct.IsCancellationRequested) return;
                if (_apClient?.State == ApConnectionState.Connected) return;

                AppendLog($"[Reconnect] Attempt {_reconnectAttempt}/{ReconnectDelays.Length}...");
                _reconnectInProgress = true;
                try     { await ConnectApGlobalAsync(_reconnectPlugin!); }
                finally { _reconnectInProgress = false; }

                if (_apClient?.State == ApConnectionState.Connected)
                {
                    AppendLog("[Reconnect] Connection restored.");
                    ToastService.Show("Reconnected",
                        "AP connection restored — checks are flowing again.",
                        ToastKind.Success);
                    return;   // attempt counter resets in UpdateApStatus(Connected)
                }
            }

            if (!ct.IsCancellationRequested)
            {
                SetStatus("AP reconnect failed — connect manually from the sidebar.");
                ToastService.Show("Reconnect failed",
                    $"Gave up after {ReconnectDelays.Length} attempts. " +
                    "Connect manually from the sidebar.", ToastKind.Error);
            }
        }
        catch (OperationCanceledException) { /* user took over or we connected */ }
        finally
        {
            _reconnectCts?.Dispose();
            _reconnectCts = null;
        }
    }

    private void CancelAutoReconnect(bool resetAttempts)
    {
        try { _reconnectCts?.Cancel(); } catch { /* already disposed */ }
        if (resetAttempts) _reconnectAttempt = 0;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Notification sounds — system sounds only, no audio assets.
    // Throttled to one sound per 400ms so item bursts don't machine-gun.
    // ══════════════════════════════════════════════════════════════════════════

    private static void PlayNotifySound(string kind)
    {
        if (!SoundsEnabled) return;
        var now = DateTime.UtcNow;
        if ((now - _lastSoundAt).TotalMilliseconds < 400) return;
        _lastSoundAt = now;
        try
        {
            switch (kind)
            {
                case "trap":  System.Media.SystemSounds.Exclamation.Play(); break;
                case "death": System.Media.SystemSounds.Hand.Play();        break;
                default:      System.Media.SystemSounds.Asterisk.Play();    break;
            }
        }
        catch { /* no audio device — never fatal */ }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Diagnostics — everything a Discord bug report needs, one clipboard away.
    // ══════════════════════════════════════════════════════════════════════════

    private string BuildDiagnosticsText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Multiworld Launcher diagnostics ===");
        sb.AppendLine($"Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Launcher  : v{LauncherUpdater.CurrentVersion}");
        sb.AppendLine($"OS        : {Environment.OSVersion} " +
                      $"({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
        sb.AppendLine($".NET      : {Environment.Version}");
        sb.AppendLine($"Path      : {AppContext.BaseDirectory}");
        sb.AppendLine($"AP state  : {_apClient?.State.ToString() ?? "no client"}");
        sb.AppendLine();
        sb.AppendLine("--- Games ---");
        foreach (var p in GameRegistry.All)
            sb.AppendLine($"{p.GameId,-22} installed={p.IsInstalled,-5} " +
                          $"running={p.IsRunning,-5} version={p.InstalledVersion ?? "-"}");
        // List catalog entries that have no AP-world credit — helps identify
        // games that need an AP author attributed (M-9).
        if (_catalogEntries?.Count > 0)
        {
            var missingApAuthor = _catalogEntries
                .Where(e => !e.Credits.Any(c =>
                    c.Role.Contains("AP", StringComparison.OrdinalIgnoreCase) ||
                    c.Role.Contains("world", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(e => e.DisplayName)
                .ToList();
            if (missingApAuthor.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"--- Catalog entries missing AP world credit ({missingApAuthor.Count}) ---");
                foreach (var e in missingApAuthor)
                    sb.AppendLine($"  {e.Id,-30} {e.DisplayName}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"--- Last {_diagLogBuffer.Count} log lines ---");
        foreach (var line in _diagLogBuffer)
            sb.AppendLine(line);

        string crashPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
        if (File.Exists(crashPath))
        {
            try
            {
                var lines = File.ReadAllLines(crashPath);
                sb.AppendLine();
                sb.AppendLine("--- crash.log (tail) ---");
                foreach (var l in lines.Skip(Math.Max(0, lines.Length - 40)))
                    sb.AppendLine(l);
            }
            catch { /* locked mid-write — skip the tail */ }
        }
        return sb.ToString();
    }

    private void UpdateApStatus(ApConnectionState state)
    {
        if (!CheckAccess()) { Dispatcher.Invoke(() => UpdateApStatus(state)); return; }

        // ── Unexpected drop → toast + auto-reconnect ─────────────────────────
        // Exception: while a ConnectsItself game runs, its in-game client owns
        // the slot — the server kicking our launcher client is EXPECTED, and
        // reconnecting would kick the player out of their own game (P1-8).
        bool wasConnected = _lastApState == ApConnectionState.Connected;
        bool dropped      = state is ApConnectionState.Disconnected or ApConnectionState.Error;
        bool slotOwnedByGame = GameRegistry.ActivePlugin?.ConnectsItself == true;
        if (wasConnected && dropped && !_apTeardownExpected && !slotOwnedByGame)
        {
            bool autoRec = SettingsStore.Load().AutoReconnect;
            // Keep the status-bar text on the same state machine as TxtApState
            // (UX-11): it used to keep saying "Playing — X" / "Connected — Y"
            // next to a red "AP: Disconnected" after an unexpected drop. The
            // reconnect loop's countdown overwrites this within a second.
            SetStatus(autoRec
                ? "AP connection lost — reconnecting..."
                : "AP connection lost — reconnect from the sidebar.");
            // Warn whenever ANY game session is live (P2-6) — the player may be
            // browsing another sidebar game while their session loses sync.
            if (GameRegistry.ActivePlugin != null)
                ToastService.Show("AP connection lost",
                    autoRec ? "Attempting to reconnect automatically..."
                            : "Reconnect from the sidebar to keep sending checks.",
                    ToastKind.Warning);
            TryStartAutoReconnect();
        }
        else if (wasConnected && dropped && slotOwnedByGame)
        {
            AppendLog("[AP] Launcher connection closed while the game holds the slot — " +
                      "expected for this game (it connects to Archipelago itself).");
        }
        if (state == ApConnectionState.Connected)
            CancelAutoReconnect(resetAttempts: true);
        _lastApState = state;

        // ── Status bar (bottom-right) ────────────────────────────────────────
        TxtApState.Text = state switch
        {
            ApConnectionState.Connected  => "AP: Connected",
            ApConnectionState.Connecting => "AP: Connecting...",
            ApConnectionState.Error      => "AP: Error",
            _                            => "AP: Disconnected"
        };
        TxtApState.Foreground = state switch
        {
            ApConnectionState.Connected => (Brush)FindResource("BrushSuccess"),
            ApConnectionState.Error     => (Brush)FindResource("BrushError"),
            _                           => (Brush)FindResource("BrushMuted")
        };

        bool connected = state == ApConnectionState.Connected;

        // ── Sidebar connection card ──────────────────────────────────────────
        ConnDot.Fill = connected
            ? (Brush)FindResource("BrushSuccess")
            : state == ApConnectionState.Connecting
                ? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))
                : state == ApConnectionState.Error
                    ? (Brush)FindResource("BrushError")
                    : new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x55));

        if (connected && _apClient != null)
        {
            string server = _currentSession?.ServerUri ?? TxtServer.Text.Trim();
            string slot   = _currentSession?.SlotName  ?? TxtSlotName.Text.Trim();
            ConnServerText.Text    = server;
            ConnSlotText.Text      = slot.Length > 0 ? $"Slot: {slot}" : "";
            BtnConnToggle.Content  = "Disconnect";
            BtnConnToggle.IsEnabled = true;
        }
        else if (state == ApConnectionState.Connecting)
        {
            ConnServerText.Text    = "Connecting...";
            ConnSlotText.Text      = "";
            BtnConnToggle.Content  = "...";
            BtnConnToggle.IsEnabled = false;
        }
        else
        {
            ConnServerText.Text    = "Not connected";
            ConnSlotText.Text      = "";
            BtnConnToggle.Content  = "Connect";
            BtnConnToggle.IsEnabled = true;
        }

        // Update the "not connected" hint and suggestion banner
        UpdateGameSuggestionBanner();

        // ── Title bar connection info (center) ───────────────────────────────
        // Read the SESSION record, not the live text boxes (P3-9) — editing
        // the server box while connected used to rewrite the title bar.
        if (connected && _apClient != null)
        {
            string tbServer = _currentSession?.ServerUri ?? TxtServer.Text.Trim();
            string tbSlot   = _currentSession?.SlotName  ?? TxtSlotName.Text.Trim();
            TitleBarConnText.Text      = tbSlot.Length > 0 ? $"{tbServer}  ·  {tbSlot}" : tbServer;
            TitleBarConnInfo.Visibility = Visibility.Visible;
        }
        else
        {
            TitleBarConnInfo.Visibility = Visibility.Collapsed;
        }

        // ── "AP Connected" badge in game header (P3-10) ──────────────────────
        BadgeSession.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

        // ── AP Chat ──────────────────────────────────────────────────────────
        TxtChat.IsEnabled     = connected;
        BtnSendChat.IsEnabled = connected;
        if (!connected) TxtChat.Clear();

        // ── AP end-of-game commands ───────────────────────────────────────────
        PanelApCommands.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        BtnRelease.IsEnabled = connected;
        BtnCollect.IsEnabled = connected;

        // ── AP ecosystem controls (DeathLink / Ready / countdown) ────────────
        BtnCountdown.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        if (!connected) _apReadySent = false;   // Ready never survives a disconnect
        RefreshDeathLinkButton();
        RefreshReadyButton();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Window chrome
    // ══════════════════════════════════════════════════════════════════════════

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Standard title-bar contract (P3-8): double-click toggles maximize,
        // single-click-drag moves. Dragging a maximized window is a no-op
        // (no drag-to-restore — keep the behavior predictable).
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (WindowState == WindowState.Normal) DragMove();
    }

    // ── Maximize / restore (P3-8) ─────────────────────────────────────────────
    // The window is borderless (WindowStyle=None + AllowsTransparency), so two
    // things WPF normally provides must be done by hand: an affordance to
    // maximize at all, and clamping the maximized bounds to the monitor's WORK
    // area — without the WM_GETMINMAXINFO hook below, a maximized borderless
    // window covers the taskbar.

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;

    /// Keep the maximize button glyph/tooltip in sync however the state
    /// changes (button, double-click, restored from settings, AeroSnap).
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool max = WindowState == WindowState.Maximized;
        BtnMaximize.Content = max ? "❐" : "□";
        BtnMaximize.ToolTip = max ? "Restore" : "Maximize";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            ClampMaximizedBoundsToWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// Fill in MINMAXINFO so a maximized window covers exactly the work area
    /// of its CURRENT monitor (multi-monitor safe) and honors Min{Width,Height}.
    private void ClampMaximizedBoundsToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = System.Runtime.InteropServices.Marshal
            .PtrToStructure<NativeMaximizeMethods.MINMAXINFO>(lParam);

        IntPtr monitor = NativeMaximizeMethods.MonitorFromWindow(
            hwnd, NativeMaximizeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new NativeMaximizeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal
                    .SizeOf<NativeMaximizeMethods.MONITORINFO>(),
            };
            if (NativeMaximizeMethods.GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;
                var area = info.rcMonitor;
                mmi.ptMaxPosition.x = work.left   - area.left;
                mmi.ptMaxPosition.y = work.top    - area.top;
                mmi.ptMaxSize.x     = work.right  - work.left;
                mmi.ptMaxSize.y     = work.bottom - work.top;
            }
        }

        // Preserve the XAML MinWidth/MinHeight (device pixels — scale by DPI).
        var dpi = VisualTreeHelper.GetDpi(this);
        mmi.ptMinTrackSize.x = (int)Math.Ceiling(MinWidth  * dpi.DpiScaleX);
        mmi.ptMinTrackSize.y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);

        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
    }

    // P/Invoke for work-area-aware maximize
    private static class NativeMaximizeMethods
    {
        internal const int MONITOR_DEFAULTTONEAREST = 2;

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct POINT { public int x; public int y; }

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct RECT { public int left; public int top; public int right; public int bottom; }

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct MONITORINFO
        {
            public int  cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int  dwFlags;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    }

    private void ResizeGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        // Ask Windows to start a native bottom-right-corner resize drag.
        // Works for WindowStyle=None windows and interacts with AeroSnap correctly.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        NativeResizeMethods.ReleaseCapture();
        NativeResizeMethods.SendMessage(hwnd, 0xA1 /* WM_NCLBUTTONDOWN */,
                                        new IntPtr(17) /* HTBOTTOMRIGHT */, IntPtr.Zero);
        e.Handled = true;
    }

    // P/Invoke for native window resize
    private static class NativeResizeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => Close();   // all shutdown logic lives in OnClosing (also covers Alt+F4)

    /// Persist window position/size/maximized state and the last-used server.
    /// When maximized, save the pre-maximize bounds so a later un-maximized
    /// start restores a sensible window instead of full-screen dimensions.
    private void SaveWindowState()
    {
        var cs = SettingsStore.Load();
        bool maximized = WindowState == WindowState.Maximized;
        var bounds = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        if (!double.IsFinite(bounds.Width) || bounds.Width < 100 ||
            !double.IsFinite(bounds.Height) || bounds.Height < 100)
            bounds = new Rect(Left, Top, Width, Height);
        cs.WindowLeft      = bounds.Left;
        cs.WindowTop       = bounds.Top;
        cs.WindowWidth     = bounds.Width;
        cs.WindowHeight    = bounds.Height;
        cs.WindowMaximized = maximized;
        if (!string.IsNullOrEmpty(TxtServer.Text))
            cs.DefaultApServer = TxtServer.Text.Trim();
        SettingsStore.Save(cs);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Closing — ONE path for the custom ✕, Alt+F4, taskbar close, and system
    // close (P2-7: only the ✕ used to run shutdown logic; every other route
    // skipped bounds/session persistence and left a ghost tray icon).
    // ══════════════════════════════════════════════════════════════════════════

    private bool _shutdownStarted;    // async teardown running — swallow re-closes
    private bool _shutdownComplete;   // teardown done — let the close proceed

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete) { base.OnClosing(e); return; }
        if (_shutdownStarted)  { e.Cancel = true;   return; }

        // ── Close-guard while a game session is live (P1-10) ─────────────────
        // The launcher IS the AP bridge in V2 — closing it mid-session silently
        // stops all check/goal syncing while the game keeps looking healthy.
        var running = GameRegistry.ActivePlugin;
        if (running != null)
        {
            var choice = ConfirmDialog.ShowThreeWay(this,
                "Game still running",
                $"{running.DisplayName} is still running — the launcher keeps " +
                "your progress synced to Archipelago. Closing it would silently " +
                "stop all syncing while the game looks fine.",
                yesText:    "Minimize to tray",
                noText:     "Stop game and exit",
                cancelText: "Cancel");

            if (choice == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (choice == MessageBoxResult.Yes)
            {
                // Minimize to tray — the session keeps running and syncing.
                e.Cancel = true;
                _trayIcon.Show($"Playing {running.DisplayName} — launcher keeps syncing");
                Hide();
                return;
            }
            // No → stop the game as part of the shutdown below.
        }

        // WPF cannot await inside OnClosing — cancel this close, run the async
        // teardown, then Close() again (passes the _shutdownComplete gate).
        e.Cancel = true;
        _ = ShutdownAsync(running);
    }

    /// Full shutdown: save window state, stop a running game when requested,
    /// tear down the AP session, drop the tray icon, then close for real.
    private async Task ShutdownAsync(IGamePlugin? stopGame)
    {
        _shutdownStarted = true;

        // Cancel all window-lifetime background tasks before tearing down UI resources.
        _mainWindowCts.Cancel();

        SaveWindowState();

        try { if (stopGame != null) await stopGame.StopAsync(); } catch { }
        try { await CleanupSessionAsync(); } catch { }

        _trayIcon.Dispose();
        _shutdownComplete = true;
        Close();
    }

    // ── Tray helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Restore the launcher window to the foreground.
    /// Called when the user clicks the tray icon or chooses "Open Launcher".
    /// </summary>
    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;  // flash-to-front trick: set then clear Topmost
        Focus();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Window bounds persistence (restore side — saving happens in BtnClose_Click)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Apply the persisted window size/position/maximized state, clamped to the
    /// current virtual screen so an unplugged monitor can never strand the
    /// window off-screen. Saved sizes below 700×500 are treated as corrupt and
    /// ignored (the XAML defaults stay in effect).
    /// </summary>
    private void RestoreWindowBounds(LauncherSettings s)
    {
        double vsLeft = SystemParameters.VirtualScreenLeft;
        double vsTop  = SystemParameters.VirtualScreenTop;
        double vsW    = SystemParameters.VirtualScreenWidth;
        double vsH    = SystemParameters.VirtualScreenHeight;

        // ── Size: honor the saved size when sane, clamped to the desktop ─────
        if (double.IsFinite(s.WindowWidth)  && s.WindowWidth  >= 700 &&
            double.IsFinite(s.WindowHeight) && s.WindowHeight >= 500)
        {
            Width  = Math.Min(s.WindowWidth,  Math.Max(700, vsW));
            Height = Math.Min(s.WindowHeight, Math.Max(500, vsH));
        }

        // ── Position: keep a usable strip of the title bar on-screen ─────────
        const double edge = 80;   // minimum visible sliver in device units
        double left = double.IsFinite(s.WindowLeft) ? s.WindowLeft : 100;
        double top  = double.IsFinite(s.WindowTop)  ? s.WindowTop  : 100;
        Left = Math.Max(vsLeft - Width + edge, Math.Min(left, vsLeft + vsW - edge));
        Top  = Math.Max(vsTop,                 Math.Min(top,  vsTop + vsH - edge));

        if (s.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Recent AP connections (MRU dropdown on the sidebar connect panel)
    // ══════════════════════════════════════════════════════════════════════════

    /// Show the "⏷ Recent" affordance only when connection history exists.
    private void RefreshRecentConnButton()
    {
        if (!CheckAccess()) { Dispatcher.Invoke(RefreshRecentConnButton); return; }
        var s = SettingsStore.Load();
        BtnRecentConn.Visibility = s.RecentConnections.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnRecentConn_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var s = SettingsStore.Load();
        if (s.RecentConnections.Count == 0)
        {
            BtnRecentConn.Visibility = Visibility.Collapsed;
            return;
        }

        var textBr  = (Brush)FindResource("BrushText");
        var menu = new ContextMenu
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x24)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            Foreground      = textBr,
            PlacementTarget = BtnRecentConn,
            Placement       = PlacementMode.Bottom,
        };

        foreach (var rc in s.RecentConnections)
        {
            // TextBlock header dodges WPF's "_" access-key parsing in slot names.
            var item = new MenuItem
            {
                Header     = new TextBlock { Text = $"{rc.Slot} @ {rc.Server}" },
                Background = Brushes.Transparent,
                Foreground = textBr,
            };
            string server = rc.Server, slot = rc.Slot;
            item.Click += (_, _) =>
            {
                TxtServer.Text   = server;
                TxtSlotName.Text = slot;
                TxtPassword.Focus();   // passwords are never stored — user types it
            };

            // Sub-item: remove just this entry from the MRU list.
            var removeItem = new MenuItem
            {
                Header     = new TextBlock { Text = "Remove this entry" },
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0x60, 0x60)),
            };
            removeItem.Click += (_, e2) =>
            {
                e2.Handled = true;
                var s2 = SettingsStore.Load();
                s2.RecentConnections.RemoveAll(r =>
                    r.Server == server && r.Slot == slot);
                SettingsStore.Save(s2);
                RefreshRecentConnButton();
                menu.IsOpen = false;
            };
            item.Items.Add(removeItem);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var clear = new MenuItem
        {
            Header     = new TextBlock { Text = "Clear recents" },
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0x60, 0x60)),
        };
        clear.Click += (_, _) =>
        {
            var s2 = SettingsStore.Load();
            s2.RecentConnections.Clear();
            SettingsStore.Save(s2);
            RefreshRecentConnButton();
        };
        menu.Items.Add(clear);

        menu.IsOpen = true;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // First-run getting-started checklist (PanelEmpty)
    // ══════════════════════════════════════════════════════════════════════════

    /// Re-evaluate the three checklist steps against live launcher state.
    /// Callers control PanelEmpty visibility; this only paints the step icons.
    private void RefreshWelcomeChecklist()
    {
        if (!CheckAccess()) { Dispatcher.Invoke(RefreshWelcomeChecklist); return; }

        bool picked    = _hasSelectedGameOnce || _selectedPlugin != null;
        bool installed = GameRegistry.All.Any(p => p.IsInstalled);
        bool connected = _apClient?.State == ApConnectionState.Connected;

        SetChecklistStep(StepPickGameIcon, picked,    "①");
        SetChecklistStep(StepInstallIcon,  installed, "②");
        SetChecklistStep(StepConnectIcon,  connected, "③");
    }

    /// Done steps show a green ✓; pending steps show their number circle.
    private void SetChecklistStep(TextBlock icon, bool done, string number)
    {
        icon.Text       = done ? "✓" : number;
        icon.Foreground = done
            ? (Brush)FindResource("BrushSuccess")
            : (Brush)FindResource("BrushMuted");
    }

    // ── Home page (M-3): featured game + recently played ─────────────────────

    private void RefreshHomePage()
    {
        if (!CheckAccess()) { Dispatcher.Invoke(RefreshHomePage); return; }

        RefreshWelcomeChecklist();

        // Installed games in library order
        var allPlugins = GameRegistry.All.ToList();
        var installed  = LibraryStore.GetSortedGameIds()
            .Select(id => allPlugins.FirstOrDefault(p => p.GameId == id))
            .Where(p => p is { IsInstalled: true })
            .Cast<IGamePlugin>()
            .ToList();

        // Featured = most-recently played installed game
        _homeHeroPlugin = installed
            .OrderByDescending(p => PlaytimeService.LastPlayed(p.GameId) ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (_homeHeroPlugin != null)
        {
            TxtHomeHeroName.Text = _homeHeroPlugin.DisplayName;

            var played = PlaytimeService.LastPlayed(_homeHeroPlugin.GameId);
            var pt     = PlaytimeService.TotalPlaytime(_homeHeroPlugin.GameId);
            TxtHomeHeroMeta.Text = pt.TotalMinutes >= 1
                ? $"{PlaytimeService.FormatPlaytime(pt)} played · last played {(played.HasValue ? PlaytimeService.FormatRelativeDate(played.Value) : "never")}"
                : "Not yet played";

            var desc = _homeHeroPlugin.Description ?? "";
            TxtHomeHeroDesc.Text = desc.Length > 240 ? desc[..240] + "…" : desc;

            string heroPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Heroes",
                $"{_homeHeroPlugin.GameId}_hero.png");
            if (!File.Exists(heroPath))
                heroPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Heroes", "_generic_hero.png");

            HomeHeroBanner.Background = LoadCachedBitmap(heroPath) is { } bmp
                ? (System.Windows.Media.Brush)new ImageBrush(bmp) { Stretch = System.Windows.Media.Stretch.UniformToFill }
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x2C));

            HomeHeroContainer.Visibility = Visibility.Visible;
        }
        else
        {
            HomeHeroContainer.Visibility = Visibility.Collapsed;
        }

        // Recently played: up to 4 other installed games with play history
        var recent = installed
            .Where(p => PlaytimeService.LastPlayed(p.GameId).HasValue
                     && p.GameId != _homeHeroPlugin?.GameId)
            .OrderByDescending(p => PlaytimeService.LastPlayed(p.GameId))
            .Take(4)
            .ToList();

        HomeRecentCards.Children.Clear();
        foreach (var p in recent)
            HomeRecentCards.Children.Add(BuildHomeRecentCard(p));
        HomeRecentSection.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Hide getting-started card once the user has installed games
        HomeGetStartedSection.Visibility = installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Update browse-all label with live count
        int total = GameRegistry.All.Count();
        BtnHomeBrowseAll.Content = $"Browse all {total} games →";
    }

    private Border BuildHomeRecentCard(IGamePlugin plugin)
    {
        var icon = new System.Windows.Controls.Image
        {
            Source  = LoadCachedBitmap(plugin.IconPath),
            Width   = 40, Height = 40,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Margin  = new Thickness(0, 0, 0, 6),
        };
        var name = new TextBlock
        {
            Text          = plugin.DisplayName,
            FontSize      = 10,
            FontWeight    = FontWeights.SemiBold,
            Foreground    = (System.Windows.Media.Brush)FindResource("BrushText"),
            TextWrapping  = TextWrapping.Wrap,
            MaxWidth      = 80,
            TextAlignment = TextAlignment.Center,
        };
        var lp = PlaytimeService.LastPlayed(plugin.GameId);
        var meta = new TextBlock
        {
            Text          = lp.HasValue ? PlaytimeService.FormatRelativeDate(lp.Value) : "",
            FontSize      = 9,
            Foreground    = (System.Windows.Media.Brush)FindResource("BrushMuted"),
            TextAlignment = TextAlignment.Center,
            Opacity       = 0.7,
            Margin        = new Thickness(0, 2, 0, 0),
        };
        var inner = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        inner.Children.Add(icon);
        inner.Children.Add(name);
        inner.Children.Add(meta);

        var normalBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x17, 0x20));
        var hoverBg  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30));
        var card = new Border
        {
            Width           = 104,
            Padding         = new Thickness(10),
            Margin          = new Thickness(0, 0, 10, 0),
            Background      = normalBg,
            BorderBrush     = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Cursor          = System.Windows.Input.Cursors.Hand,
            Child           = inner,
        };
        var p2 = plugin;
        card.MouseLeftButtonDown += (_, _) => { SelectGame(p2); HighlightGameCard(p2); };
        card.MouseEnter          += (_, _) => card.Background = hoverBg;
        card.MouseLeave          += (_, _) => card.Background = normalBg;
        return card;
    }

    private void HomeHero_Click(object sender, MouseButtonEventArgs e)
    {
        if (_homeHeroPlugin == null) return;
        SelectGame(_homeHeroPlugin);
        HighlightGameCard(_homeHeroPlugin);
    }

    private void BtnHomeHeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_homeHeroPlugin == null) return;
        SelectGame(_homeHeroPlugin);
        HighlightGameCard(_homeHeroPlugin);
        OpenGameTab(PageTab.Play);
        e.Handled = true;
    }

    private void StepPickGame_Click(object sender, MouseButtonEventArgs e)
    {
        // Do-the-thing: open the first library game, or Browse when empty.
        var firstId = LibraryStore.GetSortedGameIds().FirstOrDefault();
        var plugin  = firstId != null
            ? GameRegistry.All.FirstOrDefault(p => p.GameId == firstId)
            : null;
        if (plugin != null) SelectGame(plugin);
        else                BtnBrowse_Click(sender, new RoutedEventArgs());
    }

    private void StepInstall_Click(object sender, MouseButtonEventArgs e)
    {
        if (_selectedPlugin == null) { StepPickGame_Click(sender, e); return; }
        OpenGameTab(PageTab.Play);   // the Play tab hosts the install flow
    }

    private void StepConnect_Click(object sender, MouseButtonEventArgs e)
    {
        // Same affordance as the Play tab's connect hint
        PanelConnInputs.Visibility = Visibility.Visible;
        BtnConnToggle.Content      = "Cancel";
        TxtServer.Focus();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Items tab — List / Grid view toggle + PopTracker-style tile rendering
    // ══════════════════════════════════════════════════════════════════════════

    private void BtnTrackerViewList_Click(object sender, MouseButtonEventArgs e)
        => SetTrackerViewMode("list", persist: true);

    private void BtnTrackerViewGrid_Click(object sender, MouseButtonEventArgs e)
        => SetTrackerViewMode("grid", persist: true);

    /// Switch between the DataGrid ("list") and the aggregated tile view
    /// ("grid"). Persists the choice to launcher settings when requested.
    private void SetTrackerViewMode(string mode, bool persist)
    {
        mode = mode == "grid" ? "grid" : "list";
        _trackerViewMode = mode;
        bool grid = mode == "grid";

        TrackerGrid.Visibility       = grid ? Visibility.Collapsed : Visibility.Visible;
        TrackerTileScroll.Visibility = grid ? Visibility.Visible   : Visibility.Collapsed;

        // Segmented-toggle visuals: active half gets a filled background + gold text
        var gold     = (Brush)FindResource("BrushAccent");
        var muted    = (Brush)FindResource("BrushMuted");
        var activeBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50));
        BtnTrackerViewList.Background = grid ? Brushes.Transparent : activeBg;
        BtnTrackerViewGrid.Background = grid ? activeBg : Brushes.Transparent;
        TxtTrackerViewList.Foreground = grid ? muted : gold;
        TxtTrackerViewGrid.Foreground = grid ? gold : muted;

        if (persist)
        {
            var s = SettingsStore.Load();
            if (s.TrackerViewMode != mode)
            {
                s.TrackerViewMode = mode;
                SettingsStore.Save(s);
            }
        }

        // Re-render tiles for the current filter (no-op unless Items tab is open)
        if (grid) ApplyTrackerFilter();
    }

    /// Render the currently filtered tracked items as aggregated 64×64 tiles,
    /// grouped by item name. Border colour = AP classification (gold =
    /// progression, blue = useful, red = trap, gray = filler).
    private void RenderTrackerTiles(IReadOnlyList<TrackedItem> filtered)
    {
        TrackerTilePanel.Children.Clear();

        if (filtered.Count == 0)
        {
            TrackerTilePanel.Children.Add(new TextBlock
            {
                Text       = "No items to show.",
                FontSize   = 12,
                Foreground = (Brush)FindResource("BrushMuted"),
                Opacity    = 0.7,
                Margin     = new Thickness(4),
            });
            return;
        }

        var groups = filtered
            .GroupBy(i => i.ItemName, StringComparer.Ordinal)
            .Select(g => (Name: g.Key,
                          Items: g.ToList(),
                          Flags: g.Aggregate(0, (acc, i) => acc | i.ItemFlags)))
            .OrderBy(g => FlagRank(g.Flags))
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, items, flags) in groups)
            TrackerTilePanel.Children.Add(BuildTrackerTile(name, items, flags));
    }

    private Border BuildTrackerTile(string name, List<TrackedItem> items, int flags)
    {
        var accent = FlagAccent(flags);

        var tile = new Border
        {
            Width           = 64,
            Height          = 64,
            Margin          = new Thickness(0, 0, 10, 10),
            CornerRadius    = new CornerRadius(4),
            Background      = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x20)),
            BorderBrush     = new SolidColorBrush(accent),
            BorderThickness = new Thickness(2),
            ToolTip         = BuildTileToolTip(name, items),
        };

        var grid = new Grid();

        // Centered initials (first letters of up to two words)
        grid.Children.Add(new TextBlock
        {
            Text                = ItemInitials(name),
            FontSize            = 20,
            FontWeight          = FontWeights.Bold,
            Foreground          = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        });

        // Count badge bottom-right when the item arrived more than once
        if (items.Count > 1)
        {
            grid.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Margin              = new Thickness(0, 0, 3, 3),
                CornerRadius        = new CornerRadius(2),
                Background          = new SolidColorBrush(Color.FromArgb(0xE0, 0x0D, 0x10, 0x18)),
                Padding             = new Thickness(4, 0, 4, 1),
                Child = new TextBlock
                {
                    Text       = items.Count > 999 ? "999+" : items.Count.ToString(),
                    FontSize   = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("BrushAccent"),
                },
            });
        }

        tile.Child = grid;
        return tile;
    }

    /// Tooltip: item name (+count), then one line per instance
    /// ("from <sender> · <location> · <time>"), capped at 10 lines.
    private object BuildTileToolTip(string name, List<TrackedItem> items)
    {
        var muted = (Brush)FindResource("BrushMuted");
        var tip   = new StackPanel { MaxWidth = 400 };

        tip.Children.Add(new TextBlock
        {
            Text       = items.Count > 1 ? $"{name}  ×{items.Count}" : name,
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 3),
        });

        const int maxLines = 10;
        for (int i = 0; i < items.Count && i < maxLines; i++)
        {
            var it = items[i];
            tip.Children.Add(new TextBlock
            {
                Text         = $"from {it.SenderName} · {it.LocationName} · " +
                               $"{it.Timestamp.ToLocalTime():HH:mm:ss}",
                FontSize     = 10,
                Foreground   = muted,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        if (items.Count > maxLines)
        {
            tip.Children.Add(new TextBlock
            {
                Text       = $"+{items.Count - maxLines} more",
                FontSize   = 10,
                FontStyle  = FontStyles.Italic,
                Foreground = muted,
                Margin     = new Thickness(0, 2, 0, 0),
            });
        }

        return new System.Windows.Controls.ToolTip
        {
            Background  = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            Foreground  = (Brush)FindResource("BrushText"),
            Content     = tip,
        };
    }

    /// "Progressive Sword" → "PS" · "Bomb" → "BO" · "" → "?"
    private static string ItemInitials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
            return char.ToUpperInvariant(words[0][0]).ToString() +
                   char.ToUpperInvariant(words[1][0]);
        if (words.Length == 1)
            return words[0].Length >= 2
                ? words[0][..2].ToUpperInvariant()
                : words[0].ToUpperInvariant();
        return "?";
    }

    /// Sort rank for tile ordering: progression → useful → trap → filler.
    private static int FlagRank(int flags) =>
        (flags & 0b001) != 0 ? 0
      : (flags & 0b010) != 0 ? 1
      : (flags & 0b100) != 0 ? 2
      :                        3;

    /// AP classification → tile border colour.
    private static Color FlagAccent(int flags) =>
        (flags & 0b001) != 0 ? Color.FromRgb(0xCC, 0xA8, 0x00)   // progression — gold
      : (flags & 0b010) != 0 ? Color.FromRgb(0x4A, 0x7A, 0xDF)   // useful — blue
      : (flags & 0b100) != 0 ? Color.FromRgb(0xD9, 0x4A, 0x4A)   // trap — red
      :                        Color.FromRgb(0x3A, 0x3F, 0x55);  // filler — gray

    // ══════════════════════════════════════════════════════════════════════════
    // Keyboard shortcuts + quick switcher (Ctrl+K)
    // ══════════════════════════════════════════════════════════════════════════

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        // ── Ctrl+K: toggle the quick switcher (works even while typing) ──────
        if (ctrl && e.Key == Key.K)
        {
            if (QuickSwitchOverlay.Visibility == Visibility.Visible) CloseQuickSwitch();
            else                                                     OpenQuickSwitch();
            e.Handled = true;
            return;
        }

        // ── F1: toggle keyboard shortcuts cheat sheet ─────────────────────────
        if (e.Key == Key.F1)
        {
            BtnHelp_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // ── Esc: close the topmost overlay (works even while typing) ─────────
        if (e.Key == Key.Escape)
        {
            if (QuickSwitchOverlay.Visibility == Visibility.Visible)
            {
                CloseQuickSwitch();
                e.Handled = true;
            }
            else if (ShortcutsOverlay.Visibility == Visibility.Visible)
            {
                ShortcutsOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            else if (PanelCatalogDetail.Visibility == Visibility.Visible)
            {
                PanelCatalogDetail.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            return;
        }

        // ── Everything below respects typing context ──────────────────────────
        // TextBoxBase also covers the RichTextBox AP log (selectable, UX-12).
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
                                    or PasswordBox) return;
        if (!ctrl) return;

        // Ctrl+F: focus the sidebar library search
        if (e.Key == Key.F)
        {
            TxtSidebarSearch.Focus();
            TxtSidebarSearch.SelectAll();
            e.Handled = true;
            return;
        }

        // Ctrl+Z: undo last sidebar drag-reorder (UX-7)
        if (e.Key == Key.Z && _orderHistory.Count > 0)
        {
            var order = _orderHistory[^1];
            _orderHistory.RemoveAt(_orderHistory.Count - 1);
            LibraryStore.RestoreOrder(order);
            RebuildGameList();
            if (_selectedPlugin != null) HighlightGameCard(_selectedPlugin);
            e.Handled = true;
            return;
        }

        // Ctrl+1..5: switch game tabs (only meaningful with a selected game)
        PageTab? tab = e.Key switch
        {
            Key.D1 or Key.NumPad1 => PageTab.Play,
            Key.D2 or Key.NumPad2 => PageTab.Progression,
            Key.D3 or Key.NumPad3 => PageTab.Tracker,
            Key.D4 or Key.NumPad4 => PageTab.News,
            Key.D5 or Key.NumPad5 => PageTab.Settings,
            _                     => null,
        };
        if (tab != null && _selectedPlugin != null)
        {
            OpenGameTab(tab.Value);
            e.Handled = true;
        }
    }

    /// Bring the game page to the front (over Browse/Achievements/empty state)
    /// and switch tab.
    private void OpenGameTab(PageTab tab)
    {
        if (_selectedPlugin == null) return;
        PanelBrowse.Visibility       = Visibility.Collapsed;
        PanelAchievements.Visibility = Visibility.Collapsed;
        PanelEmpty.Visibility        = Visibility.Collapsed;
        PanelGame.Visibility         = Visibility.Visible;
        SwitchTab(tab);
    }

    private void OpenQuickSwitch()
    {
        _quickSwitchEntries = BuildQuickSwitchEntries();
        QuickSwitchOverlay.Visibility = Visibility.Visible;
        TxtQuickSwitch.Text = "";
        RefreshQuickSwitchList();
        TxtQuickSwitch.Focus();
    }

    private void CloseQuickSwitch()
    {
        QuickSwitchOverlay.Visibility = Visibility.Collapsed;
        Focus();   // hand keyboard focus back to the window
    }

    // ── Keyboard shortcuts overlay (? / F1) ───────────────────────────────────

    private void BtnHelp_Click(object sender, RoutedEventArgs e)
    {
        ShortcutsOverlay.Visibility = ShortcutsOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ShortcutsOverlay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ShortcutsOverlay.Visibility = Visibility.Collapsed;
    }

    private void BtnShortcutsClose_Click(object sender, RoutedEventArgs e)
    {
        ShortcutsOverlay.Visibility = Visibility.Collapsed;
    }

    /// Command sources: library games (in sidebar order) + static actions.
    private List<QuickSwitchEntry> BuildQuickSwitchEntries()
    {
        var list = new List<QuickSwitchEntry>();

        foreach (var id in LibraryStore.GetSortedGameIds())
        {
            var plugin = GameRegistry.All.FirstOrDefault(p => p.GameId == id);
            if (plugin == null) continue;
            string hint = plugin.IsRunning   ? "Running"
                        : plugin.IsInstalled ? "Installed"
                                             : "Not installed";
            var captured = plugin;
            list.Add(new QuickSwitchEntry(
                plugin.DisplayName, plugin.Subtitle, hint, () => SelectGame(captured)));
        }

        list.Add(new QuickSwitchEntry("Browse Games",
            "Open the Archipelago game catalog", "Action",
            () => BtnBrowse_Click(this, new RoutedEventArgs())));
        list.Add(new QuickSwitchEntry("Achievements",
            "Every trophy across all games", "Action",
            () => ShowAchievementsPage()));
        list.Add(new QuickSwitchEntry("Settings tab",
            "Open the selected game's settings", "Action",
            () => OpenGameTab(PageTab.Settings)));
        list.Add(new QuickSwitchEntry("News tab",
            "Patch notes and announcements", "Action",
            () => OpenGameTab(PageTab.News)));

        return list;
    }

    private void RefreshQuickSwitchList()
    {
        if (LstQuickSwitch == null) return;

        string q = TxtQuickSwitch?.Text.Trim() ?? "";
        IEnumerable<QuickSwitchEntry> matches = _quickSwitchEntries;
        if (q.Length > 0)
            matches = _quickSwitchEntries
                .Where(en => en.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                             en.Sub.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(en =>
                    en.Label.StartsWith(q, StringComparison.OrdinalIgnoreCase));

        LstQuickSwitch.Items.Clear();
        foreach (var entry in matches)
        {
            var item = new ListBoxItem
            {
                Style   = (Style)FindResource("QuickSwitchItemStyle"),
                Content = BuildQuickSwitchRow(entry),
                Tag     = entry,
            };
            item.PreviewMouseLeftButtonUp += (_, _) =>
            {
                LstQuickSwitch.SelectedItem = item;
                ExecuteQuickSwitchSelection();
            };
            LstQuickSwitch.Items.Add(item);
        }

        if (LstQuickSwitch.Items.Count > 0)
            LstQuickSwitch.SelectedIndex = 0;
    }

    /// Result row: initial-circle + label/sub + right-aligned state tag.
    private UIElement BuildQuickSwitchRow(QuickSwitchEntry entry)
    {
        var gold  = (Brush)FindResource("BrushAccent");
        var muted = (Brush)FindResource("BrushMuted");

        var dock = new DockPanel { LastChildFill = true };

        var iconBorder = new Border
        {
            Width             = 26,
            Height            = 26,
            CornerRadius      = new CornerRadius(4),
            Background        = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Margin            = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text                = entry.Label.Length > 0
                    ? entry.Label[..1].ToUpperInvariant() : "?",
                FontSize            = 12,
                FontWeight          = FontWeights.Bold,
                Foreground          = gold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            },
        };
        DockPanel.SetDock(iconBorder, Dock.Left);
        dock.Children.Add(iconBorder);

        var tag = new TextBlock
        {
            Text              = entry.Tag,
            FontSize          = 10,
            Foreground        = entry.Tag == "Running"
                ? (Brush)FindResource("BrushSuccess") : muted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(10, 0, 0, 0),
        };
        DockPanel.SetDock(tag, Dock.Right);
        dock.Children.Add(tag);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text         = entry.Label,
            FontSize     = 12,
            FontWeight   = FontWeights.SemiBold,
            Foreground   = (Brush)FindResource("BrushText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (entry.Sub.Length > 0)
        {
            textStack.Children.Add(new TextBlock
            {
                Text         = entry.Sub,
                FontSize     = 10,
                Foreground   = muted,
                Margin       = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        dock.Children.Add(textStack);

        return dock;
    }

    private void TxtQuickSwitch_TextChanged(object sender, TextChangedEventArgs e)
        => RefreshQuickSwitchList();

    private void TxtQuickSwitch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int count = LstQuickSwitch.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                LstQuickSwitch.SelectedIndex =
                    Math.Min(LstQuickSwitch.SelectedIndex + 1, count - 1);
                LstQuickSwitch.ScrollIntoView(LstQuickSwitch.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                LstQuickSwitch.SelectedIndex =
                    Math.Max(LstQuickSwitch.SelectedIndex - 1, 0);
                LstQuickSwitch.ScrollIntoView(LstQuickSwitch.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                ExecuteQuickSwitchSelection();
                e.Handled = true;
                break;
        }
    }

    private void QuickSwitchBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Close only on true backdrop clicks — clicks inside the card arrive
        // with a child element as the original source.
        if (ReferenceEquals(e.OriginalSource, QuickSwitchOverlay))
            CloseQuickSwitch();
    }

    private void ExecuteQuickSwitchSelection()
    {
        if (LstQuickSwitch.SelectedItem is ListBoxItem { Tag: QuickSwitchEntry entry })
        {
            CloseQuickSwitch();
            entry.Activate();
        }
    }
}
