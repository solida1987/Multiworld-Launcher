using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LauncherV2.Core.Extensions;

// extension.json, read and validated before any extension code runs.
// Deliberately the same shape and the same strictness as plugin.json -- an
// extension runs in the launcher's process just as a plugin does, so it gets
// the same scrutiny and the same consent dialog.
public sealed record ExtensionManifest(
    int      ApiVersion,
    string   ExtensionId,
    string   DisplayName,
    string   Protocol,
    string   Version,
    string   Author,
    string?  AuthorContact,
    string   Assembly,
    string   EntryType,
    string   HomepageUrl,
    bool     RulesAcknowledged)
{
    public const string FileName = "extension.json";

    /// Bump when IEmulatorBridge changes.
    public const int CurrentApiVersion = 1;

    private static readonly Regex IdShape =
        new(@"^[a-z0-9][a-z0-9_]{1,63}$", RegexOptions.Compiled);

    // The protocol token is what a game manifest names. Keeping it to the same
    // shape as an id means it can never contain a path separator or turn into
    // something the lookup treats specially.
    private static readonly Regex ProtocolShape =
        new(@"^[a-z0-9][a-z0-9_]{1,31}$", RegexOptions.Compiled);

    public static ExtensionManifest? Parse(string json, out string error)
    {
        error = "";
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            error = "extension.json is not valid JSON: " + ex.Message;
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "extension.json must be an object";
            return null;
        }

        int api = Int(root, "apiVersion");
        if (api != CurrentApiVersion)
        {
            error = api == 0
                ? "extension.json has no apiVersion"
                : $"built for extension API {api}; this launcher speaks "
                + $"{CurrentApiVersion}.";
            return null;
        }

        string id = Str(root, "extensionId") ?? "";
        if (!IdShape.IsMatch(id))
        {
            error = "extensionId must be lowercase letters, digits and "
                  + "underscores (2-64 chars); got " + Quote(id);
            return null;
        }

        string protocol = Str(root, "protocol") ?? "";
        if (!ProtocolShape.IsMatch(protocol))
        {
            error = "protocol must be a short lowercase token such as \"sni\"; "
                  + "got " + Quote(protocol);
            return null;
        }

        string assembly = Str(root, "assembly") ?? "";
        // Used to build a path. A separator would let a package point at a file
        // outside its own folder.
        if (assembly.Length == 0
            || assembly.IndexOfAny(new[] { '/', '\\', ':' }) >= 0
            || assembly.Contains("..")
            || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            error = "assembly must be a plain .dll filename inside the package; "
                  + "got " + Quote(assembly);
            return null;
        }

        string entry = Str(root, "entryType") ?? "";
        if (entry.Length == 0)
        {
            error = "entryType is missing - name the class that implements "
                  + "IEmulatorBridge";
            return null;
        }

        string display = Str(root, "displayName") ?? "";
        if (display.Length == 0) { error = "displayName is missing"; return null; }

        string author = Str(root, "author") ?? "";
        if (author.Length == 0)
        {
            error = "author is missing - the player has to know whose code this is";
            return null;
        }

        // Same device as plugin.json: not a legal instrument, but it removes
        // "I didn't know the rules existed" as an excuse.
        if (!Bool(root, "rulesAcknowledged"))
        {
            error = "rulesAcknowledged is not true - see PLUGIN_API.md";
            return null;
        }

        return new ExtensionManifest(
            api, id, display, protocol,
            Str(root, "version") ?? "0.0.0",
            author, Str(root, "authorContact"),
            assembly, entry,
            Str(root, "homepageUrl") ?? "",
            true);
    }

    private static string? Str(JsonElement o, string k)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString()?.Trim() : null;

    private static int Int(JsonElement o, string k)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out int i) ? i : 0;

    private static bool Bool(JsonElement o, string k)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Quote(string s)
        => s.Length == 0 ? "(empty)" : "\"" + s + "\"";
}
