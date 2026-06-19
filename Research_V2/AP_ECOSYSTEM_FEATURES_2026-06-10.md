# Archipelago Ecosystem Feature Research — Launcher V2.0.0
_Research date: 2026-06-10 · Current upstream Archipelago release: **0.6.7** (2026-04-01)_

Scope: protocol features and player-facing tools the V2 launcher should support, checked against
what `Core/ApClient.cs` / `Core/ApProtocol.cs` already implement and what `TODO_V2.md` already marks done.
All packet shapes below were verified against the official `docs/network protocol.md`, `NetUtils.py`,
`CommonClient.py`, and `worlds/factorio/Client.py` on the `main` branch (see Verification notes at the end).

---

## Executive summary

The V2 `ApClient` is already a competent mainline client: it speaks RoomInfo → Connect → Connected,
receives items with index tracking, sends LocationChecks, scouts with `create_as_hint`, parses
PrintJSON hints into structured `HintEntry` objects, and tracks hint points via RoomUpdate. That puts
it ahead of most game-specific clients on day one.

What it is **missing** falls into four buckets:

1. **The social/co-op layer** — DeathLink (`Bounce`/`Bounced` + `ConnectUpdate` tag toggling) is the
   single most-requested multiworld feature and is absent. EnergyLink (data storage) is the second.
2. **The data storage API** (`Get` / `Set` / `SetNotify` / `Retrieved` / `SetReply`) — unused. This is
   the only way to fetch the **hint backlog** (`_read_hints_{team}_{slot}`): today hints created before
   the launcher connected, or via the webhost tracker, are invisible until the server happens to
   re-broadcast them. It also unlocks live hint-status sync, other players' goal status, and race-mode
   detection.
3. **PrintJSON subtype routing** — only `type == "Hint"` is special-cased. `ItemSend`, `Countdown`,
   `Goal`, `Release`, `Collect`, `Join`/`Part`, `Chat` vs `ServerChat`, and `TagsChanged` all collapse
   into one undifferentiated text log, which blocks the "who sent what" feed, countdown overlay, goal
   celebrations, and trap warnings that players expect from modern clients.
4. **Correctness fixes** — three real bugs found while auditing against the spec:
   - `ForfeitAsync` sends `!forfeit`, a command **removed from AP servers years ago** (renamed
     `!release`). The Forfeit button (MainWindow.xaml.cs:3696) produces "Unknown command" on every
     modern server. Remove the button or repoint it.
   - `ApClient.HintCost` doc comment says "checked-location units" — `hint_cost` is actually a
     **percentage of total location count**. Real point price = `total_locations * hint_cost / 100`
     (points earned per check = `location_check_points` from RoomInfo). The status-bar hint counter is
     misleading without this conversion.
   - The `LocationScoutsAsync` comment has `create_as_hint` 1/2 swapped: **1 = create + broadcast
     (re-announcing already-known hints), 2 = broadcast only NEW hints** (still returned in the
     LocationInfo reply either way).
   - Minor: on Connected the client immediately sends `StatusUpdate` with `PLAYING` (20). Correct
     lifecycle is `CONNECTED(5)` while in the launcher, `READY(10)` on a Ready button, `PLAYING(20)`
     when the game process launches, `GOAL(30)` on win (already wired at MainWindow.xaml.cs:3814).

Ecosystem context: **MultiworldGG** (multiworld.gg, a 2024 fork with extra community worlds) speaks the
identical protocol, so everything below works on both networks. **PopTracker** packs and **Universal
Tracker** connect as `Tracker`-tagged clients; the launcher does not need to replace them — a deep link
("open this room in the webhost tracker") plus our own icon-grid view (already 🔴 in TODO) covers 90%
of players. Discord bridges (ArchipelaBot, Bridgeipelago, archipelago-check-notifier) exist server-side;
no launcher work needed beyond optionally posting to a user-supplied webhook URL.

---

## Protocol features table

Legend: effort S = hours, M = 1–2 days, L = multi-day. Priority P0 = next release, P1 = soon, P2 = nice, P3 = future.

| Feature | AP packet / mechanism | What players get | Already in ApClient.cs? | Effort | Priority |
|---|---|---|---|---|---|
| **DeathLink** | `Connect`/`ConnectUpdate` with tag `"DeathLink"`; `Bounce`/`Bounced` with `data: {time, source, cause}` | Shared deaths with the multiworld — most-requested co-op feature; D2 hardcore synergy | ❌ No Bounce/Bounced handling at all | M | **P0** |
| **Hint backlog fetch** | `Get` on key `_read_hints_{team}_{slot}` → `Retrieved`; `SetNotify` for live updates → `SetReply` | Hint panel shows ALL existing hints on connect, not just ones broadcast while connected; auto-updates `found` state | ❌ Hints only via live PrintJSON; `_hintKeys` dedup exists but no backlog | M | **P0** |
| **ClientStatus lifecycle** | `StatusUpdate` with `status` 0/5/10/20/30 | Correct presence on webhost tracker + `!status`; Ready-check flow before session start | ⚠️ Packet exists; sends `Playing` at connect time instead of `Connected`; no READY UI | S | **P0** |
| **Remove `!forfeit`** | (dead chat command) | No more "Unknown command" embarrassment | ❌ `ForfeitAsync` sends removed command | S | **P0** |
| **Hint cost math fix** | `RoomInfo.hint_cost` (= % of total locations), `location_check_points` | Status bar can show "Hints: 2 available (350/500 pts)" accurately | ⚠️ Value stored, semantics wrong | S | **P0** |
| **PrintJSON subtype routing** | `PrintJSON.type` ∈ ItemSend, ItemCheat, Hint, Join, Part, Chat, ServerChat, Tutorial, TagsChanged, CommandResult, AdminCommandResult, Goal, Release, Collect, Countdown | Color-coded chat; separate "events" feed; goal/release banners; join/leave notices | ⚠️ Only `Hint` is special-cased; rest flattened to text | M | **P1** |
| **Countdown** | Send `Say {"text":"!countdown 10"}`; receive `PrintJSON {type:"Countdown", countdown:int}` | Synchronized race starts with a big on-screen 3-2-1 overlay | ❌ | S | **P1** |
| **Hint priority flags** | `UpdateHint {player, location, status}`; HintStatus 0/10/20/30/40 | Mark own hints Priority/Avoid/No-priority so finders know what to chase (mirrors webhost tracker UI) | ❌ | S–M | **P1** |
| **"Who sent what" feed** | `PrintJSON type:"ItemSend"` — `receiving: int`, `item: NetworkItem` (`item.player` = finder) | Filterable feed: "items I sent" / "items sent to me", trap warnings at send time | ❌ (Items tab only covers items **received by my slot**) | M | **P1** |
| **Sound notifications** | local — hook existing `ItemsReceived` (flags), Bounced, PrintJSON Goal | Audio cue on progression item / trap / death / goal; standard in game clients | ❌ | S | **P1** |
| **Sync + auto-reconnect** | `Sync` (no args) + resend `LocationChecks` with full local check list | Seamless recovery from server restarts/network blips; no relaunch needed (TODO notes reconnect is V2.1) | ❌ Header comment explicitly defers it | M | **P1** |
| **DataPackage checksum cache** | `RoomInfo.datapackage_checksums: dict[game, checksum]`; request only stale games via `GetDataPackage {games:[...]}`; cache to disk | Connect time drops from seconds to instant on big multiworlds; protocol-recommended behaviour | ❌ Always requests fresh | S–M | **P1** |
| **Chat command autocomplete** | client-side UI over `Say` | Discoverability of `!hint`, `!missing`, `!remaining`, etc.; item-name autocomplete from DataPackage | ❌ Plain TextBox (MainWindow.xaml.cs:4295) | M | **P1** |
| **Release/Collect permission gating** | `RoomInfo.permissions {release, collect, remaining}`; Permission enum 0/1/2/6/7 | Buttons disabled (with tooltip why) when the room forbids them or requires goal first | ❌ Buttons always enabled when connected | S | **P1** |
| **EnergyLink widget** | Data storage key `EnergyLink{team}`; `SetNotify` + `Set` with `add`/`max` ops | Live shared-energy meter; deposit/withdraw for supporting games (Factorio, Satisfactory, SM…) | ❌ | M | **P2** |
| **Item links / group slots** | `Connected.slot_info: dict[slot, NetworkSlot]`, `type: 0b10 = group`, `group_members: int[]` | Correct sender/receiver names when items route through link groups instead of "Unknown player" | ❌ Only `players` array parsed | S–M | **P2** |
| **Alias updates** | `RoomUpdate.players` (full player list resent on rename); `!alias` command | Player renames reflected live in trackers/feeds | ❌ RoomUpdate only reads hint_points + checked_locations | S | **P2** |
| **Race mode awareness** | `Get` key `_read_race_mode` → int 0/1 | Hide spoiler-ish UI (scouted item names) in race rooms | ❌ | S | **P2** |
| **PrintJSON color rendering** | `JSONMessagePart {type:"color", color:"red"\|"green"\|…\|"red_bg"…}`; `player_id`/`item_id` parts already carry `flags` | AP-standard colored chat (progression purple/gold, players cyan, etc.) | ⚠️ Parts flattened to plain text | M | **P2** |
| **TagsChanged / Join tags display** | `PrintJSON type:"TagsChanged"` + `Join.tags` | See who has DeathLink on, who is a tracker, etc. | ❌ | S | **P2** |
| **InvalidPacket handling** | `InvalidPacket {type:"cmd"\|"arguments", original_cmd, text}` | Debug visibility instead of silent drops | ❌ | S | **P2** |
| **Bounce passthrough for plugins** | generic `Bounce`/`Bounced` event on `IGamePlugin` | Future game features (e.g. TrapLink, RingLink community tags) without ApClient changes | ❌ | S | **P2** |
| **PopTracker-style icon grid** | local UI + per-game pack assets (manifest.json + JSON/Lua packs) | Visual at-a-glance tracker like PopTracker packs | ❌ (already 🔴 in TODO_V2) | L | **P3** |
| **Webhost tracker deep link** | URL `https://archipelago.gg/tracker/{tracker_id}` (from room page; not in protocol) | One-click open of the official web tracker for the room | ❌ | S | P3 |
| **Discord webhook relay** | local HTTP POST of selected events to user-supplied webhook URL (what Bridgeipelago/check-notifier do) | Async-game notifications in the group's Discord without hosting a bot | ❌ | M | P3 |
| **Universal Tracker integration** | external tool (FarisTheAncient's UT apworld simulates gen logic for in-logic checks) | Out of scope to replicate — logic simulation needs the Python apworld; link/docs instead | ❌ | — | P3 |

---

## Chat command UX table

All `!commands` are server-side: the launcher just sends them through the existing
`SendSayAsync` (`{"cmd":"Say","text":"!…"}`). Recommendation column says how to surface each in the chat input.

| Command | Syntax | What it does | Recommended UI treatment |
|---|---|---|---|
| `!hint` | `!hint` | List your hints + points + cost | **Button** ("My Hints") + autocomplete |
| `!hint <item>` | `!hint Horadric Cube` | Hint where one of your items is | **Autocomplete** — after `!hint ` suggest item names from our game's DataPackage (`item_name_to_id` keys) |
| `!hint_location <loc>` | `!hint_location Den of Evil` | Reveal what an own location contains | **Autocomplete** with location names; per-location 🔍 button already exists (keep both) |
| `!release` | `!release` | Send all items in your world out | ✅ Button exists — add confirm dialog + gate on `permissions.release` |
| `!collect` | `!collect` | Pull all your items from other worlds | ✅ Button exists — gate on `permissions.collect` (usually goal-gated = 2) |
| `!forfeit` | — | **Removed from AP** (renamed `!release` pre-0.4) | **Delete the button** (`BtnForfeit`, MainWindow.xaml.cs:3696) |
| `!remaining` | `!remaining` | List items you have not yet received (no locations revealed) | **Button** in Progression tab; gate on `permissions.remaining` |
| `!missing` | `!missing` | Server-side list of your unchecked locations | Autocomplete only (Progression tab already shows this better) |
| `!checked` | `!checked` | Server-side list of your checked locations | Autocomplete only |
| `!countdown <s>` | `!countdown 10` | Broadcast countdown to all players | **Button** ("⏱ Countdown") with seconds spinner, default 10 |
| `!status [tag]` | `!status` / `!status DeathLink` | Connection + completion summary per slot | **Button** in Players panel |
| `!players` | `!players` | Who is connected/disconnected | Autocomplete (Players panel covers it) |
| `!alias <name>` | `!alias Marco` | Set/reset your display alias | Autocomplete; optional Settings field that auto-sends on connect |
| `!options` | `!options` | Show server options (includes password!) | Autocomplete only — do not make a prominent button |
| `!admin <cmd>` | `!admin /send …` | Run a server-console command remotely (if enabled) | Autocomplete with sub-suggestions (`/send`, `/send_location`, `/collect`, `/release`, `/option`) behind an "advanced" toggle |
| `!getitem <item>` | `!getitem <item>` | Cheat an item to yourself (`/option server_password`-gated cheats) | Autocomplete only, marked CHEAT in the suggestion |
| `!help` / `!license` | — | Command listing / licensing | Autocomplete only |

Local `/commands` from CommonClient (`/received`, `/missing`, `/items`, `/item_groups`, `/locations`,
`/location_groups`, `/ready`, `/connect`, `/disconnect`) are **client-side conventions**, not server
commands. The launcher already has UI equivalents for most; the one worth copying is **`/ready`** →
implement as the Ready toggle (Quick Win 3). If the user types a leading `/` in our chat box, show a
hint that these are TextClient-local commands rather than forwarding them (forwarded `/...` text would
just be chat).

Autocomplete implementation note: a `Popup` + `ListBox` driven from `TxtChat` text changes; suggest
commands when the text starts with `!`, and after `!hint ` / `!hint_location ` switch the source list
to DataPackage item/location names for `ConnectedGame`. The DataPackage dictionaries are already parsed
for the trackers, so this is pure UI work.

---

## Top 10 Quick Wins

All implementable in C#/WPF against the existing `ApClient` send/dispatch plumbing. JSON shapes are
copy-ready: AP messages are always sent as a **JSON array** of command objects (the existing
`SendJsonAsync(new object[]{...})` already does this).

### 1. DeathLink — toggle + death feed (P0)

Opt in at connect by adding the tag (or toggle later without reconnecting):

```json
[{"cmd": "Connect", "game": "Diablo II", "name": "...", "password": "...",
  "uuid": "...", "version": {"major": 0, "minor": 5, "build": 0, "class": "Version"},
  "items_handling": 7, "tags": ["AP", "DeathLink"], "slot_data": true}]
```
```json
[{"cmd": "ConnectUpdate", "tags": ["AP", "DeathLink"]}]
```

Send a death (note: `time` is a Unix epoch **float**; `source` = our slot/alias name; `cause` optional
but should contain the player name):

```json
[{"cmd": "Bounce", "tags": ["DeathLink"],
  "data": {"time": 1780000000.0, "source": "MarcoD2",
           "cause": "MarcoD2 was slain by Diablo."}}]
```

Receive: handle a new `"Bounced"` case in `DispatchAsync`:

```json
{"cmd": "Bounced", "tags": ["DeathLink"],
 "data": {"time": 1780000000.0, "source": "OtherPlayer", "cause": "..."}}
```

Echo guard (CommonClient behaviour): store the `time` of the last death we **sent**; ignore any
`Bounced` whose `data.time` equals it. UI: checkbox "💀 DeathLink" on the AP connection card +
red entries in the Play-tab log ("OtherPlayer died: cause"); D2Plugin forwards deaths over the existing
named pipe (`DEATH` message) and sends a Bounce when the DLL reports the player died.

### 2. Hint backlog via data storage (P0)

Right after `Connected` (we know `team`/`slot` then):

```json
[{"cmd": "Get", "keys": ["_read_hints_0_3"]},
 {"cmd": "SetNotify", "keys": ["_read_hints_0_3"]}]
```

Replies to handle (two new dispatch cases):

```json
{"cmd": "Retrieved", "keys": {"_read_hints_0_3": [
   {"receiving_player": 3, "finding_player": 2, "location": 9000, "item": 7000,
    "found": false, "entrance": "", "item_flags": 1, "status": 30}]}}
```
```json
{"cmd": "SetReply", "key": "_read_hints_0_3", "value": [ /* full updated Hint list */ ]}
```

Hint object fields (verbatim from `NetUtils.py`): `receiving_player`, `finding_player`, `location`,
`item`, `found`, `entrance` (default `""`), `item_flags` (default 0), `status` (default 0). Server-encoded
objects may carry an extra `"class": "Hint"` discriminator — ignore unknown fields. Map into the existing
`HintEntry` (names resolved via DataPackage + `Players`); the existing `_hintKeys` dedup keeps PrintJSON
double-delivery harmless. `SetReply` also flips `found` live → auto-strike-through hinted locations.

### 3. ClientStatus lifecycle + Ready button (P0)

```json
[{"cmd": "StatusUpdate", "status": 5}]
```

Exact values (NetUtils.py): `CLIENT_UNKNOWN = 0`, `CLIENT_CONNECTED = 5`, `CLIENT_READY = 10`,
`CLIENT_PLAYING = 20`, `CLIENT_GOAL = 30`. Change `HandleConnectedAsync` to send **5** instead of 20;
send **20** from `LaunchAsync` when the game process actually starts; add a "✋ Ready" toggle button next
to Release/Collect that flips between 10 and 5. Goal (30) is already wired. Optional richness: `Get` on
`_read_client_status_{team}_{slot}` per player to show a ready-check lobby in the Players panel.

### 4. Kill `!forfeit`, gate Release/Collect on permissions (P0)

Delete `ForfeitAsync` + `BtnForfeit`. Parse `permissions` from RoomInfo (and RoomUpdate — it can change):

```json
{"cmd": "RoomInfo", "permissions": {"release": 6, "collect": 2, "remaining": 1}, ...}
```

Permission enum: `disabled = 0`, `enabled = 1`, `goal = 2` (allowed only after goal completion),
`auto = 6` (forced on goal, manual not allowed), `auto_enabled = 7` (auto on goal + manual allowed).
Button logic: enable Release when `(release & 1) != 0`, or when `release == 2` and our status is GOAL;
tooltip explains why it is disabled. Same for Collect/`!remaining`.

### 5. Hint cost percentage fix (P0)

From RoomInfo: `hint_cost` = **percentage of total location count**; `location_check_points` = points
earned per check. Total location count = `ConnectedChecked.Count + ConnectedMissing.Count` (already
stored). Display:

```
pointCost  = max(1, totalLocations * hintCost / 100)
available  = HintPoints / pointCost
statusBar  = $"Hints: {available} available ({HintPoints}/{pointCost} pts)"
```

Also fix the `LocationScoutsAsync` comment: `create_as_hint`: 0 = scout silently; 1 = create + broadcast
hints (re-announces known ones); 2 = broadcast only **new** hints (all still appear in the LocationInfo reply).

### 6. Countdown button + overlay (P1)

Send (server command via chat): `[{"cmd": "Say", "text": "!countdown 10"}]`

Receive — one PrintJSON per second:

```json
{"cmd": "PrintJSON", "type": "Countdown", "countdown": 10,
 "data": [{"text": "[Server]: Starting countdown of 10"}]}
```

`countdown` counts down to `0` ("GO"). UI: route `type == "Countdown"` in `HandlePrintJson` to a
full-window translucent overlay with a huge number (reuse the AchievementToast overlay pattern);
play a tick sound per second and a "go" sound at 0.

### 7. ItemSend feed + trap warnings (P1)

Extend `HandlePrintJson` to surface ItemSend packets (today they are plain log lines):

```json
{"cmd": "PrintJSON", "type": "ItemSend", "receiving": 3,
 "item": {"item": 7000, "location": 9000, "player": 2, "flags": 4},
 "data": [ /* JSONMessagePart[] for display */ ]}
```

Semantics: `receiving` = receiver **slot**; `item.player` = the slot whose world contained it (the
finder/sender); `item.flags`: `0b001` progression, `0b010` useful, `0b100` trap, `0` filler. Fire a new
`ItemSendReceived(receiving, item)` event. UI: "Events" feed filtered to `receiving == Slot ||
item.player == Slot`; when `receiving == Slot && (flags & 4) != 0` show an orange "⚠ Trap incoming"
toast. Also route `type: "Goal"`, `"Release"`, `"Collect"` (fields `team`, `slot`) to banner lines
("PlayerX completed their goal! 🎉") and trigger the existing achievement/celebration system when
`slot == Slot`.

### 8. Sound notifications (P1)

No packets — pure hooks on existing events: `ItemsReceived` (progression chime when `flags & 1`,
trap sting when `flags & 4`), DeathLink `Bounced` (death sound), PrintJSON Goal (fanfare), Countdown
ticks. Implementation: `System.Media.SoundPlayer` with bundled `.wav` files in `Assets/Sounds/`
(or `SystemSounds.*` as zero-asset fallback); master toggle + per-event checkboxes in the Settings tab
LAUNCHER section, persisted in `SettingsStore`. CommonClient has no sounds — this is a differentiator
players notice immediately.

### 9. Sync + auto-reconnect (P1)

On resume after a dropped socket (and on any `ReceivedItems.index` mismatch — currently the plugin
handles dedup, but a gap means missed items):

```json
[{"cmd": "Sync"},
 {"cmd": "LocationChecks", "locations": [9001, 9002, 9003]}]
```

`Sync` makes the server resend `ReceivedItems` starting at `index: 0`; the resent `LocationChecks`
must contain the **full** local checked list (server ignores duplicates). Reconnect loop: in
`ReceiveLoopAsync`'s `finally`, if the drop was not user-initiated, retry `ConnectAsync` with
exponential backoff (2/4/8/15/30 s, cap 30 s) and a status-bar "AP: Reconnecting (attempt N)…" state.
The D2 plugin's persisted item-resume index already makes redelivery safe.

### 10. DataPackage checksum cache (P1)

RoomInfo already carries it (the field is parsed-but-unused today):

```json
{"cmd": "RoomInfo", "datapackage_checksums": {"Diablo II": "abc123…", "Clique": "def456…"}, ...}
```

Cache each game's DataPackage JSON to
`%LOCALAPPDATA%\ApLauncherV2\datapackage\{game}\{checksum}.json`. On connect, compare checksums and
request only stale/missing games:

```json
[{"cmd": "GetDataPackage", "games": ["Diablo II"]}]
```

Feed cached packages straight into the existing `DataPackageReceived` event so trackers light up
instantly even before the socket finishes the exchange. This is explicitly recommended by the protocol doc.

---

### Honourable mentions (just outside the top 10)

- **UpdateHint priority buttons** on each hint card (we are `receiving_player` for "IsForMe" hints):
  `[{"cmd": "UpdateHint", "player": 2, "location": 9000, "status": 30}]` —
  HintStatus: `HINT_UNSPECIFIED = 0`, `HINT_NO_PRIORITY = 10`, `HINT_AVOID = 20`, `HINT_PRIORITY = 30`,
  `HINT_FOUND = 40` (server-set only; cannot be set or changed away from via UpdateHint). Trap-item
  hints default to AVOID; `!hint` results default to PRIORITY. Pairs naturally with Quick Win 2.
- **EnergyLink meter** (key `EnergyLink{team}`, e.g. `"EnergyLink0"`):
  subscribe `[{"cmd": "SetNotify", "keys": ["EnergyLink0"]}]`;
  deposit `[{"cmd": "Set", "key": "EnergyLink0", "default": 0, "operations": [{"operation": "add", "value": 1000}]}]`;
  withdraw `[{"cmd": "Set", "key": "EnergyLink0", "default": 0, "operations": [{"operation": "add", "value": -1000}, {"operation": "max", "value": 0}]}]`
  — compute what was actually drained from `SetReply.original_value` vs `value`. Values can be very
  large (Factorio joules) — parse as `double`/`decimal`, not `int`.
- **Group slots / item links**: parse `Connected.slot_info` (`dict[int → {name, game, type, group_members}]`,
  `type`: 0 spectator / 1 player / 2 group) so feeds say "(ItemLink) Progressive Sword" instead of an
  unknown slot number.
- **`RoomUpdate.players`** re-parse for alias renames (full list is resent; never includes
  `missing_locations`).

---

## Verification notes

Primary sources (all read 2026-06-10, AP `main` branch at release 0.6.7):

- **Network protocol spec** — packet/field names for Bounce/Bounced, DeathLink data (`time`, `cause`,
  `source`), ConnectUpdate, Sync, Get/Set/SetNotify/Retrieved/SetReply + operation names, `_read_*` keys,
  UpdateHint, PrintJSON types & JSONMessagePart, RoomUpdate field inventory, items_handling, NetworkItem
  flags, NetworkSlot/slot_info, Permission enum, `hint_cost`/`location_check_points`, `create_as_hint`,
  datapackage_checksums guidance, Connect tags (`AP`, `DeathLink`, `Tracker`, `TextOnly`, `HintGame`,
  `NoText`; Tracker/TextOnly/HintGame require `items_handling: 0` and skip game/version validation):
  https://github.com/ArchipelagoMW/Archipelago/blob/main/docs/network%20protocol.md
- **Enum ground truth** (ClientStatus 0/5/10/20/30; HintStatus 0/10/20/30/40; SlotType 0/1/2; Hint
  NamedTuple field order/defaults; JSONTypes):
  https://github.com/ArchipelagoMW/Archipelago/blob/main/NetUtils.py
- **Reference client behaviour** (DeathLink tag toggle sends `{"cmd":"ConnectUpdate","tags":[...]}` with
  tags only; Bounce death shape; `/ready` sends CLIENT_READY/CLIENT_CONNECTED; Sync + full LocationChecks
  resend on index mismatch; SetReply keys `startswith("EnergyLink")`; no sound support upstream):
  https://github.com/ArchipelagoMW/Archipelago/blob/main/CommonClient.py
- **EnergyLink key + operations** (`f"EnergyLink{self.team}"` for generator ≥ 0.4.2; deposit via `add`;
  drain via `add` negative then `max 0`; `SetNotify` subscription):
  https://github.com/ArchipelagoMW/Archipelago/blob/main/worlds/factorio/Client.py and
  https://github.com/ArchipelagoMW/Archipelago/pull/2034
- **Server/client command reference** (`!hint`, `!hint_location`, `!release`, `!collect`, `!remaining`,
  `!missing`, `!checked`, `!countdown`, `!alias`, `!options`, `!admin`, `!players`, `!status`, `!getitem`,
  host `/` commands, CommonClient local `/` commands):
  https://archipelago.gg/tutorial/Archipelago/commands/en
- **Release cadence** (0.6.7 current, 2026-04-01): https://github.com/ArchipelagoMW/Archipelago/releases/tag/0.6.7
- **PopTracker pack format** (manifest.json + scripts/init.lua + JSON data; AP autotracking via
  Tracker-tag connection): https://github.com/black-sliver/PopTracker/blob/master/doc/PACKS.md
- **Universal Tracker** (UT apworld by Faris/qwint — simulates generation logic from yaml+apworld to
  compute in-logic checks; not replicable in C# without the Python world):
  https://github.com/FarisTheAncient/Archipelago/releases
- **CheeseTrackers** async room dashboard (slot claiming, BK/go-mode, hint classification, Discord ping
  prefs): https://github.com/cdhowie/cheese-trackers
- **Discord bridges** — ArchipelaBot: https://github.com/LegendaryLinux/ArchipelaBot ·
  Bridgeipelago: https://github.com/Quasky/bridgeipelago ·
  check-notifier: https://github.com/matthe815s-projects/archipelago-check-notifier
- **MultiworldGG fork** (protocol-compatible network with extra worlds):
  https://github.com/MultiworldGG/MultiworldGG
- **Archipelago.MultiClient.Net** — mature C#/.NET AP client library (NuGet, v6.x) worth consulting as a
  reference implementation for edge cases (it is not needed as a dependency; ApClient.cs already covers
  the core): https://www.nuget.org/packages/Archipelago.MultiClient.Net

Caveats: the `data.time` float for DeathLink is compared by exact value for echo suppression — store the
sent value verbatim. Data-storage `Get`/`Set` allow arbitrary extra fields that are echoed back in
`Retrieved`/`SetReply` (useful as request correlation ids). `Bounce` deliveries are filtered by the
union of `games`/`slots`/`tags` targets; a client that itself carries the `DeathLink` tag receives its
own death Bounced back — hence the echo guard.
