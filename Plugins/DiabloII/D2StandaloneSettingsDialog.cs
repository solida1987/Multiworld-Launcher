using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LauncherV2.Plugins.DiabloII;

/// <summary>
/// Modal randomizer dialog shown when the user clicks "Launch Standalone" for
/// Diablo II. Exposes the same options the Archipelago apworld does (seed,
/// goal, shuffles, skill pool, fillers, class filter, check categories) and
/// hands the chosen <see cref="D2RandomizerSettings"/> back to the plugin,
/// which writes them to <c>d2arch.ini [settings]</c> before launching the
/// (DLL-injected) game. Built in code to match the rest of the D2 plugin UI.
/// </summary>
internal sealed class D2StandaloneSettingsDialog : Window
{
    private static readonly Brush Muted  = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
    private static readonly Brush Fg     = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
    private static readonly Brush InputBg = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20));
    private static readonly Brush PanelBg = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x22));
    private static readonly Brush BtnBg   = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30));
    private static readonly Brush Border  = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50));
    private static readonly Brush Accent  = new SolidColorBrush(Color.FromRgb(0x7A, 0x10, 0x10));

    static D2StandaloneSettingsDialog()
    {
        Muted.Freeze(); Fg.Freeze(); InputBg.Freeze(); PanelBg.Freeze();
        BtnBg.Freeze(); Border.Freeze(); Accent.Freeze();
    }

    private readonly D2RandomizerSettings _s;
    private readonly D2SeedLibrary _lib;
    private TextBox _seedBox = null!;
    private StackPanel _seedListPanel = null!;

    /// Set on Start (new seed) or when a seed is loaded; null = cancelled.
    private D2StandaloneLaunchChoice? _choice;

    private D2StandaloneSettingsDialog(D2RandomizerSettings initial, D2SeedLibrary lib)
    {
        // Work on a copy so a Cancel leaves the caller's object untouched.
        _s   = Clone(initial);
        _lib = lib;

        Title  = "Diablo II — Standalone Randomizer";
        Width  = 880;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0A, 0x14));
        ResizeMode = ResizeMode.CanResize;
        MinWidth = 720; MinHeight = 480;

        var root = new DockPanel { Margin = new Thickness(0) };

        // ── Header ────────────────────────────────────────────────────────
        var header = new StackPanel { Margin = new Thickness(18, 16, 18, 8) };
        header.Children.Add(new TextBlock
        {
            Text = "Standalone randomizer", FontSize = 17, FontWeight = FontWeights.Bold,
            Foreground = Fg,
        });
        header.Children.Add(new TextBlock
        {
            Text = "Set every option, then start the game. These are written to the mod " +
                   "so a solo run randomizes exactly like an Archipelago world — no server needed.",
            FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ── Buttons (docked bottom so they never scroll away) ─────────────
        var buttonBar = new DockPanel { Margin = new Thickness(18, 10, 18, 14), LastChildFill = false };
        DockPanel.SetDock(buttonBar, Dock.Bottom);

        var cancelBtn = new Button
        {
            Content = "Cancel", Width = 110, Height = 34,
            Background = BtnBg, Foreground = Fg, BorderBrush = Border,
            Margin = new Thickness(0, 0, 8, 0),
        };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        var startBtn = new Button
        {
            Content = "Start game", Width = 150, Height = 34,
            Background = Accent, Foreground = Brushes.White, BorderBrush = Border,
            FontWeight = FontWeights.SemiBold,
        };
        startBtn.Click += OnStart;

        DockPanel.SetDock(cancelBtn, Dock.Right);
        DockPanel.SetDock(startBtn,  Dock.Right);
        // Right-docked items lay out right-to-left in declaration order.
        buttonBar.Children.Add(startBtn);
        buttonBar.Children.Add(cancelBtn);
        root.Children.Add(buttonBar);

        // ── Body: two columns — left = new-seed settings, right = the seed
        //    library (load a previously-generated standalone world). ──────────
        var body = new StackPanel { Margin = new Thickness(18, 4, 12, 4) };

        BuildSeed(body);
        BuildGoalAndMode(body);
        BuildChecks(body);
        BuildSkillPool(body);
        BuildShuffles(body);
        BuildFillers(body);
        BuildClassFilter(body);
        BuildBonusChecks(body);
        BuildExtraChecks(body);
        BuildCollection(body);
        BuildCustomGoal(body);
        BuildMisc(body);

        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        Grid.SetColumn(scroll, 0);
        grid.Children.Add(scroll);

        var seedPanel = BuildSeedLibraryPanel();
        Grid.SetColumn(seedPanel, 1);
        grid.Children.Add(seedPanel);

        root.Children.Add(grid);
        Content = root;
    }

    /// Show the dialog modally. Returns the launch choice (new seed or load an
    /// existing one), or null if the user cancelled.
    public static D2StandaloneLaunchChoice? ShowAndGet(
        Window? owner, D2RandomizerSettings initial, D2SeedLibrary lib)
    {
        var dlg = new D2StandaloneSettingsDialog(initial, lib);
        if (owner != null && !ReferenceEquals(owner, dlg)) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? dlg._choice : null;
    }

    /// The right column: a scrollable list of previously-generated seeds, each
    /// showing its characters and a "Load" button. Loading a seed replays that
    /// exact world — only its characters appear in-game (own save folder).
    private UIElement BuildSeedLibraryPanel()
    {
        var outer = new Border
        {
            Background = PanelBg, BorderBrush = Border, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 14, 4), CornerRadius = new CornerRadius(4),
        };
        var dock = new DockPanel { Margin = new Thickness(12, 12, 12, 12) };

        var head = new StackPanel();
        head.Children.Add(new TextBlock
        {
            Text = "PREVIOUS SEEDS", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Muted,
        });
        head.Children.Add(new TextBlock
        {
            Text = "Load a standalone world you already generated. Each seed keeps its own " +
                   "characters — only that seed's characters show up in-game.",
            FontSize = 10.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8),
        });
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);

        _seedListPanel = new StackPanel();
        RefreshSeedList();

        var listScroll = new ScrollViewer
        {
            Content = _seedListPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        dock.Children.Add(listScroll);

        outer.Child = dock;
        return outer;
    }

    private void RefreshSeedList()
    {
        _seedListPanel.Children.Clear();
        var seeds = _lib.ListSeeds();
        if (seeds.Count == 0)
        {
            _seedListPanel.Children.Add(new TextBlock
            {
                Text = "No seeds yet. Set your options on the left and press " +
                       "\"Start game\" to generate your first one.",
                FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
            return;
        }
        foreach (var s in seeds) _seedListPanel.Children.Add(SeedCard(s));
    }

    private UIElement SeedCard(D2SeedInfo s)
    {
        var card = new Border
        {
            Background = InputBg, BorderBrush = Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 8, 10, 10),
        };
        var sp = new StackPanel();

        sp.Children.Add(new TextBlock
        {
            Text = "Seed " + s.Seed.ToString(CultureInfo.InvariantCulture),
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Fg,
        });
        sp.Children.Add(new TextBlock
        {
            Text = $"Goal: {GoalName(s.Settings.Goal)}   ·   {s.Created:yyyy-MM-dd}",
            FontSize = 10, Foreground = Muted, Margin = new Thickness(0, 1, 0, 4),
        });

        string chars = s.Characters.Count == 0
            ? "No characters yet"
            : "Characters: " + string.Join(", ", s.Characters);
        sp.Children.Add(new TextBlock
        {
            Text = chars, FontSize = 11, Foreground = s.Characters.Count == 0 ? Muted : Fg,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
        var loadBtn = new Button
        {
            Content = "Load this seed", Height = 28,
            Background = BtnBg, Foreground = Fg, BorderBrush = Border,
            Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0),
        };
        loadBtn.Click += (_, _) =>
        {
            _choice = new D2StandaloneLaunchChoice
            {
                IsLoad = true, Seed = s.Seed, Settings = s.Settings,
            };
            DialogResult = true;
            Close();
        };
        var delBtn = new Button
        {
            Content = "Delete", Height = 28,
            Background = BtnBg, Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x6A, 0x6A)),
            BorderBrush = Border, Padding = new Thickness(12, 0, 12, 0),
        };
        delBtn.Click += (_, _) =>
        {
            var ans = MessageBox.Show(this,
                $"Delete seed {s.Seed} and ALL its characters " +
                $"({(s.Characters.Count == 0 ? "no characters" : string.Join(", ", s.Characters))})?\n\n" +
                "This cannot be undone.",
                "Delete seed", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ans != MessageBoxResult.Yes) return;
            if (!_lib.DeleteSeed(s.Seed))
            {
                MessageBox.Show(this,
                    $"Could not delete seed {s.Seed}. The folder may be locked by " +
                    "OneDrive sync or the running game — close the game and try again.",
                    "Delete failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            RefreshSeedList();
        };
        btnRow.Children.Add(loadBtn);
        btnRow.Children.Add(delBtn);
        sp.Children.Add(btnRow);

        card.Child = sp;
        return card;
    }

    private static string GoalName(int g) => g switch
    {
        0 => "Normal", 1 => "Nightmare", 2 => "Hell", 3 => "Collection", 4 => "Custom", _ => "Normal",
    };

    // ── Section builders ──────────────────────────────────────────────────

    private void BuildSeed(Panel host)
    {
        host.Children.Add(Section("SEED"));

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        _seedBox = new TextBox
        {
            // Always open on a FRESH random seed so the user never accidentally
            // re-launches an existing world (load those from the right instead).
            Text = System.Random.Shared.Next(1, int.MaxValue).ToString(CultureInfo.InvariantCulture),
            FontSize = 13, Padding = new Thickness(6, 5, 6, 5),
            Background = InputBg, Foreground = Fg, BorderBrush = Border,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var randomBtn = new Button
        {
            Content = "Random", Width = 90, Height = 30,
            Background = BtnBg, Foreground = Fg, BorderBrush = Border,
        };
        randomBtn.Click += (_, _) =>
        {
            // 1..int.MaxValue so the value is always a usable non-zero seed.
            _seedBox.Text = System.Random.Shared.Next(1, int.MaxValue)
                                  .ToString(CultureInfo.InvariantCulture);
        };
        DockPanel.SetDock(randomBtn, Dock.Right);
        row.Children.Add(randomBtn);
        row.Children.Add(_seedBox);
        host.Children.Add(row);
        host.Children.Add(Hint("This is the world's seed. 0 = roll a fresh random one. Each seed becomes " +
                               "its own world with its own characters (load past ones on the right)."));
    }

    private void BuildGoalAndMode(Panel host)
    {
        host.Children.Add(Section("GOAL & MODE"));

        host.Children.Add(new TextBlock
        {
            Text = "Goal / win condition:", Foreground = Fg, FontSize = 12,
            Margin = new Thickness(0, 0, 0, 3),
        });
        host.Children.Add(SegmentedChoice(
            new[] { "Normal", "Nightmare", "Hell", "Collection", "Custom" },
            Math.Clamp(_s.Goal, 0, 4), v => _s.Goal = v));

        host.Children.Add(Check("Skill Hunting (find your skills as checks)",
            _s.SkillHunting, v => _s.SkillHunting = v));
        host.Children.Add(Check("Zone Locking (unlock areas via checks)",
            _s.ZoneLocking, v => _s.ZoneLocking = v));
    }

    /// A readable segmented selector (row of buttons, the selected one
    /// highlighted) — replaces the hard-to-read themed ComboBox.
    private UIElement SegmentedChoice(string[] labels, int selected, Action<int> set)
    {
        var row = new System.Windows.Controls.WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var btns = new List<Button>();
        void Paint()
        {
            for (int i = 0; i < btns.Count; i++)
            {
                bool on = i == selected;
                btns[i].Background = on ? Accent : BtnBg;
                btns[i].Foreground = on ? Brushes.White : Fg;
                btns[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        for (int i = 0; i < labels.Length; i++)
        {
            int idx = i;
            var b = new Button
            {
                Content = labels[i], Height = 28, BorderBrush = Border, FontSize = 12,
                Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 6, 6),
            };
            b.Click += (_, _) => { selected = idx; set(idx); Paint(); };
            btns.Add(b);
            row.Children.Add(b);
        }
        Paint();
        return row;
    }

    private void BuildChecks(Panel host)
    {
        host.Children.Add(Section("PROGRESSION CHECKS"));
        host.Children.Add(Check("Hunting (kill named/super-unique monsters)",
            _s.QuestHunting, v => _s.QuestHunting = v));
        host.Children.Add(Check("Kill Zones (clear-out objectives)",
            _s.QuestKillZones, v => _s.QuestKillZones = v));
        host.Children.Add(Check("Exploration (reach areas)",
            _s.QuestExploration, v => _s.QuestExploration = v));
        host.Children.Add(Check("Waypoints (activate waypoints)",
            _s.QuestWaypoints, v => _s.QuestWaypoints = v));
        host.Children.Add(Check("Level Milestones (hit character levels)",
            _s.QuestLevelMilestones, v => _s.QuestLevelMilestones = v));
    }

    private void BuildSkillPool(Panel host)
    {
        host.Children.Add(Section("SKILL POOL"));
        host.Children.Add(Slider("Skill pool size", 1, 210, _s.SkillPoolSize,
            v => _s.SkillPoolSize = v));
        host.Children.Add(Slider("Starting skills", 0, 20, _s.StartingSkills,
            v => _s.StartingSkills = v));
        host.Children.Add(Slider("XP multiplier", 1, 10, _s.XPMultiplier,
            v => _s.XPMultiplier = v, suffix: "×"));
        host.Children.Add(Check("Skill level requirements",
            _s.SkillLevelReqs, v => _s.SkillLevelReqs = v));
        host.Children.Add(Check("Item level / stat requirements",
            _s.ItemLevelReqs, v => _s.ItemLevelReqs = v));
    }

    private void BuildShuffles(Panel host)
    {
        host.Children.Add(Section("SHUFFLES"));
        host.Children.Add(Check("Monster shuffle", _s.MonsterShuffle, v => _s.MonsterShuffle = v));
        host.Children.Add(Check("Boss shuffle",    _s.BossShuffle,    v => _s.BossShuffle = v));
        host.Children.Add(Check("Shop shuffle",    _s.ShopShuffle,    v => _s.ShopShuffle = v));
        host.Children.Add(Check("Entrance shuffle", _s.EntranceShuffle, v => _s.EntranceShuffle = v));
    }

    private void BuildFillers(Panel host)
    {
        host.Children.Add(Section("FILLER WEIGHTS"));
        host.Children.Add(Hint("Relative chance of each filler when a check isn't progression."));
        host.Children.Add(Check("Traps enabled", _s.TrapsEnabled, v => _s.TrapsEnabled = v));
        host.Children.Add(Slider("Trap weight",       0, 100, _s.TrapPct,     v => _s.TrapPct = v,     suffix: "%"));
        host.Children.Add(Slider("Gold weight",       0, 100, _s.GoldPct,     v => _s.GoldPct = v,     suffix: "%"));
        host.Children.Add(Slider("Stat points weight",  0, 100, _s.StatPtsPct,  v => _s.StatPtsPct = v,  suffix: "%"));
        host.Children.Add(Slider("Skill points weight", 0, 100, _s.SkillPtsPct, v => _s.SkillPtsPct = v, suffix: "%"));
        host.Children.Add(Slider("Reset points weight",  0, 100, _s.ResetPtsPct, v => _s.ResetPtsPct = v, suffix: "%"));
        host.Children.Add(Slider("Loot weight",       0, 100, _s.LootPct,     v => _s.LootPct = v,     suffix: "%"));
    }

    private void BuildClassFilter(Panel host)
    {
        host.Children.Add(Section("CLASS FILTER (skill pool)"));
        host.Children.Add(Hint("When on, the random skill pool only draws skills from the ticked classes."));
        host.Children.Add(Check("Enable class filter", _s.ClassFilter, v => _s.ClassFilter = v));
        host.Children.Add(Check("Amazon",     _s.ClsAmazon,      v => _s.ClsAmazon = v));
        host.Children.Add(Check("Sorceress",  _s.ClsSorceress,   v => _s.ClsSorceress = v));
        host.Children.Add(Check("Necromancer", _s.ClsNecromancer, v => _s.ClsNecromancer = v));
        host.Children.Add(Check("Paladin",    _s.ClsPaladin,     v => _s.ClsPaladin = v));
        host.Children.Add(Check("Barbarian",  _s.ClsBarbarian,   v => _s.ClsBarbarian = v));
        host.Children.Add(Check("Druid",      _s.ClsDruid,       v => _s.ClsDruid = v));
        host.Children.Add(Check("Assassin",   _s.ClsAssassin,    v => _s.ClsAssassin = v));
        host.Children.Add(Check("I play an Assassin (tune Assassin balance)",
            _s.IPlayAssassin, v => _s.IPlayAssassin = v));
    }

    private void BuildBonusChecks(Panel host)
    {
        host.Children.Add(Section("BONUS CHECKS"));
        host.Children.Add(Check("Shrines",        _s.CheckShrines,        v => _s.CheckShrines = v));
        host.Children.Add(Check("Urns",           _s.CheckUrns,           v => _s.CheckUrns = v));
        host.Children.Add(Check("Barrels",        _s.CheckBarrels,        v => _s.CheckBarrels = v));
        host.Children.Add(Check("Chests",         _s.CheckChests,         v => _s.CheckChests = v));
        host.Children.Add(Check("Set item pickups", _s.CheckSetPickups,   v => _s.CheckSetPickups = v));
        host.Children.Add(Check("Gold milestones", _s.CheckGoldMilestones, v => _s.CheckGoldMilestones = v));
    }

    private void BuildExtraChecks(Panel host)
    {
        host.Children.Add(Section("EXTRA CHECKS"));
        host.Children.Add(Check("Cow level",       _s.CheckCowLevel,        v => _s.CheckCowLevel = v));
        host.Children.Add(Check("Mercenary milestones", _s.CheckMercMilestones, v => _s.CheckMercMilestones = v));
        host.Children.Add(Check("Hellforge & high runes", _s.CheckHellforgeRunes, v => _s.CheckHellforgeRunes = v));
        host.Children.Add(Check("NPC dialogue",    _s.CheckNpcDialogue,     v => _s.CheckNpcDialogue = v));
        host.Children.Add(Check("Runeword crafting", _s.CheckRunewordCrafting, v => _s.CheckRunewordCrafting = v));
        host.Children.Add(Check("Cube recipes",    _s.CheckCubeRecipes,     v => _s.CheckCubeRecipes = v));
    }

    private void BuildCollection(Panel host)
    {
        host.Children.Add(Section("COLLECTION TARGETS"));
        host.Children.Add(Hint("Only used when Goal = Collection. Tick the sets / runes / gems / " +
                               "specials that count toward completing the collection. All on = collect everything."));

        host.Children.Add(Check("Gems count toward collection", _s.CollectGems, v => _s.CollectGems = v));

        BuildCollectionCategory(host, "Sets (32)",     D2RandomizerSettings.CollectionSetNames,     _s.CollectSets);
        BuildCollectionCategory(host, "Runes (33)",    D2RandomizerSettings.CollectionRuneNames,    _s.CollectRunes);
        BuildCollectionCategory(host, "Specials (10)", D2RandomizerSettings.CollectionSpecialNames, _s.CollectSpecials);
    }

    /// One collapsible-feeling category: a sub-header with All/None buttons,
    /// then one checkbox per item bound directly to <paramref name="arr"/>.
    private void BuildCollectionCategory(Panel host, string title, string[] names, bool[] arr)
    {
        var headerRow = new DockPanel { Margin = new Thickness(0, 10, 0, 2) };
        headerRow.Children.Add(new TextBlock
        {
            Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var boxes = new List<CheckBox>(names.Length);

        var noneBtn = new Button
        {
            Content = "None", Width = 56, Height = 22, FontSize = 10,
            Background = BtnBg, Foreground = Fg, BorderBrush = Border,
            Margin = new Thickness(6, 0, 0, 0),
        };
        var allBtn = new Button
        {
            Content = "All", Width = 56, Height = 22, FontSize = 10,
            Background = BtnBg, Foreground = Fg, BorderBrush = Border,
            Margin = new Thickness(6, 0, 0, 0),
        };
        void SetAll(bool on)
        {
            for (int i = 0; i < arr.Length; i++) arr[i] = on;
            foreach (var b in boxes) b.IsChecked = on;
        }
        allBtn.Click  += (_, _) => SetAll(true);
        noneBtn.Click += (_, _) => SetAll(false);
        DockPanel.SetDock(noneBtn, Dock.Right);
        DockPanel.SetDock(allBtn,  Dock.Right);
        headerRow.Children.Add(noneBtn);
        headerRow.Children.Add(allBtn);
        host.Children.Add(headerRow);

        for (int i = 0; i < names.Length; i++)
        {
            int idx = i;   // capture
            var cb = new CheckBox
            {
                Content = names[i], IsChecked = arr[i], Foreground = Fg, FontSize = 11,
                Margin = new Thickness(8, 1, 0, 1),
            };
            cb.Checked   += (_, _) => arr[idx] = true;
            cb.Unchecked += (_, _) => arr[idx] = false;
            boxes.Add(cb);
            host.Children.Add(cb);
        }
    }

    private void BuildCustomGoal(Panel host)
    {
        host.Children.Add(Section("CUSTOM GOAL TARGETS"));
        host.Children.Add(Hint("Only used when Goal = Custom. The game is won when EVERY ticked " +
                               "target is achieved (plus the optional gold target). This is the " +
                               "same build-your-own goal Archipelago offers."));

        var goldRow = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
        goldRow.Children.Add(new TextBlock
        {
            Text = "Lifetime gold target (0 = none)", Foreground = Fg, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var goldBox = new TextBox
        {
            Text = _s.CustomGoalGold.ToString(CultureInfo.InvariantCulture), Width = 130,
            FontSize = 12, Padding = new Thickness(6, 4, 6, 4),
            Background = InputBg, Foreground = Fg, BorderBrush = Border,
        };
        goldBox.TextChanged += (_, _) =>
        {
            if (long.TryParse(goldBox.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long g) && g >= 0)
                _s.CustomGoalGold = g;
        };
        DockPanel.SetDock(goldBox, Dock.Right);
        goldRow.Children.Add(goldBox);
        host.Children.Add(goldRow);

        foreach (var group in D2RandomizerSettings.CustomGoalCatalog.GroupBy(d => d.Group))
            BuildCustomGoalGroup(host, group.Key, group.ToArray());
    }

    private void BuildCustomGoalGroup(Panel host, string title, D2RandomizerSettings.CustomGoalDef[] defs)
    {
        var headerRow = new DockPanel { Margin = new Thickness(0, 10, 0, 2) };
        headerRow.Children.Add(new TextBlock
        {
            Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var boxes = new List<CheckBox>(defs.Length);
        var noneBtn = new Button
        {
            Content = "None", Width = 56, Height = 22, FontSize = 10,
            Background = BtnBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(6, 0, 0, 0),
        };
        var allBtn = new Button
        {
            Content = "All", Width = 56, Height = 22, FontSize = 10,
            Background = BtnBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(6, 0, 0, 0),
        };
        void SetAll(bool on)
        {
            foreach (var d in defs)
            {
                if (on) _s.CustomGoalTargets.Add(d.Token);
                else    _s.CustomGoalTargets.Remove(d.Token);
            }
            foreach (var b in boxes) b.IsChecked = on;
        }
        allBtn.Click  += (_, _) => SetAll(true);
        noneBtn.Click += (_, _) => SetAll(false);
        DockPanel.SetDock(noneBtn, Dock.Right);
        DockPanel.SetDock(allBtn,  Dock.Right);
        headerRow.Children.Add(noneBtn);
        headerRow.Children.Add(allBtn);
        host.Children.Add(headerRow);

        foreach (var d in defs)
        {
            string token = d.Token;
            var cb = new CheckBox
            {
                Content = d.Display, IsChecked = _s.CustomGoalTargets.Contains(token),
                Foreground = Fg, FontSize = 11, Margin = new Thickness(8, 1, 0, 1),
            };
            cb.Checked   += (_, _) => _s.CustomGoalTargets.Add(token);
            cb.Unchecked += (_, _) => _s.CustomGoalTargets.Remove(token);
            boxes.Add(cb);
            host.Children.Add(cb);
        }
    }

    private void BuildMisc(Panel host)
    {
        host.Children.Add(Section("DISPLAY"));
        host.Children.Add(Check("Show skill tier colours", _s.ShowTierColors, v => _s.ShowTierColors = v));
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        string raw = _seedBox.Text.Trim();
        if (raw.Length == 0) raw = "0";
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seed)
            || seed < 0 || seed > uint.MaxValue)
        {
            MessageBox.Show(this,
                "Seed must be a whole number between 0 and 4294967295.\n" +
                "0 = generate a fresh random seed for this new world.",
                "Invalid seed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // A standalone world needs a concrete seed so it gets its own save
        // folder + character list. 0 → roll a fresh non-zero one.
        if (seed == 0) seed = System.Random.Shared.Next(1, int.MaxValue);
        _s.Seed = seed;
        _choice = new D2StandaloneLaunchChoice { IsLoad = false, Seed = seed, Settings = _s };
        DialogResult = true;
        Close();
    }

    // ── Control helpers ───────────────────────────────────────────────────

    private static TextBlock Section(string text) => new()
    {
        Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
        Foreground = Muted, Margin = new Thickness(0, 16, 0, 6),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text, FontSize = 10.5, Foreground = Muted,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
    };

    private static CheckBox Check(string label, bool value, Action<bool> set)
    {
        var cb = new CheckBox
        {
            Content = label, IsChecked = value, Foreground = Fg, FontSize = 12,
            Margin = new Thickness(0, 3, 0, 3),
        };
        cb.Checked   += (_, _) => set(true);
        cb.Unchecked += (_, _) => set(false);
        return cb;
    }

    private static UIElement Slider(string label, int min, int max, int value,
                                    Action<int> set, string suffix = "")
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        var top = new DockPanel();
        var lbl = new TextBlock { Text = label, Foreground = Fg, FontSize = 12 };
        var val = new TextBlock
        {
            Text = value.ToString(CultureInfo.InvariantCulture) + suffix,
            Foreground = Muted, FontSize = 12, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        DockPanel.SetDock(val, Dock.Right);
        top.Children.Add(val);
        top.Children.Add(lbl);
        panel.Children.Add(top);

        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
            IsSnapToTickEnabled = true, TickFrequency = 1,
            SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10),
            Margin = new Thickness(0, 2, 0, 0),
        };
        slider.ValueChanged += (_, ev) =>
        {
            int v = (int)Math.Round(ev.NewValue);
            val.Text = v.ToString(CultureInfo.InvariantCulture) + suffix;
            set(v);
        };
        panel.Children.Add(slider);
        return panel;
    }

    private static D2RandomizerSettings Clone(D2RandomizerSettings s) => new()
    {
        Seed = s.Seed, Goal = s.Goal,
        SkillHunting = s.SkillHunting, ZoneLocking = s.ZoneLocking,
        QuestHunting = s.QuestHunting, QuestKillZones = s.QuestKillZones,
        QuestExploration = s.QuestExploration, QuestWaypoints = s.QuestWaypoints,
        QuestLevelMilestones = s.QuestLevelMilestones,
        SkillPoolSize = s.SkillPoolSize, StartingSkills = s.StartingSkills,
        TrapsEnabled = s.TrapsEnabled, TrapPct = s.TrapPct, GoldPct = s.GoldPct,
        StatPtsPct = s.StatPtsPct, SkillPtsPct = s.SkillPtsPct,
        ResetPtsPct = s.ResetPtsPct, LootPct = s.LootPct,
        MonsterShuffle = s.MonsterShuffle, BossShuffle = s.BossShuffle,
        ShopShuffle = s.ShopShuffle, EntranceShuffle = s.EntranceShuffle,
        SkillLevelReqs = s.SkillLevelReqs, ItemLevelReqs = s.ItemLevelReqs,
        XPMultiplier = s.XPMultiplier,
        ClassFilter = s.ClassFilter, ClsAmazon = s.ClsAmazon,
        ClsSorceress = s.ClsSorceress, ClsNecromancer = s.ClsNecromancer,
        ClsPaladin = s.ClsPaladin, ClsBarbarian = s.ClsBarbarian,
        ClsDruid = s.ClsDruid, ClsAssassin = s.ClsAssassin,
        IPlayAssassin = s.IPlayAssassin,
        CheckShrines = s.CheckShrines, CheckUrns = s.CheckUrns,
        CheckBarrels = s.CheckBarrels, CheckChests = s.CheckChests,
        CheckSetPickups = s.CheckSetPickups, CheckGoldMilestones = s.CheckGoldMilestones,
        CheckCowLevel = s.CheckCowLevel, CheckMercMilestones = s.CheckMercMilestones,
        CheckHellforgeRunes = s.CheckHellforgeRunes, CheckNpcDialogue = s.CheckNpcDialogue,
        CheckRunewordCrafting = s.CheckRunewordCrafting, CheckCubeRecipes = s.CheckCubeRecipes,
        ShowTierColors = s.ShowTierColors,
        CollectSets     = (bool[])s.CollectSets.Clone(),
        CollectRunes    = (bool[])s.CollectRunes.Clone(),
        CollectSpecials = (bool[])s.CollectSpecials.Clone(),
        CollectGems     = s.CollectGems,
        CustomGoalTargets = new System.Collections.Generic.HashSet<string>(s.CustomGoalTargets),
        CustomGoalGold    = s.CustomGoalGold,
    };
}
