using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace LauncherV2.Core.Plugins;

// Turns an approved folder on disk into a running IGamePlugin.
//
// By the time anything here executes, three things have already happened:
// the manifest was read and validated, the player saw who wrote this and said
// yes, and the folder hash matched what they approved. This is the first point
// where somebody else's code actually runs, and it is wrapped from the very
// first call.

// The load context stays internal: callers never need to touch it, they need
// "let go of this plugin", which is Unload() below.
public sealed class LoadedPlugin
{
    public   PluginManifest    Manifest  { get; }
    public   string            Directory { get; }
    public   SafePluginProxy   Plugin    { get; }
    internal PluginLoadContext Context   { get; }

    internal LoadedPlugin(PluginManifest manifest, string directory,
                          SafePluginProxy plugin, PluginLoadContext context)
    {
        Manifest = manifest; Directory = directory; Plugin = plugin; Context = context;
    }

    /// <summary>Detach and drop the context so the files can be replaced.</summary>
    public void Unload()
    {
        Plugin.Detach();
        try { Context.Unload(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[plugin] unload failed: " + ex.Message); }
    }
}

public static class PluginLoader
{
    /// <summary>
    /// Load one approved plugin folder. Returns null and fills
    /// <paramref name="error"/> rather than throwing — a broken plugin is a
    /// line in the UI, not a crash on startup.
    /// </summary>
    public static LoadedPlugin? Load(string directory, PluginManifest manifest, out string error)
    {
        error = "";
        string dll = Path.Combine(directory, manifest.Assembly);
        if (!File.Exists(dll)) { error = "missing " + manifest.Assembly; return null; }

        PluginLoadContext? ctx = null;
        try
        {
            ctx = new PluginLoadContext(dll, "plugin:" + manifest.GameId);
            Assembly asm = ctx.LoadFromAssemblyPath(Path.GetFullPath(dll));

            Type? type = asm.GetType(manifest.EntryType, throwOnError: false, ignoreCase: false);
            if (type == null)
            {
                // Naming the candidates turns "it doesn't work" into a fix.
                var candidates = asm.GetTypes()
                    .Where(t => typeof(IGamePlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                    .Select(t => t.FullName)
                    .Take(5).ToArray();
                error = $"entryType \"{manifest.EntryType}\" not found in {manifest.Assembly}"
                      + (candidates.Length > 0
                            ? ". Types implementing IGamePlugin: " + string.Join(", ", candidates)
                            : ". No type in it implements IGamePlugin.");
                Unload(ctx);
                return null;
            }

            if (!typeof(IGamePlugin).IsAssignableFrom(type))
            {
                error = $"{manifest.EntryType} does not implement IGamePlugin";
                Unload(ctx);
                return null;
            }

            object? instance = Activator.CreateInstance(type);
            if (instance is not IGamePlugin plugin)
            {
                error = $"{manifest.EntryType} could not be constructed — it needs a public parameterless constructor";
                Unload(ctx);
                return null;
            }

            var proxy = new SafePluginProxy(plugin, manifest.GameId, manifest.DisplayName);

            // A plugin whose GameId disagrees with its manifest would be
            // registered under one name and behave as another — and the manifest
            // is the half the player actually read.
            if (!string.Equals(proxy.GameId, manifest.GameId, StringComparison.OrdinalIgnoreCase))
            {
                error = $"the plugin reports GameId \"{proxy.GameId}\" but plugin.json says \"{manifest.GameId}\"";
                proxy.Detach();
                Unload(ctx);
                return null;
            }

            return new LoadedPlugin(manifest, directory, proxy, ctx);
        }
        catch (ReflectionTypeLoadException ex)
        {
            error = "could not load types: " +
                    string.Join("; ", ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Take(3));
            Unload(ctx);
            return null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Unload(ctx);
            return null;
        }
    }

    /// <summary>
    /// Every installed plugin that the player has approved and whose files
    /// still match. Anything else comes back in <paramref name="problems"/> so
    /// the UI can say why instead of silently showing fewer games.
    /// </summary>
    public static IReadOnlyList<LoadedPlugin> LoadApproved(out IReadOnlyList<string> problems)
    {
        var loaded = new List<LoadedPlugin>();
        var issues = new List<string>();

        foreach (var (dir, manifest) in PluginPackage.Installed())
        {
            var verdict = PluginTrustStore.Check(manifest.GameId, dir);
            if (verdict != PluginTrustStore.Verdict.Trusted)
            {
                issues.Add(PluginTrustStore.Explain(verdict, manifest.DisplayName));
                continue;
            }

            var lp = Load(dir, manifest, out string err);
            if (lp == null) issues.Add($"{manifest.DisplayName}: {err}");
            else            loaded.Add(lp);
        }

        problems = issues;
        return loaded;
    }

    private static void Unload(PluginLoadContext? ctx)
    {
        try { ctx?.Unload(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[plugin] unload failed: " + ex.Message); }
    }
}
