# Platform & Distribution Notes
_Updated 2026-06-11_

## Antivirus / SmartScreen posture

False positives killed user trust in V1 (multiple Discord reports), so the V2
build is configured to stay as scanner-friendly as an unsigned hobby exe can be:

| Measure | Where | Why |
|---|---|---|
| `EnableCompressionInSingleFile=false` | LauncherV2.csproj | A compressed single-file bundle looks like a packed/encrypted blob — the #1 heuristic trigger. Bigger on disk, dramatically fewer flags. |
| No UPX / no packers, ever | build process | Same reason — packed executables are auto-suspicious. (V1 lesson: UPX was disabled for 1.9.13.) |
| `PublishTrimmed=false` | csproj | Trimming produces unusual PE layouts some engines dislike; also avoids trim-related runtime breakage. |
| `PublishReadyToRun=true` | csproj | Native pre-JIT = normal-looking code sections + faster startup. |
| Full PE metadata (Company/Product/Copyright/Description/versions) | csproj | Anonymous exes score worse in every reputation system. |
| BCL-only dependencies | whole project | No exotic native DLLs to flag; WebSockets/JSON/P-Invoke are all stock .NET 8. |
| Self-contained runtime | csproj | No "downloads .NET at runtime" behavior, no embedded downloader heuristics beyond our documented updater. |

**What still triggers warnings and the only real fix:** the exe is unsigned, so
SmartScreen shows "unknown publisher" until enough reputation accumulates, and
the self-updater (downloads a zip + swaps the exe via a batch script) is
legitimate-but-updater-shaped behavior. The durable fix is an **EV code-signing
certificate** (tracked as 🔵 in TODO_V2.md — costs money, requires identity
verification, gives instant SmartScreen reputation). Until then:
- Ship release zips with published SHA256 (launcher_version.txt already
  carries the package hash and the updater verifies it before applying).
- Never change the AV-relevant csproj flags above without re-testing a fresh
  publish against VirusTotal.

## Windows support

Primary target. `net8.0-windows`, win-x64 single-file, self-contained — runs
on any Windows 10/11 x64 with no .NET install (V1's 1.9.10 lesson: ALWAYS
self-contained; framework-dependent builds stranded users without the runtime).

## Linux — honest status

**The launcher UI is WPF, and WPF is Windows-only. There is no flag or build
switch that makes the current exe run on Linux** (it does not run under Wine's
.NET either, by design assumption — untested and unsupported).

What a real Linux port looks like (tracked as the 🔵 "Avalonia migration"):

| Layer | Portability | Notes |
|---|---|---|
| `Core/` (ApClient, trackers, catalog, stores, updater) | ~95% portable | Pure BCL. Only Windows bits: registry pin (D2-specific), named-pipe naming (works on Unix sockets via NamedPipe* on Linux but D2 itself is Windows). |
| `Plugins/DiabloII` | Windows-only by nature | The game, the injector and the DLL are Windows. On Linux this would mean Wine orchestration — out of scope. |
| `Plugins/Emulated` (BizHawk) | Partially portable | BizHawk runs on Linux (Mono build); launch/args/pipe code would need platform branches. |
| `Plugins/OpenTTD` | Portable | The fork ships Linux builds. |
| `UI/` (WPF XAML + code-behind) | **Full rewrite** | Avalonia is XAML-like, so the *structure* ports, but every page, control, template and the WinForms tray icon must be redone. |
| `SystemTrayManager`, resize grip P/Invoke, registry | Replace | Avalonia has cross-platform tray support; Win32 P/Invokes go behind platform checks. |

Realistic effort: a focused multi-week project (the UI layer is ~6k lines).
Recommended timing: after V2 stabilizes on Windows (V3 cycle). Until then,
Linux users can play AP worlds through the official Archipelago client; the
launcher itself stays Windows.

## Build commands

| Purpose | Command |
|---|---|
| Dev build | `dotnet build -c Release` (or just run `Run Launcher.bat` — it builds first) |
| Release publish | `dotnet publish -c Release` → `bin/Release/net8.0-windows/win-x64/publish/` |
| Asset regeneration (procedural) | `powershell -ExecutionPolicy Bypass -File Tools_AssetGen/generate_assets.ps1` |
| Asset regeneration (AI/FLUX) | `python Tools_AssetGen/generate_ai_assets.py` (needs local ComfyUI on port 8001 — see script header) |
