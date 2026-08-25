namespace LauncherV2.Core.Plugins;

// Hvem har lavet hvad — og hvor meget skal spilleren tage stilling til?
//
// ⚠ AFGØRES AF LAUNCHEREN, IKKE AF PAKKEN. Stod niveauet i plugin.json,
// kunne enhver tredjepart skrive "jeg er launcherens forfatter" og faa det
// gronne stempel. Manifestet er forfatterens eget ord; denne liste er vores.
//
// To uafhaengige spoergsmaal, ikke eet niveau:
//   1. Hvem skrev PLUGINET?
//   2. Hvem lavede SPILLET?
// Kombinationen afgoer hvad dialogen siger.

public enum Made
{
    /// Af den samme som har lavet launcheren.
    LauncherAuthor,
    /// Af en anden.
    ThirdParty,
}

public sealed record Provenance(
    Made Plugin,
    Made Game,
    string? GameName = null,
    string? GameUrl = null,
    string? GameAuthor = null)
{
    /// ⛔ Standarden naar vi ikke kender pluginet: alt er tredjepart.
    /// Et ukendt plugin skal moede den STRENGESTE tekst, ikke den mildeste.
    public static Provenance Unknown => new(Made.ThirdParty, Made.ThirdParty);

    public bool IsFirstPartyPlugin => Plugin == Made.LauncherAuthor;
    public bool IsFirstPartyGame => Game == Made.LauncherAuthor;

    /// Overskriften i samtykkedialogen.
    public string Headline => (Plugin, Game) switch
    {
        (Made.LauncherAuthor, Made.LauncherAuthor) =>
            "Made by the launcher's own developer",
        (Made.LauncherAuthor, Made.ThirdParty) =>
            "Plugin by the launcher's developer — game by someone else",
        _ => "Not made by the launcher's developer",
    };

    /// Brodteksten. Formuleret som oplysning naar der ikke er noget at
    /// advare om, og som en klar advarsel naar der er.
    public string Body => (Plugin, Game) switch
    {
        (Made.LauncherAuthor, Made.LauncherAuthor) =>
            "This plugin and the game it installs were both made by the same "
          + "person who wrote this launcher. Nothing here comes from a third "
          + "party.",

        (Made.LauncherAuthor, Made.ThirdParty) =>
            $"This plugin was written by the launcher's developer, but "
          + $"{GameName ?? "the game"} was not — it is "
          + (GameAuthor is null ? "someone else's work." : $"made by {GameAuthor}.")
          + " Have a look at where it comes from before you install it.",

        (Made.ThirdParty, Made.LauncherAuthor) =>
            "This plugin was written by someone other than the launcher's "
          + "developer. Only install it if you trust whoever made it.",

        _ =>
            "This plugin was not written by the launcher's developer, and "
          + "neither was the game it installs. It runs with the same access as "
          + "the launcher itself. Only continue if you are sure you trust the "
          + "source.",
    };

    /// Hvor meget dialogen skal insistere.
    public bool NeedsExplicitConfirmation => Plugin == Made.ThirdParty
                                          || Game == Made.ThirdParty;
}

/// Launcherens egen liste. ⚠ Dette er den ENESTE kilde til om noget er
/// first-party — aldrig manifestet.
public static class FirstParty
{
    /// gameId -> hvem lavede SPILLET. Pluginet er per definition vores,
    /// naar id'et staar her.
    private static readonly Dictionary<string, Provenance> Known = new()
    {
        ["diablo2_archipelago"] = new(
            Made.LauncherAuthor, Made.ThirdParty,
            GameName: "Diablo II: Lord of Destruction",
            GameAuthor: "Blizzard Entertainment",
            GameUrl: "https://www.blizzard.com"),

        ["openttd_archipelago"] = new(
            Made.LauncherAuthor, Made.ThirdParty,
            GameName: "OpenTTD",
            GameAuthor: "the OpenTTD contributors",
            GameUrl: "https://www.openttd.org"),

        ["zelda2"] = new(
            Made.LauncherAuthor, Made.ThirdParty,
            GameName: "Zelda II: The Adventure of Link",
            GameAuthor: "Nintendo",
            GameUrl: "https://www.nintendo.com"),

        ["terratech"] = new(
            Made.LauncherAuthor, Made.ThirdParty,
            GameName: "TerraTech",
            GameAuthor: "Payload Studios",
            GameUrl: "https://terratechgame.com"),

        ["pokemon_fireruby"] = new(
            Made.LauncherAuthor, Made.ThirdParty,
            GameName: "Pokémon FireRed and Ruby",
            GameAuthor: "Nintendo / Game Freak",
            GameUrl: "https://www.nintendo.com"),
    };

    /// Slaa op. Ukendt id ⇒ tredjepart hele vejen, uanset hvad pakken siger.
    public static Provenance For(string gameId)
        => Known.TryGetValue(gameId, out var p) ? p : Provenance.Unknown;

    public static bool IsKnown(string gameId) => Known.ContainsKey(gameId);
}
