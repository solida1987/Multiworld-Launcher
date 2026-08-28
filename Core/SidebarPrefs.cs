using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LauncherV2.Core;

// SidebarPrefs — how the player wants their library shown.
//
// This exists because the sidebar stopped being one list. With thirty games
// installed, "a long scroll" is the whole experience, so the player now
// chooses density, grouping and order -- and those choices have to survive a
// restart, or they are not choices, just fidgeting.
//
// Deliberately its own small file rather than more fields on the library:
// the library records WHAT the player has, this records how they LOOK at it.
// Deleting this file loses a preference; deleting the library loses a
// collection. Files with different blast radii stay separate.
public sealed class SidebarPrefs
{
    /// "cards" (icon + subtitle + badge) or "rows" (one line, small icon).
    [JsonPropertyName("density")]   public string Density   { get; set; } = "cards";
    /// "status" (Favorites/Installed/Not installed), "platform", or "folder".
    [JsonPropertyName("group")]     public string Group     { get; set; } = "status";
    /// "custom" (drag order), "name", "recent" (last played), "added".
    [JsonPropertyName("sort")]      public string Sort      { get; set; } = "custom";
    /// Group headers the player has clicked shut.
    [JsonPropertyName("collapsed")] public List<string> Collapsed { get; set; } = new();

    private static readonly string PathOnDisk =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "sidebar_prefs.json");

    public static SidebarPrefs Load()
    {
        try
        {
            if (File.Exists(PathOnDisk))
                return JsonSerializer.Deserialize<SidebarPrefs>(File.ReadAllText(PathOnDisk))
                       ?? new SidebarPrefs();
        }
        catch { /* a broken prefs file must never break the sidebar */ }
        return new SidebarPrefs();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOnDisk)!);
            string tmp = PathOnDisk + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, PathOnDisk, overwrite: true);
        }
        catch { }
    }

    public bool IsCollapsed(string group)
        => Collapsed.Contains(group, StringComparer.OrdinalIgnoreCase);

    public void ToggleCollapsed(string group)
    {
        int removed = Collapsed.RemoveAll(
            g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            Collapsed.Add(group);
        Save();
    }
}
