using LauncherV2.Core;

namespace LauncherV2.Core.Trackers;

/// May London fetch map trackers onto this machine?
///
/// Asked once, and then never again — the same bargain as cover art. 147 of
/// the catalogue's games have a pack somebody else wrote, and asking on each
/// of them would be nagging rather than asking.
///
/// ⚠ The answer is remembered BOTH ways. A no is an answer, not a delay: the
/// tracker buttons disappear until the player changes their mind, rather than
/// asking again the next time they press one.
public static class TrackerConsent
{
    /// Null when it has never come up.
    public static bool? Answer => SettingsStore.Load().TrackerConsent;

    /// True when there is nothing to hide: either they said yes, or they have
    /// not been asked and the question is still worth putting.
    public static bool MayOffer => Answer != false;

    public static void Set(bool yes)
    {
        var s = SettingsStore.Load();
        s.TrackerConsent = yes;
        SettingsStore.Save(s);
    }

    // ⚠ The QUESTION itself lives in the UI, in TrackerConsentDialog.
    // Core answers what was decided; it does not open windows. A MessageBox
    // in here broke every console tool that compiles the tracker service,
    // which is how the split announced itself.
}
