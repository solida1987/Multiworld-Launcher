using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LauncherV2.Core;
using LauncherV2.Core.Archipelago;

namespace LauncherV2.UI.Controls;

/// What actually moved in the multiworld, under Join.
///
/// The slot cards have always shown counts — "60 in, 20 out" — and nothing
/// behind them. This is the list those numbers are counting: which item, from
/// whom, to whom, and where it was found. The same shape the game's own
/// session window shows, in the place people sit and watch a run from.
///
/// Every joined session keeps its own history (ApJoinSession.Items), so the
/// feed merges them and puts the newest first. Nothing is stored: it is the
/// live session's own record, and it goes when the session does.
public partial class JoinPanel
{
    private readonly ObservableCollection<TrackedItem> _flow = new();
    private bool _flowBound;

    /// Newest first, capped. A long multiworld runs to thousands of items and
    /// a grid that keeps every one of them turns the tab into a spreadsheet
    /// nobody scrolls to the bottom of.
    private const int FlowMax = 300;

    private void RefreshItemFlow()
    {
        if (!_flowBound)
        {
            GridItemFlow.ItemsSource = _flow;
            _flowBound = true;
        }

        var sessions = ApJoinSession.All;
        if (sessions.Count == 0)
        {
            if (_flow.Count > 0) _flow.Clear();
            SectionItemFlow.Visibility = Visibility.Collapsed;
            return;
        }

        // One list across every joined slot. A player with two games open is
        // watching one multiworld, not two.
        var all = new List<TrackedItem>();
        foreach (var s in sessions)
        {
            try { all.AddRange(Filter(s)); }
            catch (Exception) { /* a session tearing down owes us nothing */ }
        }

        if (all.Count == 0)
        {
            if (_flow.Count > 0) _flow.Clear();
            SectionItemFlow.Visibility = Visibility.Collapsed;
            return;
        }

        all.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        if (all.Count > FlowMax) all.RemoveRange(FlowMax, all.Count - FlowMax);

        // Rebuild only when something changed: the 2 s sweep runs whether or
        // not an item moved, and replacing the rows every time throws away the
        // player's scroll position and any row they had selected.
        if (!SameAs(all))
        {
            _flow.Clear();
            foreach (var e in all) _flow.Add(e);
        }

        int received = 0, sent = 0;
        foreach (var s in sessions)
        {
            received += s.ItemsReceived;
            sent += s.ChecksSent;
        }
        TxtItemFlowCount.Text = $"{received} in · {sent} out";
        SectionItemFlow.Visibility = Visibility.Visible;
    }

    private IEnumerable<TrackedItem> Filter(ApJoinSession s)
    {
        var mode = CmbItemFlow?.SelectedIndex ?? 0;
        int mine = s.Client?.Slot ?? -1;
        foreach (var e in s.Items.All)
        {
            if (mode == 1 && e.ReceiverSlot != mine) continue;   // received by me
            if (mode == 2 && e.SenderSlot != mine) continue;     // sent by me
            yield return e;
        }
    }

    private bool SameAs(List<TrackedItem> fresh)
    {
        if (fresh.Count != _flow.Count) return false;
        for (int i = 0; i < fresh.Count; i++)
            if (!ReferenceEquals(fresh[i], _flow[i])) return false;
        return true;
    }

    private void CmbItemFlow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _flow.Clear();          // the filter changed, so every row is new
        RefreshItemFlow();
    }
}
