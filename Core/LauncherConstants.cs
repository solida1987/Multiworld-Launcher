namespace LauncherV2.Core;

internal static class LauncherConstants
{
    /// Number of location checks shown per page in the Progression tab.
    internal const int LocPageSize = 50;

    /// Platform filter chips are only shown for platforms with at least this many games.
    internal const int PlatformChipMinGames = 3;

    /// Maximum items kept in the in-memory item tracker (older entries trimmed).
    internal const int ItemTrackerMaxEntries = 10_000;
}
