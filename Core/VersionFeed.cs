using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// The two-line version file that says "here is the newest build, and here is
// its checksum".
//
//   3.3.0
//   0f206f52e5fcbd8fc...
//
// The launcher has answered that question about itself since 2.x. Plugins now
// answer it about themselves, from their own project's repository. Same file
// shape, same parser, same failure behaviour -- because two update mechanisms
// that drift apart are two sets of bugs, and the second one is always the one
// nobody tested.
//
// Deliberately not JSON. Two lines can be written by hand during a release
// without a tool, and a release step that needs a tool is a release step that
// gets skipped.
public static class VersionFeed
{
    /// What a feed said. Null Sha means the file was there but malformed --
    /// treated the same as unreachable, because an update we cannot verify is
    /// an update we will not apply.
    public sealed record Entry(Version Version, string Sha256);

    /// Short: this runs on start-up, and a slow host must not hold the window.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// Read one feed. Returns null on ANY failure -- no network, 404, garbage.
    /// Never throws: a missing update check is a quiet non-event, not an error
    /// the player has to dismiss before they can play offline.
    public static async Task<Entry?> ReadAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ArchipelagoLauncher/3");
            return Parse(await http.GetStringAsync(url, ct).ConfigureAwait(false));
        }
        catch { return null; }
    }

    /// Split out from ReadAsync so the format can be tested without a network.
    public static Entry? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Split on '\n' and trim: the file is edited on Windows as often as not,
        // and a stray '\r' turning "3.3.0" into an unparseable string would
        // silently disable updates for everyone.
        var lines = text.Trim().Split('\n');
        if (lines.Length < 2) return null;

        if (!Version.TryParse(lines[0].Trim(), out var v)) return null;

        string sha = lines[1].Trim();
        // A checksum that is not a checksum is worse than none: it would fail
        // verification after a full download, which reads as "the download is
        // broken" rather than "the feed is wrong".
        if (sha.Length != 64 || !IsHex(sha)) return null;

        return new Entry(v, sha);
    }

    private static bool IsHex(string s)
    {
        foreach (char c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }
}
