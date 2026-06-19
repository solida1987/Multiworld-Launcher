#!/usr/bin/env python3
"""
cleanup_stale_catalog.py — remove old community-ID catalog entries whose
games are already covered by a plugin under a different GameId.

Also fixes two known bad GameIds (with spaces) in the catalog.
"""
import json
from datetime import date
from pathlib import Path

ROOT    = Path(__file__).parent.parent
CATALOG = ROOT / "CatalogRepo" / "catalog.json"

# Old community catalog IDs that are covered by the plugin below them.
# Removing the old entry is safe — the plugin's own catalog entry exists.
STALE_IDS = {
    "age_of_empires_ii_definitive_edition",       # -> age2de
    "astalon_tears_of_the_earth",                 # -> astalon
    "bkpicross_nonograms",                        # -> bk_picross
    "bloons_td_6",                                # -> btd6
    "castlevania_circle_of_the_moon",             # -> cvcotm
    "castlevania_harmony_of_dissonance",          # -> cvhod
    "castlevania_symphony_of_the_night",          # -> sotn
    "chrono_trigger",                             # -> ctjot (Jets of Time is the AP CT world)
    "dlc_quest",                                  # -> dlcquest
    "donkey_kong_country_2_diddys_kong_quest",    # -> Donkey Kong Country 2 / dkc2
    "dragon_warrior_1",                           # -> dragon_warrior
    "duke_nukem_3d_atomic_edition",               # -> duke3d
    "ender_lilies_quietus_of_the_knights",        # -> ender_lilies
    "everhood_2",                                 # -> everhood2
    "factorio_space_age_without_space",            # -> factorio (same AP world)
    "final_fantasy_iv",                           # -> ff4fe (Free Enterprise is the AP FF4 world)
    "final_fantasy_vi",                           # -> ff6wc (World Collide is the AP FF6 world)
    "final_fantasy_xii_open_world",               # -> ffxii
    "final_fantasy_xii_trial_mode",               # -> ffxiitm
    "final_fantasy_mystic_quest",                 # -> ffmq
    "final_fantasy_pixel_remaster",               # -> ff1pr
    "fire_emblem_the_sacred_stones",              # -> fe8
    "getting_over_it_with_bennett_foddy",         # -> getting_over_it
    "golden_sun_the_lost_age",                    # -> gstla
    "hatsune_miku_project_diva_mega_mix",         # -> hatsune_miku_diva
    "js_paint",                                   # -> paint
    "k_on_after_school_live",                     # -> kon_houkago_live
    "kingdom_hearts_final_mix",                   # -> kh1
    "kingdom_hearts_chain_of_memories",           # -> khcom
    "kingdom_hearts_re_chain_of_memories",        # -> khcom (same AP world)
    "kingdom_hearts_ii_final_mix",                # -> kh2
    "kingdom_hearts_birth_by_sleep_final_mix",    # -> khbbs
    "kirby_64_the_crystal_shards",                # -> kirby_64_-_the_crystal_shards
    "lego_batman_the_video_game",                 # -> lego_batman
    "lego_star_wars_tcs",                         # -> lego_star_wars_the_complete_saga
    "loonyland_halloween_hill",                   # -> loonyland
    "lufia_ii_rise_of_the_sinistrals",            # -> lufia2ac (Ancient Cave)
    "mario_luigi_superstar_saga",                 # -> mlss
    "medievil_1998",                              # -> medievil
    "mega_man_battle_network_3_blue",             # -> mmbn3
    "minishoot_adventures",                       # -> minishoot
    "nonogram_picross",                           # -> nonogram_ap
    "panel_de_pon_tetris_attack",                 # -> panel_de_pon
    "paper_mario_the_thousand_year_door",         # -> paper_mario_ttyd
    "pok_mon_red_and_blue",                       # -> pokemon_rb
    "pok_mon_pinball",                            # -> pokemon_pinball
    "pok_mon_crystal",                            # -> pokemon_crystal
    "pok_mon_firered_and_leafgreen",              # -> pokemon_frlg
    "pok_mon_emerald",                            # -> pokemon_emerald
    "pok_mon_black_and_white",                    # -> pokemon_bw
    "pok_mon_platinum",                           # -> pokemon_platinum
    "pok_mon_mystery_dungeon_explorers_of_sky",   # -> pmd_eos
    "pok_park_wii_pikachus_adventure",            # -> pokepark_wii
    "ratchet_clank_going_commando",               # -> ratchet_and_clank_2
    "ratchet_clank_3_up_your_arsenal",            # -> ratchet_and_clank_3
    "rayman_2_the_great_escape",                  # -> rayman2
    "shapez_2",                                   # -> shapez2
    "sid_meiers_civilization_v",                  # -> civ_5
    "sid_meiers_civilization_vi",                 # -> civ_6
    "sonic_the_hedgehog",                         # -> sonic1
    "sonic_adventure_2_battle",                   # -> sa2b
    "spongebob_squarepants_battle_for_bikini_bottom",  # -> spongebob_bfbb
    "starcraft_ii",                               # -> sc2
    "starfox_64",                                 # -> star_fox_64
    "super_mario_land_2_6_golden_coins",          # -> marioland2
    "super_mario_world_2_yoshis_island",          # -> yoshisisland
    "super_mario_rpg",                            # -> smrpg
    "super_metroid_map_rando",                    # -> super_metroid (same AP world)
    "the_legend_of_heroes_trails_in_the_sky_the_3rd",  # -> trails_in_the_sky_the_3rd
    "the_legend_of_zelda_ii_the_adventure_of_link",    # -> zelda2
    "the_legend_of_zelda_links_awakening_dx",          # -> ladx
    "the_legend_of_zelda_ocarina_of_time",             # -> oot
    "the_legend_of_zelda_ocarina_of_time_but_its_just_master_quest_water_temple",  # -> oot
    "the_legend_of_zelda_majoras_mask",                # -> majoras_mask_recomp
    "the_legend_of_zelda_oracle_of_ages",              # -> tloz_ooa
    "the_legend_of_zelda_oracle_of_seasons",           # -> tloz_oos
    "the_legend_of_zelda_the_wind_waker",              # -> tww
    "the_legend_of_zelda_the_minish_cap",              # -> The Minish Cap plugin
    "the_legend_of_zelda_twilight_princess",           # -> twilight_princess
    "the_legend_of_zelda_skyward_sword",               # -> skyward_sword
    "the_legend_of_zelda_phantom_hourglass",           # -> zelda_phantom_hourglass
    "the_legend_of_zelda_spirit_tracks",               # -> zelda_spirit_tracks
    "the_legend_of_zelda_a_link_between_worlds",       # -> albw
    "the_simpsons_hit_and_run",                        # -> simpsons_hit_and_run
    "toejam_earl",                                     # -> toejam_and_earl
    "total_war_warhammer_3_immortal_empires",          # -> total_war_warhammer_3
    "trackmania",                                      # -> trackmania_random_campaign
    "turnip_boy_commits_tax_evasion",                  # -> turnip_boy
    "voltorb_flip_hgss",                               # -> voltorb_flip
    "wario_land_super_mario_land_3",                   # -> wario_land_3 (series entry)
    "xcom_2_war_of_the_chosen",                        # -> xcom2
    "yarg_yet_another_rhythm_game",                    # -> yarg
    "yarg_guitar_hero_1",                              # -> yarg
    "yu_gi_oh_dungeon_dice_monsters",                  # -> yugioh_ddm
    "yu_gi_oh_ultimate_masters_wct_2006",              # -> yugioh06
    "sens_yrtnuoc_gnok_yeknod",                        # joke/prank game, no AP world
}

def main():
    with open(CATALOG, encoding="utf-8") as f:
        data = json.load(f)

    games = data["games"]
    before = len(games)

    kept    = []
    removed = []
    for g in games:
        if g["id"] in STALE_IDS:
            removed.append(g["id"])
        else:
            kept.append(g)

    print(f"Before: {before} entries")
    print(f"Removed {len(removed)} stale entries:")
    for rid in sorted(removed):
        print(f"  - {rid}")
    print(f"After:  {len(kept)} entries")

    data["games"]   = kept
    data["updated"] = str(date.today())

    with open(CATALOG, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print(f"Saved to {CATALOG}")

if __name__ == "__main__":
    main()
