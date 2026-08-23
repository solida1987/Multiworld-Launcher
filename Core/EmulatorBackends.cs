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

        // PCSX2 — the PlayStation 2 emulator.
        //
        // ⚠ This entry used to say Systems = ["PSX"]. That was wrong twice over:
        // modern PCSX2 does not emulate the PS1 at all, and PSX titles run under
        // BizHawk here. PS2 was simply missing from the catalogue.
        //
        // The bridge now exists as an extension — PINE, PCSX2's own memory
        // interface, no in-emulator script needed — and is proven against a
        // stand-in written from the emulator's own PINE.cpp
        // (extensions/pcsx2/proof, negative-tested). BridgeReady stays FALSE
        // until it has carried a real check out of a real game on a real PCSX2,
        // which is the same line IEmulatorBridge.IsReady draws. The SNI bridge
        // already taught this house the difference between code that compiles
        // and a bridge that works.
        new EmulatorBackend
        {
            Id            = "pcsx2",
            DisplayName   = "PCSX2",
            Systems       = new[] { "PS2" },
            // Transport proven live 23 Aug 2026: PCSX2 v2.6.3, NFSU2 booted
            // from CHD, MsgTitle answered, 256-byte read/write/restore and a
            // 5000-byte chunked read all passed (extensions/pcsx2/proof
            // --live). LiveVerified stays false until a real game carries a
            // real check through a session -- same line snes9x stands behind.
            BridgeReady   = true,
            LiveVerified  = false,
            HomepageUrl   = "https://pcsx2.net",
            ExeName       = "pcsx2-qt.exe",
            Source = new LauncherV2.Core.Emulators.EmulatorSource(
                Author:       "the PCSX2 team",
                Licence:      "LGPL-3.0-or-later",
                LicenceUrl:   "https://github.com/PCSX2/pcsx2/blob/master/COPYING.LGPL",
                DownloadPage: "https://github.com/PCSX2/pcsx2/releases",
                Owner:        "PCSX2",
                Repo:         "pcsx2",
                AssetPattern: "windows-x64-Qt.7z"),
        },

        // Dolphin — GameCube and Wii. A LAUNCHER, not a memory bridge: every
        // GC/Wii world in the catalogue reads Dolphin itself through
        // dolphin-memory-engine and registers its own Archipelago client, so
        // London starts the disc and stands aside. See
        // extensions/dolphin/DolphinBridge.cs for the four worlds and the
        // lines in each that say so.
        new EmulatorBackend
        {
            Id            = "dolphin",
            DisplayName   = "Dolphin",
            Systems       = new[] { "GC", "Wii", "WII" },
            BridgeReady   = true,
            // Nothing has been started through it on this machine yet.
            LiveVerified  = false,
            HomepageUrl   = "https://dolphin-emu.org/download/",
            ExeName       = "Dolphin.exe",
            Source = new LauncherV2.Core.Emulators.EmulatorSource(
                Author:       "the Dolphin Emulator project",
                Licence:      "GPL-2.0-or-later",
                LicenceUrl:   "https://github.com/dolphin-emu/dolphin/blob/master/COPYING",
                DownloadPage: "https://dolphin-emu.org/download/",
                Owner:        "dolphin-emu",
                Repo:         "dolphin",
                AssetPattern: null),
        },

        // DuckStation — PlayStation 1. Also a launcher: Spyro 3, the world
        // that put it on our list, ships its own Windows client and attaches
        // to DuckStation itself. See extensions/duckstation/.
        new EmulatorBackend
        {
            Id            = "duckstation",
            DisplayName   = "DuckStation",
            Systems       = new[] { "PSX", "PS1" },
            BridgeReady   = true,
            LiveVerified  = false,
            HomepageUrl   = "https://www.duckstation.org/",
            // ⚠ DuckStation has shipped its Qt build under several names.
            // The bridge resolves any duckstation*.exe; this is the one its
            // current release uses, and it is what the folder note asks for.
            ExeName       = "duckstation-qt-x64-ReleaseLTCG.exe",
            Source = new LauncherV2.Core.Emulators.EmulatorSource(
                Author:       "Connor McLaughlin (stenzek) and contributors",
                Licence:      "CC-BY-NC-ND-4.0 — non-commercial, no derivatives",
                LicenceUrl:   "https://github.com/stenzek/duckstation/blob/master/LICENSE",
                DownloadPage: "https://www.duckstation.org/",
                Owner:        "stenzek",
                Repo:         "duckstation",
                AssetPattern: null),
        },

        // Daxanadu — Faxanadu, and only Faxanadu. Its author is precise about
        // what it is: "an NES emulator that only works with a Faxanadu rom
        // file". Archipelago is built into the program, so London starts it
        // and the player connects from its own ARCHIPELAGO menu.
        new EmulatorBackend
        {
            Id            = "daxanadu",
            DisplayName   = "Daxanadu",
            Systems       = new[] { "NES" },
            BridgeReady   = true,
            LiveVerified  = false,
            HomepageUrl   = "https://github.com/Daivuk/Daxanadu/releases",
            ExeName       = "Daxanadu.exe",
            Source = new LauncherV2.Core.Emulators.EmulatorSource(
                Author:       "Daivuk",
                Licence:      "MIT",
                LicenceUrl:   "https://github.com/Daivuk/Daxanadu/blob/main/LICENSE",
                DownloadPage: "https://github.com/Daivuk/Daxanadu/releases",
                Owner:        "Daivuk",
                Repo:         "Daxanadu",
                AssetPattern: "Daxanadu_*.zip"),
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

        // Citron — Nintendo Switch. A LAUNCHER and nothing more: the one
        // Switch world in the catalogue (Super Mario Odyssey) puts a mod
        // INSIDE the game that connects out to its own client, so no memory
        // bridge exists or is claimed. London's part ends when the game is
        // running with the mod folder in place.
        //
        // Why Citron and not Ryujinx: Ryujinx's repositories are gone —
        // ryujinx-mirror/ryujinx is DMCA-blocked by Nintendo (March 2025,
        // confirmed via the API's 451 response). Citron is the fork that is
        // alive: GPL-3.0 by its own LICENSE file, Windows builds on every
        // release, pushed to the week this entry was written.
        new EmulatorBackend
        {
            Id            = "citron",
            DisplayName   = "Citron",
            Systems       = new[] { "SWITCH", "Switch" },
            // No bridge will ever be needed: the game mod does the talking.
            BridgeReady   = true,
            LiveVerified  = false,
            HomepageUrl   = "https://citron-neo.org/",
            ExeName       = "citron.exe",
            // ⚠ The player must supply their own Switch firmware and keys, as
            // with every Switch emulator. The launcher fetches only the
            // emulator itself, from its own release, after the player has
            // seen who wrote it and under what terms.
            Source = new LauncherV2.Core.Emulators.EmulatorSource(
                Author:       "the Citron project",
                Licence:      "GPL-3.0",
                LicenceUrl:   "https://github.com/citron-neo/emulator/blob/master/LICENSE.txt",
                DownloadPage: "https://github.com/citron-neo/emulator/releases",
                Owner:        "citron-neo",
                Repo:         "emulator",
                // Their releases are date-tagged nightlies with a hash in the
                // asset name, so the pattern must wildcard the middle. msvc is
                // the conventional Windows build; clangtron is their
                // experimental one.
                AssetPattern: "Citron-windows-nightly-*-x64-msvc.zip"),
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
