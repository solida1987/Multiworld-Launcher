using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// AssetRepair — re-fetches loose asset files that an old self-updater failed to
// deliver.

// THE HOLE THIS PLUGS (verified against git history + the shipped package):
// 2.9.29 first shipped Assets\Sounds\ (the notification sound picker)
// 2.9.30 fixed the self-updater, which until then copied ONLY the exe

// So anyone who updated FROM 2.9.29 or earlier ran the old exe-only updater:
// they received the new executable and none of the loose files beside it.
// picker appears, the wavs are absent, and every notification falls back to the
// Windows ping. The 2.9.30 fix cannot repair that retroactively — the damage was
// done by the OLD updater, which had already run.

// Rather than telling those users to unzip the package by hand, the running
// build repairs itself: any bundled sound missing from disk is pulled from the
// repo (~110 KB each, and only ever the missing ones).

// Everything here is best-effort and silent on success.
// firewall in the way — the launcher still starts, the sound just stays a ping.

public static class AssetRepair
{
    // Raw-file base for the branch that carries the assets.
    // same branch the updater reads launcher_version.txt from, so there is one
    // place to change if the release branch ever moves.
    public static string RawBaseUrl { get; set; } =
        "https://raw.githubusercontent.com/solida1987/Multiworld-Launcher/main";

    public sealed record Result(int Restored, int Failed, IReadOnlyList<string> Missing)
    {
        public string? Summary() =>
            Restored == 0 && Failed == 0
                ? null
                : $"Sound files restored: {Restored}" + (Failed > 0 ? $", {Failed} failed" : "");
    }

    // Bundled sounds absent from disk.
    // list stays in one place (MainWindow.BundledSounds) — a ninth sound added
    // there is repaired here automatically, with nothing to keep in sync.
    public static List<string> MissingSounds(IEnumerable<string> soundIds)
    {
        var missing = new List<string>();
        foreach (string id in soundIds)
        {
            string p = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", id + ".wav");
            try
            {
                // A zero-byte file counts as missing: a half-finished copy is
                // worse than no file, because SoundPlayer throws on it instead
                // of falling through to the system sound.
                if (!File.Exists(p) || new FileInfo(p).Length == 0) missing.Add(id);
            }
            catch (Exception) { missing.Add(id); }
        }
        return missing;
    }

    // Download whatever is missing.
    public static async Task<Result> EnsureSoundsAsync(
        IEnumerable<string> soundIds, CancellationToken ct = default)
    {
        var missing = MissingSounds(soundIds);
        if (missing.Count == 0) return new Result(0, 0, missing);

        string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds");
        try { Directory.CreateDirectory(dir); }
        catch (Exception) { return new Result(0, missing.Count, missing); }

        int restored = 0, failed = 0;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ArchipelagoLauncher/2");

        foreach (string id in missing)
        {
            string url = $"{RawBaseUrl}/Assets/Sounds/{Uri.EscapeDataString(id)}.wav";
            string dst = Path.Combine(dir, id + ".wav");
            string tmp = dst + ".part";
            try
            {
                byte[] data = await http.GetByteArrayAsync(url, ct);
                // A 404 page would arrive as a few hundred bytes of HTML and
                // then be played as a "sound"; the shortest real clip here is
                // ~110 KB, so anything tiny is a failed fetch, not audio.
                if (data.Length < 4096) { failed++; continue; }

                // Write to a sidecar and move into place, so an interrupted
                // download can never leave a truncated wav that looks present.
                await File.WriteAllBytesAsync(tmp, data, ct);
                File.Move(tmp, dst, overwrite: true);
                restored++;
            }
            catch (Exception)
            {
                failed++;
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
            }
        }
        return new Result(restored, failed, missing);
    }
}
