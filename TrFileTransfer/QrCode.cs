using System;
using System.Collections.Generic;
using System.Text;

namespace TrFileTransfer
{
    /// <summary>
    /// Minimal QR Code encoder (pure C#, no external dependencies). Byte mode only,
    /// error-correction levels L and M, versions 1-10 — sized for URLs like the
    /// HTTP share address (about 40-220 bytes). Follows ISO/IEC 18004: mask selection
    /// with penalty scoring, BCH format bits, and version info for version >= 7.
    /// Must stay C# 5: the test build compiles this file with the old csc.exe.
    /// </summary>
    public static class QrEncoder
    {
        /// <summary>Maximum supported payload for a given (level, capacity) combination is
        /// checked at encode time; beyond version 10 M (213 bytes) this encoder throws.</summary>
        public const int MaxVersion = 10;

        /// <summary>ECC level: L = 7% recovery, M = 15%.</summary>
        public enum EccLevel { L = 1, M = 0 }

        /// <summary>Result of an encode: square module matrix, true = dark.</summary>
        public sealed class QrMatrix
        {
            public bool[,] Modules;
            public int Size;
            public int Version;
            public EccLevel Level;
        }

        // ---- Galois field GF(256), primitive polynomial 0x11D ----

        private static readonly int[] GfExp = new int[512];
        private static readonly int[] GfLog = new int[256];

        static QrEncoder()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                GfExp[i] = x;
                GfLog[x] = i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++)
                GfExp[i] = GfExp[i - 255];
        }

        private static int GfMul(int a, int b)
        {
            if (a == 0 || b == 0) return 0;
            return GfExp[GfLog[a] + GfLog[b]];
        }

        /// <summary>Reed-Solomon ECC codewords for one data block.</summary>
        private static byte[] RsComputeEcc(byte[] data, int degree)
        {
            // Generator polynomial = product of (x - alpha^i) for i in 0..degree-1
            var generator = new int[degree];
            generator[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                // Multiply the current generator by (x - alpha^i)
                for (int j = 0; j < degree; j++)
                {
                    generator[j] = GfMul(generator[j], root);
                    if (j + 1 < degree)
                        generator[j] ^= generator[j + 1];
                }
                root = GfMul(root, 2);
            }

            var result = new byte[degree];
            for (int i = 0; i < data.Length; i++)
            {
                int factor = data[i] ^ result[0];
                Array.Copy(result, 1, result, 0, degree - 1);
                result[degree - 1] = 0;
                for (int j = 0; j < degree; j++)
                    result[j] ^= (byte)GfMul(factor, generator[j]);
            }
            return result;
        }

        // ---- Version tables (levels L and M only) ----

        // ECC codewords per block, indexed [level, version] (version 1..MaxVersion).
        // Row order matches the EccLevel enum values, which double as the ISO format
        // bits: M = 0, L = 1.
        private static readonly int[,] EccPerBlock =
        {
            { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 }, // M
            { 0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18 },  // L
        };

        // Number of error-correction blocks, indexed [level, version]
        private static readonly int[,] EccBlocks =
        {
            { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 },  // M
            { 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4 },  // L
        };

        private static int RawDataModules(int version)
        {
            int result = (16 * version + 128) * version + 64;
            if (version >= 2)
            {
                int numAlign = version / 7 + 2;
                result -= (25 * numAlign - 10) * numAlign - 55;
                if (version >= 7)
                    result -= 36;
            }
            return result;
        }

        private static int DataCodewords(int version, EccLevel level)
        {
            int idx = (int)level;
            return RawDataModules(version) / 8 - EccPerBlock[idx, version] * EccBlocks[idx, version];
        }

        private static int[] AlignmentPositions(int version)
        {
            if (version == 1) return new int[0];
            int numAlign = version / 7 + 2;
            int step = (version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
            var result = new int[numAlign];
            result[0] = 6;
            int pos = version * 4 + 10;
            for (int i = numAlign - 1; i >= 1; i--)
            {
                result[i] = pos;
                pos -= step;
            }
            return result;
        }

        // ---- Public API ----

        /// <summary>Encodes UTF-8 text into a QR matrix. Throws ArgumentException when
        /// the payload does not fit the supported versions.</summary>
        public static QrMatrix Encode(string text)
        {
            if (text == null) text = "";
            byte[] data = Encoding.UTF8.GetBytes(text);
            if (data.Length == 0)
                throw new ArgumentException("QR payload is empty");

            // Pick the smallest version that fits; try M first for better scan robustness
            foreach (EccLevel level in new EccLevel[] { EccLevel.M, EccLevel.L })
            {
                for (int version = 1; version <= MaxVersion; version++)
                {
                    int capacity = DataCodewords(version, level);
                    int ccBits = version <= 9 ? 8 : 16;
                    long neededBits = 4 + ccBits + (long)data.Length * 8;
                    // Reserve at least the 4-bit terminator
                    if (neededBits + 4 > (long)capacity * 8)
                        continue;
                    return BuildMatrix(data, version, level, capacity, ccBits);
                }
            }
            throw new ArgumentException(string.Format(
                "QR payload too large ({0} bytes; max {1} bytes at version {2} M)",
                data.Length, DataCodewords(MaxVersion, EccLevel.M) - 3, MaxVersion));
        }

        private static QrMatrix BuildMatrix(byte[] data, int version, EccLevel level, int capacity, int ccBits)
        {
            byte[] allCodewords = AssembleCodewords(data, version, level, capacity, ccBits);
            var modules = new bool[version * 4 + 17, version * 4 + 17];
            var isFunction = new bool[modules.GetLength(0), modules.GetLength(1)];
            DrawFunctionPatterns(modules, isFunction, version, level);
            DrawCodewords(modules, isFunction, allCodewords);
            ApplyBestMask(modules, isFunction, version, level);
            return new QrMatrix { Modules = modules, Size = modules.GetLength(0), Version = version, Level = level };
        }

        /// <summary>Mode indicator, length, data, terminator and pad bytes → interleaved codewords.</summary>
        private static byte[] AssembleCodewords(byte[] data, int version, EccLevel level, int capacity, int ccBits)
        {
            var bits = new List<bool>();
            AppendBits(bits, 4, 4);                       // byte mode indicator 0100
            AppendBits(bits, data.Length, ccBits);        // character count
            for (int i = 0; i < data.Length; i++)
                AppendBits(bits, data[i], 8);

            int capacityBits = capacity * 8;
            int terminator = Math.Min(4, capacityBits - bits.Count);
            AppendBits(bits, 0, terminator);
            if (bits.Count % 8 != 0)
                AppendBits(bits, 0, 8 - bits.Count % 8);

            var codewords = new List<byte>();
            for (int i = 0; i < bits.Count; i += 8)
            {
                int b = 0;
                for (int j = 0; j < 8; j++)
                    b = (b << 1) | (bits[i + j] ? 1 : 0);
                codewords.Add((byte)b);
            }
            for (int pad = 0xEC; codewords.Count < capacity; pad ^= 0xEC ^ 0x11)
                codewords.Add((byte)pad);

            // Split into blocks (the first data % blocks blocks are one codeword longer)
            int idx = (int)level;
            int eccPer = EccPerBlock[idx, version];
            int numBlocks = EccBlocks[idx, version];
            int shortBlock = capacity / numBlocks;
            int numLong = capacity % numBlocks;

            var blocks = new List<byte[]>();
            var eccLists = new List<byte[]>();
            int offset = 0;
            for (int i = 0; i < numBlocks; i++)
            {
                int len = shortBlock + (i < numLong ? 1 : 0);
                var block = new byte[len];
                codewords.CopyTo(offset, block, 0, len);
                offset += len;
                blocks.Add(block);
                eccLists.Add(RsComputeEcc(block, eccPer));
            }

            // Interleave data, then ECC, column by column
            var result = new List<byte>();
            int maxLen = shortBlock + (numLong > 0 ? 1 : 0);
            for (int col = 0; col < maxLen; col++)
            {
                for (int i = 0; i < numBlocks; i++)
                {
                    if (col < blocks[i].Length)
                        result.Add(blocks[i][col]);
                }
            }
            for (int col = 0; col < eccPer; col++)
            {
                for (int i = 0; i < numBlocks; i++)
                    result.Add(eccLists[i][col]);
            }
            return result.ToArray();
        }

        private static void AppendBits(List<bool> bits, int value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
                bits.Add(((value >> i) & 1) != 0);
        }

        // ---- Matrix drawing ----

        private static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunction, int version, EccLevel level)
        {
            int size = modules.GetLength(0);

            // Timing patterns
            for (int i = 0; i < size; i++)
            {
                SetFunction(modules, isFunction, 6, i, i % 2 == 0);
                SetFunction(modules, isFunction, i, 6, i % 2 == 0);
            }

            // Finder patterns + separators
            DrawFinder(modules, isFunction, 3, 3);
            DrawFinder(modules, isFunction, size - 4, 3);
            DrawFinder(modules, isFunction, 3, size - 4);

            // Alignment patterns
            int[] align = AlignmentPositions(version);
            for (int i = 0; i < align.Length; i++)
            {
                for (int j = 0; j < align.Length; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == align.Length - 1) || (i == align.Length - 1 && j == 0))
                        continue; // overlaps a finder pattern
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                            SetFunction(modules, isFunction, align[i] + dy, align[j] + dx, d != 1);
                        }
                    }
                }
            }

            // Reserve format info (drawn later, value depends on the chosen mask)
            DrawFormatBits(modules, isFunction, level, 0);

            // Version info blocks (version >= 7)
            if (version >= 7)
                DrawVersionBits(modules, isFunction, version);
        }

        private static void DrawFinder(bool[,] modules, bool[,] isFunction, int x, int y)
        {
            int size = modules.GetLength(0);
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || xx >= size || yy < 0 || yy >= size) continue;
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunction(modules, isFunction, yy, xx, dist != 2 && dist != 4);
                }
            }
        }

        /// <summary>Draws (or redraws) the 15-bit BCH-coded format information plus the
        /// dark module. SetFunction's argument order is (row, col); the layout below is
        /// written against ISO/IEC 18004 figures 25/26.</summary>
        private static void DrawFormatBits(bool[,] modules, bool[,] isFunction, EccLevel level, int mask)
        {
            int size = modules.GetLength(0);
            int data = (int)level << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++)
                rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            // First copy: around the top-left finder
            for (int i = 0; i <= 5; i++)
                SetFunction(modules, isFunction, i, 8, GetBit(bits, i));      // row i, col 8
            SetFunction(modules, isFunction, 7, 8, GetBit(bits, 6));
            SetFunction(modules, isFunction, 8, 8, GetBit(bits, 7));
            SetFunction(modules, isFunction, 8, 7, GetBit(bits, 8));
            for (int i = 9; i < 15; i++)
                SetFunction(modules, isFunction, 8, 14 - i, GetBit(bits, i)); // row 8, col 14-i

            // Second copy: horizontal strip under the top-right finder + vertical
            // strip right of the bottom-left finder
            for (int i = 0; i < 8; i++)
                SetFunction(modules, isFunction, 8, size - 1 - i, GetBit(bits, i));
            for (int i = 8; i < 15; i++)
                SetFunction(modules, isFunction, size - 15 + i, 8, GetBit(bits, i));
            SetFunction(modules, isFunction, size - 8, 8, true); // dark module (row size-8, col 8)
        }

        private static void DrawVersionBits(bool[,] modules, bool[,] isFunction, int version)
        {
            int size = modules.GetLength(0);
            int rem = version;
            for (int i = 0; i < 12; i++)
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = (version << 12) | rem;

            for (int i = 0; i < 18; i++)
            {
                bool bit = GetBit(bits, i);
                int a = size - 11 + i % 3;
                int b = i / 3;
                SetFunction(modules, isFunction, a, b, bit);
                SetFunction(modules, isFunction, b, a, bit);
            }
        }

        private static void DrawCodewords(bool[,] modules, bool[,] isFunction, byte[] codewords)
        {
            int size = modules.GetLength(0);
            int bitIndex = 0;
            int totalBits = codewords.Length * 8;

            // Zigzag: column pairs from the right, skipping the timing column 6
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (!isFunction[y, x] && bitIndex < totalBits)
                        {
                            modules[y, x] = GetBit(codewords[bitIndex >> 3], 7 - (bitIndex & 7));
                            bitIndex++;
                        }
                    }
                }
            }
        }

        private static void ApplyBestMask(bool[,] modules, bool[,] isFunction, int version, EccLevel level)
        {
            int size = modules.GetLength(0);
            int bestMask = 0;
            long bestPenalty = long.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(modules, isFunction, mask);
                DrawFormatBits(modules, isFunction, level, mask);
                long penalty = PenaltyScore(modules);
                if (penalty < bestPenalty)
                {
                    bestPenalty = penalty;
                    bestMask = mask;
                }
                ApplyMask(modules, isFunction, mask); // undo (XOR twice = identity)
            }
            ApplyMask(modules, isFunction, bestMask);
            DrawFormatBits(modules, isFunction, level, bestMask);
        }

        private static void ApplyMask(bool[,] modules, bool[,] isFunction, int mask)
        {
            int size = modules.GetLength(0);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (isFunction[y, x]) continue;
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (y + x) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (y + x) % 3 == 0; break;
                        case 4: invert = (y / 2 + x / 3) % 2 == 0; break;
                        case 5: invert = y * x % 2 + y * x % 3 == 0; break;
                        case 6: invert = (y * x % 2 + y * x % 3) % 2 == 0; break;
                        default: invert = ((y + x) % 2 + y * x % 3) % 2 == 0; break;
                    }
                    modules[y, x] ^= invert;
                }
            }
        }

        // ---- Mask penalty (ISO rules 1-4) ----

        private static long PenaltyScore(bool[,] modules)
        {
            int size = modules.GetLength(0);
            long result = 0;

            // Rule 1: runs of 5+ same-color modules
            for (int y = 0; y < size; y++)
            {
                bool runColor = modules[y, 0];
                int runLen = 1;
                for (int x = 1; x < size; x++)
                {
                    if (modules[y, x] == runColor) runLen++;
                    else { if (runLen >= 5) result += runLen - 2; runColor = modules[y, x]; runLen = 1; }
                }
                if (runLen >= 5) result += runLen - 2;
            }
            for (int x = 0; x < size; x++)
            {
                bool runColor = modules[0, x];
                int runLen = 1;
                for (int y = 1; y < size; y++)
                {
                    if (modules[y, x] == runColor) runLen++;
                    else { if (runLen >= 5) result += runLen - 2; runColor = modules[y, x]; runLen = 1; }
                }
                if (runLen >= 5) result += runLen - 2;
            }

            // Rule 2: 2x2 blocks of the same color
            for (int y = 0; y < size - 1; y++)
            {
                for (int x = 0; x < size - 1; x++)
                {
                    bool c = modules[y, x];
                    if (c == modules[y, x + 1] && c == modules[y + 1, x] && c == modules[y + 1, x + 1])
                        result += 3;
                }
            }

            // Rule 3: finder-like patterns (1011101 with 4 light modules on one side)
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size - 6; x++)
                {
                    bool[] pat = new bool[7];
                    for (int i = 0; i < 7; i++) pat[i] = modules[y, x + i];
                    result += FinderPatternPenalty(pat);
                }
            }
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size - 6; y++)
                {
                    bool[] pat = new bool[7];
                    for (int i = 0; i < 7; i++) pat[i] = modules[y + i, x];
                    result += FinderPatternPenalty(pat);
                }
            }

            // Rule 4: dark-module proportion deviation
            int dark = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (modules[y, x]) dark++;
            int total = size * size;
            int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            result += (long)k * 10;

            return result;
        }

        private static long FinderPatternPenalty(bool[] pat)
        {
            bool[] target = { true, false, true, true, true, false, true };
            for (int i = 0; i < 7; i++)
            {
                if (pat[i] != target[i]) return 0;
            }
            return 40;
        }

        private static void SetFunction(bool[,] modules, bool[,] isFunction, int y, int x, bool dark)
        {
            modules[y, x] = dark;
            isFunction[y, x] = true;
        }

        private static bool GetBit(int x, int i)
        {
            return ((x >> i) & 1) != 0;
        }
    }
}
