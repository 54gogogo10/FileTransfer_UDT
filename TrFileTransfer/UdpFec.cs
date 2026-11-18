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
        // ------------------------------------------------------------------
        // Systematic Vandermonde codec — wire-compatible with the Rust v2
        // implementation (reed-solomon-erasure 6.0, galois_8): the generator
        // matrix is V·inv(V_top) with V[r][c] = r^c over GF(256) / 0x11D.
        // The one-way UDP v2 protocol (Rust trft) uses this, not the Cauchy
        // code above, so parity bytes only interoperate with these routines.
        // ------------------------------------------------------------------

        private static readonly System.Collections.Generic.Dictionary<int, byte[][]> VmCache
            = new System.Collections.Generic.Dictionary<int, byte[][]>();
        private static readonly object VmCacheLock = new object();

        /// <summary>Systematic generator matrix (data+parity rows × data columns):
        /// the first dataShards rows are the identity, the rest are parity rows.</summary>
        public static byte[][] VandermondeMatrix(int dataShards, int parityShards)
        {
            if (dataShards < 1 || parityShards < 1 || dataShards + parityShards > 255)
                throw new ArgumentException("invalid Vandermonde shape");
            lock (VmCacheLock)
            {
                byte[][] cached;
                if (VmCache.TryGetValue(parityShards, out cached) && cached.Length == dataShards + parityShards)
                    return cached;
            }
            int total = dataShards + parityShards;
            var v = new byte[total][];
            for (int row = 0; row < total; row++)
            {
                v[row] = new byte[dataShards];
                for (int col = 0; col < dataShards; col++)
                    v[row][col] = Pow((byte)row, col);
            }
            var top = new byte[dataShards][];
            for (int i = 0; i < dataShards; i++) top[i] = (byte[])v[i].Clone();
            var inv = Invert(top, dataShards);
            var g = new byte[total][];
            for (int i = 0; i < total; i++) g[i] = MultiplyRow(v[i], inv, dataShards);
            lock (VmCacheLock) { VmCache[parityShards] = g; }
            return g;
        }

        /// <summary>Parity shards for one FEC group. Every data shard is exactly
        /// <paramref name="length"/> bytes (the caller zero-pads the last chunk).</summary>
        public static byte[][] VandermondeEncode(byte[][] dataShards, int parityShards, int length)
        {
            int k = dataShards.Length;
            var g = VandermondeMatrix(k, parityShards);
            var parity = new byte[parityShards][];
            for (int p = 0; p < parityShards; p++)
            {
                var acc = new byte[length];
                for (int i = 0; i < k; i++)
                    Accumulate(acc, dataShards[i], length, g[k + p][i]);
                parity[p] = acc;
            }
            return parity;
        }

        /// <summary>Recovers missing data shards of one group from any k available
        /// shards (data or parity), using the same systematic Vandermonde code.
        /// <paramref name="shardIndices"/> are the FEC positions of the available
        /// shards; a data position past the file's chunk count is passed as an
        /// all-zero shard (the sender pads it the same way). Returns one block per
        /// requested missing data position, or null when fewer than k shards are
        /// available / the matrix is singular.</summary>
        public static byte[][] VandermondeReconstruct(int[] shardIndices, byte[][] shardBlocks,
            int[] missingDataIndices, int length, int dataShards, int parityShards)
        {
            if (missingDataIndices == null || missingDataIndices.Length == 0) return new byte[0][];
            if (shardIndices == null || shardBlocks == null || shardIndices.Length < dataShards)
                return null;
            var g = VandermondeMatrix(dataShards, parityShards);
            var sub = new byte[dataShards][];
            for (int r = 0; r < dataShards; r++)
            {
                int idx = shardIndices[r];
                if (idx < 0 || idx >= dataShards + parityShards) return null;
                sub[r] = g[idx];
            }
            var inv = Invert(sub, dataShards);
            if (inv == null) return null;
            var data = new byte[dataShards][];
            for (int d = 0; d < dataShards; d++)
            {
                var acc = new byte[length];
                for (int s = 0; s < dataShards; s++)
                    Accumulate(acc, shardBlocks[s], length, inv[d][s]);
                data[d] = acc;
            }
            var result = new byte[missingDataIndices.Length][];
            for (int i = 0; i < missingDataIndices.Length; i++)
            {
                int m = missingDataIndices[i];
                if (m < 0 || m >= dataShards) return null;
                result[i] = data[m];
            }
            return result;
        }

        private static byte Pow(byte a, int n)
        {
            if (n == 0) return 1;
            if (a == 0) return 0;
            int l = (Log[a] * n) % 255;
            return Exp[l];
        }

        private static byte Div(byte a, byte b)
        {
            if (a == 0) return 0;
            if (b == 0) throw new ArgumentException("division by zero");
            int l = Log[a] - Log[b];
            if (l < 0) l += 255;
            return Exp[l];
        }

        private static byte[] MultiplyRow(byte[] row, byte[][] matrix, int n)
        {
            var result = new byte[n];
            for (int c = 0; c < n; c++)
            {
                byte acc = 0;
                for (int j = 0; j < n; j++)
                    if (row[j] != 0 && matrix[j][c] != 0) acc ^= Mul(row[j], matrix[j][c]);
                result[c] = acc;
            }
            return result;
        }

        /// <summary>Gauss-Jordan inverse over GF(256); null when singular.</summary>
        private static byte[][] Invert(byte[][] matrix, int n)
        {
            var a = new byte[n][];
            var inv = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                a[i] = (byte[])matrix[i].Clone();
                inv[i] = new byte[n];
                inv[i][i] = 1;
            }
            for (int col = 0; col < n; col++)
            {
                int pivot = -1;
                for (int r = col; r < n; r++)
                    if (a[r][col] != 0) { pivot = r; break; }
                if (pivot < 0) return null;
                if (pivot != col)
                {
                    var t = a[pivot]; a[pivot] = a[col]; a[col] = t;
                    var ti = inv[pivot]; inv[pivot] = inv[col]; inv[col] = ti;
                }
                byte pv = a[col][col];
                if (pv != 1)
                {
                    byte ip = Div(1, pv);
                    for (int c = 0; c < n; c++)
                    {
                        a[col][c] = Mul(a[col][c], ip);
                        inv[col][c] = Mul(inv[col][c], ip);
                    }
                }
                for (int r = 0; r < n; r++)
                {
                    if (r == col || a[r][col] == 0) continue;
                    byte f = a[r][col];
                    for (int c = 0; c < n; c++)
                    {
                        a[r][c] ^= Mul(a[col][c], f);
                        inv[r][c] ^= Mul(inv[col][c], f);
                    }
                }
            }
            return inv;
        }

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
