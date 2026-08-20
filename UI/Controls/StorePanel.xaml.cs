using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LauncherV2.Core;
using LauncherV2.Core.Plugins;

namespace LauncherV2.UI.Controls;

// StorePanel — the shop window over the plugin catalogue.
//
// Browsing is free; INSTALLING is exactly as guarded as it always was. The
// Install button downloads the plugin file and hands it to the same
// PluginInstallFlow a hand-picked file goes through — author, declarations,
// the manual route, the consent screen. The store removes the trip to GitHub,
// never the questions.
//
// Covers follow the launcher's one cover-art rule: they are fetched onto this
// machine only after the player has said yes to cover art at all. Before that,
// a tile with the game's initials — a store that quietly downloads fifty
// publishers' box scans the moment it opens would be making the choice for
// them.
public partial class StorePanel : System.Windows.Controls.UserControl
{
    private StoreIndex? _index;
    private readonly List<CheckBox> _platformBoxes = new();
    private readonly List<CheckBox> _genreBoxes = new();
    private bool _coversAllowed;
    private static readonly Dictionary<string, BitmapImage> _coverCache = new();

    private static string CoverCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiworldLauncher", "store_covers");

    public StorePanel()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (_index == null) _ = LoadAsync(); };
    }

    /// Called when the mode opens. Cheap when already loaded.
    public void Refresh()
    {
        _coversAllowed = SettingsStore.Load().GameArtConsent == true;
        if (_index == null) _ = LoadAsync();
        else RenderCards();
    }

    private async Task LoadAsync()
    {
        TxtStoreCount.Text = "Store — loading…";
        _coversAllowed = SettingsStore.Load().GameArtConsent == true;

        _index = await StoreCatalog.FetchAsync();

        if (_index == null)
        {
            TxtStoreCount.Text = "Store";
            TxtStoreEmpty.Text = "The catalogue could not be reached, and there is no "
                               + "saved copy yet. Check the connection and press Refresh.";
            TxtStoreEmpty.Visibility = Visibility.Visible;
            return;
        }

        BuildFilterBoxes();
        RenderCards();
    }

    private void BuildFilterBoxes()
    {
        PanelPlatforms.Children.Clear();
        PanelGenres.Children.Clear();
        _platformBoxes.Clear();
        _genreBoxes.Clear();
        if (_index == null) return;

        CheckBox Make(string label, StackPanel host, List<CheckBox> track)
        {
            var cb = new CheckBox
            {
                Content = label,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 5),
                Foreground = (Brush)FindResource("BrushText"),
            };
            cb.Checked += Filters_Changed;
            cb.Unchecked += Filters_Changed;
            host.Children.Add(cb);
            track.Add(cb);
            return cb;
        }

        foreach (string p in _index.Platforms) Make(p, PanelPlatforms, _platformBoxes);
        foreach (string g in _index.Genres)    Make(g, PanelGenres, _genreBoxes);
    }

    private static List<string> Ticked(IEnumerable<CheckBox> boxes)
        => boxes.Where(b => b.IsChecked == true)
                .Select(b => (string)b.Content)
                .ToList();

    private void Filters_Changed(object sender, RoutedEventArgs e) => RenderCards();
    private void Filters_Changed(object sender, TextChangedEventArgs e) => RenderCards();

    private void BtnClearFilters_Click(object sender, RoutedEventArgs e)
    {
        TxtSearch.Text = "";
        foreach (var b in _platformBoxes.Concat(_genreBoxes)) b.IsChecked = false;
        RenderCards();
    }

    private void BtnStoreRefresh_Click(object sender, RoutedEventArgs e)
    {
        _index = null;
        _ = LoadAsync();
    }

    // ------------------------------------------------------------------ cards

    private void RenderCards()
    {
        PanelGames.Children.Clear();
        if (_index == null) return;

        var shown = StoreCatalog.Filter(_index.Games, TxtSearch.Text,
                                        Ticked(_platformBoxes), Ticked(_genreBoxes));

        TxtStoreCount.Text = shown.Count == _index.Games.Length
            ? $"Store — {_index.Games.Length} games"
            : $"Store — {shown.Count} of {_index.Games.Length} games";
        TxtStoreEmpty.Text = "Nothing matches those filters.";
        TxtStoreEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var game in shown)
            PanelGames.Children.Add(BuildCard(game));
    }

    private UIElement BuildCard(StoreGame game)
    {
        bool installed = GameRegistry.All.Any(p =>
            string.Equals(p.GameId, game.Id, StringComparison.OrdinalIgnoreCase));

        var card = new Border
        {
            Width = 232,
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x2C, 0x3E)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 0, 12, 12),
        };
        var stack = new StackPanel();

        // Cover — or the initials tile that stands in until covers are allowed.
        var coverHost = new Border
        {
            Height = 130,
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x1E)),
        };
        if (_coversAllowed && game.Cover != null)
        {
            var img = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            _ = SetCoverAsync(img, game);
            coverHost.Child = img;
        }
        else
        {
            string initials = new string(game.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                                  .Take(3).Select(w => char.ToUpperInvariant(w[0]))
                                                  .ToArray());
            coverHost.Child = new TextBlock
            {
                Text = initials,
                FontSize = 34,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x3A, 0x50)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        stack.Children.Add(coverHost);

        var body = new StackPanel { Margin = new Thickness(12, 9, 12, 12) };
        body.Children.Add(new TextBlock
        {
            Text = game.Name,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("BrushText"),
        });
        body.Children.Add(new TextBlock
        {
            Text = game.PlatformLabel + "   ·   " + string.Join(", ", game.Genres),
            FontSize = 10.5,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("BrushMuted"),
        });
        body.Children.Add(new TextBlock
        {
            Text = $"World by {game.WorldBy}",
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("BrushMuted"),
        });

        if (installed)
        {
            body.Children.Add(new TextBlock
            {
                Text = "✓ Installed",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushSuccess"),
            });
        }
        else
        {
            var btn = new Button
            {
                Content = "Install…",
                Padding = new Thickness(0, 7, 0, 7),
                Style = (Style)FindResource("BtnPlayStyle"),
                ToolTip = "Downloads the plugin and shows the consent screen — "
                        + "who made it and what it does — before anything is added.",
            };
            btn.Click += async (_, _) => await InstallAsync(game, btn);
            body.Children.Add(btn);
        }

        stack.Children.Add(body);
        card.Child = stack;
        return card;
    }

    private async Task SetCoverAsync(Image img, StoreGame game)
    {
        try
        {
            if (_coverCache.TryGetValue(game.Id, out var hit)) { img.Source = hit; return; }

            // Disk-cached: a store you scroll twice should not fetch the same
            // fifty covers twice.
            Directory.CreateDirectory(CoverCacheDir);
            string local = Path.Combine(CoverCacheDir, game.Id + ".png");
            if (!File.Exists(local))
            {
                using var http = new System.Net.Http.HttpClient
                { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
                byte[] data = await http.GetByteArrayAsync(game.Cover);
                if (data.Length < 2000 || data[0] != 0x89) return;
                await File.WriteAllBytesAsync(local, data);
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(local);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 300;
            bmp.EndInit();
            bmp.Freeze();
            _coverCache[game.Id] = bmp;
            img.Source = bmp;
        }
        catch { /* a missing cover is an initials tile, never an error */ }
    }

    private async Task InstallAsync(StoreGame game, Button btn)
    {
        btn.IsEnabled = false;
        btn.Content = "Downloading…";

        var (path, message) = await StoreCatalog.DownloadPluginAsync(game);
        if (path == null)
        {
            btn.Content = "Install…";
            btn.IsEnabled = true;
            MessageBox.Show(Window.GetWindow(this), message, "Store",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // THE point of the store: from here on it is byte-for-byte the manual
        // flow — the same consent screen, the same declarations, the same
        // right to say no.
        var result = PluginInstallFlow.AddFromFile(Window.GetWindow(this), path);
        if (result.Message != null)
            MessageBox.Show(Window.GetWindow(this), result.Message,
                result.Added ? "Plugin added" : "Plugin not added",
                MessageBoxButton.OK,
                result.Added ? MessageBoxImage.Information : MessageBoxImage.Warning);

        try { File.Delete(path); } catch { }

        btn.Content = "Install…";
        btn.IsEnabled = true;
        if (result.Added)
        {
            RenderCards();
            InstalledSomething?.Invoke();
        }
    }

    /// The library's game list needs a rebuild after an install; the window
    /// owns that list, so the window listens.
    public event Action? InstalledSomething;
}
