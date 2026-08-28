using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LauncherV2.Core.Archipelago;

/// One player's place in a stored seed. PatchFile is the container the engine
/// wrote for them, when their game has one -- non-ROM games have none, and the
/// slot is still real.
public sealed record SeedSlot(
    [property: JsonPropertyName("player")]     int Player,
    [property: JsonPropertyName("name")]       string Name,
    [property: JsonPropertyName("game")]       string Game,
    [property: JsonPropertyName("patch_file")] string? PatchFile);

/// A seed as the library knows it: the folder, the players, and the files.
public sealed record SeedInfo(
    [property: JsonPropertyName("id")]             string Id,
    [property: JsonPropertyName("created")]        DateTime Created,
    [property: JsonPropertyName("engine_version")] string EngineVersion,
    [property: JsonPropertyName("slots")]          IReadOnlyList<SeedSlot> Slots,
    [property: JsonPropertyName("multidata")]      string MultidataFile,
    [property: JsonPropertyName("spoiler")]        string? SpoilerFile)
{
    [JsonIgnore] public string Folder { get; init; } = "";

    [JsonIgnore] public string MultidataPath => Path.Combine(Folder, MultidataFile);
    [JsonIgnore] public string? SpoilerPath  => SpoilerFile == null ? null : Path.Combine(Folder, SpoilerFile);

    /// The server writes its save as a sibling of the multidata, extension
    /// swapped. Its existence alone does NOT mean the last run was healthy --
    /// a crashed startup writes one too -- which is why the host marker below
    /// exists at all.
    [JsonIgnore] public bool HasSave
        => File.Exists(Path.ChangeExtension(MultidataPath, ".apsave"));
}

// ApSeedLibrary — where generated seeds live, and what London knows about them.
//
// The generator's output is a zip in a temp-named folder: usable by a person
// with patience, invisible to everything else. Ingest unpacks it into a folder
// the launcher owns and writes down what is inside -- who plays what, which
// patch container belongs to whom -- so the Seeds surface has something to
// show and the server has something to host.
//
// Patch containers are matched to players by the manifest INSIDE them, never
// by file name or extension. FRLG alone can emit .apleafgreen or .apfirered
// depending on the yaml, and the multidata's own name says nothing about
// players at all.
public static class ApSeedLibrary
{
    private const string ManifestName = "seed.json";

    /// Settable for the proofs, which must not write into the real library.
    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiworldLauncher", "Multiworld", "library");

    /// Unpacks a generated seed and records it. <paramref name="slots"/> is
    /// what London itself sent to the generator -- the zip cannot tell us the
    /// players for games that produce no patch file, but London already knows.
    public static SeedInfo? Ingest(string zipPath, ApEngine.Report engine,
                                   IReadOnlyList<ApSlot> slots)
    {
        if (!File.Exists(zipPath)) return null;

        string id = Path.GetFileNameWithoutExtension(zipPath);
        string folder = Path.Combine(Root, id);
        // The same seed number produces the same id. Both runs deserve to
        // exist -- the second gets a suffix instead of eating the first.
        for (int n = 2; Directory.Exists(folder); n++)
            folder = Path.Combine(Root, $"{id}_{n}");

        try
        {
            Directory.CreateDirectory(folder);
            ZipFile.ExtractToDirectory(zipPath, folder);

            string? multidata = null, spoiler = null;
            var patches = new List<(string File, string? Game, int Player, string? PlayerName)>();

            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".archipelago", StringComparison.OrdinalIgnoreCase))
                    multidata = name;
                else if (name.Contains("Spoiler", StringComparison.OrdinalIgnoreCase))
                    spoiler = name;
                else if (ReadPatchManifest(file) is { } m)
                    patches.Add((name, m.Game, m.Player, m.PlayerName));
            }

            if (multidata == null)
            {
                // A seed the server cannot host is not a seed. Leave nothing.
                Directory.Delete(folder, true);
                return null;
            }

            // London's own slot list is the spine; the zip's manifests attach
            // the patch files to it. Match by name first (authoritative), then
            // by game for the odd manifest with a blank player_name.
            var seedSlots = new List<SeedSlot>();
            int player = 1;
            foreach (var s in slots)
            {
                var patch = patches.FirstOrDefault(p =>
                                string.Equals(p.PlayerName, s.Name, StringComparison.OrdinalIgnoreCase));
                if (patch.File == null)
                    patch = patches.FirstOrDefault(p =>
                                p.PlayerName == null
                                && string.Equals(p.Game, s.Game, StringComparison.OrdinalIgnoreCase));
                seedSlots.Add(new SeedSlot(player++, s.Name, s.Game, patch.File));
            }

            var info = new SeedInfo(
                Path.GetFileName(folder),
                DateTime.Now,
                engine.Version?.ToString() ?? "unknown",
                seedSlots, multidata, spoiler)
            { Folder = folder };

            File.WriteAllText(Path.Combine(folder, ManifestName),
                JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            return info;
        }
        catch
        {
            // Half an ingest is worse than none: the zip still exists where
            // the generator put it, so nothing is lost by cleaning up.
            try { Directory.Delete(folder, true); } catch { }
            return null;
        }
    }

    /// Every seed the library holds, newest first.
    public static IReadOnlyList<SeedInfo> List()
    {
        var found = new List<SeedInfo>();
        if (!Directory.Exists(Root)) return found;

        foreach (string dir in Directory.EnumerateDirectories(Root))
        {
            string manifest = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifest)) continue;
            try
            {
                var info = JsonSerializer.Deserialize<SeedInfo>(File.ReadAllText(manifest));
                if (info != null) found.Add(info with { Folder = dir });
            }
            catch { /* one unreadable seed must not hide the rest */ }
        }
        return found.OrderByDescending(s => s.Created).ToList();
    }

    public static void Delete(SeedInfo seed)
    {
        try { Directory.Delete(seed.Folder, true); } catch { }
    }

    private sealed record PatchManifest(string? Game, int Player, string? PlayerName);

    /// A patch container is a zip with archipelago.json inside. Anything that
    /// is not one is simply not a patch -- no guessing from extensions.
    private static PatchManifest? ReadPatchManifest(string file)
    {
        try
        {
            using var zip = ZipFile.OpenRead(file);
            var entry = zip.GetEntry("archipelago.json");
            if (entry == null) return null;

            using var s = entry.Open();
            using var doc = JsonDocument.Parse(s);
            var r = doc.RootElement;

            string? game = r.TryGetProperty("game", out var g) ? g.GetString() : null;
            int playerNo = r.TryGetProperty("player", out var p) && p.TryGetInt32(out int pi) ? pi : 0;
            string? playerName = r.TryGetProperty("player_name", out var pn) ? pn.GetString() : null;
            if (string.IsNullOrWhiteSpace(playerName)) playerName = null;

            return new PatchManifest(game, playerNo, playerName);
        }
        catch { return null; }
    }
}
