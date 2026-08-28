using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using LauncherV2.Core.Patching;

namespace LauncherV2.Core;

/// Remembers which patch file belongs to which (seed, slot), so a player is
/// asked for it exactly once.
///
/// Why a store and not just the file name: the generator names its output
/// AP_&lt;seed&gt;_P1_&lt;slot&gt;.ap&lt;game&gt;, but that is a convention, not a contract --
/// a renamed file is still a valid patch, and a file named after one seed is
/// not proof it belongs to it. The patch's own manifest carries player_name and
/// game but NO seed, so the seed link cannot be read out of the file at all.
/// It only exists at the moment the player hands the patch over while connected
/// to that seed, which is exactly when this records it.
public sealed class SeedPatchStore
{
    public sealed class Entry
    {
        [JsonPropertyName("seed")]     public string Seed     { get; set; } = "";
        [JsonPropertyName("slot")]     public string Slot     { get; set; } = "";
        [JsonPropertyName("file")]     public string File     { get; set; } = "";
        [JsonPropertyName("added")]    public string Added    { get; set; } = "";
        [JsonPropertyName("game")]     public string Game     { get; set; } = "";
    }

    private readonly string _gameId;
    private readonly List<Entry> _entries = new();
    private readonly object _gate = new();

    private SeedPatchStore(string gameId)
    {
        _gameId = gameId;
        Load();
    }

    private static readonly Dictionary<string, SeedPatchStore> Cache = new();

    public static SeedPatchStore For(string gameId)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(gameId, out var s))
                Cache[gameId] = s = new SeedPatchStore(gameId);
            return s;
        }
    }

    private string FilePath => Path.Combine(
        AppContext.BaseDirectory, "Data", $"{_gameId}_patches.json");

    /// Where this game's patch files live. Same folder the drag-and-drop import
    /// writes to, so a patch dropped on the window and one picked in the dialog
    /// end up in the same place.
    public string PatchDirectory => Path.Combine(
        AppContext.BaseDirectory, "Games", "ROMs", _gameId, "patches");

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath));
            if (list != null) _entries.AddRange(list);
        }
        catch { /* an unreadable store is an empty one; the player re-picks */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string json = JsonSerializer.Serialize(_entries,
                new JsonSerializerOptions { WriteIndented = true });
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* losing the mapping costs one extra question, not the session */ }
    }

    /// Seeds are compared case-sensitively (the server's own string) but slots
    /// case-insensitively, because a player typing their slot name in the
    /// connect box is not expected to match capitalisation.
    private static bool Matches(Entry e, string seed, string slot)
        => string.Equals(e.Seed, seed, StringComparison.Ordinal)
        && string.Equals(e.Slot, slot, StringComparison.OrdinalIgnoreCase);

    /// The patch for this (seed, slot), or null when we have never been given
    /// one -- or were given one that has since been deleted, which counts as
    /// never: a remembered path to a file that is gone is worse than no memory,
    /// because it makes the launcher confidently do nothing.
    public string? Resolve(string seed, string slot)
    {
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => Matches(x, seed, slot));
            if (e == null) return null;

            string full = Path.Combine(PatchDirectory, e.File);
            if (File.Exists(full)) return full;

            _entries.Remove(e);
            Save();
            return null;
        }
    }

    /// Copy `sourcePath` into the game's patch folder and remember it for this
    /// (seed, slot). The source is only ever READ -- a player who picked the
    /// file out of their Downloads folder keeps it.
    ///
    /// Returns the stored path. Throws with a player-readable message when the
    /// patch does not belong to this slot: catching that here, before anything
    /// is patched, is the whole point of asking.
    public string Import(string sourcePath, string seed, string slot, string apWorldName)
    {
        var manifest = ApPatch.ReadManifest(sourcePath)
            ?? throw new InvalidDataException(
                $"{Path.GetFileName(sourcePath)} is not an Archipelago patch.");

        if (!string.Equals(manifest.Game, apWorldName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"That patch is for {manifest.Game}, not {apWorldName}.");

        if (!string.IsNullOrWhiteSpace(manifest.PlayerName)
            && !string.Equals(manifest.PlayerName, slot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"That patch belongs to slot \"{manifest.PlayerName}\", but you are "
              + $"connected as \"{slot}\".\n\nPick the patch generated for your own slot.");

        Directory.CreateDirectory(PatchDirectory);

        // Name the stored copy after the seed and slot rather than keeping the
        // original name: two seeds can hand out identically named files, and the
        // second would otherwise overwrite the first.
        string safe = string.Concat($"{seed}_{slot}"
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string fileName = safe + manifest.PatchFileEnding;
        string dest = Path.Combine(PatchDirectory, fileName);

        if (Path.GetFullPath(sourcePath) != Path.GetFullPath(dest))
            File.Copy(sourcePath, dest, overwrite: true);

        lock (_gate)
        {
            _entries.RemoveAll(x => Matches(x, seed, slot));
            _entries.Add(new Entry
            {
                Seed  = seed,
                Slot  = slot,
                File  = fileName,
                Game  = manifest.Game,
                Added = DateTimeOffset.UtcNow.ToString("O"),
            });
            Save();
        }

        return dest;
    }

    /// Store a file for (seed, slot) WITHOUT reading a patch manifest.
    ///
    /// For the games whose world builds the ROM in its own code there is no
    /// container to inspect -- the thing the player supplies IS the finished,
    /// randomized game file. The plugin has already checked what it can (that
    /// the file is not the vanilla dump); this just files it under the seed so
    /// the next Play finds it without asking.
    public string ImportRaw(string sourcePath, string seed, string slot, string apWorldName)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The chosen file does not exist.", sourcePath);

        Directory.CreateDirectory(PatchDirectory);

        string safe = string.Concat($"{seed}_{slot}"
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string fileName = safe + Path.GetExtension(sourcePath);
        string dest = Path.Combine(PatchDirectory, fileName);

        if (Path.GetFullPath(sourcePath) != Path.GetFullPath(dest))
            File.Copy(sourcePath, dest, overwrite: true);

        lock (_gate)
        {
            _entries.RemoveAll(x => Matches(x, seed, slot));
            _entries.Add(new Entry
            {
                Seed  = seed,
                Slot  = slot,
                File  = fileName,
                Game  = apWorldName,
                Added = DateTimeOffset.UtcNow.ToString("O"),
            });
            Save();
        }
        return dest;
    }

    /// Every patch already sitting in the folder that belongs to this seed but
    /// has not been claimed by a slot yet. Lets one pick cover a whole seed:
    /// a player who drops in all eight files for an eight-player game is not
    /// asked eight times.
    public int AdoptFolder(string folder, string seed, string apWorldName)
    {
        int adopted = 0;
        if (!Directory.Exists(folder)) return 0;

        foreach (string f in Directory.EnumerateFiles(folder))
        {
            var m = ApPatch.ReadManifest(f);
            if (m == null || string.IsNullOrWhiteSpace(m.PlayerName)) continue;
            if (!string.Equals(m.Game, apWorldName, StringComparison.OrdinalIgnoreCase)) continue;
            if (Resolve(seed, m.PlayerName) != null) continue;

            try { Import(f, seed, m.PlayerName!, apWorldName); adopted++; }
            catch { /* one unusable file must not stop the rest */ }
        }
        return adopted;
    }
}
