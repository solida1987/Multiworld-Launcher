using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Worlds;

/// Star Fox 64's Archipelago client, built into London.
///
/// ⭐ THE POINT: the player never opens somebody else's window. Archipelago's
/// own client for this game is a separate program with its own connect bar,
/// and starting it -- visible or hidden -- is not a solution, it is the
/// problem wearing a hat. So the transport was measured in the world's own
/// source (MIT; the licence is the permission) and reimplemented here.
///
/// ── The wire ────────────────────────────────────────────────────────────
/// The ROM keeps two mailboxes in RDRAM. connector_sf64_bizhawk.lua reads the
/// pointers at 0x80400000 (in) and 0x80400004 (out), and tunnels whatever it
/// finds over TCP to 127.0.0.1:24420 -- which is what this class listens on.
/// So the Lua is a dumb pipe and every decision lives here.
///
/// Every frame, both ways:
///
///     [u16 size][u16 cmd][payload...]        big-endian
///
/// where size counts the cmd field too, i.e. size = 2 + payload.Length. The
/// emulator side reads size+2 bytes in total.
///
/// ── The conversation ────────────────────────────────────────────────────
/// The ROM opens with HANDSHAKE carrying its own version and "HELO"; we answer
/// with the same version and "'LO!". It then PINGs, we PONG, and only then does
/// the seed's state go down: SEED, OPTIONS, READY, LOCATIONS, ITEMS. After
/// that it is a steady state -- the ROM reports checks, we push items.
///
/// ⚠ The order of that opening burst is not decoration. The ROM applies
/// OPTIONS before it will accept ITEMS, and sending items first drops them.
internal sealed class StarFox64Relay : IAsyncDisposable
{
    public const int Port = 0x5F64;          // 24420, the world's own choice

    private enum Cmd : ushort
    {
        None = 0, Handshake = 1, Ping = 2, Pong = 3, Seed = 4, Options = 5,
        Ready = 6, Locations = 7, Items = 8, DeathLink = 9, RingLink = 10,
        Message = 11,
    }

    private enum Phase { Disconnected, Connecting, Connected }

    private readonly Action<string> _log;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Phase _phase = Phase.Disconnected;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Queue<string> _messages = new();
    private readonly object _msgLock = new();

    /// Session facts, supplied by the plugin. Nulls are tolerated: the ROM is
    /// told what we know and the rest stays at zero rather than refusing.
    public Func<int>? GetSlot { get; set; }
    public Func<string?>? GetSeedName { get; set; }
    public Func<JsonElement?>? GetSlotData { get; set; }
    public Func<long[]>? GetCheckedLocations { get; set; }
    public Func<long[]>? GetReceivedItems { get; set; }

    public event Action<long[]>? LocationsChecked;
    public event Action? GoalCompleted;

    public bool RomAttached => _phase == Phase.Connected;

    public StarFox64Relay(Action<string>? log = null)
        => _log = log ?? (_ => { });

    /// ⚠⚠ RETURNS AS SOON AS THE SOCKET IS OPEN -- it does not return the
    /// accept loop. The first version handed the caller that loop, which never
    /// finishes, so `await relay.StartAsync(ct)` blocked the launch forever:
    /// the port was listening and the emulator was never started. The bind is
    /// synchronous on purpose, so a port already in use throws HERE, where the
    /// caller can tell the player what to close.
    public void Start(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _log($"[SF64] listening on 127.0.0.1:{Port}");
        _ = Task.Run(() => AcceptLoopAsync(ct), ct);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log($"[SF64] accept failed: {ex.Message}"); return; }

            // One emulator at a time. A second connection means the previous
            // session died without closing -- take the new one.
            try { _client?.Close(); } catch { }
            _client = client;
            _stream = client.GetStream();
            _phase = Phase.Disconnected;
            _log("[SF64] emulator connected");

            try { await ReadLoopAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log($"[SF64] session ended: {ex.Message}"); }

            _phase = Phase.Disconnected;
            _log("[SF64] emulator disconnected");
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var head = new byte[4];
        while (!ct.IsCancellationRequested && _stream != null)
        {
            if (!await ReadExactlyAsync(head, 0, 4, ct)) return;

            int size = (head[0] << 8) | head[1];
            var cmd = (Cmd)((head[2] << 8) | head[3]);

            // The world's own client refuses anything outside this range, and
            // so do we: a desynchronised stream reads as an enormous length
            // and would otherwise hang the read forever.
            int payloadLen = size - 2;
            if (payloadLen < 0 || payloadLen > 512)
            {
                _log($"[SF64] invalid packet (size {size}) -- dropping the connection");
                return;
            }

            var payload = new byte[payloadLen];
            if (payloadLen > 0 && !await ReadExactlyAsync(payload, 0, payloadLen, ct)) return;

            await HandleAsync(cmd, payload, ct);
        }
    }

    private async Task HandleAsync(Cmd cmd, byte[] data, CancellationToken ct)
    {
        switch (_phase)
        {
            case Phase.Disconnected when cmd == Cmd.Handshake:
            {
                if (data.Length < 8)
                {
                    _log("[SF64] handshake too short");
                    return;
                }
                uint v = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
                if (v != StarFox64Ids.RomVersion)
                {
                    // Worth saying loudly: it means the ROM was patched by a
                    // different release of the world than the one installed.
                    _log($"[SF64] ROM version mismatch: launcher expects "
                       + $"0x{StarFox64Ids.RomVersion:X6}, ROM says 0x{v:X6}");
                    return;
                }
                if (Encoding.ASCII.GetString(data, 4, 4) != "HELO")
                {
                    _log("[SF64] unexpected handshake payload");
                    return;
                }

                var reply = new byte[8];
                WriteU32(reply, 0, StarFox64Ids.RomVersion);
                Encoding.ASCII.GetBytes("'LO!", 0, 4, reply, 4);
                await SendAsync(Cmd.Handshake, reply, ct);
                _phase = Phase.Connecting;
                return;
            }

            case Phase.Connecting when cmd == Cmd.Ping:
            {
                await SendAsync(Cmd.Pong, Array.Empty<byte>(), ct);
                await SendSeedAsync(ct);
                await SendOptionsAsync(ct);
                await SendAsync(Cmd.Ready, Array.Empty<byte>(), ct);
                await SendLocationsAsync(GetCheckedLocations?.Invoke() ?? Array.Empty<long>(), ct);
                await SendItemsAsync(GetReceivedItems?.Invoke() ?? Array.Empty<long>(), ct);
                _phase = Phase.Connected;
                _log("[SF64] ROM ready — checks and items are flowing");
                return;
            }

            case Phase.Connected:
                switch (cmd)
                {
                    case Cmd.Ping:
                        await SendAsync(Cmd.Pong, Array.Empty<byte>(), ct);
                        return;

                    case Cmd.Locations:
                    {
                        var ids = new List<long>();
                        bool goal = false;
                        for (int i = 0; i + 4 <= data.Length; i += 4)
                        {
                            long id = (data[i] << 24) | (data[i + 1] << 16)
                                    | (data[i + 2] << 8) | data[i + 3];
                            // ⚠ The goal is a LOCATION here. Forwarding it as a
                            // check would report an id the server does not have
                            // and the goal would never be recorded.
                            if (id == StarFox64Ids.GoalLocationId) goal = true;
                            else ids.Add(id);
                        }
                        if (ids.Count > 0)
                        {
                            _log($"[SF64] {ids.Count} check(s) from the game");
                            try { LocationsChecked?.Invoke(ids.ToArray()); }
                            catch (Exception ex) { _log($"[SF64] check handler: {ex.Message}"); }
                        }
                        if (goal)
                        {
                            _log("[SF64] goal completed");
                            try { GoalCompleted?.Invoke(); }
                            catch (Exception ex) { _log($"[SF64] goal handler: {ex.Message}"); }
                        }
                        return;
                    }

                    case Cmd.Message:
                    {
                        // The ROM asks for the next line to draw on screen; it
                        // keeps asking as long as we keep answering.
                        string? next = null;
                        lock (_msgLock) { if (_messages.Count > 0) next = _messages.Dequeue(); }
                        if (next != null) await SendMessageAsync(next, ct);
                        return;
                    }

                    // DeathLink and RingLink ride on the AP session's own tags,
                    // which London does not carry for this game yet. Answering
                    // nothing is correct -- the ROM does not wait on a reply.
                    case Cmd.DeathLink:
                    case Cmd.RingLink:
                        return;

                    default:
                        _log($"[SF64] unexpected packet: {cmd}");
                        return;
                }
        }
    }

    // ── outbound ────────────────────────────────────────────────────────────

    /// Tell the ROM which multiworld it is part of.
    ///
    /// ⚠ Team is 0. London does not track teams, and every seed it makes has
    /// one; a wrong team here would only matter in a multi-team room.
    private Task SendSeedAsync(CancellationToken ct)
    {
        string? seed = GetSeedName?.Invoke();
        if (string.IsNullOrEmpty(seed)) return Task.CompletedTask;

        int slot = GetSlot?.Invoke() ?? 0;
        var name = Encoding.ASCII.GetBytes(seed);
        var body = new byte[4 + name.Length];
        body[0] = 0; body[1] = 0;                       // team
        body[2] = (byte)(slot >> 8); body[3] = (byte)slot;
        Buffer.BlockCopy(name, 0, body, 4, name.Length);
        return SendAsync(Cmd.Seed, body, ct);
    }

    /// The seed's options, as (id, value) pairs the ROM understands.
    ///
    /// ⚠ Options the world does not name are skipped rather than guessed: an
    /// unknown id would be written into whatever the ROM keeps at that index.
    private Task SendOptionsAsync(CancellationToken ct)
    {
        JsonElement? slotData = GetSlotData?.Invoke();
        if (slotData is null) return Task.CompletedTask;

        if (!slotData.Value.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Object)
            return Task.CompletedTask;

        var body = new List<byte>();
        foreach (var p in options.EnumerateObject())
        {
            if (!StarFox64Ids.OptionIds.TryGetValue(p.Name, out ushort id)) continue;
            int value = p.Value.ValueKind switch
            {
                JsonValueKind.Number => p.Value.TryGetInt32(out int n) ? n : 0,
                JsonValueKind.True   => 1,
                JsonValueKind.False  => 0,
                _ => 0,
            };
            body.Add((byte)(id >> 8));    body.Add((byte)id);
            body.Add((byte)(value >> 8)); body.Add((byte)value);
        }
        return SendChunkedAsync(Cmd.Options, body.ToArray(), 4, ct);
    }

    public Task SendLocationsAsync(long[] locations, CancellationToken ct)
    {
        if (locations.Length == 0) return Task.CompletedTask;
        var body = new byte[locations.Length * 4];
        for (int i = 0; i < locations.Length; i++) WriteU32(body, i * 4, (uint)locations[i]);
        return SendChunkedAsync(Cmd.Locations, body, 4, ct);
    }

    public Task SendItemsAsync(long[] itemIds, CancellationToken ct)
    {
        if (itemIds.Length == 0) return Task.CompletedTask;
        var body = new byte[itemIds.Length * 4];
        for (int i = 0; i < itemIds.Length; i++) WriteU32(body, i * 4, (uint)itemIds[i]);
        return SendChunkedAsync(Cmd.Items, body, 4, ct);
    }

    /// Queue a line for the ROM to draw. It is shown only while the ROM keeps
    /// asking (Cmd.Message), so a queue is the right shape, not a push.
    public void QueueMessage(string message)
    {
        lock (_msgLock)
        {
            if (_messages.Count > 32) _messages.Dequeue();   // never grow without bound
            _messages.Enqueue(message);
        }
    }

    private Task SendMessageAsync(string message, CancellationToken ct)
    {
        // The ROM's font is upper-case only and the string is NUL-terminated.
        var text = Encoding.ASCII.GetBytes(message.ToUpperInvariant());
        var body = new byte[text.Length + 1];
        Buffer.BlockCopy(text, 0, body, 0, text.Length);
        return SendAsync(Cmd.Message, body, ct);
    }

    /// ⚠ The ROM's mailbox is 512 bytes. Anything longer must go as several
    /// frames, split on an element boundary -- a payload cut mid-id would be
    /// read as two different ids.
    private async Task SendChunkedAsync(Cmd cmd, byte[] body, int elementSize,
                                        CancellationToken ct)
    {
        const int max = 510;                       // not counting the cmd field
        int chunk = max - max % elementSize;
        if (body.Length == 0) { await SendAsync(cmd, body, ct); return; }
        for (int i = 0; i < body.Length; i += chunk)
        {
            int n = Math.Min(chunk, body.Length - i);
            var part = new byte[n];
            Buffer.BlockCopy(body, i, part, 0, n);
            await SendAsync(cmd, part, ct);
        }
    }

    private async Task SendAsync(Cmd cmd, byte[] payload, CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null) return;

        var frame = new byte[4 + payload.Length];
        int size = 2 + payload.Length;             // size counts the cmd field
        frame[0] = (byte)(size >> 8); frame[1] = (byte)size;
        frame[2] = (byte)((ushort)cmd >> 8); frame[3] = (byte)(ushort)cmd;
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);

        await _writeLock.WaitAsync(ct);
        try { await stream.WriteAsync(frame, ct); await stream.FlushAsync(ct); }
        catch (Exception ex) { _log($"[SF64] write failed: {ex.Message}"); }
        finally { _writeLock.Release(); }
    }

    private async Task<bool> ReadExactlyAsync(byte[] buf, int off, int count,
                                              CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null) return false;
        int read = 0;
        while (read < count)
        {
            int n;
            try { n = await stream.ReadAsync(buf.AsMemory(off + read, count - read), ct); }
            catch { return false; }
            if (n <= 0) return false;              // the emulator closed the pipe
            read += n;
        }
        return true;
    }

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    public async ValueTask DisposeAsync()
    {
        try { _client?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        _writeLock.Dispose();
        await Task.CompletedTask;
    }
}
