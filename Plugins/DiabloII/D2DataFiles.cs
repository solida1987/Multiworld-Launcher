using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LauncherV2.Plugins.DiabloII;

// <summary>
// Seed-bound D2 data-file (.txt) patcher.

// Diablo II loads its tab-separated data tables (<c>data\global\excel\*.txt</c>)
// at game start. Now that the launcher owns the install folder we can edit those
// tables BEFORE launch to implement settings that the old DLL-runtime approach
// did unreliably — skill level requirements, item level/stat requirements, and
// (later) shop shuffle.
// and is fully verifiable (we can read the patched .txt back).

// Architecture (per Marco's design):
// • <see cref="EnsureBackup"/> snapshots the PRISTINE tables once into
// <c>data\_apbackup\excel\</c> (taken before any patch, so it is always clean).
// • <see cref="GenerateForSeed"/> writes a COMPLETE set of the managed tables —
// transformed per the seed's settings, or a pristine copy when no change is
// needed — into the seed's own folder (<c>save\seed_&lt;seed&gt;\excel\</c>).
// So the SEED owns its tables, default or not.
// • <see cref="ApplySeed"/> (called right before launch) restores pristine, then
// overlays the seed's tables onto the live install → the game always loads the
// seed's tables.
// • <see cref="RestorePristine"/> (called when the game exits) resets the live
// install back to pristine, so the folder is never left patched — crash-safe,
// because ApplySeed also restores-then-overlays at the start of every launch.

// All operations are best-effort: a failure never blocks launch (the engine just
// loads whatever tables are currently present).
// </summary>
public static class D2DataFiles
{
    // The tables we manage.
    private static readonly string[] Managed =
        { "skills.txt", "weapons.txt", "armor.txt", "misc.txt", "Levels.txt", "SuperUniques.txt",
          // 2.x — affix + set/unique tables, so "disable item level requirement" can
          // reach the AFFIX-derived level reqs (magic/rare) and set/unique reqs, not
          // just the base-item levelreq in weapons/armor/misc.
          // rare gear kept its required level even with the toggle on.
          "MagicPrefix.txt", "MagicSuffix.txt", "SetItems.txt", "UniqueItems.txt" };

    // True when the file name is one of the randomizer-managed excel tables
    // (the ones the pristine-backup/restore cycle owns).
    // flow to decide whether a repair actually touched managed data.
    public static bool IsManaged(string fileName)
        => Managed.Any(m => m.Equals(fileName, StringComparison.OrdinalIgnoreCase));

    // Refresh ONE managed table's pristine snapshot from its live copy.
    // For the repair flow: the repaired live file is freshly extracted from
    // the release zip (= genuinely pristine), but OTHER live tables may
    // still be seed-patched after a mid-game crash — so a full
    // InvalidateBackup would re-snapshot those patched tables as "pristine".
    // Per-file refresh keeps every other backup intact.
    public static void RefreshBackupFile(string gameDir, string fileName)
    {
        try
        {
            if (!IsManaged(fileName)) return;
            string live = Path.Combine(ExcelDir(gameDir),  fileName);
            string bak  = Path.Combine(BackupDir(gameDir), fileName);
            if (!File.Exists(live)) return;
            Directory.CreateDirectory(BackupDir(gameDir));
            File.Copy(live, bak, overwrite: true);
        }
        catch { /* non-fatal */ }
    }

    private static string ExcelDir(string gameDir)
        => Path.Combine(gameDir, "data", "global", "excel");

    private static string BackupDir(string gameDir)
        => Path.Combine(gameDir, "data", "_apbackup", "excel");

    private static string SeedExcelDir(string seedFolder)
        => Path.Combine(seedFolder, "excel");

    // --- Backup / restore ---

    // <summary>
    // Snapshot the pristine tables into the backup folder, once.
    // file when its backup is missing, so it always captures the clean original
    // (this runs in GenerateForSeed BEFORE ApplySeed ever patches the live copy).
    // Call <see cref="InvalidateBackup"/> from the game-update flow so a new game
    // version re-captures fresh pristine tables.
    // </summary>
    public static void EnsureBackup(string gameDir)
    {
        try
        {
            string excel = ExcelDir(gameDir);
            string backup = BackupDir(gameDir);
            Directory.CreateDirectory(backup);
            foreach (string file in Managed)
            {
                string live = Path.Combine(excel, file);
                string bak  = Path.Combine(backup, file);
                if (File.Exists(live) && !File.Exists(bak))
                    File.Copy(live, bak, overwrite: false);
            }
        }
        catch { /* non-fatal */ }
    }

    // <summary>Delete the backup so the next launch re-captures pristine tables.
    // Call this when the game is (re)installed/updated and its tables change.</summary>
    public static void InvalidateBackup(string gameDir)
    {
        try
        {
            string backup = BackupDir(gameDir);
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        }
        catch { /* non-fatal */ }
    }

    // <summary>Reset the live tables back to pristine (copy backup → live).
    // Safe to call any time; a no-op if no backup exists yet.</summary>
    public static void RestorePristine(string gameDir)
    {
        try
        {
            string excel = ExcelDir(gameDir);
            string backup = BackupDir(gameDir);
            if (!Directory.Exists(backup)) return;
            Directory.CreateDirectory(excel);
            foreach (string file in Managed)
            {
                string bak  = Path.Combine(backup, file);
                string live = Path.Combine(excel, file);
                if (File.Exists(bak)) File.Copy(bak, live, overwrite: true);
            }
            DeleteBins(excel);   // drop any patched .bin so D2 recompiles pristine
        }
        catch { /* non-fatal */ }
    }

    // <summary>
    // Delete the compiled <c>.bin</c> cache for each managed table.
    // a pre-compiled <c>&lt;table&gt;.bin</c> next to every <c>.txt</c>; with the
    // <c>-txt</c> flag the engine reads the <c>.txt</c> and re-compiles the <c>.bin</c>,
    // but a stale <c>.bin</c> can otherwise shadow our patched <c>.txt</c>.
    // forces the engine to recompile from whatever <c>.txt</c> is currently in place —
    // guaranteeing every patch (and every restore) actually takes effect.
    // names are lower-case (e.g.
    // </summary>
    private static void DeleteBins(string excelDir)
    {
        try
        {
            foreach (string file in Managed)
            {
                string bin = Path.Combine(
                    excelDir, Path.GetFileNameWithoutExtension(file).ToLowerInvariant() + ".bin");
                if (File.Exists(bin)) File.Delete(bin);
            }
        }
        catch { /* non-fatal */ }
    }

    // ── Verification (powers the on-screen "confirmed" step) ────────────────

    // <summary>Confirm the live tables byte-match the seed's generated tables —
    // i.e. <see cref="ApplySeed"/> actually moved every file into place.
    // Returns (matched, total) over the managed files the seed generated.</summary>
    public static (int ok, int total) VerifyApplied(string seedFolder, string gameDir)
    {
        string excel = ExcelDir(gameDir);
        string seedExcel = SeedExcelDir(seedFolder);
        int ok = 0, total = 0;
        foreach (string file in Managed)
        {
            string seedFile = Path.Combine(seedExcel, file);
            if (!File.Exists(seedFile)) continue;   // nothing generated for it
            total++;
            if (FilesEqual(seedFile, Path.Combine(excel, file))) ok++;
        }
        return (ok, total);
    }

    // <summary>Confirm the live tables byte-match the pristine backup — i.e.
    // install was fully reset after the game closed.
    public static (int ok, int total) VerifyPristine(string gameDir)
    {
        string excel = ExcelDir(gameDir);
        string backup = BackupDir(gameDir);
        int ok = 0, total = 0;
        foreach (string file in Managed)
        {
            string bak = Path.Combine(backup, file);
            if (!File.Exists(bak)) continue;
            total++;
            if (FilesEqual(bak, Path.Combine(excel, file))) ok++;
        }
        return (ok, total);
    }

    // <summary>Byte-exact file comparison (length first, then streamed).
    // any IO error returns false so the caller reports "not confirmed".</summary>
    private static bool FilesEqual(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            using var sa = fa.OpenRead();
            using var sb = fb.OpenRead();
            byte[] ba = new byte[65536];
            byte[] bb = new byte[65536];
            int n;
            while ((n = sa.Read(ba, 0, ba.Length)) > 0)
            {
                int m = 0;
                while (m < n) { int r = sb.Read(bb, m, n - m); if (r == 0) break; m += r; }
                if (m != n) return false;
                for (int i = 0; i < n; i++) if (ba[i] != bb[i]) return false;
            }
            return true;
        }
        catch { return false; }
    }

    // --- Per-seed generation + apply ---

    // <summary>
    // Generate the seed's complete table set from the PRISTINE originals + its
    // settings, into <c>save\seed_&lt;seed&gt;\excel\</c>.
    // managed file (transformed or a pristine copy) so the seed fully owns its
    // tables. Idempotent — safe to call on every launch.
    // </summary>
    public static void GenerateForSeed(D2RandomizerSettings s, long seed, string seedFolder, string gameDir)
    {
        try
        {
            EnsureBackup(gameDir);                       // capture pristine before anything patches
            // Start from nothing: the map is static, so a seed generated earlier in
            // this launcher session would otherwise be published for one that has
            // the shuffle switched off.
            _lastBossShuffleMap.Clear();
            string backup = BackupDir(gameDir);
            string outDir = SeedExcelDir(seedFolder);
            Directory.CreateDirectory(outDir);

            foreach (string file in Managed)
            {
                string src = Path.Combine(backup, file);
                if (!File.Exists(src)) continue;          // no pristine snapshot yet → skip
                var lines = File.ReadAllLines(src).ToList();

                // #1 — Skill level requirements.
                // skill can take points regardless of character level.
                if (file.Equals("skills.txt", StringComparison.OrdinalIgnoreCase) && !s.SkillLevelReqs)
                    SetColumn(lines, "reqlevel", "1");

                // #2 — Item LEVEL requirements.
                // regardless of character level.
                // armor/misc), the AFFIX-derived level reqs (MagicPrefix/MagicSuffix
                // "levelreq" — magic/rare gear), and set/unique reqs (SetItems/
                // UniqueItems "lvl req").
                // before, which is why magic/rare/set/unique gear kept its level req.
                if (!s.ItemLevelReqs)
                {
                    if (file.Equals("weapons.txt",     StringComparison.OrdinalIgnoreCase) ||
                        file.Equals("armor.txt",       StringComparison.OrdinalIgnoreCase) ||
                        file.Equals("misc.txt",        StringComparison.OrdinalIgnoreCase) ||
                        file.Equals("MagicPrefix.txt", StringComparison.OrdinalIgnoreCase) ||
                        file.Equals("MagicSuffix.txt", StringComparison.OrdinalIgnoreCase))
                        SetColumn(lines, "levelreq", "0");
                    if (file.Equals("SetItems.txt",    StringComparison.OrdinalIgnoreCase) ||
                        file.Equals("UniqueItems.txt", StringComparison.OrdinalIgnoreCase))
                        SetColumn(lines, "lvl req", "0");
                }

                // #2b — Item STATS requirements (Strength / Dexterity).
                // remove: every item equips regardless of STR/DEX.
                // (affixes don't add str/dex requirements).
                if (!s.ItemStatsReqs &&
                    (file.Equals("weapons.txt", StringComparison.OrdinalIgnoreCase) ||
                     file.Equals("armor.txt",   StringComparison.OrdinalIgnoreCase) ||
                     file.Equals("misc.txt",    StringComparison.OrdinalIgnoreCase)))
                {
                    SetColumn(lines, "reqstr", "0");
                    SetColumn(lines, "reqdex", "0");
                }

                // #3 — Shop shuffle. Permute which vendor stocks each GEAR item
                // (weapons.txt + armor.txt), seeded.
                // keys) is intentionally LEFT ALONE so the game always stays buyable
                // and playable. Preserves each item's stock COUNT (same number of
                // vendors carry it) — it just relocates which ones.
                if (s.ShopShuffle &&
                    (file.Equals("weapons.txt", StringComparison.OrdinalIgnoreCase) ||
                     file.Equals("armor.txt",   StringComparison.OrdinalIgnoreCase)))
                {
                    long salt = file.Equals("armor.txt", StringComparison.OrdinalIgnoreCase) ? 0x4172L : 0x5765L;
                    ShuffleVendorStocking(lines, seed ^ salt);
                }

                // Levels.txt — ALWAYS force full, max-size generation first (so no
                // area can generate empty), independent of any randomization toggle;
                // then optionally permute the now-populated pools (monster shuffle).
                if (file.Equals("Levels.txt", StringComparison.OrdinalIgnoreCase))
                {
                    ForceFullGeneration(lines);

                    // Monster shuffle — permute which monster pool each POPULATED area
                    // uses (levels.txt mon/nmon/umon).
                    // no area is emptied (also sidesteps the #18 empty-area symptom the
                    // DLL shuffle produced).
                    if (s.MonsterShuffle)
                        ShuffleMonsters(lines, seed ^ 0x4C56L);
                }

                // Super-unique shuffle — permute each SuperUnique's monster base
                // (Class + hcIdx) within the EXISTING pool, so a named mini-boss
                // (Bishibosh, Rakanishu, Pindleskin…) appears as a different but
                // always-killable type.
                // DLL handles cosmetically for Andariel/Duriel/Mephisto/Diablo/Baal.
                if (s.SuperUniqueShuffle && file.Equals("SuperUniques.txt", StringComparison.OrdinalIgnoreCase))
                    ShuffleBosses(lines, seed ^ 0x4253L);

                File.WriteAllLines(Path.Combine(outDir, file), lines);
            }
            // Publish where the shuffle sent everyone (or clear a stale map when
            // it did not run this time) — see WriteBossShuffleMap.
            WriteBossShuffleMap(gameDir);
        }
        catch { /* non-fatal — ApplySeed will just leave pristine tables in place */ }
    }

    // <summary>
    // Make the live install load the seed's tables: restore pristine first (crash-
    // safe baseline), then overlay any of the seed's generated tables onto the live
    // excel folder. Call immediately before launching the game.
    // </summary>
    public static void ApplySeed(string seedFolder, string gameDir)
    {
        try
        {
            RestorePristine(gameDir);
            string seedExcel = SeedExcelDir(seedFolder);
            if (!Directory.Exists(seedExcel)) return;
            string excel = ExcelDir(gameDir);
            Directory.CreateDirectory(excel);
            foreach (string file in Managed)
            {
                string srcFile = Path.Combine(seedExcel, file);
                if (File.Exists(srcFile))
                    File.Copy(srcFile, Path.Combine(excel, file), overwrite: true);
            }
            DeleteBins(excel);   // force D2 (-txt) to recompile from our patched .txt
        }
        catch { /* non-fatal */ }
    }

    // --- Tiny tab-separated table editor ---

    // <summary>
    // Set <paramref name="colName"/> to <paramref name="value"/> on every data row
    // (case-insensitive header match).
    // only touch a row when it actually has that column, so column counts stay
    // intact and malformed/terminator rows are left untouched.
    // </summary>
    private static void SetColumn(List<string> lines, string colName, string value)
    {
        if (lines.Count == 0) return;
        string[] header = lines[0].Split('\t');
        int col = -1;
        for (int i = 0; i < header.Length; i++)
            if (header[i].Trim().Equals(colName, StringComparison.OrdinalIgnoreCase)) { col = i; break; }
        if (col < 0) return;

        for (int r = 1; r < lines.Count; r++)
        {
            if (lines[r].Length == 0) continue;
            string[] cells = lines[r].Split('\t');
            if (cells.Length <= col) continue;           // row doesn't reach this column — skip
            if (cells[col] == value) continue;
            cells[col] = value;
            lines[r] = string.Join('\t', cells);
        }
    }

    // --- Shop shuffle: relocate gear stocking across vendors ---

    // <summary>
    // Find each vendor's column group (<c>&lt;Npc&gt;Min/Max/MagicMin/MagicMax</c>)
    // from the header. A group only counts when all four siblings exist, which
    // filters out non-vendor "...Min" columns and is robust to D2's spelling quirks
    // (we don't touch the typo'd <c>MagicLvl</c> column at all).
    // </summary>
    private static List<(int min, int max, int mmin, int mmax)> FindVendorGroups(string[] header)
    {
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++) idx[header[i].Trim()] = i;

        var groups = new List<(int, int, int, int)>();
        foreach (var raw in header)
        {
            string c = raw.Trim();
            if (!c.EndsWith("Min", StringComparison.OrdinalIgnoreCase)) continue;
            if (c.EndsWith("MagicMin", StringComparison.OrdinalIgnoreCase)) continue;
            string p = c.Substring(0, c.Length - 3);     // vendor prefix
            if (idx.TryGetValue(p + "Min", out int mi) && idx.TryGetValue(p + "Max", out int ma) &&
                idx.TryGetValue(p + "MagicMin", out int mmi) && idx.TryGetValue(p + "MagicMax", out int mma))
                groups.Add((mi, ma, mmi, mma));
        }
        return groups;
    }

    // <summary>
    // Per row, permute the (Min, Max, MagicMin, MagicMax) tuples across the vendor
    // groups, seeded — so each item is stocked by a different set of vendors but the
    // same NUMBER of them. Deterministic for a given seed + row order.
    // </summary>
    private static void ShuffleVendorStocking(List<string> lines, long seed)
    {
        if (lines.Count == 0) return;
        string[] header = lines[0].Split('\t');
        var groups = FindVendorGroups(header);
        if (groups.Count < 2) return;

        int maxCol = 0;
        foreach (var g in groups)
            maxCol = Math.Max(maxCol, Math.Max(Math.Max(g.min, g.max), Math.Max(g.mmin, g.mmax)));

        // locate the itemtype column so class-locked gear can be left alone.
        int typeCol = -1;
        for (int i = 0; i < header.Length; i++)
            if (header[i].Trim().Equals("type", StringComparison.OrdinalIgnoreCase)) { typeCol = i; break; }

        var rng = new Random(unchecked((int)(seed ^ (seed >> 32))));
        for (int r = 1; r < lines.Count; r++)
        {
            if (lines[r].Length == 0) continue;
            string[] cells = lines[r].Split('\t');
            if (cells.Length <= maxCol) continue;

            // do NOT relocate class-specific gear.
            // item's per-vendor stocking columns class-blindly, which is how Akara
            // ended up selling Sorceress orbs and other wrong-class items
            // (Knuckleduster). Vanilla deliberately puts each class's gear with the
            // vendor that class actually buys from, so these rows keep their vanilla
            // stocking; every non-class item still shuffles normally.
            if (typeCol >= 0 && typeCol < cells.Length &&
                IsClassSpecificItemType(cells[typeCol]))
                continue;

            var t = groups.Select(g => (cells[g.min], cells[g.max], cells[g.mmin], cells[g.mmax])).ToArray();
            for (int i = t.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (t[i], t[j]) = (t[j], t[i]); }
            for (int gi = 0; gi < groups.Count; gi++)
            {
                cells[groups[gi].min]  = t[gi].Item1;
                cells[groups[gi].max]  = t[gi].Item2;
                cells[groups[gi].mmin] = t[gi].Item3;
                cells[groups[gi].mmax] = t[gi].Item4;
            }
            lines[r] = string.Join('\t', cells);
        }
    }

    // <summary>
    // Class-locked ItemTypes.txt codes (the gear only one character class can use).
    // Rows with these types are excluded from the vendor-stocking shuffle so a
    // class's gear stays with the vendor that actually sells to that class.
    // Verified against the shipped Weapons.txt / Armor.txt type columns.
    // </summary>
    private static bool IsClassSpecificItemType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        switch (type.Trim().ToLowerInvariant())
        {
            case "abow":   // Amazon bows
            case "aspe":   // Amazon spears
            case "ajav":   // Amazon javelins (blank vendor tuples today, but future-proof)
            case "h2h":    // Assassin katars
            case "h2h2":   // Assassin katars (tier 2)
            case "orb":    // Sorceress orbs
            case "ashd":   // Paladin shields (auric)
            case "head":   // Necromancer shrunken heads
            case "pelt":   // Druid pelts
            case "phlm":   // Barbarian helms
                return true;
            default:
                return false;
        }
    }

    // --- Guaranteed-populated, max-size generation (always on) ---

    // Density floor applied to every populated non-town area, so no area can
    // generate completely empty under bad RNG on a small layout.
    
    // lowered 1200 -> 800. 1200 sat at roughly 1.8-2.7x vanilla density for
    // most areas, which is what produced the "huge density outdoors" reports
    // (Maegis) and the 3-5x-too-many-monsters-in-early-areas feedback (Freedom).
    // 800 is still above the ~680 of the areas that were reported empty — so it
    // keeps doing its job — while staying close to vanilla everywhere else.
    // Note the real cause of the empty dungeons turned out to be the DLL's own
    // room-count inflation (see V1); this floor is only the safety net.
    // the floor now only RESCUES, it no longer reshapes.
    
    // It used to be a flat 800 applied to every populated area, written to all
    // three difficulties. Vanilla densities are not uniform, so a flat floor
    // hit them very unevenly: outdoor Act 1 (520-600) gained 33-54%, while the
    // indoor levels that people actually complained about -- Jail and
    // Catacombs at 680, Crypt and Tower Cellar at 1024-1056 -- gained 18% or
    // nothing at all. Relative to the now much denser outdoors the dungeons
    // felt thinner than they used to, which is exactly what Maegis reported
    // ("big map areas increased while jail/tower/catacomb decreased") and why
    // he asked for the change to be reverted rather than the quests removed.
    
    // The floor existed to stop dungeons generating empty.
    // that are now fixed: forcing Hell-sized maps (removed in 2.8.1) and the
    // monster shuffle overwriting the six bytes that decide whether a monster
    // may be placed at all (Stable 3.2.1).
    // rescue value, applied only where an area would otherwise be at zero.
    private const int MonDenRescue = 400;

    // Boss / event arenas that MUST stay free of random spawns — populating them
    // would break their set-piece (Baal in the Worldstone Chamber, the three
    // Ancients on the summit, the Shenk siege).
    private static readonly string[] KeepEmptyArenas =
        { "Act 5 - World Stone", "Act 5 - Mountain Top", "Act 5 - Siege 1" };

    // <summary>
    // Always-applied Levels.txt transform (independent of every randomization
    // toggle): copy an act-appropriate monster pool into the few dungeons that
    // ship with none, and rescue any difficulty whose density would be zero.
    // Map sizes are NOT touched (that was removed in 2.8.1) and vanilla
    // densities are left alone (3.2.2).
    // Idempotent. Runs BEFORE the monster shuffle so the shuffle permutes the
    // now-populated pools.
    // </summary>
    private static void ForceFullGeneration(List<string> lines)
    {
        if (lines.Count == 0) return;
        string[] header = lines[0].Split('\t');
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++) idx[header[i].Trim()] = i;

        int Col(string n) => idx.TryGetValue(n, out int v) ? v : -1;
        int name = Col("Name");
        int sx = Col("SizeX"), sxn = Col("SizeX(N)"), sxh = Col("SizeX(H)");
        int sy = Col("SizeY"), syn = Col("SizeY(N)"), syh = Col("SizeY(H)");
        int md = Col("MonDen"), mdn = Col("MonDen(N)"), mdh = Col("MonDen(H)");
        if (name < 0 || md < 0) return;

        int mon1 = Col("mon1");
        var monCols = new List<int>();
        foreach (string pre in new[] { "mon", "nmon", "umon" })
            for (int n = 1; n <= 10; n++) { int ci = Col(pre + n); if (ci >= 0) monCols.Add(ci); }
        // the donor copy must bring the COUNT columns too.
        // mon/nmon/umon type lists left NumMon (how many distinct types the level
        // picks) and MonUMin/MonUMax (unique/champion pack count) at the empty
        // level's own values — typically blank/0 — so the receiver drew ZERO types
        // and stayed empty despite having a full monster list.
        // generated seed: Tower Cellar 2 received "skeleton1" but had NumMon empty.
        foreach (string cn in new[] { "NumMon", "MonUMin", "MonUMax",
                                      "MonUMin(N)", "MonUMax(N)",   // NM pack counts —
                                      "MonUMin(H)", "MonUMax(H)" }) // else blank→0 packs in NM/Hell
        { int ci = Col(cn); if (ci >= 0) monCols.Add(ci); }

        int maxCol = new[] { name, sx, sxn, sxh, sy, syn, syh, md, mdn, mdh, mon1 }
                     .Concat(monCols).Max();

        static int IntOr0(string[] cells, int col)
            => (col >= 0 && col < cells.Length && int.TryParse(cells[col], out int v)) ? v : 0;
        static void Set(string[] cells, int col, string val)
        { if (col >= 0 && col < cells.Length) cells[col] = val; }
        static string ActOf(string nm)
        { int d = nm.IndexOf(" - ", StringComparison.Ordinal); return d > 0 ? nm.Substring(0, d) : ""; }

        // Parse every wide-enough data row up front so empty dungeons can pull a
        // donor pool from a preceding populated level in the same act.
        var cells = new string[lines.Count][];
        for (int r = 1; r < lines.Count; r++)
        {
            if (lines[r].Length == 0) continue;
            string[] c = lines[r].Split('\t');
            if (c.Length > maxCol) cells[r] = c;
        }

        string[]? DonorInAct(int beforeRow, string act)
        {
            for (int r = beforeRow - 1; r >= 1; r--)
            {
                var c = cells[r];
                if (c == null || mon1 < 0 || mon1 >= c.Length) continue;
                if (string.IsNullOrWhiteSpace(c[mon1])) continue;
                if (!string.Equals(ActOf(c[name]), act, StringComparison.OrdinalIgnoreCase)) continue;
                // Never donate a set-piece's preset monsters (e.g.
                // a normal dungeon — those arenas are excluded from being a source.
                if (KeepEmptyArenas.Any(a => c[name].Equals(a, StringComparison.OrdinalIgnoreCase))) continue;
                return c;
            }
            return null;
        }

        for (int r = 1; r < lines.Count; r++)
        {
            var c = cells[r];
            if (c == null) continue;
            string nm = name < c.Length ? c[name] : "";
            if (string.IsNullOrWhiteSpace(nm)) continue;
            if (nm.IndexOf("Town", StringComparison.OrdinalIgnoreCase) >= 0 ||
                nm.Equals("Null", StringComparison.OrdinalIgnoreCase) ||
                nm.Equals("Expansion", StringComparison.OrdinalIgnoreCase))
                continue;                                   // towns: never gain monsters
            if (KeepEmptyArenas.Any(a => nm.Equals(a, StringComparison.OrdinalIgnoreCase)))
                continue;                                   // boss/event set-pieces: leave as-is

            // DO NOT force map size.
            // every area to its largest ("Hell") size on all difficulties to
            // combat empty floors. That backfired: D2 places a roughly fixed
            // monster count per area, so a Normal/NM area blown up to Hell
            // dimensions spreads those monsters paper-thin — the tester saw
            // Tower Cellar / Catacombs nearly empty with only the fixed
            // champion/elite packs spawning.
            // the density math sane; the donor-pool copy + density floor below
            // are what actually prevent empty dungeons (without inflating maps).

            bool populated = mon1 >= 0 && mon1 < c.Length && !string.IsNullOrWhiteSpace(c[mon1]);

            // 2) Empty dungeon → copy an act-appropriate pool from the nearest
            // preceding populated level in the same act.
            if (!populated)
            {
                var donor = DonorInAct(r, ActOf(nm));
                if (donor != null)
                {
                    foreach (int ci in monCols)
                        if (ci < c.Length && ci < donor.Length) c[ci] = donor[ci];
                    populated = true;
                }
            }

            // 3) Rescue only. Vanilla per-difficulty densities are kept as they
            // are -- D2 is balanced around them -- and a difficulty is only
            // touched when it would otherwise be zero, which happens on a
            // handful of rows (e.g.
            if (populated)
            {
                // `nm` is taken by the level-name local in the enclosing loop.
                int denN = IntOr0(c, md), denNm = IntOr0(c, mdn), denH = IntOr0(c, mdh);
                int fallback = Math.Max(MonDenRescue,
                                        Math.Max(denN, Math.Max(denNm, denH)));
                if (denN  <= 0) Set(c, md,  fallback.ToString());
                if (denNm <= 0) Set(c, mdn, fallback.ToString());
                if (denH  <= 0) Set(c, mdh, fallback.ToString());
            }

            lines[r] = string.Join('\t', c);
        }
    }

    // --- Monster + boss shuffle ---

    // <summary>
    // Monster shuffle — permute the mon/nmon/umon spawn columns among the
    // POPULATED level rows (mon1 non-empty), seeded.
    // skipped so no populated area becomes empty and towns never gain monsters.
    // </summary>
    private static void ShuffleMonsters(List<string> lines, long seed)
    {
        if (lines.Count == 0) return;
        string[] header = lines[0].Split('\t');
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++) idx[header[i].Trim()] = i;

        var cols = new List<int>();
        foreach (string pre in new[] { "mon", "nmon", "umon" })
            for (int n = 1; n <= 10; n++)
                if (idx.TryGetValue(pre + n, out int ci)) cols.Add(ci);
        if (cols.Count == 0) return;
        int mon1 = idx.TryGetValue("mon1", out int m1) ? m1 : cols[0];
        int nameCol = idx.TryGetValue("Name", out int ncv) ? ncv : 0;
        int maxCol = cols.Max();

        var rows = new List<int>();
        var byRow = new Dictionary<int, string[]>();
        for (int r = 1; r < lines.Count; r++)
        {
            if (lines[r].Length == 0) continue;
            string[] c = lines[r].Split('\t');
            if (c.Length <= maxCol) continue;
            if (string.IsNullOrWhiteSpace(c[mon1])) continue;   // town / no-monster level
            if (nameCol < c.Length && IsExcludedFromMonsterShuffle(c[nameCol]))
                continue;   // secret Cow Level: keep its cows, never feed them to the pool
            // never shuffle the boss/event set-piece arenas.
            // is the three Ancients (isSpawn blank, Rarity 0 = 100% UNSPAWNABLE by the
            // normal population code), so any level that received it spawned literally
            // nothing — not even champions or barrels.
            // level per seed is absolutely empty".
            // from ForceFullGeneration's donor pool for the same reason; excluding
            // them here also keeps Baal/Ancients/Shenk set-pieces intact.
            if (nameCol < c.Length &&
                KeepEmptyArenas.Any(a => c[nameCol].Equals(a, StringComparison.OrdinalIgnoreCase)))
                continue;
            rows.Add(r);
            byRow[r] = c;
        }
        if (rows.Count < 2) return;

        var caps = rows.Select(r => cols.Select(ci => byRow[r][ci]).ToArray()).ToList();
        var rng = new Random(unchecked((int)(seed ^ (seed >> 32))));

        // band the derangement BY ACT instead of deranging globally.
        
        // In 1.13 a monster's Normal-difficulty stats come from its own MonStats row
        // (the area-level override only applies in NM/Hell), so dropping an Act 5
        // monster type into an early Act 1 area spawns it with full native HP and
        // regeneration — the "unkillable monster in Normal" reports.
        // within the same act keeps every monster roughly level-appropriate while
        // still fully randomizing what you meet where.
        
        // Each act is deranged independently (Sattolo = single cycle, so every row
        // still moves). An act with fewer than 2 shuffleable rows is left alone.
        static string ActKey(string nm)
        { int d = nm.IndexOf(" - ", StringComparison.Ordinal); return d > 0 ? nm.Substring(0, d) : ""; }

        var byAct = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows.Count; i++)
        {
            string nm = nameCol < byRow[rows[i]].Length ? byRow[rows[i]][nameCol] : "";
            string act = ActKey(nm);
            if (!byAct.TryGetValue(act, out var list)) { list = new List<int>(); byAct[act] = list; }
            list.Add(i);                       // index into rows/caps
        }

        var perm = Enumerable.Range(0, rows.Count).ToArray();   // identity by default
        foreach (var kv in byAct)
        {
            var members = kv.Value;
            if (members.Count < 2) continue;   // nothing to derange within this act
            var order = members.ToArray();
            for (int i = order.Length - 1; i > 0; i--)
            { int j = rng.Next(i); (order[i], order[j]) = (order[j], order[i]); }
            // members[k] receives the data currently held by order[k]
            for (int k = 0; k < members.Count; k++) perm[members[k]] = order[k];
        }

        for (int i = 0; i < rows.Count; i++)
        {
            string[] c = byRow[rows[i]];
            string[] src = caps[perm[i]];
            for (int k = 0; k < cols.Count; k++) c[cols[k]] = src[k];
            lines[rows[i]] = string.Join('\t', c);
        }
    }

    // <summary>
    // Levels deliberately kept OUT of the monster shuffle so they keep their
    // signature spawns. The secret Cow Level (Levels.txt Name "Act 1 - Moo Moo
    // Farm") must always keep its hellbovines — and never donate them to other
    // areas — so it is excluded from the shuffle pool entirely (neither receives
    // other monsters nor contributes its cows).
    // player opens the cow portal, exactly as in vanilla.
    // </summary>
    private static bool IsExcludedFromMonsterShuffle(string levelName)
        => !string.IsNullOrEmpty(levelName)
           && levelName.IndexOf("moo", StringComparison.OrdinalIgnoreCase) >= 0;

    // <summary>
    // Boss shuffle — permute each SuperUnique's monster base (Class + hcIdx) among
    // the EXISTING set of SuperUnique bases, seeded.
    // pool guarantees every result is a real, killable boss type.
    // </summary>
    // Row index -> row index whose identity it now wears.
    // ShuffleBosses so the spoiler map below can be written from the same
    // permutation the rows got, rather than a second one.
    private static readonly Dictionary<int, int> _shufflePerm = new();

    // SuperUniques.txt names the mod/apworld spell differently.
    // on hcIdx so it never notices, but this lookup goes through the hunt
    // LOCATION name, which uses the display spelling.
    private static readonly Dictionary<string, string> SuNameAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Leatherarm"]           = "Creeping Feature",
            ["Web Mage the Burning"] = "Sszark the Burning",
            ["Siege Boss"]           = "Shenk the Overseer",
        };

    // Which (act, gate region) a super-unique's hunt check belongs to, via the
    // generated tables: "Hunt: &lt;name&gt;" -> quest id -> area -> region.
    // Null when the name has no hunt check or the area is unmapped — those
    // rows are pinned rather than guessed at.
    private static (int Act, int Region)? HuntRegionFor(string suName)
    {
        if (string.IsNullOrWhiteSpace(suName)) return null;
        string display = SuNameAliases.TryGetValue(suName, out var alias) ? alias : suName;
        if (!D2LogicTables.LocationQuest.TryGetValue("Hunt: " + display, out int qid))
            return null;
        if (!D2LogicTables.QuestZone.TryGetValue(qid, out int area)) return null;
        return D2LogicTables.ZoneRegion.TryGetValue(area, out var ar) ? ar : null;
    }

    private static void ShuffleBosses(List<string> lines, long seed)
    {
        if (lines.Count == 0) return;
        string[] header = lines[0].Split('\t');
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++) idx[header[i].Trim()] = i;
        if (!idx.TryGetValue("Class", out int classCol) || !idx.TryGetValue("hcIdx", out int hcCol)) return;
        int maxCol = Math.Max(classCol, hcCol);

        var rows = new List<int>();
        var byRow = new Dictionary<int, string[]>();
        for (int r = 1; r < lines.Count; r++)
        {
            if (lines[r].Length == 0) continue;
            string[] c = lines[r].Split('\t');
            if (c.Length <= maxCol) continue;
            if (string.IsNullOrWhiteSpace(c[classCol])) continue;

            // PINNED rows — never shuffled:
            
            // 1. The mod's dedicated GATE-BOSS rows (hcIdx >= 66, "Gate <name>").
            // Their hcIdx IS their identity: the DLL's kill detection reads a
            // killed superunique's hcIdx column and treats >= 100 as "gate
            // boss" (key, no Hunt credit).
            // superset (vanilla ends at 65).
            // hcIdx to a random vanilla row — and that row's kills would
            // silently stop crediting Hunt checks.
            
            // 2. ENGINE-quest bosses. The vanilla quest handlers for Radament
            // (A2Q1), The Summoner (A2Q6, act-progression!) and Shenk (A5Q1)
            // key on the monster itself — a shuffled stand-in never completes
            // the quest (field report: skeleton-Radament didn't finish
            // Radament's Lair). Their LOOK must stay their QUEST identity.
            if (int.TryParse(c[hcCol].Trim(), out int hc) && hc >= 66) continue;
            string suName = c[0].Trim();
            if (suName.Equals("Radament", StringComparison.OrdinalIgnoreCase) ||
                suName.Equals("The Summoner", StringComparison.OrdinalIgnoreCase) ||
                suName.Equals("Shenk the Overseer", StringComparison.OrdinalIgnoreCase))
                continue;

            rows.Add(r);
            byRow[r] = c;
        }
        if (rows.Count < 2) return;

        // --- Never let an identity cross a gate ---
        
        // The shuffle moves Class+hcIdx to another ROW; the row keeps its own
        // spawn point. The mod credits a hunt by hcIdx, so the check for
        // "Hunt: X" is completed wherever X's hcIdx landed — NOT where the
        // apworld says that check lives.
        
        // Unconstrained, that breaks Archipelago's model of the seed.
        // report (fariel, 2026-08-09): "Hunt: Pitspawn Fouldog" holds an Act 2
        // key and AP places it in Jail Level 2, but the shuffle had moved the
        // identity behind Act 5 — a check the player physically could not reach
        // without the key it contained.
        // cheated past. Same report: killing the boss standing in Pitspawn's
        // spot completed the ACT 5 quest instead, which is this swap seen from
        // the inside.
        
        // Permuting only WITHIN an (act, gate region) bucket fixes it at the
        // root: a hunt stays behind exactly the gates it was already behind, so
        // AP's reachability stays true.
        // pinning an unknown is always safe, shuffling one is not.
        // Rows with NO hunt check carry no Archipelago logic at all, so they can
        // swap freely with each other — they just cannot receive a hunt
        // identity, which is what keeping them in their own bucket guarantees.
        // Pinning them instead would have cut the shuffle from 64 rows to 27
        // and made the option barely worth having.
        var bucketOf = new Dictionary<int, (int Act, int Region)>();
        foreach (int r in rows)
            bucketOf[r] = HuntRegionFor(byRow[r][0].Trim()) ?? (0, 0);

        var rng = new Random(unchecked((int)(seed ^ (seed >> 32))));
        int moved = 0, pinned = 0;
        foreach (var group in rows.Where(bucketOf.ContainsKey)
                                  .GroupBy(r => bucketOf[r]))
        {
            var g = group.ToList();
            if (g.Count < 2) { pinned += g.Count; continue; }   // nothing to swap with
            var pairs = g.Select(r => (cls: byRow[r][classCol], hc: byRow[r][hcCol])).ToList();
            var perm = Enumerable.Range(0, g.Count).ToArray();
            // Sattolo's algorithm (rng.Next(i), not i+1) = a single-cycle
            // DERANGEMENT: every row in the bucket is guaranteed to move.
            for (int i = perm.Length - 1; i > 0; i--)
            { int j = rng.Next(i); (perm[i], perm[j]) = (perm[j], perm[i]); }

            for (int i = 0; i < g.Count; i++)
            {
                string[] c = byRow[g[i]];
                c[classCol] = pairs[perm[i]].cls;
                c[hcCol]    = pairs[perm[i]].hc;
                lines[g[i]] = string.Join('\t', c);
                _shufflePerm[g[i]] = g[perm[i]];
            }
            moved += g.Count;
        }
        pinned += rows.Count(r => !bucketOf.ContainsKey(r));
        System.Diagnostics.Debug.WriteLine(
            $"[D2] boss shuffle: {moved} moved within their gate band, {pinned} pinned");

        // Write down where everyone went, so the in-game hunt list can say it.
        
        // The shuffle moves an identity, not a spawn point: row i keeps standing
        // where it always stood, but is now wearing row perm[i]'s Class and
        // hcIdx. The mod credits a hunt by hcIdx, so "Hunt: <perm[i]>" is
        // completed at row i's place.
        // and only this permutation knows how — which is why players reported
        // hunting a boss that "isn't there".
        
        // One line per moved identity: the hcIdx the mod will see, who that is,
        // and whose spot they are now standing in.
        _lastBossShuffleMap.Clear();
        int nameCol = idx.TryGetValue("Name", out int nc) ? nc : 0;
        foreach (var kv in _shufflePerm)
        {
            string[] host  = byRow[kv.Key];            // the spot
            string[] ident = byRow[kv.Value];          // whose identity it wears
            string movedName = ident.Length > nameCol ? ident[nameCol].Trim() : "";
            string hostName  = host.Length  > nameCol ? host[nameCol].Trim()  : "";
            if (movedName.Length == 0 || hostName.Length == 0) continue;
            if (!int.TryParse(host[hcCol].Trim(), out int movedHc)) continue;
            _lastBossShuffleMap.Add($"hc={movedHc}|who={movedName}|at={hostName}");
        }
    }

    // Filled by ShuffleBosses; written next to the seed's tables so the mod can
    // read it. Empty when the shuffle did not run.
    private static readonly List<string> _lastBossShuffleMap = new();

    // Publish (or clear) the boss-shuffle map for the mod.
    // apply path: with the shuffle off the file is DELETED, because a leftover
    // from a previous seed would send players to the wrong place with total
    // confidence — worse than saying nothing.
    public static void WriteBossShuffleMap(string gameDir)
    {
        try
        {
            string path = Path.Combine(gameDir, "Archipelago", "su_shuffle.dat");
            if (_lastBossShuffleMap.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            File.WriteAllLines(path, _lastBossShuffleMap);
        }
        catch { /* non-fatal — the hunt list just does not annotate */ }
    }
}
