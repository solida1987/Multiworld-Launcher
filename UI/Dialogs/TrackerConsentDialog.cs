using System.Windows;
using LauncherV2.Core.Trackers;

namespace LauncherV2.UI.Dialogs;

/// The map-tracker question, asked once.
///
/// ⚠ Lives here and not in Core. The policy — what was answered, and what
/// that permits — is a setting and belongs with the other settings; the
/// WINDOW is a user-interface concern. Keeping the MessageBox in Core broke
/// every console tool that compiles the tracker service, which is how the
/// split announced itself.
public static class TrackerConsentDialog
{
    public static bool Ask(Window? owner, TrackerEntry entry)
    {
        string author = entry.PackRepo.Split('/')[0];
        return Ask(owner,
            $"{entry.Game} has a map tracker: {entry.PackName}, by {author}.",
            "London would download two things onto this computer — PopTracker "
          + "itself (about 7 MB, once) and this game's pack. Neither is part "
          + "of the launcher and neither is stored in our repository; they come "
          + "from their own authors' releases.");
    }

    /// The same question for Universal Tracker, which is neither a pack nor
    /// PopTracker — but is still somebody else's work downloaded here, so it
    /// asks the same thing rather than slipping in under the first answer.
    public static bool AskForUniversal(Window? owner) =>
        Ask(owner,
            "Nobody has built a map tracker for this game.",
            "Universal Tracker adds a tracking tab to the Archipelago client "
          + "for any game at all. London would download one apworld (about "
          + "170 KB) into your engine's custom_worlds folder. It is written "
          + "by FarisTheAncient and is not part of the launcher.");

    private static bool Ask(Window? owner, string headline, string what)
    {
        if (TrackerConsent.Answer is { } already) return already;

        var result = MessageBox.Show(owner,
            headline + "\n\n" + what + "\n\n"
            + "Say yes once and every game with a tracker gets the same, "
            + "without being asked again. Say no and the tracker buttons stay "
            + "hidden — you can turn them back on here later.\n\n"
            + "Download map trackers?",
            "Map trackers", MessageBoxButton.YesNo, MessageBoxImage.Question);

        bool yes = result == MessageBoxResult.Yes;
        TrackerConsent.Set(yes);
        return yes;
    }
}
