using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Plugins;

// GameArtCache — the art for installed games, fetched to the player's machine.
//
// The catalogue carries addresses, never pixels; that rule is settled. What
// was missing is the other half: the shipped plugins answer IconPath with
// Assets/<GameId>.png, and NOTHING ever wrote that file -- which is why every
// catalogue game sat in the sidebar with a blank square. This closes the loop
// without touching a single published plugin: the launcher downloads the
// cover to exactly the path the plugin already looks at.
//
// Banners land in Assets/Heroes/<GameId>_hero.png for the same reason -- the
// home hero and the overview brush already resolve that path, so a downloaded
// banner lights them up with no further wiring.
public static class GameArtCache
{
    private static int _ran;

    public static string IconPath(string gameId) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", $"{gameId}.png");

    public static string BannerPath(string gameId) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Heroes", $"{gameId}_hero.png");

    /// Fetch missing icons and banners for every installed game the store
    /// knows. Once per process, in the background, best effort file by file:
    /// offline simply means the fallbacks stand for one more session.
    /// Calls `changed` once at the end if anything new landed.
    public static async Task PrefetchAsync(Action? changed = null,
                                           CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _ran, 1) == 1) return;

        StoreIndex? index;
        try { index = await StoreCatalog.FetchAsync(ct).ConfigureAwait(false); }
        catch (Exception) { return; }
        if (index?.Games is not { } storeGames) return;

        var byId = storeGames
            .Where(g => g.Id is { Length: > 0 })
            .ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");

        bool got = false;
        foreach (var plugin in GameRegistry.All)
        {
            if (ct.IsCancellationRequested) break;
            if (!byId.TryGetValue(plugin.GameId, out var entry)) continue;

            got |= await FetchOneAsync(http, entry.Cover, IconPath(plugin.GameId), ct)
                       .ConfigureAwait(false);
            got |= await FetchOneAsync(http, entry.Banner, BannerPath(plugin.GameId), ct)
                       .ConfigureAwait(false);
        }
        if (got) changed?.Invoke();
    }

    private static async Task<bool> FetchOneAsync(
        HttpClient http, string? url, string dest, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || File.Exists(dest)) return false;
        try
        {
            byte[] bytes = await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            // An error page saved as a .png poisons the cache forever, because
            // File.Exists then says the art is there. Only plausible image
            // payloads may claim the path.
            if (bytes.Length < 128) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // Write-then-move so a crash mid-download never leaves a half
            // file at the name every future check trusts.
            string tmp = dest + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, dest, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;   // next launch tries again
        }
    }
}
