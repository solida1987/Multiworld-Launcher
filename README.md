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

Some antivirus tools and Windows SmartScreen may flag **`Multiworld Launcher.exe`** the first time you run it. **This is a false positive.** The launcher is a brand-new application that Windows doesn't recognise yet, and unrecognised, unsigned programs are flagged by default until they build up reputation — regardless of what they actually contain. It is safe to run.

This is being addressed on two fronts: the application has been submitted to Microsoft for review, and Windows SmartScreen builds trust automatically as more people download and run it, so the warning clears on its own over time. *(A commercial code-signing certificate would remove the warning instantly, but it carries a significant recurring cost, so we're pursuing the free Microsoft review and reputation route first.)*

**To run it:** on the SmartScreen prompt click **"More info"** → **"Run anyway"**. If your antivirus quarantines the file, restore it or add an exception.

### Self-update
On launch, the launcher checks for a newer version of itself and updates automatically when one is available — you always stay current without re-downloading by hand. Game updates are offered separately as an optional button, so updating the game never blocks you from playing.

---

## Using the launcher

1. **Install** Diablo II Archipelago — the launcher downloads the mod and sets it up around your own copy of Diablo II's game data.
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

The launcher is provided for use with Diablo II Archipelago and requires a legally-owned copy of Diablo II: Lord of Destruction.
