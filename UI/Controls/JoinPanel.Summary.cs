using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core.AchievementSystem;
using LauncherV2.Core.Archipelago;

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
        if (saved != null)
            return (saved.Done, total > 0 ? total : saved.Total, saved.ItemsIn,
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
