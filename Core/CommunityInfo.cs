using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// CommunityInfo — the live numbers behind the server card in the right rail.
//
// Discord answers for a public invite without any token: name, icon and the
// approximate online/member counts. That is exactly what the card in Discord's
// own UI shows, so the launcher can draw the real thing instead of a screenshot
// that is wrong a week after it is taken.
//
// Everything is best-effort. Offline, the card keeps its written name and its
// button, and simply says nothing about how many people are there -- which is
// better than saying something untrue.
public static class CommunityInfo
{
    public const string InviteUrl = "https://discord.com/invite/hwCcPDv5E9";

    private const string ApiUrl =
        "https://discord.com/api/v10/invites/hwCcPDv5E9?with_counts=true";

    public sealed record Snapshot(string? Name, string? IconUrl, int Online, int Members);

    public static async Task<Snapshot?> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");

            string json = await http.GetStringAsync(ApiUrl, ct).ConfigureAwait(false);
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

            return new Snapshot(name, iconUrl, online, members);
        }
        catch
        {
            // No network, rate limited, invite revoked -- all the same to the
            // card: it shows what it was written with.
            return null;
        }
    }
}
