"""
enrich_catalog.py — Upgrades catalog.json from schema v1 to v2.

Adds to every entry:
  • install_strategy  (inferred from plugin_type / platforms / requires_rom)
  • credits           (populated for well-known games; stub for others)
  • links             (ap_game_page generated from ap_world_name)
  • subcategory       (inferred from category)
  • est_playtime_min  (rough estimate by category)
  • player_count      ("1+")

Run from the Launcher V2.0.0 directory:
    python Tools/enrich_catalog.py
"""

import json, re, pathlib, sys

ROOT  = pathlib.Path(__file__).parent.parent
SRC   = ROOT / "CatalogRepo" / "catalog.json"
DEST  = ROOT / "CatalogRepo" / "catalog.json"   # overwrite in-place

# ── Per-game manual enrichments ────────────────────────────────────────────────
# Keys must match catalog "id" field.  Only override what the auto-logic can't
# guess correctly.

MANUAL = {
    "diablo2_archipelago": {
        "install_strategy": "point_to_existing",
        "credits": [
            {"role": "Original game", "name": "Blizzard Entertainment",
             "url": "https://blizzard.com"},
            {"role": "AP world & mod", "name": "Marco / Solida Games",
             "url": "https://github.com/solida1987/Diablo-II-Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Diablo%20II%20Archipelago",
            "ap_discord":   "https://discord.gg/archipelago",
            "ap_github":    "https://github.com/solida1987/Diablo-II-Archipelago",
        },
        "install_guide": (
            "## Diablo II Archipelago — Install Guide\n\n"
            "### Requirements\n"
            "- Diablo II Lord of Destruction (any version)\n"
            "- Windows 10 or 11\n\n"
            "### Steps\n"
            "1. Click **Point to Existing Install** and select your `Diablo II.exe`.\n"
            "2. The launcher downloads and applies the mod automatically.\n"
            "3. Enter your AP server address and slot name, then click **Play**."
        ),
        "est_playtime_min": 240,
        "player_count": "1–8",
        "purchase_url": "https://us.battle.net/shop/en/product/diablo-ii",
    },
    "openttd_archipelago": {
        "install_strategy": "direct_download",
        "credits": [
            {"role": "Original game", "name": "OpenTTD Community",
             "url": "https://openttd.org"},
            {"role": "AP world", "name": "Marco / Solida Games",
             "url": "https://github.com/solida1987"},
        ],
        "links": {
            "official_site": "https://openttd.org",
            "ap_discord":    "https://discord.gg/archipelago",
        },
        "est_playtime_min": 120,
        "subcategory": "Simulation",
    },
    "a_link_to_the_past": {
        "install_strategy": "rom_required",
        "credits": [
            {"role": "Original game", "name": "Nintendo",
             "url": "https://nintendo.com"},
            {"role": "AP world", "name": "Berserker66",
             "url": "https://github.com/Berserker66"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/A%20Link%20to%20the%20Past",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "est_playtime_min": 90,
        "subcategory": "Zelda",
        "install_guide": (
            "## A Link to the Past — Install Guide\n\n"
            "### Requirements\n- ALttP (USA) SNES ROM (.sfc / .smc)\n"
            "- BizHawk (auto-installed by the launcher)\n\n"
            "### Steps\n"
            "1. Click **Select ROM** and choose your .sfc file.\n"
            "2. The launcher patches and launches via BizHawk."
        ),
    },
    "hollow_knight": {
        "install_strategy": "mod_on_top",
        "credits": [
            {"role": "Original game", "name": "Team Cherry",
             "url": "https://teamcherry.com.au"},
            {"role": "AP world & mod", "name": "BadMagic100",
             "url": "https://github.com/BadMagic100"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Hollow%20Knight",
            "ap_discord":   "https://discord.gg/archipelago",
            "ap_github":    "https://github.com/BadMagic100/HollowKnight.Archipelago",
        },
        "est_playtime_min": 480,
        "steam_app_id": 367520,
        "purchase_url": "https://store.steampowered.com/app/367520/Hollow_Knight/",
    },
    "minecraft": {
        "install_strategy": "external_client",
        "credits": [
            {"role": "Original game", "name": "Mojang / Microsoft",
             "url": "https://minecraft.net"},
            {"role": "AP world", "name": "KonoTyran",
             "url": "https://github.com/KonoTyran"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Minecraft",
            "ap_discord":   "https://discord.gg/archipelago",
            "ap_github":    "https://github.com/KonoTyran/Minecraft_AP_Randomizer",
        },
        "est_playtime_min": 360,
        "player_count": "1–8",
        "purchase_url": "https://www.minecraft.net/en-us/store/minecraft-java-bedrock-edition-pc",
    },
    "subnautica": {
        "install_strategy": "mod_on_top",
        "credits": [
            {"role": "Original game", "name": "Unknown Worlds Entertainment",
             "url": "https://unknownworlds.com"},
            {"role": "AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Subnautica",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "est_playtime_min": 480,
        "steam_app_id": 264710,
        "purchase_url": "https://store.steampowered.com/app/264710/Subnautica/",
    },
    "slay_the_spire": {
        "install_strategy": "mod_on_top",
        "credits": [
            {"role": "Original game", "name": "MegaCrit",
             "url": "https://megacrit.com"},
            {"role": "AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Slay%20the%20Spire",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "steam_app_id": 646570,
        "est_playtime_min": 60,
        "purchase_url": "https://store.steampowered.com/app/646570/Slay_the_Spire/",
    },
    "starcraft_2": {
        "install_strategy": "external_client",
        "credits": [
            {"role": "Original game", "name": "Blizzard Entertainment",
             "url": "https://blizzard.com"},
            {"role": "AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page":  "https://archipelago.gg/games/Starcraft%202",
            "official_site": "https://starcraft2.com",
            "ap_discord":    "https://discord.gg/archipelago",
        },
        "est_playtime_min": 60,
    },
    "super_metroid": {
        "install_strategy": "rom_required",
        "credits": [
            {"role": "Original game", "name": "Nintendo",
             "url": "https://nintendo.com"},
            {"role": "AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Super%20Metroid",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "est_playtime_min": 120,
        "subcategory": "Metroid",
    },
    "ocarina_of_time": {
        "install_strategy": "rom_required",
        "credits": [
            {"role": "Original game", "name": "Nintendo",
             "url": "https://nintendo.com"},
            {"role": "AP world", "name": "TestRunnerSRL",
             "url": "https://github.com/TestRunnerSRL"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Ocarina%20of%20Time",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "est_playtime_min": 180,
        "subcategory": "Zelda",
    },
    "blasphemous": {
        "install_strategy": "mod_on_top",
        "credits": [
            {"role": "Original game", "name": "The Game Kitchen",
             "url": "https://thegamekitchen.com"},
            {"role": "AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Blasphemous",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "steam_app_id": 774361,
        "est_playtime_min": 360,
        "purchase_url": "https://store.steampowered.com/app/774361/Blasphemous/",
    },
    "timespinner": {
        "install_strategy": "mod_on_top",
        "credits": [
            {"role": "Original game", "name": "Lunar Ray Games",
             "url": "https://timespinnergame.com"},
            {"role": "AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Timespinner",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "steam_app_id": 368620,
        "est_playtime_min": 240,
        "purchase_url": "https://store.steampowered.com/app/368620/Timespinner/",
    },
    "pokemon_red_and_blue": {
        "install_strategy": "rom_required",
        "credits": [
            {"role": "Original game", "name": "Nintendo / Game Freak",
             "url": "https://pokemon.com"},
            {"role": "AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Pokemon%20Red%20and%20Blue",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "est_playtime_min": 180,
        "subcategory": "Pokémon",
    },
    "hylics_2": {
        "install_strategy": "direct_download",
        "credits": [
            {"role": "Original game", "name": "Mason Lindroth",
             "url": "https://masonlindroth.itch.io"},
            {"role": "AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page":   "https://archipelago.gg/games/Hylics%202",
            "ap_discord":     "https://discord.gg/archipelago",
            "purchase_url":   "https://masonlindroth.itch.io/hylics-2",
        },
        "est_playtime_min": 90,
    },
    "lufia_ii_ancient_cave": {
        "install_strategy": "rom_required",
        "credits": [
            {"role": "Original game", "name": "Taito / Neverland",
             "url": ""},
            {"role": "AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Lufia%20II%20Ancient%20Cave",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "est_playtime_min": 90,
    },
    "overcooked_2": {
        "install_strategy": "mod_on_top",
        "credits": [
            {"role": "Original game", "name": "Ghost Town Games",
             "url": "https://www.ghosttowngames.com"},
            {"role": "AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Overcooked%21%202",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "steam_app_id": 728880,
        "est_playtime_min": 90,
        "player_count": "1–4",
        "purchase_url": "https://store.steampowered.com/app/728880/Overcooked_2/",
    },
    "meritous": {
        "install_strategy": "direct_download",
        "credits": [
            {"role": "Original game", "name": "Micro / Asceai", "url": ""},
            {"role": "AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Meritous",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "est_playtime_min": 60,
    },
    "archip_idle": {
        "install_strategy": "web_only",
        "credits": [
            {"role": "Game & AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/ArchipIDLE",
            "ap_discord":   "https://discord.gg/archipelago",
        },
    },
    "clique": {
        "install_strategy": "web_only",
        "credits": [
            {"role": "Game & AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Clique",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "est_playtime_min": 5,
    },
    "vvvvvv": {
        "install_strategy": "mod_on_top",
        "credits": [
            {"role": "Original game", "name": "Terry Cavanagh",
             "url": "https://terrycavanagh.itch.io"},
            {"role": "AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/VVVVVV",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "steam_app_id": 70300,
        "est_playtime_min": 60,
        "purchase_url": "https://store.steampowered.com/app/70300/VVVVVV/",
    },
    "rogue_legacy": {
        "install_strategy": "mod_on_top",
        "credits": [
            {"role": "Original game", "name": "Cellar Door Games",
             "url": "https://cellardoorgames.com"},
            {"role": "AP world", "name": "Archipelago Community",
             "url": "https://github.com/ArchipelagoMW/Archipelago"},
        ],
        "links": {
            "ap_game_page": "https://archipelago.gg/games/Rogue%20Legacy",
            "ap_discord":   "https://discord.gg/archipelago",
        },
        "steam_app_id": 241600,
        "est_playtime_min": 120,
        "purchase_url": "https://store.steampowered.com/app/241600/Rogue_Legacy/",
    },
}

# ── Category → subcategory map ────────────────────────────────────────────────
SUBCATEGORIES = {
    "Zelda": ["zelda", "link", "hyrule"],
    "Pokémon": ["pokemon", "pokémon"],
    "Mario": ["mario", "yoshi"],
    "Metroid": ["metroid"],
    "Final Fantasy": ["final fantasy", "ff1", "ff6"],
    "Sonic": ["sonic"],
    "Donkey Kong": ["donkey kong", "dkc"],
}

# ── Install strategy auto-inference ──────────────────────────────────────────

def infer_strategy(game: dict) -> str:
    pt  = game.get("plugin_type", "external")
    plat = " ".join(game.get("platforms", [])).lower()
    tags = " ".join(game.get("tags", [])).lower()
    has_url = bool(game.get("install_url"))

    if pt == "emulated":
        return "rom_required"
    if "discord" in plat or "discord" in tags:
        return "web_only"
    if "browser" in plat or "web" in plat or game.get("hint_game"):
        return "web_only"
    if pt == "external":
        return "external_client" if has_url else "manual_only"
    if pt == "native":
        # D2-style (already handled by MANUAL) — default: point_to_existing
        return "point_to_existing"
    return "manual_only"

# ── Playtime estimate by category ─────────────────────────────────────────────
PLAYTIME = {
    "action rpg": 180, "rpg": 180, "platformer": 90, "metroidvania": 300,
    "roguelike": 60, "strategy": 120, "simulation": 120, "survival": 360,
    "sandbox": 240, "co-op": 90, "puzzle": 30, "hint game": 15, "idle": 0,
}

# ── AP game page URL generator ────────────────────────────────────────────────
def ap_game_url(ap_world_name: str) -> str:
    import urllib.parse
    return f"https://archipelago.gg/games/{urllib.parse.quote(ap_world_name)}"

# ── Subcategory inference ─────────────────────────────────────────────────────
def infer_subcategory(game: dict) -> str:
    text = (game.get("display_name", "") + " " +
            game.get("ap_world_name", "") + " " +
            " ".join(game.get("tags", ""))).lower()
    for sub, keywords in SUBCATEGORIES.items():
        if any(k in text for k in keywords):
            return sub
    return ""

# ── Main enrichment ───────────────────────────────────────────────────────────

def enrich(game: dict) -> dict:
    gid = game.get("id", "")
    manual = MANUAL.get(gid, {})

    # schema_version 2 new fields — only add if missing
    if "install_strategy" not in game:
        game["install_strategy"] = manual.get("install_strategy") or infer_strategy(game)
    if "credits" not in game:
        game["credits"] = manual.get("credits", [])
    if "links" not in game:
        links = manual.get("links", {})
        # Always add ap_game_page if missing and we have ap_world_name
        if "ap_game_page" not in links and game.get("ap_world_name"):
            links["ap_game_page"] = ap_game_url(game["ap_world_name"])
        if "ap_discord" not in links:
            links["ap_discord"] = "https://discord.gg/archipelago"
        game["links"] = links
    if "subcategory" not in game:
        game["subcategory"] = manual.get("subcategory") or infer_subcategory(game)
    if "est_playtime_min" not in game:
        cat = game.get("category", "").lower()
        game["est_playtime_min"] = manual.get("est_playtime_min") or PLAYTIME.get(cat, 60)
    if "player_count" not in game:
        game["player_count"] = manual.get("player_count", "1+")
    if "steam_app_id" not in game:
        game["steam_app_id"] = manual.get("steam_app_id", 0)
    if "purchase_url" not in game:
        game["purchase_url"] = manual.get("purchase_url")
    if "install_guide" not in game:
        game["install_guide"] = manual.get("install_guide")

    # Override any manual fields
    for k, v in manual.items():
        if k not in ("credits", "links") or not game.get(k):
            game[k] = v

    return game


def main():
    print(f"Reading: {SRC}")
    with open(SRC, encoding="utf-8") as f:
        data = json.load(f)

    games = data.get("games", [])
    print(f"  {len(games)} games found  (schema_version {data.get('schema_version', 1)})")

    enriched = [enrich(g) for g in games]
    data["schema_version"] = 2
    data["games"] = enriched

    with open(DEST, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    print(f"Written: {DEST}  ({len(enriched)} games, schema_version 2)")


if __name__ == "__main__":
    main()
