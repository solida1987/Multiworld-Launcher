using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LauncherV2.Core.Archipelago;

/// One slot on somebody else's server.
///
/// The Join tab was built around seeds in your own library: pick the seed, and
/// every slot in it is listed because the manifest says so. A session hosted
/// elsewhere has no manifest here — all you have is an address and the name
/// you were given. So each of these is added by hand and remembered, and the
/// list of them is what the tab draws when you are not on one of your own.
///
/// Several are normal, not exceptional. In a twelve-player session one person
/// may hold five slots, and each is a separate game with its own connection.
public sealed record ExternalSlot(
    [property: JsonPropertyName("id")]        string Id,
    [property: JsonPropertyName("address")]   string Address,
    [property: JsonPropertyName("slot")]      string SlotName,
    [property: JsonPropertyName("password")]  string Password,
    /// What the server said this slot plays, resolved by the probe when it was
    /// added. Null when the probe could not get in — then the player picked.
    [property: JsonPropertyName("game")]      string? Game,
    [property: JsonPropertyName("added")]     DateTime Added)
{
    /// A short label for the card, e.g. "archipelago.gg:38281".
    public string DisplayAddress =>
        Address.Replace("wss://", "").Replace("ws://", "");
}

/// The saved list. Small enough to rewrite whole on every change.
public static class ExternalSlotStore
{
    private static readonly string Path = System.IO.Path.Combine(
        AppContext.BaseDirectory, "Data", "external_slots.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
    };

    public static IReadOnlyList<ExternalSlot> All()
    {
        try
        {
            if (!File.Exists(Path)) return Array.Empty<ExternalSlot>();
            return JsonSerializer.Deserialize<List<ExternalSlot>>(
                       File.ReadAllText(Path)) ?? new List<ExternalSlot>();
        }
        catch (IOException)   { return Array.Empty<ExternalSlot>(); }
        catch (JsonException) { return Array.Empty<ExternalSlot>(); }
    }

    public static void Add(ExternalSlot slot)
    {
        var list = All().ToList();
        // The same slot name on the same server twice is the same slot, not a
        // second one -- adding it again should refresh it, not duplicate it.
        list.RemoveAll(s => string.Equals(s.Address, slot.Address, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(s.SlotName, slot.SlotName, StringComparison.Ordinal));
        list.Add(slot);
        Save(list);
    }

    public static void Remove(string id)
    {
        var list = All().ToList();
        list.RemoveAll(s => s.Id == id);
        Save(list);
    }

    private static void Save(List<ExternalSlot> list)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(list, Json));
        }
        catch (IOException) { /* a list that cannot be saved is not worth a crash */ }
    }
}
