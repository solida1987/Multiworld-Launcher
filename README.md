# Multiworld Launcher

The launcher for **Diablo II Archipelago** — install the mod, keep it updated, connect to an [Archipelago](https://archipelago.gg/) multiworld, or play standalone with your own randomizer settings. Two channels are available: the main release and an experimental build for testing upcoming changes.

This launcher is dedicated to Diablo II. For other Archipelago games, use the official Archipelago Launcher.

---

## Download & Install

1. Download **launcher_package.zip** from the [latest release](https://github.com/solida1987/Multiworld-Launcher/releases/latest).
2. Extract it to a folder of your choice (anywhere you have write access — e.g. your Desktop or Documents).
3. Run **`Multiworld Launcher.exe`**.

No separate runtime is required — everything needed is bundled.

### Requirements
- Windows 10 / 11
- A legally-owned installation of Diablo II: Lord of Destruction, **patched to 1.10f**

The launcher ships nothing belonging to Blizzard. It copies the game data *and* the 1.10f engine out of your own installation, verifying each engine file by exact size — the mod hooks fixed addresses inside 1.10f and cannot run on 1.13c or 1.14.

**Getting to 1.10f.** This project does not distribute Blizzard's patches and does not link to any source for them; obtaining the patch is up to you. The process is: install Classic Diablo II + Lord of Destruction from your own copy into its own folder (keep it separate from a 1.14 installation), apply the 1.10f patch there, then check that `Game.exe` is about 90 KB and that `D2Client.dll`, `D2Game.dll`, `D2Common.dll` and `Storm.dll` sit next to it. If those files are missing, the folder is still 1.14 — under 1.14 they are merged into the main executable.

### Antivirus & Windows SmartScreen

Because the launcher is currently unsigned, Windows SmartScreen or some antivirus products may display a warning the first time you run it. Unrecognised, unsigned programs are flagged by default until they build up reputation.

The launcher's full source code is in this repository for inspection, and the executable has been submitted to Microsoft for review. SmartScreen reputation also builds over time as more people download it. *(A code-signing certificate would remove the warning immediately, but carries a significant recurring cost.)*

How you respond to a warning from your own security software is your decision — please make it based on the source code and on your own judgement.

### Self-update
On launch, the launcher checks for a newer version of itself and updates automatically when one is available — you always stay current without re-downloading by hand. Game updates are offered separately as an optional button, so updating the game never blocks you from playing.

---

## Using the launcher

1. **Install** Diablo II Archipelago — the launcher downloads the mod and installs it, using the required game files from your own verified Diablo II 1.10f installation.
2. **Play**, one of two ways:
   - **AP Play** — enter your Archipelago room's server address, slot name and password, then launch already connected to the multiworld.
   - **Standalone** — play solo with your own randomizer settings.
3. **Track progress** — the **Locations** and **Items** tabs show your checks and received items live while you play, and the **Map** tab shows zones, gates and hunts.

---

## Features

- One-click **install / update** with **delta updates** (only changed files are downloaded).
- **AP Play** (connect to a multiworld) and **Standalone** play from one screen.
- Live **location / item trackers** and a graphical **map tracker**.
- **Playtime & achievements** tracking.
- **Launcher self-update** built in.

---

## License

The launcher's own source code is licensed under Apache-2.0 (see `LICENSE`).
It is distributed together with several independent open-source components,
each of which stays under its own licence — the full list, with authors and
sources, is in `THIRD-PARTY-NOTICES.md`, and the complete licence texts are in
`licenses/`.

The launcher requires a legally-owned copy of Diablo II: Lord of Destruction.
No game files are distributed with it.

### Logo

The Multiworld Launcher logo is an **adapted work** based on the Archipelago
logo — the ring of spheres is the original, the skull and flames are the
adaptation.

> The Archipelago logo © 2022 by Krista Corkos and Christopher Wilson is
> licensed under [CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/).
> This logo is a modified version of that work and is licensed under the same
> terms.

The logo is therefore **not** covered by this project's Apache-2.0 licence.
CC BY-NC 4.0 permits non-commercial use only, and this launcher is free: no
payment, subscription, advertising or paid tier. Neither Archipelago nor the
original artists endorse or are affiliated with this project.
