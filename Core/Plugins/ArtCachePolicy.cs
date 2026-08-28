using System;
using System.Globalization;

namespace LauncherV2.Core.Plugins;

/// Why a cached cover or banner may be fetched again.
///
/// This lives apart from GameArtCache so it can be proved. The old rule was
/// one line -- skip anything already on disk -- and it meant a wrong address
/// could never be corrected for a player who had already seen it. The rule
/// below is small enough to read and small enough to test; the proof in
/// tools/ArtCacheProof compiles THIS file, not a built copy of it, so it
/// cannot pass against a stale build.
public enum ArtFetchReason
{
    /// Already correct and recently confirmed. No request.
    UpToDate,
    /// Nothing on disk yet.
    Missing,
    /// The catalogue now names a different address than the one that produced
    /// this file. This is what makes a correction travel to the player.
    AddressChanged,
    /// On disk from before we recorded addresses. Confirm once, then it is
    /// known.
    Unrecorded,
    /// Same address, but old enough that the host may have swapped the image
    /// behind it. Asked with If-Modified-Since, so unchanged costs a 304.
    DueForRecheck,
}

public static class ArtCachePolicy
{
    public static ArtFetchReason Decide(bool onDisk, string? currentUrl,
                                        string? recordedUrl, string? recordedFetchedUtc,
                                        DateTime nowUtc, TimeSpan recheckAfter)
    {
        if (string.IsNullOrWhiteSpace(currentUrl)) return ArtFetchReason.UpToDate;
        if (!onDisk) return ArtFetchReason.Missing;
        if (recordedUrl is null) return ArtFetchReason.Unrecorded;
        if (!string.Equals(recordedUrl, currentUrl, StringComparison.Ordinal))
            return ArtFetchReason.AddressChanged;
        // An unparsable or absent date is treated as long ago, not as fresh --
        // erring towards one extra conditional request, never towards showing
        // a picture we can no longer vouch for.
        if (!TryParseUtc(recordedFetchedUtc, out DateTime stamp)) return ArtFetchReason.DueForRecheck;
        return nowUtc - stamp > recheckAfter ? ArtFetchReason.DueForRecheck : ArtFetchReason.UpToDate;
    }

    /// May this request be answered with "unchanged"? Only a time-based
    /// re-check may. A changed address must fetch bytes, whatever date the
    /// file carries -- otherwise the correction silently does nothing.
    public static bool MayUseIfModifiedSince(ArtFetchReason reason) =>
        reason == ArtFetchReason.DueForRecheck;

    public static bool TryParseUtc(string? s, out DateTime utc)
    {
        utc = DateTime.MinValue;
        return !string.IsNullOrWhiteSpace(s)
            && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
    }
}
