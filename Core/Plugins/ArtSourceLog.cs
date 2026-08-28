using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LauncherV2.Core.Plugins;

/// Which address produced each cached image.
///
/// Two different code paths write art to the same files -- the background
/// prefetch and the "download covers" action -- and both used to skip any
/// path that already existed. That is why a corrected address never reached
/// a player who had already seen the wrong one. They now share this record,
/// so a change in the catalogue is a reason to fetch again, whichever path
/// runs first.
public sealed class ArtSource
{
    public string Url { get; set; } = "";
    public string Fetched { get; set; } = "";     // ISO-8601 UTC
}

public static class ArtSourceLog
{
    public static string Path =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "art_sources.json");

    public static Dictionary<string, ArtSource> Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, ArtSource>>(File.ReadAllText(Path));
                if (d is not null)
                    return new Dictionary<string, ArtSource>(d, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception)
        {
            // Unreadable bookkeeping must never cost the player their art. An
            // empty map means every file is confirmed once more, not deleted.
        }
        return new Dictionary<string, ArtSource>(StringComparer.OrdinalIgnoreCase);
    }

    public static void Save(Dictionary<string, ArtSource> map)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string tmp = Path + ".part";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                map, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception) { }
    }

    /// Does this payload begin like an image?
    ///
    /// ⚠ The old check demanded a PNG signature -- but 355 of the catalogue's
    /// 478 covers are JPEG, so the "download covers" action rejected three out
    /// of four games and told the player their download "was not an image".
    /// The point of the check is to keep an HTML error page from being saved
    /// under a .png name and trusted forever; that only needs the formats we
    /// actually address.
    public static bool LooksLikeImage(byte[] b)
    {
        if (b.Length < 128) return false;
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;  // PNG
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;                  // JPEG
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return true;                  // GIF
        if (b[0] == 0x42 && b[1] == 0x4D) return true;                                  // BMP
        if (b.Length > 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true; // WebP
        return false;
    }
}
