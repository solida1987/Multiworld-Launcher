using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LauncherV2.Core.Archipelago;

// ApYamlFlow — the item lists and item maps a player types, as YAML.
//
// A fifth of every world's options are lists of item or location names, and
// another slice are maps of name to count. The Create YAML form lets people
// edit those as one entry per line, which means something has to turn
//
//     Bow, Silver Arrows
//     Kirby's Dream Land
//
// into a flow literal the generator accepts, and back again for editing.
// That is fiddly enough to be worth doing in one place with a test around it
// rather than inline in a dialog:
//
//   * item names contain COMMAS ("Bow, Silver Arrows"), so lines are the
//     separator and commas are not;
//   * item names contain APOSTROPHES ("Kirby's Dream Land"), so quoting is
//     YAML's own doubling and not a backslash;
//   * an empty list must be `[]` and an empty map `{}` -- never a bare key,
//     which parses to null and is a crash rather than a default.
public static class ApYamlFlow
{
    /// Lines to a flow list: `['A', 'B']`. Empty input gives `[]`.
    public static string LinesToList(string text)
    {
        var lines = SplitLines(text);
        return lines.Count == 0
            ? "[]"
            : "[" + string.Join(", ", lines.Select(Quote)) + "]";
    }

    /// Lines to a flow map: `{'A': 2}`. A line with no count means one.
    /// Empty input gives `{}`.
    public static string LinesToMap(string text)
    {
        var pairs = new List<string>();
        foreach (string line in SplitLines(text))
        {
            // LAST colon, not the first: "Zelda: A Link to the Past: 2" names
            // an item containing a colon, and splitting at the first would
            // make the count "A Link to the Past: 2".
            int colon = line.LastIndexOf(':');
            string key = colon < 0 ? line : line[..colon].Trim();
            string val = colon < 0 ? "1"  : line[(colon + 1)..].Trim();

            if (key.Length == 0) continue;
            // A non-numeric tail was part of the NAME, not a count -- so the
            // whole line is the name and the count is one.
            if (val.Length == 0 || !long.TryParse(val, out _))
            {
                key = line.Trim();
                val = "1";
            }
            pairs.Add($"{Quote(key)}: {val}");
        }
        return pairs.Count == 0 ? "{}" : "{" + string.Join(", ", pairs) + "}";
    }

    /// A flow list or map back to one entry per line, so a value already in
    /// the template can be edited instead of retyped.
    public static string ToLines(string flow)
    {
        string s = (flow ?? "").Trim();
        if (s.Length < 2) return "";

        bool isMap = s[0] == '{' && s[^1] == '}';
        if ((s[0] == '[' && s[^1] == ']') || isMap) s = s[1..^1];
        s = s.Trim();
        if (s.Length == 0) return "";

        var parts = new List<string>();
        var buf = new StringBuilder();
        bool inQuote = false;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'')
            {
                // '' inside a quoted scalar is one literal apostrophe.
                if (inQuote && i + 1 < s.Length && s[i + 1] == '\'')
                {
                    buf.Append('\'');
                    i++;
                    continue;
                }
                inQuote = !inQuote;
                continue;
            }
            // Only a separator OUTSIDE quotes separates entries.
            if (c == ',' && !inQuote)
            {
                parts.Add(buf.ToString().Trim());
                buf.Clear();
                continue;
            }
            buf.Append(c);
        }
        if (buf.Length > 0) parts.Add(buf.ToString().Trim());

        return string.Join('\n', parts.Where(p => p.Length > 0));
    }

    private static List<string> SplitLines(string text)
        => (text ?? "").Split('\n')
                       .Select(l => l.Trim().TrimEnd('\r'))
                       .Where(l => l.Length > 0)
                       .ToList();

    /// Single-quoted with inner quotes doubled — YAML's own escaping, and the
    /// only form that survives an apostrophe in an item name.
    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
}
