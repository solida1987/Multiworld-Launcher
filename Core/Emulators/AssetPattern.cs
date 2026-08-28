namespace LauncherV2.Core.Emulators;

// Whether a release asset's file name fits a pattern like
// "azahar-windows-msvc-*.zip".
//
// ⚠ THE BUG THIS FILE REPLACES. The installer used to look for the pattern as
// a LITERAL substring of the file name -- star included. No file name contains
// a star, so every wildcard pattern matched nothing, and the dialog told the
// player the release held no such file while the file sat right there. Nobody
// noticed for weeks because the emulators actually fetched (BizHawk,
// DuckStation) use fixed asset names without a star; Azahar was the first
// wildcard pattern a player pressed the button on, 25 Aug 2026.
public static class AssetPattern
{
    /// Case-insensitive. '*' matches any run of characters, including none.
    /// The pattern is anchored at both ends: "a-*.zip" does not accept
    /// "a-1.zip.sig".
    public static bool Matches(string name, string pattern)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(pattern))
            return false;

        string[] parts = pattern.Split('*');
        int pos = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0) continue;

            int at = name.IndexOf(part, pos, System.StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;

            // The first segment must sit at the very start, or "windows" would
            // accept "not-for-windows".
            if (i == 0 && at != 0) return false;
            pos = at + part.Length;
        }

        // The last segment must reach the very end, unless the pattern itself
        // ends with a star.
        if (!pattern.EndsWith("*") && pos != name.Length) return false;
        return true;
    }
}
