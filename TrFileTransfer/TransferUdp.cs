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

        public const int HeaderSize = 20;   // magic(2) + ver(1) + type(1) + sessionId(16)
        public const int MinStartBody = 54; // size(8)+chunk(4)+total(8)+nameLen(2)+hash(32)
        public const byte Version = 1;
        public const int MaxChunkSize = 60000;   // datagram payload cap (IP-local, avoids fragmentation bombs)
        public const int DefaultChunkSize = 1200; // fits a typical 1500-byte MTU without fragmentation
        public const long MaxChunks = 16 * 1024 * 1024; // 16M chunks @ 1200B ≈ 19 GiB cover
        public const int MaxNameLen = 255;
        /// <summary>Concurrent in-flight sessions cap. Every session holds a file
        /// handle plus a coverage bitmap (worst case ~16 MB at MaxChunks), so an
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
            if (emptyToFinalize != null) FinalizeSession(emptyToFinalize, from);
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
            if (coveredBytes >= 0 && s.TotalSize > 0)
            {
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
                if (finalize) FinalizeSession(s, from);
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
        /// per session and OUTSIDE the session lock (hashing may take seconds).</summary>
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
            string finalPath = Utils.GetUniqueSavePath(dir, s.FileName);
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
            private FileStream _fs;
            private readonly bool[] _covered;
            private long _coveredBytes;
            private readonly object _io = new object();

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
                _covered = new bool[totalChunks];
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
                    if (_covered[chunkIndex]) return _coveredBytes; // re-send: already have it
                    try
                    {
                        _fs.Seek((long)chunkIndex * ChunkSize, SeekOrigin.Begin);
                        _fs.Write(buf, payloadStart, payloadLen);
                    }
                    catch
                    {
                        return -1;
                    }
                    _covered[chunkIndex] = true;
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

                var sw = System.Diagnostics.Stopwatch.StartNew();
                Log(L.UdpC_Sending(_fileName, Utils.FormatSize(_fileSize), _ip, _port.ToString()));

                // START
                byte[] start = BuildStart(session, totalChunks, hash);
                await udp.SendAsync(start, start.Length).ConfigureAwait(false);
                if (_limiter != null) await _limiter.ThrottleAsync(start.Length, ct).ConfigureAwait(false);

                // DATA
                byte[] frame = new byte[UdpOneWay.HeaderSize + 4 + _chunkSize];
                byte[] head = UdpOneWay.BuildHeader(UdpOneWay.FrameData, session);
                Buffer.BlockCopy(head, 0, frame, 0, UdpOneWay.HeaderSize);
                long sent = 0;
                var progressWatch = System.Diagnostics.Stopwatch.StartNew();
                DateTime lastProgress = DateTime.UtcNow.AddSeconds(-1);
                using (var fs = File.OpenRead(_path))
                {
                    byte[] chunk = new byte[_chunkSize];
                    for (long i = 0; i < totalChunks; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        int n = await fs.ReadAsync(chunk, 0, _chunkSize, ct).ConfigureAwait(false);
                        if (n <= 0) break;
                        Buffer.BlockCopy(BitConverter.GetBytes((int)i), 0, frame, UdpOneWay.HeaderSize, 4);
                        Buffer.BlockCopy(chunk, 0, frame, UdpOneWay.HeaderSize + 4, n);
                        int frameLen = UdpOneWay.HeaderSize + 4 + n;
                        await udp.SendAsync(frame, frameLen).ConfigureAwait(false);
                        if (_limiter != null) await _limiter.ThrottleAsync(frameLen, ct).ConfigureAwait(false);
                        sent += n;

                        DateTime now = DateTime.UtcNow;
                        if ((now - lastProgress).TotalMilliseconds >= 100 || sent == _fileSize)
                        {
                            lastProgress = now;
                            var h = OnProgress;
                            if (h != null)
                            {
                                h(new TransferProgress
                                {
                                    BytesTransferred = sent,
                                    TotalBytes = _fileSize,
                                    SpeedBytesPerSecond = sent / Math.Max(0.001, progressWatch.Elapsed.TotalSeconds),
                                    Elapsed = progressWatch.Elapsed,
                                    FileName = _fileName
                                });
                            }
                        }
                    }
                }

                // END
                byte[] end = UdpOneWay.BuildHeader(UdpOneWay.FrameEnd, session);
                await udp.SendAsync(end, end.Length).ConfigureAwait(false);

                var complete = OnTransferComplete;
                if (complete != null) complete();
                Log(L.UdpC_Done(_fileName, Utils.FormatSize(sent),
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
