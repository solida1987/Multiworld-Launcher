using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LauncherV2.Plugins.DiabloII;

/// <summary>
/// 2.1 — Seed-bound D2 data-file (.txt) patcher.
///
/// Diablo II loads its tab-separated data tables (<c>data\global\excel\*.txt</c>)
/// at game start. Now that the launcher owns the install folder we can edit those
/// tables BEFORE launch to implement settings that the old DLL-runtime approach
/// did unreliably — skill level requirements, item level/stat requirements, and
/// (later) shop shuffle. This is far more robust than patching the running engine
/// and is fully verifiable (we can read the patched .txt back).
///
/// Architecture (per Marco's design):
///   • <see cref="EnsureBackup"/> snapshots the PRISTINE tables once into
///     <c>data\_apbackup\excel\</c> (taken before any patch, so it is always clean).
///   • <see cref="GenerateForSeed"/> writes a COMPLETE set of the managed tables —
///     transformed per the seed's settings, or a pristine copy when no change is
///     needed — into the seed's own folder (<c>save\seed_&lt;seed&gt;\excel\</c>).
///     So the SEED owns its tables, default or not.
///   • <see cref="ApplySeed"/> (called right before launch) restores pristine, then
///     overlays the seed's tables onto the live install → the game always loads the
///     seed's tables.
///   • <see cref="RestorePristine"/> (called when the game exits) resets the live
///     install back to pristine, so the folder is never left patched — crash-safe,
///     because ApplySeed also restores-then-overlays at the start of every launch.
///
/// All operations are best-effort: a failure never blocks launch (the engine just
/// loads whatever tables are currently present).
/// </summary>
public static class D2DataFiles
{
    /// The tables we manage. Keep this list tight — only files we actually patch.
    private static readonly string[] Managed =
        { "skills.txt", "weapons.txt", "armor.txt", "misc.txt", "Levels.txt", "SuperUniques.txt" };

    private static string ExcelDir(string gameDir)
        => Path.Combine(gameDir, "data", "global", "excel");

    private static string BackupDir(string gameDir)
        => Path.Combine(gameDir, "data", "_apbackup", "excel");

    private static string SeedExcelDir(string seedFolder)
        => Path.Combine(seedFolder, "excel");

    // ── Backup / restore ────────────────────────────────────────────────────

    /// <summary>
    /// Snapshot the pristine tables into the backup folder, once. Only copies a
    /// file when its backup is missing, so it always captures the clean original
    /// (this runs in GenerateForSeed BEFORE ApplySeed ever patches the live copy).
    /// Call <see cref="InvalidateBackup"/> from the game-update flow so a new game
    /// version re-captures fresh pristine tables.
    /// </summary>
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

    /// <summary>Delete the backup so the next launch re-captures pristine tables.
    /// Call this when the game is (re)installed/updated and its tables change.</summary>
    public static void InvalidateBackup(string gameDir)
    {
        try
        {
            string backup = BackupDir(gameDir);
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Reset the live tables back to pristine (copy backup → live).
    /// Safe to call any time; a no-op if no backup exists yet.</summary>
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

    /// <summary>
    /// Delete the compiled <c>.bin</c> cache for each managed table. Diablo II ships
    /// a pre-compiled <c>&lt;table&gt;.bin</c> next to every <c>.txt</c>; with the
    /// <c>-txt</c> flag the engine reads the <c>.txt</c> and re-compiles the <c>.bin</c>,
    /// but a stale <c>.bin</c> can otherwise shadow our patched <c>.txt</c>. Deleting it
    /// forces the engine to recompile from whatever <c>.txt</c> is currently in place —
    /// guaranteeing every patch (and every restore) actually takes effect. The <c>.bin</c>
    /// names are lower-case (e.g. <c>Levels.txt</c> → <c>levels.bin</c>).
    /// </summary>
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

    /// <summary>Confirm the live tables byte-match the seed's generated tables —
    /// i.e. <see cref="ApplySeed"/> actually moved every file into place.
    /// Returns (matched, total) over the managed files the seed generated.</summary>
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

    /// <summary>Confirm the live tables byte-match the pristine backup — i.e. the
    /// install was fully reset after the game closed. Returns (matched, total).</summary>
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

    /// <summary>Byte-exact file comparison (length first, then streamed). Best-effort:
    /// any IO error returns false so the caller reports "not confirmed".</summary>
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

    // ── Per-seed generation + apply ─────────────────────────────────────────

    /// <summary>
    /// Generate the seed's complete table set from the PRISTINE originals + its
    /// settings, into <c>save\seed_&lt;seed&gt;\excel\</c>. Always writes every
    /// managed file (transformed or a pristine copy) so the seed fully owns its
    /// tables. Idempotent — safe to call on every launch.
    /// </summary>
    public static void GenerateForSeed(D2RandomizerSettings s, long seed, string seedFolder, string gameDir)
    {
        try
        {
            EnsureBackup(gameDir);                       // capture pristine before anything patches
            string backup = BackupDir(gameDir);
            string outDir = SeedExcelDir(seedFolder);
            Directory.CreateDirectory(outDir);

            foreach (string file in Managed)
            {
                string src = Path.Combine(backup, file);
                if (!File.Exists(src)) continue;          // no pristine snapshot yet → skip
                var lines = File.ReadAllLines(src).ToList();

                // #1 — Skill level requirements. OFF (false) = remove: any unlocked
                // skill can take points regardless of character level.
                if (file.Equals("skills.txt", StringComparison.OrdinalIgnoreCase) && !s.SkillLevelReqs)
                    SetColumn(lines, "reqlevel", "1");

                // #2 — Item level / stat requirements. OFF (false) = remove: items
                // equip regardless of Level / Strength / Dexterity.
                if (!s.ItemLevelReqs &&
                    (file.Equals("weapons.txt", StringComparison.OrdinalIgnoreCase) ||
                     file.Equals("armor.txt",   StringComparison.OrdinalIgnoreCase) ||
                     file.Equals("misc.txt",    StringComparison.OrdinalIgnoreCase)))
                {
                    SetColumn(lines, "levelreq", "0");
                    SetColumn(lines, "reqstr",   "0");
                    SetColumn(lines, "reqdex",   "0");
                }

                // #3 — Shop shuffle. Permute which vendor stocks each GEAR item
                // (weapons.txt + armor.txt), seeded. misc.txt (potions/scrolls/TP/
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
                    // uses (levels.txt mon/nmon/umon). Towns + empty rows untouched, so
                    // no area is emptied (also sidesteps the #18 empty-area symptom the
                    // DLL shuffle produced). Supersedes the DLL monster shuffle.
                    if (s.MonsterShuffle)
                        ShuffleMonsters(lines, seed ^ 0x4C56L);
                }

                // Super-unique shuffle — permute each SuperUnique's monster base
                // (Class + hcIdx) within the EXISTING pool, so a named mini-boss
                // (Bishibosh, Rakanishu, Pindleskin…) appears as a different but
                // always-killable type. Independent of the act-boss shuffle, which the
                // DLL handles cosmetically for Andariel/Duriel/Mephisto/Diablo/Baal.
                if (s.SuperUniqueShuffle && file.Equals("SuperUniques.txt", StringComparison.OrdinalIgnoreCase))
                    ShuffleBosses(lines, seed ^ 0x4253L);

                File.WriteAllLines(Path.Combine(outDir, file), lines);
            }
        }
        catch { /* non-fatal — ApplySeed will just leave pristine tables in place */ }
    }

    /// <summary>
    /// Make the live install load the seed's tables: restore pristine first (crash-
    /// safe baseline), then overlay any of the seed's generated tables onto the live
    /// excel folder. Call immediately before launching the game.
    /// </summary>
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

    // ── Tiny tab-separated table editor ─────────────────────────────────────

    /// <summary>
    /// Set <paramref name="colName"/> to <paramref name="value"/> on every data row
    /// (case-insensitive header match). D2 tables are positional tab-separated; we
    /// only touch a row when it actually has that column, so column counts stay
    /// intact and malformed/terminator rows are left untouched.
    /// </summary>
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

    // ── Shop shuffle: relocate gear stocking across vendors ─────────────────

    /// <summary>
    /// Find each vendor's column group (<c>&lt;Npc&gt;Min/Max/MagicMin/MagicMax</c>)
    /// from the header. A group only counts when all four siblings exist, which
    /// filters out non-vendor "...Min" columns and is robust to D2's spelling quirks
    /// (we don't touch the typo'd <c>MagicLvl</c> column at all).
    /// </summary>
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

    /// <summary>
    /// Per row, permute the (Min, Max, MagicMin, MagicMax) tuples across the vendor
    /// groups, seeded — so each item is stocked by a different set of vendors but the
    /// same NUMBER of them. Deterministic for a given seed + row order.
    /// </summary>
    private static void ShuffleVendorStocking(List<string> lines, long seed)
    {
        if (lines.Count == 0) return;
        string[] header = lines[0].Split('\t');
        var groups = FindVendorGroups(header);
        if (groups.Count < 2) return;

        int maxCol = 0;
        foreach (var g in groups)
            maxCol = Math.Max(maxCol, Math.Max(Math.Max(g.min, g.max), Math.Max(g.mmin, g.mmax)));

        var rng = new Random(unchecked((int)(seed ^ (seed >> 32))));
        for (int r = 1; r < lines.Count; r++)
        {
            if (lines[r].Length == 0) continue;
            string[] cells = lines[r].Split('\t');
            if (cells.Length <= maxCol) continue;

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

    // ── Guaranteed-populated, max-size generation (always on) ───────────────

    /// Density floor applied to every populated non-town area. The tester-reported
    /// empty floors (Catacombs / Tower Cellar) sit at ~680 MonDen and still
    /// generate empty under bad RNG on small layouts; 1200 is comfortably above
    /// that and below the densest vanilla areas (~2200). Tunable in one place.
    private const int MonDenFloor = 1200;

    /// Boss / event arenas that MUST stay free of random spawns — populating them
    /// would break their set-piece (Baal in the Worldstone Chamber, the three
    /// Ancients on the summit, the Shenk siege). Matched on the Levels.txt Name.
    private static readonly string[] KeepEmptyArenas =
        { "Act 5 - World Stone", "Act 5 - Mountain Top", "Act 5 - Siege 1" };

    /// <summary>
    /// Always-applied Levels.txt transform (independent of every randomization
    /// toggle): force each non-town area to its largest defined ("Hell") size,
    /// raise monster density to <see cref="MonDenFloor"/> so no area can generate
    /// empty, and copy an act-appropriate monster pool into the few dungeons that
    /// ship with none. Towns and the boss/event arenas are left untouched.
    /// Idempotent. Runs BEFORE the monster shuffle so the shuffle permutes the
    /// now-populated pools.
    /// </summary>
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
                // Never donate a set-piece's preset monsters (e.g. the Ancients) into
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

            // 1) Force the largest ("Hell") size across all difficulties.
            int bx = Math.Max(IntOr0(c, sx), Math.Max(IntOr0(c, sxn), IntOr0(c, sxh)));
            int by = Math.Max(IntOr0(c, sy), Math.Max(IntOr0(c, syn), IntOr0(c, syh)));
            if (bx > 0) { Set(c, sx, bx.ToString()); Set(c, sxn, bx.ToString()); Set(c, sxh, bx.ToString()); }
            if (by > 0) { Set(c, sy, by.ToString()); Set(c, syn, by.ToString()); Set(c, syh, by.ToString()); }

            bool populated = mon1 >= 0 && mon1 < c.Length && !string.IsNullOrWhiteSpace(c[mon1]);

            // 2) Empty dungeon → copy an act-appropriate pool from the nearest
            //    preceding populated level in the same act.
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

            // 3) Density floor so no populated area can generate empty.
            if (populated)
            {
                int den = Math.Max(IntOr0(c, md), Math.Max(IntOr0(c, mdn), IntOr0(c, mdh)));
                den = Math.Max(den, MonDenFloor);
                Set(c, md, den.ToString()); Set(c, mdn, den.ToString()); Set(c, mdh, den.ToString());
            }

            lines[r] = string.Join('\t', c);
        }
    }

    // ── Monster + boss shuffle ──────────────────────────────────────────────

    /// <summary>
    /// Monster shuffle — permute the mon/nmon/umon spawn columns among the
    /// POPULATED level rows (mon1 non-empty), seeded. Towns / no-monster rows are
    /// skipped so no populated area becomes empty and towns never gain monsters.
    /// </summary>
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
            rows.Add(r);
            byRow[r] = c;
        }
        if (rows.Count < 2) return;

        var caps = rows.Select(r => cols.Select(ci => byRow[r][ci]).ToArray()).ToList();
        var perm = Enumerable.Range(0, rows.Count).ToArray();
        var rng = new Random(unchecked((int)(seed ^ (seed >> 32))));
        // Sattolo's algorithm (rng.Next(i), not i+1) = a single-cycle DERANGEMENT:
        // every row is guaranteed to move, so no area/boss ever keeps its own data
        // (a plain shuffle could map e.g. Blood Moor back onto itself).
        for (int i = perm.Length - 1; i > 0; i--) { int j = rng.Next(i); (perm[i], perm[j]) = (perm[j], perm[i]); }

        for (int i = 0; i < rows.Count; i++)
        {
            string[] c = byRow[rows[i]];
            string[] src = caps[perm[i]];
            for (int k = 0; k < cols.Count; k++) c[cols[k]] = src[k];
            lines[rows[i]] = string.Join('\t', c);
        }
    }

    /// <summary>
    /// Levels deliberately kept OUT of the monster shuffle so they keep their
    /// signature spawns. The secret Cow Level (Levels.txt Name "Act 1 - Moo Moo
    /// Farm") must always keep its hellbovines — and never donate them to other
    /// areas — so it is excluded from the shuffle pool entirely (neither receives
    /// other monsters nor contributes its cows). It still only exists when the
    /// player opens the cow portal, exactly as in vanilla.
    /// </summary>
    private static bool IsExcludedFromMonsterShuffle(string levelName)
        => !string.IsNullOrEmpty(levelName)
           && levelName.IndexOf("moo", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Boss shuffle — permute each SuperUnique's monster base (Class + hcIdx) among
    /// the EXISTING set of SuperUnique bases, seeded. Staying inside the existing
    /// pool guarantees every result is a real, killable boss type.
    /// </summary>
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
            rows.Add(r);
            byRow[r] = c;
        }
        if (rows.Count < 2) return;

        var pairs = rows.Select(r => (cls: byRow[r][classCol], hc: byRow[r][hcCol])).ToList();
        var perm = Enumerable.Range(0, rows.Count).ToArray();
        var rng = new Random(unchecked((int)(seed ^ (seed >> 32))));
        // Sattolo's algorithm (rng.Next(i), not i+1) = a single-cycle DERANGEMENT:
        // every row is guaranteed to move, so no area/boss ever keeps its own data
        // (a plain shuffle could map e.g. Blood Moor back onto itself).
        for (int i = perm.Length - 1; i > 0; i--) { int j = rng.Next(i); (perm[i], perm[j]) = (perm[j], perm[i]); }

        for (int i = 0; i < rows.Count; i++)
        {
            string[] c = byRow[rows[i]];
            c[classCol] = pairs[perm[i]].cls;
            c[hcCol]    = pairs[perm[i]].hc;
            lines[rows[i]] = string.Join('\t', c);
        }
    }
}
