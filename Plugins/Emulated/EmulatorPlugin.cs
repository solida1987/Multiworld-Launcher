using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;
using LauncherV2.Core.Nwa;

namespace LauncherV2.Plugins.Emulated;

/// Describes a ROM the launcher needs but could not locate or verify itself,
/// so the UI can ask the player to point to it. Carries the EXACT version
/// wanted (and its MD5 when a patch tells us) so the prompt is unambiguous.
public sealed record RomRequirement(
    string  GameName,            // "Pokémon Emerald"
    string  SystemLabel,        // "GBA"
    string  VersionLabel,       // "Pokémon Emerald (USA, Europe) — vanilla dump"
    string? RequiredMd5,        // expected MD5 of the file, or null when unknown
    bool    WrongVersionPresent,// true = a ROM is set but it's the wrong one
    string  FileFilter);        // OpenFileDialog filter for the picker

/// A known-good vanilla ROM identified by its CONTENTS, never its filename —
/// players name their dumps anything. Size is the fast, name-independent
/// detector; the MD5 (when known) confirms the exact dump.
public sealed record RomIdentity(
    long    SizeBytes,          // exact file size of the vanilla cartridge dump
    string? Md5,                // exact MD5, or null to accept any file of this size
    string  Label);            // "Pokémon Emerald (USA, Europe)"

// ═══════════════════════════════════════════════════════════════════════════════
// EmulatorPlugin — base class for ROM-based AP games.
//
// HOW ROM GAMES WORK IN V2
// ─────────────────────────
// When the user selects a ROM game (e.g. Pokemon FireRed):
//
//   1. Launcher checks if the correct emulator (BizHawk) is installed.
//      If not, it downloads and installs it automatically.
//
//   2. User browses to their own ROM file (required — we never ship ROMs).
//      ROM LIBRARY POLICY (§11 — never touch the user's original copy):
//      the chosen file is COPIED into the launcher's own library at
//      Games/ROMs/<GameId>/<original filename> and RomPath points at the
//      COPY. The launcher operates only on its copy — the user's original
//      file is never modified, moved or deleted. A same-name file already
//      in the library is reused when the size matches (same file), and a
//      different file under the same name gets a _2/_3/… suffix instead of
//      overwriting. BizHawk reinstalls wipe only Emulators/BizHawk/ (with
//      user-data preservation, P2-9) — Games/ROMs/ is a separate tree no
//      install/uninstall path ever touches. The copy path is stored in the
//      per-game settings file and remembered per-game.
//
//   3. Launcher starts BizHawk with:
//        - The ROM file loaded
//        - The generic AP connector Lua script injected via --lua=
//          (Plugins/Scripts/bizhawk_ap_connector.lua — BizHawk has native Lua)
//        - AP session credentials + pipe name + per-game module name written
//          to ap_config.json in the BizHawk folder (absolute path also passed
//          to the BizHawk process via the AP_CONFIG_PATH environment variable)
//
//   4. The connector loads the per-game module
//      Plugins/Scripts/games/<LuaModuleName>.lua (memory map + check/goal
//      logic), opens the launcher's named pipe PAIR through the CRT
//      (io.open "\\\\.\\pipe\\<name>_c2s" / "..._s2c") and exchanges
//      newline-framed text — same framing as D2Plugin's pipe bridge, but
//      BYTE mode (CRT file handles cannot read message-mode pipes) and one
//      pipe PER DIRECTION (see PIPE PROTOCOL below).
//
// PIPE PROTOCOL (newline-framed UTF-8 text, TWO byte-mode pipes)
// ───────────────────────────────────────────────────────────────
//   The launcher creates two pipe servers per launch; ap_config.json carries
//   the base name <pipe_name> and the connector opens both ends "r+b":
//     <pipe_name>_c2s   connector → launcher   "CHECK:<id1>,<id2>\n"
//                                              "GOAL\n"
//                                              "SYNC\n"  per-frame item poll
//     <pipe_name>_s2c   launcher → connector   zero or more "ITEM:<id>\n"
//                                              + one "SYNCEND\n", sent ONLY
//                                              as the reply to a SYNC poll
//   Request/response keeps the Lua side's blocking CRT reads bounded to one
//   local round-trip (pure Lua cannot poll a pipe for readability, so
//   unsolicited pushes would stall the emulator).
//
//   WHY TWO PIPES — a single duplex CRT stream ("r+b") obeys the ANSI
//   update-mode rules: fflush between write→read, a seek between
//   read→write. A pipe is not seekable, and the UCRT cannot complete the
//   write→read handover without one: the first read after a write returns
//   an INSTANT EOF with the stream error flag set and errno 0 (reproduced
//   in isolation — this is the "connected → read failed one frame later"
//   bridge drop the single-pipe v1 shipped with). One pipe per direction
//   means each CRT stream only ever moves one way, so the direction rules
//   never engage, on any CRT version. Both pipes are still created duplex
//   (InOut): CRT "wb" carries create/truncate open dispositions that named
//   pipes reject, so "r+b" (GENERIC_READ|WRITE, OPEN_EXISTING) is the one
//   client open mode proven safe — the server grants both directions and
//   each side simply never uses one of them.
//
// BRIDGE DIAGNOSTICS
// ───────────────────
//   Lua side: always-on <EmulatorDirectory>\ap_connector.log (truncated per
//   start, SYNC roundtrips sampled). Launcher side: set AP_BRIDGE_TRACE=1 on
//   the launcher process to mirror the bridge into
//   <EmulatorDirectory>\bridge_trace.log.
//
// WHY BIZHAWK
// ────────────
//   • Lua scripting natively supported — no emulator code changes needed.
//   • Multi-system: GBA, GBC, GB, NES, SNES, N64, Genesis, SMS, Atari 2600 …
//   • AP community already ships BizHawk-based Lua AP clients for many games —
//     we can reuse those scripts directly.
//
// SUBCLASS EXAMPLE (real ones live in Plugins/Emulated/Games/):
//   sealed class PokemonEmeraldPlugin : EmulatorPlugin
//   {
//       public override string GameId         => "pokemon_emerald";
//       public override string DisplayName    => "Pokémon Emerald";
//       public override string ApWorldName    => "Pokemon Emerald";
//       public override string Description    => "…";
//       protected override string RomSystem   => "GBA";
//       protected override string LuaScriptName => "bizhawk_ap_connector.lua";
//       protected override string LuaModuleName => "pokemon_emerald";
//   }
// ═══════════════════════════════════════════════════════════════════════════════

public abstract class EmulatorPlugin : IGamePlugin
{
    // ── IGamePlugin identity (subclass must implement) ────────────────────────
    public abstract string GameId      { get; }
    public abstract string DisplayName { get; }
    public abstract string Subtitle    { get; }
    public abstract string ApWorldName { get; }
    public abstract string Description { get; }

    public string  IconPath          => Path.Combine(AppContext.BaseDirectory, "Assets", $"{GameId}.png");
    public string? VideoPreviewUrl   => null;
    public string[] ScreenshotUrls  => Array.Empty<string>();
    /// Subclasses can override to provide a per-game accent color; default = neutral dark-blue.
    public virtual string ThemeAccentColor => "#1A2A5E";
    /// Requirement badges are STATE-AWARE: a satisfied requirement disappears
    /// instead of nagging forever ("ROM needed" only while no ROM is imported).
    /// The emulator name is NOT a badge — the subtitle already carries it.
    public virtual string[] GameBadges
        => RomPath != null && File.Exists(RomPath)
            ? Array.Empty<string>()
            : new[] { "ROM needed" };

    // ── Emulator specifics (subclass must implement) ──────────────────────────

    /// BizHawk system identifier e.g. "GBA", "GBC", "SNES", "NES", "N64", "GEN"
    protected abstract string RomSystem { get; }

    /// Filename (not path) of the Lua bridge script shipped in Plugins/Scripts/.
    /// The script opens a named pipe and sends CHECK: / receives ITEM: messages.
    /// BizHawk games share the generic "bizhawk_ap_connector.lua".
    protected abstract string LuaScriptName { get; }

    /// Name (no ".lua" extension, no path) of the per-game module in
    /// Plugins/Scripts/games/. The generic connector reads this from
    /// ap_config.json ("lua_module") and loads the module via dofile().
    /// Default = GameId, which matches the shipped module naming convention
    /// (e.g. GameId "pokemon_emerald" → games/pokemon_emerald.lua).
    protected virtual string LuaModuleName => GameId;

    /// True once the per-game Lua module carries a VERIFIED RAM address map
    /// and can actually detect checks. The V2.0 modules ship with empty maps
    /// (they load cleanly and report nothing), so this defaults to false —
    /// the launcher warns at launch so a player never sits in a multiworld
    /// for an hour wondering why no checks are being sent. Each game
    /// overrides this to true when its address map is verified in-game.
    public virtual bool ChecksImplemented => false;

    /// True once the Lua connector attached to this launch's named pipe.
    /// Stays false when the 60 s wait in LaunchAsync timed out — BizHawk is
    /// running but NO check/item traffic will ever flow. The launcher reads
    /// this right after LaunchAsync (same pattern as ChecksImplemented) and
    /// warns instead of letting the session degrade silently (P2-10).
    public bool ConnectorAttached { get; private set; }

    // ── Version state ─────────────────────────────────────────────────────────
    /// ROM games have no mod version of their own — what is "installed" is the
    /// BizHawk emulator, so say exactly that. (The old internal placeholder
    /// "emulator-ready" leaked into the header as
    /// "Installed: emulator-ready" — P3-15.)
    public string? InstalledVersion  => IsEmulatorPresent ? "BizHawk" : null;
    public string? AvailableVersion  { get; protected set; }
    public bool    IsInstalled       => RomPath != null && File.Exists(RomPath) && IsEmulatorPresent;
    public bool    IsRunning         { get; private set; }

    /// Returns the emulator directory (where BizHawk lives) as the game folder.
    public string GameDirectory      => EmulatorDirectory;

    // ── ROM and emulator paths ────────────────────────────────────────────────

    /// Absolute path to this game's ROM — always the launcher-library COPY
    /// under Games/ROMs/<GameId>/ (§11), never the user's original file.
    /// Set via PromptForRomFile (Settings panel + post-install flow).
    public string? RomPath { get; set; }

    /// Launch EmuHawk with --fullscreen (§12). Persisted per-game in
    /// Data/{GameId}_settings.json next to the ROM path.
    public bool StartFullscreen { get; set; }

    /// Root directory where the SELECTED backend is (or will be) installed:
    /// Emulators/&lt;subdir&gt;. Computed (not stored) so install/launch/trace/
    /// config always target the chosen emulator. BizHawk keeps its historical
    /// "Emulators/BizHawk"; snes9x lands in "Emulators/snes9x".
    public string EmulatorDirectory
        => Path.Combine(AppContext.BaseDirectory, "Emulators",
                        ResolveSelectedBackend().InstallSubdir);

    private bool IsEmulatorPresent
        => File.Exists(Path.Combine(EmulatorDirectory, ResolveSelectedBackend().ExeName));

    /// Per-game emulator backend choice (§14). Persisted in the per-game
    /// settings file. Seeded by LoadSettings to the system's default backend
    /// (the first BridgeReady one, i.e. "bizhawk" today) — null only in the
    /// window before LoadSettings runs, which every consumer guards against by
    /// resolving through EmulatorBackends. Only BridgeReady backends are
    /// selectable in the UI, so a non-ready value never lands here through the
    /// dropdown — but LaunchAsync still guards against a stale/hand-edited one.
    public string? SelectedEmulatorId { get; set; }

    /// Backing field initialiser runs after EmulatorDirectory above; uses the
    /// subclass's RomSystem to pick the default. RomSystem is abstract, so this
    /// cannot be a field initialiser — it is seeded lazily on first read of the
    /// resolved backend (and overwritten by LoadSettings when a value persists).
    private EmulatorBackend ResolveSelectedBackend()
    {
        var byId = EmulatorBackends.ById(SelectedEmulatorId);
        // Honest fallback: an unknown or non-ready id (stale settings, a backend
        // that lost BridgeReady, a hand-edit) drops back to the default working
        // backend for this system rather than trying to launch something we
        // cannot actually drive.
        if (byId == null || !byId.BridgeReady)
            return EmulatorBackends.Default(RomSystem);
        return byId;
    }

    /// The emulator backend ids THIS game can run on (§14 — "de forskellige
    /// emulatorer som er supportet af selve spillet"). Default: every backend
    /// that can host the game's system. A subclass narrows this to the emulators
    /// the game's apworld/community actually trusts (e.g. a SNES world lists
    /// bizhawk + snes9x, not Mesen if untested). Order = dropdown order. Unknown
    /// ids are ignored. Returning an empty list falls back to the system default.
    protected virtual IReadOnlyList<string> SupportedEmulatorIds => Array.Empty<string>();

    /// Backends shown in this game's "Emulator" dropdown: the intersection of
    /// SupportedEmulatorIds (when the game declares one) with the backends that
    /// can host its system, in the game's declared order. Falls back to every
    /// backend for the system when the game declares nothing — so behaviour is
    /// unchanged for games that don't override SupportedEmulatorIds.
    public IReadOnlyList<EmulatorBackend> AvailableBackends()
    {
        var forSystem = EmulatorBackends.BackendsForSystem(RomSystem);
        var declared  = SupportedEmulatorIds;
        if (declared.Count == 0) return forSystem;

        var bySystem = forSystem.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);
        var ordered  = new List<EmulatorBackend>();
        foreach (string id in declared)
            if (bySystem.TryGetValue(id, out var b) && !ordered.Contains(b))
                ordered.Add(b);
        // A game that declared only ids its system can't host would end up with an
        // empty dropdown — fall back to the full system list rather than show none.
        return ordered.Count > 0 ? ordered : forSystem;
    }

    // ── Internal state ────────────────────────────────────────────────────────

    private Process?                _emuProcess;
    /// Connector → launcher pipe ("<base>_c2s"): CHECK / GOAL / SYNC.
    private NamedPipeServerStream?  _pipeIn;
    /// Launcher → connector pipe ("<base>_s2c"): ITEM / SYNCEND replies.
    private NamedPipeServerStream?  _pipeOut;
    private CancellationTokenSource? _pipeCts;

    // ── NWA backend (snes9x) state — null unless an NWA backend is active ──────
    private NwaClient?       _nwaClient;
    private Snes9xLuaBridge? _nwaBridge;
    private int              _nwaDelivered;   // monotonic item-delivery cursor

    /// Per-launch pipe name disambiguator: a previous launch's servers may
    /// still be winding down (attach timeout, EmuHawk just killed) when the
    /// next launch creates new ones — same-name single-instance pipes would
    /// collide with IOException "all pipe instances are busy".
    private static int _pipeNameSeq;

    /// SYNC roundtrips answered this session (trace sampling).
    private long _syncCount;

    /// The slot's FULL ordered item stream, by absolute server index.
    /// ReceiveItemsAsync places items at their AP `index`; an index-0 packet
    /// is the server's authoritative full resend (connect/reconnect) and
    /// replaces the list. Kept for the whole AP session — NOT cleared per
    /// launch — because save-state games (Pokémon Emerald's receive buffer
    /// counts processed items inside the save) need the entire backlog from
    /// index 0 replayed every launch so a resumed save can pick up exactly
    /// where its own counter says it stopped.
    private readonly List<ApNetworkItem> _itemsReceived = new();
    private readonly object _itemsLock = new();

    /// Per-launch replay cursor into _itemsReceived: next index to hand to
    /// the Lua connector on its SYNC polls. Reset to 0 by every launch.
    private int _itemCursor;

    // ── AP bridge ────────────────────────────────────────────────────────────
    public event Action<long[]>? LocationsChecked;
    public event Action<int>?    GameExited;

    /// Fired when the Lua script sends a "GOAL" message.
    public event Action? GoalCompleted;

    // ── UI-wired suppliers (assigned by MainWindow per session, same pattern
    //    as D2Plugin.GetSlotData) — let WriteApConfig hand the Lua module the
    //    multiworld context it cannot learn any other way. ─────────────────────

    /// Current AP slot_data (game options baked into the seed), or null.
    public Func<JsonElement?>? GetSlotData { get; set; }

    /// All of this slot's server location ids (checked + missing from the
    /// Connected packet) — the Lua module's `ctx.server_locations` filter.
    public Func<long[]?>? GetServerLocations { get; set; }

    /// Our own slot number in the multiworld (0 when unknown).
    public Func<int>? GetOwnSlot { get; set; }

    /// The connected room's seed name (null when not connected). Patch files
    /// are named AP_<seed>_P<slot>_<player>.<ext> — the patch resolver uses
    /// this to pick the file belonging to THIS multiworld.
    public Func<string?>? GetSeedName { get; set; }

    /// Slot name of the session being launched (set at LaunchAsync entry,
    /// null before the first launch). Lets patch resolvers match a patch
    /// container's player_name against the player actually connecting.
    protected string? CurrentSlotName { get; private set; }

    /// One-line, player-readable note about the ROM chosen for the last
    /// launch (e.g. "AP-patched copy applied" / "vanilla — items cannot be
    /// delivered"). Set by PrepareSessionRomAsync overrides; MainWindow logs
    /// it right after LaunchAsync.
    public string? SessionRomNote { get; protected set; }

    /// The patched ROM actually launched this session (a per-seed library copy),
    /// or null when the vanilla/unpatched library ROM was used. Lets session-end
    /// and goal accounting attribute playtime to the right seed in the
    /// SeedLibraryStore. Set in LaunchAsync once PrepareSessionRomAsync returns.
    public string? ActivePatchedRomPath { get; private set; }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // ROM games don't have "mod versions" — the version is the ROM hash.
        // BizHawk update checking is future work.
        await Task.CompletedTask;
    }

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // Step 1: install BizHawk if not present
        if (!IsEmulatorPresent)
        {
            progress.Report((10, "Downloading BizHawk emulator..."));
            await InstallBizHawkAsync(progress, ct);
        }

        // Step 2: the AP Lua script ships with the launcher — nothing to download
        progress.Report((90, $"Verifying {DisplayName} AP script..."));
        if (!File.Exists(LuaScriptPath))
            throw new FileNotFoundException(
                $"AP Lua script not found: {LuaScriptPath}\n" +
                "Re-install the launcher to restore it.");

        progress.Report((100, "Ready."));
        // InstalledVersion is computed from IsEmulatorPresent — nothing to stamp.
    }

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsEmulatorPresent &&
               RomPath != null && File.Exists(RomPath) &&
               File.Exists(LuaScriptPath);
    }

    public async Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(RomPath) || !File.Exists(RomPath))
            throw new FileNotFoundException(
                $"ROM file not found: {RomPath}\n\n" +
                $"Select your {DisplayName} ROM in Settings → {DisplayName}.");

        CurrentSlotName = session.SlotName;

        // 0. Give the subclass a chance to produce a session-specific ROM
        //    (e.g. Pokémon Emerald applies the seed's .apemerald patch to a
        //    library COPY). Failures fall back to the library ROM — the
        //    subclass explains itself through SessionRomNote.
        SessionRomNote      = null;
        ActivePatchedRomPath = null;
        string launchRom = RomPath;
        try
        {
            string? prepared = await PrepareSessionRomAsync(session, ct);
            if (!string.IsNullOrEmpty(prepared) && File.Exists(prepared))
                launchRom = prepared;
        }
        catch (Exception ex)
        {
            Trace($"session ROM preparation failed: {ex.Message}");
            SessionRomNote ??= $"[{DisplayName}] ROM patching failed ({ex.Message}) — " +
                               "launching the unpatched library ROM.";
        }

        // A per-seed patched copy (anything other than the plain library ROM)
        // is what playtime/goal accounting attributes to a SeedLibraryStore row.
        if (!string.Equals(launchRom, RomPath, StringComparison.OrdinalIgnoreCase))
            ActivePatchedRomPath = launchRom;

        // §14: NWA backends (snes9x-emunwa) launch + bridge over TCP, not the
        // BizHawk Lua named pipes. Branch here so the proven pipe path below is
        // only ever reached by Pipe-dialect backends (BizHawk) — unchanged.
        var nwaBackend = ResolveSelectedBackend();
        if (nwaBackend.Dialect == BridgeDialect.Nwa)
        {
            await LaunchViaNwaAsync(nwaBackend, launchRom, session, ct);
            return;
        }

        // 1. Start the named pipe servers BEFORE BizHawk so the Lua connector
        //    can attach. BYTE mode (not message mode): the Lua side opens the
        //    pipes through the CRT via io.open("...", "r+b"), and CRT file
        //    handles do not speak message-mode framing. Both sides frame
        //    messages with '\n' — PipeLoopAsync reassembles partial/multiple
        //    lines per read. TWO pipes, one per direction, because a single
        //    duplex CRT stream cannot switch read/write direction on a
        //    non-seekable pipe (see PIPE PROTOCOL in the class header).

        // Tear down leftovers from a previous launch first (an attach timeout
        // leaves servers listening with no loop to dispose them).
        _pipeCts?.Cancel();
        DisposeQuietly(_pipeIn);
        DisposeQuietly(_pipeOut);
        _pipeIn  = null;
        _pipeOut = null;

        StartTrace();   // AP_BRIDGE_TRACE=1 → <EmulatorDirectory>\bridge_trace.log

        string pipeBase = $"emu_{GameId}_{Environment.ProcessId}_" +
                          Interlocked.Increment(ref _pipeNameSeq);
        var pipeIn  = CreateBridgePipe(pipeBase + "_c2s");
        var pipeOut = CreateBridgePipe(pipeBase + "_s2c");
        _pipeIn   = pipeIn;
        _pipeOut  = pipeOut;
        _pipeCts  = new CancellationTokenSource();
        _syncCount = 0;
        ConnectorAttached = false;   // per-launch; set when the Lua side attaches
        Trace($"pipe servers created: {pipeBase}_c2s / {pipeBase}_s2c");

        // Rewind the item replay cursor: every launch re-delivers the full
        // ordered stream so a resumed save's own received-count handshake
        // (Pokémon Emerald) finds the items it has not processed yet. The
        // list itself survives — it is the session's server-authoritative
        // item state, refreshed by every index-0 ReceivedItems packet.
        lock (_itemsLock) _itemCursor = 0;

        // 2. Write AP credentials + pipe base name + Lua module name to the
        //    JSON sidecar the connector reads on startup.
        WriteApConfig(session, pipeBase, launchRom);

        // 3. Start the emulator.
        //    §14 plumbing: the launch uses the SELECTED backend's exe/dir. Only
        //    BridgeReady backends are pickable in the UI and BizHawk is the only
        //    one today, so behaviour is unchanged — but a stale/hand-edited
        //    non-ready id falls back to the default working backend (with a log
        //    line) instead of trying to launch an emulator we cannot drive.
        var backend = EmulatorBackends.ById(SelectedEmulatorId);
        if (backend == null || !backend.BridgeReady)
        {
            var fallback = EmulatorBackends.Default(RomSystem);
            Trace($"selected emulator '{SelectedEmulatorId}' is not bridge-ready — " +
                  $"falling back to {fallback.Id} ({fallback.ExeName})");
            backend = fallback;
        }

        //    WorkingDirectory = EmulatorDirectory so the connector's relative
        //    "ap_config.json" fallback open resolves correctly; AP_CONFIG_PATH
        //    carries the absolute path (Lua reads it via os.getenv) as the
        //    primary lookup.
        string bizhawk = Path.Combine(EmulatorDirectory, backend.ExeName);
        var psi = new ProcessStartInfo
        {
            FileName         = bizhawk,
            // No --system flag: BizHawk has no such argument (its ArgParser
            // rejects it with "Unrecognized command or argument") — the core
            // is auto-detected from the ROM file. RomSystem is only used for
            // our own file-picker filters.
            Arguments        = $"\"{launchRom}\" " +
                               $"--lua=\"{LuaScriptPath}\"" +
                               // §12: EmuHawk's own "launch in fullscreen"
                               // switch (BizHawk ArgParser --fullscreen).
                               (StartFullscreen ? " --fullscreen" : ""),
            WorkingDirectory = EmulatorDirectory,
            UseShellExecute  = false,
        };
        psi.EnvironmentVariables["AP_CONFIG_PATH"]
            = Path.Combine(EmulatorDirectory, "ap_config.json");
        var emuProc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start BizHawk.");
        _emuProcess = emuProc;

        IsRunning = true;
        emuProc.EnableRaisingEvents = true;
        // Capture the local `proc` — reading the field would throw if a
        // relaunch replaced it before this (older) instance exited (ExitCode
        // on a running process throws on a threadpool thread → process death).
        emuProc.Exited += (_, _) =>
        {
            IsRunning = false;
            ScrubApConfigPassword();   // session over — blank the password (P3-20)
            int code = 0;
            try { code = emuProc.ExitCode; } catch { /* handle already gone */ }
            GameExited?.Invoke(code);
        };

        // 4. Wait for the Lua connector to attach BOTH pipes, then start the
        //    bridge loop. If it never attaches (script disabled or errored
        //    inside BizHawk), give up after 60s — non-fatal: the game still
        //    runs, just without AP sync (mirrors the OpenTTD plugin's
        //    connect-timeout pattern).
        Trace($"EmuHawk started (pid {emuProc.Id}) — waiting for connector");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _pipeCts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await Task.WhenAll(
                pipeIn.WaitForConnectionAsync(timeoutCts.Token),
                pipeOut.WaitForConnectionAsync(timeoutCts.Token));
            ConnectorAttached = true;
            Trace("connector attached (both pipes) — bridge loop starting");
            _ = Task.Run(() => PipeLoopAsync(pipeIn, pipeOut, _pipeCts.Token));
        }
        catch (OperationCanceledException)
        {
            // Timed out (or torn down) — BizHawk is running but the connector
            // never attached to both pipes. ConnectorAttached stays false; the
            // launcher surfaces the warning right after LaunchAsync returns
            // (P2-10). Dispose the servers so nothing leaks and a half-attached
            // client reads a clean EOF instead of hanging on a dead pipe.
            Trace("connector did not attach within 60s — disposing pipe servers");
            DisposeQuietly(pipeIn);
            DisposeQuietly(pipeOut);
            if (ReferenceEquals(_pipeIn,  pipeIn))  _pipeIn  = null;
            if (ReferenceEquals(_pipeOut, pipeOut)) _pipeOut = null;
        }
    }

    /// One bridge pipe server. Duplex (InOut) even though each pipe carries
    /// one direction — the CRT client must open "r+b" (the only fopen mode
    /// with named-pipe-safe open semantics), which requests GENERIC_READ |
    /// GENERIC_WRITE and a directional server would refuse. Explicit buffer
    /// sizes: with the .NET default of 0, every WriteFile blocks until the
    /// peer reads it (rendezvous) — replies must never be able to wedge the
    /// bridge loop behind a slow emulator frame.
    private static NamedPipeServerStream CreateBridgePipe(string name)
        => new(name, PipeDirection.InOut,
               maxNumberOfServerInstances: 1,
               transmissionMode: PipeTransmissionMode.Byte,
               options: PipeOptions.Asynchronous,
               inBufferSize:  65536,
               outBufferSize: 65536);

    private static void DisposeQuietly(IDisposable? d)
    {
        try { d?.Dispose(); } catch { }
    }

    // ── §14 NWA launch path (snes9x-emunwa) ─────────────────────────────────────

    /// Launch an NWA backend, connect to its NWA server, and run this game's Lua
    /// module launcher-side via Snes9xLuaBridge. No in-emulator script. Graceful:
    /// if NWA never answers, the game still runs — just without AP sync (mirrors
    /// the pipe path's connector timeout). The LIVE confirmation (real binary +
    /// ROM reporting a real check) is the owner's in-emulator gate, as documented
    /// on EmulatorBackends.snes9x.
    private async Task LaunchViaNwaAsync(EmulatorBackend backend, string launchRom,
                                         ApSession session, CancellationToken ct)
    {
        StartTrace();
        DisposeNwa();   // tear down any previous session

        string exe = Path.Combine(EmulatorDirectory, backend.ExeName);
        var psi = new ProcessStartInfo
        {
            FileName         = exe,
            // snes9x takes the ROM positionally; -fullscreen is its own switch.
            Arguments        = $"\"{launchRom}\"" + (StartFullscreen ? " -fullscreen" : ""),
            WorkingDirectory = EmulatorDirectory,
            UseShellExecute  = false,
        };
        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {backend.DisplayName}.");
        _emuProcess = proc;
        IsRunning = true;
        ConnectorAttached = false;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            ScrubApConfigPassword();
            int code = 0; try { code = proc.ExitCode; } catch { /* gone */ }
            GameExited?.Invoke(code);
        };
        Trace($"{backend.DisplayName} started (pid {proc.Id}) — connecting to NWA");

        // The NWA build serves on 0xBEEF as soon as it is up; the emulator needs
        // a moment to boot + load the ROM, so retry the connect for ~45 s.
        NwaClient? nwa = null;
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (nwa == null && DateTime.UtcNow < deadline
               && !ct.IsCancellationRequested && !proc.HasExited)
        {
            try { nwa = await NwaClient.ConnectAsync("127.0.0.1", NwaClient.DefaultPort, 8, ct); }
            catch (OperationCanceledException) { break; }
            catch { nwa = null; }
            if (nwa == null) { try { await Task.Delay(1000, ct); } catch { break; } }
        }

        if (nwa == null)
        {
            Trace("NWA connect timed out — game runs without AP sync");
            SessionRomNote ??= $"[{DisplayName}] {backend.DisplayName} started but its " +
                "Network Access server did not answer — the game runs, but in-game " +
                "checks won't sync. Make sure this is the NWA build of snes9x.";
            return;
        }

        _nwaClient = nwa;
        ConnectorAttached = true;
        Trace($"NWA connected on port {nwa.Port} — starting Lua bridge ({LuaModuleName})");

        string modulePath = Path.Combine(ScriptsDirectory, "games", LuaModuleName + ".lua");
        var cfg = new BridgeConfig
        {
            SlotNumber = GetOwnSlot?.Invoke() ?? 0,
            Locations  = GetServerLocations?.Invoke() ?? Array.Empty<long>(),
            SlotData   = ConvertSlotData(GetSlotData?.Invoke()),
        };

        Snes9xLuaBridge bridge;
        try { bridge = new Snes9xLuaBridge(nwa, modulePath, cfg, log: Trace); }
        catch (Exception ex)
        {
            Trace($"Lua bridge failed to start: {ex.Message}");
            SessionRomNote ??= $"[{DisplayName}] connected to {backend.DisplayName} but the " +
                $"AP logic module failed to load ({ex.Message}) — game runs without sync.";
            return;
        }
        bridge.LocationsChecked += ids =>
        { try { LocationsChecked?.Invoke(ids); } catch (Exception ex) { Trace($"LocationsChecked handler: {ex.Message}"); } };
        bridge.GoalCompleted += () =>
        { try { GoalCompleted?.Invoke(); } catch (Exception ex) { Trace($"GoalCompleted handler: {ex.Message}"); } };
        bridge.Disconnected += why => Trace($"NWA bridge disconnected: {why}");
        _nwaBridge = bridge;

        // Replay the item backlog (full stream from index 0), then future items
        // flow in through ReceiveItemsAsync.
        lock (_itemsLock) { _nwaDelivered = 0; PushItemsToNwaBridgeLocked(); }
        bridge.Start(ct);
    }

    /// Top-level slot_data (JsonElement) → the bool/long/double/string map the
    /// Lua modules read (e.g. remote_items). Null/non-object → null.
    private static IReadOnlyDictionary<string, object?>? ConvertSlotData(JsonElement? slotData)
    {
        if (slotData is not { ValueKind: JsonValueKind.Object } el) return null;
        var d = new Dictionary<string, object?>();
        foreach (var p in el.EnumerateObject())
        {
            d[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.True   => true,
                JsonValueKind.False  => false,
                JsonValueKind.Number => p.Value.TryGetInt64(out var l) ? l : p.Value.GetDouble(),
                JsonValueKind.String => p.Value.GetString(),
                _                    => null,
            };
        }
        return d;
    }

    private void DisposeNwa()
    {
        try { _nwaBridge?.Dispose(); } catch { }
        try { _nwaClient?.Dispose(); } catch { }
        _nwaBridge = null;
        _nwaClient = null;
        _nwaDelivered = 0;
    }

    public Task StopAsync()
    {
        _pipeCts?.Cancel();
        DisposeNwa();
        try { _emuProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        ScrubApConfigPassword();   // plaintext credentials die with the session (P3-20)
        return Task.CompletedTask;
    }

    /// Blank the password in ap_config.json once the session ends (P3-20) —
    /// the plaintext room password should not outlive the session on disk.
    /// Safe timing: the Lua connector reads the sidecar only at script start,
    /// and every launch rewrites it first (WriteApConfig).
    private void ScrubApConfigPassword()
    {
        try
        {
            string configPath = Path.Combine(EmulatorDirectory, "ap_config.json");
            if (!File.Exists(configPath)) return;

            // Generic field-preserving scrub (the sidecar now carries
            // locations/slot_data/etc. — a fixed-shape rewrite would drop them).
            var node = System.Text.Json.Nodes.JsonNode
                .Parse(File.ReadAllText(configPath))?.AsObject();
            if (node is null) return;
            if (node["password"]?.GetValue<string>() is not { Length: > 0 }) return;

            node["password"] = "";
            File.WriteAllText(configPath, node.ToJsonString());
        }
        catch { /* best effort — the next launch overwrites the file anyway */ }
    }

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // Items are kept in an index-addressed list rather than written to
        // the pipe directly, because:
        //   1. The Lua connector drains them with per-frame SYNC polls
        //      (request/response), so its blocking CRT pipe reads always
        //      complete in one local round-trip instead of stalling the core.
        //   2. All pipe writes stay on the PipeLoopAsync thread —
        //      NamedPipeServerStream does not support concurrent writers.
        //   3. The AP `index` is load-bearing: game modules that track their
        //      own processed-item count inside the save (Pokémon Emerald)
        //      need every item at its absolute stream position, and an
        //      index-0 packet is the server's authoritative full resend.
        if (index < 0) return Task.CompletedTask;

        lock (_itemsLock)
        {
            for (int i = 0; i < items.Length; i++)
            {
                int abs = index + i;
                if      (abs <  _itemsReceived.Count) _itemsReceived[abs] = items[i];
                else if (abs == _itemsReceived.Count) _itemsReceived.Add(items[i]);
                else
                {
                    // Gap in the stream — AP guarantees contiguity per
                    // connection, so this is a desync; drop and trace rather
                    // than deliver items under wrong indices.
                    Trace($"item packet skipped: index {abs} would leave a gap " +
                          $"(have {_itemsReceived.Count})");
                    break;
                }
            }

            // Index-0 packet = full authoritative state: anything beyond it
            // is stale (slot switch within one launcher run).
            if (index == 0 && _itemsReceived.Count > items.Length)
                _itemsReceived.RemoveRange(items.Length, _itemsReceived.Count - items.Length);

            if (_itemCursor > _itemsReceived.Count)
                _itemCursor = _itemsReceived.Count;

            // §14 NWA path: push newly-known items to the in-launcher Lua bridge.
            // Monotonic — never re-pushed — so an index-0 full resend of the same
            // stream can't double-deliver; the module gates real writes on the
            // game's own received-count.
            if (_nwaBridge != null)
                PushItemsToNwaBridgeLocked();
        }
        return Task.CompletedTask;
    }

    /// Enqueue every item past the delivery cursor into the NWA bridge. Caller
    /// holds _itemsLock.
    private void PushItemsToNwaBridgeLocked()
    {
        if (_nwaBridge == null) return;
        if (_nwaDelivered > _itemsReceived.Count) _nwaDelivered = _itemsReceived.Count;
        for (int k = _nwaDelivered; k < _itemsReceived.Count; k++)
        {
            var it = _itemsReceived[k];
            _nwaBridge.EnqueueItem(it.ItemId, k, it.Player, it.Flags, it.LocationId);
        }
        _nwaDelivered = _itemsReceived.Count;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // Write ap_state.json alongside ap_config.json so the Lua connector can
        // detect AP disconnects/reconnects by polling the file on each frame.
        // The Lua side checks: connected = (state == "connected")
        try
        {
            string stateFile = Path.Combine(EmulatorDirectory, "ap_state.json");
            File.WriteAllText(stateFile, System.Text.Json.JsonSerializer.Serialize(new
            {
                state     = state.ToString().ToLowerInvariant(),
                connected = state == LauncherV2.Core.ApConnectionState.Connected,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }));
        }
        catch { /* best effort — game still runs if this fails */ }
    }

    /// Subclasses may produce a session-specific ROM to launch instead of the
    /// library ROM (e.g. apply the multiworld seed's AP patch to a COPY —
    /// the library ROM itself is never modified). Return null to launch the
    /// library ROM as-is. Runs before the pipe servers and ap_config.json
    /// are created; explain the outcome through SessionRomNote.
    protected virtual Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
        => Task.FromResult<string?>(null);

    public virtual UIElement? CreateSettingsPanel()
    {
        var muted  = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg     = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var gold   = new SolidColorBrush(Color.FromRgb(0xE8, 0xA0, 0x18));
        var error  = new SolidColorBrush(Color.FromRgb(0xF0, 0x50, 0x50));
        var success= new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Section: ROM file ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "ROM FILE", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text       = $"Provide your own {DisplayName} ROM. The launcher never ships ROM files.",
            FontSize   = 11,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin     = new Thickness(0, 0, 0, 8),
        });

        var romRow   = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var romBox   = new TextBox
        {
            Text    = RomPath ?? "",
            Margin  = new Thickness(0, 0, 8, 0),
            IsReadOnly = true,
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var romBtn = new Button
        {
            Content    = "Browse...",
            Width      = 90,
            Padding    = new Thickness(0, 6, 0, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        // §11: tell the user where their ROM actually lives (the library copy)
        // and that the original stays untouched.
        var libNote = new TextBlock
        {
            FontSize     = 10,
            Foreground   = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 0),
        };
        void UpdateLibNote() => libNote.Text = RomPath == null
            ? $"Your ROM is copied into the launcher library ({RomLibraryDirectory}) — " +
              "the original file is never modified."
            : $"Library copy: {RomPath} — your original file is never modified.";
        UpdateLibNote();

        romBtn.Click += (_, _) =>
        {
            if (PromptForRomFile())
            {
                romBox.Text = RomPath ?? "";
                UpdateLibNote();
            }
        };
        DockPanel.SetDock(romBtn, Dock.Right);
        romRow.Children.Add(romBtn);
        romRow.Children.Add(romBox);
        panel.Children.Add(romRow);
        panel.Children.Add(libNote);

        // ROM system chip
        panel.Children.Add(new TextBlock
        {
            Text     = $"System: {RomSystem}",
            FontSize = 10,
            Foreground = muted,
            Margin   = new Thickness(0, 4, 0, 20),
        });

        // ── Section: launch options (§12) ────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LAUNCH OPTIONS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });
        var chkFullscreen = new CheckBox
        {
            Content    = "Start the emulator in fullscreen",
            IsChecked  = StartFullscreen,
            Foreground = fg,
            Margin     = new Thickness(0, 0, 0, 20),
            ToolTip    = "Launches the selected emulator in fullscreen mode " +
                         "(applied per emulator — BizHawk --fullscreen, etc.).",
        };
        chkFullscreen.Checked   += (_, _) => { StartFullscreen = true;  SaveSettings(); };
        chkFullscreen.Unchecked += (_, _) => { StartFullscreen = false; SaveSettings(); };
        panel.Children.Add(chkFullscreen);

        // ── Section: emulator choice (§14) ───────────────────────────────────
        // A dropdown of every backend that can host this game's system. Backends
        // without a working AP check bridge are shown but DISABLED with a
        // "(coming soon)" suffix — honest availability: only working backends
        // are pickable. Today that means BizHawk (selected) + greyed
        // snes9x/mGBA/Mesen for the relevant system.
        panel.Children.Add(new TextBlock
        {
            Text = "EMULATOR", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        var emuCombo = new System.Windows.Controls.ComboBox
        {
            Margin     = new Thickness(0, 0, 0, 6),
            Padding    = new Thickness(8, 5, 8, 5),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth   = 220,
        };
        // Resolve the persisted selection up front so the combo opens on it.
        string selectedId = ResolveSelectedBackend().Id;
        // §14: only the emulators THIS game supports (intersected with the ones
        // that can host its system) — not every backend for the system.
        var availableBackends = AvailableBackends();
        foreach (var backend in availableBackends)
        {
            var item = new System.Windows.Controls.ComboBoxItem
            {
                Content   = !backend.BridgeReady  ? $"{backend.DisplayName} (coming soon)"
                          : !backend.LiveVerified ? $"{backend.DisplayName} (experimental)"
                          :                          backend.DisplayName,
                Tag       = backend.Id,
                // Honest availability: not-bridge-ready backends are visible but
                // cannot be selected; experimental (wired, unconfirmed) ones can.
                IsEnabled = backend.BridgeReady,
                Foreground = backend.BridgeReady ? fg : muted,
            };
            if (string.Equals(backend.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                item.IsSelected = true;
            emuCombo.Items.Add(item);
        }
        emuCombo.SelectionChanged += (_, _) =>
        {
            if (emuCombo.SelectedItem is System.Windows.Controls.ComboBoxItem ci &&
                ci.Tag is string id)
            {
                SelectedEmulatorId = id;
                SaveSettings();
            }
        };
        panel.Children.Add(emuCombo);

        // Honest footnote: name the verified backend(s) and, when present, the
        // ones still landing AP sync ("coming soon"). Computed from the game's
        // own list so it stays accurate as backends graduate to BridgeReady.
        var verifiedNames     = availableBackends.Where(b => b.BridgeReady && b.LiveVerified)
                                                 .Select(b => b.DisplayName).ToList();
        var experimentalNames = availableBackends.Where(b => b.BridgeReady && !b.LiveVerified)
                                                 .Select(b => b.DisplayName).ToList();
        var comingNames       = availableBackends.Where(b => !b.BridgeReady)
                                                 .Select(b => b.DisplayName).ToList();
        var noteParts = new List<string>();
        if (verifiedNames.Count > 0)
            noteParts.Add($"AP check detection is verified on {string.Join(" / ", verifiedNames)}.");
        if (experimentalNames.Count > 0)
            noteParts.Add($"{string.Join(" / ", experimentalNames)} " +
                          $"{(experimentalNames.Count == 1 ? "is" : "are")} experimental — fully wired, " +
                          "awaiting first live confirmation.");
        if (comingNames.Count > 0)
            noteParts.Add($"{string.Join(" / ", comingNames)} " +
                          $"{(comingNames.Count == 1 ? "is" : "are")} coming soon.");
        string emuNote = string.Join(" ", noteParts);
        panel.Children.Add(new TextBlock
        {
            Text = emuNote,
            FontSize = 10, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 20),
        });

        // ── Section: BizHawk emulator ────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "BIZHAWK EMULATOR", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        bool emuPresent = IsEmulatorPresent;
        // §3: show the recorded installed version (the tag the pin resolved to)
        // when present, so the player can see exactly what BizHawk build is on
        // disk vs. the pinned target.
        string presentText = InstalledEmulatorVersion is { Length: > 0 } iv
            ? $"✓ BizHawk {iv} is installed"
            : "✓ BizHawk is installed";
        var emuStatus = new TextBlock
        {
            Text       = emuPresent ? presentText : "✗ BizHawk not found",
            FontSize   = 12,
            Foreground = emuPresent ? success : error,
            Margin     = new Thickness(0, 0, 0, 8),
        };
        panel.Children.Add(emuStatus);

        if (!emuPresent)
        {
            var installBtn = new Button
            {
                Content = "Download BizHawk",
                Padding = new Thickness(14, 7, 14, 7),
                Margin  = new Thickness(0, 0, 0, 8),
                Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
                Foreground  = fg,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            };
            var progressBar = new ProgressBar
            {
                Height    = 4,
                Minimum   = 0,
                Maximum   = 100,
                Foreground = gold,
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x38)),
                BorderThickness = new Thickness(0),
                Margin    = new Thickness(0, 0, 0, 4),
                Visibility = Visibility.Collapsed,
            };
            var progressText = new TextBlock
            {
                FontSize   = 10,
                Foreground = muted,
                Margin     = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed,
            };
            panel.Children.Add(installBtn);
            panel.Children.Add(progressBar);
            panel.Children.Add(progressText);

            installBtn.Click += async (_, _) =>
            {
                installBtn.IsEnabled    = false;
                progressBar.Visibility  = Visibility.Visible;
                progressText.Visibility = Visibility.Visible;

                var progress = new Progress<(int Pct, string Msg)>(rep =>
                {
                    progressBar.Value  = rep.Pct;
                    progressText.Text  = rep.Msg;
                });
                try
                {
                    await InstallBizHawkAsync(progress, CancellationToken.None);
                    emuStatus.Text       = "✓ BizHawk is installed";
                    emuStatus.Foreground = success;
                    installBtn.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    progressText.Text = $"Failed: {ex.Message}";
                    installBtn.IsEnabled = true;
                }
            };
        }

        panel.Children.Add(new TextBlock
        {
            Text     = $"Install path: {EmulatorDirectory}",
            FontSize = 10,
            Foreground = muted,
            Margin   = new Thickness(0, 4, 0, 0),
        });

        return panel;
    }

    /// Open the ROM picker, copy the choice into the launcher's ROM library
    /// and persist the COPY's path (§11 — the original file is never touched).
    /// Returns true when a file was selected and imported. Shared by the
    /// Settings panel and the post-install "select your ROM to finish setup"
    /// flow (UX-5) — the ROM step used to be discoverable only by stumbling
    /// into Settings.
    public bool PromptForRomFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = $"Select {DisplayName} ROM",
            Filter = BuildRomFilter(),
        };
        if (dlg.ShowDialog() != true) return false;

        // Content check (size first, then MD5) — the file name is irrelevant.
        string? bad = ValidateBaseRom(dlg.FileName);
        if (bad != null)
        {
            MessageBox.Show(bad, $"Not a valid {DisplayName} ROM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            RomPath = ImportRomToLibrary(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not copy the ROM into the launcher library:\n{ex.Message}\n\n" +
                "Nothing was changed — your original file is untouched. " +
                "Free up disk space or pick a different file and try again.",
                "ROM import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        SaveSettings();
        return true;
    }

    /// Root of this game's ROM library folder (the launcher's own tree).
    protected string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    /// Delete a library-generated patched ROM and drop its registry row. The
    /// patched .gba (always inside the launcher's own Games/ROMs tree) is
    /// removed; the source .apemerald is deleted ONLY when it lives inside the
    /// launcher's own tree — patches under %ProgramData%\Archipelago (the AP
    /// generator's output) are the player's, never touched. The user's original
    /// ROM and any emulator save data are never involved.
    /// Returns null on success, or a short error string.
    public static string? DeletePatchedRom(SeedEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.PatchedRomPath))
            return "No ROM to delete.";

        try
        {
            string rom = Path.GetFullPath(entry.PatchedRomPath);

            // Hard guard: only ever delete inside the launcher's own ROM tree.
            string launcherRoms = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "Games", "ROMs"));
            if (!rom.StartsWith(launcherRoms, StringComparison.OrdinalIgnoreCase))
                return "Refused: ROM is outside the launcher library.";

            if (File.Exists(rom))
                File.Delete(rom);

            // The originating patch — delete only if it sits in the launcher's
            // own tree AND not under %ProgramData%\Archipelago.
            if (!string.IsNullOrEmpty(entry.PatchPath))
            {
                try
                {
                    string patch    = Path.GetFullPath(entry.PatchPath);
                    string appRoot  = Path.GetFullPath(AppContext.BaseDirectory);
                    string apShared = Path.GetFullPath(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Archipelago"));

                    bool insideLauncher = patch.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase);
                    bool insideApShared = patch.StartsWith(apShared, StringComparison.OrdinalIgnoreCase);
                    if (insideLauncher && !insideApShared && File.Exists(patch))
                        File.Delete(patch);
                }
                catch { /* patch removal is best-effort; the ROM is what matters */ }
            }
        }
        catch (Exception ex)
        {
            return $"Could not delete ROM: {ex.Message}";
        }

        SeedLibraryStore.Instance.Remove(entry.GameId, entry.PatchedRomPath);
        return null;
    }

    /// §11: copy `sourcePath` into Games/ROMs/<GameId>/ and return the copy's
    /// path. The source is only ever READ. Rules:
    ///   · picking a file already inside the library = use it as-is (no copy)
    ///   · same name + same size already in the library = reuse the existing
    ///     copy (re-picking your ROM is idempotent)
    ///   · same name but different size = a DIFFERENT file — keep both by
    ///     suffixing the new copy _2, _3, … (never overwrite)
    private string ImportRomToLibrary(string sourcePath)
    {
        string dir = RomLibraryDirectory;
        Directory.CreateDirectory(dir);

        string source = Path.GetFullPath(sourcePath);
        string dest   = Path.Combine(dir, Path.GetFileName(source));

        // Picked the library copy itself — nothing to import.
        if (string.Equals(source, Path.GetFullPath(dest),
                          StringComparison.OrdinalIgnoreCase))
            return dest;

        long srcLen = new FileInfo(source).Length;
        if (File.Exists(dest))
        {
            if (new FileInfo(dest).Length == srcLen) return dest;   // same file → reuse

            string stem = Path.GetFileNameWithoutExtension(dest);
            string ext  = Path.GetExtension(dest);
            for (int i = 2; ; i++)
            {
                string candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
                if (File.Exists(candidate))
                {
                    if (new FileInfo(candidate).Length == srcLen)
                        return candidate;                            // already imported earlier
                    continue;
                }
                dest = candidate;
                break;
            }
        }

        File.Copy(source, dest, overwrite: false);
        return dest;
    }

    // ── Content-based ROM validation ────────────────────────────────────────

    /// Known-good vanilla base ROM(s) for this game, identified by CONTENT
    /// (size + optional MD5) — NEVER by filename, because players name their
    /// dumps anything. Empty = accept any file the picker allows. Subclasses
    /// override with the exact dump(s) the randomizer accepts.
    protected virtual IReadOnlyList<RomIdentity> AcceptableBaseRoms
        => Array.Empty<RomIdentity>();

    /// Validate a candidate ROM by its CONTENTS. Size is checked first (instant,
    /// name-independent — the detection the owner asked for); a known MD5 then
    /// confirms it is the exact dump. Returns null when acceptable, else a
    /// plain-English reason. Public so the UI's locate flow can pre-check too.
    public string? ValidateBaseRom(string path)
    {
        var known = AcceptableBaseRoms;
        if (known.Count == 0) return null;          // game declares no constraint
        if (!File.Exists(path)) return "The file no longer exists.";

        long size = new FileInfo(path).Length;
        var bySize = known.Where(k => k.SizeBytes == size).ToList();
        if (bySize.Count == 0)
        {
            string want = string.Join(" or ",
                known.Select(k => $"{FormatBytes(k.SizeBytes)} ({k.Label})"));
            return $"This file is {FormatBytes(size)} — that is not the size of a " +
                   $"{DisplayName} ROM (expected {want}).\n\n" +
                   "The file name doesn't matter — I check the contents — and this " +
                   "isn't the right file.";
        }

        var withMd5 = bySize.Where(k => k.Md5 != null).ToList();
        if (withMd5.Count > 0)
        {
            string actual = ComputeMd5(path);
            if (!withMd5.Any(k => k.Md5!.Equals(actual, StringComparison.OrdinalIgnoreCase)))
                return $"This file is the right size but not the exact {DisplayName} " +
                       $"dump the randomizer needs (its fingerprint is {actual}).\n\n" +
                       "It may be a modified or wrong-region copy — patching would fail. " +
                       "Use the original cartridge dump.";
        }
        return null;
    }

    /// Human-readable byte size, e.g. "16 MB" / "512 KB".
    protected static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.#} MB"
         : bytes >= 1024        ? $"{bytes / 1024.0:0.#} KB"
         : $"{bytes} bytes";

    // ── ROM safety net ────────────────────────────────────────────────────────

    /// Returns null when a usable ROM is in place; otherwise a RomRequirement
    /// the UI shows so the player can locate the file. The base implementation
    /// only knows "a ROM is missing"; subclasses with a patch (which carries
    /// the exact base-ROM MD5) override this to demand the RIGHT version.
    public virtual RomRequirement? GetUnmetRomRequirement()
    {
        if (RomPath != null && File.Exists(RomPath)) return null;
        return new RomRequirement(
            DisplayName, RomSystem,
            $"any {RomSystem} {DisplayName} ROM",
            RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());
    }

    /// Validate a ROM the player located in response to a RomRequirement, then
    /// import the COPY into the library (§11 — original untouched) and persist.
    /// Returns null on success (RomPath updated); otherwise a human-readable
    /// reason to show before letting them try again.
    public string? TryImportLocatedRom(string sourcePath, RomRequirement req)
    {
        try
        {
            // Content check (size first — name-independent), then the patch's
            // exact MD5 when this requirement carries one.
            string? bad = ValidateBaseRom(sourcePath);
            if (bad != null) return bad;
            if (req.RequiredMd5 != null)
            {
                string actual = ComputeMd5(sourcePath);
                if (!actual.Equals(req.RequiredMd5, StringComparison.OrdinalIgnoreCase))
                    return $"That file is not the version this multiworld needs.\n\n" +
                           $"Needed ({req.VersionLabel})\n  MD5 {req.RequiredMd5}\n" +
                           $"You picked\n  MD5 {actual}\n\n" +
                           "Pick the matching ROM and try again.";
            }
            RomPath = ImportRomToLibrary(sourcePath);
            SaveSettings();
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not import the file: {ex.Message}\n\n" +
                   "Your original file is untouched — free up disk space or " +
                   "pick a different file.";
        }
    }

    /// MD5 of a file as lowercase hex (matches the AP patch manifest format).
    protected static string ComputeMd5(string path)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var fs  = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
    }

    /// Build an OpenFileDialog filter from the ROM system.
    protected string BuildRomFilter()
        => RomSystem switch
        {
            "GBA"  => "GBA ROM (*.gba)|*.gba|All files (*.*)|*.*",
            "GBC"  => "Game Boy Color ROM (*.gbc)|*.gbc|Game Boy ROM (*.gb)|*.gb|All files (*.*)|*.*",
            "GB"   => "Game Boy ROM (*.gb)|*.gb|All files (*.*)|*.*",
            "SNES" => "SNES ROM (*.sfc;*.smc)|*.sfc;*.smc|All files (*.*)|*.*",
            "NES"  => "NES ROM (*.nes)|*.nes|All files (*.*)|*.*",
            "N64"  => "N64 ROM (*.n64;*.z64;*.v64)|*.n64;*.z64;*.v64|All files (*.*)|*.*",
            "GEN"  => "Genesis ROM (*.md;*.bin)|*.md;*.bin|All files (*.*)|*.*",
            "NDS"  => "Nintendo DS ROM (*.nds)|*.nds|All files (*.*)|*.*",
            _      => "ROM files|*.*",
        };

    /// Persist per-game settings (ROM library path + launch options) to
    /// Data/{GameId}_settings.json. Subclasses add their own keys through
    /// OnSavingSettings.
    protected void SaveSettings()
    {
        string dir  = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"{GameId}_settings.json");
        var bag = new Dictionary<string, object?>
        {
            ["rom_path"]    = RomPath,
            ["fullscreen"]  = StartFullscreen,
            ["emulator_id"] = SelectedEmulatorId ?? EmulatorBackends.Default(RomSystem).Id,
        };
        OnSavingSettings(bag);
        File.WriteAllText(file, JsonSerializer.Serialize(bag));
    }

    /// Subclass hook: add extra keys to the per-game settings file.
    protected virtual void OnSavingSettings(IDictionary<string, object?> bag) { }

    /// Subclass hook: read extra keys back from the per-game settings file.
    protected virtual void OnLoadingSettings(JsonElement root) { }

    /// Load per-game settings from disk (call from constructor or plugin
    /// registration).
    protected void LoadSettings()
    {
        // Default the emulator choice to this system's default backend (the
        // first BridgeReady one — "bizhawk" today). Done here, not as a field
        // initialiser, because RomSystem is abstract and unavailable then. A
        // persisted value below overrides it.
        SelectedEmulatorId ??= EmulatorBackends.Default(RomSystem).Id;

        string file = Path.Combine(AppContext.BaseDirectory, "Data", $"{GameId}_settings.json");
        if (!File.Exists(file)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("rom_path", out var el))
                RomPath = el.GetString();
            if (doc.RootElement.TryGetProperty("fullscreen", out var fs))
                StartFullscreen = fs.ValueKind == JsonValueKind.True;
            if (doc.RootElement.TryGetProperty("emulator_id", out var eid) &&
                eid.GetString() is { Length: > 0 } savedId)
                SelectedEmulatorId = savedId;
            OnLoadingSettings(doc.RootElement);
        }
        catch { /* ignore — stale/corrupt settings */ }
    }

    public Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
        => Task.FromResult(Array.Empty<NewsItem>());

    // ── Named pipe receive loop ───────────────────────────────────────────────
    //
    // The pipes run in BYTE mode (the Lua client is a CRT file handle — see
    // LaunchAsync), so messages are newline-framed text. A single ReadAsync may
    // deliver several lines, half a line, or a line split across reads; the
    // loop accumulates chars and only dispatches complete '\n'-terminated
    // frames. Reads come from pipeIn (<base>_c2s), replies go to pipeOut
    // (<base>_s2c) — this loop thread is the only writer on pipeOut.
    //
    // The pipe instances are parameters, not the fields: the loop must read
    // from — and finally dispose — ITS OWN pipes even if a relaunch already
    // swapped the fields to fresh server streams (same rule as D2Plugin).

    private async Task PipeLoopAsync(
        NamedPipeServerStream pipeIn, NamedPipeServerStream pipeOut,
        CancellationToken ct)
    {
        var buf     = new byte[4096];
        var chars   = new char[8192];
        var decoder = Encoding.UTF8.GetDecoder();  // stateful — split sequences OK
        var pending = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested && pipeIn.IsConnected)
            {
                int n = await pipeIn.ReadAsync(buf, ct);
                if (n == 0) { Trace("pipe loop: connector closed the pipe (EOF)"); break; }

                int charCount = decoder.GetChars(buf, 0, n, chars, 0);
                pending.Append(chars, 0, charCount);

                int nl;
                while ((nl = IndexOfNewline(pending)) >= 0)
                {
                    string line = pending.ToString(0, nl).TrimEnd('\r').Trim();
                    pending.Remove(0, nl + 1);
                    if (line.Length > 0)
                        await HandleLineAsync(line, pipeOut, ct);
                }

                // Safety valve: a misbehaving client that never sends '\n'
                // must not grow this buffer without bound.
                if (pending.Length > 65536) pending.Clear();
            }
        }
        catch (OperationCanceledException) { Trace("pipe loop: canceled (session stop)"); }
        catch (IOException ex)             { Trace($"pipe loop: pipe broke — {ex.Message}"); }
        catch (ObjectDisposedException)    { Trace("pipe loop: pipe torn down mid-read"); }
        catch (Exception ex)               { Trace($"pipe loop: unexpected exception — {ex}"); }
        finally
        {
            // Break the connection so the Lua side's blocking reads return EOF
            // instead of hanging the emulator if this loop dies first. Dispose
            // our own streams so the next launch starts from a clean slate.
            Trace("pipe loop: exited — disposing both pipes");
            DisposeQuietly(pipeIn);
            DisposeQuietly(pipeOut);
            if (ReferenceEquals(_pipeIn,  pipeIn))  _pipeIn  = null;
            if (ReferenceEquals(_pipeOut, pipeOut)) _pipeOut = null;
        }
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
            if (sb[i] == '\n') return i;
        return -1;
    }

    private async Task HandleLineAsync(
        string msg, NamedPipeServerStream pipeOut, CancellationToken ct)
    {
        // "CHECK:<locationId1>,<locationId2>,..." — completed location checks
        if (msg.StartsWith("CHECK:", StringComparison.Ordinal))
        {
            Trace($"RX {msg}");
            var ids = new List<long>();
            foreach (string p in msg[6..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (long.TryParse(p.Trim(), out long id))
                    ids.Add(id);

            // A faulting subscriber (UI-layer handler) must never kill this
            // loop — that would tear down both pipes and silently drop every
            // further CHECK and GOAL for the rest of the session (the same
            // hardening D2Plugin's loop got in P1-4).
            if (ids.Count > 0)
            {
                try { LocationsChecked?.Invoke(ids.ToArray()); }
                catch (Exception ex) { Trace($"LocationsChecked handler failed: {ex}"); }
            }
        }
        // "GOAL" — Lua connector signals the game goal is complete
        else if (msg == "GOAL")
        {
            Trace("RX GOAL");
            try { GoalCompleted?.Invoke(); }
            catch (Exception ex) { Trace($"GoalCompleted handler failed: {ex}"); }
        }
        // "SYNC" — per-frame poll from the Lua connector. ALWAYS answered —
        // zero queued items, AP not connected, whatever: the Lua side issued
        // a blocking read and only this reply releases it. Every not-yet-
        // relayed item goes out as an extended line
        //   "ITEM:<id>|<index>|<player>|<flags>|<locationId>"
        // (index = absolute position in the slot's item stream; the game
        // module needs it for its save-side received-count handshake),
        // terminated by "SYNCEND", in a single buffered write so the reply
        // stays contiguous. A write failure here means the connector is
        // gone — let the IOException reach PipeLoopAsync and end the loop.
        else if (msg == "SYNC")
        {
            var reply = new StringBuilder();
            int itemCount = 0;
            lock (_itemsLock)
            {
                while (_itemCursor < _itemsReceived.Count)
                {
                    var it = _itemsReceived[_itemCursor];
                    reply.Append("ITEM:").Append(it.ItemId)
                         .Append('|').Append(_itemCursor)
                         .Append('|').Append(it.Player)
                         .Append('|').Append(it.Flags)
                         .Append('|').Append(it.LocationId)
                         .Append('\n');
                    _itemCursor++;
                    itemCount++;
                }
            }
            reply.Append("SYNCEND\n");

            long nSync = ++_syncCount;   // only touched on this loop thread
            if (itemCount > 0 || nSync <= 3 || nSync % 100 == 0)
                Trace($"SYNC #{nSync} -> {itemCount} item(s) + SYNCEND");

            byte[] outBuf = Encoding.UTF8.GetBytes(reply.ToString());
            if (pipeOut.IsConnected)
                await pipeOut.WriteAsync(outBuf, ct);
        }
        else
        {
            Trace($"RX unknown line ignored: {msg}");
        }
    }

    // ── Bridge trace (debug instrumentation) ─────────────────────────────────
    // Gated on AP_BRIDGE_TRACE=1 in the LAUNCHER process's environment.
    // Writes <EmulatorDirectory>\bridge_trace.log: connection lifecycle, every
    // non-SYNC line in, sampled SYNC roundtrips (first 3, then every 100th),
    // every loop exception with stack. The Lua connector keeps its own
    // always-on ap_connector.log — together they show both ends of any
    // future bridge failure.

    private bool _traceEnabled;
    private readonly object _traceGate = new();

    private string TracePath => Path.Combine(EmulatorDirectory, "bridge_trace.log");

    /// Latch the env var and truncate the trace file. Called per launch.
    private void StartTrace()
    {
        _traceEnabled = Environment.GetEnvironmentVariable("AP_BRIDGE_TRACE") == "1";
        if (!_traceEnabled) return;
        try
        {
            lock (_traceGate)
                File.WriteAllText(TracePath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] bridge trace started ({GameId})" +
                    Environment.NewLine);
        }
        catch { _traceEnabled = false; }   // unwritable dir — disable quietly
    }

    private void Trace(string msg)
    {
        if (!_traceEnabled) return;
        try
        {
            lock (_traceGate)
                File.AppendAllText(TracePath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}" + Environment.NewLine);
        }
        catch { /* tracing must never break the bridge */ }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// Directory holding the shipped Lua connector and its games/ modules.
    private static string ScriptsDirectory
        => Path.Combine(AppContext.BaseDirectory, "Plugins", "Scripts");

    private string LuaScriptPath
        => Path.Combine(ScriptsDirectory, LuaScriptName);

    // UA header set exactly once (P3-2 twin) — per-install TryParseAdd on a
    // shared static client grows the header and races concurrent requests.
    private static readonly HttpClient _http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.TryParseAdd("Archipelago-Launcher/2.0");
        return http;
    }

    private async Task InstallBizHawkAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        // ── 1. Resolve the PINNED release URL from GitHub API ────────────────
        // §3: apworlds are built against specific emulator behaviors, so we
        // download the EXACT pinned tag for the selected backend (from
        // EmulatorBackends) rather than chasing "latest" — which can break
        // overnight. The pinned tag's win-x64 ZIP is resolved below; if that
        // tag is unreachable we fall back to latest with a logged warning.
        // Resolve through the SAME helper EmulatorDirectory uses, so the backend
        // we download and the folder we extract into never disagree.
        var backend = ResolveSelectedBackend();
        string repo  = backend.DownloadRepo;
        string tag   = backend.PinnedVersion;
        progress.Report((5, $"Checking {backend.DisplayName} {tag} release..."));

        string? downloadUrl   = null;
        string? assetName     = null;
        string  resolvedVer   = tag;

        // Pinned tag first.
        try
        {
            string tagUrl    = $"https://api.github.com/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}";
            string tagJson   = await _http.GetStringAsync(tagUrl, ct);
            using var tagDoc = JsonDocument.Parse(tagJson);
            (downloadUrl, assetName) = FindBackendAsset(tagDoc.RootElement, backend);
            if (downloadUrl == null)
                Trace($"pinned {backend.DisplayName} tag {tag} has no {backend.AssetSystemTag}{backend.ArchiveExt} asset — falling back to latest");
        }
        catch (Exception ex)
        {
            Trace($"pinned BizHawk tag {tag} unreachable ({ex.Message}) — falling back to latest");
        }

        // Fallback: latest release (only when the pin could not be resolved).
        if (downloadUrl == null)
        {
            progress.Report((6, "Pinned version unavailable — checking latest BizHawk..."));
            string apiUrl    = $"https://api.github.com/repos/{repo}/releases/latest";
            string apiJson   = await _http.GetStringAsync(apiUrl, ct);
            using var apiDoc = JsonDocument.Parse(apiJson);
            (downloadUrl, assetName) = FindBackendAsset(apiDoc.RootElement, backend);
            resolvedVer = apiDoc.RootElement.TryGetProperty("tag_name", out var tn)
                ? (tn.GetString() ?? tag)
                : tag;
        }

        if (downloadUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows BizHawk release asset.\n" +
                "Download manually from https://github.com/TASEmulators/BizHawk/releases");

        // ── 2. Download ZIP to a temp file ───────────────────────────────────
        progress.Report((10, $"Downloading {assetName}..."));

        string tempDir  = Path.Combine(Path.GetTempPath(), "bizhawk_install");
        Directory.CreateDirectory(tempDir);
        string tempZip  = Path.Combine(tempDir, assetName!);

        using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? totalBytes  = response.Content.Headers.ContentLength;
        await using var stream   = await response.Content.ReadAsStreamAsync(ct);
        await using var fileOut  = File.Create(tempZip);

        var   buf          = new byte[81920];
        long  downloaded   = 0;
        int   read;
        while ((read = await stream.ReadAsync(buf, ct)) > 0)
        {
            await fileOut.WriteAsync(buf.AsMemory(0, read), ct);
            downloaded += read;
            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                int pct = (int)(10 + 70.0 * downloaded / totalBytes.Value);
                progress.Report((pct, $"Downloading {assetName} ({downloaded / 1048576}MB / {totalBytes / 1048576}MB)..."));
            }
        }
        await fileOut.DisposeAsync();

        // ── 3. Extract ZIP to EmulatorDirectory ──────────────────────────────
        // Re-installing used to wipe the WHOLE emulator folder — including
        // SaveRAM/, save states, cheats and config.ini. For AP ROM games the
        // SaveRAM IS the player's progress (P2-9). Park the user data next to
        // the install (same volume, so Directory.Move never copies), wipe,
        // extract, then restore — restored entries overwrite extracted defaults.
        progress.Report((78, "Preserving save data..."));
        string backupRoot = EmulatorDirectory.TrimEnd('\\', '/') + "_userdata.bak";
        try { if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, recursive: true); }
        catch { /* stale leftover — restore below merges around it */ }
        BackupUserData(backupRoot);

        try
        {
            progress.Report((80, $"Extracting {backend.DisplayName}..."));
            if (Directory.Exists(EmulatorDirectory))
                Directory.Delete(EmulatorDirectory, recursive: true);
            Directory.CreateDirectory(EmulatorDirectory);

            // .zip via the BCL, .7z via Windows bsdtar — ArchiveExtractor picks.
            ArchiveExtractor.Extract(tempZip, EmulatorDirectory);

            // Some archives extract into a single sub-folder (BizHawk_X.Y.Z/ or
            // snes9x-…-win32-x64/) — lift it so the exe sits at the top level.
            ArchiveExtractor.FlattenSingleSubdir(EmulatorDirectory, backend.ExeName);
        }
        finally
        {
            // The user's data comes back even when the extract failed —
            // losing saves is never acceptable.
            RestoreUserData(backupRoot);
        }

        // ── 4. Clean up temp ─────────────────────────────────────────────────
        progress.Report((95, "Cleaning up..."));
        try { File.Delete(tempZip); } catch { /* non-fatal */ }

        if (!File.Exists(Path.Combine(EmulatorDirectory, backend.ExeName)))
            throw new FileNotFoundException(
                $"{backend.DisplayName} extracted but {backend.ExeName} not found. " +
                $"Check the install directory: {EmulatorDirectory}");

        // Record which version actually landed so the launcher knows what is
        // present (§3 — the pin can fall back to latest; this captures the real
        // resolved tag). A stamp file next to the exe, written best-effort.
        try
        {
            File.WriteAllText(
                Path.Combine(EmulatorDirectory, "ap_installed_version.txt"),
                resolvedVer);
        }
        catch { /* informational only — never block a successful install */ }

        progress.Report((100, $"{backend.DisplayName} {resolvedVer} installed successfully."));
    }

    /// Find the backend's Windows asset on a release JSON element by its platform
    /// token + archive extension (BizHawk "win-x64".zip, snes9x "win32-x64".7z).
    /// The extension guard skips the Linux .tar.gz that also carries the token.
    private static (string? Url, string? Name) FindBackendAsset(
        JsonElement release, EmulatorBackend backend)
    {
        if (release.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains(backend.AssetSystemTag, StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(backend.ArchiveExt, StringComparison.OrdinalIgnoreCase))
                {
                    return (asset.GetProperty("browser_download_url").GetString(), name);
                }
            }
        }
        return (null, null);
    }

    /// The emulator version recorded by the last successful install (the tag the
    /// pin actually resolved to), or null when nothing has been installed yet.
    private string? InstalledEmulatorVersion
    {
        get
        {
            try
            {
                string stamp = Path.Combine(EmulatorDirectory, "ap_installed_version.txt");
                return File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : null;
            }
            catch { return null; }
        }
    }

    // ── User-data preservation across reinstall (P2-9) ────────────────────────
    // BizHawk keeps battery saves, save states, cheats and its config either
    // flat at the root (SaveRAM/, config.ini) or nested one level under a
    // per-system folder (GBA/SaveRAM/, GBA/State/) depending on version and
    // path config — both layouts are covered.

    private static readonly string[] UserDataDirNames =
        { "SaveRAM", "Saves", "State", "Savestates", "Cheats", "Firmware" };
    private static readonly string[] UserDataRootFiles =
        { "config.ini" };

    /// Move every user-data item out of EmulatorDirectory into backupRoot,
    /// mirroring relative paths. Per-item best effort — one locked file must
    /// not strand the rest.
    private void BackupUserData(string backupRoot)
    {
        if (!Directory.Exists(EmulatorDirectory)) return;

        var relPaths = new List<string>();
        try
        {
            foreach (var name in UserDataDirNames)
                if (Directory.Exists(Path.Combine(EmulatorDirectory, name)))
                    relPaths.Add(name);

            foreach (var file in UserDataRootFiles)
                if (File.Exists(Path.Combine(EmulatorDirectory, file)))
                    relPaths.Add(file);

            // Per-system layout: "GBA\SaveRAM", "SNES\State", ...
            foreach (var sysDir in Directory.GetDirectories(EmulatorDirectory))
                foreach (var name in UserDataDirNames)
                    if (Directory.Exists(Path.Combine(sysDir, name)))
                        relPaths.Add(Path.GetRelativePath(
                            EmulatorDirectory, Path.Combine(sysDir, name)));
        }
        catch { /* enumeration failed — nothing to preserve */ }

        foreach (var relPath in relPaths)
        {
            try
            {
                string src  = Path.Combine(EmulatorDirectory, relPath);
                string dest = Path.Combine(backupRoot, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if      (Directory.Exists(src)) Directory.Move(src, dest);
                else if (File.Exists(src))      File.Move(src, dest, overwrite: true);
            }
            catch { /* skip the locked item, keep the rest */ }
        }
    }

    /// Merge the backed-up tree back into EmulatorDirectory (user data wins
    /// over freshly extracted defaults), then drop the backup folder.
    private void RestoreUserData(string backupRoot)
    {
        if (!Directory.Exists(backupRoot)) return;
        try
        {
            MoveTreeOverwrite(backupRoot, EmulatorDirectory);
            Directory.Delete(backupRoot, recursive: true);
        }
        catch { /* leftovers stay in *_userdata.bak — recoverable by hand */ }
    }

    /// Recursive merge-move: files overwrite; directories move wholesale when
    /// the target is absent, otherwise merge item by item.
    private static void MoveTreeOverwrite(string srcDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (string f in Directory.GetFiles(srcDir))
        {
            try { File.Move(f, Path.Combine(destDir, Path.GetFileName(f)), overwrite: true); }
            catch { /* locked target — keep the extracted copy */ }
        }

        foreach (string d in Directory.GetDirectories(srcDir))
        {
            string dest = Path.Combine(destDir, Path.GetFileName(d));
            if (!Directory.Exists(dest) && !File.Exists(dest))
            {
                try { Directory.Move(d, dest); continue; }
                catch { /* fall through to merge */ }
            }
            MoveTreeOverwrite(d, dest);
        }
    }

    private void WriteApConfig(ApSession session, string? pipeName = null,
                               string? launchRom = null)
    {
        // Write a JSON sidecar that the Lua connector reads on startup.
        //   pipe_name   → BASE name of this launch's pipe pair; the connector
        //                 appends "_c2s" (its send pipe) and "_s2c" (its
        //                 receive pipe) — see PIPE PROTOCOL in the class header
        //   lua_module  → which Plugins/Scripts/games/<module>.lua to dofile()
        //   script_dir  → absolute path of Plugins/Scripts/ so the connector can
        //                 locate games/ without relying on its own script path
        //                 (it still falls back to debug.getinfo if absent).
        //   slot_number / locations / slot_data → multiworld context for the
        //                 game module (own slot id, the slot's full server
        //                 location-id set from the Connected packet, and the
        //                 seed's game options). Null when AP never connected —
        //                 modules must degrade to read-only safely.
        //   rom         → the ROM actually launched (lets the module log
        //                 patched-vs-vanilla without guessing).
        string configPath = Path.Combine(EmulatorDirectory, "ap_config.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new
        {
            server      = session.ServerUri,
            slot        = session.SlotName,
            password    = session.Password,
            game        = session.Game,
            pipe_name   = pipeName,
            lua_module  = LuaModuleName,
            script_dir  = ScriptsDirectory,
            rom         = launchRom,
            slot_number = GetOwnSlot?.Invoke() ?? 0,
            locations   = GetServerLocations?.Invoke(),
            slot_data   = GetSlotData?.Invoke(),
        }));
    }
}
