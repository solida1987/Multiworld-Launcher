# Third-party notices

Diablo II Archipelago is assembled from its own code plus a few independent
open-source projects. This file lists every one of them: what it does, which
files it provides, who wrote it, and under what licence. The full licence text
of each is in [`licenses/`](licenses/) so it can be read here without
downloading anything.

**No files belonging to Blizzard Entertainment are distributed with this
project** — no game data and no engine binaries. Those come from the player's
own Diablo II installation. See [Engine patches](#engine-patches) below for the
only changes made to them, and the README for the 1.10f requirement.

---

## Bundled components

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
A DirectDraw wrapper that lets a 2003 game render correctly on modern Windows.
Only its `winmm.dll` component is shipped.

- **Files:** `winmm.dll`
- **Copyright:** © 2025 github.com/FunkyFr3sh
- **Source:** https://github.com/FunkyFr3sh/cnc-ddraw
- **Licence:** [`licenses/cnc-ddraw-MIT.txt`](licenses/cnc-ddraw-MIT.txt)

### SFmpqapi — BSD-style (2-clause)
A library for reading and writing MPQ archives, used by the mod's tooling.

- **Files:** `SFMPQ.dll`
- **Copyright:** © 2002–2010 ShadowFlare
- **Source:** https://github.com/ShadowFlare/SFmpqapi
- **Licence:** [`licenses/SFmpqapi-terms.txt`](licenses/SFmpqapi-terms.txt)

---

## Not distributed — installed by the player

Three optional add-ons used to be included in this package. They no longer are.

| Component | Licence | What it does |
|---|---|---|
| [d2gl](https://github.com/bayaraa/d2gl) | GPL-3.0 | Glide-to-OpenGL renderer — the HD graphics option |
| [SGD2FreeRes](https://github.com/mir-diablo-ii-tools/SlashGaming-Diablo-II-Free-Resolution) | AGPL-3.0-or-later | Unlocks resolutions the original game does not offer |
| [DSOAL](https://github.com/kcat/dsoal) | LGPL-2.1 | Restores the hardware-accelerated 3D audio the game was written for |

Bundling them was a mistake, and it is worth being plain about why rather than
quietly dropping them.

Each of these is an independent program that Diablo II loads in its own right.
None of them is linked into this project's code: the mod's own library,
`D2Archipelago.dll`, imports only from the Windows system libraries
`KERNEL32`, `USER32`, `ADVAPI32` and `XINPUT9_1_0`. That argument — that they
are separate works merely shipped side by side — is a real one, and it is the
argument this project was relying on.

But it is an argument, not a fact, and the GPL family defines *propagation*
broadly enough that reasonable people read the boundary differently. Putting a
GPL-3.0 renderer, an AGPL-3.0 resolution patch and an LGPL-2.1 audio driver
into the same download as this project asks a licence question that nobody
here can answer with certainty, and the cost of being wrong falls on the
people who wrote those components.

So the question is no longer asked. They are add-ons, not part of the mod, and
the mod runs without all three. The README explains what each does, links to
its author's own releases, and describes where the files go — the same way the
player already supplies their own copy of Diablo II. Downloading a program and
running it on your own machine carries none of these obligations;
redistributing it is what does.

The settings files `d2gl.ini`, `d2gl.json` and `SGD2FreeResolution.json` are
still included. Those are this project's own tuned configuration for those
tools, not the tools themselves, and they simply sit unused until the matching
component is installed.

---

## Artwork

### Archipelago logo — CC BY-NC 4.0

**What it is.** The launcher's own logo — the ring of coloured spheres with a
skull at its centre, wreathed in green flame — is an **adapted work** built on
the Archipelago logo. The arrangement of spheres is the original; the skull,
the flames and the colour treatment are the adaptation.

**Original.** The Archipelago logo © 2022 by Krista Corkos and Christopher
Wilson, licensed under Creative Commons Attribution-NonCommercial 4.0
International (CC BY-NC 4.0).

- Licence: https://creativecommons.org/licenses/by-nc/4.0/
- Archipelago: https://archipelago.gg/

**Where it appears.** `Assets/app.ico` (the executable's icon),
`Assets/logo.png`, `Assets/_generic.png` and `Assets/Thumbs/_generic_thumb.png`
(shown for a game that has no artwork of its own yet).

**What that licence requires of this project.**

- The logo is **not** covered by this project's Apache-2.0 licence. It stays
  under CC BY-NC 4.0, and the adaptation inherits that licence.
- **NonCommercial.** The launcher is distributed free of charge, with no
  payment, subscription, advertising or paid tier. Should that ever change,
  the logo has to be replaced or separate permission obtained from the rights
  holders — CC BY-NC does not allow commercial use.
- Neither Archipelago nor the original artists endorse, sponsor or are
  affiliated with this launcher.

---

## How the bundled components relate to this project's own code

The four components listed under **Bundled components** are all under
permissive licences (MIT and BSD 2-clause) that impose no condition beyond
keeping the copyright notice and licence text — which this file and the
`licenses/` directory do.

Anyone redistributing this package must keep this file, the `licenses/` folder,
and the components' own copyright notices intact. The source of every component
is linked above.

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
