using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// GameArtFetcher — puts a cover on every game tile.
//
// WHY IT WORKS THE WAY IT DOES
// Box art is a copyrighted work and the logos on it are trademarks; being sold
// in every shop is what copyright protects, not an exception to it. So the
// launcher applies the same rule it already applies to emulators: we never
// bundle or redistribute the thing, we fetch it onto the player's own machine
// once, with their say-so.
//
// Our catalogue therefore stores ADDRESSES, never images -- the same line
// catalog/index.json already draws ("Addresses and metadata. Never anyone
// else's content."). The addresses are resolved once, by hand, before release:
// matching 54 marketing names against dump names is guesswork, and guesswork
// belongs where a human can check it, not in 54 repeats on every install.
//
// Everything here is best-effort. No cover is worth a failed startup, and a
// missing cover is only ever a blank tile.
public static class GameArtFetcher
{
    // The catalogue's cover address list. Same repo the plugin updater reads.
    public static string ArtIndexUrl { get; set; } =
        "https://raw.githubusercontent.com/solida1987/london-plugin-catalog/main/catalog/art.json";

    public sealed record Result(int Fetched, int Failed, int AlreadyHad);

    private sealed class ArtIndex
    {
        public Dictionary<string, string>? covers { get; set; }
    }

    /// <summary>Games whose cover file is not on disk yet.</summary>
    public static IReadOnlyList<string> MissingFor(IEnumerable<(string GameId, string IconPath)> games)
        => games.Where(g => !string.IsNullOrWhiteSpace(g.GameId)
                            && !string.IsNullOrWhiteSpace(g.IconPath)
                            && !File.Exists(g.IconPath))
                .Select(g => g.GameId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>
    /// Downloads the covers that are missing. Only ever writes files that are
    /// absent, so a player who replaced a cover by hand keeps their own.
    /// </summary>
    public static async Task<Result> FetchMissingAsync(
        IEnumerable<(string GameId, string IconPath)> games,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var wanted = games.Where(g => !string.IsNullOrWhiteSpace(g.GameId)
                                      && !string.IsNullOrWhiteSpace(g.IconPath))
                          .ToList();
        int had = wanted.Count(g => File.Exists(g.IconPath));
        var todo = wanted.Where(g => !File.Exists(g.IconPath)).ToList();
        if (todo.Count == 0) return new Result(0, 0, had);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");

        Dictionary<string, string> covers;
        try
        {
            string json = await http.GetStringAsync(ArtIndexUrl, ct).ConfigureAwait(false);
            covers = JsonSerializer.Deserialize<ArtIndex>(json)?.covers
                     ?? new Dictionary<string, string>();
        }
        catch (Exception e)
        {
            progress?.Report($"Could not read the cover list: {e.Message}");
            return new Result(0, todo.Count, had);
        }

        int ok = 0, failed = 0;
        foreach (var (gameId, iconPath) in todo)
        {
            ct.ThrowIfCancellationRequested();

            if (!covers.TryGetValue(gameId, out string? url) || string.IsNullOrWhiteSpace(url))
            {
                // Deliberate for combination hacks and anything else that never
                // had a retail box. Not a failure -- there is nothing to get.
                continue;
            }

            try
            {
                byte[] data = await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);

                // A redirect to an error page is bytes too. Only a real PNG.
                if (data.Length < 2000 || data[0] != 0x89 || data[1] != 0x50
                    || data[2] != 0x4E || data[3] != 0x47)
                {
                    progress?.Report($"{gameId}: the download was not an image");
                    failed++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
                // Write beside the target and move into place, so a cover is
                // never half-written if the run is interrupted.
                string tmp = iconPath + ".part";
                await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
                File.Move(tmp, iconPath, overwrite: true);
                ok++;
                progress?.Report($"{gameId}: cover downloaded");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                progress?.Report($"{gameId}: {e.Message}");
                failed++;
            }
        }

        return new Result(ok, failed, had);
    }
}
