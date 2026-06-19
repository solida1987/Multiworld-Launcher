# Pokémon Emerald — AP bridge ground truth (2026-06-12)

Ground truth extracted from the official Archipelago 0.6.6 install at
`C:\ProgramData\Archipelago` (read-only). The apworld ships **compiled
.pyc only** (CPython 3.13, magic `F3 0D 0D 0A`); all logic below was
recovered by disassembling the .pyc files with the system Python 3.13
(`%LOCALAPPDATA%\Programs\Python\Python313\python.exe`, same bytecode
magic) and by reading the apworld's plain-JSON data files.

Extraction working set (regenerable, lives in `%TEMP%`):
- `%TEMP%\emerald_world\` ← unzipped `C:\ProgramData\Archipelago\lib\worlds\pokemon_emerald.apworld`
- `%TEMP%\bizhawk_world\` ← copied `C:\ProgramData\Archipelago\lib\worlds\_bizhawk\`
- `%TEMP%\emerald_world\dis_*.txt` ← `dis` dumps of client.pyc / rom.pyc / data.pyc /
  `_bizhawk\__init__.pyc` / `lib\worlds\Files.pyc`

## 1. The .apemerald patch container

Newest patch for the live server seed 81769672980259429628:
`C:\ProgramData\Archipelago\output\AP_81769672980259429628\AP_81769672980259429628_P1_test.apemerald`

It is a ZIP (APProcedurePatch, `worlds/Files.py:285`) containing exactly:

| file | size | role |
|---|---|---|
| `archipelago.json` | 330 B | manifest |
| `base_patch.bsdiff4` | 243,175 B | vanilla→AP base bsdiff4 |
| `token_data.bin` | 79,384 B | per-seed token writes |

Manifest (verbatim):
```json
{"compatible_version": 6, "version": 7, "server": "", "player": 1,
 "player_name": "test", "game": "Pokemon Emerald",
 "patch_file_ending": ".apemerald",
 "procedure": [["apply_bsdiff4", ["base_patch.bsdiff4"]],
               ["apply_tokens", ["token_data.bin"]]],
 "base_checksum": "605b89b67018abcea91e693a4dd25be3",
 "result_file_ending": ".gba"}
```
`base_checksum` = **MD5 of the vanilla Pokémon Emerald (U) ROM**. The
container is fully self-contained — base_patch.bsdiff4 is inside the zip,
so patching needs nothing from the apworld at runtime.

### apply_bsdiff4 (`worlds/Files.py:475`, dis_files.txt:2780)
`rom = bsdiff4.patch(rom, patch_bytes)` — standard BSDIFF40 format
(bzip2-compressed control/diff/extra blocks). The AP install bundles the
C extension as `C:\ProgramData\Archipelago\lib\bsdiff4.core.cp313-win_amd64.pyd`
(python package itself inside `lib\library.zip`); a .pyd cannot be imported
from a zip path directly, so it must be pre-registered in `sys.modules`
via `importlib.util.spec_from_file_location("bsdiff4.core", <pyd>)` before
`import bsdiff4`. Pure-python fallback implemented with stdlib `bz2`.

### apply_tokens (`worlds/Files.py:480`, dis_files.txt:2793–2980)
`token_data.bin` layout (all little-endian):
```
u32 token_count
repeat token_count times:
  u8  token_type
  u32 offset          (into the ROM)
  u32 size            (byte length of data that follows)
  u8[size] data
```
Token types (`worlds/Files.py:382`, dis_files.txt:2379–2395):
`WRITE=0, COPY=1, RLE=2, AND_8=3, OR_8=4, XOR_8=5`.
- AND_8/OR_8/XOR_8: `rom[offset] op= data[0]`
- COPY: `length=u32(data[0:4])`, `src=u32(data[4:8])` → `rom[offset:offset+length] = rom[src:src+length]`
- RLE:  `length=u32(data[0:4])`, `value=u32(data[4:8])` → fill `length` bytes of `value`
- WRITE (default): `rom[offset:offset+size] = data`

## 2. Check detection (client.py `game_watcher`, line 201; dis_client.txt:982–2275)

All reads/writes use BizHawk domain **"System Bus"** with full GBA bus
addresses (matches `worlds/_bizhawk` read/write primitives).

RAM symbols (apworld `data/extracted_data.json` → `misc_ram_addresses`,
`_rom_name` = `pokemon emerald version / AP 5`):

| symbol | address |
|---|---|
| gSaveBlock1Ptr | 0x03005D8C |
| gSaveBlock2Ptr | 0x03005D90 |
| gMain | 0x030022C0 |
| CB2_Overworld | 0x080867F0 (ROM code address) |
| gArchipelagoReceivedItem | 0x0203D1E8 |
| gArchipelagoDeathLinkQueued | 0x0203D218 |

Guards used by every guarded read/write (client.py:201ff):
- "IN OVERWORLD": u32 at `gMain+4` (callback2) == `CB2_Overworld + 1`
  (+1 = THUMB bit; dis_client.txt gw lines 76–92).
- "SAVE BLOCK 1"/"2": the pointers re-read and compared (DMA shuffle guard).
  sb1 must stay constant across the dependent read, else discard.

Flag scan (gw lines 160–206):
- `flag_bytes = read(sb1 + 5200, 150) .. read(sb1 + 5350, 150)`
  → SaveBlock1 **flags at offset 0x1450, 300 bytes total** (AP-patched
  layout — NOT vanilla 0x1270). FLAGS_COUNT = 2400 (extracted_data
  constants).
- For each set bit: `flag_id = byte_i*8 + bit`,
  `location_id = flag_id + BASE_OFFSET`; reported **only if
  location_id ∈ ctx.server_locations** (the slot's real location set).
- `BASE_OFFSET = 3860000` (data.py module line 17, dis_data.txt:69).

Dexsanity (gw lines 208–236 + 334–368), only when
`slot_data['dexsanity'] == Toggle.option_true`:
- `pokedex_caught_bytes = read(sb2 + 40, 52)` (SaveBlock2 + 0x28).
- set bit → `dex_number = byte_i*8 + bit + 1`,
  `location_id = dex_number + BASE_OFFSET + POKEDEX_OFFSET`;
  `POKEDEX_OFFSET = 10000` (data.py line 18, dis_data.txt:72).

Goal (client.py module level, dis_client.txt:123–157 + gw lines 12–47):
- `DEFEATED_WALLACE_FLAG = constants['TRAINER_FLAGS_START'] + constants['TRAINER_WALLACE']` = 1280 + 335 = **1615**
- `DEFEATED_STEVEN_FLAG  = 1280 + constants['TRAINER_STEVEN'(804)]` = **2084**
- `DEFEATED_NORMAN_FLAG  = 1280 + constants['TRAINER_NORMAN_1'(269)]` = **1549**
- slot_data['goal']: 0=champion→Wallace flag, 1=steven, 2=norman,
  3=legendary_hunt (goal flag None; counts caught/defeated legendaries —
  NOT implemented in our bridge v1, documented limitation).
- game_clear when the goal flag bit is set during the flag scan; client
  then sends StatusUpdate CLIENT_GOAL.

ROM identity check (client.py `validate_rom`, line 164, dis_client.txt:747–866):
- read 32 bytes at ROM offset **0x108** (264), domain "ROM", strip NULs →
  must equal `EXPECTED_ROM_NAME = 'pokemon emerald version / AP 5'`
  (client.py line 23). Exactly `'pokemon emerald version'` = unpatched
  vanilla ROM (items cannot be delivered). Client sets
  `watcher_timeout = 0.125 s` (line 190) — our poll cadence mirrors it.

## 3. Item delivery (client.py `handle_received_items`, line 495; dis_client.txt:2746–2947)

State the game exposes:
- `num_received_items` = **u16 at sb1 + 14200 (0x3778)** — how many items
  the game has processed, persisted in the save.
- receive buffer `gArchipelagoReceivedItem` (0x0203D1E8):
  `+0 u16 item_id_game`, `+2 u16 next_index`, `+4 u8 is_filled`,
  `+5 u8 should_display`.

Protocol per iteration (guards: IN OVERWORLD + SAVE BLOCK 1):
1. Read `num_received_items` (sb1+14200, 2 bytes) and `is_filled`
   (gArchipelagoReceivedItem+4, 1 byte).
2. If `num_received_items < len(ctx.items_received)` AND `is_filled == 0`:
   `next_item = ctx.items_received[num_received_items]`, then 4 writes:
   - `+0 ← u16le(next_item.item − BASE_OFFSET)`
   - `+2 ← u16le(num_received_items + 1)`
   - `+4 ← 0x01`
   - `+5 ← should_display` where
     `should_display = 1 if (next_item.flags & 1) or (next_item.player == ctx.slot) else 0`
   The game consumes the buffer (clears +4, bumps its save counter), and
   the loop feeds the next item. **Index correctness depends on
   ctx.items_received being the slot's full ordered server item stream.**

items_handling: validate_rom sets **1** (remote-only — the patched game
grants locally-found items itself); game_watcher upgrades to **3** via
ConnectUpdate only when `slot_data['remote_items'] == true`. Our launcher
connects with items_handling=7 globally, so the Lua module must filter the
stream to the same effective list (drop own-world items unless
remote_items; drop server/starting entries `player==0 && location<=0`)
**before** indexing — the game's save counter then matches the filtered
sequence, which is content-identical to what the official client delivers.

## 4. _bizhawk framework notes (`lib\worlds\_bizhawk\__init__.pyc`)

`bizhawk.read/write(ctx, [(address, size_or_bytes, domain)])`,
`guarded_read/guarded_write(ctx, ..., guards)` where each guard is
`(address, expected_bytes, domain)` — a guard mismatch aborts the batch.
Domains used by the emerald client: "System Bus" (RAM) and "ROM"
(validate_rom only). Our Lua equivalent: `memory.read_u8/u16_le/u32_le`
and `memory.write_*` against "System Bus", `memory.read*("ROM")` for the
identity check, with the SAVE BLOCK guard emulated by re-reading
gSaveBlock1Ptr after each dependent batch and discarding on change.

## 5. Bridge design decisions (launcher V2)

- **Patch apply**: `Plugins\Scripts\apply_appatch.py`, executed by the
  system Python 3 (discovered via `py -3` / `python` / install-dir probe).
  bsdiff4 via the AP install's own `bsdiff4.core` .pyd when present (path
  passed in), else a pure-python bz2 fallback. Validates `base_checksum`
  MD5 before patching, writes `<rom>_<patchstem>.gba` next to the library
  ROM, never touches the original.
- **Item indexing**: `EmulatorPlugin` now keeps the session's full ordered
  item list (`ReceiveItemsAsync` honors the AP `index` parameter) and each
  launch replays it from index 0 through SYNC as extended lines
  `ITEM:<id>|<index>|<player>|<flags>|<locationId>` (old plain `ITEM:<id>`
  still parsed). The Emerald module rebuilds its filtered delivery list
  from these and runs the §3 handshake against the save counter, so
  resuming a save mid-multiworld delivers exactly the missing items.
- **Location set**: ap_config.json now carries the slot's
  `locations` (checked+missing from the Connected packet), `slot_number`
  and raw `slot_data` — the module mirrors the client's
  `ctx.server_locations` filter without needing any apworld files on the
  player machine.

## 6. Verification record (2026-06-12)

Patch apply (proven, 3 ways byte-identical, md5 `c538d166735cc228ec275e55018ee92a`):
pip bsdiff4, the AP-bundled `bsdiff4.core` .pyd preload, and the
pure-python fallback (`py -3 -S`, 1.3 s for the 16 MB ROM). Output ROM name
at 0x108 = `pokemon emerald version / AP 5` exactly.

Mock harness (`lupa` real-Lua runtime + simulated GBA memory): 16/16
assertions — flag scan → location ids, dedup, goal flag, the 4-write
receive handshake (incl. resumed save at count=1 delivering list[2]),
own-item filtering with remote_items on/off, vanilla-ROM inertness, legacy
ITEM lines.

Live (launcher → BizHawk → patched ROM → localhost:38281 slot `test`):
- `.apemerald` auto-detected and applied inside the real launch flow
  (launcher log: "AP patch applied … (bsdiff4: pip, md5 c538d166…)"),
  cache reuse on second launch ("Using existing AP-patched ROM").
- Connector attached attempt #1; module logged
  `AP ROM verified: 'pokemon emerald version / AP 5'`,
  `location map: 194 flag checks` (= the server's 194 missing locations),
  `ready: slot #1, goal=0 (flag 1615), remote_items=false, dexsanity=false`.
- A throwaway AP client committed location 3860094 (NOTE: AP servers
  silently ignore LocationChecks from TextOnly/Tracker-tagged clients);
  server answered `ReceivedItems index 0 → (3860030, 3860094, 1, 0)`.
- Relaunch: launcher replayed the stream — bridge_trace
  `SYNC #1 -> 1 item(s)`, connector `ITEM received: 3860030 (index 0)`,
  module stored it and (correctly) filtered it from delivery
  (own-world item, remote_items off — exactly items_handling=1 semantics).
- NOT yet exercised live: an in-overworld save (no save file existed), so
  the flag scan against real save data and an actual receive-buffer write
  remain mock/source-proven only. ~10k polls idled cleanly at the title
  screen (guards held, zero stray writes).
