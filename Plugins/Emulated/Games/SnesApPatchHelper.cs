using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SnesApPatchHelper — shared AP-patch application for the SNES SNIClient games
// (A Link to the Past = .aplttp, Super Metroid = .apsm).
//
// These are APProcedurePatch containers, exactly like Pokémon Emerald's
// .apemerald — a ZIP holding archipelago.json + the procedure's data files
// (bsdiff4 / IPS / token blobs). The only mechanical differences between games
// are the patch file extension and the produced ROM extension; everything else
// (locate the seed's patch, validate the base ROM's MD5, run the manifest-driven
// Plugins/Scripts/apply_appatch.py on a library COPY, never touch the original)
// is identical, so it lives here once and both plugins call it.
//
// Why Python (mirrors PokemonEmeraldPlugin): bsdiff4 is bzip2-based and IPS/
// token application is trivial, but the .NET BCL has no bzip2; every AP player
// machine already runs Python, and apply_appatch.py carries pure-python
// fallbacks so no pip packages are required. The script validates the manifest
// base_checksum (MD5 of the vanilla ROM) before writing anything.
// ═══════════════════════════════════════════════════════════════════════════════

internal static class SnesApPatchHelper
{
    /// Standard AP generator output folder (newest patch auto-detected here).
    public static string ApOutputDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Archipelago", "output");

    /// All *.<patchExt> candidates under the AP output folder, newest first.
    public static List<FileInfo> EnumeratePatches(string patchExt)
    {
        try
        {
            if (!Directory.Exists(ApOutputDirectory)) return new List<FileInfo>();
            return Directory
                .EnumerateFiles(ApOutputDirectory, "*" + patchExt, SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }
        catch { return new List<FileInfo>(); }
    }

    /// A field from a patch container's archipelago.json, or null.
    public static string? ReadManifestField(string patchPath, string field)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(patchPath);
            var entry     = zip.GetEntry("archipelago.json");
            if (entry == null) return null;
            using var s   = entry.Open();
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty(field, out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    /// Seed name out of "AP_<seed>_P<slot>_<player>.<ext>", or "unknown".
    public static string ParseSeedFromPatch(string patchPath)
    {
        string name = Path.GetFileNameWithoutExtension(patchPath);
        const string prefix = "AP_";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string rest = name.Substring(prefix.Length);
            int us = rest.IndexOf('_');
            string seed = us >= 0 ? rest.Substring(0, us) : rest;
            if (seed.Length > 0) return seed;
        }
        return "unknown";
    }

    /// The patch that would be used right now (same priority order as Emerald):
    ///   1. explicit user pick   2. this room's seed AND matching player_name
    ///   3. matching player_name (newest)   4. newest generated patch.
    public static string? ResolvePatch(
        string patchExt, string? explicitPick, string? seed, string? slot, out string how)
    {
        if (!string.IsNullOrEmpty(explicitPick) && File.Exists(explicitPick))
        {
            how = "picked in Settings";
            return explicitPick;
        }

        var candidates = EnumeratePatches(patchExt);

        if (!string.IsNullOrEmpty(seed))
        {
            var seedMatches = candidates.Where(f => f.Name.StartsWith(
                $"AP_{seed}", StringComparison.OrdinalIgnoreCase)).ToList();
            if (seedMatches.Count > 0)
            {
                var exact = seedMatches.FirstOrDefault(f =>
                    string.Equals(ReadManifestField(f.FullName, "player_name"), slot,
                                  StringComparison.OrdinalIgnoreCase)) ?? seedMatches[0];
                how = "matched this room's seed";
                return exact.FullName;
            }
        }

        if (!string.IsNullOrEmpty(slot))
        {
            var slotMatch = candidates.FirstOrDefault(f =>
                string.Equals(ReadManifestField(f.FullName, "player_name"), slot,
                              StringComparison.OrdinalIgnoreCase));
            if (slotMatch != null)
            {
                how = $"matched your slot name '{slot}'";
                return slotMatch.FullName;
            }
        }

        how = "newest generated patch";
        return candidates.FirstOrDefault()?.FullName;
    }

    /// Result of applying a patch.
    public readonly record struct ApplyResult(string? OutRom, string Note);

    /// Apply <paramref name="patch"/> to a library COPY of <paramref name="romPath"/>,
    /// producing "<rom>_<patchstem><resultExt>". Reuses an up-to-date output. The
    /// original ROM is only read. Returns the produced ROM path (or null) plus a
    /// player-readable note for SessionRomNote.
    public static async Task<ApplyResult> ApplyAsync(
        string gameLabel, string patch, string how, string romPath,
        string romLibraryDirectory, string resultExt, CancellationToken ct)
    {
        string outRom = Path.Combine(
            romLibraryDirectory,
            $"{Path.GetFileNameWithoutExtension(romPath)}_{Path.GetFileNameWithoutExtension(patch)}{resultExt}");

        if (File.Exists(outRom) &&
            File.GetLastWriteTimeUtc(outRom) >= File.GetLastWriteTimeUtc(patch))
        {
            return new ApplyResult(outRom,
                $"[{gameLabel}] Using existing AP-patched ROM: {outRom} " +
                $"(patch {Path.GetFileName(patch)}, {how}).");
        }

        var python = FindPython();
        if (python == null)
        {
            return new ApplyResult(null,
                $"[{gameLabel}] Cannot apply the AP patch: no Python 3 found on this " +
                "machine (looked for 'py', 'python' and a user install). Install " +
                "Python 3, then relaunch. Launching the vanilla ROM — items cannot " +
                "be delivered.");
        }

        string script = Path.Combine(AppContext.BaseDirectory, "Plugins", "Scripts", "apply_appatch.py");
        string apLib  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Archipelago", "lib");

        var psi = new ProcessStartInfo
        {
            FileName               = python.Value.File,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        foreach (string pre in python.Value.PreArgs) psi.ArgumentList.Add(pre);
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(patch);
        psi.ArgumentList.Add(romPath);
        psi.ArgumentList.Add(outRom);
        if (Directory.Exists(apLib)) psi.ArgumentList.Add(apLib);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start python");
        string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        string stderr = await proc.StandardError.ReadToEndAsync(ct);
        using (var patchCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            patchCts.CancelAfter(TimeSpan.FromSeconds(120));
            try { await proc.WaitForExitAsync(patchCts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("AP patch apply timed out after 120 s");
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout.Trim());
            var root = doc.RootElement;
            if (root.GetProperty("ok").GetBoolean())
            {
                string bsd = root.TryGetProperty("bsdiff4", out var b) ? (b.GetString() ?? "n/a") : "n/a";
                string md5 = root.TryGetProperty("md5", out var m) ? (m.GetString() ?? "?") : "?";
                return new ApplyResult(outRom,
                    $"[{gameLabel}] AP patch applied: {Path.GetFileName(patch)} → {outRom} " +
                    $"({how}; bsdiff4: {bsd}, md5 {md5}).");
            }
            string? err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            return new ApplyResult(null,
                $"[{gameLabel}] AP patch apply FAILED: {err}. Launching the vanilla " +
                "ROM — items cannot be delivered. (A 'checksum mismatch' means your " +
                "ROM is not the vanilla dump the patch was generated against.)");
        }
        catch
        {
            string error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            if (error.Length > 400) error = error[..400];
            return new ApplyResult(null,
                $"[{gameLabel}] AP patch apply FAILED: {error}. Launching the vanilla " +
                "ROM — items cannot be delivered.");
        }
    }

    // ── Python discovery (shared; identical to the Emerald plugin) ─────────────

    private static (string File, string[] PreArgs)? _pythonCache;

    public static (string File, string[] PreArgs)? FindPython()
    {
        if (_pythonCache != null) return _pythonCache;

        if (ProbePython("py", "-3 --version"))
            return _pythonCache = ("py", new[] { "-3" });
        if (ProbePython("python", "--version"))
            return _pythonCache = ("python", Array.Empty<string>());

        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python");
            if (Directory.Exists(root))
            {
                string? exe = Directory.GetDirectories(root, "Python3*")
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "python.exe"))
                    .FirstOrDefault(File.Exists);
                if (exe != null && ProbePython(exe, "--version"))
                    return _pythonCache = (exe, Array.Empty<string>());
            }
        }
        catch { /* probing only */ }
        return null;
    }

    private static bool ProbePython(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
