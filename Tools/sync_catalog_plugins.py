#!/usr/bin/env python3
"""
sync_catalog_plugins.py  —  add missing plugin entries to catalog.json

Reads every .cs plugin file, extracts GameId / DisplayName / ApWorldName
and a handful of other fields, then adds a minimal catalog entry for any
plugin not already in catalog.json.

Usage:
    python Tools/sync_catalog_plugins.py

Run from the Launcher V2.0.0 directory.
"""

import json, os, re, sys
from pathlib import Path

# ── Paths ──────────────────────────────────────────────────────────────────
ROOT        = Path(__file__).parent.parent          # Launcher V2.0.0/
PLUGINS_DIR = ROOT / "Plugins"
CATALOG     = ROOT / "CatalogRepo" / "catalog.json"

# ── Regex patterns ─────────────────────────────────────────────────────────
RE_GAME_ID   = re.compile(r'public(?:\s+override)?\s+string\s+GameId\s+=>\s+"([^"]+)"')
RE_DISP_NAME = re.compile(r'public(?:\s+override)?\s+string\s+DisplayName\s+=>\s+"([^"]+)"')
RE_SUBTITLE  = re.compile(r'public(?:\s+override)?\s+string\s+Subtitle\s+=>\s+"([^"]+)"')
RE_AP_WORLD  = re.compile(r'public(?:\s+override)?\s+string\s+ApWorldName\s+=>\s+"([^"]+)"')
RE_CONNECTS  = re.compile(r'public(?:\s+override)?\s+bool\s+ConnectsItself\s+=>\s+(true|false)')
RE_STANDALONE= re.compile(r'public(?:\s+override)?\s+bool\s+SupportsStandalone\s+=>\s+(true|false)')
RE_GH_OWNER  = re.compile(r'private\s+const\s+string\s+GH_OWNER\s*=\s*"([^"]+)"')
RE_GH_REPO   = re.compile(r'private\s+const\s+string\s+GH_REPO\s*=\s*"([^"]+)"')
RE_STEAM_ID  = re.compile(r'private\s+const\s+int\s+STEAM_APPID\s*=\s*(\d+)')
RE_DESCR     = re.compile(r'public(?:\s+override)?\s+string\s+Description\s+=>\s+"((?:[^"\\]|\\.)*)"')
RE_ACCENT    = re.compile(r'public(?:\s+override)?\s+string\s+ThemeAccentColor\s+=>\s+"(#[0-9A-Fa-f]+)"')

# Platform keyword detection for subtitle
PLATFORM_HINTS = {
    "SNES": "SNES",  "Super Nintendo": "SNES",
    "GBA":  "GBA",   "Game Boy Advance": "GBA",
    "GBC":  "GBC",   "Game Boy Color": "GBC",
    "GB":   "GB",    "Game Boy": "GB",
    "N64":  "N64",   "Nintendo 64": "N64",
    "NES":  "NES",   "Famicom": "NES",
    "PS1":  "PS1",   "PlayStation": "PS1",  "PSX": "PS1",
    "PS2":  "PS2",   "PlayStation 2": "PS2",
    "GCN":  "GCN",   "GameCube": "GCN",
    "DS":   "DS",    "Nintendo DS": "DS",
    "Wii":  "Wii",
    "Genesis": "Genesis", "Mega Drive": "Genesis",
    "2600": "Atari 2600",
}

CATEGORY_MAP = {
    "SNES": "Platformer",
    "GBA":  "Platformer",
    "GBC":  "Adventure",
    "GB":   "Adventure",
    "N64":  "Platformer",
    "NES":  "Platformer",
    "PS1":  "Action",
    "PS2":  "Action",
    "GCN":  "Adventure",
    "DS":   "Role-Playing",
    "Wii":  "Action",
    "Genesis": "Platformer",
    "Atari 2600": "Arcade",
}


def extract_field(text, pattern):
    m = pattern.search(text)
    return m.group(1).strip() if m else None


def is_emulated(filepath: Path) -> bool:
    return "Emulated" in str(filepath.parent)


def detect_platform(subtitle: str | None) -> str | None:
    if not subtitle:
        return None
    for kw, plat in PLATFORM_HINTS.items():
        if kw in subtitle:
            return plat
    return None


def infer_category(subtitle: str | None, display_name: str) -> str:
    plat = detect_platform(subtitle)
    if plat and plat in CATEGORY_MAP:
        return CATEGORY_MAP[plat]
    dn = display_name.lower()
    if any(k in dn for k in ["zelda", "mario", "sonic", "kirby", "donkey kong",
                               "mega man", "castlevania", "metroid", "earthbound",
                               "chrono", "final fantasy", "pokemon", "pokémon",
                               "lufia", "secret of", "fire emblem"]):
        return "Adventure"
    if any(k in dn for k in ["golf", "pinball", "sports", "tennis", "kart"]):
        return "Sports"
    if any(k in dn for k in ["strategy", "civilization", "wargroove"]):
        return "Strategy"
    if any(k in dn for k in ["puzzle", "tetris", "picross", "nonogram", "sudoku",
                               "crossword", "jigsaw", "bk picross"]):
        return "Puzzle"
    if any(k in dn for k in ["card", "balatro", "slay the spire", "dicey"]):
        return "Card Game"
    if any(k in dn for k in ["rogue", "binding of", "gungeon", "hades",
                               "risk of rain", "brotato"]):
        return "Roguelite"
    if any(k in dn for k in ["rpg", "role"]):
        return "Role-Playing"
    if any(k in dn for k in ["horror", "resident"]):
        return "Survival Horror"
    return "Action"


def infer_install_strategy(is_emu: bool, connects_itself: bool) -> str:
    if is_emu:
        return "rom_required"
    if connects_itself:
        return "mod_on_top"
    return "external_client"


def scan_plugins() -> dict[str, dict]:
    """Return dict of GameId → extracted fields from plugin CS files."""
    result: dict[str, dict] = {}

    for cs_file in PLUGINS_DIR.rglob("*.cs"):
        # Skip the base class and interface files
        if cs_file.name in ("IGamePlugin.cs", "EmulatorPlugin.cs",
                             "EmulatedGamePlugin.cs", "GameRegistry.cs"):
            continue
        try:
            text = cs_file.read_text(encoding="utf-8", errors="ignore")
        except Exception:
            continue

        game_id = extract_field(text, RE_GAME_ID)
        if not game_id:
            continue

        result[game_id] = {
            "game_id":         game_id,
            "display_name":    extract_field(text, RE_DISP_NAME) or game_id,
            "subtitle":        extract_field(text, RE_SUBTITLE),
            "ap_world_name":   extract_field(text, RE_AP_WORLD) or game_id,
            "connects_itself": (extract_field(text, RE_CONNECTS) or "false") == "true",
            "standalone":      (extract_field(text, RE_STANDALONE) or "false") == "true",
            "gh_owner":        extract_field(text, RE_GH_OWNER),
            "gh_repo":         extract_field(text, RE_GH_REPO),
            "steam_appid":     int(extract_field(text, RE_STEAM_ID) or 0),
            "description":     extract_field(text, RE_DESCR),
            "accent":          extract_field(text, RE_ACCENT) or "#405080",
            "is_emulated":     is_emulated(cs_file),
            "filepath":        str(cs_file.relative_to(ROOT)),
        }

    return result


def make_catalog_entry(info: dict) -> dict:
    game_id   = info["game_id"]
    disp_name = info["display_name"]
    subtitle  = info["subtitle"]
    is_emu    = info["is_emulated"]
    ci        = info["connects_itself"]

    plat = detect_platform(subtitle)
    category = infer_category(subtitle, disp_name)
    strategy = infer_install_strategy(is_emu, ci)

    # Platform list
    if is_emu:
        platforms = [plat] if plat else ["Unknown"]
    else:
        platforms = ["PC"]

    # GitHub URLs
    gh_owner = info["gh_owner"]
    gh_repo  = info["gh_repo"]
    install_url = news_url = None
    if gh_owner and gh_repo:
        install_url = f"https://github.com/{gh_owner}/{gh_repo}/releases/latest"
        news_url    = f"https://api.github.com/repos/{gh_owner}/{gh_repo}/releases"

    steam_id = info["steam_appid"]
    purchase_url = f"https://store.steampowered.com/app/{steam_id}/" if steam_id else None

    description = info["description"] or ""
    if description:
        # Unescape C# string literals
        description = description.replace('\\"', '"').replace("\\n", "\n").replace("\\\\", "\\")

    return {
        "id":                 game_id,
        "display_name":       disp_name,
        "author":             "",
        "category":           category,
        "description":        description,
        "ap_world_name":      info["ap_world_name"],
        "plugin_type":        "emulated" if is_emu else "native",
        "emulator":           "bizhawk" if is_emu else None,
        "thumbnail_url":      f"Assets/Thumbs/{game_id.lower().replace(' ', '_').replace(':', '')}_thumb.png",
        "video_url":          None,
        "screenshot_urls":    [],
        "install_url":        install_url,
        "news_url":           news_url,
        "tags":               [],
        "hint_game":          False,
        "min_launcher_version": "2.0.0",
        "status":             "available",
        "platforms":          platforms,
        "free":               False,
        "requires_rom":       is_emu,
        "install_strategy":   strategy,
        "credits":            [],
        "links":              {
            "ap_game_page":  f"https://archipelago.gg/games/{info['ap_world_name'].replace(' ', '%20')}",
            "ap_discord":    "https://discord.gg/archipelago",
        },
        "subcategory":        "",
        "est_playtime_min":   60,
        "player_count":       "1+",
        "steam_app_id":       steam_id,
        "purchase_url":       purchase_url,
        "install_guide":      None,
    }


def main():
    print(f"Reading catalog from {CATALOG}")
    with open(CATALOG, encoding="utf-8") as f:
        catalog = json.load(f)

    existing_ids = {g["id"] for g in catalog["games"]}
    print(f"  {len(existing_ids)} existing entries")

    print(f"Scanning plugins in {PLUGINS_DIR}")
    plugins = scan_plugins()
    print(f"  {len(plugins)} plugins found")

    missing = {gid: info for gid, info in plugins.items() if gid not in existing_ids}
    print(f"  {len(missing)} plugins not in catalog — adding them")

    if not missing:
        print("Nothing to add. Catalog is up to date.")
        return 0

    new_entries = []
    for game_id, info in sorted(missing.items()):
        entry = make_catalog_entry(info)
        new_entries.append(entry)
        print(f"  + {game_id!r:45s}  ({info['filepath']})")

    # Append new entries to the games array
    from datetime import date
    catalog["games"].extend(new_entries)
    catalog["updated"] = str(date.today())

    out = json.dumps(catalog, ensure_ascii=False, indent=2)
    with open(CATALOG, "w", encoding="utf-8") as f:
        f.write(out)
        f.write("\n")

    print(f"\nWrote {len(catalog['games'])} total entries to {CATALOG}")
    print(f"Added {len(new_entries)} new entries.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
