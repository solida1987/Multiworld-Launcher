using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LauncherV2.Core;

// Ask the server which game a slot belongs to, BEFORE committing to a game.
//
// The launcher used to require that you pick the right game in the library
// first, because the Connect packet has to name a game and it took that name
// from the selected plugin. Pick the wrong one and the server answers
// InvalidGame -- which reads as "my slot name is wrong" and is impossible to
// act on, since nothing on screen says which game the slot actually is.
//
// It does not have to work that way. A client that connects with an EMPTY game
// and the "Tracker" tag is admitted without a game check, and the Connected
// packet carries slot_info, which names every slot's game. Measured against
// 0.6.7 -- an empty game with any other tag combination is still InvalidGame,
// so the tag is what makes this legal, not the empty string.
//
// So: probe first, learn the game, then connect for real as that game.
public sealed record ApSlotProbeResult(
    string? Game,          // the slot's game, when the probe got in
    string[]? Refusal,     // AP refusal codes, when it did not
    string? Error);        // transport-level failure text

public static class ApSlotProbe
{
    public static async Task<ApSlotProbeResult> ResolveGameAsync(
        string serverUri, string slotName, string password, CancellationToken ct = default)
    {
        // Same scheme handling as ApClient: an explicit scheme is honoured,
        // a bare host:port tries TLS first and falls back to plain.
        bool explicitScheme =
            serverUri.StartsWith("ws://",  StringComparison.OrdinalIgnoreCase) ||
            serverUri.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
        string[] candidates = explicitScheme
            ? new[] { serverUri }
            : new[] { "wss://" + serverUri, "ws://" + serverUri };

        Exception? last = null;
        foreach (string uri in candidates)
        {
            using var ws = new ClientWebSocket();
            try { await ws.ConnectAsync(new Uri(uri), ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; continue; }

            try { return await TalkAsync(ws, slotName, password, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; }
        }
        return new ApSlotProbeResult(null, null, last?.Message ?? "Could not reach the server.");
    }

    private static async Task<ApSlotProbeResult> TalkAsync(
        ClientWebSocket ws, string slotName, string password, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        await ReadAsync(ws, timeout.Token);                    // RoomInfo

        var connect = new object[]
        {
            new
            {
                cmd            = "Connect",
                game           = "",                            // the whole point
                name           = slotName,
                password       = password,
                uuid           = Guid.NewGuid().ToString("N"),
                version        = ApVersion.ClientVersion,
                items_handling = 0,                             // a tracker receives nothing
                tags           = new[] { "Tracker" },           // what makes the empty game legal
                slot_data      = false
            }
        };
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(connect));
        await ws.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token);

        for (int i = 0; i < 8; i++)
        {
            using var doc = JsonDocument.Parse(await ReadAsync(ws, timeout.Token));
            foreach (var msg in doc.RootElement.EnumerateArray())
            {
                if (!msg.TryGetProperty("cmd", out var cmdEl)) continue;
                string cmd = cmdEl.GetString() ?? "";

                if (cmd == "ConnectionRefused")
                {
                    var codes = new List<string>();
                    if (msg.TryGetProperty("errors", out var errs))
                        foreach (var e in errs.EnumerateArray())
                            if (e.ValueKind == JsonValueKind.String) codes.Add(e.GetString()!);
                    return new ApSlotProbeResult(null, codes.ToArray(), null);
                }

                if (cmd != "Connected") continue;

                // slot_info is keyed by slot number as a STRING.
                int slot = msg.TryGetProperty("slot", out var s) ? s.GetInt32() : -1;
                if (msg.TryGetProperty("slot_info", out var info) &&
                    info.ValueKind == JsonValueKind.Object &&
                    info.TryGetProperty(slot.ToString(), out var mine) &&
                    mine.TryGetProperty("game", out var g) &&
                    g.ValueKind == JsonValueKind.String)
                    return new ApSlotProbeResult(g.GetString(), null, null);

                return new ApSlotProbeResult(null, null,
                    "The server let the slot in but did not say which game it is.");
            }
        }
        return new ApSlotProbeResult(null, new[] { "Timeout" }, null);
    }

    // AP frames can arrive split across several WebSocket fragments.
    private static async Task<string> ReadAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var sb  = new StringBuilder();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new IOException("The server closed the connection during login.");
            sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            if (r.EndOfMessage && sb.Length > 0) return sb.ToString();
        }
    }
}
