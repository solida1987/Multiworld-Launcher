using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LauncherV2.Core.Patching;

/// Reads and applies an Archipelago patch container -- the .apemerald,
/// .apfirered, .apmc … file the generator hands each player.
///
/// The container is a ZIP holding archipelago.json (which slot it belongs to,
/// the MD5 of the vanilla ROM it was built against, and the list of steps to
/// run) plus one file per step. Everything here mirrors worlds/Files.py in
/// Archipelago itself; the step names are theirs, not ours.
///
/// The base ROM is only ever READ. The player's own file is never modified,
/// never moved, and never deleted -- the patched copy is a new file.
public static class ApPatch
{
    public sealed record Step(string Name, IReadOnlyList<string> Args);

    public sealed record Manifest(
        string Game,
        string? PlayerName,
        string? Server,
        // A LIST, because a world may accept more than one dump: Pokémon
        // FireRed/LeafGreen ships rev0 and rev1 patches in one container and
        // names both hashes. Read as a single string this field came back null,
        // which switched the wrong-ROM check off without saying so.
        IReadOnlyList<string> BaseChecksums,
        string PatchFileEnding,
        string ResultFileEnding,
        IReadOnlyList<Step> Procedure);

    public sealed record Result(string OutPath, long Size, string Md5, Manifest Manifest);

    /// Read just the manifest. Returns null when the file is not an AP patch at
    /// all -- a wrong file dropped on the window is a normal thing to happen,
    /// not an error worth throwing over.
    public static Manifest? ReadManifest(string patchPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(patchPath);
            var entry = zip.GetEntry("archipelago.json");
            if (entry is null) return null;

            using var s = entry.Open();
            using var doc = JsonDocument.Parse(s);
            var r = doc.RootElement;

            if (!r.TryGetProperty("game", out var g) || g.GetString() is not string game
                || string.IsNullOrWhiteSpace(game))
                return null;

            var steps = new List<Step>();
            if (r.TryGetProperty("procedure", out var proc)
                && proc.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in proc.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Array) continue;
                    var parts = p.EnumerateArray().ToArray();
                    if (parts.Length == 0 || parts[0].ValueKind != JsonValueKind.String) continue;

                    var args = new List<string>();
                    if (parts.Length > 1 && parts[1].ValueKind == JsonValueKind.Array)
                        args.AddRange(parts[1].EnumerateArray()
                                              .Where(a => a.ValueKind == JsonValueKind.String)
                                              .Select(a => a.GetString()!));
                    steps.Add(new Step(parts[0].GetString()!, args));
                }
            }

            return new Manifest(
                game,
                Str(r, "player_name"),
                Str(r, "server"),
                Hashes(r, "base_checksum"),
                Str(r, "patch_file_ending") ?? ".appatch",
                Str(r, "result_file_ending") ?? ".rom",
                steps);
        }
        catch
        {
            return null;   // unreadable ZIP, bad JSON -- "not a patch" either way
        }

        static string? Str(JsonElement o, string k)
            => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
               ? v.GetString() : null;

        // Accepts a single hash or a list of them, lowercased. Empty means the
        // patch states no hash at all -- a real state, and quite different from
        // "it stated one and we failed to read it", which is what returning
        // null for an array quietly did.
        static IReadOnlyList<string> Hashes(JsonElement o, string k)
        {
            if (!o.TryGetProperty(k, out var v)) return Array.Empty<string>();
            if (v.ValueKind == JsonValueKind.String)
                return new[] { v.GetString()!.ToLowerInvariant() };
            if (v.ValueKind == JsonValueKind.Array)
                return v.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!.ToLowerInvariant())
                        .ToArray();
            return Array.Empty<string>();
        }
    }

    /// Apply `patchPath` to `baseRomPath` and write the result to `outPath`.
    ///
    /// Throws with a message meant for the player when the base ROM is the
    /// wrong one, or when the patch asks for a step we cannot perform. Both are
    /// worth stopping for: a patched ROM built from the wrong base does not
    /// announce itself, it just behaves strangely hours later.
    public static Result Apply(string patchPath, string baseRomPath, string outPath)
    {
        var manifest = ReadManifest(patchPath)
            ?? throw new InvalidDataException(
                $"{Path.GetFileName(patchPath)} is not an Archipelago patch "
              + "(no archipelago.json inside).");

        Dictionary<string, byte[]> files;
        using (var zip = ZipFile.OpenRead(patchPath))
            files = zip.Entries
                       .Where(e => e.Name.Length > 0 && e.FullName != "archipelago.json")
                       .ToDictionary(e => e.FullName, e =>
                       {
                           using var s = e.Open();
                           using var ms = new MemoryStream();
                           s.CopyTo(ms);
                           return ms.ToArray();
                       });

        // A patch with NO steps is not "already done" -- it is a container this
        // patcher cannot act on. Worlds built on APAutoPatchInterface (Pokémon
        // Black/White, Platinum) patch in their own Python and ship no
        // procedure at all; running an empty loop here would write the vanilla
        // ROM back out and report it as patched. Refusal is the only honest
        // answer the container leaves us.
        if (manifest.Procedure.Count == 0)
            throw new NotSupportedException(
                $"{Path.GetFileName(patchPath)} carries no patch procedure -- "
              + "this game's world patches in its own code, which the launcher "
              + "cannot run. Nothing was written.");

        byte[] rom = File.ReadAllBytes(baseRomPath);

        if (manifest.BaseChecksums.Count > 0)
        {
            string actual = Convert.ToHexString(MD5.HashData(rom)).ToLowerInvariant();
            if (!manifest.BaseChecksums.Contains(actual))
                throw new InvalidDataException(
                    "This is not the ROM the patch was built for.\n\n"
                  + (manifest.BaseChecksums.Count == 1
                        ? $"The patch expects MD5 {manifest.BaseChecksums[0]}\n"
                        : "The patch accepts these dumps:\n"
                          + string.Concat(manifest.BaseChecksums.Select(h => $"  {h}\n")))
                  + $"your file is    MD5 {actual}\n\n"
                  + "Use the exact dump the world asks for -- a different region or "
                  + "revision will patch into something that looks fine and then breaks.");
        }

        foreach (var step in manifest.Procedure)
        {
            byte[] Arg(int i)
            {
                if (i >= step.Args.Count)
                    throw new InvalidDataException(
                        $"the patch is incomplete: step {step.Name} wants a file "
                      + "argument it does not have.");

                // A world may override a step in Python and pick a DIFFERENT
                // file than the manifest names. The one replica we carry is
                // consulted here, per step, against the CURRENT rom bytes --
                // exactly when and how the Python side asks.
                string name = OverrideFile(manifest.Game, step.Name, rom) ?? step.Args[i];

                if (!files.TryGetValue(name, out var b))
                    throw new InvalidDataException(
                        $"the patch is incomplete: step {step.Name} wants {name}, "
                      + "which is not inside the container.");
                return b;
            }

            rom = step.Name switch
            {
                "apply_bsdiff4" => ApplyBsdiff4(rom, Arg(0)),
                "apply_ips"     => ApplyIps(rom, Arg(0)),
                "apply_tokens"  => ApplyTokens(rom, Arg(0)),
                _ when IsKnownNoOpStep(manifest.Game, step.Name) => rom,
                // A step name we do not know is a world running its own Python
                // (APPatchExtension). Refusing is the ONLY safe answer: the
                // container cannot tell us what the code did.
                _ => throw new NotSupportedException(
                        $"This patch uses a step the launcher cannot perform yet: "
                      + $"{step.Name}. Nothing was written.")
            };
        }

        // Temp file + replace, so an interrupted patch cannot leave a half
        // written ROM sitting where a valid one is expected.
        string dir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
        Directory.CreateDirectory(dir);
        string tmp = outPath + ".tmp";
        File.WriteAllBytes(tmp, rom);
        File.Move(tmp, outPath, overwrite: true);

        return new Result(outPath, rom.LongLength,
                          Convert.ToHexString(MD5.HashData(rom)).ToLowerInvariant(),
                          manifest);
    }

    /// Replicas of per-game Python patch-step overrides (APPatchExtension).
    ///
    /// Worlds can override procedure steps in Python, and the container does
    /// not say so -- the manifest looks ordinary. Two classes exist:
    ///
    ///   * NEW step names (MLSS's enemy_randomize, CotM's apply_patches): the
    ///     switch above throws on the unknown name. Loud, automatic, safe.
    ///   * REUSED generic names with changed behaviour: generic code runs and
    ///     produces silently wrong output. THAT class must be replicated here,
    ///     and checked for at manifest time -- see catalog/SCHEMA.md.
    ///
    /// Pokemon FireRed/LeafGreen (worlds/pokemon_frlg/rom.py,
    /// PokemonFRLGPatchExtension): one container carries rev0 AND rev1 patch
    /// files; both steps switch to the rev1 file when the rom's revision byte
    /// (offset 0xBC) is 1. The byte is read from the CURRENT rom state at each
    /// step, exactly as their Python does -- do not hoist this to a one-time
    /// check, the base patch is free to change the byte.
    static string? OverrideFile(string game, string stepName, byte[] rom)
    {
        if (string.Equals(game, "Pokemon FireRed and LeafGreen", StringComparison.Ordinal)
            && rom.Length > 0xBC && rom[0xBC] == 1)
        {
            if (stepName == "apply_bsdiff4") return "base_patch_rev1.bsdiff4";
            if (stepName == "apply_tokens")  return "token_data_rev1.bin";
        }

        // Pokémon Crystal (pokemon_crystal/rom.py): same revision idea as FRLG
        // but with the byte at 332 (the world's AP_ROM_Revision address, from
        // its data.json) and only the bsdiff step dispatching. A patch made
        // without 1.1 support simply lacks basepatch11.bsdiff4, and the
        // missing-file error is the same refusal their own code raises.
        if (string.Equals(game, "Pokemon Crystal", StringComparison.Ordinal)
            && rom.Length > 332 && rom[332] == 1
            && stepName == "apply_bsdiff4")
            return "basepatch11.bsdiff4";

        return null;
    }

    /// Steps a game's Python runs that are, for us, deliberate no-ops.
    ///
    /// Pokémon Crystal's apply_overrides reads option_overrides out of the
    /// PLAYER'S host.yaml and rewrites the ROM accordingly -- a power-user
    /// feature of the Archipelago install, not of the patch. With no overrides
    /// configured their code returns the rom unchanged, and the launcher has no
    /// host.yaml at all, so unchanged IS the faithful replica here. A game not
    /// listed still refuses on the unknown step name.
    static bool IsKnownNoOpStep(string game, string stepName)
        => string.Equals(game, "Pokemon Crystal", StringComparison.Ordinal)
           && stepName == "apply_overrides";

    // --- the three steps -----------------------------------------------------

    /// BSDIFF40: a control stream of (copy-with-diff, copy-verbatim, seek)
    /// triples, plus the diff and extra byte streams. All three are bzip2.
    static byte[] ApplyBsdiff4(byte[] src, byte[] patch)
    {
        if (patch.Length < 32 ||
            patch[0] != 'B' || patch[1] != 'S' || patch[2] != 'D' || patch[3] != 'I' ||
            patch[4] != 'F' || patch[5] != 'F' || patch[6] != '4' || patch[7] != '0')
            throw new InvalidDataException("the base patch is not a BSDIFF40 stream");

        long lenControl = ReadInt64(patch, 8);
        long lenDiff = ReadInt64(patch, 16);
        long lenDst = ReadInt64(patch, 24);
        if (lenControl < 0 || lenDiff < 0 || lenDst < 0 ||
            32 + lenControl + lenDiff > patch.Length)
            throw new InvalidDataException("the base patch header is corrupt");

        byte[] control = BZip2.Decompress(Slice(patch, 32, lenControl));
        byte[] diff = BZip2.Decompress(Slice(patch, 32 + lenControl, lenDiff));
        byte[] extra = BZip2.Decompress(Slice(patch, 32 + lenControl + lenDiff,
                                              patch.LongLength - 32 - lenControl - lenDiff));

        var dst = new byte[lenDst];
        long cp = 0, dp = 0, ep = 0, posSrc = 0, posDst = 0;

        while (posDst < lenDst)
        {
            if (cp + 24 > control.LongLength)
                throw new InvalidDataException("the base patch ended mid-instruction");

            long x = ReadInt64(control, cp);
            long y = ReadInt64(control, cp + 8);
            long z = ReadInt64(control, cp + 16);
            cp += 24;

            if (x < 0 || y < 0 || posDst + x + y > lenDst ||
                posSrc + x > src.LongLength || dp + x > diff.LongLength ||
                ep + y > extra.LongLength)
                throw new InvalidDataException("the base patch does not fit this ROM");

            // Bytes that differ only slightly are stored as a difference from
            // the source, which is what makes bsdiff so much smaller than a
            // plain copy of the new file.
            for (long i = 0; i < x; i++)
                dst[posDst + i] = (byte)(src[posSrc + i] + diff[dp + i]);
            posDst += x; posSrc += x; dp += x;

            Array.Copy(extra, ep, dst, posDst, y);
            posDst += y; ep += y;

            posSrc += z;                        // z may be negative -- seek back
        }

        return dst;
    }

    /// Classic IPS, used by the worlds whose base patch is a .ips rather than a
    /// bsdiff. Records are 3-byte offset, 2-byte length, then that many bytes;
    /// a zero length means a run-length record instead.
    static byte[] ApplyIps(byte[] rom, byte[] patch)
    {
        if (patch.Length < 5 ||
            patch[0] != 'P' || patch[1] != 'A' || patch[2] != 'T' ||
            patch[3] != 'C' || patch[4] != 'H')
            throw new InvalidDataException("not an IPS stream (missing PATCH header)");

        var outp = new List<byte>(rom);
        int pos = 5;

        while (pos < patch.Length)
        {
            if (pos + 3 <= patch.Length &&
                patch[pos] == 'E' && patch[pos + 1] == 'O' && patch[pos + 2] == 'F')
            {
                pos += 3;
                if (pos + 3 <= patch.Length)
                {
                    int truncate = (patch[pos] << 16) | (patch[pos + 1] << 8) | patch[pos + 2];
                    if (truncate > 0 && truncate < outp.Count)
                        outp.RemoveRange(truncate, outp.Count - truncate);
                }
                break;
            }

            if (pos + 5 > patch.Length)
                throw new InvalidDataException("the IPS patch ended mid-record");

            int offset = (patch[pos] << 16) | (patch[pos + 1] << 8) | patch[pos + 2];
            int length = (patch[pos + 3] << 8) | patch[pos + 4];
            pos += 5;

            if (length == 0)
            {
                if (pos + 3 > patch.Length)
                    throw new InvalidDataException("the IPS patch ended mid-run");
                int runLen = (patch[pos] << 8) | patch[pos + 1];
                byte value = patch[pos + 2];
                pos += 3;
                Grow(outp, offset + runLen);
                for (int i = 0; i < runLen; i++) outp[offset + i] = value;
            }
            else
            {
                if (pos + length > patch.Length)
                    throw new InvalidDataException("the IPS patch ended mid-record");
                Grow(outp, offset + length);
                for (int i = 0; i < length; i++) outp[offset + i] = patch[pos + i];
                pos += length;
            }
        }

        return outp.ToArray();

        static void Grow(List<byte> b, int size)
        {
            while (b.Count < size) b.Add(0);
        }
    }

    /// The per-seed half: a list of writes that put THIS player's item and
    /// location data into the ROM. Layout is a u32 count, then per token a type
    /// byte, u32 offset, u32 size and that many bytes.
    static byte[] ApplyTokens(byte[] rom, byte[] tokens)
    {
        var outp = (byte[])rom.Clone();
        if (tokens.Length < 4)
            throw new InvalidDataException("the token data is truncated");

        uint count = BitConverter.ToUInt32(tokens, 0);
        int p = 4;

        for (uint i = 0; i < count; i++)
        {
            if (p + 9 > tokens.Length)
                throw new InvalidDataException("the token data is truncated");

            byte type = tokens[p];
            int offset = (int)BitConverter.ToUInt32(tokens, p + 1);
            int size = (int)BitConverter.ToUInt32(tokens, p + 5);
            p += 9;

            if (size < 0 || p + size > tokens.Length)
                throw new InvalidDataException("the token data is truncated");

            switch (type)
            {
                case 3: Require(offset, 1); outp[offset] &= tokens[p]; break;   // AND_8
                case 4: Require(offset, 1); outp[offset] |= tokens[p]; break;   // OR_8
                case 5: Require(offset, 1); outp[offset] ^= tokens[p]; break;   // XOR_8

                case 1:                                                          // COPY
                case 2:                                                          // RLE
                {
                    int length = (int)BitConverter.ToUInt32(tokens, p);
                    int value = (int)BitConverter.ToUInt32(tokens, p + 4);
                    Require(offset, length);
                    if (type == 1)
                    {
                        Require(value, length);
                        Array.Copy(outp, value, outp, offset, length);
                    }
                    else
                    {
                        for (int k = 0; k < length; k++) outp[offset + k] = (byte)(value & 0xFF);
                    }
                    break;
                }

                default:                                                         // WRITE
                    Require(offset, size);
                    Array.Copy(tokens, p, outp, offset, size);
                    break;
            }

            p += size;
        }

        return outp;

        void Require(int offset, int length)
        {
            if (offset < 0 || length < 0 || (long)offset + length > outp.LongLength)
                throw new InvalidDataException(
                    "the patch writes past the end of the ROM -- this is not the "
                  + "file it was built for.");
        }
    }

    // bsdiff stores its integers sign-and-magnitude, NOT two's complement: the
    // top bit is the sign and the rest is the size. Reading these as ordinary
    // little-endian longs gives correct-looking small numbers and nonsense
    // negatives, so the seek offsets quietly go wrong.
    static long ReadInt64(byte[] b, long off)
    {
        long magnitude = BitConverter.ToInt64(b, (int)off) & 0x7FFFFFFFFFFFFFFF;
        return (b[off + 7] & 0x80) != 0 ? -magnitude : magnitude;
    }

    static byte[] Slice(byte[] src, long offset, long length)
    {
        var b = new byte[length];
        Array.Copy(src, offset, b, 0, length);
        return b;
    }
}
