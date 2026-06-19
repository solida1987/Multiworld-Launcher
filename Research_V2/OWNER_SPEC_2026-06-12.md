# Owner specification — 2026-06-12 (decoded from voice-dictated feedback)

The product owner's requirements for the next build-out wave, in priority order.
This file is the single source of truth for the wave; agents read THIS, not the
original dictation.

## 1. Platform filter (Browse)
A platform chip group in the Browse filter bar: PC, SNES, N64, PlayStation, GBA,
GameCube, Genesis, Web, ... (derive the set from the catalog data — `Platforms[]`
already exists on every entry). Order: main filter (All/Available/Official/
Unofficial) → **platform** → category. All three compose (AND).

## 2. Honest install-capability semantics
"Available" must mean: **the launcher itself can get you playing.** Recompute
status/labels from what the launcher can actually do (InstallStrategy + plugin):

| Capability | Label shown | Meaning |
|---|---|---|
| Full auto | "Auto-install" | Launcher downloads game + mod, ready to run (D2, OpenTTD) |
| Mod-on-top | "Auto-mod (you own the game)" | User owns/installs the base game, launcher installs + applies the mod |
| ROM | "Bring your own ROM" | Launcher installs emulator + mod, user supplies the ROM |
| Manual | "Manual setup required" | Launcher cannot automate it (yet) — can still be added to the library; show install guide/links |

- Games whose integration is untested/unfinished are NOT "available" no matter
  what (the emulated trio stays gated until check detection lands).
- "Coming soon" as a label is reserved for things we are actively going to
  automate; everything else uses "Manual setup required" — never a misleading
  coming-soon.
- FREE tag stays and must be correct per game.
- Every capability label needs a short tooltip/explanation — the user should
  never wonder "what happens if I click Install".

## 3. Real playtime, never fake numbers
- The per-run estimate pill (⏱ ~1h) on Browse cards READS as "time played" and
  must go from the cards (it may live in the detail page, clearly labeled
  "Typical run length").
- Anywhere playtime is shown for the user's own games: REAL tracked playtime
  (PlaytimeService). Zero play = "Never played" / 0h — never a placeholder.

## 4. Achievements rework
- REMOVE the achievements category from the per-game Progression sidebar.
- New GLOBAL achievements view: every achievement across all games, grouped by
  game (a general/launcher group + one group per game), with earned state,
  date, and progress counts. Hundreds of definitions: generate per-game ladders
  programmatically (install / first connect / first check / 10/50/100/500/1000
  checks / first goal / playtime tiers 1h/10h/50h / DeathLink events / hint
  usage, etc.) for every game with a plugin + the launcher-general set.
- Inside a specific game's pages, only THAT game's achievements may appear
  (as a small section), never the global list.

## 5. Steam-like Browse cards + detail page
- KILL the "Details →" button. The CARD ITSELF is clickable → opens the detail
  page. Cards show: art, name, platform chip, capability label, FREE tag —
  nothing else (no description walls, no link buttons, no credits).
- The detail page carries EVERYTHING: full description (original game info +
  what the mod does), category, age rating where known, mod author/credits,
  correct links (mod GitHub, AP page, official game site), purchase link
  (Steam/Epic/GOG/official store) when the game is bought — or "Free — released
  <year>" styling when free, install guide, screenshots, capability explanation.
- Add to Library lives on the card (small) AND in the detail page.

## 6. Library game front page ("Overview" tab)
Selecting a game in MY LIBRARY lands on an OVERVIEW tab (new, default), not
Play: hero art background, about text, latest news teaser, REAL playtime +
completion stats, achievements teaser for that game, and top buttons: Play
(jumps to Play tab flow), Buy/Get the game (when not owned/installed → official
store link), website. Play/Progression/Items/News/Settings remain as tabs after
it.

## 7. Per-game install/mod research
For every game we surface as more than "Manual": verify HOW it actually
installs + mods (download URLs, mod loaders like BepInEx, patchers, apworld
files) and encode it in the capability data. Long-term goal: more games move up
the capability ladder. Document findings per game in the catalog data
(install_guide etc.).

## 9. One install surface (added 2026-06-12, round 2)
The game-header corner buttons ("Install Game" + "Install"/Play) are REMOVED
from view — the Overview action row is the only install/play surface. The
header keeps identity + badges only. (The hidden header button may remain in
the tree as the state-machine host that Overview mirrors/re-raises.)

## 10. Capability-labeled action buttons
The Overview primary action adapts to InstallCapability:
- AutoInstall → "Install" (launcher downloads everything)
- AutoMod → "Find original game…" (folder picker to locate the user's existing
  install, validated + registered) and, once located, "Install mod"
- RomRequired → "Install ROM…" (installs emulator/mod, then ROM picker)
- ManualSetup → no install button; show the install guide / project link
After install, the same slot becomes Play/Update+Play/Stop exactly as today.

## 11. Never touch the user's original copy
Everything the launcher installs lives in the launcher's own folders. When the
user points at an original game or supplies a ROM: COPY it into the launcher's
library (e.g. Games/ROMs/<gameId>/), register the copy, and operate ONLY on
the copy. Install/uninstall via the launcher must never modify or delete the
user's original files.

## 12. Per-game launch settings
Every plugin exposes the launch settings the launcher can actually control in
its settings panel (D2: windowed/no-sound — exists; OpenTTD: e.g. fullscreen;
emulated: e.g. fullscreen). Only settings that demonstrably affect launch —
no placebo toggles.

## 13. Canonical link buttons (sidebar bottom row)
Next to Session/Browse: three canonical buttons — Archipelago.gg front page,
the official Discord, the official wiki (https://archipelago.miraheze.org/).
These are THE home of those links: remove the Website/Discord links from the
status bar. All other hardcoded UI links audited for correctness.

## 14. Per-game emulator choice + launcher-managed emulator profiles (added 2026-06-12, Discord request)
- Where a game has more than one viable emulator (e.g. SNES: BizHawk or
  snes9x; OoT: BizHawk/N64 or Ship of Harkinian https://www.shipofharkinian.com/),
  the player chooses per game in that game's settings ("Emulator" dropdown).
- IMPORTANT engineering reality: emulator choice is not just a different exe —
  the check-detection bridge is per-emulator. BizHawk's Lua bridge is DONE;
  each additional backend (snes9x-rr Lua dialect, SoH's native AP integration)
  needs its own connector port. Ship the dropdown with honest availability:
  only backends with a working bridge are selectable; others show
  "coming soon" in the dropdown.
- RESEARCH MANDATE: go through all catalog/emulated games and map which
  emulator backends each can use (BizHawk systems, snes9x, mGBA, SoH, etc.)
  → a per-game emulator matrix in the catalog data.
- LAUNCHER-MANAGED EMULATOR PROFILES: a settings section where the player
  defines their standard setup PER EMULATOR (controller bindings, volume,
  fullscreen, ...) which the launcher writes into that emulator's own config
  (BizHawk config.ini, snes9x .cfg, ...) before EVERY launch — so controls
  and audio are always right without ever opening the emulator's own menus.
  Same verified-keys-only discipline as §12 (no placebo settings).

## 15. Upstream-update tracking (added 2026-06-12 — game integrations must not rot)
Upstream apworlds/mods/forks update on their own schedule. Requirements:
- **Provenance stamps**: every per-game integration records what it was built
  against (apworld datapackage checksum, AP version, fork release tag) — in
  the plugin (constant) and the module header.
- **Runtime mismatch detection**: the AP server announces its per-game
  datapackage checksums in RoomInfo. On connect, compare against the selected
  plugin's built-against stamp; on mismatch show an honest warning ("<game>'s
  world data has been updated upstream — check detection may be incomplete
  until the launcher updates") instead of silently missing checks.
- **Fast regeneration**: the Emerald derivation method (extract apworld →
  read/disassemble client logic → emit module + research note) is the
  documented recipe; an upstream update = one agent run against the new
  apworld, verified by the same live procedure. Keep modules data-driven
  where possible (slot locations/slot_data already arrive via ap_config at
  launch) so many upstream changes need no code at all.
- **Distribution**: module updates ship through the existing launcher
  self-update channel + the hosted catalog updates live from the repo —
  no extra infrastructure.

## 8. Overall bar
Professional, "wow" first impression, minimum friction from install to playing.
When in doubt: fewer words on cards, more depth one click in, honest labels.
