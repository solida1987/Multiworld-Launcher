# OpenTTD × Archipelago — Reality Check & V2.1 Plan
_Research date: 2026-06-10 · Plugin audited: `Plugins/OpenTTD/OpenTTDPlugin.cs` (Launcher V2.0.0)_

Scope: the OpenTTD plugin skeleton contained three load-bearing claims that were never verified —
a repo URL, an integration architecture (GameScript ↔ launcher pipe), and a download URL. All three
were checked against live sources on 2026-06-10. Two of the three were wrong; the third was wrong
twice over (stale version AND broken filename pattern). This note records what actually exists,
which architecture is feasible, and the recommended V2.1 plan. Every claim below carries the URL
it was verified against.

---

## Executive summary

1. **`https://github.com/citymania-org/openttd-archipelago` does not exist** — HTTP 404 on both
   `github.com` and `api.github.com`. The `citymania-org` org is real (CityMania patched client,
   `cmclient`) but has no Archipelago repo. Every reference to this URL in the plugin (links panel
   + news feed) pointed at a 404.
2. **The real integration exists, and it is ours: `https://github.com/solida1987/openttd-archipelago`**
   — "OpenTTD 15.2 with Archipelago multiworld randomizer integration". Same GitHub account that
   hosts Diablo-II-Archipelago. It is a *patched fork with a native in-game AP client* (architecture
   option "b"), released and downloadable: **v1.4.1, published 2026-03-19**, with a standalone
   Windows build (base graphics bundled) and a separate `openttd.apworld` asset.
3. **The GameScript-bridge architecture in the old header comment is impossible.** The GS API
   (176 classes, `https://docs.openttd.org/gs-api/annotated.html`) has **no socket, HTTP, or file-IO
   classes**. The only outbound channels are `GSLog` (log lines) and `GSAdmin` (JSON to admin-port
   clients — server-mode only). A Squirrel Game Script cannot open a TCP or pipe connection to
   anything, by design.
4. **The admin-port approach is feasible but unnecessary.** It only listens on a *server* instance
   (default TCP 3977, `[network] server_admin_port` in `openttd.cfg`; `admin_password` lives in
   `secrets.cfg` since the 14.x secrets split, and password login is additionally gated behind
   `allow_insecure_admin_login=false` by default). Because our fork has a native AP client, the
   launcher never needs to speak the admin protocol for normal play.
5. **The download URL in the plugin was a guaranteed 404.** Verified by ranged GET:
   `…/14.1/openttd-14.1-windows-x64.zip` → **404**. The official CDN pattern is
   `windows-win64`, not `windows-x64` (`…/14.1/openttd-14.1-windows-win64.zip` → 200). Current
   vanilla stable is **15.3** (2026-04-04). Irrelevant for install anyway — the plugin should
   install the fork release, which bundles everything.

OpenTTD is **not** on the official supported-games list at `https://archipelago.gg/games`
(checked 2026-06-10) — the integration is a custom apworld distributed via the fork's releases.

---

## 1. What actually exists

### 1.1 The repo claimed by the old code — 404

| Check | Result |
|---|---|
| `https://github.com/citymania-org/openttd-archipelago` | HTTP **404** |
| `https://api.github.com/repos/citymania-org/openttd-archipelago` | HTTP **404** |
| `citymania-org` org | exists, 11 repos (`cmclient`, `cmbase`, `grf-py`, …) — none AP-related |

GitHub repo search for `openttd archipelago` returns exactly **2** results
(`https://api.github.com/search/repositories?q=openttd+archipelago`); the relevant one is below.

### 1.2 The real project — `solida1987/openttd-archipelago`

Verified via `https://api.github.com/repos/solida1987/openttd-archipelago` (2026-06-10):

- Description: *"OpenTTD 15.2 with Archipelago multiworld randomizer integration"*
- Language C++, created 2026-03-05, last push 2026-03-19, default branch `main`, not a GitHub fork
  (full source import), 4 stars, 5 open issues.
- **Latest release: `v1.4.1` — "OpenTTD Archipelago v1.4.1", published 2026-03-19T09:56:54Z**
  (`https://api.github.com/repos/solida1987/openttd-archipelago/releases/latest`). Assets:

| Asset | Size | Notes |
|---|---|---|
| `openttd-archipelago-v1.4.1-win64.zip` | 70,156,309 B | Standalone Windows build. OpenGFX/OpenSFX/OpenMSX bundled — no vanilla OpenTTD install needed |
| `openttd-archipelago-v1.4.1-linux-amd64.tar.gz` | 93,727,632 B | Linux standalone |
| `openttd.apworld` | 57,730 B | Drop into Archipelago `custom_worlds/` (default `C:\ProgramData\Archipelago\custom_worlds\`) |

Download URL pattern (verified from the release assets API):
`https://github.com/solida1987/openttd-archipelago/releases/download/v{X.Y.Z}/openttd-archipelago-v{X.Y.Z}-win64.zip`

### 1.3 How the integration actually works (from the fork source)

The AP client is **native C++ inside the game binary** — not a Game Script, not an external bridge:

- `src/archipelago.cpp` — WebSocket AP client, TLS via SChannel on Windows / OpenSSL elsewhere
  (`InitializeSecurityContextA`, `SSL_set_tlsext_host_name`), full slot_data parser, item/check
  handling, DeathLink (`Bounce` packets).
- `src/archipelago_gui.cpp` — in-game **Archipelago** main-menu window: Server / Slot / Password
  edit boxes, Connect / Disconnect / NewGRF buttons, persistent top-right status overlay. Default
  server placeholder `archipelago.gg:38281`.
- `src/archipelago_manager.cpp` — game-side systems (missions, ruins, demigods, shop, traps,
  win conditions). Also defines `_ap_bridge_mode` (headless mode: runs without GUI and
  auto-creates Company 0 — used for dedicated-server style operation).
- `src/saveload/archipelago_sl.cpp` — AP state persisted in savegames.

**Connection-settings file (the launcher's prefill hook), verified from
`archipelago_manager.cpp` (`AP_SaveConnectionConfig` / `AP_LoadConnectionConfig`):**

```
<personal_dir>/ap_connection.cfg     # plain key=value, one per line
host=archipelago.gg
port=38281
slot=PlayerName
pass=
ssl=0                                # parsed but currently unused — TLS is auto-detected
```

The in-game GUI **loads this file every time the Archipelago window opens** and saves it on every
successful connect. So anything the launcher writes there pre-fills the in-game dialog.

**Where `<personal_dir>` is** — the fork deliberately redefines it (`src/os/windows/win32.cpp`):

> `SP_PERSONAL_DIR — self-contained: <exe_dir>/data/` — *"instead of using Documents/OpenTTD, put
> all user data (saves, config, NewGRFs, etc.) in a 'data' folder next to the exe. This avoids
> conflicts with vanilla OpenTTD installations."*

`src/fileio.cpp` confirms config resolution always picks `SP_PERSONAL_DIR` ("Skip FioFindFullPath
which would find a stale openttd.cfg in Documents/OpenTTD"). So for an install at
`<GameDirectory>`, the file is **`<GameDirectory>\data\ap_connection.cfg`** — fully deterministic,
no Documents-folder guessing.

### 1.4 AP game name — one trap to watch

- The **released** `openttd.apworld` (v1.4.1 asset, inspected directly) registers
  `game = "OpenTTD"` (apworld folder `openttd/`).
- The **main branch** has an in-flight rename: `apworld/openttd_exp/` with `game = "OpenTTD-Exp"`
  (three occurrences in `__init__.py`). The repo README's YAML example still says `game: OpenTTD`.

The plugin's `ApWorldName` must stay `"OpenTTD"` to match the shipped v1.4.1 apworld, but the next
fork release will likely change it to `"OpenTTD-Exp"` — re-check on every fork release bump.

---

## 2. Why the old architecture comment was impossible

The old header claimed: *"The AP world ships a Game Script that … opens a TCP or pipe connection
back to the launcher."* Verified false:

- The GS API class list (`https://docs.openttd.org/gs-api/annotated.html`, 176 classes) contains
  **no** networking class (no sockets, no HTTP) and **no** arbitrary file IO. Scripts are sandboxed
  Squirrel limited to game-state classes (GSVehicle, GSTown, GSGoal, …).
- The only ways data leaves a Game Script:
  - `GSLog` — writes to the OpenTTD console/log;
  - `GSAdmin` — sends JSON tables to **admin-port clients** connected to a server instance.
- Therefore a GS can only reach the outside world *through the admin port of a running server* —
  which is exactly architecture option (a), not the pipe design the file described.

### Admin-port facts (the fallback that vanilla integrations would need)

Verified from the fork's own settings tables (identical to upstream OpenTTD 14+/15.x):

| Setting | Location | Default | Source |
|---|---|---|---|
| `server_admin_port` | `openttd.cfg` `[network]` | `3977` (`NETWORK_ADMIN_PORT`, `src/network/core/config.h`) | `src/table/settings/network_settings.ini` |
| `server_admin_chat` | `openttd.cfg` `[network]` | `true` | same |
| `allow_insecure_admin_login` | `openttd.cfg` `[network]` | `false` | same |
| `admin_password` | **`secrets.cfg`** `[network]` (NOT openttd.cfg — secrets split) | `""` (disabled) | `src/table/settings/network_secrets_settings.ini` |

Key constraints that make it the *wrong* primary path for us:
- The admin port only exists on a **network server** instance — a plain single-player client never
  opens it.
- Since 14.x, plain password admin login is disabled unless `allow_insecure_admin_login=true`;
  the modern path is authorized-key auth.
- It is observation/rcon only — fine for live *status*, clumsy for item delivery (you would be
  pushing rcon commands at a Game Script).

With a native-client fork in hand, none of this is needed for play. It remains the only option if
we ever wanted AP against *unmodified* OpenTTD — and the conclusion there is: that is exactly why
the fork was built.

---

## 3. Vanilla OpenTTD version + CDN URL (the third bad claim)

Verified 2026-06-10 via `https://www.openttd.org/downloads/openttd-releases/latest` and ranged
GETs against the CDN:

| URL | Status |
|---|---|
| `https://cdn.openttd.org/openttd-releases/15.3/openttd-15.3-windows-win64.zip` | **206** (valid; 10.7 MiB) |
| `https://cdn.openttd.org/openttd-releases/14.1/openttd-14.1-windows-x64.zip` (old plugin constant) | **404** |
| `https://cdn.openttd.org/openttd-releases/14.1/openttd-14.1-windows-win64.zip` | 200 |
| `https://cdn.openttd.org/openttd-releases/15.3/openttd-15.3-windows-x64.zip` | 404 |

- Current stable: **15.3**, released 2026-04-04.
- Correct filename pattern: `openttd-{version}-windows-win64.zip` — the `windows-x64` pattern in
  the old constant has *never* been the CDN's pattern; the install path would have thrown on first
  use.
- Note: the vanilla ZIP does not bundle base graphics; the fork's win64 zip does. One more reason
  the plugin installs the fork, not vanilla. The vanilla constant is kept in the plugin only as a
  verified reference.

---

## 4. Recommended V2.1 implementation plan

### Phase 0 — done in this pass (plugin made honest, ~0 extra effort)

- Install source switched to the fork's GitHub releases (`releases/latest` with pinned v1.4.1
  fallback), mirroring the D2Plugin GitHub pattern. The win64 zip is standalone; extract + flatten.
- `openttd.apworld` release asset downloaded next to the install so the user can drop it into
  `custom_worlds` (path surfaced in the settings panel).
- `LaunchAsync` writes `<GameDirectory>\data\ap_connection.cfg` (verified format above) so the
  in-game Archipelago dialog opens pre-filled with the session's server/slot/password — then
  launches `openttd.exe` normally. Plain (non-AP) play unaffected; `SupportsStandalone` enabled.
- Speculative named-pipe bridge, `-G openttd_ap` Game Script launch flag, and `info.nut` version
  parsing removed (architecture verified impossible). News + links repointed at the real repo.

### Phase 1 — V2.1: launcher-side live status (recommended path: fork-side, NOT admin port)

We own the fork, so the cheapest reliable channel is to add it there rather than bolt an
admin-port client onto the launcher:

1. **Auto-connect key** — teach `AP_LoadConnectionConfig` an `autoconnect=1` key; when set, the
   intro GUI connects immediately with the loaded credentials. Launcher writes it; zero clicks.
   (~0.5 day fork-side, trivial launcher-side.)
2. **Status feedback** — have the fork write a small `data/ap_status.json` (state, slot, checks
   sent, items received, goal flag) on state changes; launcher tails it for IsRunning/goal UI.
   File-based, no sockets, no AV surface. (~1 day fork-side, ~0.5 day launcher-side.)
3. Optional later: a localhost socket in the fork (it already links a WebSocket/TLS stack and has
   `_ap_bridge_mode` for headless runs) if two-way control is ever needed. Not required for V2.1.

Launcher core consideration: the fork's client talks to the AP server **itself** — the launcher
must NOT also connect `ApClient` to the same slot while the game runs (duplicate-slot conflict).
The plugin therefore performs no item/check forwarding; its `LocationsChecked`/`ReceiveItemsAsync`
bridge members are intentionally inert until a fork-side channel exists.

### Phase 2 — polish (independent, ~0.5 day each)

- Auto-install `openttd.apworld` into `%ProgramData%\Archipelago\custom_worlds\` when that
  directory exists (with user consent in the settings panel).
- Re-check `ApWorldName` on every fork release (the pending `OpenTTD-Exp` rename, §1.4).
- Screenshot/video URLs for the store page from the fork repo once media is published.

**Total V2.1 estimate: ~2–3 days** (fork + launcher combined), dominated by fork-side work that
also benefits non-launcher users.

---

## Verification notes (all checked 2026-06-10)

- 404 checks: `curl` against `github.com/citymania-org/openttd-archipelago` and the API repo
  endpoint — both 404.
- Repo metadata/releases/assets: `api.github.com/repos/solida1987/openttd-archipelago` (+
  `/releases/latest`, `/git/trees/main?recursive=1`).
- Fork source inspected raw from `main`: `src/archipelago.h`, `src/archipelago.cpp`,
  `src/archipelago_gui.cpp`, `src/archipelago_manager.cpp` (lines ~7906–7945:
  `AP_SaveConnectionConfig`/`AP_LoadConnectionConfig`), `src/os/windows/win32.cpp`
  (SP_PERSONAL_DIR = `<exe_dir>/data/`), `src/fileio.cpp` (config-dir resolution),
  `src/table/settings/network_settings.ini`, `network_secrets_settings.ini`,
  `src/network/core/config.h` (`NETWORK_ADMIN_PORT = 3977`).
- Released apworld: downloaded the 57,730-byte `openttd.apworld` asset, confirmed zip entries
  under `openttd/` and `game = "OpenTTD"` in `__init__.py`.
- GS API: `https://docs.openttd.org/gs-api/annotated.html` (176 classes; GSAdmin/GSLog the only
  outbound channels; no socket/file classes).
- Vanilla version/CDN: `https://www.openttd.org/downloads/openttd-releases/latest` + ranged GETs
  shown in §3.
- AP supported-games list: `https://archipelago.gg/games` — no OpenTTD entry.
