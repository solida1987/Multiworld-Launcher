# Emulator Backend Matrix & Portability Assessment — Owner Spec §14

Date: 2026-06-12
Scope: per-game emulator choice ("Emulator" dropdown), launcher-managed emulator
profiles, and the backend abstraction for `Plugins/Emulated/EmulatorPlugin.cs`.
Baseline: the BizHawk two-pipe CRT bridge (DONE, see `EmulatorPlugin.cs` header
and `Plugins/Scripts/bizhawk_ap_connector.lua`).

---

## 1. Executive summary

1. **BizHawk remains the only emulator that covers (nearly) everything.** Of the
   138 `plugin_type: "emulated"` catalog entries, ~95 are on systems BizHawk
   hosts natively (GBA, GB/GBC, SNES, NES, N64, Genesis, SMS, Atari 2600, PSX,
   and **NDS via its built-in melonDS core**). Our existing Lua-pipe bridge
   already works on all of them unchanged — emulator *choice* is a per-system
   add-on, not a replacement.
2. **The bridge dialect is the real axis, not the exe.** Five dialects cover the
   whole catalog: (A) in-emulator Lua + CRT named pipes (BizHawk — done;
   snes9x-rr portable), (B) in-emulator Lua + **TCP socket** (mGBA, Mesen/CE —
   their Lua exposes sockets, `io` is absent/gated), (C) **launcher-side TCP
   memory protocol**, no script at all (snes9x-nwa "NWA", PCSX2 "PINE",
   RetroArch UDP, Azahar RPC), (D) **launcher-side process-memory reading**
   (Dolphin via the dolphin-memory-engine approach, DuckStation), (E) **native
   port with built-in AP client** — nothing to bridge, the game connects itself
   (Ship of Harkinian, OpenGOAL/Jak, sm64ex, MM Recomp; same pattern as the
   OpenTTD fork).
3. **SNES is the one system where the official AP ecosystem mandates a different
   connector.** The classic worlds (ALttP, Super Metroid, SMZ3, SMW, Yoshi's
   Island, KDL3, EarthBound, FFMQ, Lufia II, Secret of Evermore, CT: Jets of
   Time) connect through **SNI**, not the BizHawk generic client — SNI in turn
   accepts snes9x-rr/snes9x-nwa/BSNES-plus/BizHawk/RetroArch. Because our
   launcher owns the game logic itself (per-game Lua modules — `alttp.lua` and
   `super_metroid.lua` already exist), we are *not* bound by that mandate as
   long as we read the same RAM; but it tells us which emulators the community
   already trusts per game.
4. **Recommended 2nd backend: snes9x — via the NWA fork (`snes9x-emunwa`), with
   the snes9x-rr Lua port as the cheap fallback.** It is the §14 Discord ask
   verbatim ("SNES: BizHawk or snes9x"), SNES is our largest single-system
   cohort (27 entries), and snes9x is dramatically lighter than BizHawk+BSNES.
5. **Ship of Harkinian is not an emulator integration at all** — it ships a
   built-in AP client (connect from the in-game quest-select / ESC menu). The
   launcher integration is the `ConnectsItself` pattern: install the
   AP-enabled SoH build + apworld, generate the OTR from the user's ROM on
   first run, launch, monitor the process. No bridge code. Effort S, high
   user value (full-speed native OoT vs. BizHawk's slow Ares64 core).
6. **Several catalog rows are factually wrong today** and §14's "honest
   availability" rule means they must be corrected before any dropdown ships:
   `super_mario_64` (official AP path is the sm64ex **native port**, not
   BizHawk), `jak_and_daxter` (OpenGOAL native, not PCSX2),
   `the_legend_of_zelda_majoras_mask` (MM Recomp native), all `melonds` rows
   (standalone melonDS has **no scripting** — official path is BizHawk's
   melonDS core), `lime3ds` (project discontinued — successor is **Azahar**),
   `castlevania_symphony_of_the_night` (apworld uses BizHawk generic, not
   DuckStation), `ratchet_clank` PS3 (homebrew/RaCMAN-based, emulator support
   explicitly unplanned), PICO-8 rows (browser **web client**, no emulator).
7. **Watch items:** Mesen2 was archived 2026-06-04 and continues as
   **MesenCE**; DuckStation relicensed (CC-BY-NC-ND, packaging prohibited) —
   we may auto-download its official binaries but must never re-host or
   modify them; simple64 is archived (successor Gopher64, no scripting);
   melonDS Lua is an unmerged PR.

---

## 2. Per-emulator capability table

Hard requirement recap: scriptability with memory READ+WRITE **plus** an IO
channel reachable from the launcher (our CRT pipe trick needs `io.open` of
`\\.\pipe\…`; a TCP socket API is an acceptable substitute — costs us a small
TCP listener next to the existing pipe servers).

| Emulator | Systems | Scripting | Memory R/W | IO channel (pipe `io.open` / socket) | Launch-time script load | Maintenance | Windows | Bridge verdict |
|---|---|---|---|---|---|---|---|---|
| **BizHawk 2.10** | GBA, GB/GBC, SNES, NES, N64 (Ares64/Mupen64Plus), GEN, SMS, 2600, PSX (Nymashock), **NDS (melonDS core)**, more | Lua (mature, NLua), our baseline | Full, all domains | `io.open` pipes **proven** (our bridge); also built-in `--socket-ip/--socket-port` | `--lua=`, `--config=`, `--fullscreen` ([ArgParser.cs](https://github.com/TASEmulators/BizHawk/blob/master/src/BizHawk.Client.Common/ArgParser.cs)) | Active ([repo](https://github.com/TASEmulators/BizHawk)) | Yes (x64) | **DONE — baseline** |
| **snes9x-rr** (1.51/1.53 line) | SNES | Lua 5.1 since 2008, `memory.readbyte/writebyte`, frame loop ([wiki](https://github.com/TASEmulators/snes9x-rr/wiki/Lua-Functions)) | Full bus incl. WRAM 0x7E0000+ | Standard Lua `io` + bundled luasocket (`socket.dll`) — SNI's `Connector.lua` runs on it; CRT pipe trick expected to port 1:1 (verify in-emulator) | `.cfg` can autoload Lua; CLI ROM arg | **Archived** at TASEmulators (legacy; still the community workhorse for SNI) | Yes (x86) | **Portable, dialect A** — cheap port, old core |
| **snes9x-nwa** (`Skarsnik/snes9x-emunwa`, snes9x 1.62.3 base) | SNES | None — instead **NWA TCP protocol** ([emulator-networkaccess](https://github.com/usb2snes/emulator-networkaccess)) | `CORE_MEMORY_READ/WRITE` over TCP | TCP (launcher is the client; no script at all) | Enable "Emulator Network Access" (config-writable); CLI ROM arg | Active ([releases](https://github.com/Skarsnik/snes9x-emunwa/releases)) | Yes (x64) | **Recommended snes9x path, dialect C** — modern core, zero in-emulator code |
| **mGBA 0.10+** | GBA, GB/GBC | Lua 5.4 since 0.10 ([docs](https://mgba.io/docs/scripting.html)): `read8/16/32`, `write8/16/32`, `readRange`, frame callback, storage API | Full | **Custom TCP socket API** (`socket.bind/connect/tcp`, `hasdata`, `receive`, `send`); standard `io` undocumented — assume unavailable, use sockets | **Gap:** stable has no `--script` CLI ([man page](https://man.archlinux.org/man/extra/mgba-qt/mgba-qt.6.en) — only `-C key=value`, `-f`, `-t`, …); scripts load via Tools → Scripting UI. Check 0.11/dev before building | Active ([mgba.io](https://mgba.io/)) | Yes | **Portable, dialect B** — blocked on autoload story |
| **Mesen2 → MesenCE** | NES, SNES, GB/GBC, GBA, PCE, SMS/GG, WS | Lua: `emu.read/write(memType)`, callbacks ([API](https://www.mesen.ca/docs/apireference.html)) | Full, debug domains avoid side-effects | Bundled **LuaSocket** (`require "socket.core"`); `io`/`os` exist but gated behind "Allow access to I/O and OS functions" setting (config-writable) | Script window UI; CLI ROM arg; verify CLI script flag at build time | Mesen2 **archived 2026-06-04**; continued as [MesenCE](https://github.com/nesdev-org/MesenCE) (2.2.x, active) | Yes | **Portable, dialect B** — one backend covering 4 of our systems; fork-transition risk |
| **Project64 4.x** | N64 | **JavaScript** (Duktape, ES5+) ([API doc](https://hack64.net/docs/pj64d/apidoc.php)): `mem.*`, `events.onexec/ondraw`, `Socket`/`Server` classes | Full (`mem.ptr` raw buffer) | JS `Socket` (TCP) | Scripts dir autorun; debugger build | Active | Yes | Possible 3rd N64 option, dialect B in JS — new language, only worth it if BizHawk N64 perf complaints persist |
| **simple64 / RMG / Gopher64** | N64 | None (simple64 **archived**, successor Gopher64; RMG = plain mupen64plus GUI) | n/a | n/a | n/a | mixed | Yes | **Not viable** |
| **DuckStation** | PSX | **No Lua.** AP worlds that use it ship dedicated clients reading **process memory** (e.g. Spyro 3's `S3AP.exe`, Interpreter execution mode required) | external only | external only | `-fullscreen` etc. | Active, but relicensed 2024 → CC-BY-NC-ND, **packaging/derivatives prohibited** ([gamingonlinux](https://www.gamingonlinux.com/2024/09/playstation-1-emulator-duckstation-changes-license-for-no-commercial-use-and-no-derivatives/)) | Yes | Dialect D; only for worlds whose own client mandates it. Never re-host the binary |
| **melonDS (standalone)** | NDS | Lua = unmerged [PR #1671](https://github.com/melonDS-emu/melonDS/pull/1671) | n/a today | n/a | n/a | Active emulator, no scripting | Yes | **Not viable today — use BizHawk's melonDS core** (official AP path for DS worlds) |
| **Dolphin** | GC, Wii | No Lua in mainline. AP worlds use the **dolphin-memory-engine** approach: external client attaches to Dolphin's emulated-RAM region ([TWW setup](https://archipelago.gg/tutorial/The%20Wind%20Waker/setup_en)); "Emulated Memory Size Override" must be OFF | external (process memory) | external | CLI `-e <iso>`, config INIs | Active | Yes | Dialect D, effort L — C# port of DME reader |
| **PCSX2 ≥1.7 (rec 2.2.0)** | PS2 | No Lua needed — **PINE IPC** (enable in Advanced settings, slot 28011) ([APRac2 setup](https://github.com/evilwb/APRac2/blob/main/docs/setup_en.md)) | PINE read/write | local socket (PINE) | CLI ROM arg; portable config | Active | Yes | Dialect C |
| **Azahar** (Citra/Lime3DS successor) | 3DS | "Enable RPC Server" debug option; mods via `load/mods/`; `.cci` only ([ALBW setup](https://github.com/randomsalience/albw-archipelago/blob/main/docs/setup_en.md)) | RPC read/write | UDP/TCP RPC | config-writable | Active (Lime3DS discontinued) | Yes | Dialect C, per-world |
| **RPCS3 / PPSSPP / Cemu** | PS3 / PSP / Wii U | No common scripting; per-world exotic glue (RaCMAN for R&C1-PS3; PPSSPP debugger WS for the PSP title — unverified; Cemu `settings.xml` + graphic packs for XCX ([apworld](https://github.com/MaragonMH/Archipelago/releases))) | varies | varies | varies | Active | Yes | **Exotic tier** — wrap the world's own client, don't build a bridge |
| **PICO-8 titles** | PICO-8 | n/a — AP PICO-8 games ship a **browser client** (game + AP client in one page, URL params `?Hostname=&Port=&Name=` ([Air Delivery](https://github.com/qwint/ap-air-delivery/releases), hosted at qwint.github.io/air_delivery) | n/a | n/a | open URL | Active | any browser | **Web pattern** — launcher just opens the URL with prefilled params |

---

## 3. Per-game matrix (all 138 emulated catalog entries)

Legend — **Official connector** = what the world's own docs mandate:
- `BHC` = AP BizHawk Client family (`connector_bizhawk_generic.lua` or a world-specific BizHawk lua) → our existing bridge replaces it 1:1.
- `SNI` = SNIClient + SNI (emulators: snes9x-rr / snes9x-nwa / BSNES-plus / BizHawk+BSNES core / RetroArch ≥1.10.1).
- `NATIVE` = native PC port with its own AP client (nothing to bridge).
- `EXT` = world-specific external client (process memory / IPC); we orchestrate, not bridge.
- `WEB` = browser-hosted client.
- `?` = not individually verified — check the world's setup doc at integration time (rule: search its docs for "SNI" vs "BizHawk Client").

**Our backend preference order** assumes our launcher-owned game-logic model
(per-game Lua modules), so "BizHawk (done)" is always first where the system
allows it.

### GBA (16) — official path is BizHawk for all
| Game | Official connector | Viable backends (preference) |
|---|---|---|
| pokemon_emerald, pok_mon_emerald, pok_mon_firered_and_leafgreen | BHC (verified — [Emerald setup](https://gist.github.com/Zunawe/406ea7a7ff50db9bf80e4bd040009fed)) | BizHawk (done) → mGBA (dialect B) → MesenCE |
| castlevania_circle_of_the_moon | BHC (official tutorial) | same |
| castlevania_harmony_of_dissonance | BHC ? | same |
| final_fantasy_tactics_advance | BHC ? | same |
| fire_emblem_the_sacred_stones | BHC (verified — [fe8 setup](https://github.com/CT075/fe8-archipelago/blob/main/setup.md)) | same |
| golden_sun_the_lost_age | BHC ? | same |
| kingdom_hearts_chain_of_memories | BHC ? | same |
| mario_luigi_superstar_saga | BHC ? | same |
| mega_man_battle_network_3_blue | BHC (in AP main) | same |
| metroid_fusion, metroid_zero_mission | BHC ? | same |
| the_legend_of_zelda_the_minish_cap | BHC ? | same |
| wario_land_4 | BHC ? | same |
| yu_gi_oh_dungeon_dice_monsters, yu_gi_oh_ultimate_masters_wct_2006 (official tutorial), yu_gi_oh_gx_duel_academy | BHC ? | same |

### GB / GBC (8)
| Game | Official connector | Viable backends |
|---|---|---|
| pok_mon_red_and_blue | BHC (official tutorial) | BizHawk (done) → MesenCE → mGBA |
| pok_mon_crystal | BHC ? | same |
| pok_mon_pinball | BHC ? | same |
| the_legend_of_zelda_links_awakening_dx | **Own client, dual backend already**: RetroArch ≥1.10.3 (UDP network commands, SameBoy core) **or** BizHawk ≥2.8 via `connector_ladx_bizhawk.lua` ([setup](https://archipelago.gg/tutorial/Links%20Awakening%20DX/setup_en)) | BizHawk (done) → RetroArch (dialect C, UDP 55355) |
| the_legend_of_zelda_oracle_of_ages / _seasons | BHC ? | BizHawk → MesenCE → mGBA |
| bomberman_quest, super_mario_land_2_6_golden_coins (discord_only), wario_land_super_mario_land_3 | BHC ? | same |

### SNES (27) — the split system: SNI worlds vs BizHawk worlds
| Game | Official connector | Viable backends |
|---|---|---|
| alttp | **SNI** (verified — [setup](https://archipelago.gg/tutorial/A%20Link%20to%20the%20Past/multiworld_en)) | BizHawk (done — our `alttp.lua` module) → **snes9x-nwa** → snes9x-rr → MesenCE |
| super_metroid | **SNI** (verified — [setup](https://archipelago.gg/tutorial/Super%20Metroid/multiworld_en)) | same (our `super_metroid.lua` exists) |
| smz3 | **SNI** (verified) | same |
| super_mario_world | **SNI** (verified — [setup](https://archipelago.gg/tutorial/Super%20Mario%20World/setup_en)) | same |
| super_mario_world_2_yoshis_island | **SNI** (official tutorial) | same |
| kirbys_dream_land_3 | **SNI** (official tutorial) | same |
| earthbound | **SNI** (verified — [setup](https://archipelago.gg/tutorial/EarthBound/setup_en)) | same |
| final_fantasy_mystic_quest | **SNI** (official tutorial) | same |
| lufia_ii_rise_of_the_sinistrals | **SNI** (official tutorial) | same |
| secret_of_evermore | **SNI** (verified — [setup](https://archipelago.gg/tutorial/Secret%20of%20Evermore/multiworld_en)) | same |
| chrono_trigger | **SNI** (verified — [CT:JoT multiworld](https://www.wiki.ctjot.com/doku.php?id=multiworld); beta apworld) | same |
| super_metroid_map_rando, super_junkoid (SM hacks) | SNI ? | same |
| smw_spicy_mycena_waffles (SMW hack) | SNI ? | same |
| actraiser | ? | BizHawk → snes9x — check world docs |
| donkey_kong_country, donkey_kong_country_2_diddys_kong_quest | ? (note: official *DKC3* is SNI; 1/2 are community worlds) | same |
| final_fantasy_iv, final_fantasy_vi | ? | same |
| kirby_super_star | ? | same |
| mario_is_missing | ? | same |
| mega_man_x, mega_man_x2, mega_man_x3 | ? | same |
| panel_de_pon_tetris_attack | ? | same |
| plok | ? | same |
| soul_blazer | ? | same |
| super_mario_rpg | ? | same |

### NES (11)
| Game | Official connector | Viable backends |
|---|---|---|
| final_fantasy | BHC (verified — [FF1 BizHawk PR #4448](https://github.com/ArchipelagoMW/Archipelago/pull/4448)) | BizHawk (done) → MesenCE → FCEUX (later) |
| the_legend_of_zelda, the_legend_of_zelda_ii_the_adventure_of_link | BHC ? (official tutorial for TLoZ) | same |
| mega_man_2, mega_man_3 | BHC (official tutorials) | same |
| dragon_warrior_1, faxanadu (official tutorial), crystalis, spelunker (discord_only) | BHC ? | same |
| adventure (Atari 2600) | BHC (verified — [setup](https://archipelago.gg/tutorial/Adventure/setup_en)) | BizHawk only |

### N64 (17)
| Game | Official connector | Viable backends |
|---|---|---|
| the_legend_of_zelda_ocarina_of_time | BHC-family (verified — **BizHawk 2.10 only**, `connector_oot.lua`; "Project64 not supported" — [setup](https://archipelago.gg/tutorial/Ocarina%20of%20Time/setup_en)) | BizHawk (done) → **offer SoH entry as the fast alternative** (separate world!) → PJ64 (only if built later) |
| the_legend_of_zelda_ocarina_of_time_but_its_just_master_quest_water_temple | BHC (Alchav fork of the OoT world) | BizHawk |
| the_legend_of_zelda_majoras_mask | **NATIVE — MM Recomp** ([RecompRando](https://github.com/RecompRando/MMRecompRando/releases), catalog install_url already points there). Catalog `emulator: bizhawk` is wrong | ConnectsItself pattern |
| super_mario_64 | **NATIVE — sm64ex + SM64AP-Launcher** (verified — [setup](https://archipelago.gg/tutorial/Super%20Mario%2064/setup_en); MSYS compile on Windows). Catalog `bizhawk` is wrong | ConnectsItself/orchestrate pattern |
| banjo_tooie | BHC (verified — BizHawk + lua, `/autostart` in its client; Everdrive HW alt — [setup](https://github.com/jjjj12212/Archipelago-BanjoTooie/blob/main/worlds/banjo_tooie/docs/setup_en.md)) | BizHawk (done) |
| donkey_kong_64, diddy_kong_racing, kirby_64_the_crystal_shards, paper_mario, starfox_64, mario_kart_64, castlevania (CV64), castlevania_legacy_of_darkness, shadowgate_64, gauntlet_legends, bomberman_64, bomberman_64_the_second_attack, bomberman_hero (discord_only) | BHC ? (CV64 has an official tutorial) | BizHawk (done) → PJ64 (later) |

### Genesis / SMS (6)
| Game | Official connector | Viable backends |
|---|---|---|
| landstalker_the_treasures_of_king_nole | own client ? (official tutorial) | BizHawk (done) → RetroArch |
| sonic_the_hedgehog (blue_sphere too), streets_of_rage, toejam_earl | BHC ? | BizHawk |
| zillion | own client ? (official tutorial — verify RetroArch vs BizHawk requirement) | BizHawk / RetroArch |

### PSX (12) — split between BizHawk worlds and DuckStation-client worlds
| Game | Official connector | Viable backends |
|---|---|---|
| castlevania_symphony_of_the_night | **BHC** (verified — `.apsotn` + `connector_bizhawk_generic.lua` — [setup](https://www.hexa.media/tutorial/Symphony%20of%20the%20Night/setup_en)). Catalog `duckstation` is wrong | BizHawk (done) |
| spyro_2, spyro_3 | **EXT — DuckStation + world's own client** (Spyro 3 verified: `S3AP.exe`, Interpreter mode — [releases](https://github.com/Uroogla/S3AP/releases)) | DuckStation (orchestrate world client) |
| ape_escape, armored_core, brave_fencer_musashi, digimon_world, medievil_1998, the_grinch, yu_gi_oh_forbidden_memories | EXT/BHC ? — check each world's docs; both patterns exist on PSX | per world: DuckStation client or BizHawk Nymashock |

### PS2 (6)
| Game | Official connector | Viable backends |
|---|---|---|
| sly_2_band_of_thieves, sly_cooper_and_the_thievius_raccoonus, ratchet_clank_going_commando, ratchet_clank_3_up_your_arsenal, ape_escape_3 | **EXT — PCSX2 ≥1.7 + PINE (slot 28011)** (verified — [APRac2](https://github.com/evilwb/APRac2/blob/main/docs/setup_en.md), [AE3](https://github.com/aidanii24/ae3-archipelago/blob/main/worlds/ape_escape_3/docs/setup.md)) | PCSX2 (dialect C — C# PINE client, or orchestrate world client) |
| jak_and_daxter_the_precursor_legacy | **NATIVE — OpenGOAL + ArchipelaGOAL mod**, in AP main since 0.6.2 (verified — [setup](https://archipelago.gg/tutorial/Jak%20and%20Daxter:%20The%20Precursor%20Legacy/setup_en)). Catalog `pcsx2` is wrong | ConnectsItself/orchestrate |

### GC / Wii (17)
luigis_mansion, metroid_prime, the_legend_of_zelda_the_wind_waker,
the_legend_of_zelda_twilight_princess, the_legend_of_zelda_skyward_sword,
paper_mario_the_thousand_year_door, pikmin_2, kirby_air_ride,
mario_kart_double_dash, mario_super_sluggers, pok_park_wii_pikachus_adventure,
super_mario_sunshine (+arcade_2), sonic_riders, shadow_the_hedgehog,
scooby_doo_night_of_100_frights, spongebob_squarepants_battle_for_bikini_bottom
→ all **EXT — Dolphin + world clients built on dolphin-memory-engine**
(verified pattern for [TWW](https://archipelago.gg/tutorial/The%20Wind%20Waker/setup_en)
and [Metroid Prime](https://github.com/Electro1512/MetroidAPrime/blob/main/docs/setup_en.md);
"Emulated Memory Size Override" must be disabled). Backend: Dolphin, dialect D.

### DS (10)
castlevania_dawn_of_sorrow, final_fantasy_tactics_a2_grimoire_of_the_rift,
pok_mon_black_and_white, pok_mon_platinum, pok_mon_mystery_dungeon_explorers_of_sky,
sonic_rush, the_legend_of_zelda_phantom_hourglass, the_legend_of_zelda_spirit_tracks,
voltorb_flip_hgss → **BHC with BizHawk's melonDS core** (verified for
[Pokemon Platinum](https://github.com/ljtpetersen/platinum_archipelago/blob/master/docs/setup_en.md);
standalone melonDS has no scripting — [PR #1671](https://github.com/melonDS-emu/melonDS/pull/1671)
unmerged). **Catalog `emulator: melonds` should read `bizhawk` for all of these.**
Backend: BizHawk (done — add "NDS" to RomSystem coverage).

### 3DS (1)
the_legend_of_zelda_a_link_between_worlds → **EXT — Azahar** ("Enable RPC
Server", mods in `load/mods/`, `.cci` not `.3ds` — [setup](https://github.com/randomsalience/albw-archipelago/blob/main/docs/setup_en.md)).
Catalog `lime3ds` is stale (Lime3DS merged into Azahar). Dialect C, per-world.

### Exotic singles (4)
| Game | Reality |
|---|---|
| ratchet_clank (PS3) | bordplate's randomizer: homebrew PS3 + WebMAN + **RaCMAN**, PAL digital copy; "no plans to support emulator", RPCS3 reported workable ([repo](https://github.com/bordplate/rac1-randomizer)) — keep `discord_only`-style honesty |
| k_on_after_school_live (PSP) | apworld not found in public registries during this research — verify source before listing as available; PPSSPP has no Lua, its debugger WebSocket API is the plausible channel |
| xenoblade_chronicles_x (Wii U) | [MaragonMH apworld](https://github.com/MaragonMH/Archipelago/releases) — client edits Cemu `settings.xml` + graphic packs; orchestrate-only |
| air_delivery, metrocubevania, the_forged_curse (PICO-8) | **WEB** — browser client w/ URL params, e.g. `https://qwint.github.io/air_delivery/?Hostname=…&Port=…&Name=…` ([releases](https://github.com/qwint/ap-air-delivery/releases)). Launcher = open URL; no emulator install at all |

### SM64 / MM / OoT-SoH native-port trio (also catalogued)
the_legend_of_zelda_ocarina_of_time_ship_of_harkinian is already
`plugin_type: external` — correct. super_mario_64 and majoras_mask should
follow the same pattern (see §4 for the shape).

---

## 4. Ship of Harkinian — integration plan

**What it is.** Native PC port of OoT (HarbourMasters "Shipwright"); the AP
integration lives in a **separate SoH build** from
[HarbourMasters/Archipelago-SoH](https://github.com/HarbourMasters/Archipelago-SoH)
(latest: *SoH Archipelago 1.2.1*, 2026-02-19, apworld-only patch; 1.0.0 carried
Windows/Linux/Mac/Steam Deck builds).

**How it connects (verified via setup/community docs):**
- The game has a **built-in AP client**: pick *Archipelago* on the quest-select
  screen; the ESC menu exposes a server console, connection settings and a
  Death Link toggle. The save file is bound to the slot and **auto-reconnects
  when loaded** — exactly our OpenTTD-fork `ConnectsItself` pattern.
- Server side needs **Archipelago 0.6.7+** and the **`oot_soh.apworld`** from the
  same release page.
- Game data: SoH generates an **OTR archive from the user's own OoT ROM** on
  first launch (same bring-your-own-ROM stance as our §11 ROM-library policy).

**Launcher integration shape (new `SoHPlugin`, not an EmulatorPlugin):**
1. *Install*: GitHub latest-release API on `HarbourMasters/Archipelago-SoH`,
   pick the Windows zip asset, extract to `Games/SoH-AP/` (reuse the BizHawk
   installer code path). Keep the user's vanilla SoH untouched — the AP build
   is intentionally separate.
2. *ROM step*: browse for the OoT ROM → copy into `Games/ROMs/<GameId>/` per
   §11 → place/point it for SoH's OTR generator on first run (SoH prompts by
   itself; we just pre-stage the file next to `soh.exe`).
3. *Session*: write the AP host/port/slot the user picked in our UI into the
   game's config before launch so the in-game client connects with zero typing
   (config = `shipofharkinian.json` next to `soh.exe`; verify exact CVar names
   for the AP fork at build time — upstream stores all CVars there, e.g.
   `gfxbackend`, and supports drag-drop config import). If the CVars turn out
   write-hostile, fall back to "launch + show the values to type once" — the
   save remembers them afterwards.
4. *Monitor*: `ConnectsItself` semantics — launcher tracks process lifetime,
   no pipe servers, no Lua. Status comes from the AP server side (our ApClient
   already sees the slot connect).
5. *Catalog*: the `…_ship_of_harkinian` entry stays a **separate game** (it has
   its own apworld + world name) rather than an "Emulator" dropdown option on
   the BizHawk OoT entry — different item/location pools mean different YAML.
   In the OoT (N64) game page, cross-link it: "Prefer native PC? Play the Ship
   of Harkinian world."

**Profile settings:** fullscreen/volume/controller all live in
`shipofharkinian.json` (CVar store; macOS path documented, Windows = next to
exe — [SoH FAQ/PCGW](https://www.pcgamingwiki.com/wiki/The_Legend_of_Zelda:_Ocarina_of_Time_(Ship_of_Harkinian))).
Same verified-keys-only rule: dump a real file from a local install, snapshot
the keys we intend to write, unit-test the merge.

---

## 5. Emulator profile configs (§14 settings) — reference

Rule of thumb everywhere: **read-modify-write the emulator's own file while the
emulator is NOT running** (all four apps rewrite their config on exit, so a
write during play is lost), and only touch keys we've verified against a file
generated by that exact emulator version.

### BizHawk (`Emulators/BizHawk/config.ini` — JSON despite the name)
- CLI: `--config=<path>` (point BizHawk at a launcher-owned profile copy),
  `--fullscreen`, `--lua=`, plus `--load-state`, `--chromeless`,
  `--socket-ip/--socket-port`, `--userdata` — full list in
  [ArgParser.cs](https://github.com/TASEmulators/BizHawk/blob/master/src/BizHawk.Client.Common/ArgParser.cs);
  `--config` history in [issue #1404](https://github.com/TASEmulators/BizHawk/issues/1404).
- Controller bindings: per-system virtual-pad maps under the trollers section
  (`AllTrollers` / analog variants) — bind host buttons to e.g. `"P1 A"`.
  Volume/sound and start-fullscreen are top-level config properties.
  **Action:** generate a config.ini with 2.10 locally and snapshot exact key
  names before writing any of them (JSON shape shifts between majors; safest
  is deserialising with BizHawk's own key names captured in a fixture test).
- Safest pattern: keep a per-user `Profiles/BizHawk/config.ini`, merge our
  managed keys into it, pass `--config=` — never mutate the emulator's default
  file, so a launcher bug can't brick a user's hand-tuned setup.

### snes9x (`snes9x.conf`, key=value with `Section:Key` names, `#` comments)
- Sample real-world files: [RASnes9x snes9x.conf](https://github.com/RetroAchievements/RASuite/blob/master/RASnes9x/win32/snes9x.conf);
  writer source: [win32/wconfig.cpp](https://github.com/snes9xgit/snes9x/blob/master/win32/wconfig.cpp).
- Known keys (verify against our shipped build's generated file):
  `Fullscreen:EmulateFullscreen`, `Window:Width`/`Window:Height`,
  `Stretch:Enabled`, `Stretch:MaintainAspectRatio`, sound section
  (driver/buffer/mute), joypad sections (`Controls\Win` bindings, plus
  `UseJoypad1`-style toggles). CLI `-fullscreen` exists.
- snes9x-nwa is snes9x 1.62.3-based, same conf family; NWA itself is toggled
  via the netplay menu / config ("Enable Emulator Network Access") — write
  that key so the bridge is on before first launch.

### mGBA (`config.ini`, classic INI)
- Portable mode: create `portable.ini` next to the exe → config lives in the
  emulator folder (perfect for launcher-owned installs).
- `[ports.qt]`: `volume`, `mute`, `fullscreen`, `width`, `height`,
  `lockAspectRatio`, `pauseOnFocusLost`, `fpsTarget`, … ;
  `[input.QT_K]` (keyboard): `keyA`, `keyB`, `keyL`, `keyR`, `keyStart`,
  `keySelect`, `keyUp/Down/Left/Right` (gamepad sections analogous).
- CLI: `-f` (fullscreen) and `-C option=value` per-run config overrides
  ([man page](https://man.archlinux.org/man/extra/mgba-qt/mgba-qt.6.en)) —
  `-C` is ideal for §12-style launch toggles without touching the file.
  **No stable `--script` flag** — the open question for the whole backend.

### Ship of Harkinian (`shipofharkinian.json`)
- Single JSON CVar store next to `soh.exe` (Windows); holds graphics backend
  (`gfxbackend`), window/fullscreen state, audio volumes, controller bindings,
  and all enhancement CVars. Community tooling edits it offline
  ([example tool](https://gamebanana.com/tools/20860)).
- Write while the game is closed; SoH also supports drag-dropping a json onto
  the window to import (manual fallback).

---

## 6. Recommendation

### 6.1 Backend abstraction for `EmulatorPlugin`
Today `EmulatorPlugin` hardcodes BizHawk (install URL, `EmuHawk.exe`, `--lua=`,
pipe pair). Extract an emulator-backend seam; keep `IGamePlugin` untouched:

```csharp
interface IEmulatorBackend
{
    string BackendId        { get; }   // "bizhawk", "snes9x", "mgba", ...
    string DisplayName      { get; }   // dropdown label
    string[] Systems        { get; }   // which RomSystem values it can host
    BridgeDialect Dialect   { get; }   // LuaCrtPipes | LuaTcpSocket | TcpProtocol | ProcessMemory | None(native)
    bool   IsInstalled      { get; }
    Task   InstallAsync(IProgress<(int,string)> p, CancellationToken ct);  // GitHub latest-release pattern (reuse InstallBizHawkAsync)
    ProcessStartInfo BuildLaunch(string romPath, BridgeSession session, EmulatorProfile profile);
    void   WriteProfile(EmulatorProfile profile);   // §14 config-write recipe, per §5 above
}
```

- `BridgeSession` carries pipe base-name **or** TCP port + the per-game module
  name; for `LuaTcpSocket`/`TcpProtocol` dialects the launcher adds one TCP
  listener implementing the same newline-framed CHECK/GOAL/SYNC protocol the
  pipes speak today (protocol unchanged — only the transport differs), so all
  per-game Lua modules and the launcher-side item queue are reused as-is.
- Per-game backend choice: catalog gains `"emulators": ["bizhawk", "snes9x"]`
  (ordered) and the game-settings panel renders the §14 dropdown from the
  intersection of that list with backends whose `Dialect` is implemented —
  others render disabled with "coming soon", exactly per spec.
- `TcpProtocol` (NWA/PINE) needs game logic on the launcher side. Cheapest
  route that avoids rewriting the existing modules: embed a C# Lua interpreter
  (MoonSharp/NLua), expose `memory.read_u8/write_u8`-shaped shims backed by the
  TCP protocol, and run the very same `Plugins/Scripts/games/*.lua` modules
  in-launcher. One logic source, three transports.

### 6.2 Build order (effort: S < M < L)
| Priority | Backend | Why | Effort |
|---|---|---|---|
| 1 | **Ship of Harkinian (native, `ConnectsItself`)** | Whole-game win for the catalog's flagship N64 title, zero bridge code, the AP build + apworld are shipped and current (1.2.1) | **S** |
| 2 | **snes9x (snes9x-nwa via NWA TCP; snes9x-rr Lua-pipe as fallback)** | The literal §14 Discord request; unlocks the SNES cohort (27 games incl. ALttP/SM/SMZ3 where our modules already exist); modern maintained fork; no in-emulator script needed | **M** |
| 3 | **mGBA (Lua TCP socket dialect)** | Lightweight GBA/GB alternative; gate on solving script autoload (stable lacks `--script` — test dev/0.11 or the scripts-autorun dir before committing) | **M** |
| 4 | **MesenCE (Lua TCP socket dialect)** | One backend = NES+SNES+GB+GBA coverage; requires enabling its io/os + socket script permissions via config; young fork (Mesen2 archived 2026-06) | **M** |
| 5 | **PCSX2 (PINE)** / **Dolphin (process memory)** | Opens PS2 + GC/Wii cohorts, but each world's official client already exists — consider orchestrating those clients (install+launch them) before building our own dialect | **M / L** |
| — | Project64 (JS), DuckStation, Azahar, RPCS3/PPSSPP/Cemu | Niche or per-world exotic; wrap the world's own tooling, honest "coming soon"/external labels in the dropdown | L |

### 6.3 Catalog corrections to land with §14 (honest-availability prerequisite)
1. `super_mario_64` → native sm64ex/SM64AP-Launcher flow (external-style entry, not `emulator: bizhawk`).
2. `jak_and_daxter_the_precursor_legacy` → OpenGOAL/ArchipelaGOAL (native), not `pcsx2`.
3. `the_legend_of_zelda_majoras_mask` → MM Recomp native flow (install_url already correct).
4. All 9 `emulator: melonds` rows → `bizhawk` (melonDS core inside BizHawk; standalone melonDS unscriptable).
5. `the_legend_of_zelda_a_link_between_worlds` → `azahar` (Lime3DS discontinued).
6. `castlevania_symphony_of_the_night` → `bizhawk` (apworld is BizHawk-generic).
7. PICO-8 rows → web-client launch (no emulator install).
8. `ratchet_clank` (PS3) → mark exotic/external; emulator path unsupported upstream.

### 6.4 Risks / open verifications before implementation
- snes9x-rr `io.open("\\\\.\\pipe\\…")` is *expected* to behave like BizHawk
  (same Win32 CRT) but must be proven in-emulator — same gate discipline as
  the original pipe bridge.
- mGBA standard-`io` availability and script autoload: unverified/undocumented;
  the TCP dialect assumption needs a 1-hour spike on 0.10.5 and current dev.
- The `?`-flagged worlds in §3 (mostly custom SNES apworlds + N64/Genesis
  community worlds): one-time docs check each at integration time — the
  SNI-vs-BizHawk answer changes which RAM map our module reads (SNI exposes
  the FX Pak address space; BizHawk lua reads system-bus/WRAM domains).
- DuckStation: download only from official releases at user action; no
  re-hosting/repacking (CC-BY-NC-ND, packaging prohibited).

---

## 7. Source index
- BizHawk CLI: https://github.com/TASEmulators/BizHawk/blob/master/src/BizHawk.Client.Common/ArgParser.cs ; --config request: https://github.com/TASEmulators/BizHawk/issues/1404 ; README (cores: melonDS NDS, Ares64 N64, Nymashock PSX): https://github.com/TASEmulators/BizHawk
- SNI: https://github.com/alttpo/sni (lua bridge on 127.0.0.1:65398; snes9x-rr/BizHawk/RetroArch/FX Pak; v0.0.103 2026-06)
- snes9x-rr Lua: https://github.com/TASEmulators/snes9x-rr/wiki/Lua-Functions (archived repo)
- snes9x-nwa: https://github.com/Skarsnik/snes9x-emunwa ; NWA protocol: https://github.com/usb2snes/emulator-networkaccess
- mGBA scripting: https://mgba.io/docs/scripting.html ; dev: https://mgba.io/docs/dev/scripting.html ; CLI man page: https://man.archlinux.org/man/extra/mgba-qt/mgba-qt.6.en ; announcement: https://mgba.io/2022/05/29/scripting/
- Mesen2 API: https://www.mesen.ca/docs/apireference.html ; archive/continuation: https://github.com/SourMesen/Mesen2 → https://github.com/nesdev-org/MesenCE
- Project64 JS API: https://hack64.net/docs/pj64d/apidoc.php
- melonDS Lua PR: https://github.com/melonDS-emu/melonDS/pull/1671
- DuckStation license change: https://www.gamingonlinux.com/2024/09/playstation-1-emulator-duckstation-changes-license-for-no-commercial-use-and-no-derivatives/
- simple64 archived / RMG: https://github.com/simple64/simple64 , https://github.com/Rosalie241/RMG
- AP setup guides (connector mandates): OoT https://archipelago.gg/tutorial/Ocarina%20of%20Time/setup_en ; SM64 https://archipelago.gg/tutorial/Super%20Mario%2064/setup_en ; ALttP https://archipelago.gg/tutorial/A%20Link%20to%20the%20Past/multiworld_en ; Super Metroid https://archipelago.gg/tutorial/Super%20Metroid/multiworld_en ; EarthBound https://archipelago.gg/tutorial/EarthBound/setup_en ; SoE https://archipelago.gg/tutorial/Secret%20of%20Evermore/multiworld_en ; SMW https://archipelago.gg/tutorial/Super%20Mario%20World/setup_en ; LADX https://archipelago.gg/tutorial/Links%20Awakening%20DX/setup_en ; Adventure https://archipelago.gg/tutorial/Adventure/setup_en ; TWW https://archipelago.gg/tutorial/The%20Wind%20Waker/setup_en ; tutorial index https://archipelago.gg/tutorial/
- World repos: Banjo-Tooie https://github.com/jjjj12212/Archipelago-BanjoTooie ; Spyro 3 https://github.com/Uroogla/S3AP ; SotN guide https://www.hexa.media/tutorial/Symphony%20of%20the%20Night/setup_en ; RaC2 https://github.com/evilwb/APRac2 ; AE3 https://github.com/aidanii24/ae3-archipelago ; Metroid Prime https://github.com/Electro1512/MetroidAPrime ; Pokemon Platinum https://github.com/ljtpetersen/platinum_archipelago ; ALBW https://github.com/randomsalience/albw-archipelago ; CT:JoT https://www.wiki.ctjot.com/doku.php?id=multiworld ; RaC1 PS3 https://github.com/bordplate/rac1-randomizer ; XCX https://github.com/MaragonMH/Archipelago/releases ; PICO-8 Air Delivery https://github.com/qwint/ap-air-delivery ; FF1 BizHawk PR https://github.com/ArchipelagoMW/Archipelago/pull/4448 ; Jak https://archipelago.gg/tutorial/Jak%20and%20Daxter:%20The%20Precursor%20Legacy/setup_en
- SoH: https://github.com/HarbourMasters/Archipelago-SoH (release 1.2.1, 2026-02-19) ; https://www.shipofharkinian.com/setup-guide ; config tooling https://gamebanana.com/tools/20860 ; PCGW page https://www.pcgamingwiki.com/wiki/The_Legend_of_Zelda:_Ocarina_of_Time_(Ship_of_Harkinian)
- snes9x conf samples: https://github.com/RetroAchievements/RASuite/blob/master/RASnes9x/win32/snes9x.conf ; writer https://github.com/snes9xgit/snes9x/blob/master/win32/wconfig.cpp
