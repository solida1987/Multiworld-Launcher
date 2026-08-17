using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LauncherV2.Core.Extensions;

/// A .londonextension the player picked, looked at but not yet installed.
public sealed record ExtensionCandidate(
    string            SourcePath,
    string            Sha256,
    ExtensionManifest? Manifest,
    string?           Error)
{
    public bool IsUsable => Manifest != null && Error == null;

    public string ShortHash => Sha256.Length >= 16 ? Sha256[..16] + "…" : Sha256;
}

/// Installing an emulator bridge, mirroring PluginPackage deliberately.
///
/// An extension runs in the launcher's process exactly as a plugin does, so it
/// gets the same treatment: look inside without running anything, show the
/// player what it declares, and only then unpack it.
public static class ExtensionPackage
{
    public const string Extension = ".londonextension";

    /// Where installed bridges live. BridgeRegistry reads the same folder.
    public static string RootDirectory => BridgeRegistry.Directory;

    public static string DirectoryFor(string extensionId)
        => Path.Combine(RootDirectory, extensionId);

    /// Look inside a package. Reads the manifest and hashes the file; writes
    /// nothing, runs nothing. Never throws.
    public static ExtensionCandidate Inspect(string path)
    {
        string hash;
        try { hash = HashFile(path); }
        catch (Exception ex)
        {
            return new ExtensionCandidate(path, "", null,
                "could not read the file: " + ex.Message);
        }

        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(ExtensionManifest.FileName);
            if (entry == null)
                return new ExtensionCandidate(path, hash, null,
                    $"no {ExtensionManifest.FileName} at the top of the package "
                  + $"— is this a {Extension} file?");

            // A manifest is a few hundred bytes. A multi-megabyte one is either
            // broken or an attempt to make us allocate.
            if (entry.Length > 256 * 1024)
                return new ExtensionCandidate(path, hash, null,
                    "extension.json is implausibly large");

            using var s = entry.Open();
            using var r = new StreamReader(s, Encoding.UTF8);
            var manifest = ExtensionManifest.Parse(r.ReadToEnd(), out string err);

            return manifest == null
                ? new ExtensionCandidate(path, hash, null, err)
                : new ExtensionCandidate(path, hash, manifest, null);
        }
        catch (InvalidDataException)
        {
            return new ExtensionCandidate(path, hash, null, "not a valid archive");
        }
        catch (Exception ex)
        {
            return new ExtensionCandidate(path, hash, null, ex.Message);
        }
    }

    /// Unpack an approved package into its own folder, replacing whatever was
    /// there. Returns null on success, otherwise the reason.
    public static string? Install(ExtensionCandidate candidate)
    {
        if (!candidate.IsUsable) return candidate.Error ?? "package is not usable";
        var m = candidate.Manifest!;

        // Refusing this here as well as in BridgeRegistry is deliberate: telling
        // the player at install time is far better than accepting the file and
        // then silently skipping it at every startup.
        if (BridgeRegistry.BuiltIn.Contains(m.Protocol))
            return $"\"{m.Protocol}\" is built into the launcher and cannot be "
                 + "replaced by an extension";

        string dest = DirectoryFor(m.ExtensionId);
        try
        {
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.CreateDirectory(dest);

            using var zip = ZipFile.OpenRead(candidate.SourcePath);
            string root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;

            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;

                // Zip entries carry whatever path the archive author wrote,
                // including "..\..\Windows\System32\". Resolve first, then
                // refuse anything that landed outside our folder.
                string target = Path.GetFullPath(Path.Combine(dest, e.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return $"package tries to write outside its folder: {e.FullName}";

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, overwrite: true);
            }

            string dll = Path.Combine(dest, m.Assembly);
            if (!File.Exists(dll))
                return $"the package does not contain {m.Assembly}";

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// What the consent dialog shows. An extension cannot claim less than this:
    /// every one of them runs in our process and drives an external program.
    public static IReadOnlyList<string> Describe(ExtensionManifest m) => new[]
    {
        $"speaks the \"{m.Protocol}\" protocol for games that need it",
        "runs inside the launcher with the same rights as the launcher",
        "starts a program you installed yourself, from Emulators\\",
        "never downloads an emulator — you provide it",
    };

    public static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
