using System;

namespace TrFileTransfer
{
    /// <summary>
    /// Reed-Solomon ERASURE coding over GF(256) for the one-way UDP FEC. The channel
    /// is pure erasure (a lost datagram is KNOWN to be lost — its chunk index simply
    /// never arrives), so only erasure decoding is needed: no syndrome/locator search,
    /// just a small linear solve over GF(256). The code is systematic:
    ///   parity_j = Σ_i A[j][i] · data_i,   A[j][i] = 1 / (j ⊕ (GroupSize + i))
    /// — a Cauchy matrix (x_j = j for parity rows, y_i = GroupSize + i for data
    /// columns; the ranges never collide, so the denominator is never zero). Every
    /// square submatrix of a Cauchy matrix is invertible, which is exactly the MDS
    /// property needed: any ≤ m erasures per group are recoverable from any m
    /// surviving parity rows, regardless of which positions they sit in.
    /// Must stay C# 5: the test build compiles this file with the old csc.exe.
    /// </summary>
    public static class UdpFec
    {
        // GF(256) with primitive polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D)
        private static readonly byte[] Exp = new byte[512];
        private static readonly byte[] Log = new byte[256];

        static UdpFec()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                Exp[i] = (byte)x;
                Log[x] = (byte)i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        }

        public static byte Mul(byte a, byte b)
        {
            if (a == 0 || b == 0) return 0;
            return Exp[Log[a] + Log[b]];
        }

        /// <summary>Cauchy coefficient for parity row j over data column i (both
        /// 0-based within the group; the column tag is the member position, so a
        /// sender and a receiver agree on it from the chunk index minus groupStart).
        /// </summary>
        public static byte Coefficient(int j, int i, int groupSize)
        {
            int tag = j ^ (groupSize + i);
            if (tag == 0 || tag >= 256) throw new ArgumentException("invalid Cauchy tag");
            return Exp[255 - Log[tag]]; // multiplicative inverse
        }

        /// <summary>XOR-accumulates one data block into parity row j:
        /// acc[b] ^= A[j][i] · data[b] for the first dataLen bytes. Incremental, so
        /// the sender never holds a whole group in memory; a short last chunk
        /// contributes only its existing bytes (zero-padding semantics).</summary>
        public static void Accumulate(byte[] acc, byte[] data, int dataLen, byte coef)
        {
            if (coef == 0 || data == null) return;
            if (coef == 1)
            {
                for (int b = 0; b < dataLen; b++) acc[b] ^= data[b];
                return;
            }
            int logC = Log[coef];
            for (int b = 0; b < dataLen; b++)
            {
                byte d = data[b];
                if (d != 0) acc[b] ^= Exp[logC + Log[d]];
            }
        }

        /// <summary>Recovers the missing data blocks of one group. Solving the e×e
        /// system (e = missing count) over GF(256) with Gauss-Jordan:
        ///   A[P_r][D_c] · x_c = parity_r ⊕ Σ_s A[P_r][S_s] · surviving_s
        /// where P_r are the surviving parity row indices, D_c the missing member
        /// columns, S_s the surviving member columns. Surviving blocks may be SHORT
        /// (the group's last chunk) — zero-padding semantics apply, so the caller
        /// passes their actual bytes and the result is zero-padded to chunkSize.
        /// Returns one block per missing column (chunkSize long), or null when
        /// fewer parity rows than erasures survive (keep the cache, wait for more)
        /// — a singular matrix cannot happen for a Cauchy code but is handled the
        /// same safe way.</summary>
        public static byte[][] Solve(byte[][] parityPayloads, int[] parityRowIdx,
            int[] survivingCols, byte[][] survivingBlocks, int[] missingCols, int chunkSize, int groupSize)
        {
            int e = missingCols.Length;
            if (e == 0) return new byte[0][];
            if (parityPayloads == null || parityRowIdx == null ||
                parityPayloads.Length < e || parityRowIdx.Length < e)
                return null;

            var M = new byte[e][];
            var rhs = new byte[e][];
            for (int r = 0; r < e; r++)
            {
                M[r] = new byte[e];
                for (int c = 0; c < e; c++)
                    M[r][c] = Coefficient(parityRowIdx[r], missingCols[c], groupSize);
                var line = new byte[chunkSize];
                Buffer.BlockCopy(parityPayloads[r], 0, line, 0, chunkSize);
                for (int s = 0; s < survivingCols.Length; s++)
                {
                    byte coef = Coefficient(parityRowIdx[r], survivingCols[s], groupSize);
                    Accumulate(line, survivingBlocks[s], survivingBlocks[s].Length, coef);
                }
                rhs[r] = line;
            }

            // Gauss-Jordan: reduce M to the identity, applying every operation to rhs
            for (int col = 0; col < e; col++)
            {
                int pivot = -1;
                for (int r = col; r < e; r++)
                {
                    if (M[r][col] != 0) { pivot = r; break; }
                }
                if (pivot < 0) return null; // singular — treated like "not enough parity"
                if (pivot != col)
                {
                    var tm = M[pivot]; M[pivot] = M[col]; M[col] = tm;
                    var tr = rhs[pivot]; rhs[pivot] = rhs[col]; rhs[col] = tr;
                }
                byte invP = Exp[255 - Log[M[col][col]]];
                if (invP != 1)
                {
                    for (int c = 0; c < e; c++) M[col][c] = Mul(M[col][c], invP);
                    for (int b = 0; b < chunkSize; b++) rhs[col][b] = Mul(rhs[col][b], invP);
                }
                for (int r = 0; r < e; r++)
                {
                    if (r == col || M[r][col] == 0) continue;
                    byte f = M[r][col];
                    for (int c = 0; c < e; c++)
                        if (M[col][c] != 0) M[r][c] ^= Mul(M[col][c], f);
                    // f == 1 shortcut is subsumed by the general path below
                    for (int b = 0; b < chunkSize; b++)
                    {
                        byte v = rhs[col][b];
                        if (v != 0) rhs[r][b] ^= Mul(v, f);
                    }
                }
            }

            var result = new byte[e][];
            for (int c = 0; c < e; c++) result[c] = rhs[c];
            return result;
        }
    }
}
