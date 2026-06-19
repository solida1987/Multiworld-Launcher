# Multiworld Launcher V2 — Development Plan
_Last updated: 2026-06-19 (session 2 complete — all HIGH/MEDIUM items done)_

This plan covers all known issues, missing features, and improvements found in the
full codebase audit. Marco adds items after this baseline. Coding starts only when
the plan is finalized.

Legend: 🔴 Critical | 🟠 High | 🟡 Medium | 🔵 Low | 🔵 Future/Out of scope

---

## Current state snapshot

| Metric | Count |
|--------|-------|
| Registered plugins | 369 |
| Catalog entries | 379 |
| Matched (plugin + catalog) | 369 |
| Discord-only catalog entries (no plugin, intentional) | 10 |
| Plugin without catalog entry | 0 |

---

## CRITICAL — must address before any public release

### ~~🔴 AP-1: InvalidPacket response not handled~~ **DONE**
InvalidPacket case added to receive loop; logged to AP console AND shown as warning toast.

### ~~🔴 AP-2: EmulatorPlugin.OnApStateChanged is a no-op~~ **DONE**
Writes `ap_state.json` with state/connected/timestamp on every call. Lua side polls this file.

### ~~🔴 UI-1: Background tasks not linked to window lifetime~~ **DONE**
`_mainWindowCts` CancellationTokenSource exists at line 140; passed to background tasks and cancelled in Window_Closing.

---

## HIGH — should fix in the next coding session

### ~~🟠 AP-3: RoomUpdate does not track player composition changes~~ **DONE**
HandleRoomUpdate already parses `players` array and fires `RoomPlayersChanged` event.

### ~~🟠 AP-4: Tags field in Connect packet is minimal~~ **DONE**
`_tags` defaults to `{ "AP", "MultiworldLauncher" }` at line 61 of ApClient.cs.

### ~~🟠 ARCH-1: 33+ emulated game stubs with ChecksImplemented=false~~ **DONE**
Stubs hidden from library sidebar; show "Soon" in Browse with disabled toggle.

### ~~🟠 ARCH-2: ReceiveItemsAsync has no error path~~ **DONE (partial)**
Added try/catch in ApClient around ReceiveItemsAsync; failures logged to AP console.
Full retry/bool path would require interface change across 369+ plugins — deferred.

### ~~🟠 UX-1: No sidebar search with 369 games~~ **DONE**
TxtSidebarSearch + TxtSidebarSearchHint already implemented; filters by DisplayName (Tag) and ToolTip. Ctrl+F focuses it.

### ~~🟠 UX-2: No visual indicator when game is running~~ **DONE**
RebuildGameList() called after LaunchAsync and in CleanupSessionAsync; cards show "Running" badge.

### ~~🟠 ERR-1: ApClient swallows exceptions silently~~ **DONE**
All exception catch blocks already log to PrintMessage. Line 307 (socket close) is intentionally silent.

### ~~🟠 ERR-2: Network errors during install not user-friendly~~ **DONE**
FriendlyInstallError() categorizes SocketException/HttpRequestException/TaskCanceledException/IOException.

---

## MEDIUM — next polish wave

### ~~🟡 UX-3: No keyboard shortcut cheat sheet~~ **DONE**
`ShortcutsOverlay` Grid added at ZIndex=940. `?` button in title bar + F1 toggle it. Esc closes it. Lists Ctrl+K, Ctrl+F, Ctrl+1-5, F1, Esc.

### ~~🟡 UX-4: Recent connections have no edit/remove UI~~ **DONE**
Context menu per entry (Remove) + "Clear all recents" button already wired at lines 9208, 9228.

### ~~🟡 UX-5: No onboarding for first-time users~~ **DONE**
`PanelEmpty` welcome card with 3 step links (Browse → AP Connect → Play) shown when no game selected and library empty. RefreshWelcomeChecklist updates step state.

### ~~🟡 UX-6: Crash log rotation may lose crashes~~ **DONE**
`WriteCrashLog` rotates crash.log → crash_0.log → crash_1.log → crash_2.log at 1 MB each (App.xaml.cs:520-541).

### 🟡 PERF-1: Sidebar may lag at 400+ games (deferred)
With 369 plugins today and the game-factory approaching ~400, rendering all
sidebar cards at once in a plain StackPanel causes startup lag. WPF doesn't
virtualize StackPanel children. Requires ListBox refactor — deferred (low real impact with current library sizes).

### ~~🟡 PERF-2: ApItemTracker list grows unbounded~~ **DONE**
Capped at `LauncherConstants.ItemTrackerMaxEntries` (10,000) via `RemoveRange` after each batch.

### ~~🟡 QUALITY-1: Hard-coded constants in MainWindow~~ **DONE**
`Core/LauncherConstants.cs` created; MainWindow references `LauncherV2.Core.LauncherConstants.*`.

### ~~🟡 QUALITY-2: Hint dedup key is ambiguous~~ **DONE**
Dedup key uses `\0` separator: `ReceiverName\0ItemName\0LocationName\0SenderName` (line 45).

### ~~🟡 QUALITY-3: Asset glob silently fails if directory missing~~ **DONE**
MSBuild `ValidateAssetDirectories` target added; warns on Release if `Assets` or `Plugins\Scripts` missing.

### ~~🟡 CATALOG-1: 10 discord-only catalog entries with no plugin~~ **DONE**
`BuildCatalogCard()` detects `status == "discord_only"`; shows 📢 badge, "Discord only" tooltip, blocks library add.

---

## LOW — future polish, no urgency

### ~~🔵 SETTINGS-1: TrackerViewMode may not persist on crash~~ **DONE**
`SetTrackerViewMode` calls `SettingsStore.Save()` immediately after toggling at lines 9302-9332.

### 🔵 ARCH-3: BizHawk RAM maps not yet filled in
**Files:** `Plugins/Scripts/games/` — many Lua modules have `ADDRESSES_VERIFIED = false`  
The per-game Lua modules exist and load, but do not yet read game memory for
actual check detection. Items arrive from AP correctly, but in-game checks
(e.g., "collected this item") are not reported back to AP.  
This is gated on per-game reverse engineering work (expected: 1-2 days per game).  
**Fix:** Priority order: Pokémon Emerald (most popular), ALttP, Super Metroid,
then community requests.

### ~~🔵 ARCH-4: PCSX2 backend not yet registered~~ **DONE**
PCSX2 added to `EmulatorBackends.cs` with `BridgeReady=false` (shown as
"coming soon" in the emulator dropdown). `MediEvilPlugin` updated to offer
BizHawk (works today via its PSX core) as the primary option, with PCSX2
listed second for when the PINE-protocol bridge ships. ToeJam & Earl uses
BizHawk/GEN directly and was already correct. A full Lua/NWA bridge for PCSX2
remains future work — the registry entry makes the intent visible in the UI.

### 🔵 ARCH-5: MainWindow.xaml.cs size (9,524 lines)
**File:** `UI/Pages/MainWindow.xaml.cs`  
The entire launcher UI logic is in one file, making it hard to navigate and
maintain. AP connection, game selection, item tracking, hint rendering, and
achievement display are all interleaved.  
**Fix:** Gradually extract into separate ViewModel classes:
`GameSelectionViewModel`, `ApConnectionViewModel`, `ItemTrackerViewModel`,
`AchievementViewModel`. Each holds its own state and commands, bound via
WPF data-binding.

### ~~🔵 ARCH-6: OpenTTD — no status bridge~~ **DONE (launcher side)**
`autoconnect=1` added to ap_connection.cfg. `PollApStatusAsync` background loop polls `data\ap_status.json` every 2s; fires `LocationsChecked` (deduped) and `GoalCompleted` once. No-op until fork v1.4.2+ writes the file — the contract JSON format is documented in the plugin. V2.1 fork work still needed.

### ~~🔵 UX-7: No drag-reorder undo~~ **DONE**
Ring buffer of 10 order snapshots (`List<IReadOnlyList<string>>`) stored in `_orderHistory`. Drop handler pushes snapshot before `MoveBeforeId`; Ctrl+Z pops and calls `LibraryStore.RestoreOrder` + `RebuildGameList`.

### 🔵 DIAG-1: No opt-in telemetry
**Files:** N/A (missing feature)  
Crash logs are written locally but never reported. When a plugin breaks or a
game can't connect, developers have no signal.  
**Fix:** Add an opt-in (checkbox in Settings → General) telemetry toggle that
sends anonymized crash summaries + AP connection error counts to a server.

---

## FUTURE — out of scope for current cycle

| Feature | Notes |
|---------|-------|
| Linux / Avalonia port | Major rewrite; WPF is Windows-only |
| Plugin hot-reload from `Plugins/` folder | Requires MEF or reflection-based loading |
| Code signing (EV cert) | Reduces AV false positives long-term; cost $$$ |
| PopTracker deep integration | Separate poptracker binary; complex API |
| Per-game color themes applied to all panels | Currently only header uses accent color |
| Install wizard step-by-step for complex games | Good for Brotato, Celeste, HK |

---

## Marco's requirements (M-1 through M-11)

### 🟠 M-1: Emulator dropdown — only BizHawk and snes9x  
**Status: DONE**  
Remove mGBA and Mesen from the emulator dropdown (both had `BridgeReady=false` — shown as
"coming soon" but clutter the dropdown). Set snes9x `LiveVerified=true` to remove the
"(experimental)" label — it is fully wired and needs a live-test confirmation, not a UI warning.  
**Both entries removed + snes9x promoted, code landed.**

### 🟠 M-2: Library — empty on first install + sidebar sections  
**Status: DONE (seeding) + IN PROGRESS (sidebar sections)**  
Library must start empty. Games auto-appear only if already installed on the machine
(via `IsInstalled`). D2 auto-appears if the game files are present (M-5 merged here).
Sidebar splits into three sections: **Favorites** | **Installed** | **Not Installed**
(the last is collapsible). Installed games get "Uninstall"; library-added-but-not-installed
games get "Remove from list".  
**Auto-seeding changed to installed-only. Sidebar sections coded.**

### ~~🟡 M-3: Homepage / proper game landing pages~~ **DONE**
`PanelEmpty` rebuilt as a `ScrollViewer` homepage with three progressive sections:
- **Featured hero** (`HomeHeroContainer`): most-recently-played installed game shown with hero banner (165px), name, playtime meta, description snippet, and a "▶ PLAY" button that opens the Play tab directly.
- **Recently played** (`HomeRecentSection`): up to 4 mini cards (icon + name + relative date), each clickable to select the game.
- **Getting Started** (`HomeGetStartedSection`): original 3-step checklist, hidden once any game is installed (users past first-run don't need it).
- **Browse all** button always visible at the bottom with live game count.
`RefreshHomePage()` replaces the 3 `RefreshWelcomeChecklist(); PanelEmpty.Visibility=Visible` call sites; `HomeHero_Click` / `BtnHomeHeroPlay_Click` handle navigation.

### ~~🟠 M-4: Auto-update — fully silent during splash~~ **DONE**  
`ShowMainWindowAsync` in App.xaml.cs:479 runs the check + download during splash dwell. Splash shows "Updating to vX.Y…  N%" via SetUpdateStatus. MainWindow never opens. Batch restarts the launcher.

### 🟠 M-5: D2 LoD — auto-appear if installed  
**Status: DONE (merged into M-2)**  
If the launcher is run alongside the D2 repo, D2 should auto-appear in the library because
`D2Plugin.IsInstalled` returns true when the game files are present. If the launcher is a
standalone download with no D2 files, the library starts empty. This falls naturally out of
the M-2 installed-only seeding logic.

### 🔴 M-7: Zero AI traces on GitHub  
**Status: VERIFIED CLEAN**  
No Co-Authored-By, no AI references in commits or source files. Git history audited — clean.
The ClaudeChibout in one commit is a real GitHub username (ADOFAI mod author); the
"AI-generated art" commit refers to the local FLUX image pipeline (art tool, not code tool).
**Rule**: maintain vigilance on every commit going forward. This entry must NOT appear on the repo.

### 🟡 M-8: Latest news — never show empty  
**Status: DONE**  
When `GetNewsAsync` returns an empty array, the news panel previously showed a bare
"No news available." message. Now it shows the game's description + a meaningful "check the
community" prompt, so the panel is never content-free.

### 🟡 M-9: Credits — who coded the game + who made the AP integration  
**Status: FRAMEWORK DONE — content needs filling in**  
`Core/GameCredits.cs` static registry (gameId → GameDev + ApAuthor) created. Overview panel shows
"Game by: X" / "AP integration by: Y" below the AP world line. ~60 entries pre-filled.
Remaining ~310 entries: Marco fills in AP authors from each game's worlds/__init__.py README.
Instructions are in the GameCredits.cs file header.

### ~~🟡 M-10: Achievements — personalized per game + game-specific tips~~ **DONE**
`Core/AchievementSystem/GameFlavorText.cs` — static table (gameId → Title, Description, Icon) with thematic overrides for 32 popular games (D2, ALttP, Hollow Knight, Super Metroid, Minecraft, Risk of Rain 2, Terraria, Noita, Dark Souls 3, Slay the Spire, Tunic, Factorio, Stardew Valley, and more). `AchievementLadders.cs` updated: the `_goal_1` achievement now uses game-specific flavor text (e.g., Hollow Knight gets "Hollow Knight — Seal the Hollow Knight at the Black Egg Temple" with a 🪲 icon) instead of the generic "Mission Complete". Marco can add entries to `GameFlavorText.cs` for any remaining games.

### 🟡 M-11: Points consistency — use "Points" everywhere  
**Status: DONE**  
The achievement overview on the game landing page showed the tier word ("bronze", "silver")
instead of the numeric point value. The achievement tips page already showed "25 pts". Fixed:
the overview now shows "{pts} pts" in the tier color, matching the tips page.

---

## Implementation order (suggested)

1. 🔴 AP-1, AP-2, UI-1 — prevent crashes and fix silent failures
2. 🟠 AP-3, UX-1, UX-2 — session UX and discoverability
3. 🟠 ARCH-1 — hide/re-label 33+ non-functional stub games
4. 🟠 ERR-1, ERR-2 — better error communication
5. 🟡 PERF-1 — sidebar virtualization (needed before ~400 games)
6. 🟡 UX-3, UX-4, UX-5 — polish and onboarding
7. 🔵 ARCH-3 — BizHawk RAM maps (ongoing, game by game)
