using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;
using LauncherV2.Core.Archipelago;

namespace LauncherV2.UI.Dialogs;

// YamlBuilderDialog — "Create YAML" for every game, not just the two that
// hand-wrote one.
//
// Diablo II and OpenTTD each ship a YAML dialog with their options typed out
// by hand. That is the right answer for two games and an impossible one for
// three hundred and forty: nobody is transcribing every option of every world.
//
// So this reads the SAME thing Archipelago's own generator reads -- the option
// template the world publishes -- and draws a form from it. A game that has a
// template gets a real form with every option, its description, its range and
// its default, for free. A game whose apworld is not installed still gets a
// valid YAML with the defaults; it just cannot show what those defaults are.
//
// Nothing here executes a world's code. A template is text (see
// ApOptionTemplate), and this only ever reads it.
public sealed class YamlBuilderDialog : Window
{
    // Not readonly: the dialog can heal itself after opening — install the
    // world, have the engine write the template, and reload — and the reload
    // replaces both of these.
    private ApTemplate? _template;
    private string _gameName;               // the AP world name, e.g. "Ocarina of Time"
    private readonly string _displayName;
    private readonly string? _gameId;       // catalogue id, for the apworld updater
    private readonly ApEngine.Report? _engine;
    private ScrollViewer? _scroll;
    private bool _userTouched;              // any input inside the options area
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly TextBox _slotName;
    private readonly TextBlock _status;

    /// Every option row with the text it can be found by, so the filter can
    /// hide rows without rebuilding the form (which would throw away every
    /// control's state, including the ones the player just set).
    private readonly List<(string Haystack, UIElement Row, bool IsHeader)> _rows = new();
    private TextBlock? _filterCount;

    /// Open the builder for one game. `apWorldName` is the name Archipelago
    /// knows it by, which is not always the name on the card. `gameId` is the
    /// catalogue id — with it, the dialog can fetch the world itself.
    public static void ShowFor(Window? owner, string displayName, string apWorldName,
                               string? gameId = null)
        => new YamlBuilderDialog(owner, displayName, apWorldName, gameId).ShowDialog();

    private YamlBuilderDialog(Window? owner, string displayName, string apWorldName,
                              string? gameId = null)
    {
        _gameName = apWorldName;
        _displayName = displayName;
        _gameId = gameId;

        Title  = $"Create YAML — {displayName}";
        Width  = 720;
        Height = 780;
        // Owner BEFORE the location: CenterOwner with no owner silently means
        // CenterScreen, so setting it afterwards would have centred every one
        // of these on the desktop instead of on the launcher.
        Owner  = owner;
        WindowStartupLocation = owner == null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        Background = Brush("BrushBackground", "#0D1018");

        // The template comes from the engine the player nominated, which is
        // also the engine that will generate the seed -- so an option this
        // form offers is an option that generator understands.
        var settings = SettingsStore.Load();
        var engine   = ApEngine.Discover(string.IsNullOrWhiteSpace(settings.ApEnginePath)
                                         ? null : settings.ApEnginePath);
        _engine = engine;
        if (engine is { Usable: true })
            TryLoadTemplate(engine);

        // (Defaults are seeded by TryLoadTemplate, so a reload after the
        // dialog heals itself seeds them the same way.)

        var root = new Grid { Margin = new Thickness(22, 18, 22, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── head ──────────────────────────────────────────────────────────
        var head = new StackPanel();
        head.Children.Add(new TextBlock
        {
            Text = "Create your YAML",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("BrushAccent", "#CCA800"),
        });
        head.Children.Add(new TextBlock
        {
            Text = $"This is the file a multiworld host asks you for. It says who you "
                 + $"are and how you want {displayName} randomised. Fill it in here and "
                 + "save it — no text editor involved.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 14),
            Foreground = Brush("BrushMuted", "#727A99"),
        });

        head.Children.Add(new TextBlock
        {
            Text = "YOUR SLOT NAME",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = Brush("BrushMuted", "#727A99"),
        });
        _slotName = new TextBox
        {
            Text = Environment.UserName,
            FontSize = 13,
            Padding = new Thickness(7, 5, 7, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 280,
        };
        head.Children.Add(_slotName);
        head.Children.Add(new TextBlock
        {
            Text = $"How you appear to everyone else in the session. "
                 + $"Up to {ApSlot.MaxNameLength} characters.",
            FontSize = 10.5,
            Margin = new Thickness(0, 3, 0, 14),
            Foreground = Brush("BrushMuted", "#727A99"),
            Opacity = 0.85,
        });
        // A world can offer well over a hundred options. Scrolling for the one
        // you came to change is the difference between a form and a wall.
        var filterRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var filterCount = new TextBlock
        {
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = Brush("BrushMuted", "#727A99"),
        };
        DockPanel.SetDock(filterCount, Dock.Right);
        var filter = new TextBox
        {
            FontSize = 12,
            Padding = new Thickness(7, 4, 7, 4),
            ToolTip = "Type part of an option's name or description",
        };
        filter.TextChanged += (_, _) => ApplyFilter(filter.Text, filterCount);
        filterRow.Children.Add(filterCount);
        filterRow.Children.Add(filter);
        head.Children.Add(new TextBlock
        {
            Text = "FIND AN OPTION",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = Brush("BrushMuted", "#727A99"),
        });
        head.Children.Add(filterRow);
        _filterCount = filterCount;

        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // ── options ───────────────────────────────────────────────────────
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildOptionsList(),
            Margin = new Thickness(0, 0, 0, 12),
        };
        // Any input inside the options area means the player has started
        // filling the form. A background world-update must then leave the UI
        // alone: rebuilding it would eat their edits, the same way the Join
        // sweep ate slot names.
        scroll.PreviewKeyDown   += (_, _) => _userTouched = true;
        scroll.PreviewMouseDown += (_, _) => _userTouched = true;
        _scroll = scroll;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // Heal after the window is up, not in the constructor: fetching a
        // world and asking the engine to write templates takes seconds, and a
        // dialog that blocks before it exists looks like a hang.
        Loaded += (_, _) => _ = PrepareAsync();

        // ── foot ──────────────────────────────────────────────────────────
        var foot = new StackPanel();
        _status = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = Brush("BrushMuted", "#727A99"),
            Text = "Saved as a .yaml file you can send to the host — or drop into "
                 + "Multiworld → New seed to use yourself.",
        };
        foot.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var btnClose = new Button { Content = "Close", Padding = new Thickness(16, 7, 16, 7),
                                    Margin = new Thickness(0, 0, 8, 0) };
        var btnSave  = new Button { Content = "Save YAML…", Padding = new Thickness(16, 7, 16, 7),
                                    IsDefault = true };
        if (Application.Current?.TryFindResource("BtnSecondaryStyle") is Style sec)
            btnClose.Style = sec;
        if (Application.Current?.TryFindResource("BtnPlayStyle") is Style pri)
            btnSave.Style = pri;
        btnClose.Click += (_, _) => Close();
        btnSave.Click  += (_, _) => Save();
        buttons.Children.Add(btnClose);
        buttons.Children.Add(btnSave);
        foot.Children.Add(buttons);
        Grid.SetRow(foot, 2);
        root.Children.Add(foot);

        // Sets the initial "N options" label now that the rows exist.
        if (_filterCount != null) ApplyFilter("", _filterCount);

        Content = root;
    }

    /// Show only the rows that match, by hiding rather than rebuilding: a
    /// rebuild would discard every control's state, including the value the
    /// player set two seconds ago.
    private void ApplyFilter(string query, TextBlock count)
    {
        string q = query.Trim();
        int shown = 0;

        // Two passes, because a group header's fate depends on the rows after
        // it: match first, then hide any heading left standing over nothing.
        foreach (var (haystack, row, isHeader) in _rows)
        {
            if (isHeader) continue;
            bool hit = q.Length == 0
                    || haystack.Contains(q, StringComparison.OrdinalIgnoreCase);
            row.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
            if (hit) shown++;
        }

        UIElement? header = null;
        bool anyUnder = false;
        foreach (var (_, row, isHeader) in _rows)
        {
            if (isHeader)
            {
                if (header != null)
                    header.Visibility = anyUnder ? Visibility.Visible : Visibility.Collapsed;
                header = row;
                anyUnder = false;
                continue;
            }
            if (row.Visibility == Visibility.Visible) anyUnder = true;
        }
        if (header != null)
            header.Visibility = anyUnder ? Visibility.Visible : Visibility.Collapsed;

        count.Text = q.Length == 0
            ? $"{shown} option{(shown == 1 ? "" : "s")}"
            : shown == 0 ? "nothing matches" : $"{shown} match{(shown == 1 ? "" : "es")}";
    }

    // ------------------------------------------------------------------ rows

    private UIElement BuildRow(ApOption opt)
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        box.Children.Add(new TextBlock
        {
            Text = Pretty(opt.Key),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Foreground = Brush("BrushText", "#CCD0E0"),
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
                Foreground = Brush("BrushMuted", "#727A99"),
            });

        string current = _values.TryGetValue(opt.Key, out var v) ? v : opt.Default ?? "";

        switch (opt.Kind)
        {
            // A third of every world's options are toggles, and a two-item
            // dropdown reading 'false'/'true' is the worst possible control
            // for one. It gets a checkbox -- which is also the thing people
            // reach for and then find they cannot click.
            case ApOptionKind.Toggle when TryOnOff(opt, out string on, out string off):
            {
                var check = new CheckBox
                {
                    Content = "Yes",
                    FontSize = 12,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    IsChecked = string.Equals(current, on, StringComparison.OrdinalIgnoreCase),
                    Foreground = Brush("BrushText", "#CCD0E0"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                // The world's own words for the two states, not ours: some say
                // true/false, some say on/off, and the YAML must carry back
                // exactly what its template offered.
                check.Checked   += (_, _) => { _values[opt.Key] = on;  check.Content = "Yes"; };
                check.Unchecked += (_, _) => { _values[opt.Key] = off; check.Content = "No";  };
                check.Content = check.IsChecked == true ? "Yes" : "No";
                box.Children.Add(check);
                break;
            }

            case ApOptionKind.ItemList:
            {
                box.Children.Add(ListEditor(opt, current, dictionary: false));
                break;
            }

            case ApOptionKind.ItemDict:
            {
                box.Children.Add(ListEditor(opt, current, dictionary: true));
                break;
            }

            case ApOptionKind.Choice:
            case ApOptionKind.Toggle:
            {
                var combo = new ComboBox
                {
                    Width = 300,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Foreground = Brushes.White,
                };
                // Plain strings, not ComboBoxItem: ComboBoxItem carries its own
                // dark default foreground that the app's light TextBlock style
                // never reaches, and the selected value renders unreadable.
                foreach (var c in opt.Choices) combo.Items.Add(Label(c.Value));
                combo.SelectedItem = Label(current);
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedIndex >= 0 && combo.SelectedIndex < opt.Choices.Count)
                        _values[opt.Key] = opt.Choices[combo.SelectedIndex].Value;
                };
                box.Children.Add(combo);
                break;
            }

            case ApOptionKind.Range:
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var text = new TextBox { Width = 120, Padding = new Thickness(6, 4, 6, 4),
                                         Text = current };
                text.TextChanged += (_, _) => _values[opt.Key] = text.Text.Trim();
                row.Children.Add(text);
                row.Children.Add(new TextBlock
                {
                    Text = $"   {opt.Min} – {opt.Max}",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    Foreground = Brush("BrushMuted", "#727A99"),
                });

                // The named landmarks are the fastest way to a sensible value.
                var named = opt.Choices.Where(c => c.Equivalent != null).ToList();
                if (named.Count > 0)
                {
                    var pick = new ComboBox { Width = 170, Margin = new Thickness(10, 0, 0, 0),
                                              Foreground = Brushes.White };
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
                    Width = 300,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(6, 4, 6, 4),
                    Text = current,
                };
                text.TextChanged += (_, _) => _values[opt.Key] = text.Text;
                box.Children.Add(text);
                break;
            }

            default:
            {
                box.Children.Add(new TextBlock
                {
                    Text = current.Length == 0 ? "(empty — left as the world's default)" : current,
                    FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 620,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Foreground = Brush("BrushMuted", "#727A99"),
                });
                break;
            }
        }

        return box;
    }

    /// A toggle's two values, as the world itself names them. Returns false
    /// for anything that is not a clean pair, which then falls through to the
    /// dropdown rather than guessing which half means "on".
    private static bool TryOnOff(ApOption opt, out string on, out string off)
    {
        on = off = "";
        if (opt.Choices.Count != 2) return false;

        foreach (var c in opt.Choices)
        {
            if (c.Value is "true" or "on" or "yes" or "1") on = c.Value;
            else if (c.Value is "false" or "off" or "no" or "0") off = c.Value;
        }
        return on.Length > 0 && off.Length > 0;
    }

    /// One name per line, turned into a YAML flow list or mapping.
    ///
    /// A fifth of every world's options are item or location lists, and they
    /// used to render as the word "(empty)" with no way to change them — a
    /// form that shows an option and refuses to let you set it is worse than
    /// one that hides it. Lines are used rather than commas because item names
    /// contain commas ("Bow, Silver Arrows"), which a comma-separated box
    /// would silently split in half.
    private UIElement ListEditor(ApOption opt, string current, bool dictionary)
    {
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = dictionary
                ? "One per line, as  Item name: 3  — leave empty for none."
                : "One name per line — leave empty for none.",
            FontSize = 10.5,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = Brush("BrushMuted", "#727A99"),
            Opacity = 0.85,
        });

        var edit = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 58,
            MaxHeight = 130,
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            Padding = new Thickness(6, 4, 6, 4),
            Text = ApYamlFlow.ToLines(current),
        };

        edit.TextChanged += (_, _) => _values[opt.Key] =
            dictionary ? ApYamlFlow.LinesToMap(edit.Text)
                       : ApYamlFlow.LinesToList(edit.Text);
        stack.Children.Add(edit);
        return stack;
    }

    // ------------------------------------------------------- template lookup

    /// Resolve and parse this game's template, adopting the template's OWN
    /// world name when it differs from what the catalogue said. The saved
    /// YAML must carry the name the generator answers to — "Starcraft 2" —
    /// not the retail spelling a store card was built with ("StarCraft II").
    private void TryLoadTemplate(ApEngine.Report engine)
    {
        var hit = ResolveTemplate(engine.TemplatesDir, _gameName);
        if (hit == null) return;
        try { _template = ApOptionTemplate.ParseFile(hit.Value.Path); }
        catch (Exception) { _template = null; return; }
        _gameName = hit.Value.WorldName;

        // Defaults first, so a player who changes nothing still gets a
        // complete, honest YAML rather than an empty one. Never over a value
        // the player has already set.
        foreach (var o in _template?.Options ?? Array.Empty<ApOption>())
            if (!o.IsPlumbing && o.Default != null && !_values.ContainsKey(o.Key))
                _values[o.Key] = o.Default;
    }

    /// Find the template for a world name, forgivingly.
    ///
    /// 1. `<name>.yaml` — the honest case.
    /// 2. The `game:` line INSIDE each template. Filenames drop characters
    ///    Windows forbids (a colon), so the file's own name is the authority.
    /// 3. Folded: case, accents, punctuation and roman numerals normalised,
    ///    accepted only when exactly ONE template matches. This is what turns
    ///    a catalogue's "StarCraft II" into the engine's "Starcraft 2" instead
    ///    of an empty form.
    private static (string Path, string WorldName)? ResolveTemplate(
        string templatesDir, string apWorldName)
    {
        string direct = Path.Combine(templatesDir, apWorldName + ".yaml");
        if (File.Exists(direct)) return (direct, apWorldName);
        if (!Directory.Exists(templatesDir)) return null;

        var all = new List<(string Path, string Name)>();
        foreach (string f in Directory.EnumerateFiles(templatesDir, "*.yaml"))
        {
            string? name = TemplateGameName(f);
            if (name == null) continue;
            if (string.Equals(name, apWorldName, StringComparison.OrdinalIgnoreCase))
                return (f, name);
            all.Add((f, name));
        }

        string want = Fold(apWorldName);
        var near = all.Where(c => Fold(c.Name) == want).ToList();
        return near.Count == 1 ? (near[0].Path, near[0].Name) : null;
    }

    /// The world's name as the template itself states it, or null.
    private static string? TemplateGameName(string path)
    {
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                // Top-level key only — options are indented.
                if (!line.StartsWith("game:", StringComparison.Ordinal)) continue;
                string v = line[5..].Trim();
                if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0])
                {
                    char q = v[0];
                    v = v[1..^1];
                    // YAML escapes a quote by doubling it: 'A Hero''s Tail'
                    // means A Hero's Tail. Without this, the doubled quote
                    // would be adopted as the world's name and the generator
                    // would refuse it.
                    if (q == '\'') v = v.Replace("''", "'");
                    else v = v.Replace("\\\"", "\"");
                }
                return v.Length > 0 ? v : null;
            }
        }
        catch (IOException) { }
        return null;
    }

    /// One fold for the whole launcher — the registry's plugin lookup and
    /// this template lookup must forgive the same set of drifts.
    private static string Fold(string s) => GameRegistry.FoldWorldName(s);

    // --------------------------------------------------------- self-healing

    /// Whatever stands between the player and a full option list, fixed in
    /// order, behind the one button they already pressed:
    /// no apworld → fetch it; apworld but no template → have the engine write
    /// them; template present but the world has an update → take it.
    /// Best-effort throughout — a failure leaves the honest note standing.
    private async Task PrepareAsync()
    {
        if (_engine is not { Usable: true } engine) return;

        try
        {
            if (_template == null)
            {
                SetStatus("Fetching this game's option list…");

                // The world itself, when the catalogue knows where it lives.
                if (_gameId != null)
                {
                    var st = await ApworldUpdater.CheckAsync(_gameId).ConfigureAwait(true);
                    if (st.State is ApworldState.Missing or ApworldState.UpdateAvailable)
                    {
                        SetStatus("Installing this game's world into the engine…");
                        await ApworldUpdater.UpdateAsync(_gameId).ConfigureAwait(true);
                    }
                }

                SetStatus("Asking Archipelago to write its option templates…");
                await GenerateTemplatesAsync(engine).ConfigureAwait(true);

                TryLoadTemplate(engine);
                ReloadOptionsUi();
                SetStatus(_template != null ? null
                    : "The option list still could not be produced — the YAML "
                    + "below works with the game's own defaults.");
                return;
            }

            // Template already on screen: quietly take a world update so the
            // options shown are the options the current world understands.
            if (_gameId == null) return;
            var check = await ApworldUpdater.CheckAsync(_gameId).ConfigureAwait(true);
            if (check.State != ApworldState.UpdateAvailable) return;

            SetStatus("A newer world was published — updating…");
            string? err = await ApworldUpdater.UpdateAsync(_gameId).ConfigureAwait(true);
            if (err != null) { SetStatus(null); return; }
            await GenerateTemplatesAsync(engine).ConfigureAwait(true);

            if (_userTouched)
            {
                // Their edits outrank our refresh — same lesson as the Join
                // sweep that ate slot names mid-word.
                SetStatus("The world was updated — reopen this to see any new options.");
                return;
            }
            _template = null;
            TryLoadTemplate(engine);
            ReloadOptionsUi();
            SetStatus("Updated to the newest world.");
        }
        catch (Exception)
        {
            SetStatus(null); // the form still saves a valid defaults YAML
        }
    }

    /// Run the engine's own "Generate Template Options" — the only writer of
    /// Players\Templates — and wait for it.
    private static async Task GenerateTemplatesAsync(ApEngine.Report engine)
    {
        string exe = Path.Combine(engine.Root, "ArchipelagoLauncher.exe");
        if (!File.Exists(exe)) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = engine.Root,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("Generate Template Options");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return;
            using var cts = new System.Threading.CancellationTokenSource(
                TimeSpan.FromSeconds(180));
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    private void ReloadOptionsUi()
    {
        if (_scroll == null) return;
        _scroll.Content = BuildOptionsList();
        if (_filterCount != null) ApplyFilter("", _filterCount);
    }

    private void SetStatus(string? text)
    {
        _status.Text = text
            ?? "Saved as a .yaml file you can send to the host — or drop into "
             + "Multiworld → New seed to use yourself.";
    }

    /// The options area, built from whatever `_template` currently is.
    private StackPanel BuildOptionsList()
    {
        _rows.Clear();
        var list = new StackPanel();
        if (_template == null)
        {
            list.Children.Add(NoTemplateNote(_displayName, _engine, _gameName));
            return list;
        }

        // Plando last. It is the most advanced thing a world offers and it
        // sorts first alphabetically, so the template order put "Plando
        // Connections" — with a paragraph of format instructions — in front
        // of every ordinary setting. Nobody's first impression of a game's
        // options should be its expert feature.
        var shown = _template.Options
            .Where(o => !o.IsPlumbing)
            .OrderBy(o => o.Key.StartsWith("plando", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToList();

        if (shown.Count == 0)
            list.Children.Add(Note($"{_displayName} has no options to set — the "
                                 + "YAML below is all it needs."));

        foreach (var group in shown.Select(o => o.Group).Distinct())
        {
            var header = new TextBlock
            {
                Text = group.ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 14, 0, 8),
                Foreground = Brush("BrushAccent", "#CCA800"),
                Opacity = 0.9,
            };
            list.Children.Add(header);
            _rows.Add((group, header, true));

            foreach (var opt in shown.Where(o => o.Group == group))
            {
                var row = BuildRow(opt);
                list.Children.Add(row);
                _rows.Add((Pretty(opt.Key) + " " + opt.Key + " "
                         + opt.Description, row, false));
            }
        }
        return list;
    }

    private UIElement NoTemplateNote(string displayName, ApEngine.Report? engine,
                                     string apWorldName)
    {
        // THREE different missing things, three different fixes. The middle one
        // was missing and its absence was a lie: with burnout3.apworld sitting
        // in custom_worlds, this told the player to install a game they had
        // already installed. The template is a SEPARATE artefact -- Archipelago
        // writes Players\Templates only when its Launcher is asked to, so an
        // installed world with no template is the normal state right after
        // installing, not a missing world.
        // Both homes count: custom_worlds AND the worlds Archipelago itself
        // ships (lib/worlds). StarCraft 2 is bundled, and telling its owner
        // to "install the game" they were looking at was a lie.
        bool worldInstalled = engine is { Usable: true }
            && (engine.CustomWorlds.Any(w =>
                    string.Equals(w.Game, apWorldName, StringComparison.OrdinalIgnoreCase))
                || ApworldUpdater.BundledGames(engine.Root).Contains(apWorldName));

        string why = engine is not { Usable: true }
            ? "London has no usable Archipelago engine yet, and the options come "
            + "from the engine's own templates. Set one up under Multiworld and "
            + "reopen this."
            : worldInstalled
            ? $"{displayName}'s apworld IS installed, but the engine has not "
            + "written its option template yet. Templates are generated by "
            + "Archipelago itself — run its Launcher once (\"Generate Template "
            + "Options\") and reopen this."
            : $"{displayName}'s apworld is not installed in that engine, so its "
            + "option list is not there to read. Install the game (or copy its "
            + "apworld into the engine) and reopen this.";

        var stack = new StackPanel();
        stack.Children.Add(Note(why));
        stack.Children.Add(Note(
            "You can still save a working YAML right now: it will name you and "
            + $"the game, and every option takes {displayName}'s own default — "
            + "which is exactly what most players send a host anyway."));
        return stack;
    }

    private UIElement Note(string text) => new Border
    {
        Background      = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x20)),
        BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        BorderThickness = new Thickness(1),
        CornerRadius    = new CornerRadius(5),
        Padding         = new Thickness(14, 11, 14, 12),
        Margin          = new Thickness(0, 0, 0, 8),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("BrushMuted", "#727A99"),
        },
    };

    // ------------------------------------------------------------------ save

    private void Save()
    {
        string name = _slotName.Text.Trim();
        if (name.Length == 0)
        {
            Fail("Type a slot name first — it is how the others see you.");
            return;
        }
        if (name.Length > ApSlot.MaxNameLength)
        {
            // Caught here rather than discovered after generation, when the
            // truncated name is already in everybody's tracker.
            Fail($"That name is {name.Length} characters. Archipelago cuts slot "
               + $"names at {ApSlot.MaxNameLength}, so shorten it here where it "
               + "still matters.");
            return;
        }

        var slot = new ApSlot(name, _gameName, _values);
        string yaml = ApPlayerYaml.Render(slot, _template?.RequiresEngineVersion);

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Save your YAML",
            FileName   = SafeFileName($"{name}_{_gameName}") + ".yaml",
            Filter     = "Archipelago YAML (*.yaml)|*.yaml|All files (*.*)|*.*",
            DefaultExt = ".yaml",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, yaml);
            Saved($"Saved to {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Fail("Could not write that file: " + ex.Message);
        }
    }

    private static string SafeFileName(string s)
        => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private void Fail(string msg)
    {
        _status.Foreground = Brush("BrushError", "#D94A4A");
        _status.Text = msg;
    }

    private void Saved(string msg)
    {
        _status.Foreground = Brush("BrushSuccess", "#22C55E");
        _status.Text = msg;
    }

    // ---------------------------------------------------------------- pieces

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

    /// Themed brush when the app's resources are loaded, a literal otherwise —
    /// this dialog must also open from a context that has no App resources.
    private static Brush Brush(string key, string fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
        }
        catch (Exception) { }
        return (Brush)new BrushConverter().ConvertFrom(fallback)!;
    }
}
