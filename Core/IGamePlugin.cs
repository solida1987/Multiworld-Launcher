using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace LauncherV2.Core;

// IGamePlugin — the contract every game plugin implements.
//
// The launcher core knows nothing about specific games. Nearly every member
// has a default that means "this game does not do that", and the launcher
// simply does not draw what a plugin does not answer.
//
// Full documentation, contracts and the list of required members: PLUGIN_API.md.
// Async methods are fire-and-forget from the UI thread; events fire on
// arbitrary threads and handlers must marshal to UI themselves.

// What the launcher can actually do for this game — drives badges and buttons.
public enum InstallCapability
{
    // Launcher downloads game + mod; click Install and play.
    AutoInstall,

    // User owns the base game; the launcher installs the mod on top.
    AutoMod,

    // Launcher installs emulator + mod; the user supplies the ROM.
    RomRequired,

    // Cannot be automated — install guide and links only.
    ManualSetup,
}

public enum ComponentNeed
{
    // The game cannot start without it.
    Required,

    // The game runs fine; this only makes it nicer.
    Optional,
}

// One component the game needs beside its own files (renderer, sound driver…).
// Present = there AND able to do its job, not merely on disk.
public sealed record GameComponent(
    string Name,
    bool Present,
    ComponentNeed Need,
    string Status,
    string? Advice = null,
    string? Url = null);

// A game asking for the player's own copy of the base game it modifies.
// The wording is the plugin's; the dialog and picker are the launcher's.
public sealed record BaseGameFolderRequest(
    string Title,
    string Explanation,
    string PickerTitle,
    string? InitialDirectory = null);

// The live AP session, as presented to the game's own UI. ItemNames and
// LocationNamesInSeed are what THIS seed contains — offering names the seed
// does not use means offering server errors as buttons.
public sealed record ApSessionContext(
    string SlotName,
    IReadOnlyCollection<string> ItemNames,
    IReadOnlyCollection<string> LocationNamesInSeed,
    int HintPoints,
    int HintCostPoints,
    Func<string, Task> Send);

// A game-defined achievement. IsMet reads the launcher's own bookkeeping.
public sealed record GameAchievement(
    string Id,
    string Title,
    string Description,
    string Icon,
    string Tier,                       // "bronze" | "silver" | "gold" | "platinum"
    Func<AchievementSystem.AchievementStore, bool> IsMet);

// A known bug with a player-performable workaround, shown as a card.
public sealed record KnownIssue(string Symptom, string Cause, string Fix);

// A game asking for the file belonging to the seed just connected to.
// Seed and Slot are shown verbatim so the player can match them against what
// they downloaded; FileFilter is the picker's filter (".apemerald" etc.).
//
// WhatToPick names the thing being asked for when it is not a patch: the games
// whose world builds the ROM itself need "the randomized game file for this
// seed", and calling that a patch sends the player looking for the wrong file.
// Null keeps the classic patch wording.
public sealed record SeedPatchRequest(
    string GameName,
    string Seed,
    string Slot,
    string FileFilter,
    string? WhatToPick = null);

// One credit line on the Overview. Highlight lifts a line visually.
public sealed record GameCredit(string Role, string Name, bool Highlight = false);

// One named button in the Overview action row. NeedsInstall=false for things
// done before installing (e.g. writing a settings YAML).
public sealed record GameCommand(
    string Label,
    string Tooltip,
    Action<Window> Run,
    bool NeedsInstall = true);

public interface IGamePlugin
{
    // --- Identity ---

    // Stable key, lowercase ASCII, no spaces. Must equal plugin.json.
    string GameId { get; }

    string DisplayName { get; }

    string Subtitle { get; }

    // Absolute path to a 256×256 PNG shipped in the package.
    string IconPath { get; }

    // --- Version state (read on every library refresh — keep cheap) ---

    string? InstalledVersion { get; }

    // Empty string when not installed.
    string GameDirectory { get; }

    string? AvailableVersion { get; }

    bool IsInstalled { get; }

    bool IsRunning { get; }

    // --- Lifecycle ---

    // Must not throw on network failure — set AvailableVersion null and return.
    Task CheckForUpdateAsync(CancellationToken ct = default);

    // Must be idempotent; called again when already up to date.
    Task InstallOrUpdateAsync(IProgress<(int Pct, string Msg)> progress,
                              CancellationToken ct = default);

    // Check only; never repair. The caller decides.
    Task<bool> VerifyInstallAsync(CancellationToken ct = default);

    // Null = the folder the player picked looks right; otherwise a short
    // reason shown to them so they can pick again.
    string? ValidateExistingInstall(string folder) => null;

    // The launcher's AP session is already connected when this is called.
    Task LaunchAsync(ApSession session, CancellationToken ct = default);

    // Shows the "Launch Standalone" button.
    bool SupportsStandalone => false;

    // Browser games with no local install. Never auto-added to a library.
    bool IsWebBased => false;

    // --- What the launcher can do, and where the base game comes from ---

    InstallCapability InstallCapability => InstallCapability.AutoInstall;

    // Changes "Get the game" to "Free — get it".
    bool IsFreeGame => false;

    // Null hides the button.
    string? PurchaseUrl => null;

    // Null hides the link.
    string? WebsiteUrl => null;

    // --- Components ---

    // One badge each; launch is refused while anything Required is missing.
    IReadOnlyList<GameComponent> DetectComponents() => Array.Empty<GameComponent>();

    // Same list, but may first copy files from the player's own base game.
    // Split from DetectComponents: a question asked to DECIDE things should
    // not rearrange the disk while answering.
    IReadOnlyList<GameComponent> DetectComponentsAdopting() => DetectComponents();

    // A guided walkthrough for the components above: one screen per component,
    // naming its author and licence, with the player pressing yes before
    // anything is fetched. False means this game has none and the launcher
    // shows no such offer.
    //
    // The launcher only ever OFFERS this. Nothing here downloads by itself --
    // that is the whole point of asking per component.
    bool HasComponentSetup => false;

    void ShowComponentSetup(System.Windows.Window? owner) { }

    // --- Install problems and repair ---

    // Path is relative to GameDirectory; Reason is shown to the player verbatim.
    public sealed record InstallProblem(string Path, string Reason);

    // File-by-file integrity scan. Empty = healthy. Null = COULD NOT TELL
    // (offline, no manifest) — callers must not treat null as failure.
    Task<IReadOnlyList<InstallProblem>?> ScanInstallProblemsAsync(
            CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<InstallProblem>?>(null);

    // Restore the named files. Unrepairable = not in the release package,
    // which is reported differently from a failed download.
    Task<(IReadOnlyList<string> Restored, IReadOnlyList<string> Unrepairable)>
        RepairFilesAsync(IEnumerable<string> paths,
                         IProgress<(int Pct, string Msg)> progress,
                         CancellationToken ct = default)
        => Task.FromResult<(IReadOnlyList<string>, IReadOnlyList<string>)>(
               (Array.Empty<string>(), paths.ToList()));

    // DeathLink receive side; send side is IApServices.ReportDeath.
    Task OnDeathLinkReceivedAsync(string source, string cause) => Task.CompletedTask;

    // --- The base game this one modifies ---

    // Null = nothing to ask (none needed, already known, or just found).
    // Try to detect it before asking — see PLUGIN_API.md.
    BaseGameFolderRequest? NeedsBaseGameFolder() => null;

    // False sends the launcher back through the request and a reinstall.
    bool HasBaseGameFiles() => true;

    // The picked folder was accepted. The plugin persists it; the launcher
    // keeps no copy.
    void SetBaseGameFolder(string folder) { }

    // --- The live Archipelago session ---

    // Everything the game can ask of the live connection, or null when the
    // session ends. See IApServices.
    void OnApServicesAttached(IApServices? services) { }

    // Session presentation state (slot, hint economy, in-seed names).
    // Cheap and idempotent by contract — over-calling is the safe failure.
    void OnApSessionChanged(ApSessionContext? session) { }

    // The Items tab's hint/cheat dialog. Null hides the button.
    Action<Window, ApSessionContext>? ItemActions => null;

    // --- What the last launch did to the game file ---
    //
    // These live on the INTERFACE, not just on EmulatorPlugin, because every
    // plugin loaded from a package is wrapped in SafePluginProxy -- and the
    // proxy is not an EmulatorPlugin. A host that reaches for these with
    // `plugin is EmulatorPlugin` finds them on the launcher's own built-in
    // games and never on a plugin, which reads as "this game had nothing to
    // say" rather than as a bug.

    // True when the player supplies their own game file and the launcher keeps
    // a per-seed patched copy: it shows the ROMs tab and the seed library.
    bool UsesRomLibrary => false;

    // False turns on the "checks are not detected yet" warning at launch. The
    // default is true because a game that says nothing is a finished one; the
    // emulator base class overrides it to false until a RAM map is measured.
    bool ChecksImplemented => true;

    // Whether the in-game connector script actually attached. Null means the
    // question does not apply to this game, which is not the same as "no" --
    // answering false here would warn every native game about a script it does
    // not have.
    bool? ApConnectorAttached => null;

    // One line about ROM preparation: patched for this slot, played unpatched,
    // or patching failed. Null when there was nothing to say.
    string? SessionRomNote => null;

    // The per-seed file this session is actually running, when it differs from
    // the player's own library copy. This is the key the seed library uses to
    // attribute playtime and completion, so a null here loses both silently.
    string? ActivePatchedRomPath => null;

    // The player's own game file, and the question "is it there and is it the
    // right one?". A game without a ROM library answers null/null and the
    // launcher draws nothing.
    string? RomPath { get => null; set { } }

    LauncherV2.Plugins.Emulated.RomRequirement? GetUnmetRomRequirement() => null;

    // Ask the player for their file, and take a file they already located.
    // Both return the stored path, or null/false when nothing was chosen.
    bool PromptForRomFile() => false;
    string? TryImportLocatedRom(string sourcePath,
                                LauncherV2.Plugins.Emulated.RomRequirement req) => null;

    // --- The per-seed patch ---
    //
    // The player's own ROM is the game; the patch is THIS multiworld. The two
    // are supplied at completely different times: the ROM once, ever, and a
    // patch per seed they join.
    //
    // Null = we already have the patch for this (seed, slot), or this game does
    // not use one. Non-null asks the player for it, once, and the answer is
    // remembered -- see SeedPatchStore for why the link cannot be read out of
    // the patch file itself.
    SeedPatchRequest? GetUnmetSeedPatch(string seed, string slot) => null;

    // Store a patch the player just picked. Returns null on success, or a
    // reason to show them: wrong game, wrong slot, not a patch at all.
    string? ImportSeedPatch(string sourcePath, string seed, string slot) => null;

    // --- What the launcher hands the running game ---
    //
    // Set at launch, once the AP handshake is done, so the game side can be
    // told its slot number, the room's location list, the slot data its world
    // generated, and which seed this is. Everything the connector writes into
    // ap_config.json comes from these four.
    //
    // Default setters deliberately DISCARD: a game that does not talk to a
    // connector has nothing to do with the values, and should not have to
    // declare four properties to say so. But a game that DOES use them and is
    // loaded as a plugin only ever sees them through SafePluginProxy -- which
    // is why they belong here and not only on EmulatorPlugin. Unforwarded, the
    // connector starts with slot 0, no locations and no slot data, and simply
    // never recognises a check.
    Func<System.Text.Json.JsonElement?>? GetSlotData { get => null; set { } }
    Func<long[]?>? GetServerLocations             { get => null; set { } }
    Func<int>? GetOwnSlot                          { get => null; set { } }
    Func<string?>? GetSeedName                     { get => null; set { } }

    // --- Locations, without a server ---

    // The game's own location table in DataPackage shape, for standalone runs.
    System.Text.Json.JsonElement? GetLocationDataPackage() => null;

    // Every location that EXISTS in the standalone run about to start.
    // Called after LaunchStandaloneAsync — the answer depends on the settings
    // that launch just wrote.
    long[] GetStandaloneLocationUniverse() => Array.Empty<long>();

    // Locations that exist but are not yet checked.
    event Action<long[]>? LocationsMissing;

    // A standalone run handed the player something (no server to route it).
    event Action<string>? StandaloneItemReceived;

    // --- Achievements ---

    // Prefix for generated achievement ids. FROZEN once shipped: earned
    // achievements are stored by id, changing this un-earns everything.
    string AchievementIdPrefix => GameId;

    // Replaces the generic first-goal achievement with the game's actual win
    // condition. Null keeps the generic one.
    (string Title, string Description, string Icon)? GoalAchievement => null;

    // Gates the DeathLink achievement — never offer one nobody can earn.
    bool SendsDeathLink => false;

    IReadOnlyList<GameAchievement> ExtraAchievements
        => Array.Empty<GameAchievement>();

    // --- About the game ---

    // Wide banner behind the page header. Null = generic banner, then tint.
    string? HeaderArtPath => null;

    // Empty hides the card.
    IReadOnlyList<KnownIssue> KnownIssues => Array.Empty<KnownIssue>();

    // Empty hides the credit lines.
    IReadOnlyList<GameCredit> Credits => Array.Empty<GameCredit>();

    // --- Critical files, right before launch ---
    // Separate from the integrity scan: antivirus quarantine strikes BETWEEN
    // install and launch, so an install-time check proves nothing.

    // Files the game cannot start without. Empty = ready.
    IReadOnlyList<string> GetMissingCriticalFiles() => Array.Empty<string>();

    // One sentence on what usually removes them, shown in the repair prompt.
    string? MissingCriticalFilesCause => null;

    // Returns how many came back.
    Task<int> RepairMissingCriticalFilesAsync(IProgress<(int Pct, string Msg)> progress)
        => Task.FromResult(0);

    // --- Antivirus ---

    // A launch failed and looks like antivirus. True = the game handled it
    // and showed its own UI; false = launcher shows its generic dialog.
    Task<bool> TryHandleAntivirusBlockAsync(Window owner, Exception failure)
        => Task.FromResult(false);

    // --- Commands ---

    // Buttons in the Overview action row. Called on every page draw.
    IReadOnlyList<GameCommand> GetCommands() => Array.Empty<GameCommand>();

    // The plugin's line into the launcher's log. The plugin writes the whole
    // line, prefix and all.
    event Action<string>? LogLine;

    // True when the game ships its own AP client. The launcher then stays off
    // the slot entirely (AP servers kick the older connection per slot) and
    // launches with credential prefill only.
    bool ConnectsItself => false;

    // The AP datapackage checksum this integration was built against; the
    // launcher warns when the server announces a different one. Null = n/a.
    string? BuiltAgainstDataPackageChecksum => null;

    // Only used when SupportsStandalone is true.
    Task LaunchStandaloneAsync(CancellationToken ct = default) => Task.CompletedTask;

    // Stop cleanly, then make sure the process is gone.
    Task StopAsync();

    // --- AP bridge, launcher-owned connection ---

    // The game completed checks (numeric AP location ids). Forwarded to the server.
    event Action<long[]>? LocationsChecked;

    // The game process exited, with its exit code.
    event Action<int>? GameExited;

    // The Archipelago goal is complete.
    event Action? GoalCompleted;

    // Items arrived for this slot. index = AP resume index (dedup contract).
    Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default);

    void OnApStateChanged(ApConnectionState state);

    // The seed's slot_data arrived.
    void OnSlotData(System.Text.Json.JsonElement slotData) { }

    // The seed's location name -> id table, for games that report by name.
    void OnLocationTable(IReadOnlyDictionary<string, long> nameToId) { }

    // Scout replies: location id -> "player (game)".
    void OnLocationHints(IReadOnlyDictionary<long, string> idToLabel) { }

    // --- UI (both called on the UI thread only) ---

    // Null = no settings gear.
    UIElement? CreateSettingsPanel();

    // Shows the Map tab.
    bool SupportsMapTracker => false;

    // Only called when SupportsMapTracker is true.
    UIElement? CreateMapTrackerPanel() => null;

    // --- Presentation ---

    // One-paragraph description on the Overview.
    string Description { get; }

    // Ignored for disk-loaded plugins (remote media is never fetched).
    string? VideoPreviewUrl { get; }

    // Ignored for disk-loaded plugins.
    string[] ScreenshotUrls { get; }

    // The game name as Archipelago knows it (DataPackage lookup).
    string ApWorldName { get; }

    // Hex accent blended into the header background at ~25%.
    string ThemeAccentColor { get; }

    // 1-2 short badges next to the version badges, e.g. ["Requires D2"].
    string[] GameBadges { get; }

    // Patch notes / announcements, newest first, at most 20.
    Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default);
}

// One entry on a game's news page.
public sealed record NewsItem(
    string Title,        // e.g. "Beta 1.9.14 released"
    string Body,         // markdown body
    string Version,      // e.g. "1.9.14"
    DateTimeOffset Date, // publish date
    string? Url          // link to full release page, or null
);

public enum ApConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

// Connection credentials for one AP slot.
public sealed record ApSession(
    string ServerUri,   // e.g. "archipelago.gg:38281" or "ws://localhost:38281"
    string SlotName,
    string Password,    // "" if none
    string Game,        // AP game name, e.g. "Diablo II Archipelago"
    string? Uuid = null // auto-generated if null
);

// One item received from the AP server.
public sealed record ApNetworkItem(
    long ItemId,
    long LocationId,
    int  Player,      // AP player slot that found it
    int  Flags        // AP item classification flags
);
