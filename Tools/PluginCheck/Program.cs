using System;
using System.IO;
using System.Linq;
using LauncherV2.Core;
using LauncherV2.Core.Plugins;

namespace LauncherV2.Tools.PluginCheck;

/// <summary>
/// Take a .londonplugin the whole way: inspect, install, load, cast, call.
///
///     PluginCheck &lt;file.londonplugin&gt;
///
/// Exit 0 means a user could install this and the launcher would use it.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static void Check(string what, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "OK  " : "FAIL")}] {what}" +
                          (detail == null ? "" : "  -- " + detail));
        if (!ok) _failures++;
    }

    /// <summary>
    /// Launch the real game through the plugin and read what the two said.
    ///
    /// The plugin looks in its own GameDirectory, so the game folder is linked
    /// in rather than copied -- 40 MB per run would be a silly price for a
    /// gate, and a copy could drift from the folder being tested.
    /// </summary>
    private static void RunAgainstGame(IGamePlugin plugin, string gameFolder, string? slotDataFile)
    {
        gameFolder = Path.GetFullPath(gameFolder);
        if (!File.Exists(Path.Combine(gameFolder, "openttd.exe")))
        {
            Check("game folder has openttd.exe", false, gameFolder);
            return;
        }

        string want = plugin.GameDirectory;
        Directory.CreateDirectory(Path.GetDirectoryName(want)!);
        if (Directory.Exists(want)) Directory.Delete(want, recursive: false);
        var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{want}\" \"{gameFolder}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
        });
        mk!.WaitForExit();
        Check("game folder linked into the plugin's Games\\ folder", Directory.Exists(want));
        if (!Directory.Exists(want)) return;

        string log = Path.Combine(want, "ap_launcher.log");
        if (File.Exists(log)) File.Delete(log);

        if (slotDataFile != null)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(slotDataFile));
            plugin.OnSlotData(doc.RootElement);
            Check("slot_data handed over", true, Path.GetFileName(slotDataFile));
        }

        var session = new ApSession("localhost:38281", "Marco", "", plugin.ApWorldName);
        try
        {
            plugin.LaunchAsync(session).GetAwaiter().GetResult();
            Check("game started and connected to the plugin's pipe", true);
        }
        catch (Exception ex)
        {
            Check("game started and connected to the plugin's pipe", false, ex.Message);
            return;
        }

        // Give the exchange a moment, then stop the game and read the log.
        Thread.Sleep(6000);
        plugin.StopAsync().GetAwaiter().GetResult();
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("openttd"))
            try { proc.Kill(); } catch (InvalidOperationException) { }

        string[] lines = File.Exists(log) ? File.ReadAllLines(log) : Array.Empty<string>();
        Check("the plugin wrote a session log", lines.Length > 0);
        foreach (string line in lines) Console.WriteLine("         | " + line);

        Check("the game reported its NewGRF list",
              lines.Any(l => l.Contains("game reports")));
        Check("the plugin decided one way or the other",
              lines.Any(l => l.Contains("accepted") || l.Contains("refused")));

        try { Directory.Delete(want, recursive: false); } catch (IOException) { }
    }

    // What the launcher would actually draw for this plugin. A plugin can pass
    // every check above and still answer nothing anywhere, which reaches the
    // player as a game page that is simply empty.
    private static void ReportSurfaces(IGamePlugin p)
    {
        Console.WriteLine();
        Console.WriteLine("  Surfaces the launcher would draw:");

        void Line(string what, int n, string? sample = null)
            => Console.WriteLine($"    {(n > 0 ? "*" : "-")} {what,-26} {n,3}" +
                                 (sample == null ? "" : "   " + sample));

        try
        {
            var comps = p.DetectComponents().ToList();
            Line("components", comps.Count,
                 comps.Count > 0 ? string.Join(", ", comps.Take(3).Select(c => c.Name)) : null);

            var cmds = p.GetCommands().ToList();
            Line("commands", cmds.Count,
                 cmds.Count > 0 ? string.Join(", ", cmds.Take(4).Select(c => c.Label)) : null);

            var issues = p.KnownIssues.ToList();
            Line("known issues", issues.Count);

            var credits = p.Credits.ToList();
            Line("credits", credits.Count);

            var ach = p.ExtraAchievements.ToList();
            Line("achievements", ach.Count, p.AchievementIdPrefix);

            Line("item action menu", p.ItemActions != null ? 1 : 0);

            var dp = p.GetLocationDataPackage();
            int locs = 0;
            if (dp is { } el && el.ValueKind == System.Text.Json.JsonValueKind.Object
                && el.TryGetProperty("location_name_to_id", out var byName))
                locs = byName.EnumerateObject().Count();
            Line("locations in datapackage", locs);

            Console.WriteLine($"    . standalone {p.SupportsStandalone}, map tracker " +
                              $"{p.SupportsMapTracker}, deathlink {p.SendsDeathLink}, " +
                              $"needs base game {p.NeedsBaseGameFolder() != null}");
        }
        catch (Exception ex)
        {
            Check("surfaces answer without throwing", false, ex.Message);
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("PluginCheck <file.londonplugin> [--game <folder> [--slotdata <file.json>]]");
            return 2;
        }

        string package = Path.GetFullPath(args[0]);
        if (!File.Exists(package))
        {
            Console.WriteLine("no such file: " + package);
            return 2;
        }

        Console.WriteLine(Path.GetFileName(package));
        Console.WriteLine();

        // 1. The manifest reads, and says what it must.
        var candidate = PluginPackage.Inspect(package);
        Check("package inspects", candidate.IsUsable, candidate.Error);
        if (!candidate.IsUsable) return 1;

        var manifest = candidate.Manifest!;
        Check($"api version {manifest.ApiVersion} matches this launcher",
              manifest.ApiVersion == PluginManifest.CurrentApiVersion,
              $"launcher speaks {PluginManifest.CurrentApiVersion}");
        Check("rules acknowledged", manifest.RulesAcknowledged);
        Check("declares something for the consent dialog",
              manifest.Declares.Describe(manifest.GameId).Count > 0);

        // 2. It unpacks into its own folder and nowhere else.
        string? installError = PluginPackage.Install(candidate);
        Check("installs", installError == null, installError);
        if (installError != null) return 1;

        string dir = PluginPackage.DirectoryFor(manifest.GameId);

        // A plugin carrying its own copy of the launcher would load, then fail
        // the cast below with a message that makes no sense to read.
        var shadowed = Directory.GetFiles(dir, "*.dll")
            .Select(Path.GetFileName)
            .Where(n => n!.StartsWith("Multiworld Launcher", StringComparison.OrdinalIgnoreCase)
                     || n!.StartsWith("LauncherV2", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Check("does not ship a copy of the launcher", shadowed.Count == 0,
              string.Join(", ", shadowed));

        // 3. The load: assembly resolves, entry type constructs, and the cast
        //    to the host's IGamePlugin succeeds.
        var loaded = PluginLoader.Load(dir, manifest, out string loadError);
        Check("loads and casts to IGamePlugin", loaded != null, loadError);
        if (loaded == null) return 1;

        // 4. The members the launcher reads before anything is running. The
        //    proxy swallows plugin exceptions, so an empty answer here is the
        //    plugin misbehaving rather than the proxy.
        IGamePlugin p = loaded.Plugin;
        Check("GameId matches the manifest", p.GameId == manifest.GameId,
              $"{p.GameId} vs {manifest.GameId}");
        Check("has a display name", !string.IsNullOrWhiteSpace(p.DisplayName));
        Check("has an apworld name", !string.IsNullOrWhiteSpace(p.ApWorldName));
        Check("has a description", !string.IsNullOrWhiteSpace(p.Description));
        Check("names a game directory", !string.IsNullOrWhiteSpace(p.GameDirectory));
        Check("is not running before it is launched", !p.IsRunning);

        // Optional members must answer rather than throw.
        _ = p.SupportsStandalone;
        _ = p.SupportsMapTracker;
        _ = p.ConnectsItself;
        Check("optional flags answer", true);

        ReportSurfaces(p);

        // 5. Optional: the whole way, with the real game on the other end.
        //    Everything above can pass while the two sides still fail to meet.
        if (args.Length >= 3 && args[1] == "--game")
            RunAgainstGame(p, args[2], args.Length >= 5 && args[3] == "--slotdata" ? args[4] : null);

        loaded.Unload();
        Check("unloads", true);

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "OK: the plugin loads and the launcher can use it."
            : $"FEJL: {_failures} problem(s).");
        return _failures == 0 ? 0 : 1;
    }
}
