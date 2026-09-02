using System;
using System.IO;

namespace LauncherV2.Core.Archipelago;

///
/// Where Archipelago is, as one answer everybody asks the same way.
///
/// London does not ship Archipelago; it drives the copy the player already
/// has. Several things need to know where that is — updating the worlds,
/// rewriting the YAML forms, generating a seed — and each of them used to work
/// it out for itself. That is fine while the guess is right, and useless the
/// moment it is not: a player whose install is somewhere unusual got a button
/// that quietly did nothing and a message telling them to set it up "under
/// Multiworld", which is not where anything can be set.
///
/// So: one place answers the question, one place says plainly what the answer
/// is, and one place asks when there is no answer.
///
public static class ApEngineLocation
{
    /// How the current answer was arrived at — the difference matters to the
    /// player, because a guess can be wrong and a choice cannot.
    public enum Source { None, Chosen, Found }

    public sealed record Where(ApEngine.Report? Report, Source How)
    {
        public bool Usable => Report is { Usable: true };

        /// The folder itself, or null when there is nothing to point at.
        public string? Path => Report?.Root;
    }

    /// Resolve it: the folder the player chose if they chose one, otherwise
    /// whatever discovery can find.
    public static Where Current()
    {
        try
        {
            var s = SettingsStore.Load();
            string chosen = (s.ApEnginePath ?? string.Empty).Trim();
            if (chosen.Length > 0)
                return new Where(ApEngine.Inspect(chosen), Source.Chosen);

            var found = ApEngine.Discover();
            return new Where(found, found == null ? Source.None : Source.Found);
        }
        catch (Exception)
        {
            return new Where(null, Source.None);
        }
    }

    /// Remember a folder the player pointed at. Empty clears the choice and
    /// puts London back on discovery.
    public static void Choose(string? path)
    {
        var s = SettingsStore.Load();
        s.ApEnginePath = (path ?? string.Empty).Trim();
        SettingsStore.Save(s);
    }

    ///
    /// One line saying exactly where London is looking and whether that is
    /// going to work — the thing a player needs when a button does nothing.
    ///
    public static string Describe(Where w)
    {
        if (w.Report is not { } r || !r.Exists)
            return "London could not find an Archipelago installation. "
                 + "Point it at the folder that holds ArchipelagoGenerate.exe.";

        string how = w.How == Source.Chosen ? "You chose this folder." : "Found automatically.";
        if (!r.Usable)
            return $"{how} It cannot be used: {string.Join("; ", r.Problems)}";

        string v = r.Version?.ToString() ?? "unknown version";
        string worlds = r.CustomWorlds.Count == 0
            ? "no extra worlds"
            : $"{r.CustomWorlds.Count} extra world(s)"
              + (r.BrokenWorldCount > 0 ? $", {r.BrokenWorldCount} of them broken" : "");
        return $"{how} Archipelago {v} — {worlds}.";
    }

    /// The two folders inside it that London actually writes to, spelled out
    /// so a player can go and look at them.
    public static string DescribeFolders(Where w)
    {
        if (w.Report is not { Exists: true } r) return string.Empty;
        return $"Worlds: {r.CustomWorldsDir}\nYAML forms: {r.TemplatesDir}";
    }

    /// True when a folder the player picks is one London can actually drive.
    public static ApEngine.Report Check(string path) => ApEngine.Inspect(path);

    /// A sensible place to open a folder picker at.
    public static string? StartingPointForBrowse(Where w)
    {
        try
        {
            if (w.Path is { Length: > 0 } p && Directory.Exists(p)) return p;
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string guess = System.IO.Path.Combine(pd, "Archipelago");
            return Directory.Exists(guess) ? guess : (Directory.Exists(pd) ? pd : null);
        }
        catch (Exception) { return null; }
    }
}
