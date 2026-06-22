# Per-Game Install Audit (autonomous loop)

Goal: go through **every** plugin one at a time, determine the **correct** install
method from that game's own AP/GitHub setup documentation, make the launcher's
install wizard do it correctly (or document the exact manual steps where it can't
be automated), then move to the next game.

## Procedure (per game — followed every time)
1. Read `Plugins/<Game>/<Game>Plugin.cs` — header comment (usually documents the
   verified setup method), `GameDirectory`, `InstallOrUpdateAsync`, settings panel.
2. Decide the CORRECT method:
   - **own-download** — launcher downloads the whole game/mod into `Games/<gameId>`
     (freeware full games, self-contained mods).
   - **own-copy** — download mod into `Games/<gameId>` AND copy needed files from
     the user's original install (D2-class; rare).
   - **on-top** — mod is installed INTO the user's existing game folder (Steam
     BepInEx / MelonLoader etc.) — the correct documented method for those.
   - **rom** — emulator + user-supplied ROM (Emulated/* base pattern).
   - **manual** — launcher opens the setup guide / releases page; user installs.
   - **web** — browser game, nothing to install.
3. Verify the code matches; FIX it if wrong. Where it can't be automated, make the
   Settings panel state the exact manual steps clearly.
4. Build after each fix (or batch of fixes). Record the result below. Move on.

## Status legend
- `[x]` done. Tag: `OK` (already correct), `FIX` (code changed), `DOC` (manual steps
  documented), `WEB`/`ROM` (trivial category, verified).

## PROGRESS
- Completed: 264 / 264 — ✅ AUDIT COMPLETE (every checklist line audited)
- Next: — (done; see SUMMARY below)
- Note: the single "Emulated" line is a CATEGORY covering ~100 ROM games via the
  shared EmulatorPlugin base (audited as one unit), so the launcher actually ships
  ~360+ game plugins total across these 264 audit lines.

## SUMMARY OF FINDINGS (2026-06-22)
Overwhelming result: the codebase is in excellent shape. Almost every plugin already
implements its game's OWN documented install method, with detailed honest headers and
no fabricated one-click installs. Verdict distribution: the vast majority are **OK**
(correct + honest). Only ONE install was genuinely broken (D2 — fixed + published as
v2.1.1 this session). The rest of the flags are NOTES (enhancement candidates or
unverified-placeholder details), not breakage.

### Genuine issues / action queue
1. **D2 — FIXED + published (v2.1.1)** — reworked to separate Games/diablo2_archipelago
   folder + copy user's original MPQs + overlay mod; original never modified.
2. **DUPLICATE game: SWE1R ≡ SwRacer** — both register the SAME game (Star Wars
   Episode I: Racer, wcolding apworld+client, Steam 808910) under different GameIds
   ("star_wars_ep1_racer" vs "star_wars_episode_i_racer"). One must be removed
   (delete plugin + App.xaml.cs registration + catalog entry + build-verify).
   (Also flagged in [[project-pending-tasks]].)

### Cosmetic only (not bugs)
- **Hades2** — appid is ALREADY correct (H2_STEAM_APP_ID="1145350"); the header
  comment + 2 settings-UI strings still say "(UNVERIFIED)" about the appid, which is
  now misleading since it was verified/corrected. Optional: strip the stale UNVERIFIED
  labels. Functionally fine.

### Web-verify queue (UNVERIFIED placeholders flagged in-code; recipe shape is correct)
- ClairObscur (appid/exe/folder/game-string), DoronkoWanko (placeholder mod repo),
  FlipWitch (appid/game-string/folder), Hacknet (game-string),
  RE2R (game-string), VampireSurvivors (appid 1794680), Wargroove2 (game-string),
  Signalis (soft appid note), ZeldaMajorasMask (soft game-string note).

### Auto-download enhancement candidates (manual-download standalones — NOT broken,
### work as documented; could auto-download into Games/<id> like AM2R/Celeste64/HighRoller)
- APSudoku, BKPicross, ChecksMate, CornKidz64, CrosswordAP, DeliveringHOPE, Dracomino,
  FridayNightFunkin, Frogmonster, Glyphs, Lingo2, Loonyland, OurAscent, VoidSols,
  KeymastersKeep (framework), LeagueOfLegends (LoLAP companion). Optional polish.

### Honest stubs (documented recipe; in-launcher bridge pending — rely on community
### client / external emulator meanwhile; same graceful pattern as Emulated non-BizHawk)
- Emulated non-BizHawk consoles (GameCube/Wii→Dolphin, PS1/PS2→DuckStation/PCSX2),
  PokemonMysteryDungeonEoS (BizHawk Lua bridge pending), ZeldaALinkBetweenWorlds (Citra).

### Fixes already applied this session (wrong update-check repos → correct MOD repos)
- HatsuneMikuDiva, MomodoraMonlitFarewell, Webfishing.

## Results
<!-- one line per game: - [x] GameName — METHOD — tag — note -->

## Checklist (264)
- [x] ADanceOfFireAndIce — on-top (Steam BepInEx) — OK — auto-installs BepInEx 5.4.22 + ADOFAI_AP-Mod into the Steam game folder; correct per README. No change.
- [x] ADifficultGameAboutClimbing — on-top (Steam BepInEx) — OK — extracts GrabbingChecks into <Game>/BepInEx/plugins/, connection prefill, guided BepInEx prereq + presence check. Correct. (Could auto-stage BepInEx like ADOFAI for nicer UX — optional.)
- [x] AgainstTheStorm — on-top (Steam Thunderstore mod) — OK — stages mod DLL into <ATS>/BepInEx/plugins; honestly documents BepInEx+ATS_API deps via Thunderstore Mod Manager; in-game console connect. Correct.
- [x] AgeOfEmpires2DE — manual/guided + external client — OK — detects Steam install + AoE2 user folder + bundled AP client; launches client+game; guides Workshop mod + apworld + scenario. Never modifies Steam copy. Can't auto-install (Workshop/community apworld); correct.
- [x] AHatInTime — manual/guided (Steam Workshop-only) — OK — no fake download; opens Workshop page + setup guide with exact steps (backup, tcplink beta branch, subscribe, console), external AHIT Client connection. Honest + correct.
- [x] AirDelivery — manual download (PICO-8/web) — OK — opens releases page; user extracts + points launcher at folder; launch opens HTML in browser / .p8 in PICO-8. Documented. (Could auto-download the small HTML/cart asset into Games/air_delivery — minor.)
- [x] AM2R — own-download — OK — downloads the AP-integrated build zip from Ehseezed/Archipelago-Integration into Games/AM2R (or user folder); in-game connection. Correct (AP build is compiled-in). Note: ApWorldName "AM2R" flagged UNVERIFIED in-plugin.
- [x] AnimalWell — vanilla Steam + external client + apworld staging — OK — downloads animal_well.apworld to sidecar + opens folder for manual copy to custom_worlds (read-only); launches unmodified game. Correct + honest.
- [x] Anodyne — on-top (itch.io Mods folder) — OK — downloads SephDB mod zip + extracts into <AnodyneSharp>/Mods/; in-game connection. Correct.
- [x] AnotherCrabsTreasure — on-top (Steam BepInEx, auto) — OK — auto-installs BepInEx + ACTAP.dll into <ACT>/BepInEx/plugins, stages apworld; in-game overlay connect. Fully automated, correct.
- [x] AnUntitledStory — own-download — OK — self-contained patched build (zip) extracted to its own folder; ArchipelagoConnectionInfo.ini prefill (no password — mod limitation). Correct.
- [x] Apquest — web — OK — opens archipelago.gg APQuest page; bundled web game, nothing to install. Correct.
- [x] APSudoku — manual download (standalone app) — NOTE — opens releases page; expects exe at LocalAppData/MultiworldLauncher/APSudoku (non-standard, uncommunicated). Not broken (Play falls back to releases page) but ENHANCEMENT candidate: auto-download+extract into Games/apsudoku like AM2R.
- [x] Aquaria — own-copy — OK — copies the user's Aquaria into Games/Aquaria then overlays the tioui randomizer (built-in AP client); original install never modified. Correct D2-class handling, done right.
- [x] Archipeladoku — web — OK — opens galdiuz.github.io/archipeladoku (GitHub Pages); nothing to install. Correct.
- [x] ArchipelaGo — mobile/link-only — OK — Android APK; can't be PC-installed, honestly links to releases for phone install. Correct.
- [x] AShortHike — on-top (BrandenEK Modding API) — OK — best-effort zip drop into <game>/Modding/plugins/, leads user to Short Hike Mod Installer for the Modding-API prereq; in-game connect. Correct + honest.
- [x] Astalon — on-top (Steam BepInEx IL2CPP) — OK — best-effort mod-DLL install into BepInEx/plugins/Archipelago.Astalon; guided BepInEx (manual — IL2CPP needs game-run); Archipelago.cfg connection prefill. Correct + honest.
- [x] Autopelago — web — OK — opens autopelago.app with h/p/s/w URL prefill; hosted Angular SPA, nothing to install. Correct.
- [x] AxiomVerge — on-top patch (Steam) — OK — downloads AxiomVergeAP.zip + runs applyPatchFinal.bat into Steam install; guides secretworlds beta branch; stages apworld; in-game connect. Correct per documented patch method.
- [x] BabaIsYou — manual/guided (Steam Lua mod) — NOTE — InstallOrUpdate only prints instructions; user downloads Babapelago, levelpack self-installs on first AP connection; built-in Lua client. Functional but placement guidance vague; ENHANCEMENT: auto-download Babapelago / clearer steps (verify mod mechanism).
- [x] Balatro — on-top (Steam Lua mod) — OK — auto-installs BalatroAP into %AppData%/Balatro/Mods/, guides Lovely/Steamodded prereq, writes APSettings.json prefill; Steam launch. Correct + honest.
- [x] BindingOfIsaacRepentance — on-top (Steam Lua mod) — OK — installs into Documents/My Games/.../mods/, auto --luadebug, guides MCM Pure dep (Workshop), in-game MCM connect. Correct + honest.
- [x] BKPicross — manual download (standalone) — NOTE — needs AP TextClient (ConnectsItself=false); Browse picker + falls back to releases page. Functional; ENHANCEMENT: auto-download into Games/bk_picross.
- [x] Blasphemous — on-top (BrandenEK Modding API) — OK — best-effort zip drop into <game>/Modding/plugins/, leads user to Blasphemous Mod Installer for API + deps; in-game connect. Correct + honest.
- [x] Blasphemous2 — on-top (MelonLoader + BlasII.ModdingAPI) — OK — best-effort zip drop into <game>/Modding/plugins/, leads user to Blasphemous Mod Installer for MelonLoader+API+deps; in-game connect. Correct + honest.
- [x] BloonsTD6 — on-top (MelonLoader / BTD6 Mod Helper) — OK — GamingInfinite BloonsArchipelago + bloonstd6.apworld; in-game client (appid 960090). Correct + honest.
- [x] BombRushCyberfunk — on-top (Steam BepInEx mono) — OK — auto-stages BepInEx 5.4.22 x64 + ModLocalizer.dll + mod into BepInEx/plugins; run-once + in-game connect guided. Correct + honest.
- [x] Brotato — on-top (Godot ModLoader via Steam Workshop) — OK — copies mod zip UNEXTRACTED into placeholder Workshop folder (3369699033) + ap_config.json prefill; guides placeholder subscription. Correct + honest.
- [x] BuckshotRoulette — on-top (Godot ModLoader, custom GML) — OK — downloads custom GML addons into game root, drops APBuckshot zip unextracted into /mods, guides run-once --script setup; in-game connect. Correct + honest.
- [x] BumperStickers — own-download — OK — self-contained AP build (single GitHub release zip) into its own folder; in-game connect. Clean. Correct.
- [x] CandyBox2 — web (community fork) — NOTE — launch opens releases page for the AP-enabled version (vicr123/Archipelago); thin guidance. Functional; could host clearer play link/steps.
- [x] CatQuest — manual/guided (Steam mod) — NOTE — no auto mod download; detects Steam install + launches + links to releases. Honest but unautomated; ENHANCEMENT: automate mod download if install method known.
- [x] CavernOfDreams — on-top (Steam BepInEx mono) — OK — mod into BepInEx/plugins; pre-writes savedSessions.json connection prefill. Correct + honest.
- [x] CavesOfQud — on-top (Qud C# mod) — OK — extracts mod zip into <CoQ>/Mods/; in-game connect. Correct.
- [x] CaveStory — external client + patcher — OK — Cave Story Client (in AP Launcher) patches freeware game + connects; launcher guides + launches AP Launcher. Correct + honest (can't auto-patch).
- [x] Celeste — on-top (Everest mod, Steam) — OK — guides Everest/Olympus (interactive), auto-downloads+extracts AP mod into <Celeste>/Mods/; in-game connect. Correct + honest.
- [x] Celeste64 — own-download — OK — free standalone AP build; writes AP.json connection prefill in install root. Clean. Correct.
- [x] CelesteOpenWorld — on-top (Everest mod, Steam) — OK — guides Everest/Olympus, auto-downloads mod into Mods/, pre-writes Everest modsettings YAML connection. Correct + honest.
- [x] ChainedEchoes — on-top (Steam BepInEx IL2CPP) — OK — auto-installs BepInEx 6 IL2CPP + CERandomizer.dll into BepInEx/plugins + RandomizerOptions.txt; connection prefill. Correct + honest.
- [x] ChecksFinder — own-download — OK — free standalone (single GitHub zip + launch); in-game "Play Online" connect. Correct.
- [x] ChecksMate — manual download (standalone) — NOTE — free standalone, user sets folder via Settings; ENHANCEMENT: auto-download into Games/checksmate. Functional.
- [x] ChooChooCharles — on-top (RE-UE4SS) — OK — downloads CCCharles_Random.zip + extracts Obscure/ folder into game root; in-game /connect console. Correct + honest.
- [x] Civ6 — on-top + external client — OK — stages mod into Documents/My Games/.../Mods (OneDrive-aware); bundled Civ6 Client (FireTuner) owns connection; guides R&F+GS DLCs. Correct + honest.
- [x] CivilizationV — on-top — OK — stages 1313e mod into Documents/My Games/.../MODS with built-in AP client; guides G&K+BNW DLCs + in-game enable. Correct + honest.
- [x] ClairObscur — on-top mod — NOTE — MODEL correct (on-top mod, Demorck/ClairObscur_APWorld) but appid/exe/folder/game-string/connection ALL flagged UNVERIFIED (new 2025 game). VERIFY against real game+mod before it works.
- [x] Clique — web/manual (Forgejo) — OK — opens pharware.com Forgejo releases page for the HTML build (can't auto-download from Forgejo). Correct + honest.
- [x] ClusterTruck — manual/guided (Steam mod) — NOTE — detects + launches + guides mod download/placement per README; no auto-install. Functional, unautomated. ENHANCEMENT candidate.
- [x] CobaltCore — on-top (Nickel mod loader) — OK — detects Steam install + Nickel folder, stages mod into <Nickel>/ModLibrary/, launches via Nickel.exe; Nickel separate prereq (guided); in-game connect. Correct + honest.
- [x] CornKidz64 — manual download (standalone) — NOTE — user sets folder; ENHANCEMENT: auto-download into Games/corn_kidz_64. [cluster: manual-download standalones]
- [x] CrossCode — on-top (CCLoader + .ccmod) — OK — installs CCLoader (zipball into game folder), places .ccmod into assets/mods/, guides in-game mod manager for deps; in-game connect. Correct + honest.
- [x] CrosswordAP — manual download (standalone) — NOTE — user sets folder; ENHANCEMENT: auto-download into Games/crossword_ap. [cluster: manual-download standalones]
- [x] CryptOfTheNecroDancer — on-top (Steam BepInEx Mono) — OK — mod zip into game root (BepInEx/plugins); guides Synchrony DLC + BepInEx; in-game connect. Correct + honest.
- [x] CrystalProject — on-top (Steam beta branch + installer) — OK — guides "archipelago" beta branch + CrystalProjectAPModInstaller + .NET 8 runtime (multi-step, honest). Correct.
- [x] Cuphead — on-top (Steam BepInEx Mono) — OK — best-effort stages BepInEx + mod into BepInEx/plugins; in-game C+Z connect. Correct + honest.
- [x] DarkSouls3 — on-top (Mod Engine 2 + setup tool) — OK — downloads+stages DS3.Archipelago package (archipelago.dll + DS3Randomizer); connection in setup tool GUI. Correct + honest.
- [x] DarkSoulsII — on-top (dinput8.dll proxy) — OK — drops dinput8.dll into game folder (SotFS 335300 / vanilla 236430 auto-select); console-window connection. Correct + honest.
- [x] DarkSoulsRemastered — external client (memory-attach) — OK — stages DSAP Avalonia client + dsr.apworld; guides offline mode + DSAP connect. Correct + honest.
- [x] DeathsDoor — on-top (BepInEx 5.4.22) — OK — installs 20250825_DD_plugins.zip snapshot into Steam game folder; in-game connection. Correct + honest.
- [x] DeepRockGalactic — on-top (pak via MINT) + external client — OK — pak mod via MINT sideloader + external Cousinit117 AP-client fork (text-file IPC). Honest external-client scope.
- [x] DeliveringHOPE — own-download (standalone) — NOTE — works as documented (manual download from GitHub releases + set path); auto-download enhancement candidate (verify asset+exe before coding). Not broken.
- [x] Deltarune — guided (own copy + patch) — OK — detects DELTARUNE 1.04, guides apworld + /auto_patch (DeltaruneClient owns slot), launches patched copy. Original not modified.
- [x] DevilMayCry3 — on-top (DLL injection) — OK — DMCHDLoader dinput8.dll chain-loader + dmc3_randomizer.dll into DMC HD Collection (631510); DDMK downgrade guided, not auto. Correct + honest.
- [x] DiabloII — own-copy (separate folder + copy original) — OK (FIXED v2.1.1) — installs to Games/diablo2_archipelago, copies user's original MPQs + overlays AP mod; original never modified. Published.
- [x] DiceyDungeons — on-top (apworld + mod arg) — OK — .apworld drop-in + bundled Python client patches game data, launches diceydungeons.exe mod=diceyap. Correct + honest.
- [x] DLCQuest — guided (BepInEx mod + installer, patches copy) — OK — DLCQuestipelago built-in client; launcher prefills ArchipelagoConnectionInfo.json for auto-connect. Correct + honest.
- [x] DomeKeeper — on-top (Steam Workshop mod) — OK — guides Workshop sub via steam://, downloads dome_keeper.apworld. Correct + honest.
- [x] DontStarveTogether — on-top (Steam Workshop mod) — OK — detects Steam install, downloads dontstarvetogether.apworld, guides Workshop sub. Correct + honest.
- [x] Doom — own-download engine + BYO-WAD — OK — downloads APDOOM (Crispy fork, prerelease-aware resolver + Daivuk fallback); user supplies DOOM.WAD; CLI-arg connect. Correct + honest.
- [x] DoomII — own-download engine + BYO-WAD — OK — same APDOOM engine, DOOM2.WAD + -game doom2. Correct + honest.
- [x] DoronkoWanko — on-top (Steam mod) — NOTE — Steam game (2168430) launches fine; AP mod repo is an UNVERIFIED placeholder (alwaysintreble/DoronkoWankoArchipelago, flagged in code). Recipe incomplete — verify real repo before relying on update-check.
- [x] Dracomino — own-download (standalone) — NOTE — guidance-only install + Browse-to-set-path (alwaysintreble/Dracomino); auto-download enhancement candidate. Not broken.
- [x] Dredge — on-top (Winch via DREDGE Mod Manager) — OK — honestly can't stage Winch mod; detects game + guides dredgemods.com; in-game connect. Correct + honest.
- [x] DukeNukem3D — own-download engine + BYO-GRP — OK — downloads Duke3DAP (rednukemAP.exe + scripts + apworld); user supplies duke3d.grp/DUKE.RTS; built-in launcher-UI connect. Correct + honest.
- [x] DungeonClawler — on-top (BepInEx 5/Harmony) — OK — installs Clawrchipelago.Full (BepInEx+mod) / Plugin zip for updates + apworld; in-game client. Correct + honest.
- [x] Emulated — rom_required CATEGORY (~100 games via EmulatorPlugin base) — OK — base class auto-installs BizHawk, copies user ROM to Games/ROMs/<id>/ (original NEVER modified, §11), Lua memory-bridge + named-pipe AP connection. BizHawk-family (SNES/GBA/NES/GB/N64) fully integrated; non-BizHawk (GCN/Wii→Dolphin, PS1/PS2→DuckStation/PCSX2) are honest STUBS (ChecksImplemented=false: register game + name correct emulator + BYO-ISO + setup guide, full bridge = future update). Correct recipe documented per console; no faked one-click.
- [x] EnderLilies — on-top (LiveSplit component) — OK — downloads enderlilies.apworld + Trexounay LiveSplit randomizer; memory-scan + shared-memory bridge on user's Steam game. Correct + honest.
- [x] EnterTheGungeon — on-top (BepInEx/MTG) — OK — ArchipelaGun mod; honestly guides r2modman/Thunderstore for unbundled deps (BepInEx+MtG_API+Alexandria); in-game weapon→menu connect. Correct + honest.
- [x] Everhood2 — on-top (MelonLoader) — OK — copies ArchipelagoEverhood.zip DLLs into Mods\, guides MelonLoader installer, pre-writes LastLogin.txt connection seed. Correct + honest.
- [x] Factorio — external-client (official AP-main) — OK — bundled Factorio Client owns slot, per-seed AP_*.zip mod into mods folder; honest guided setup (host.yaml executable path). Correct + honest.
- [x] Faxanadu — own-download custom emulator + BYO-ROM — OK — downloads Daivuk's DAXANADU NES emulator (built-in AP client); user supplies Faxanadu ROM; in-game ARCHIPELAGO menu connect. Correct + honest.
- [x] FF1PixelRemaster — guided (BepInEx 6 IL2CPP) — OK — correctly guide-only (IL2CPP can't be safely auto-staged); detects Steam (1173770), full documented recipe (BepInEx 6 + FF1PRAP.zip + ff1pr.apworld). Correct + honest.
- [x] FFXII — guided/external-client — OK — detects Steam FF12 Zodiac Age (594230); guides Bartz24 Open World randomizer + standard AP client. Correct + honest.
- [x] FinalFantasyXIITrialMode — rom (BizHawk PS2 core) + external client — OK — downloads ffxiitm.apworld + ffxii_tm_ap.lua, detects base game (595520/PS2 ISO); Python client holds slot. Correct + honest.
- [x] FlipWitch — on-top (BepInEx) — NOTE — FlipwitchAPClient mod shape correct, but appid (1748620), AP game-string, and Steam folder are UNVERIFIED placeholders (flagged in code). Verify before shipping. R18+ surfaced in settings.
- [x] FreedomPlanet2 — on-top (BepInEx 5 + FP2Lib) — OK — Knuxfan24 mod (Archipelago.7z + fp2.apworld) into BepInEx\plugins\; prefills K24_FP2_Archipelago.cfg connection. Correct + honest.
- [x] FridayNightFunkin — own-download (standalone) — NOTE — manual download (Archipelago-Games org) + set path; auto-download enhancement candidate. Not broken.
- [x] Frogmonster — own-download (standalone) — NOTE — manual download (alwaysintreble) + set path; auto-download enhancement candidate. Not broken.
- [x] GarfieldKart — on-top (BepInEx) — OK — FeluciaPS mod with built-in AP client into Steam game (1085510). Correct + honest.
- [x] GettingOverIt — on-top (BepInEx 5) — OK — CheckingOverIt mod zip + apworld into game root; prefills CheckingOverIt.cfg connection. Correct + honest.
- [x] Glyphs — own-download (standalone) — NOTE — manual download (alwaysintreble) + set path; auto-download enhancement candidate. Not broken.
- [x] GrimDawn — on-top (native Lua mod) — OK — patched lua51.dll + lua-apclientpp.dll via Grim Dawn native mods folder; honestly guides Nexus-Mods download + manual prereqs. Correct + honest.
- [x] GuildWars2 — guided external-client — OK — GW2 BYO via ArenaNet launcher (no auto-install possible); Feldar99/Gw2ArchipelagoClient overlay (GW2 API/Mumble) + connection guided; CDN version-check. Correct + honest.
- [x] GzDoom — guided (own-download-capable) — OK — GZDoom engine + ToxicFrog/doom-mods AP integration + BYO-WADs guided; CDN version-check. Recipe correct; minor auto-download enhancement candidate (engine+mod freely downloadable like APDOOM).
- [x] Hacknet — on-top (Extension drop-in) — NOTE — HacknetAP Extension mechanism correct (appid 365450 verified), but AP game-string is UNVERIFIED (flagged in code). Verify before shipping.
- [x] Hades — on-top (SGG Lua mod) — OK — downloads PolycosmosInstaller + deps into Content/Mods; StyxScribe Python bridge + HadesClient; hades.apworld. Correct + honest.
- [x] Hades2 — on-top (mod) — OK (appid corrected) — actual constant H2_STEAM_APP_ID="1145350" (line 102, "the two were swapped; corrected here") — NO collision with Hades 1 (1145360). Only stale "UNVERIFIED" text remains in the header comment + 2 UI strings (cosmetic; appid is verified). Folder/exe/game-string have safe fallbacks. JFrog-55 H2_Archipelago mod. Correct + honest. (My initial NOTE was based on the stale header — corrected after cross-checking the code + memory.)
- [x] Hammerwatch — on-top (HarmonyX) — OK — HammerwatchAPModInstaller patches SDL2-CS.dll; in-game menu; downloads hammerwatch.apworld (appid 239070 verified). Correct + honest.
- [x] HasteBrokenWorlds — on-top (Steam Workshop / LPF) — OK — guides Workshop sub (3462307025), downloads haste apworld; AP client built into Workshop mod; in-game connect. Correct + honest.
- [x] HatsuneMikuDiva — on-top (DivaModLoader) — OK (FIXED) — dinput8.dll loader → mods\ArchipelagoMod\; appid 1761390 + game-string verified. Update-check repo fixed this session (→ Cynichill/Diva-Archipelago-Mod). Correct + honest.
- [x] HereComesNiko — on-top (BepInEx 5) — OK — NikoArchipelagoMod DLL → BepInEx\plugins\; prefills APSavedSettings.json (Host+SlotName). Correct + honest.
- [x] Heretic — own-download APDOOM engine + BYO-WAD — OK — same APDOOM build as Doom/DoomII, BYO heretic.wad, CLI connect. Correct + honest.
- [x] HeroCore — own-download (self-contained freeware) — OK — auto-downloads HeroCoreRandomizer zip (patched herocore.exe + gm-apclientpp.dll) + writes ConnectionInfo.ini. Fully automated. Correct + honest.
- [x] HexcellsInfinite — guided Steam mod — OK — Heaxeus/Archipelago built-in client; guidance-only install + steam:// launch (appid 304410). Honest guided case.
- [x] HiFiRush — on-top (UE4SS Lua mod) — OK — HbkArchipelago into Hibiki\Binaries\Win64\ UE4SS Mods; guides UE4SS + hibiki-bootstrap prereq; F10-console connect. Correct + honest.
- [x] HighRoller — own-download (standalone) — OK — AUTO-downloads + extracts High.Roller.zip + high_roller.apworld (fully automated, not manual). Correct + honest.
- [x] HintMachine — own-download (utility) — OK — hint-point farming tool auto-installed to LocalAppData; self-managed AP connection. Correct + honest.
- [x] HollowKnight — on-top (HK Modding API) — OK — Archipelago.HollowKnight mod → hollow_knight_Data/Managed/Mods/; honestly flags zip needs Modding API + dep mods, guides them. Correct + honest.
- [x] Holo8 — on-top (BepInEx 5.4.23) — OK — ArchipelagoHolo8 mod + holo8.apworld; BepInEx prereq guided (appid 3373960). Correct + honest.
- [x] HololiveTreasureMountain — on-top (BepInEx) — OK — StellatedCUBE mod (BepInEx subtree to game root) + apworld; in-game Options→Archipelago-icon connect (appid 2972990). Correct + honest.
- [x] Hylics2 — on-top (BepInEx 5) — OK — ArchipelagoHylics2 mod; appid corrected in code to 1286710 (task-brief 1349230 was wrong); core AP world. Correct + honest.
- [x] Iji — own-download (self-contained freeware) — OK — MinishLink patched iji.exe + gm-apclientpp.dll; auto-downloads + writes ConnectionInfo.ini (HeroCore sibling). Correct + honest.
- [x] Inscryption — on-top (BepInEx) — OK — DrBibop ArchipelagoMod (appid 1092790); core AP world. Correct + honest.
- [x] IntoTheBreach — on-top (native Lua mod) — OK — Ishigh1 randomizer → mods/randomizer/ via ITB native mod framework; bundles lua-apclientpp.dll. Correct + honest.
- [x] IslesOfSeaAndSky — guided Steam mod — OK — alwaysintreble built-in client; guidance-only install + steam:// launch (appid 1694010). Honest guided case.
- [x] IttleDew2 — on-top (BepInEx 5 + ModCore) — OK — Extra-2-Dew ArchipelagoRandomizer → BepInEx/plugins/ (appid 395620). Correct + honest.
- [x] Jak — guided (OpenGOAL + BYO ISO) — OK — ArchipelaGOAL on OpenGOAL; guides OpenGOAL Launcher (MSI) + in-launcher mod compile + BYO Jak PS2 ISO; refuses fake prefill. Exemplary honesty.
- [x] JetIsland — on-top (BepInEx 5) — OK — Nullctipus mod → BepInEx/plugins/ (VR, appid 1178660). Correct + honest.
- [x] Jigsaw — web game — OK — browser-based (spineraks-org/ArchipelagoJigsaw); no install, in-page AP connect. Correctly a no-op install. Correct + honest.
- [x] KeepTalking — Steam mod / built-in client — OK — KTaNE AP support (appid 341800, GreenPower713); guided. Correct + honest.
- [x] KeymastersKeep — own-download (meta-framework) — NOTE — KmK AP client/framework (SerpentAI fork); manual download + set-path; auto-download enhancement candidate. Not broken.
- [x] KingdomHearts1 — guided (official AP) — OK — detects paid KH 1.5+2.5 ReMIX (Steam 2552430/Epic), never ships (§11), guides mod. Correct + honest.
- [x] KingdomHearts2 — guided (official AP) — OK — separate long-running AP client (ships in Archipelago) + paid base game multi-mod stack; honestly guided. Correct + honest.
- [x] KingdomHeartsBBS — guided (OpenKH family) — OK — detects paid KH HD 2.8 (Steam 1086940), community apworld (gaithernOrg); guides like KH1/KH2. Correct + honest.
- [x] LeagueOfLegends — own-download (companion app) — NOTE — LoL BYO via Riot Client (inherent); LoLAP companion manual download + browse; auto-download enhancement candidate. Not broken.
- [x] LegoBatman — Steam mod / built-in client — OK — ZAPaDASH04 mod (appid 21000); guided. Correct + honest.
- [x] LegoStarWars — on-top (Steam mod) — OK — Mysteryem/Archipelago-TCS built-in client (appid 32200). Correct + honest.
- [x] LethalCompany — on-top (BepInEx 5) — OK — T0r1nn/APLC via Thunderstore + GitHub (appid 1966720). Correct + honest.
- [x] LilGatorGame — on-top (BepInEx 5) — OK — GatorRando mod + lil_gator_game.apworld (appid 1586800). Correct + honest.
- [x] Lingo — on-top/guided — OK — hatkirby Lingo AP Randomizer; core AP world. Correct + honest.
- [x] Lingo2 — own-download (standalone) — NOTE — manual download (code.fourisland.com host) + set path; auto-download candidate (non-GitHub fetch). Not broken.
- [x] LittleWitchNobeta — on-top (MelonLoader) — OK — LWNAP.zip → Mods/; guides MelonLoader prereq; in-game ImGUI connect. Correct + honest.
- [x] Loonyland — own-download (standalone) — NOTE — manual download (GitHub AutomaticFrenzy/HamSandwich) + browse; auto-download candidate. Not broken.
- [x] Lunacid — on-top (BepInEx 5) — OK — Witchybun LunacidAP mod (LunacidAP.dll) + lunacid.apworld (appid 1745510). Correct + honest.
- [x] Mario64 — own-download build-tool + BYO-ROM (compile) — OK — exemplary: no prebuilt exe possible (compiles from user's SM64 ROM); downloads SM64AP-Launcher build tool, guides local compile. Correct + honest.
- [x] Meritous — own-download (self-contained freeware) — OK — FelicitusNeko/meritous-ap auto-downloads GitHub release + JSON config (Celeste64 sibling, no asset gate). Correct + honest.
- [x] Messenger — on-top (Steam mod) — OK — alwaysintreble TheMessengerRandomizerModAP; core AP world (appid 764790). Correct + honest.
- [x] MetroCUBEvania — web/PICO-8 — OK — browser HTML cart + AP Text Client; honest ConnectsItself=false; HTML→p8→releases launch fallback. Correct + honest.
- [x] MetroidPrime — guided (Dolphin + BYO GCN ISO) — OK — Electro1512 community apworld; detects + never ships ISO (§11). Correct + honest.
- [x] Mindustry — own-download (AP fork) — OK — free/open-source game; downloads JohnMahglass modified Java build w/ built-in client; auto-installs. Correct + honest.
- [x] Minecraft — guided/own-download (NeoForge server mod) — OK — downloads qixils/NeoForgeAP local-server mod + apworld; local dedicated server + own Java client (Java 21). Correct + honest.
- [x] MinecraftDig — guided/own-download (Forge server mod) — OK — distinct minecraft_dig.apworld + Forge mod (MC Java 1.19.4, .apmcdig); BYO Minecraft Java. Correct + honest.
- [x] MinishootAdventures — on-top (BepInEx 5) — OK — TheNooodle MinishootRandomizer + minishoot.apworld (appid 1634860). Correct + honest.
- [x] Minit — guided/external (proxy client + bsdiff) — OK — qwint APMinit Python proxy client + bsdiff-patched game data; BYO Minit (appid 609490). Correct + honest.
- [x] MomodoraMonlitFarewell — on-top (MelonLoader 0.5.7) — OK (FIXED) — alditoOt mod → Mods\, prefillable config.json; update-check repo fixed this session (→ alditoOt). Correct + honest.
- [x] MonsterSanctuary — on-top (BepInEx) — OK — Gtaray self-contained mod zip + apworld (appid 814370). Correct + honest.
- [x] MuseDash — on-top (MelonLoader) — OK — DeamonHunter ArchipelagoMuseDash → Mods/; core AP world (appid 774171). Correct + honest.
- [x] NeonWhite — on-top (BepInEx) — OK — s5bug/NeonWhiteAP built-in client dialog in main menu (appid 1533950). Correct + honest.
- [x] NineSols — on-top (BepInEx 5) — OK — Ixrec NineSolsArchipelagoRandomizer; in-game menu connect; BepInEx 5.4.23.2 prereq (appid 1809540). Correct + honest.
- [x] Nodebuster — own-download (self-contained freeware) — OK — Emerald836 mod IS the game (no asset gate); auto-downloads GitHub release zip. Correct + honest.
- [x] Noita — on-top (Noita Lua mod) — OK — DaftBrit NoitaArchipelago; core AP world (appid 881100). Correct + honest.
- [x] NonogramAP — web game — OK — spineraks-org GitHub Pages app + optional desktop; built-in client. Correct + honest.
- [x] OblivionRemastered — on-top (guided) — OK — POD-io two-part: launcher stages in-game file-bridge mod, guides user-side apworld install. Correct + honest.
- [x] OldSchoolRunescape — guided (RuneLite Plugin Hub) — OK — digiholic plugin; detects RuneLite + guides Plugin-Hub install (can't auto-install Java plugin from .NET); connect from plugin panel. Correct + honest.
- [x] OpenRCT2 — guided/on-top (JS plugin) — OK — Crazycolbster JS plugin + apworld + scenarios; BYO OpenRCT2 (openrct2.io) + RCT1/2 data. Correct + honest.
- [x] OpenTTD — own-download (standalone fork) — OK — solida1987/openttd-archipelago patched fork, fully bundled (OpenGFX/SFX/MSX), native WebSocket client; main-menu connect. Correct + honest.
- [x] OriAndTheWillOfTheWisps — on-top/guided (Ori Randomizer) — OK — ori-rando/wotw-client runtime-patch randomizer + alwaysintreble/owotu apworld (appid 1057090). Correct + honest.
- [x] OriBlindForest — on-top (BepInEx) — OK — c-ostic OriBFArchipelago → BepInEx\plugins\ + apworld (appid 387290). Correct + honest.
- [x] Osu — guided (companion) — OK — osu! BYO via ppy launcher; lilymnky-F Archipelago-Osu client. Correct + honest.
- [x] OurAscent — own-download (standalone) — NOTE — manual download (alwaysintreble) + set path; auto-download enhancement candidate. Not broken.
- [x] OuterWilds — on-top (OWML mod) — OK — Ixrec Archipelago mod via Outer Wilds Mod Loader (appid 753640). Correct + honest.
- [x] Overcooked2 — on-top (BepInEx/Harmony) — OK — toasterparty OC2-Modding; core AP world. Correct + honest.
- [x] OxygenNotIncluded — on-top (Workshop or GitHub zip) — OK — ShadowKitty42 ArchipelagoNotIncluded + oni.apworld (appid 457140). Correct + honest.
- [x] Paint — web game — OK — jsPaint browser app (mariomantaw.github.io/jspaint); in-page connect, no-op install. Correct + honest.
- [x] Parkitect — on-top (Steam mod) — OK — in-game AP client mod (appid 453090). Correct + honest.
- [x] Peak — on-top (BepInEx 5) — OK — Mickemoose PEAKPELAGO via Thunderstore + peak.apworld (game-string "peak" lowercase). Correct + honest.
- [x] PinballFX3 — guided/external (memory-attach client) — OK — SerpentAI apworld + PinballFX3Client (Python, reads process memory + holds slot); detects Steam (441090). Correct + honest.
- [x] PizzaTower — on-top (GameMaker patch) — OK — BabyblueSheep gm-apclientpp via UndertaleModTool data.win patch (appid 2231450). Correct + honest.
- [x] PlacidPlasticDuckSimulator — on-top (MelonLoader) — OK — SWCreeperKing Duckipelago.dll → Mods\ + apworld; guides MelonLoader (appid 1999360). Correct + honest.
- [x] PlateUp — on-top (Steam Workshop) — OK — CazIsABoi PlateupAP in-game client. Correct + honest.
- [x] PokemonMysteryDungeonEoS — rom (BizHawk melonDS NDS) — OK (honest stub) — BYO EU ROM (MD5 verified) + community bundled BizHawk client; launcher's own Lua-bridge module pending (flagged, like Emulated Dolphin/PS2 stubs). Correct + honest.
- [x] PowerwashSimulator — on-top (BepInEx 5) — OK — SWCreeperKing PowerwashSimAP mod → game folder (appid 1290000). Correct + honest.
- [x] Prodigal — guided (itch.io BYO + patched build) — OK — randomsalience ProdigalArchipelago patched GameMaker build w/ built-in AP UI (itch.io game). Correct + honest.
- [x] Pseudoregalia — on-top (BepInEx 5) — OK — qwint/pseudoregalia-archipelago mod → Steam game dir (appid 2365810). Correct + honest.
- [x] Psychonauts — on-top (game-dir mod) — OK — Akashortstack Psychonauts-AP-Integration built-in client overlaid on game dir. Correct + honest.
- [x] RabiRibi — on-top/guided (patcher) — OK — tdkollins Archipelago-Rabi-Ribi companion/patcher (appid 400910). Correct + honest.
- [x] Raft — on-top/guided — OK — Raftipelago mod; honestly notes larger irreducible manual portion (appid 648800). Correct + honest.
- [x] RainWorld — on-top (BepInEx 5) — OK — alphappy ArchipelagoRW → BepInEx\plugins\ (appid 312520). Correct + honest.
- [x] Rayman2 — on-top/external (DLL connector) — OK — Aeltumn Rayman2AP + Rayman2APConnector.exe (DLL injection); GOG-only BYO, user browses. Correct + honest.
- [x] RE2R — on-top (mod) — NOTE — FuzzyGamesOn RE2R_AP_World (appid 883710 OK), but AP game-string flagged UNVERIFIED in code. Verify before shipping.
- [x] RE3R — on-top (mod) — OK — TheRealSolidusSnake RE3R_AP_World (appid 952060 verified). Correct + honest.
- [x] Refunct — on-top (UE4 mod) — OK — spinerak refunct-tas-archipelago DLL injection (appid 376030). Correct + honest.
- [x] REPO — on-top (BepInEx 5) — OK — Automagic00 R.E.P.O. mod (appid 3241660). Correct + honest.
- [x] Reventure — on-top (mod) — OK — Droppel ReventureEndingRando (appid 900270). Correct + honest.
- [x] RiftOfTheNecroDancer — on-top (BepInEx 5) — OK — studkid RiftArchipelago (appid 1681570). Correct + honest.
- [x] RiftWizard — on-top (Python source patch) — OK — TheBigSalarius patched-Python overlay into install dir (Python/pygame game). Correct + honest.
- [x] RiskOfRain — on-top (mod) — OK — studkid RoR_Archipelago for 2013 original (appid 248820, disambiguated from RoR2). Correct + honest.
- [x] RiskOfRain2 — on-top (BepInEx, Thunderstore) — OK — Sneaki/Archipelago → BepInEx\plugins\ (appid 632360). Correct + honest.
- [x] RiskOfRainReturns — on-top (BepInEx) — OK — studkid RoR_Archipelago; in-game lobby (appid 1337520). Correct + honest.
- [x] RogueLegacy2 — on-top (BepInEx/Harmony) — OK — zaphim12 RL2Archipelago; ConnectsItself=false (launcher relays items, valid variant) (appid 1253920). Correct + honest.
- [x] RustedMoss — on-top (patcher/companion) — OK — dgrossmann144/Archipelago (GameMaker, appid 1772830). Correct + honest.
- [x] Satisfactory — on-top/guided — OK — Archipelago Randomizer mod; in-game client (Steam 526870/Epic). Correct + honest.
- [x] SavingPrincess — guided (BYO itch.io + bsdiff patch) — OK — BRAINOS freeware + bsdiff4 patch over GameMaker data.win + gm-apclientpp; AP-launcher client extracts/patches. Correct + honest.
- [x] SentinelsOfTheMultiverse — on-top (mod) — OK — Totox00 Archipelago-sotm via GitHub releases (appid 337150). Correct + honest.
- [x] SeveredSoul — own-download (self-contained) — OK — Grenhunterr build IS the game (no Steam, no asset gate); download + extract into GameDirectory. Correct + honest.
- [x] ShadowHedgehog — rom/guided (Dolphin + BYO GCN ISO) — OK — choatix apworld download + guides (can't manage Dolphin/ISO; GameCube NTSC-U). Correct + honest.
- [x] Shapez — on-top (first-party JS modloader) — OK — shapezipelago single-.js mod via shapez built-in modloader (appid 1318690). Correct + honest.
- [x] Shapez2 — on-top (mod) — OK — BlastSlimey 2hapezipelago built-in client (appid 2162800). Correct + honest.
- [x] Shivers — guided/external (ScummVM + randomizer client) — OK — ScummVM ≥2.7.0 runs Sierra game + Shivers Randomizer Client; BYO game data. Correct + honest.
- [x] Signalis — on-top (mod) — OK — devoidlazarus SIGNALISArchipelagoRandomizer; game-string verified (appid 1262350 soft self-verify note, not hard placeholder). Correct + honest.
- [x] SimonTathamPuzzles — web game — OK — ishanpm web puzzle collection; in-browser AP client. Correct + honest.
- [x] Sims4 — guided/external client — OK — itsmisscactus companion client (Client.py) holds slot; ConnectsItself=false (appid 1222670). Correct + honest.
- [x] SkywardSword — rom/guided (Dolphin + BYO Wii ISO) — OK — Battlecats59/SS_APWorld + SS AP Patcher (BYO vanilla US Wii ISO + .apssr → randomized ISO for Dolphin). Correct + honest.
- [x] SlayTheSpire — on-top (ModTheSpire .jar) — OK — cjmang/StS-AP-World (appid 646570). Correct + honest.
- [x] SlimeRancher — on-top (BepInEx 5) — OK — SWCreeperKing Slimipelago (appid 433340). Correct + honest.
- [x] SlyCooper — rom/guided (PCSX2 + PINE + BYO PS2 ISO) — OK — hoppel16 sly1.apworld w/ bundled Python client via PCSX2 PINE IPC. Correct + honest.
- [x] SmushiComeHome — on-top (BepInEx) — OK — xMcacutt Archipelago-SmushiComeHome (appid 1790730). Correct + honest.
- [x] SoH — own-download port + BYO OoT ROM — OK — Ship of Harkinian native OoT port w/ built-in AP client (HarbourMasters Archipelago-SoH). Shipped reference plugin. Correct + honest.
- [x] SonicAdventure2Battle — on-top (mod) — OK — SA2B_Archipelago (appid 213610). Correct + honest.
- [x] SonicAdventureDX — on-top (BepInEx) — OK — ClassicSpeed sadx-classic-randomizer (appid 71360). Correct + honest.
- [x] SonicHeroes — on-top (mod) — OK — Ethicallogic SonicHeroesArchipelago (appid 306020). Correct + honest.
- [x] Spelunky2 — on-top (Playlunky mod) — OK — DDR-Khat Spelunky2-Archipelago (appid 418530). Correct + honest.
- [x] Spinball — web game — OK — spineraks-org browser pinball; in-page AP client. Correct + honest.
- [x] SpyroYearOfTheDragonPSX — rom/external (DuckStation + BYO PS1 ISO) — OK — Uroogla/S3AP DuckStation client + spyro3.apworld (NTSC-U v1.1). Correct + honest.
- [x] Stacklands — on-top (BepInEx, auto-install) — OK — JammyGeeza Stacklands-Randomizer; auto-downloads BepInEx + mod + config prefill (appid 1948280; CDN-corruption fixed earlier confirmed clean). Correct + honest.
- [x] StarCraft2 — guided/external (official AP) — OK — detects SC2 + maps, guides bundled Starcraft 2 Client (AP-main worlds/sc2). Correct + honest.
- [x] StardewValley — on-top/guided (SMAPI) — OK — StardewArchipelago mod; detects Steam (413150), guides SMAPI wizard installer. Shipped reference plugin. Correct + honest.
- [x] StickRanger — web game — OK — Kryen112 in-browser WebSocket client (kryen112.github.io). Correct + honest.
- [x] Subnautica — on-top (BepInEx) — OK — Archipelago Mod (appid 264710/Epic). Shipped reference plugin. Correct + honest.
- [x] SuperCatPlanet — own-download (self-contained) — OK — lone01/scp bundles patched game + apworld (freeware, no Steam). Correct + honest.
- [x] SWE1R — external client — OK-recipe / NOTE (DUPLICATE) — wcolding SWR_apworld + SWR_AP_Client (Steam 808910). DUPLICATE of SwRacer (same game/mod/client/appid; GameId "star_wars_ep1_racer" vs SwRacer's "star_wars_episode_i_racer"). One must be removed (catalog + registration cleanup + build-verify).
- [x] SwRacer — external client — OK-recipe / NOTE (DUPLICATE) — same wcolding SWR_apworld + SWR_AP_Client (Steam 808910) as SWE1R. The two are the SAME GAME registered twice; resolve which to keep.
- [x] SystemShock2 — on-top/guided — OK — Partatio community apworld (Codeberg SS2-Apworld; 1999/2025 AE). Correct + honest.
- [x] TABS — on-top (MelonLoader) — OK — duckboycool TABS_AP_Plugin → Mods\ + tabs.apworld (appid 508440). Correct + honest.
- [x] TcgCardShopSimulator — on-top (mod) — OK — FyreDay built-in client (appid 3070070). Correct + honest.
- [x] Terraria — on-top/guided (tModLoader) — OK — Seldom's Archipelago Randomizer; core AP world; guides tModLoader (1281930) + Terraria (105600). Correct + honest.
- [x] Tevi — on-top (mod) — OK — built-in AP client (appid 1845730). Correct + honest.
- [x] TheForgedCurse — web/PICO-8 — OK — cheesepak HTML cart + AP Text Client (honest ConnectsItself=false). Correct + honest.
- [x] Timespinner — on-top (drop-in randomizer) — OK — Jarno458 TsRandomizer.exe (appid 368620). Correct + honest.
- [x] TotalWarWarhammer3 — on-top/guided — OK — jordansds Archipelago_TWW3_Alt + tww3.apworld (appid 1142710). Correct + honest.
- [x] Touhou185 — guided/manual — OK — furret78 apworld; BYO game (browse) + external Text Client (ConnectsItself=false). Correct + honest.
- [x] Trackmania — on-top/guided (OpenPlanet) — OK — SerialBoxes plugin + apworld (F2P, appid 2225070/Ubisoft). Correct + honest.
- [x] TrailsInTheSkyThe3rd — on-top (patch/mod) — OK — built-in AP client (appid 907040). Correct + honest.
- [x] Tunic — on-top (BepInEx) — OK — silent-destroyer TUNIC Randomizer (appid 553420). Shipped reference. Correct + honest.
- [x] TurnipBoy — on-top (BepInEx) — OK — pointfivetee turnip_boy_mod.zip (pre-configured BepInEx; appid 1205450). Correct + honest.
- [x] TwilightPrincess — rom/guided (Dolphin + BYO ISO + patcher) — OK — WritingHusky community apworld (3-component, like SkywardSword). Correct + honest.
- [x] TwistyCube — own-download/manual + Text Client — OK — spineraks-org GitHub release; browse + external Text Client (ConnectsItself=false). Correct + honest.
- [x] Tyrian — own-download (self-contained freeware) — OK — KScl/TyrianArchipelago (Tyrian 2000 freeware build). Correct + honest.
- [x] TyTheTasmanianTiger — on-top (TygerFramework) — OK — xMcacutt Ty1AP-Client.dll + apworld (appid 411960). Correct + honest.
- [x] UFO50 — on-top (GameMaker binary-patch + DLL) — OK — UFO-50-Archipelago gm-apclientpp injection (appid 2147860). Correct + honest.
- [x] Ultrakill — on-top (BepInEx) — OK — TRPG0/ArchipelagoULTRAKILL via r2modman/Gale or manual (appid 1229490). Correct + honest.
- [x] Unbeatable — on-top (BepInEx) — OK — AllPoland unbeatAP (game-string "unbeatable_arcade"). Correct + honest.
- [x] Undertale — guided (official AP) — OK — Undertale Client (Python, ships in Archipelago) + bsdiff4 patch on a COPY of player's Steam data.win. Shipped reference. Correct + honest.
- [x] UnfairFlips — on-top (BepInEx 5 Mono) — OK — robotzurg UnfairFlipsAPMod via Thunderstore (appid 3925760). Correct + honest.
- [x] VampireSurvivors — on-top (MelonLoader) — NOTE — SWCreeperKing ArchipelagoSurvivors; appid 1794680 flagged UNVERIFIED in code (potential confusion w/ PPDS appid range). Verify appid.
- [x] VoidSols — own-download (standalone) — NOTE — manual download (cookie966507) + set path; auto-download enhancement candidate. Not broken.
- [x] VoidStranger — on-top (GameMaker xdelta-patch + DLL) — OK — CriminalPancake; in-game F10 connect (appid 2121980). Correct + honest.
- [x] VVVVVV — own-download (AP fork) — OK — N00byKing V6AP fork w/ built-in APCpp.dll client; core AP world (open-source game). Correct + honest.
- [x] Wargroove — guided/external client (official AP) — OK — bundled AP client relays to game (AP-main worlds/wargroove). Correct + honest.
- [x] Wargroove2 — guided/external (official-style) — NOTE — FlySniper fork (like Wargroove); AP game-string inferred/UNVERIFIED in code. Verify game string before shipping.
- [x] WateryWords — web game — OK — spineraks-org browser word game; no-op install. Correct + honest.
- [x] Webfishing — on-top (GDWeave Godot mod) — OK (FIXED) — mwoiii webfishing-ap; update-check repo fixed this session (GDWEAVE_*→MOD_*). Correct + honest.
- [x] WestOfLoathing — on-top (mod) — OK — built-in AP client (appid 597220). Correct + honest.
- [x] WindWaker — rom/guided (Dolphin + BYO GCN ISO) — OK — AP-main worlds/tww + tanjo3/wwrando fork (3-component). Correct + honest.
- [x] Witness — external client (process-memory injector) — OK — standalone C++ randomizer hooks running Steam game. Shipped reference. Correct + honest.
- [x] Wordipelago — web game — OK — ProfDeCube itch.io browser word game; built-in client. Correct + honest.
- [x] Xcom2 — on-top (Steam Workshop) — OK — Snyax/WOTCArchipelago built-in TcpLink client (appid 593380, WOTC). Correct + honest.
- [x] YachtDice — web game — OK — bundled AP browser dice game; built-in client. Correct + honest.
- [x] YARG — on-top (BepInEx 5) + BYO YARG — OK — Thedrummonger YargArchipelagoPluginV2; YARG via YARC Launcher, browse to folder. Correct + honest.
- [x] YokusIslandExpress — on-top (mod) — OK — alwaysintreble Archipelago mod. Correct + honest.
- [x] YookaLaylee — on-top (BepInEx) — OK — SunnyBat YLRandomizer + Awareqwx apworld. Correct + honest.
- [x] ZeldaALinkBetweenWorlds — rom (Citra/3DS) — OK (honest stub) — ChecksImplemented=false (no BizHawk 3DS core); graceful "no emulator configured" + BYO 3DS ROM; Citra/Lime3DS bridge pending. Correct + honest (same as Emulated Dolphin/PS2 stubs).
- [x] ZeldaMajorasMask — own-download recomp + BYO N64 ROM — OK — RecompRando on Zelda64-Recompiled; recompiles user's MM ROM into native PC port (Mario64-style); game-string inferred (soft-verify). Correct + honest.
- [x] Zork — own-download client (BYO Zork GI) — OK — SerpentAI fork built-in AP client; game-string verified vs catalog.json (Zork: Grand Inquisitor). Correct + honest.
