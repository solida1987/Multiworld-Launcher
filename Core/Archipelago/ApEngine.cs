using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace LauncherV2.Core.Archipelago;

// ApEngine — finding the Archipelago engine, and saying honestly whether it
// can be used.
//
// WHAT THIS IS NOT
// It is not a wrapper that runs generation. It answers one question before any
// of that is attempted: is there an engine here, which version, and would a
// generation actually work? Everything downstream (stages B-E) assumes a valid
// engine, so this is where "no" has to be said clearly.
//
// WHY DISCOVERY IS NOT JUST "DOES THE FOLDER EXIST"
// Measured on a real 0.6.7 install:
//   * The exes write their logs into the install folder unconditionally --
//     there is no log-path flag. A read-only install does not merely stop
//     London from writing; it breaks the engine itself. So writability is a
//     capability, not a nicety.
//   * The install carries somebody's custom_worlds, and EVERY generation
//     imports EVERY one of them, whether or not the seed uses that game. A
//     single broken world is a broken engine for all games. Counting them is
//     part of knowing whether generation will work.
//   * A folder can hold ArchipelagoServer.exe and still be useless for us if
//     ArchipelagoGenerate.exe is missing. Both are required; name both.
//
// London never reads or writes the engine's host.yaml. Everything is passed
// explicitly on the command line, because the host.yaml on a real machine is
// already customised in ways that silently change results.
public static class ApEngine
{
    /// The three programs London drives. Nothing else in the install is used.
    public const string GenerateExeName = "ArchipelagoGenerate.exe";
    public const string ServerExeName   = "ArchipelagoServer.exe";
    /// Only used to regenerate the option templates, headless.
    public const string LauncherExeName = "ArchipelagoLauncher.exe";

    /// The oldest engine London will drive. Below this, worlds the catalogue
    /// ships have already been seen to refuse to load.
    public static readonly Version MinimumVersion = new(0, 6, 0);

    public sealed record World(string File, string? Game, string? WorldVersion, bool ManifestOk);

    /// What an install can and cannot do. `Usable` is the only field callers
    /// need to gate on; the rest is for telling the player why.
    public sealed record Report(
        string Root,
        bool Exists,
        Version? Version,
        bool HasGenerate,
        bool HasServer,
        bool HasLauncher,
        bool Writable,
        string TemplatesDir,
        string CustomWorldsDir,
        IReadOnlyList<World> CustomWorlds,
        IReadOnlyList<string> Problems)
    {
        public bool Usable => Exists && HasGenerate && HasServer && Writable
                              && Version != null && Version >= MinimumVersion;

        public int BrokenWorldCount => CustomWorlds.Count(w => !w.ManifestOk);

        /// One line for the log and the readiness panel.
        public string Summary()
        {
            if (!Exists)   return $"No Archipelago engine at {Root}";
            if (!Usable)   return $"Engine at {Root} cannot be used: {string.Join("; ", Problems)}";
            string v = Version?.ToString() ?? "unknown";
            string worlds = CustomWorlds.Count == 0
                ? "no extra worlds installed"
                : $"{CustomWorlds.Count} extra world(s)"
                  + (BrokenWorldCount > 0 ? $", {BrokenWorldCount} with a broken manifest" : "");
            return $"Archipelago {v} at {Root} — {worlds}";
        }
    }

    /// Places an install is likely to be, most-specific first. A path the
    /// player nominated always wins; we never go hunting when we were told.
    public static IEnumerable<string> LikelyRoots(string? nominated = null)
    {
        if (!string.IsNullOrWhiteSpace(nominated)) yield return nominated!;

        string[] bases =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        foreach (string b in bases)
            if (!string.IsNullOrEmpty(b))
                yield return Path.Combine(b, "Archipelago");
    }

    /// First usable engine among the likely places, or null.
    ///
    /// A nominated path is the answer, usable or not. Falling through to some
    /// other install when the chosen one has a problem would mean London runs
    /// an engine the player did not pick and never mentions it -- and then the
    /// problem they need to fix stays invisible. Say what is wrong with the
    /// one they chose instead.
    public static Report? Discover(string? nominated = null)
    {
        if (!string.IsNullOrWhiteSpace(nominated))
            return Inspect(nominated!);

        Report? firstFound = null;
        foreach (string root in LikelyRoots())
        {
            var r = Inspect(root);
            if (r.Usable) return r;
            // Remember the first thing that at least looked like an install, so
            // the player is told what is wrong with it rather than "not found".
            if (r.Exists && firstFound == null) firstFound = r;
        }
        return firstFound;
    }

    // ⚠ Inspect() opens EVERY apworld in the engine and writes a probe file to
    // see whether the folder takes writes. That is a fair price once. It is not
    // a fair price from a slot card, which is rebuilt every two seconds, once
    // per slot -- and that is exactly where the launcher was standing when it
    // ran out of stack on 28 August: BuildCard -> IsInstalled -> ApworldPath ->
    // Discover -> Inspect -> reading zip central directories.
    //
    // Held briefly, so a sweep of cards costs one look at the disk instead of
    // one per card. Anything that CHANGES the engine calls Forget().
    private static readonly object _reportLock = new();
    private static readonly Dictionary<string, (DateTime At, Report R)> _reports = new();
    private static readonly TimeSpan ReportLifetime = TimeSpan.FromSeconds(5);

    /// Throw the remembered reports away — the engine on disk just changed.
    public static void Forget()
    {
        lock (_reportLock) _reports.Clear();
    }

    /// Everything knowable about an install without running any of its code.
    public static Report Inspect(string root)
    {
        string key = root ?? "";
        lock (_reportLock)
        {
            if (_reports.TryGetValue(key, out var hit)
                && DateTime.UtcNow - hit.At < ReportLifetime)
                return hit.R;
        }

        var fresh = InspectFromDisk(root);
        lock (_reportLock) _reports[key] = (DateTime.UtcNow, fresh);
        return fresh;
    }

    private static Report InspectFromDisk(string root)
    {
        var problems = new List<string>();
        var worlds = new List<World>();

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new Report(root ?? "", false, null, false, false, false, false,
                              "", "", worlds, new[] { "the folder does not exist" });

        bool hasGen = File.Exists(Path.Combine(root, GenerateExeName));
        bool hasSrv = File.Exists(Path.Combine(root, ServerExeName));
        bool hasLau = File.Exists(Path.Combine(root, LauncherExeName));

        if (!hasGen) problems.Add($"{GenerateExeName} is missing — seeds cannot be generated");
        if (!hasSrv) problems.Add($"{ServerExeName} is missing — seeds cannot be hosted");
        if (!hasLau) problems.Add($"{LauncherExeName} is missing — option templates cannot be refreshed");

        Version? version = ReadVersion(root, problems);
        if (version != null && version < MinimumVersion)
            problems.Add($"version {version} is older than {MinimumVersion}, "
                       + "which several catalogue games already refuse to run on");

        // The engine writes its own logs here on every run, with no way to send
        // them elsewhere. If we cannot write, neither can it.
        bool writable = CanWrite(root);
        if (!writable)
            problems.Add("the folder cannot be written to — the engine writes its "
                       + "own logs there on every run, so generation would fail");

        string customWorlds = Path.Combine(root, "custom_worlds");
        string templates    = Path.Combine(root, "Players", "Templates");

        if (Directory.Exists(customWorlds))
            worlds.AddRange(ReadWorlds(customWorlds));

        int broken = worlds.Count(w => !w.ManifestOk);
        if (broken > 0)
            problems.Add($"{broken} installed world(s) have a broken or missing manifest; "
                       + "every generation loads them all, so one bad world can stop "
                       + "seeds for games that do not use it");

        return new Report(root, true, version, hasGen, hasSrv, hasLau, writable,
                          templates, customWorlds, worlds, problems);
    }

    /// The engine stamps its own version into manifest.json. No code runs.
    private static Version? ReadVersion(string root, List<string> problems)
    {
        string manifest = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifest))
        {
            problems.Add("manifest.json is missing — this does not look like an Archipelago install");
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("version", out var v)
                || v.ValueKind != JsonValueKind.Array)
            {
                problems.Add("manifest.json does not state a version");
                return null;
            }
            var parts = v.EnumerateArray().Select(e => e.TryGetInt32(out int i) ? i : 0).ToArray();
            return parts.Length switch
            {
                >= 3 => new Version(parts[0], parts[1], parts[2]),
                2    => new Version(parts[0], parts[1]),
                _    => null,
            };
        }
        catch (Exception e)
        {
            problems.Add("manifest.json could not be read: " + e.Message);
            return null;
        }
    }

    /// An .apworld is a zip; its manifest is plain JSON inside. Reading it is
    /// safe -- unlike loading the world, which executes its Python.
    private static IEnumerable<World> ReadWorlds(string dir)
    {
        foreach (string file in Directory.EnumerateFiles(dir, "*.apworld").OrderBy(f => f))
        {
            string name = Path.GetFileName(file);
            string? game = null, wv = null;
            bool ok = false;
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("archipelago.json", StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                {
                    using var s = entry.Open();
                    using var doc = JsonDocument.Parse(s);
                    var r = doc.RootElement;
                    game = r.TryGetProperty("game", out var g) ? g.GetString() : null;
                    wv   = r.TryGetProperty("world_version", out var w) ? w.ToString() : null;
                    // The loader compares `version` as an integer. A manifest
                    // without it is rejected even though the file parses -- so
                    // "has a manifest" is not the same as "has a valid one".
                    bool hasIntVersion = r.TryGetProperty("version", out var ver)
                                         && ver.ValueKind == JsonValueKind.Number;
                    ok = !string.IsNullOrWhiteSpace(game) && hasIntVersion;
                }
            }
            catch
            {
                // Unreadable zip or unparseable manifest: reported as broken,
                // never thrown. A bad world in the folder must not stop London
                // from describing the rest.
            }
            yield return new World(name, game, wv, ok);
        }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".london_write_probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
