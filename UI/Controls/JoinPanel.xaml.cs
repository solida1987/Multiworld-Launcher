using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LauncherV2.Core;
using LauncherV2.Core.Archipelago;
using LauncherV2.Core.Plugins;

namespace LauncherV2.UI.Controls;

// JoinPanel — the seed you harvested, as things you can press Play on.
//
// One card per player slot. The card answers, in order, the only questions
// that matter: can THIS machine play it, and with one press. Installed game →
// Play (server hosted, patch placed, AP connected, game launched). Game with a
// London plugin but not installed → where to get it. Game with no plugin →
// said plainly, because "not possible yet" is an answer and silence is not.
//
// Several cards can be Playing at once — each runs its own ApJoinSession, and
// stopping one never touches the others.
public partial class JoinPanel : System.Windows.Controls.UserControl
{
    private IReadOnlyList<SeedInfo> _seeds = Array.Empty<SeedInfo>();
    private SeedInfo? _seed;
    private ApEngine.Report? _engine;

    /// The SAME catalogue the Plugin Library shows, keyed by the name a seed's
    /// slot uses. Sharing it is the point: a game the library offers one click
    /// away must never be reported here as one nothing covers.
    private Dictionary<string, StoreGame>? _catalogue;
    private readonly DispatcherTimer _tick;

    public JoinPanel()
    {
        InitializeComponent();

        // Live counters and running-state changes arrive from pool threads and
        // from game processes; a 2 s sweep keeps every card honest without any
        // card having to know why something changed.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _tick.Tick += (_, _) => RefreshCards();

        Loaded   += (_, _) => { Refresh(); _tick.Start(); };
        Unloaded += (_, _) => _tick.Stop();
    }

    /// Re-reads the library and the engine. Called every time the mode opens.
    public void Refresh()
    {
        var settings = SettingsStore.Load();
        _engine = ApEngine.Discover(string.IsNullOrWhiteSpace(settings.ApEnginePath)
                                    ? null : settings.ApEnginePath);

        _seeds = ApSeedLibrary.List();
        string? keep = _seed?.Id;

        CmbSeed.Items.Clear();
        foreach (var s in _seeds)
            CmbSeed.Items.Add($"{s.Id}   ·   {s.Slots.Count} players   ·   {s.Created:d MMM HH:mm}");

        int idx = keep == null ? 0 : Math.Max(0, _seeds.ToList().FindIndex(s => s.Id == keep));
        if (CmbSeed.Items.Count > 0) CmbSeed.SelectedIndex = idx;
        else { _seed = null; RefreshCards(); }

        if (_catalogue == null) _ = LoadCatalogueAsync();
    }

    /// The catalogue's game -> plugin map. Best-effort: offline, the cards
    /// still work for installed games, and the rest say the lookup failed
    /// rather than guessing.
    private async Task LoadCatalogueAsync()
    {
        var map = new Dictionary<string, StoreGame>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var index = await StoreCatalog.FetchAsync();
            foreach (var g in index?.Games ?? Array.Empty<StoreGame>())
                map[string.IsNullOrWhiteSpace(g.ApWorldName) ? g.Name : g.ApWorldName] = g;
        }
        catch { /* offline: installed games still play, the rest say so */ }
        _catalogue = map;
        Dispatcher.BeginInvoke(RefreshCards);
    }

    private void CmbSeed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = CmbSeed.SelectedIndex;
        _seed = i >= 0 && i < _seeds.Count ? _seeds[i] : null;
        RefreshCards();
    }

    // ------------------------------------------------------------------ cards

    private void RefreshCards()
    {
        PanelSlots.Children.Clear();
        PanelJoinEmpty.Visibility = _seed == null ? Visibility.Visible : Visibility.Collapsed;
        RefreshServerChip();
        if (_seed == null) return;

        foreach (var slot in _seed.Slots)
            PanelSlots.Children.Add(BuildCard(_seed, slot));
    }

    private void RefreshServerChip()
    {
        var host = _seed == null ? null : ApServerHost.For(_seed);
        bool up = host is { IsRunning: true };
        DotServer.Fill = new SolidColorBrush(up
            ? Color.FromRgb(0x4F, 0xA9, 0x7B) : Color.FromRgb(0x4A, 0x51, 0x70));
        TxtServer.Text = up
            ? $"Hosting on port {host!.Port} — friends on your network join "
              + $"{Environment.MachineName}:{host.Port}"
            : _seed != null && ApServerHost.CanResume(_seed)
                ? "Server stopped — Play resumes the session"
                : "Server starts when you press Play";
        BtnStopServer.Visibility = up ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement BuildCard(SeedInfo seed, SeedSlot slot)
    {
        var plugin = GameRegistry.All.FirstOrDefault(p =>
            string.Equals(p.ApWorldName, slot.Game, StringComparison.OrdinalIgnoreCase));
        var session = plugin == null ? null : ApJoinSession.For(plugin);
        bool playing = session is { Current: ApJoinSession.Stage.Playing or ApJoinSession.Stage.Connecting };

        var card = new Border
        {
            Width = 330,
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x30)),
            BorderBrush = new SolidColorBrush(playing
                ? Color.FromRgb(0x4F, 0xA9, 0x7B) : Color.FromRgb(0x26, 0x2C, 0x3E)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(15, 12, 15, 13),
            Margin = new Thickness(0, 0, 12, 12),
        };
        var stack = new StackPanel();

        // P2 · Zelda — the slot identity players log in with.
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x2C, 0x3E)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = $"P{slot.Player} · {slot.Name}",
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushMuted"),
            },
        });
        stack.Children.Add(head);

        stack.Children.Add(new TextBlock
        {
            Text = slot.Game,
            FontSize = 14.5,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 2),
            Foreground = (Brush)FindResource("BrushText"),
        });

        var status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 9),
            Foreground = (Brush)FindResource("BrushMuted"),
        };
        stack.Children.Add(status);

        Button Btn(string text, bool primary)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(0, 8, 0, 8),
                Style = (Style)FindResource(primary ? "BtnPlayStyle" : "BtnSecondaryStyle"),
            };
            stack.Children.Add(b);
            return b;
        }

        if (plugin == null)
        {
            // Not installed. The catalogue decides which of the two honest
            // answers this card gives.
            if (_catalogue == null)
                status.Text = "Checking whether London has a plugin for this…";
            else if (_catalogue.TryGetValue(slot.Game, out var entry))
            {
                status.Text = "London has a plugin for this game — install it here "
                            + "and the slot becomes playable.";
                var b = Btn("Install plugin", primary: true);
                b.Click += async (_, _) => await InstallPluginAsync(entry, b, status);
                stack.Children.Add(new TextBlock
                {
                    Text = "Shows the same consent screen as the Plugin Library: "
                         + "who made it and what it does, before anything is added.",
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 5, 0, 0),
                    Foreground = (Brush)FindResource("BrushMuted"),
                });
            }
            else
                status.Text = "No London plugin covers this game yet. The slot still "
                            + "works from the game's own Archipelago client.";
        }
        else if (playing)
        {
            status.Text = session!.StatusText;
            stack.Children.Add(new TextBlock
            {
                Text = $"{session.ChecksSent} checks sent   ·   {session.ItemsReceived} items received",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 9),
                Foreground = (Brush)FindResource("BrushSuccess"),
            });
            var b = Btn("Stop", primary: false);
            b.Click += async (_, _) => { await session.StopAsync(); RefreshCards(); };
        }
        else
        {
            bool ready = plugin.IsInstalled;
            status.Text = !ready
                ? "The plugin is installed but the game is not — open it in the "
                  + "library and install it first."
                : slot.PatchFile != null
                    ? "Ready — this slot's patch is in the seed and will be applied."
                    : "Ready.";

            var b = Btn("▶  Play this slot", primary: true);
            b.IsEnabled = ready && _engine is { Usable: true };
            if (_engine is not { Usable: true })
                status.Text = "No usable Archipelago engine — set one up under Multiworld.";
            b.Click += async (_, _) => await PlayAsync(seed, slot, plugin, b, status);
        }

        card.Child = stack;
        return card;
    }

    private async Task PlayAsync(SeedInfo seed, SeedSlot slot, IGamePlugin plugin,
                                 Button button, TextBlock status)
    {
        button.IsEnabled = false;
        button.Content = "Starting…";
        status.Foreground = (Brush)FindResource("BrushAccent");
        status.Text = ApServerHost.For(seed) is { IsRunning: true }
            ? "Connecting and starting the game…"
            : "Starting the Archipelago server — a few seconds while it loads "
            + "every installed world. Then the patch, the connection, the game.";

        var (session, message) = await ApJoinSession.StartAsync(_engine!, seed, slot, plugin);

        if (session == null)
        {
            status.Foreground = (Brush)FindResource("BrushError");
            status.Text = message;
            button.Content = "▶  Play this slot";
            button.IsEnabled = true;
            RefreshServerChip();
            return;
        }
        RefreshCards();
    }

    private void BtnStopServer_Click(object sender, RoutedEventArgs e)
    {
        _ = StopServerAsync();
    }

    private async Task StopServerAsync()
    {
        if (_seed == null) return;
        // Sessions first: a server yanked out from under live games is the
        // disconnect story, and this button promised a stop, not an accident.
        foreach (var s in ApJoinSession.All.Where(s => s.SeedId == _seed.Id))
            await s.StopAsync();
        if (ApServerHost.For(_seed) is { } host) await host.StopAsync();
        RefreshCards();
    }

    /// Downloads the plugin and hands it to the SAME consent flow the Plugin
    /// Library and a hand-picked file both use. The seed surface is a shortcut
    /// to the shop, never a way around its questions.
    private async Task InstallPluginAsync(StoreGame game, Button button, TextBlock status)
    {
        button.IsEnabled = false;
        button.Content = "Downloading…";
        status.Foreground = (Brush)FindResource("BrushAccent");
        status.Text = $"Fetching {game.Name} from {game.PluginBy}'s release…";

        var (path, message) = await StoreCatalog.DownloadPluginAsync(game);
        if (path == null)
        {
            status.Foreground = (Brush)FindResource("BrushError");
            status.Text = message;
            button.Content = "Install plugin";
            button.IsEnabled = true;
            return;
        }

        var result = PluginInstallFlow.AddFromFile(Window.GetWindow(this), path);
        if (result.Message != null)
            MessageBox.Show(Window.GetWindow(this), result.Message,
                result.Added ? "Plugin added" : "Plugin not added",
                MessageBoxButton.OK,
                result.Added ? MessageBoxImage.Information : MessageBoxImage.Warning);

        try { File.Delete(path); } catch { }

        button.Content = "Install plugin";
        button.IsEnabled = true;
        if (result.Added)
        {
            PluginInstalled?.Invoke();
            RefreshCards();
        }
    }

    /// The library's game list needs rebuilding after an install here too.
    public event Action? PluginInstalled;

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }
}
