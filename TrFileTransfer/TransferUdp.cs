using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>
    /// One-way raw-UDP file transfer (no UDT, no TCP, no ACK channel). Designed for
    /// links with no return path (satellite / one-way broadcast): the sender fires
    /// self-contained datagrams and never waits for anything.
    ///
    /// Reliability model: every datagram carries the session id and its own chunk
    /// index, and the session id is DERIVED from (file name, size, mtime). Re-running
    /// the send re-transmits the whole file; the receiver keeps a sparse ".part" file
    /// per session on disk and only fills the chunks it is still missing, so repeated
    /// passes converge ("one-way resume") without any feedback channel.
    ///
    /// There is deliberately no pairing/encryption/compression: a one-way datagram
    /// stream cannot run a challenge-response handshake, and an unauthenticated MAC
    /// would be spoofable by anyone who observed traffic. Restrict senders with the
    /// same IP filter the other protocols use (IpAllowed).
    /// </summary>
    public static class UdpOneWay
    {
        public const byte FrameStart = 0x01;
        public const byte FrameData = 0x02;
        public const byte FrameEnd = 0x03;
        /// <summary>Forward-error-correction parity frame (Reed-Solomon erasure code,
        /// one frame per parity row of a group; the group's chunks XOR-combine under
        /// Cauchy coefficients). Old receivers hit the unknown-type fall-through and
        /// ignore it, so enabling FEC needs no negotiation.</summary>
        public const byte FrameParity = 0x04;

        public const int HeaderSize = 20;   // magic(2) + ver(1) + type(1) + sessionId(16)
        public const int MinStartBody = 54; // size(8)+chunk(4)+total(8)+nameLen(2)+hash(32)
        public const int MinParityBody = 6; // groupStart(4) + groupCount(1) + parityIndex(1)
        public const byte Version = 1;
        public const int MaxChunkSize = 60000;   // datagram payload cap (IP-local, avoids fragmentation bombs)
        public const int DefaultChunkSize = 1200; // fits a typical 1500-byte MTU without fragmentation
        public const long MaxChunks = 16 * 1024 * 1024; // 16M chunks @ 1200B ≈ 19 GiB cover
        public const int MaxNameLen = 255;
        /// <summary>Chunks per FEC group. With interleaved sending, same-group members
        /// ride ≥GroupSize wire positions apart, so a burst of up to GroupSize-1
        /// consecutive losses hits at most one member per group — recoverable from the
        /// single XOR parity frame without any retransmission.</summary>
        public const int FecGroupSize = 8;
        /// <summary>Chunks per interleaving super-block (GroupSize × GroupSize). FEC
        /// accumulators only need to cover one super-block, so parity memory stays at
        /// GroupSize × ChunkSize regardless of file size.</summary>
        public const int FecSuperBlock = FecGroupSize * FecGroupSize;
        /// <summary>Concurrent in-flight sessions cap. Every session holds a file
        /// handle plus a coverage bit set (worst case ~2 MB at MaxChunks), so an
        /// unbounded table is a memory/handle-exhaustion DoS for a datagram blaster.
        /// Excess evicts the most-idle session — a legitimate re-send re-STARTs anyway.</summary>
        public const int MaxSessions = 64;

        public static readonly byte[] Magic = new byte[] { (byte)'T', (byte)'U' };

        /// <summary>Deterministic session id: re-sending the same file version must map
        /// to the same session or the receiver could not fill earlier gaps.</summary>
        public static Guid DeriveSession(string fileName, long size, long mtimeUtcTicks)
        {
            string key = fileName.ToLowerInvariant() + "|" + size.ToString() + "|" + mtimeUtcTicks.ToString();
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
                return new Guid(Utils.CopyBytes(h, 0, 16));
            }
        }

        public static byte[] BuildHeader(byte type, Guid session)
        {
            var b = new byte[HeaderSize];
            b[0] = Magic[0];
            b[1] = Magic[1];
            b[2] = Version;
            b[3] = type;
            byte[] g = session.ToByteArray();
            Buffer.BlockCopy(g, 0, b, 4, 16);
            return b;
        }

        /// <summary>Interleaved send order for FEC: chunk indices in super-blocks of
        /// GroupSize×GroupSize, read column-major within each block. Same-group members
        /// end up exactly R wire positions apart, where R is the group count of that
        /// super-block — R = GroupSize for every full block. The final partial block
        /// is RE-PARTITIONED into more, smaller groups (size ⌈remainder/GroupSize⌉), so
        /// its spacing rises to the group count instead of collapsing (the tail cannot
        /// interleave against already-sent content; residual tail bursts wider than R
        /// there are covered by re-send passes). Pure — the sender iterates it
        /// directly, the receiver needs no mirror (parity frames are self-describing).
        /// </summary>
        public static System.Collections.Generic.IEnumerable<long> InterleavedChunkOrder(long totalChunks, int groupSize)
        {
            long super = (long)groupSize * groupSize;
            for (long sbStart = 0; sbStart < totalChunks; sbStart += super)
            {
                long sbLen = Math.Min(super, totalChunks - sbStart);
                int size = sbLen == super ? groupSize : (int)Math.Max(1, (sbLen + groupSize - 1) / groupSize);
                int groups = (int)((sbLen + size - 1) / size);
                for (int col = 0; col < size; col++)
                {
                    for (int g = 0; g < groups; g++)
                    {
                        if (g * (long)size + col < sbLen)
                            yield return sbStart + g * (long)size + col;
                    }
                }
            }
        }
    }

    /// <summary>Management of the receiver's on-disk .part files — the "disk is the
    /// state" of one-way UDP. Listing and cleanup are pure file operations shared by
    /// the UI; deleting the part of a LIVE session fails on the open handle (the
    /// receiver holds it write-exclusive), which is exactly the desired refusal.</summary>
    public static class UdpPartStore
    {
        public sealed class PartInfo
        {
            public string Path;
            public long Bytes;
            public DateTime LastWriteUtc;
        }

        public static string PartDir(string saveDir)
        {
            return Path.Combine(saveDir ?? "", ".udp");
        }

        /// <summary>All .part files under the save dir, path-sorted; empty when none.</summary>
        public static List<PartInfo> ListParts(string saveDir)
        {
            var list = new List<PartInfo>();
            if (string.IsNullOrEmpty(saveDir)) return list;
            string dir = PartDir(saveDir);
            if (!Directory.Exists(dir)) return list;
            try
            {
                foreach (string p in Directory.GetFiles(dir, "*.part"))
                {
                    try
                    {
                        var fi = new FileInfo(p);
                        list.Add(new PartInfo { Path = p, Bytes = fi.Length, LastWriteUtc = fi.LastWriteTimeUtc });
                    }
                    catch { }
                }
            }
            catch { }
            list.Sort(delegate(PartInfo a, PartInfo b)
            {
                return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        /// <summary>Deletes one part; false when missing or locked (live session).</summary>
        public static bool DeletePart(string partPath)
        {
            try
            {
                if (!File.Exists(partPath)) return false;
                File.Delete(partPath);
                return !File.Exists(partPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Deletes every listed part; returns how many actually went away.</summary>
        public static int DeleteAll(string saveDir)
        {
            int n = 0;
            foreach (PartInfo p in ListParts(saveDir))
                if (DeletePart(p.Path)) n++;
            return n;
        }
    }

    /// <summary>Receiver for one-way UDP sends. Binds one family (v4-any, v6-any or a
    /// specific literal — same wildcard semantics as the UDT tab), reassembles chunk
    /// streams into sparse .part files and finalizes complete files with a whole-file
    /// SHA256 check.</summary>
    public class TransferUdpServer
    {
        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private readonly string _bindAddress;
        private readonly int _port;
        private readonly string _saveDirectory;
        private readonly string _partDir;
        private volatile bool _isRunning;
        private readonly Dictionary<Guid, UdpRecvSession> _sessions = new Dictionary<Guid, UdpRecvSession>();
        private readonly object _sessionLock = new object();
        /// <summary>Sessions delivered recently — a multi-pass sender re-STARTs the
        /// same id every pass; suppressing re-delivery within the window keeps extra
        /// passes from turning into _1 duplicate files. After the window (or a
        /// restart) a repeat send delivers again, exactly as before.</summary>
        private readonly Dictionary<Guid, DateTime> _delivered = new Dictionary<Guid, DateTime>();
        private static readonly TimeSpan DeliveredWindow = TimeSpan.FromMinutes(10);
        private const int MaxDeliveredMemory = 256;
        /// <summary>Finalizations run on the pool but one at a time: GetUniqueSavePath
        /// + Move must not interleave across sessions (two racing finalizes would pick
        /// the same final path and one would lose its part file).</summary>
        private readonly object _finalizeGate = new object();
        private Timer _gc;

        /// <summary>Fired for every log message.</summary>
        public event Action<string> OnLog;
        /// <summary>Fired on non-fatal errors.</summary>
        public event Action<string> OnError;
        /// <summary>Fired when the receiver starts.</summary>
        public event Action OnStarted;
        /// <summary>Fired when the receiver stops.</summary>
        public event Action OnStopped;
        /// <summary>Fired when a file has been fully received and verified (path, size).</summary>
        public event Action<string, long> OnFileReceived;
        /// <summary>Fired when a session finishes, for the transfer statistics.</summary>
        public event Action<WireSessionStats> OnSessionStats;
        /// <summary>Per-sender progress (endpoint-keyed, feeds the UI cards).</summary>
        public event Action<IPEndPoint, TransferProgress> OnClientProgress;
        /// <summary>Fired when a sender's session is finalized (complete or discarded).</summary>
        public event Action<IPEndPoint> OnClientTransferComplete;
        /// <summary>Connection gate: false drops every datagram from that IP.</summary>
        public Func<string, bool> IpAllowed;
        /// <summary>Save each sender's files under a per-device subdirectory.</summary>
        public bool PerDeviceFolder;
        /// <summary>When a name-colliding file already exists byte-identical, drop the
        /// fresh copy instead of saving it with a suffix (same rule as TCP/UDT 0x00).</summary>
        public bool SkipDuplicateFiles;
        /// <summary>Maps a sender IP to a friendly device folder name.</summary>
        public Func<string, string> ResolveDeviceName;

        public bool IsRunning { get { return _isRunning; } }
        public string SaveDirectory { get { return _saveDirectory; } }

        public TransferUdpServer(string bindAddress, int port, string saveDirectory)
        {
            _bindAddress = bindAddress;
            _port = port;
            _saveDirectory = saveDirectory;
            _partDir = Path.Combine(saveDirectory, ".udp");
        }

        /// <summary>Stops ICMP port-unreachable from poisoning the next send/recv on
        /// Windows (WSAECONNRESET after answering a vanished peer). One-way traffic
        /// must survive unreachable moments.</summary>
        private static void DisableConnReset(Socket s)
        {
            try
            {
                // SIO_UDP_CONNRESET = IOC_IN(0x80000000) | IOC_VENDOR(0x18000000) | 4
                s.IOControl(unchecked((int)0x98000004), new byte[] { 0 }, null);
            }
            catch { }
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            try { Directory.CreateDirectory(_saveDirectory); } catch { }

            IPAddress bindIp;
            bool isV6 = IPAddress.TryParse(_bindAddress, out bindIp)
                && bindIp.AddressFamily == AddressFamily.InterNetworkV6;
            try
            {
                Socket s;
                if (isV6)
                {
                    // Family-scoped wildcard (like the TCP v6 tab): v4 senders do not
                    // reach this socket, so both tabs may share the port number.
                    s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                    s.DualMode = false;
                    s.Bind(new IPEndPoint(IPAddress.IPv6Any, _port));
                }
                else
                {
                    string v4 = bindIp != null && bindIp.AddressFamily == AddressFamily.InterNetwork
                        ? _bindAddress : "0.0.0.0";
                    s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s.Bind(new IPEndPoint(IPAddress.Parse(v4), _port));
                }
                DisableConnReset(s);
                // The receive loop does disk I/O per datagram; a burst from a fast
                // sender must queue instead of dropping
                try { s.ReceiveBufferSize = 4 * 1024 * 1024; } catch { }
                // net48 UdpClient has no Socket constructor — close the placeholder the
                // family ctor allocated, then adopt our prepared socket
                _udp = new UdpClient(isV6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
                try { _udp.Client.Close(); } catch { }
                _udp.Client = s;
            }
            catch (Exception ex)
            {
                Log(L.S_BindFailed(_bindAddress, _port.ToString(), ex.Message));
                var err = OnError;
                if (err != null) err(ex.Message);
                var stopped = OnStopped;
                if (stopped != null) stopped();
                return;
            }

            _isRunning = true;
            _gc = new Timer(delegate(object state) { SweepIdleSessions(); }, null, 30000, 30000);
            var handler = OnStarted;
            if (handler != null) handler();
            Log(L.UdpS_Started(_port.ToString(), _saveDirectory));

            var ct = _cts.Token;
            Task.Factory.StartNew(delegate { ReceiveLoop(ct); }, ct,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Stop()
        {
            _isRunning = false;
            if (_gc != null) { try { _gc.Dispose(); } catch { } _gc = null; }
            var cts = _cts;
            if (cts != null) { try { cts.Cancel(); } catch { } }
            if (_udp != null)
            {
                try { _udp.Close(); } catch { }
                _udp = null;
            }
            lock (_sessionLock)
            {
                foreach (var s in _sessions.Values) s.CloseStream();
                _sessions.Clear();
            }
            var handler = OnStopped;
            if (handler != null) handler();
            Log(L.UdpS_Stopped);
        }

        private async void ReceiveLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                try
                {
                    UdpReceiveResult r = await _udp.ReceiveAsync().ConfigureAwait(false);
                    HandleDatagram(r.Buffer, r.RemoteEndPoint);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException)
                {
                    // ICMP resets are disabled; anything else (e.g. transient WSAECONNRESET
                    // races) must not kill the receiver
                    if (ct.IsCancellationRequested) break;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Log(L.UdpS_RecvError(ex.Message));
                }
            }
        }

        private void HandleDatagram(byte[] buf, IPEndPoint from)
        {
            if (buf == null || buf.Length < UdpOneWay.HeaderSize) return;
            if (buf[0] != UdpOneWay.Magic[0] || buf[1] != UdpOneWay.Magic[1]) return;
            if (buf[2] != UdpOneWay.Version) return;
            string peerIp = from != null ? from.Address.ToString() : "";

            if (IpAllowed != null && !IpAllowed(peerIp))
            {
                Log(L.UdpS_IpRefused(peerIp));
                return;
            }

            byte type = buf[3];
            Guid session = new Guid(Utils.CopyBytes(buf, 4, 16));
            if (type == UdpOneWay.FrameStart) HandleStart(buf, session, from, peerIp);
            else if (type == UdpOneWay.FrameData) HandleData(buf, session, from);
            else if (type == UdpOneWay.FrameParity) HandleParity(buf, session, from);
            else if (type == UdpOneWay.FrameEnd) TouchSession(session);
        }

        private void HandleStart(byte[] buf, Guid session, IPEndPoint from, string peerIp)
        {
            if (buf.Length < UdpOneWay.HeaderSize + UdpOneWay.MinStartBody) return;
            int p = UdpOneWay.HeaderSize;
            long size = BitConverter.ToInt64(buf, p); p += 8;
            int chunkSize = BitConverter.ToInt32(buf, p); p += 4;
            long totalChunks = BitConverter.ToInt64(buf, p); p += 8;
            int nameLen = BitConverter.ToInt16(buf, p); p += 2;
            if (nameLen < 0 || nameLen > UdpOneWay.MaxNameLen
                || buf.Length < UdpOneWay.HeaderSize + UdpOneWay.MinStartBody + nameLen)
                return;
            string rawName = System.Text.Encoding.UTF8.GetString(buf, p, nameLen); p += nameLen;
            var hash = Utils.CopyBytes(buf, p, 32);

            // Declared-shape sanity: reject nonsense before touching the disk
            if (size < 0 || size > Utils.MaxTransferSize) return;
            if (chunkSize <= 0 || chunkSize > UdpOneWay.MaxChunkSize) return;
            if (totalChunks < 0 || totalChunks > UdpOneWay.MaxChunks) return;
            long expectChunks = size == 0 ? 0 : (size + chunkSize - 1) / chunkSize;
            if (totalChunks != expectChunks) return;
            if (!Utils.HasFreeSpaceFor(_saveDirectory, size))
            {
                Log(L.UdpS_NoSpace(rawName));
                return;
            }

            string fileName = Path.GetFileName(Utils.SanitizeRelativePath(rawName.Replace('\\', '_')));
            if (fileName.Length == 0) fileName = "udp_file";

            lock (_sessionLock)
            {
                DateTime deliveredAt;
                if (_delivered.TryGetValue(session, out deliveredAt)
                    && DateTime.UtcNow - deliveredAt < DeliveredWindow)
                {
                    // Extra pass of a multi-pass send whose earlier pass already landed
                    Log(L.UdpS_AlreadyDelivered(fileName));
                    return;
                }
            }

            UdpRecvSession emptyToFinalize = null;
            lock (_sessionLock)
            {
                UdpRecvSession s;
                if (_sessions.TryGetValue(session, out s))
                {
                    if (s.Matches(size, chunkSize, hash) && s.Usable)
                    {
                        s.Touch(); // repeated START of the same file version: keep filling
                        return;
                    }
                    // Different shape (source changed) or an unusable part file — restart
                    s.Discard();
                    _sessions.Remove(session);
                }
                s = new UdpRecvSession(session, fileName, size, chunkSize, totalChunks, hash, peerIp, _partDir);
                _sessions[session] = s;
                if (_sessions.Count > UdpOneWay.MaxSessions)
                {
                    // Evict the most-idle session to stay bounded
                    Guid oldest = Guid.Empty;
                    DateTime oldestAt = DateTime.MaxValue;
                    foreach (var kv in _sessions)
                    {
                        if (kv.Value.LastHeard < oldestAt)
                        {
                            oldestAt = kv.Value.LastHeard;
                            oldest = kv.Key;
                        }
                    }
                    UdpRecvSession evicted;
                    if (_sessions.TryGetValue(oldest, out evicted))
                    {
                        evicted.CloseStream(); // keep its part: the sender may come back
                        _sessions.Remove(oldest);
                        Log(L.UdpS_SessionEvicted(evicted.FileName));
                    }
                }
                Log(L.UdpS_SessionStart(fileName, Utils.FormatSize(size), peerIp));

                if (size == 0)
                {
                    // Zero-byte files carry no DATA frames; take it straight to finalize.
                    // Remove first so the finalize path (whole-file hash, rename) runs
                    // OUTSIDE the session lock — hashing a big file must not stall the
                    // receive loop for every other session.
                    _sessions.Remove(session);
                    emptyToFinalize = s;
                }
            }
            if (emptyToFinalize != null)
                Task.Run(delegate { lock (_finalizeGate) FinalizeSession(emptyToFinalize, from); });
        }

        private void HandleData(byte[] buf, Guid session, IPEndPoint from)
        {
            if (buf.Length < UdpOneWay.HeaderSize + 4) return;
            UdpRecvSession s;
            lock (_sessionLock)
            {
                if (!_sessions.TryGetValue(session, out s)) return; // unknown/finished: sender re-runs will re-START
                s.Touch();
            }
            int chunkIndex = BitConverter.ToInt32(buf, UdpOneWay.HeaderSize);
            int payloadStart = UdpOneWay.HeaderSize + 4;
            int payloadLen = buf.Length - payloadStart;
            long coveredBytes = s.WriteChunk(chunkIndex, payloadLen, buf, payloadStart);
            // FEC reverse direction: this chunk may be the second-to-last missing
            // member of a group whose parity is already cached — one attempt now
            // finishes the group without waiting for anything else. The cached frame
            // is self-describing (groupStart + count), so containment — not any
            // shared partition math — finds it (the tail super-block re-partitions).
            if (coveredBytes >= 0)
            {
                long hit;
                if (s.FindCachedGroupContaining(chunkIndex, out hit))
                    TryRecoverAndFinalize(s, hit, from, session);
            }
            // Progress feed is throttled to the sender's 100ms cadence: a datagram
            // is only ~1.2 KB, so per-chunk events would flood the UI dispatcher
            // (a 1 GiB send ≈ 900K dispatches). Only the receive loop touches
            // LastProgressUtc; the completing chunk always reports.
            bool reachedEnd = coveredBytes >= s.TotalSize;
            if (coveredBytes >= 0 && s.TotalSize > 0
                && (reachedEnd || (DateTime.UtcNow - s.LastProgressUtc).TotalMilliseconds >= 100))
            {
                s.LastProgressUtc = DateTime.UtcNow;
                double secs = Math.Max(0.001, (DateTime.UtcNow - s.StartedUtc).TotalSeconds);
                var prog = new TransferProgress
                {
                    BytesTransferred = coveredBytes,
                    TotalBytes = s.TotalSize,
                    FileName = s.FileName,
                    Elapsed = TimeSpan.FromSeconds(secs),
                    SpeedBytesPerSecond = coveredBytes / secs
                };
                var ph = OnClientProgress;
                if (ph != null) ph(from, prog);
            }
            if (s.IsComplete)
            {
                bool finalize = false;
                lock (_sessionLock)
                {
                    // Remove-first: exactly one finalize, and it runs without the lock
                    if (_sessions.Remove(session)) finalize = true;
                }
                if (finalize)
                    Task.Run(delegate { lock (_finalizeGate) FinalizeSession(s, from); });
            }
        }

        /// <summary>FEC parity frame: one Reed-Solomon parity row of a group (Cauchy
        /// coefficients, see UdpFec). Cached (small, byte-budgeted) and immediately
        /// attempted — if the surviving parity rows cover the group's missing members,
        /// they reconstruct in place; otherwise the cache lets a LATER data chunk
        /// trigger the recovery once the erasure count drops to the parity count.</summary>
        private void HandleParity(byte[] buf, Guid session, IPEndPoint from)
        {
            if (buf.Length < UdpOneWay.HeaderSize + UdpOneWay.MinParityBody) return;
            UdpRecvSession s;
            lock (_sessionLock)
            {
                if (!_sessions.TryGetValue(session, out s)) return; // unknown/finished
                s.Touch();
            }
            int p = UdpOneWay.HeaderSize;
            int groupStart = BitConverter.ToInt32(buf, p); p += 4;
            int groupCount = buf[p]; p += 1;
            int parityIndex = buf[p]; p += 1;
            int payloadLen = buf.Length - p;
            if (groupStart < 0 || groupStart >= s.TotalChunks
                || groupCount < 1 || groupCount > UdpOneWay.FecGroupSize
                || groupStart + groupCount > s.TotalChunks
                || parityIndex < 0 || parityIndex >= UdpOneWay.FecGroupSize
                || payloadLen != s.ChunkSize)
                return; // corrupt/hostile parity — ignored, coverage unaffected

            s.StoreParity(groupStart, groupCount, parityIndex, buf, p, payloadLen);
            TryRecoverAndFinalize(s, groupStart, from, session);
        }

        /// <summary>Runs one recovery attempt for a group and, if the session thereby
        /// completed, performs the remove-first finalize dance (hash + rename off the
        /// receive loop, same as the DATA path).</summary>
        private void TryRecoverAndFinalize(UdpRecvSession s, long groupStart, IPEndPoint from, Guid session)
        {
            if (s.TryRecoverGroup(groupStart))
                Log(L.UdpS_ParityRecovered(s.FileName, groupStart.ToString()));
            if (s.IsComplete)
            {
                bool finalize = false;
                lock (_sessionLock)
                {
                    if (_sessions.Remove(session)) finalize = true;
                }
                if (finalize)
                    Task.Run(delegate { lock (_finalizeGate) FinalizeSession(s, from); });
            }
        }

        private void TouchSession(Guid session)
        {
            lock (_sessionLock)
            {
                UdpRecvSession s;
                if (_sessions.TryGetValue(session, out s)) s.Touch();
            }
        }

        /// <summary>Whole-file SHA256 check, then rename into the save dir. The caller
        /// has already removed the session from the table, so this runs at most once
        /// per session; it runs on a pool thread behind _finalizeGate — hashing a
        /// multi-GB part takes seconds and must not stall the receive loop (every
        /// OTHER session's datagrams keep draining meanwhile).</summary>
        private void FinalizeSession(UdpRecvSession s, IPEndPoint from)
        {
            // The writer handle (ReadWrite) must go first: opening the file for hashing
            // or moving it would hit a sharing violation otherwise.
            s.CloseStream();
            byte[] actual = null;
            try { actual = ClientWire.ComputeFileHash(s.PartPath, CancellationToken.None); }
            catch (Exception ex)
            {
                Log(L.UdpS_FinalizeFailed(s.FileName, ex.Message));
            }

            bool ok = actual != null && Utils.ConstantTimeEquals(actual, s.ExpectedHash);

            if (!ok)
            {
                // Silent here would look like a black hole: the sender saw "finished"
                // and the file simply never appears. Say what happened and why.
                Log(L.UdpS_HashMismatch(s.FileName,
                    actual == null ? "-" : Utils.FormatSize(s.TotalSize)));
                s.Discard();
                var done = OnClientTransferComplete;
                if (done != null) done(from);
                return;
            }

            string dir = _saveDirectory;
            if (PerDeviceFolder)
            {
                string device = ResolveDeviceName != null ? ResolveDeviceName(s.PeerIp) : s.PeerIp;
                if (!string.IsNullOrEmpty(device))
                {
                    dir = Path.Combine(_saveDirectory, Utils.SanitizeRelativePath(device));
                    try { Directory.CreateDirectory(dir); } catch { dir = _saveDirectory; }
                }
            }
            string basePath = Path.Combine(dir, s.FileName);
            string finalPath = Utils.GetUniqueSavePath(dir, s.FileName);
            // Skip-duplicates mirrors the TCP/UDT 0x00 rule: only when the name
            // actually collided (the unique path renamed), and only when the survivor
            // is byte-identical — s.ExpectedHash is already verified against the
            // received bytes at this point, so one digest of the old file decides.
            if (SkipDuplicateFiles
                && !string.Equals(finalPath, basePath, StringComparison.OrdinalIgnoreCase)
                && ExistingFileMatches(basePath, s.TotalSize, s.ExpectedHash))
            {
                Log(L.S_DuplicateSkipped(s.FileName));
                s.Discard(); // the on-disk original wins; the fresh copy is dropped
                var recvDup = OnFileReceived;
                if (recvDup != null) recvDup(basePath, s.TotalSize);
                var statsDup = new WireSessionStats
                {
                    Peer = s.PeerIp,
                    Bytes = s.TotalSize,
                    Files = 1,
                    Detail = s.FileName,
                    Path = basePath
                };
                var shDup = OnSessionStats;
                if (shDup != null) shDup(statsDup);
                var doneDup = OnClientTransferComplete;
                if (doneDup != null) doneDup(from);
                return;
            }
            try
            {
                s.MoveTo(finalPath);
            }
            catch (Exception ex)
            {
                Log(L.UdpS_FinalizeFailed(s.FileName, ex.Message));
                s.Discard();
                var done2 = OnClientTransferComplete;
                if (done2 != null) done2(from);
                return;
            }

            Log(L.UdpS_Delivered(s.FileName, Utils.FormatSize(s.TotalSize)));
            lock (_sessionLock)
            {
                _delivered[s.Id] = DateTime.UtcNow;
                if (_delivered.Count > MaxDeliveredMemory) PruneDeliveredUnlocked();
            }
            try
            {
                // Register the digest so dedup / scrub / 0x04 skip recognize UDP-received files
                long fsz, fmt;
                if (Utils.StatFile(finalPath, out fsz, out fmt))
                    FileHashCache.ForServer.StoreIfUnchanged(finalPath, s.ExpectedHash, fsz, fmt);
            }
            catch { }
            var recv = OnFileReceived;
            if (recv != null) recv(finalPath, s.TotalSize);
            var stats = new WireSessionStats
            {
                Peer = s.PeerIp,
                Bytes = s.TotalSize,
                Files = 1,
                Detail = s.FileName,
                Path = finalPath
            };
            var sh = OnSessionStats;
            if (sh != null) sh(stats);
            var done3 = OnClientTransferComplete;
            if (done3 != null) done3(from);
        }

        /// <summary>Duplicate check against an already-saved file: size first, then
        /// the whole-file digest — cache-aware, so a file this receiver delivered
        /// earlier answers from the journal without a disk read (same decision chain
        /// as ServerWire.VerifyFullHashFile).</summary>
        private static bool ExistingFileMatches(string path, long size, byte[] expected)
        {
            try
            {
                long existSize, existMTime;
                if (!FileHashCache.Stat(path, out existSize, out existMTime)) return false;
                if (existSize != size) return false;
                var cache = FileHashCache.ForServer;
                bool cached;
                if (cache.TryMatch(path, existSize, existMTime, expected, out cached))
                    return cached;
                byte[] hash = ClientWire.ComputeFileHash(path, CancellationToken.None);
                if (hash == null) return false;
                cache.StoreIfUnchanged(path, hash, existSize, existMTime);
                return Utils.ConstantTimeEquals(expected, hash);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Drops delivered-memory entries past the window; if a burst still
        /// overflows the cap the whole map resets — the worst case is one duplicate
        /// file, never unbounded memory.</summary>
        private void PruneDeliveredUnlocked()
        {
            DateTime now = DateTime.UtcNow;
            List<Guid> stale = null;
            foreach (var kv in _delivered)
            {
                if (now - kv.Value >= DeliveredWindow)
                {
                    if (stale == null) stale = new List<Guid>();
                    stale.Add(kv.Key);
                }
            }
            if (stale != null)
                foreach (Guid g in stale) _delivered.Remove(g);
            if (_delivered.Count > MaxDeliveredMemory) _delivered.Clear();
        }

        /// <summary>Closes sessions idle past the window; .part files stay on disk so a
        /// later re-send still fills the gaps (one-way resume across restarts). Parts
        /// with no live session older than a week are orphans (the source changed or the
        /// sender never returned) — swept away.</summary>
        private void SweepIdleSessions()
        {
            List<UdpRecvSession> stale = null;
            lock (_sessionLock)
            {
                List<Guid> remove = null;
                foreach (var kv in _sessions)
                {
                    if ((DateTime.UtcNow - kv.Value.LastHeard).TotalMinutes >= 2)
                    {
                        if (remove == null) remove = new List<Guid>();
                        remove.Add(kv.Key);
                        if (stale == null) stale = new List<UdpRecvSession>();
                        stale.Add(kv.Value);
                    }
                }
                if (remove != null)
                    foreach (Guid g in remove) _sessions.Remove(g);
            }
            if (stale != null)
                foreach (var s in stale)
                {
                    Log(L.UdpS_SessionIdle(s.FileName));
                    s.CloseStream();
                }
            try
            {
                if (Directory.Exists(_partDir))
                {
                    foreach (string part in Directory.GetFiles(_partDir, "*.part"))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(part) < DateTime.UtcNow.AddDays(-7))
                                File.Delete(part);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }

        /// <summary>In-flight receive state: sparse .part file plus per-chunk coverage.</summary>
        private sealed class UdpRecvSession
        {
            public readonly Guid Id;
            public readonly string FileName;
            public readonly long TotalSize;
            public readonly int ChunkSize;
            public readonly long TotalChunks;
            public readonly byte[] ExpectedHash;
            public readonly string PeerIp;
            public readonly string PartPath;
            public readonly DateTime StartedUtc = DateTime.UtcNow;
            public DateTime LastHeard = DateTime.UtcNow;
            /// <summary>Last OnClientProgress emission (receive-loop thread only).</summary>
            public DateTime LastProgressUtc = DateTime.MinValue;
            private FileStream _fs;
            // Coverage as a bit set rather than bool[]: a hostile START can declare
            // 16M chunks (19 GiB @ 1200B) without ever sending a byte, and the part
            // file itself is sparse — bool[] would commit 16 MB per session (64 such
            // sessions ≈ 1 GB of RAM); bits cut that eightfold.
            private readonly byte[] _coveredBits;
            private long _coveredBytes;
            private readonly object _io = new object();
            // FEC parity cache: recent group → (payload, member count). Bounded hard —
            // a hostile chunkSize (60000) must not multiply into per-session megabytes.
            // FEC parity cache: group start → (member count, parity rows by index).
            // Bounded hard on both entry count AND bytes — a hostile chunkSize (60000)
            // must not multiply into per-session megabytes.
            private sealed class ParityGroup
            {
                public int Count;
                public readonly Dictionary<int, byte[]> Frames = new Dictionary<int, byte[]>();
            }
            private readonly Dictionary<long, ParityGroup> _parity = new Dictionary<long, ParityGroup>();
            private readonly Queue<long> _parityOrder = new Queue<long>();
            private const int MaxParityCacheEntries = 4;
            private const int MaxParityCacheBytes = 256 * 1024;
            private long _parityBytes;

            public bool IsComplete { get { return TotalSize > 0 && _coveredBytes >= TotalSize; } }

            public UdpRecvSession(Guid id, string fileName, long size, int chunkSize,
                long totalChunks, byte[] hash, string peerIp, string partDir)
            {
                Id = id;
                FileName = fileName;
                TotalSize = size;
                ChunkSize = chunkSize;
                TotalChunks = totalChunks;
                ExpectedHash = hash;
                PeerIp = peerIp;
                PartPath = Path.Combine(partDir, id.ToString("N") + ".part");
                _coveredBits = new byte[(totalChunks + 7) / 8];
                try { Directory.CreateDirectory(partDir); } catch { }
                try
                {
                    // Truncate a stale longer part (source changed size between sends)
                    var fi = new FileInfo(PartPath);
                    if (fi.Exists && fi.Length != size)
                    {
                        using (var tf = File.Open(PartPath, FileMode.Create)) { }
                    }
                }
                catch { }
                try
                {
                    _fs = new FileStream(PartPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                }
                catch (Exception)
                {
                    _fs = null; // finalize/hash will surface the disk problem
                }
            }

            public bool Matches(long size, int chunkSize, byte[] hash)
            {
                if (size != TotalSize || chunkSize != ChunkSize) return false;
                return Utils.ConstantTimeEquals(hash, ExpectedHash);
            }

            /// <summary>Whether a parity payload for this exact group start is cached.</summary>
            public bool HasParity(long groupStart)
            {
                lock (_io) { return _parity.ContainsKey(groupStart); }
            }

            /// <summary>Finds a cached parity whose group range contains idx (the cache
            /// holds ≤4 self-describing entries, so a linear scan is the whole cost).</summary>
            public bool FindCachedGroupContaining(long idx, out long groupStart)
            {
                lock (_io)
                {
                    foreach (var kv in _parity)
                    {
                        if (idx >= kv.Key && idx < kv.Key + kv.Value.Count)
                        {
                            groupStart = kv.Key;
                            return true;
                        }
                    }
                }
                groupStart = -1;
                return false;
            }

            /// <summary>Whether chunks can still land (the part file is writable).
            /// A session whose part file could not be opened is dead weight — a fresh
            /// START must get a fresh chance instead of filling a black hole.</summary>
            public bool Usable
            {
                get
                {
                    lock (_io) { return _fs != null; }
                }
            }

            public void Touch() { LastHeard = DateTime.UtcNow; }

            /// <summary>Writes one chunk; returns the session's covered byte count, or
            /// -1 when the datagram was ignored (bad index / wrong length / duplicate /
            /// unusable part file). Coverage is only marked when the bytes really landed
            /// on disk — otherwise the session would "complete" with holes.</summary>
            public long WriteChunk(int chunkIndex, int payloadLen, byte[] buf, int payloadStart)
            {
                if (chunkIndex < 0 || chunkIndex >= TotalChunks) return -1;
                long expected = TotalSize - (long)chunkIndex * ChunkSize;
                if (expected > ChunkSize) expected = ChunkSize;
                if (payloadLen != expected) return -1; // corrupt/oversized datagram
                lock (_io)
                {
                    if (_fs == null) return -1; // part file unusable — finalize will say why
                    int bitByte = chunkIndex >> 3;
                    int bitMask = 1 << (chunkIndex & 7);
                    if ((_coveredBits[bitByte] & bitMask) != 0) return _coveredBytes; // re-send: already have it
                    try
                    {
                        _fs.Seek((long)chunkIndex * ChunkSize, SeekOrigin.Begin);
                        _fs.Write(buf, payloadStart, payloadLen);
                    }
                    catch
                    {
                        return -1;
                    }
                    _coveredBits[bitByte] |= (byte)bitMask;
                    _coveredBytes += payloadLen;
                    return _coveredBytes;
                }
            }

            public void CloseStream()
            {
                lock (_io)
                {
                    if (_fs != null)
                    {
                        try { _fs.Flush(); } catch { }
                        try { _fs.Dispose(); } catch { }
                        _fs = null;
                    }
                }
            }

            /// <summary>Caches one parity row for a group (FIFO- and byte-bounded).</summary>
            public void StoreParity(long groupStart, int groupCount, int parityIndex, byte[] buf, int payloadOff, int payloadLen)
            {
                lock (_io)
                {
                    ParityGroup pg;
                    if (!_parity.TryGetValue(groupStart, out pg))
                    {
                        pg = new ParityGroup { Count = groupCount };
                        _parity[groupStart] = pg;
                        _parityOrder.Enqueue(groupStart);
                        while (_parityOrder.Count > MaxParityCacheEntries) EvictOldestParity();
                    }
                    if (!pg.Frames.ContainsKey(parityIndex)) _parityBytes += payloadLen;
                    pg.Frames[parityIndex] = Utils.CopyBytes(buf, payloadOff, payloadLen);
                    while (_parityBytes > MaxParityCacheBytes && _parityOrder.Count > 1) EvictOldestParity();
                }
            }

            private void EvictOldestParity()
            {
                if (_parityOrder.Count == 0) return;
                long oldest = _parityOrder.Dequeue();
                ParityGroup old;
                if (_parity.TryGetValue(oldest, out old))
                {
                    foreach (var f in old.Frames.Values) _parityBytes -= f.Length;
                    _parity.Remove(oldest);
                }
            }

            /// <summary>Recovers a group's missing members from its cached parity rows
            /// (Reed-Solomon erasure solve, surviving members read back from the part
            /// file) and writes them through the normal WriteChunk path. Returns false
            /// when there is no cache entry, nothing missing, or fewer surviving rows
            /// than erasures — the entry then stays for a later attempt.</summary>
            public bool TryRecoverGroup(long groupStart)
            {
                lock (_io)
                {
                    ParityGroup pg;
                    if (_fs == null || !_parity.TryGetValue(groupStart, out pg)) return false;
                    int groupCount = pg.Count;
                    if (groupCount < 1 || groupStart + groupCount > TotalChunks) return false;

                    var missing = new List<int>();
                    for (int i = 0; i < groupCount; i++)
                    {
                        int bitByte = (int)((groupStart + i) >> 3);
                        int bitMask = 1 << (int)((groupStart + i) & 7);
                        if ((_coveredBits[bitByte] & bitMask) == 0) missing.Add(i);
                    }
                    if (missing.Count == 0)
                    {
                        RemoveParityGroup(groupStart);
                        return false;
                    }
                    if (pg.Frames.Count < missing.Count) return false; // keep for later

                    var survCols = new List<int>();
                    var survBlocks = new List<byte[]>();
                    for (int i = 0; i < groupCount; i++)
                    {
                        int bitByte = (int)((groupStart + i) >> 3);
                        int bitMask = 1 << (int)((groupStart + i) & 7);
                        if ((_coveredBits[bitByte] & bitMask) == 0) continue;
                        long len = TotalSize - (groupStart + i) * (long)ChunkSize;
                        if (len > ChunkSize) len = ChunkSize;
                        var block = new byte[len];
                        _fs.Seek((groupStart + i) * (long)ChunkSize, SeekOrigin.Begin);
                        int read = 0;
                        while (read < (int)len)
                        {
                            int r = _fs.Read(block, read, (int)len - read);
                            if (r <= 0) return false; // part file shrank underneath us
                            read += r;
                        }
                        survCols.Add(i);
                        survBlocks.Add(block);
                    }

                    var payloads = new byte[pg.Frames.Count][];
                    var rowIdx = new int[pg.Frames.Count];
                    int w = 0;
                    foreach (var kv in pg.Frames) { rowIdx[w] = kv.Key; payloads[w] = kv.Value; w++; }

                    byte[][] solved = UdpFec.Solve(payloads, rowIdx,
                        survCols.ToArray(), survBlocks.ToArray(), missing.ToArray(), ChunkSize, UdpOneWay.FecGroupSize);
                    if (solved == null) return false;

                    bool any = false;
                    for (int c = 0; c < missing.Count; c++)
                    {
                        long globalIdx = groupStart + missing[c];
                        long expected = TotalSize - globalIdx * (long)ChunkSize;
                        if (expected > ChunkSize) expected = ChunkSize;
                        long r2 = WriteChunk((int)globalIdx, (int)expected, solved[c], 0);
                        if (r2 >= 0) any = true;
                    }
                    RemoveParityGroup(groupStart);
                    return any;
                }
            }

            private void RemoveParityGroup(long groupStart)
            {
                ParityGroup old;
                if (_parity.TryGetValue(groupStart, out old))
                {
                    foreach (var f in old.Frames.Values) _parityBytes -= f.Length;
                    _parity.Remove(groupStart);
                }
                // rebuild the FIFO without the removed group (≤4 entries)
                var keep = new Queue<long>();
                while (_parityOrder.Count > 0)
                {
                    long k = _parityOrder.Dequeue();
                    if (k != groupStart) keep.Enqueue(k);
                }
                foreach (long k in keep) _parityOrder.Enqueue(k);
            }

            /// <summary>Closes the stream and deletes the part file (hash mismatch or
            /// shape change — nothing here is worth keeping).</summary>
            public void Discard()
            {
                CloseStream();
                try { File.Delete(PartPath); } catch { }
            }

            public void MoveTo(string finalPath)
            {
                CloseStream();
                File.Move(PartPath, finalPath);
            }
        }
    }

    /// <summary>Sender for one-way UDP transfers. Fires START + chunks + END, applies
    /// the speed limit, reports local progress, and never waits for a reply.</summary>
    public class TransferUdpClient
    {
        private readonly string _ip;
        private readonly int _port;
        private readonly string _path;
        private readonly int _chunkSize;
        private readonly int _srcPort;
        private readonly SpeedLimiter _limiter;
        private readonly long _fileSize;
        private readonly string _fileName;
        private CancellationTokenSource _cts;
        private volatile bool _wasCancelled;
        /// <summary>Whole-file re-send passes (clamped 1-5). A one-way link has no
        /// feedback, so repeating the send IS the loss recovery; the receiver
        /// suppresses a session it just delivered, so extra passes never create
        /// duplicate files.</summary>
        public int Passes = 1;
        private const int InterPassDelayMs = 400;
        /// <summary>START/END redundant copies and the spacing between them — a burst
        /// shorter than the spacing cannot kill all copies.</summary>
        public const int StartCopies = 3;
        public const int FrameSpacingMs = 40;
        /// <summary>Forward error correction: Reed-Solomon erasure coding with
        /// `FecParity` parity frames per GroupSize-chunk group (1 = single parity,
        /// 2+ recovers multiple losses per group) plus interleaved sending. Safe
        /// against old receivers (they drop the unknown frame type), so it defaults
        /// on; --nofec / Config "UdpFec"=0 turns it off, "UdpFecParity" (1-8) sets
        /// the redundancy.</summary>
        public bool FecEnabled = true;
        /// <summary>Parity frames per FEC group (clamped 1..GroupSize at use). 2 is the
        /// default: two arbitrary losses per 8-chunk group recover in a single pass at
        /// 25% wire overhead — on a no-return link a re-send pass costs the whole file,
        /// so overhead is the cheap currency here.</summary>
        public int FecParity = 2;

        public event Action<string> OnLog;
        public event Action<TransferProgress> OnProgress;
        public event Action<string> OnError;
        /// <summary>Fired when the last datagram (END) is out. One-way means the
        /// receiver's fate is not observable from here.</summary>
        public event Action OnTransferComplete;
        public event Action OnStopped;

        /// <summary>True when the send ended because the user cancelled it.</summary>
        public bool WasCancelled { get { return _wasCancelled; } }

        public TransferUdpClient(string ip, int port, string path,
            int chunkSize = UdpOneWay.DefaultChunkSize, int srcPort = 0, int speedLimitBytesPerSec = 0)
        {
            _ip = ip;
            _port = port;
            _path = path;
            _chunkSize = Math.Max(1, Math.Min(UdpOneWay.MaxChunkSize, chunkSize));
            _srcPort = srcPort;
            _limiter = speedLimitBytesPerSec > 0 ? new SpeedLimiter(speedLimitBytesPerSec) : null;
            _fileName = Path.GetFileName(path);
            try { _fileSize = new FileInfo(path).Length; } catch { _fileSize = 0; }
        }

        public void Cancel()
        {
            _wasCancelled = true;
            var cts = _cts;
            if (cts != null) { try { cts.Cancel(); } catch { } }
        }

        public async Task SendAsync()
        {
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            UdpClient udp = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                IPAddress target;
                if (!IPAddress.TryParse(_ip, out target))
                    throw new IOException(L.UdpC_BadTarget(_ip));
                var socket = new Socket(
                    target.AddressFamily == AddressFamily.InterNetworkV6
                        ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
                    SocketType.Dgram, ProtocolType.Udp);
                if (_srcPort > 0)
                    socket.Bind(new IPEndPoint(
                        target.AddressFamily == AddressFamily.InterNetworkV6
                            ? IPAddress.IPv6Any : IPAddress.Any, _srcPort));
                // One-way: an unreachable receiver must not poison the socket
                try
                {
                    socket.IOControl(unchecked((int)0x98000004), new byte[] { 0 }, null);
                }
                catch { }
                try { socket.SendBufferSize = 4 * 1024 * 1024; } catch { }
                socket.Connect(new IPEndPoint(target, _port));
                udp = new UdpClient(target.AddressFamily);
                try { udp.Client.Close(); } catch { }
                udp.Client = socket;

                // Whole-file hash before the first datagram (also the session key input)
                DateTime mtimeUtc;
                try { mtimeUtc = new FileInfo(_path).LastWriteTimeUtc; }
                catch { mtimeUtc = DateTime.MinValue; }
                Log(L.UdpC_Hashing(_fileName));
                byte[] hash = await Task.Run(delegate { return ClientWire.ComputeFileHash(_path, ct); }, ct)
                    .ConfigureAwait(false);
                Guid session = UdpOneWay.DeriveSession(_fileName, _fileSize, mtimeUtc.Ticks);
                long totalChunks = _fileSize == 0 ? 0 : (_fileSize + _chunkSize - 1) / _chunkSize;
                int passes = Math.Max(1, Math.Min(5, Passes));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                Log(L.UdpC_Sending(_fileName, Utils.FormatSize(_fileSize), _ip, _port.ToString()));
                if (FecEnabled && totalChunks > 1)
                    Log(L.UdpC_FecOn(UdpOneWay.FecGroupSize.ToString(),
                        Math.Max(1, Math.Min(UdpOneWay.FecGroupSize, FecParity)).ToString()));
                byte[] start = BuildStart(session, totalChunks, hash);
                byte[] end = UdpOneWay.BuildHeader(UdpOneWay.FrameEnd, session);
                byte[] frame = new byte[UdpOneWay.HeaderSize + 4 + _chunkSize];
                byte[] head = UdpOneWay.BuildHeader(UdpOneWay.FrameData, session);
                Buffer.BlockCopy(head, 0, frame, 0, UdpOneWay.HeaderSize);
                long overall = 0;                    // bytes sent across all passes
                long grandTotal = _fileSize * passes; // progress denominator
                var progressWatch = System.Diagnostics.Stopwatch.StartNew();
                DateTime lastProgress = DateTime.UtcNow.AddSeconds(-1);
                Action<long> reportProgress = delegate(long passSent)
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastProgress).TotalMilliseconds >= 100 || passSent == _fileSize)
                    {
                        lastProgress = now;
                        long shown = overall + passSent;
                        var h = OnProgress;
                        if (h != null)
                        {
                            h(new TransferProgress
                            {
                                BytesTransferred = shown,
                                TotalBytes = grandTotal,
                                SpeedBytesPerSecond = shown / Math.Max(0.001, progressWatch.Elapsed.TotalSeconds),
                                Elapsed = progressWatch.Elapsed,
                                FileName = _fileName
                            });
                        }
                    }
                };

                for (int pass = 1; pass <= passes; pass++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (pass > 1)
                    {
                        Log(L.UdpC_Pass(pass.ToString(), passes.ToString()));
                        await Task.Delay(InterPassDelayMs, ct).ConfigureAwait(false);
                    }

                    // START ×3, spaced: a single lost START voids the whole pass (the
                    // receiver ignores every DATA of an unregistered session), so the
                    // one frame that must survive rides in three copies
                    for (int s = 0; s < StartCopies; s++)
                    {
                        if (s > 0) await Task.Delay(FrameSpacingMs, ct).ConfigureAwait(false);
                        await udp.SendAsync(start, start.Length).ConfigureAwait(false);
                        if (_limiter != null) await _limiter.ThrottleAsync(start.Length, ct).ConfigureAwait(false);
                    }

                    // DATA — the hash is computed once above and reused every pass
                    long sent = 0;
                    using (var fs = File.OpenRead(_path))
                    {
                        byte[] chunk = new byte[_chunkSize];
                        if (FecEnabled && totalChunks > 1)
                        {
                            sent = await SendChunksFecAsync(udp, fs, chunk, frame, session,
                                totalChunks, reportProgress, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            sent = await SendChunksSequentialAsync(udp, fs, chunk, frame,
                                totalChunks, reportProgress, ct).ConfigureAwait(false);
                        }
                    }

                    // END ×3, spaced the same way
                    for (int s = 0; s < StartCopies; s++)
                    {
                        if (s > 0) await Task.Delay(FrameSpacingMs, ct).ConfigureAwait(false);
                        await udp.SendAsync(end, end.Length).ConfigureAwait(false);
                    }
                    overall += sent;
                }

                var complete = OnTransferComplete;
                if (complete != null) complete();
                Log(L.UdpC_Done(_fileName, Utils.FormatSize(overall),
                    sw.Elapsed.TotalSeconds.ToString("0.0")));
            }
            catch (OperationCanceledException)
            {
                _wasCancelled = true;
                var h = OnError;
                if (h != null) h(L.UdpC_Cancelled(_fileName));
            }
            catch (Exception ex)
            {
                var h = OnError;
                if (h != null) h(ex.Message);
            }
            finally
            {
                if (udp != null) { try { udp.Close(); } catch { } }
                var stopped = OnStopped;
                if (stopped != null) stopped();
            }
        }

        /// <summary>Legacy path: chunks in natural order, no parity frames.</summary>
        private async Task<long> SendChunksSequentialAsync(UdpClient udp, FileStream fs, byte[] chunk,
            byte[] frame, long totalChunks, Action<long> reportProgress, CancellationToken ct)
        {
            long sent = 0;
            for (long i = 0; i < totalChunks; i++)
            {
                ct.ThrowIfCancellationRequested();
                int n = await ReadChunkAsync(fs, chunk, i, ct).ConfigureAwait(false);
                if (n <= 0) break;
                await SendDataChunkAsync(udp, frame, i, chunk, n, ct).ConfigureAwait(false);
                sent += n;
                reportProgress(sent);
            }
            return sent;
        }

        /// <summary>FEC path: chunks interleaved (super-block column-major, same-group
        /// members ride the block's group count apart — see
        /// UdpOneWay.InterleavedChunkOrder) with `FecParity` Reed-Solomon parity
        /// frames per group (Cauchy erasure code, see UdpFec). Up to FecParity losses
        /// per group then recover on the receiver with no retransmission. Parity
        /// frames are queued and spread across the NEXT super-block's columns, so a
        /// burst cannot cover a group's data members and all its parity copies in the
        /// same window; the tail block's parities go out just before END. Parity
        /// memory is bounded by the super-block: GroupSize×FecParity accumulators.</summary>
        private async Task<long> SendChunksFecAsync(UdpClient udp, FileStream fs, byte[] chunk,
            byte[] frame, Guid session, long totalChunks, Action<long> reportProgress, CancellationToken ct)
        {
            int k = UdpOneWay.FecGroupSize;
            int m = Math.Max(1, Math.Min(k, FecParity));
            long super = (long)k * k;
            // parity accumulators, indexed [g * m + j] (g = group in the block, j = row)
            byte[][] acc = new byte[k * m][];
            for (int r = 0; r < acc.Length; r++) acc[r] = new byte[_chunkSize];
            int[] groupCount = new int[k];
            byte[] parityHead = UdpOneWay.BuildHeader(UdpOneWay.FrameParity, session);
            var pending = new System.Collections.Generic.List<byte[]>();
            long sent = 0;
            long sbStart = -1, sbEnd = -1;
            int size = k, groups = k;
            long lastCol = -1;

            foreach (long idx in UdpOneWay.InterleavedChunkOrder(totalChunks, k))
            {
                ct.ThrowIfCancellationRequested();
                if (idx >= sbEnd)
                {
                    if (sbStart >= 0)
                        QueueParities(pending, acc, groupCount, parityHead, sbStart, size, groups, m, k, _chunkSize);
                    sbStart = idx / super * super;
                    sbEnd = sbStart + super;
                    long sbLen = Math.Min(super, totalChunks - sbStart);
                    // The final partial block re-partitions into more, smaller groups
                    // (see InterleavedChunkOrder) so its interleave spacing — the group
                    // count — stays as high as the remainder allows
                    size = sbLen == super ? k : (int)Math.Max(1, (sbLen + k - 1) / k);
                    groups = (int)((sbLen + size - 1) / size);
                    for (int r = 0; r < acc.Length; r++) Array.Clear(acc[r], 0, acc[r].Length);
                    for (int gi = 0; gi < k; gi++) groupCount[gi] = 0;
                    lastCol = -1;
                }
                long col = (idx - sbStart) % size;
                if (col != lastCol)
                {
                    lastCol = col;
                    // Spread the previous block's queued parities across this block's
                    // columns (ceil share per remaining column — the queue drains
                    // exactly by the block's end)
                    long colsLeft = size - col;
                    int drain = pending.Count == 0 ? 0
                        : (int)Math.Min(pending.Count, (pending.Count + colsLeft - 1) / colsLeft);
                    for (int d = 0; d < drain; d++)
                    {
                        byte[] pf = pending[0];
                        pending.RemoveAt(0);
                        await udp.SendAsync(pf, pf.Length).ConfigureAwait(false);
                        if (_limiter != null) await _limiter.ThrottleAsync(pf.Length, ct).ConfigureAwait(false);
                    }
                }
                int g = (int)((idx - sbStart) / size);
                int n = await ReadChunkAsync(fs, chunk, idx, ct).ConfigureAwait(false);
                if (n <= 0) break;
                await SendDataChunkAsync(udp, frame, idx, chunk, n, ct).ConfigureAwait(false);
                sent += n;
                reportProgress(sent);
                int member = (int)(idx - (sbStart + g * (long)size));
                for (int j = 0; j < m; j++)
                    UdpFec.Accumulate(acc[g * m + j], chunk, n, UdpFec.Coefficient(j, member, k));
                groupCount[g]++;
            }
            // The tail block has no transition after it — queue its parities here,
            // then drain everything (queued at the loop's last transition + tail)
            // before END. Forgetting this step left every file's last super-block
            // unprotected and the tail's dropped chunks permanently missing.
            if (sbStart >= 0)
                QueueParities(pending, acc, groupCount, parityHead, sbStart, size, groups, m, k, _chunkSize);
            for (int d = 0; d < pending.Count; d++)
            {
                byte[] pf = pending[d];
                await udp.SendAsync(pf, pf.Length).ConfigureAwait(false);
                if (_limiter != null) await _limiter.ThrottleAsync(pf.Length, ct).ConfigureAwait(false);
            }
            return sent;
        }

        /// <summary>Freezes a finished super-block's parity rows into self-describing
        /// PARITY frames ([groupStart(4)][groupCount(1)][parityIndex(1)][payload]) and
        /// queues them for spread emission over the next block's columns.</summary>
        private static void QueueParities(System.Collections.Generic.List<byte[]> pending, byte[][] acc,
            int[] groupCount, byte[] parityHead, long sbStart, int size, int groups, int m, int k, int chunkSize)
        {
            for (int g = 0; g < groups; g++)
            {
                if (groupCount[g] == 0) continue;
                long groupStart = sbStart + g * (long)size;
                for (int j = 0; j < m; j++)
                {
                    byte[] f = new byte[UdpOneWay.HeaderSize + UdpOneWay.MinParityBody + chunkSize];
                    Buffer.BlockCopy(parityHead, 0, f, 0, UdpOneWay.HeaderSize);
                    Buffer.BlockCopy(BitConverter.GetBytes((int)groupStart), 0, f, UdpOneWay.HeaderSize, 4);
                    f[UdpOneWay.HeaderSize + 4] = (byte)groupCount[g];
                    f[UdpOneWay.HeaderSize + 5] = (byte)j;
                    Buffer.BlockCopy(acc[g * m + j], 0, f, UdpOneWay.HeaderSize + 6, chunkSize);
                    pending.Add(f);
                }
            }
        }

        /// <summary>Positions at idx and fill-reads the complete chunk — a short read
        /// would emit an undersized frame, and the receiver permanently rejects any
        /// chunk whose length differs from the declared shape (the coverage would
        /// never reach complete while we report done).</summary>
        private async Task<int> ReadChunkAsync(FileStream fs, byte[] chunk, long idx, CancellationToken ct)
        {
            fs.Seek(idx * (long)_chunkSize, SeekOrigin.Begin);
            int n = 0;
            while (n < _chunkSize)
            {
                int r = await fs.ReadAsync(chunk, n, _chunkSize - n, ct).ConfigureAwait(false);
                if (r <= 0) break;
                n += r;
            }
            return n;
        }

        private async Task SendDataChunkAsync(UdpClient udp, byte[] frame, long idx, byte[] chunk, int n,
            CancellationToken ct)
        {
            Buffer.BlockCopy(BitConverter.GetBytes((int)idx), 0, frame, UdpOneWay.HeaderSize, 4);
            Buffer.BlockCopy(chunk, 0, frame, UdpOneWay.HeaderSize + 4, n);
            int frameLen = UdpOneWay.HeaderSize + 4 + n;
            await udp.SendAsync(frame, frameLen).ConfigureAwait(false);
            if (_limiter != null) await _limiter.ThrottleAsync(frameLen, ct).ConfigureAwait(false);
        }

        private byte[] BuildStart(Guid session, long totalChunks, byte[] hash)
        {
            byte[] name = System.Text.Encoding.UTF8.GetBytes(_fileName);
            if (name.Length > UdpOneWay.MaxNameLen) name = Utils.CopyBytes(name, 0, UdpOneWay.MaxNameLen);
            int len = UdpOneWay.HeaderSize + 8 + 4 + 8 + 2 + name.Length + 32;
            byte[] b = new byte[len];
            byte[] head = UdpOneWay.BuildHeader(UdpOneWay.FrameStart, session);
            Buffer.BlockCopy(head, 0, b, 0, UdpOneWay.HeaderSize);
            int p = UdpOneWay.HeaderSize;
            Buffer.BlockCopy(BitConverter.GetBytes(_fileSize), 0, b, p, 8); p += 8;
            Buffer.BlockCopy(BitConverter.GetBytes(_chunkSize), 0, b, p, 4); p += 4;
            Buffer.BlockCopy(BitConverter.GetBytes(totalChunks), 0, b, p, 8); p += 8;
            Buffer.BlockCopy(BitConverter.GetBytes((short)name.Length), 0, b, p, 2); p += 2;
            Buffer.BlockCopy(name, 0, b, p, name.Length); p += name.Length;
            Buffer.BlockCopy(hash, 0, b, p, 32);
            return b;
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }
}
