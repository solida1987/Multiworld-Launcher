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
    public int ChecksSent { get; private set; }
    public int ItemsReceived { get; private set; }

    /// Raised on pool threads; the UI hops itself.
    public event Action? Changed;

    private ApClient? _client;

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

        var session = new ApJoinSession(plugin, slot.Name, seed.Id);
        lock (_all) _all.Add(session);

        try
        {
            // 3. Connect as the slot. Wired the way the two-player proof and
            //    the live Minish run were wired -- that exact shape is what
            //    has been proven end to end.
            var ap = new ApSession($"127.0.0.1:{hosted.Host.Port}", slot.Name, "",
                                   plugin.ApWorldName ?? slot.Game);
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

            // 4. The plugin's window into the session — same four answers the
            //    launcher's own flow provides.
            plugin.GetServerLocations = () => client.ConnectedMissing.ToArray();
            plugin.GetOwnSlot         = () => client.Slot;
            plugin.GetSlotData        = () => client.SlotData;
            plugin.GetSeedName        = () => client.SeedName ?? seed.Id;

            plugin.LocationsChecked += ids =>
            {
                session.ChecksSent += ids.Length;
                try { client.SendLocationsCheckedAsync(ids).GetAwaiter().GetResult(); }
                catch { /* the reconnect story lives in the client */ }
                session.Changed?.Invoke();
            };
            client.ItemsReceived += (items, _, _) =>
            {
                session.ItemsReceived += items.Length;
                session.Changed?.Invoke();
            };
            plugin.GoalCompleted += () => session.Set(Stage.Playing, "Goal complete!");
            plugin.GameExited += _ => session.End("Game closed");

            // 5. The game itself.
            await plugin.LaunchAsync(ap).ConfigureAwait(false);
            plugin.LastSlotName = slot.Name;

            session.Set(Stage.Playing, $"Playing on port {hosted.Host.Port}");
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

    /// Player-initiated stop: game first, connection second.
    public async Task StopAsync()
    {
        try { await Plugin.StopAsync().ConfigureAwait(false); } catch { }
        End("Stopped");
    }

    private void End(string why)
    {
        if (Current is Stage.Ended) return;
        Set(Stage.Ended, why);
        _ = TearDownAsync();
    }

    private async Task TearDownAsync()
    {
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

    /// Everything, stopped — the launcher is closing.
    public static async Task StopAllAsync()
    {
        foreach (var s in All)
            try { await s.StopAsync().ConfigureAwait(false); } catch { }
    }
}
