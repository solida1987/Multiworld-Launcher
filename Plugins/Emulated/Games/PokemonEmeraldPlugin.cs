using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// PokemonEmeraldPlugin — Archipelago integration for Pokémon Emerald (GBA).
//
// STATUS: Bridge implemented end-to-end (patch apply + checks + items).
// ─────────────────────────────────────────────────────────────────────
// Launch chain (mostly inherited from EmulatorPlugin):
//   1. BizHawk is auto-installed from GitHub releases on first install.
//   2. The user supplies their own VANILLA ROM (never shipped, picked in
//      Settings; copied into the launcher library — §11).
//   3. PrepareSessionRomAsync applies the multiworld seed's .apemerald patch
//      to a library COPY (<rom>_<patch>.gba) — see AP PATCH below. The
//      original ROM is never modified. Vanilla fallback launches with a
//      loud "items cannot be delivered" note.
//   4. EmuHawk starts with the generic connector
//      Plugins/Scripts/bizhawk_ap_connector.lua, which loads the per-game
//      module Plugins/Scripts/games/pokemon_emerald.lua (LuaModuleName).
//   5. Connector ↔ launcher named pipe (byte mode, newline-framed):
//      CHECK:/GOAL/SYNC up, ITEM:(extended, with stream index)/SYNCEND down.
//
// AP PATCH (.apemerald)
// ──────────────────────
//   The generator's output is an APProcedurePatch ZIP (manifest +
//   base_patch.bsdiff4 + token_data.bin; see
//   Research_V2/POKEMON_EMERALD_BRIDGE_2026-06-12.md). The launcher applies
//   it by invoking a system Python 3 on Plugins/Scripts/apply_appatch.py —
//   bsdiff4 is bzip2-based and the .NET BCL has no bzip2, while every AP
//   player machine already runs Python (the script also carries a pure-
//   python bsdiff4 fallback, so no pip packages are required). The script
//   validates the manifest's base_checksum (MD5 of the vanilla ROM) before
//   touching anything.
//
//   Patch selection: explicit "Select AP patch…" pick in Settings wins;
//   otherwise the newest *.apemerald under %ProgramData%\Archipelago\output
//   (the standard AP generator output folder) is auto-offered at launch.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PokemonEmeraldPlugin : EmulatorPlugin
{
    public PokemonEmeraldPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "pokemon_emerald";
    public override string DisplayName => "Pokémon Emerald";
    public override string Subtitle    => "GBA · Emulated";
    public override string ApWorldName => "Pokemon Emerald";

    public override string Description =>
        "Pokémon Emerald is the definitive Hoenn adventure for the Game Boy Advance. " +
        "In the Archipelago randomizer, badges, HMs, key items, and wild encounters " +
        "feed the multiworld item pool. Become the Pokémon League Champion to " +
        "complete your goal.";

    // ── Visual identity ───────────────────────────────────────────────────────

    public override string ThemeAccentColor => "#1A8E5A";   // Hoenn emerald-green
    // GameBadges inherited: state-aware "ROM needed" that disappears once imported.

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBA";

    // §14: the emulators the GBA AP ecosystem trusts for this game. BizHawk is
    // the verified default; mGBA and Mesen are listed as the GBA alternatives
    // (shown "coming soon" until their bridge is verified in-emulator).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "pokemon_emerald";

    /// The vanilla Pokémon Emerald cartridge dump the randomizer is built for,
    /// identified by CONTENT (16 MB + MD5) — never by filename. The AP world
    /// validates exactly this MD5 (matches the patch base_checksum).
    protected override System.Collections.Generic.IReadOnlyList<RomIdentity> AcceptableBaseRoms
        => new[]
        {
            new RomIdentity(16 * 1024 * 1024,                    // 16,777,216 bytes
                            "605b89b67018abcea91e693a4dd25be3",
                            "Pokémon Emerald (USA, Europe)"),
        };

    /// pokemon_emerald.lua carries the full address map (mirrored from the
    /// official apworld client, see Research_V2/POKEMON_EMERALD_BRIDGE_…md)
    /// and self-disables on a non-AP ROM, so the generic "checks not
    /// implemented" launch warning no longer applies.
    public override bool ChecksImplemented => true;

    /// §15 upstream-update stamp: the Pokemon Emerald datapackage checksum of
    /// the AP 0.6.6 apworld this integration was derived from. The launcher
    /// warns at connect when the server announces a different checksum —
    /// regenerate the module (and this stamp) when the apworld updates.
    public string? BuiltAgainstDataPackageChecksum
        => "16a680f0579d0d34572cdf08c8beb442ec24824b";

    // ── AP patch settings ─────────────────────────────────────────────────────

    /// Explicit .apemerald chosen by the user (Settings). Null = auto-detect.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(System.Collections.Generic.IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el))
            ApPatchPath = el.GetString();
    }

    /// Standard AP generator output folder, searched for the newest patch.
    private static string ApOutputDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Archipelago", "output");

    /// All *.apemerald candidates under the AP output folder, newest first.
    private static List<FileInfo> EnumerateGeneratedPatches()
    {
        try
        {
            if (!Directory.Exists(ApOutputDirectory)) return new List<FileInfo>();
            return Directory
                .EnumerateFiles(ApOutputDirectory, "*.apemerald", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }
        catch { return new List<FileInfo>(); }
    }

    /// base_checksum (MD5 of the vanilla ROM the patch was built from) from a
    /// patch container's archipelago.json, or null.
    private static string? ReadPatchBaseChecksum(string patchPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(patchPath);
            var entry     = zip.GetEntry("archipelago.json");
            if (entry == null) return null;
            using var s   = entry.Open();
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty("base_checksum", out var bc)
                ? bc.GetString() : null;
        }
        catch { return null; }
    }

    /// ROM safety net: the resolved patch tells us the EXACT vanilla ROM it
    /// needs (base_checksum). Demand that specific file when our current ROM
    /// is missing or is the wrong version — so the launcher can ask the player
    /// for it by name + fingerprint instead of failing or silently patching
    /// the wrong dump.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        string? wantMd5 = null;
        var patch = ResolvePatchPath(out _);
        if (patch != null) wantMd5 = ReadPatchBaseChecksum(patch);

        bool haveRom = RomPath != null && File.Exists(RomPath);

        // No way to verify (no patch yet) → only flag a fully missing ROM.
        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("Pokémon Emerald", "GBA",
                       "Pokémon Emerald (USA, Europe)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        // Patch known → require exactly its base ROM.
        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement(
            "Pokémon Emerald", "GBA",
            "Pokémon Emerald (USA, Europe) — the original vanilla cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    /// player_name from a patch container's archipelago.json, or null.
    private static string? ReadPatchPlayerName(string patchPath)
    {
        try
        {
            using var zip   = System.IO.Compression.ZipFile.OpenRead(patchPath);
            var entry       = zip.GetEntry("archipelago.json");
            if (entry == null) return null;
            using var s     = entry.Open();
            using var doc   = JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty("player_name", out var pn)
                ? pn.GetString() : null;
        }
        catch { return null; }
    }

    /// Seed name out of a patch filename "AP_<seed>_P<slot>_<player>.apemerald":
    /// the run of characters after "AP_" up to the next underscore. "unknown"
    /// when the filename doesn't follow the convention.
    private static string ParseSeedFromPatch(string patchPath)
    {
        string name = Path.GetFileNameWithoutExtension(patchPath);
        const string prefix = "AP_";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string rest = name.Substring(prefix.Length);
            int us = rest.IndexOf('_');
            string seed = us >= 0 ? rest.Substring(0, us) : rest;
            if (seed.Length > 0) return seed;
        }
        return "unknown";
    }

    /// Record a freshly produced (or reused) patched ROM in the seed library so
    /// the ROMs tab can list it. Never throws.
    private void RegisterSeed(string outRom, string patch)
    {
        try
        {
            SeedLibraryStore.Instance.Register(new SeedEntry
            {
                GameId         = GameId,
                PatchedRomPath = outRom,
                PatchPath      = patch,
                SeedName       = ParseSeedFromPatch(patch),
                SlotName       = CurrentSlotName ?? "",
                CreatedUtc     = DateTimeOffset.UtcNow,
            });
        }
        catch { /* registry is non-essential to launching */ }
    }

    /// The patch that would be used right now, best match first:
    ///   1. explicit user pick (Settings)
    ///   2. filename matches the CONNECTED room's seed (AP_<seed>_…) AND the
    ///      container's player_name matches our slot — the patch generated
    ///      for exactly this multiworld and player
    ///   3. container player_name matches our slot (newest such)
    ///   4. newest generated patch (legacy behavior)
    /// Sets <paramref name="how"/> to a short, player-readable explanation.
    private string? ResolvePatchPath(out string how)
    {
        if (!string.IsNullOrEmpty(ApPatchPath) && File.Exists(ApPatchPath))
        {
            how = "picked in Settings";
            return ApPatchPath;
        }

        var candidates = EnumerateGeneratedPatches();
        string? seed = GetSeedName?.Invoke();
        string? slot = CurrentSlotName;

        if (!string.IsNullOrEmpty(seed))
        {
            var seedMatches = candidates.Where(f => f.Name.StartsWith(
                $"AP_{seed}", StringComparison.OrdinalIgnoreCase)).ToList();
            if (seedMatches.Count > 0)
            {
                var exact = seedMatches.FirstOrDefault(f =>
                    string.Equals(ReadPatchPlayerName(f.FullName), slot,
                                  StringComparison.OrdinalIgnoreCase))
                    ?? seedMatches[0];
                how = "matched this room's seed";
                return exact.FullName;
            }
        }

        if (!string.IsNullOrEmpty(slot))
        {
            var slotMatch = candidates.FirstOrDefault(f =>
                string.Equals(ReadPatchPlayerName(f.FullName), slot,
                              StringComparison.OrdinalIgnoreCase));
            if (slotMatch != null)
            {
                how = $"matched your slot name '{slot}'";
                return slotMatch.FullName;
            }
        }

        how = "newest generated patch";
        return candidates.FirstOrDefault()?.FullName;
    }

    /// Backwards-compatible overload for call sites that don't need the reason.
    private string? ResolvePatchPath() => ResolvePatchPath(out _);

    /// Set the explicit AP patch (used by drag-and-drop / room-link import) and
    /// persist it through the existing settings file. The patch is resolved at
    /// launch exactly like a "Select AP patch…" Settings pick — it always wins.
    public void SetExplicitPatch(string path)
    {
        ApPatchPath = path;
        SaveSettings();
    }

    // ── Session ROM: apply the .apemerald to a library copy ──────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = ResolvePatchPath(out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Pokémon Emerald] No AP patch (.apemerald) found — launching the " +
                "vanilla ROM. Checks and ITEM DELIVERY CANNOT WORK on a vanilla ROM: " +
                "generate the multiworld, then pick the patch in Settings → Pokémon " +
                "Emerald (or drop it under " + ApOutputDirectory + ").";
            return null;
        }

        // Output: a sibling library copy named after rom + patch, so a new
        // seed (new patch file) gets its own ROM and re-launching reuses it.
        string outRom = Path.Combine(
            RomLibraryDirectory,
            $"{Path.GetFileNameWithoutExtension(RomPath!)}_{Path.GetFileNameWithoutExtension(patch)}.gba");

        if (File.Exists(outRom) &&
            File.GetLastWriteTimeUtc(outRom) >= File.GetLastWriteTimeUtc(patch))
        {
            SessionRomNote = $"[Pokémon Emerald] Using existing AP-patched ROM: {outRom} " +
                             $"(patch {Path.GetFileName(patch)}, {how}).";
            RegisterSeed(outRom, patch);
            return outRom;
        }

        var python = FindPython();
        if (python == null)
        {
            SessionRomNote =
                "[Pokémon Emerald] Cannot apply the AP patch: no Python 3 found on " +
                "this machine (looked for 'py', 'python' and a user install). " +
                "Install Python 3, then relaunch. Launching the vanilla ROM — " +
                "items cannot be delivered.";
            return null;
        }

        string script = Path.Combine(AppContext.BaseDirectory, "Plugins", "Scripts", "apply_appatch.py");
        string apLib  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Archipelago", "lib");

        var psi = new ProcessStartInfo
        {
            FileName               = python.Value.File,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        foreach (string pre in python.Value.PreArgs) psi.ArgumentList.Add(pre);
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(patch);
        psi.ArgumentList.Add(RomPath!);
        psi.ArgumentList.Add(outRom);
        if (Directory.Exists(apLib)) psi.ArgumentList.Add(apLib);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start python");
        string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        string stderr = await proc.StandardError.ReadToEndAsync(ct);
        using (var patchCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            patchCts.CancelAfter(TimeSpan.FromSeconds(120));
            try { await proc.WaitForExitAsync(patchCts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("AP patch apply timed out after 120 s");
            }
        }

        string? error = null;
        try
        {
            using var doc = JsonDocument.Parse(stdout.Trim());
            var root = doc.RootElement;
            if (root.GetProperty("ok").GetBoolean())
            {
                SessionRomNote =
                    $"[Pokémon Emerald] AP patch applied: {Path.GetFileName(patch)} → {outRom} " +
                    $"({how}; bsdiff4: {root.GetProperty("bsdiff4").GetString()}, " +
                    $"md5 {root.GetProperty("md5").GetString()}).";
                RegisterSeed(outRom, patch);
                return outRom;
            }
            error = root.GetProperty("error").GetString();
        }
        catch
        {
            error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            if (error.Length > 400) error = error[..400];
        }

        SessionRomNote =
            $"[Pokémon Emerald] AP patch apply FAILED: {error}. Launching the " +
            "vanilla ROM — items cannot be delivered. (A 'checksum mismatch' " +
            "means your ROM is not the vanilla US/EU Emerald dump the patch " +
            "was generated against.)";
        return null;
    }

    // ── Python discovery ──────────────────────────────────────────────────────

    private static (string File, string[] PreArgs)? _pythonCache;

    /// Locate a runnable Python 3: the 'py' launcher (needs a "-3" prefix
    /// argument), plain 'python' on PATH, then the per-user CPython install
    /// directory. Result cached for the process lifetime.
    private static (string File, string[] PreArgs)? FindPython()
    {
        if (_pythonCache != null) return _pythonCache;

        if (ProbePython("py", "-3 --version"))
            return _pythonCache = ("py", new[] { "-3" });
        if (ProbePython("python", "--version"))
            return _pythonCache = ("python", Array.Empty<string>());

        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python");
            if (Directory.Exists(root))
            {
                string? exe = Directory.GetDirectories(root, "Python3*")
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "python.exe"))
                    .FirstOrDefault(File.Exists);
                if (exe != null && ProbePython(exe, "--version"))
                    return _pythonCache = (exe, Array.Empty<string>());
            }
        }
        catch { /* probing only */ }
        return null;
    }

    private static bool ProbePython(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── Settings panel: base panel + AP patch section ─────────────────────────

    public override UIElement? CreateSettingsPanel()
    {
        var basePanel = base.CreateSettingsPanel();
        if (basePanel is not StackPanel panel) return basePanel;

        var muted = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg    = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));

        panel.Children.Add(new TextBlock
        {
            Text = "ARCHIPELAGO PATCH", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 20, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "The multiworld generator produces a .apemerald patch for your slot. " +
                   "The launcher applies it to a COPY of your ROM at launch (your file " +
                   "is never modified). Leave empty to auto-use the newest patch from " +
                   "the Archipelago output folder.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var patchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var patchBox = new TextBox
        {
            Text = ApPatchPath ?? "",
            Margin = new Thickness(0, 0, 8, 0),
            IsReadOnly = true,
            FontSize = 12,
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var clearBtn = new Button
        {
            Content = "Auto",
            Width   = 50,
            Margin  = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            ToolTip = "Clear the explicit pick — auto-detect the newest generated patch.",
        };
        var pickBtn = new Button
        {
            Content = "Select AP patch...",
            Width   = 130,
            Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };

        var autoNote = new TextBlock
        {
            FontSize = 10, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        void RefreshAutoNote()
        {
            string? auto = ResolvePatchPath(out string how);
            autoNote.Text = ApPatchPath != null
                ? "Explicit patch selected — auto-detection off."
                : auto != null
                    ? $"Auto-detected ({how}): {auto}"
                    : $"No .apemerald found under {ApOutputDirectory} yet.";
        }
        RefreshAutoNote();

        pickBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Archipelago patch for Pokémon Emerald",
                Filter = "AP Emerald patch (*.apemerald)|*.apemerald|All files (*.*)|*.*",
            };
            if (Directory.Exists(ApOutputDirectory)) dlg.InitialDirectory = ApOutputDirectory;
            if (dlg.ShowDialog() != true) return;
            ApPatchPath   = dlg.FileName;
            patchBox.Text = dlg.FileName;
            SaveSettings();
            RefreshAutoNote();
        };
        clearBtn.Click += (_, _) =>
        {
            ApPatchPath   = null;
            patchBox.Text = "";
            SaveSettings();
            RefreshAutoNote();
        };

        DockPanel.SetDock(pickBtn,  Dock.Right);
        DockPanel.SetDock(clearBtn, Dock.Right);
        patchRow.Children.Add(pickBtn);
        patchRow.Children.Add(clearBtn);
        patchRow.Children.Add(patchBox);
        panel.Children.Add(patchRow);
        panel.Children.Add(autoNote);

        // "Import patch file…" — copies the patch into the launcher library and
        // sets it as the explicit pick, the same zero-friction path as dropping
        // a file onto the window. Routes through MainWindow.ImportPatchFile so
        // there is one storage/routing implementation.
        var importBtn = new Button
        {
            Content = "Import patch file...",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin  = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            ToolTip = "Pick an .apemerald patch — it is copied into the launcher " +
                      "library and selected automatically (your original file is " +
                      "never modified).",
        };
        importBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Import Archipelago patch",
                Filter = "AP patch (*.ap*)|*.ap*|All files (*.*)|*.*",
            };
            if (Directory.Exists(ApOutputDirectory)) dlg.InitialDirectory = ApOutputDirectory;
            if (dlg.ShowDialog() != true) return;

            // The launcher owns drop/import storage + routing + UI pre-fill.
            if (System.Windows.Application.Current?.MainWindow
                    is LauncherV2.UI.Pages.MainWindow mw)
            {
                mw.ImportPatchFile(dlg.FileName);
                patchBox.Text = ApPatchPath ?? "";   // reflect what the import set
                RefreshAutoNote();
            }
        };
        panel.Children.Add(importBtn);

        return panel;
    }
}
