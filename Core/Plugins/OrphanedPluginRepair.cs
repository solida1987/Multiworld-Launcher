using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Plugins;

/// Plugins London installed that did not come back after a start.
///
/// The installed-plugin registry says what should be here; the game registry
/// says what loaded. Anything in the first and not the second is an orphan:
/// a folder gone, a folder half-deleted by an update the old launcher could
/// not finish, an approval revoked, a build the new launcher cannot load.
/// Whatever the cause, the player did not ask for the game to disappear, and
/// the catalogue still has the plugin — so it is fetched and put back.
///
/// Ours goes back without a dialog, as an ordinary update would. Anybody
/// else's is offered through the same consent window a fresh install uses.
public static class OrphanedPluginRepair
{
    public sealed record Orphan(string GameId, string DisplayName, string Reason);
    public sealed record Result(bool Restored, string Message, LoadedPlugin? Plugin);

    /// Registered plugins that should exist but do not, with the best
    /// available one-line reason. Reads the disk; run it off the UI thread.
    public static IReadOnlyList<Orphan> Find()
    {
        var found = new List<Orphan>();
        foreach (var (gameId, entry) in InstalledPluginRegistry.All())
        {
            if (GameRegistry.Find(gameId) != null) continue;

            string name = string.IsNullOrWhiteSpace(entry.DisplayName) ? gameId : entry.DisplayName;
            string dir = PluginPackage.DirectoryFor(gameId);
            string reason;
            if (!Directory.Exists(dir))
                reason = "its plugin folder is gone";
            else if (!File.Exists(Path.Combine(dir, "plugin.json")))
                reason = "its plugin folder is incomplete";
            else
                reason = PluginTrustStore.Check(gameId, dir) switch
                {
                    PluginTrustStore.Verdict.Unknown => "its plugin is not approved on this computer",
                    PluginTrustStore.Verdict.Changed => "its plugin files changed since they were approved",
                    _ => "its plugin did not load",
                };
            found.Add(new Orphan(gameId, name, reason));
        }
        return found;
    }

    /// Fetch the catalogue's package for this game and install it.
    /// Never throws. Runs Install on the calling thread — call from the UI.
    public static async Task<Result> RestoreAsync(Orphan o, StoreIndex index,
                                                  System.Windows.Window? owner,
                                                  CancellationToken ct = default)
    {
        try
        {
            var game = index.Games.FirstOrDefault(g =>
                string.Equals(g.Id, o.GameId, StringComparison.OrdinalIgnoreCase));
            if (game is null || string.IsNullOrWhiteSpace(game.PluginUrl))
                return new Result(false,
                    $"{o.DisplayName}: not in the catalogue — use Add plugin to put it back.", null);

            var (path, msg) = await StoreCatalog.DownloadPluginAsync(game, ct);
            if (path == null)
                return new Result(false, $"{o.DisplayName}: the plugin could not be fetched — {msg}", null);

            var candidate = PluginPackage.Inspect(path);
            if (!candidate.IsUsable || candidate.Manifest is not { } m)
                return new Result(false,
                    $"{o.DisplayName}: the catalogue package is not usable — {candidate.Error}", null);
            if (!string.Equals(m.GameId, o.GameId, StringComparison.OrdinalIgnoreCase))
                return new Result(false,
                    $"{o.DisplayName}: the catalogue package names a different game "
                  + $"(\"{m.GameId}\") — not applied.", null);

            bool ours = FirstParty.For(o.GameId).IsFirstPartyPlugin
                     || PluginAutoUpdatePolicy.OursByAddress(game.PluginUrl);
            var outcome = ours
                ? PluginInstallFlow.Restore(candidate)
                : PluginInstallFlow.AddFromFile(owner, path);

            try { File.Delete(path); } catch (Exception) { }

            return outcome.Added
                ? new Result(true, $"{o.DisplayName} {m.Version} is back.", outcome.Plugin)
                : new Result(false, outcome.Message ?? $"{o.DisplayName}: not restored.", null);
        }
        catch (Exception ex)
        {
            return new Result(false, $"{o.DisplayName}: could not be restored — {ex.Message}", null);
        }
    }
}
