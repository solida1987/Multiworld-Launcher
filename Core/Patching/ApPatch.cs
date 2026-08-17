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
        string? BaseChecksum,
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
                Str(r, "base_checksum")?.ToLowerInvariant(),
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

        byte[] rom = File.ReadAllBytes(baseRomPath);

        if (!string.IsNullOrEmpty(manifest.BaseChecksum))
        {
            string actual = Convert.ToHexString(MD5.HashData(rom)).ToLowerInvariant();
            if (actual != manifest.BaseChecksum)
                throw new InvalidDataException(
                    "This is not the ROM the patch was built for.\n\n"
                  + $"The patch expects MD5 {manifest.BaseChecksum}\n"
                  + $"your file is    MD5 {actual}\n\n"
                  + "Use the exact dump the world asks for -- a different region or "
                  + "revision will patch into something that looks fine and then breaks.");
        }

        foreach (var step in manifest.Procedure)
        {
            byte[] Arg(int i)
            {
                if (i >= step.Args.Count || !files.TryGetValue(step.Args[i], out var b))
                    throw new InvalidDataException(
                        $"the patch is incomplete: step {step.Name} wants "
                      + $"{(i < step.Args.Count ? step.Args[i] : "a file")}, "
                      + "which is not inside the container.");
                return b;
            }

            rom = step.Name switch
            {
                "apply_bsdiff4" => ApplyBsdiff4(rom, Arg(0)),
                "apply_ips"     => ApplyIps(rom, Arg(0)),
                "apply_tokens"  => ApplyTokens(rom, Arg(0)),
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
