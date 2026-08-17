using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// What a game can ask of the live AP connection, handed over as ONE
// object. It replaced eleven settable delegates that MainWindow filled in
// one by one — where a forgotten assignment produced no error, only a
// feature that silently did nothing. Thread-safe from plugin threads.
public interface IApServices
{
    // --- Identity ---

    // Our own AP player slot, to tell our things from other people's.
    int OwnSlot { get; }

    // The seed's slot_data. Null before the handshake completes.
    JsonElement? SlotData { get; }

    // The multiworld's seed name — stable per world, so a game can derive
    // reproducible per-world randomisation from it.
    string? SeedName { get; }

    // A player slot's display name (alias if they set one, else their name).
    string ResolvePlayerName(int slot);

    // --- Locations ---

    // Locations already sent this session.
    long[] CheckedLocations();

    // Locations NOT sent yet — the only ones that can still be holding
    // something. Scouting the rest is wasted traffic and a bigger self-spoiler.
    long[] UncheckedLocations();

    // Ask the server what is sitting at these locations.
    // Always a FREE scout (create_as_hint = 0): this must never spend the
    // player's hint points behind their back.
    Task ScoutLocationsAsync(long[] locationIds);

    // Scout replies, as they arrive.
    event Action<ApNetworkItem[]>? LocationsScouted;

    // Ask the server to resend the whole item stream from index 0.
    // Needed after a game's pipe attaches: items delivered while the player
    // was still in the launcher (notably precollected starting items) were
    // sent before there was anywhere to put them.
    Task ResyncAsync();

    // --- DeathLink ---

    // Whether the player opted in. A game must check this before reporting a
    // death — sending anyway would push deaths at people who said no.
    bool DeathLinkEnabled { get; }

    // The player died in-game. Ignored when DeathLink is off.
    void ReportDeath(string? cause);
}
