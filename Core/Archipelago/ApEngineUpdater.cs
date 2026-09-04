using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Archipelago;

// ApEngineUpdater — keeping the Archipelago engine itself current.
//
// WHY
// "Update AP worlds" fetched every world to its newest release and left the
// program that loads them where it was. A world published for a newer
// Archipelago than the one on disk then fails in the engine's own words —
// "cannot import name 'RuleWorldMixin'" — and nothing anywhere said that the
// fix was an engine update, not a world update. So the update buttons look at
// the engine first.
//
// HOW FAR THIS GOES
// Archipelago publishes one Windows artefact: an Inno Setup installer. Inno
// takes /VERYSILENT and /DIR, so an install that Archipelago's own installer
// made (it leaves unins000.exe behind) can be brought forward in place, into
// the same folder, keeping custom_worlds and Players — which is exactly what a
// player clicking through the installer by hand gets. An install laid out any
// other way is not ours to guess at; that one gets the download page.
//
// ⚠ Still asked first, every time. Running somebody else's installer is not a
// side effect of a button called "Update AP worlds", and Windows will raise a
// permission prompt of its own on top. The player says yes to a sentence that
// names the version, the folder, and what happens.
public static class ApEngineUpdater
{
    /// What is installed, what is published, and whether the two differ.
    public sealed record Check(ApEngine.Report? Engine, ApEngineSource.Offer? Offer, Version? Latest)
    {
        public bool HasEngine => Engine is { Usable: true, Version: not null };
        public bool Newer     => HasEngine && IsNewer(Engine!.Version, Latest);

        /// Archipelago's installer leaves its uninstaller beside the engine.
        /// That file is how London knows the folder was made by the installer
        /// and can be updated by it — and not by London writing into a layout
        /// it does not understand.
        public bool CanInstallInPlace
            => HasEngine && File.Exists(Path.Combine(Engine!.Root, "unins000.exe"));
    }

    public sealed record Result(bool Ok, string Message, Version? Now);

    /// "0.6.8" or "v0.6.8" → 0.6.8. A pre-release tag ("0.6.8-rc1") is null on
    /// purpose: /releases/latest never returns one, and if it ever did, a
    /// release candidate is not something to install under a player unasked.
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string t = tag.Trim();
        if (t.Length > 1 && (t[0] == 'v' || t[0] == 'V') && char.IsDigit(t[1])) t = t[1..];
        if (t.Any(c => !char.IsDigit(c) && c != '.')) return null;
        var parts = t.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length > 4) return null;
        var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out nums[i])) return null;
        return parts.Length switch
        {
            2 => new Version(nums[0], nums[1]),
            3 => new Version(nums[0], nums[1], nums[2]),
            _ => new Version(nums[0], nums[1], nums[2], nums[3]),
        };
    }

    public static bool IsNewer(Version? installed, Version? latest)
        => installed != null && latest != null && latest > installed;

    public static async Task<Check> CheckAsync(CancellationToken ct = default)
    {
        ApEngine.Report? engine = null;
        try
        {
            var s = SettingsStore.Load();
            engine = ApEngine.Discover(
                string.IsNullOrWhiteSpace(s.ApEnginePath) ? null : s.ApEnginePath);
        }
        catch (Exception) { }

        var offer = await ApEngineSource.LatestAsync(ct).ConfigureAwait(false);
        return new Check(engine, offer, offer == null ? null : ParseTag(offer.Version));
    }

    /// Archipelago programs running right now. The installer cannot replace a
    /// file that one of them holds open, and with message boxes suppressed it
    /// would fail rather than ask — so London asks first, by name.
    public static IReadOnlyList<string> Busy()
    {
        var names = new List<string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName.StartsWith("Archipelago", StringComparison.OrdinalIgnoreCase))
                        names.Add(p.ProcessName);
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
        }
        catch (Exception) { }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
    }

    private static string DownloadDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiworldLauncher", "downloads");

    /// Download the installer, check it, run it silently into the engine's
    /// own folder, and read the folder back to see that it took.
    ///
    /// Consent is the caller's job: this does not ask, so nothing here may be
    /// reached except from a button whose sentence the player said yes to.
    public static async Task<Result> InstallAsync(Check check,
                                                  IProgress<string>? progress = null,
                                                  CancellationToken ct = default)
    {
        Version? now = check.Engine?.Version;
        if (!check.Newer || check.Offer == null || check.Engine == null)
            return new Result(false, "There is no newer Archipelago to install.", now);

        string root = check.Engine.Root.TrimEnd('\\', '/');
        if (!check.CanInstallInPlace)
            return new Result(false,
                $"The Archipelago at {root} was not put there by Archipelago's installer, "
              + "so London cannot update it in place. Install "
              + $"{check.Offer.Version} by hand from {ApEngineSource.ProjectPage}", now);

        var busy = Busy();
        if (busy.Count > 0)
            return new Result(false,
                $"Close {string.Join(", ", busy)} first — the installer cannot replace "
              + "files that are in use. Archipelago was not changed.", now);

        var got = await ApEngineSource.FetchInstallerAsync(check.Offer, DownloadDir,
            new Progress<ApEngineSource.Progress>(p =>
                progress?.Report(p.Percent > 0 ? $"{p.Stage} — {p.Percent}%" : p.Stage)),
            ct).ConfigureAwait(false);
        if (!got.Ok || got.InstallerPath == null)
            return new Result(false, got.Message, now);

        progress?.Report($"Installing Archipelago {check.Offer.Version} into {root} — "
                       + "Windows may ask for permission…");
        string log = Path.Combine(DownloadDir, $"archipelago-{check.Offer.Version}-install.log");
        int code;
        try
        {
            // /VERYSILENT: no wizard. /SUPPRESSMSGBOXES: no questions, take
            // the defaults. /NORESTART: never reboot on our account. /DIR: the
            // folder the player's engine is already in, so worlds and YAMLs
            // stay where they are. UseShellExecute so the installer's own
            // manifest can raise the permission prompt it needs.
            var psi = new ProcessStartInfo
            {
                FileName = got.InstallerPath,
                Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"{root}\" /LOG=\"{log}\"",
                UseShellExecute = true,
            };
            using var p = Process.Start(psi);
            if (p == null)
                return new Result(false, "The installer did not start. Archipelago was not changed.", now);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            code = p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return new Result(false,
                "Windows asked for permission to run the installer and it was declined. "
              + "Archipelago was not changed.", now);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return new Result(false,
                "The installer could not be started: " + e.Message
              + ". Archipelago was not changed.", now);
        }

        // The folder just changed under every report London is holding.
        ApEngine.Forget();
        var after = ApEngine.Inspect(root);
        now = after.Version;

        if (code != 0)
            return new Result(false,
                $"Archipelago's installer stopped with code {code} ({InnoMeaning(code)}). "
              + $"The folder reports {after.Version?.ToString() ?? "no version"}. "
              + $"Its log is at {log}", now);

        if (after.Version == null || check.Latest == null || after.Version < check.Latest)
            return new Result(false,
                $"The installer finished, but {root} still reports "
              + $"{after.Version?.ToString() ?? "no version"} rather than {check.Offer.Version}. "
              + $"Its log is at {log}", now);

        try { File.Delete(got.InstallerPath); } catch (Exception) { }
        return new Result(true, $"Archipelago {after.Version} is now installed at {root}.", now);
    }

    /// Inno Setup's documented exit codes, in words.
    private static string InnoMeaning(int code) => code switch
    {
        1 => "setup could not initialise",
        2 => "cancelled before it began",
        3 => "a fatal error before installing",
        4 => "a fatal error while installing",
        5 => "a restart was declined",
        6 => "a restart is needed",
        7 => "a prerequisite check failed",
        8 => "a restart is required to finish",
        _ => "not a code the installer documents",
    };
}
