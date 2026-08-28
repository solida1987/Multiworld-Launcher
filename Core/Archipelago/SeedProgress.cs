using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LauncherV2.Core.Archipelago;

/// How many locations each slot in a seed has, read from the seed's own
/// spoiler.
///
/// ⚠ A SeedSlot knows the player number, the name and the game — and nothing
/// about size. So the Join tab could not say "12 of 278" for a slot it was not
/// connected to, which is most of the time. The spoiler has carried the answer
/// since the seed was generated:
///
///     Total Location Count:            2273
///     Player 1: Solida-ALTTP
///     Game:                            A Link to the Past
///     Location Count:                  288
///
/// Parsed once and cached beside the seed, because a spoiler is large and this
/// is read every time the tab is drawn.
public static class SeedSpoiler
{
    public sealed record Sizes(int Total, IReadOnlyDictionary<string, int> BySlot);

    private static readonly Dictionary<string, Sizes> Cache = new(StringComparer.Ordinal);

    public static Sizes For(SeedInfo seed)
    {
        lock (Cache)
            if (Cache.TryGetValue(seed.Id, out var hit)) return hit;

        var sizes = Read(seed);
        lock (Cache) Cache[seed.Id] = sizes;
        return sizes;
    }

    private static Sizes Read(SeedInfo seed)
    {
        var bySlot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        try
        {
            if (seed.SpoilerPath is not { } path || !File.Exists(path))
                return new Sizes(0, bySlot);

            string? slot = null;
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();

                if (line.StartsWith("Total Location Count:", StringComparison.Ordinal))
                {
                    total = Number(line);
                    continue;
                }
                // "Player 1: Solida-ALTTP" — the name after the colon is the
                // slot, which is the only handle the Join card has.
                if (line.StartsWith("Player ", StringComparison.Ordinal)
                    && line.Contains(':'))
                {
                    slot = line[(line.IndexOf(':') + 1)..].Trim();
                    continue;
                }
                if (slot != null
                    && line.StartsWith("Location Count:", StringComparison.Ordinal))
                {
                    bySlot[slot] = Number(line);
                    // ⚠ Cleared, or the next player's own header would be read
                    // as another count for this one. The spoiler repeats the
                    // key for every player.
                    slot = null;
                }

                // The per-player headers all sit above the item table, which is
                // the bulk of the file. Stop there rather than read megabytes.
                if (line.StartsWith("Locations:", StringComparison.Ordinal)) break;
            }
        }
        catch (IOException) { /* an unreadable spoiler is simply no sizes */ }

        return new Sizes(total, bySlot);
    }

    private static int Number(string line)
    {
        string tail = line[(line.IndexOf(':') + 1)..].Trim();
        return int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out int n) ? n : 0;
    }
}

/// What a slot has actually done, kept between sessions.
///
/// ⚠ Live figures live on ApJoinSession and die with it. The Join tab is
/// looked at when nothing is running — that is the whole point of it — so the
/// numbers have to survive being disconnected or the cards are blank exactly
/// when they are being read.
public sealed record SlotProgress(
    [property: JsonPropertyName("seed")]     string SeedId,
    [property: JsonPropertyName("slot")]     string SlotName,
    [property: JsonPropertyName("done")]     int Done,
    [property: JsonPropertyName("total")]    int Total,
    [property: JsonPropertyName("itemsIn")]  int ItemsIn,
    [property: JsonPropertyName("itemsOut")] int ItemsOut,
    [property: JsonPropertyName("seconds")]  long Seconds,
    [property: JsonPropertyName("last")]     DateTime Last);

public static class SeedProgressStore
{
    private static readonly string Path = System.IO.Path.Combine(
        AppContext.BaseDirectory, "Data", "seed_progress.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Sync = new();

    private static string Key(string seedId, string slot) => seedId + "|" + slot;

    public static IReadOnlyList<SlotProgress> All()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(Path)) return Array.Empty<SlotProgress>();
                return JsonSerializer.Deserialize<List<SlotProgress>>(
                           File.ReadAllText(Path)) ?? new List<SlotProgress>();
            }
            catch (IOException)   { return Array.Empty<SlotProgress>(); }
            catch (JsonException) { return Array.Empty<SlotProgress>(); }
        }
    }

    public static SlotProgress? For(string seedId, string slot) =>
        All().FirstOrDefault(p => p.SeedId == seedId
                               && string.Equals(p.SlotName, slot, StringComparison.Ordinal));

    /// Record where a slot got to. Called while a session runs and once when it
    /// stops, so the card is right the moment the player looks at it again.
    ///
    /// Seconds ACCUMULATE; everything else is the latest truth. Time is the one
    /// figure that is a running total rather than a state.
    public static void Record(string seedId, string slot, int done, int total,
                              int itemsIn, int itemsOut, long addSeconds)
    {
        lock (Sync)
        {
            var list = All().ToList();
            int i = list.FindIndex(p => p.SeedId == seedId
                                     && string.Equals(p.SlotName, slot, StringComparison.Ordinal));
            long seconds = (i >= 0 ? list[i].Seconds : 0) + Math.Max(0, addSeconds);
            var row = new SlotProgress(seedId, slot, done, total, itemsIn, itemsOut,
                                       seconds, DateTime.Now);
            if (i >= 0) list[i] = row; else list.Add(row);

            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, JsonSerializer.Serialize(list, Json));
            }
            catch (IOException) { /* losing one write costs a stale card, not a crash */ }
        }
    }
}
