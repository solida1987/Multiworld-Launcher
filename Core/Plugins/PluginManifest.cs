using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LauncherV2.Core.Plugins;

// plugin.json, read and validated before any plugin code runs. The
// declares block is what the consent dialog shows.

/// What the plugin says it does. Author's word, not a restriction.
public sealed record PluginDeclarations(
    bool     InstallsFiles,
    string[] DownloadsFrom,
    bool     RunsExternalProcess,
    bool     ConnectsToAp,
    bool     RequiresOriginalGame)
{
    public static PluginDeclarations Empty =>
        new(false, Array.Empty<string>(), false, false, false);

    /// Bullet lines for the consent dialog. Empty when it claims nothing.
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

/// Where a plugin publishes its own newer builds.
///
/// Served by the plugin's OWN project, not by us. The launcher is a host, not a
/// distributor: pointing every plugin's update check at something we run would
/// make us the supply chain for other people's code.
///
/// `VersionUrl` is a two-line VersionFeed file; `PackageUrl` is the
/// .londonplugin it describes. Both must be https -- a plaintext update feed is
/// somebody else's choice of what you install.
public sealed record PluginUpdateSource(string VersionUrl, string PackageUrl)
{
    public static PluginUpdateSource? From(string? versionUrl, string? packageUrl)
        => Https(versionUrl) && Https(packageUrl)
               ? new PluginUpdateSource(versionUrl!, packageUrl!)
               : null;

    private static bool Https(string? u)
        => !string.IsNullOrWhiteSpace(u)
           && Uri.TryCreate(u, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps;

    /// Shown in the consent dialog: the host, not the whole URL.
    public string Host
        => Uri.TryCreate(VersionUrl, UriKind.Absolute, out var u) ? u.Host : VersionUrl;
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
    bool     RulesAcknowledged,
    // Optional. Null means this plugin never checks for its own updates --
    // which stays the default, because a check is a network call to a
    // third-party host and that is the plugin author's choice to declare.
    PluginUpdateSource? Update = null,
    // Optional. The oldest launcher this build can actually run on. A plugin
    // references the exact launcher assembly it was compiled against, so one
    // built for a newer launcher fails to load with a raw .NET binding error
    // that means nothing to a player. This field turns that into a sentence.
    System.Version? MinLauncher = null)
{
    public const string FileName = "plugin.json";

    /// The API revision this launcher can load. Bump when IGamePlugin changes.
    ///
    /// 2: OnLocationTable and OnLocationHints, for games that report checks by
    ///    name instead of id. Both have default implementations, so a plugin
    ///    built for 1 still compiles -- but it would load against a launcher
    ///    that has moved on, so the number moves too.
    ///
    public const int CurrentApiVersion = 2;

    // A game id becomes a folder name and a registry key. Anything outside this
    // set is either a path escape or a collision waiting to happen.
    private static readonly Regex GameIdShape = new(@"^[a-z0-9][a-z0-9_]{1,63}$", RegexOptions.Compiled);

    ///
    /// Parse and validate. Returns null and fills error on
    /// anything wrong — a bad manifest is a normal event, not an exception.
    ///
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

        // An update block that is present but malformed becomes null rather
        // than an error: the plugin is still perfectly usable, it just will not
        // offer updates. Refusing to install it over a bad URL would punish the
        // player for the author's typo.
        PluginUpdateSource? update = null;
        if (root.TryGetProperty("update", out var u) && u.ValueKind == JsonValueKind.Object)
            update = PluginUpdateSource.From(Str(u, "versionUrl"), Str(u, "packageUrl"));

        // System.Version, spelled out: the record's own `Version` property
        // shadows the type inside this class.
        System.Version? minLauncher = null;
        if (System.Version.TryParse(Str(root, "minLauncher") ?? "", out var ml))
        {
            minLauncher = ml;
            // Caught here rather than at load time. The .NET binding failure
            // this prevents reads "Could not load file or assembly 'Multiworld
            // Launcher, Version=3.3.0.0'", which tells a player nothing about
            // what to do next.
            if (ml > LauncherUpdater.CurrentVersion)
            {
                error = $"needs Multiworld Launcher {Short(ml)} or newer; this one is "
                      + $"{Short(LauncherUpdater.CurrentVersion)}. Update the launcher "
                      + "first, then add the plugin again.";
                return null;
            }
        }

        return new PluginManifest(
            api, gameId, display,
            Str(root, "subtitle") ?? "",
            Str(root, "version") ?? "0.0.0",
            author,
            Str(root, "authorContact"),
            assembly, entry, declares, true,
            update, minLauncher);
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

    /// Three fields at most, and never more than the version actually has.
    /// The assembly carries a fourth that is always 0, and ToString(3) THROWS
    /// on a version written as "3.3" -- which a plugin author will write.
    private static string Short(System.Version v)
        => v.ToString(v.Build >= 0 ? 3 : v.Minor >= 0 ? 2 : 1);
}
