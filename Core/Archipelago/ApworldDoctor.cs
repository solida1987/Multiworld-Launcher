using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace LauncherV2.Core.Archipelago;

///
/// Problems London can SEE in somebody else's AP world — and offer to fix.
///
/// ⚠⚠ These worlds are not ours. They are other people's work, sitting in the
/// player's own Archipelago install, and it is not London's place to quietly
/// rewrite them because we happen to disagree with a file name. So nothing
/// here changes anything on its own: it looks, it says plainly what is wrong
/// and what it would do about it, and the player decides.
///
/// Two rules that make an offer honest rather than a nag:
///
///   • The answer is remembered against the EXACT FILE — its name, size and
///     timestamp. A world the player later updates themselves is a different
///     file, so London looks again instead of either re-applying an old fix
///     or staying quiet about a problem that came back.
///   • A fix is applied to the version it was offered for and no other. If the
///     file moved between the offer and the yes, the offer is void.
///
public static class ApworldDoctor
{
    /// One thing wrong with one world, described so a player can decide.
    public sealed record Issue(
        string FilePath,
        string FileName,
        string Identity,
        string Kind,
        string Problem,
        string Consequence,
        string Fix,
        string? ProposedName);

    /// Name + size + timestamp. Cheap, and it changes the moment the player
    /// installs a different version — which is exactly when a stored answer
    /// should stop counting.
    public static string IdentityOf(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return $"{fi.Name}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception) { return Path.GetFileName(path); }
    }

    /// A stem Python can import: letters, digits and underscores, not starting
    /// with a digit. ⚠ The dot is the one that breaks a module path — dashes
    /// are fine, which is why "cyberpunk2077-0.7.1" fails and "mega-man" does
    /// not.
    private static bool Importable(string stem) =>
        stem.Length > 0 && !char.IsDigit(stem[0])
        && stem.All(c => char.IsLetterOrDigit(c) || c == '_');

    ///
    /// Look at every world in the folder and report what is wrong. Reads
    /// nothing but file names until something looks wrong, so a healthy
    /// folder of 500 worlds costs a directory listing.
    ///
    public static IReadOnlyList<Issue> Scan(string? customWorldsDir)
    {
        var found = new List<Issue>();
        if (string.IsNullOrWhiteSpace(customWorldsDir) || !Directory.Exists(customWorldsDir))
            return found;

        string[] files;
        try { files = Directory.GetFiles(customWorldsDir, "*.apworld"); }
        catch (Exception) { return found; }

        foreach (string path in files)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (Importable(stem)) continue;

            string? module = ModuleNameInside(path);
            if (module is not { Length: > 0 } || !Importable(module)) continue;

            found.Add(new Issue(
                FilePath: path,
                FileName: Path.GetFileName(path),
                Identity: IdentityOf(path),
                Kind: "unimportable-name",
                Problem: $"Its file name is \"{stem}\", and Archipelago turns a world's "
                       + $"file name into a Python module path. The dot ends the name "
                       + $"early, so it looks for \"worlds.{stem.Split('.')[0]}\" and "
                       + "finds nothing.",
                Consequence: "Archipelago's server refuses to start at all while this "
                           + "file is there — for every game, not just this one.",
                Fix: $"Rename it to \"{module}.apworld\", which is the name the world "
                   + "calls itself inside the file. Nothing about the world changes.",
                ProposedName: module + ".apworld"));
        }
        return found;
    }

    public sealed record Applied(bool Ok, string Note);

    ///
    /// Do what the offer described — and only to the file it was offered for.
    ///
    /// The identity is checked again first. Between the offer and the answer
    /// the player may have updated that world themselves, and their newer copy
    /// is theirs to keep: an offer about a file that no longer exists is not
    /// an offer about the file that took its place.
    ///
    public static Applied Apply(Issue issue)
    {
        if (!File.Exists(issue.FilePath))
            return new Applied(false, $"{issue.FileName} is no longer there — nothing done.");
        if (!string.Equals(IdentityOf(issue.FilePath), issue.Identity, StringComparison.Ordinal))
            return new Applied(false,
                $"{issue.FileName} has changed since this was offered — left alone.");

        if (issue.Kind != "unimportable-name" || issue.ProposedName is not { Length: > 0 })
            return new Applied(false, $"No fix is implemented for {issue.Kind}.");

        string dir = Path.GetDirectoryName(issue.FilePath)!;
        string dest = Path.Combine(dir, issue.ProposedName);
        try
        {
            if (File.Exists(dest))
            {
                // A correctly named copy is already there, so this file is the
                // same world twice — Archipelago would load it twice. The good
                // one stays; we are not overwriting somebody's working world.
                File.Delete(issue.FilePath);
                return new Applied(true,
                    $"{issue.FileName} removed — {issue.ProposedName} was already there "
                  + "and holds the same world.");
            }
            File.Move(issue.FilePath, dest);
            return new Applied(true, $"{issue.FileName} renamed to {issue.ProposedName}.");
        }
        catch (Exception e)
        {
            return new Applied(false, $"{issue.FileName} could not be renamed: {e.Message}");
        }
    }

    ///
    /// The one file an install is allowed to delete: the copy London itself
    /// wrote earlier, under a name it no longer uses.
    ///
    /// ⚠ Installing a game cleans up the previous copy so Archipelago does not
    /// load the same world twice — and it must stop exactly there. The
    /// question is never "is there a file in the way?" but "did WE put it
    /// there?". `recordedAsset` is London's own bookkeeping of what it wrote,
    /// so a null answer means we have no record of writing anything — and a
    /// file we did not write belongs to the player, whatever it is called and
    /// however broken its name looks. That one gets an offer, not a delete.
    ///
    public static string? OurStaleCopy(string? recordedAsset, string newFileName) =>
        recordedAsset is { Length: > 0 }
        && !string.Equals(recordedAsset, newFileName, StringComparison.OrdinalIgnoreCase)
            ? recordedAsset
            : null;

    /// The single top-level folder inside the zip IS the module name, so the
    /// right name never has to be guessed at.
    private static string? ModuleNameInside(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var tops = zip.Entries
                .Select(e => e.FullName.Replace('\\', '/'))
                .Where(n => n.Contains('/'))
                .Select(n => n[..n.IndexOf('/')])
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToList();
            return tops.Count == 1 ? tops[0] : null;
        }
        catch (Exception) { return null; }
    }
}
