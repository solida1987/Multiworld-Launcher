using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;
using LauncherV2.Core.Archipelago;

namespace LauncherV2.UI.Pages;

/// Keeping the Create YAML forms honest.
///
/// The option list every "Create YAML" form draws comes from a template that
/// Archipelago generated from the worlds it could load AT THAT MOMENT. That
/// makes templates derived data, and derived data goes stale in silence: a
/// stale template still draws a perfectly good-looking form, just of options
/// the world no longer has. A player then sends a host a YAML the generator
/// rejects, and nothing anywhere said why.
///
/// So two things happen here. The button throws the whole set away and has
/// them written again; and on start-up the launcher compares what the
/// templates were built from against what is installed now, and quietly does
/// the same when they no longer agree.
public partial class MainWindow
{
    /// The engine London generates with — the same lookup every other panel
    /// uses, so all of them mean the same install.
    private static ApEngine.Report? TemplateEngine()
    {
        try
        {
            var s = SettingsStore.Load();
            return ApEngine.Discover(string.IsNullOrWhiteSpace(s.ApEnginePath)
                                     ? null : s.ApEnginePath);
        }
        catch (Exception) { return null; }
    }

    private static string LauncherVersion => LauncherUpdater.CurrentVersion.ToString();

    private async void BtnYamlRefresh_Click(object sender, RoutedEventArgs e)
    {
        // The templates are written by Archipelago, not by London, so this
        // button is worthless without an install. Rather than refusing and
        // sending the player off to find a setting, ask them where it is.
        var engine = TemplateEngine();
        if (engine is not { Usable: true })
        {
            var located = UI.Dialogs.ApEngineFolderDialog.Ask(this,
                "The YAML forms are written by Archipelago itself, so London needs "
                + "to know which installation to ask.");
            if (located is not { Usable: true }) return;
            engine = located.Report;
        }

        // The engine before the templates: the forms are written BY
        // Archipelago from the worlds it can load, so a newer Archipelago
        // means different forms. Rewriting them first and updating second
        // would leave the player with the forms of the release they no
        // longer have.
        try
        {
            if (await UI.Dialogs.ApEngineUpdateDialog.OfferAsync(this, m => AppendLog(m)))
                engine = TemplateEngine() ?? engine;
        }
        catch (Exception ex) { AppendLog("[AP engine] Could not check for an update: " + ex.Message); }

        // Asked, not assumed: this deletes files, and it takes a while because
        // Archipelago loads every installed world to write them.
        var answer = MessageBox.Show(this,
            "Throw away every generated option template and have Archipelago "
            + "write them all again?\n\n"
            + "This is what the Create YAML forms are drawn from, so it is worth "
            + "doing after a game or the launcher has been updated. Your own "
            + "saved YAML files and the Presets folder are not touched.\n\n"
            + "It takes a minute or two — Archipelago loads every world it has.",
            "Update YAML forms", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        BtnYamlRefresh.IsEnabled = false;
        object? was = BtnYamlRefresh.Content;
        BtnYamlRefresh.Content = "Rewriting…";
        AppendLog("[YAML] Rewriting every option template — Archipelago is loading its worlds.");
        try
        {
            var r = await ApWorldProvisioner.RefreshTemplatesAsync(engine)
                                            .ConfigureAwait(true);
            if (r.Note != null)
            {
                AppendLog("[YAML] " + r.Note);
                MessageBox.Show(this, r.Note, "Update YAML forms",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                StampTemplates(engine);
                AppendLog($"[YAML] {r.Written} option templates rewritten "
                        + $"({r.Removed} removed first).");
                MessageBox.Show(this,
                    $"{r.Written} option templates rewritten.\n\n"
                    + "Create YAML now shows what each world actually offers.",
                    "Update YAML forms", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            BtnYamlRefresh.Content = was;
            BtnYamlRefresh.IsEnabled = true;
        }
    }

    ///
    /// Rewrite the templates when what they were built from has moved.
    ///
    /// Runs detached at start-up: it is slow, it is not what the player opened
    /// the launcher to do, and a first run on a machine that has never stamped
    /// would otherwise hold the window for a minute.
    ///
    /// ⚠ Never on the very first sighting. A fresh install has no stamp, and
    /// rewriting 400 templates before the player has done anything is a long
    /// unexplained wait. The stamp is simply recorded, and the first REAL
    /// change after that is what triggers a rewrite.
    ///
    private void EnsureTemplatesFresh()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var engine = TemplateEngine();
                if (engine is not { Usable: true, HasLauncher: true }) return;

                string now = ApWorldProvisioner.Fingerprint(engine, LauncherVersion);
                var s = SettingsStore.Load();
                if (string.IsNullOrEmpty(s.TemplatesStamp))
                {
                    s.TemplatesStamp = now;
                    SettingsStore.Save(s);
                    return;
                }
                if (string.Equals(s.TemplatesStamp, now, StringComparison.Ordinal)) return;

                Dispatcher.BeginInvoke(() => AppendLog(
                    "[YAML] The launcher or an AP world has changed — rewriting the "
                  + "option templates so Create YAML stays in step."));

                var r = await ApWorldProvisioner.RefreshTemplatesAsync(engine)
                                                .ConfigureAwait(false);
                if (r.Note == null)
                {
                    var save = SettingsStore.Load();
                    save.TemplatesStamp = now;
                    SettingsStore.Save(save);
                    Dispatcher.BeginInvoke(() => AppendLog(
                        $"[YAML] {r.Written} option templates rewritten."));
                }
                else
                {
                    // Leave the stamp alone: a rewrite that did not finish must
                    // be tried again, not remembered as done.
                    Dispatcher.BeginInvoke(() => AppendLog("[YAML] " + r.Note));
                }
            }
            catch (Exception) { /* forms still draw from whatever is there */ }
        });
    }

    ///
    /// Offer to fix a world London can see is broken — and offer only.
    ///
    /// ⚠ Somebody else's world is not ours to mend uninvited. London looks,
    /// says what is wrong and what it would do, and does nothing at all
    /// unless the player says yes. An answer is remembered against the exact
    /// file, so a no is honoured for that copy and a version the player
    /// installs later gets looked at again instead of inheriting it.
    ///
    /// Silent when nothing is wrong, which is the normal case.
    ///
    ///
    /// Ask where Archipelago is, once, when London genuinely cannot find it.
    ///
    /// Everything that needs the engine — updating worlds, rewriting the YAML
    /// forms, generating a seed — fails quietly without it, and the failure
    /// looks like a broken button rather than a missing folder. So London says
    /// so itself instead of waiting to be asked.
    ///
    /// Asked once and then remembered: a prompt that returns every launch is
    /// one the player closes without reading, and the buttons ask again at the
    /// moment the answer actually buys them something.
    ///
    private void OfferApEngineFolder()
    {
        try
        {
            var s = SettingsStore.Load();
            if (s.ApEngineAsked) return;
            if (ApEngineLocation.Current().Usable) return;

            // After the window is up: a modal on top of a half-drawn launcher
            // reads as a crash, not a question.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ApEngineLocation.Current().Usable) return;
                var s2 = SettingsStore.Load();
                s2.ApEngineAsked = true;
                SettingsStore.Save(s2);

                var w = UI.Dialogs.ApEngineFolderDialog.Ask(this,
                    "London could not find an Archipelago installation on this "
                    + "machine. Without one it cannot update your AP worlds, "
                    + "rewrite the YAML forms, or generate a seed.");
                AppendLog(w is { Usable: true }
                    ? $"[AP] Archipelago folder set to {w.Path}"
                    : "[AP] No Archipelago folder set — you can point at one under Settings.");
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (Exception) { /* a question we cannot ask is not worth crashing over */ }
    }

    private void OfferApworldFixes()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var engine = TemplateEngine();
                if (engine is not { Usable: true }) return;

                // The scan opens zips, so it runs here off the window; the
                // asking and the fixing happen on it, in the one shared flow.
                if (ApworldDoctor.Scan(engine.CustomWorldsDir).Count == 0) return;

                Dispatcher.BeginInvoke(() =>
                    UI.Dialogs.ApworldFixDialog.OfferNow(this, engine.CustomWorldsDir, m => AppendLog(m)));
            }
            catch (Exception) { /* a scan that fails changes nothing */ }
        });
    }

    /// Record what the templates now match, after a deliberate rewrite.
    private void StampTemplates(ApEngine.Report engine)
    {
        try
        {
            var s = SettingsStore.Load();
            s.TemplatesStamp = ApWorldProvisioner.Fingerprint(engine, LauncherVersion);
            SettingsStore.Save(s);
        }
        catch (Exception) { }
    }
}
