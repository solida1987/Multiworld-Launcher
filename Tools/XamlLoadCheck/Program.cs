using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace LauncherV2.Tools.XamlLoadCheck;

// Construct every Window and UserControl in the launcher assembly.
//
// The point is InitializeComponent: that is where XAML actually becomes
// objects, where a missing StaticResource throws, where a Setter for a
// property the target does not have throws, and where a template that
// references a name that is not there throws. None of it is reachable from a
// build.
//
// Anything needing constructor arguments is skipped and REPORTED as skipped —
// a silent skip would turn "we could not check this" into "this is fine",
// which is the failure mode this whole tool exists to prevent.
internal static class Program
{
    private sealed record Result(string Type, bool Ok, string? Why, bool Skipped);

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("XamlLoadCheck");
        Console.WriteLine(new string('-', 62));

        Assembly asm;
        try { asm = Assembly.Load("Multiworld Launcher"); }
        catch (Exception ex)
        {
            Console.WriteLine("could not load the launcher assembly: " + ex.Message);
            return 2;
        }

        // The launcher's OWN App must be the Application instance.
        //
        // App.xaml keeps its palette and its control templates inline, in
        // <Application.Resources>. Those only exist once the generated App
        // constructor has run InitializeComponent -- a bare Application, or a
        // ResourceDictionary pointed at App.xaml, gives neither, and then
        // every window fails on the first StaticResource for a reason that
        // has nothing to do with the window.
        //
        // Constructing App does NOT run OnStartup; that needs Run().
        Application app;
        try
        {
            Type? appType = asm.GetType("LauncherV2.App", throwOnError: false);
            if (appType == null)
            {
                Console.WriteLine("could not find LauncherV2.App");
                return 2;
            }
            app = (Application)Activator.CreateInstance(appType)!;

            // For a Window, the generated constructor calls
            // InitializeComponent. For an Application it does NOT -- WPF's
            // generated Main does it as a separate step. Miss this and the
            // app has zero resources, and every window then fails on its
            // first StaticResource for a reason that looks like the window's
            // fault and is not.
            appType.GetMethod("InitializeComponent",
                              BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.Instance)
                   ?.Invoke(app, null);

            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
        catch (Exception ex)
        {
            Console.WriteLine("could not construct the launcher's App:");
            Console.WriteLine("  " + Describe(ex));
            return 2;
        }

        Console.WriteLine($"App resources: {app.Resources.Count} key(s)");
        Console.WriteLine();

        var targets = asm.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition
                        && (typeof(Window).IsAssignableFrom(t)
                            || typeof(UserControl).IsAssignableFrom(t)))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        var results = new List<Result>();

        foreach (Type t in targets)
        {
            var ctor = t.GetConstructor(Type.EmptyTypes);
            if (ctor == null)
            {
                results.Add(new Result(t.Name, false, "no parameterless constructor", true));
                continue;
            }

            try
            {
                object instance = ctor.Invoke(null);
                // Windows are never shown; constructing them is what runs
                // InitializeComponent, and that is the whole test.
                if (instance is Window w) w.Close();
                results.Add(new Result(t.Name, true, null, false));
            }
            catch (TargetInvocationException ex)
            {
                results.Add(new Result(t.Name, false, Describe(ex.InnerException ?? ex), false));
            }
            catch (Exception ex)
            {
                results.Add(new Result(t.Name, false, Describe(ex), false));
            }
        }

        foreach (var r in results)
        {
            string mark = r.Skipped ? "skip" : r.Ok ? " ok " : "FAIL";
            Console.WriteLine($"  [{mark}]  {r.Type}");
            if (r.Why != null && !r.Ok)
                Console.WriteLine("            " + r.Why);
        }

        int failed  = results.Count(r => !r.Ok && !r.Skipped);
        int skipped = results.Count(r => r.Skipped);

        Console.WriteLine();
        Console.WriteLine($"{results.Count} type(s): {results.Count(r => r.Ok)} ok, "
                        + $"{failed} failed, {skipped} skipped");

        if (skipped > 0)
            Console.WriteLine("skipped types take constructor arguments — they were NOT checked");

        app.Shutdown();

        if (failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("FEJL: XAML der bygger groent men kaster ved konstruktion.");
            return 1;
        }
        Console.WriteLine("OK — hvert vindue og hver kontrol kan konstrueres.");
        return 0;
    }

    // The message alone is rarely enough: WPF wraps the real cause, and the
    // useful sentence is usually two levels down.
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e != null; e = e.InnerException)
            parts.Add(e.GetType().Name + ": " + e.Message.Replace("\r\n", " ").Replace("\n", " "));
        return string.Join("  <-  ", parts);
    }
}
