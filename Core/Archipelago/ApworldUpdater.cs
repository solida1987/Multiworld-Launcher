using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Archipelago;

/// One game's world file, as its author publishes it.
public sealed class ApworldEntry
{
    [JsonPropertyName("game")]          public string Game { get; set; } = "";
    [JsonPropertyName("ap_world_name")] public string ApWorldName { get; set; } = "";
    [JsonPropertyName("source")]        public string Source { get; set; } = "";
    [JsonPropertyName("asset")]         public string Asset { get; set; } = "";
    [JsonPropertyName("url")]           public string Url { get; set; } = "";
    [JsonPropertyName("size")]          public long Size { get; set; }
    [JsonPropertyName("tag")]           public string Tag { get; set; } = "";
    [JsonPropertyName("published")]     public string Published { get; set; } = "";
    [JsonPropertyName("prerelease")]    public bool Prerelease { get; set; }
}

public sealed class ApworldIndex
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("games")] public Dictionary<string, ApworldEntry> Games { get; set; } = new();
}

/// Which world file belongs to which game, published beside the store.
///
/// The address in a game's own manifest is almost always a releases PAGE, not
/// a file. Resolving that page to the right asset needs the GitHub API and a
/// rule for repositories that publish many worlds at once, so it happens in
/// our tooling and the answer is published — the player's machine reads one
/// small file instead of spending its rate limit.
public static class ApworldCatalog
{
    private const string Url =
        "https://raw.githubusercontent.com/solida1987/london-plugin-catalog/main/catalog/apworlds.json";

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiworldLauncher", "apworlds_cache.json");

    private static ApworldIndex? _memory;

    public static async Task<ApworldIndex?> FetchAsync(CancellationToken ct = default)
    {
        if (_memory != null) return _memory;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
            string json = await http.GetStringAsync(Url, ct).ConfigureAwait(false);
            _memory = JsonSerializer.Deserialize<ApworldIndex>(json);
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
                _memory = JsonSerializer.Deserialize<ApworldIndex>(File.ReadAllText(CachePath));
        }
        catch (Exception) { }
        return _memory;
    }

    public static async Task<ApworldEntry?> ForGameAsync(string gameId,
                                                         CancellationToken ct = default)
    {
        var index = await FetchAsync(ct).ConfigureAwait(false);
        if (index?.Games == null || string.IsNullOrWhiteSpace(gameId)) return null;
        return index.Games.TryGetValue(gameId, out var e) && e.Url.Length > 0 ? e : null;
    }
}

/// What London actually wrote into custom_worlds, per game.
///
/// Without this the launcher can see a file and know nothing about it — not
/// which release it came from, not whether the one on the internet is newer.
/// "Is there an update?" is a question about provenance, not about bytes.
public sealed class ApworldInstalled
{
    [JsonPropertyName("asset")]        public string Asset { get; set; } = "";
    [JsonPropertyName("url")]          public string Url { get; set; } = "";
    [JsonPropertyName("tag")]          public string Tag { get; set; } = "";
    [JsonPropertyName("size")]         public long Size { get; set; }
    [JsonPropertyName("sha256")]       public string Sha256 { get; set; } = "";
    [JsonPropertyName("installed_at")] public string InstalledAt { get; set; } = "";
}

public static class ApworldRecord
{
    private static string PathOnDisk => Path.Combine(
        AppContext.BaseDirectory, "Data", "apworlds_installed.json");

    private static Dictionary<string, ApworldInstalled> Load()
    {
        try
        {
            if (File.Exists(PathOnDisk))
                return JsonSerializer.Deserialize<Dictionary<string, ApworldInstalled>>(
                    File.ReadAllText(PathOnDisk)) ?? new();
        }
        catch (Exception) { /* unreadable: start from nothing rather than throw */ }
        return new();
    }

    public static ApworldInstalled? Get(string gameId)
        => Load().TryGetValue(gameId, out var r) ? r : null;

    public static void Set(string gameId, ApworldInstalled rec)
    {
        try
        {
            var all = Load();
            all[gameId] = rec;
            Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
            string tmp = PathOnDisk + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, PathOnDisk, overwrite: true);
        }
        catch (Exception) { /* non-fatal: the world is installed either way */ }
    }
}

public enum ApworldState
{
    /// Nothing to offer: the world ships inside the plugin, or nobody could
    /// resolve where it is published.
    None,
    /// There is a world to fetch and it is not in the engine yet.
    Missing,
    /// A newer release than the one on disk.
    UpdateAvailable,
    UpToDate,
    /// A world exists but London cannot tell — no engine, or no network.
    Unknown,
}

public sealed record ApworldStatus(ApworldState State, string Detail,
                                   ApworldEntry? Entry, string? FilePath)
{
    /// Should the button be shown at all?
    public bool Actionable => State is ApworldState.Missing
                                    or ApworldState.UpdateAvailable;
}

/// Keeping the engine's custom_worlds current, one game at a time or all at once.
///
/// ⚠ Deliberately NOT automatic. Updating the game and updating its world are
/// two decisions: a world built for a newer release can refuse to generate with
/// the seed a group is already playing. London lights a button and waits.
public static class ApworldUpdater
{
    public static string? WorldsDir()
    {
        try
        {
            var s = SettingsStore.Load();
            var eng = ApEngine.Discover(
                string.IsNullOrWhiteSpace(s.ApEnginePath) ? null : s.ApEnginePath);
            return eng is { Usable: true } ? eng.CustomWorldsDir : null;
        }
        catch (Exception) { return null; }
    }

    /// Games the engine already ships a world for.
    ///
    /// ⚠⚠ Downloading one of these is not merely wasted: Archipelago loads the
    /// bundled copy and prints "Did not load X as its game Y is already
    /// loaded", so the file London put there does nothing at all while looking
    /// exactly like a world that is installed and current.
    ///
    /// Read from the engine rather than listed here, because the bundled set
    /// grows with every Archipelago release — a list in the catalogue would be
    /// wrong the week after it was written.
    ///
    /// Cached per process: 70-odd small zips, and the answer cannot change
    /// while the launcher is running.
    private static HashSet<string>? _bundled;
    private static HashSet<string>? _bundledModules;

    /// The one sentence CheckAsync uses for a bundled world, so callers that
    /// want to count those separately compare against a constant rather than
    /// a copy of the prose.
    public const string BundledDetail = "Archipelago already ships this world";

    /// The folder the engine keeps its own worlds in, or null without an engine.
    private static string? BundledDir(string? engineRootOverride)
    {
        string? root = engineRootOverride;
        if (root == null)
        {
            var s = SettingsStore.Load();
            root = ApEngine.Discover(
                string.IsNullOrWhiteSpace(s.ApEnginePath) ? null : s.ApEnginePath)?.Root;
        }
        if (root == null) return null;
        string dir = Path.Combine(root, "lib", "worlds");
        return Directory.Exists(dir) ? dir : null;
    }

    public static HashSet<string> BundledGames(string? engineRootOverride = null)
    {
        if (_bundled != null && engineRootOverride == null) return _bundled;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? dir = BundledDir(engineRootOverride);
            if (dir == null) return _bundled = found;

            foreach (string f in Directory.EnumerateFiles(dir, "*.apworld"))
            {
                try
                {
                    // The world's own word, the same field the engine keys on.
                    string? g = WorldNameInside(File.ReadAllBytes(f));
                    if (g is { Length: > 0 }) found.Add(g);
                }
                catch (Exception) { /* one unreadable world is not a failure */ }
            }
        }
        catch (Exception) { /* no engine, no bundled list — download as before */ }
        if (engineRootOverride == null) _bundled = found;
        return found;
    }

    /// The FILE stems the engine ships — "musedash", "mm3" — beside the game
    /// names above.
    ///
    /// ⚠ A second signal, because the first has a hole: a bundled world with no
    /// archipelago.json inside declares no game name, and a catalogue row for
    /// it would sail past BundledGames. Archipelago imports every world as
    /// `worlds.<stem>`, so two files with one stem are one module twice, and
    /// the stem alone is enough to know the download would be dead on arrival.
    public static HashSet<string> BundledModules(string? engineRootOverride = null)
    {
        if (_bundledModules != null && engineRootOverride == null) return _bundledModules;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? dir = BundledDir(engineRootOverride);
            if (dir != null)
                foreach (string f in Directory.EnumerateFiles(dir, "*.apworld"))
                    found.Add(Path.GetFileNameWithoutExtension(f));
        }
        catch (Exception) { }
        if (engineRootOverride == null) _bundledModules = found;
        return found;
    }

    /// Does the engine already carry this catalogue row's world, by either
    /// name it goes under?
    public static bool IsBundled(ApworldEntry entry, string? engineRootOverride = null)
    {
        if (entry.ApWorldName is { Length: > 0 } name
            && BundledGames(engineRootOverride).Contains(name)) return true;
        string stem = Path.GetFileNameWithoutExtension(entry.Asset ?? "");
        return stem.Length > 0 && BundledModules(engineRootOverride).Contains(stem);
    }

    public static async Task<ApworldStatus> CheckAsync(string gameId,
                                                       CancellationToken ct = default)
    {
        ApworldEntry? entry;
        try { entry = await ApworldCatalog.ForGameAsync(gameId, ct).ConfigureAwait(false); }
        catch (Exception) { return new(ApworldState.Unknown, "could not reach the catalogue", null, null); }
        if (entry == null)
            return new(ApworldState.None, "", null, null);

        string? worlds = WorldsDir();
        if (worlds == null)
            return new(ApworldState.Unknown,
                       "no Archipelago engine is set up yet", entry, null);

        // ⚠⚠ Nothing to do when Archipelago already ships this world.
        //
        // Putting a second copy in custom_worlds does not override the
        // bundled one — the engine loads whichever it reaches first and says
        // so out loud: "Did not load mm3.apworld as its game Mega Man 3 is
        // already loaded". The download would sit there doing nothing while
        // presenting as an installed, current world.
        //
        // Measured on a real engine: three catalogue rows (Mega Man 3,
        // Satisfactory, Shapez) name games Archipelago 0.6.7 bundles.
        if (IsBundled(entry))
            return new(ApworldState.None, BundledDetail, entry, null);

        string path = Path.Combine(worlds, entry.Asset);
        var rec = ApworldRecord.Get(gameId);

        // The file the record names may differ from the one published now:
        // an author who renames the asset has still shipped an update.
        string? onDisk = File.Exists(path) ? path
            : rec is { Asset.Length: > 0 } && File.Exists(Path.Combine(worlds, rec.Asset))
                ? Path.Combine(worlds, rec.Asset)
                : null;

        if (onDisk == null)
            return new(ApworldState.Missing,
                       $"{entry.Asset} is not in your engine yet", entry, path);

        // A recorded release tag is the honest comparison. Without one — the
        // player put the file there themselves — size is the only evidence,
        // and it is treated as evidence, not proof.
        if (rec != null && rec.Tag.Length > 0 && entry.Tag.Length > 0)
            return string.Equals(rec.Tag, entry.Tag, StringComparison.OrdinalIgnoreCase)
                ? new(ApworldState.UpToDate, $"{entry.Tag}", entry, onDisk)
                : new(ApworldState.UpdateAvailable,
                      $"{rec.Tag} installed, {entry.Tag} published", entry, onDisk);

        try
        {
            long len = new FileInfo(onDisk).Length;
            if (entry.Size > 0 && len != entry.Size)
                return new(ApworldState.UpdateAvailable,
                           $"the published {entry.Asset} is a different file", entry, onDisk);
        }
        catch (Exception) { }

        return new(ApworldState.UpToDate,
                   entry.Tag.Length > 0 ? entry.Tag : "already in your engine", entry, onDisk);
    }

    /// Fetch this game's world into the engine. Returns null on success.
    public static async Task<string?> UpdateAsync(string gameId,
                                                  IProgress<string>? progress = null,
                                                  CancellationToken ct = default)
    {
        var status = await CheckAsync(gameId, ct).ConfigureAwait(false);
        if (status.Entry is not { } entry)
            return "There is no world for London to fetch for this game.";

        // ⚠⚠ CheckAsync hands the entry back even when it says None, so a
        // bundled world reached this far with a perfectly good download
        // address. Every button hides itself on !Actionable, but a refusal
        // that lives only in the buttons is a refusal the next caller forgets.
        // The download itself has to say no.
        if (status.State == ApworldState.None)
            return status.Detail.Length > 0
                ? $"{entry.Asset} was not fetched — {status.Detail}."
                : "There is no world for London to fetch for this game.";
        string? worlds = WorldsDir();
        if (worlds == null)
            return "No Archipelago engine is set up yet — do that under Multiworld first.";

        progress?.Report($"Downloading {entry.Asset}…");
        byte[] data;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
            data = await http.GetByteArrayAsync(entry.Url, ct).ConfigureAwait(false);
        }
        catch (Exception e) { return $"{entry.Asset} could not be downloaded: {e.Message}"; }

        // An .apworld is a zip. An error page in custom_worlds breaks EVERY
        // generation, not just this game's.
        if (data.Length < 200 || data[0] != 0x50 || data[1] != 0x4B)
            return $"What came back from {entry.Source} was not a world file.";

        // ⚠⚠ And it must be THIS game's world. Several of these repositories
        // publish many worlds from one release, and one of them once offered
        // sonic_battle.apworld as ActRaiser's. A foreign world in custom_worlds
        // is not a wrong button — it is every seed in the folder failing.
        string? isReally = WorldNameInside(data);
        if (isReally != null && entry.ApWorldName.Length > 0
            && !Fold(isReally).Equals(Fold(entry.ApWorldName), StringComparison.Ordinal))
            return $"That file calls itself \"{isReally}\", not \"{entry.ApWorldName}\" "
                 + "— London did not install it.";

        // ⚠⚠ THE FILE IS NAMED AFTER THE MODULE INSIDE IT, not after whatever
        // the author called the asset on their release page.
        //
        // Archipelago imports a world as `worlds.<filename without .apworld>`,
        // so the stem has to be the module's own name. An author who puts the
        // version in the filename ships something the engine cannot load at
        // all — measured, from a real generation log:
        //
        //   cyberpunk2077-0.7.1.apworld
        //     -> ModuleNotFoundError: No module named 'worlds.cyberpunk2077-0'
        //
        // The dot truncates the module path. Two of the worlds in that
        // player's folder were dead for exactly this reason, and four rows in
        // the catalogue would have delivered more. Dashes and leading digits
        // are fine; the dot is the one that breaks it.
        //
        // The zip's single top-level folder IS the module name, so the file
        // can always be named correctly without guessing.
        string fileName = ModuleNameInside(data) is { Length: > 0 } mod
            ? mod + ".apworld"
            : entry.Asset;

        try
        {
            Directory.CreateDirectory(worlds);
            string dest = Path.Combine(worlds, fileName);
            string part = dest + ".part";
            await File.WriteAllBytesAsync(part, data, ct).ConfigureAwait(false);
            File.Move(part, dest, overwrite: true);

            // An author who renamed the asset leaves the old file behind, and
            // Archipelago would load BOTH copies of the same world. London
            // clears that up — but ONLY the copy London itself put there.
            //
            // ⚠ The published asset name is NOT ours to delete. A file sitting
            // in the player's folder under that name may be their own copy,
            // put there by hand or by some other tool, and deleting it because
            // we happen to be installing the same game is us throwing away
            // somebody else's file without asking. That is what ApworldDoctor
            // exists for: it notices the broken name, explains it, and offers.
            //
            // ApworldRecord is London's own bookkeeping of what London wrote,
            // so it is the only safe answer to "did we put this here?".
            if (ApworldDoctor.OurStaleCopy(ApworldRecord.Get(gameId)?.Asset, fileName)
                is { } ours)
            {
                try { File.Delete(Path.Combine(worlds, ours)); }
                catch (Exception) { /* leaving it is not worth failing over */ }
            }

            ApworldRecord.Set(gameId, new ApworldInstalled
            {
                Asset = fileName,
                Url = entry.Url,
                Tag = entry.Tag,
                Size = data.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
                InstalledAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"),
            });
            ApEngine.Forget();
            progress?.Report($"{entry.Asset} installed.");
            return null;
        }
        catch (Exception e) { return $"{entry.Asset} could not be written: {e.Message}"; }
    }

    /// Every game this sweep should consider.
    ///
    /// ⚠⚠ NOT the same as "games installed in London", which is what the first
    /// version used. What is being updated lives in the ENGINE, and the engine
    /// carries worlds for games London has never installed — 36 of them on the
    /// machine that found this, 14 of which this catalogue knows by name, and
    /// not one belonging to an installed game. The sweep reported nothing to do
    /// while fourteen worlds sat there going stale.
    ///
    /// So: what London has installed, PLUS whatever world files are already in
    /// custom_worlds. A world in the folder is a world worth keeping current,
    /// whoever put it there.
    /// <param name="worldsDirOverride">A folder to read instead of the engine's.
    /// Only the proof passes this — it is how "does this find a world sitting
    /// in the folder?" can be answered without standing up a real engine.</param>
    public static async Task<List<string>> CandidatesAsync(
        IEnumerable<string> installedGameIds, string? worldsDirOverride = null,
        CancellationToken ct = default)
    {
        var ids = new HashSet<string>(installedGameIds, StringComparer.OrdinalIgnoreCase);

        var index = await ApworldCatalog.FetchAsync(ct).ConfigureAwait(false);
        string? worlds = worldsDirOverride ?? WorldsDir();
        if (index?.Games != null && worlds != null && Directory.Exists(worlds))
        {
            HashSet<string> present;
            try
            {
                present = new HashSet<string>(
                    Directory.EnumerateFiles(worlds, "*.apworld")
                             .Select(f => Path.GetFileName(f)),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception) { return ids.ToList(); }

            foreach (var pair in index.Games)
                if (pair.Value.Asset.Length > 0 && present.Contains(pair.Value.Asset))
                    ids.Add(pair.Key);
        }
        return ids.ToList();
    }

    /// Every candidate whose world is behind. Returns one line per game.
    public static async Task<List<string>> UpdateAllAsync(IEnumerable<string> gameIds,
                                                          IProgress<string>? progress = null,
                                                          CancellationToken ct = default)
    {
        var lines = new List<string>();
        foreach (string id in gameIds)
        {
            ct.ThrowIfCancellationRequested();
            var status = await CheckAsync(id, ct).ConfigureAwait(false);
            if (!status.Actionable) continue;
            progress?.Report($"{status.Entry!.Game}…");
            string? err = await UpdateAsync(id, null, ct).ConfigureAwait(false);
            lines.Add(err == null
                ? $"{status.Entry.Game}: {status.Entry.Asset} {status.Entry.Tag}".TrimEnd()
                : $"{status.Entry.Game}: {err}");
        }
        return lines;
    }

    /// The name a world calls itself, read out of its own archipelago.json.
    /// Null when the file does not say — an older world, or not one at all.
    ///
    /// Public because it is the one property worth proving: a foreign world in
    /// custom_worlds breaks every generation in the folder, so the refusal
    /// above has to be exercised against real files, not argued for.
    public static string? WorldNameInside(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith("archipelago.json", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            using var s = entry.Open();
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty("game", out var g) ? g.GetString() : null;
        }
        catch (Exception) { return null; }
    }

    /// The module folder inside an .apworld — which is the name Archipelago
    /// will import it under, and therefore the name the FILE has to have.
    ///
    /// Null when the archive does not have exactly one top-level folder: that
    /// is not a shape we can name with confidence, and a wrong rename is worse
    /// than the author's own name.
    public static string? ModuleNameInside(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var tops = zip.Entries
                .Select(e => e.FullName.Replace('\\', '/'))
                .Where(n => n.Contains('/'))
                .Select(n => n[..n.IndexOf('/')])
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToList();
            return tops.Count == 1 ? tops[0] : null;
        }
        catch (Exception) { return null; }
    }

    private static string Fold(string s)
        => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
