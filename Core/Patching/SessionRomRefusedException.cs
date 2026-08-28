using System;

namespace LauncherV2.Core.Patching;

/// The ROM chosen for this seed is provably the wrong file, and playing it
/// would be worse than not starting.
///
/// LaunchAsync swallows every other failure from PrepareSessionRomAsync on
/// purpose — a patcher that cannot build this seed's ROM still leaves a
/// playable game, and a note explains it. This one is different: it is thrown
/// only when the launcher can PROVE the file is not the seed's, and the honest
/// outcome is a stop with a message, not a session that quietly sends nothing.
public sealed class SessionRomRefusedException : Exception
{
    public SessionRomRefusedException(string message) : base(message) { }
}
