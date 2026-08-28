using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace LauncherV2.Core.Extensions;

public sealed class LoadedExtension
{
    public ExtensionManifest Manifest { get; }
    public IEmulatorBridge   Bridge   { get; }
    public string            Directory { get; }

    internal LoadedExtension(ExtensionManifest m, IEmulatorBridge b, string dir)
        => (Manifest, Bridge, Directory) = (m, b, dir);
}

/// Which emulator bridges are installed, and which protocol each one speaks.
///
/// The point of this class is the NEGATIVE answer. A game manifest names the
/// protocol its Archipelago world talks -- "bizhawk" for GBA worlds, "sni" for
/// every SNES world. When nothing installed speaks that protocol, the launcher
/// has to say so BEFORE starting an emulator, because the failure otherwise has
/// no symptom: the game runs, the player plays, and no check is ever sent.
public static class BridgeRegistry
{
    /// Deliberately not "Extensions" next to the exe alone -- installed
    /// launchers keep player-installed things beside the player's data.
    public static string Directory
        => Path.Combine(AppContext.BaseDirectory, "Extensions");

    private static readonly Dictionary<string, LoadedExtension> ByProtocol =
        new(StringComparer.OrdinalIgnoreCase);

    /// Protocols the launcher can serve without any extension installed.
    ///
    /// EMPTY on purpose. BizHawk used to live here, and moving it out is the
    /// whole point: London knows no emulator of its own, so every one of them
    /// arrives the same way and can be replaced, updated or added without a new
    /// launcher build. The BizHawk extension simply ships pre-installed so a
    /// fresh copy works out of the box.
    public static readonly IReadOnlySet<string> BuiltIn =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<LoadedExtension> Installed => ByProtocol.Values;

    /// True only when a game on this protocol could actually start RIGHT NOW.
    ///
    /// IsReady alone is not enough: a finished bridge whose program the player
    /// has not put in place yet is not servable either. Checking only IsReady
    /// once had this reporting "yes" for Ship of Harkinian while its own
    /// GetUnmetRequirement said soh.exe was missing -- the launcher would have
    /// gone ahead and started nothing.
    public static bool CanServe(string protocol)
        => BuiltIn.Contains(protocol)
        || (ByProtocol.TryGetValue(protocol, out var e)
            && e.Bridge.IsReady
            && e.Bridge.GetUnmetRequirement() is null);

    /// A FINISHED bridge for this protocol is installed -- the question a
    /// readiness badge should ask, and deliberately weaker than CanServe.
    ///
    /// CanServe also demands the bridge could connect this instant. For an
    /// attach-style bridge (PINE) that means PCSX2 is already running with a
    /// game loaded, which before the player presses Play is never true -- so
    /// asking CanServe on the game page would paint every PS2 game "not ready"
    /// forever. Asking nothing at all was the other failure, and the one we
    /// actually shipped: pcsx2-qt.exe sat in Emulators\ with no PINE extension
    /// installed anywhere, and the page said READY TO PLAY for a game that had
    /// nothing to start it with. Pressing Play did nothing, silently.
    public static bool BridgeInstalled(string protocol)
        => BuiltIn.Contains(protocol)
        || (ByProtocol.TryGetValue(protocol, out var e) && e.Bridge.IsReady);

    public static IEmulatorBridge? Find(string protocol)
        => ByProtocol.TryGetValue(protocol, out var e) ? e.Bridge : null;

    /// The installable declaration for Emulators\&lt;folderName&gt;\, if some
    /// installed bridge has made one.
    ///
    /// Looked up by FOLDER rather than by protocol, because that is the thing
    /// the settings page and the built-in backend table have in common with a
    /// bridge's requirement -- both of them are ultimately talking about one
    /// directory under Emulators\. Returns null when no bridge offers that
    /// folder, or when its declaration is incomplete, and null simply means the
    /// player is told to install it themselves.
    public static EmulatorRequirement? OfferFor(string folderName)
    {
        foreach (var ext in ByProtocol.Values)
            foreach (var need in ext.Bridge.Emulators)
                if (need.CanOfferInstall
                    && string.Equals(need.FolderName, folderName,
                                     StringComparison.OrdinalIgnoreCase))
                    return need;
        return null;
    }

    /// Why this game cannot run, in words a player can act on. Null = it can.
    public static string? ExplainMissing(string protocol, string gameName)
    {
        if (BuiltIn.Contains(protocol)) return null;

        if (!ByProtocol.TryGetValue(protocol, out var ext))
            return $"{gameName} talks to Archipelago over \"{protocol}\", and no "
                 + "extension for that is installed.\n\n"
                 + "Add the matching bridge extension, then start the game "
                 + "again. Without it the game would run but never send a "
                 + "single check.";

        // Two different situations, and telling them apart matters to the
        // player: "the extension is unfinished" is our problem, "your copy is
        // not in place" is a thing they can fix in a minute.
        string? unmet = ext.Bridge.GetUnmetRequirement();
        if (unmet != null)
            return ext.Bridge.IsReady
                ? unmet
                : $"{ext.Manifest.DisplayName} is installed but not finished:\n\n"
                  + unmet;

        if (!ext.Bridge.IsReady)
            return $"{ext.Manifest.DisplayName} is installed but its bridge is "
                 + "not finished yet, so it cannot carry checks for "
                 + $"{gameName}.";

        return null;
    }

    /// Load every extension in Directory. Returns what went wrong, per folder;
    /// a bad extension is a normal event, not an exception.
    public static IReadOnlyList<string> LoadInstalled()
    {
        var problems = new List<string>();
        ByProtocol.Clear();

        if (!System.IO.Directory.Exists(Directory)) return problems;

        foreach (string dir in System.IO.Directory.GetDirectories(Directory))
        {
            string manifestPath = Path.Combine(dir, ExtensionManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                problems.Add($"{Path.GetFileName(dir)}: no {ExtensionManifest.FileName}");
                continue;
            }

            var manifest = ExtensionManifest.Parse(
                File.ReadAllText(manifestPath), out string error);
            if (manifest is null)
            {
                problems.Add($"{Path.GetFileName(dir)}: {error}");
                continue;
            }

            if (BuiltIn.Contains(manifest.Protocol))
            {
                // Refusing this is the point: an extension that claimed
                // "bizhawk" would quietly displace the bridge we have proven.
                problems.Add($"{manifest.ExtensionId}: protocol "
                           + $"\"{manifest.Protocol}\" is built into the "
                           + "launcher and cannot be replaced by an extension");
                continue;
            }

            if (ByProtocol.TryGetValue(manifest.Protocol, out var already))
            {
                problems.Add($"{manifest.ExtensionId}: protocol "
                           + $"\"{manifest.Protocol}\" is already served by "
                           + $"{already.Manifest.ExtensionId}");
                continue;
            }

            try
            {
                string dll = Path.Combine(dir, manifest.Assembly);
                var ctx = new AssemblyLoadContext(manifest.ExtensionId, isCollectible: false);
                ctx.Resolving += (c, name) =>
                {
                    string side = Path.Combine(dir, name.Name + ".dll");
                    return File.Exists(side) ? c.LoadFromAssemblyPath(side) : null;
                };

                Assembly asm = ctx.LoadFromAssemblyPath(Path.GetFullPath(dll));
                Type? type = asm.GetType(manifest.EntryType);
                if (type is null)
                {
                    problems.Add($"{manifest.ExtensionId}: no type "
                               + $"{manifest.EntryType} in {manifest.Assembly}");
                    continue;
                }

                if (Activator.CreateInstance(type) is not IEmulatorBridge bridge)
                {
                    problems.Add($"{manifest.ExtensionId}: {manifest.EntryType} "
                               + "does not implement IEmulatorBridge");
                    continue;
                }

                // The manifest is what the consent dialog showed the player, so
                // the code must not be allowed to claim something else.
                if (!string.Equals(bridge.Protocol, manifest.Protocol,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{manifest.ExtensionId}: manifest says protocol "
                               + $"\"{manifest.Protocol}\" but the code says "
                               + $"\"{bridge.Protocol}\"");
                    continue;
                }

                ByProtocol[manifest.Protocol] = new LoadedExtension(manifest, bridge, dir);
            }
            catch (Exception ex)
            {
                problems.Add($"{manifest.ExtensionId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return problems;
    }
}
