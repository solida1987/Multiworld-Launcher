# Third-party notices

Diablo II Archipelago is assembled from its own code plus several independent
open-source projects. This file lists every one of them: what it does, which
files it provides, who wrote it, and under what licence. The full licence text
of each is in [`licenses/`](licenses/) so it can be read here without
downloading anything.

**No files belonging to Blizzard Entertainment are distributed with this
project** — no game data and no engine binaries. Those come from the player's
own Diablo II installation. See [Engine patches](#engine-patches) below for the
only changes made to them, and the README for the 1.10f requirement.

---

## Components

### D2.Detours — MIT
Loads a mod DLL into Diablo II and redirects the game's own functions to it.
This is the mechanism the whole mod runs on.

- **Files:** `D2.Detours.dll`, `D2.DetoursLauncher.exe`
- **Copyright:** © 2017 Lectem
- **Source:** https://github.com/Lectem/D2.Detours
- **Licence:** [`licenses/D2.Detours-MIT.txt`](licenses/D2.Detours-MIT.txt)

### D2MOO — MIT
An open-source re-implementation of Diablo II's game logic. The mod ships
D2MOO builds of two libraries, which the game loads in place of its own, plus
its debugger.

- **Files:** `patch/D2Game.dll`, `patch/Fog.dll`, `D2Debugger.dll`
- **Copyright:** © 2020–2025 The Phrozen Keep community
- **Source:** https://github.com/ThePhrozenKeep/D2MOO
- **Licence:** [`licenses/D2MOO-MIT.txt`](licenses/D2MOO-MIT.txt)

### cnc-ddraw — MIT
A DirectDraw wrapper that lets a 2003 game render correctly on modern Windows,
with windowed mode and scaling.

- **Files:** `ddraw.dll`, `ddraw.ini`, `winmm.dll`
- **Copyright:** © 2025 github.com/FunkyFr3sh
- **Source:** https://github.com/FunkyFr3sh/cnc-ddraw
- **Licence:** [`licenses/cnc-ddraw-MIT.txt`](licenses/cnc-ddraw-MIT.txt)

### d2gl — GPL-3.0
A Glide-to-OpenGL renderer that provides the HD graphics option.

- **Files:** `glide3x.dll`, `d2gl.mpq`, `d2gl.ini`, `d2gl.json`
- **Copyright:** © Bayaraa
- **Source:** https://github.com/bayaraa/d2gl
- **Licence:** [`licenses/d2gl-GPL-3.0.txt`](licenses/d2gl-GPL-3.0.txt)

### SlashGaming Diablo II Free Resolution (SGD2FreeRes) — AGPL-3.0-or-later
Unlocks resolutions the original game does not offer.

- **Files:** `SGD2FreeRes.dll`, `SGD2FreeRes.mpq`, `SGD2FreeResolution.json`
- **Copyright:** © 2019–2024 Mir Drualga
- **Source:** https://github.com/mir-diablo-ii-tools/SlashGaming-Diablo-II-Free-Resolution
- **Licence:** [`licenses/SGD2FreeRes-AGPL-3.0.txt`](licenses/SGD2FreeRes-AGPL-3.0.txt)

### DSOAL — LGPL-2.1
Restores the hardware-accelerated 3D audio the original game was written for,
through OpenAL. Shipped unmodified.

- **Files:** `dsound.dll`, `dsoal-aldrv.dll`
- **Copyright:** © Chris Robinson (kcat)
- **Source:** https://github.com/kcat/dsoal
- **Licence:** [`licenses/DSOAL-LGPL-2.1.txt`](licenses/DSOAL-LGPL-2.1.txt)

### SFmpqapi — BSD-style (2-clause)
A library for reading and writing MPQ archives, used by the mod's tooling.

- **Files:** `SFMPQ.dll`
- **Copyright:** © 2002–2010 ShadowFlare
- **Source:** https://github.com/ShadowFlare/SFmpqapi
- **Licence:** [`licenses/SFmpqapi-terms.txt`](licenses/SFmpqapi-terms.txt)

---

## How these components relate to this project's own code

The mod's own library, `D2Archipelago.dll`, imports only from the Windows
system libraries `KERNEL32`, `USER32`, `ADVAPI32` and `XINPUT9_1_0`. It does
not link against d2gl, SGD2FreeRes, DSOAL or any other component listed above.

Each of those is an independent program that Diablo II loads in its own right —
a renderer, an audio driver, a resolution patch — and each keeps its own
licence. They are distributed alongside this project rather than built into it,
and the licence on this project's own code applies only to that code, never to
them.

Anyone redistributing this package must keep this file, the `licenses/` folder,
and the components' own copyright notices intact, and must pass on the source
availability the GPL, AGPL and LGPL require. The source of every component is
linked above.

---

## Engine patches

The mod changes 32 bytes across three of Diablo II's own libraries, applied by
the launcher to the player's own copies. The files themselves are never
distributed; what is distributed is the description of the changes, which lives
in the launcher's source in `Plugins/DiabloII/D2EnginePatch.cs`:

| File | Changes | Purpose |
|---|---|---|
| `Storm.dll` | 1 byte | Diablo II otherwise refuses to load a modded archive and stops with *"The file data is corrupt"* |
| `D2Glide.dll` | 2 bytes | Display handling the mod depends on |
| `D2Launch.dll` | 29 bytes | Main-menu adjustments |

Each edit records the exact bytes it expects to replace, and each file is
identified by the SHA-256 of both its unpatched and patched forms, so the
change is fully reproducible and verifiable from the source.
