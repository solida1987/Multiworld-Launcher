using System;
using System.Collections.Generic;
using System.Linq;

namespace LauncherV2.Core;

// EmulatorBackends — static catalog of the emulator backends the launcher knows.

// Owner spec §14 ("Emulator" dropdown, per-game emulator choice).
// game-logic lives in per-game Lua modules (Plugins/Scripts/games/*.lua) talking
// the newline-framed CHECK/GOAL/SYNC protocol over the BizHawk two-pipe CRT
// bridge — so emulator CHOICE is a per-system add-on, not a rewrite.
// backend has a working AP check bridge today (BizHawk); the others are listed so
// the UI can show them HONESTLY (greyed "coming soon") instead of pretending.

// ⚠ THIS TABLE NO LONGER DECIDES HOW A GAME STARTS.
//
// Launching is owned by the installed bridge extension: LaunchAsync asks
// BridgeRegistry for the game's protocol and runs the LaunchPlan it gets back.
// What survives here is description — the "Emulator" dropdown in the UI, and
// the Emulators\<InstallSubdir>\ folders with their notes.
//
// BridgeReady is still the honesty gate for the dropdown: true only for a
// backend whose AP check bridge actually works end-to-end. Whether that bridge
// is INSTALLED is a separate question, and BridgeRegistry answers it.

// The launcher never downloads an emulator -- HomepageUrl is shown to the player
// so they can fetch their own, and it lands in Emulators\<InstallSubdir>\.

// How the launcher talks to an emulator's memory.
// Pipe = in-emulator Lua opens the launcher's named pipes (BizHawk).
// Nwa = launcher is a TCP client of the emulator's NWA server and runs the
// game logic itself via Snes9xLuaBridge (snes9x-emunwa).
public enum BridgeDialect { Pipe, Nwa }

// One emulator backend the launcher can describe (and, when BridgeReady, drive).
public sealed record EmulatorBackend
{
    // Stable internal id, e.g.
    public required string Id { get; init; }

    // Dropdown label, e.g. "BizHawk".
    public required string DisplayName { get; init; }

    // Systems this emulator can host (matched against EmulatorPlugin.RomSystem),
    // e.g. ["GBA","GBC","GB","SNES","NES","N64","GEN",...].
    public required string[] Systems { get; init; }

    // True ONLY for backends with a working AP check bridge.
    // today; every other backend = false until its dialect ships.
    // this to decide which entries are pickable vs.
    public required bool BridgeReady { get; init; }
    // Where the player gets this emulator themselves. Shown to them; the
    // launcher never fetches it.
    public required string HomepageUrl { get; init; }

    // The EXACT release tag to download — NOT "latest" (§3).

    // Executable that identifies a present install / is launched, e.g.
    // "EmuHawk.exe".
    public required string ExeName { get; init; }

    // Transport dialect (which launch + bridge path the plugin uses).
    public BridgeDialect Dialect { get; init; } = BridgeDialect.Pipe;

    // Platform token in the release asset name: "win-x64" (BizHawk .zip) vs


    // True once confirmed against the REAL emulator (not just mock-verified).
    // A BridgeReady backend that is not yet LiveVerified is selectable but
    // labelled "(experimental)" — fully wired, awaiting its first live run.
    public bool LiveVerified { get; init; } = true;

    // Folder the launcher installs this backend into, next to the exe:
    // Emulators/<id>.
    public string InstallSubdir => Id == "bizhawk" ? "BizHawk" : Id;
}

// Static registry of known emulator backends + lookup helpers.
public static class EmulatorBackends
{
    // BizHawk is pinned to 2.11.1 — the current stable tag on
    // TASEmulators/BizHawk (released 2026-05-01; verified via the releases API).
    // The matrix's capability table cites the 2.10/2.11 line as the known-good
    // baseline; 2.11.1 is the latest patch release of that line.
    // numeric ("2.11.1", "2.11", "2.10") and the win-x64 asset is named

    // Every backend the launcher knows about (ordered: working first).
    public static readonly IReadOnlyList<EmulatorBackend> All = new[]
    {
        // --- The one working backend ---
        new EmulatorBackend
        {
            Id            = "bizhawk",
            DisplayName   = "BizHawk",
            // BizHawk hosts (nearly) the whole emulated catalog natively,
            // including NDS via its built-in melonDS core (matrix §1, §2).
            Systems       = new[] { "GBA", "GBC", "GB", "SNES", "NES", "N64",
                                    "GEN", "SMS", "A26", "PSX", "NDS" },
            BridgeReady   = true,                       // proven (Pokémon Emerald)
            HomepageUrl   = "https://tasvideos.org/BizHawk",
            ExeName       = "EmuHawk.exe",
        },

        // PCSX2 — dedicated PS1/PS2 emulator.
        // PSX games (MediEvil) show "PCSX2 (coming soon)" honestly and the user
        // understands BizHawk is the current path for PSX AP play.
        // will flip true once a Lua/NWA bridge is ported to the PCSX2 scripting
        // interface (PINE protocol / QMT).
        new EmulatorBackend
        {
            Id            = "pcsx2",
            DisplayName   = "PCSX2",
            Systems       = new[] { "PSX" },
            BridgeReady   = false,
            HomepageUrl   = "https://pcsx2.net",
            ExeName       = "pcsx2-qt.exe",
        },

        // snes9x — the literal §14 Discord request ("SNES: BizHawk or snes9x").
        // Uses the snes9x-nwa fork (NWA TCP protocol, no in-emu script); modern
        // core based on snes9x 1.62.3 (matrix §2, §6.2).
        // verified — install (.7z), launch, NWA connect, and the Lua bridge
        // (ArchiveExtractor + NwaClient + Snes9xLuaBridge over NWA, 12/12).
        new EmulatorBackend
        {
            Id            = "snes9x",
            DisplayName   = "snes9x",
            Systems       = new[] { "SNES" },
            BridgeReady   = true,
            LiveVerified  = true,
            HomepageUrl   = "https://github.com/Skarsnik/snes9x-emunwa",
            // Verified via the releases API: latest non-prerelease tag, asset
            // "snes9x-1.63-nwa-win32-x64.7z", exe "snes9x-x64.exe".
            ExeName       = "snes9x-x64.exe",
            Dialect       = BridgeDialect.Nwa,
        },
    };

    // Look up a backend by id (case-insensitive); null when unknown.
    public static EmulatorBackend? ById(string? id)
        => id == null
            ? null
            : All.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    // All backends that can host system (e.g.
    // in registry order (working backend first).
    public static IReadOnlyList<EmulatorBackend> BackendsForSystem(string system)
        => All.Where(b => b.Systems.Contains(system, StringComparer.OrdinalIgnoreCase))
              .ToList();

    // The default backend for a system: the first BridgeReady one (always
    // BizHawk today). Falls back to the first backend listed for the system,
    // then to BizHawk, so this never returns null for a known system.
    public static EmulatorBackend Default(string system)
    {
        var forSystem = BackendsForSystem(system);
        return forSystem.FirstOrDefault(b => b.BridgeReady)
            ?? forSystem.FirstOrDefault()
            ?? ById("bizhawk")!;
    }
}
