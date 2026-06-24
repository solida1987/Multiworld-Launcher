using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
// The project enables WindowsForms, so these names are ambiguous — pin them to WPF.
using UserControl = System.Windows.Controls.UserControl;
using ListBox     = System.Windows.Controls.ListBox;
using Rectangle   = System.Windows.Shapes.Rectangle;
using Path        = System.IO.Path;

namespace LauncherV2.Plugins.DiabloII;

// ═══════════════════════════════════════════════════════════════════════════════
// D2 Map Tracker — the launcher-side graphical map (PopTracker / maphack style).
//
// DATA FLOW (the injected DLL feeds this; see docs/MAP_TRACKER_DESIGN.md):
//   • The DLL walks D2's DRLG room/collision data and exports, per area, a
//     walkable grid + room bounds + objects to <GameDir>\Archipelago\map\
//     level_<id>.map as the player explores.
//   • It also writes zonelock.dat (the live per-difficulty area lock state) so
//     the map can colour every area green (open) / red (locked).
//   • It streams "POS:<levelId>|<x>|<y>" over the pipe for the live "you are
//     here" dot, and CHECK:/MISSING: for the per-area checklist (done/total).
//
// The control owns the model + rendering. It shows the FULL D2 area catalogue
// (every area, all five acts) the moment it opens — areas you have not entered
// yet render as "not explored" but still list their checks and lock state — and
// fills in the real collision map as you walk.
// ═══════════════════════════════════════════════════════════════════════════════

// ── Data model ───────────────────────────────────────────────────────────────

/// One generated area (= one D2 level). Coordinates are D2 sub-tile units; the
/// walkable grid is row-major Width*Height with (0,0) at world tile (OriginX,
/// OriginY) so areas can later be stitched into an act/world overview.
public sealed class D2MapArea
{
    public int    LevelId;
    public string Name = "";
    public int    Act;
    public int    Width;
    public int    Height;
    public int    OriginX;
    public int    OriginY;
    public bool[]? Walkable;                 // true = walkable floor (collision detail)
    public bool[]? Known;                    // true = room exists but not yet explored (dim block)

    public List<D2MapExit>  Exits    = new();
    public List<D2MapPoi>   Pois     = new();

    public bool At(int gx, int gy)
        => Walkable != null && gx >= 0 && gy >= 0 && gx < Width && gy < Height
           && Walkable[gy * Width + gx];
}

public sealed class D2MapExit  { public int TargetLevelId; public string TargetName = ""; public int X, Y; public bool Locked; }
public sealed class D2MapPoi   { public string Kind = ""; public string Label = ""; public int X, Y; } // waypoint / chest / shrine / barrel …

public sealed class D2MapWorld
{
    public long Seed;
    public Dictionary<int, D2MapArea> Areas = new();
}

public sealed class D2PlayerPos { public int LevelId; public int X, Y; }

// ── The control ──────────────────────────────────────────────────────────────

/// Self-contained WPF control: a full area list (grouped by act, coloured by
/// lock state), a rendered collision map with a live player dot + object
/// markers, and a per-area info panel (lock state, gate, checklist done/total).
/// Thread-safe entry points marshal to the UI thread.
public sealed class D2MapTrackerControl : UserControl
{
    // Map palette — warm parchment floor on a dark "void", the readable maphack look.
    private static readonly Color VoidColor  = Color.FromRgb(0x0E, 0x0F, 0x13);
    private static readonly Color FloorColor = Color.FromRgb(0xC2, 0xB6, 0x96);  // explored, walkable
    private static readonly Color DimColor   = Color.FromRgb(0x33, 0x30, 0x2A);  // known room, unexplored
    private static readonly Brush Panel      = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
    private static readonly Brush Muted      = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA6));
    private static readonly Brush Gold       = new SolidColorBrush(Color.FromRgb(0xE6, 0xB8, 0x4C));
    private static readonly Brush OpenGreen  = new SolidColorBrush(Color.FromRgb(0x57, 0xC7, 0x6B));
    private static readonly Brush LockedRed  = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x4F));
    private static readonly Brush CheckDone  = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0x6A));
    private static readonly Brush CheckOpen  = new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x50));
    private static readonly Brush PlayerDot  = new SolidColorBrush(Color.FromRgb(0x55, 0xC8, 0xFF));
    private static readonly Brush SelBg      = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x36));

    private D2MapWorld   _world  = new();          // visited areas (real collision)
    private D2PlayerPos? _player;
    private int          _selectedLevelId = 1;
    private int          _diff = 0;                // 0=Normal 1=Nightmare 2=Hell (viewed difficulty)
    private bool         _showMarkers = true;

    // Live tracker state fed by D2Plugin.
    private readonly HashSet<long> _checkedIds = new();
    private readonly HashSet<long> _activeIds  = new();        // run's location universe (MISSING:)
    private Dictionary<long, string> _locNames = new();        // location id → name (d2_locations.json)
    private readonly HashSet<int>[]  _zoneLocked = { new(), new(), new() };  // per-diff locked area ids
    private bool _haveLockData;
    private bool _diffAutoSet;        // auto-jumped to the player's live difficulty once

    private readonly ListBox  _areaList   = new();
    private readonly Image    _mapImage   = new() { SnapsToDevicePixels = true };
    private readonly Canvas   _overlay    = new();
    private readonly Grid     _mapStack   = new();
    private readonly StackPanel _info     = new();
    private readonly TextBlock _emptyHint = new();
    private readonly StackPanel _diffBar  = new() { Orientation = System.Windows.Controls.Orientation.Horizontal };
    private readonly Button[]  _diffBtns  = new Button[3];

    public D2MapTrackerControl()
    {
        Background = new SolidColorBrush(VoidColor);
        Build();
        RebuildAreaList();
        SelectArea(_selectedLevelId);
    }

    private void Build()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(248) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

        // ── Left: difficulty selector + area list ────────────────────────────
        _areaList.Background        = Panel;
        _areaList.BorderThickness   = new Thickness(0);
        _areaList.Foreground        = Brushes.White;
        ScrollViewer.SetHorizontalScrollBarVisibility(_areaList, ScrollBarVisibility.Disabled);
        _areaList.SelectionChanged += (_, _) =>
        {
            if (_areaList.SelectedItem is ListBoxItem li && li.Tag is int id) SelectArea(id);
        };

        var leftHost = new DockPanel { Background = Panel };

        string[] dn = { "Normal", "Nightmare", "Hell" };
        for (int i = 0; i < 3; i++)
        {
            int d = i;
            var b = new Button
            {
                Content = dn[i], Tag = d, FontSize = 11, Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(i == 0 ? 12 : 3, 8, i == 2 ? 12 : 0, 4),
                Background = Panel, Foreground = Muted, BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Click += (_, _) => SetDifficulty(d);
            _diffBtns[i] = b;
            _diffBar.Children.Add(b);
        }
        DockPanel.SetDock(_diffBar, Dock.Top);
        leftHost.Children.Add(_diffBar);

        var leftHdr = new TextBlock
        {
            Text = "AREAS", Foreground = Muted, FontWeight = FontWeights.Bold,
            FontSize = 11, Margin = new Thickness(12, 6, 0, 6),
        };
        DockPanel.SetDock(leftHdr, Dock.Top);
        leftHost.Children.Add(leftHdr);

        var markerChk = new CheckBox
        {
            Content = "📍 Show markers", IsChecked = true,
            Foreground = Brushes.White, Margin = new Thickness(12, 6, 8, 10),
        };
        markerChk.Checked   += (_, _) => { _showMarkers = true;  DrawOverlay(); };
        markerChk.Unchecked += (_, _) => { _showMarkers = false; DrawOverlay(); };
        DockPanel.SetDock(markerChk, Dock.Bottom);
        leftHost.Children.Add(markerChk);

        leftHost.Children.Add(_areaList);
        Grid.SetColumn(leftHost, 0);
        root.Children.Add(leftHost);
        UpdateDiffButtons();

        // ── Center: the map ──────────────────────────────────────────────────
        _mapStack.Children.Add(_mapImage);
        _mapStack.Children.Add(_overlay);
        var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = _mapStack, Margin = new Thickness(8) };
        _emptyHint.Foreground          = Muted;
        _emptyHint.FontSize            = 14;
        _emptyHint.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyHint.VerticalAlignment   = VerticalAlignment.Center;
        _emptyHint.TextAlignment       = TextAlignment.Center;
        var center = new Grid();
        center.Children.Add(viewbox);
        center.Children.Add(_emptyHint);
        Grid.SetColumn(center, 1);
        root.Children.Add(center);

        // ── Right: per-area info ─────────────────────────────────────────────
        var infoScroll = new ScrollViewer
        {
            Background = Panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _info,
        };
        _info.Margin = new Thickness(14);
        Grid.SetColumn(infoScroll, 2);
        root.Children.Add(infoScroll);

        Content = root;
    }

    // ── Public data API (thread-safe) ────────────────────────────────────────

    public void SetWorld(D2MapWorld world)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetWorld(world)); return; }
        _world = world ?? new D2MapWorld();
        // Re-render the selected area (its collision may have just arrived).
        SelectArea(_selectedLevelId);
    }

    public void SetPlayer(D2PlayerPos pos)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetPlayer(pos)); return; }
        bool areaChanged = _player?.LevelId != pos?.LevelId;
        _player = pos;
        if (areaChanged && pos != null && D2Cat.ContainsKey(pos.LevelId))
        {
            RebuildAreaList();              // refresh the "▸ you are here" marker
            SelectArea(pos.LevelId);        // follow the player into the new area
        }
        else DrawOverlay();
    }

    /// Feed the tracker's location state: the run's full universe (active) and the
    /// checked subset. Drives the per-area checklist + each area's (done/total).
    public void SetLocations(IEnumerable<long>? active, IEnumerable<long>? checkedIds)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetLocations(active, checkedIds)); return; }
        if (active != null)     { _activeIds.Clear();  foreach (var i in active)     _activeIds.Add(i); }
        if (checkedIds != null) { _checkedIds.Clear(); foreach (var i in checkedIds) _checkedIds.Add(i); }
        RebuildAreaList();
        DrawInfo();
    }

    // ── Live map source (the DLL's per-room collision export) ────────────────

    private string?          _mapDir;
    private DispatcherTimer?  _mapPoll;
    private long             _mapStamp;

    /// Point the control at the DLL's map-export folder
    /// (&lt;GameDir&gt;\Archipelago\map). Polls for new room data + the live
    /// zonelock.dat and rebuilds as the player explores. Safe to call repeatedly.
    public void SetMapSource(string mapDir)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetMapSource(mapDir)); return; }
        _mapDir = mapDir;
        EnsureObjCat(mapDir);
        LoadLocationNames(mapDir);             // <GameDir>\Archipelago\d2_locations.json
        _mapStamp = -1;
        if (_mapPoll == null)
        {
            _mapPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _mapPoll.Tick += (_, _) => PollMapDir();
        }
        _mapPoll.Start();
        PollMapDir();
    }

    private void PollMapDir()
    {
        try
        {
            if (string.IsNullOrEmpty(_mapDir) || !Directory.Exists(_mapDir)) return;
            LoadZoneLocks(_mapDir);            // tiny file, reload every poll (keys unlock live)
            long stamp = 0;
            foreach (var f in Directory.GetFiles(_mapDir, "level_*.map"))
            {
                var fi = new FileInfo(f);
                stamp ^= fi.Length * 31 + fi.LastWriteTimeUtc.Ticks;
            }
            if (stamp == _mapStamp) return;
            _mapStamp = stamp;
            var world = BuildWorldFromDir(_mapDir);
            if (world.Areas.Count > 0) SetWorld(world);
        }
        catch { /* best-effort — never break the UI on a half-written file */ }
    }

    /// Load <GameDir>\Archipelago\d2_locations.json → id→name (all difficulties).
    private void LoadLocationNames(string mapDir)
    {
        try
        {
            string archDir = Path.GetFullPath(Path.Combine(mapDir, ".."));
            string p = Path.Combine(archDir, "d2_locations.json");
            if (!File.Exists(p)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            if (!doc.RootElement.TryGetProperty("location_name_to_id", out var n2i)) return;
            var map = new Dictionary<long, string>();
            foreach (var prop in n2i.EnumerateObject())
                if (prop.Value.TryGetInt64(out long id)) map[id] = prop.Name;
            _locNames = map;
        }
        catch { /* missing/old install → checklist falls back to #id */ }
    }

    /// Load the DLL's live per-difficulty lock state (zonelock.dat). Each line is
    /// "<diff>:<lockedId,lockedId,...>"; an area not listed is open.
    private void LoadZoneLocks(string mapDir)
    {
        try
        {
            string p = Path.Combine(mapDir, "zonelock.dat");
            if (!File.Exists(p)) { _haveLockData = false; return; }
            var lines = File.ReadAllLines(p);
            for (int d = 0; d < 3; d++) _zoneLocked[d].Clear();
            foreach (var line in lines)
            {
                if (line.StartsWith("CUR:", StringComparison.Ordinal))
                {
                    // Auto-jump to the player's live difficulty the first time only,
                    // then respect any manual selection.
                    if (!_diffAutoSet && int.TryParse(line.AsSpan(4).Trim(), out int cur) && cur is >= 0 and <= 2)
                    {
                        _diffAutoSet = true;
                        if (cur != _diff) { _diff = cur; UpdateDiffButtons(); }
                    }
                    continue;
                }
                int c = line.IndexOf(':');
                if (c <= 0 || !int.TryParse(line.AsSpan(0, c), out int d) || d < 0 || d > 2) continue;
                foreach (var tok in line[(c + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(tok.Trim(), out int id)) _zoneLocked[d].Add(id);
            }
            _haveLockData = true;
            RebuildAreaList();
            SelectArea(_selectedLevelId);
        }
        catch { /* best-effort */ }
    }

    // ── Per-difficulty area state ────────────────────────────────────────────

    private void SetDifficulty(int d)
    {
        _diffAutoSet = true;            // user chose explicitly — stop auto-jumping
        if (d == _diff) return;
        _diff = d;
        UpdateDiffButtons();
        RebuildAreaList();
        SelectArea(_selectedLevelId);
    }

    private void UpdateDiffButtons()
    {
        for (int i = 0; i < 3; i++)
        {
            bool on = i == _diff;
            _diffBtns[i].Background = on ? Gold : Panel;
            _diffBtns[i].Foreground = on ? Brushes.Black : Muted;
            _diffBtns[i].FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    /// True if the area is locked (red) at the viewed difficulty. Mirrors the
    /// DLL's ground truth; when no lock data exists (plain standalone / not loaded)
    /// every area reads as open.
    private bool IsLocked(int areaId)
        => _haveLockData && _zoneLocked[_diff].Contains(areaId);

    /// Every check that belongs to an area at the viewed difficulty (kills, hunts,
    /// area-entry/"connecting" quests, waypoints, story), with its checked state.
    /// A check "exists" if d2_locations.json has its id — that's the canonical
    /// full set, so ALL quest types show, not just the ones the run's MISSING:
    /// universe happened to deliver.
    private List<(string Name, bool Done)> ChecksFor(int areaId)
    {
        var list = new List<(string, bool)>();
        foreach (var kv in D2QuestArea)
        {
            if (kv.Value != areaId) continue;
            long locId = 42000 + kv.Key + _diff * 1000;
            if (!_locNames.TryGetValue(locId, out var name)) continue;     // not a real check here/diff
            list.Add((name, _checkedIds.Contains(locId)));
        }
        return list.OrderBy(t => t.Item2).ThenBy(t => t.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── Area list (left) — the full catalogue, grouped by act ────────────────

    private void RebuildAreaList()
    {
        int keep = _selectedLevelId;
        _areaList.Items.Clear();
        int curAct = -1;
        foreach (var id in D2Cat.Keys.OrderBy(k => D2Cat[k].Act).ThenBy(k => k))
        {
            var (name, act) = D2Cat[id];
            if (act != curAct)
            {
                curAct = act;
                _areaList.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock
                    {
                        Text = $"ACT {act}", Foreground = Muted, FontWeight = FontWeights.Bold,
                        FontSize = 10, Margin = new Thickness(2, 8, 0, 2),
                    },
                    IsHitTestVisible = false, Focusable = false, Padding = new Thickness(8, 0, 0, 0),
                });
            }

            bool locked = IsLocked(id);
            var checks = ChecksFor(id);
            int done = checks.Count(c => c.Done), total = checks.Count;
            bool here = _player?.LevelId == id;

            var row = new DockPanel { LastChildFill = true };
            var dot = new Ellipse { Width = 8, Height = 8, Fill = locked ? LockedRed : OpenGreen,
                                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            if (total > 0)
            {
                var prog = new TextBlock
                {
                    Text = $"{done}/{total}", Foreground = done >= total && total > 0 ? CheckDone : Muted,
                    FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
                };
                DockPanel.SetDock(prog, Dock.Right);
                row.Children.Add(prog);
            }
            row.Children.Add(new TextBlock
            {
                Text = (here ? "▸ " : "") + name,
                Foreground = locked ? LockedRed : Brushes.White,
                FontWeight = here ? FontWeights.Bold : FontWeights.Normal,
                FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });

            _areaList.Items.Add(new ListBoxItem
            {
                Content = row, Tag = id, Foreground = Brushes.White,
                Padding = new Thickness(8, 2, 6, 2),
                Background = id == keep ? SelBg : Brushes.Transparent,
            });
        }
        // Restore selection highlight without re-entrancy.
        foreach (var it in _areaList.Items)
            if (it is ListBoxItem li && li.Tag is int t && t == keep) { _areaList.SelectedItem = li; break; }
    }

    private void SelectArea(int levelId)
    {
        _selectedLevelId = levelId;
        foreach (var it in _areaList.Items)
            if (it is ListBoxItem li && li.Tag is int t)
                li.Background = t == levelId ? SelBg : Brushes.Transparent;

        DrawCollision();
        DrawOverlay();
        DrawInfo();
    }

    // ── Map rendering (collision + overlay) ──────────────────────────────────

    private D2MapArea? CurrentMap
        => _world.Areas.TryGetValue(_selectedLevelId, out var a) ? a : null;

    private void DrawCollision()
    {
        var a = CurrentMap;
        if (a?.Walkable == null || a.Width <= 0 || a.Height <= 0)
        {
            _mapImage.Source = null;
            _overlay.Children.Clear();
            _emptyHint.Text = D2Cat.TryGetValue(_selectedLevelId, out var c)
                ? $"{c.Name}\n\nNot explored yet —\nenter this area in-game to reveal the map."
                : "Not explored yet.";
            _emptyHint.Visibility = Visibility.Visible;
            return;
        }
        _emptyHint.Visibility = Visibility.Collapsed;

        int w = a.Width, h = a.Height;
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        var px  = new byte[w * h * 4];
        var walk  = a.Walkable;
        var known = a.Known;
        for (int i = 0; i < w * h; i++)
        {
            Color cc = (walk  != null && walk[i])  ? FloorColor
                     : (known != null && known[i]) ? DimColor
                     :                                VoidColor;
            int o = i * 4;
            px[o + 0] = cc.B; px[o + 1] = cc.G; px[o + 2] = cc.R; px[o + 3] = 0xFF;
        }
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        _mapImage.Source = bmp;
        _mapStack.Width  = w;
        _mapStack.Height = h;
        RenderOptions.SetBitmapScalingMode(_mapImage, BitmapScalingMode.NearestNeighbor);
    }

    private void DrawOverlay()
    {
        var a = CurrentMap;
        _overlay.Children.Clear();
        if (a == null) return;
        _overlay.Width  = a.Width;
        _overlay.Height = a.Height;

        foreach (var e in a.Exits)
        {
            bool tlock = IsLocked(e.TargetLevelId);
            AddMarker(e.X - a.OriginX, e.Y - a.OriginY, tlock ? LockedRed : OpenGreen, 3.6,
                      $"→ {e.TargetName}{(tlock ? " (locked)" : "")}", square: true);
        }

        if (_showMarkers)
            foreach (var p in a.Pois)
                AddMarker(p.X - a.OriginX, p.Y - a.OriginY, MarkerBrush(p.Kind), 2.2, p.Kind);

        if (_player != null && _player.LevelId == a.LevelId)
        {
            double dx = _player.X - a.OriginX, dy = _player.Y - a.OriginY;
            var d = new Ellipse { Width = 5, Height = 5, Fill = PlayerDot, Stroke = Brushes.White, StrokeThickness = 0.5 };
            Canvas.SetLeft(d, dx - 2.5);
            Canvas.SetTop(d, dy - 2.5);
            _overlay.Children.Add(d);
        }
    }

    private void AddMarker(double x, double y, Brush fill, double r, string tip, bool square = false)
    {
        FrameworkElement m = square
            ? new Rectangle { Width = r * 2, Height = r * 2, Fill = fill }
            : new Ellipse   { Width = r * 2, Height = r * 2, Fill = fill };
        m.ToolTip = tip;
        Canvas.SetLeft(m, x - r);
        Canvas.SetTop(m, y - r);
        _overlay.Children.Add(m);
    }

    private static Brush MarkerBrush(string kind) => kind switch
    {
        "Shrine"   => new SolidColorBrush(Color.FromRgb(0x55, 0xC8, 0xFF)),
        "Chest"    => new SolidColorBrush(Color.FromRgb(0xE6, 0xB8, 0x4C)),
        "Waypoint" => new SolidColorBrush(Color.FromRgb(0x70, 0x90, 0xFF)),
        "Barrel"   => new SolidColorBrush(Color.FromRgb(0xC8, 0x80, 0x40)),
        "Urn"      => new SolidColorBrush(Color.FromRgb(0xA8, 0x88, 0x58)),
        "Well"     => new SolidColorBrush(Color.FromRgb(0x60, 0xB8, 0xC8)),
        _          => new SolidColorBrush(Color.FromRgb(0xC0, 0xA8, 0x70)),
    };

    // ── Info panel (right) — lock state + gate + checklist ───────────────────

    private void DrawInfo()
    {
        _info.Children.Clear();
        if (!D2Cat.TryGetValue(_selectedLevelId, out var cat)) return;

        _info.Children.Add(Header(cat.Name));

        bool locked = IsLocked(_selectedLevelId);
        if (_haveLockData)
            _info.Children.Add(Note(locked ? "🔴 Locked — no access yet" : "🟢 Open — accessible",
                                    locked ? LockedRed : OpenGreen));
        if (locked)
        {
            string gate = D2GateHint(_selectedLevelId);
            if (gate.Length > 0) _info.Children.Add(Body(gate, Muted));
        }

        var checks = ChecksFor(_selectedLevelId);
        int done = checks.Count(c => c.Done);
        _info.Children.Add(Note($"Checks: {done} / {checks.Count}",
                                checks.Count > 0 && done >= checks.Count ? CheckDone : Muted));

        if (CurrentMap == null)
            _info.Children.Add(Body("Map not revealed — enter this area in-game.", Muted));

        if (checks.Count > 0)
        {
            _info.Children.Add(SubHeader("Checklist"));
            foreach (var c in checks)
                _info.Children.Add(Body((c.Done ? "✔ " : "▢ ") + c.Name, c.Done ? CheckDone : Brushes.White));
        }
        else
        {
            _info.Children.Add(Body("No tracked checks in this area.", Muted));
        }

        // Connections discovered by walking between areas (entrance/exit dots).
        var map = CurrentMap;
        if (map != null && map.Exits.Count > 0)
        {
            _info.Children.Add(SubHeader("Leads to"));
            foreach (var e in map.Exits.GroupBy(e => e.TargetLevelId).Select(g => g.First())
                                       .OrderBy(e => e.TargetName, StringComparer.OrdinalIgnoreCase))
            {
                bool tlock = IsLocked(e.TargetLevelId);
                _info.Children.Add(Body((tlock ? "🔒 " : "→ ") + e.TargetName, tlock ? LockedRed : Brushes.White));
            }
        }
    }

    /// Which gate/region unlocks a locked area (from the apworld's region map).
    private static string D2GateHint(int areaId)
    {
        if (!D2AreaRegion.TryGetValue(areaId, out var ar)) return "";
        if (ar.Region <= 1) return "Opens once you reach Act " + ar.Act + ".";
        return $"Needs Act {ar.Act} Gate {ar.Region - 1} (zone key).";
    }

    // ── Tiny UI helpers ──────────────────────────────────────────────────────
    private static TextBlock Header(string t) => new()
    { Text = t, Foreground = Gold, FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
    private static TextBlock SubHeader(string t) => new()
    { Text = t.ToUpperInvariant(), Foreground = Muted, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4) };
    private static TextBlock Body(string t, Brush? fg = null) => new()
    { Text = t, Foreground = fg ?? Brushes.White, FontSize = 13, Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap };
    private static TextBlock Note(string t, Brush fg) => new()
    { Text = t, Foreground = fg, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };

    // ── DLL map-export parsing (collision + room bounds + objects) ────────────

    private static Dictionary<int, string>? s_objCat;

    private static void EnsureObjCat(string mapDir)
    {
        if (s_objCat != null) return;
        var map = new Dictionary<int, string>();
        try
        {
            string gameDir = Path.GetFullPath(Path.Combine(mapDir, "..", ".."));
            string objTxt  = Path.Combine(gameDir, "data", "global", "excel", "objects.txt");
            if (File.Exists(objTxt))
            {
                var lines = File.ReadAllLines(objTxt);
                if (lines.Length > 1)
                {
                    var hdr = lines[0].Split('\t');
                    int ni = Array.IndexOf(hdr, "Name"); if (ni < 0) ni = 0;
                    for (int row = 1; row < lines.Length; row++)
                    {
                        var cells = lines[row].Split('\t');
                        if (cells.Length <= ni) continue;
                        string? cat = CategorizeObject(cells[ni]);
                        if (cat != null) map[row - 1] = cat;   // txtFileNo = data-row index (0-based)
                    }
                }
            }
        }
        catch { /* no objects.txt → no object markers */ }
        s_objCat = map;
    }

    /// Keep only interactable objects worth a marker; decorations (torches, fires,
    /// ambient sound, dummies) return null = skipped, so the map stays clean.
    private static string? CategorizeObject(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("dummy") || n.Contains("torch") || n.Contains("fire") ||
            n.Contains("brazier") || n.Contains("ambient") || n.Contains("light")) return null;
        if (n.Contains("shrine"))   return "Shrine";
        if (n.Contains("waypoint")) return "Waypoint";
        if (n.Contains("chest"))    return "Chest";
        if (n.Contains("barrel"))   return "Barrel";
        if (n.Contains("urn") || n.Contains("jar") || n.Contains("coffin")) return "Urn";
        if (n.Contains("stash"))    return "Stash";
        if (n.Contains("well"))     return "Well";
        if (n.Contains("cairn") || n.Contains("tome") || n.Contains("portal")) return "Special";
        return null;
    }

    private static D2MapWorld BuildWorldFromDir(string dir)
    {
        var world = new D2MapWorld();
        foreach (var path in Directory.GetFiles(dir, "level_*.map"))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (!stem.StartsWith("level_", StringComparison.Ordinal)) continue;
            if (!int.TryParse(stem.AsSpan(6), out int levelId)) continue;

            var roomsC = new List<(int px, int py, int sx, int sy, byte[] bits)>();
            var roomsR = new List<(int px, int py, int sx, int sy)>();
            var objs   = new List<(int id, int px, int py)>();
            var exits  = new List<(int x, int y, int target)>();
            string[] lines;
            try { lines = File.ReadAllLines(path); } catch { continue; }
            foreach (var line in lines)
            {
                var p = line.Split(',');
                if (p.Length < 2) continue;
                if (p[0] == "O")
                {
                    if (p.Length >= 4 && int.TryParse(p[1], out int oid) &&
                        int.TryParse(p[2], out int opx) && int.TryParse(p[3], out int opy))
                        objs.Add((oid, opx, opy));
                    continue;
                }
                if (p[0] == "X")                                  // X,x,y,targetLevelId (crossing)
                {
                    if (p.Length >= 4 && int.TryParse(p[1], out int ex) &&
                        int.TryParse(p[2], out int ey) && int.TryParse(p[3], out int et))
                        exits.Add((ex, ey, et));
                    continue;
                }
                if (p.Length < 6) continue;
                if (!int.TryParse(p[1], out int px) || !int.TryParse(p[2], out int py) ||
                    !int.TryParse(p[3], out int sx) || !int.TryParse(p[4], out int sy)) continue;
                if (sx <= 0 || sy <= 0 || sx > 2048 || sy > 2048) continue;
                if      (p[0] == "C") roomsC.Add((px, py, sx, sy, HexToBytes(p[5])));
                else if (p[0] == "R") roomsR.Add((px, py, sx, sy));
            }
            if (roomsC.Count == 0 && roomsR.Count == 0) continue;

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            void Extend(int px, int py, int sx, int sy)
            {
                if (px < minX) minX = px;
                if (py < minY) minY = py;
                if (px + sx > maxX) maxX = px + sx;
                if (py + sy > maxY) maxY = py + sy;
            }
            foreach (var r in roomsC) Extend(r.px, r.py, r.sx, r.sy);
            foreach (var r in roomsR) Extend(r.px, r.py, r.sx, r.sy);
            int w = maxX - minX, h = maxY - minY;
            if (w <= 0 || h <= 0 || w > 2048 || h > 2048) continue;

            var walk = new bool[w * h];
            var coll = new bool[w * h];
            var rect = new bool[w * h];
            foreach (var r in roomsC)
            {
                for (int cy = 0; cy < r.sy; cy++)
                for (int cx = 0; cx < r.sx; cx++)
                {
                    int gx = r.px - minX + cx, gy = r.py - minY + cy;
                    if (gx < 0 || gy < 0 || gx >= w || gy >= h) continue;
                    coll[gy * w + gx] = true;
                    int idx = cy * r.sx + cx;
                    if ((idx >> 3) < r.bits.Length && (r.bits[idx >> 3] & (1 << (idx & 7))) != 0)
                        walk[gy * w + gx] = true;
                }
            }
            foreach (var r in roomsR)
            {
                for (int cy = 0; cy < r.sy; cy++)
                for (int cx = 0; cx < r.sx; cx++)
                {
                    int gx = r.px - minX + cx, gy = r.py - minY + cy;
                    if (gx >= 0 && gy >= 0 && gx < w && gy < h) rect[gy * w + gx] = true;
                }
            }
            var known = new bool[w * h];
            for (int i = 0; i < w * h; i++) known[i] = rect[i] && !coll[i];

            var area = new D2MapArea
            {
                LevelId = levelId, Name = D2LevelName(levelId), Width = w, Height = h,
                Act = D2Cat.TryGetValue(levelId, out var c2) ? c2.Act : 0,
                OriginX = minX, OriginY = minY, Walkable = walk, Known = known,
            };
            if (s_objCat != null)
                foreach (var (id, opx, opy) in objs)
                    if (s_objCat.TryGetValue(id, out var cat))
                        area.Pois.Add(new D2MapPoi { Kind = cat, Label = cat, X = opx, Y = opy });
            foreach (var (ex, ey, et) in exits)
                area.Exits.Add(new D2MapExit { X = ex, Y = ey, TargetLevelId = et, TargetName = D2LevelName(et) });
            world.Areas[levelId] = area;
        }
        return world;
    }

    private static byte[] HexToBytes(string hex)
    {
        hex = hex.Trim();
        int n = hex.Length / 2;
        var b = new byte[n];
        for (int i = 0; i < n; i++)
            byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i]);
        return b;
    }

    private static string D2LevelName(int id)
        => D2Cat.TryGetValue(id, out var c) ? c.Name : $"Level {id}";

    // ── Static D2 data (catalogue · check→area · region gating) ───────────────

    /// Every D2 area: id → (canonical name, act). The full list the left panel
    /// shows even before you enter — keyed by the in-game levelId (same id space
    /// the DLL streams and the apworld gates on).
    private static readonly Dictionary<int, (string Name, int Act)> D2Cat = new()
    {
        [1]=("Rogue Encampment",1),[2]=("Blood Moor",1),[3]=("Cold Plains",1),[4]=("Stony Field",1),
        [5]=("Dark Wood",1),[6]=("Black Marsh",1),[7]=("Tamoe Highland",1),[8]=("Den of Evil",1),
        [9]=("Cave Level 1",1),[10]=("Underground Passage Level 1",1),[11]=("Hole Level 1",1),
        [12]=("Hole Level 2",1),[13]=("Cave Level 2",1),[14]=("Underground Passage Level 2",1),
        [15]=("Pit Level 1",1),[16]=("Pit Level 2",1),[17]=("Burial Grounds",1),[18]=("The Crypt",1),
        [19]=("The Mausoleum",1),[20]=("Forgotten Tower",1),[21]=("Tower Cellar Level 1",1),
        [22]=("Tower Cellar Level 2",1),[23]=("Tower Cellar Level 3",1),[24]=("Tower Cellar Level 4",1),
        [25]=("Tower Cellar Level 5",1),[26]=("Monastery Gate",1),[27]=("Outer Cloister",1),
        [28]=("Barracks",1),[29]=("Jail Level 1",1),[30]=("Jail Level 2",1),[31]=("Jail Level 3",1),
        [32]=("Inner Cloister",1),[33]=("Cathedral",1),[34]=("Catacombs Level 1",1),
        [35]=("Catacombs Level 2",1),[36]=("Catacombs Level 3",1),[37]=("Catacombs Level 4",1),
        [38]=("Tristram",1),[39]=("Moo Moo Farm",1),
        [40]=("Lut Gholein",2),[41]=("Rocky Waste",2),[42]=("Dry Hills",2),[43]=("Far Oasis",2),
        [44]=("Lost City",2),[45]=("Valley of Snakes",2),[46]=("Canyon of the Magi",2),
        [47]=("Sewers Level 1",2),[48]=("Sewers Level 2",2),[49]=("Sewers Level 3",2),
        [50]=("Harem Level 1",2),[51]=("Harem Level 2",2),[52]=("Palace Cellar Level 1",2),
        [53]=("Palace Cellar Level 2",2),[54]=("Palace Cellar Level 3",2),[55]=("Stony Tomb Level 1",2),
        [56]=("Halls of the Dead Level 1",2),[57]=("Halls of the Dead Level 2",2),
        [58]=("Claw Viper Temple Level 1",2),[59]=("Stony Tomb Level 2",2),
        [60]=("Halls of the Dead Level 3",2),[61]=("Claw Viper Temple Level 2",2),
        [62]=("Maggot Lair Level 1",2),[63]=("Maggot Lair Level 2",2),[64]=("Maggot Lair Level 3",2),
        [65]=("Ancient Tunnels",2),[66]=("Tal Rasha's Tomb 1",2),[67]=("Tal Rasha's Tomb 2",2),
        [68]=("Tal Rasha's Tomb 3",2),[69]=("Tal Rasha's Tomb 4",2),[70]=("Tal Rasha's Tomb 5",2),
        [71]=("Tal Rasha's Tomb 6",2),[72]=("Tal Rasha's Tomb 7",2),[73]=("Duriel's Lair",2),
        [74]=("Arcane Sanctuary",2),
        [75]=("Kurast Docks",3),[76]=("Spider Forest",3),[77]=("Great Marsh",3),[78]=("Flayer Jungle",3),
        [79]=("Lower Kurast",3),[80]=("Kurast Bazaar",3),[81]=("Upper Kurast",3),[82]=("Kurast Causeway",3),
        [83]=("Travincal",3),[84]=("Spider Cave",3),[85]=("Spider Cavern",3),[86]=("Swampy Pit Level 1",3),
        [87]=("Swampy Pit Level 2",3),[88]=("Flayer Dungeon Level 1",3),[89]=("Flayer Dungeon Level 2",3),
        [90]=("Swampy Pit Level 3",3),[91]=("Flayer Dungeon Level 3",3),[92]=("Sewers Level 1",3),
        [93]=("Sewers Level 2",3),[94]=("Ruined Temple",3),[95]=("Disused Fane",3),
        [96]=("Forgotten Reliquary",3),[97]=("Forgotten Temple",3),[98]=("Ruined Fane",3),
        [99]=("Disused Reliquary",3),[100]=("Durance of Hate Level 1",3),[101]=("Durance of Hate Level 2",3),
        [102]=("Durance of Hate Level 3",3),
        [103]=("The Pandemonium Fortress",4),[104]=("Outer Steppes",4),[105]=("Plains of Despair",4),
        [106]=("City of the Damned",4),[107]=("River of Flame",4),[108]=("Chaos Sanctuary",4),
        [109]=("Harrogath",5),[110]=("Bloody Foothills",5),[111]=("Frigid Highlands",5),
        [112]=("Arreat Plateau",5),[113]=("Crystalline Passage",5),[114]=("Frozen River",5),
        [115]=("Glacial Trail",5),[116]=("Drifter Cavern",5),[117]=("Frozen Tundra",5),
        [118]=("The Ancients' Way",5),[119]=("Icy Cellar",5),[120]=("Arreat Summit",5),
        [121]=("Nihlathak's Temple",5),[122]=("Halls of Anguish",5),[123]=("Halls of Pain",5),
        [124]=("Halls of Vaught",5),[125]=("Abaddon",5),[126]=("Pit of Acheron",5),
        [127]=("Infernal Pit",5),[128]=("Worldstone Keep Level 1",5),[129]=("Worldstone Keep Level 2",5),
        [130]=("Worldstone Keep Level 3",5),[131]=("Throne of Destruction",5),
        [132]=("The Worldstone Chamber",5),
    };

    /// quest/check id → area id (mirrors the apworld's QUEST_ID_TO_AREA). The
    /// per-area checklist is built by inverting this against d2_locations.json.
    private static readonly Dictionary<int, int> D2QuestArea = new()
    {
        [1]=8, [2]=17, [3]=28, [4]=5, [5]=25, [6]=37, [7]=8, [8]=3, [9]=18, [10]=2, [11]=3,
        [12]=4, [13]=5, [14]=6, [15]=7, [16]=8, [17]=9, [18]=10, [19]=17, [20]=18, [21]=19,
        [22]=22, [23]=23, [24]=24, [25]=25, [26]=26, [27]=27, [28]=28, [29]=29, [30]=30,
        [31]=31, [32]=32, [33]=33, [34]=34, [35]=35, [36]=36, [37]=38, [38]=13, [39]=14,
        [40]=2, [41]=3, [42]=4, [43]=5, [44]=6, [45]=7, [46]=8, [47]=38, [48]=34, [49]=21,
        [50]=3, [51]=4, [52]=5, [53]=6, [54]=27, [55]=30, [56]=28, [57]=35, [59]=11,
        [60]=12, [70]=9, [71]=4, [72]=5, [73]=38, [74]=25, [75]=34, [77]=33, [78]=1,
        [79]=1, [80]=28, [81]=1, [82]=1, [83]=1, [101]=49, [102]=60, [103]=61, [104]=74,
        [105]=74, [106]=73, [110]=41, [111]=42, [112]=43, [113]=44, [114]=45, [117]=56,
        [118]=57, [119]=60, [120]=62, [121]=63, [122]=64, [123]=65, [124]=74, [125]=52,
        [126]=53, [127]=54, [128]=46, [129]=55, [140]=41, [141]=42, [142]=43, [143]=44,
        [144]=74, [151]=42, [152]=57, [153]=43, [154]=44, [155]=52, [156]=74, [157]=46,
        [170]=49, [171]=42, [172]=61, [173]=43, [174]=52, [175]=64, [176]=44, [177]=44,
        [178]=74, [179]=46, [180]=1, [181]=1, [182]=1, [183]=1, [184]=1, [201]=95,
        [202]=83, [203]=83, [204]=80, [205]=83, [206]=102, [210]=76, [211]=77, [212]=78,
        [213]=79, [214]=80, [215]=81, [216]=83, [217]=84, [218]=88, [219]=89, [222]=100,
        [223]=101, [224]=82, [240]=76, [241]=78, [242]=80, [243]=83, [244]=100, [250]=76,
        [251]=77, [252]=78, [253]=79, [254]=80, [255]=81, [256]=83, [257]=101, [271]=84,
        [272]=88, [273]=79, [274]=94, [275]=92, [276]=83, [277]=83, [278]=83, [279]=100,
        [280]=101, [281]=101, [282]=1, [283]=1, [284]=1, [285]=1, [301]=105, [302]=107,
        [303]=108, [310]=104, [311]=105, [312]=106, [313]=107, [314]=108, [340]=104,
        [341]=105, [342]=106, [343]=107, [344]=108, [350]=106, [351]=107, [370]=104,
        [371]=105, [372]=106, [373]=107, [374]=108, [375]=108, [376]=108, [401]=110,
        [402]=112, [403]=113, [404]=124, [405]=120, [406]=132, [410]=110, [411]=111,
        [412]=112, [413]=113, [414]=118, [415]=119, [416]=117, [417]=122, [418]=123,
        [419]=124, [420]=128, [421]=129, [422]=130, [423]=131, [440]=110, [441]=111,
        [442]=112, [443]=113, [444]=128, [450]=111, [451]=112, [452]=113, [454]=123,
        [455]=115, [456]=117, [457]=129, [470]=110, [471]=111, [472]=112, [473]=118,
        [474]=119, [475]=121, [476]=115, [477]=129,
    };

    /// area id → (act, region number). Region 1 is always-open within a reached
    /// act; region N (>1) needs that act's Gate N-1 key. Mirrors the apworld's
    /// ACT_REGIONS — used only for the "needs Gate X" hint.
    private static readonly Dictionary<int, (int Act, int Region)> D2AreaRegion = BuildAreaRegion();

    private static Dictionary<int, (int, int)> BuildAreaRegion()
    {
        var d = new Dictionary<int, (int, int)>();
        void R(int act, int region, params int[] zones) { foreach (var z in zones) d[z] = (act, region); }
        R(1,1, 1,2,3,8,9,13,17,18,19);  R(1,2, 4,5,10,14,38);
        R(1,3, 6,7,11,12,15,16,20,21,22,23,24,25,26);  R(1,4, 27,28,29,30,31,32);  R(1,5, 33,34,35,36,37);
        R(2,1, 40,41,47,48,49,50);  R(2,2, 42,51,55,56,57,59,60);  R(2,3, 43,52,53,54,62,63,64);
        R(2,4, 44,45,58,61,65,74);  R(2,5, 46,66,67,68,69,70,71,72,73);
        R(3,1, 75,76,77,84,85);  R(3,2, 78,79,86,87,88,89,90,91);  R(3,3, 80,81,92,93,94,95,96,97);
        R(3,4, 82,83,98,99);  R(3,5, 100,101,102);
        R(4,1, 103,104,105);  R(4,2, 106,107);  R(4,3, 108);
        R(5,1, 109,110,111);  R(5,2, 112,113,114);  R(5,3, 115,116,117,118,119,120);
        R(5,4, 128,129);  R(5,5, 130,131,132);
        return d;
    }
}
