using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// InstallVerifier — is every file the one we installed, byte for byte?
//
// The launch-time check is size-only, on purpose: it has to finish in the
// moment between pressing Play and the game starting. That catches a file
// that is missing or truncated, which is most of what goes wrong.
//
// It does not catch the case a tester asked about: the file is there, it is
// the right length, and its CONTENTS are wrong. A half-written update, a
// disk error, an antivirus that "cleaned" a few bytes — all of them leave a
// file that passes a size check and crashes the game.
//
// The install manifest has carried a SHA256 per file the whole time. This
// reads them. It is slower than a size check because it reads every byte, so
// it is something the player asks for rather than something that happens on
// every launch.
public static class InstallVerifier
{
    /// What the installer wrote down about one file.
    private sealed record ManifestFile(
        [property: JsonPropertyName("path")]   string Path,
        [property: JsonPropertyName("sha256")] string? Sha256,
        [property: JsonPropertyName("size")]   long Size);

    private sealed record Manifest(
        [property: JsonPropertyName("version")]         string? Version,
        [property: JsonPropertyName("version_display")] string? VersionDisplay,
        [property: JsonPropertyName("files")]           List<ManifestFile>? Files);

    public enum Fault
    {
        /// The file is not there at all.
        Missing,
        /// Present, but not the length it was installed at — a truncated
        /// download, almost always.
        WrongSize,
        /// Right length, wrong bytes, in a file that must never change --
        /// an executable or a library. This is the one a size check cannot
        /// see, and the one that explains a crash after a bad update.
        WrongContent,
        /// Right length, wrong bytes, in a file the game itself writes --
        /// a config, a data table the mod patches, a runtime marker. Almost
        /// always normal, so it is reported apart from the damage rather than
        /// counted with it.
        ChangedSinceInstall,
        /// Present but could not be read (locked, permissions).
        Unreadable,
    }

    public sealed record BadFile(string Path, Fault Fault, string Detail);

    /// Files that must be byte-for-byte what we installed. Everything else in
    /// a game folder is fair game for the game to rewrite: d2gl.ini is a
    /// graphics config, Missiles.txt is a table the randomiser patches, and
    /// D2COMMON_LOADED.txt is a marker the game drops at runtime. Calling
    /// those "damaged" is how a verify button trains people to ignore it.
    private static readonly string[] MustNotChange =
        { ".dll", ".exe", ".asi", ".so", ".dylib" };

    private static bool IsExecutable(string path)
        => MustNotChange.Contains(System.IO.Path.GetExtension(path),
                                  StringComparer.OrdinalIgnoreCase);

    /// Files the game creates, rewrites or deletes as it runs. A fresh install
    /// legitimately lacks some of them until the game has started once, so
    /// their ABSENCE is not damage either.
    ///
    /// Kept deliberately narrow -- extensions the game owns, not "anything
    /// that is not an executable". A missing data table IS damage, and
    /// widening this to hide it would make the button worthless.
    private static readonly string[] GameManagedExtensions = { ".ini", ".dat", ".cfg" };

    /// True when replacing this file would destroy something the player owns.
    /// Exposed so the repair path can refuse it outright rather than relying on
    /// the classifier upstream having got it right -- overwriting somebody's
    /// settings is not a mistake worth leaving to one line of logic.
    public static bool IsPlayerOwned(string path) => IsGameManaged(path);

    private static bool IsGameManaged(string path)
        => GameManagedExtensions.Contains(System.IO.Path.GetExtension(path),
                                          StringComparer.OrdinalIgnoreCase)
           || System.IO.Path.GetFileName(path)
                    .EndsWith("_LOADED.txt", StringComparison.OrdinalIgnoreCase);

    public sealed record Result(
        int Checked,
        long BytesRead,
        IReadOnlyList<BadFile> Bad,
        string? ManifestVersion,
        string? CouldNotRun,
        /// Set when the record was written for a DIFFERENT version than the
        /// one installed. Everything below it is then measured against the
        /// wrong yardstick, and saying "5 files are wrong" would be a lie.
        string? StaleRecord = null)
    {
        /// Faults that mean something is actually wrong. A config the game
        /// rewrote is not damage, and counting it as such would make every
        /// healthy install look broken.
        public IReadOnlyList<BadFile> Damage =>
            Bad.Where(b => b.Fault != Fault.ChangedSinceInstall).ToList();

        public IReadOnlyList<BadFile> Changed =>
            Bad.Where(b => b.Fault == Fault.ChangedSinceInstall).ToList();

        public bool Healthy =>
            CouldNotRun == null && StaleRecord == null && Damage.Count == 0;

        /// One line for the log and the toast.
        public string Summary()
        {
            if (CouldNotRun != null) return CouldNotRun;
            if (StaleRecord != null) return StaleRecord;

            string aside = Changed.Count == 0 ? ""
                : $" ({Changed.Count} config or data file(s) have been changed since "
                  + "install, which is normal.)";

            if (Damage.Count == 0)
                return $"All {Checked} files check out — nothing is missing or damaged."
                     + aside;

            var byFault = Damage.GroupBy(b => b.Fault)
                                .Select(g => $"{g.Count()} {Describe(g.Key)}");
            return $"{Damage.Count} of {Checked} files are wrong — "
                 + string.Join(", ", byFault) + "." + aside;
        }

        public static string Describe(Fault f) => f switch
        {
            Fault.Missing      => "missing",
            Fault.WrongSize    => "the wrong size",
            Fault.WrongContent => "damaged (right size, wrong contents)",
            Fault.ChangedSinceInstall => "changed since install (normal for config and data)",
            _                  => "unreadable",
        };
    }

    public static string ManifestPath(string gameDirectory)
        => System.IO.Path.Combine(gameDirectory, "game_manifest.json");

    /// True when this game can be verified at all — i.e. the installer left a
    /// manifest behind. A game without one is not broken; it just cannot be
    /// checked this way, and the button should not pretend otherwise.
    public static bool CanVerify(string? gameDirectory)
        => !string.IsNullOrWhiteSpace(gameDirectory)
           && File.Exists(ManifestPath(gameDirectory!));

    /// Check every file the manifest lists. Reports progress as
    /// (percent, "file 120 of 649").
    ///
    /// Never throws for a file's sake: an unreadable file is a finding, not a
    /// crash. It throws only if cancelled.
    /// <param name="installedVersion">
    /// What the game says it is. When this and the record disagree, the record
    /// is stale and no file comparison from it means anything -- that is
    /// reported instead of a list of "wrong" files that are in fact correct.
    /// </param>
    public static async Task<Result> VerifyAsync(
        string gameDirectory,
        string? installedVersion = null,
        IProgress<(int Pct, string Msg)>? progress = null,
        CancellationToken ct = default)
    {
        string manifestPath = ManifestPath(gameDirectory);
        Manifest? manifest;
        try
        {
            string json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<Manifest>(json);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new Result(0, 0, Array.Empty<BadFile>(), null,
                "The install manifest could not be read, so nothing can be "
                + "checked against it: " + ex.Message);
        }

        var files = manifest?.Files;
        if (files == null || files.Count == 0)
            return new Result(0, 0, Array.Empty<BadFile>(), manifest?.Version,
                "The install manifest lists no files, so there is nothing to check.");

        // Before reading a single byte: is this record even about this build?
        //
        // A tester saw "5 files are the wrong size" for five files that were
        // perfectly correct — his install record was from an earlier version.
        // Comparing against it produced five confident, wrong accusations.
        string? recorded = manifest?.VersionDisplay ?? manifest?.Version;
        if (!string.IsNullOrWhiteSpace(recorded)
            && !string.IsNullOrWhiteSpace(installedVersion)
            && !SameVersion(recorded!, installedVersion!))
        {
            return new Result(files.Count, 0, Array.Empty<BadFile>(), recorded, null,
                $"This install's record was written for {recorded}, but the game "
                + $"reports {installedVersion}. Nothing can be checked against it — "
                + "the record has to be refreshed first. Updating or repairing the "
                + "game rewrites it.");
        }

        var bad = new List<BadFile>();
        long bytesRead = 0;
        int done = 0;

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            if (done % 8 == 0 || done == files.Count)
                progress?.Report((done * 100 / files.Count,
                                  $"Checking file {done} of {files.Count}…"));

            string full = System.IO.Path.Combine(gameDirectory, f.Path);
            FileInfo info;
            try { info = new FileInfo(full); }
            catch (Exception ex)
            {
                bad.Add(new BadFile(f.Path, Fault.Unreadable, ex.GetType().Name));
                continue;
            }

            if (!info.Exists)
            {
                // A missing config or state file is the game's business: it
                // writes them itself and a fresh install has not run yet.
                // Anything else missing is damage.
                bad.Add(new BadFile(f.Path,
                    IsGameManaged(f.Path) ? Fault.ChangedSinceInstall : Fault.Missing,
                    IsGameManaged(f.Path)
                        ? "not there yet — the game writes this one itself"
                        : "not found"));
                continue;
            }

            // Size first: it is free, and a wrong size makes hashing pointless
            // — the file is already known to be wrong, and reading a gigabyte
            // to confirm it would only cost time.
            if (info.Length != f.Size)
            {
                // A config the game writes to CHANGES SIZE — that is what
                // saving a setting does. Reporting d2arch.ini as damage sent a
                // tester chasing a fault that was his own keybindings, and
                // worse, offered to "repair" it: replacing the file would have
                // thrown his settings away to fix nothing.
                //
                // Damage is for files the game does not write. Everything the
                // game owns is reported apart and never repaired.
                bad.Add(new BadFile(f.Path,
                    IsGameManaged(f.Path) ? Fault.ChangedSinceInstall : Fault.WrongSize,
                    IsGameManaged(f.Path)
                        ? "a different size — normal for a file the game writes to"
                        : $"{info.Length:N0} bytes, expected {f.Size:N0}"));
                continue;
            }

            // No recorded hash means the manifest cannot answer for the
            // contents. Right size is all that can honestly be claimed.
            if (string.IsNullOrWhiteSpace(f.Sha256)) continue;

            string actual;
            try
            {
                actual = await Sha256Async(full, ct).ConfigureAwait(false);
                bytesRead += info.Length;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                bad.Add(new BadFile(f.Path, Fault.Unreadable, ex.GetType().Name));
                continue;
            }

            if (!actual.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                bad.Add(new BadFile(f.Path,
                    IsExecutable(f.Path) ? Fault.WrongContent : Fault.ChangedSinceInstall,
                    "the file is the right size but its contents differ"));
        }

        return new Result(files.Count, bytesRead, bad, manifest?.Version, null);
    }

    /// "v3.8.6" and "3.8.6" are the same version written two ways, and a
    /// false mismatch here would hide every real one behind a wrong warning.
    private static bool SameVersion(string a, string b)
        => a.Trim().TrimStart('v', 'V').Equals(b.Trim().TrimStart('v', 'V'),
                                               StringComparison.OrdinalIgnoreCase);

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        // Shared read: a file the game or an antivirus has open should be
        // hashed, not reported as a fault it is not.
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 20, useAsync: true);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
