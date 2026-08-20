using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace LauncherV2.Core.Plugins;

// Wraps every call into plugin code. First exception quarantines the
// plugin for the session: reported once, all later calls return defaults.

public sealed class SafePluginProxy : IGamePlugin
{
    private readonly IGamePlugin _inner;
    private readonly string      _label;

    // Captured at construction: once quarantined we still have to answer
    // "which game was that?" without calling back into the plugin.
    private readonly string _gameId;
    private readonly string _displayName;

    private volatile bool _quarantined;
    private string?       _reason;

    /// Raised the first time the plugin misbehaves. UI shows it on the game.
    public event Action<string, string>? Quarantined;   // (gameId, reason)

    public bool    IsQuarantined     => _quarantined;
    public string? QuarantineReason  => _reason;

    public SafePluginProxy(IGamePlugin inner, string fallbackId, string fallbackName)
    {
        _inner       = inner ?? throw new ArgumentNullException(nameof(inner));
        _gameId      = Get(() => inner.GameId,      fallbackId);
        _displayName = Get(() => inner.DisplayName, fallbackName);
        _label       = _displayName;

        // Subscribing is itself a call into the plugin.
        Guard(() => inner.LocationsChecked += OnLocations);
        Guard(() => inner.GameExited       += OnExited);
        Guard(() => inner.GoalCompleted    += OnGoal);
        Guard(() => inner.LogLine          += OnLogLine);
        Guard(() => inner.LocationsMissing += OnMissing);
        Guard(() => inner.StandaloneItemReceived += OnStandaloneItem);
    }

    private void Quarantine(Exception ex, string where)
    {
        if (_quarantined) return;
        _quarantined = true;
        _reason = $"{_label} failed in {where}: {ex.Message}";
        Debug.WriteLine("[plugin] QUARANTINE " + _reason);
        try { Quarantined?.Invoke(_gameId, _reason); } catch { /* nothing above us to tell */ }
    }

    // --- call wrappers ---

    private void Guard(Action call, [System.Runtime.CompilerServices.CallerMemberName] string where = "")
    {
        if (_quarantined) return;
        try { call(); } catch (Exception ex) { Quarantine(ex, where); }
    }

    private T Get<T>(Func<T> call, T fallback,
                     [System.Runtime.CompilerServices.CallerMemberName] string where = "")
    {
        if (_quarantined) return fallback;
        try { return call(); } catch (Exception ex) { Quarantine(ex, where); return fallback; }
    }

    /// Get's mirror for the settable members. A plugin that throws from a
    /// setter is as broken as one that throws from a getter, and gets the same
    /// treatment -- quarantined rather than allowed to take the launcher down.
    private void Set(Action call,
                     [System.Runtime.CompilerServices.CallerMemberName] string where = "")
    {
        if (_quarantined) return;
        try { call(); } catch (Exception ex) { Quarantine(ex, where); }
    }

    private async Task GuardAsync(Func<Task> call,
                                  [System.Runtime.CompilerServices.CallerMemberName] string where = "")
    {
        if (_quarantined) return;
        // Cancellation is the caller's decision, not a plugin fault — a player
        // pressing Stop must not brand the plugin as broken.
        try { await call().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Quarantine(ex, where); }
    }

    private async Task<T> GetAsync<T>(Func<Task<T>> call, T fallback,
                                      [System.Runtime.CompilerServices.CallerMemberName] string where = "")
    {
        if (_quarantined) return fallback;
        try { return await call().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Quarantine(ex, where); return fallback; }
    }

    // --- events, forwarded defensively both ways ---

    private void OnLocations(long[] ids) { try { LocationsChecked?.Invoke(ids); } catch (Exception ex) { Quarantine(ex, "LocationsChecked"); } }
    private void OnExited(int code)      { try { GameExited?.Invoke(code); }      catch (Exception ex) { Quarantine(ex, "GameExited"); } }
    private void OnGoal()                { try { GoalCompleted?.Invoke(); }       catch (Exception ex) { Quarantine(ex, "GoalCompleted"); } }
    private void OnLogLine(string line)  { try { LogLine?.Invoke(line); }         catch (Exception ex) { Quarantine(ex, "LogLine"); } }
    private void OnMissing(long[] ids)   { try { LocationsMissing?.Invoke(ids); } catch (Exception ex) { Quarantine(ex, "LocationsMissing"); } }
    private void OnStandaloneItem(string s) { try { StandaloneItemReceived?.Invoke(s); } catch (Exception ex) { Quarantine(ex, "StandaloneItemReceived"); } }

    public event Action<long[]>? LocationsChecked;
    public event Action<int>?    GameExited;
    public event Action?         GoalCompleted;
    public event Action<string>? LogLine;
    public event Action<long[]>? LocationsMissing;
    public event Action<string>? StandaloneItemReceived;

    // --- identity ---

    public string  GameId      => _gameId;
    public string  DisplayName => _displayName;
    public string  Subtitle    => Get(() => _inner.Subtitle, "");
    public string  IconPath    => Get(() => _inner.IconPath, "");

    // --- version state ---

    public string? InstalledVersion => Get(() => _inner.InstalledVersion, null);
    public string  GameDirectory    => Get(() => _inner.GameDirectory, "");
    public string? AvailableVersion => Get(() => _inner.AvailableVersion, null);
    public bool    IsInstalled      => Get(() => _inner.IsInstalled, false);
    // A quarantined plugin is never "running" — otherwise the one-game-at-a-time
    // rule would be held hostage by a plugin that broke while starting.
    public bool    IsRunning        => !_quarantined && Get(() => _inner.IsRunning, false);

    // --- lifecycle ---

    public Task CheckForUpdateAsync(CancellationToken ct = default)
        => GuardAsync(() => _inner.CheckForUpdateAsync(ct));

    public Task InstallOrUpdateAsync(IProgress<(int Pct, string Msg)> progress, CancellationToken ct = default)
        => GuardAsync(() => _inner.InstallOrUpdateAsync(progress, ct));

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => GetAsync(() => _inner.VerifyInstallAsync(ct), false);

    public string? ValidateExistingInstall(string folder)
        // A plugin that cannot answer must not silently accept the folder.
        => Get(() => _inner.ValidateExistingInstall(folder), "this plugin could not check the folder");

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
        => GuardAsync(() => _inner.LaunchAsync(session, ct));

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
        => GuardAsync(() => _inner.LaunchStandaloneAsync(ct));

    public Task StopAsync() => GuardAsync(() => _inner.StopAsync());

    public bool    SupportsStandalone => Get(() => _inner.SupportsStandalone, false);
    public bool    IsWebBased         => Get(() => _inner.IsWebBased, false);
    public bool    ConnectsItself     => Get(() => _inner.ConnectsItself, false);
    public string? BuiltAgainstDataPackageChecksum => Get(() => _inner.BuiltAgainstDataPackageChecksum, null);

    // --- AP bridge ---

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => GuardAsync(() => _inner.ReceiveItemsAsync(items, index, ct));

    public void OnApStateChanged(ApConnectionState state)
        => Guard(() => _inner.OnApStateChanged(state));

    public void OnSlotData(System.Text.Json.JsonElement slotData)
        => Guard(() => _inner.OnSlotData(slotData));

    public void OnLocationTable(IReadOnlyDictionary<string, long> nameToId)
        => Guard(() => _inner.OnLocationTable(nameToId));

    public void OnLocationHints(IReadOnlyDictionary<long, string> idToLabel)
        => Guard(() => _inner.OnLocationHints(idToLabel));

    public void OnCheckedLocations(long[] locationIds)
        => Guard(() => _inner.OnCheckedLocations(locationIds));

    // --- UI ---
    // A panel that throws while being built would take down the whole settings
    // page, so the plugin loses its tab rather than the launcher losing the tab
    // strip.

    public UIElement? CreateSettingsPanel()   => Get<UIElement?>(() => _inner.CreateSettingsPanel(), null);
    public bool       SupportsMapTracker      => Get(() => _inner.SupportsMapTracker, false);
    public UIElement? CreateMapTrackerPanel() => Get<UIElement?>(() => _inner.CreateMapTrackerPanel(), null);

    // --- catalog / presentation ---

    public string   Description      => Get(() => _inner.Description, "");

    // Deliberately dropped for plugins, and this is not a limitation to fix.
    // Both fields are URLs. Honouring them would have the launcher fetch media
    // from an address a third party chose — the launcher downloading something
    // on a plugin's say-so. That is the exact shape of the problem that started
    // all of this. A plugin shows what it shipped inside its own package, which
    // the player already saw when they approved it, or it shows nothing.
    public string?  VideoPreviewUrl  => null;
    public string[] ScreenshotUrls   => Array.Empty<string>();
    public string   ApWorldName      => Get(() => _inner.ApWorldName, "");
    public string   ThemeAccentColor => Get(() => _inner.ThemeAccentColor, "#3A4060");
    public string[] GameBadges       => Get(() => _inner.GameBadges, Array.Empty<string>());

    public Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
        => GetAsync(() => _inner.GetNewsAsync(ct), Array.Empty<NewsItem>());

    // --- install shape ---
    //
    // Every member below forwards to the plugin. A member that is NOT here
    // silently resolves to the interface default instead, which reaches the
    // player as a game page that is simply missing that part -- with no error
    // anywhere. Adding a member to IGamePlugin means adding it here too.
    //
    // Each fallback is the answer that is safe when the plugin is broken, and
    // that is not always "empty": a scan that cannot run must say "could not
    // tell" (null), never "healthy" (empty).

    public InstallCapability InstallCapability
        => Get(() => _inner.InstallCapability, InstallCapability.AutoInstall);

    public bool    IsFreeGame  => Get(() => _inner.IsFreeGame, false);
    public string? PurchaseUrl => Get(() => _inner.PurchaseUrl, null);
    public string? WebsiteUrl  => Get(() => _inner.WebsiteUrl, null);

    // Read AFTER a launch, so these must come from the real plugin: the
    // interface default is null, and a null here is indistinguishable from
    // "nothing happened" -- which is exactly what an unpatched ROM looks like.
    public string? SessionRomNote       => Get(() => _inner.SessionRomNote, null);
    public string? ActivePatchedRomPath => Get(() => _inner.ActivePatchedRomPath, null);

    // Whether this game has a ROM library, and the two launch diagnostics. Not
    // forwarding these is how a catalogue game ends up with no ROMs tab and no
    // warning when its connector never attached.
    public bool  UsesRomLibrary      => Get(() => _inner.UsesRomLibrary, false);
    public bool  ChecksImplemented    => Get(() => _inner.ChecksImplemented, true);
    public bool? ApConnectorAttached  => Get<bool?>(() => _inner.ApConnectorAttached, null);

    public string? RomPath
    {
        get => Get(() => _inner.RomPath, null);
        set => Set(() => _inner.RomPath = value);
    }

    public LauncherV2.Plugins.Emulated.RomRequirement? GetUnmetRomRequirement()
        => Get<LauncherV2.Plugins.Emulated.RomRequirement?>(() => _inner.GetUnmetRomRequirement(), null);

    public bool PromptForRomFile()
        => Get(() => _inner.PromptForRomFile(), false);

    public string? TryImportLocatedRom(string sourcePath,
                                       LauncherV2.Plugins.Emulated.RomRequirement req)
        => Get(() => _inner.TryImportLocatedRom(sourcePath, req), null);

    // Fallback null = "nothing to ask". A broken plugin must not block a launch
    // with a question the player cannot satisfy; the launch itself will report
    // the missing patch through SessionRomNote.
    public string? LastSlotName
    {
        get => Get<string?>(() => _inner.LastSlotName, null);
        set { try { _inner.LastSlotName = value; } catch { /* plugin fault, not ours */ } }
    }

    public string? SelectedEmulatorId
    {
        get => Get<string?>(() => _inner.SelectedEmulatorId, null);
        set { try { _inner.SelectedEmulatorId = value; } catch { /* plugin fault, not ours */ } }
    }

    // Fails CLOSED: a plugin that throws here is not "ready", it is broken.
    public bool RomReady => Get<bool>(() => _inner.RomReady, false);

    public bool EmulatorReady => Get<bool>(() => _inner.EmulatorReady, false);

    public IReadOnlyList<EmulatorBackend> AvailableBackends()
        => Get<IReadOnlyList<EmulatorBackend>>(
               () => _inner.AvailableBackends(), Array.Empty<EmulatorBackend>());

    public SeedPatchRequest? GetUnmetSeedPatch(string seed, string slot)
        => Get<SeedPatchRequest?>(() => _inner.GetUnmetSeedPatch(seed, slot), null);

    public string? ImportSeedPatch(string sourcePath, string seed, string slot)
        => Get(() => _inner.ImportSeedPatch(sourcePath, seed, slot),
               "the plugin could not store the patch");

    // Set by the launcher after the AP handshake. These are the connector's
    // entire picture of the room -- forwarding the getter but not the setter
    // would leave the plugin reading its own untouched nulls.
    public Func<System.Text.Json.JsonElement?>? GetSlotData
    {
        get => Get<Func<System.Text.Json.JsonElement?>?>(() => _inner.GetSlotData, null);
        set => Set(() => _inner.GetSlotData = value);
    }

    public Func<long[]?>? GetServerLocations
    {
        get => Get<Func<long[]?>?>(() => _inner.GetServerLocations, null);
        set => Set(() => _inner.GetServerLocations = value);
    }

    public Func<int>? GetOwnSlot
    {
        get => Get<Func<int>?>(() => _inner.GetOwnSlot, null);
        set => Set(() => _inner.GetOwnSlot = value);
    }

    public Func<string?>? GetSeedName
    {
        get => Get<Func<string?>?>(() => _inner.GetSeedName, null);
        set => Set(() => _inner.GetSeedName = value);
    }

    public IReadOnlyList<GameComponent> DetectComponents()
        => Get(() => _inner.DetectComponents(), Array.Empty<GameComponent>());

    public IReadOnlyList<GameComponent> DetectComponentsAdopting()
        => Get(() => _inner.DetectComponentsAdopting(), Array.Empty<GameComponent>());

    public bool HasComponentSetup => Get(() => _inner.HasComponentSetup, false);

    public void ShowComponentSetup(System.Windows.Window? owner)
        => Guard(() => _inner.ShowComponentSetup(owner));

    // null = could not tell. Empty would claim the install is healthy.
    public Task<IReadOnlyList<IGamePlugin.InstallProblem>?> ScanInstallProblemsAsync(
            CancellationToken ct = default)
        => GetAsync(() => _inner.ScanInstallProblemsAsync(ct),
                    (IReadOnlyList<IGamePlugin.InstallProblem>?)null);

    // A repair that never ran restored nothing: every file is unrepairable.
    public Task<(IReadOnlyList<string> Restored, IReadOnlyList<string> Unrepairable)>
        RepairFilesAsync(IEnumerable<string> paths,
                         IProgress<(int Pct, string Msg)> progress,
                         CancellationToken ct = default)
        => GetAsync(() => _inner.RepairFilesAsync(paths, progress, ct),
                    ((IReadOnlyList<string>)Array.Empty<string>(),
                     (IReadOnlyList<string>)paths.ToList()));

    // --- the original game a mod is built from ---

    public BaseGameFolderRequest? NeedsBaseGameFolder()
        => Get<BaseGameFolderRequest?>(() => _inner.NeedsBaseGameFolder(), null);

    // A broken plugin must not make the launcher demand a folder it cannot use.
    public bool HasBaseGameFiles() => Get(() => _inner.HasBaseGameFiles(), true);

    public void SetBaseGameFolder(string folder)
        => Guard(() => _inner.SetBaseGameFolder(folder));

    // --- files the game cannot start without ---

    public IReadOnlyList<string> GetMissingCriticalFiles()
        => Get(() => _inner.GetMissingCriticalFiles(), Array.Empty<string>());

    public string? MissingCriticalFilesCause
        => Get(() => _inner.MissingCriticalFilesCause, null);

    public Task<int> RepairMissingCriticalFilesAsync(IProgress<(int Pct, string Msg)> progress)
        => GetAsync(() => _inner.RepairMissingCriticalFilesAsync(progress), 0);

    // false = "not an antivirus problem", so the launcher shows its own error
    // rather than swallowing one the plugin never handled.
    public Task<bool> TryHandleAntivirusBlockAsync(Window owner, Exception failure)
        => GetAsync(() => _inner.TryHandleAntivirusBlockAsync(owner, failure), false);

    // --- the live Archipelago session ---

    public void OnApServicesAttached(IApServices? services)
        => Guard(() => _inner.OnApServicesAttached(services));

    public void OnApSessionChanged(ApSessionContext? session)
        => Guard(() => _inner.OnApSessionChanged(session));

    public Task OnDeathLinkReceivedAsync(string source, string cause)
        => GuardAsync(() => _inner.OnDeathLinkReceivedAsync(source, cause));

    public bool SendsDeathLink => Get(() => _inner.SendsDeathLink, false);

    public System.Text.Json.JsonElement? GetLocationDataPackage()
        => Get<System.Text.Json.JsonElement?>(() => _inner.GetLocationDataPackage(), null);

    public long[] GetStandaloneLocationUniverse()
        => Get(() => _inner.GetStandaloneLocationUniverse(), Array.Empty<long>());

    // --- what the game page draws ---

    public Action<Window, ApSessionContext>? ItemActions
        => Get<Action<Window, ApSessionContext>?>(() => _inner.ItemActions, null);

    public IReadOnlyList<GameCommand> GetCommands()
        => Get(() => _inner.GetCommands(), Array.Empty<GameCommand>());

    public IReadOnlyList<KnownIssue> KnownIssues
        => Get(() => _inner.KnownIssues, Array.Empty<KnownIssue>());

    public IReadOnlyList<GameCredit> Credits
        => Get(() => _inner.Credits, Array.Empty<GameCredit>());

    public string? HeaderArtPath => Get(() => _inner.HeaderArtPath, null);

    // Falls back to the game id, exactly as the interface does -- achievement
    // ids are stored raw, so a wrong prefix would merge two games' records.
    public string AchievementIdPrefix => Get(() => _inner.AchievementIdPrefix, _gameId);

    public IReadOnlyList<GameAchievement> ExtraAchievements
        => Get(() => _inner.ExtraAchievements, Array.Empty<GameAchievement>());

    public (string Title, string Description, string Icon)? GoalAchievement
        => Get<(string, string, string)?>(() => _inner.GoalAchievement, null);

    /// Stop listening, so the context can be unloaded.
    public void Detach()
    {
        try
        {
            _inner.LocationsChecked -= OnLocations;
            _inner.GameExited       -= OnExited;
            _inner.GoalCompleted    -= OnGoal;
        }
        catch { /* detaching a broken plugin is best effort */ }
    }
}
