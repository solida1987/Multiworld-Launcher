# Fan-art asset policy for Launcher V2

Date: 2026-06-13
Project: Diablo II Archipelago Launcher V2.0.0
Scope: generated launcher artwork for the game catalog.

This is an engineering risk document, not legal advice. For commercial release, distribution through stores, or disputed IP, get legal review.

## Project inventory

Catalog file: CatalogRepo/catalog.json

- Games in catalog: 381
- Required asset types per game: 3
- Total target assets if every game gets all types: 1143
- Existing non-generic root icons found: 5
- Existing thumbnails found: 29
- Existing hero images found: 5
- Existing generated/handmade assets found across these three types: 39
- Approximate missing assets if full coverage is required: 1104
- Catalog entries with 	humbnail_url already set: 5

Known launcher asset types from project TODOs and current folders:

| Type | Path pattern | Size noted in project | Use |
|---|---|---:|---|
| Icon | Assets/<id>.png | 256x256 | Small game/icon identity |
| Thumbnail | Assets/Thumbs/<id>_thumb.png | 488x256 | Browse/catalog card |
| Hero | Assets/Heroes/<id>_hero.png | 1380x280 | Game detail/header background |

Status distribution:
- available: 363
- discord_only: 15
- coming_soon: 3

Top category distribution:
- PC Game: 56
- Platformer: 48
- Action RPG: 44
- RPG: 41
- Console Game: 33
- Shooter: 24
- Strategy: 21
- Puzzle: 20
- Simulation: 20
- Adventure: 18
- Action: 15
- Indie: 12
- Racing: 7
- Hint Game: 7
- Rhythm: 7
- Roguelike: 5
- Multiplayer: 3


## Legal baseline

### 1. Fan art is not automatically safe

Fan art normally sits in a gray area. It may be original artwork, but if it closely uses protected characters, creatures, logos, screenshots, UI, box art, or distinctive visual expression from a game, it can still be treated as a derivative work or trademark use.

The U.S. Copyright Office explains that a derivative work is based on one or more existing works and that only the copyright owner has the right to authorize adaptations. It also states that unauthorized adaptations may constitute infringement.

Source: U.S. Copyright Office, Circular 14, Copyright in Derivative Works and Compilations: https://www.copyright.gov/circs/circ14.pdf

### 2. Fair use is case-by-case, not a blanket permission

Fair use evaluates four factors: purpose/character, nature of the work, amount/substantiality used, and market effect. Transformative, non-substitutive uses are generally better positioned, but there is no formula that guarantees safety.

Source: U.S. Copyright Office Fair Use Index: https://www.copyright.gov/fair-use/

Practical implication for this launcher: using generated art as catalog/promotional UI for hundreds of games is riskier than private fan art, because the images help present and distribute a product-like launcher experience. Even if the launcher is free, the use is still public-facing and repeated at scale.

### 3. Trademarks are a separate risk

Game titles can usually be used referentially to identify a game, but logos, stylized title treatments, publisher marks, and confusing presentation can create trademark risk. The safest pattern is plain-text title usage plus a visible unofficial/fan-made disclaimer.

Source: USPTO trademark basics: https://www.uspto.gov/trademarks/basics/strong-trademarks

### 4. AI generation does not remove IP risk

AI-generated output can still infringe if it is substantially similar to protected expression or uses protected marks. Also, the U.S. Copyright Office has taken the position that copyright protection for AI-assisted works depends on human authorship and case-specific human creative contribution.

Source: U.S. Copyright Office AI report: https://www.copyright.gov/ai/Copyright-and-Artificial-Intelligence-Part-2-Copyrightability-Report.pdf

## Publisher guideline examples

The catalog contains many publishers, so there is no single universal rule. Some publishers provide permissive fan-content rules, some limit use to screenshots/video sharing, and some are strict or silent.

### Nintendo / Pokémon / Zelda / Mario / Metroid risk

Nintendo's online video/image guidelines allow certain gameplay footage and screenshots on sharing platforms if rules are followed, but they explicitly say fan art and other uses outside gameplay screenshots/videos are outside those guidelines and subject to applicable law. They also prohibit selling images created using Nintendo Game Content and reserve takedown rights.

Source: Nintendo Game Content Guidelines: https://www.nintendo.co.jp/networkservice_guideline/en/index.html

Launcher implication: do not generate images that copy Nintendo characters, official logos, screenshots, sprites, UI, box art, or exact title treatments. Use genre/metaphor art plus plain text labels instead.

### Microsoft / Xbox-owned games

Microsoft's Game Content Usage Rules grant a revocable, noncommercial limited license for fan-created items based on Microsoft-published game content, with required attribution/disclaimer and restrictions against confusing titles, logos, offensive content, reverse engineering, and most monetization.

Source: Microsoft/Xbox Game Content Usage Rules: https://www.xbox.com/en-US/developers/rules

Launcher implication: Microsoft-owned game art may be possible under a rule-following, noncommercial approach, but logos should still be avoided and disclaimers should be present.

### Riot Games

Riot permits free community fan projects under a limited, revocable license, but prohibits commercial projects and specifically prohibits use of Riot IP in games/apps unless an exception applies. It also prohibits use of Riot logos/trademarks without written license.

Source: Riot Legal Jibber Jabber: https://www.riotgames.com/en/legal

Launcher implication: if any Riot game appears in the catalog, do not generate or ship Riot-character/brand artwork inside the launcher without separate review. Use neutral genre art or an opt-in policy.

### Minecraft / Mojang

Minecraft has its own usage guidelines and is not covered by Microsoft's general Xbox rules. It should be handled under the Minecraft-specific policy, not a generic Microsoft assumption.

Source: Minecraft Usage Guidelines: https://www.minecraft.net/en-us/usage-guidelines

Launcher implication: Minecraft should be reviewed separately before using recognizable block mobs, official logo styling, or marketplace-like presentation.

## Safe asset-generation policy

### Preferred safe style

Use "inspired-by genre representation," not direct reproduction.

Allowed prompt direction:

- Capture broad genre, mood, era, and gameplay concept.
- Use original compositions, original silhouettes, original UI-free artwork.
- Use plain text for game titles only where needed for identification.
- Use text like "Archipelago-ready", "Randomizer support", "ROM required", or platform/category metadata when factual.
- Add a small disclaimer elsewhere in the launcher, not necessarily inside every image.

Avoid in prompts:

- "in the style of [living artist]" or direct copying of official art style.
- Official logos, box art, screenshots, sprites, UI, maps, HUDs, exact character likenesses, mascots, monsters, weapons, ships, or copyrighted locations.
- Publisher/developer logos.
- Trade dress: exact title typography, exact color layouts from box art, exact menu compositions.
- Claims like "official", "licensed", "Nintendo approved", "Blizzard approved", etc.

### Text policy

Safe text:

- Plain game title as factual identification.
- Platform/category facts from catalog metadata.
- "Unofficial launcher art" if space allows.

Risky text:

- Official logo recreation.
- Stylized title marks that mimic the original logo.
- Publisher marks.
- Marketing claims that imply endorsement.

### Prompt template

Use this template per game:

`	ext
Create original unofficial launcher artwork for the game catalog entry: "<display_name>".
Represent the broad genre, mood, and gameplay concept without copying official screenshots, logos, box art, character designs, sprites, UI, maps, or protected trade dress.
Use a fresh original composition suitable for <icon/thumbnail/hero>.
Text, if any, must be plain readable text only: "<display_name>" plus factual metadata such as "<category>" or "ROM required".
Do not include publisher/developer logos. Do not imply official endorsement.
Style: polished original fan-inspired game-catalog illustration, not a replica of official art.
`

### Asset-specific art direction

Icon, Assets/<id>.png, 256x256:

- Strong symbolic object or abstract genre mark.
- No small text unless absolutely necessary.
- Must remain legible at small size.

Thumbnail, Assets/Thumbs/<id>_thumb.png, 488x256:

- One clear scene or emblem.
- Optional plain title text.
- Leave safe margins for rounded card clipping.

Hero, Assets/Heroes/<id>_hero.png, 1380x280:

- Wide atmospheric banner.
- Important subject should not be near the edges.
- Avoid dense detail; UI overlays may cover parts of the image.

## Risk tiers for this catalog

### Low risk

- Open-source games with permissive branding/content rules, after checking each project.
- Generic puzzle/hint/community tools where the art can be abstract and non-IP-specific.
- Games represented by broad genre concepts only, with plain title text.

### Medium risk

- Indie games with no clear fan-content policy.
- Art that evokes gameplay concepts but not characters/logos/screenshots.
- Catalog cards for games requiring users to own the original game/ROM.

### High risk

- Nintendo, Pokémon, Zelda, Mario, Metroid, Kirby, etc.
- Disney/Square Enix/Kingdom Hearts-type mixed IP.
- Blizzard, FromSoftware/Bandai Namco, Valve, Sony, Capcom, Sega, and other major-publisher IP where no specific permission has been verified.
- Any image that recreates characters, monsters, title logos, screenshots, official UI, box art, or recognizable locations.

## Required disclaimer recommendation

Add a global visible notice in the launcher About/Credits area and possibly catalog footer:

`	ext
This launcher is an unofficial community project. Game names are used only to identify compatible Archipelago entries. Generated catalog artwork is unofficial and not endorsed by, affiliated with, or sponsored by the original game publishers or rights holders.
`

For individual high-risk entries, prefer a per-game credits/disclaimer note when practical.

## Production recommendation

Do not generate "looks exactly like the game" assets for all 381 games. That goal is legally and practically unsafe.

Use a safer target:

1. Generate three original, non-replicating assets per catalog entry.
2. Use game title and metadata for identification, not official logos.
3. Avoid protected characters and screenshot-like imagery.
4. For high-risk publishers/franchises, generate abstract/genre art rather than fan-character art.
5. Keep a manifest recording prompt, date, source metadata, and risk tier for every generated asset.
6. Add a review gate before replacing all existing assets in the live catalog.

## Next implementation plan

If proceeding, the next safe engineering step is to generate an asset manifest first, not images:

- id
- display_name
- category
- platforms
- equires_rom
- 	humbnail_url
- target icon path
- target thumbnail path
- target hero path
- risk tier
- prompt family
- generation status
- review status

This lets generation run in batches while preserving traceability and avoiding accidental high-risk prompt wording.
