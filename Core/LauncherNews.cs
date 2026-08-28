using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// LauncherNews — what the landing page's NEWS section says.
//
// Same pattern as CommunityDirectory, for the same reason: news compiled into
// a binary stops being news the day after the release. The catalogue file is
// the live source and can say something new any day; the built-in list only
// covers the gap before that file exists, so it speaks strictly about what
// shipped in this very build — the one set of facts a binary can vouch for.
public static class LauncherNews
{
    public static string IndexUrl { get; set; } =
        "https://raw.githubusercontent.com/solida1987/london-plugin-catalog/main/catalog/news.json";

    public sealed record NewsItem(
        [property: JsonPropertyName("date")]  string Date,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("text")]  string Text);

    private sealed record Feed(
        [property: JsonPropertyName("news")] List<NewsItem>? News);

    private static readonly List<NewsItem> BuiltIn = new()
    {
        new("August 2026", "Join a server hosted elsewhere",
            "The Join tab can now sign in to a multiworld somebody else is "
          + "running: type the address and your slot name, and London asks the "
          + "server which game the slot plays and sets it up — including asking "
          + "for the seed's patch file when one is needed."),

        new("August 2026", "The community, in the launcher",
            "Two new pages in the top bar: Streamers, with channels that run "
          + "Archipelago sessions in several languages, and Discord, with "
          + "community servers whose member counts are read live when the page "
          + "opens."),

        new("August 2026", "How a multiworld works",
            "The Multiworld tab grew a guide that explains the whole idea from "
          + "zero: what a seed is, how to build and host one, and the four "
          + "things you can do with it — including randomising several of your "
          + "own games into one run."),
    };

    private static IReadOnlyList<NewsItem>? _cache;

    /// The catalogue's news when it can be reached, the built-in notes
    /// otherwise. Never empty unless the catalogue explicitly publishes an
    /// empty list — which is a statement, and respected.
    public static async Task<IReadOnlyList<NewsItem>> LoadAsync(CancellationToken ct = default)
    {
        if (_cache != null) return _cache;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
            string json = await http.GetStringAsync(IndexUrl, ct).ConfigureAwait(false);
            if (JsonSerializer.Deserialize<Feed>(json) is { News: { } items })
                return _cache = items;
        }
        catch (Exception)
        {
            // Offline or not published yet -- the built-in notes stand.
        }
        return _cache = BuiltIn;
    }
}
