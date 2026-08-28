using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;
using LauncherV2.Core.Archipelago;

namespace LauncherV2.UI.Controls;

// MultiworldPanel — the surface where seeds are made, kept and hosted.
//
// It lives above the game library rather than inside a game's page, because a
// seed spans games: three slots can be three different titles, the apworlds
// are shared, and so is the seed library. A "Generate" tab on one game's page
// could never hold any of that.
//
// The forms are not hand-written. Every option control is built from
// Archipelago's own option template for that game, so a game nobody has
// thought about gets the same form as the ones we test with -- 105 games and
// roughly 4,800 options, none of them typed out here.
public partial class MultiworldPanel : System.Windows.Controls.UserControl
{
    /// One row in the slot list. Either London's own (template + edited
    /// values) or an imported yaml, which is used exactly as it is -- the
    /// player wrote it, and rewriting it into something else would make the
    /// seed disagree with their file.
    private sealed class Slot
    {
        public string Name = "Player";
        public string Game = "";
        public ApTemplate? Template;
        public string? ImportPath;
        public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);

        public bool Imported => ImportPath != null;
        public override string ToString()
            => Imported ? $"{Name}  ·  {Game}  (imported)" : $"{Name}  ·  {Game}";
    }

    private readonly List<Slot> _slots = new();
    private readonly List<string> _genLog = new();
    private ApEngine.Report? _engine;
    private Slot? _current;
    private bool _loadingForm;
    private CancellationTokenSource? _running;

    /// London's own working folders. Never inside the engine's install: that
    /// folder belongs to Archipelago and is shared with whatever else the
    /// player runs.
    private static string WorkRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiworldLauncher", "Multiworld");

    private static string PlayersDir => Path.Combine(WorkRoot, "players");
    private static string OutputDir  => Path.Combine(WorkRoot, "gen");

    public MultiworldPanel()
    {
        InitializeComponent();

        // 0 None · 1 Options · 2 + playthrough · 3 everything. Index = the
        // engine's own --spoiler value, so no mapping table can drift.
        CmbSpoiler.Items.Add("None — nothing is revealed");
        CmbSpoiler.Items.Add("Options only");
        CmbSpoiler.Items.Add("Options + playthrough");
        CmbSpoiler.Items.Add("Everything (default)");
        CmbSpoiler.SelectedIndex = 3;

        Loaded += (_, _) => Refresh();
    }

    // ---------------------------------------------------------------- engine

    /// Re-reads the engine and everything that depends on it. Safe to call at
    /// any time; nothing here runs the engine's code.
    public void Refresh()
    {
        var settings = SettingsStore.Load();
        _engine = ApEngine.Discover(string.IsNullOrWhiteSpace(settings.ApEnginePath)
                                    ? null : settings.ApEnginePath);

        bool usable = _engine is { Usable: true };
        EngineBanner.Visibility = usable ? Visibility.Collapsed : Visibility.Visible;

        if (!usable)
        {
            TxtEngineHeadline.Text = _engine is { Exists: true }
                ? "The Archipelago engine on this machine cannot be used"
                : "No Archipelago engine found";
            TxtEngineDetail.Text = _engine is { Exists: true }
                ? string.Join("  ·  ", _engine.Problems)
                : "London generates seeds with Archipelago's own engine (MIT). "
                + "Point at an installation you already have, or get it from the project.";
        }

        LoadGameList();
        RefreshApworlds();
        RefreshSlotList();
        RefreshSeeds();
        RefreshReadiness();
        if (_current == null) ShowSlot(null);   // paints the how-to
    }

    private void LoadGameList()
    {
        CmbAddGame.Items.Clear();
        if (_engine == null || !Directory.Exists(_engine.TemplatesDir)) return;

        foreach (string f in Directory.GetFiles(_engine.TemplatesDir, "*.yaml")
                                      .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            CmbAddGame.Items.Add(Path.GetFileNameWithoutExtension(f));

        if (CmbAddGame.Items.Count > 0) CmbAddGame.SelectedIndex = 0;
    }

    private void RefreshApworlds()
    {
        PanelApworldList.Children.Clear();
        _apworldRows.Clear();
        if (_engine == null) { TxtApworldSummary.Text = "No engine"; return; }

        int broken = _engine.BrokenWorldCount;
        TxtApworldSummary.Text = $"{_engine.CustomWorlds.Count} extra worlds installed"
                               + (broken > 0 ? $" — {broken} with a broken manifest" : "");

        foreach (var w in _engine.CustomWorlds.OrderBy(w => w.Game ?? w.File))
        {
            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x30)),
                BorderBrush = new SolidColorBrush(w.ManifestOk
                    ? Color.FromRgb(0x26, 0x2C, 0x3E) : Color.FromRgb(0x8A, 0x65, 0x20)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = w.Game ?? w.File,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushText"),
            });
            stack.Children.Add(new TextBlock
            {
                Text = w.ManifestOk
                    ? $"{w.File}   version {w.WorldVersion ?? "—"}"
                    : $"{w.File}   —   its manifest is missing or invalid, so it will "
                    + "stop working with Archipelago 0.7.0",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = (Brush)FindResource(w.ManifestOk ? "BrushMuted" : "BrushAccent"),
            });
            row.Child = stack;
            PanelApworldList.Children.Add(row);
            _apworldRows[w.File] = stack;
        }

        // The catalogue arrives over the network. The list above is already on
        // screen; this walks back over it and adds a button to the worlds that
        // are behind.
        //
        // ⚠ It ADDS to the rows it just built — it does not redraw. A refresh
        // that starts something which redraws it is how the Join panel ran out
        // of stack; see lint_no_redraw_cycle.py.
        _ = DecorateApworldRowsAsync();
    }

    /// file name -> the row's text stack, so the update button can be added
    /// after the catalogue answers.
    private readonly Dictionary<string, StackPanel> _apworldRows =
        new(StringComparer.OrdinalIgnoreCase);

    private async Task DecorateApworldRowsAsync()
    {
        ApworldIndex? index;
        try { index = await ApworldCatalog.FetchAsync(); }
        catch (Exception) { return; }
        if (index?.Games == null) return;

        int behind = 0;
        foreach (var pair in index.Games)
        {
            if (!_apworldRows.TryGetValue(pair.Value.Asset, out var stack)) continue;

            ApworldStatus st;
            try { st = await ApworldUpdater.CheckAsync(pair.Key); }
            catch (Exception) { continue; }
            if (!st.Actionable) continue;
            behind++;

            string gameId = pair.Key;
            var btn = new Button
            {
                Content = "↑  Update this world",
                Style = (Style)FindResource("BtnPlayStyle"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = $"{st.Detail} — from {pair.Value.Source}",
            };
            btn.Click += async (_, _) =>
            {
                btn.IsEnabled = false;
                object? label = btn.Content;
                string? err = await ApworldUpdater.UpdateAsync(gameId,
                    new Progress<string>(m => btn.Content = m));
                btn.Content = err ?? "Updated";
                btn.IsEnabled = err != null;
                if (err != null) btn.Content = label;
            };
            stack.Children.Add(btn);
        }

        BtnUpdateApworlds.Visibility = behind > 0
            ? Visibility.Visible : Visibility.Collapsed;
        BtnUpdateApworlds.Content = behind == 1
            ? "↑  Update 1 world"
            : $"↑  Update {behind} worlds";
    }

    /// Every world in the engine that is behind, in one press.
    private async void BtnUpdateApworlds_Click(object sender, RoutedEventArgs e)
    {
        BtnUpdateApworlds.IsEnabled = false;
        object? was = BtnUpdateApworlds.Content;
        try
        {
            var ids = await ApworldUpdater.CandidatesAsync(Array.Empty<string>());
            var lines = await ApworldUpdater.UpdateAllAsync(ids,
                new Progress<string>(m => BtnUpdateApworlds.Content = m));
            ToastService.Show(
                lines.Count == 0 ? "Nothing to update" : $"{lines.Count} world(s) updated",
                lines.Count == 0 ? "Every world London recognises is current."
                                 : string.Join(", ", lines.Take(3)),
                ToastKind.Success);
        }
        finally
        {
            BtnUpdateApworlds.IsEnabled = true;
            BtnUpdateApworlds.Content = was;
            RefreshApworlds();
        }
    }

    // ----------------------------------------------------------------- seeds

    /// The library, drawn as cards. Called after every generation and every
    /// host/stop, so the surface never claims something the disk disagrees
    /// with.
    private void RefreshSeeds()
    {
        PanelSeedList.Children.Clear();
        var seeds = ApSeedLibrary.List();
        PanelSeedsEmpty.Visibility = seeds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtSeedsSummary.Text = seeds.Count switch
        {
            0 => "Seeds",
            1 => "Seeds — 1 in the library",
            var n => $"Seeds — {n} in the library",
        };

        foreach (var seed in seeds)
            PanelSeedList.Children.Add(BuildSeedCard(seed));
    }

    private UIElement BuildSeedCard(SeedInfo seed)
    {
        var host = ApServerHost.For(seed);
        bool hosting = host is { IsRunning: true };

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x30)),
            BorderBrush = new SolidColorBrush(hosting
                ? Color.FromRgb(0x4F, 0xA9, 0x7B) : Color.FromRgb(0x26, 0x2C, 0x3E)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = seed.Id + "   ·   " + seed.Created.ToString("d MMM HH:mm"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("BrushText"),
        });

        stack.Children.Add(new TextBlock
        {
            Text = string.Join("   ·   ", seed.Slots.Select(s => $"{s.Name} ({s.Game})")),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (Brush)FindResource("BrushMuted"),
        });

        var status = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 8),
            Foreground = (Brush)FindResource(hosting ? "BrushSuccess" : "BrushMuted"),
            Text = hosting
                ? $"Hosting on port {host!.Port} — players connect to this machine's address, port {host.Port}"
                : _lastHostError.TryGetValue(seed.Id, out var why)
                    ? "Did not start: " + why
                    : ApServerHost.CanResume(seed)
                        ? "Stopped — the session can be resumed where it left off"
                        : "Not hosted",
        };
        if (!hosting && _lastHostError.ContainsKey(seed.Id))
            status.Foreground = (Brush)FindResource("BrushError");
        stack.Children.Add(status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Button Make(string text, RoutedEventHandler? click, bool primary = false)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindResource(primary ? "BtnPlayStyle" : "BtnSecondaryStyle"),
            };
            if (click != null) b.Click += click;
            buttons.Children.Add(b);
            return b;
        }

        if (!hosting)
        {
            var hostBtn = Make(ApServerHost.CanResume(seed) ? "Resume hosting" : "Host",
                               null, primary: true);
            hostBtn.Click += async (_, _) =>
            {
                hostBtn.IsEnabled = false;
                hostBtn.Content = "Starting…";
                status.Foreground = (Brush)FindResource("BrushAccent");
                status.Text = "Starting the Archipelago server — this takes a few "
                            + "seconds while it loads every installed world.";
                await HostSeedAsync(seed);
            };
        }
        else
            Make("Stop", async (_, _) =>
            {
                if (host != null) await host.StopAsync();
                RefreshSeeds();
            });

        if (seed.SpoilerPath != null && File.Exists(seed.SpoilerPath))
            Make("Spoiler", (_, _) => OpenPath(seed.SpoilerPath!));

        Make("Folder", (_, _) => OpenPath(seed.Folder));

        Make("Delete", async (_, _) =>
        {
            var ask = MessageBox.Show(Window.GetWindow(this),
                $"Delete {seed.Id} and its save? Players cannot resume it afterwards.",
                "Delete seed", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return;
            if (ApServerHost.For(seed) is { } h) await h.StopAsync();
            ApSeedLibrary.Delete(seed);
            RefreshSeeds();
        });

        stack.Children.Add(buttons);
        card.Child = stack;
        return card;
    }

    private async Task HostSeedAsync(SeedInfo seed)
    {
        if (_engine == null) return;
        var result = await ApServerHost.StartAsync(_engine, seed);
        if (result.Host == null)
        {
            // Said twice on purpose: the dialog is the interruption, the card
            // is what is still there afterwards.
            _lastHostError[seed.Id] = result.Message;
            MessageBox.Show(Window.GetWindow(this), result.Message,
                "The server did not start", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else _lastHostError.Remove(seed.Id);
        RefreshSeeds();
    }

    /// Why the last Host attempt failed, per seed, so the card can keep
    /// saying it after the dialog is gone.
    private readonly Dictionary<string, string> _lastHostError = new();

    private static void OpenPath(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { /* a viewer that will not open is not worth a crash */ }
    }

    // ----------------------------------------------------------------- slots

    private void RefreshSlotList()
    {
        int keep = ListSlots.SelectedIndex;
        ListSlots.Items.Clear();
        foreach (var s in _slots) ListSlots.Items.Add(s.ToString());
        TxtSlotCount.Text = _slots.Count == 0 ? "SLOTS" : $"SLOTS — {_slots.Count}";
        if (keep >= 0 && keep < ListSlots.Items.Count) ListSlots.SelectedIndex = keep;
    }

    private void BtnAddSlot_Click(object sender, RoutedEventArgs e)
    {
        if (_engine == null) return;
        string game = CmbAddGame.Text?.Trim() ?? "";
        if (game.Length == 0) return;

        string tpl = Path.Combine(_engine.TemplatesDir, game + ".yaml");
        if (!File.Exists(tpl))
        {
            Log($"No option template for \"{game}\". Install its apworld first, "
              + "then refresh the templates.");
            return;
        }

        var parsed = ApOptionTemplate.ParseFile(tpl);
        if (parsed == null) { Log($"The option template for \"{game}\" could not be read."); return; }

        var slot = new Slot
        {
            Name = UniqueName(game),
            Game = parsed.Game,
            Template = parsed,
        };
        foreach (var o in parsed.Options)
            if (o.Default != null) slot.Values[o.Key] = o.Default;

        _slots.Add(slot);
        RefreshSlotList();
        ListSlots.SelectedIndex = _slots.Count - 1;
        RefreshReadiness();
    }

    /// Player files made elsewhere come in as they are. What London refuses is
    /// the two file names the generator silently obeys: a stray meta.yaml
    /// rewrites every slot's options without a word.
    private void BtnImportYaml_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import player yaml files",
            Filter = "Player files (*.yaml)|*.yaml",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;

        foreach (string file in dlg.FileNames)
        {
            if (ApPlayerYaml.IsHijackFile(file))
            {
                Log($"{Path.GetFileName(file)} was refused: a file with that name "
                  + "silently rewrites every player's options.");
                continue;
            }

            _slots.Add(new Slot
            {
                Name = ReadYamlField(file, "name") ?? Path.GetFileNameWithoutExtension(file),
                Game = ReadYamlField(file, "game") ?? "unknown game",
                ImportPath = file,
            });
        }
        RefreshSlotList();
        ListSlots.SelectedIndex = _slots.Count - 1;
        RefreshReadiness();
    }

    /// The first top-level "key: value" line. Enough for a label; the
    /// generator does the real parsing.
    private static string? ReadYamlField(string file, string key)
    {
        try
        {
            foreach (string line in File.ReadLines(file).Take(80))
                if (line.StartsWith(key + ":", StringComparison.Ordinal))
                    return line[(key.Length + 1)..].Trim().Trim('\'', '"');
        }
        catch { }
        return null;
    }

    /// Slot names must be unique and short; Archipelago truncates past 16.
    private string UniqueName(string game)
    {
        string basis = new string(game.Where(char.IsLetterOrDigit).ToArray());
        if (basis.Length == 0) basis = "Player";
        if (basis.Length > 12) basis = basis[..12];

        string name = basis;
        int n = 2;
        while (_slots.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = basis + n++;
        return name.Length > ApSlot.MaxNameLength ? name[..ApSlot.MaxNameLength] : name;
    }

    private void BtnRemoveSlot_Click(object sender, RoutedEventArgs e)
    {
        int i = ListSlots.SelectedIndex;
        if (i < 0 || i >= _slots.Count) return;
        _slots.RemoveAt(i);
        _current = null;
        RefreshSlotList();
        ShowSlot(_slots.Count > 0 ? _slots[Math.Min(i, _slots.Count - 1)] : null);
        RefreshReadiness();
    }

    private void ListSlots_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = ListSlots.SelectedIndex;
        ShowSlot(i >= 0 && i < _slots.Count ? _slots[i] : null);
    }

    private void TxtSlotName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingForm || _current == null) return;
        _current.Name = TxtSlotName.Text;

        var probe = new ApSlot(_current.Name, _current.Game, _current.Values);
        TxtNameWarn.Text = probe.IsNameValid
            ? ""
            : $"1–{ApSlot.MaxNameLength} characters, no padding";

        RefreshSlotList();
        RefreshReadiness();
    }

    // ------------------------------------------------------------ the form

    private void ShowSlot(Slot? slot)
    {
        _current = slot;
        PanelOptions.Children.Clear();

        if (slot == null)
        {
            TxtSlotHeader.Text = "Build a seed";
            _loadingForm = true;
            TxtSlotName.Text = "";
            _loadingForm = false;
            TxtSlotName.IsEnabled = false;
            BuildHowTo();
            return;
        }

        if (slot.Imported)
        {
            TxtSlotHeader.Text = slot.Game + "  (imported)";
            _loadingForm = true;
            TxtSlotName.Text = slot.Name;
            _loadingForm = false;
            TxtSlotName.IsEnabled = false;

            PanelOptions.Children.Add(Note(
                "This file was made outside London and is used exactly as it is — "
              + "nothing in it is changed. If it holds weighted options, the "
              + "generator rolls them, so what the player gets can differ from "
              + "run to run.\n\nFile: " + slot.ImportPath));
            return;
        }

        _loadingForm = true;
        TxtSlotHeader.Text = slot.Game;
        TxtSlotName.IsEnabled = true;
        TxtSlotName.Text = slot.Name;
        _loadingForm = false;

        foreach (var group in slot.Template!.Options.GroupBy(o => o.Group))
        {
            PanelOptions.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushMuted"),
                Margin = new Thickness(0, 18, 0, 8),
            });

            foreach (var opt in group)
                PanelOptions.Children.Add(BuildRow(slot, opt));
        }
    }

    /// What the middle of the screen says before there is anything to edit.
    /// The player asked, reasonably, how one is supposed to know what to do.
    private void BuildHowTo()
    {
        var steps = new (string Title, string Body)[]
        {
            ("1 · Pick a game",
             "The list at the bottom left holds every game this machine can "
           + "generate — one entry per option template. Type to jump."),
            ("2 · Add a slot",
             "A slot is one player in one game. Add as many as you like, "
           + "mixing games freely — that is the multiworld. Each slot starts "
           + "with the game's own default options; select it to change them."),
            ("3 · Generate",
             "London first checks that the seed is possible, then builds it. "
           + "The finished seed lands under Seeds, where one button hosts it."),
        };

        foreach (var (title, body) in steps)
        {
            PanelOptions.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, 16, 0, 3),
                Foreground = (Brush)FindResource("BrushText"),
            });
            PanelOptions.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560,
                HorizontalAlignment = HorizontalAlignment.Left,
                Foreground = (Brush)FindResource("BrushMuted"),
            });
        }

        PanelOptions.Children.Add(Note(
            "Playing alone? One slot is a complete seed — generate and host it, "
          + "and you have a randomizer for that game."));
    }

    private UIElement Note(string text) => new Border
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x30)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x2C, 0x3E)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(12, 9, 12, 9),
        Margin = new Thickness(0, 18, 0, 0),
        MaxWidth = 560,
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("BrushMuted"),
        },
    };

    /// One option, as a labelled control. The description is the engine's own
    /// wording -- London does not paraphrase somebody else's game.
    private UIElement BuildRow(Slot slot, ApOption opt)
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        box.Children.Add(new TextBlock
        {
            Text = Pretty(opt.Key),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("BrushText"),
        });

        if (opt.Description.Length > 0)
            box.Children.Add(new TextBlock
            {
                Text = opt.Description,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 620,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 3, 0, 6),
                Foreground = (Brush)FindResource("BrushMuted"),
            });

        string current = slot.Values.TryGetValue(opt.Key, out var v) ? v : opt.Default ?? "";

        switch (opt.Kind)
        {
            case ApOptionKind.Choice:
            case ApOptionKind.Toggle:
            {
                var combo = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
                foreach (var c in opt.Choices)
                    combo.Items.Add(Label(c.Value));
                combo.SelectedItem = Label(current);
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedIndex >= 0 && combo.SelectedIndex < opt.Choices.Count)
                        slot.Values[opt.Key] = opt.Choices[combo.SelectedIndex].Value;
                };
                box.Children.Add(combo);
                break;
            }

            case ApOptionKind.Range:
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var text = new TextBox { Width = 120, Padding = new Thickness(6, 4, 6, 4), Text = current };
                text.TextChanged += (_, _) => slot.Values[opt.Key] = text.Text.Trim();
                row.Children.Add(text);
                row.Children.Add(new TextBlock
                {
                    Text = $"   {opt.Min} – {opt.Max}",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    Foreground = (Brush)FindResource("BrushMuted"),
                });

                // The named landmarks are the fastest way to a sensible value.
                var named = opt.Choices.Where(c => c.Equivalent != null).ToList();
                if (named.Count > 0)
                {
                    var pick = new ComboBox { Width = 150, Margin = new Thickness(10, 0, 0, 0) };
                    foreach (var c in named) pick.Items.Add(c.Value);
                    pick.SelectedItem = named.FirstOrDefault(c => c.Value == current)?.Value;
                    pick.SelectionChanged += (_, _) =>
                    {
                        if (pick.SelectedItem is string s) text.Text = s;
                    };
                    row.Children.Add(pick);
                }
                box.Children.Add(row);
                break;
            }

            case ApOptionKind.FreeText:
            {
                var text = new TextBox
                {
                    Width = 260,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(6, 4, 6, 4),
                    Text = current,
                };
                text.TextChanged += (_, _) => slot.Values[opt.Key] = text.Text;
                box.Children.Add(text);
                break;
            }

            default:
            {
                // Lists and mappings need an editor of their own. Until they
                // have one, the template's value is shown as it is rather than
                // offering a control that cannot edit it.
                box.Children.Add(new TextBlock
                {
                    Text = current.Length == 0 ? "(empty)" : current,
                    FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 620,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Foreground = (Brush)FindResource("BrushMuted"),
                });
                break;
            }
        }

        return box;
    }

    /// "random" and its siblings are legal values, but they are instructions,
    /// not settings -- shown in words that say what they do.
    private static string Label(string value) => value switch
    {
        "" => "(blank)",
        "random" => "random — roll it for me",
        "random-low" => "random — leaning low",
        "random-high" => "random — leaning high",
        _ when value.StartsWith("random-range-", StringComparison.Ordinal)
            => "random — " + value["random-range-".Length..],
        _ => value,
    };

    private static string Pretty(string key)
        => string.Join(' ', key.Split('_')
                               .Where(p => p.Length > 0)
                               .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    // ------------------------------------------------------------ readiness

    /// Everything that has to be true before the button does anything, checked
    /// while there is still something to click. The alternative is a six-second
    /// wait ending in a Python traceback.
    private void RefreshReadiness()
    {
        PanelReadiness.Children.Clear();
        bool ok = true;

        void Line(bool good, string text)
        {
            if (!good) ok = false;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 7) };
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Fill = (Brush)FindResource(good ? "BrushSuccess" : "BrushError"),
            });
            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 220,
                Foreground = (Brush)FindResource("BrushMuted"),
            });
            PanelReadiness.Children.Add(row);
        }

        Line(_engine is { Usable: true },
             _engine is { Usable: true }
                 ? $"Engine {_engine.Version} found"
                 : "No usable engine");

        Line(_slots.Count > 0,
             _slots.Count switch { 0 => "No slots yet", 1 => "1 slot", var n => $"{n} slots" });

        // Imported files carry their own names; the generator is their judge.
        var own = _slots.Where(s => !s.Imported).ToList();
        var badNames = own.Where(s => !new ApSlot(s.Name, s.Game, s.Values).IsNameValid).ToList();
        Line(badNames.Count == 0,
             badNames.Count == 0 ? "Every slot name fits" : $"{badNames.Count} name(s) will not fit");

        var dupes = _slots.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                          .Where(g => g.Count() > 1).ToList();
        Line(dupes.Count == 0,
             dupes.Count == 0 ? "No repeated names" : "Two slots share a name");

        BtnGenerate.IsEnabled = ok && _running == null;
        BtnValidate.IsEnabled = ok && _running == null;
        // Exactly one of "start it" and "stop it" is ever available, so the
        // panel is never a dead end.
        BtnStopGenerate.Visibility = _running == null
            ? Visibility.Collapsed : Visibility.Visible;
    }

    /// Give the buttons back.
    ///
    /// ⚠ The point is not tidiness. A run holds BtnGenerate and BtnValidate
    /// disabled for as long as it lasts, and a generation can legitimately
    /// take many minutes — so without a way to end one, "it froze" and "it is
    /// still working" look identical and neither can be acted on. Cancelling
    /// unwinds through RunAsync's finally, which re-enables everything.
    private void BtnStopGenerate_Click(object sender, RoutedEventArgs e)
    {
        if (_running == null) return;
        Log("Stopping…");
        try { _running.Cancel(); }
        catch (ObjectDisposedException) { /* it finished while we were asked to stop */ }
    }

    // ----------------------------------------------------------- generating

    /// Accumulates instead of replacing: the log is the only account of what
    /// the generator did, and a label that flashes one line at a time reads as
    /// nothing happening at all.
    private void Log(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (string part in line.Split('\n'))
            {
                _genLog.Add(part.TrimEnd());
                if (_genLog.Count > 200) _genLog.RemoveAt(0);
            }
            TxtGenLog.Text = string.Join("\n", _genLog);
            ScrollGenLog.ScrollToEnd();
        });
    }

    private void TabSeeds_Click(object sender, RoutedEventArgs e)    { RefreshSeeds(); Show(PanelSeeds); }
    private void TabNewSeed_Click(object sender, RoutedEventArgs e)  => Show(PanelNewSeed);
    private void TabApworlds_Click(object sender, RoutedEventArgs e) => Show(PanelApworlds);
    private void TabGuide_Click(object sender, RoutedEventArgs e)    => Show(PanelGuide);

    private void Show(UIElement which)
    {
        PanelSeeds.Visibility    = ReferenceEquals(which, PanelSeeds)    ? Visibility.Visible : Visibility.Collapsed;
        PanelNewSeed.Visibility  = ReferenceEquals(which, PanelNewSeed)  ? Visibility.Visible : Visibility.Collapsed;
        PanelApworlds.Visibility = ReferenceEquals(which, PanelApworlds) ? Visibility.Visible : Visibility.Collapsed;
        PanelGuide.Visibility    = ReferenceEquals(which, PanelGuide)    ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnValidate_Click(object sender, RoutedEventArgs e) => _ = RunAsync(validateOnly: true);
    private void BtnGenerate_Click(object sender, RoutedEventArgs e) => _ = RunAsync(validateOnly: false);

    private async Task RunAsync(bool validateOnly)
    {
        if (_engine is not { Usable: true } || _running != null) return;

        // The seed number is the player's, so a typo is refused here rather
        // than silently becoming "random".
        long? seed = null;
        string seedText = TxtSeedNumber.Text.Trim();
        if (seedText.Length > 0)
        {
            if (!long.TryParse(seedText, out long parsed))
            {
                Log($"\"{seedText}\" is not a number. Leave the seed field empty for a random one.");
                return;
            }
            seed = parsed;
        }

        _running = new CancellationTokenSource();
        // Through RefreshReadiness rather than by hand: _running is set, so it
        // disables both start buttons AND reveals Stop. Setting the two flags
        // here directly is how the Stop button stayed hidden for the whole run
        // it exists for.
        RefreshReadiness();

        try
        {
            // The players folder is rewritten from what is on screen, so what
            // generates is what was shown -- never a leftover from last time.
            if (Directory.Exists(PlayersDir)) Directory.Delete(PlayersDir, true);
            Directory.CreateDirectory(PlayersDir);

            var slotRecords = new List<ApSlot>();
            foreach (var s in _slots)
            {
                if (s.Imported)
                {
                    File.Copy(s.ImportPath!, Path.Combine(PlayersDir, Path.GetFileName(s.ImportPath!)), true);
                    slotRecords.Add(new ApSlot(s.Name, s.Game, new Dictionary<string, string>()));
                }
                else
                {
                    ApPlayerYaml.Write(new ApSlot(s.Name, s.Game, s.Values),
                                       PlayersDir, _engine.Version?.ToString());
                    slotRecords.Add(new ApSlot(s.Name, s.Game, s.Values));
                }
            }

            var progress = new Progress<ApGenerator.Progress>(p =>
                Log(p.Detail == null ? p.Stage : $"{p.Stage} — {p.Detail}"));

            int spoiler = CmbSpoiler.SelectedIndex < 0 ? 3 : CmbSpoiler.SelectedIndex;
            bool race = ChkRace.IsChecked == true;

            var result = validateOnly
                ? await ApGenerator.ValidateAsync(_engine, PlayersDir, progress, _running.Token)
                : await ApGenerator.GenerateAsync(_engine, PlayersDir, OutputDir,
                                                  seed, spoiler, race, progress, _running.Token);

            if (result.IsSlotProblem)
            {
                Log(result.Message);
                foreach (string err in result.SlotErrors) Log("  " + err);
            }
            else if (!result.Ok)
            {
                Log(result.Message);
            }
            else if (validateOnly)
            {
                Log(result.Message);
            }
            else
            {
                // Into the library, onto the Seeds surface. A zip in a temp
                // folder is not a finished seed; a card with a Host button is.
                var info = ApSeedLibrary.Ingest(result.SeedZip!, _engine, slotRecords);
                if (info != null)
                {
                    Log($"Seed {info.Id} is in the library — opening Seeds.");
                    RefreshSeeds();
                    Show(PanelSeeds);
                }
                else
                {
                    Log("The seed was generated but could not be filed. It is still at: "
                        + result.SeedZip);
                }
            }
        }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception ex) { Log(ex.Message); }
        finally
        {
            _running?.Dispose();
            _running = null;
            RefreshReadiness();
        }
    }

    // --------------------------------------------------------------- engine

    private void BtnEngineLocate_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Find ArchipelagoGenerate.exe in your Archipelago folder",
            Filter = "ArchipelagoGenerate.exe|ArchipelagoGenerate.exe|Programs (*.exe)|*.exe",
        };
        if (dlg.ShowDialog() != true) return;

        string root = Path.GetDirectoryName(dlg.FileName) ?? "";
        var probe = ApEngine.Inspect(root);
        if (!probe.Usable)
        {
            MessageBox.Show(Window.GetWindow(this),
                probe.Summary(), "That folder cannot be used",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = SettingsStore.Load();
        settings.ApEnginePath = root;
        SettingsStore.Save(settings);
        Refresh();
    }

    private async void BtnEngineGet_Click(object sender, RoutedEventArgs e)
    {
        BtnEngineGet.IsEnabled = false;
        try
        {
            var offer = await ApEngineSource.LatestAsync();
            if (offer == null)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "The release could not be read. Install by hand from "
                  + ApEngineSource.ProjectPage, "Archipelago engine",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var ask = MessageBox.Show(Window.GetWindow(this),
                string.Join("\n\n", ApEngineSource.ConsentLines(offer))
                + "\n\nDownload it?",
                "Get the Archipelago engine", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return;

            string dir = Path.Combine(WorkRoot, "downloads");
            var got = await ApEngineSource.FetchInstallerAsync(offer, dir);

            MessageBox.Show(Window.GetWindow(this), got.Message,
                "Archipelago engine", MessageBoxButton.OK,
                got.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

            // Show them the file rather than running it: this is somebody
            // else's installer, and where it goes is their decision.
            if (got.Ok && got.InstallerPath != null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    ArgumentList = { "/select,", got.InstallerPath },
                    UseShellExecute = true,
                });
        }
        finally { BtnEngineGet.IsEnabled = true; }
    }
}
