using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using LauncherV2.Core.Extensions;

namespace LauncherV2.Core.Emulators;

/// Fetch an emulator from its author's own release, once the player has said
/// yes to that specific author and licence.
///
/// This is the ONLY place in the launcher that may do so, and it exists so
/// that there is one implementation to read rather than one per bridge. It
/// refuses unless the source names an author, a licence and an address
/// (EmulatorSource.IsComplete), so an incomplete declaration can never turn
/// into a silent download.
///
/// The player's alternative is always open: every offer carries the download
/// page, and installing by hand into Emulators/&lt;folder&gt;/ works exactly as it
/// did before this file existed.
public static class EmulatorInstaller
{
    public sealed record Progress(string Stage, int Percent);

    public sealed record Result(bool Ok, string Message, string? ExePath);

    static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MultiworldLauncher");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// The asset this source points at, so the player can be shown its real
    /// name and size before agreeing rather than after.
    public static async Task<(string Url, string Name, long Size, string Tag)?>
        FindAssetAsync(EmulatorSource src, CancellationToken ct = default)
    {
        if (!src.IsComplete) return null;

        string api = src.PinnedTag is null
            ? $"https://api.github.com/repos/{src.Owner}/{src.Repo}/releases/latest"
            : $"https://api.github.com/repos/{src.Owner}/{src.Repo}/releases/tags/{src.PinnedTag}";

        using var http = NewClient();
        string json;
        try { json = await http.GetStringAsync(api, ct); }
        catch { return null; }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var t)
                     ? t.GetString() ?? "" : "";

        if (!root.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var a in assets.EnumerateArray())
        {
            string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (name.Length == 0
                || name.IndexOf(src.AssetPattern, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            string url = a.TryGetProperty("browser_download_url", out var u)
                         ? u.GetString() ?? "" : "";
            long size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            if (url.Length > 0) return (url, name, size, tag);
        }
        return null;
    }

    /// Download, unpack into Emulators/&lt;folder&gt;/, and leave the licence beside
    /// it. Returns where the executable ended up, or why it did not.
    public static async Task<Result> InstallAsync(
        EmulatorRequirement req, EmulatorSource src, string emulatorsRoot,
        IProgress<Progress>? progress = null, CancellationToken ct = default)
    {
        if (!src.IsComplete)
            return new Result(false,
                "This bridge does not say who wrote its emulator or under what "
              + "licence, so the launcher will not fetch it. Install it yourself "
              + "from the project's own page.", null);

        // FolderName arrives from an extension, which is somebody else's code.
        if (!req.IsSafeFolderName)
            return new Result(false, "The bridge asked for an unsafe folder name.", null);

        var found = await FindAssetAsync(src, ct);
        if (found is null)
            return new Result(false,
                $"No download matching \"{src.AssetPattern}\" was found in "
              + $"{src.Owner}/{src.Repo}'s releases. The project may have renamed "
              + "its files — install by hand from " + src.DownloadPage, null);

        var (url, assetName, size, tag) = found.Value;
        progress?.Report(new Progress($"Downloading {assetName}", 0));

        byte[] bytes;
        using (var http = NewClient())
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? size;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var mem = new MemoryStream();
            var buf = new byte[81920];
            long got = 0;
            int read;
            while ((read = await stream.ReadAsync(buf, ct)) > 0)
            {
                await mem.WriteAsync(buf.AsMemory(0, read), ct);
                got += read;
                if (total > 0)
                    progress?.Report(new Progress($"Downloading {assetName}",
                                                  (int)(got * 100 / total)));
            }
            bytes = mem.ToArray();
        }

        string dest = Path.Combine(emulatorsRoot, req.FolderName);
        Directory.CreateDirectory(dest);
        progress?.Report(new Progress("Unpacking", 100));

        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith("/")) continue;

                string rel = entry.FullName.Replace('\\', '/');
                if (src.RootInsideArchive is string root)
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
                    return new Result(false,
                        "The archive tried to write outside the emulator folder. "
                      + "Nothing was installed.", null);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
        catch (InvalidDataException)
        {
            return new Result(false,
                $"{assetName} is not a zip archive this launcher can unpack. "
              + "Install by hand from " + src.DownloadPage, null);
        }

        // The licence travels with the files, so the folder can still answer
        // "whose is this and under what terms" long after the dialog is gone.
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dest, "SOURCE.txt"),
                $"{req.DisplayName}\r\n"
              + $"By {src.Author}, licensed {src.Licence}\r\n"
              + $"Licence text: {src.LicenceUrl}\r\n"
              + $"Downloaded from: {url}\r\n"
              + $"Release: {tag}\r\n\r\n"
              + "Fetched by Multiworld Launcher at the player's request. This "
              + "program is the author's work, not the launcher's.\r\n", ct);
        }
        catch { /* the emulator is installed; the note is a courtesy */ }

        string? exe = req.Resolve(emulatorsRoot);
        if (exe is null)
            return new Result(false,
                $"The download unpacked, but {req.ExeName} is not in it. "
              + "Check " + src.DownloadPage, null);

        return new Result(true,
            $"{req.DisplayName} {tag} installed — by {src.Author}, {src.Licence}.", exe);
    }
}
