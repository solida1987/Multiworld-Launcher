using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Plugins;

///
/// Plugins installed before update feeds existed, and how to get them moving
/// again.
///
/// ⚠⚠ THE FAILURE THIS EXISTS FOR
///
/// A plugin says where to check for its own newer builds, in its manifest's
/// `update` block. Plugins built before that block existed carry no address —
/// and the update check skips anything with nothing to ask. Silently. Forever.
///
/// Measured 2 September 2026: a player on Diablo II plugin 1.1.0 while 1.2.12
/// was published. London was current, the game was current, the feed was
/// correct, and none of it could reach them, because the one file that says
/// where to look was written before the file existed. Nothing in the launcher
/// could ever have repaired that — the only escape was reinstalling by hand,
/// which is exactly what they were eventually told to do.
///
/// THE WAY OUT
///
/// The catalogue knows where every game's plugin is published, independently
/// of what the installed copy happens to remember. So for a plugin with no
/// address of its own, London asks the catalogue instead, fetches what is
/// published there, and compares versions by reading the package. The package
/// it fetches carries a modern manifest, so once this has run the plugin has
/// an address of its own and the ordinary machinery takes over again.
///
/// It is a repair, not a routine: it costs one small download per feed-less
/// plugin, and it only ever runs while one is still on the machine.
///
public static class LegacyPluginRepair
{
    public sealed record Candidate(
        string GameId,
        string DisplayName,
        string InstalledVersion,
        string NewVersion,
        string PackagePath,
        /// The catalogue address it came from. With no update block to judge,
        /// this is the only evidence of whose plugin this is.
        string SourceUrl);

    /// Installed plugins that have no address to check against.
    public static IReadOnlyList<PluginManifest> WithoutFeed()
    {
        try
        {
            return PluginPackage.Installed()
                .Select(x => x.Manifest)
                .Where(m => !PluginUpdater.HasFeed(m))
                .ToList();
        }
        catch (Exception) { return Array.Empty<PluginManifest>(); }
    }

    ///
    /// What the catalogue publishes for this game, if it is newer than what is
    /// installed. Null for every ordinary outcome — not in the catalogue, not
    /// reachable, not actually newer — because none of them is worth a word to
    /// the player.
    ///
    public static async Task<Candidate?> FindNewerAsync(
        PluginManifest installed, StoreIndex? index, CancellationToken ct = default)
    {
        try
        {
            var game = index?.Games.FirstOrDefault(g =>
                string.Equals(g.Id, installed.GameId, StringComparison.OrdinalIgnoreCase));
            if (game is null || string.IsNullOrWhiteSpace(game.PluginUrl)) return null;

            var (path, _) = await StoreCatalog.DownloadPluginAsync(game, ct)
                                              .ConfigureAwait(false);
            if (path == null) return null;

            var candidate = PluginPackage.Inspect(path);
            if (!candidate.IsUsable || candidate.Manifest is not { } fresh) return null;

            // The catalogue naming a different game than the one installed is
            // not an update; it is a mix-up, and replacing on it would be how
            // one game quietly becomes another.
            if (!string.Equals(fresh.GameId, installed.GameId,
                               StringComparison.OrdinalIgnoreCase))
                return null;

            if (!Version.TryParse(fresh.Version, out var fresher)) return null;
            if (!Version.TryParse(installed.Version, out var current))
                current = new Version(0, 0, 0);
            if (fresher <= current) return null;

            return new Candidate(installed.GameId, installed.DisplayName,
                                 installed.Version, fresh.Version, path,
                                 game.PluginUrl);
        }
        catch (Exception) { return null; }
    }

    /// Whether London may put this one right without asking. Same answer as
    /// for an ordinary update: our own plugins repair themselves, somebody
    /// else's is offered and never taken.
    public static bool MayAutoApply(Candidate c)
        => (FirstParty.For(c.GameId).IsFirstPartyPlugin
            || PluginAutoUpdatePolicy.OursByAddress(c.SourceUrl))
        && PluginTrustStore.Get(c.GameId) != null;
}
