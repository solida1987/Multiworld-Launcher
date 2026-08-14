using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace LauncherV2.Core.Plugins;

// Every call into a third-party plugin is a call into code nobody reviewed.
//
// D2Plugin is ours; if it throws, the launcher falls over and that is fair,
// because it is our bug and we want to see it. A plugin somebody else wrote
// must not have that power. One bad plugin should lose its own game tile, not
// take the whole library, the AP session and every other game down with it.
//
// So every member is wrapped. The first exception quarantines the plugin: it
// stops being called, its tile shows why, and the rest of the launcher carries
// on. Events matter as much as methods here — a plugin that throws inside
// LocationsChecked would otherwise take the AP thread with it.

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

    /// <summary>Raised the first time the plugin misbehaves. UI shows it on the game.</summary>
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

    public event Action<long[]>? LocationsChecked;
    public event Action<int>?    GameExited;
    public event Action?         GoalCompleted;

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

    /// <summary>Stop listening, so the context can be unloaded.</summary>
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
