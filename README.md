# Multiworld Launcher

A desktop launcher for [Archipelago](https://archipelago.gg/) game
integrations. It installs and updates an integration, holds the connection to a
multiworld, and shows your checks and items live while you play.

Games reach it as **plugins**: separate files you add yourself, that the
launcher loads at runtime. This repository is the launcher — the host
application and its plugin system.

## It ships with no games

Not one. Install it and the library is empty until you add something.

That includes the games written by the same person who wrote this launcher.
They are plugins like any other, downloaded from their own project and added
by hand, and they get no special treatment here: no shortcut, no listing, no
"recommended" section, no button that fetches one for you.

This is a deliberate line, and it costs the first-run experience something.
The reason is simple: a launcher that ships or fetches games is a distribution
channel for those games, and answerable for them. A launcher that loads a file
the player brought is a tool. This one is a tool.

So there is no catalogue to browse and no list to choose from — and this
README will not point you at one either. If you are here, something else
already sent you.

---

## Download & Install

1. Download **launcher_package.zip** from the [latest release](https://github.com/solida1987/Multiworld-Launcher/releases/latest).
2. Extract it to a folder you have write access to — your Desktop or Documents is fine.
3. Run **`Multiworld Launcher.exe`**.

No separate runtime is required; everything needed is bundled.

**Requirements:** Windows 10 or 11.

A game integration may have requirements of its own — an installation of the
original game, a specific patch level, extra content to install. Those belong to
the integration, and its own documentation is where they are written down. The
launcher ships no game files.

### Antivirus & Windows SmartScreen

Because the launcher is currently unsigned, Windows SmartScreen or some
antivirus products may display a warning the first time you run it.
Unrecognised, unsigned programs are flagged by default until they build up
reputation.

The launcher's full source code is in this repository for inspection, and the
executable has been submitted to Microsoft for review. SmartScreen reputation
also builds over time as more people download it. *(A code-signing certificate
would remove the warning immediately, but carries a significant recurring
cost.)*

How you respond to a warning from your own security software is your decision —
please make it based on the source code and on your own judgement.

### Self-update

On launch, the launcher checks for a newer version of itself and updates
automatically when one is available. Game updates are offered separately as an
optional button, so updating a game never blocks you from playing.

---

## Adding a plugin

A plugin arrives as a single `.londonplugin` file. You get it from whoever
wrote it — this launcher will not fetch one for you, and there is no list of
them here.

1. **Add plugin…** on the launcher's front page
2. Pick the `.londonplugin` file
3. Read what the plugin says it will do, and approve it if you agree

Step 3 is the one that matters. A plugin is a program: once loaded it runs with
the same rights as the launcher itself. The dialog shows what the plugin
declares — whether it installs files, starts an external program, downloads
from somewhere, needs the original game — along with who published it and a
hash of the exact file you are approving.

**Your approval is bound to that hash, not to the plugin's name.** If the file
changes, the plugin will not load until you look at it again. That is
deliberate: approving "some game" once should not hand a blank cheque to
whatever later arrives under the same name.

Nothing about this makes a plugin safe. It makes it *your decision*, with the
facts in front of you. Only add plugins from people you have reason to trust.

---

## Building it yourself

The full source is here, and the build is two commands.

**You need:** the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
and Windows. Nothing else — no Visual Studio, no restore steps of your own.

```
git clone https://github.com/solida1987/Multiworld-Launcher
cd Multiworld-Launcher
dotnet build -c Release
```

That produces a runnable launcher in
`bin/Release/net8.0-windows/win-x64/`.

To build the same self-contained package the releases ship:

```
dotnet publish -c Release -r win-x64 --self-contained true
python Tools/pack_launcher.py bin/Release/net8.0-windows/win-x64/publish launcher_package.zip
```

`pack_launcher.py` is not just a zip. It excludes runtime state that a
developer's build accumulates — the installed-games list, settings, crash logs —
and then **verifies what actually landed in the archive**: that the excluded
files are gone, that the executable is there and carries the expected version,
and that every file under `Assets/` made it in. A missing asset is invisible at
runtime (an icon silently falls back, a logo simply does not draw), so it is
checked where it can be counted.

### The gates

Two scripts enforce what this launcher is, and both should pass before a
release:

```
python Tools/lint_no_builtin_game.py
python Tools/lint_no_game_index.py --package bin/Release/net8.0-windows/win-x64
```

- **`lint_no_builtin_game.py`** counts references to any specific game outside
  its own plugin. The launcher must not know that a particular game exists.
  Target: **0**.
- **`lint_no_game_index.py`** fails if the code fetches a list of games from
  anywhere, or if a list of games ships inside the package.

`Tools/PluginCheck` is a separate project that takes a finished `.londonplugin`
the whole way — inspect, install, load, cast, call — and reports whether the
launcher can actually use it.

---

## Writing a plugin

**[PLUGIN_API.md](PLUGIN_API.md)** is the guide: the project layout, the
manifest, the interface, packaging, and the five mistakes everyone makes.

The short version: a plugin is a .NET class library implementing `IGamePlugin`,
plus a `plugin.json` describing it, zipped as `.londonplugin`. It builds
against this launcher but is never compiled into it.

`Tools/pack_plugin.py` builds the package from a project's build output, and
`Tools/PluginCheck` (see above) tells you whether it will load.

### The rules come first

If your plugin integrates an Archipelago game, **Archipelago's own content
rules apply to you**, not to this launcher. Read them before you publish
anything. A plugin that breaks them is your responsibility, and the manifest
asks you to confirm you have read them.

This project will not host your plugin, list it, link to it, or help
distribute it. That is not unfriendliness — it is the line that keeps this
launcher a tool rather than a distribution channel.

---

## Features

- Plugin system with per-plugin consent bound to a file hash
- One-click **install / update** with **delta updates** — only changed files download
- **AP Play** (connect to a multiworld) and **Standalone** play from one screen
- Live **location** and **item** trackers, and a graphical **map tracker**
- **Playtime & achievements** tracking
- **Launcher self-update** built in

Every one of those works the same for a plugin as it did for the game that
used to be compiled in — that was the test the plugin system had to pass
before the last built-in game was moved out.

---

## License

The launcher's own source code is licensed under Apache-2.0 (see `LICENSE`).
It is distributed together with several independent open-source components,
each of which stays under its own licence — the full list, with authors and
sources, is in `THIRD-PARTY-NOTICES.md`, and the complete licence texts are in
`licenses/`.

No game files are distributed with the launcher.

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

---

## AI Usage Disclosure

AI-assisted tools are used throughout parts of this project as productivity
tools.

This includes, but is not limited to:

- Artwork and other visual assets
- Translation between Danish and English
- Discord messages and community communication
- Patch notes, documentation and release notes
- Source-code comments and other explanatory text
- General text editing, rewriting and formatting

AI tools may also be used as part of the overall development workflow.
Regardless of what tools are used during development, I remain responsible for
the project, its implementation, testing, releases and any code that is
distributed.

My native language is Danish, so AI is particularly useful for quickly
converting what I want to say into readable English instead of spending a large
amount of development time translating and rewriting everything manually.

AI-generated or AI-assisted visual assets may also be used where appropriate. I
am not an artist, and these tools allow me to create artwork for areas of the
project that would otherwise have little or no custom artwork.

This disclosure is here so there is no ambiguity about the use of AI-assisted
tools in the project.

