# Master Roadmap — Support EVERY Archipelago game in the launcher

Date: 2026-06-13
Status today: **7 integrated games** (D2 LoD, OpenTTD, Ship of Harkinian, Pokémon
Emerald, A Link to the Past, Super Metroid, Castlevania: Circle of the Moon) +
emulator choice (BizHawk verified, snes9x experimental). Goal: **all of them.**

---

## 0. The core insight

The per-game work is **mostly mechanical and source-derived**. Castlevania proved
the pattern end-to-end:

1. Find the game's AP world (open source).
2. **Generate** the location table from the apworld's own `locations.py` (no
   hand-copying → zero transcription errors).
3. Write the `games/<id>.lua` detection module + **mock-verify it through
   MoonSharp** against synthetic memory.
4. Write a **lean plugin** that reuses the existing `SnesApPatchHelper`
   (`.apXXX` → ROM patch apply) + base-ROM-by-content validation (§11).
5. Register it.

Reaching "all games" = **industrialize this pattern (tooling) + run it in parallel
(agents) + community/owner verification per game.** Brute-force by hand is
infeasible; the machine is the deliverable.

---

## 1. Every game fits one of 5 integration patterns

| # | Pattern | Transport | Examples | Count (≈) | Effort |
|---|---------|-----------|----------|-----------|--------|
| 1 | **BizHawk-Lua** | proven pipe bridge | GBA/GB/NES/SNES/N64/GEN/PSX/NDS generic-BizHawk-client worlds | ~95 of 138 | **S–M** (tooled) |
| 2 | **SNES / SNI** | BizHawk + snes9x, our modules | ALttP, SM, SMW, EarthBound, KDL3, SMZ3… | ~27 | **S–M** |
| 3 | **Native ConnectsItself** | install fork, prefill config, no bridge | SoH ✅, OpenTTD ✅, sm64ex, Jak/OpenGOAL, MM Recomp | ~6 | **S** each |
| 4 | **EXT process-memory** | orchestrate the world's own client / memory reader | Dolphin GC-Wii (~17), PCSX2 PS2 (~6), DuckStation PSX-specific | ~25 | **L** |
| 5 | **WEB** | launcher opens a URL with params | PICO-8 (Air Delivery…) | ~4 | **XS** |

(Full per-game classification already exists in
`EMULATOR_MATRIX_2026-06-12.md` §3 — we reuse it as the backlog source.)

### Effort sub-tiers inside BizHawk-Lua / SNI (the bulk)
- **Tier A** — `items_handling = 0b001` (the patched game grants its own local
  items) + **static** addresses + flag-array detection. Detection ships fast via
  the generator; solo play is complete; multiworld checks flow. Remote-item
  delivery is a later completion pass. *(This is Castlevania, ALttP, Pokémon RB…
  the majority.)*
- **Tier B** — `items_handling = 0b111` (client applies every item) or
  patch-generated addresses. Needs the item-application logic ported per game.

---

## 2. The factory (how we actually scale)

### Phase 0 — Tooling (force multiplier, build once)
- `Tools/apworld_recipe.py`: given an AP world (AP-main checkout or GitHub raw),
  extract a structured **recipe**: system, `ApWorldName`, `items_handling`, base
  ROM size + MD5(s), patch extension, AP signature, memory domains, the
  **location→flag table**, the goal condition.
- `Tools/gen_game.py`: from a recipe + Lua/C# templates, emit
  `games/<id>.lua` + `<Game>Plugin.cs` + the `App.xaml.cs` registration line +
  a MoonSharp mock-test. Human/agent then verifies + tunes the gate/goal.
- Result: each Tier-A game drops from ~12 manual steps to **generate → verify →
  commit**.

### Phase 1 — Backlog
- One tracking sheet: `game → pattern → tier → ROM/patch facts → status →
  owner`. Seeded from the matrix §3. Drives the waves.

### Phase 2 — Parallel waves (the throughput lever)
- **One agent per game (or small batch).** Each agent: run the recipe+generator,
  mock-verify the module (the same 4/4-style MoonSharp check), write/adjust the
  plugin, build, commit. BizHawk-Lua Tier-A first (most games, fastest), ordered
  by popularity.
- Integrate each wave (build 0/0 + smoke), commit per game.

### Phase 3 — Harder patterns
- SNES/SNI remaining → our snes9x + BizHawk modules.
- Native ConnectsItself (sm64ex, Jak, MM Recomp) → SoH/OpenTTD pattern.
- EXT (Dolphin/PCSX2/DuckStation) → orchestrate the world's own client where one
  exists; build a C# process-memory reader only where it doesn't.
- WEB (PICO-8) → trivial URL launch.

### Phase 4 — Completion pass
- Wire the deferred **remote-item delivery** for the detection-first games.
- Flip `ChecksImplemented` / `LiveVerified` as each is confirmed against the real
  ROM (owner/community).

---

## 3. Two honest truths

1. **Verification ceiling.** Each game's final "the real ROM reports a real
   check / receives a real item" needs the actual ROM running — owner's or the
   community's, per game. We ship **mock-verified detection**; live confirmation
   is the per-game gate (exactly like Pokémon Emerald). The launcher labels
   unconfirmed games honestly.
2. **Throughput.** Sequentially (≈1 game/session) 138 games = months. The real
   lever is **agent parallelism** — a fleet porting in parallel (owner previously
   authorized "maksimalt ressourcer"). With N agents, the Tier-A bulk lands in a
   handful of waves; the long tail (EXT) is the slow part.

**Realistic end state:** ~85–90% of the catalog fully playable-in-launcher; the
exotic EXT/PS3/Wii-U tail stays "orchestrate the world's own client / external"
with honest labels. That IS "all games supported," just not all by the same
mechanism.

---

## 4. Immediate next actions
1. Build Phase 0 tooling (recipe extractor + generator).
2. Generate the Tier-A backlog from the matrix + a `items_handling` probe.
3. Launch wave 1 (Tier-A BizHawk-Lua, by popularity) — **parallel agents.**
4. Owner verifies each in-emulator as they land; flip the honest labels.
