using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LauncherV2.Core.Plugins;

// A .londonplugin file, opened without running anything inside it.
//
// The order matters and is the whole security model:
//
//   1. hash the file          — identity of exactly these bytes
//   2. read plugin.json       — who it says it is
//   3. ask the player         — with 1 and 2 on screen
//   4. extract                — only after a yes
//   5. load the assembly      — only after extraction
//
// Steps 1 and 2 read the archive; they never write to disk and never execute.
// A package the player declines leaves nothing behind.

/// <summary>A package inspected but not yet installed.</summary>
public sealed record PluginCandidate(
    string          SourcePath,
    string          Sha256,
    PluginManifest? Manifest,
    string?         Error)
{
    public bool IsUsable => Manifest != null && Error == null;

    /// <summary>Short hash for the consent dialog — nobody reads 64 hex digits.</summary>
    public string ShortHash => Sha256.Length >= 16 ? Sha256[..16] + "…" : Sha256;
}

public static class PluginPackage
{
    public const string Extension = ".londonplugin";

    /// <summary>Where installed plugins live, beside the launcher.</summary>
    /// <remarks>
    /// Deliberately not "Plugins" — an installed launcher already has a Plugins
    /// folder holding Scripts (BizHawk connector and friends). Two different
    /// things called the same would get merged by somebody eventually.
    /// </remarks>
    public static string RootDirectory =>
        Path.Combine(AppContext.BaseDirectory, "GamePlugins");

    public static string DirectoryFor(string gameId)
        => Path.Combine(RootDirectory, gameId);

    /// <summary>
    /// Look inside a package. Reads the manifest and hashes the file; writes
    /// nothing, runs nothing. Never throws.
    /// </summary>
    public static PluginCandidate Inspect(string path)
    {
        string hash;
        try { hash = HashFile(path); }
        catch (Exception ex) { return new PluginCandidate(path, "", null, "could not read the file: " + ex.Message); }

        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(PluginManifest.FileName);
            if (entry == null)
                return new PluginCandidate(path, hash, null,
                    $"no {PluginManifest.FileName} at the top of the package — is this a {Extension} file?");

            // A manifest is a few hundred bytes. A multi-megabyte one is either
            // broken or an attempt to make us allocate.
            if (entry.Length > 256 * 1024)
                return new PluginCandidate(path, hash, null, "plugin.json is implausibly large");

            using var s = entry.Open();
            using var r = new StreamReader(s, Encoding.UTF8);
            string json = r.ReadToEnd();

            var manifest = PluginManifest.Parse(json, out string err);
            return manifest == null
                ? new PluginCandidate(path, hash, null, err)
                : new PluginCandidate(path, hash, manifest, null);
        }
        catch (InvalidDataException)
        {
            return new PluginCandidate(path, hash, null, "not a valid archive");
        }
        catch (Exception ex)
        {
            return new PluginCandidate(path, hash, null, ex.Message);
        }
    }

    /// <summary>
    /// Unpack an approved package into its own folder, replacing whatever was
    /// there. Returns null on success, otherwise the reason.
    /// </summary>
    public static string? Install(PluginCandidate candidate)
    {
        if (!candidate.IsUsable) return candidate.Error ?? "package is not usable";
        string dest = DirectoryFor(candidate.Manifest!.GameId);

        try
        {
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.CreateDirectory(dest);

            using var zip = ZipFile.OpenRead(candidate.SourcePath);
            string root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;

            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;   // directory entry

                // Zip entries carry whatever path the archive author wrote,
                // including "..\..\Windows\System32\". Resolve first, then
                // refuse anything that landed outside our folder.
                string target = Path.GetFullPath(Path.Combine(dest, e.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return $"package tries to write outside its folder: {e.FullName}";

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, overwrite: true);
            }

            string dll = Path.Combine(dest, candidate.Manifest.Assembly);
            if (!File.Exists(dll))
                return $"the package does not contain {candidate.Manifest.Assembly}";

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Every installed plugin folder that has a readable manifest.</summary>
    public static IReadOnlyList<(string Directory, PluginManifest Manifest)> Installed()
    {
        var found = new List<(string, PluginManifest)>();
        if (!Directory.Exists(RootDirectory)) return found;

        foreach (string dir in Directory.EnumerateDirectories(RootDirectory))
        {
            string mf = Path.Combine(dir, PluginManifest.FileName);
            if (!File.Exists(mf)) continue;
            try
            {
                var m = PluginManifest.Parse(File.ReadAllText(mf), out _);
                if (m != null) found.Add((dir, m));
            }
            catch { /* an unreadable folder is skipped, not fatal */ }
        }
        return found;
    }

    /// <summary>SHA-256 of a file, lowercase hex.</summary>
    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// SHA-256 over an installed folder's contents, so a plugin that is edited
    /// on disk after approval stops matching what was approved.
    /// </summary>
    public static string HashDirectory(string dir)
    {
        using var sha = SHA256.Create();
        var files = new List<string>(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
        files.Sort(StringComparer.OrdinalIgnoreCase);   // stable order or the hash is meaningless

        foreach (string f in files)
        {
            byte[] rel = Encoding.UTF8.GetBytes(Path.GetRelativePath(dir, f).ToLowerInvariant());
            sha.TransformBlock(rel, 0, rel.Length, null, 0);
            byte[] body = File.ReadAllBytes(f);
            sha.TransformBlock(body, 0, body.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
