using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace LauncherV2.Core.Plugins;

// One collectible AssemblyLoadContext per plugin so it can be unloaded.
// Launcher assemblies resolve to the HOST's copy — a second copy would make
// IGamePlugin a different type and the cast would fail.

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
        return path == null ? null : LoadFromFileWithoutLock(path);
    }

    ///
    /// Load from bytes, never from the path: a path-loaded assembly keeps its
    /// file locked for the life of the process — Unload() only schedules the
    /// release — and updating an installed plugin then fails with
    /// "Access to the path is denied". Bytes cost a copy and free the file.
    ///
    public Assembly LoadFromFileWithoutLock(string path)
    {
        using var dll = new System.IO.MemoryStream(System.IO.File.ReadAllBytes(path));
        string pdbPath = System.IO.Path.ChangeExtension(path, ".pdb");
        if (System.IO.File.Exists(pdbPath))
        {
            using var pdb = new System.IO.MemoryStream(System.IO.File.ReadAllBytes(pdbPath));
            return LoadFromStream(dll, pdb);
        }
        return LoadFromStream(dll);
    }

    protected override IntPtr LoadUnmanagedDll(string name)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(name);
        return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
