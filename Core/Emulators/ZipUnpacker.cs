using System;
using System.IO;
using System.IO.Compression;

namespace LauncherV2.Core.Emulators;

/// Unpacking a release archive into a folder.
///
/// ⚠ Its own file, and free of every dependency the installer has, so a proof
/// can feed it a REAL release zip. The bug that put it here only appears in
/// somebody else's archive, and arguing about it is no substitute for running
/// Azahar's own file through the code that failed.
internal static class ZipUnpacker
{

/// Unpack a zip into a folder. Returns null on success, or the sentence
/// to show the player.
///
/// ⚠ Split out from InstallAsync so it can be run against a REAL release
/// archive without a download: the separator bug below only appears in
/// somebody else's zip, and arguing about it is no substitute for feeding
/// Azahar's own file through it.
public static string? Extract(byte[] bytes, string dest,
                                   string? rootInsideArchive)
{
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                // ⚠⚠ Normalise BEFORE deciding what is a directory.
                //
                // Azahar's release zip stores EVERY path with backslashes --
                // all 31 entries, its own folder entry included. A test for a
                // trailing "/" therefore saw a FILE, and ExtractToFile on a
                // path that ends in a separator throws
                // DirectoryNotFoundException. The 3DS install failed every
                // time, and the message named a folder rather than the entry,
                // so it read like a broken download rather than our bug.
                //
                // Two signals, because either alone has been wrong here: the
                // trailing separator, and an entry whose Name is empty --
                // which is what a directory entry is.
                string rel = entry.FullName.Replace('\\', '/');
            if (rel.EndsWith('/') || entry.Name.Length == 0) continue;

                if (rootInsideArchive is string root)
                {
                    string prefix = root.TrimEnd('/') + "/";
                    if (!rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    rel = rel[prefix.Length..];
                }

                // An archive entry is untrusted input: "../" in a name would
                // write outside the folder the player agreed to.
                string target = Path.GetFullPath(Path.Combine(dest, rel));
                if (!target.StartsWith(Path.GetFullPath(dest) + Path.DirectorySeparatorChar,
                                       StringComparison.OrdinalIgnoreCase))
                    return "The archive tried to write outside the emulator "
                         + "folder. Nothing was installed.";

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
    return null;
}
}
