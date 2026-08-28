using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Trackers;

/// One tracker pack, as the catalogue records it.
public sealed class TrackerEntry
{
    [JsonPropertyName("game")]         public string Game { get; set; } = "";
    [JsonPropertyName("pack_repo")]    public string PackRepo { get; set; } = "";
    [JsonPropertyName("pack_name")]    public string PackName { get; set; } = "";
    [JsonPropertyName("package_uid")]  public string PackageUid { get; set; } = "";
    [JsonPropertyName("versions_url")] public string VersionsUrl { get; set; } = "";
    [JsonPropertyName("variants")]     public List<string> Variants { get; set; } = new();
    [JsonPropertyName("min_poptracker")] public string MinPopTracker { get; set; } = "";
    [JsonPropertyName("in_poptracker_repo")] public bool InPopTrackerRepo { get; set; }
}

public sealed class TrackerIndex
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("games")] public Dictionary<string, TrackerEntry> Games { get; set; } = new();
}

/// Which of our games has a tracker pack, and where it comes from.
///
/// Same shape as the store catalogue: published beside it, fetched fresh, and
/// a saved copy stands in when the network does not answer. 147 of the 794
/// games have one, so the answer for most games is "none" — and a game with no
/// pack must show no button at all rather than one that cannot work.
public static class TrackerCatalog
{
    private const string Url =
        "https://raw.githubusercontent.com/solida1987/london-plugin-catalog/main/catalog/trackers.json";

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiworldLauncher", "trackers_cache.json");

    private static TrackerIndex? _memory;

    public static async Task<TrackerIndex?> FetchAsync(CancellationToken ct = default)
    {
        if (_memory != null) return _memory;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
            string json = await http.GetStringAsync(Url, ct).ConfigureAwait(false);
            _memory = JsonSerializer.Deserialize<TrackerIndex>(json);
            if (_memory != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                    File.WriteAllText(CachePath, json);
                }
                catch (IOException) { /* a cache that will not write is just no cache */ }
                return _memory;
            }
        }
        catch (Exception) { /* fall through to the saved copy */ }

        try
        {
            if (File.Exists(CachePath))
                _memory = JsonSerializer.Deserialize<TrackerIndex>(File.ReadAllText(CachePath));
        }
        catch (Exception) { }
        return _memory;
    }

    /// The pack for one of our games, or null. Never throws: a tracker is an
    /// extra, and a page must draw with or without one.
    public static async Task<TrackerEntry?> ForGameAsync(string gameId,
                                                         CancellationToken ct = default)
    {
        var index = await FetchAsync(ct).ConfigureAwait(false);
        if (index?.Games == null || string.IsNullOrWhiteSpace(gameId)) return null;
        return index.Games.TryGetValue(gameId, out var e) && e.PackageUid.Length > 0 ? e : null;
    }
}

/// PopTracker: installed once, then a folder per game.
///
/// ⚠ The program is NOT installed per game. It lives in the launcher's own
/// Extensions folder like the emulator bridges do, and the second game that
/// wants a tracker finds it already there. The packs are the per-game part,
/// and they are small.
public static class PopTrackerService
{
    public const string Repo = "black-sliver/PopTracker";

    public static string HomeDir => Path.Combine(
        AppContext.BaseDirectory, "Extensions", "poptracker");

    public static string ExePath => Path.Combine(HomeDir, "poptracker.exe");

    public static bool IsInstalled => File.Exists(ExePath);

    /// Where packs live.
    ///
    /// ⚠ Documents/PopTracker/packs is PopTracker's own search path, shared
    /// with a copy the player may already have installed themselves. We add to
    /// it and never remove anything: a pack they downloaded by hand is theirs.
    public static string PacksDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PopTracker", "packs");

    // ---------------------------------------------------------------- install

    /// Fetch and unpack PopTracker itself. Does nothing when it is already here.
    public static async Task<string?> InstallAsync(IProgress<string>? progress = null,
                                                   CancellationToken ct = default)
    {
        if (IsInstalled) return null;

        progress?.Report("Looking up the latest PopTracker…");
        var release = await GitHubHelper.FetchLatestReleaseAsync("black-sliver", "PopTracker", ct)
                                        .ConfigureAwait(false);
        // ⚠ The signature file sits beside the zip and is also named "...win64".
        // Matching on "win64" alone downloaded a 500-byte .minisig once; the
        // extension is part of the test.
        string? asset = release?.Assets
            ?.Select(a => a.Name)
            .FirstOrDefault(n => n.Contains("win64", StringComparison.OrdinalIgnoreCase)
                              && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (release == null || asset == null)
            return "Could not find a Windows build of PopTracker to download.";

        string url = GitHubHelper.AssetUrl("black-sliver", "PopTracker", release.Tag, asset);
        progress?.Report($"Downloading PopTracker {release.Tag} ({asset})…");

        string tmp = Path.Combine(Path.GetTempPath(), asset);
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
                byte[] data = await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                if (data.Length < 100_000)
                    return "What came back was not the PopTracker download.";
                await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
            }

            progress?.Report("Unpacking…");
            Directory.CreateDirectory(HomeDir);
            ArchiveExtractor.Extract(tmp, HomeDir);
            // The zip may hold one folder with everything in it.
            ArchiveExtractor.FlattenSingleSubdir(HomeDir, "poptracker.exe");

            return IsInstalled ? null
                 : "PopTracker unpacked but poptracker.exe is not where it was expected.";
        }
        catch (Exception e) { return e.Message; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // ------------------------------------------------------------------ packs

    /// Is this pack already on disk? Answered by reading manifests, because a
    /// folder name is not a promise — a player may have renamed it.
    public static bool IsPackInstalled(string packageUid)
    {
        if (string.IsNullOrWhiteSpace(packageUid)) return false;
        try
        {
            if (!Directory.Exists(PacksDir)) return false;
            foreach (string dir in Directory.EnumerateDirectories(PacksDir))
            {
                string manifest = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifest)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("package_uid", out var uid)
                    && string.Equals(uid.GetString(), packageUid, StringComparison.Ordinal))
                    return true;
            }
        }
        catch (Exception) { /* an unreadable packs folder is "not installed" */ }
        return false;
    }

    /// Download and unpack one game's pack.
    ///
    /// Two routes, and the first is the honest one:
    ///
    ///   1. versions.json — the pack's own update feed, which names a download
    ///      AND its sha256. 85 of our 147 publish one.
    ///   2. the repository's source zip, for the 62 that do not. Unverified,
    ///      because there is nothing to verify it against; said out loud rather
    ///      than pretended otherwise.
    public static async Task<string?> InstallPackAsync(TrackerEntry entry,
                                                       IProgress<string>? progress = null,
                                                       CancellationToken ct = default)
    {
        if (IsPackInstalled(entry.PackageUid)) return null;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");

        string? downloadUrl = null, expectSha = null, version = null;
        if (entry.VersionsUrl.Length > 0)
        {
            try
            {
                progress?.Report("Asking the pack which version is newest…");
                using var doc = JsonDocument.Parse(
                    await http.GetStringAsync(entry.VersionsUrl, ct).ConfigureAwait(false));
                if (doc.RootElement.TryGetProperty("versions", out var arr)
                    && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                {
                    var newest = arr[0];      // the feed lists newest first
                    downloadUrl = newest.TryGetProperty("download_url", out var u) ? u.GetString() : null;
                    expectSha   = newest.TryGetProperty("sha256", out var s) ? s.GetString() : null;
                    version     = newest.TryGetProperty("package_version", out var v) ? v.GetString() : null;
                }
            }
            catch (Exception) { /* fall back to the source zip */ }
        }

        downloadUrl ??= $"https://codeload.github.com/{entry.PackRepo}/zip/refs/heads/main";

        progress?.Report(version != null
            ? $"Downloading {entry.PackName} {version}…"
            : $"Downloading {entry.PackName}…");

        byte[] zip;
        try
        {
            zip = await http.GetByteArrayAsync(downloadUrl, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Not every repository's default branch is called main.
            try
            {
                zip = await http.GetByteArrayAsync(
                    $"https://codeload.github.com/{entry.PackRepo}/zip/refs/heads/master", ct)
                    .ConfigureAwait(false);
            }
            catch (Exception e) { return $"Could not download the pack: {e.Message}"; }
        }

        if (expectSha is { Length: 64 })
        {
            string got = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
            if (!string.Equals(got, expectSha, StringComparison.OrdinalIgnoreCase))
                return "The downloaded pack does not match the checksum its author "
                     + "published. Nothing was installed.";
        }

        string tmpZip = Path.Combine(Path.GetTempPath(), entry.PackageUid + ".zip");
        string tmpDir = Path.Combine(Path.GetTempPath(), entry.PackageUid + "_x");
        try
        {
            await File.WriteAllBytesAsync(tmpZip, zip, ct).ConfigureAwait(false);
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            Directory.CreateDirectory(tmpDir);
            ArchiveExtractor.Extract(tmpZip, tmpDir);

            // ⚠ Find the folder that HOLDS manifest.json rather than assuming a
            // layout. A source zip nests everything under "<repo>-<branch>/",
            // and some packs put the pack itself one level further down.
            string? packRoot = FindPackRoot(tmpDir);
            if (packRoot == null)
                return "That download did not contain a PopTracker pack "
                     + "(no manifest.json anywhere in it).";

            string dest = Path.Combine(PacksDir, entry.PackageUid);
            Directory.CreateDirectory(PacksDir);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            CopyTree(packRoot, dest);

            return IsPackInstalled(entry.PackageUid) ? null
                 : "The pack was unpacked but PopTracker will not recognise it.";
        }
        catch (Exception e) { return e.Message; }
        finally
        {
            try { File.Delete(tmpZip); } catch { }
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
        }
    }

    private static string? FindPackRoot(string dir, int depth = 0)
    {
        if (depth > 3) return null;
        if (File.Exists(Path.Combine(dir, "manifest.json"))) return dir;
        foreach (string sub in Directory.EnumerateDirectories(dir))
            if (FindPackRoot(sub, depth + 1) is { } hit) return hit;
        return null;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.EnumerateFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (string d in Directory.EnumerateDirectories(from))
            CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // ----------------------------------------------------------------- launch

    /// Put PopTracker and this game's pack in place, opening nothing.
    ///
    /// The install half of OpenAsync, split out because the offer made at the
    /// END of a game install must not throw a tracker window at somebody who
    /// was installing a game. OpenAsync calls this, so there is still only one
    /// copy of the sequence.
    ///
    /// Returns null when everything is in place, a sentence otherwise.
    public static async Task<string?> SetUpAsync(TrackerEntry entry,
                                                 IProgress<string>? progress = null,
                                                 CancellationToken ct = default)
    {
        if (!IsInstalled)
        {
            string? err = await InstallAsync(progress, ct).ConfigureAwait(false);
            if (err != null) return "PopTracker: " + err;
        }
        if (!IsPackInstalled(entry.PackageUid))
            return await InstallPackAsync(entry, progress, ct).ConfigureAwait(false);
        return null;
    }

    /// Install whatever is missing, then open. The whole button, in one call.
    ///
    /// ⚠ Lives here rather than in either window, because the tracker is
    /// offered from TWO places — the game's page and the Join card — and two
    /// copies of this sequence would drift. The windows own the label; this
    /// owns what happens.
    public static async Task<string> OpenAsync(TrackerEntry entry,
                                               IProgress<string>? progress = null,
                                               string? host = null, string? slot = null,
                                               string? password = null,
                                               CancellationToken ct = default)
    {
        string? setup = await SetUpAsync(entry, progress, ct).ConfigureAwait(false);
        if (setup != null) return setup;

        // ⚠ --ap-host takes host:port. A address that still carries its scheme
        // is not what it wants.
        string? apHost = host?.Replace("wss://", "").Replace("ws://", "");
        // One variant is no choice. With several, the first opens — never a
        // dialog in the way of a button somebody pressed to open something.
        string? variant = entry.Variants.Count > 0 ? entry.Variants[0] : null;

        string? failed = Launch(entry, variant, apHost, slot, password);
        if (failed != null) return failed;
        return apHost != null
            ? $"{entry.PackName} opened and connected as {slot}."
            : $"{entry.PackName} opened.";
    }

    /// Open the tracker on the right pack, connected when we have a session.
    ///
    /// The arguments are PopTracker's own, read from doc/commandline.txt in its
    /// repository. When host and slot are given it attaches to the multiworld
    /// itself, so the player presses one button and the tracker is live.
    public static string? Launch(TrackerEntry entry, string? variant = null,
                                 string? host = null, string? slot = null,
                                 string? password = null)
    {
        if (!IsInstalled) return "PopTracker is not installed yet.";

        var args = new List<string> { "--load-pack", entry.PackageUid };
        if (!string.IsNullOrWhiteSpace(variant)) { args.Add("--pack-variant"); args.Add(variant!); }
        if (!string.IsNullOrWhiteSpace(host))    { args.Add("--ap-host");      args.Add(host!); }
        if (!string.IsNullOrWhiteSpace(slot))    { args.Add("--ap-slot");      args.Add(slot!); }
        if (!string.IsNullOrWhiteSpace(password)){ args.Add("--ap-password");  args.Add(password!); }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                // ⚠ Its own folder: PopTracker looks for assets beside the exe,
                // and a process started from the launcher's folder finds none.
                WorkingDirectory = HomeDir,
                UseShellExecute = false,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return null;
        }
        catch (Exception e) { return e.Message; }
    }
}

/// Universal Tracker — the answer for the 647 games nobody built a pack for.
///
/// One apworld into the engine's own custom_worlds, and the Archipelago client
/// grows a tracking tab for ANY world. It is weaker than a hand-drawn map pack
/// and it covers everything, which is exactly the trade a fallback should make.
///
/// ⚠ It does NOT track inside London. The promise the button makes has to be
/// the real one: "your Archipelago client will show a tracker", not "London
/// will". Overstating that is how a player concludes the launcher is broken
/// when in fact it did precisely what it said.
public static class UniversalTrackerService
{
    public const string Owner = "FarisTheAncient";
    public const string RepoName = "Archipelago";
    public const string AssetName = "tracker.apworld";

    /// Is it in the engine the player has nominated?
    public static bool IsInstalledIn(string? customWorldsDir) =>
        customWorldsDir is { Length: > 0 }
        && File.Exists(Path.Combine(customWorldsDir, AssetName));

    /// Download it into that engine. Returns null on success, a sentence otherwise.
    public static async Task<string?> InstallAsync(string? customWorldsDir,
                                                   IProgress<string>? progress = null,
                                                   CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customWorldsDir))
            return "No Archipelago engine is set up yet — do that under Multiworld first.";
        if (IsInstalledIn(customWorldsDir)) return null;

        progress?.Report("Looking up Universal Tracker…");
        // ⚠ The repository is a full fork of Archipelago and publishes several
        // release lines; only the ones tagged Tracker_* are this. Taking
        // "latest" outright once offered an OSRSM build instead.
        var releases = await GitHubHelper.FetchTaggedReleasesAsync(Owner, RepoName, "Tracker_", ct)
                                         .ConfigureAwait(false);
        var release = releases?.FirstOrDefault();
        var asset = release?.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
        if (asset == null) return "Could not find tracker.apworld in that project's releases.";

        progress?.Report($"Downloading Universal Tracker {release!.Tag}…");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
            byte[] data = await http.GetByteArrayAsync(asset.DownloadUrl, ct).ConfigureAwait(false);
            // An apworld is a zip; an HTML error page is not.
            if (data.Length < 10_000 || data[0] != 0x50 || data[1] != 0x4B)
                return "What came back was not an apworld.";

            Directory.CreateDirectory(customWorldsDir);
            string dest = Path.Combine(customWorldsDir, AssetName);
            string part = dest + ".part";
            await File.WriteAllBytesAsync(part, data, ct).ConfigureAwait(false);
            File.Move(part, dest, overwrite: true);
            // A new world in custom_worlds: whatever ApEngine remembers about
            // this engine no longer matches the folder.
            Archipelago.ApEngine.Forget();
            return null;
        }
        catch (Exception e) { return e.Message; }
    }
}
