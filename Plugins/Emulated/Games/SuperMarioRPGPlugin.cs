using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SuperMarioRPGPlugin — Archipelago integration for Super Mario RPG: Legend of the
// Seven Stars (SNES), on the proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The 246-location
// table in smrpg.lua was GENERATED from the apworld
// TheRealSolidusSnake/SMRPG_apworld (Rom.location_data ⋈ Locations.location_table,
// main branch); the addresses, bitmasks, set-when-checked polarity, the
// check_if_items_sendable gate and the victory-music goal are mirrored from
// Client.py + Rom.py, and mock-verified through MoonSharp against synthetic
// memory. items_handling = 0b101 — this game DOES receive remote multiworld items
// (+ starting inventory) but NOT its own locally-found items. The client applies
// remote items by category (free-slot inventory scans, the Alto→Tenor→Soprano
// card progression, coin/flower/frog-coin accumulation, party recovery), each
// racing the game's own RAM writes; that delivery path is the documented deferred
// piece (smrpg.lua receive_item buffers but never writes) until it is confirmed
// in-emulator, rather than shipped unverified and risking save corruption.
//
// AP PATCH (.apsmrpg → .sfc): an APDeltaPatch ZIP (the same container family as
// A Link to the Past's .aplttp / Super Metroid's .apsm), applied via the shared
// SnesApPatchHelper (resolve the seed's patch, validate the base ROM MD5, run
// apply_appatch.py on a library COPY — the original is never modified). Base ROM:
// the user's own Super Mario RPG (USA 1.0) cartridge dump (4 MB, SA-1), identified
// by CONTENT (size + MD5) — never by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SuperMarioRPGPlugin : EmulatorPlugin
{
    public SuperMarioRPGPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "smrpg";
    public override string DisplayName => "Super Mario RPG";
    public override string Subtitle    => "SNES · Emulated";

    // Exact AP world `game` string (SMRPGClient.game / SMRPGWorld.game).
    public override string ApWorldName => "Super Mario RPG Legend of the Seven Stars";

    public override string Description =>
        "Super Mario RPG: Legend of the Seven Stars is the SNES Square × Nintendo " +
        "classic that turned the Mushroom Kingdom into a turn-based RPG. The " +
        "Archipelago randomizer shuffles every treasure chest, boss reward, and " +
        "key item across the multiworld pool. Repair the Star Road by defeating " +
        "Smithy to complete your goal.";

    public override string ThemeAccentColor => "#E03020";   // Mario red

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "SNES";
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "smrpg";

    // §14: the SNES emulators this world is played on. BizHawk is verified; snes9x
    // and Mesen are the alternatives (shown per their bridge state in the
    // dropdown — "coming soon" until each bridge is confirmed in-emulator).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "snes9x", "mesen" };

    /// smrpg.lua reports checks + goal (table generated from the apworld; mock-
    /// verified). Remote-item delivery is the documented deferred piece.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (4 MB + MD5) — never by filename. This is the apworld's own pinned
    /// md5_hash (Rom.md5_hash) for the NA (1.0) release; apply_appatch.py
    /// independently re-checks the patch container's base_checksum before
    /// patching, so this only drives the launcher's "wrong version" hint.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(4 * 1024 * 1024,                          // 4,194,304 bytes
                        "d0b68d68d9efc0558242f5476d1c5b81",
                        "Super Mario RPG: Legend of the Seven Stars (USA)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt  = ".apsmrpg";
    private const string ResultExt = ".sfc";

    /// Explicit .apsmrpg chosen by the user (Settings / drag-drop). Null = auto.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el)) ApPatchPath = el.GetString();
    }

    private string? ResolvePatchPath(out string how)
        => SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out how);

    /// Set the explicit AP patch (drag-and-drop / room-link import) and persist it.
    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    /// ROM safety net: when a patch is known, demand exactly its base ROM
    /// (base_checksum) so the launcher can ask the player by fingerprint instead
    /// of silently patching the wrong dump.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        string? wantMd5 = null;
        var patch = ResolvePatchPath(out _);
        if (patch != null) wantMd5 = SnesApPatchHelper.ReadManifestField(patch, "base_checksum");

        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("Super Mario RPG", "SNES",
                       "Super Mario RPG: Legend of the Seven Stars (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement(
            "Super Mario RPG", "SNES",
            "Super Mario RPG: Legend of the Seven Stars (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apsmrpg to a library copy ─────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = ResolvePatchPath(out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Super Mario RPG] No AP patch (.apsmrpg) found — launching the " +
                "vanilla ROM. Checks and ITEM DELIVERY CANNOT WORK on a vanilla ROM: " +
                "generate the multiworld, then pick the patch in Settings → Super " +
                "Mario RPG (or drop it under " + SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        if (RomPath == null || !File.Exists(RomPath))
        {
            SessionRomNote =
                "[Super Mario RPG] No base ROM set — import your Super Mario RPG " +
                "(USA 1.0) cartridge dump in Settings, then relaunch.";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Super Mario RPG", patch, how, RomPath!, RomLibraryDirectory, ResultExt, ct);
        SessionRomNote = res.Note;
        if (res.OutRom != null) RegisterSeed(res.OutRom, patch);
        return res.OutRom;
    }

    /// Record a produced/reused patched ROM in the seed library. Never throws.
    private void RegisterSeed(string outRom, string patch)
    {
        try
        {
            SeedLibraryStore.Instance.Register(new SeedEntry
            {
                GameId         = GameId,
                PatchedRomPath = outRom,
                PatchPath      = patch,
                SeedName       = SnesApPatchHelper.ParseSeedFromPatch(patch),
                SlotName       = CurrentSlotName ?? "",
                CreatedUtc     = DateTimeOffset.UtcNow,
            });
        }
        catch { /* registry is non-essential to launching */ }
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
            Text = "The multiworld generator produces a .apsmrpg patch for your slot. " +
                   "The launcher applies it to a COPY of your ROM at launch (your file " +
                   "is never modified). Leave empty to auto-use the newest patch from " +
                   "the Archipelago output folder.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var patchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var patchBox = new TextBox
        {
            Text = ApPatchPath ?? "", Margin = new Thickness(0, 0, 8, 0),
            IsReadOnly = true, FontSize = 12,
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var clearBtn = new Button
        {
            Content = "Auto", Width = 50, Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            ToolTip = "Clear the explicit pick — auto-detect the newest generated patch.",
        };
        var pickBtn = new Button
        {
            Content = "Select AP patch...", Width = 130, Padding = new Thickness(0, 6, 0, 6),
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
                    : $"No .apsmrpg found under {SnesApPatchHelper.ApOutputDirectory} yet.";
        }
        RefreshAutoNote();

        pickBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Archipelago patch for Super Mario RPG",
                Filter = "AP SMRPG patch (*.apsmrpg)|*.apsmrpg|All files (*.*)|*.*",
            };
            if (Directory.Exists(SnesApPatchHelper.ApOutputDirectory))
                dlg.InitialDirectory = SnesApPatchHelper.ApOutputDirectory;
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

        return panel;
    }
}
