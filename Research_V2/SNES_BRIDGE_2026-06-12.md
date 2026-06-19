# SNES AP bridge ground truth — A Link to the Past + Super Metroid (2026-06-12)

Ground truth extracted from the official Archipelago 0.6.7 install at
`C:\ProgramData\Archipelago` (read-only). Both worlds ship **compiled .pyc
only** (CPython 3.13, magic `F3 0D 0D 0A`); all logic below was recovered by
disassembling the .pyc with the system Python 3.13
(`%LOCALAPPDATA%\Programs\Python\Python313\python.exe`, same bytecode magic),
and — for ALttP's location tables — by exec-ing the module code objects in a
stubbed namespace to dump the real dict values.

Extraction working set (regenerable, lives in `%TEMP%`):
- `%TEMP%\alttp_world\` ← copied `C:\ProgramData\Archipelago\lib\worlds\alttp\` (a directory, not an .apworld)
- `%TEMP%\sm_world\sm\` ← unzipped `C:\ProgramData\Archipelago\lib\worlds\sm.apworld`
- `%TEMP%\alttp_client_dis.txt`, `%TEMP%\sm_client_dis.txt` ← `dis` dumps of the two `Client.pyc`
- `%TEMP%\alttp_tables.json` ← the four `location_table_*_id` dicts dumped from `Client.pyc`

## 0. THE BIG DIFFERENCE FROM EMERALD: SNI, not _bizhawk

ALttP and SM are **SNI** games. Their AP clients subclass `SNIClient`
(`worlds/AutoSNIClient.py`) and address memory through SNI's combined memory
model, NOT the GBA `_bizhawk` connector. SNI's address space (the numbers in
the client source):

| SNI symbol | value | meaning |
|---|---|---|
| `ROM_START`  | `0`          | ROM image |
| `WRAM_START` | `16056320` = `0xF50000` | SNES work RAM `$7E0000–$7FFFFF` |
| `WRAM_SIZE`  | `131072` = `0x20000` | 128 KB |
| `SRAM_START` | `14680064` = `0xE00000` | cartridge SRAM (save RAM) |

**Translation to BizHawk Lua memory domains** (this is the whole trick — the
same physical bytes are reachable from BizHawk's SNES core):
- SNI `WRAM_START + off`  → BizHawk domain **`"WRAM"`**, offset `off`
  (equivalently bus address `$7E0000 + off` on `"System Bus"`).
- SNI `SRAM_START + off`  → BizHawk domain **`"CARTRAM"`** (BizHawk's name for
  SNES battery/save RAM; the bsnes/snes9x cores expose it as `CARTRAM`),
  offset `off`. Fallback domain name `"SRAM"` is tried too.
- The ROM header name both clients read lives at `SRAM_START + 0x7FC0` in SNI
  space — i.e. the SNES internal ROM header at `$00:FFC0`. On BizHawk that is
  reachable on the `"System Bus"` at `0x00FFC0`, or `"CARTROM"`+`0x7FC0`. We
  read the **CARTRAM-mirrored AP signature** instead (see each game) because
  the AP basepatch writes a known marker into SRAM that is far more robust than
  guessing the ROM-header mapping across cores.

BizHawk memory API used by both modules (resolved at init, same as Emerald):
`memory.read_u8 / read_u16_le / read_u32_le`, `memory.write_u8 / write_u16_le`,
`memory.read_bytes_as_array`, each taking an optional 2nd-arg domain with a
current-domain fallback for older cores.

## 1. PATCH CONTAINERS — NOT bsdiff4

| game | patch ext | result | base ROM | base ROM size / MD5 |
|---|---|---|---|---|
| ALttP | `.aplttp` (legacy `.apz3`) | `.sfc` | LttP US 1.0 | 1,048,576 B · `03a63945398191337e896e5771f77173` |
| SM    | `.apsm` (legacy `.apm3`)  | `.sfc` | Super Metroid US/JU | 3,145,728 B · `21f3e98df4780ee1c667b84e57d88675` |

`patch_suffix` strings verified in each `*SNIClient` class const:
ALttP `['.aplttp','.apz3']`, SM `['.apsm','.apm3']`.

**Patch class.** ALttP uses `worlds/alttp/Rom.py:LttPProcedurePatch` and SM uses
`worlds/sm/Rom.py:SMProcedurePatch`; both are `APProcedurePatch` subclasses
(`worlds/Files.py`) — the SAME container family as Emerald's `.apemerald`.
A container is a ZIP holding `archipelago.json` (manifest with `base_checksum`,
`procedure`, `patch_file_ending`, `result_file_ending`) plus the data files the
procedure references. The differences from Emerald are only in the **procedure
step list**:
- ALttP's procedure is `apply_bsdiff4` over `base_patch.bsdiff4` then
  `apply_tokens` (same two ops Emerald uses) — so `apply_appatch.py` already
  handles it unchanged.
- SM's procedure applies **IPS** patches (`SMBasepatch_prebuilt/*.ips`,
  `variapatches.ips`) plus `apply_tokens`. IPS is a step `apply_ips` /
  `apply_bsdiff4` is NOT used. The base patches ship INSIDE the apworld, but
  the per-seed container still carries its own token data and references the
  procedure; whichever steps the container's manifest lists are what we run.

`apply_appatch.py` is therefore extended with an **`apply_ips`** step (classic
Patchy/IPS format: 5-byte "PATCH" header, 3-byte offset + 2-byte length
records, RLE records with length 0, "EOF" terminator, optional 3-byte
truncate). Pure-python, stdlib only. The bsdiff4 + tokens paths are untouched.
The script reads the manifest `procedure` and dispatches per step, so it is
fully manifest-driven and version-correct for any APProcedurePatch SNES game.

> NOTE — exact MD5s above are the long-standing community-known hashes for the
> canonical base ROMs every AP SNES generator validates against; the live
> generator embeds the same value in each container's `base_checksum`, and
> `apply_appatch.py` validates the user's ROM against **the container's**
> `base_checksum` before patching (so a wrong constant here can never cause a
> bad patch — at worst the launcher's pre-flight "wrong version" hint is off).

## 2. A LINK TO THE PAST — `worlds/alttp/Client.py`

All addresses below are SNI-space; the module subtracts `WRAM_START`/`SRAM_START`
to get BizHawk WRAM/CARTRAM offsets.

### 2a. Constants (Client.py module top, dis lines 78–187)
```
WRAM_START      = 0xF50000
SRAM_START      = 0xE00000
ROMNAME_START   = SRAM_START + 0x2000        # 0xE02000  → CARTRAM 0x2000
ROMNAME_SIZE    = 21
INGAME_MODES    = {0x07, 0x09, 0x0B}         # {7,9,11}
ENDGAME_MODES   = {0x19, 0x1A}               # {25,26}  (triumph / credits)
DEATH_MODES     = {0x12}                      # {18}
GAME_MODE_ADDR  = WRAM 0x0010                 # $7E0010 main module byte
SAVEDATA_START  = WRAM_START + 0xF000         # 0xF5F000 → WRAM 0xF000 = $7EF000
SAVEDATA_SIZE   = 0x500 (1280)
RECV_PROGRESS_ADDR     = SAVEDATA + 0x4D0 (1232)   # u16: # items game has taken
RECV_ITEM_ADDR         = SAVEDATA + 0x4D2 (1234)   # u8 : item id to grant
RECV_ITEM_PLAYER_ADDR  = SAVEDATA + 0x4D3 (1235)   # u8 : sending player (0=self)
ROOMID_ADDR            = SAVEDATA + 0x4D4 (1236)
ROOMDATA_ADDR          = SAVEDATA + 0x4D6 (1238)
DEATH_LINK_ACTIVE_ADDR = ROMNAME_START + 0x15
```
`game_watcher` watcher cadence 0.25 s (we poll faster; idempotent).

### 2b. ROM identity (`validate_rom`, dis: consts `[…, 2, b'AP', …]`)
Reads **2 bytes at `ROMNAME_START`** (CARTRAM 0x2000) and requires they equal
`b'AP'`. The AP basepatch writes "AP" + the rom-name there. items_handling set
to `1` (remote-only; the patched game self-grants its own found items), upgraded
later by ConnectUpdate. Our module reads the same 2 bytes; if not `AP` it
self-disables (vanilla / unpatched ROM → no checks, no writes).

### 2c. Location detection (`track_locations` → `new_check`, Client.py:334–460)
Four sub-tables, all keyed by **AP location id** (already absolute — no base
offset needed, unlike Emerald). Values dumped to `alttp_tables.json`:

1. **Underworld / dungeon rooms** `location_table_uw_id` — **220 entries**,
   `{ap_id: [room_id, mask16]}`. Read 2 bytes at `SAVEDATA + room_id*2`,
   `roomdata = lo | (hi<<8)`; check `roomdata & mask != 0`.
   (e.g. `60175: [285,16]` = Blind's Hideout - Top.)
2. **Overworld** `location_table_ow_id` — **12 entries**, `{ap_id: screen_id}`.
   Read 1 byte at `SAVEDATA + 0x280 + screen_id`; check `byte & 0x40`.
   (Region base const `640` = 0x280, mask const `64`; e.g. `1573194: 42`.)
3. **NPC / item flags** `location_table_npc_id` — **13 entries**,
   `{ap_id: mask16}`. Read 2 bytes at `SAVEDATA + 0x410`,
   `val = lo | (hi<<8)`; check `val & mask != 0`.
   (Region base const `1040` = 0x410; e.g. `1572883: 4096`.)
4. **Misc** `location_table_misc_id` — **4 entries**, `{ap_id: [addr, mask8]}`
   where `addr` is an **absolute SAVEDATA byte offset** (966=0x3C6, 969=0x3C9).
   Read 1 byte at `SAVEDATA + addr`; check `byte & mask != 0`.
   (e.g. `191256: [969,2]`.)

The official client also writes "collected" bits back into the save buffer for
locations the *server* says are already checked (so the game stops re-offering
them). Our bridge is **read-only for detection** — it never writes save bits —
which is safe: we only ever *report* a newly-set bit upward; we never need to
suppress the game's own item, and not back-writing cannot lose a check (the bit
stays set in the save). Item delivery still writes the receive buffer (§2d).

Only ids that are in the slot's server location set (`cfg.locations`) are
reported, mirroring the client's `ctx.server_locations` gate. The module embeds
all four tables in full, so it needs no apworld file on the player machine.

### 2d. Item delivery (`game_watcher`, dis around RECV_* / consts 1091,1070)
1. Read `RECV_PROGRESS_ADDR` (u16) = how many items the game has already
   consumed (persisted in save).
2. If `recv_index < len(items_received)`:
   - `next = items_received[recv_index]`
   - write `RECV_ITEM_ADDR` (u8) ← `next.item & 0xFF`  *(ALttP item ids are 1 byte)*
   - write `RECV_ITEM_PLAYER_ADDR` (u8) ← sending player index (0 if self/own)
   - the game picks the item up, bumps its own `RECV_PROGRESS_ADDR`. The client
     does NOT bump the counter itself — it only writes item+player and waits for
     the game's progress to advance, then feeds the next. (This is the SNES
     pattern: the ASM hook owns the counter.)
3. Guard: only while game mode ∈ INGAME_MODES.

items_handling is effectively `1` (remote items only — the patched game grants
its own locally-found items). The launcher connects items_handling=7 globally,
so the module filters the stream to the same effective list (drop own-world
items unless remote_items; drop server/start entries) before indexing, exactly
like the Emerald module — keeping the index aligned with the game's counter.

### 2e. Goal
`finished_game` when game mode ∈ `ENDGAME_MODES = {0x19, 0x1A}`.
DeathLink module value `0x12`. (Non-Ganon goals — fast-Ganon, pedestal, etc. —
all still funnel through the same credits modules, so the mode check covers
them; the apworld validates the actual goal server-side.)

## 3. SUPER METROID — `worlds/sm/Client.py`

SM does **NOT** flag-scan. It uses two **ring queues in SRAM** written by the
SMBasepatch ASM. Constants (Client.py module top, dis lines 14–46):
```
WRAM_START   = 0xF50000
SRAM_START   = 0xE00000
SM_ROMNAME_START = SRAM_START + 0x7FC0       # 0xE07FC0 → ROM header / CARTRAM 0x7FC0
ROMNAME_SIZE = 21
SM_INGAME_MODES  = {0x07, 0x09, 0x0B}        # {7,9,11}
SM_ENDGAME_MODES = {0x26, 0x27}              # {38,39}  (Mother Brain dead / ending)
SM_DEATH_MODES   = {19..26}
GAMEMODE_ADDR    = WRAM 0x0998                # $7E0998 game-state byte
SM_SEND_QUEUE_START  = SRAM + 0x2700 (9984)  # game→client: locations found
SM_SEND_QUEUE_RCOUNT = SRAM + 0x2680 (9856)  # u16 read cursor (client owns)
SM_SEND_QUEUE_WCOUNT = SRAM + 0x2682 (9858)  # u16 write cursor (game owns)
SM_RECV_QUEUE_START  = SRAM + 0x2000 (8192)  # client→game: items granted
SM_RECV_QUEUE_WCOUNT = SRAM + 0x2602 (9730)  # u16 write cursor (client owns)
SM_DEATH_LINK_ACTIVE_ADDR = 0x278204 (2588420)
SM_REMOTE_ITEM_FLAG_ADDR  = 0x278206 (2588422)
SM_ROM_MAX_PLAYERID       (clamp for foreign player ids)
```
> NOTE on SEND cursor naming: in the disasm the read-side variable is loaded
> from `SM_SEND_QUEUE_RCOUNT`; the 4 bytes there are `[read_index_u16,
> write_index_u16]` (the client reads BOTH from the RCOUNT address as one
> 4-byte block: `recv_index = lo16`, `recv_item = hi16 = how many the game has
> written`). The module reads 4 bytes at SEND_RCOUNT exactly like the client.

### 3a. ROM identity (`validate_rom`, consts `[…, 2, b'SM', b'1234567890', …]`)
Reads **2 bytes at `SM_ROMNAME_START`** and requires `b'SM'`. (The full name
field continues with the seed digits, validated loosely.) Not `SM` → self-disable.

### 3b. Location detection (SEND queue, game_watcher dis lines 106–129)
```
data = read 4 bytes @ SEND_RCOUNT
recv_index = data[0] | data[1]<<8      -- our read cursor
recv_item  = data[2] | data[3]<<8      -- game's write count
while recv_index < recv_item:
    msg = read 8 bytes @ SEND_QUEUE_START + recv_index*8
    item_index = (msg[4] | msg[5]<<8) >> 3
    location_id = locations_start_id + item_index     -- ← SLOT-RELATIVE
    report location_id
    recv_index += 1
    write SEND_RCOUNT (2 bytes) ← recv_index           -- advance our cursor
```
Each SEND entry is **8 bytes**; the location is encoded in bytes 4–5 as
`value>>3`. The module advances RCOUNT (the client's own cursor), which is the
intended consume mechanism.

### 3c. Item delivery (RECV queue, game_watcher dis lines 131–152)
```
item_out_ptr = read 2 bytes @ RECV_WCOUNT
while item_out_ptr < len(items_received):
    it = items_received[item_out_ptr]
    item_id  = it.item - items_start_id                 -- ← SLOT-RELATIVE
    if (items_handling & 2) and it.location > 0 and own-item:
        location_id = it.location - locations_start_id
    else:
        location_id = 0
    player_id = it.player if it.player <= SM_ROM_MAX_PLAYERID else 0
    write 4 bytes @ RECV_QUEUE_START + item_out_ptr*4:
        [player_id&0xFF, player_id>>8 &0xFF, item_id&0xFF, location_id&0xFF]
    item_out_ptr += 1
    write RECV_WCOUNT (2 bytes) ← item_out_ptr
```
Each RECV entry is **4 bytes**: `(player_lo, player_hi, item_id_lo, loc_lo)`.
The client OWNS the write cursor here (unlike ALttP, where the game owns its
counter) — so the module both writes entries and advances WCOUNT.

### 3d. Goal
`finished_game` when game-state byte (`$7E0998`) ∈ `SM_ENDGAME_MODES = {0x26,0x27}`.

### 3e. THE SLOT-RELATIVE PROBLEM (why SM ships gated)
SM location ids = `locations_start_id + index` and item ids =
`item - items_start_id`. `locations_start_id` / `items_start_id` are the AP
**base ids for THIS slot in THIS room**, known only from the live server's
data package / Connected packet — they are NOT in the apworld as constants and
are NOT currently carried in `ap_config.json`. Without them the module cannot
turn a SEND-queue `index` into the real AP location id, nor map a received AP
item id back to the game's 1-byte item id. The module is written in full and is
structurally correct, but `ADDRESSES_VERIFIED` (the base-id availability gate)
is **false** until the connector passes `locations_start_id`/`items_start_id`
in the config, so SM stays a safe no-op that loads and runs crash-free.
`ChecksImplemented` for SM stays **false** for the same reason. ALttP has no
such problem (its tables are absolute AP ids), so ALttP ships **live**.

## 4. Data-package checksums (BuiltAgainstDataPackageChecksum)
Not embedded in the apworld as a plain constant; the AP server announces each
game's datapackage `checksum` in RoomInfo. Left **null** in both plugins with a
note — set it from a live RoomInfo capture when available. (Mismatch only
drives a soft "apworld updated, regenerate the module" warning, never blocks.)

## 5. Bridge design decisions (launcher V2)
- **Patch apply**: `Plugins\Scripts\apply_appatch.py`, system Python 3, manifest-
  driven. ALttP = existing bsdiff4+tokens path. SM = new `apply_ips` step (+
  tokens). base_checksum MD5 validated before any write; original ROM read-only;
  output `<rom>_<patchstem>.sfc` in the library.
- **Module memory domains**: `"WRAM"` for SAVEDATA/game-mode, `"CARTRAM"`
  (fallback `"SRAM"`) for the SM queues + both ROM-name markers.
- **Location set**: `cfg.locations` mirrors `ctx.server_locations`; item stream
  filtered to the items_handling=1 effective list before indexing (Emerald
  parity).
- **ALttP**: `ChecksImplemented = true`, module live, source-derived (full
  table set verified against Client.pyc).
- **SM**: module complete but `ADDRESSES_VERIFIED=false` + `ChecksImplemented
  =false` pending the base-id handshake; loads/runs as a no-op.

## 6. Verification status (2026-06-12)
- Disassembly: both clients' magic = py313 `f30d0d0a`; all addresses/masks read
  directly from bytecode consts.
- ALttP tables: dumped as real Python dicts via stubbed exec — 220 uw / 12 ow /
  13 npc / 4 misc, byte-for-byte from `Client.pyc` (`lookup_name_to_id` = 249).
- Lua: both modules block/bracket-balanced; load-safe (every guarded read behind
  the verified gate; vanilla/unpatched ROM → inert).
- NOT live-proven: no ALttP/SM base ROM or generated seed on this machine, so
  the real flag-scan / SEND-queue read / receive writes are SOURCE-DERIVED, not
  observed in a running game. A real playthrough is needed to confirm. SM
  additionally needs the base-id handshake before it can light up at all.
