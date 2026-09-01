using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Plugins;

// Does an installed plugin have a newer build?
//
// The launcher has always answered that about ITSELF. This answers it about
// each plugin, from the same kind of feed (VersionFeed), with the same
// checksum rule -- one mechanism, two callers.
//
// ⛔ WHAT THIS DELIBERATELY DOES NOT DO: replace anything.
//
// A plugin's approval is bound to the HASH of what got installed, and the
// player was told in as many words that approving a name is not a blank cheque
// for whatever later arrives under it. Swapping the files behind their back
// would make that sentence a lie. So this class checks, downloads on request,
// and then hands the package to the ordinary "Add plugin" flow -- which asks
// again, showing the new hash and what the new version now declares it will do.
public static class PluginUpdater
{
    /// A newer build, found and not yet fetched.
    public sealed record Available(
        string  GameId,
        string  DisplayName,
        string  InstalledVersion,
        Version NewVersion,
        string  Sha256,
        PluginUpdateSource Source);

    /// Downloads report bytes so the dialog can show a real bar rather than a
    /// spinner that means "something is happening, possibly".
    public sealed record Progress(long BytesDone, long BytesTotal)
    {
        public double? Fraction => BytesTotal > 0
            ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : null;
    }

    private const string UserAgent = "ArchipelagoLauncher/3";

    /// Check every installed plugin that publishes a feed.
    ///
    /// Never throws and never blocks on one slow host holding up the rest --
    /// this runs on start-up, and an update check must not be something you
    /// wait for before you can play.
    public static async Task<IReadOnlyList<Available>> CheckAllAsync(
        IEnumerable<LoadedPlugin> installed, CancellationToken ct = default)
    {
        var checks = installed
            .Where(p => p.Manifest.Update != null)
            .Select(p => CheckOneAsync(p.Manifest, ct))
            .ToList();

        var results = await Task.WhenAll(checks).ConfigureAwait(false);
        return results.Where(r => r != null).Select(r => r!).ToList();
    }

    /// Check one. Null means "no newer build", "no feed", or "could not tell" --
    /// three different things that all mean the same to the player: nothing to
    /// do right now.
    public static async Task<Available?> CheckOneAsync(
        PluginManifest m, CancellationToken ct = default)
    {
        if (m.Update is not { } src) return null;

        var entry = await VersionFeed.ReadAsync(src.VersionUrl, ct).ConfigureAwait(false);
        if (entry == null) return null;

        // An unparseable installed version compares as 0.0.0, so a plugin with a
        // sloppy version string still gets offered the update rather than being
        // stuck forever.
        if (!Version.TryParse(m.Version, out var current)) current = new Version(0, 0, 0);
        if (entry.Version <= current) return null;

        return new Available(m.GameId, m.DisplayName, m.Version,
                             entry.Version, entry.Sha256, src);
    }

    /// Fetch the package to a temp file and verify it against the feed's
    /// checksum. Returns the path; the caller passes it to PluginInstallFlow,
    /// which asks the player before a single file is unpacked.
    ///
    /// Throws with a player-readable message on failure -- unlike the check,
    /// this one was asked for, so silence would be wrong.
    public static async Task<string> DownloadAsync(
        Available update, IProgress<Progress>? progress, CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "multiworld_plugin_update");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, update.GameId + PluginPackage.Extension);

        // The VERSIONED asset first, the bare name second.
        //
        // ⚠ The bare name is replaced in place on every release, and the feed
        // rides a CDN with its own cache. For about five minutes around a
        // release the two describe DIFFERENT versions, and a checksum check
        // done in that window correctly discards a perfectly good file —
        // measured live on StarCraft II 1.4.1→1.4.2. The versioned name is
        // immutable: whatever version the feed names, that asset can never
        // disagree with it. Releases carry both names for exactly this.
        var candidates = new List<string>();
        string bare = update.Source.PackageUrl;
        if (bare.EndsWith(PluginPackage.Extension, StringComparison.OrdinalIgnoreCase))
            candidates.Add(bare[..^PluginPackage.Extension.Length]
                         + "-" + update.NewVersion + PluginPackage.Extension);
        candidates.Add(bare);

        string? failure = null;
        foreach (string url in candidates)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                using var resp = await http.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    failure ??= $"The download returned {(int)resp.StatusCode}. The release "
                              + "page may have moved — try downloading the plugin by hand instead.";
                    continue;
                }

                long total = resp.Content.Headers.ContentLength ?? -1;
                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = File.Create(path))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                        done += read;
                        progress?.Report(new Progress(done, total));
                    }
                }

                // The feed said which bytes it meant. Anything else is not the
                // update that was described, whatever the reason.
                string actual = Sha256Of(path);
                if (actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    return path;

                try { File.Delete(path); } catch { }
                failure = "The downloaded file does not match the checksum its project "
                        + "published, so it was discarded. If the release is only minutes "
                        + "old this is its mirrors catching up — try again shortly. If it "
                        + "keeps happening, get the plugin from the project's own releases "
                        + "page instead.";
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException e)
            {
                failure ??= "The download failed: " + e.Message;
            }
        }

        throw new InvalidOperationException(failure ?? "The download failed.");
    }

    private static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
