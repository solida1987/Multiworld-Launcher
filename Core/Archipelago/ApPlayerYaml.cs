using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LauncherV2.Core.Archipelago;

/// One player's slot: who they are, what they play, and the options they chose.
public sealed record ApSlot(
    string Name,
    string Game,
    IReadOnlyDictionary<string, string> Options)
{
    /// Archipelago truncates slot names past 16 characters, and a name that
    /// arrives truncated is a name the player will not recognise in the
    /// tracker. Checked here rather than discovered after generation.
    public const int MaxNameLength = 16;

    public bool IsNameValid
        => !string.IsNullOrWhiteSpace(Name)
        && Name.Length <= MaxNameLength
        && Name.All(c => !char.IsControl(c))
        && Name.Trim() == Name;
}

// ApPlayerYaml — writing the file the generator reads, and refusing the files
// that would quietly change somebody else's game.
//
// PLAIN VALUES, NOT WEIGHTS
// A template is a weight map, and the generator will happily roll dice across
// it. London writes single values instead: the form showed the player exactly
// what they picked, so that is what the file must say. A weighted file would
// mean the seed does not match the screen, and no amount of good UI recovers
// from that.
//
// Weighted files are still legal input -- a player may bring their own -- and
// those are passed through untouched and marked as randomised rather than
// rewritten into something they did not ask for.
public static class ApPlayerYaml
{
    /// Files the generator picks up from a player folder without being asked,
    /// and which silently rewrite everyone's options when present. When a
    /// player imports a folder from elsewhere, these must not come with it.
    public static readonly string[] HijackFiles = { "meta.yaml", "weights.yaml" };

    public static bool IsHijackFile(string path)
        => HijackFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    /// The yaml text for one slot.
    public static string Render(ApSlot slot, string? engineVersion = null)
    {
        var sb = new StringBuilder();
        sb.Append("name: ").Append(Scalar(slot.Name)).Append('\n');
        sb.Append("description: Created in London\n");
        sb.Append("game: ").Append(Scalar(slot.Game)).Append('\n');

        if (!string.IsNullOrWhiteSpace(engineVersion))
            sb.Append("requires:\n  version: ").Append(engineVersion).Append('\n');

        sb.Append('\n').Append(Scalar(slot.Game)).Append(':');

        // A game section with no options must be an explicit empty MAP. Left
        // as a bare "Game:" the line parses to null, and a null where the
        // generator expects a mapping is a crash rather than "use the
        // defaults". This happens for real: the Create YAML dialog renders
        // exactly this whenever the world's option template is not installed.
        if (slot.Options.Count == 0)
        {
            sb.Append(" {}\n");
            return sb.ToString();
        }

        sb.Append('\n');
        foreach (var (key, value) in slot.Options.OrderBy(o => o.Key, StringComparer.Ordinal))
            sb.Append("  ").Append(key).Append(": ").Append(Scalar(value)).Append('\n');

        return sb.ToString();
    }

    /// Writes one slot into a folder London owns. Returns the file written.
    public static string Write(ApSlot slot, string playersDir, string? engineVersion = null)
    {
        if (!slot.IsNameValid)
            throw new ArgumentException(
                $"\"{slot.Name}\" cannot be used as a slot name: it must be 1-"
              + $"{ApSlot.MaxNameLength} characters with no padding.", nameof(slot));

        Directory.CreateDirectory(playersDir);
        string file = Path.Combine(playersDir, SafeFileName(slot.Name) + ".yaml");
        // No BOM: the generator reads plain UTF-8 happily, and a BOM is one
        // more thing to go wrong in a file we generate ourselves.
        File.WriteAllText(file, Render(slot, engineVersion), new UTF8Encoding(false));
        return file;
    }

    /// Copies a folder of player files London did not write, leaving behind
    /// the ones that would rewrite every slot's options.
    public sealed record Import(int Copied, IReadOnlyList<string> Refused);

    public static Import ImportFolder(string sourceDir, string playersDir)
    {
        Directory.CreateDirectory(playersDir);
        var refused = new List<string>();
        int copied = 0;

        // Non-recursive on purpose: the generator itself only reads loose files
        // in the folder's root, so anything deeper would be copied in and never
        // used -- which looks like a slot that vanished.
        foreach (string src in Directory.EnumerateFiles(sourceDir, "*.yaml"))
        {
            if (IsHijackFile(src)) { refused.Add(Path.GetFileName(src)); continue; }
            File.Copy(src, Path.Combine(playersDir, Path.GetFileName(src)), overwrite: true);
            copied++;
        }
        return new Import(copied, refused);
    }

    /// Quotes only when a bare value would change meaning. An unquoted `true`,
    /// `no`, `12` or `1.4` is read as a boolean or a number by every YAML
    /// parser, so anything that looks like one gets quotes when it is meant as
    /// text -- and numbers meant as numbers do not.
    private static string Scalar(string value)
    {
        if (value.Length == 0) return "''";

        // A flow literal is the value, not text that happens to contain
        // brackets. Quoting `[]` turns an empty list into a list holding the
        // string "[]", and the generator then refuses it as an unknown item
        // name -- six seconds in, with a Python traceback.
        if ((value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal))
            || (value.StartsWith("{", StringComparison.Ordinal) && value.EndsWith("}", StringComparison.Ordinal)))
            return value;

        bool needsQuotes =
            value.Any(c => ":#{}[],&*?|<>=!%@`\"'\\".Contains(c) || char.IsControl(c))
            || value != value.Trim()
            || value.StartsWith("-", StringComparison.Ordinal);

        if (!needsQuotes) return value;
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(bad.Contains(c) ? '_' : c);
        return sb.ToString();
    }

    /// Turns a parsed template into a slot carrying every option's default --
    /// the starting point a form edits from.
    public static ApSlot DefaultsFor(ApTemplate template, string playerName)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var o in template.Options)
        {
            if (o.Default == null) continue;
            options[o.Key] = o.Default;
        }
        return new ApSlot(playerName, template.Game, options);
    }
}
