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
        var engine = TemplateEngine();
        if (engine is not { Usable: true })
        {
            MessageBox.Show(this,
                "No Archipelago engine is set up yet. Point London at one under "
                + "Multiworld first — the templates are written by Archipelago "
                + "itself, not by London.",
                "Update YAML forms", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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
    private void OfferApworldFixes()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var engine = TemplateEngine();
                if (engine is not { Usable: true }) return;

                var all = ApworldDoctor.Scan(engine.CustomWorldsDir);
                if (all.Count == 0) return;

                var answers = SettingsStore.Load().ApworldFixAnswers;
                var unanswered = all.Where(i => !answers.ContainsKey(i.Identity)).ToList();

                Dispatcher.BeginInvoke(() =>
                {
                    // Said out loud whether or not we ask: the player should be
                    // able to see the problem in the log even after declining.
                    foreach (var i in all)
                        AppendLog($"[AP worlds] {i.FileName}: {i.Problem} {i.Consequence}");
                    if (unanswered.Count == 0) return;

                    var (accepted, offered) = UI.Dialogs.ApworldFixDialog.Ask(this, unanswered);

                    var s = SettingsStore.Load();
                    foreach (var i in offered)
                        s.ApworldFixAnswers[i.Identity] =
                            accepted.Any(a => a.Identity == i.Identity);
                    SettingsStore.Save(s);

                    foreach (var i in accepted)
                    {
                        var r = ApworldDoctor.Apply(i);
                        AppendLog("[AP worlds] " + r.Note);
                    }
                    if (accepted.Count == 0)
                        AppendLog("[AP worlds] Left alone — you said no. Nothing was changed.");
                });
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
