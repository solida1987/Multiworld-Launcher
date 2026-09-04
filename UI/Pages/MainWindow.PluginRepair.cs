using System;
using System.Linq;
using System.Threading.Tasks;
using LauncherV2.Core;
using LauncherV2.Core.Plugins;
using LauncherV2.UI.Controls;

namespace LauncherV2.UI.Pages;

/// Putting back the plugins that did not come back.
///
/// The installed-plugin registry is London's own list of what it put on this
/// machine. After the plugins have loaded, anything on that list that is not
/// registered is missing — not "not installed", missing — and the player is
/// told so and it is fetched again from the catalogue. Before this, the
/// sidebar said "Your library is empty" and the player re-added the plugin
/// by hand (Maegis, 5 September, after a launcher update; not the first time).
public partial class MainWindow
{
    /// How many registered plugins are missing right now — drawn into the
    /// empty-library message so an empty sidebar never reads as "nothing here".
    private int _missingPluginCount;

    private async Task RestoreMissingPluginsAsync()
    {
        try
        {
            var orphans = await Task.Run(() => OrphanedPluginRepair.Find());
            _missingPluginCount = orphans.Count;
            if (orphans.Count == 0) return;

            foreach (var o in orphans)
                AppendLog($"[Plugin] {o.DisplayName} is installed but did not load — {o.Reason}.");
            RebuildGameList();   // the empty message now says why

            StoreIndex? index;
            try { index = await StoreCatalog.FetchAsync(); }
            catch (Exception) { index = null; }
            if (index is not { Games.Length: > 0 })
            {
                AppendLog("[Plugin] The catalogue could not be reached, so nothing was restored. "
                        + "Add plugin puts a game back by hand.");
                ToastService.Show("Plugins missing",
                    $"{orphans.Count} of your games have no working plugin and the catalogue "
                  + "is unreachable. Use Add plugin, or restart when online.", ToastKind.Warning);
                return;
            }

            int restored = 0;
            foreach (var o in orphans)
            {
                AppendLog($"[Plugin] Restoring {o.DisplayName} from the catalogue…");
                var r = await OrphanedPluginRepair.RestoreAsync(o, index, this);
                AppendLog("[Plugin] " + r.Message.Replace(Environment.NewLine, " "));
                if (r.Restored)
                {
                    restored++;
                    if (r.Plugin != null) await SettleReplacedPluginAsync(r.Plugin);
                }
            }

            _missingPluginCount = orphans.Count - restored;
            RebuildGameList();
            if (restored > 0)
                ToastService.Show("Plugins restored",
                    restored == 1
                        ? $"{orphans[0].DisplayName} did not load after the update and has been put back."
                        : $"{restored} of your plugins did not load after the update and have been put back.",
                    ToastKind.Info);
            if (_missingPluginCount > 0)
                ToastService.Show("Plugin missing",
                    $"{_missingPluginCount} of your games could not be restored — see the log, "
                  + "or use Add plugin.", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            AppendLog("[Plugin] Restore check failed: " + ex.Message);
        }
    }
}
