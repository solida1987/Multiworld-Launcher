using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;
using LauncherV2.Core.AchievementSystem;
using LauncherV2.Core.Archipelago;
using LauncherV2.Core.Trackers;
using LauncherV2.UI.Dialogs;

namespace LauncherV2.UI.Controls;

/// THIS SEED — everything the seed on screen adds up to, in the right column.
///
/// ⚠ The Join tab showed three cards with a Play button and nothing else. A
/// player sitting on a three-game seed could not see how far along any of them
/// was, how many checks were left, or how long they had spent — the figures
/// existed, in the seed's own spoiler and in the play log, and no surface read
/// them back. Twice I built this somewhere else (the per-game Progression tab)
/// which is not where the question is asked. It is asked here, looking at the
/// seed you are about to play.
public partial class JoinPanel
{
    private static Brush Tint(byte r, byte g, byte b) =>
        new SolidColorBrush(Color.FromRgb(r, g, b));

    private static string Clock(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m" : $"{(int)t.TotalSeconds}s";

    /// What one slot has done, live if it is running and from disk if it is not.
    ///
    /// The live session wins because it is newer by definition; the stored row
    /// is what the same session wrote the last time it changed.
    internal (int Done, int Total, int In, int Out, TimeSpan Played, bool Live)
        SlotFigures(SeedInfo seed, SeedSlot slot)
    {
        int total = SeedSpoiler.For(seed).BySlot.TryGetValue(slot.Name, out int t) ? t : 0;

        var live = ApJoinSession.All.FirstOrDefault(
            s => s.SeedId == seed.Id
              && string.Equals(s.SlotName, slot.Name, StringComparison.Ordinal));
        var saved = SeedProgressStore.For(seed.Id, slot.Name);

        // ⚠ The spoiler is the honest denominator, and it is known before any
        // connection has ever been made. The server's own count only arrives
        // once connected, and is preferred then because a seed can be rolled
        // with settings the spoiler header rounds.
        if (live != null)
        {
            if (live.LocationTotal > 0) total = live.LocationTotal;
            return (live.LocationsDone, total, live.ItemsReceived, live.ChecksSent,
                    DateTimeOffset.Now - live.StartedAt
                        + TimeSpan.FromSeconds(saved?.Seconds ?? 0), true);
        }
        // ⭐ A stored total came from the SERVER and outranks the spoiler
        // header, which counts what the world declares rather than what is in
        // play. Measured: 288 in the header, 278 on the server, for the same
        // slot.
        if (saved != null)
            return (saved.Done, saved.Total > 0 ? saved.Total : total, saved.ItemsIn,
                    saved.ItemsOut, TimeSpan.FromSeconds(saved.Seconds), false);

        return (0, total, 0, 0, TimeSpan.Zero, false);
    }

    /// The block that goes on every slot card, under the status line.
    internal UIElement SlotFigureBlock(SeedInfo seed, SeedSlot slot)
    {
        var (done, total, inn, outt, played, live) = SlotFigures(seed, slot);
        var col = new StackPanel { Margin = new Thickness(0, 2, 0, 9) };

        // The bar. Only when a denominator is actually known — a full-width
        // bar over an unknown total is a claim, not a picture.
        if (total > 0)
        {
            col.Children.Add(new TextBlock
            {
                Text = $"{done} / {total} checks   ·   {(int)Math.Round(100.0 * done / total)}%",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = Tint(0xE8, 0xA0, 0x18),
                Margin = new Thickness(0, 0, 0, 5),
            });
            var track = new Border
            {
                Height = 4, CornerRadius = new CornerRadius(2),
                Background = Tint(0x22, 0x27, 0x38),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var fill = new Border
            {
                Height = 4, CornerRadius = new CornerRadius(2),
                Background = Tint(0xE8, 0xA0, 0x18),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            track.Child = fill;
            track.SizeChanged += (_, _) =>
                fill.Width = Math.Max(0, track.ActualWidth * done / (double)total);
            col.Children.Add(track);
        }
        else
        {
            col.Children.Add(new TextBlock
            {
                // Said plainly. "0 / 0" would read as a finished game.
                Text = "Check count unknown — this seed shipped without a spoiler.",
                FontSize = 10.5, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("BrushText"),
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        col.Children.Add(new TextBlock
        {
            Text = $"↓ {inn} in   ↑ {outt} out   ·   {Clock(played)} played"
                 + (live ? "   ·   open now" : ""),
            FontSize = 10.5,
            Opacity = 0.75,
            Foreground = (Brush)FindResource("BrushText"),
        });
        return col;
    }

    // ---------------------------------------------------- somebody else's server

    /// A slot on a server we do not host. Same shape as SlotFigures, but the
    /// totals come from the SERVER rather than from a spoiler we do not have.
    internal (int Done, int Total, int In, int Out, TimeSpan Played, bool Live)
        ExternalFigures(ExternalSlot ext)
    {
        var live = ApJoinSession.All.FirstOrDefault(
            s => string.Equals(s.SlotName, ext.SlotName, StringComparison.Ordinal)
              && s.SeedId == ext.DisplayAddress);
        var saved = SeedProgressStore.For(ext.DisplayAddress, ext.SlotName);

        if (live != null)
            return (live.LocationsDone, live.LocationTotal, live.ItemsReceived,
                    live.ChecksSent,
                    DateTimeOffset.Now - live.StartedAt
                        + TimeSpan.FromSeconds(saved?.Seconds ?? 0), true);
        if (saved != null)
            return (saved.Done, saved.Total, saved.ItemsIn, saved.ItemsOut,
                    TimeSpan.FromSeconds(saved.Seconds), false);
        return (0, 0, 0, 0, TimeSpan.Zero, false);
    }

    internal UIElement ExternalFigureBlock(ExternalSlot ext)
    {
        var (done, total, inn, outt, played, live) = ExternalFigures(ext);
        var col = new StackPanel { Margin = new Thickness(0, 2, 0, 9) };

        if (total > 0)
        {
            col.Children.Add(new TextBlock
            {
                Text = $"{done} / {total} checks   ·   {(int)Math.Round(100.0 * done / total)}%",
                FontSize = 11.5, FontWeight = FontWeights.Bold,
                Foreground = Tint(0xE8, 0xA0, 0x18),
                Margin = new Thickness(0, 0, 0, 5),
            });
            var track = new Border
            {
                Height = 4, CornerRadius = new CornerRadius(2),
                Background = Tint(0x22, 0x27, 0x38),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var fill = new Border
            {
                Height = 4, CornerRadius = new CornerRadius(2),
                Background = Tint(0xE8, 0xA0, 0x18),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            track.Child = fill;
            track.SizeChanged += (_, _) =>
                fill.Width = Math.Max(0, track.ActualWidth * done / (double)total);
            col.Children.Add(track);
        }
        else
        {
            col.Children.Add(new TextBlock
            {
                Text = "Asking the server how big this slot is…",
                FontSize = 10.5, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("BrushText"),
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        col.Children.Add(new TextBlock
        {
            Text = $"↓ {inn} in   ↑ {outt} out   ·   {Clock(played)} played"
                 + (live ? "   ·   open now" : ""),
            FontSize = 10.5, Opacity = 0.75,
            Foreground = (Brush)FindResource("BrushText"),
        });
        return col;
    }

    /// Ask the server how far each of its slots has got.
    ///
    /// ⚠ There is no spoiler for somebody else's session, so the only source
    /// is the server itself — and it will tell us: the Connected packet carries
    /// checked_locations and missing_locations for the slot that logged in, and
    /// ApSlotProbe already logs in as a Tracker without playing anything.
    ///
    /// Rate-limited to once every two minutes per slot. The tab redraws often,
    /// and a login per redraw would be rude to a stranger's server.
    private static readonly Dictionary<string, DateTimeOffset> _probedAt = new();

    /// The same question, asked of a seed WE host.
    ///
    /// ⚠ The spoiler and the server do not always agree. Measured on Marco's
    /// own seed: the spoiler header says A Link to the Past has 288 locations
    /// and the server's missing_locations says 278 — the header counts what
    /// the world declares, the server counts what is actually in play once
    /// exclusions and event locations are settled. The server is the one the
    /// player's own tracker agrees with, so when it is up we take its number
    /// and the spoiler goes back to being the pre-connect estimate it is.
    private async Task RefreshSeedFiguresAsync(SeedInfo seed)
    {
        var host = ApServerHost.For(seed);
        if (host is not { IsRunning: true }) return;
        string address = "127.0.0.1:" + host.Port;

        // ⚠ Set the moment a probe moves a number, and nothing else. The
        // redraw at the bottom used to be unconditional -- and once the
        // throttle below skips every slot, this method never awaits, so that
        // redraw ran inside RefreshCards's own stack and started this method
        // again. Two crashes, 0xC00000FD, no crash.log. See RefreshCards.
        bool changed = false;

        foreach (var slot in seed.Slots)
        {
            string key = seed.Id + "|" + slot.Name;
            lock (_probedAt)
            {
                if (_probedAt.TryGetValue(key, out var when)
                    && DateTimeOffset.Now - when < TimeSpan.FromMinutes(2)) continue;
                _probedAt[key] = DateTimeOffset.Now;
            }
            if (ApJoinSession.All.Any(s => s.SeedId == seed.Id
                 && string.Equals(s.SlotName, slot.Name, StringComparison.Ordinal))) continue;

            try
            {
                var r = await ApSlotProbe.ResolveGameAsync(address, slot.Name, "")
                                         .ConfigureAwait(true);
                if (r.Total > 0)
                {
                    var had = SeedProgressStore.For(seed.Id, slot.Name);
                    SeedProgressStore.Record(seed.Id, slot.Name, r.Checked, r.Total,
                        had?.ItemsIn ?? 0, had?.ItemsOut ?? 0, 0);
                    changed = true;
                }
            }
            catch { /* our own server not answering is not worth a dialog */ }
        }

        // Posted, never called: it must land in a stack of its own, so a
        // redraw can never be part of the call that started this.
        if (changed) Dispatcher.BeginInvoke(new Action(RefreshCards));
    }

    private async Task RefreshExternalFiguresAsync(ExternalServer server)
    {
        // Same shape, same trap, same fix as the seed sweep above.
        bool changed = false;

        foreach (var ext in ExternalSlotStore.ForServer(server.Id))
        {
            string key = server.Id + "|" + ext.SlotName;
            lock (_probedAt)
            {
                if (_probedAt.TryGetValue(key, out var when)
                    && DateTimeOffset.Now - when < TimeSpan.FromMinutes(2)) continue;
                _probedAt[key] = DateTimeOffset.Now;
            }
            // A slot that is being played reports itself; probing it would open
            // a second login for no reason.
            if (ApJoinSession.All.Any(s =>
                    string.Equals(s.SlotName, ext.SlotName, StringComparison.Ordinal)
                 && s.SeedId == ext.DisplayAddress)) continue;

            try
            {
                var r = await ApSlotProbe.ResolveGameAsync(
                    ext.Address, ext.SlotName, ext.Password).ConfigureAwait(true);
                if (r.Total > 0)
                {
                    var had = SeedProgressStore.For(ext.DisplayAddress, ext.SlotName);
                    SeedProgressStore.Record(ext.DisplayAddress, ext.SlotName,
                        r.Checked, r.Total, had?.ItemsIn ?? 0, had?.ItemsOut ?? 0, 0);
                    changed = true;
                }
            }
            catch { /* an asleep server is not an error worth a dialog */ }
        }

        if (changed) Dispatcher.BeginInvoke(new Action(RefreshCards));
    }

    // ------------------------------------------------------------ the tracker

    /// Trackers we have already looked up, so a redraw does not re-ask.
    private static readonly Dictionary<string, TrackerEntry?> _trackerCache = new();

    /// The tracker button for one slot card, or nothing.
    ///
    /// ⚠ Returns nothing at all for the 647 games with no pack. A disabled
    /// "Open tracker" would read as broken software rather than as a game
    /// nobody has built a tracker for.
    ///
    /// The Join card is the better of the two homes for this: it knows the
    /// host, the port and the slot, so the tracker opens already attached to
    /// the session the player is about to play.
    internal UIElement? TrackerButton(IGamePlugin plugin, string? host, string? slot)
    {
        TrackerEntry? entry;
        lock (_trackerCache)
        {
            if (!_trackerCache.TryGetValue(plugin.GameId, out entry))
            {
                // Not known yet: ask, and redraw when the answer lands. The
                // card must draw now, with or without a tracker.
                _trackerCache[plugin.GameId] = null;
                _ = LookUpTrackerAsync(plugin.GameId);
                return null;
            }
        }
        if (entry == null) return null;
        if (!TrackerConsent.MayOffer) return null;

        bool ready = PopTrackerService.IsInstalled
                  && PopTrackerService.IsPackInstalled(entry.PackageUid);

        // The label is the promise: "Open" when it will open, "Get" when a
        // download has to happen first.
        // "Get" downloads, "Update" replaces the pack with the newer published
        // one and then opens, "Open" opens. Same three promises as the game page.
        var pending = ready ? PopTrackerService.PendingUpdate(entry.PackageUid) : null;
        var b = new Button
        {
            Content = !ready ? "🗺  Get the tracker"
                    : pending != null ? "🗺  Update tracker"
                    : "🗺  Open tracker",
            ToolTip = !ready ? $"Download {entry.PackName}"
                               + (PopTrackerService.IsInstalled ? "" : " and PopTracker itself")
                               + ", then open it"
                    : pending != null
                    ? $"{entry.PackName} {pending.Newest} is published and you have "
                      + $"{pending.Installed} — update it, then open it"
                    : $"Open {entry.PackName} in PopTracker",
            Padding = new Thickness(0, 6, 0, 6),
            Margin = new Thickness(0, 6, 0, 0),
            Style = (Style)FindResource("BtnSecondaryStyle"),
        };
        b.Click += async (_, _) =>
        {
            if (!TrackerConsentDialog.Ask(Window.GetWindow(this), entry)) { RefreshCards(); return; }
            b.IsEnabled = false;
            object? was = b.Content;
            var progress = new Progress<string>(m =>
                b.Content = m.Length > 30 ? m[..30] + "…" : m);
            try
            {
                b.Content = await PopTrackerService.OpenAsync(
                    entry, progress, host, slot, null,
                    plugin.BuildTrackerArtworkAsync);
            }
            finally
            {
                // Long enough to read what happened, then back to a button.
                await Task.Delay(2000);
                b.Content = was;
                b.IsEnabled = true;
                RefreshCards();
            }
        };
        return b;
    }

    private async Task LookUpTrackerAsync(string gameId)
    {
        TrackerEntry? entry;
        try
        {
            entry = await TrackerCatalog.ForGameAsync(gameId);
        }
        catch (Exception)
        {
            // ⚠ A failed lookup is NOT an answer. Caching it as "no tracker"
            // means one moment offline hides the button for the rest of the
            // session, on a game that has a pack. Drop the placeholder so the
            // next redraw asks again.
            lock (_trackerCache) _trackerCache.Remove(gameId);
            return;
        }

        lock (_trackerCache) _trackerCache[gameId] = entry;

        // Posted, not called: this can run inside RefreshCards's own stack
        // when the catalogue answers from cache and the await never yields.
        if (entry != null) Dispatcher.BeginInvoke(new Action(RefreshCards));
    }

    /// The right column: this seed, added up.
    private void RefreshSeedSummary()
    {
        PanelSeedSummary.Children.Clear();
        if (_seed is not { } seed) { PanelSeedSide.Visibility = Visibility.Collapsed; return; }
        PanelSeedSide.Visibility = Visibility.Visible;

        var muted = (Brush)FindResource("BrushMuted");
        var fg = (Brush)FindResource("BrushText");

        void Head(string text) => PanelSeedSummary.Children.Add(new TextBlock
        {
            Text = text, FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        Head("THIS SEED");

        int done = 0, total = 0, inn = 0, outt = 0;
        TimeSpan played = TimeSpan.Zero;
        var rows = new List<(SeedSlot Slot, int Done, int Total, bool Live)>();
        foreach (var slot in seed.Slots)
        {
            var f = SlotFigures(seed, slot);
            done += f.Done; total += f.Total; inn += f.In; outt += f.Out;
            played += f.Played;
            rows.Add((slot, f.Done, f.Total, f.Live));
        }

        void Tile(string label, string value, Brush tint)
        {
            var b = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0x1E, 0x22, 0x33)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(11, 8, 11, 8),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, FontSize = 9.5, Foreground = muted });
            sp.Children.Add(new TextBlock
            { Text = value, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = tint });
            b.Child = sp;
            PanelSeedSummary.Children.Add(b);
        }

        Tile("Checks across every slot",
             total > 0 ? $"{done} / {total}   ({(int)Math.Round(100.0 * done / total)}%)"
                       : $"{done} done",
             Tint(0xE8, 0xA0, 0x18));
        Tile("Still to find", total > 0 ? Math.Max(0, total - done).ToString() : "—", fg);
        Tile("Items received", inn.ToString(), Tint(0x4F, 0xA9, 0x7B));
        Tile("Items sent out", outt.ToString(), Tint(0x4F, 0xA9, 0x7B));
        Tile("Time in this seed", Clock(played), fg);

        // Per slot, smallest useful form: who, how far, and whether it is open.
        PanelSeedSummary.Children.Add(new Border { Height = 10 });
        Head("PER SLOT");
        foreach (var (slot, sDone, sTotal, live) in rows)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7, Margin = new Thickness(0, 5, 7, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Fill = live ? Tint(0x4F, 0xA9, 0x7B) : Tint(0x4A, 0x51, 0x70),
            });
            top.Children.Add(new TextBlock
            {
                Text = slot.Game, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 200,
            });
            row.Children.Add(top);
            row.Children.Add(new TextBlock
            {
                Text = sTotal > 0 ? $"    {sDone} / {sTotal}" : $"    {sDone} checks",
                FontSize = 11, Opacity = 0.7, Foreground = fg,
            });
            PanelSeedSummary.Children.Add(row);
        }

        // ⚠ Said out loud. Every figure above is either live or the last thing
        // a session wrote; a page of numbers with no provenance is how a stale
        // one gets read as current.
        PanelSeedSummary.Children.Add(new TextBlock
        {
            Text = rows.Any(r => r.Live)
                ? "Live figures update while a slot is open."
                : "Nothing is connected — these are from the last time you played.",
            FontSize = 10, Opacity = 0.55, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0), Foreground = fg,
        });
    }
}
