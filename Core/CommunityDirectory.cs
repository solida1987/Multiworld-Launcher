using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// CommunityDirectory — the people and the places, kept honest.
//
// Two rules shaped this, and both come from the same problem: anything written
// about someone else goes out of date, and a launcher that ships a number in a
// compiled binary cannot correct it without a release.
//
//   * Discord servers are never described from memory. The list holds invite
//     CODES; the name, the icon and the member count are asked of Discord at
//     the moment the panel opens, exactly as the existing server card in the
//     right rail already does. That is also the "invite image" -- the real one,
//     not a screenshot that was true once.
//
//   * Streamers cannot be asked that way (Twitch and YouTube both want an API
//     key for it), so their figures carry the date they were checked and are
//     rendered with it. A number nobody has looked at since spring should LOOK
//     like a number nobody has looked at since spring.
//
// The whole list is overridable from the catalogue, so a channel that changes
// hands, a server whose invite is revoked, or a creator who asks to be removed
// is a file edit -- not a release. What ships in the binary is only the
// starting point, and every entry in it was verified against the live service
// on the date each one carries.
public static class CommunityDirectory
{
    /// Same repo as the plugin catalogue. A missing file is not an error --
    /// it just means the built-in list stands.
    public static string IndexUrl { get; set; } =
        "https://raw.githubusercontent.com/solida1987/london-plugin-catalog/main/catalog/community.json";

    // ---------------------------------------------------------------- shapes

    /// A Discord server. Everything the player sees except the blurb is asked
    /// of Discord live, so this carries almost nothing.
    public sealed record DiscordServer(
        [property: JsonPropertyName("invite")]   string InviteCode,
        [property: JsonPropertyName("blurb")]    string Blurb,
        [property: JsonPropertyName("language")] string? Language = null,
        /// A fact players need before they click, not a warning we invented --
        /// an age preference the server itself states, for instance.
        [property: JsonPropertyName("note")]     string? Note = null,
        [property: JsonPropertyName("official")] bool Official = false);

    /// A streamer or channel. Figures are optional; absent means the card says
    /// nothing about size rather than guessing at it.
    public sealed record Streamer(
        [property: JsonPropertyName("name")]      string Name,
        [property: JsonPropertyName("blurb")]     string Blurb,
        [property: JsonPropertyName("twitch")]    string? Twitch = null,
        [property: JsonPropertyName("youtube")]   string? YouTube = null,
        [property: JsonPropertyName("language")]  string? Language = null,
        [property: JsonPropertyName("image")]     string? ImageUrl = null,
        [property: JsonPropertyName("followers")] string? Followers = null,
        [property: JsonPropertyName("subs")]      string? Subscribers = null,
        /// When the figures above were last confirmed, e.g. "21 August 2026".
        [property: JsonPropertyName("checked")]   string? Checked = null);

    public sealed record Directory_(
        [property: JsonPropertyName("discord")]   List<DiscordServer>? Discord,
        [property: JsonPropertyName("streamers")] List<Streamer>? Streamers);

    /// Live facts about one server, straight from Discord.
    public sealed record DiscordLive(string? Name, string? IconUrl,
                                     int Members, int Online);

    // --------------------------------------------------------------- the list

    // Verified on 21 August 2026: every invite below was resolved against
    // Discord's own endpoint and answered with a live guild; every follower
    // figure was read from Twitch the same day; and every person listed was
    // tied to Archipelago through something concrete -- a session of theirs
    // that can be pointed at, not a reputation. Nothing is here that could
    // not be checked.
    private static readonly Directory_ BuiltIn = new(
        Discord: new()
        {
            new("8Z65BR2",
                "The project's own server, made by Archipelago's founder in 2020 and "
                + "by far the largest place the community gathers. Sub-forums per "
                + "game, help with setting a run up, and a few times a year \"The Big "
                + "Async\" — one multiworld with thousands of slots, played out over "
                + "weeks. The people who maintain the individual games are here too, "
                + "which makes it the place a bug report actually reaches someone.",
                Language: "English", Official: true),

            new("kgsvpf6Stb",
                "The community around MultiworldGG, a fork of Archipelago that "
                + "carries extra third-party worlds which cannot be part of the "
                + "original project, plus worlds still early in development. Their "
                + "site hosts open lobbies around the clock, and bug reports for "
                + "the fork's worlds go through this server.",
                Language: "English"),

            new("360chrism",
                "The community server of 360Chrism, who has run some of the largest "
                + "Archipelago multiworlds anywhere — hang out, share your interests, "
                + "and catch the next big community randomizer.",
                Language: "English"),

            new("qdvcPUe",
                "The community server of cjya, the speedrunner who hosted an "
                + "1100-player cross-game Archipelago randomizer.",
                Language: "English"),

            new("wolfpack",
                "LobosJr's community server — RPGs, roguelikes and Souls challenge "
                + "runs, with Archipelago team races among them.",
                Language: "English"),

            new("0gOY9qyDVUxk59ar",
                "GrandPooBear's community server — speedrunning, Mario, and the home "
                + "crowd for his randomizer and Archipelago runs.",
                Language: "English"),

            new("FD2pPnqneH",
                "The French-speaking Archipelago community. Runs are organised here, "
                + "and there are people who will help you get your configuration "
                + "working if it fights you.",
                Language: "Français"),

            new("PPEfMDedP6",
                "The Spanish-speaking Archipelago community — they create and host "
                + "randomiser sessions and will show you how the software works.",
                Language: "Español"),

            new("c4yjE78tfD",
                "A small, relaxed gaming server that runs Archipelago syncs and "
                + "asyncs regularly, either to join or to host yourself.",
                Language: "English",
                Note: "The server states it is mainly for adults (18+)."),

            new("xeRgQb36Dt",
                "A community event server for retro gaming, run by Sprasshu and "
                + "PolterGhost, with racing series streamed live. They host community "
                + "Archipelago syncs regularly, and re-stream them fortnightly as a "
                + "series of their own.",
                Language: "English"),

            new("6nyDzj62SJ",
                "The Turkish Archipelago community — a small server building a place "
                + "for Turkish speakers to play together, with help on installation "
                + "and YAML configuration for anyone new to it.",
                Language: "Türkçe"),
        },
        Streamers: new()
        {
            new("360Chrism",
                "Randomisers and speedruns — Super Mario Odyssey, Paper Mario: The "
                + "Thousand-Year Door, Pokémon Snap, Luigi's Mansion. Has run some of "
                + "the largest Archipelago multiworlds anywhere, including a "
                + "770-player community session and a twelve-game cross-game run "
                + "together with PangaTAS.",
                Twitch: "https://www.twitch.tv/360chrism",
                YouTube: "https://www.youtube.com/@360Chrism",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "5cab2af5-26d0-4156-8692-b39ae5079652-profile_image-300x300.png",
                Followers: "138,775", Subscribers: "97,300",
                Checked: "21 August 2026"),

            new("cjya",
                "Speedrunner — Super Mario 64, Hollow Knight, Celeste — and the host "
                + "of an 1100-player cross-game Archipelago randomizer, one of the "
                + "biggest ever run. Also part of a metroidvania Archipelago "
                + "multiworld with GrandPooBear, SmallAnt, Samura1man and CraftyBoss.",
                Twitch: "https://www.twitch.tv/cjya",
                YouTube: "https://www.youtube.com/@cjya",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "0c51cc21-f3b5-47f4-a92d-77234fb5e4c8-profile_image-300x300.png",
                Followers: "148,357", Subscribers: "540,000",
                Checked: "21 August 2026"),

            new("LobosJr",
                "RPGs, roguelikes and the Dark Souls games, played every way they "
                + "were never meant to be played. Runs Archipelago too — including a "
                + "Dark Souls trilogy team Archipelago race with an enemy randomizer "
                + "on top.",
                Twitch: "https://www.twitch.tv/lobosjr",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "lobosjr-profile_image-b5e3a6c3556aed54-300x300.png",
                Followers: "433,758",
                Checked: "21 August 2026"),

            new("GrandPooBear",
                "Speedrunner and variety gamer, best known for Mario. Played a "
                + "metroidvania Archipelago multiworld together with cjya, SmallAnt, "
                + "Samura1man and CraftyBoss.",
                Twitch: "https://www.twitch.tv/grandpoobear",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "860047bb-4417-44cb-b059-348d5275e60c-profile_image-300x300.png",
                Followers: "332,893",
                Checked: "21 August 2026"),

            new("SmallAnt",
                "\"I play games wrong most of the time.\" Challenge runs and "
                + "speedruns — Pokémon, Super Mario Odyssey — for one of the largest "
                + "audiences on Twitch. Part of the metroidvania Archipelago "
                + "multiworld with GrandPooBear and cjya.",
                Twitch: "https://www.twitch.tv/smallant",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "90591f72-6bf7-48c9-8dae-04ba6aeb906a-profile_image-300x300.png",
                Followers: "1,479,225",
                Checked: "21 August 2026"),

            new("PangaTAS",
                "Creator of Super Dram World and the Item Abuse series — one of the "
                + "best-known names in Mario challenge content. Ran a twelve-game "
                + "cross-game Archipelago randomizer together with 360Chrism.",
                Twitch: "https://www.twitch.tv/pangaeapanga",
                YouTube: "https://www.youtube.com/@PangaTAS",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "pangaeapanga-profile_image-dfae8789666c2bc2-300x300.png",
                Followers: "197,719",
                Checked: "21 August 2026"),

            new("TiTavion",
                "Streams in French — recently Onimusha 3, Mortal Shell II and Diablo "
                + "II. Plays Archipelago as part of a French multiworld group "
                + "alongside M_Bubbles, Nalahri, Pinde, Redrosetv and Skaradams.",
                Twitch: "https://www.twitch.tv/titavion",
                Language: "Français",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "e990b10a-2c51-4335-ae90-4bd1708a30f8-profile_image-300x300.png",
                Followers: "45,208",
                Checked: "21 August 2026"),

            new("Pinde",
                "FromSoftware \"specialist\", factory-game addict and self-declared "
                + "hardcore randomizer fan — in his own words. Part of the French "
                + "Archipelago multiworld group with TiTavion.",
                Twitch: "https://www.twitch.tv/pinde",
                Language: "Français",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "c870e4bd-27f4-44fc-9880-d330d6fafea6-profile_image-300x300.png",
                Followers: "19,072",
                Checked: "21 August 2026"),

            new("ZeroLenny",
                "Souls games and comedy in equal measure. Ran an Archipelago "
                + "randomizer together with LobosJr, Parky and star0chris.",
                Twitch: "https://www.twitch.tv/zerolenny",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "7f83d747-1ee1-4598-bfd7-db54e4cca99c-profile_image-300x300.png",
                Followers: "49,549",
                Checked: "21 August 2026"),

            new("Samura1man",
                "Finnish speedrunner with over fifteen years of runs behind him. "
                + "Part of the metroidvania Archipelago multiworld with GrandPooBear "
                + "and cjya.",
                Twitch: "https://www.twitch.tv/samura1man",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "7421feb2be049dbc-profile_image-300x300.png",
                Followers: "53,059",
                Checked: "21 August 2026"),

            new("CraftyBoss",
                "Speedrunner and modder. Part of the metroidvania Archipelago "
                + "multiworld with GrandPooBear, cjya and SmallAnt.",
                Twitch: "https://www.twitch.tv/craftyboss",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "a2b42a06-7812-4cd1-bca8-1400cfe9ac51-profile_image-300x300.png",
                Followers: "33,336",
                Checked: "21 August 2026"),

            new("star0chris",
                "Regular in the 360Chrism circle's Archipelago sessions — appears in "
                + "their multiworld runs alongside LobosJr, ZeroLenny, YoJosherino "
                + "and Mitchriz.",
                Twitch: "https://www.twitch.tv/star0chris",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "ac686200-e53b-4f2d-a645-863054ac9d57-profile_image-300x300.png",
                Followers: "54,229",
                Checked: "21 August 2026"),

            new("Mitchriz",
                "Sekiro and Elden Ring speedrunner with world records to his name. "
                + "Part of the Archipelago randomizer sessions in the 360Chrism "
                + "circle.",
                Twitch: "https://www.twitch.tv/mitchriz",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "6b2c8d23-e4fd-493c-b21c-b675b0523a2b-profile_image-300x300.png",
                Followers: "44,385",
                Checked: "21 August 2026"),

            new("YoJosherino",
                "Speedrunner and challenge runner. Part of the Archipelago "
                + "randomizer sessions in the 360Chrism circle.",
                Twitch: "https://www.twitch.tv/yojosherino",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "7c69a929-57c8-4663-8b31-d8eb33b818f7-profile_image-300x300.png",
                Followers: "22,167",
                Checked: "21 August 2026"),

            new("Parky",
                "A smaller channel from the same circle — ran Archipelago "
                + "randomizers together with LobosJr, ZeroLenny, star0chris and "
                + "360Chrism.",
                Twitch: "https://www.twitch.tv/parky",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "parky-profile_image-006820dead930db3-300x300.png",
                Followers: "1,611",
                Checked: "21 August 2026"),

            new("MaegisVonTempest",
                "\"The regular Canadian guy who enjoys a good randomizer\" — in his "
                + "own words. RPGs, action and adventure, randomizers and "
                + "multiworlds, and one of the first to play a full run of the "
                + "Diablo II Archipelago world. A small channel at the very start "
                + "of its road.",
                Twitch: "https://www.twitch.tv/maegisvontempest",
                Language: "English",
                ImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/"
                        + "7b78450b-bb71-4688-a954-ddf4f687877f-profile_image-300x300.jpeg",
                Followers: "16",
                Checked: "21 August 2026"),
        });

    private static Directory_? _cache;

    /// The catalogue's list when it can be reached, the built-in one otherwise.
    public static async Task<Directory_> LoadAsync(CancellationToken ct = default)
    {
        if (_cache != null) return _cache;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
            string json = await http.GetStringAsync(IndexUrl, ct).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<Directory_>(json);

            // A catalogue that answers with nothing is not an instruction to
            // show nothing -- it is a file that has not been written yet.
            if (loaded is { } d && (d.Discord?.Count > 0 || d.Streamers?.Count > 0))
                return _cache = d;
        }
        catch (Exception)
        {
            // Offline, rate limited, file not published yet -- all the same.
        }
        return _cache = BuiltIn;
    }

    /// Ask Discord about one invite. No token needed for a public one.
    public static async Task<DiscordLive?> FetchLiveAsync(
        string inviteCode, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");

            string json = await http.GetStringAsync(
                $"https://discord.com/api/v10/invites/{inviteCode}?with_counts=true",
                ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("guild", out var guild)) return null;

            string? name = guild.TryGetProperty("name", out var n) ? n.GetString() : null;

            string? iconUrl = null;
            if (guild.TryGetProperty("id", out var id)
                && guild.TryGetProperty("icon", out var icon)
                && icon.ValueKind == JsonValueKind.String)
            {
                iconUrl = $"https://cdn.discordapp.com/icons/{id.GetString()}"
                        + $"/{icon.GetString()}.png?size=128";
            }

            int online = root.TryGetProperty("approximate_presence_count", out var p)
                         && p.TryGetInt32(out int po) ? po : 0;
            int members = root.TryGetProperty("approximate_member_count", out var m)
                          && m.TryGetInt32(out int mo) ? mo : 0;

            return new DiscordLive(name, iconUrl, members, online);
        }
        catch (Exception)
        {
            // An invite that was revoked since the list was written answers
            // with a 404 here. The card then shows only what we wrote, and no
            // number at all -- which is the truthful outcome.
            return null;
        }
    }

    public static string InviteUrl(string code) => $"https://discord.com/invite/{code}";

    /// One listed streamer who is on air right now, with the live preview
    /// frame Twitch serves for their stream.
    public sealed record LiveStreamer(Streamer Who, string ThumbnailUrl);

    /// Which of the listed streamers are live on Twitch at this moment.
    ///
    /// No API key: Twitch's public preview CDN answers for every channel.
    /// A LIVE channel serves a real thumbnail at its preview address; an
    /// offline one redirects to a shared "404_preview" placeholder. The final
    /// URL after redirects is therefore the answer -- and for a live channel
    /// the bytes behind it are a current frame of the stream, which is
    /// exactly what a "live now" card wants to show.
    ///
    /// Best effort throughout: a channel that cannot be checked is reported
    /// as not live, because "live" is a claim and silence is not.
    public static async Task<IReadOnlyList<LiveStreamer>> WhoIsLiveAsync(
        CancellationToken ct = default)
    {
        var dir = await LoadAsync(ct).ConfigureAwait(false);
        var candidates = new List<(Streamer S, string Login)>();
        foreach (var s in dir.Streamers ?? new List<Streamer>())
        {
            // The login is the channel URL's last segment; anything without a
            // Twitch link simply cannot be live here.
            if (s.Twitch is not { Length: > 0 } url) continue;
            string login = url.TrimEnd('/')[(url.TrimEnd('/').LastIndexOf('/') + 1)..];
            if (login.Length > 0) candidates.Add((s, login.ToLowerInvariant()));
        }
        if (candidates.Count == 0) return Array.Empty<LiveStreamer>();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");

        var checks = candidates.Select(async c =>
        {
            try
            {
                string probe = "https://static-cdn.jtvnw.net/previews-ttv/"
                             + $"live_user_{c.Login}-440x248.jpg";
                using var resp = await http.GetAsync(probe,
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                string final = resp.RequestMessage?.RequestUri?.ToString() ?? "";
                bool live = resp.IsSuccessStatusCode
                            && !final.Contains("404_preview", StringComparison.OrdinalIgnoreCase);
                return live ? new LiveStreamer(c.S, probe) : null;
            }
            catch (Exception) { return null; }
        });

        var results = await Task.WhenAll(checks).ConfigureAwait(false);
        var live = new List<LiveStreamer>();
        foreach (var r in results) if (r != null) live.Add(r);
        return live;
    }
}
