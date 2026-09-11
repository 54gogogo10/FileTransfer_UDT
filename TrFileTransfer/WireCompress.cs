using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>
    /// Segment-wise DEFLATE transport compression (0x08 negotiation). Every
    /// WriteExactAsync call becomes one self-contained wire segment:
    /// [Int32 flagsLen][payload] — the payload is the deflate of the written bytes,
    /// or the raw bytes with the length's high bit set when deflate would grow
    /// them (already-compressed media, tiny protocol headers). Reads reassemble
    /// segments into a pending buffer so arbitrary ReadExact/ReadSome boundaries
    /// work unchanged. State is per direction: the transfer pipelines overlap one
    /// reader and one writer on the same stream. Offsets negotiated by 0x03/0x04
    /// count UNcompressed logical bytes on both sides, so resume and the receive
    /// limiter keep working through the decorator.
    /// Must stay C# 5: the test build compiles this file with the old csc.exe.
    /// </summary>
    public class CompressedWireStream : IWireStream
    {
        /// <summary>Segments larger than this are refused — decompression-bomb
        /// guard (legitimate segments are bounded by the 4 MB transfer buffer).</summary>
        public const int MaxSegment = 64 * 1024 * 1024;

        /// <summary>Writes below this always go stored — deflating a protocol
        /// header costs more framing than it could ever save.</summary>
        private const int MinCompressible = 64;

        /// <summary>Bytes probed at the head of a segment to decide whether the whole
        /// segment is worth deflating. Keeping it small bounds the probe cost even on
        /// incompressible data (~1 ms at the measured ~51 MB/s, under 0.05% of a 4 MB
        /// segment) — the probe only has to avoid the far larger cost of deflating a
        /// segment that turns out to be stored anyway.</summary>
        public const int SampleBytes = 64 * 1024;

        /// <summary>A segment is deflated only when the probe predicts the result would
        /// be at most this fraction of the input — a deliberately conservative gate
        /// requiring at least 70% reduction.
        ///
        /// Why not "compress whenever it gets smaller": deflate measured on this exact
        /// code path runs ~51 MB/s on incompressible data but only ~16 MB/s on the worst
        /// semi-compressible data (repetitive enough to keep LZ77 searching, random
        /// enough never to match). Compression only pays when the link is slower than
        /// deflate divided by the saving, so a marginal win costs far more CPU than it
        /// saves — and on a fast link (loopback/gigabit) it is a pure loss. Requiring
        /// 70% keeps that pathological band out; the cost is declining to compress
        /// borderline data, which is the safe direction to err.
        ///
        /// Measured effect: incompressible 64 MB over loopback TCP went from 1.4s
        /// (deflate then fall back to stored) to ~0.2s (probe rejects, never deflates).</summary>
        public const double MaxCompressedRatio = 0.30;

        private const uint StoredFlag = 0x80000000;

        private readonly IWireStream _inner;
        private readonly bool _ownsInner;
        // Per-direction length scratch (a reader and a writer may run concurrently)
        private readonly byte[] _lenBufOut = new byte[4];
        private readonly byte[] _lenBufIn = new byte[4];
        // Read-side inflated bytes not yet consumed by the caller
        private byte[] _plain = Utils.EmptyBytes;
        private int _plainPos;
        private int _plainLen;
        private bool _disposed;

        /// <param name="ownsInner">False when the transport owns the inner stream: the
        /// server disposes the decorator to free its own state but must not close the
        /// socket before it has sent the 1-byte application ACK.</param>
        public CompressedWireStream(IWireStream inner)
            : this(inner, true)
        {
        }

        public CompressedWireStream(IWireStream inner, bool ownsInner)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            _inner = inner;
            _ownsInner = ownsInner;
        }

        public async Task WriteExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (count <= 0) return;

            // Probe the head before deflating anything: if that sample does not shrink
            // well, deflating the whole segment almost certainly will not either, so we
            // skip it and send stored — avoiding a full-segment deflate on data we
            // would only throw away (see MaxCompressedRatio).
            byte[] deflated = null;
            if (count >= MinCompressible && ProbeIsCompressible(buffer, offset, count))
                deflated = TryDeflate(buffer, offset, count);

            if (deflated != null && deflated.Length < count)
            {
                await WriteLenAsync((uint)deflated.Length, ct).ConfigureAwait(false);
                await _inner.WriteExactAsync(deflated, 0, deflated.Length, ct).ConfigureAwait(false);
            }
            else
            {
                await WriteLenAsync((uint)count | StoredFlag, ct).ConfigureAwait(false);
                await _inner.WriteExactAsync(buffer, offset, count, ct).ConfigureAwait(false);
            }
        }

        private async Task WriteLenAsync(uint len, CancellationToken ct)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(len), 0, _lenBufOut, 0, 4);
            await _inner.WriteExactAsync(_lenBufOut, 0, 4, ct).ConfigureAwait(false);
        }

        /// <summary>Deflates the head of the segment and reports whether it shrank by at
        /// least (1 - MaxCompressedRatio). Small segments are probed whole. Fails closed:
        /// any problem (or an unhelpful probe) means "not compressible", so a bad probe
        /// can only cost compression efficiency, never correctness.</summary>
        private static bool ProbeIsCompressible(byte[] buffer, int offset, int count)
        {
            int probeLen = count <= SampleBytes ? count : SampleBytes;
            if (probeLen < MinCompressible) return false;
            byte[] probe = TryDeflate(buffer, offset, probeLen);
            if (probe == null) return false;
            return probe.Length <= (long)(probeLen * MaxCompressedRatio);
        }

        private static byte[] TryDeflate(byte[] buffer, int offset, int count)
        {
            try
            {
                using (var ms = new MemoryStream(count / 4 + 64))
                {
                    // Fastest: measured ~2.5x the throughput of Optimal (and ~4x on
                    // pathological data) for a compression-ratio change small enough
                    // to be irrelevant next to the link speed (see MaxCompressedRatio).
                    using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, true))
                        ds.Write(buffer, offset, count);
                    // ds disposed above flushes the deflate tail into ms
                    return ms.ToArray();
                }
            }
            catch (IOException)
            {
                return null; // fall back to a stored segment
            }
        }

        public async Task<int> ReadSomeAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_plainPos >= _plainLen)
                await FillPendingAsync(ct).ConfigureAwait(false);
            if (_plainPos >= _plainLen)
                return 0; // clean EOF at a segment boundary
            int take = Math.Min(count, _plainLen - _plainPos);
            Buffer.BlockCopy(_plain, _plainPos, buffer, offset, take);
            _plainPos += take;
            return take;
        }

        public async Task ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int got = 0;            while (got < count)
            {
                if (_plainPos >= _plainLen)
                    await FillPendingAsync(ct).ConfigureAwait(false);
                if (_plainPos >= _plainLen)
                    throw new IOException("connection closed mid-segment");
                int take = Math.Min(count - got, _plainLen - _plainPos);
                Buffer.BlockCopy(_plain, _plainPos, buffer, offset + got, take);
                _plainPos += take;
                got += take;
            }
        }

        /// <summary>Forwarded: the raw socket underneath is the one that has a timeout, and
        /// the caller must still be able to widen it (see IWireStream.SetReadTimeoutMs).</summary>
        public void SetReadTimeoutMs(int milliseconds)
        {
            _inner.SetReadTimeoutMs(milliseconds);
        }

        /// <summary>Pulls the next wire segment and inflates it into the pending
        /// buffer. Premature closes surface as IOException from the inner reads.</summary>
        private async Task FillPendingAsync(CancellationToken ct)
        {
            _plain = Utils.EmptyBytes;
            _plainPos = 0;
            _plainLen = 0;

            await _inner.ReadExactAsync(_lenBufIn, 0, 4, ct).ConfigureAwait(false);
            uint lenAndFlag = BitConverter.ToUInt32(_lenBufIn, 0);
            uint len = lenAndFlag & ~StoredFlag;
            bool stored = (lenAndFlag & StoredFlag) != 0;
            if (len == 0 || len > MaxSegment)
                throw new IOException("invalid compressed segment length");

            if (stored)
            {
                _plain = new byte[len];
                await _inner.ReadExactAsync(_plain, 0, (int)len, ct).ConfigureAwait(false);
                _plainLen = (int)len;
                return;
            }

            var wire = new byte[len];
            await _inner.ReadExactAsync(wire, 0, (int)len, ct).ConfigureAwait(false);
            _plain = InflateSegment(wire);
            _plainLen = _plain.Length;
        }

        private static byte[] InflateSegment(byte[] wire)
        {
            try
            {
                using (var input = new MemoryStream(wire, false))
                using (var ds = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buf = new byte[64 * 1024];
                    int n;
                    while ((n = ds.Read(buf, 0, buf.Length)) > 0)
                    {
                        output.Write(buf, 0, n);
                        if (output.Length > MaxSegment)
                            throw new IOException("inflated segment exceeds cap");
                    }
                    return output.ToArray();
                }
            }
            catch (InvalidDataException ex)
            {
                throw new IOException("corrupt compressed segment", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsInner) _inner.Dispose();
        }
    }
}
