using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MoonSharp.Interpreter;

namespace LauncherV2.Core.Nwa;

// ═══════════════════════════════════════════════════════════════════════════════
// Snes9xLuaBridge — runs the SAME per-game AP Lua modules (Plugins/Scripts/games/
// *.lua) that the BizHawk connector runs, but launcher-side, over NWA.
//
// snes9x-emunwa exposes memory over the NWA TCP protocol and runs NO in-emulator
// script, so the launcher must BE the connector: load the game module, give it a
// `memory` global that reads/writes emulated RAM via NwaClient, and drive its
// poll()/is_goal_complete()/receive_item() contract each tick. The module code is
// byte-for-byte the one already mock-verified on BizHawk — one logic source, two
// transports (§14 / EMULATOR_MATRIX §6.1). MoonSharp is a pure-managed Lua 5.2
// interpreter (no native/packed blob → AV-neutral).
//
// PERFORMANCE: a single poll() does 200+ memory reads (e.g. alttp's 220 dungeon
// rooms). Doing each as its own NWA round-trip would be unusably slow, so the
// loop snapshots all of WRAM (128 KB) ONCE per tick with one CORE_READ and the
// `memory` shim serves WRAM reads from that buffer; only the rare SRAM read (the
// "AP" cartridge signature) and item WRITES hit the socket.
//
// VERIFICATION: exercised end-to-end against a mock NWA server with a synthetic
// WRAM (Tools/NwaSelfTest) — a set check bit makes the real alttp.lua emit the
// right AP location id. The LIVE confirmation (real snes9x-emunwa + real ROM) is
// the owner's in-emulator gate, same as BizHawk+Emerald.
// ═══════════════════════════════════════════════════════════════════════════════

/// Multiworld context handed to a game module's init(ctx).
public sealed class BridgeConfig
{
    public int SlotNumber { get; init; }
    /// All of this slot's server location ids (the module's `wanted` filter).
    public IReadOnlyList<long> Locations { get; init; } = Array.Empty<long>();
    /// slot_data option flags the module reads (e.g. remote_items). Values are
    /// bool/long/string. Null/empty is fine — modules guard for it.
    public IReadOnlyDictionary<string, object?>? SlotData { get; init; }
}

public sealed class Snes9xLuaBridge : IDisposable
{
    private const int WramSize = 0x20000;          // SNES WRAM = 128 KB
    private const int TickMs   = 50;               // 20 Hz check polling

    private readonly NwaClient _nwa;
    private readonly Action<string>? _log;
    private readonly Script _script;
    private readonly Table  _module;
    private readonly byte[] _wram = new byte[WramSize];
    private readonly ConcurrentQueue<ItemMeta> _incoming = new();

    private CancellationTokenSource? _cts;
    private volatile bool _disconnected;

    /// AP location ids the module reports as newly checked (one event per tick
    /// that produced any). Wired to the plugin's own LocationsChecked.
    public event Action<long[]>? LocationsChecked;
    /// Raised once when the module reports the goal complete.
    public event Action? GoalCompleted;
    /// Raised when the NWA link drops (emulator closed / unreadable).
    public event Action<string>? Disconnected;

    private readonly record struct ItemMeta(
        long Id, long Index, long Player, long Flags, long Location);

    public Snes9xLuaBridge(NwaClient nwa, string moduleLuaPath, BridgeConfig config,
                           Action<string>? log = null)
    {
        _nwa = nwa;
        _log = log;

        // Pure-managed interpreter. Default preset gives string/math/table/os —
        // everything our modules use; the file/io globals they DON'T use are
        // harmless (the modules are our own trusted code, not user input).
        _script = new Script(CoreModules.Preset_Default);
        InjectMemoryApi();
        InjectHelpers();

        // Load the module (returns table M) and run its init(ctx).
        DynValue ret = _script.DoString(File.ReadAllText(moduleLuaPath),
                                        codeFriendlyName: Path.GetFileName(moduleLuaPath));
        if (ret.Type != DataType.Table)
            throw new InvalidOperationException(
                $"Lua module '{Path.GetFileName(moduleLuaPath)}' did not return a table.");
        _module = ret.Table;

        var ctx = BuildContext(config);
        CallModule("init", ctx);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// Queue one received AP item for delivery on the next tick (mirrors the
    /// connector's ITEM line — index is the absolute position in the slot stream).
    public void EnqueueItem(long itemId, long index, long player, long flags, long locationId)
        => _incoming.Enqueue(new ItemMeta(itemId, index, player, flags, locationId));

    /// Start the per-tick loop on a background task.
    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => LoopBody(_cts.Token));
    }

    // ── Tick loop ───────────────────────────────────────────────────────────────

    private void LoopBody(CancellationToken ct)
    {
        var seen = new HashSet<long>();   // session dedup of reported checks
        bool goalSent = false;

        while (!ct.IsCancellationRequested && !_disconnected)
        {
            try
            {
                // 1. One CORE_READ of all WRAM → the per-tick snapshot the memory
                //    shim serves reads from.
                byte[] snap = _nwa.ReadMemoryAsync("WRAM", 0, WramSize, ct)
                                  .GetAwaiter().GetResult();
                Buffer.BlockCopy(snap, 0, _wram, 0, Math.Min(snap.Length, WramSize));

                // 2. poll() → newly-checked AP location ids.
                var newChecks = new List<long>();
                DynValue polled = CallModule("poll");
                if (polled.Type == DataType.Table)
                {
                    var t = polled.Table;
                    for (int i = 1; i <= t.Length; i++)
                    {
                        var v = t.Get(i);
                        if (v.Type == DataType.Number)
                        {
                            long id = (long)v.Number;
                            if (seen.Add(id)) newChecks.Add(id);
                        }
                    }
                }
                if (newChecks.Count > 0)
                {
                    _log?.Invoke($"[snes9x] checks: {string.Join(",", newChecks)}");
                    LocationsChecked?.Invoke(newChecks.ToArray());
                }

                // 3. is_goal_complete() → GOAL once.
                if (!goalSent)
                {
                    DynValue g = CallModule("is_goal_complete");
                    if (g.Type == DataType.Boolean && g.Boolean)
                    {
                        goalSent = true;
                        _log?.Invoke("[snes9x] goal complete");
                        GoalCompleted?.Invoke();
                    }
                }

                // 4. Deliver queued items: module.receive_item(id, meta).
                while (_incoming.TryDequeue(out var it))
                    CallModule("receive_item", DynValue.NewNumber(it.Id), MetaTable(it));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _disconnected = true;
                _log?.Invoke($"[snes9x] bridge stopped: {ex.Message}");
                Disconnected?.Invoke(ex.Message);
                break;
            }

            try { Task.Delay(TickMs, ct).Wait(ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Lua glue ──────────────────────────────────────────────────────────────

    private DynValue CallModule(string fn, params DynValue[] args)
    {
        DynValue f = _module.Get(fn);
        if (f.Type != DataType.Function) return DynValue.Nil;
        try { return _script.Call(f, args); }
        catch (Exception ex) { _log?.Invoke($"[snes9x] {fn}() error: {ex.Message}"); return DynValue.Nil; }
    }

    private DynValue MetaTable(ItemMeta it)
    {
        var t = new Table(_script);
        t["id"]     = it.Id;
        t["index"]  = it.Index;
        t["player"] = it.Player;
        t["flags"]  = it.Flags;
        // Provide BOTH key spellings the modules read (connector uses `location`,
        // some modules read `locId`) so item delivery works either way.
        t["location"] = it.Location;
        t["locId"]    = it.Location;
        return DynValue.NewTable(t);
    }

    /// The ctx passed to module.init: { config = {...}, json_decode = fn, log = fn }.
    private DynValue BuildContext(BridgeConfig cfg)
    {
        var config = new Table(_script);
        config["slot_number"] = cfg.SlotNumber;

        var locs = new Table(_script);
        for (int i = 0; i < cfg.Locations.Count; i++) locs[i + 1] = (double)cfg.Locations[i];
        config["locations"] = locs;

        if (cfg.SlotData is { Count: > 0 })
        {
            var sd = new Table(_script);
            foreach (var kv in cfg.SlotData)
                sd[kv.Key] = kv.Value switch
                {
                    bool b   => DynValue.NewBoolean(b),
                    long l   => DynValue.NewNumber(l),
                    int i    => DynValue.NewNumber(i),
                    double d => DynValue.NewNumber(d),
                    string s => DynValue.NewString(s),
                    _        => DynValue.Nil,
                };
            config["slot_data"] = sd;
        }

        var ctx = new Table(_script);
        ctx["config"]      = config;
        ctx["json_decode"] = (Func<string, DynValue>)(s => DynValue.Nil);  // modules guard for nil
        ctx["log"]         = (Action<string>)(m => _log?.Invoke(m));
        return DynValue.NewTable(ctx);
    }

    // ── memory.* shim (BizHawk-compatible names, NWA-backed) ────────────────────

    private void InjectMemoryApi()
    {
        var mem = new Table(_script);
        mem["read_u8"]      = DynValue.NewCallback((_, a) => DynValue.NewNumber(ReadU8(Addr(a), Domain(a, 1))));
        mem["read_u16_le"]  = DynValue.NewCallback((_, a) => DynValue.NewNumber(ReadU16(Addr(a), Domain(a, 1))));
        mem["write_u8"]     = DynValue.NewCallback((_, a) => { WriteU8(Addr(a), (int)a[1].Number, Domain(a, 2)); return DynValue.Nil; });
        mem["write_u16_le"] = DynValue.NewCallback((_, a) => { WriteU16(Addr(a), (int)a[1].Number, Domain(a, 2)); return DynValue.Nil; });
        _script.Globals["memory"] = mem;
    }

    private void InjectHelpers()
    {
        // A couple of modules log through a global; harmless if unused.
        var console = new Table(_script);
        console["log"] = (Action<string>)(m => _log?.Invoke(m));
        _script.Globals["console"] = console;
    }

    private static int Addr(CallbackArguments a) => (int)a[0].Number;

    /// The domain argument (index `i`) → NWA region. WRAM is default; CARTRAM and
    /// SRAM both map to the NWA "SRAM" region (cartridge battery RAM).
    private static string Domain(CallbackArguments a, int i)
    {
        string d = a.Count > i && a[i].Type == DataType.String ? a[i].String : "WRAM";
        return d.Equals("CARTRAM", StringComparison.OrdinalIgnoreCase) ||
               d.Equals("SRAM",    StringComparison.OrdinalIgnoreCase) ? "SRAM" : "WRAM";
    }

    private int ReadU8(int addr, string region)
    {
        if (region == "WRAM")
            return (addr >= 0 && addr < _wram.Length) ? _wram[addr] : 0;
        byte[] b = _nwa.ReadMemoryAsync("SRAM", addr, 1).GetAwaiter().GetResult();
        return b.Length > 0 ? b[0] : 0;
    }

    private int ReadU16(int addr, string region)
    {
        if (region == "WRAM")
        {
            if (addr < 0 || addr + 1 >= _wram.Length) return 0;
            return _wram[addr] | (_wram[addr + 1] << 8);
        }
        byte[] b = _nwa.ReadMemoryAsync("SRAM", addr, 2).GetAwaiter().GetResult();
        return b.Length >= 2 ? b[0] | (b[1] << 8) : 0;
    }

    private void WriteU8(int addr, int value, string region)
    {
        byte[] data = { (byte)(value & 0xFF) };
        _nwa.WriteMemoryAsync(region, addr, data).GetAwaiter().GetResult();
        if (region == "WRAM" && addr >= 0 && addr < _wram.Length) _wram[addr] = data[0];
    }

    private void WriteU16(int addr, int value, string region)
    {
        byte[] data = { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
        _nwa.WriteMemoryAsync(region, addr, data).GetAwaiter().GetResult();
        if (region == "WRAM" && addr >= 0 && addr + 1 < _wram.Length)
        { _wram[addr] = data[0]; _wram[addr + 1] = data[1]; }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
    }
}
