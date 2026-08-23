using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
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

    // A cached image used to be permanent: FetchOneAsync skipped any path that
    // already existed, so a wrong address stayed wrong on the player's machine
    // forever -- correcting the catalogue changed nothing for anyone who had
    // already seen it. This record is what makes a correction travel: it
    // remembers WHICH address produced each file, so a changed address is a
    // reason to fetch again.
    public static string IconPath(string gameId) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", $"{gameId}.png");

    public static string BannerPath(string gameId) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Heroes", $"{gameId}_hero.png");

    // Steam can also swap the art behind an unchanged address -- that is how
    // Timespinner came to show its sequel's key art. Re-asking on every launch
    // would be hundreds of needless requests, so we re-ask at most this often,
    // and then only with If-Modified-Since, which costs a 304 when nothing
    // changed.
    private static readonly TimeSpan Recheck = TimeSpan.FromDays(30);

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

        var sources = ArtSourceLog.Load();
        bool got = false;
        foreach (var plugin in GameRegistry.All)
        {
            if (ct.IsCancellationRequested) break;
            if (!byId.TryGetValue(plugin.GameId, out var entry)) continue;

            got |= await FetchOneAsync(http, entry.Cover, IconPath(plugin.GameId),
                                       sources, ct).ConfigureAwait(false);
            got |= await FetchOneAsync(http, entry.Banner, BannerPath(plugin.GameId),
                                       sources, ct).ConfigureAwait(false);
        }
        ArtSourceLog.Save(sources);
        if (got) changed?.Invoke();
    }

    /// Fetch ONE game's art right now, regardless of the once-per-process
    /// prefetch. This is what makes a newly installed game get its cover in
    /// the same session -- the prefetch ran before the game existed, and
    /// "restart the launcher to see the picture" is not an answer.
    public static async Task FetchForGameAsync(string gameId,
                                               Action? changed = null,
                                               CancellationToken ct = default)
    {
        StoreIndex? index;
        try { index = await StoreCatalog.FetchAsync(ct).ConfigureAwait(false); }
        catch (Exception) { return; }
        var entry = index?.Games?.FirstOrDefault(g =>
            string.Equals(g.Id, gameId, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");

        var sources = ArtSourceLog.Load();
        bool got = false;
        got |= await FetchOneAsync(http, entry.Cover, IconPath(gameId), sources, ct)
                   .ConfigureAwait(false);
        got |= await FetchOneAsync(http, entry.Banner, BannerPath(gameId), sources, ct)
                   .ConfigureAwait(false);
        ArtSourceLog.Save(sources);
        if (got) changed?.Invoke();
    }

    private static async Task<bool> FetchOneAsync(
        HttpClient http, string? url, string dest,
        Dictionary<string, ArtSource> sources, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        string key = Path.GetFileName(dest);
        sources.TryGetValue(key, out var known);

        var reason = ArtCachePolicy.Decide(File.Exists(dest), url, known?.Url, known?.Fetched,
                                           DateTime.UtcNow, Recheck);
        if (reason == ArtFetchReason.UpToDate) return false;
        ArtCachePolicy.TryParseUtc(known?.Fetched, out DateTime stamp);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Only a time-based re-check may be answered with "unchanged". A
            // changed address must fetch, whatever the file's date says.
            if (ArtCachePolicy.MayUseIfModifiedSince(reason) && stamp > DateTime.MinValue)
                req.Headers.IfModifiedSince = stamp;

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                sources[key] = new ArtSource { Url = url, Fetched = DateTime.UtcNow.ToString("o") };
                return false;                       // same picture, new date
            }
            resp.EnsureSuccessStatusCode();
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            // An error page saved as a .png poisons the cache forever, because
            // File.Exists then says the art is there. Only real image payloads
            // may claim the path.
            if (!ArtSourceLog.LooksLikeImage(bytes)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // Write-then-move so a crash mid-download never leaves a half
            // file at the name every future check trusts.
            string tmp = dest + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, dest, overwrite: true);
            sources[key] = new ArtSource { Url = url, Fetched = DateTime.UtcNow.ToString("o") };
            return true;
        }
        catch (Exception)
        {
            return false;   // next launch tries again
        }
    }
}
