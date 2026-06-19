using System;
using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// PokemonPinballPlugin — Archipelago integration for Pokemon Pinball
// (Game Boy Color, 1999), on the BizHawk Lua-pipe bridge.
//
// WORLD SOURCE: ArchipelagoMW/Archipelago (AP-main).
//   game string: "Pokemon Pinball"
//   system:      GB (Game Boy / Game Boy Color).
//   emulator:    BizHawk (Gambatte / GBHawk core).
//
// STATUS: STUB — ChecksImplemented = false. No Lua memory module has been
// built and verified for this game through the launcher bridge. The plugin
// registers the game in the catalog so it appears in the UI.
//
// GATE TO ENABLE: build a pokemon_pinball.lua module mapping GB RAM addresses
// to the apworld's location ids, verify in-emulator, then set
// ChecksImplemented = true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PokemonPinballPlugin : EmulatorPlugin
{
    public PokemonPinballPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "pokemon_pinball";
    public override string DisplayName => "Pokemon Pinball";
    public override string Subtitle    => "GB · Emulated";
    public override string ApWorldName => "Pokemon Pinball";

    public override string Description =>
        "Pokemon Pinball (1999, Jupiter / Nintendo) is a Game Boy Color pinball " +
        "game where you catch all 151 original Pokemon across two boards — Red and " +
        "Blue — each with its own unique layout, music, and Pokemon distribution. " +
        "Catching Pokemon requires triggering catch mode and then hitting the " +
        "target Pokemon with the pinball multiple times before it escapes. " +
        "The game is notable for having a built-in rumble pak in the cartridge.\n\n" +
        "In the Archipelago randomizer, Pokemon catches and stage progression " +
        "checks join the multiworld pool. Catch all 151 Pokemon to complete " +
        "your goal.\n\n" +
        "Source: github.com/ArchipelagoMW/Archipelago (AP-main).";

    public override string ThemeAccentColor => "#CC0000";   // Pokemon Pinball red

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "GB";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "pokemon_pinball";

    // No Lua module built or verified yet — stub only.
    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory; emulated
    // ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;
}
