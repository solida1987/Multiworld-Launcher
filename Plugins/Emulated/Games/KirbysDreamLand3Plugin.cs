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
// KirbysDreamLand3Plugin — Archipelago integration for Kirby's Dream Land 3 (SNES),
// on the proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The consumable
// (44) and star (767) location tables in kdl3.lua were GENERATED from
// worlds/kdl3/client_addrs.py (consumable_addrs / star_addrs); stages, heart
// stars and bosses are computed exactly as worlds/kdl3/client.py game_watcher
// does, and the four-way goal mirrors the client's per-save status test. All
// reads are SRAM_1 → BizHawk "CARTRAM". items_handling = 0b101 — the PATCHED
// GAME grants its own locally-found items (plus a remote start inventory), so a
// SOLO seed plays fully and every check is reported in a multiworld. Delivering
// REMOTE multiworld items is the client's in-game item-queue splice; it is
// deferred (kdl3.lua receive_item is a no-op) until it can be confirmed
// in-emulator, rather than shipped unverified.
//
// THE PATCH (.apkdl3) is an APProcedurePatch ZIP (bsdiff4 + tokens + post-patch),
// the same family as A Link to the Past's .aplttp — handled by the shared
// SnesApPatchHelper + Plugins/Scripts/apply_appatch.py. PrepareSessionRomAsync
// applies the seed's patch to a library COPY (<rom>_<patch>.sfc); the original
// ROM is never modified, and the vanilla fallback launches with a loud note.
//
// Base ROM: the user's own Kirby's Dream Land 3 (USA) cartridge dump
// (1,572,864-byte headerless .sfc), identified by CONTENT (size + MD5) — never
// by filename (§11). The Japanese dump is also accepted (the apworld validates
// both). apply_appatch.py independently re-checks the patch container's own
// base_checksum before patching.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class KirbysDreamLand3Plugin : EmulatorPlugin
{
    public KirbysDreamLand3Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "kdl3";
    public override string DisplayName => "Kirby's Dream Land 3";
    public override string Subtitle    => "SNES · Emulated";
    public override string ApWorldName => "Kirby's Dream Land 3";

    public override string Description =>
        "Kirby's Dream Land 3 is the SNES platformer where Kirby and Gooey team up " +
        "with a roster of animal friends to drive Dark Matter out of Dream Land. " +
        "The Archipelago randomizer shuffles stage clears, heart stars, and " +
        "optional consumables and stars into the multiworld pool. Reach your " +
        "chosen goal — purify Zero, clear the Boss Butch, ace the MG5, or finish " +
        "the Jumping minigame — to complete your run.";

    // ── Visual identity ───────────────────────────────────────────────────────

    public override string ThemeAccentColor => "#E84B8A";   // Kirby pink

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "SNES";
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "kdl3";

    // §14: the SNES emulators this world is played on. BizHawk is verified; the
    // §14 Discord ask was "SNES: BizHawk or snes9x", so snes9x is the headline
    // alternative, with Mesen as a third option (both shown per their bridge
    // state in the dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "snes9x", "mesen" };

    /// kdl3.lua reports checks + goal (consumable/star tables generated from the
    /// apworld; stages/heart-stars/bosses/goal mirror client.py). Remote-item
    /// delivery is the documented deferred piece.
    public override bool ChecksImplemented => true;

    private const string PatchExt  = ".apkdl3";
    private const string ResultExt = ".sfc";

    /// The vanilla Kirby's Dream Land 3 cartridge dump the randomizer is built
    /// for, identified by CONTENT (size + MD5), never by filename. The headerless
    /// .sfc (1,572,864 bytes) is the canonical dump and carries the exact MD5 the
    /// apworld validates (US and JP share that size — ValidateBaseRom accepts the
    /// file when ANY known MD5 of that size matches). A copier-headered dump
    /// (.smc, +512 bytes) is accepted by size: apply_appatch.py / the AP patcher
    /// strips the 512-byte header before hashing and patching, so its on-disk MD5
    /// differs and only size can vouch for it here. The patch's own base_checksum
    /// is the authoritative gate before anything is written.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(1_572_864, "201e7658f6194458a3869dde36bf8ec2",
                        "Kirby's Dream Land 3 (USA)"),
        new RomIdentity(1_572_864, "b2f2d004ea640c3db66df958fce122b2",
                        "Hoshi no Kirby 3 (Japan)"),
        new RomIdentity(1_573_376, null,
                        "Kirby's Dream Land 3 (copier-headered dump)"),
    };

    /// The Kirby's Dream Land 3 datapackage checksum is announced by the server in
    /// RoomInfo, not embedded as a constant in the apworld; left null. The
    /// launcher's "apworld updated" warning is simply not raised for this game
    /// until a live RoomInfo checksum is captured and pinned here.
    public string? BuiltAgainstDataPackageChecksum => null;

    // ── AP patch settings ─────────────────────────────────────────────────────

    /// Explicit .apkdl3 chosen by the user (Settings / drag-drop). Null = auto.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el))
            ApPatchPath = el.GetString();
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
                 : new RomRequirement("Kirby's Dream Land 3", "SNES",
                       "Kirby's Dream Land 3 (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement(
            "Kirby's Dream Land 3", "SNES",
            "Kirby's Dream Land 3 (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

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

    // ── Session ROM: apply the .apkdl3 to a library copy ──────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = ResolvePatchPath(out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Kirby's Dream Land 3] No AP patch (.apkdl3) found — launching the " +
                "vanilla ROM. Checks and ITEM DELIVERY CANNOT WORK on a vanilla ROM: " +
                "generate the multiworld, then pick the patch in Settings → Kirby's " +
                "Dream Land 3 (or drop it under " + SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        if (RomPath == null || !File.Exists(RomPath))
        {
            SessionRomNote =
                "[Kirby's Dream Land 3] No base ROM set — import your Kirby's Dream " +
                "Land 3 (USA) cartridge dump in Settings, then relaunch.";
            return null;
        }

        var result = await SnesApPatchHelper.ApplyAsync(
            "Kirby's Dream Land 3", patch, how, RomPath!, RomLibraryDirectory, ResultExt, ct);
        SessionRomNote = result.Note;
        if (result.OutRom != null) RegisterSeed(result.OutRom, patch);
        return result.OutRom;
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
            Text = "The multiworld generator produces a .apkdl3 patch for your slot. " +
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
                    : $"No .apkdl3 found under {SnesApPatchHelper.ApOutputDirectory} yet.";
        }
        RefreshAutoNote();

        pickBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Archipelago patch for Kirby's Dream Land 3",
                Filter = "AP KDL3 patch (*.apkdl3)|*.apkdl3|All files (*.*)|*.*",
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
