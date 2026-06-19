# Approved fan-art generation rules

Date: 2026-06-13
Project: Diablo II Archipelago Launcher V2.0.0
Decision: use a medium-risk fan-art style, not the ultra-conservative abstract-only style.

## Goal

Generate launcher artwork that clearly feels relevant to each game while avoiding direct copying of protected assets.

The target is: recognizable genre/game vibe, original composition, no official asset reproduction.

## Required asset types

Each catalog game may receive three images:

| Type | File pattern | Size |
|---|---|---:|
| Icon | `Assets/<id>.png` | 256x256 |
| Thumbnail | `Assets/Thumbs/<id>_thumb.png` | 488x256 |
| Hero | `Assets/Heroes/<id>_hero.png` | 1380x280 |

## Approved style rule

Use unofficial fan-made artwork that evokes the game through:

- genre
- mood
- world type
- gameplay loop
- era/platform feel
- broad visual motifs
- original characters, creatures, props, scenes, and silhouettes

Do not use official game assets.

## Allowed

- Original fan-art scenes inspired by the game concept.
- Original creature, character, vehicle, environment, item, and enemy designs.
- Plain readable text with the catalog display name or a clearly unofficial label.
- Game-relevant motifs, if not copied from official art.
- Nostalgic platform-era feeling, without copying sprites or UI.
- More direct “fanart” feeling than purely generic abstract art.

## Not allowed

- Official logos or logo-like typography.
- Publisher/developer logos.
- Screenshots, sprites, box art, maps, UI, HUDs, menus, or exact layout copies.
- Direct copies of known characters, monsters, bosses, weapons, vehicles, mascots, or locations.
- Trade dress that makes the image look official.
- Text such as “official”, “licensed”, “approved”, or anything implying endorsement.
- Watermarks.

## High-risk franchise handling

For high-risk games/franchises, use the same approved fan-art direction but push designs further away from official specifics:

- no exact character likenesses
- no exact mascot silhouettes
- no official logo colors/letterforms
- no official box-art composition
- no direct creature/team/class replication

Example: for a monster-catching RPG, it is acceptable to show an original trainer-like adventurer and original companion creatures, but not specific official monsters, balls, logos, sprites, maps, or UI.

## Text rule

Preferred:

- No text inside icon assets.
- Optional plain title text in thumbnails/heroes if composition supports it.
- Optional small disclaimer text: `Unofficial Fan Art`.

Avoid:

- official logo recreation
- stylized title marks
- crowded text

## Standard prompt scaffold

```text
Create unofficial fan-made launcher artwork for "<display_name>".
Make it feel relevant to the game through genre, mood, world, gameplay motifs, and platform-era energy.
Use original designs only: original characters, creatures, objects, vehicles, enemies, and environments.
Do not copy official logos, screenshots, sprites, UI, maps, box art, typography, characters, creatures, or trade dress.
Do not imply official endorsement.
Asset type: <icon|thumbnail|hero>.
Output composition must fit <size>.
If text is included, use plain readable text only: "<display_name>" and optionally "Unofficial Fan Art".
```

## Manifest rule

Every generated image must be traceable in a manifest with:

- stable asset key
- game id
- display name
- asset type
- output path
- size
- prompt summary
- risk tier
- generation status
- review status

## Practical production rule

Generate in batches. Do not replace the whole catalog blindly without a manifest and review trail.
