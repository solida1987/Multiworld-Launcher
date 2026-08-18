using System;
using System.Windows;

namespace LauncherV2.Core.Extensions;

public sealed record ExtensionInstallResult(bool Added, string? Message);

/// Adding an emulator bridge, in the one order that is safe:
/// inspect without running anything, ask, install, reload the registry.
///
/// Deliberately the same shape as PluginInstallFlow. An extension runs in this
/// process with the launcher's own rights, so the player gets told what it is
/// and who wrote it before a single byte is unpacked.
public static class ExtensionInstallFlow
{
    public static ExtensionInstallResult AddFromFile(Window owner, string path)
    {
        var candidate = ExtensionPackage.Inspect(path);
        if (!candidate.IsUsable)
            return new ExtensionInstallResult(false,
                "That file could not be used as an extension:\n\n" + candidate.Error);

        var m = candidate.Manifest!;

        string body =
            $"{m.DisplayName}  {m.Version}\n"
          + $"by {m.Author}\n\n"
          + "What it does:\n"
          + string.Join("\n", ExtensionPackage.Describe(m)) + "\n\n"
          + $"Protocol: {m.Protocol}\n"
          + $"SHA-256:  {candidate.ShortHash}\n\n"
          + "An extension is a program. Once loaded it runs with the same rights "
          + "as the launcher itself. Only add extensions from people you have "
          + "reason to trust.\n\n"
          + "Add it?";

        if (MessageBox.Show(owner, body, "Add extension",
                            MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
            return new ExtensionInstallResult(false, null);   // declined: say nothing

        string? err = ExtensionPackage.Install(candidate);
        if (err != null)
            return new ExtensionInstallResult(false, "Could not install it:\n\n" + err);

        // Reload so the new bridge is usable now, and so its Emulators\ folders
        // and notes appear without a restart. Without this the player would add
        // an extension, see nothing happen, and reasonably assume it failed.
        var problems = BridgeRegistry.LoadInstalled();
        LauncherV2.Plugins.Emulated.EmulatorPlugin.EnsureEmulatorFolders();

        var bridge = BridgeRegistry.Find(m.Protocol);
        if (bridge is null)
            return new ExtensionInstallResult(false,
                "It installed but did not load:\n\n"
              + (problems.Count > 0 ? string.Join("\n", problems) : "unknown reason"));

        // Being honest at this exact moment matters: the extension IS added, but
        // it may still not be able to run anything, and the player should learn
        // that here rather than when a game silently sends no checks.
        string? unmet = bridge.GetUnmetRequirement();
        string note = unmet is null
            ? $"{m.DisplayName} is ready."
            : $"{m.DisplayName} was added, but is not ready yet:\n\n{unmet}";

        return new ExtensionInstallResult(true, note);
    }
}
