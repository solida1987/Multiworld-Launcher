using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LauncherV2.Core.Plugins;

// What a plugin says it is, read before any of its code runs.
//
// This is the whole point of shipping a manifest next to the assembly: the
// launcher can tell the player who made this, what it installs, and where it
// downloads from, while the plugin is still an inert file. A manifest that lies
// is at least a lie the player can see. An assembly that lies can only be
// caught afterwards.
//
// Nothing here is enforced technically — we cannot sandbox .NET, and pretending
// otherwise would be worse than being honest. `Declares` is a statement by the
// author, shown to the player, and something we can point at if it turns out
// to be false.

/// <summary>What the plugin says it does. Author's word, not a restriction.</summary>
public sealed record PluginDeclarations(
    bool     InstallsFiles,
    string[] DownloadsFrom,
    bool     RunsExternalProcess,
    bool     ConnectsToAp,
    bool     RequiresOriginalGame)
{
    public static PluginDeclarations Empty =>
        new(false, Array.Empty<string>(), false, false, false);

    /// <summary>Bullet lines for the consent dialog. Empty when it claims nothing.</summary>
    public IReadOnlyList<string> Describe(string gameId)
    {
        var lines = new List<string>();
        if (InstallsFiles)
            lines.Add($"installs files in Games\\{gameId}");
        foreach (string host in DownloadsFrom.Where(h => !string.IsNullOrWhiteSpace(h)))
            lines.Add($"downloads from {host.Trim()}");
        if (RunsExternalProcess)   lines.Add("starts an external program");
        if (ConnectsToAp)          lines.Add("connects to Archipelago");
        if (RequiresOriginalGame)  lines.Add("requires you to own the original game");
        return lines;
    }
}

public sealed record PluginManifest(
    int      ApiVersion,
    string   GameId,
    string   DisplayName,
    string   Subtitle,
    string   Version,
    string   Author,
    string?  AuthorContact,
    string   Assembly,
    string   EntryType,
    PluginDeclarations Declares,
    bool     RulesAcknowledged)
{
    public const string FileName = "plugin.json";

    /// <summary>The API revision this launcher can load. Bump when IGamePlugin changes.</summary>
    /// <remarks>
    /// 2: OnLocationTable and OnLocationHints, for games that report checks by
    ///    name instead of id. Both have default implementations, so a plugin
    ///    built for 1 still compiles -- but it would load against a launcher
    ///    that has moved on, so the number moves too.
    /// </remarks>
    public const int CurrentApiVersion = 2;

    // A game id becomes a folder name and a registry key. Anything outside this
    // set is either a path escape or a collision waiting to happen.
    private static readonly Regex GameIdShape = new(@"^[a-z0-9][a-z0-9_]{1,63}$", RegexOptions.Compiled);

    /// <summary>
    /// Parse and validate. Returns null and fills <paramref name="error"/> on
    /// anything wrong — a bad manifest is a normal event, not an exception.
    /// </summary>
    public static PluginManifest? Parse(string json, out string error)
    {
        error = "";
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex) { error = "plugin.json is not valid JSON: " + ex.Message; return null; }

        if (root.ValueKind != JsonValueKind.Object) { error = "plugin.json must be an object"; return null; }

        int api = Int(root, "apiVersion", 0);
        if (api != CurrentApiVersion)
        {
            error = api == 0
                ? "plugin.json has no apiVersion"
                : api < CurrentApiVersion
                    ? $"built for plugin API {api}; this launcher speaks {CurrentApiVersion}. The plugin needs updating."
                    : $"built for plugin API {api}; this launcher only speaks {CurrentApiVersion}. Update the launcher.";
            return null;
        }

        string gameId = Str(root, "gameId") ?? "";
        if (!GameIdShape.IsMatch(gameId))
        {
            error = "gameId must be lowercase letters, digits and underscores (2–64 chars); got " + Quote(gameId);
            return null;
        }

        string assembly = Str(root, "assembly") ?? "";
        // The assembly name is used to build a path. A name containing a
        // separator would let a package point at a file outside its own folder.
        if (assembly.Length == 0
            || assembly.IndexOfAny(new[] { '/', '\\', ':' }) >= 0
            || assembly.Contains("..")
            || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            error = "assembly must be a plain .dll filename inside the package; got " + Quote(assembly);
            return null;
        }

        string entry = Str(root, "entryType") ?? "";
        if (entry.Length == 0) { error = "entryType is missing — name the class that implements IGamePlugin"; return null; }

        string display = Str(root, "displayName") ?? "";
        if (display.Length == 0) { error = "displayName is missing"; return null; }

        string author = Str(root, "author") ?? "";
        if (author.Length == 0) { error = "author is missing — the player has to know whose code this is"; return null; }

        // Not a legal device, and we know it. It is a prompt: you cannot ship a
        // plugin without having been told the rules exist and saying you read
        // them. "I didn't know" is the excuse this field removes.
        if (!Bool(root, "rulesAcknowledged"))
        {
            error = "rulesAcknowledged is not true — see PLUGIN_API.md, 'Your responsibility'";
            return null;
        }

        var declares = ParseDeclarations(root);

        return new PluginManifest(
            api, gameId, display,
            Str(root, "subtitle") ?? "",
            Str(root, "version") ?? "0.0.0",
            author,
            Str(root, "authorContact"),
            assembly, entry, declares, true);
    }

    private static PluginDeclarations ParseDeclarations(JsonElement root)
    {
        if (!root.TryGetProperty("declares", out var d) || d.ValueKind != JsonValueKind.Object)
            return PluginDeclarations.Empty;

        string[] hosts = Array.Empty<string>();
        if (d.TryGetProperty("downloadsFrom", out var arr) && arr.ValueKind == JsonValueKind.Array)
            hosts = arr.EnumerateArray()
                       .Where(e => e.ValueKind == JsonValueKind.String)
                       .Select(e => e.GetString()!)
                       .Where(s => !string.IsNullOrWhiteSpace(s))
                       .ToArray();

        return new PluginDeclarations(
            Bool(d, "installsFiles"), hosts,
            Bool(d, "runsExternalProcess"),
            Bool(d, "connectsToAp"),
            Bool(d, "requiresOriginalGame"));
    }

    private static string? Str(JsonElement o, string k)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString()?.Trim() : null;

    private static int Int(JsonElement o, string k, int fallback)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out int i) ? i : fallback;

    private static bool Bool(JsonElement o, string k)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Quote(string s) => s.Length == 0 ? "(empty)" : "\"" + s + "\"";
}
