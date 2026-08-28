using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LauncherV2.Core.Archipelago;

/// What kind of control an option wants.
public enum ApOptionKind
{
    /// A fixed set of named values.
    Choice,
    /// A Choice with exactly two values that read as on/off.
    Toggle,
    /// A number between Min and Max, sometimes with named landmarks.
    Range,
    /// A list of item or location names. Empty in the template: `[]`.
    ItemList,
    /// A mapping, e.g. start_inventory. Empty in the template: `{}`.
    ItemDict,
    /// Text the player types. The template shows it as a single empty value
    /// carrying the weight -- `'': 50` -- which means "nothing set yet",
    /// not "one thing to choose from".
    FreeText,
}

/// One selectable value. <paramref name="Equivalent"/> is set for the named
/// landmarks on a range ("normal # equivalent to 50").
public sealed record ApChoice(string Value, string? Description, long? Equivalent, int Weight = 0);

/// One option, as a form control could render it.
public sealed record ApOption(
    string Key,
    string Description,
    ApOptionKind Kind,
    IReadOnlyList<ApChoice> Choices,
    long? Min,
    long? Max,
    string? Default,
    string Group)
{
    /// Options whose value London always owns, so the form never shows them.
    public bool IsPlumbing => Key is "progression_balancing" or "accessibility";
}

/// A whole game's options, read from its template.
public sealed record ApTemplate(
    string Game,
    string? RequiresEngineVersion,
    string? RequiresGameVersion,
    IReadOnlyList<ApOption> Options)
{
    public IEnumerable<string> Groups => Options.Select(o => o.Group).Distinct();
}

// ApOptionTemplate — reading Archipelago's own option templates so London can
// draw a form for any game without anyone hand-writing one.
//
// WHY THIS IS PARSED BY HAND AND NOT WITH A YAML LIBRARY
// Everything that makes the form usable lives in the comments: the option's
// description, its minimum and maximum, and what the named landmarks on a
// range actually mean ("extreme # equivalent to 99"). A YAML parser throws all
// of that away and hands back a dictionary of weights. So this reads lines.
//
// The file is a weight map: every allowed value is listed with a number, and
// the default is the one carrying the weight. That is also the format the
// generator wants back, which is the happy part -- London edits weights and
// writes the file out again. Nothing is translated.
//
// Nothing here executes any of the game's code. A template is text.
public static class ApOptionTemplate
{
    private static readonly Regex GameLine     = new(@"^game:\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex VersionLine  = new(@"^\s+version:\s*([0-9.]+)", RegexOptions.Compiled);
    private static readonly Regex OptionLine   = new(@"^  ([A-Za-z_][A-Za-z0-9_]*):\s*$", RegexOptions.Compiled);
    private static readonly Regex ValueLine    = new(@"^    (?:'([^']*)'|""([^""]*)""|([^:#]+?)):\s*(-?\d+)\s*(?:#\s*(.*))?$", RegexOptions.Compiled);
    /// A key whose value is not a weight -- e.g. `'1': B01810`. The option is
    /// then a mapping the player fills in, not a set of choices.
    private static readonly Regex TextEntryLine = new(@"^    (?:'([^']*)'|""([^""]*)""|([^:#]+?)):\s*(\S.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex BannerLine   = new(@"^  #\s*(.+?)\s*#\s*$", RegexOptions.Compiled);
    private static readonly Regex MinLine      = new(@"Minimum value is (-?\d+)", RegexOptions.Compiled);
    private static readonly Regex MaxLine      = new(@"Maximum value is (-?\d+)", RegexOptions.Compiled);
    private static readonly Regex EquivLine    = new(@"equivalent to (-?\d+)", RegexOptions.Compiled);

    /// Values the generator understands but a person should not be shown as
    /// if they were ordinary choices -- they mean "roll it for me".
    public static bool IsRandomDirective(string value)
        => value.Equals("random", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("random-", StringComparison.OrdinalIgnoreCase);

    public static ApTemplate? ParseFile(string path)
    {
        try { return Parse(File.ReadAllLines(path)); }
        catch { return null; }
    }

    public static ApTemplate? Parse(IEnumerable<string> rawLines)
    {
        // The files are UTF-8 with a BOM and CRLF endings; both are stripped
        // here rather than at every comparison below.
        var lines = rawLines.Select(l => l.TrimEnd('\r').TrimStart('﻿')).ToList();

        string? game = null;
        string? engineVersion = null, gameVersion = null;
        var options = new List<ApOption>();

        // Header: everything before the game's own section.
        int i = 0;
        for (; i < lines.Count; i++)
        {
            var m = GameLine.Match(lines[i]);
            if (m.Success && game == null) { game = m.Groups[1].Value.Trim(); continue; }

            if (game != null && lines[i].StartsWith("requires:", StringComparison.Ordinal))
            {
                // requires:
                //   version: 0.6.7
                //   game:
                //     Pokemon Crystal: 5.3.9
                for (int j = i + 1; j < lines.Count && lines[j].StartsWith("  ", StringComparison.Ordinal); j++)
                {
                    var v = VersionLine.Match(lines[j]);
                    if (v.Success && engineVersion == null) { engineVersion = v.Groups[1].Value; continue; }
                    int colon = lines[j].LastIndexOf(':');
                    if (colon > 0 && lines[j].StartsWith("    ", StringComparison.Ordinal))
                    {
                        string tail = lines[j][(colon + 1)..].Trim();
                        if (tail.Length > 0 && char.IsDigit(tail[0])) gameVersion = tail;
                    }
                }
            }

            // The game's own top-level key opens the options body.
            if (game != null && lines[i].StartsWith(game + ":", StringComparison.Ordinal)) { i++; break; }
        }

        if (game == null) return null;

        string group = "Options";
        string? key = null;
        var comments = new List<string>();
        var choices = new List<ApChoice>();
        long? min = null, max = null;
        string? inlineLiteral = null;
        int textEntries = 0;
        int bestWeight = int.MinValue;
        string? best = null;

        void Flush()
        {
            if (key == null) return;

            string description = string.Join("\n", comments).Trim();
            ApOptionKind kind;

            // An inline literal is the option's whole value: `[]` when empty,
            // but also `['Upgrade', 'Hidden']` when the world ships defaults.
            if (inlineLiteral != null && inlineLiteral.StartsWith("[", StringComparison.Ordinal))
                kind = ApOptionKind.ItemList;
            else if (inlineLiteral != null && inlineLiteral.StartsWith("{", StringComparison.Ordinal))
                kind = ApOptionKind.ItemDict;
            // Keys with text values rather than weights: a mapping to fill in.
            else if (choices.Count == 0 && textEntries > 0) kind = ApOptionKind.ItemDict;
            else if (min != null || max != null) kind = ApOptionKind.Range;
            // A lone empty value is a blank the player fills in. A lone
            // non-empty one (Hollow Knight's only start location) really is a
            // choice with one legal answer, and stays a choice.
            else if (choices.Count == 1 && choices[0].Value.Length == 0)
                kind = ApOptionKind.FreeText;
            // A count table looks EXACTLY like a weight map -- "20 Rupees: 53"
            // parses the same as "champion: 50". The tell is the template
            // convention itself: a generated choice marks the default with 50
            // and every alternative with 0, so exactly one entry carries
            // weight. Several nonzero entries mean the numbers are the VALUE
            // (how many of each), and flattening them to one string is how
            // Minish Cap's filler distribution became an un-generatable seed.
            //
            // Counted across ALL entries: ALttP's medallions put their 50 on
            // `random` itself, and skipping the random directives here turned
            // a perfectly ordinary choice into an all-zero "count table".
            else if (choices.Count(c => c.Weight != 0) != 1)
                kind = ApOptionKind.ItemDict;
            else
            {
                var real = choices.Where(c => !IsRandomDirective(c.Value)).ToList();
                kind = real.Count == 2 && real.All(c => LooksBoolean(c.Value))
                    ? ApOptionKind.Toggle
                    : ApOptionKind.Choice;
            }

            string? deflt = best;
            if (kind is ApOptionKind.ItemList or ApOptionKind.ItemDict)
                deflt = inlineLiteral
                    ?? (choices.Count > 0 ? RenderMapping(choices)
                        : kind == ApOptionKind.ItemList ? "[]" : "{}");

            options.Add(new ApOption(key, description, kind,
                                     choices.ToList(), min, max, deflt, group));

            key = null; comments.Clear(); choices.Clear();
            min = max = null; inlineLiteral = null; textEntries = 0;
            bestWeight = int.MinValue; best = null;
        }

        for (; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0) continue;

            // Section banner: "  # Game Options #" between two rows of hashes.
            var banner = BannerLine.Match(line);
            if (banner.Success && key == null)
            {
                string g = banner.Groups[1].Value.Trim();
                if (g.Length > 0 && !g.All(c => c == '#')) group = g;
                continue;
            }
            if (line.TrimStart().StartsWith("###", StringComparison.Ordinal)) continue;

            var opt = OptionLine.Match(line);
            if (opt.Success)
            {
                Flush();
                key = opt.Groups[1].Value;
                continue;
            }

            if (key == null) continue;

            string body = line.Trim();

            if (body.StartsWith("#", StringComparison.Ordinal))
            {
                string text = body.TrimStart('#').Trim();
                var mn = MinLine.Match(text); if (mn.Success) min = long.Parse(mn.Groups[1].Value, CultureInfo.InvariantCulture);
                var mx = MaxLine.Match(text); if (mx.Success) max = long.Parse(mx.Groups[1].Value, CultureInfo.InvariantCulture);
                if (text.Length > 0) comments.Add(text);
                continue;
            }

            if ((body.StartsWith("[", StringComparison.Ordinal) && body.EndsWith("]", StringComparison.Ordinal))
                || (body.StartsWith("{", StringComparison.Ordinal) && body.EndsWith("}", StringComparison.Ordinal)))
            {
                inlineLiteral = body;
                continue;
            }

            var val = ValueLine.Match(line);
            if (!val.Success)
            {
                // Not a weight. If it is still `key: something`, the option is
                // a mapping (hex colours, names) rather than a set of choices.
                if (TextEntryLine.IsMatch(line)) textEntries++;
                continue;
            }
            {
                // Quoted or bare: whichever group matched is the value.
                string value = (val.Groups[1].Success ? val.Groups[1].Value
                              : val.Groups[2].Success ? val.Groups[2].Value
                              : val.Groups[3].Value).Trim();
                int weight = int.Parse(val.Groups[4].Value, CultureInfo.InvariantCulture);
                string? note = val.Groups[5].Success ? val.Groups[5].Value.Trim() : null;

                long? equiv = null;
                if (note != null)
                {
                    var e = EquivLine.Match(note);
                    if (e.Success) equiv = long.Parse(e.Groups[1].Value, CultureInfo.InvariantCulture);
                }

                choices.Add(new ApChoice(value, note, equiv, weight));

                // The default is whatever the template weighted highest. The
                // convention is a single 50, but reading the weights means an
                // unusual template still yields the value it would actually roll.
                if (weight > bestWeight) { bestWeight = weight; best = value; }
            }
        }
        Flush();

        return new ApTemplate(game, engineVersion, gameVersion, options);
    }

    /// A count table, rendered back as the inline mapping the generator
    /// expects: {'20 Rupees': 53, '1 Rupee': 36}. The numbers travel as
    /// numbers; only the keys are quoted.
    private static string RenderMapping(IEnumerable<ApChoice> entries)
        => "{" + string.Join(", ",
               entries.Where(c => !IsRandomDirective(c.Value))
                      .Select(c => $"'{c.Value.Replace("'", "''")}': {c.Weight}"))
             + "}";

    private static bool LooksBoolean(string v)
        => v is "true" or "false" or "on" or "off" or "yes" or "no";
}
