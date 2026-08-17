using System.IO;

namespace LauncherV2.Core.Patching;

/// bzip2 DEcompression, and nothing else.
///
/// Why this exists: an Archipelago .ap&lt;game&gt; patch carries a BSDIFF40 base
/// patch, and BSDIFF40 stores its three sections as bzip2 streams. .NET ships
/// Deflate, GZip, ZLib and Brotli in the box -- but no bzip2. The choice was a
/// third-party binary or these few hundred lines, and the launcher deliberately
/// ships no third-party binaries.
///
/// Only the decoder is here. Compression needs Burrows-Wheeler sorting and
/// Huffman tree construction; decoding only has to read the tables back and
/// undo the transform, which is a fraction of the work and all we ever do.
///
/// Correctness is not taken on faith: tools/PatchCheck applies a real seed
/// patch and compares the result byte-for-byte with Archipelago's own Python
/// patcher.
internal static class BZip2
{
    const int MaxCodeLen = 23;
    const int RunA = 0, RunB = 1;
    const int GroupSize = 50;

    const ulong BlockMagic = 0x314159265359UL;   // pi
    const ulong EndMagic   = 0x177245385090UL;   // sqrt(pi)

    public static byte[] Decompress(byte[] data)
    {
        var br = new BitReader(data);
        if (br.ReadBits(8) != 'B' || br.ReadBits(8) != 'Z' || br.ReadBits(8) != 'h')
            throw new InvalidDataException("not a bzip2 stream");

        int level = (int)br.ReadBits(8) - '0';
        if (level < 1 || level > 9)
            throw new InvalidDataException($"bad bzip2 block-size level {level}");

        var outp = new MemoryStream();
        while (true)
        {
            ulong magic = br.ReadBits48();
            if (magic == EndMagic)
            {
                br.ReadBits(32);              // combined CRC, not checked
                break;
            }
            if (magic != BlockMagic)
                throw new InvalidDataException("corrupt bzip2 block header");

            br.ReadBits(32);                  // block CRC, not checked
            if (br.ReadBit())
                throw new InvalidDataException(
                    "randomised bzip2 blocks are not supported (deprecated since 0.9.5; "
                  + "no encoder in use today produces them)");

            DecodeBlock(br, level * 100_000, outp);
        }
        return outp.ToArray();
    }

    static void DecodeBlock(BitReader br, int blockSize, MemoryStream outp)
    {
        int origPtr = (int)br.ReadBits(24);

        // Which byte values occur in this block, as a two-level bitmap: 16 bits
        // say which groups of 16 are used, then 16 bits per used group.
        var inUse = new bool[256];
        uint used16 = br.ReadBits(16);
        for (int i = 0; i < 16; i++)
        {
            if ((used16 & (1 << (15 - i))) == 0) continue;
            uint bits = br.ReadBits(16);
            for (int j = 0; j < 16; j++)
                if ((bits & (1 << (15 - j))) != 0) inUse[i * 16 + j] = true;
        }

        var seqToUnseq = new byte[256];
        int symbolCount = 0;
        for (int i = 0; i < 256; i++)
            if (inUse[i]) seqToUnseq[symbolCount++] = (byte)i;
        if (symbolCount == 0)
            throw new InvalidDataException("bzip2 block uses no symbols");

        // +2: the two run-length symbols at the bottom and end-of-block at the top.
        int alphaSize = symbolCount + 2;
        int eob = alphaSize - 1;

        int nGroups = (int)br.ReadBits(3);
        int nSelectors = (int)br.ReadBits(15);
        if (nGroups < 2 || nGroups > 6)
            throw new InvalidDataException($"bad bzip2 group count {nGroups}");
        if (nSelectors < 1)
            throw new InvalidDataException("bzip2 block has no selectors");

        // Selectors say which Huffman table each 50-symbol run uses. They are
        // move-to-front coded and then written in unary.
        var mtfGroups = new byte[nGroups];
        for (byte i = 0; i < nGroups; i++) mtfGroups[i] = i;

        var selector = new byte[nSelectors];
        for (int i = 0; i < nSelectors; i++)
        {
            int j = 0;
            while (br.ReadBit())
                if (++j >= nGroups)
                    throw new InvalidDataException("bad bzip2 selector");

            byte pick = mtfGroups[j];
            for (int k = j; k > 0; k--) mtfGroups[k] = mtfGroups[k - 1];
            mtfGroups[0] = pick;
            selector[i] = pick;
        }

        // Code lengths, stored as a walk: a 1 bit means "adjust", and the bit
        // after it says which way.
        var len = new byte[nGroups][];
        for (int t = 0; t < nGroups; t++)
        {
            len[t] = new byte[alphaSize];
            int c = (int)br.ReadBits(5);
            for (int s = 0; s < alphaSize; s++)
            {
                while (br.ReadBit()) c += br.ReadBit() ? -1 : 1;
                if (c < 1 || c > MaxCodeLen)
                    throw new InvalidDataException($"bad bzip2 code length {c}");
                len[t][s] = (byte)c;
            }
        }

        // Canonical-Huffman decode tables. limit[n] is the largest code value of
        // length n, so "keep reading bits while the value is above the limit"
        // walks straight to the right length without a tree.
        var limit = new int[nGroups][];
        var codeBase = new int[nGroups][];
        var perm = new int[nGroups][];
        var minLens = new int[nGroups];
        var maxLens = new int[nGroups];

        for (int t = 0; t < nGroups; t++)
        {
            limit[t] = new int[MaxCodeLen + 2];
            codeBase[t] = new int[MaxCodeLen + 2];
            perm[t] = new int[alphaSize];

            int minLen = MaxCodeLen, maxLen = 0;
            foreach (byte l in len[t])
            {
                if (l > maxLen) maxLen = l;
                if (l < minLen) minLen = l;
            }
            minLens[t] = minLen;
            maxLens[t] = maxLen;

            int pp = 0;
            for (int i = minLen; i <= maxLen; i++)
                for (int s = 0; s < alphaSize; s++)
                    if (len[t][s] == i) perm[t][pp++] = s;

            for (int s = 0; s < alphaSize; s++) codeBase[t][len[t][s] + 1]++;
            for (int i = 1; i < MaxCodeLen + 2; i++) codeBase[t][i] += codeBase[t][i - 1];

            int vec = 0;
            for (int i = minLen; i <= maxLen; i++)
            {
                vec += codeBase[t][i + 1] - codeBase[t][i];
                limit[t][i] = vec - 1;
                vec <<= 1;
            }
            for (int i = minLen + 1; i <= maxLen; i++)
                codeBase[t][i] = ((limit[t][i - 1] + 1) << 1) - codeBase[t][i];
        }

        // tt doubles as the BWT working array: the low 8 bits hold the byte, and
        // the inverse transform later packs a pointer into the upper bits.
        var tt = new int[blockSize];
        var counts = new int[256];
        var mtf = new byte[256];
        for (int i = 0; i < symbolCount; i++) mtf[i] = (byte)i;

        int nblock = 0, groupNo = -1, groupPos = 0, table = 0;

        int NextSymbol()
        {
            if (groupPos == 0)
            {
                if (++groupNo >= nSelectors)
                    throw new InvalidDataException("bzip2 block ran past its selectors");
                groupPos = GroupSize;
                table = selector[groupNo];
            }
            groupPos--;

            int zn = minLens[table];
            int zvec = (int)br.ReadBits(zn);
            while (zvec > limit[table][zn])
            {
                if (++zn > maxLens[table])
                    throw new InvalidDataException("corrupt bzip2 Huffman code");
                zvec = (zvec << 1) | (br.ReadBit() ? 1 : 0);
            }
            int idx = zvec - codeBase[table][zn];
            if (idx < 0 || idx >= alphaSize)
                throw new InvalidDataException("corrupt bzip2 Huffman code");
            return perm[table][idx];
        }

        int sym = NextSymbol();
        while (sym != eob)
        {
            if (sym == RunA || sym == RunB)
            {
                // Runs of the most-recent symbol are counted in bijective base 2,
                // which unlike plain binary has no ambiguous leading-zero form.
                int run = 0, shift = 0;
                do
                {
                    run += (sym == RunA ? 1 : 2) << shift;
                    shift++;
                    sym = NextSymbol();
                }
                while (sym == RunA || sym == RunB);

                byte b = seqToUnseq[mtf[0]];
                if (nblock + run > blockSize)
                    throw new InvalidDataException("bzip2 block overruns its declared size");
                counts[b] += run;
                for (int i = 0; i < run; i++) tt[nblock++] = b;
                continue;                      // sym already holds the next symbol
            }

            int v = sym - 1;                   // symbols 1..n are move-to-front indices
            byte pick = mtf[v];
            for (int k = v; k > 0; k--) mtf[k] = mtf[k - 1];
            mtf[0] = pick;

            byte value = seqToUnseq[pick];
            if (nblock >= blockSize)
                throw new InvalidDataException("bzip2 block overruns its declared size");
            counts[value]++;
            tt[nblock++] = value;
            sym = NextSymbol();
        }

        if (origPtr < 0 || origPtr >= nblock)
            throw new InvalidDataException("bzip2 block pointer is outside the block");

        // Undo the Burrows-Wheeler transform. Sorting the bytes gives the first
        // column of the matrix; threading each byte back to its position in the
        // last column gives a linked list that walks the original text.
        var cftab = new int[257];
        for (int i = 0; i < 256; i++) cftab[i + 1] = counts[i];
        for (int i = 1; i <= 256; i++) cftab[i] += cftab[i - 1];
        for (int i = 0; i < nblock; i++)
        {
            int ch = tt[i] & 0xFF;
            tt[cftab[ch]] |= i << 8;
            cftab[ch]++;
        }

        // Final stage undoes the run-length pass the encoder applied FIRST: four
        // equal bytes are followed by a count of how many more to emit.
        int tPos = tt[origPtr] >> 8;
        int prev = -1, runLen = 0;
        for (int i = 0; i < nblock; i++)
        {
            byte b = (byte)(tt[tPos] & 0xFF);
            tPos = tt[tPos] >> 8;

            if (runLen == 4)
            {
                for (int k = 0; k < b; k++) outp.WriteByte((byte)prev);
                runLen = 0;
                prev = -1;
                continue;
            }

            if (b == prev) runLen++;
            else { runLen = 1; prev = b; }
            outp.WriteByte(b);
        }
    }

    /// Most-significant-bit-first bit reader. bzip2 is defined on a bit stream
    /// that pays no attention to byte boundaries, so everything goes through here.
    sealed class BitReader(byte[] data)
    {
        readonly byte[] _d = data;
        int _pos, _bit;

        public bool ReadBit()
        {
            if (_pos >= _d.Length)
                throw new InvalidDataException("bzip2 stream ended early");
            bool v = (_d[_pos] & (1 << (7 - _bit))) != 0;
            if (++_bit == 8) { _bit = 0; _pos++; }
            return v;
        }

        public uint ReadBits(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++) v = (v << 1) | (ReadBit() ? 1u : 0u);
            return v;
        }

        public ulong ReadBits48()
        {
            ulong v = 0;
            for (int i = 0; i < 48; i++) v = (v << 1) | (ReadBit() ? 1UL : 0UL);
            return v;
        }
    }
}
