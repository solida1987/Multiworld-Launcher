using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Archipelago;

// ApJoinSession — one slot of a seed, joined: server ensured, patch in place,
// AP connected, game launched. Several of these can run at once.
//
// WHY THIS EXISTS NEXT TO THE LIBRARY'S OWN PLAY FLOW
// The library's session plumbing is deliberately single-game: one ApClient,
// one tracker, one reconnect banner ("One game at a time", P2-5). That is the
// right shape for playing somebody else's multiworld. But a seed you host
// yourself IS several games at once, and the delivery chain underneath --
// client to plugin to pipe to game -- has no such limit: the two-player proof
// runs two full chains side by side. So joining gets its own small sessions,
// wired exactly like that proof, and the library keeps its one-at-a-time flow
// untouched.
//
// ONE RULE THE UI MUST RESPECT: one session per PLUGIN. A catalogue plugin is
// a singleton with one pipe and one emulator profile -- the same game twice
// would be two sessions fighting over both. Two different games are two
// plugins, and that is the multi-open Marco asked for.
public sealed class ApJoinSession
{
    public enum Stage { Connecting, Playing, Refused, Failed, Ended }

    private static readonly List<ApJoinSession> _all = new();
    public static IReadOnlyList<ApJoinSession> All { get { lock (_all) return _all.ToList(); } }

    public static ApJoinSession? For(IGamePlugin plugin)
    {
        lock (_all)
            return _all.FirstOrDefault(s => ReferenceEquals(s.Plugin, plugin)
                                            && s.Current is Stage.Connecting or Stage.Playing);
    }

    public IGamePlugin Plugin { get; }
    public string SlotName { get; }
    public string SeedId { get; }
    public Stage Current { get; private set; } = Stage.Connecting;
    public string StatusText { get; private set; } = "Connecting";
    /// When this connection opened. Per SESSION, not per game: the tracker
    /// shows both, because "40 minutes in this sitting" and "nine hours in
    /// this game" answer different questions and one is not the other.
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    /// How many locations this slot has in total, as the server counted them
    /// at connect. Zero until Connected lands, and zero is shown as "—"
    /// rather than as a denominator nobody can trust.
    public int LocationTotal =>
        Client is { } c ? c.ConnectedChecked.Count + c.ConnectedMissing.Count : 0;

    /// Checked according to the SERVER, which includes anything checked in an
    /// earlier sitting. ChecksSent counts only what this connection sent, and
    /// showing that against the total would tell a returning player they had
    /// done nothing.
    public int LocationsDone =>
        Client is { } c ? Math.Max(c.ConnectedChecked.Count, 0) + ChecksSent : 0;

    public int ChecksSent { get; private set; }
    public int ItemsReceived { get; private set; }

    /// Raised on pool threads; the UI hops itself.
    public event Action? Changed;

    /// The live AP connection this session runs on. Exposed so the launcher's
    /// trackers (Items, Progression) can follow a joined game the same way
    /// they follow one started from the library — they used to sit at zero
    /// for every joined session, because this client was private.
    public ApClient? Client { get; private set; }

    /// Fired once a session is connected and its game is being launched.
    public static event Action<ApJoinSession>? Started;

    private ApClient? _client;

    /// host:port this session is connected to. Kept because the map tracker
    /// needs it: PopTracker takes --ap-host and joins the same room, and
    /// nothing else on the session carried the address.
    public string ServerAddress { get; private set; } = "";

    private ApJoinSession(IGamePlugin plugin, string slotName, string seedId)
    {
        Plugin = plugin;
        SlotName = slotName;
        SeedId = seedId;
    }

    private void Set(Stage stage, string text)
    {
        Current = stage;
        StatusText = text;
        Changed?.Invoke();
    }

    /// The whole join: host the seed's server if nothing hosts it, put the
    /// slot's patch where the plugin's own resolver looks, connect as the
    /// slot, hand the plugin its session context, and launch the game.
    public static async Task<(ApJoinSession? Session, string Message)> StartAsync(
        ApEngine.Report engine, SeedInfo seed, SeedSlot slot, IGamePlugin plugin,
        CancellationToken ct = default)
    {
        if (For(plugin) is { } already)
            return (null, $"{plugin.DisplayName} is already playing as "
                        + $"\"{already.SlotName}\". One session per game — stop "
                        + "that one first.");

        // 1. The server. Resume and fresh start are the same call: the server
        //    reads its own save when one sits beside the multidata.
        var hosted = ApServerHost.For(seed) is { IsRunning: true } h
            ? new ApServerHost.StartResult(h, "already hosting")
            : await ApServerHost.StartAsync(engine, seed, ct).ConfigureAwait(false);
        if (hosted.Host == null)
            return (null, "The seed's server did not start: " + hosted.Message);

        // 2. The patch, laid where the plugin's resolver already looks. The
        //    resolver matches on the manifest INSIDE the file and claims it
        //    for the seed on first connect -- so "install the patch" is
        //    nothing more than the file being present.
        if (slot.PatchFile != null)
        {
            try
            {
                string src = Path.Combine(seed.Folder, slot.PatchFile);
                string dir = Path.Combine(AppContext.BaseDirectory,
                                          "Games", "ROMs", plugin.GameId, "patches");
                Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, slot.PatchFile);
                if (!File.Exists(dst) && File.Exists(src)) File.Copy(src, dst);
            }
            catch (Exception e)
            {
                return (null, "The seed's patch could not be put in place: " + e.Message);
            }
        }

        // askForPatch: false -- the seed's own patch was just laid where the
        // plugin's resolver looks, so asking would demand a file that is
        // already in place.
        return await ConnectAndLaunchAsync(
            plugin, $"127.0.0.1:{hosted.Host.Port}", slot.Name, "",
            plugin.ApWorldName ?? slot.Game, seed.Id,
            askForPatch: false, ct).ConfigureAwait(false);
    }

    /// UI hook: asked on the external path when the connected seed has no
    /// stored patch for this slot. Returns true once one was imported. Set by
    /// the main window; null means nobody can ask, and the launch proceeds --
    /// the plugin's own SessionRomNote then says what that means.
    public static Func<IGamePlugin, SeedPatchRequest, Task<bool>>? AskForSeedPatch { get; set; }

    /// Join a slot on a server somebody else is running.
    ///
    /// No server is started and no patch is laid out: the session already
    /// exists and the player supplies their own game as usual. Everything the
    /// plugin is told is the same, because it is the same code path.
    public static async Task<(ApJoinSession? Session, string Message)> StartExternalAsync(
        ExternalSlot ext, IGamePlugin plugin, CancellationToken ct = default)
    {
        if (For(plugin) is { } already)
            return (null, $"{plugin.DisplayName} is already playing as "
                        + $"\"{already.SlotName}\". One session per game — stop "
                        + "that one first.");

        // askForPatch: true -- there is no seed folder here. If the seed
        // needs a patch for this slot, the only place it can come from is
        // the player, who got it from whoever generated the multiworld.
        return await ConnectAndLaunchAsync(
            plugin, ext.Address, ext.SlotName, ext.Password,
            plugin.ApWorldName ?? ext.Game ?? "", ext.DisplayAddress,
            askForPatch: true, ct).ConfigureAwait(false);
    }

    /// The shared half: connect as the slot, wire the plugin, launch the game.
    private static async Task<(ApJoinSession? Session, string Message)> ConnectAndLaunchAsync(
        IGamePlugin plugin, string address, string slotName, string password,
        string gameName, string sessionLabel, bool askForPatch, CancellationToken ct)
    {
        var session = new ApJoinSession(plugin, slotName, sessionLabel)
        { ServerAddress = address };
        lock (_all) _all.Add(session);

        try
        {
            // 3. Connect as the slot. Wired the way the two-player proof and
            //    the live Minish run were wired -- that exact shape is what
            //    has been proven end to end.
            var ap = new ApSession(address, slotName, password, gameName);
            var client = new ApClient(ap, plugin);
            session._client = client;

            var ready = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            client.SessionConnected += (_, _) => ready.TrySetResult(true);
            client.ConnectionRefusedReceived += errs => ready.TrySetException(
                new InvalidOperationException(string.Join("; ", errs)));

            await client.ConnectAsync().ConfigureAwait(false);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                using (timeout.Token.Register(() => ready.TrySetCanceled()))
                    await ready.Task.ConfigureAwait(false);
            }

            // 4. The plugin's window into the session — the same answers the
            //    launcher's own flow provides. The PUSH calls below are not
            //    optional extras: a plugin that owns a game process (OpenTTD's
            //    pipe) never polls the Get* delegates -- it waits for
            //    OnSlotData and OnLocationTable, and a join that skips them
            //    launches a game that sits forever at zero.
            plugin.GetServerLocations = () => client.ConnectedMissing.ToArray();
            plugin.GetOwnSlot         = () => client.Slot;
            plugin.GetSlotData        = () => client.SlotData;
            plugin.GetSeedName        = () => client.SeedName ?? sessionLabel;

            plugin.OnApServicesAttached(new JoinApServices(client));
            if (client.SlotData is { } sd) plugin.OnSlotData(sd);

            string worldName = gameName;
            Dictionary<string, long>? table = null;
            Dictionary<string, long>? itemTable = null;
            client.DataPackageReceived += (gameKey, data) =>
            {
                if (!string.Equals(gameKey, worldName, StringComparison.OrdinalIgnoreCase))
                    return;
                // Extract while the JsonElement is alive -- the client does
                // not clone it for this event.
                // Items ride in the same payload as locations, and a game
                // whose mod resolves items by name needs both halves.
                if (data.TryGetProperty("item_name_to_id", out var itemMap))
                {
                    var items = new Dictionary<string, long>(StringComparer.Ordinal);
                    foreach (var kv in itemMap.EnumerateObject())
                        items[kv.Name] = kv.Value.GetInt64();
                    itemTable = items;
                    plugin.OnItemTable(items);
                }
                if (!data.TryGetProperty("location_name_to_id", out var locMap)) return;
                var byName = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var kv in locMap.EnumerateObject())
                    byName[kv.Name] = kv.Value.GetInt64();
                table = byName;
                plugin.OnLocationTable(byName);
                // Resume: what this slot already checked, now that the plugin
                // has the table to name them with.
                plugin.OnCheckedLocations(client.ConnectedChecked.ToArray());
            };
            client.ServerCheckedLocations += ids => plugin.OnCheckedLocations(ids);
            client.DeathLinkReceived += (source, cause) =>
                _ = plugin.OnDeathLinkReceivedAsync(source, cause);
            // The table is a second round trip the client only makes on
            // request. Fire-and-forget: a failure degrades to id labels.
            _ = client.GetDataPackageAsync(new[] { worldName });

            plugin.LocationsChecked += ids =>
            {
                session.ChecksSent += ids.Length;
                try { client.SendLocationsCheckedAsync(ids).GetAwaiter().GetResult(); }
                catch { /* the reconnect story lives in the client */ }
                session.Persist();
                session.Changed?.Invoke();
            };
            client.ItemsReceived += (items, _, _) =>
            {
                session.ItemsReceived += items.Length;
                session.Persist();
                session.Changed?.Invoke();
            };
            plugin.GoalCompleted += () => session.Set(Stage.Playing, "Goal complete!");
            plugin.GameExited += _ => session.End("Game closed");

            // 4½. The patch, on the external path only. The seed name exists
            // now that we are connected, and this is the first moment the
            // question "does the store hold this seed's patch for this slot?"
            // can even be asked. Same reasoning as the library Play button --
            // asked ONCE, because the import is stored against (seed, slot).
            //
            // ⚠ A DECLINED ASK NOW STOPS THE JOIN. It used to launch anyway --
            // the return value of ask() was discarded on this very line -- on
            // the theory that an unpatched game that runs beats no game at all,
            // with the plugin's SessionRomNote left to explain. It does not:
            // the unpatched game sends nothing and receives nothing while
            // looking completely normal, so the player finds out an evening
            // later. If they cannot produce the patch, the honest outcome is
            // that the join does not happen.
            if (askForPatch
                && client.SeedName is { Length: > 0 } seedName
                && plugin.GetUnmetSeedPatch(seedName, slotName) is { } patchReq
                && AskForSeedPatch is { } ask)
            {
                session.Set(Stage.Connecting, "This seed needs its patch file…");
                bool got;
                // A prompt that THROWS is our bug, not the player's answer, and
                // must not read as "they said no" -- carry on in that case.
                try { got = await ask(plugin, patchReq).ConfigureAwait(false); }
                catch (Exception) { got = true; }

                if (!got)
                {
                    session.End("Cancelled — the seed's patch was not provided");
                    await client.DisconnectAsync().ConfigureAwait(false);
                    return (null, $"{plugin.DisplayName} needs this seed's patch "
                                + "file before it can join. Nothing was started. "
                                + "Press Play again once you have it.");
                }
            }

            // 5. The game itself.
            await plugin.LaunchAsync(ap).ConfigureAwait(false);
            plugin.LastSlotName = slotName;

            // Push again what may have raced ahead of the launch: a plugin
            // that resets session state in LaunchAsync loses anything that
            // arrived before it. All three calls are idempotent by contract.
            if (client.SlotData is { } sd2) plugin.OnSlotData(sd2);
            if (itemTable is { } it2) plugin.OnItemTable(it2);
            if (table is { } t2)
            {
                plugin.OnLocationTable(t2);
                plugin.OnCheckedLocations(client.ConnectedChecked.ToArray());
            }

            session.Client = client;
            try { Started?.Invoke(session); }
            catch { /* a tracker that fails must not end the session */ }

            session.Set(Stage.Playing, $"Playing — {address}");
            return (session, "Joined.");
        }
        catch (OperationCanceledException)
        {
            session.Set(Stage.Failed, "The server did not answer in time.");
            await session.TearDownAsync().ConfigureAwait(false);
            return (null, session.StatusText);
        }
        catch (Exception e)
        {
            string why = e.Message.Split('\n')[0];
            session.Set(e is InvalidOperationException ? Stage.Refused : Stage.Failed, why);
            await session.TearDownAsync().ConfigureAwait(false);
            return (null, why);
        }
    }

    /// Player-initiated stop: game first, connection second. The teardown is
    /// AWAITED here — StopAllAsync's caller (launcher shutdown, the proofs)
    /// must be able to trust that "stopped" means gone, and the fire-and-
    /// forget End() below cannot promise that.
    /// Write this slot's figures where the Join tab can read them when
    /// nothing is running.
    ///
    /// ⚠ Everything on this object dies with the connection, and the Join tab
    /// is looked at BETWEEN sessions. Without this the cards are blank exactly
    /// when they are being read.
    private DateTimeOffset _lastPersist = DateTimeOffset.Now;

    internal void Persist()
    {
        try
        {
            long add = (long)(DateTimeOffset.Now - _lastPersist).TotalSeconds;
            _lastPersist = DateTimeOffset.Now;
            SeedProgressStore.Record(SeedId, SlotName, LocationsDone, LocationTotal,
                                     ItemsReceived, ChecksSent, add);
        }
        catch { /* bookkeeping must never take a session down */ }
    }

    public async Task StopAsync()
    {
        try { await Plugin.StopAsync().ConfigureAwait(false); } catch { }
        if (Current is not Stage.Ended) Set(Stage.Ended, "Stopped");
        await TearDownAsync().ConfigureAwait(false);
    }

    private void End(string why)
    {
        if (Current is Stage.Ended) return;
        Set(Stage.Ended, why);
        _ = TearDownAsync();
    }

    private int _tornDown;

    private async Task TearDownAsync()
    {
        // Both End() (event-driven) and StopAsync (awaited) reach here; the
        // second arrival must be a no-op, not a double-dispose.
        if (System.Threading.Interlocked.Exchange(ref _tornDown, 1) == 1) return;

        // Last word before the figures go. The card the player sees next is
        // written here.
        Persist();
        var c = _client;
        _client = null;
        if (c != null)
        {
            try { await c.DisconnectAsync().ConfigureAwait(false); } catch { }
            try { await c.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        lock (_all) _all.Remove(this);
        Changed?.Invoke();
    }

    // IApServices over the bare client -- the joined game's window into its
    // own session. The classic flow's adapter lives in the UI layer and needs
    // the dispatcher and tracker; a join session has neither, and the games
    // played through it need identity, resync, scouts and DeathLink.
    private sealed class JoinApServices : IApServices
    {
        private readonly ApClient _ap;
        public JoinApServices(ApClient ap)
        {
            _ap = ap;
            _ap.LocationInfoReceived += items => LocationsScouted?.Invoke(items);
            _ap.ServerCheckedLocations += ids =>
            {
                lock (_checked) foreach (long id in ids) _checked.Add(id);
            };
            lock (_checked) foreach (long id in ap.ConnectedChecked) _checked.Add(id);
        }

        private readonly HashSet<long> _checked = new();

        public int OwnSlot => _ap.Slot;
        public System.Text.Json.JsonElement? SlotData => _ap.SlotData;
        public string? SeedName => _ap.SeedName;

        public string ResolvePlayerName(int slot)
        {
            var p = _ap.Players.FirstOrDefault(x => x.Slot == slot);
            return p?.Alias ?? p?.Name ?? $"Player {slot}";
        }

        public long[] CheckedLocations()
        {
            lock (_checked) return _checked.ToArray();
        }

        public long[] UncheckedLocations()
        {
            lock (_checked)
                return _ap.ConnectedMissing.Where(id => !_checked.Contains(id)).ToArray();
        }

        public Task ScoutLocationsAsync(long[] locationIds)
            => _ap.LocationScoutsAsync(locationIds, createAsHint: 0);

        public event Action<ApNetworkItem[]>? LocationsScouted;

        public Task ResyncAsync() => _ap.SyncAsync();

        public bool DeathLinkEnabled => _ap.DeathLinkEnabled;

        public void ReportDeath(string? cause)
        {
            if (!_ap.DeathLinkEnabled) return;
            _ = _ap.SendDeathLinkAsync(string.IsNullOrWhiteSpace(cause) ? "died" : cause);
        }
    }

    /// Everything, stopped — the launcher is closing.
    public static async Task StopAllAsync()
    {
        foreach (var s in All)
            try { await s.StopAsync().ConfigureAwait(false); } catch { }
    }
}
