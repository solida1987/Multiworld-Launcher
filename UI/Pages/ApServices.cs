using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using LauncherV2.Core;
using LauncherV2.Core.AchievementSystem;

namespace LauncherV2.UI.Pages;

// The launcher's side of IApServices — thin adapter over the AP client,
// tracker and player table. Dispatcher hops are load-bearing: plugins call
// from their own pipe threads.
internal sealed class ApServices : IApServices
{
    private readonly ApClient    _ap;
    private readonly Dispatcher  _dispatcher;
    private readonly LocationTracker _locations;
    private readonly Func<int, string> _resolvePlayerName;
    private readonly string      _gameId;

    public ApServices(ApClient ap,
                      Dispatcher dispatcher,
                      LocationTracker locations,
                      Func<int, string> resolvePlayerName,
                      string gameId)
    {
        _ap                = ap;
        _dispatcher        = dispatcher;
        _locations         = locations;
        _resolvePlayerName = resolvePlayerName;
        _gameId            = gameId;

        _ap.LocationInfoReceived += OnLocationInfo;
    }

    // Called when the session ends, so the client stops holding this alive.
    public void Detach() => _ap.LocationInfoReceived -= OnLocationInfo;

    private void OnLocationInfo(ApNetworkItem[] items) => LocationsScouted?.Invoke(items);

    // --- Identity ---

    public int OwnSlot => _ap.Slot;

    public JsonElement? SlotData => _ap.SlotData;

    public string? SeedName => _ap.SeedName;

    public string ResolvePlayerName(int slot)
        => _dispatcher.Invoke(() => _resolvePlayerName(slot));

    // --- Locations ---

    public long[] CheckedLocations()
        => _dispatcher.Invoke(() => _locations.GetCheckedIdSet().ToArray());

    public long[] UncheckedLocations() => _dispatcher.Invoke(() =>
    {
        var done = _locations.GetCheckedIdSet();
        return _locations.GetAllIds().Where(id => !done.Contains(id)).ToArray();
    });

    // createAsHint stays 0: a free lookup, never the player's hint points.
    public Task ScoutLocationsAsync(long[] locationIds)
        => _ap.LocationScoutsAsync(locationIds, createAsHint: 0);

    public event Action<ApNetworkItem[]>? LocationsScouted;

    public Task ResyncAsync() => _ap.SyncAsync();

    // --- DeathLink ---

    public bool DeathLinkEnabled => _ap.DeathLinkEnabled;

    public void ReportDeath(string? cause)
    {
        if (!_ap.DeathLinkEnabled) return;
        _ = _ap.SendDeathLinkAsync(
            string.IsNullOrWhiteSpace(cause) ? "died" : cause);

        // Achievement ladder: a death actually shared with the pack, not
        // merely one that happened.
        AchievementStore.Instance.IncrementCounter(
            _gameId, AchievementCounters.DeathsShared);
    }

    // --- Chat ---

    public Task SendSayAsync(string text)
        => string.IsNullOrWhiteSpace(text) ? Task.CompletedTask : _ap.SendSayAsync(text);
}
