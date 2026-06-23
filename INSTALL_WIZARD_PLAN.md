# INSTALL WIZARD PLAN — Multiworld Launcher

**Dato:** 2026-06-21  
**Status:** UDKAST — ingen kode skrives før planen er godkendt  
**Baseret på:** Research af alle plugins + gammeldt launcher (Diablo II Lod/) som reference

---

## Indholdsfortegnelse

1. [Wizard-type definitioner](#1-wizard-type-definitioner)
2. [Universelle arkitekturændringer](#2-universelle-arkitekturændringer)
3. [Auto-update per spil](#3-auto-update-per-spil)
4. [Per-spil kategorisering](#4-per-spil-kategorisering)
5. [Manglende GitHub-links (19 spil)](#5-manglende-github-links-19-spil)
6. [External bridge → intern C# bridge (17 spil)](#6-external-bridge--intern-c-bridge-17-spil)
7. [StarCraft2 og auto-detect fix](#7-starcraft2-og-auto-detect-fix)
8. [Implementeringsrækkefølge](#8-implementeringsrækkefølge)
9. [Åbne spørgsmål](#9-åbne-spørgsmål)

---

## 1. Wizard-type definitioner

Der er 9 wizard-typer. Alle ikke-web-spil bruger `Games/<GameId>/` som standard-installationsmappe (se sektion 2.1).

---

### TYPE 1: WEB (18 spil)
Ingen installation. Åbner browser direkte.

**Wizard-flow:** Ingen wizard. "Åbn i browser"-knap vises i stedet for "Installer".

**Spil:**
AirDelivery, Archipeladoku, Apquest, Autopelago, BKPicross, CandyBox2, Clique, Jigsaw,
MetroCUBEvania, NonogramAP, Paint, SimonTathamPuzzles, Spinball, StickRanger,
TheForgedCurse, WateryWords, Wordipelago, YachtDice

---

### TYPE 2: EMULATED / ROM (~110 spil)
ROM-spil der kræver brugerens egen ROM-fil. `EmulatorPlugin`-basisklassen håndterer
BizHawk-download, ROM-kopiering og AP-bridge via Lua named pipes.

**Wizard-flow:**
1. Vælg installationsmappe (standard: `Games/<GameId>/` ved siden af launcher .exe)
2. Brugeren angiver sti til vanilla ROM-fil (original røres aldrig)
3. ROM kopieres automatisk til `Games/ROMs/<GameId>/`
4. Download BizHawk automatisk (pinned version fra TASEmulators/BizHawk)
5. Download `.apworld`-fil fra AP repository
6. Tilføj spil til bibliotek

**Tre ROM-undertyper:**

**2A — APProcedurePatch:** Launcher patcher ROM automatisk ved session-start.
AP-generatoren leverer en `.ap<game>`-patch-fil; `SnesApPatchHelper.ApplyAsync()` (via
`apply_appatch.py` + bsdiff4) producerer en session-specifik patchet ROM. Originalen
bevares uberørt.

Spil (undertype 2A):
| Spil | System | Patch-extension |
|------|--------|----------------|
| Super Metroid | SNES | .apsm |
| Super Mario World | SNES | .apsmw |
| Pokémon Emerald | GBA | .apemerald (kræver Python 3) |
| Pokémon Red / Blue | GB | .apred / .apblue |
| Pokémon Crystal | GBC | .apcrystal |
| Final Fantasy VI | SNES | .apff6wc |
| Final Fantasy IV | SNES | .apff4 |
| Mega Man X | SNES | .apmmx |
| Mega Man 2 | NES | .apmm2 |
| Link's Awakening DX | GBC | .apladx |

**2B — Ekstern randomizer:** Spiller producerer selv den randomiserede ROM via eksternt
tool. Launcher starter spillet som-er, ingen patch-trin.

| Spil | System | Eksternt tool |
|------|--------|---------------|
| Ocarina of Time | N64 | OoT-AP-Client (i AP installation) |
| Donkey Kong 64 | N64 | dev.dk64randomizer.com |
| Final Fantasy 1 | NES | finalfantasyrandomizer.com |

**2C — Manuel stub:** Ingen automatisering. Viser trin-for-trin guide til brugeren.

| Spil | Status |
|------|--------|
| Panel de Pon | Brugeren patcher manuelt fra AgStarRay/TetrisAttackAP (GitHub) |

---

### TYPE 3: BEPINEX STEAM (~80 spil)
Steam-spil modificeret med BepInEx mod-loader.

**Forudsætninger:** Steam installeret; spillet ejet og installeret i Steam.

**Wizard-flow:**
1. Tjek Steam registry (HKCU\Software\Valve\Steam → SteamPath)
2. Find spil via VDF-parsing (libraryfolders.vdf → appmanifest_{appid}.acf)
3. Valider at spil-exe eksisterer
4. Download BepInEx til spil-mappen (eller Thunderstore-pakke)
5. Download AP-mod (udpak til BepInEx/plugins/)
6. Download `.apworld`
7. Tilføj til bibliotek

Ekstra for Thunderstore-spil (Risk of Rain 2, Inscryption, m.fl.):
Hent manifest fra Thunderstore API → download alle afhængigheder.

---

### TYPE 4: MELONLOADER STEAM (~10 spil)
Som TYPE 3 men med MelonLoader i stedet for BepInEx.

**Ekstra trin:** Download og kør MelonLoader installer (interaktiv opsætning).

Spil: Vampire Survivors, (øvrige bekræftes)

---

### TYPE 5: EVEREST — CELESTE (1 spil)
**Steam AppID:** 504230

**Wizard-flow:**
1. Find Celeste via Steam VDF (se TYPE 3 trin 1-3)
2. Download Olympus (Everest-installer) fra EverestAPI/Everest
3. Guide brugeren igennem Olympus-installation (interaktiv)
4. Download AP-mod via Lumafly eller direkte zip
5. Download `.apworld`
6. Tilføj til bibliotek

---

### TYPE 6: SMAPI — STARDEW VALLEY (1 spil)
**Steam AppID:** 413150

**Wizard-flow:**
1. Find Stardew Valley via Steam VDF
2. Download SMAPI wizard installer (Pathoschild/SMAPI)
3. Guide brugeren til at køre SMAPI-installeren (interaktiv — kan IKKE silent-installeres)
4. Download StardewArchipelago mod (agilbert1412/StardewArchipelago) → Mods/-mappe
5. Download `.apworld`
6. Tilføj til bibliotek

---

### TYPE 7: TMODLOADER — TERRARIA (1 spil)
**Steam AppID:** 105600 (Terraria) + 1281930 (tModLoader)

**Wizard-flow:**
1. Find Terraria via Steam VDF
2. Tjek om tModLoader (Steam App 1281930) er installeret
3. Hvis ej: guide bruger til at installere tModLoader fra Steam
4. Mod downloades via Steam Workshop subscription (seldom-SE/archipelago_terraria_client)
5. Launch point = tModLoader.exe (IKKE Terraria.exe)
6. Download `.apworld`
7. Tilføj til bibliotek

---

### TYPE 8: STANDALONE (~15 spil)
Downloades direkte fra GitHub — ingen Steam nødvendig.

**Wizard-flow:**
1. Vælg installationsmappe (standard: `Games/<GameId>/` ved siden af launcher .exe)
2. Find seneste GitHub-release via HEAD redirect (CDN, ingen API-rate-limit):
   `HEAD https://github.com/<owner>/<repo>/releases/latest` → 302 → tag
3. Download release-zip fra CDN
4. Udpak til installationsmappe
5. Download `.apworld`
6. Gem installeret version (til auto-update tjek)
7. Tilføj til bibliotek

**Spil:**

| Spil | GitHub | Specielle krav | ConnectsItself |
|------|--------|----------------|----------------|
| Diablo II | (eget repo) | Originale Blizzard MPQ-filer (se neden for) | **FALSE** (eneste!) |
| AM2R | AM2R-Community-Development/AM2R-Archipelago | Fan game — frit tilgængeligt | true |
| Aquaria | TBD | | true |
| Cave Story+ | TBD | | true |
| Duke Nukem 3D | TBD | eduke32-baseret | true |
| GZDoom | TBD (mangler GitHub-link) | Doom II / Heretic WAD-filer kræves | true |
| Doom II | TBD | Doom II IWAD kræves (købt) | true |
| Heretic | TBD | Heretic IWAD kræves (købt) | true |
| Faxanadu | TBD | | true |
| SWE1R | TBD | Star Wars Ep.1 Racer game assets kræves | true |
| OpenTTD | TBD | | true |

**Særtilfælde — Diablo II:**
`game_package.zip` indeholder kun AP-modifikationsfilerne. De originale Blizzard MPQ-filer
skal brugeren selv eje (købt spil). Wizard INSTALLERER ALDRIG oven på brugerens
eksisterende Blizzard-installation.

Udvidet wizard-flow for D2:
1. Vælg installationsmappe (standard: `Games/DiabloII/` — IKKE Desktop!)
2. Download game_package.zip fra eget GitHub repo (CDN HEAD redirect)
3. Udpak mod-filer til installationsmappe
4. Prompt: "Angiv sti til mappe med originale Diablo II MPQ-filer (LOD_DATA.MPQ m.fl.)"
5. Valider at MPQ-filerne er til stede (check filnavne/størrelse)
6. Kopiér MPQ-filerne til installationsmappe (eller symlink — TBD)
7. Download `.apworld` (diablo2_archipelago.apworld)
8. Tilføj til bibliotek

Launch-guard: Hvis MPQ-filer mangler ved launch → vis fejlbesked med knap til at
angive dem på ny.

---

### TYPE 9: SPECIELLE TILFÆLDE (7 spil)
Spil med unikke opsætningskrav der ikke passer i øvrige kategorier.

---

**StarCraft2**
SC2 er gratis fra Blizzard (battle.net). Kræver Archipelago installeret med bundlet
ArchipelagoStarcraft2Client.exe.

Wizard-flow:
1. Tjek om SC2 er installeret (Battle.net registry / SC2.exe)
   - Hvis nej: guide til gratis download fra blizzard.com
2. Tjek om Archipelago er installeret (med SC2-client)
   - Hvis nej: guide til download fra archipelago.gg
3. Aflevér AP-maps automatisk via `/download_data` kommando i SC2-client
4. Tilføj til bibliotek

**BUG (se sektion 7):** IsInstalled checker kun for client exe, IKKE SC2 selv.

---

**Minecraft**
Kræver Minecraft Java Edition (købt + installeret af bruger) + Java 21+.

Wizard-flow:
1. Tjek Java 21+ er installeret (`java -version`)
2. Tjek Minecraft Java Edition er ejet (Launcher registry / .minecraft-mappe)
3. Download NeoForge installer (qixils/NeoForgeAP)
4. Kør NeoForge installer → server jar installeres i `Games/Minecraft/server/`
5. Download AP-mod jar → `Games/Minecraft/server/mods/`
6. Download `.apworld`
7. Placer `.apmc` seed-fil i APData/-mappe (brugeren henter fra AP-generator)
8. Tilføj til bibliotek

Launch: Launcher starter lokal NeoForge server. Bruger forbinder med Minecraft client til
`localhost:25565`.

---

**Factorio**
Kræver Factorio (købt — Steam App 427520 eller standalone).

Wizard-flow:
1. Find Factorio via Steam VDF ELLER brugerdefineret sti
2. Tjek ArchipelagoFactorioClient.exe er til stede (bundlet i AP)
3. Guide brugeren til at konfigurere `host.yaml` med sti til Factorio.exe
4. Anbefal separat isoleret Factorio-installation (AP-klienten overtager kontrol)
5. Tilføj til bibliotek

---

**Kingdom Hearts 1**
Kræver KH HD 1.5+2.5 ReMIX (Steam 2552430 eller Epic Games Store).

Wizard-flow:
1. Find KH1 via Steam VDF eller Epic registry
2. Download OpenKH Mods Manager (OpenKH/OpenKh) og guide installationen
3. Download KH1FM Randomizer software (gaithern/KH1FM-RANDOMIZER) → `Games/KH1FMRandomizer/`
4. Guide opsætning af OpenKH Mods Manager til at pege på KH1-installation
5. Tjek KH1 Client er tilgængeligt i AP-installation (gaithernOrg/KH1FM-AP)
6. Download `.apworld`
7. Tilføj til bibliotek

---

**Kingdom Hearts 2**
Kræver KH HD 1.5+2.5 ReMIX (samme som KH1).

Wizard-flow:
1. Find KH2 via Steam VDF eller Epic
2. Download KH2 Randomizer (tommadness/KH2Randomizer) → `Games/KH2Randomizer/`
3. Download og installer OpenKH Mod Manager + Panacea + GoA ROM + APCompanion + KH2-ArchipelagoEnablers
4. Seeds genereres IGENNEM Archipelago (IKKE standalone randomizer)
5. KH2 Client bundlet i AP håndterer AP-forbindelsen (hooks game memory)
6. Download `.apworld`
7. Tilføj til bibliotek

---

**Skyward Sword**
Kræver nordamerikansk Wii ISO (Nintendo, ejet af bruger — SOUE disc-prefix).

Wizard-flow:
1. Download SS AP Patcher (Battlecats59/SS_APWorld) → `Games/SkywardSword/`
2. Tjek Dolphin-emulator er installeret (eller download)
3. Prompt: Angiv sti til nordamerikansk Wii ISO (validates "SOUE"-prefix)
4. SS AP Patcher genererer randomiseret ISO ved session-start
5. Skyward Sword Client (bundlet i AP) hooks Dolphin memory og håndterer AP
6. Download `.apworld`
7. Tilføj til bibliotek

---

**Twilight Princess**
Kræver nordamerikansk GCN eller Wii ISO (Nintendo, ejet af bruger — RZDE prefix).

Wizard-flow:
1. Download TP Randomizer (WritingHusky/Twilight_Princess_apworld) → `Games/TwilightPrincess/`
2. Tjek Dolphin-emulator er installeret (eller download)
3. Prompt: Angiv sti til nordamerikansk GCN/Wii ISO (validates "RZDE"-prefix)
4. TP Randomizer genererer patchet GCI save-fil
5. Twilight Princess Client (bundlet i AP) hooks Dolphin og håndterer AP
6. Download `.apworld`
7. Tilføj til bibliotek

---

## 2. Universelle arkitekturændringer

### 2.1 Standard installationsmappe (KRITISK — alle spil)

**Nuværende fejl:** `DefaultD2Path()` i `Core/SettingsStore.cs` peger på Desktop.

**Korrekt:** Alle spil installerer til `Games/<GameId>/` PLACERET VED SIDEN AF launcher .exe.

```
MultiWorldLauncher.exe
Games/
  DiabloII/          ← Standalone spil
  AM2R/
  Minecraft/
  ...
ROMs/                ← ROM-spil (kopieres af EmulatorPlugin)
  PokemonEmerald/
  SuperMetroid/
  ...
```

Implementering i `Core/SettingsStore.cs`:
```csharp
public static string DefaultGamePath(string gameId)
    => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Games", gameId);
```

Alle plugins' `GameDirectory`-property skal som default bruge denne metode.

### 2.2 Fjern auto-tilføj til bibliotek (KRITISK)

**Nuværende fejl i `App.xaml.cs`:** Alle `IsInstalled && !IsWebBased` spil tilføjes
automatisk til biblioteket ved opstart.

**Fix:** Fjern blokken helt. Spil tilføjes KUN til biblioteket når brugeren gennemfører
install wizard.

```csharp
// FJERNES — denne blok må ikke eksistere:
foreach (var plugin in GameRegistry.All)
{
    if (plugin.IsInstalled && !plugin.IsWebBased)
        _library.Add(plugin.GameId);
}
```

### 2.3 `IsInstalled` bruges kun til UI-tilstand

`IsInstalled` på et plugin bruges fremover KUN til at vise korrekt tilstand på
install-knappen (Installer / Opdater / Afinstaller). Det bruges IKKE til at populere
biblioteket.

---

## 3. Auto-update per spil

Marco's krav: Hvert spil skal tjekke om der er en ny version på GitHub og tilbyde opdatering
som et valg (ikke tvungen).

**Alle downloadede spil (TYPE 8 Standalone + TYPE 9 specielle) implementerer:**

```
LaunchAsync() / ved app-start → FetchLatestTagAsync() → sammenlign med gemt tag
→ hvis nyere: vis "Opdatering tilgængelig: v1.2.3 → v1.3.0" banner med knap
→ bruger vælger "Opdater nu" eller "Ikke nu"
→ ved opdatering: kør InstallOrUpdateAsync() med manifest-baseret diff (kun ændrede filer)
```

D2-plugin har allerede dette via `FetchLatestTagAsync()` + `InstallFromManifestAsync()`.
Dette mønster copy-pastes til alle standalone-spil.

**ROM-spil (TYPE 2):** BizHawk-versionen tjekkes separat (pinned version tag).
APProcedurePatch-filer kommer fra AP-generatoren ved session-start — ingen separat update.

**Steam-spil (TYPE 3-7):** Steam håndterer game updates. AP-mod-opdateringer tjekkes via
GitHub (mod-repoets releases).

**`.apworld`-filer:** Alle typer spil tjekker om `.apworld` er opdateret fra AP-repositoriet.

---

## 4. Per-spil kategorisering

### 4.1 Web-spil (TYPE 1) — 18 spil
Ingen wizard. Se sektion 1, TYPE 1.

### 4.2 Emulerede / ROM-spil (TYPE 2) — ~110 spil
EmulatorPlugin-basisklassen bruges. Primær emulator: BizHawk (automatisk download).

Nøgle-spil dokumenteret af research-agent:

| Spil | System | Undertype | Emulator | Checks virker? |
|------|--------|-----------|----------|----------------|
| Super Metroid | SNES | 2A | BizHawk/snes9x/Mesen | Afventer server data |
| Super Mario World | SNES | 2A | BizHawk/snes9x/Mesen | ✅ |
| Pokémon Emerald | GBA | 2A (Python 3!) | BizHawk/mGBA/Mesen | ✅ |
| Pokémon Red/Blue | GB | 2A | BizHawk/mGBA/Mesen | ✅ |
| Pokémon Crystal | GBC | 2A | BizHawk/mGBA/Mesen | ✅ |
| Final Fantasy VI | SNES | 2A | BizHawk/snes9x/Mesen | ✅ |
| Final Fantasy IV | SNES | 2A | BizHawk/snes9x/Mesen | ? |
| Mega Man X | SNES | 2A | BizHawk/snes9x/Mesen | ✅ |
| Mega Man 2 | NES | 2A | BizHawk/Mesen | ? |
| Link's Awakening DX | GBC | 2A | BizHawk/mGBA/Mesen | ✅ |
| Ocarina of Time | N64 | 2B | BizHawk only | ✅ |
| Donkey Kong 64 | N64 | 2B | BizHawk only | ✅ |
| Final Fantasy 1 | NES | 2B (FFR) | BizHawk/Mesen | ✅ |
| Mario Kart 64 | N64 | 2A? | BizHawk only | ? |
| Panel de Pon | SNES | 2C Manuel | BizHawk (manuelt) | ? |
| + ~95 øvrige | GBA/SNES/GB/NES/N64... | 2A/2B | BizHawk primær | varierer |

**Python 3 krav (Pokémon Emerald):** Launcher prober for `py -3`, derefter `python`.
Hvis ikke fundet → advarsel vises + vanilla ROM bruges (ingen AP-checks).

### 4.3 BepInEx Steam-spil (TYPE 3) — ~80 spil

Nøgle-spil (bekræftet af research-agent):

| Spil | Steam AppID | GitHub (mod) | Loader |
|------|-------------|--------------|--------|
| Hollow Knight | 367520 | ArchipelagoMW-HollowKnight/Archipelago.HollowKnight | Lumafly |
| Risk of Rain 2 | 632360 | Sneaki/Archipelago | Thunderstore |
| Subnautica | 264710 | Berserker66/ArchipelagoSubnauticaModSrc | Bundlet i zip |
| Inscryption | 1092790 | Ballin_Inc/ArchipelagoMod | Thunderstore |
| Cuphead | 268910 | JKLeckr/CupheadArchipelagoMod | BepInEx 5.4.23.2 (pinned!) |
| Noita | 881100 | DaftBrit/NoitaArchipelago | Native mod-system |
| Deep Rock Galactic | 548430 | Cousinit117/Deep-Rock-Galactic-AP | mint mod loader |
| Spelunky 2 | 418530 | DDR-Khat/Spelunky2-Archipelago | Playlunky |
| Outer Wilds | 753640 | Ixrec/OuterWildsArchipelagoRandomizer | OWML |
| Balatro | ? | BurndiL/BalatroAP | Steamodded + Lovely Injector |
| CrossCode | ? | CodeTriangle/CCMultiworldRandomizer | CCLoader (JavaScript) |
| Hades | ? | NaixGames/Polycosmos | StyxScribe Python bridge |
| Slay the Spire | 646570 | cjmang/StS-AP-World | ModTheSpire (.jar) |
| Don't Starve Together | ? | DragonWolfLeo/Archipelago-DST | Lua Workshop + Python client |
| Pizza Tower | ? | BabyblueSheep/pizza-tower-ap | UndertaleModTool patch |
| + ~65 øvrige | varierer | varierer | BepInEx standard |

**Balatro** er det ENESTE spil der pre-skriver connection-config (APSettings.json) —
Lua-mod'en læser den ved profil-load. Alle andre spil kræver manuel connection-input.

### 4.4 MelonLoader Steam-spil (TYPE 4) — ~10 spil
- Vampire Survivors (bekræftet)
- Øvrige identificeres ved gennemgang af plugin-filer

### 4.5 Everest (TYPE 5)
- Celeste (Steam 504230) — EverestAPI/Everest

### 4.6 SMAPI (TYPE 6)
- Stardew Valley (Steam 413150) — agilbert1412/StardewArchipelago

### 4.7 tModLoader (TYPE 7)
- Terraria (Steam 105600) — seldom-SE/archipelago_terraria_client

### 4.8 Standalone (TYPE 8)
Se sektion 1, TYPE 8 for fuld liste.

### 4.9 Specielle tilfælde (TYPE 9)
StarCraft2, Minecraft, Factorio, KH1, KH2, Skyward Sword, Twilight Princess.
Se sektion 1, TYPE 9 for detaljer.

---

## 5. Manglende GitHub-links (19 spil)

Disse spil mangler GitHub-links i deres plugin-implementering og kan IKKE tilbyde
download eller auto-opdatering. De skal enten have links tilføjet eller skjules fra
launcher med en advarsel.

| Spil | Sandsynlig type | Handling |
|------|----------------|----------|
| Apquest | WEB | Find AP-hjemmeside-URL |
| Clique | WEB | Find AP-hjemmeside-URL |
| GuildWars2 | BepInEx/Special | Find GitHub-repo |
| GzDoom | STANDALONE | Find GitHub-repo (kræver også IWAD) |
| HexcellsInfinite | Steam/BepInEx | Find GitHub-repo |
| KeepTalking | Steam/Special | Find GitHub-repo |
| LegoBatman | ? | Find GitHub-repo |
| Lingo | Steam/Standalone | Find GitHub-repo |
| Lingo2 | Steam/Standalone | Find GitHub-repo |
| Osu | Steam/Special | Find GitHub-repo |
| Parkitect | Steam/BepInEx | Find GitHub-repo |
| RiskOfRain2 | BepInEx | Muligvis Sneaki/Archipelago — bekræft |
| Shapez2 | ? | Find GitHub-repo |
| SystemShock2 | STANDALONE | Find GitHub-repo |
| TcgCardShopSimulator | Steam | Find GitHub-repo |
| Tevi | Steam | Find GitHub-repo |
| Tyrian | ? | Find GitHub-repo |
| WestOfLoathing | ? | Find GitHub-repo |
| YachtDice | WEB | Er web-baseret — ikke relevant? |

---

## 6. External bridge → intern C# bridge (17 spil)

Disse spil bruger det gamle Python `ap_bridge.py`-mønster og kalder eksternt script.
De skal opdateres til at bruge launcherens interne `ApClient` C# bridge.

**Eneste korrekte bridge:** Launcherens `ApClient` — den bruges allerede af D2 (eneste
spil med `ConnectsItself=false`). Disse 17 spil har stadig ekstern Python-afhængighed.

| Spil | Type | Nuværende bridge | Prioritet |
|------|------|-----------------|-----------|
| AnUntitledStory | Steam/BepInEx | ap_bridge.py | Medium |
| DarkSoulsII | Steam/BepInEx | ap_bridge.py | Høj |
| GrimDawn | Steam/Special | ap_bridge.py | Høj |
| Hades | Steam (StyxScribe) | StyxScribe Python | Høj |
| HeroCore | ? | ap_bridge.py | Medium |
| HiFiRush | Steam/BepInEx | ap_bridge.py | Høj |
| Iji | STANDALONE | ap_bridge.py | Medium |
| IntoTheBreach | ? | ap_bridge.py | Medium |
| PizzaTower | Steam (custom patch) | ap_bridge.py | Høj |
| RiftWizard | ? | ap_bridge.py | Medium |
| SavingPrincess | ? | ap_bridge.py | Lav |
| SWE1R | STANDALONE | ap_bridge.py | Medium |
| SwRacer | ? | ap_bridge.py | Lav |
| UFO50 | Steam | ap_bridge.py | Høj |
| VoidStranger | ? | ap_bridge.py | Medium |
| Wargroove | Steam | ap_bridge.py | Medium |
| DontStarveTogether | Steam (Lua+Python) | Python IPC client | Høj |

**Note:** D2 er IKKE på denne liste. D2 har `ConnectsItself=false` og er allerede den
interne ApClient — det er den korrekte implementation. De 17 spil ovenfor har
`ConnectsItself=true` men kalder stadig eksternt Python. Det er inkonsistent og skal rettes.

**Implementering:** For hvert spil ændres plugin til at bruge den interne `ApClient`
(samme pattern som D2). `ap_bridge.py` fjernes som afhængighed.

---

## 7. StarCraft2 og auto-detect fix

### SC2 IsInstalled bug

**Problem:**
`SC2Plugin.IsInstalled` returnerer `true` blot fordi `ArchipelagoStarcraft2Client.exe`
eksisterer i Archipelago-installationen. Klienten er bundlet med alle AP-installationer,
så SC2 vises som "installeret" på ENHVER maskine med AP — selv hvis SC2 aldrig har
været installeret.

**Detektionslogik i dag (SC2Plugin.cs):**
```
Checks: %ProgramData%\Archipelago\ArchipelagoStarcraft2Client.exe
     OR %LocalAppData%\Archipelago\ArchipelagoStarcraft2Client.exe
     OR HKLM\SOFTWARE\...\Archipelago (InstallLocation)
```
SC2 selv tjekkes IKKE.

**Fix — Option A (anbefalet):**
Fjern auto-library-tilføjelse fra App.xaml.cs (sektion 2.2). SC2 tilføjes kun til
biblioteket når brugeren klikker "Installer" og wizard gennemføres. `IsInstalled`-værdien
er derefter irrelevant for library-population.

**Fix — Option B (dybere fix):**
Ret `SC2Plugin.IsInstalled` til at kræve BEGGE:
1. `ArchipelagoStarcraft2Client.exe` til stede
2. StarCraft II er installeret (Battle.net registry: `HKCU\Software\Blizzard Entertainment\Starcraft II` eller `SC2.exe` på kendte stier)

### Auto-add bug (alle spil)

Rod-årsag er `App.xaml.cs` startup-loop'en der auto-populerer biblioteket.
Fix: Fjern hele blokken (se sektion 2.2). Dette løser SC2-problemet og alle lignende
tilfælde på én gang.

---

## 8. Implementeringsrækkefølge

### Fase 1 — Kritiske arkitekturfix (ingen synlig wizard)
Disse ændringer er forudsætninger for alt andet.

1. **Fjern auto-add blok** fra `App.xaml.cs`
2. **Fix default installationsmappe** — tilføj `DefaultGamePath(gameId)` til `Core/SettingsStore.cs`
3. **Opdater D2Plugin** til at bruge `DefaultGamePath("DiabloII")` i stedet for `DefaultD2Path()`
4. (SC2-bug løses automatisk af punkt 1)

### Fase 2 — D2 install wizard (reference implementation)
D2 er allerede delvist implementeret og er det bedst kendte spil. Bruges som reference
for alle andre standalone-wizards.

5. D2 install wizard i MainWindow: mappe-valg, MPQ-validering, download, apworld
6. D2 launch guard: tjek MPQ-filer eksisterer inden launch; vis fejlbesked med reparations-knap
7. D2 auto-update banner

### Fase 3 — Generisk standalone wizard
Template fra D2 skaleres til alle TYPE 8 spil.

8. Generisk `StandaloneInstallWizard`-komponent (download zip → udpak → verify)
9. Auto-update-tjek for alle standalone-spil (HEAD redirect → sammenlign tag)
10. Anvend på: AM2R, Aquaria, Cave Story, OpenTTD, Duke Nukem 3D, Faxanadu, SWE1R

### Fase 4 — BepInEx Steam wizard
11. Genbrugelig Steam VDF-parser (hvis ikke allerede abstraheret)
12. BepInEx download + install-komponent
13. Mod-download + install (zip → plugins/)
14. Thunderstore integration (for RoR2, Inscryption m.fl.)
15. Auto-update-tjek for mod-releases

### Fase 5 — ROM/Emulated wizard
EmulatorPlugin har allerede meget — dette er primært UI.

16. ROM-fil picker UI i install wizard
17. BizHawk download-UI med progress
18. Patch-flow UI (undertype 2A)
19. Vejledning til ekstern randomizer (undertype 2B)
20. Fix installationsmappe til at bruge `Games/<GameId>/`

### Fase 6 — Specielle tilfælde
21. Minecraft wizard (Java-tjek, NeoForge server install)
22. SC2 wizard (Blizzard download guide + AP maps download)
23. Factorio wizard (host.yaml guide)
24. KH1/KH2 wizard (OpenKH guide)
25. Skyward Sword / Twilight Princess wizard (ISO-validering, patcher)

### Fase 7 — Bridge replacement (17 spil)
26. Prioriterede bridge replacements: DarkSoulsII, GrimDawn, HiFiRush, PizzaTower, UFO50
27. Medium prioritet: Hades, Wargroove, VoidStranger, DontStarveTogether
28. Lav prioritet: øvrige

### Fase 8 — Manglende GitHub-links
29. Find og indsæt GitHub-links for de 19 spil
30. Beslut visning for spil der IKKE har links endnu (skjul? eller vis med advarsel?)

---

## 9. Åbne spørgsmål

Disse kræver svar fra Marco inden implementering:

1. **ROM-spil scope:** Har vi fungerende Lua-scripts til ALLE ~110 emulerede spil, eller
   kun dem der eksplicit er bekræftet? Skal wizard vises for alle eller kun bekræftede?

2. **MelonLoader-spil:** Hvilke af de ~10 MelonLoader-spil er prioriterede? Kun Vampire
   Survivors bekræftet.

3. **Hades / StyxScribe:** StyxScribe er en særlig Python-bridge. Skal den erstattes med
   intern C# ApClient, eller er den for kompleks til at porte?

4. **GZDoom/Doom II/Heretic:** Disse kræver IWAD-filer som brugeren ejer (ligesom D2 og MPQ).
   Skal wizard bede om dem, som D2 wizard beder om MPQ? Og hvilke WAD-filer præcis?

5. **Panel de Pon:** Beholdes som manuel stub (guide-tekst), eller opgraderes til fuld wizard?

6. **Spil uden GitHub-links:** Skjules fra launcher indtil link er fundet? Eller vises de
   med en "ikke tilgængelig endnu"-advarsel?

7. **Don't Starve Together:** DST bruger en Lua Workshop mod + separat Python IPC client.
   Python client skal erstattes med intern bridge — det er et større arbejde. Prioritet?

8. **Installationsmappe for ROM-filer:** ROMs går i `Games/ROMs/<GameId>/` eller
   `Games/<GameId>/ROMs/`? EmulatorPlugin bruger `Games/ROMs/<GameId>/` i dag.

9. **Symlinks vs. kopier for D2 MPQ-filer:** Brugeren kan have store MPQ-filer (flere GB).
   Skal vi kopiere dem (bruger dobbelt diskplads) eller symlinke (kræver admin)?

---

*Oprettet: 2026-06-21*
*Research-basis: 4 subagents der gennemgik ~50 plugin-filer + EmulatorPlugin basisklasse*
*Næste skridt: Marco godkender planen → Fase 1 implementering starter*
