# Steam-Like UX — Feature Gap Analysis for Archipelago Launcher V2.0.0

_Research date: 2026-06-10. Compared against: Steam (2019 library redesign + downloads page), GOG Galaxy 2.0 (bookmarks, Recent view, Atlas global search), Playnite (open-source WPF launcher — closest architectural sibling), Heroic Games Launcher, and the Epic/EA launchers (mostly as negative examples)._

_Scope: everything marked ✅ in `TODO_V2.md` is excluded. Items already tracked as 🔴/planned in `TODO_V2.md` or `TODO_V2_MASTER.md` are cross-referenced, not re-pitched._

---

## Executive Summary

The launcher already has the **bones** of a Steam-like product: library sidebar with favorites + drag-reorder + search, per-game pages with tabs, a 400-game Browse catalog with detail pages, achievements with toasts, skeleton loading states, and tab fade transitions. What it lacks is the **connective tissue that makes a launcher feel personal and alive**: nothing in the UI tells you *how much you've played*, *when you last played*, or *what changed since you were last here* — even though the session data layer (`PlaySession` → `Data/sessions.json` via `AchievementStore`) is already fully built and only feeds achievements today. The second gap cluster is **feedback and speed**: downloads show "42MB / 90MB" with no speed/ETA, there are zero keyboard shortcuts in the entire window, no generic toast channel (only achievements toast), and the window forgets its size (only Left/Top persist). Third: the **first five minutes** — a new user sees "Select a game from the library" instead of being walked to their first AP connection. The headline finding: **8 of the 10 quick wins below need no new data, no assets, and no network code** — they surface state the launcher already records. The two biggest *perceived-quality* items (hero art, game icons) are already tracked in the TODOs and are asset-gated, not code-gated.

---

## Gap Table

Effort: **S** = hours–1 day · **M** = 1–3 days · **L** = a week or more.
Priority: **P1** = do next cycle · **P2** = soon after · **P3** = when the library grows / opportunistic.

| Feature | What Steam/others do | Why it matters | Where it fits in our UI | Effort | Priority |
|---|---|---|---|---|---|
| Playtime + last-played surfaced | Steam shows "X hrs on record / Last Played" on every library page; Playnite auto-tracks playtime for every launched game; GOG's Recent view summarizes play activity | This is the single strongest "it's *my* library" signal. Our data already exists in `AchievementStore` (`TotalPlaytime`, `FastestGoal`, sessions.json) — it is currently invisible | Game header badge row, sidebar card tooltip, new "Your Stats" card in Play tab | S | P1 |
| Recently-played sort | Steam's Recent Games shelf is the most-used part of its library home; GOG has a Recent view | Returning users want "continue where I left off" in one glance, not a manually-ordered list | Sort toggle next to `TxtSidebarSearch` (Custom / Recent / A-Z) | S | P1 |
| Download speed + ETA | Steam's downloads page shows live speed, a scaling speed graph, session peak, and a continuously-updated ETA | Trust during installs. "Is it stuck?" is the #1 download anxiety; raw MB counts don't answer it | `TxtProgress` in Play tab `ProgressArea`; computed inside `D2Plugin.DownloadFile` loop (bytes already counted) | S | P1 |
| Generic toast notifications | Steam pops bottom-right toasts for download-complete, friend events, screenshots; we only toast achievements | Installs/updates finish while the user is on another tab or minimized to tray — completion currently announced only in the status bar | Generalize `AchievementToast` into a `ToastService` (queue, stack of 3, click-to-act) | M | P1 |
| Window size + maximize persistence | Every major launcher restores size, position, and maximized state | Baseline desktop polish; we persist Left/Top only, so users re-size every launch | `LauncherSettings` + restore in `MainWindow` ctor (complements the V2.2 "multi-monitor position" item in MASTER §8) | S | P1 |
| First-run welcome / getting-started | GOG Galaxy onboards by connecting platforms; Playnite's first-run wizard imports your library before showing the main window | AP is jargon-heavy (server:port, slot, YAML). Our current first screen is dead text: "Select a game from the library" | Upgrade `PanelEmpty` into a live 3-step checklist (add → install → connect) | M | P1 |
| Ctrl+K quick switcher / command palette | GOG Galaxy 2.0's Atlas update added global search; Slack popularized Ctrl+K; VS Code/Discord made it an expectation | One keystroke to any game, tab, or action; doubles as feature discovery; scales to the 400-game catalog | New overlay grid in `MainWindow` root + `PreviewKeyDown` | M | P1 |
| AP connection history / profiles | Analogous to Steam remembering accounts and GOG remembering connected platforms | Async multiworlds run for weeks — players re-type the same server:port + slot dozens of times | Recent-connections dropdown on the sidebar connect panel (`PanelConnInputs`) | S | P1 |
| AP auto-reconnect with backoff | Steam survives network blips invisibly | Already tracked (MASTER §2.3, V2.1) — bumping here because a dropped WebSocket mid-session is our equivalent of Steam going offline mid-game | `ApClient` receive loop | M | P1 |
| Keyboard shortcuts (tabs, search, Esc-back) | Steam: huge shortcut surface; Heroic praised for keyboard accessibility. We have exactly one `KeyDown` handler (chat box) | Power users feel friction immediately; Esc-to-close-overlay is muscle memory from every app | Window-level `PreviewKeyDown` dispatch to existing click handlers | S | P2 |
| Live session timer / rich presence text | Steam shows "In-Game" + session context; Discord rich presence | "● In Session" badge is static; showing elapsed time + checks done makes the launcher feel like a live companion | `BadgeSession`, `TitleBarConnText`, tray icon tooltip | S | P2 |
| "What's New" aggregated across library | Steam Library Home's What's New shelf shows updates for all your games in one feed | Today news is per-game and lazy-loaded per tab visit; updates to non-selected games are invisible | New default "Home" view replacing `PanelEmpty` once first-run is done | M | P2 |
| Verify-install button in UI | Steam's "Verify integrity of game files" is the universal fix-it ritual | Already tracked (MASTER §3.8) — `VerifyInstallAsync` exists, just unexposed | D2 settings panel + game card context menu | S | P2 |
| Periodic update re-check + Check Now button | Steam checks continuously; we check once at startup | Already tracked (MASTER §3.9). Long-lived launcher processes (tray) never see new releases | Game header + 24h `DispatcherTimer` | S | P2 |
| Stop confirmation dialog | Prevents one-click session kills | Already tracked (MASTER §3.13) | Stop button handler | S | P2 |
| Session history page | GOG's Recent view shows play summaries per period; Steam's year-in-review | sessions.json already stores every session with server/slot/goal/duration — a timeline UI is pure rendering (export already tracked MASTER §8) | New Progression sidebar category or Settings section | M | P2 |
| Library collections / grouping | Steam collections + dynamic (tag-rule) collections; GOG bookmarks pin filtered views; Playnite filter presets | Low value at 2–4 games; becomes essential when emulator sub-plugins land (300+ catalog games). Cheap version now: group headers (Favorites / Installed / Not installed) | Sidebar `GameListPanel` group headers now; rule-based collections later | M | P3 |
| Game hero/banner art | Steam's library hero images are most of its perceived quality | Already tracked 🔴 in TODO_V2 ("Per-game hero/background art"). Asset-gated. Interim: procedural gradient from the per-game accent color costs nothing | `GameHeader` background | S (gradient) / M (art) | P1 |
| Real icons (sidebar 32px, catalog 128px) | Every launcher; Epic is criticized for slow-loading art | Already tracked 🔴 in TODO_V2 + MASTER §4 with full spec. Asset-gated, not code-gated | `Assets/`, `CatalogRepo/thumbnails/` | M | P1 |
| Downloads queue manager page | Steam has a dedicated downloads page with queue + pause/resume; Heroic does parallel downloads | Irrelevant with one real plugin; required once emulator games multiply install jobs | New full page (pattern: `PanelBrowse`) | L | P3 |
| Settings search | Steam's settings has a search filter | Our settings surface is one page — searching it adds nothing today; revisit when plugins multiply | Settings tab header | S | P3 |
| UI scale / accessibility | Heroic ships UI-scale + font controls in an Accessibility menu | 4K/laptop users; WPF makes this a window-level `LayoutTransform` + slider | Settings LAUNCHER section | M | P3 |
| Controller / Big Picture mode | Steam Big Picture, Playnite fullscreen mode | Wrong audience for now — multiworld players are at a desk juggling trackers and chat | — | L | P3 |

---

## Top 10 Quick Wins

All ten are implementable purely in C#/WPF with **no external assets, no new network protocols, no third-party services**. Ordered by value-per-effort.

### 1. Surface playtime + last-played (the free feature)
The entire data layer exists: `AchievementStore.Instance` already persists every session to `Data/sessions.json` and exposes `TotalPlaytime(gameId)`, `TotalSessions(gameId)`, `GoalsReached(gameId)`, `FastestGoal(gameId)`. Nothing reads them except achievement checks.
**Build:**
- Add a `LastPlayed(string gameId)` query to `AchievementStore` (max `EndedAt` over `_sessions`).
- **Game header** (`MainWindow.xaml`, badge row next to `BadgeInstalled` ~line 590): a muted badge "⏱ 12.4 h · last played yesterday".
- **Sidebar cards**: tooltip (or second-line text) "Last played 3 days ago — 12.4 h total".
- **Play tab**: a "YOUR STATS" card at the top of `PagePlay`'s StackPanel — four stat tiles: Sessions, Total playtime, Goals reached, Fastest goal. Reuses the stat-tile pattern from `RenderPlayerSessionView`.
- Shared `FormatPlaytime`/`FormatRelativeDate` helpers ("12.4 h", "yesterday", "3 days ago").

### 2. Recently-played sort in the sidebar
**Build:** a 3-state sort toggle (`Custom ▾` → `Recent` → `A–Z`) as a small button inside the sidebar search border (`MainWindow.xaml` ~line 476, next to `TxtSidebarSearch`). Persist choice as `LibrarySort` enum in `LauncherSettings`. `Recent` orders by `AchievementStore.LastPlayed(gameId)` descending; favorites stay pinned on top in all modes (matches existing `LibraryStore` contract). Never-played games fall to the bottom alphabetically.

### 3. Download speed + ETA in install progress
**Build:** in `D2Plugin.cs` `DownloadFile` loop (~lines 1112–1140, `bytesRead` already counted): keep a small ring buffer of `(timestamp, bytesRead)` samples; every progress tick compute speed over the last ~3 s, smooth with an exponential moving average, derive ETA from remaining bytes. Change the progress message to `"42 MB / 90 MB — 8.4 MB/s — about 6 s left"`. Renders through the existing `IProgress<(int, string)>` into `TxtProgress` — **zero XAML changes**. Apply the same helper to the full-ZIP install path.

### 4. Generic `ToastService` (generalize the achievement toast)
**Build:** extract the `AchievementToast` show/animate/dismiss pattern into `UI/Controls/ToastService.cs`: `ToastService.Show(title, body, icon, onClick)` with a queue, max 3 stacked bottom-right of the owner window, ~4 s auto-dismiss, optional click action. Fire it for: **install/update complete** ("Diablo II updated to 1.9.14 — click to Play"), **update available** (startup check), **AP disconnected unexpectedly** (click → reconnect), **goal completed**. Keep achievements on the same channel. This matters doubly because the window already minimizes to tray during play — pair with `NotifyIcon.ShowBalloonTip` when the window is hidden (the `SystemTrayManager` WinForms icon supports this natively).

### 5. Window size + maximized persistence
**Build:** add `WindowWidth`, `WindowHeight`, `WindowMaximized` to `LauncherSettings` (`SettingsStore.cs` — Left/Top pattern is at lines 41–42). Save where Left/Top are saved (`MainWindow.xaml.cs` ~line 4446); restore in the ctor block (~line 108) **with a clamp** against `SystemParameters.VirtualScreenWidth/Height` so a disconnected monitor can't strand the window off-screen (this also pre-solves part of the V2.2 multi-monitor item).

### 6. First-run getting-started checklist
**Build:** replace the dead two-line `PanelEmpty` (`MainWindow.xaml` ~line 542) with a welcome card shown until `LauncherSettings.FirstRunCompleted`: a short headline ("Welcome to the Archipelago Launcher") plus three checklist rows that reflect **live state** and each carry their own action button — ① *Pick a game* → Browse button (done when a library game is selected/added), ② *Install it* → Install button (done when `CheckForUpdateAsync` reports installed), ③ *Connect to Archipelago* → opens the sidebar connect panel (done on first successful connect; explain server:port + slot in one sentence here). Add a "What is Archipelago? ↗" link. After all three complete, fall back to the normal empty state (or the Home view, see Bigger Bets). Onboarding research consistently shows do-the-thing checklists beat tour screens.

### 7. Ctrl+K quick switcher
**Build:** a hidden overlay in `MainWindow`'s root Grid (RowSpan all rows): dim backdrop + centered panel with a TextBox and a ListBox. `Window.PreviewKeyDown` opens on Ctrl+K, Esc closes, ↑/↓/Enter navigate. Command sources (all in-memory already): library games ("Play Diablo II", "Switch to OpenTTD"), the five tabs, actions (Connect/Disconnect, Browse, Check for updates, Open install folder, Launch Standalone), and catalog entries prefixed "Browse:" (the loaded `GameCatalog` list). Simple case-insensitive subsequence scoring is plenty — ~200 lines of code-behind, no packages. This single feature reads as "modern app" more than anything else on this list.

### 8. AP connection history / one-click reconnect
**Build:** add `List<ApConnectionProfile>` (`Server`, `Slot`, `LastUsed` — **never the password**) to `LauncherSettings`, capped at 10, upserted on every successful connect. UI: a small "recent ▾" affordance on the Server row of `PanelConnInputs` opening a Popup list — "`Marco` @ archipelago.gg:38281 · 2 d ago"; clicking fills server + slot and focuses the password box. Directly serves the documented connect-flow pain (AP race: connect first, then launch) by making reconnects two clicks.

### 9. Live session timer + rich-presence text
**Build:** a 30–60 s `DispatcherTimer` while a session is active: `BadgeSession` becomes "● In Session — 1 h 24 m"; append "· 1 h 24 m" to `TitleBarConnText`; set the tray icon tooltip to "Playing Diablo II — 1 h 24 m — Marco @ archipelago.gg:38281". If `LocationTracker` totals are loaded, add "142/200 checks". Start time already exists (the `BeginSession` token / launch timestamp). Distinct from the tracked Discord-RPC sidecar (MASTER §7) — this is in-launcher only and needs no external service.

### 10. Keyboard shortcut suite
**Build:** one `Window.PreviewKeyDown` dispatcher (plus tooltip hints on the targets): **Ctrl+1..5** → tabs (Play/Progression/Items/News/Settings), **Ctrl+F** → focus `TxtSidebarSearch` (or `TxtTrackerSearch` when Items tab is active), **Ctrl+B** → Browse, **Esc** → close topmost overlay (catalog detail → Browse → game view; later: command palette), **F5** → refresh news/catalog on those pages, **Ctrl+Enter** in connect panel → Connect. Every target handler already exists; this is routing only. Do this together with #7 (shared key-handling plumbing).

---

## Bigger Bets (future cycles)

1. **Library Home dashboard (Steam Library Home analog).** When no game is selected (post-first-run), show a Home view instead of empty state: a *Continue Playing* shelf (recently played, big Play buttons, playtime), a *What's New* feed aggregating `GetNewsAsync` across **all installed** plugins (cache per-plugin with a TTL; it's the same GitHub releases call the News tab already makes), and an *Up Next* card (active AP session info, or update-available prompts). This is the feature that makes opening the launcher feel like an arrival rather than a form. (M–L)

2. **Companion mini-tracker window ("overlay without injection").** A compact, always-on-top, borderless WPF window (~320×500, toggleable from the In-Session badge and tray menu) showing live session essentials while the game runs: elapsed time, last 5 items received, checks done/total, hint points, and a one-line chat send box. D2 1.10f runs happily windowed (`D2Windowed` setting exists), and this delivers the *value* of the Steam overlay with zero injection risk — important given the project's documented AV-false-positive history. Should be reusable for every future plugin. (L)

3. **Session history & stats page.** Render `Data/sessions.json` as a browsable timeline: per-session rows (date, game, server/slot, duration, players, goal ✓), per-game lifetime aggregates, streaks ("played 4 weekends in a row"), personal bests (fastest goal), and a "year so far" summary block. Pairs with the already-tracked CSV/JSON export (MASTER §8). All rendering, no new data collection. (M)

4. **Downloads & jobs manager.** Once emulator sub-plugins ship, installs become multi-job: a dedicated page (pattern: `PanelBrowse`) listing queued/active/completed jobs with per-job progress, speed (from Quick Win #3's helper), pause/cancel, and a simple speed sparkline (`Polyline` over the rolling sample buffer — Steam's graph in 30 lines of WPF). Until then the Play-tab progress bar is fine. (L)

5. **Catalog-hosted art pipeline.** The hero/icon/thumbnail gaps (tracked in TODO_V2 + MASTER §4) shouldn't be solved by bundling PNGs: extend the already-hosted catalog repo (`CatalogUrl` mechanism exists) with `icon_url`/`hero_url` per entry, download lazily, cache to `Data/art/<gameId>/`, fall back to the current accent-color gradient. Art becomes a content update, not a release, and the binary stays small. (M code + ongoing asset curation)

---

## Anti-Recommendations

Steam features that would be **wrong** for this launcher (single developer, no backend beyond GitHub releases + the AP WebSocket):

- **Friends list / social graph / online presence.** Requires accounts and a presence server. The AP room *is* the social graph — the existing Other Players panel + per-player session views already cover it. Invest there instead.
- **Store, cart, payments, pricing.** AP games are mods over games people own; `PurchaseUrl` ("Get Game ↗") already hands off to real stores. Any commerce UI invites EULA/legal exposure the project has carefully engineered away from.
- **Cloud save sync.** No server to sync to, and D2 1.10f save handling is delicate (registry-pinned save path). A broken sync conflict can destroy a multiworld run; the user's own backup tooling does this better.
- **User reviews / ratings / community hubs.** Needs storage, identity, and moderation. Link to the AP Discord (already in the status bar) instead.
- **In-process game overlay (Steam-overlay style DLL injection).** Injecting UI into arbitrary games multiplies AV false positives — already a documented battle for this project — and 1.10f is fragile. The companion window (Bigger Bet #2) delivers the benefit safely.
- **Telemetry-driven recommendations ("Because you played…").** The launcher's no-telemetry stance (stated in `AchievementStore`'s header) is a feature; keep all heuristics local (recently-played, same-category suggestions from the catalog at most).
- **Big Picture / controller mode.** Playnite's fullscreen mode serves couch gaming; multiworld players sit at a desk with chat, trackers, and a keyboard. Skip until there is demand.
- **Mandatory accounts / login of any kind.** Steam's is a store requirement, not a UX feature. Frictionless local-only startup is a genuine advantage over Epic/EA — protect it.

---

## Source Notes

- Steam 2019 library redesign (collections, dynamic collections, shelves, What's New, Recent Games): store.steampowered.com/libraryupdate; Neowin/TechSpot coverage.
- Steam downloads UX (speed graph with adaptive scale, session peak, rolling ETA): Steam client + community documentation threads.
- GOG Galaxy 2.0 (bookmarked filtered views, Recent activity view, Atlas update's global search): gogalaxy.com, Atlas update coverage (Wccftech), GOG Galaxy feature posts.
- Playnite (automatic playtime tracking for all launched games, filter presets, fullscreen mode, MIT-licensed C#/WPF — closest architectural reference for this codebase): playnite.link, github.com/JosefNemec/Playnite.
- Heroic Games Launcher (HowLongToBeat + system requirements on game pages, accessibility/UI-scale menu, parallel downloads; PC Gamer: "miles better at being the Epic Games Launcher than the Epic Games Launcher"): pcgamer.com, Softpedia review.
- Epic Games Launcher criticisms (slow art loading, poor installed-state visibility in large libraries): PC Gamer / Windows Central comparisons.
- Command palette pattern (Slack's 2014 Quick Switcher popularized Ctrl+K; discoverability benefits): Mobbin glossary, uxpatterns.dev.
- Onboarding research (interactive do-the-thing checklists over passive tour screens; defer non-essentials): Appcues, Userpilot, UX Collective game-onboarding guides.
