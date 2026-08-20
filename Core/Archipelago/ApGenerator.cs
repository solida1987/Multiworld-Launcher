using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Archipelago;

// ApGenerator — running Archipelago's generator the way it actually behaves.
//
// Every rule below was measured on a real 0.6.7 install, and each one is a bug
// London would otherwise ship:
//
//   * The process does not exit when it is done. It parks in an atexit
//     input(). Waiting for exit hangs forever, and reading its output to EOF
//     hangs with it. Finished means "the files are there".
//   * stderr is loud on a healthy run -- a dozen warnings about other people's
//     worlds. stderr is not failure.
//   * The exit code is -1 for a missing file and -1 for a port clash and -1
//     for a broken world. The code tells you nothing; the text does.
//   * The same seed produces the same file name, so two runs sharing an output
//     folder overwrite each other in silence. Each run gets its own folder.
//   * host.yaml on a real machine is already customised -- the one on this
//     machine silently strips item plando. Nothing is left to it: every
//     setting travels on the command line.
public static class ApGenerator
{
    /// How far along, in words a person can read. Percent is deliberately
    /// absent: the generator reports no progress, and inventing a bar that
    /// crawls to 90% and waits is worse than saying what is happening.
    public sealed record Progress(string Stage, string? Detail = null);

    public sealed record Result(
        bool Ok,
        string Message,
        string? SeedZip,
        string? OutputDir,
        IReadOnlyList<string> SlotErrors,
        string Log)
    {
        /// The generator names the slot in its error text. Surfacing that is
        /// the difference between "generation failed" and "Marco's Pokemon
        /// options are impossible".
        public bool IsSlotProblem => SlotErrors.Count > 0;
    }

    /// Plando kinds London always allows. The engine's own default strips
    /// some of them and warns in a log nobody reads; a silently ignored
    /// plando block is a support nightmare.
    private const string AllPlando = "bosses,items,texts,connections";

    /// Does this set of slots generate at all? Runs the real fill and writes
    /// nothing (~5.6 s on this machine). Cheap enough to run before every real
    /// generation, which turns a six-second mystery into an answer.
    public static Task<Result> ValidateAsync(
        ApEngine.Report engine, string playersDir,
        IProgress<Progress>? progress = null, CancellationToken ct = default)
        => RunAsync(engine, playersDir, null, null, 3, false, true, progress, ct);

    /// Generate for real. <paramref name="outputRoot"/> gets a fresh subfolder
    /// per run, because identical seeds produce identical file names.
    public static Task<Result> GenerateAsync(
        ApEngine.Report engine, string playersDir, string outputRoot,
        long? seed = null, int spoiler = 3, bool race = false,
        IProgress<Progress>? progress = null, CancellationToken ct = default)
        => RunAsync(engine, playersDir, outputRoot, seed, spoiler, race, false, progress, ct);

    private static async Task<Result> RunAsync(
        ApEngine.Report engine, string playersDir, string? outputRoot,
        long? seed, int spoiler, bool race, bool validateOnly,
        IProgress<Progress>? progress, CancellationToken ct)
    {
        if (!engine.Usable)
            return new Result(false, engine.Summary(), null, null,
                              Array.Empty<string>(), "");

        if (!Directory.Exists(playersDir)
            || !Directory.EnumerateFiles(playersDir, "*.yaml").Any())
            return new Result(false, "There are no slots to generate.", null, null,
                              Array.Empty<string>(), "");

        // A run of its own, so nothing can overwrite anything.
        string outDir = "";
        if (!validateOnly)
        {
            outDir = Path.Combine(outputRoot!, "gen_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(outDir);
        }

        var psi = new ProcessStartInfo
        {
            FileName               = Path.Combine(engine.Root, ApEngine.GenerateExeName),
            WorkingDirectory       = engine.Root,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("--player_files_path"); psi.ArgumentList.Add(playersDir);
        psi.ArgumentList.Add("--plando");            psi.ArgumentList.Add(AllPlando);
        psi.ArgumentList.Add("--log_level");         psi.ArgumentList.Add("info");
        if (validateOnly) psi.ArgumentList.Add("--skip_output");
        else { psi.ArgumentList.Add("--outputpath"); psi.ArgumentList.Add(outDir); }
        if (!validateOnly) { psi.ArgumentList.Add("--spoiler"); psi.ArgumentList.Add(spoiler.ToString()); }
        if (race) psi.ArgumentList.Add("--race");
        if (seed != null) { psi.ArgumentList.Add("--seed"); psi.ArgumentList.Add(seed.Value.ToString()); }

        var log = new StringBuilder();
        void Note(string line)
        {
            lock (log) log.AppendLine(line);
            var p = Narrate(line);
            if (p != null) progress?.Report(p);
        }

        progress?.Report(new Progress(validateOnly ? "Checking the seed" : "Generating"));

        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception e)
        {
            return new Result(false, "The generator could not be started: " + e.Message,
                              null, outDir, Array.Empty<string>(), log.ToString());
        }

        using (proc)
        {
            // Asynchronous on purpose: the process never closes its streams,
            // so reading them to the end would wait forever.
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) Note(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) Note(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            bool Finished() => validateOnly
                ? proc.HasExited || log.ToString().Contains("Done.", StringComparison.Ordinal)
                : Directory.GetFiles(outDir, "*.zip").Length > 0 || proc.HasExited;

            try
            {
                while (!Finished())
                    await Task.Delay(200, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(proc);
                throw;
            }
            finally
            {
                // Give the last lines a moment to arrive, then stop it: it
                // will not stop by itself.
                await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
                TryKill(proc);
            }
        }

        string text = log.ToString();
        var slotErrors = ReadSlotErrors(text);

        if (validateOnly)
        {
            bool ok = slotErrors.Count == 0
                      && text.Contains("Done.", StringComparison.Ordinal);
            return new Result(ok,
                ok ? "These slots can be generated."
                   : slotErrors.Count > 0
                       ? "The seed cannot be generated yet."
                       : Summarise(text),
                null, null, slotErrors, text);
        }

        var zips = Directory.GetFiles(outDir, "*.zip");
        if (zips.Length == 0)
            return new Result(false,
                slotErrors.Count > 0 ? "The seed could not be generated." : Summarise(text),
                null, outDir, slotErrors, text);

        return new Result(true, "Seed generated.", zips[0], outDir, slotErrors, text);
    }

    /// Turns one line of the generator's chatter into something worth showing.
    /// Anything unrecognised is kept in the log and not shown, because a
    /// progress line that reads like a stack trace is not progress.
    private static Progress? Narrate(string line)
    {
        if (line.Contains("Weights:", StringComparison.Ordinal))
            return new Progress("Reading slots", line.Trim());
        if (line.Contains("Generating for", StringComparison.Ordinal))
            return new Progress("Building the multiworld", line.Trim());
        if (line.Contains("Filling the multiworld", StringComparison.Ordinal))
            return new Progress("Placing items", line.Trim());
        if (line.Contains("progression balancing", StringComparison.OrdinalIgnoreCase))
            return new Progress("Balancing progression");
        if (line.Contains("Beginning output", StringComparison.Ordinal))
            return new Progress("Writing the seed");
        if (line.Contains("Calculating playthrough", StringComparison.Ordinal))
            return new Progress("Working out the playthrough");
        if (line.Contains("Done.", StringComparison.Ordinal))
            return new Progress("Finished");
        return null;
    }

    /// The generator reports bad slots as a numbered list naming the file and
    /// the reason. Those lines are the only part a player can act on.
    private static IReadOnlyList<string> ReadSlotErrors(string text)
    {
        var found = new List<string>();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i].Trim();
            if (!l.Contains("is invalid. Please fix your yaml", StringComparison.Ordinal)) continue;

            // "1. File Marco.yaml document #1 (name: Marco) is invalid..."
            // followed by "Exception: <the actual reason>".
            string who = l;
            int dot = who.IndexOf(". File ", StringComparison.Ordinal);
            if (dot >= 0) who = who[(dot + 2)..];

            string? why = lines.Skip(i + 1).Take(3)
                               .Select(x => x.Trim())
                               .FirstOrDefault(x => x.StartsWith("Exception:", StringComparison.Ordinal));
            found.Add(why == null ? who : who + "  —  " + why["Exception:".Length..].Trim());
        }
        return found;
    }

    /// One sentence for a failure with no slot attached. The last traceback
    /// line is the closest thing the engine gives to a cause, except for the
    /// EOFError from its own atexit handler, which is never the cause.
    private static string Summarise(string text)
    {
        var lines = text.Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .ToList();

        string? last = lines.LastOrDefault(l =>
            (l.StartsWith("Exception", StringComparison.Ordinal)
             || l.Contains("Error:", StringComparison.Ordinal))
            && !l.Contains("EOFError", StringComparison.Ordinal));

        return last ?? "The generator stopped without producing a seed.";
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}
