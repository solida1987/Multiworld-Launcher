using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Extensions;

// An emulator bridge, installed as an EXTENSION rather than compiled in.
//
// The launcher drives NO emulator of its own. BizHawk used to be compiled in;
// it is an extension now like everything else, and simply ships pre-installed
// so a fresh copy works out of the box. Every other protocol -- SNI for SNES,
// NWA for snes9x, whatever comes next -- is a different conversation with a
// different program, and baking each one in would mean a launcher release every
// time somebody needs one more.
//
// So a bridge ships as a .londonextension the player installs, exactly like a
// game plugin. A game declares the protocol it speaks; if no bridge for that
// protocol is installed, the launcher SAYS SO instead of starting an emulator
// that will never connect.
//
// ⛔ A bridge carries PROTOCOL, never an emulator. The player installs their own
//    emulator into Emulators\<backend>\ -- see Tools/lint_no_emulator_download.py,
//    which covers this folder too. Fetching somebody else's emulator would make
//    us its distributor.
public interface IEmulatorBridge
{
    /// Matches a game manifest's client.protocol, e.g. "sni" or "bizhawk".
    string Protocol { get; }

    /// Shown to the player, e.g. "SNI (Super Nintendo Interface)".
    string DisplayName { get; }

    /// Systems this bridge can serve, matched against a plugin's RomSystem.
    string[] Systems { get; }

    /// TRUE only when this bridge has been proven end to end against a real
    /// game. The same honesty gate as EmulatorBackend.BridgeReady: an
    /// unfinished bridge is listed and explained, never silently offered.
    bool IsReady { get; }

    /// Where the player gets the emulator or helper program THEMSELVES.
    /// Shown to them; the launcher never fetches it.
    string HomepageUrl { get; }

    /// Every program the player has to install for this bridge to work.
    ///
    /// London creates Emulators\&lt;FolderName&gt;\ for each one at startup, with a
    /// note saying exactly what belongs there -- the same treatment the
    /// built-in backends get. The player drops their own copy in; we never
    /// fetch it. An empty list means the bridge needs nothing installed.
    IReadOnlyList<EmulatorRequirement> Emulators { get; }

    /// Human-readable reason this bridge cannot run right now, or null when it
    /// can. Checked before launch so the player learns "SNI is not running"
    /// rather than watching a game sit there sending nothing.
    string? GetUnmetRequirement();

    /// What London should actually start, resolved inside the folder the player
    /// filled. Null means "nothing to start" -- a bridge like SNI attaches to a
    /// program the player runs themselves, while a native port IS the program.
    ///
    /// The extension decides; London does not guess an executable name. That is
    /// the whole point of the player putting it in a named folder: we read where
    /// it is and run it from there.
    LaunchPlan? GetLaunchPlan(BridgeContext context, string emulatorsRoot);

    Task<bool> ConnectAsync(BridgeContext context, CancellationToken ct);
    Task<byte[]> ReadAsync(long address, int length, CancellationToken ct);
    Task WriteAsync(long address, byte[] data, CancellationToken ct);
    Task DisconnectAsync();
}

/// One program the player installs themselves, and where it goes.
///
/// FolderName becomes Emulators\&lt;FolderName&gt;\ — it is used to build a path, so
/// it must be a plain folder name. ExeName is what the note tells the player to
/// end up with sitting directly in that folder, so they can check their own work.
public sealed record EmulatorRequirement(
    string FolderName,
    string DisplayName,
    string HomepageUrl,
    string ExeName)
{
    /// A name that cannot escape the Emulators folder. Checked rather than
    /// trusted: an extension is somebody else's code.
    public bool IsSafeFolderName
        => FolderName.Length > 0
        && FolderName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
        && FolderName != "." && FolderName != ".."
        && !FolderName.Contains("..");

    /// Where this program lives once the player has followed the note, or null
    /// when they have not put it there yet. The folder is ours; the file in it
    /// is theirs.
    public string? Resolve(string emulatorsRoot)
    {
        if (!IsSafeFolderName) return null;
        string path = System.IO.Path.Combine(emulatorsRoot, FolderName, ExeName);
        return System.IO.File.Exists(path) ? path : null;
    }
}

/// What London should start, once the extension has resolved it.
///
/// WorkingDirectory matters: a native port and an emulator both look for their
/// own data beside their executable, so starting them from anywhere else finds
/// nothing.
///
/// Environment is how a bridge hands its own program a value it cannot pass on
/// the command line -- BizHawk's Lua connector reads AP_CONFIG_PATH with
/// os.getenv, because a path with spaces does not survive as an argument.
public sealed record LaunchPlan(
    string ExePath,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null);

/// What the launcher tells a bridge about the session it is joining.
///
/// RomPath is the file to actually load -- already patched for this seed when
/// the game does that, so a bridge never has to know about patching.
/// ScriptPath and ConfigPath are empty for bridges that do not use a script.
public sealed record BridgeContext(
    string GameId,
    string RomSystem,
    string RomPath,
    string EmulatorDirectory,
    string ScriptPath = "",
    string ConfigPath = "",
    bool   Fullscreen = false);
