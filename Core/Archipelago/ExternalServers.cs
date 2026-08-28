using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LauncherV2.Core.Archipelago;

/// A server somebody else is hosting, with a name you gave it.
///
/// The Join tab was built around seeds in your own library: pick the seed from
/// the dropdown, and its slots are the cards. A session hosted elsewhere had
/// nothing to pick — every external slot was appended to whatever seed was
/// showing, so a player with slots on three different servers saw all of them
/// piled under one unrelated seed. There was no way to say "show me Børge's
/// game", because nothing knew Børge existed.
///
/// So an address gets a name, and a named server takes its place in the same
/// dropdown a seed does. It exists BEFORE it has any slots, which is the point:
/// you add the server, then fill in the slots you were given, one at a time.
public sealed record ExternalServer(
    [property: JsonPropertyName("id")]       string Id,
    [property: JsonPropertyName("name")]     string Name,
    [property: JsonPropertyName("address")]  string Address,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("added")]    DateTime Added)
{
    /// "archipelago.gg:38281" — the address without the scheme nobody types.
    public string DisplayAddress =>
        Address.Replace("wss://", "").Replace("ws://", "");

    /// What the dropdown shows. The name leads, because the name is what the
    /// player was told ("join Børge's game"), not the port number.
    public string DropdownLabel(int slots) =>
        $"{Name}   ·   {DisplayAddress}   ·   "
        + (slots == 1 ? "1 slot" : $"{slots} slots");
}

/// The saved list of servers. Small; rewritten whole on every change.
public static class ExternalServerStore
{
    private static readonly string Path = System.IO.Path.Combine(
        AppContext.BaseDirectory, "Data", "external_servers.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static IReadOnlyList<ExternalServer> All()
    {
        try
        {
            if (!File.Exists(Path)) return Array.Empty<ExternalServer>();
            return JsonSerializer.Deserialize<List<ExternalServer>>(
                       File.ReadAllText(Path)) ?? new List<ExternalServer>();
        }
        catch (IOException)   { return Array.Empty<ExternalServer>(); }
        catch (JsonException) { return Array.Empty<ExternalServer>(); }
    }

    public static ExternalServer? ById(string? id) =>
        id == null ? null : All().FirstOrDefault(s => s.Id == id);

    public static ExternalServer Add(string name, string address, string password)
    {
        var server = new ExternalServer(
            Guid.NewGuid().ToString("N")[..8],
            string.IsNullOrWhiteSpace(name) ? address : name.Trim(),
            address.Trim(), password ?? "", DateTime.Now);

        var list = All().ToList();
        list.Add(server);
        Save(list);
        return server;
    }

    public static void Remove(string id)
    {
        Save(All().Where(s => s.Id != id).ToList());
        // The slots go with it. A slot whose server is gone can never be
        // joined and would sit in the file forever, invisible and unjoinable.
        foreach (var slot in ExternalSlotStore.All().Where(s => s.ServerId == id))
            ExternalSlotStore.Remove(slot.Id);
    }

    public static void Rename(string id, string name)
    {
        var list = All().ToList();
        int i = list.FindIndex(s => s.Id == id);
        if (i < 0) return;
        list[i] = list[i] with { Name = name.Trim() };
        Save(list);
    }

    private static void Save(List<ExternalServer> list)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(list, Json));
        }
        catch (IOException) { /* a list we cannot save is one we ask for again */ }
    }

    /// Give every slot from before this existed a server to belong to.
    ///
    /// ⚠ Runs on every read of the Join tab, and does nothing once there is
    /// nothing to move. Slots saved by earlier versions carry an address and
    /// no ServerId; without this they would vanish from a tab that now draws
    /// by server — the player's own saved slots, gone, with no message.
    public static void MigrateLooseSlots()
    {
        var loose = ExternalSlotStore.All()
            .Where(s => string.IsNullOrEmpty(s.ServerId)).ToList();
        if (loose.Count == 0) return;

        var byAddress = All().ToDictionary(s => s.Address, StringComparer.OrdinalIgnoreCase);
        foreach (var slot in loose)
        {
            if (!byAddress.TryGetValue(slot.Address, out var server))
            {
                // Named after the address, because that is genuinely all we
                // know. The player can rename it.
                server = Add(slot.DisplayAddress, slot.Address, slot.Password);
                byAddress[slot.Address] = server;
            }
            ExternalSlotStore.Attach(slot.Id, server.Id);
        }
    }
}
