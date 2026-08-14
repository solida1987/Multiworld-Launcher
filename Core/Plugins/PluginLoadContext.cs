using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace LauncherV2.Core.Plugins;

// One load context per plugin, so a plugin can be unloaded again without
// restarting the launcher — the player drops a file in, changes their mind,
// removes it, and nothing lingers.
//
// The subtle part is what must NOT be loaded from the plugin's own folder.
// A plugin references the launcher to get IGamePlugin. If we let it load its
// own copy of that assembly, its IGamePlugin would be a different type from
// ours — same name, same shape, different identity — and every cast would fail
// with a message that makes no sense ("cannot convert IGamePlugin to
// IGamePlugin"). So anything the host already has must resolve to the host's
// copy, and only the plugin's private dependencies come from its folder.

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string mainAssemblyPath, string name)
        : base(name, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        // Already in the host? Use the host's — that is what unifies the
        // contract types. Returning null falls through to the default context.
        if (AssemblyLoadContext.Default.Assemblies
                .Any(a => string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase)))
            return null;

        string? path = _resolver.ResolveAssemblyToPath(name);
        return path == null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string name)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(name);
        return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
