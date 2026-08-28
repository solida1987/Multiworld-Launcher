using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Archipelago;

// ApEngineSource — where the Archipelago engine comes from, and how far London
// is willing to go to put it on the machine.
//
// THE CONSTRAINT THAT SHAPES THIS
// Archipelago publishes exactly one Windows artefact per release: an installer
// executable. There is no portable archive -- checked across six releases,
// 0.6.5 through 0.6.7. So the emulator model (download a zip, unpack it into a
// folder London owns) is simply unavailable here.
//
// WHAT LONDON DOES INSTEAD
// It fetches the installer, checks it is the file the release says it is, and
// then stops. The player runs it, sees Archipelago's own installer, and picks
// their own install location. London re-checks afterwards.
//
// That line is deliberate. Unpacking an emulator into our own folder changes
// nothing outside it; running somebody else's installer writes wherever it
// likes and registers itself with Windows. A launcher should not do that
// quietly on a player's behalf, however convenient it would be.
public static class ApEngineSource
{
    public const string Author      = "the Archipelago project and its contributors";
    public const string Licence     = "MIT";
    public const string LicenceUrl  = "https://github.com/ArchipelagoMW/Archipelago/blob/main/LICENSE";
    public const string ProjectPage = "https://github.com/ArchipelagoMW/Archipelago/releases/latest";

    private const string Owner = "ArchipelagoMW";
    private const string Repo  = "Archipelago";

    /// The Windows artefact is named "Setup.Archipelago.<version>.exe".
    private const string AssetPattern = "Setup.Archipelago";

    public sealed record Offer(string Version, string AssetName, string Url, long Size);
    public sealed record Progress(string Stage, int Percent);
    public sealed record Fetched(bool Ok, string Message, string? InstallerPath, string? Sha256);

    private static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
        return c;
    }

    /// What the project is currently offering, or null if it cannot be read.
    /// Never throws: no engine offer is a worse outcome than a crash only in
    /// the sense that the player has to install by hand, which always works.
    public static async Task<Offer?> LatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = NewClient();
            string json = await http.GetStringAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct)
                .ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";

            if (!root.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array) return null;

            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.IndexOf(AssetPattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                string url = a.TryGetProperty("browser_download_url", out var u)
                             ? u.GetString() ?? "" : "";
                long size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                if (url.Length > 0 && size > 0) return new Offer(tag, name, url, size);
            }
            return null;
        }
        catch { return null; }
    }

    /// Downloads the installer into <paramref name="targetDir"/> and hashes it.
    /// Does NOT run it -- see the note at the top of this file.
    public static async Task<Fetched> FetchInstallerAsync(
        Offer offer, string targetDir,
        IProgress<Progress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(targetDir);
            string dest = Path.Combine(targetDir, offer.AssetName);

            progress?.Report(new Progress($"Downloading {offer.AssetName}", 0));

            using (var http = NewClient())
            using (var resp = await http.GetAsync(offer.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? offer.Size;

                // Written beside the target and moved into place, so an
                // interrupted download never looks like a finished one.
                string part = dest + ".part";
                using (var src = await resp.Content.ReadAsStreamAsync(ct))
                using (var dst = File.Create(part))
                {
                    var buf = new byte[81920];
                    long got = 0;
                    int read;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                        got += read;
                        if (total > 0)
                            progress?.Report(new Progress(
                                $"Downloading {offer.AssetName}", (int)(got * 100 / total)));
                    }
                }
                File.Move(part, dest, overwrite: true);
            }

            long actual = new FileInfo(dest).Length;
            if (offer.Size > 0 && actual != offer.Size)
            {
                File.Delete(dest);
                return new Fetched(false,
                    $"The download is {actual:N0} bytes but the release says "
                  + $"{offer.Size:N0}. Nothing was kept — install by hand from "
                  + ProjectPage, null, null);
            }

            progress?.Report(new Progress("Checking the file", 100));
            string sha;
            using (var fs = File.OpenRead(dest))
                sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();

            return new Fetched(true,
                $"Archipelago {offer.Version} installer downloaded. Run it to install "
              + "the engine, then point London at it.", dest, sha);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return new Fetched(false,
                "The installer could not be downloaded: " + e.Message
              + ". Install by hand from " + ProjectPage, null, null);
        }
    }

    /// What the player is agreeing to, in the words they will read. Kept here
    /// rather than in the window so the declaration and the download cannot
    /// drift apart.
    public static IReadOnlyList<string> ConsentLines(Offer offer) => new[]
    {
        $"Archipelago {offer.Version} — {offer.AssetName} ({offer.Size / 1_000_000.0:F0} MB)",
        $"Written by {Author}, licensed {Licence}.",
        "London will download the installer and check it, but will not run it. "
      + "You run it yourself and choose where it goes.",
        "Downloaded from the project's own release page: " + ProjectPage,
    };
}
