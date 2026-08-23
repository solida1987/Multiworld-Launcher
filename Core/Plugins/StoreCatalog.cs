using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Plugins;

/// One game in the shop window. Everything here is metadata and addresses —
/// pressing Install downloads the plugin file and hands it to the SAME consent
/// flow a manually picked file goes through. The store is a shop window, not a
/// side door.
public sealed record StoreGame(
    [property: JsonPropertyName("id")]             string Id,
    [property: JsonPropertyName("name")]           string Name,
    /// The name a seed's slot uses. "Diablo II: Lord of Destruction" plays a
    /// world called "Diablo II Archipelago", so the two cannot be assumed equal.
    [property: JsonPropertyName("ap_world_name")]  string ApWorldName,
    [property: JsonPropertyName("subtitle")]       string Subtitle,
    [property: JsonPropertyName("description")]    string Description,
    [property: JsonPropertyName("platform")]       string Platform,
    [property: JsonPropertyName("platform_label")] string PlatformLabel,
    [property: JsonPropertyName("family")]         string Family,
    [property: JsonPropertyName("genres")]         string[] Genres,
    [property: JsonPropertyName("version")]        string Version,
    [property: JsonPropertyName("world_by")]       string WorldBy,
    [property: JsonPropertyName("world_licence")]  string WorldLicence,
    [property: JsonPropertyName("plugin_by")]      string PluginBy,
    [property: JsonPropertyName("cover")]          string? Cover,
    [property: JsonPropertyName("plugin_url")]     string PluginUrl,
    [property: JsonPropertyName("page")]           string Page,
    /// How much of the install London can do: "rom", "apworld_only",
    /// "apworld_and_mod", "mod_package", "bundled", "manual".
    [property: JsonPropertyName("install_kind")]   string? InstallKind = null,
    /// Has a human actually played this through London? Built is not tested,
    /// and a card that hides the difference is a card that lies quietly.
    [property: JsonPropertyName("tested")]         bool Tested = false,
    /// Wide header art (Steam library_hero, a console title screen). An
    /// ADDRESS, like the cover -- the launcher fetches it to the player's
    /// machine; the catalogue never carries pixels.
    [property: JsonPropertyName("banner")]         string? Banner = null,
    /// Set when this entry is the same game as another entry -- a second
    /// integration, not a different game. Points at the primary entry's id.
    [property: JsonPropertyName("variant_of")]     string? VariantOf = null,
    /// Which of the store's three lists this game belongs to: "stable",
    /// "unstable" or "adult". Assigned by the catalogue build from the
    /// community games sheet and content_policy.json -- each decision there
    /// carries its own source -- never by the launcher guessing. "adult" is
    /// hidden until the player confirms they are 18 or older.
    [property: JsonPropertyName("section")]        string Section = "stable",
    /// The words behind the section: "Stable", "Unstable", "Broken on Main",
    /// "Proven in London" (our own live test register), or "Unknown".
    [property: JsonPropertyName("stability")]      string Stability = "Unknown",
    /// Whether this is the published game or someone's remake of it:
    /// "rom_hack", "fan_game", "fan_port", or null for a commercial release.
    ///
    /// It does NOT move the game off its console -- a Nintendo 64 ROM hack is
    /// still a Nintendo 64 game and belongs in that filter. It answers a
    /// different question: will the player recognise the box this came in?
    /// Someone looking for Super Mario 64 should not have to install it to
    /// find out it is a hack of it, and someone hunting for hacks should be
    /// able to ask for exactly those.
    [property: JsonPropertyName("mod_kind")]       string? ModKind = null);

public sealed record StoreIndex(
    [property: JsonPropertyName("count")]     int Count,
    [property: JsonPropertyName("platforms")] string[] Platforms,
    [property: JsonPropertyName("families")]  string[] Families,
    [property: JsonPropertyName("genres")]    string[] Genres,
    [property: JsonPropertyName("games")]     StoreGame[] Games);

// StoreCatalog — the catalogue's shop window, fetched and filtered.
//
// The filtering lives here rather than in the panel because filtering is the
// part with rules worth proving: a search that silently matches nothing, or a
// checkbox that excludes instead of includes, is the kind of bug a UI hides
// and a test catches in one line.
public static class StoreCatalog
{
    public static string IndexUrl { get; set; } =
        "https://raw.githubusercontent.com/solida1987/london-plugin-catalog/main/catalog/store.json";

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiworldLauncher", "store_cache.json");

    /// Fetches the index; falls back to the last good copy when offline. Null
    /// only when there has never been a good copy.
    public static async Task<StoreIndex?> FetchAsync(CancellationToken ct = default)
    {
        // A store.json placed beside the launcher wins over the published one.
        // This is how an UNPUBLISHED catalogue build gets looked at in the
        // test install before anyone decides to ship it: nothing goes to
        // GitHub, the file goes in Data\, and this install shows it. Deleting
        // the file puts the launcher back on the published truth.
        try
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                        "Data", "store_override.json");
            if (File.Exists(local))
            {
                var over = JsonSerializer.Deserialize<StoreIndex>(
                    File.ReadAllText(local));
                if (over is { Games.Length: > 0 })
                    return over;
            }
        }
        catch { /* an unreadable override must not take the store down */ }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
            string json = await http.GetStringAsync(IndexUrl, ct).ConfigureAwait(false);
            var index = JsonSerializer.Deserialize<StoreIndex>(json);
            if (index is { Games.Length: > 0 })
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                    File.WriteAllText(CachePath, json);
                }
                catch { /* a cache that fails to write is just no cache */ }
                return index;
            }
        }
        catch { /* fall through to the cache */ }

        try
        {
            if (File.Exists(CachePath))
                return JsonSerializer.Deserialize<StoreIndex>(File.ReadAllText(CachePath));
        }
        catch { }
        return null;
    }

    /// The store's one question: which games survive the current search box
    /// and checkbox state? Empty filter sets mean "everything" — a store where
    /// unticking every box shows nothing is a store that looks broken.
    public static IReadOnlyList<StoreGame> Filter(
        IEnumerable<StoreGame> games,
        string? query,
        IReadOnlyCollection<string>? platforms = null,
        IReadOnlyCollection<string>? genres = null,
        IReadOnlyCollection<string>? families = null,
        IReadOnlyCollection<string>? modKinds = null)
    {
        var q = (query ?? "").Trim();
        return games.Where(g =>
                (q.Length == 0
                 || g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                 || g.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase)
                 || g.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                 || g.Genres.Any(x => x.Contains(q, StringComparison.OrdinalIgnoreCase)))
                && (platforms is not { Count: > 0 }
                    || platforms.Contains(g.PlatformLabel))
                && (families is not { Count: > 0 }
                    || families.Contains(g.Family))
                && (genres is not { Count: > 0 }
                    || g.Genres.Any(genres.Contains))
                // "official" is a real choice, not the absence of one: a
                // player who ticks only it is asking to be shown commercial
                // releases, and null is what those carry.
                && (modKinds is not { Count: > 0 }
                    || modKinds.Contains(g.ModKind ?? "official")))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// Downloads a plugin file to a temp path, ready for the SAME AddFromFile
    /// consent flow a hand-picked file goes through. The download is the only
    /// thing the store does that the file dialog did not.
    public static async Task<(string? Path, string Message)> DownloadPluginAsync(
        StoreGame game, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
            byte[] data = await http.GetByteArrayAsync(game.PluginUrl, ct).ConfigureAwait(false);

            // A release asset that vanished serves an error page; a plugin
            // package is a zip. Refuse anything that is not one.
            if (data.Length < 4 || data[0] != 'P' || data[1] != 'K')
                return (null, $"The download for {game.Name} was not a plugin package. "
                            + "The release may have moved — try its page instead.");

            string dir = Path.Combine(Path.GetTempPath(), "london_store");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, game.Id + ".londonplugin");
            await File.WriteAllBytesAsync(path, data, ct).ConfigureAwait(false);
            return (path, "Downloaded.");
        }
        catch (Exception e)
        {
            return (null, $"{game.Name} could not be downloaded: {e.Message}");
        }
    }
}
