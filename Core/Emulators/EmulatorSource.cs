namespace LauncherV2.Core.Emulators;

/// Where an emulator would come from, and whose work it is.
///
/// An emulator is somebody else's program under somebody else's licence. The
/// launcher will fetch one only when every field here is filled in, because
/// these are exactly the things the player is agreeing to when they press yes:
/// who wrote it, under what licence, and from which address. A download the
/// player cannot attribute is a download they cannot consent to.
///
/// Nothing here is a default. A bridge that does not state its emulator's
/// author and licence simply gets no auto-install offer, and the player is
/// told to install it themselves — which is the behaviour the launcher had
/// before this existed, and remains the fallback.
public sealed record EmulatorSource(
    /// Who wrote the emulator. Shown to the player, verbatim.
    string Author,

    /// The licence's short name — "MIT", "GPL-3.0", "non-commercial".
    string Licence,

    /// The licence text itself, at the author's own address. The player can
    /// read it before agreeing, and a copy is written next to the installed
    /// files so it travels with them.
    string LicenceUrl,

    /// The page a person would visit to download this by hand. The manual
    /// route always stays open, and this is where it points.
    string DownloadPage,

    /// GitHub owner/repo the release is read from.
    string Owner,
    string Repo,

    /// Matched case-insensitively against the release's asset names.
    string AssetPattern,

    /// Release tag to pin, or null for whatever the project calls latest.
    string? PinnedTag = null,

    /// A folder inside the archive whose contents are the real payload, or
    /// null when the archive's root is already the program.
    string? RootInsideArchive = null)
{
    /// An offer may only be made when the player can be told all three of who,
    /// under what licence, and from where. Anything less and the launcher says
    /// nothing and lets them do it themselves.
    public bool IsComplete
        => !string.IsNullOrWhiteSpace(Author)
        && !string.IsNullOrWhiteSpace(Licence)
        && !string.IsNullOrWhiteSpace(LicenceUrl)
        && !string.IsNullOrWhiteSpace(DownloadPage)
        && !string.IsNullOrWhiteSpace(Owner)
        && !string.IsNullOrWhiteSpace(Repo)
        && !string.IsNullOrWhiteSpace(AssetPattern)
        && LicenceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && DownloadPage.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
