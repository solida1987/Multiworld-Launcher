using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// ApClient — native C# Archipelago WebSocket client.
//
// REPLACES ap_bridge.py ENTIRELY — no Python dependency, no subprocess spawn,
// runs in-process inside the launcher.
//
// ARCHITECTURE
// ────────────
// One ApClient per active game slot. The launcher creates it when the user
// clicks Play, passes it to the game plugin via LaunchAsync, and disposes
// it when the game exits. The plugin receives items via ReceiveItemsAsync;
// the plugin reports checks by firing LocationsChecked, which the launcher
// intercepts and forwards to the AP server via SendLocationsCheckedAsync.
//
// THREADING
// ─────────
// ConnectAsync starts a receive loop on a background Task. All events fire
// on that background thread — handlers must marshal to UI thread if needed.
// SendXxxAsync is safe to call from any thread: a SemaphoreSlim serialises
// the send path, because ClientWebSocket allows at most ONE outstanding
// SendAsync — a second concurrent call throws InvalidOperationException
// (concurrent senders are real: UI chat/toggles, the receive loop's
// connect burst, and the game plugin's pipe-loop check forwarding).
//
// RECONNECT
// ─────────
// No automatic retry loop in V2.0.0 — if the connection drops, GameExited
// fires, the launcher notifies the plugin and cleans up. The caller may call
// ConnectAsync again on the same instance: after a re-connect handshake the
// client sends Sync and replays every locally-known checked location, so no
// checks or items are lost (the server ignores duplicate checks and the
// plugin's index tracking dedups re-sent items).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ApClient : IAsyncDisposable
{
    private readonly ApSession   _session;
    private readonly IGamePlugin _plugin;

    private ClientWebSocket?         _ws;
    private CancellationTokenSource? _cts;

    // AP slot identity (populated after Connected)
    private int _team;
    private int _slot;

    // Connection tags — "AP" plus feature tags like "DeathLink". "MultiworldLauncher"
    // identifies this client in AP server logs and community tools.
    // Sent with Connect; updated via ConnectUpdate when toggled mid-session.
    private string[] _tags = { "AP", "MultiworldLauncher" };

    // Unix time of the last DeathLink Bounce WE sent. Echo guard — because we
    // carry the DeathLink tag ourselves, the server bounces our own death back.
    private double _lastDeathLinkSentTime = double.NaN;

    // True once the first Connected handshake completed — a later Connected on
    // the same client instance means we re-connected and must resync.
    private bool _wasConnectedBefore;

    // Every location ID known checked this session (server-reported + sent by
    // us). Replayed after a re-connect; the server ignores duplicates.
    private readonly HashSet<long> _localChecked = new();
    private readonly object        _checkedLock  = new();

    // Serialises every outbound WS write — ClientWebSocket supports at most
    // one outstanding SendAsync (see THREADING in the header). Never disposed:
    // a racing sender releasing a disposed semaphore would throw in `finally`.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Data storage key holding this slot's hint list (valid after Connected).
    private string HintsStorageKey => $"_read_hints_{_team}_{_slot}";

    // ── Public state ─────────────────────────────────────────────────────────

    public ApConnectionState State { get; private set; } = ApConnectionState.Disconnected;

    /// Players in the multiworld, populated after Connected.
    public IReadOnlyList<ApNetworkPlayer> Players { get; private set; }
        = Array.Empty<ApNetworkPlayer>();

    /// AP slot number assigned to us (available after Connected).
    public int Slot => _slot;

    /// AP team number (almost always 0).
    public int Team => _team;

    /// The game name for our slot, populated after Connected. Null when disconnected.
    public string? ConnectedGame { get; private set; }

    /// Hint cost as a PERCENTAGE of this slot's total location count (from
    /// RoomInfo / RoomUpdate, 0 = hints are free). NOT an absolute point price —
    /// use HintCostPoints for the actual amount a hint deducts.
    public int HintCost { get; private set; }

    /// Actual point price of one hint: HintCost percent of the total location
    /// count (ConnectedChecked + ConnectedMissing). 0 until Connected arrives.
    public int HintCostPoints
        => Math.Max(0, (ConnectedChecked.Count + ConnectedMissing.Count) * HintCost / 100);

    /// Current hint points balance (from Connected / RoomUpdate).
    /// Points are earned per check (location_check_points in RoomInfo).
    public int HintPoints { get; private set; }

    /// True while the "DeathLink" tag is active for this connection.
    public bool DeathLinkEnabled { get; private set; }

    /// Game-specific slot data from the Connected packet (the settings the
    /// multiworld seed was generated with). Null until connected. Cloned —
    /// safe to hold across packets.
    public JsonElement? SlotData { get; private set; }

    /// Per-game datapackage checksums announced in RoomInfo. Used by the
    /// upstream-update detector (§15): a checksum differing from what a game
    /// integration was built against means the apworld changed upstream.
    public IReadOnlyDictionary<string, string> DataPackageChecksums { get; private set; }
        = new Dictionary<string, string>();

    /// The multiworld's seed name from RoomInfo (e.g. "81769672980259429628").
    /// Patch files are named AP_<seed>_P<slot>_<player> — this lets the patch
    /// resolver pick the file that belongs to THIS room.
    public string? SeedName { get; private set; }

    /// Location IDs already checked when we connected (from Connected packet).
    public IReadOnlyList<long> ConnectedChecked { get; private set; } = Array.Empty<long>();

    /// Location IDs still unchecked when we connected (from Connected packet).
    public IReadOnlyList<long> ConnectedMissing { get; private set; } = Array.Empty<long>();

    // ── Events ────────────────────────────────────────────────────────────────

    /// Fires whenever State changes.
    public event Action<ApConnectionState>? StateChanged;

    /// Fires when the server sends a DataPackage for one or more games.
    /// (gameKey, dataPackageElement) — wire to ApItemTracker.OnDataPackage.
    public event Action<string, System.Text.Json.JsonElement>? DataPackageReceived;

    /// Raw JSON messages for the "AP Console" log tab.
    /// bool = true → outbound (we sent it), false → inbound (server sent it).
    public event Action<string, bool>? RawMessage;

    /// Plain-text chat / notification decoded from PrintJSON.
    public event Action<string>? PrintMessage;

    /// Fires when a Hint PrintJSON is received (type == "Hint").
    /// Payload = the plain-text hint string.
    public event Action<string>? HintReceived;

    /// Fires when a Hint PrintJSON is received and parsed into a structured entry.
    public event Action<HintEntry>? HintEntryReceived;

    /// Fires once after the Connected handshake completes.
    /// (mySlot, playerList) — use to initialise the item tracker.
    public event Action<int, IReadOnlyList<ApNetworkPlayer>>? SessionConnected;

    /// Fires whenever ReceivedItems arrives from the server.
    /// (items, receiverSlot) — receiverSlot == Slot for normal single-slot clients.
    public event Action<ApNetworkItem[], int>? ItemsReceived;

    /// Fires after LocationScouts reply arrives with item info.
    public event Action<ApNetworkItem[]>? LocationInfoReceived;

    /// Fires when hint points change (Connected packet or RoomUpdate).
    public event Action<int>? HintPointsChanged;

    /// Fires when the server reports new checked locations (RoomUpdate).
    public event Action<long[]>? ServerCheckedLocations;

    /// Fires when another player's DeathLink death arrives (Bounced packet).
    /// (source, cause) — source is the dying player's slot/alias name.
    /// Our own bounced-back deaths are filtered out (echo guard).
    public event Action<string, string>? DeathLinkReceived;

    /// Fires when RoomUpdate carries an updated player list (player joins / leaves).
    public event Action<IReadOnlyList<ApNetworkPlayer>>? RoomPlayersChanged;

    /// Fires for every PrintJSON of type "ItemSend" — any item moving between
    /// any two slots in the multiworld, not just items for our slot.
    /// (receivingSlot, sendingSlot, itemId, itemFlags, locationId)
    public event Action<int, int, long, int, long>? ItemSendReceived;

    /// Fires once per tick of a server countdown (PrintJSON type "Countdown").
    /// Value counts down to 0 ("GO").
    public event Action<int>? CountdownTick;

    /// Fires when a player completes their goal (PrintJSON type "Goal"). int = slot.
    public event Action<int>? GoalAnnounced;

    /// Fires when a player releases their remaining items (PrintJSON type "Release"). int = slot.
    public event Action<int>? ReleaseAnnounced;

    /// Fires when a player collects their items (PrintJSON type "Collect"). int = slot.
    public event Action<int>? CollectAnnounced;

    /// Fires with the raw hint array for our slot from the data storage API
    /// (Retrieved reply to the connect-time Get, and live SetReply updates for
    /// the "_read_hints_{team}_{slot}" key). The JsonElement is a clone — safe
    /// to hold past the handler. Each entry carries: receiving_player,
    /// finding_player, location, item, found, entrance, item_flags, status.
    public event Action<JsonElement>? HintsRetrieved;

    /// Fires when the server refuses the Connect handshake. Payload = the raw
    /// AP error codes ("InvalidSlot", "InvalidPassword", "InvalidGame", ...).
    /// Raised BEFORE the state flips to Error so handshake waiters can see the
    /// specific refusal rather than a generic state change.
    public event Action<string[]>? ConnectionRefusedReceived;

    // ── Constructor ──────────────────────────────────────────────────────────

    public ApClient(ApSession session, IGamePlugin plugin)
    {
        _session = session;
        _plugin  = plugin;
    }

    // ── Connect / disconnect ─────────────────────────────────────────────────

    /// Open the WebSocket connection and start the receive loop.
    /// After this returns the loop is running; Connected state arrives
    /// asynchronously via StateChanged when the handshake completes.
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State is ApConnectionState.Connecting or ApConnectionState.Connected)
            return;

        SetState(ApConnectionState.Connecting);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Scheme handling follows the official AP client convention:
        // an explicit ws:// or wss:// is honored as-is; a bare host:port
        // tries wss:// FIRST (archipelago.gg rooms are TLS-only) and falls
        // back to ws:// (locally hosted MultiServer instances have no TLS).
        // The failed wss attempt against a plain server rejects fast — the
        // TLS handshake dies on the first response bytes.
        string raw = _session.ServerUri;
        bool explicitScheme =
            raw.StartsWith("ws://",  StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);

        string[] candidates = explicitScheme
            ? new[] { raw }
            : new[] { "wss://" + raw, "ws://" + raw };

        Exception? lastError = null;
        bool connected = false;
        foreach (string uri in candidates)
        {
            _ws?.Dispose();
            _ws = new ClientWebSocket();
            // permessage-deflate: AP servers warn "your client does not
            // support compressed websocket connections! It may stop working
            // in the future" without it. .NET 8 negotiates it natively.
            _ws.Options.DangerousDeflateOptions =
                new WebSocketDeflateOptions();
            try
            {
                await _ws.ConnectAsync(new Uri(uri), ct);
                connected = true;
                break;
            }
            catch (OperationCanceledException)
            {
                SetState(ApConnectionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (!connected)
        {
            // A failed socket connect (bad host, refused port, malformed URI)
            // must not strand the instance in Connecting forever — the early
            // return above would turn every retry on this client into a
            // silent no-op (P3-5).
            SetState(ApConnectionState.Disconnected);
            throw lastError ?? new InvalidOperationException("Connect failed.");
        }

        // Receive loop runs on a background thread
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    /// Cleanly close the connection.
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client disconnect",
                    CancellationToken.None);
            }
            catch { /* socket may already be gone */ }
        }
        SetState(ApConnectionState.Disconnected);
    }

    // ── Outbound commands ────────────────────────────────────────────────────

    /// Request the DataPackage for one or more games.
    /// Call after Connected to populate item/location name lookup tables.
    public Task GetDataPackageAsync(string[] games, CancellationToken ct = default)
        => SendJsonAsync(new object[]
        {
            new { cmd = "GetDataPackage", games }
        }, ct);

    /// Tell the AP server which location checks the game just completed.
    /// IDs are also remembered locally so a re-connect can replay them.
    public Task SendLocationsCheckedAsync(long[] locationIds, CancellationToken ct = default)
    {
        lock (_checkedLock)
            foreach (long id in locationIds) _localChecked.Add(id);

        return SendJsonAsync(new object[]
        {
            new { cmd = "LocationChecks", locations = locationIds }
        }, ct);
    }

    /// Scout (and optionally hint) a set of locations.
    /// createAsHint: 0 = scout only (no hints created), 1 = create hints and
    /// broadcast them all (re-announces already-known ones), 2 = create hints
    /// but only broadcast NEW ones. The LocationInfo reply contains every
    /// scouted location either way. Creating hints costs HintCostPoints points
    /// per location when createAsHint >= 1.
    public Task LocationScoutsAsync(long[] locationIds, int createAsHint = 0,
                                    CancellationToken ct = default)
        => SendJsonAsync(new object[]
        {
            new { cmd = "LocationScouts", locations = locationIds, create_as_hint = createAsHint }
        }, ct);

    /// Update the AP server with the current client status (Playing, Goal, etc.).
    public Task SendStatusUpdateAsync(int status, CancellationToken ct = default)
        => SendJsonAsync(new object[]
        {
            new { cmd = "StatusUpdate", status }
        }, ct);

    /// Typed wrapper around SendStatusUpdateAsync — see ClientStatus for the
    /// intended lifecycle (Connected in launcher → Playing in-game → Goal).
    public Task SetStatusAsync(ClientStatus status, CancellationToken ct = default)
        => SendStatusUpdateAsync((int)status, ct);

    /// Send a chat message to the AP server (relayed to all players).
    public Task SendSayAsync(string text, CancellationToken ct = default)
        => SendJsonAsync(new object[]
        {
            new { cmd = "Say", text }
        }, ct);

    /// Ask the server to resend the full ReceivedItems stream (from index 0).
    /// Safe by design: receivers dedup via their resume index. Called by the
    /// launcher after a game's pipe attaches so items that arrived while no
    /// game was running get re-delivered through the plugin's slicing guard.
    public Task SyncAsync(CancellationToken ct = default)
        => SendJsonAsync(new object[]
        {
            new { cmd = "Sync" }
        }, ct);

    /// Send !release command — releases all remaining items in this slot to other players.
    public Task ReleaseAsync(CancellationToken ct = default)
        => SendSayAsync("!release", ct);

    /// Send !collect command — collects all items owed to this slot from other players' worlds.
    public Task CollectAsync(CancellationToken ct = default)
        => SendSayAsync("!collect", ct);

    /// Legacy alias for ReleaseAsync. The "!forfeit" chat command was removed
    /// from AP servers (renamed "!release"), so this sends "!release" — kept
    /// only so existing callers keep working. Prefer ReleaseAsync.
    public Task ForfeitAsync(CancellationToken ct = default)
        => SendSayAsync("!release", ct);

    // HintStatus values for UpdateHintStatusAsync (mirrors AP NetUtils.HintStatus;
    // 0 = unspecified, 40 = found — found is server-set and cannot be sent).
    public const int HintStatusNoPriority = 10;
    public const int HintStatusAvoid      = 20;
    public const int HintStatusPriority   = 30;

    /// Set the priority of an existing hint whose receiver is our slot.
    /// findingPlayer = the slot whose world contains the hinted location;
    /// status = one of the HintStatus* constants above.
    public Task UpdateHintStatusAsync(int findingPlayer, long locationId, int status,
                                      CancellationToken ct = default)
        => SendJsonAsync(new object[]
        {
            new { cmd = "UpdateHint", player = findingPlayer, location = locationId, status }
        }, ct);

    // ── DeathLink ────────────────────────────────────────────────────────────

    /// Enable or disable DeathLink without reconnecting. Sends a ConnectUpdate
    /// carrying the full tag set; when called before the handshake it simply
    /// primes the tags the next Connect command will carry.
    public async Task SetDeathLinkAsync(bool enabled, CancellationToken ct = default)
    {
        if (DeathLinkEnabled == enabled) return;

        DeathLinkEnabled = enabled;
        _tags = enabled ? new[] { "AP", "DeathLink" } : new[] { "AP" };

        await SendJsonAsync(new object[]
        {
            new { cmd = "ConnectUpdate", tags = _tags }
        }, ct);
    }

    /// Broadcast our own death to every DeathLink-tagged client in the room.
    /// cause should be a full sentence including the player name,
    /// e.g. "Marco was slain by Diablo." — it is shown verbatim by other clients.
    public Task SendDeathLinkAsync(string cause, CancellationToken ct = default)
    {
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _lastDeathLinkSentTime = now;   // echo guard — see HandleBounced

        return SendJsonAsync(new object[]
        {
            new
            {
                cmd  = "Bounce",
                tags = new[] { "DeathLink" },
                data = new { time = now, source = _session.SlotName, cause }
            }
        }, ct);
    }

    // ── Receive loop ─────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[65536];
        var sb  = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buf, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
                }
                while (!result.EndOfMessage);

                string json = sb.ToString();
                RawMessage?.Invoke(json, false);

                await DispatchAsync(json, ct);
            }
        }
        catch (OperationCanceledException) { /* clean shutdown — not an error */ }
        catch (WebSocketException ex)
        {
            PrintMessage?.Invoke($"[AP] Connection lost: {ex.Message}");
        }
        finally
        {
            SetState(ApConnectionState.Disconnected);
            _plugin.OnApStateChanged(ApConnectionState.Disconnected);
        }
    }

    // ── Message dispatch ─────────────────────────────────────────────────────

    private async Task DispatchAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);

        foreach (var cmdEl in doc.RootElement.EnumerateArray())
        {
            if (!cmdEl.TryGetProperty("cmd", out var cmdProp)) continue;

            switch (cmdProp.GetString())
            {
                case "RoomInfo":
                    HandleRoomInfo(cmdEl);             // parse hint_cost before connecting
                    await SendConnectCmdAsync(ct);
                    break;

                case "Connected":
                    await HandleConnectedAsync(cmdEl, ct);
                    break;

                case "ConnectionRefused":
                    HandleConnectionRefused(cmdEl);
                    break;

                case "ReceivedItems":
                    await HandleReceivedItemsAsync(cmdEl, ct);
                    break;

                case "DataPackage":
                    HandleDataPackage(cmdEl);
                    break;

                case "PrintJSON":
                    HandlePrintJson(cmdEl);
                    break;

                case "RoomUpdate":
                    HandleRoomUpdate(cmdEl);
                    break;

                case "LocationInfo":
                    HandleLocationInfo(cmdEl);
                    break;

                case "Bounced":
                    HandleBounced(cmdEl);
                    break;

                case "Retrieved":
                    HandleRetrieved(cmdEl);
                    break;

                case "SetReply":
                    HandleSetReply(cmdEl);
                    break;

                case "InvalidPacket":
                {
                    string cmd   = cmdEl.TryGetProperty("original_cmd", out var oc) ? oc.GetString() ?? "?" : "?";
                    string error = cmdEl.TryGetProperty("text",         out var tx) ? tx.GetString() ?? ""  : "";
                    PrintMessage?.Invoke($"[AP] Server rejected packet '{cmd}': {error}");
                    break;
                }
            }
        }
    }

    // ── Handshake ────────────────────────────────────────────────────────────

    private void HandleRoomInfo(JsonElement el)
    {
        if (el.TryGetProperty("hint_cost", out var hc))
            HintCost = hc.GetInt32();

        if (el.TryGetProperty("seed_name", out var sn) &&
            sn.ValueKind == JsonValueKind.String)
            SeedName = sn.GetString();

        // Per-game datapackage checksums — the upstream-update detector (§15)
        // compares these against the checksum a game integration was built
        // against, so a silently-updated apworld surfaces as a warning
        // instead of missing checks.
        if (el.TryGetProperty("datapackage_checksums", out var sums) &&
            sums.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in sums.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    map[p.Name] = p.Value.GetString()!;
            DataPackageChecksums = map;
        }
    }

    private Task SendConnectCmdAsync(CancellationToken ct)
        => SendJsonAsync(new object[]
        {
            new
            {
                cmd            = "Connect",
                game           = _session.Game,
                name           = _session.SlotName,
                password       = _session.Password,
                uuid           = _session.Uuid ?? Guid.NewGuid().ToString("N"),
                // The single source of truth for the claimed client version
                // (P3-18) — a hardcoded anonymous twin here drifted from it.
                version        = ApVersion.ClientVersion,
                items_handling = ApItemsHandling.All,
                tags           = _tags,
                slot_data      = true
            }
        }, ct);

    // ── Inbound handlers ─────────────────────────────────────────────────────

    private async Task HandleConnectedAsync(JsonElement el, CancellationToken ct)
    {
        _team = el.TryGetProperty("team",  out var t) ? t.GetInt32() : 0;
        _slot = el.TryGetProperty("slot",  out var s) ? s.GetInt32() : 0;

        // Build player list
        if (el.TryGetProperty("players", out var playersEl))
        {
            var players = new List<ApNetworkPlayer>();
            foreach (var p in playersEl.EnumerateArray())
            {
                players.Add(new ApNetworkPlayer(
                    Team:  p.TryGetProperty("team",  out var pt) ? pt.GetInt32()    : 0,
                    Slot:  p.TryGetProperty("slot",  out var ps) ? ps.GetInt32()    : 0,
                    Alias: p.TryGetProperty("alias", out var pa) ? pa.GetString()!  : "",
                    Name:  p.TryGetProperty("name",  out var pn) ? pn.GetString()!  : "",
                    Game:  p.TryGetProperty("game",  out var pg) ? pg.GetString()!  : ""));
            }
            Players = players;
        }

        // Determine our game name from the player list
        ConnectedGame = Players.FirstOrDefault(p => p.Slot == _slot && p.Team == _team)?.Game;

        // Extract checked / missing location IDs for the LocationTracker
        if (el.TryGetProperty("checked_locations", out var checkedEl))
        {
            var list = new List<long>();
            foreach (var v in checkedEl.EnumerateArray()) list.Add(v.GetInt64());
            ConnectedChecked = list;
        }
        if (el.TryGetProperty("missing_locations", out var missingEl))
        {
            var list = new List<long>();
            foreach (var v in missingEl.EnumerateArray()) list.Add(v.GetInt64());
            ConnectedMissing = list;
        }
        // Slot data — game-specific settings baked into the multiworld seed.
        // Cloned so it outlives the receive buffer's JsonDocument.
        if (el.TryGetProperty("slot_data", out var slotDataEl))
            SlotData = slotDataEl.Clone();
        // Server-known checks join the local replay set (see reconnect below)
        lock (_checkedLock)
            foreach (long id in ConnectedChecked) _localChecked.Add(id);
        // Initial hint points
        if (el.TryGetProperty("hint_points", out var hpEl))
        {
            HintPoints = hpEl.GetInt32();
            HintPointsChanged?.Invoke(HintPoints);
        }

        SetState(ApConnectionState.Connected);
        _plugin.OnApStateChanged(ApConnectionState.Connected);
        SessionConnected?.Invoke(_slot, Players);

        // Report Connected (5) — we are in the launcher, not in-game yet.
        // Playing (20) belongs to the moment the game process actually starts
        // (SetStatusAsync(ClientStatus.Playing)); Goal (30) stays wired to
        // goal completion via SendStatusUpdateAsync.
        await SendStatusUpdateAsync(ApClientStatus.Connected, ct);

        // Fetch the existing hint backlog and subscribe to live hint updates
        // (data storage API). Replies arrive as Retrieved / SetReply packets
        // and surface through the HintsRetrieved event.
        await SendJsonAsync(new object[]
        {
            new { cmd = "Get",       keys = new[] { HintsStorageKey } },
            new { cmd = "SetNotify", keys = new[] { HintsStorageKey } }
        }, ct);

        // Reconnect hardening: on a re-connect, ask the server to resend the
        // item stream from index 0 and replay every locally-known check.
        // Safe by design — the server ignores duplicate checks and the game
        // plugin's resume-index tracking dedups re-delivered items.
        if (_wasConnectedBefore)
        {
            await SyncAsync(ct);

            long[] replay;
            lock (_checkedLock) replay = _localChecked.ToArray();
            if (replay.Length > 0)
                await SendLocationsCheckedAsync(replay, ct);
        }
        _wasConnectedBefore = true;

        PrintMessage?.Invoke(
            $"[AP] Connected — slot {_slot}, team {_team}, " +
            $"{Players.Count} player(s) in session");
    }

    private void HandleConnectionRefused(JsonElement el)
    {
        var errors = new List<string>();
        if (el.TryGetProperty("errors", out var errEl))
            foreach (var e in errEl.EnumerateArray())
                errors.Add(e.GetString() ?? "Unknown");
        if (errors.Count == 0) errors.Add("Unknown");

        string msg = string.Join(", ", errors);
        PrintMessage?.Invoke($"[AP] Connection refused: {msg}");

        // Refusal event first, THEN the Error state — handshake waiters race
        // on whichever signal lands first and the refusal codes are the
        // useful one (they translate to actionable user guidance).
        ConnectionRefusedReceived?.Invoke(errors.ToArray());

        SetState(ApConnectionState.Error);
        _plugin.OnApStateChanged(ApConnectionState.Error);
    }

    private async Task HandleReceivedItemsAsync(JsonElement el, CancellationToken ct)
    {
        if (!el.TryGetProperty("index", out var idxEl)) return;
        int index = idxEl.GetInt32();

        var items = new List<ApNetworkItem>();
        if (el.TryGetProperty("items", out var itemsEl))
        {
            foreach (var itemEl in itemsEl.EnumerateArray())
            {
                items.Add(new ApNetworkItem(
                    ItemId:     itemEl.TryGetProperty("item",     out var ii) ? ii.GetInt64() : 0,
                    LocationId: itemEl.TryGetProperty("location", out var il) ? il.GetInt64() : 0,
                    Player:     itemEl.TryGetProperty("player",   out var ip) ? ip.GetInt32() : 0,
                    Flags:      itemEl.TryGetProperty("flags",    out var ifl) ? ifl.GetInt32() : 0
                ));
            }
        }

        var itemArray = items.ToArray();

        // Forward to the game plugin — plugin handles dedup via index tracking
        try
        {
            await _plugin.ReceiveItemsAsync(itemArray, index, ct);
        }
        catch (Exception ex)
        {
            PrintMessage?.Invoke($"[AP] Warning: item delivery to game failed ({itemArray.Length} items, index {index}): {ex.Message}");
        }

        // Also notify the item tracker (fired after plugin, so dedup is respected)
        ItemsReceived?.Invoke(itemArray, _slot);
    }

    private void HandleDataPackage(JsonElement el)
    {
        // el is the DataPackage command object.
        // "data" → { "games": { "<gameName>": { "item_name_to_id": {...}, "location_name_to_id": {...} } } }
        if (!el.TryGetProperty("data", out var data)) return;
        if (!data.TryGetProperty("games", out var games)) return;

        foreach (var kv in games.EnumerateObject())
            DataPackageReceived?.Invoke(kv.Name, kv.Value);
    }

    private void HandleRoomUpdate(JsonElement el)
    {
        // Hint cost (a percentage — see HintCost) can be changed mid-session
        if (el.TryGetProperty("hint_cost", out var hc))
            HintCost = hc.GetInt32();
        // Hint points may change after each check or hint purchase
        if (el.TryGetProperty("hint_points", out var hp))
        {
            HintPoints = hp.GetInt32();
            HintPointsChanged?.Invoke(HintPoints);
        }
        // Other players may check locations that are in our world
        if (el.TryGetProperty("checked_locations", out var cl))
        {
            var ids = new List<long>();
            foreach (var v in cl.EnumerateArray()) ids.Add(v.GetInt64());
            if (ids.Count > 0)
            {
                lock (_checkedLock)
                    foreach (long id in ids) _localChecked.Add(id);
                ServerCheckedLocations?.Invoke(ids.ToArray());
            }
        }
        // Player composition may change mid-session (join / leave)
        if (el.TryGetProperty("players", out var playersEl) &&
            playersEl.ValueKind == JsonValueKind.Array)
        {
            var players = new List<ApNetworkPlayer>();
            foreach (var p in playersEl.EnumerateArray())
            {
                players.Add(new ApNetworkPlayer(
                    Team:  p.TryGetProperty("team",  out var pt) ? pt.GetInt32() : 0,
                    Slot:  p.TryGetProperty("slot",  out var ps) ? ps.GetInt32() : 0,
                    Alias: p.TryGetProperty("alias", out var pa) ? pa.GetString() ?? "" : "",
                    Name:  p.TryGetProperty("name",  out var pn) ? pn.GetString() ?? "" : "",
                    Game:  p.TryGetProperty("game",  out var pg) ? pg.GetString() ?? "" : ""));
            }
            if (players.Count > 0)
            {
                Players = players;
                RoomPlayersChanged?.Invoke(players);
            }
        }
    }

    private void HandleLocationInfo(JsonElement el)
    {
        if (!el.TryGetProperty("locations", out var locsEl)) return;
        var items = new List<ApNetworkItem>();
        foreach (var itemEl in locsEl.EnumerateArray())
        {
            items.Add(new ApNetworkItem(
                ItemId:     itemEl.TryGetProperty("item",     out var ii)  ? ii.GetInt64()  : 0,
                LocationId: itemEl.TryGetProperty("location", out var il)  ? il.GetInt64()  : 0,
                Player:     itemEl.TryGetProperty("player",   out var ip)  ? ip.GetInt32()  : 0,
                Flags:      itemEl.TryGetProperty("flags",    out var ifl) ? ifl.GetInt32() : 0));
        }
        if (items.Count > 0)
            LocationInfoReceived?.Invoke(items.ToArray());
    }

    private void HandlePrintJson(JsonElement el)
    {
        // Decode the styled-text data array to a plain string for the log
        var sb = new StringBuilder();
        if (el.TryGetProperty("data", out var dataEl))
            foreach (var part in dataEl.EnumerateArray())
                if (part.TryGetProperty("text", out var textEl))
                    sb.Append(textEl.GetString());

        string text = sb.ToString().Trim();
        if (text.Length > 0) PrintMessage?.Invoke(text);

        string? msgType = el.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

        // Fire hint-specific events
        if (msgType?.Equals("Hint", StringComparison.OrdinalIgnoreCase) == true &&
            text.Length > 0)
        {
            HintReceived?.Invoke(text);
            var entry = ParseHintEntry(el, text);
            if (entry != null) HintEntryReceived?.Invoke(entry);
        }

        // Structured subtype events — additive: the plain-text log above keeps
        // firing for every type. Malformed/missing fields skip the event silently.
        try { RoutePrintJsonSubtype(el, msgType); }
        catch { /* malformed PrintJSON — the plain-text path already handled it */ }
    }

    private void RoutePrintJsonSubtype(JsonElement el, string? msgType)
    {
        switch (msgType)
        {
            case "ItemSend":
            {
                // receiving = destination slot; item is a NetworkItem whose
                // player field = the slot whose world contained it (the finder)
                if (!el.TryGetProperty("receiving", out var recvEl) ||
                    !el.TryGetProperty("item",      out var itemEl)) break;

                ItemSendReceived?.Invoke(
                    recvEl.GetInt32(),
                    itemEl.TryGetProperty("player",   out var sp)  ? sp.GetInt32()  : 0,
                    itemEl.TryGetProperty("item",     out var ii)  ? ii.GetInt64()  : 0,
                    itemEl.TryGetProperty("flags",    out var ifl) ? ifl.GetInt32() : 0,
                    itemEl.TryGetProperty("location", out var il)  ? il.GetInt64()  : 0);
                break;
            }

            case "Countdown":
                if (el.TryGetProperty("countdown", out var cd))
                    CountdownTick?.Invoke(cd.GetInt32());
                break;

            case "Goal":
                if (el.TryGetProperty("slot", out var gs))
                    GoalAnnounced?.Invoke(gs.GetInt32());
                break;

            case "Release":
                if (el.TryGetProperty("slot", out var rs))
                    ReleaseAnnounced?.Invoke(rs.GetInt32());
                break;

            case "Collect":
                if (el.TryGetProperty("slot", out var cs))
                    CollectAnnounced?.Invoke(cs.GetInt32());
                break;
        }
    }

    private HintEntry? ParseHintEntry(JsonElement el, string rawText)
    {
        try
        {
            int  receiverSlot = el.TryGetProperty("receiving", out var r) ? r.GetInt32()   : 0;
            bool isChecked    = el.TryGetProperty("found",     out var f) && f.GetBoolean();
            long itemId       = 0;
            long locationId   = 0;
            int  senderSlot   = 0;
            int  flags        = 0;

            if (el.TryGetProperty("item", out var itemEl))
            {
                itemId     = itemEl.TryGetProperty("item",     out var ii)  ? ii.GetInt64()  : 0;
                locationId = itemEl.TryGetProperty("location", out var il)  ? il.GetInt64()  : 0;
                senderSlot = itemEl.TryGetProperty("player",   out var ip)  ? ip.GetInt32()  : 0;
                flags      = itemEl.TryGetProperty("flags",    out var ifl) ? ifl.GetInt32() : 0;
            }

            string receiverName = "", itemName = "", senderName = "", locationName = "";

            // Walk the data array to extract display names from annotated parts
            if (el.TryGetProperty("data", out var dataEl))
            {
                foreach (var part in dataEl.EnumerateArray())
                {
                    string? partType = part.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                    string  partText = part.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";

                    switch (partType)
                    {
                        case "player_id":
                            if (part.TryGetProperty("slot", out var slotEl))
                            {
                                int slot = slotEl.GetInt32();
                                if      (slot == receiverSlot && receiverName.Length == 0) receiverName = partText;
                                else if (slot == senderSlot   && senderName.Length   == 0) senderName   = partText;
                            }
                            break;
                        case "item_id"     when itemName.Length     == 0: itemName     = partText; break;
                        case "location_id" when locationName.Length == 0: locationName = partText; break;
                    }
                }
            }

            return new HintEntry
            {
                ReceiverName = receiverName,
                ItemName     = itemName.Length     > 0 ? itemName     : $"Item #{itemId}",
                SenderName   = senderName,
                LocationName = locationName.Length > 0 ? locationName : $"Location #{locationId}",
                IsChecked    = isChecked,
                ItemFlags    = flags,
                RawText      = rawText,
                IsForMe      = receiverSlot == _slot,
                IsOurs       = senderSlot   == _slot,
            };
        }
        catch (Exception ex)
        {
            PrintMessage?.Invoke($"[AP] Warning: could not parse PrintJSON: {ex.Message}");
            return null;
        }
    }

    private void HandleBounced(JsonElement el)
    {
        try
        {
            // Only DeathLink bounces are interesting to us
            bool deathLink = false;
            if (el.TryGetProperty("tags", out var tagsEl))
                foreach (var tag in tagsEl.EnumerateArray())
                    if (tag.GetString() == "DeathLink") { deathLink = true; break; }
            if (!deathLink) return;

            if (!el.TryGetProperty("data", out var dataEl)) return;

            string source = dataEl.TryGetProperty("source", out var srcEl) ? srcEl.GetString() ?? "" : "";
            string cause  = dataEl.TryGetProperty("cause",  out var cEl)   ? cEl.GetString()   ?? "" : "";
            double time   = dataEl.TryGetProperty("time",   out var tEl)   ? tEl.GetDouble()   : 0.0;

            // Echo guard: we carry the DeathLink tag ourselves, so our own
            // Bounce comes back to us — drop our slot name and the exact
            // timestamp of the last death we sent.
            if (source == _session.SlotName)        return;
            if (time   == _lastDeathLinkSentTime)   return;

            DeathLinkReceived?.Invoke(source, cause);
        }
        catch (Exception ex)
        {
            PrintMessage?.Invoke($"[AP] Warning: could not parse Bounced packet: {ex.Message}");
        }
    }

    private void HandleRetrieved(JsonElement el)
    {
        // Reply to our connect-time Get — keys maps each key to its stored value
        if (!el.TryGetProperty("keys", out var keysEl) ||
            keysEl.ValueKind != JsonValueKind.Object) return;

        if (keysEl.TryGetProperty(HintsStorageKey, out var hintsEl) &&
            hintsEl.ValueKind == JsonValueKind.Array)
            HintsRetrieved?.Invoke(hintsEl.Clone());   // Clone — outlives the JsonDocument
    }

    private void HandleSetReply(JsonElement el)
    {
        // Live data-storage update from our SetNotify subscription
        if (!el.TryGetProperty("key", out var keyEl) ||
            keyEl.ValueKind != JsonValueKind.String ||
            keyEl.GetString() != HintsStorageKey) return;

        if (el.TryGetProperty("value", out var valEl) &&
            valEl.ValueKind == JsonValueKind.Array)
            HintsRetrieved?.Invoke(valEl.Clone());     // Clone — outlives the JsonDocument
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        string json  = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        RawMessage?.Invoke(json, true);

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws?.State != WebSocketState.Open) return;   // re-check under the lock
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void SetState(ApConnectionState state)
    {
        if (state == ApConnectionState.Disconnected)
        {
            ConnectedGame    = null;
            ConnectedChecked = Array.Empty<long>();
            ConnectedMissing = Array.Empty<long>();
            HintPoints       = 0;
        }
        State = state;
        StateChanged?.Invoke(state);
    }

    // ── IAsyncDisposable ─────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
