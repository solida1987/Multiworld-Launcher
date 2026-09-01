using System;

namespace LauncherV2.Core.Plugins;

// May a plugin update install itself, with no dialog?
//
// The policy in one sentence: OUR OWN code updates itself; everybody else's
// still gets asked. "Our own" is decided two ways, neither of them by the
// package's word alone:
//
//   * the FirstParty list — the launcher's own record of the hand-built
//     plugins (PluginProvenance). Auto-installing these is the same act of
//     trust as installing a launcher update: the same author's code, from
//     the same releases.
//   * the release owner — the catalogue ships hundreds of plugins, all
//     published from the developer's own GitHub account. An update whose
//     feed AND package both live under that account is our own published
//     file, checksum-verified against our own feed, and the package's game
//     id is checked before install. The URLs come from the INSTALLED
//     manifest: a third party could point theirs at our account, but then
//     the only thing that can ever auto-install is our published package
//     for that same game id — a replacement, not a way in.
//
// This file is deliberately free of WPF and of installed-state lookups, so
// the ProvenanceProof suite can compile it from source and pin the policy.
// The consent dialog shows a sentence based on the same test, so what the
// player is told at approval time and what later happens cannot drift.
public static class PluginAutoUpdatePolicy
{
    public const string ReleaseOwner = "solida1987";

    /// The pure question: is this manifest's update channel ours?
    public static bool WouldAutoUpdate(PluginManifest m)
        => WouldAutoUpdate(m.GameId, m.Update);

    /// Same question from the raw parts — this is the overload the
    /// ProvenanceProof suite pins, so it must carry the whole rule.
    public static bool WouldAutoUpdate(string gameId, PluginUpdateSource? update)
        => FirstParty.For(gameId).IsFirstPartyPlugin
        || (update is { } src && OursByReleaseOwner(src));

    public static bool OursByReleaseOwner(PluginUpdateSource src)
        => UnderReleaseOwner(src.VersionUrl) && UnderReleaseOwner(src.PackageUrl);

    private static bool UnderReleaseOwner(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || u.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        && u.AbsolutePath.TrimStart('/')
            .StartsWith(ReleaseOwner + "/", StringComparison.OrdinalIgnoreCase);
}
