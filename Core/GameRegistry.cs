using System;
using System.Collections.Generic;
using System.Linq;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// GameRegistry — central list of installed game plugins.
//
// V2.0.0: plugins are compiled in and registered by App.xaml.cs at startup:
//     GameRegistry.Register(new DiabloII.D2Plugin { GameDirectory = ... });
//
// Future: Register can scan a Plugins/ directory and load assemblies via
//     reflection. The interface stays identical — GameRegistry never changes.
// ═══════════════════════════════════════════════════════════════════════════════

public static class GameRegistry
{
    private static readonly List<IGamePlugin> _plugins = new();

    // ── Registration ─────────────────────────────────────────────────────────

    /// Register a game plugin. Called once at startup per game.
    /// Throws if a plugin with the same GameId is already registered.
    public static void Register(IGamePlugin plugin)
    {
        if (_plugins.Any(p => p.GameId == plugin.GameId))
            throw new InvalidOperationException(
                $"A plugin with GameId '{plugin.GameId}' is already registered.");
        _plugins.Add(plugin);
    }

    // ── Lookup ────────────────────────────────────────────────────────────────

    /// All registered plugins in registration order.
    public static IReadOnlyList<IGamePlugin> All => _plugins;

    /// Find a plugin by its stable GameId. Returns null if not found.
    public static IGamePlugin? Find(string gameId)
        => _plugins.FirstOrDefault(p => string.Equals(p.GameId, gameId,
               StringComparison.OrdinalIgnoreCase));

    /// Currently running plugin (at most one game at a time in V2.0.0).
    public static IGamePlugin? ActivePlugin
        => _plugins.FirstOrDefault(p => p.IsRunning);
}
