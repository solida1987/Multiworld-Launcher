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
// Attach = the launcher starts nothing: it attaches to a helper the player
// (or the launcher, when the helper is in its folder) runs — SNI — and runs
// the game logic itself over that helper's protocol.
public enum BridgeDialect { Pipe, Nwa, Attach }

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

    // Does picking this actually start the game?
    //
    // Every entry in the emulator menu is a promise that pressing Play opens
    // the game. SNI breaks that promise by design: it is not an emulator, it
    // ATTACHES to one you are already running (or to real hardware). Listed
    // beside BizHawk and snes9x it read as a third way to play, and picking it
    // produced a launcher that looked busy and did nothing — every time.
    //
    // The transport stays; it is how a player on original hardware or their own
    // emulator connects. It just does not belong in a list of ways to start a
    // game, and will come back as its own explicit choice rather than a peer.
    public bool LaunchesGame { get; init; } = true;

    // Where this program comes from, and whose work it is.
    //
    // Only bridge EXTENSIONS could declare this before, through
    // BridgeRegistry.OfferFor. But the launcher drives some backends itself
    // (the NWA and Attach dialects have no extension), so those had no
    // declaration and the only thing left to do was open a browser on somebody
    // else's repository — which is a strange place to land from a game page.
    // A backend the launcher drives declares its own source here, and gets the
    // same consent window and installer as an extension-declared one.
    //
    // Null means no offer: the player is told where to get it and does it
    // themselves. Nothing is ever bundled, and nothing is fetched without the
    // author and the licence being shown and agreed to first.
    public LauncherV2.Core.Emulators.EmulatorSource? Source { get; init; }
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
                                    "GEN", "SMS", "A26", "2600", "PSX", "NDS" },
            BridgeReady   = true,                       // proven (Pokémon Emerald)
            HomepageUrl   = "https://tasvideos.org/BizHawk",
            ExeName       = "EmuHawk.exe",
            // Verified against the releases API: tag 2.11.1 carries exactly
            // BizHawk-2.11.1-win-x64.zip for Windows.
            //
            // The licence line is deliberately not the flat "MIT" GitHub would
            // suggest. BizHawk's own LICENSE says the team's work is MIT but the
            // repository also embeds cores under other, partly incompatible
            // licences, and calls its own condition "a minefield". The player is
            // told that and given the file to read, rather than a tidy label
            // that is not quite true.
            Source = new LauncherV2.Core.Emulators.EmulatorSource(
                Author:       "the BizHawk team",
                Licence:      "MIT for BizHawk's own work; bundled cores carry their own",
                LicenceUrl:   "https://github.com/TASEmulators/BizHawk/blob/master/LICENSE",
                DownloadPage: "https://github.com/TASEmulators/BizHawk/releases",
                Owner:        "TASEmulators",
                Repo:         "BizHawk",
                AssetPattern: "BizHawk-2.11.1-win-x64.zip",
                PinnedTag:    "2.11.1"),
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
            DisplayName   = "snes9x (NWA build)",
            Systems       = new[] { "SNES" },
            BridgeReady   = true,
            // Wired and mock-verified 12/12, never yet seen carrying a check
            // from a real emulator — shown "(experimental)" until it has been.
            LiveVerified  = false,
            HomepageUrl   = "https://github.com/Skarsnik/snes9x-emunwa",
            // Verified via the releases API: latest non-prerelease tag, asset
            // "snes9x-1.63-nwa-win32-x64.7z", exe "snes9x-x64.exe".
            ExeName       = "snes9x-x64.exe",
            Dialect       = BridgeDialect.Nwa,
            // Read off the repository, not assumed: the only asset on the
            // current release is snes9x-1.63-nwa-win32-x64.7z under tag
            // 1.63-sa1, and snes9x's own licence grants binary distribution
            // "for non-commercial purposes" while calling itself "freeware for
            // PERSONAL USE only". So the launcher may fetch a copy FOR the
            // player from the author's own release once they have seen who
            // wrote it and under what terms — and may never ship it itself.
            Source = new LauncherV2.Core.Emulators.EmulatorSource(
                Author:       "Skarsnik (NWA fork) — snes9x by the Snes9x team",
                Licence:      "Snes9x licence — non-commercial, personal use only",
                LicenceUrl:   "https://github.com/Skarsnik/snes9x-emunwa/blob/master/LICENSE",
                DownloadPage: "https://github.com/Skarsnik/snes9x-emunwa/releases",
                Owner:        "Skarsnik",
                Repo:         "snes9x-emunwa",
                AssetPattern: "snes9x-1.63-nwa-win32-x64.7z",
                PinnedTag:    "1.63-sa1"),
        },

        // SNI — not an emulator but the SNES community's bridge program: it
        // attaches to snes9x-emunwa, RetroArch, or real hardware (FX Pak Pro)
        // and serves their memory over one protocol. Picking it means "I run my
        // own SNES emulator; the launcher connects through SNI". The launcher
        // starts sni.exe from Emulators\sni\ when it is there, and the sni
        // bridge extension carries the transport.
        new EmulatorBackend
        {
            Id            = "sni",
            DisplayName   = "SNI (own emulator or console)",
            Systems       = new[] { "SNES" },
            BridgeReady   = true,
            LiveVerified  = false,
            HomepageUrl   = "https://github.com/alttpo/sni",
            ExeName       = "sni.exe",
            Dialect       = BridgeDialect.Attach,
            // Not a way to start a game — see LaunchesGame. Kept working and
            // shipped, kept out of the picker.
            LaunchesGame  = false,
            // Verified against the releases API: tag v0.0.103 publishes ten
            // platform builds, of which sni-v0.0.103-windows-amd64.zip is the
            // 64-bit Windows one. Plain MIT, stated as such by the project.
            Source = new LauncherV2.Core.Emulators.EmulatorSource(
                Author:       "the SNI project (alttpo)",
                Licence:      "MIT",
                LicenceUrl:   "https://github.com/alttpo/sni/blob/main/LICENSE",
                DownloadPage: "https://github.com/alttpo/sni/releases",
                Owner:        "alttpo",
                Repo:         "sni",
                AssetPattern: "sni-v0.0.103-windows-amd64.zip",
                PinnedTag:    "v0.0.103"),
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
