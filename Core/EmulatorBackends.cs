using System;
using System.Collections.Generic;
using System.Linq;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// EmulatorBackends — static catalog of the emulator backends the launcher knows.
//
// Owner spec §14 ("Emulator" dropdown, per-game emulator choice). The launcher's
// game-logic lives in per-game Lua modules (Plugins/Scripts/games/*.lua) talking
// the newline-framed CHECK/GOAL/SYNC protocol over the BizHawk two-pipe CRT
// bridge — so emulator CHOICE is a per-system add-on, not a rewrite. Only one
// backend has a working AP check bridge today (BizHawk); the others are listed so
// the UI can show them HONESTLY (greyed "coming soon") instead of pretending.
//
// BridgeReady is the honesty gate: it is true ONLY for a backend whose AP check
// bridge actually works end-to-end (BizHawk — proven with Pokémon Emerald). Every
// other backend is BridgeReady=false until its dialect is ported in a later wave
// (snes9x NWA-TCP, mGBA/Mesen Lua-TCP — see EMULATOR_MATRIX_2026-06-12.md §6.2).
//
// VERSION PINNING (§3): apworlds are built against specific emulator behaviors, so
// we pin an exact release tag instead of chasing GitHub "latest" (which can break
// overnight). PinnedVersion is the tag to download; the installer resolves that
// tag's win-x64 asset and falls back to latest only if the tag is unreachable.
//
// Sources for the populated values: Research_V2/EMULATOR_MATRIX_2026-06-12.md
// (per-emulator capability table §2, build-order §6.2). BizHawk tag verified live
// against the TASEmulators/BizHawk GitHub releases API (latest = 2.11.1, asset
// "BizHawk-2.11.1-win-x64.zip", bare-numeric tag format).
// ═══════════════════════════════════════════════════════════════════════════════

/// How the launcher talks to an emulator's memory.
///   Pipe = in-emulator Lua opens the launcher's named pipes (BizHawk).
///   Nwa  = launcher is a TCP client of the emulator's NWA server and runs the
///          game logic itself via Snes9xLuaBridge (snes9x-emunwa).
public enum BridgeDialect { Pipe, Nwa }

/// One emulator backend the launcher can describe (and, when BridgeReady, drive).
public sealed record EmulatorBackend
{
    /// Stable internal id, e.g. "bizhawk", "snes9x", "mgba", "mesen".
    public required string Id { get; init; }

    /// Dropdown label, e.g. "BizHawk".
    public required string DisplayName { get; init; }

    /// Systems this emulator can host (matched against EmulatorPlugin.RomSystem),
    /// e.g. ["GBA","GBC","GB","SNES","NES","N64","GEN",...].
    public required string[] Systems { get; init; }

    /// True ONLY for backends with a working AP check bridge. BizHawk = true
    /// today; every other backend = false until its dialect ships. The UI uses
    /// this to decide which entries are pickable vs. greyed "(coming soon)".
    public required bool BridgeReady { get; init; }

    /// GitHub "owner/repo" (or a CDN base) the installer downloads from.
    public required string DownloadRepo { get; init; }

    /// The EXACT release tag to download — NOT "latest" (§3). For BizHawk this is
    /// the tag the win-x64 asset hangs off (e.g. "2.11.1").
    public required string PinnedVersion { get; init; }

    /// Executable that identifies a present install / is launched, e.g.
    /// "EmuHawk.exe".
    public required string ExeName { get; init; }

    /// Transport dialect (which launch + bridge path the plugin uses).
    public BridgeDialect Dialect { get; init; } = BridgeDialect.Pipe;

    /// Platform token in the release asset name: "win-x64" (BizHawk .zip) vs
    /// "win32-x64" (snes9x-emunwa .7z). Matched case-insensitively.
    public string AssetSystemTag { get; init; } = "win-x64";

    /// Release archive extension: ".zip" (BCL) or ".7z" (Windows bsdtar).
    public string ArchiveExt { get; init; } = ".zip";

    /// True once confirmed against the REAL emulator (not just mock-verified).
    /// A BridgeReady backend that is not yet LiveVerified is selectable but
    /// labelled "(experimental)" — fully wired, awaiting its first live run.
    public bool LiveVerified { get; init; } = true;

    /// Folder the launcher installs this backend into, next to the exe:
    /// Emulators/&lt;id&gt;. BizHawk keeps its historical "Emulators/BizHawk".
    public string InstallSubdir => Id == "bizhawk" ? "BizHawk" : Id;
}

/// Static registry of known emulator backends + lookup helpers.
public static class EmulatorBackends
{
    // BizHawk is pinned to 2.11.1 — the current stable tag on
    // TASEmulators/BizHawk (released 2026-05-01; verified via the releases API).
    // The matrix's capability table cites the 2.10/2.11 line as the known-good
    // baseline; 2.11.1 is the latest patch release of that line. Tags are bare
    // numeric ("2.11.1", "2.11", "2.10") and the win-x64 asset is named
    // "BizHawk-<version>-win-x64.zip".
    public const string BizHawkPinnedVersion = "2.11.1";

    /// Every backend the launcher knows about (ordered: working first).
    public static readonly IReadOnlyList<EmulatorBackend> All = new[]
    {
        // ── The one working backend ──────────────────────────────────────────
        new EmulatorBackend
        {
            Id            = "bizhawk",
            DisplayName   = "BizHawk",
            // BizHawk hosts (nearly) the whole emulated catalog natively,
            // including NDS via its built-in melonDS core (matrix §1, §2).
            Systems       = new[] { "GBA", "GBC", "GB", "SNES", "NES", "N64",
                                    "GEN", "SMS", "A26", "PSX", "NDS" },
            BridgeReady   = true,                       // proven (Pokémon Emerald)
            DownloadRepo  = "TASEmulators/BizHawk",
            PinnedVersion = BizHawkPinnedVersion,
            ExeName       = "EmuHawk.exe",
        },

        // PCSX2 — dedicated PS1/PS2 emulator. AP bridge not yet built; listed so
        // PSX games (MediEvil) show "PCSX2 (coming soon)" honestly and the user
        // understands BizHawk is the current path for PSX AP play. BridgeReady
        // will flip true once a Lua/NWA bridge is ported to the PCSX2 scripting
        // interface (PINE protocol / QMT). Pinned to 2.3.61 (2026-05 stable).
        new EmulatorBackend
        {
            Id            = "pcsx2",
            DisplayName   = "PCSX2",
            Systems       = new[] { "PSX" },
            BridgeReady   = false,
            DownloadRepo  = "PCSX2/pcsx2",
            PinnedVersion = "2.3.61",
            ExeName       = "pcsx2-qt.exe",
        },

        // snes9x — the literal §14 Discord request ("SNES: BizHawk or snes9x").
        // Uses the snes9x-nwa fork (NWA TCP protocol, no in-emu script); modern
        // core based on snes9x 1.62.3 (matrix §2, §6.2). Fully wired and live-
        // verified — install (.7z), launch, NWA connect, and the Lua bridge
        // (ArchiveExtractor + NwaClient + Snes9xLuaBridge over NWA, 12/12).
        new EmulatorBackend
        {
            Id            = "snes9x",
            DisplayName   = "snes9x",
            Systems       = new[] { "SNES" },
            BridgeReady   = true,
            LiveVerified  = true,
            DownloadRepo  = "Skarsnik/snes9x-emunwa",
            // Verified via the releases API: latest non-prerelease tag, asset
            // "snes9x-1.63-nwa-win32-x64.7z", exe "snes9x-x64.exe".
            PinnedVersion = "1.63-sa1",
            ExeName       = "snes9x-x64.exe",
            Dialect       = BridgeDialect.Nwa,
            AssetSystemTag= "win32-x64",
            ArchiveExt    = ".7z",
        },
    };

    /// Look up a backend by id (case-insensitive); null when unknown.
    public static EmulatorBackend? ById(string? id)
        => id == null
            ? null
            : All.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    /// All backends that can host <paramref name="system"/> (e.g. "SNES"),
    /// in registry order (working backend first).
    public static IReadOnlyList<EmulatorBackend> BackendsForSystem(string system)
        => All.Where(b => b.Systems.Contains(system, StringComparer.OrdinalIgnoreCase))
              .ToList();

    /// The default backend for a system: the first BridgeReady one (always
    /// BizHawk today). Falls back to the first backend listed for the system,
    /// then to BizHawk, so this never returns null for a known system.
    public static EmulatorBackend Default(string system)
    {
        var forSystem = BackendsForSystem(system);
        return forSystem.FirstOrDefault(b => b.BridgeReady)
            ?? forSystem.FirstOrDefault()
            ?? ById("bizhawk")!;
    }
}
