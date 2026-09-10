using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>
    /// Transport-agnostic wire protocol shared by the TCP and UDT transfers.
    /// Both sides speak the same 0x00-0x03 line protocol (see WIRE-PROTOCOL.md);
    /// the transports differ only in how bytes move, which IWireStream abstracts.
    /// </summary>
    #pragma warning disable 1591

    #region Wire stream abstraction

    /// <summary>Exact read/write byte stream over a transfer connection.</summary>
    public interface IWireStream : IDisposable
    {
        /// <summary>Writes exactly count bytes (looping over short writes).</summary>
        Task WriteExactAsync(byte[] buffer, int offset, int count, CancellationToken ct);

        /// <summary>Reads exactly count bytes; throws IOException on premature close.</summary>
        Task ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct);

        /// <summary>Reads whatever is available (at most count). Returns 0 on EOF.</summary>
        Task<int> ReadSomeAsync(byte[] buffer, int offset, int count, CancellationToken ct);
    }

    /// <summary>IWireStream over a TCP NetworkStream.</summary>
    public class TcpWireStream : IWireStream
    {
        private readonly NetworkStream _stream;
        private readonly string _eofMessage;
        private bool _disposed;

        public TcpWireStream(NetworkStream stream, string eofMessage)
        {
            _stream = stream;
            _eofMessage = eofMessage;
        }

        public async Task WriteExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await _stream.WriteAsync(buffer, offset, count, ct).ConfigureAwait(false);
        }

        public async Task ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await _stream.ReadAsync(buffer, offset + totalRead, count - totalRead, ct).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException(_eofMessage);
                totalRead += read;
            }
        }

        public async Task<int> ReadSomeAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            return await _stream.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _stream.Dispose();
            }
        }
    }

    /// <summary>IWireStream over a native UDT socket (see UdtIo for the byte movers).</summary>
    public class UdtWireStream : IWireStream
    {
        private readonly int _socket;
        private readonly bool _ownsSocket;
        private bool _disposed;

        /// <param name="ownsSocket">true when Dispose should close the native socket.</param>
        public UdtWireStream(int socket, bool ownsSocket)
        {
            _socket = socket;
            _ownsSocket = ownsSocket;
        }

        public Task WriteExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            return UdtIo.UdtWriteExactAsync(_socket, buffer, offset, count, ct);
        }

        public Task ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            return UdtIo.UdtReadExactAsync(_socket, buffer, offset, count, ct);
        }

        public Task<int> ReadSomeAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            return UdtIo.UdtReadAsync(_socket, buffer, offset, count, ct);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsSocket)
            {
                try { UdtNative.udt_close(_socket); } catch { }
            }
        }
    }

    #endregion

    #region Shared callbacks and server context

    /// <summary>Pairing-code helpers for the 0x05 authentication frame.</summary>
    public static class WireAuth
    {
        /// <summary>SHA256 of the UTF-8 pairing code — what actually crosses the wire,
        /// so the raw code never leaves the machine.</summary>
        public static byte[] HashCode(string code)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(code ?? ""));
        }

        /// <summary>Cryptographically random 6-digit pairing code for the server UI.</summary>
        public static string GeneratePairingCode()
        {
            var buf = new byte[4];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(buf);
            int v = BitConverter.ToInt32(buf, 0) & 0x7FFFFFFF;
            return (v % 1000000).ToString("D6");
        }
    }

    /// <summary>Events the shared protocol code raises; transports fan these out to their own listeners.</summary>
    public class WireCallbacks
    {
        public Action<string> Log;
        public Action<TransferProgress> Progress;
        public Action<string> Error;
        public Action Complete;
        public Action<string, long> FileReceived;
        public Action<string> TextReceived;
        /// <summary>True when the transport's completion verdict arrives as a byte on the
        /// wire stream (TCP) rather than out-of-band (UDT's 1-byte app ACK). ClientWire
        /// reads the stream only in the former case.</summary>
        public bool CompletionAckOnStream = true;

        public void RaiseLog(string msg) { var h = Log; if (h != null) h(msg); }
        public void RaiseProgress(TransferProgress p) { var h = Progress; if (h != null) h(p); }
        public void RaiseError(string msg) { var h = Error; if (h != null) h(msg); }
        public void RaiseComplete() { var h = Complete; if (h != null) h(); }
        public void RaiseFileReceived(string path, long size) { var h = FileReceived; if (h != null) h(path, size); }
        public void RaiseTextReceived(string text) { var h = TextReceived; if (h != null) h(text); }
    }

    /// <summary>Outcome of a server-side protocol dispatch. Lets transports keep their own
    /// ACK / per-client-completion semantics instead of hard-coding them here.</summary>
    public class WireOutcome
    {
        /// <summary>True when the transfer (or chunk assembly) verified and finished.</summary>
        public bool Success;
        /// <summary>True when the request was a 0x02 chunk (transports ACK these differently).</summary>
        public bool IsChunked;
        /// <summary>True when the transfer type carries its own terminal response the
        /// client reads in-protocol (0x03's final 0x10 status). The UDT transport must
        /// NOT send its 1-byte app ACK for these flows — the 0x10 IS the verdict — and
        /// the client must not wait for an ACK after consuming it.</summary>
        public bool HasOwnFinalResponse;
        /// <summary>True when the connection was refused by policy (IP filter, pairing
        /// code, receive confirmation). UDT answers these with a 0x00 NACK so the
        /// client fails fast instead of waiting out its ACK timeout.</summary>
        public bool Rejected;
        /// <summary>Per-connection receive statistics (null when the transport passed no IP).</summary>
        public WireSessionStats Session;
    }

    /// <summary>Receive-side accounting for one client connection (raised as session
    /// stats when the connection finishes successfully).</summary>
    public class WireSessionStats
    {
        public string Peer;
        public readonly System.Diagnostics.Stopwatch Watch = System.Diagnostics.Stopwatch.StartNew();
        public long Bytes;
        public int Files;
        public bool Encrypted;
        /// <summary>True when the 0x08 compression prelude was accepted for the connection.</summary>
        public bool Compressed;
        public bool Rejected;
        /// <summary>Display name of the transfer (file or folder name) — first one wins.</summary>
        public string Detail;
        /// <summary>First received file's full save path (history "open location").</summary>
        public string Path;
    }

    /// <summary>State a server keeps across client connections: save location plus chunk/resume tracking.</summary>
    public class ServerWireContext
    {
        public string SaveDirectory;
        public int BufferSize = 4194304;
        /// <summary>When non-empty, clients must present this pairing code (0x05/0x07 frame)
        /// before any transfer type is accepted. Empty = open to the LAN.</summary>
        public string PairingCode;
        /// <summary>When true, an incoming file identical (size + SHA256) to an existing
        /// one is discarded instead of saved under a _1 suffix.</summary>
        public bool SkipDuplicateFiles;
        /// <summary>When true, each client's files land in a per-device subdirectory of
        /// SaveDirectory (device name from ResolveDeviceName, else the IP).</summary>
        public bool PerDeviceFolder;
        /// <summary>Connection gate: returns false to drop the connection before any
        /// bytes are read. Null = allow all.</summary>
        public Func<string, bool> IpAllowed;
        /// <summary>Receive confirmation gate: (ip, name, size or -1, fileCount, isFolder) →
        /// false refuses the transfer. Null = accept silently.</summary>
        public Func<string, string, long, int, bool, Task<bool>> ConfirmRequest;
        /// <summary>Maps a client IP to a friendly device folder name (per-device mode).</summary>
        public Func<string, string> ResolveDeviceName;
        /// <summary>Receive-side rate limiter shared by every connection of this server
        /// (one global bucket). Null = unlimited. Thread-safe (see SpeedLimiter).</summary>
        public SpeedLimiter ReceiveLimiter;
        public WireCallbacks Cb = new WireCallbacks();
        // Not readonly: per-connection clones (BeginSession) share the parent's instances
        public ConcurrentDictionary<string, ChunkTracker> ChunkTrackers
            = new ConcurrentDictionary<string, ChunkTracker>();
        public ConcurrentDictionary<Guid, ResumeState> ResumeStates
            = new ConcurrentDictionary<Guid, ResumeState>();
        /// <summary>One mutex per resume session: a second connection carrying the same
        /// sessionId must not share the first one's write stream — that interleaves
        /// offsets and corrupts both the file and the persisted checkpoint.</summary>
        public ConcurrentDictionary<Guid, System.Threading.SemaphoreSlim> ResumeLocks
            = new ConcurrentDictionary<Guid, System.Threading.SemaphoreSlim>();
        /// <summary>Consecutive pairing failures per peer IP. The pairing code is only
        /// 6 digits, so without a cap a LAN peer can brute-force it with cheap 0x05/0x07
        /// frames; after MaxAuthFailures the peer is locked out until it authenticates
        /// successfully (or the server restarts).</summary>
        public ConcurrentDictionary<string, int> AuthFailures
            = new ConcurrentDictionary<string, int>();
        /// <summary>Pairing failures allowed per peer IP before lockout. 0 disables.</summary>
        public int MaxAuthFailures = 10;
        /// <summary>Statistics for the CURRENT connection (set by BeginSession).</summary>
        public WireSessionStats Session;
        /// <summary>True when BeginSession redirected SaveDirectory into a per-device folder.</summary>
        public bool PerDeviceApplied;
        /// <summary>True for transports whose sender would otherwise never learn the
        /// receiver's verdict (TCP): after a 0x00/0x01/0x06 transfer the server writes
        /// one status byte through the live stream (0x01 accepted, 0x00 refused) so a
        /// rejected transfer is not reported as a success. UDT has its own out-of-band
        /// ACK, so its server leaves this false and the wire format is unchanged.</summary>
        public bool CompletionAck;

        /// <summary>Persists incomplete resume sessions and disposes trackers (called from server Stop).</summary>
        public void Shutdown()
        {
            foreach (var kv in ChunkTrackers)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            ChunkTrackers.Clear();

            // Persist incomplete resume states before releasing file handles.
            // Each step is guarded separately: a Flush/Save hiccup (it races the
            // handler thread's in-flight WriteAsync) must NOT skip the Dispose —
            // a leaked write handle keeps FileShare.None locked, and the next
            // server run's restore-open fails and discards the checkpoint.
            foreach (var kv in ResumeStates)
            {
                ResumeState st = kv.Value;
                try { if (st.WriteStream != null) st.WriteStream.Flush(); }
                catch { }
                try { ServerResumeStore.Save(st); }
                catch { }
                if (st.WriteStream != null)
                {
                    try { st.WriteStream.Dispose(); }
                    catch { }
                    st.WriteStream = null;
                }
            }
            ResumeStates.Clear();

            foreach (var kv in ResumeLocks)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            ResumeLocks.Clear();
        }
    }

    #endregion

    #region Server-side protocol

    /// <summary>Receiving side of the 0x00-0x03 protocol, shared by TCP and UDT servers.</summary>
    public static class ServerWire
    {
        /// <summary>Maximum payload size for a 0x06 text message (1 MB of UTF-8 bytes).</summary>
        public const int MaxTextBytes = 1048576;
        /// <summary>Upper bound on the file count in a folder manifest (0x01/0x04).</summary>
        public const int MaxFolderFileCount = 1000000;

        /// <summary>Single-line preview of a text message for logs and balloon tips.</summary>
        public static string Preview(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string line = text.Replace("\r", "").Replace("\n", " / ");
            if (line.Length > 160) line = line.Substring(0, 160) + "…";
            return line;
        }

        /// <summary>
        /// Builds the per-connection context: a WireCallbacks wrapper counting received
        /// bytes/files (session stats), and — when per-device folders are enabled — a
        /// save directory redirected into a per-device subfolder. The chunk/resume
        /// dictionaries stay shared with the parent context.
        /// </summary>
        public static ServerWireContext BeginSession(ServerWireContext ctx, string clientIp)
        {
            if (string.IsNullOrEmpty(clientIp)) return ctx;
            var stats = new WireSessionStats { Peer = clientIp };
            var scb = new WireCallbacks();
            scb.Log = ctx.Cb.Log;
            scb.Progress = ctx.Cb.Progress;
            scb.Error = ctx.Cb.Error;
            scb.Complete = ctx.Cb.Complete;
            scb.TextReceived = ctx.Cb.TextReceived;
            scb.FileReceived = delegate(string path, long size)
            {
                stats.Bytes += size;
                stats.Files++;
                if (string.IsNullOrEmpty(stats.Path)) stats.Path = path;
                ctx.Cb.RaiseFileReceived(path, size);
            };

            var clone = new ServerWireContext();
            clone.SaveDirectory = ctx.SaveDirectory;
            clone.BufferSize = ctx.BufferSize;
            clone.PairingCode = ctx.PairingCode;
            clone.SkipDuplicateFiles = ctx.SkipDuplicateFiles;
            clone.IpAllowed = ctx.IpAllowed;
            clone.ConfirmRequest = ctx.ConfirmRequest;
            clone.ResolveDeviceName = ctx.ResolveDeviceName;
            // One shared receive bucket for the whole server — concurrent connections
            // split the configured limit instead of each getting a full one
            clone.ReceiveLimiter = ctx.ReceiveLimiter;
            // Chunk reassembly and resume sessions are process-wide state — every
            // connection must share the parent's dictionaries
            clone.ChunkTrackers = ctx.ChunkTrackers;
            clone.ResumeStates = ctx.ResumeStates;
            // Per-session mutexes must also be process-wide, or two connections could
            // each take their own copy of the "same" lock and still share a FileStream.
            clone.ResumeLocks = ctx.ResumeLocks;
            // Pairing-failure counters are process-wide (per peer IP, across connections)
            clone.AuthFailures = ctx.AuthFailures;
            clone.MaxAuthFailures = ctx.MaxAuthFailures;
            // Transport-selected: TCP needs the sender to read the verdict byte.
            clone.CompletionAck = ctx.CompletionAck;
            clone.Cb = scb;
            clone.Session = stats;

            if (ctx.PerDeviceFolder && ctx.ResolveDeviceName != null)
            {
                string sub = SanitizeDeviceFolder(ctx.ResolveDeviceName(clientIp), clientIp);
                if (!string.IsNullOrEmpty(sub))
                {
                    clone.SaveDirectory = Path.Combine(ctx.SaveDirectory, sub);
                    clone.PerDeviceApplied = true;
                }
            }
            return clone;
        }

        /// <summary>Maps a device name to a safe single-level folder name; falls back
        /// to the IP when nothing usable remains.</summary>
        private static string SanitizeDeviceFolder(string raw, string fallbackIp)
        {
            if (string.IsNullOrWhiteSpace(raw)) raw = fallbackIp;
            var sb = new System.Text.StringBuilder();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                bool bad = false;
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (invalid[j] == c) { bad = true; break; }
                }
                sb.Append(bad ? '_' : c);
            }
            string s = sb.ToString().Trim();
            if (s.Length > 60) s = s.Substring(0, 60);
            return s.Length == 0 ? fallbackIp : s;
        }

        /// <summary>Reads the transfer type and dispatches. Throws on connection errors.
        /// clientIp enables the policy gates (IP filter, per-device folders, session stats).</summary>
        public static async Task<WireOutcome> HandleClientAsync(IWireStream s, ServerWireContext ctx, CancellationToken ct,
            string clientIp = null)
        {
            var outcome = new WireOutcome();

            if (!string.IsNullOrEmpty(clientIp) && ctx.IpAllowed != null && !ctx.IpAllowed(clientIp))
            {
                ctx.Cb.RaiseLog(L.S_IpRejected(clientIp));
                ctx.Cb.RaiseError(L.S_IpRejected(clientIp));
                outcome.Rejected = true;
                return outcome;
            }

            ctx = BeginSession(ctx, clientIp);
            outcome.Session = ctx.Session;

            var typeBuf = new byte[1];
            await s.ReadExactAsync(typeBuf, 0, 1, ct).ConfigureAwait(false);
            byte transferType = typeBuf[0];

            IWireStream active = s;
            try
            {
                // Prelude frames, in any mix the client chose: 0x05 pairing, 0x08
                // compression (wraps the stream), 0x07 encryption (wraps again —
                // wire order is encrypt(compress(plain))). A hostile client could
                // otherwise stack unlimited decorators (each 0x08 adds a nested
                // decompressor) — cap the prelude length.
                bool authSeen = false;
                const int MaxPreludeFrames = 8;
                int preludeFrames = 0;
                while (true)
                {
                    if (transferType == 0x07)
                    {
                        if (++preludeFrames > MaxPreludeFrames)
                        {
                            ctx.Cb.RaiseLog(L.S_InvalidHeader(transferType, preludeFrames));
                            return outcome;
                        }
                        IWireStream enc = await HandleEncryptedAuthAsync(active, ctx, ct).ConfigureAwait(false);
                        if (enc == null)
                            return outcome; // rejected — outcome.Rejected was flagged via Session
                        active = enc;
                        authSeen = true;
                    }
                    else if (transferType == 0x05)
                    {
                        if (++preludeFrames > MaxPreludeFrames)
                        {
                            ctx.Cb.RaiseLog(L.S_InvalidHeader(transferType, preludeFrames));
                            return outcome;
                        }
                        if (!await HandleAuthAsync(active, ctx, ct).ConfigureAwait(false))
                        {
                            outcome.Rejected = true;
                            return outcome;
                        }
                        authSeen = true;
                    }
                    else if (transferType == 0x09)
                    {
                        if (++preludeFrames > MaxPreludeFrames)
                        {
                            ctx.Cb.RaiseLog(L.S_InvalidHeader(transferType, preludeFrames));
                            return outcome;
                        }
                        IWireStream enc2 = await HandleEcdhAuthAsync(active, ctx, ct).ConfigureAwait(false);
                        if (enc2 == null)
                            return outcome; // rejected — Session.Rejected flagged
                        active = enc2;
                        authSeen = true;
                    }
                    else if (transferType == 0x08)
                    {
                        if (++preludeFrames > MaxPreludeFrames)
                        {
                            ctx.Cb.RaiseLog(L.S_InvalidHeader(transferType, preludeFrames));
                            return outcome;
                        }
                        // Compression must not be negotiated before authentication: an
                        // unauthenticated peer could otherwise stack decompressors and
                        // force large per-segment allocations (attacker-declared length,
                        // up to the 64 MB cap) on a server that requires pairing.
                        // Clients with a code always authenticate first, so this is not
                        // reachable for well-behaved peers.
                        if (!authSeen && !string.IsNullOrEmpty(ctx.PairingCode))
                        {
                            ctx.Cb.RaiseLog(L.S_InvalidHeader(transferType, preludeFrames));
                            outcome.Rejected = true;
                            return outcome;
                        }
                        active = await HandleCompressionOfferAsync(active, ctx, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        break;
                    }
                    await active.ReadExactAsync(typeBuf, 0, 1, ct).ConfigureAwait(false);
                    transferType = typeBuf[0];
                }

                if (!authSeen && !string.IsNullOrEmpty(ctx.PairingCode))
                {
                    // Server requires pairing — reject clients that skip authentication
                    await SendAuthResponse(active, 1, ct).ConfigureAwait(false);
                    ctx.Cb.RaiseLog(L.S_AuthRequired);
                    ctx.Cb.RaiseError(L.S_AuthRequired);
                    outcome.Rejected = true;
                    return outcome;
                }

                // Reject unknown transfer types explicitly. Without this the final
                // `return` below parses any unrecognized byte as a 0x00 single-file
                // header, which is confusing and lets a stray type byte look like a
                // legitimate transfer.
                if (transferType != 0x00 && transferType != 0x01 && transferType != 0x02 &&
                    transferType != 0x03 && transferType != 0x04 && transferType != 0x06)
                {
                    ctx.Cb.RaiseLog(L.S_InvalidHeader(transferType, 0));
                    ctx.Cb.RaiseError(L.S_InvalidHeader(transferType, 0));
                    outcome.Rejected = true;
                    return outcome;
                }

                // Per-device save root must exist before single-file handlers write into it
                if (transferType != 0x06 && ctx.PerDeviceApplied)
                {
                    try { Directory.CreateDirectory(ctx.SaveDirectory); } catch { }
                }

                // 0x00/0x01/0x06 have no other way for the sender to learn the verdict.
                // On TCP the server writes one status byte through the live (possibly
                // decorated) stream: 0x01 accepted, 0x00 refused. On UDT CompletionAck
                // is false — the transport sends its own out-of-band ACK/NACK and the
                // wire format stays byte-identical.
                if (transferType == 0x01)
                {
                    bool okFolder = await HandleFolderTransfer(active, ctx, ct).ConfigureAwait(false);
                    if (ctx.CompletionAck) await WriteCompletionAck(active, okFolder, ct).ConfigureAwait(false);
                    return UpdateOutcome(outcome, okFolder);
                }
                if (transferType == 0x02)
                {
                    outcome.IsChunked = true;
                    return UpdateOutcome(outcome, await HandleChunkedFile(active, ctx, ct).ConfigureAwait(false));
                }
                if (transferType == 0x03)
                {
                    var o3 = UpdateOutcome(outcome, await HandleResumableFile(active, ctx, ct).ConfigureAwait(false));
                    o3.HasOwnFinalResponse = true; // the final 0x10 status was the verdict
                    return o3;
                }
                if (transferType == 0x04)
                    return UpdateOutcome(outcome, await HandleFolderResumableAsync(active, ctx, ct).ConfigureAwait(false));
                if (transferType == 0x06)
                {
                    bool okText = await HandleTextMessage(active, ctx, ct).ConfigureAwait(false);
                    if (ctx.CompletionAck) await WriteCompletionAck(active, okText, ct).ConfigureAwait(false);
                    return UpdateOutcome(outcome, okText);
                }
                bool okFile = await HandleFileTransfer(active, ctx, ct).ConfigureAwait(false);
                if (ctx.CompletionAck) await WriteCompletionAck(active, okFile, ct).ConfigureAwait(false);
                return UpdateOutcome(outcome, okFile);
            }
            finally
            {
                if (ctx.Session != null && ctx.Session.Rejected) outcome.Rejected = true;
                if (!ReferenceEquals(active, s)) active.Dispose();
            }
        }

        private static WireOutcome UpdateOutcome(WireOutcome outcome, bool success)
        {
            outcome.Success = success;
            return outcome;
        }

        /// <summary>TCP completion verdict for 0x00/0x01/0x06: one byte the client
        /// blocks on (0x01 = accepted, 0x00 = refused). Best-effort — a failure here
        /// means the connection is already gone, which the client sees as no ACK.</summary>
        private static async Task WriteCompletionAck(IWireStream s, bool ok, CancellationToken ct)
        {
            var buf = new byte[1];
            buf[0] = ok ? (byte)0x01 : (byte)0x00;
            try
            {
                await s.WriteExactAsync(buf, 0, 1, ct).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Receive-confirmation gate. Runs the context's ConfirmRequest delegate
        /// (UI prompt) and refuses the transfer when it answers false.</summary>
        private static async Task<bool> GateAsync(ServerWireContext ctx, string name, long size, int fileCount, bool isFolder)
        {
            // Remember what this connection is about to receive (session stats detail)
            if (ctx.Session != null && string.IsNullOrEmpty(ctx.Session.Detail))
                ctx.Session.Detail = name;
            var gate = ctx.ConfirmRequest;
            if (gate == null) return true;
            string ip = ctx.Session != null ? ctx.Session.Peer : "";
            bool ok;
            try
            {
                ok = await gate(ip, name, size, fileCount, isFolder).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The gate is the receive-confirmation security control: on failure it
                // must deny, not silently allow. The log keeps a broken gate diagnosable.
                ok = false;
                ctx.Cb.RaiseLog(L.S_ConfirmGateError(ex.Message));
            }
            if (ok)
            {
                ctx.Cb.RaiseLog(L.S_ConfirmAccepted(name));
            }
            else
            {
                if (ctx.Session != null) ctx.Session.Rejected = true;
                ctx.Cb.RaiseLog(L.S_ConfirmDenied(name, ip));
                ctx.Cb.RaiseError(L.S_ConfirmDenied(name, ip));
            }
            return ok;
        }

        /// <summary>
        /// Handles the 0x07 encrypted pairing frame: verifies the code hash and, on
        /// success, answers 0x17 status 0 and wraps the stream for the rest of the
        /// session. Returns null when the connection was rejected. Returns the raw
        /// stream when the server has no pairing code (status 2 — both sides continue
        /// in plaintext on the same connection).
        /// </summary>
        private static async Task<IWireStream> HandleEncryptedAuthAsync(IWireStream s, ServerWireContext ctx, CancellationToken ct)
        {
            var hashBuf = new byte[32];
            await s.ReadExactAsync(hashBuf, 0, 32, ct).ConfigureAwait(false);
            var salt = new byte[SessionCrypto.SaltBytes];
            await s.ReadExactAsync(salt, 0, salt.Length, ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(ctx.PairingCode))
            {
                await SendEncAuthResponse(s, 2, ct).ConfigureAwait(false);
                ctx.Cb.RaiseLog(L.S_EncryptUnsupported);
                return s;
            }

            string legacyPeer = ctx.Session != null ? ctx.Session.Peer : null;
            if (AuthLockedOut(ctx, legacyPeer))
            {
                await SendEncAuthResponse(s, 1, ct).ConfigureAwait(false);
                ctx.Cb.RaiseLog(L.S_AuthLockedOut(legacyPeer));
                if (ctx.Session != null) ctx.Session.Rejected = true;
                return null;
            }

            byte[] codeHash = WireAuth.HashCode(ctx.PairingCode);
            if (!Utils.ConstantTimeEquals(hashBuf, codeHash))
            {
                NoteAuthFailure(ctx, legacyPeer);
                await SendEncAuthResponse(s, 1, ct).ConfigureAwait(false);
                ctx.Cb.RaiseLog(L.S_AuthFailed);
                ctx.Cb.RaiseError(L.S_AuthFailed);
                if (ctx.Session != null) ctx.Session.Rejected = true;
                return null;
            }
            NoteAuthSuccess(ctx, legacyPeer);

            await SendEncAuthResponse(s, 0, ct).ConfigureAwait(false);
            byte[] c2sEnc, c2sMac, s2cEnc, s2cMac;
            SessionCrypto.DeriveSessionKeys(codeHash, salt, out c2sEnc, out c2sMac, out s2cEnc, out s2cMac);
            if (ctx.Session != null) ctx.Session.Encrypted = true;
            ctx.Cb.RaiseLog(L.S_EncryptedOn);
            // Legacy 0x07: the session key is derived from the pairing-code hash the
            // client just sent in the clear, so this only protects against a passive
            // eavesdropper who missed the handshake. Warn so operators upgrade.
            ctx.Cb.RaiseLog(L.S_LegacyCryptoWeak);
            // The server sends with the s2c keys and receives with the c2s keys.
            // ownsInner:false — the transport owns the socket and must keep it open to
            // send its application-level ACK/NACK after this decorator is disposed.
            return new EncryptedWireStream(s, s2cEnc, s2cMac, c2sEnc, c2sMac, false);
        }

        /// <summary>True when the peer has failed pairing too many times and is locked out.</summary>
        private static bool AuthLockedOut(ServerWireContext ctx, string peer)
        {
            if (ctx.MaxAuthFailures <= 0 || string.IsNullOrEmpty(peer)) return false;
            int n;
            return ctx.AuthFailures.TryGetValue(peer, out n) && n >= ctx.MaxAuthFailures;
        }

        private static void NoteAuthFailure(ServerWireContext ctx, string peer)
        {
            if (ctx.MaxAuthFailures <= 0 || string.IsNullOrEmpty(peer)) return;
            ctx.AuthFailures.AddOrUpdate(peer, 1, delegate (string k, int v) { return v + 1; });
        }

        private static void NoteAuthSuccess(ServerWireContext ctx, string peer)
        {
            if (string.IsNullOrEmpty(peer)) return;
            int ignored;
            ctx.AuthFailures.TryRemove(peer, out ignored);
        }

        private static async Task SendEncAuthResponse(IWireStream s, byte status, CancellationToken ct)
        {
            var resp = new byte[2]; // type(1) + status(1)
            resp[0] = 0x17;
            resp[1] = status;
            await s.WriteExactAsync(resp, 0, 2, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Server side of the authenticated ECDH handshake (0x09). Wire:
        ///   client → server: [0x09][codeHash 32][clientPub 72]
        ///   server → client: [0x19][status 1][serverPub 72][confirmS 32]
        ///   client → server: [confirmC 32]
        /// status: 0 accepted, 1 code wrong (session key is then a random dummy so the
        /// reply carries no information an attacker could use), 2 pairing disabled
        /// (continue plaintext on the same connection). Returns the raw stream for
        /// status 1/2; a wrapped EncryptedWireStream on success.
        /// </summary>
        private static async Task<IWireStream> HandleEcdhAuthAsync(IWireStream s, ServerWireContext ctx, CancellationToken ct)
        {
            var buf = new byte[32 + SessionCrypto.EcdhPublicBytes];
            await s.ReadExactAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
            var codeHash = new byte[32];
            Buffer.BlockCopy(buf, 0, codeHash, 0, 32);
            var clientPub = new byte[SessionCrypto.EcdhPublicBytes];
            Buffer.BlockCopy(buf, 32, clientPub, 0, clientPub.Length);

            string peer = ctx.Session != null ? ctx.Session.Peer : null;
            if (string.IsNullOrEmpty(ctx.PairingCode))
            {
                await SendEcdhAuthResponse(s, 2, new byte[SessionCrypto.EcdhPublicBytes], new byte[32], ct).ConfigureAwait(false);
                ctx.Cb.RaiseLog(L.S_EncryptUnsupported);
                return s;
            }

            bool codeOk = !AuthLockedOut(ctx, peer)
                && Utils.ConstantTimeEquals(codeHash, WireAuth.HashCode(ctx.PairingCode));
            if (!codeOk && AuthLockedOut(ctx, peer))
                ctx.Cb.RaiseLog(L.S_AuthLockedOut(peer));

            byte[] serverPub;
            byte[] authKey;
            try
            {
                using (var serverKey = SessionCrypto.CreateEcdhKey())
                {
                    serverPub = SessionCrypto.EcdhPublicBlob(serverKey);
                    byte[] shared = null;
                    try { shared = SessionCrypto.EcdhDeriveShared(serverKey, clientPub); }
                    catch { /* malformed peer blob — generate a dummy secret below */ }
                    if (shared == null)
                    {
                        shared = new byte[32];
                        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                            rng.GetBytes(shared);
                    }
                    // Always derive a key (even on mismatch) so the rejection reply is
                    // indistinguishable from a success reply except for the status byte.
                    byte[] keyMaterial = codeOk ? SessionCrypto.DeriveAuthKey(ctx.PairingCode, clientPub, serverPub, shared) : null;
                    if (keyMaterial == null)
                    {
                        keyMaterial = new byte[SessionCrypto.KeyBytes];
                        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                            rng.GetBytes(keyMaterial);
                    }
                    authKey = keyMaterial;

                    byte[] confirmS = SessionCrypto.Confirm(authKey, "s2c-confirm", clientPub, serverPub);
                    await SendEcdhAuthResponse(s, codeOk ? (byte)0 : (byte)1, serverPub, confirmS, ct).ConfigureAwait(false);

                    if (!codeOk)
                    {
                        NoteAuthFailure(ctx, peer);
                        ctx.Cb.RaiseLog(L.S_AuthFailed);
                        ctx.Cb.RaiseError(L.S_AuthFailed);
                        if (ctx.Session != null) ctx.Session.Rejected = true;
                        return null;
                    }

                    // Client prove-it confirm
                    var confirmC = new byte[32];
                    await s.ReadExactAsync(confirmC, 0, 32, ct).ConfigureAwait(false);
                    byte[] expected = SessionCrypto.Confirm(authKey, "c2s-confirm", clientPub, serverPub);
                    if (!Utils.ConstantTimeEquals(confirmC, expected))
                    {
                        ctx.Cb.RaiseLog(L.S_EncHandshakeFailed);
                        ctx.Cb.RaiseError(L.S_EncHandshakeFailed);
                        if (ctx.Session != null) ctx.Session.Rejected = true;
                        return null;
                    }

                    NoteAuthSuccess(ctx, peer);
                    byte[] c2sEnc, c2sMac, s2cEnc, s2cMac;
                    SessionCrypto.DeriveSessionKeys(authKey, new byte[SessionCrypto.SaltBytes], out c2sEnc, out c2sMac, out s2cEnc, out s2cMac);
                    if (ctx.Session != null) ctx.Session.Encrypted = true;
                    ctx.Cb.RaiseLog(L.S_EncryptedOn);
                    // ownsInner:false — the transport sends its own ACK/NACK afterwards.
                    // bindSequence:true — 0x09 peers use the sequence-bound segment format.
                    return new EncryptedWireStream(s, s2cEnc, s2cMac, c2sEnc, c2sMac, false, true);
                }
            }
            catch (IOException)
            {
                ctx.Cb.RaiseLog(L.S_EncHandshakeFailed);
                if (ctx.Session != null) ctx.Session.Rejected = true;
                return null;
            }
        }

        private static async Task SendEcdhAuthResponse(IWireStream s, byte status, byte[] serverPub, byte[] confirmS, CancellationToken ct)
        {
            var resp = new byte[1 + 1 + SessionCrypto.EcdhPublicBytes + 32];
            resp[0] = 0x19;
            resp[1] = status;
            Buffer.BlockCopy(serverPub, 0, resp, 2, serverPub.Length);
            Buffer.BlockCopy(confirmS, 0, resp, 2 + serverPub.Length, 32);
            await s.WriteExactAsync(resp, 0, resp.Length, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Handles the 0x08 compression offer: 16 padding bytes follow the type (an
        /// older peer mistakes the frame for a corrupt transfer header and drops the
        /// connection — exactly what the client's fallback detects). The 0x18 status 0
        /// answer accepts, and every following byte rides in deflate segments.
        /// </summary>
        private static async Task<IWireStream> HandleCompressionOfferAsync(IWireStream s, ServerWireContext ctx, CancellationToken ct)
        {
            var pad = new byte[16];
            await s.ReadExactAsync(pad, 0, 16, ct).ConfigureAwait(false);

            var resp = new byte[2]; // type(1) + status(1)
            resp[0] = 0x18;
            resp[1] = 0; // accepted
            await s.WriteExactAsync(resp, 0, 2, ct).ConfigureAwait(false);

            if (ctx.Session != null) ctx.Session.Compressed = true;
            ctx.Cb.RaiseLog(L.S_CompressedOn);
            // ownsInner:false — same reason as the encryption decorator: keep the socket
            // open for the transport's ACK after the decorator is disposed.
            return new CompressedWireStream(s, false);
        }

        /// <summary>
        /// Verifies a 0x05 pairing frame and answers with 0x15 status. A server without
        /// a configured code accepts any 0x05 (lenient path — clients may always send
        /// their code). Returns false (after rejecting) when the code is wrong.
        /// </summary>
        private static async Task<bool> HandleAuthAsync(IWireStream s, ServerWireContext ctx, CancellationToken ct)
        {
            var hashBuf = new byte[32];
            await s.ReadExactAsync(hashBuf, 0, 32, ct).ConfigureAwait(false);

            string peer = ctx.Session != null ? ctx.Session.Peer : null;
            if (AuthLockedOut(ctx, peer))
            {
                await SendAuthResponse(s, 1, ct).ConfigureAwait(false);
                ctx.Cb.RaiseLog(L.S_AuthLockedOut(peer));
                ctx.Cb.RaiseError(L.S_AuthFailed);
                return false;
            }

            byte status;
            if (string.IsNullOrEmpty(ctx.PairingCode))
            {
                status = 0;
            }
            else
            {
                status = Utils.ConstantTimeEquals(hashBuf, WireAuth.HashCode(ctx.PairingCode)) ? (byte)0 : (byte)1;
            }
            await SendAuthResponse(s, status, ct).ConfigureAwait(false);

            if (status == 1)
            {
                NoteAuthFailure(ctx, peer);
                ctx.Cb.RaiseLog(L.S_AuthFailed);
                ctx.Cb.RaiseError(L.S_AuthFailed);
                return false;
            }
            NoteAuthSuccess(ctx, peer);
            ctx.Cb.RaiseLog(L.S_AuthOk);
            return true;
        }

        private static async Task SendAuthResponse(IWireStream s, byte status, CancellationToken ct)
        {
            var resp = new byte[2]; // type(1) + status(1)
            resp[0] = 0x15;
            resp[1] = status;
            await s.WriteExactAsync(resp, 0, 2, ct).ConfigureAwait(false);
        }

        /// <summary>Receives a 0x06 UTF-8 text message. Delivered to the UI via
        /// TextReceived; success mirrors file semantics (clean close on TCP, 1-byte
        /// app ACK on UDT), so no extra response frame is sent.</summary>
        private static async Task<bool> HandleTextMessage(IWireStream s, ServerWireContext ctx, CancellationToken ct)
        {
            var lenBuf = new byte[4];
            await s.ReadExactAsync(lenBuf, 0, 4, ct).ConfigureAwait(false);
            int len = BitConverter.ToInt32(lenBuf, 0);
            if (len < 0 || len > MaxTextBytes)
            {
                ctx.Cb.RaiseLog(L.S_TextRejected);
                ctx.Cb.RaiseError(L.S_TextRejected);
                return false;
            }

            var buf = new byte[len];
            if (len > 0)
                await s.ReadExactAsync(buf, 0, len, ct).ConfigureAwait(false);
            string text = System.Text.Encoding.UTF8.GetString(buf);

            ctx.Cb.RaiseLog(L.S_TextReceived(Preview(text)));
            ctx.Cb.RaiseTextReceived(text);
            return true;
        }

        /// <summary>Normalizes a received name to a bare file name with a fallback.
        /// Reserved device names (CON, NUL, COM1…) and trailing dots/spaces are
        /// neutralized — they either open a device instead of a file or desync the
        /// unique-name collision check from what actually lands on disk.</summary>
        private static string SafeName(string rawName, string fallback)
        {
            string name = Path.GetFileName(rawName);
            if (string.IsNullOrWhiteSpace(name))
                name = fallback;
            name = name.TrimEnd('.', ' ');
            if (name.Length == 0)
                return fallback;
            if (Utils.IsReservedFileName(name))
                name = "_" + name;
            return name;
        }

        /// <summary>Deterministic save directory for a folder-resume session — the same
        /// session always maps to the same directory (across restarts), without the
        /// uniqueness suffix that GetUniqueSavePath would vary between attempts.</summary>
        public static string GetFolderSessionDir(string saveDirectory, string folderName, Guid sessionId)
        {
            return Path.Combine(saveDirectory, folderName + "." + sessionId.ToString("N").Substring(0, 8));
        }

        /// <summary>
        /// Folder resume (type 0x04). The client sends a manifest (path, size, full-file
        /// SHA256 per file); the server scans its session directory for already-complete
        /// files and answers where to continue. Files are assumed to be sent sequentially
        /// in manifest order, so the resume point is (first incomplete index, offset in it).
        /// </summary>
        private static async Task<bool> HandleFolderResumableAsync(IWireStream s, ServerWireContext ctx, CancellationToken ct)
        {
            // Header: sessionId(16) + folderNameLen(2) + folderName + fileCount(4) + totalBytes(8)
            var headBuf = new byte[16 + 2];
            await s.ReadExactAsync(headBuf, 0, headBuf.Length, ct).ConfigureAwait(false);
            var sidBytes = new byte[16];
            Buffer.BlockCopy(headBuf, 0, sidBytes, 0, 16);
            var sessionId = new Guid(sidBytes);
            int folderNameLen = BitConverter.ToInt16(headBuf, 16);
            if (folderNameLen <= 0 || folderNameLen > 4096) return false;

            var folderNameBuf = new byte[folderNameLen];
            await s.ReadExactAsync(folderNameBuf, 0, folderNameLen, ct).ConfigureAwait(false);
            string folderName = SafeName(System.Text.Encoding.UTF8.GetString(folderNameBuf), "received_folder");

            var countBuf = new byte[4 + 8];
            await s.ReadExactAsync(countBuf, 0, countBuf.Length, ct).ConfigureAwait(false);
            int fileCount = BitConverter.ToInt32(countBuf, 0);
            long totalBytes = BitConverter.ToInt64(countBuf, 4);
            if (fileCount <= 0 || fileCount > MaxFolderFileCount || totalBytes < 0 || totalBytes > Utils.MaxTransferSize) return false;

            var relativePaths = new string[fileCount];
            var sizes = new long[fileCount];
            var fullHashes = new byte[fileCount][];
            long manifestTotal = 0;
            for (int i = 0; i < fileCount; i++)
            {
                var fileHeader = new byte[8 + 2];
                await s.ReadExactAsync(fileHeader, 0, fileHeader.Length, ct).ConfigureAwait(false);
                long size = BitConverter.ToInt64(fileHeader, 0);
                int pathLen = BitConverter.ToInt16(fileHeader, 8);
                if (size < 0 || size > Utils.MaxTransferSize || pathLen <= 0 || pathLen > 4096) return false;

                var pathBuf = new byte[pathLen];
                await s.ReadExactAsync(pathBuf, 0, pathLen, ct).ConfigureAwait(false);
                relativePaths[i] = Utils.SanitizeRelativePath(System.Text.Encoding.UTF8.GetString(pathBuf));

                var hashBuf = new byte[32];
                await s.ReadExactAsync(hashBuf, 0, 32, ct).ConfigureAwait(false);
                fullHashes[i] = hashBuf;

                sizes[i] = size;
                manifestTotal += size;
            }
            if (manifestTotal != totalBytes) return false;

            if (!await GateAsync(ctx, folderName, totalBytes, fileCount, true).ConfigureAwait(false))
                return false;

            string dir = GetFolderSessionDir(ctx.SaveDirectory, folderName, sessionId);
            Directory.CreateDirectory(dir);

            // Resume scan: everything before the first incomplete entry is considered done
            int resumeIndex = fileCount;
            long resumeOffset = 0;
            for (int i = 0; i < fileCount; i++)
            {
                string path = Path.Combine(dir, relativePaths[i]);
                if (!File.Exists(path))
                {
                    resumeIndex = i;
                    resumeOffset = 0;
                    break;
                }
                long len = new FileInfo(path).Length;
                if (len == sizes[i])
                {
                    // Complete size — verify content against the manifest hash
                    if (await VerifyFullHashFile(path, fullHashes[i], ct).ConfigureAwait(false))
                        continue;
                    resumeIndex = i;
                    resumeOffset = 0;
                    break;
                }
                if (len > 0 && len < sizes[i])
                {
                    resumeIndex = i;
                    resumeOffset = len;
                    break;
                }
                // Missing, empty-but-nonzero, or oversized — rewrite from scratch
                resumeIndex = i;
                resumeOffset = 0;
                break;
            }

            byte status = (byte)(resumeIndex >= fileCount ? 2 : (resumeIndex > 0 || resumeOffset > 0 ? 1 : 0));

            // 0x11 response: resumeFileIndex(8) + resumeOffset(8) + status(1)
            var resp = new byte[1 + 8 + 8 + 1];
            resp[0] = 0x11;
            Buffer.BlockCopy(BitConverter.GetBytes((long)resumeIndex), 0, resp, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(resumeOffset), 0, resp, 9, 8);
            resp[17] = status;
            await s.WriteExactAsync(resp, 0, resp.Length, ct).ConfigureAwait(false);

            ctx.Cb.RaiseLog(string.Format("Folder resume: session={0} start={1}/{2} offset={3} status={4}",
                sessionId.ToString("N"), resumeIndex, fileCount, resumeOffset, status));

            if (status == 2)
            {
                ctx.Cb.RaiseLog(L.S_TransferDone(folderName, Utils.FormatSize(totalBytes), 0.0, ""));
                ctx.Cb.RaiseComplete();
                return true;
            }

            // Receive body: for each file from resumeIndex, [increment bytes][32-byte SHA256 of increment]
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long completedBytes = resumeOffset;
            for (int i = 0; i < resumeIndex; i++) completedBytes += sizes[i];

            for (int i = resumeIndex; i < fileCount && !ct.IsCancellationRequested; i++)
            {
                long start = (i == resumeIndex) ? resumeOffset : 0;
                string savePath = Path.Combine(dir, relativePaths[i]);
                string subDir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(subDir) && !Directory.Exists(subDir))
                    Directory.CreateDirectory(subDir);

                if (!Utils.HasFreeSpaceFor(dir, sizes[i]))
                {
                    ctx.Cb.RaiseLog(L.S_DiskFull(relativePaths[i], Utils.FormatSize(sizes[i])));
                    ctx.Cb.RaiseError(L.S_DiskFull(relativePaths[i], Utils.FormatSize(sizes[i])));
                    return false;
                }

                using (var fileStream = new FileStream(savePath, start > 0 ? FileMode.Open : FileMode.Create,
                    FileAccess.Write, FileShare.None, ctx.BufferSize, FileOptions.SequentialScan))
                {
                    if (start > 0)
                        fileStream.Seek(start, SeekOrigin.Begin);

                    using (var sha256 = System.Security.Cryptography.SHA256.Create())
                    {
                        long remaining = sizes[i] - start;
                        if (remaining == 0)
                        {
                            sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                        }
                        else
                        {
                            var buf = new byte[ctx.BufferSize];
                            while (remaining > 0 && !ct.IsCancellationRequested)
                            {
                                int toRead = (int)Math.Min(remaining, (long)buf.Length);
                                int read = await s.ReadSomeAsync(buf, 0, toRead, ct).ConfigureAwait(false);
                                if (read <= 0)
                                    throw new IOException(L.S_ConnClosedPrematurely);
                                if (ctx.ReceiveLimiter != null)
                                    await ctx.ReceiveLimiter.ThrottleAsync(read, ct).ConfigureAwait(false);
                                sha256.TransformBlock(buf, 0, read, null, 0);
                                fileStream.Write(buf, 0, read);
                                remaining -= read;
                            }
                            sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                        }

                        var receivedHash = new byte[32];
                        await s.ReadExactAsync(receivedHash, 0, 32, ct).ConfigureAwait(false);
                        if (!Utils.ConstantTimeEquals(receivedHash, sha256.Hash))
                        {
                            // Keep the partial file so a retry can resume from it
                            ctx.Cb.RaiseLog(L.S_HashFailed(relativePaths[i]));
                            ctx.Cb.RaiseError(L.S_HashFailed(relativePaths[i]));
                            return false;
                        }
                    }
                }

                // A resumed file is old-prefix + new-suffix. The increment hash covers
                // only the suffix, so re-verify the assembled file against the manifest
                // hash — otherwise a stale/corrupt prefix yields a silently wrong file.
                if (start > 0 && !await VerifyFullHashFile(savePath, fullHashes[i], ct).ConfigureAwait(false))
                {
                    ctx.Cb.RaiseLog(L.S_FullHashFailed(relativePaths[i]));
                    ctx.Cb.RaiseError(L.S_FullHashFailed(relativePaths[i]));
                    return false;
                }

                ctx.Cb.RaiseFileReceived(savePath, sizes[i]);
                completedBytes += sizes[i] - start;

                ctx.Cb.RaiseProgress(new TransferProgress
                {
                    BytesTransferred = completedBytes,
                    TotalBytes = totalBytes,
                    SpeedBytesPerSecond = completedBytes / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                    Elapsed = sw.Elapsed,
                    FileName = folderName
                });
            }

            sw.Stop();
            ctx.Cb.RaiseLog(L.S_FolderTransferDone(folderName, fileCount, Utils.FormatSize(totalBytes),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(totalBytes / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

            ctx.Cb.RaiseComplete();
            return true;
        }

        private static async Task<bool> HandleFileTransfer(IWireStream s, ServerWireContext ctx, CancellationToken ct)
        {
            var headerBuf = new byte[12];
            await s.ReadExactAsync(headerBuf, 0, 12, ct).ConfigureAwait(false);

            long fileSize = BitConverter.ToInt64(headerBuf, 0);
            int nameLen = BitConverter.ToInt32(headerBuf, 8);

            if (fileSize < 0 || fileSize > Utils.MaxTransferSize || nameLen <= 0 || nameLen > 4096)
            {
                ctx.Cb.RaiseLog(L.S_InvalidHeader(fileSize, nameLen));
                return false;
            }

            var nameBuf = new byte[nameLen];
            await s.ReadExactAsync(nameBuf, 0, nameLen, ct).ConfigureAwait(false);
            string fileName = SafeName(System.Text.Encoding.UTF8.GetString(nameBuf), L.S_ReceivedFile);

            if (!await GateAsync(ctx, fileName, fileSize, 0, false).ConfigureAwait(false))
                return false;

            string basePath = Path.Combine(ctx.SaveDirectory, fileName);
            string savePath = Utils.GetUniqueSavePath(ctx.SaveDirectory, fileName);

            ctx.Cb.RaiseLog(L.S_Receiving(fileName, Utils.FormatSize(fileSize)));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool hashOk = await ReceiveFilePayload(s, ctx, savePath, basePath, fileSize, fileName, ct).ConfigureAwait(false);
            sw.Stop();

            if (hashOk)
            {
                ctx.Cb.RaiseLog(L.S_TransferDone(fileName, Utils.FormatSize(fileSize),
                    sw.Elapsed.TotalSeconds,
                    Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));
                ctx.Cb.RaiseComplete();
                // Dedup-skip deleted the suffixed copy — report the surviving original
                string reportedPath = File.Exists(savePath) ? savePath : basePath;
                ctx.Cb.RaiseFileReceived(reportedPath, fileSize);
            }
            return hashOk;
        }

        private static async Task<bool> HandleFolderTransfer(IWireStream s, ServerWireContext ctx, CancellationToken ct)
        {
            // Folder header: folderNameLen(2) + folderName + fileCount(4)
            var folderHeaderBuf = new byte[2];
            await s.ReadExactAsync(folderHeaderBuf, 0, 2, ct).ConfigureAwait(false);
            int folderNameLen = BitConverter.ToInt16(folderHeaderBuf, 0);
            if (folderNameLen <= 0 || folderNameLen > 4096) return false;

            var folderNameBuf = new byte[folderNameLen];
            await s.ReadExactAsync(folderNameBuf, 0, folderNameLen, ct).ConfigureAwait(false);
            string folderName = SafeName(System.Text.Encoding.UTF8.GetString(folderNameBuf), "received_folder");

            var fileCountBuf = new byte[4];
            await s.ReadExactAsync(fileCountBuf, 0, 4, ct).ConfigureAwait(false);
            int fileCount = BitConverter.ToInt32(fileCountBuf, 0);
            // Same bound as the 0x04 manifest: without it a peer can drive an
            // unbounded per-file loop (directory creation) on one connection.
            if (fileCount <= 0 || fileCount > MaxFolderFileCount) return false;

            if (!await GateAsync(ctx, folderName, -1, fileCount, true).ConfigureAwait(false))
                return false;

            string folderSaveDir = Utils.GetUniqueSavePath(ctx.SaveDirectory, folderName);
            Directory.CreateDirectory(folderSaveDir);

            ctx.Cb.RaiseLog(L.S_ReceivingFolder(folderName, fileCount, "..."));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long totalSize = 0;
            int filesReceived = 0;

            for (int i = 0; i < fileCount && !ct.IsCancellationRequested; i++)
            {
                var fileHeaderBuf = new byte[10];
                await s.ReadExactAsync(fileHeaderBuf, 0, 10, ct).ConfigureAwait(false);
                long fileSize = BitConverter.ToInt64(fileHeaderBuf, 0);
                int pathLen = BitConverter.ToInt16(fileHeaderBuf, 8);
                if (fileSize < 0 || fileSize > Utils.MaxTransferSize || pathLen <= 0 || pathLen > 4096) return false;

                var pathBuf = new byte[pathLen];
                await s.ReadExactAsync(pathBuf, 0, pathLen, ct).ConfigureAwait(false);
                string relativePath = Utils.SanitizeRelativePath(System.Text.Encoding.UTF8.GetString(pathBuf));

                string savePath = Path.Combine(folderSaveDir, relativePath);
                string dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                bool hashOk = await ReceiveFilePayload(s, ctx, savePath, savePath, fileSize, relativePath, ct).ConfigureAwait(false);
                if (!hashOk) return false;
                ctx.Cb.RaiseFileReceived(savePath, fileSize);

                totalSize += fileSize;
                filesReceived++;

                ctx.Cb.RaiseProgress(new TransferProgress
                {
                    BytesTransferred = filesReceived,
                    TotalBytes = fileCount,
                    SpeedBytesPerSecond = (totalSize > 0 ? totalSize : 0) / sw.Elapsed.TotalSeconds,
                    Elapsed = sw.Elapsed,
                    FileName = folderName
                });
            }

            sw.Stop();
            ctx.Cb.RaiseLog(L.S_FolderTransferDone(folderName, fileCount, Utils.FormatSize(totalSize),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(totalSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

            ctx.Cb.RaiseComplete();
            return true;
        }

        private static async Task<bool> HandleChunkedFile(IWireStream s, ServerWireContext ctx, CancellationToken ct)
        {
            // Chunk header: totalSize(8) + chunkOffset(8) + chunkSize(8) + nameLen(4)
            var headerBuf = new byte[28];
            await s.ReadExactAsync(headerBuf, 0, 28, ct).ConfigureAwait(false);

            long totalSize = BitConverter.ToInt64(headerBuf, 0);
            long chunkOffset = BitConverter.ToInt64(headerBuf, 8);
            long chunkSize = BitConverter.ToInt64(headerBuf, 16);
            int nameLen = BitConverter.ToInt32(headerBuf, 24);

            if (totalSize <= 0 || totalSize > Utils.MaxTransferSize || chunkOffset < 0 || chunkSize <= 0 || chunkOffset > totalSize - chunkSize
                || nameLen <= 0 || nameLen > 4096)
            {
                ctx.Cb.RaiseLog(L.S_InvalidHeader(totalSize, nameLen));
                return false;
            }

            var nameBuf = new byte[nameLen];
            await s.ReadExactAsync(nameBuf, 0, nameLen, ct).ConfigureAwait(false);
            string fileName = SafeName(System.Text.Encoding.UTF8.GetString(nameBuf), L.S_ReceivedFile);

            // The chunk file is preallocated at totalSize — require that much headroom
            if (!Utils.HasFreeSpaceFor(ctx.SaveDirectory, totalSize))
            {
                ctx.Cb.RaiseLog(L.S_DiskFull(fileName, Utils.FormatSize(totalSize)));
                ctx.Cb.RaiseError(L.S_DiskFull(fileName, Utils.FormatSize(totalSize)));
                return false;
            }

            if (!await GateAsync(ctx, fileName, totalSize, 0, false).ConfigureAwait(false))
                return false;

            // Tracker key includes the peer: two devices concurrently sending
            // same-named chunked files must not share one reassembly tracker.
            string trackerKey = (ctx.Session != null && !string.IsNullOrEmpty(ctx.Session.Peer)
                ? ctx.Session.Peer : "") + "|" + fileName;
            ChunkTracker tracker = ChunkTracker.GetOrCreate(
                ctx.ChunkTrackers, trackerKey, fileName, totalSize, ctx.SaveDirectory);

            // A tracker left behind by an aborted transfer of a DIFFERENT size under
            // the same name must not be reused: mixing two files' chunks would corrupt
            // the result and, in the coverage model, never complete. Evict it so this
            // transfer gets a clean reassembly.
            if (tracker.TotalSize != totalSize && !tracker.Complete)
            {
                ChunkTracker stale;
                if (ctx.ChunkTrackers.TryRemove(trackerKey, out stale))
                {
                    try { stale.Dispose(); } catch { }
                    ctx.Cb.RaiseLog(L.S_ChunkTrackerReset(fileName));
                }
                tracker = ChunkTracker.GetOrCreate(
                    ctx.ChunkTrackers, trackerKey, fileName, totalSize, ctx.SaveDirectory);
            }

            // Stream chunk data through a fixed-size buffer — no giant array allocation
            const int BufSize = 4194304;
            var buf = new byte[BufSize];
            bool hashOk;
            bool isComplete = false;
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                long remaining = chunkSize;
                long writeOffset = chunkOffset;
                while (remaining > 0 && !ct.IsCancellationRequested)
                {
                    int toRead = (int)Math.Min(remaining, (long)BufSize);
                    await s.ReadExactAsync(buf, 0, toRead, ct).ConfigureAwait(false);
                    if (ctx.ReceiveLimiter != null)
                        await ctx.ReceiveLimiter.ThrottleAsync(toRead, ct).ConfigureAwait(false);
                    sha256.TransformBlock(buf, 0, toRead, null, 0);
                    isComplete = tracker.WriteChunk(writeOffset, buf, toRead);
                    writeOffset += toRead;
                    remaining -= toRead;
                }
                sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                var receivedHash = new byte[32];
                await s.ReadExactAsync(receivedHash, 0, 32, ct).ConfigureAwait(false);
                hashOk = Utils.ConstantTimeEquals(receivedHash, sha256.Hash);
            }

            if (hashOk)
            {
                // Report aggregate progress across all chunks
                ctx.Cb.RaiseProgress(new TransferProgress
                {
                    BytesTransferred = tracker.BytesReceived,
                    TotalBytes = totalSize,
                    SpeedBytesPerSecond = 0,
                    Elapsed = TimeSpan.Zero,
                    FileName = fileName
                });

                ctx.Cb.RaiseLog(L.S_ChunkOk(fileName, chunkOffset, Utils.FormatSize(chunkSize),
                    tracker.ChunksCompleted));

                if (isComplete)
                {
                    tracker.Dispose();
                    ChunkTracker removed;
                    ctx.ChunkTrackers.TryRemove(trackerKey, out removed);
                    ctx.Cb.RaiseLog(L.S_TransferDone(fileName, Utils.FormatSize(totalSize), 0.0, ""));
                    ctx.Cb.RaiseComplete();
                    ctx.Cb.RaiseFileReceived(tracker.SavePath, totalSize);
                }
            }
            else
            {
                // Clean up tracker on hash failure
                ChunkTracker removed;
                if (ctx.ChunkTrackers.TryRemove(trackerKey, out removed))
                {
                    try { removed.Dispose(); } catch { }
                }
                ctx.Cb.RaiseLog(L.S_HashFailed(fileName));
                ctx.Cb.RaiseError(L.S_HashFailed(fileName));
            }
            return hashOk;
        }

        private static async Task<bool> HandleResumableFile(IWireStream s, ServerWireContext ctx, CancellationToken ct)
        {
            // Read 0x03 header: sessionId(16) + totalSize(8) + resumeOffset(8) + nameLen(4) = 36
            var headerBuf = new byte[36];
            await s.ReadExactAsync(headerBuf, 0, 36, ct).ConfigureAwait(false);

            var sidBytes = new byte[16];
            Buffer.BlockCopy(headerBuf, 0, sidBytes, 0, 16);
            var sessionId = new Guid(sidBytes);
            long totalSize = BitConverter.ToInt64(headerBuf, 16);
            long clientOffset = BitConverter.ToInt64(headerBuf, 24);
            int nameLen = BitConverter.ToInt32(headerBuf, 32);

            if (totalSize <= 0 || totalSize > Utils.MaxTransferSize || clientOffset < 0 || clientOffset > totalSize || nameLen <= 0 || nameLen > 4096)
            {
                ctx.Cb.RaiseLog(L.S_InvalidHeader(totalSize, nameLen));
                return false;
            }

            var nameBuf = new byte[nameLen];
            await s.ReadExactAsync(nameBuf, 0, nameLen, ct).ConfigureAwait(false);
            string fileName = SafeName(System.Text.Encoding.UTF8.GetString(nameBuf), L.S_ReceivedFile);

            // Optional full-file hash verification extension: [verifyFlag(1) + fullHash(32)]
            var flagBuf = new byte[1];
            await s.ReadExactAsync(flagBuf, 0, 1, ct).ConfigureAwait(false);
            byte[] expectedFullHash = null;
            if (flagBuf[0] == 1)
            {
                expectedFullHash = new byte[32];
                await s.ReadExactAsync(expectedFullHash, 0, 32, ct).ConfigureAwait(false);
            }

            if (!await GateAsync(ctx, fileName, totalSize, 0, false).ConfigureAwait(false))
                return false;

            // One connection at a time per session. Two connections carrying the same
            // sessionId would otherwise share a single ResumeState/FileStream and
            // interleave seeks — corrupting the file and the checkpoint. A retry after
            // a dropped connection is fine: the old handler releases before the new
            // one arrives (and the client retries again if it loses that race).
            var sessionLock = ctx.ResumeLocks.GetOrAdd(sessionId, delegate (Guid g)
            {
                return new System.Threading.SemaphoreSlim(1, 1);
            });
            try
            {
                if (!await sessionLock.WaitAsync(0, ct).ConfigureAwait(false))
                {
                    ctx.Cb.RaiseLog(string.Format(
                        "Resume: session {0} is already active on another connection — refusing",
                        sessionId.ToString("N")));
                    return false;
                }
            }
            catch (OperationCanceledException) { return false; }

            // From here on, the session is ours: persist any incomplete state to disk
            // on the way out (connection drop / server restart) and clear it once the
            // transfer completes or is discarded.
            try
            {
                return await HandleResumableCore(s, ctx, ct, sessionId, totalSize, clientOffset, fileName, expectedFullHash).ConfigureAwait(false);
            }
            finally
            {
                // Save only — never Delete here: Stop() may have already saved and
                // cleared the dict, and blindly deleting would lose the checkpoint.
                ResumeState st;
                if (ctx.ResumeStates.TryGetValue(sessionId, out st))
                {
                    // Flush so the persisted ReceivedBytes always matches what is on disk
                    try { if (st.WriteStream != null) st.WriteStream.Flush(); } catch { }
                    // Guarded: Stop()'s Shutdown() may be saving the same file right
                    // now, and an IOException escaping this finally would replace
                    // the transfer's own exception on the way out.
                    try { ServerResumeStore.Save(st); } catch { }
                }
                try { sessionLock.Release(); } catch { }
            }
        }

        private static async Task<bool> HandleResumableCore(IWireStream s, ServerWireContext ctx, CancellationToken ct,
            Guid sessionId, long totalSize, long clientOffset, string fileName, byte[] expectedFullHash)
        {
            // Dedup shortcut: with skip-duplicates on and a full hash available, an
            // identical file already on disk answers "already complete" — nothing travels.
            if (ctx.SkipDuplicateFiles && expectedFullHash != null)
            {
                string basePath = Path.Combine(ctx.SaveDirectory, fileName);
                var existing = new FileInfo(basePath);
                if (existing.Exists && existing.Length == totalSize
                    && await VerifyFullHashFile(basePath, expectedFullHash, ct).ConfigureAwait(false))
                {
                    await SendResumeResponse(s, totalSize, 2, ct).ConfigureAwait(false);
                    ctx.Cb.RaiseLog(L.S_DuplicateSkipped(fileName));
                    ctx.Cb.RaiseComplete();
                    ctx.Cb.RaiseFileReceived(basePath, totalSize);
                    return true;
                }
            }

            // The preallocated file needs full size headroom
            if (!Utils.HasFreeSpaceFor(ctx.SaveDirectory, totalSize))
            {
                ctx.Cb.RaiseLog(L.S_DiskFull(fileName, Utils.FormatSize(totalSize)));
                ctx.Cb.RaiseError(L.S_DiskFull(fileName, Utils.FormatSize(totalSize)));
                return false;
            }

            string peer = ctx.Session != null ? ctx.Session.Peer : null;

            ResumeState state = null;
            ctx.ResumeStates.TryGetValue(sessionId, out state);
            byte status;
            long resumeFrom;
            bool isNew = (state == null);

            if (isNew)
            {
                // No in-memory state: try to recover from a previous server run.
                var disk = ServerResumeStore.Load(sessionId);
                if (disk != null && disk.TotalSize == totalSize
                    && !string.IsNullOrEmpty(disk.SavePath) && File.Exists(disk.SavePath)
                    && disk.ReceivedBytes >= 0 && disk.ReceivedBytes < totalSize)
                {
                    var fi = new FileInfo(disk.SavePath);
                    if (fi.Length == totalSize)
                    {
                        try
                        {
                            disk.WriteStream = new FileStream(disk.SavePath, FileMode.Open, FileAccess.Write,
                                FileShare.None, ctx.BufferSize, FileOptions.RandomAccess);
                            disk.WriteStream.Seek(disk.ReceivedBytes, SeekOrigin.Begin);
                            if (ctx.ResumeStates.TryAdd(sessionId, disk))
                            {
                                state = disk;
                                isNew = false;
                                ctx.Cb.RaiseLog(string.Format("Resume: restored server state for session {0} at offset {1}",
                                    sessionId.ToString("N"), disk.ReceivedBytes));
                            }
                            else
                            {
                                disk.WriteStream.Dispose();
                                // isNew stays true when the winner vanished (completed and
                                // cleaned up before we could read it) — fall through to a
                                // fresh session below
                                if (!ctx.ResumeStates.TryGetValue(sessionId, out state))
                                    state = null;
                                else
                                    isNew = false;
                            }
                        }
                        catch (IOException)
                        {
                            // File locked or otherwise unusable — discard disk state
                            try { disk.WriteStream.Dispose(); } catch { }
                            ServerResumeStore.Delete(sessionId);
                        }
                    }
                    else
                    {
                        // Stale disk state (file size changed) — restart from scratch
                        ServerResumeStore.Delete(sessionId);
                    }
                }
                else if (disk != null)
                {
                    // Size mismatch or already-complete record — discard
                    ServerResumeStore.Delete(sessionId);
                }
            }

            if (isNew)
            {
                var newState = new ResumeState
                {
                    SessionId = sessionId,
                    TotalSize = totalSize,
                    FileName = fileName,
                    Peer = peer,
                    ReceivedBytes = 0
                };
                newState.SavePath = Utils.GetUniqueSavePath(ctx.SaveDirectory, fileName);
                newState.WriteStream = new FileStream(newState.SavePath, FileMode.Create, FileAccess.Write,
                    FileShare.None, ctx.BufferSize, FileOptions.RandomAccess);
                newState.WriteStream.SetLength(totalSize);

                if (ctx.ResumeStates.TryAdd(sessionId, newState))
                {
                    state = newState;
                }
                else
                {
                    newState.WriteStream.Dispose();
                    try { File.Delete(newState.SavePath); } catch { }
                    ctx.ResumeStates.TryGetValue(sessionId, out state);
                    isNew = false;
                }
            }

            // The resolved session (live or restored) belongs to exactly one peer and
            // one file name. A peer that guesses/replays another sessionId must not be
            // able to write into or finalize someone else's file. An empty Peer is a
            // checkpoint written by an older build — adopt the current peer.
            if (!isNew && state != null)
            {
                if (string.IsNullOrEmpty(state.Peer)) state.Peer = peer;
                if (!string.Equals(state.Peer, peer, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(state.FileName, fileName, StringComparison.Ordinal))
                {
                    ctx.Cb.RaiseLog(string.Format(
                        "Resume: session {0} belongs to another peer/file — refusing",
                        sessionId.ToString("N")));
                    try { if (state.WriteStream != null) state.WriteStream.Dispose(); } catch { }
                    state.WriteStream = null;
                    ResumeState removedMismatch;
                    ctx.ResumeStates.TryRemove(sessionId, out removedMismatch);
                    return false;
                }
            }

            if (isNew)
            {
                status = 0;
                resumeFrom = 0;
                if (clientOffset > 0)
                {
                    ctx.Cb.RaiseLog(string.Format("Resume: no server state for session {0}, restarting from 0 (client claimed {1})",
                        sessionId.ToString("N"), clientOffset));
                }
            }
            else
            {
                if (state == null)
                {
                    // Lost the add race and the winner already finished and cleaned up;
                    // fail this attempt — the client's retry starts a clean session
                    ctx.Cb.RaiseLog(string.Format("Resume: session {0} state disappeared mid-race",
                        sessionId.ToString("N")));
                    return false;
                }
                if (state.TotalSize != totalSize)
                {
                    ctx.Cb.RaiseLog(string.Format("Resume size mismatch: session={0} expect={1} got={2}",
                        sessionId.ToString("N"), state.TotalSize, totalSize));
                    return false;
                }
                if (state.ReceivedBytes >= totalSize)
                {
                    // Payload fully on disk from an earlier connection that died before
                    // the final handshake — finalize like the success path so the file
                    // is not left locked by the abandoned write stream.
                    try { state.WriteStream.Dispose(); } catch { }
                    state.WriteStream = null;
                    ResumeState removedDone;
                    ctx.ResumeStates.TryRemove(sessionId, out removedDone);
                    ServerResumeStore.Delete(sessionId);
                    status = 2;
                    resumeFrom = totalSize;
                    await SendResumeResponse(s, resumeFrom, status, ct).ConfigureAwait(false);
                    ctx.Cb.RaiseComplete();
                    ctx.Cb.RaiseFileReceived(state.SavePath, totalSize);
                    return true;
                }
                status = 1;
                resumeFrom = state.ReceivedBytes;
            }

            // The server is authoritative: the transfer always continues from the bytes
            // it has actually received and persisted. A client claiming to be further
            // ahead must NOT be believed — accepting the larger offset would skip the
            // gap [resumeFrom, clientOffset), and no hash covers those bytes, so a
            // zero-filled (or stale) file would be reported as fully received.
            // Claiming to be behind is harmless: the overlapping prefix is re-sent.
            long actualStart = isNew ? 0 : resumeFrom;
            if (clientOffset > actualStart)
            {
                ctx.Cb.RaiseLog(string.Format(
                    "Resume: client claimed offset {0} but server has {1} for session {2} — re-sending the gap",
                    clientOffset, actualStart, sessionId.ToString("N")));
            }
            await SendResumeResponse(s, actualStart, status, ct).ConfigureAwait(false);

            // If client has less than server, it'll re-send from actualStart
            long remaining = totalSize - actualStart;
            ctx.Cb.RaiseLog(string.Format("Resume: {0} offset={1} remaining={2} status={3}",
                fileName, actualStart, Utils.FormatSize(remaining), status));

            // Receive remaining data + SHA256
            state.WriteStream.Seek(actualStart, SeekOrigin.Begin);
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                long bytesRead = 0;
                var buf = new byte[ctx.BufferSize];
                var progressTimer = System.Diagnostics.Stopwatch.StartNew();
                var elapsedSw = System.Diagnostics.Stopwatch.StartNew();

                while (bytesRead < remaining && !ct.IsCancellationRequested)
                {
                    int toRead = (int)Math.Min(remaining - bytesRead, (long)buf.Length);
                    int read = await s.ReadSomeAsync(buf, 0, toRead, ct).ConfigureAwait(false);
                    if (read <= 0)
                        throw new IOException(L.S_ConnClosedPrematurely);
                    if (ctx.ReceiveLimiter != null)
                        await ctx.ReceiveLimiter.ThrottleAsync(read, ct).ConfigureAwait(false);
                    sha256.TransformBlock(buf, 0, read, null, 0);
                    await state.WriteStream.WriteAsync(buf, 0, read, ct).ConfigureAwait(false);
                    bytesRead += read;
                    state.ReceivedBytes = actualStart + bytesRead;

                    if (progressTimer.ElapsedMilliseconds >= 100)
                    {
                        progressTimer.Restart();
                        ctx.Cb.RaiseProgress(new TransferProgress
                        {
                            BytesTransferred = state.ReceivedBytes,
                            TotalBytes = totalSize,
                            SpeedBytesPerSecond = state.ReceivedBytes / Math.Max(elapsedSw.Elapsed.TotalSeconds, 0.001),
                            Elapsed = elapsedSw.Elapsed,
                            FileName = fileName
                        });
                    }
                }

                sha256.TransformFinalBlock(buf, 0, 0);
                var computedHash = sha256.Hash;
                var receivedHash = new byte[32];
                await s.ReadExactAsync(receivedHash, 0, 32, ct).ConfigureAwait(false);

                if (Utils.ConstantTimeEquals(computedHash, receivedHash))
                {
                    // Close the write stream so the file can be re-read for full verification
                    state.WriteStream.Dispose();
                    state.WriteStream = null;

                    if (expectedFullHash == null || await VerifyFullHashFile(state.SavePath, expectedFullHash, ct).ConfigureAwait(false))
                    {
                        ResumeState removed;
                        ctx.ResumeStates.TryRemove(sessionId, out removed);
                        ServerResumeStore.Delete(sessionId);
                        ctx.Cb.RaiseLog(L.S_TransferDone(fileName, Utils.FormatSize(totalSize), 0.0, ""));
                        await SendResumeResponse(s, totalSize, 2, ct).ConfigureAwait(false);
                        ctx.Cb.RaiseComplete();
                        ctx.Cb.RaiseFileReceived(state.SavePath, totalSize);
                        return true;
                    }

                    // Full-file hash mismatch — the resumed file mixes old and new
                    // segments; discard it and tell the client to retry.
                    ResumeState removedFh;
                    ctx.ResumeStates.TryRemove(sessionId, out removedFh);
                    ServerResumeStore.Delete(sessionId);
                    try { File.Delete(state.SavePath); } catch { }
                    ctx.Cb.RaiseLog(L.S_FullHashFailed(fileName));
                    ctx.Cb.RaiseError(L.S_FullHashFailed(fileName));
                    await SendResumeResponse(s, totalSize, 3, ct).ConfigureAwait(false);
                    return false;
                }

                // Hash mismatch — discard server state so a retry starts a fresh file
                // instead of answering status=2 ("already complete") on a corrupt file.
                try { state.WriteStream.Dispose(); } catch { }
                state.WriteStream = null;
                ResumeState removed2;
                ctx.ResumeStates.TryRemove(sessionId, out removed2);
                ServerResumeStore.Delete(sessionId);
                ctx.Cb.RaiseLog(L.S_HashFailed(fileName));
                ctx.Cb.RaiseError(L.S_HashFailed(fileName));
                return false;
            }
        }

        private static async Task SendResumeResponse(IWireStream s, long offset, byte status, CancellationToken ct)
        {
            var resp = new byte[10]; // type(1) + offset(8) + status(1)
            resp[0] = 0x10;
            Buffer.BlockCopy(BitConverter.GetBytes(offset), 0, resp, 1, 8);
            resp[9] = status;
            await s.WriteExactAsync(resp, 0, 10, ct).ConfigureAwait(false);
        }

        private static async Task<bool> VerifyFullHashFile(string savePath, byte[] expected, CancellationToken ct)
        {
            try
            {
                using (var fs = new FileStream(savePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4194304, FileOptions.SequentialScan))
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    var buf = new byte[4194304];
                    int read;
                    while ((read = await fs.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                        sha.TransformBlock(buf, 0, read, null, 0);
                    sha.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                    return Utils.ConstantTimeEquals(expected, sha.Hash);
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>Receives file bytes into savePath and verifies the trailing SHA256. Returns true on match.
        /// basePath is the un-suffixed collision path used by the skip-duplicates check (equal to
        /// savePath when dedup does not apply). Rejects the file up front when disk space is short.</summary>
        private static async Task<bool> ReceiveFilePayload(IWireStream s, ServerWireContext ctx, string savePath, string basePath,
            long fileSize, string displayName, CancellationToken ct)
        {
            if (!Utils.HasFreeSpaceFor(Path.GetDirectoryName(savePath), fileSize))
            {
                ctx.Cb.RaiseLog(L.S_DiskFull(displayName, Utils.FormatSize(fileSize)));
                ctx.Cb.RaiseError(L.S_DiskFull(displayName, Utils.FormatSize(fileSize)));
                return false;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytesRead = 0;
            var bufA = new byte[ctx.BufferSize];
            var bufB = new byte[ctx.BufferSize];
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();

            // Receive into a sibling temp file and only move it to the final name once
            // the trailing SHA256 verifies. Previously a dropped/failed transfer left a
            // truncated file at the real name, which also pushed a later good transfer
            // to a "_1" name — the user saw a corrupt file plus a good file.
            string tempPath = savePath + ".part-" + Guid.NewGuid().ToString("N");
            bool committed = false;
            try
            {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            using (var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, ctx.BufferSize, FileOptions.SequentialScan))
            {
                long remaining = fileSize;
                byte[] computedHash;
                if (remaining == 0)
                {
                    // Empty file: only the 32-byte hash follows
                    var emptyHash = new byte[32];
                    await s.ReadExactAsync(emptyHash, 0, 32, ct).ConfigureAwait(false);
                    computedHash = sha256.ComputeHash(Utils.EmptyBytes);
                    if (!Utils.ConstantTimeEquals(emptyHash, computedHash))
                    {
                        ctx.Cb.RaiseLog(L.S_HashFailed(displayName));
                        ctx.Cb.RaiseError(L.S_HashFailed(displayName));
                        return false;
                    }
                }
                else
                {
                int toRead = (int)Math.Min(remaining, (long)bufA.Length);
                int read = await s.ReadSomeAsync(bufA, 0, toRead, ct).ConfigureAwait(false);
                if (read <= 0)
                    throw new IOException(L.S_ConnClosedPrematurely);

                // Throttle the FIRST read too: on loopback the whole payload can already
                // sit in the socket buffer, and a small file may complete inside it
                if (ctx.ReceiveLimiter != null)
                    await ctx.ReceiveLimiter.ThrottleAsync(read, ct).ConfigureAwait(false);

                remaining -= read;
                var cur = bufA;
                var nxt = bufB;

                while (remaining > 0 && !ct.IsCancellationRequested)
                {
                    int nextToRead = (int)Math.Min(remaining, (long)nxt.Length);
                    var nextReadTask = s.ReadSomeAsync(nxt, 0, nextToRead, ct);

                    sha256.TransformBlock(cur, 0, read, null, 0);
                    await fileStream.WriteAsync(cur, 0, read, ct);
                    bytesRead += read;

                    read = await nextReadTask.ConfigureAwait(false);
                    if (read <= 0)
                        throw new IOException(L.S_ConnClosedPrematurely);
                    remaining -= read;

                    // One paced bucket across all connections of this server: throttling
                    // the read paces the sender too via TCP/UDT flow control
                    if (ctx.ReceiveLimiter != null)
                        await ctx.ReceiveLimiter.ThrottleAsync(read, ct).ConfigureAwait(false);

                    var tmp = cur; cur = nxt; nxt = tmp;

                    if (progressTimer.ElapsedMilliseconds >= 100 || remaining == 0)
                    {
                        progressTimer.Restart();
                        ctx.Cb.RaiseProgress(new TransferProgress
                        {
                            BytesTransferred = bytesRead,
                            TotalBytes = fileSize,
                            SpeedBytesPerSecond = bytesRead / sw.Elapsed.TotalSeconds,
                            Elapsed = sw.Elapsed,
                            FileName = displayName
                        });
                    }
                }

                // Process final chunk
                sha256.TransformBlock(cur, 0, read, null, 0);
                await fileStream.WriteAsync(cur, 0, read, ct);
                bytesRead += read;

                // Final progress update
                ctx.Cb.RaiseProgress(new TransferProgress
                {
                    BytesTransferred = bytesRead,
                    TotalBytes = fileSize,
                    SpeedBytesPerSecond = bytesRead / sw.Elapsed.TotalSeconds,
                    Elapsed = sw.Elapsed,
                    FileName = displayName
                });

                sha256.TransformFinalBlock(new byte[0], 0, 0);
                await fileStream.FlushAsync(ct);

                var receivedHash = new byte[32];
                await s.ReadExactAsync(receivedHash, 0, 32, ct).ConfigureAwait(false);
                computedHash = sha256.Hash;

                if (!Utils.ConstantTimeEquals(receivedHash, computedHash))
                {
                    ctx.Cb.RaiseLog(L.S_HashFailed(displayName));
                    ctx.Cb.RaiseError(L.S_HashFailed(displayName));
                    return false;
                }
                } // end non-empty payload

                // Skip-duplicates: an identical file already on disk wins; the freshly
                // received copy is discarded. Only reached when the collision suffix
                // actually renamed this save (basePath != savePath).
                if (ctx.SkipDuplicateFiles && !string.Equals(savePath, basePath, StringComparison.OrdinalIgnoreCase))
                {
                    bool dup = false;
                    try
                    {
                        var fi = new FileInfo(basePath);
                        dup = fi.Exists && fi.Length == fileSize;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    if (dup)
                        dup = await VerifyFullHashFile(basePath, computedHash, ct).ConfigureAwait(false);
                    if (dup)
                    {
                        ctx.Cb.RaiseLog(L.S_DuplicateSkipped(displayName));
                        return true; // finally deletes the temp copy
                    }
                }

                // Hash verified — close the temp handle before moving it into place.
                fileStream.Dispose();
                File.Move(tempPath, savePath);
                committed = true;
            }
            return true;
            }
            finally
            {
                if (!committed)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }
        }
    }

    #endregion

    #region Client-side protocol

    /// <summary>Sending side of the 0x00-0x03 protocol, shared by TCP and UDT clients.
    /// Connection setup (and its transport-specific logging) stays in the transport classes.</summary>
    /// <summary>Result of the client-side 0x05/0x07/0x08 prelude negotiation.</summary>
    public class AuthResult
    {
        /// <summary>The stream to run the protocol on — the raw stream, or the
        /// compression/encryption decorators wrapping it after accepted preludes.</summary>
        public IWireStream Stream;
        /// <summary>True when the session is encrypted (0x07 accepted).</summary>
        public bool Encrypted;
        /// <summary>True when the session is compressed (0x08 accepted).</summary>
        public bool Compressed;
        /// <summary>True when the peer ignored the 0x07 frame (an older version) —
        /// the caller must reconnect and retry with the plain 0x05 frame instead.</summary>
        public bool NeedPlainFallback;
        /// <summary>True when the peer dropped the 0x08 compression offer (an older
        /// version) — the caller must reconnect without the offer.</summary>
        public bool NeedNoCompressionFallback;
    }

    public static class ClientWire
    {
        /// <summary>Sends one file (type 0x00).</summary>
        public static async Task SendSingleFileAsync(IWireStream s, string filePath, int bufferSize,
            SpeedLimiter limiter, WireCallbacks cb, CancellationToken ct)
        {
            var fileInfo = new FileInfo(filePath);
            long fileSize = fileInfo.Length;
            string fileName = fileInfo.Name;
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);

            var header = new byte[1 + 12 + nameBytes.Length];
            header[0] = 0x00; // single file
            Buffer.BlockCopy(BitConverter.GetBytes(fileSize), 0, header, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(nameBytes.Length), 0, header, 9, 4);
            Buffer.BlockCopy(nameBytes, 0, header, 13, nameBytes.Length);
            await s.WriteExactAsync(header, 0, header.Length, ct).ConfigureAwait(false);

            cb.RaiseLog(L.C_Sending(fileName, Utils.FormatSize(fileSize)));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await SendFilePayload(s, filePath, fileSize, fileName, bufferSize, limiter, cb, ct, 0).ConfigureAwait(false);
            sw.Stop();

            cb.RaiseLog(L.C_TransferDone(fileName, Utils.FormatSize(fileSize),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

            await ConfirmCompletionAsync(s, cb, ct).ConfigureAwait(false);
            cb.RaiseComplete();
        }

        /// <summary>
        /// TCP completion verdict for 0x00/0x01/0x06. A current server writes one status
        /// byte through the live stream (0x01 accepted, 0x00 refused); older servers
        /// close the connection instead. A refusal, or EOF before any byte, means the
        /// receiver did NOT accept the transfer — reporting success there used to lose
        /// files silently (e.g. disk full, receive confirmation denied, IP filter).
        /// User cancellation is not an error.
        /// </summary>
        internal static async Task ConfirmCompletionAsync(IWireStream s, WireCallbacks cb, CancellationToken ct)
        {
            // UDT signals success with its own out-of-band 1-byte ACK (read by
            // RunUdtTransfer), so there is nothing on the byte stream to wait for.
            if (cb != null && !cb.CompletionAckOnStream) return;

            var buf = new byte[1];
            int n;
            try
            {
                n = await s.ReadSomeAsync(buf, 0, 1, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (ct.IsCancellationRequested) return; // user cancelled — not a failure
                throw new IOException(L.C_NoCompletionAck);
            }
            if (n <= 0)
            {
                if (ct.IsCancellationRequested) return;
                throw new IOException(L.C_NoCompletionAck);
            }
            if (buf[0] != 0x01)
                throw new IOException(L.C_RejectedByPeer);
        }

        /// <summary>Sends a folder recursively (type 0x01), preserving relative paths.</summary>
        public static async Task SendFolderAsync(IWireStream s, string folderPath, int bufferSize,
            SpeedLimiter limiter, WireCallbacks cb, CancellationToken ct)
        {
            string folderName = Path.GetFileName(folderPath);
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = "folder";
            byte[] folderNameBytes = System.Text.Encoding.UTF8.GetBytes(folderName);

            var files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                cb.RaiseLog(L.C_ZeroFiles);
                cb.RaiseError(L.C_ZeroFiles);
                return;
            }

            // Pre-compute file entries to avoid duplicate FileInfo creation
            var fileEntries = new FileEntry[files.Length];
            long totalSize = 0;
            for (int i = 0; i < files.Length; i++)
            {
                var fi = new FileInfo(files[i]);
                long size = fi.Length;
                fileEntries[i] = new FileEntry
                {
                    Path = files[i],
                    Size = size,
                    RelativePath = files[i].Substring(folderPath.Length).TrimStart('\\', '/')
                };
                totalSize += size;
            }

            cb.RaiseLog(L.C_SendingFolder(folderName, files.Length, Utils.FormatSize(totalSize)));

            // Folder header: type(1) + folderNameLen(2) + folderName + fileCount(4)
            var header = new byte[1 + 2 + folderNameBytes.Length + 4];
            int pos = 0;
            header[pos++] = 0x01; // folder
            Buffer.BlockCopy(BitConverter.GetBytes((short)folderNameBytes.Length), 0, header, pos, 2); pos += 2;
            Buffer.BlockCopy(folderNameBytes, 0, header, pos, folderNameBytes.Length); pos += folderNameBytes.Length;
            Buffer.BlockCopy(BitConverter.GetBytes(files.Length), 0, header, pos, 4);
            await s.WriteExactAsync(header, 0, header.Length, ct).ConfigureAwait(false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long totalSent = 0;

            foreach (var entry in fileEntries)
            {
                if (ct.IsCancellationRequested) break;

                // File entry header: fileSize(8) + pathLen(2) + relativePath
                byte[] relPathBytes = System.Text.Encoding.UTF8.GetBytes(entry.RelativePath);
                var fileHeader = new byte[8 + 2 + relPathBytes.Length];
                Buffer.BlockCopy(BitConverter.GetBytes(entry.Size), 0, fileHeader, 0, 8);
                Buffer.BlockCopy(BitConverter.GetBytes((short)relPathBytes.Length), 0, fileHeader, 8, 2);
                Buffer.BlockCopy(relPathBytes, 0, fileHeader, 10, relPathBytes.Length);
                await s.WriteExactAsync(fileHeader, 0, fileHeader.Length, ct).ConfigureAwait(false);

                await SendFilePayload(s, entry.Path, entry.Size, entry.RelativePath, bufferSize, limiter, cb, ct, 0).ConfigureAwait(false);
                totalSent += entry.Size;

                cb.RaiseProgress(new TransferProgress
                {
                    BytesTransferred = totalSent,
                    TotalBytes = totalSize,
                    SpeedBytesPerSecond = totalSent / sw.Elapsed.TotalSeconds,
                    Elapsed = sw.Elapsed,
                    FileName = folderName
                });
            }

            sw.Stop();
            cb.RaiseLog(L.C_FolderTransferDone(folderName, files.Length, Utils.FormatSize(totalSize),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(totalSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

            await ConfirmCompletionAsync(s, cb, ct).ConfigureAwait(false);
            cb.RaiseComplete();
        }

        /// <summary>
        /// Sends a folder with resume support (type 0x04). Sends a manifest with per-file
        /// full-file SHA256, then only the bytes the server reports as missing. The client
        /// persists a FolderResumeState so an interrupted session can be retried later.
        /// With keepState=true (sync mode) the state survives completion — the session
        /// maps to a fixed server directory, so repeat runs transfer only differences —
        /// and the state is flagged IsSync to stay out of the resume dialog.
        /// </summary>
        public static async Task SendFolderResumableAsync(IWireStream s, string folderPath, Guid sessionId,
            string serverIp, int port, bool isUdt, int bufferSize, SpeedLimiter limiter, WireCallbacks cb,
            CancellationToken ct, bool keepState = false)
        {
            string folderName = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = "folder";

            var files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                cb.RaiseLog(L.C_ZeroFiles);
                cb.RaiseError(L.C_ZeroFiles);
                return;
            }

            // Pre-compute manifest entries; hashes let the server verify complete files on resume
            cb.RaiseLog(L.ComputingFolderHashes(files.Length));
            var relativePaths = new string[files.Length];
            var sizes = new long[files.Length];
            var hashes = new byte[files.Length][];
            long totalBytes = 0;
            for (int i = 0; i < files.Length; i++)
            {
                var fi = new FileInfo(files[i]);
                sizes[i] = fi.Length;
                relativePaths[i] = files[i].Substring(folderPath.Length).TrimStart('\\', '/');
                hashes[i] = ComputeFileHash(files[i]);
                totalBytes += sizes[i];
            }

            // Persist the session before sending so an interruption keeps it resumable
            var state = new FolderResumeState
            {
                SessionId = sessionId,
                FolderPath = folderPath,
                FolderName = folderName,
                ServerIp = serverIp,
                Port = port,
                IsUdt = isUdt,
                Created = DateTime.UtcNow,
                FileCount = files.Length,
                TotalBytes = totalBytes,
                SentBytes = 0,
                IsSync = keepState
            };
            state.Save();

            cb.RaiseLog(L.C_SendingFolder(folderName, files.Length, Utils.FormatSize(totalBytes)));

            // Header: type(1) + sessionId(16) + folderNameLen(2) + folderName + fileCount(4) + totalBytes(8)
            var folderNameBytes = System.Text.Encoding.UTF8.GetBytes(folderName);
            var header = new byte[1 + 16 + 2 + folderNameBytes.Length + 4 + 8];
            int pos = 0;
            header[pos++] = 0x04;
            Buffer.BlockCopy(sessionId.ToByteArray(), 0, header, pos, 16); pos += 16;
            Buffer.BlockCopy(BitConverter.GetBytes((short)folderNameBytes.Length), 0, header, pos, 2); pos += 2;
            Buffer.BlockCopy(folderNameBytes, 0, header, pos, folderNameBytes.Length); pos += folderNameBytes.Length;
            Buffer.BlockCopy(BitConverter.GetBytes(files.Length), 0, header, pos, 4); pos += 4;
            Buffer.BlockCopy(BitConverter.GetBytes(totalBytes), 0, header, pos, 8); pos += 8;
            await s.WriteExactAsync(header, 0, header.Length, ct).ConfigureAwait(false);

            // Manifest: per file [size(8)][pathLen(2)][path][fullHash(32)]
            for (int i = 0; i < files.Length; i++)
            {
                byte[] relPathBytes = System.Text.Encoding.UTF8.GetBytes(relativePaths[i]);
                var entry = new byte[8 + 2 + relPathBytes.Length + 32];
                Buffer.BlockCopy(BitConverter.GetBytes(sizes[i]), 0, entry, 0, 8);
                Buffer.BlockCopy(BitConverter.GetBytes((short)relPathBytes.Length), 0, entry, 8, 2);
                Buffer.BlockCopy(relPathBytes, 0, entry, 10, relPathBytes.Length);
                Buffer.BlockCopy(hashes[i], 0, entry, 10 + relPathBytes.Length, 32);
                await s.WriteExactAsync(entry, 0, entry.Length, ct).ConfigureAwait(false);
            }

            // 0x11 response: resumeFileIndex(8) + resumeOffset(8) + status(1) = 18 bytes
            var resp = new byte[1 + 8 + 8 + 1];
            await s.ReadExactAsync(resp, 0, resp.Length, ct).ConfigureAwait(false);
            if (resp[0] != 0x11)
                throw new InvalidDataException(string.Format("Unexpected folder resume response type: {0}", resp[0]));
            int resumeIndex = (int)BitConverter.ToInt64(resp, 1);
            long resumeOffset = BitConverter.ToInt64(resp, 9);
            byte status = resp[17];

            if (status == 2)
            {
                cb.RaiseLog(L.C_AlreadyReceived(folderName));
                if (!keepState) FolderResumeState.Delete(sessionId);
                cb.RaiseComplete();
                return;
            }

            cb.RaiseLog(L.FolderResumeStart(resumeIndex + 1, files.Length, Utils.FormatSize(resumeOffset)));

            state.SentBytes = resumeOffset;
            for (int i = 0; i < resumeIndex; i++) state.SentBytes += sizes[i];
            state.Save();

            // Send body: per file [increment][32-byte SHA256 of the increment]
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long totalSent = state.SentBytes;
            for (int i = resumeIndex; i < files.Length && !ct.IsCancellationRequested; i++)
            {
                long start = (i == resumeIndex) ? resumeOffset : 0;
                await SendFilePayload(s, files[i], sizes[i] - start, relativePaths[i],
                    bufferSize, limiter, cb, ct, start).ConfigureAwait(false);
                totalSent += sizes[i] - start;

                state.SentBytes = totalSent;
                state.Save();

                cb.RaiseProgress(new TransferProgress
                {
                    BytesTransferred = totalSent,
                    TotalBytes = totalBytes,
                    SpeedBytesPerSecond = totalSent / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                    Elapsed = sw.Elapsed,
                    FileName = folderName
                });
            }

            sw.Stop();
            cb.RaiseLog(L.C_FolderTransferDone(folderName, files.Length, Utils.FormatSize(totalBytes),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(totalBytes / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

            // Sync mode keeps the state: the next run of the same (folder, target) pair
            // reuses the session and the server scan skips everything already received
            if (!keepState) FolderResumeState.Delete(sessionId);
            cb.RaiseComplete();
        }

        /// <summary>Computes the SHA256 of a file's full content.</summary>
        private static byte[] ComputeFileHash(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4194304, FileOptions.SequentialScan))
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return sha.ComputeHash(fs);
        }

        /// <summary>
        /// Client side of the connection prelude, shared by TCP and UDT. Order:
        /// with a pairing code the 0x05 plain auth or the 0x07 encrypted auth first,
        /// THEN the 0x08 compression offer — wrapping the (possibly encrypted)
        /// stream last puts compression on the OUTSIDE, so the wire order is
        /// encrypt(compress(plain)) and deflation sees plaintext, not ciphertext.
        /// A peer that drops the 0x08 offer or never answers 0x17 is an older
        /// version — the NeedNoCompression / NeedPlain fallback flags tell the
        /// caller to reconnect without the feature. An explicit wrong code throws.
        /// </summary>
        public static async Task<AuthResult> AuthenticateAsync(IWireStream s, string pairingCode, bool tryEncryption,
            bool tryCompression, WireCallbacks cb, CancellationToken ct)
        {
            return await AuthenticateAsync(s, pairingCode, tryEncryption, tryCompression, cb, ct, true).ConfigureAwait(false);
        }

        /// <param name="allowDowngrade">False forbids negotiating a weaker handshake:
        /// if the peer does not answer the authenticated ECDH 0x09 frame, the transfer
        /// is refused instead of silently continuing in plaintext (or with the legacy
        /// 0x07 encryption whose key is derivable from the wire).</param>
        public static async Task<AuthResult> AuthenticateAsync(IWireStream s, string pairingCode, bool tryEncryption,
            bool tryCompression, WireCallbacks cb, CancellationToken ct, bool allowDowngrade)
        {
            IWireStream current = s;
            bool encrypted = false;

            if (!string.IsNullOrEmpty(pairingCode))
            {
                if (!tryEncryption)
                {
                    await SendAuthFrameAsync(current, pairingCode, cb, ct).ConfigureAwait(false);
                }
                else
                {
                    cb.RaiseLog(L.C_Authing);
                    // Preferred: authenticated ECDH (0x09).
                    var ecdh = await TryEcdhHandshakeAsync(current, pairingCode, cb, ct).ConfigureAwait(false);
                    if (ecdh.Handled)
                    {
                        if (ecdh.Rejected) throw new IOException(L.C_AuthFailed);
                        if (ecdh.Encrypted) { current = ecdh.Stream; encrypted = true; }
                        // else: peer has no pairing code — continue plaintext on this connection
                    }
                    else
                    {
                        // The peer did not answer 0x09: either an older build, or an
                        // on-path attacker forcing a weaker path. Fail closed unless
                        // the caller explicitly allowed a downgrade; otherwise the
                        // caller reconnects with encryption disabled (plain 0x05),
                        // never with the legacy 0x07 whose key rides the wire.
                        if (!allowDowngrade)
                            throw new IOException(L.C_EncryptPeerTooOld);
                        return new AuthResult { Stream = current, NeedPlainFallback = true };
                    }
                }
            }

            bool compressed = false;
            if (tryCompression)
            {
                // The 16 random pad bytes make an older peer parse this frame as a
                // corrupt transfer header and drop the connection — that drop IS the
                // fallback signal (same trick the 0x07 prelude relies on).
                var pad = new byte[16];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                    rng.GetBytes(pad);
                var frame8 = new byte[17];
                frame8[0] = 0x08;
                Buffer.BlockCopy(pad, 0, frame8, 1, 16);
                await current.WriteExactAsync(frame8, 0, frame8.Length, ct).ConfigureAwait(false);

                var resp8 = new byte[2];
                try
                {
                    await current.ReadExactAsync(resp8, 0, 2, ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    return new AuthResult { Stream = current, Encrypted = encrypted, NeedNoCompressionFallback = true };
                }
                if (resp8[0] != 0x18)
                    return new AuthResult { Stream = current, Encrypted = encrypted, NeedNoCompressionFallback = true };
                if (resp8[1] == 0)
                {
                    // Wrapping LAST keeps compression outermost: deflate sees
                    // plaintext and the inner decorator (if any) encrypts the result
                    current = new CompressedWireStream(current);
                    compressed = true;
                    cb.RaiseLog(L.C_CompressedOn);
                }
                // Any other status: the peer declined — continue uncompressed
            }

            return new AuthResult { Stream = current, Encrypted = encrypted, Compressed = compressed };
        }

        /// <summary>
        /// Attempts the authenticated ECDH handshake (0x09). Handled=false means the
        /// peer did not answer with a 0x19 frame (older build, or an attacker cutting
        /// the handshake) — the caller decides whether to downgrade or refuse. On the
        /// wire: [0x09][codeHash 32][clientPub 72], then the server's 0x19 reply, then
        /// the client's confirmation. The session keys come from the DH secret, never
        /// from anything an eavesdropper can see.
        /// </summary>
        private static async Task<EcdhHandshakeResult> TryEcdhHandshakeAsync(IWireStream s, string pairingCode,
            WireCallbacks cb, CancellationToken ct)
        {
            using (var clientKey = SessionCrypto.CreateEcdhKey())
            {
                byte[] clientPub = SessionCrypto.EcdhPublicBlob(clientKey);
                byte[] codeHash = WireAuth.HashCode(pairingCode);

                var frame = new byte[1 + 32 + SessionCrypto.EcdhPublicBytes];
                frame[0] = 0x09;
                Buffer.BlockCopy(codeHash, 0, frame, 1, 32);
                Buffer.BlockCopy(clientPub, 0, frame, 33, clientPub.Length);
                await s.WriteExactAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);

                var resp = new byte[2 + SessionCrypto.EcdhPublicBytes + 32];
                try
                {
                    await s.ReadExactAsync(resp, 0, resp.Length, ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    return new EcdhHandshakeResult { Handled = false };
                }
                if (resp[0] != 0x19)
                    return new EcdhHandshakeResult { Handled = false };

                byte status = resp[1];
                var serverPub = new byte[SessionCrypto.EcdhPublicBytes];
                Buffer.BlockCopy(resp, 2, serverPub, 0, serverPub.Length);
                var confirmS = new byte[32];
                Buffer.BlockCopy(resp, 2 + serverPub.Length, confirmS, 0, 32);

                if (status == 1)
                    return new EcdhHandshakeResult { Handled = true, Rejected = true };
                if (status == 2)
                {
                    // Server has pairing disabled and will not encrypt. Continuing
                    // plaintext on this connection is the documented lenient path.
                    cb.RaiseLog(L.C_EncryptUnsupported);
                    return new EcdhHandshakeResult { Handled = true };
                }
                if (status != 0)
                    return new EcdhHandshakeResult { Handled = false };

                byte[] shared;
                try { shared = SessionCrypto.EcdhDeriveShared(clientKey, serverPub); }
                catch (Exception) { return new EcdhHandshakeResult { Handled = true, Rejected = true }; }

                byte[] authKey = SessionCrypto.DeriveAuthKey(pairingCode, clientPub, serverPub, shared);
                byte[] expectedS = SessionCrypto.Confirm(authKey, "s2c-confirm", clientPub, serverPub);
                if (!Utils.ConstantTimeEquals(confirmS, expectedS))
                {
                    // The 0x19 reply was not produced by someone holding the code and
                    // the matching private key — refuse (possible MITM).
                    return new EcdhHandshakeResult { Handled = true, Rejected = true };
                }

                byte[] confirmC = SessionCrypto.Confirm(authKey, "c2s-confirm", clientPub, serverPub);
                await s.WriteExactAsync(confirmC, 0, confirmC.Length, ct).ConfigureAwait(false);

                byte[] c2sEnc, c2sMac, s2cEnc, s2cMac;
                SessionCrypto.DeriveSessionKeys(authKey, new byte[SessionCrypto.SaltBytes],
                    out c2sEnc, out c2sMac, out s2cEnc, out s2cMac);
                cb.RaiseLog(L.C_EncryptedOn);
                return new EcdhHandshakeResult
                {
                    Handled = true,
                    Encrypted = true,
                    // bindSequence:true — matches the server's 0x09 segment format
                    Stream = new EncryptedWireStream(s, c2sEnc, c2sMac, s2cEnc, s2cMac, true, true)
                };
            }
        }

        private sealed class EcdhHandshakeResult
        {
            /// <summary>The peer answered the 0x09 frame (so no fallback is needed).</summary>
            public bool Handled;
            /// <summary>The peer answered but the handshake was refused/tampered.</summary>
            public bool Rejected;
            /// <summary>A session-encrypting decorator was installed.</summary>
            public bool Encrypted;
            public IWireStream Stream;
        }

        /// <summary>
        /// Sends the 0x05 pairing frame and waits for the 0x15 verdict. No-op when
        /// pairingCode is empty (works against servers with pairing disabled too —
        /// they accept any 0x05). Throws IOException when the code is rejected.
        /// </summary>
        public static async Task SendAuthFrameAsync(IWireStream s, string pairingCode, WireCallbacks cb, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(pairingCode)) return;
            cb.RaiseLog(L.C_Authing);
            var frame = new byte[1 + 32];
            frame[0] = 0x05;
            Buffer.BlockCopy(WireAuth.HashCode(pairingCode), 0, frame, 1, 32);
            await s.WriteExactAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);

            var resp = new byte[2];
            await s.ReadExactAsync(resp, 0, 2, ct).ConfigureAwait(false);
            if (resp[0] != 0x15)
                throw new InvalidDataException(string.Format("Unexpected auth response type: {0}", resp[0]));
            if (resp[1] != 0)
                throw new IOException(L.C_AuthFailed);
        }

        /// <summary>
        /// Sends a UTF-8 text message (type 0x06): [0x06][4-byte byte length][UTF-8 bytes].
        /// Delivery is confirmed the same way as files — clean close on TCP, the
        /// 1-byte application ACK on UDT — so no separate response frame exists.
        /// </summary>
        public static async Task SendTextAsync(IWireStream s, string text, WireCallbacks cb, CancellationToken ct)
        {
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(text);
            if (payload.Length > ServerWire.MaxTextBytes)
                throw new IOException(L.SendTextTooLarge);

            var header = new byte[1 + 4];
            header[0] = 0x06;
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, header, 1, 4);
            await s.WriteExactAsync(header, 0, header.Length, ct).ConfigureAwait(false);
            if (payload.Length > 0)
                await s.WriteExactAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);

            cb.RaiseLog(L.C_SendingText(ServerWire.Preview(text), Utils.FormatSize(payload.Length)));
            await ConfirmCompletionAsync(s, cb, ct).ConfigureAwait(false);
            cb.RaiseComplete();
        }

        /// <summary>Sends one chunk of a larger file (type 0x02) for concurrent transfers.</summary>
        public static async Task SendChunkAsync(IWireStream s, string filePath, long offset, long chunkSize,
            long totalSize, int bufferSize, SpeedLimiter limiter, WireCallbacks cb, CancellationToken ct)
        {
            var fileInfo = new FileInfo(filePath);
            string fileName = fileInfo.Name;
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);

            // Header: type(1) + totalSize(8) + chunkOffset(8) + chunkSize(8) + nameLen(4) + name
            var header = new byte[1 + 28 + nameBytes.Length];
            header[0] = 0x02;
            Buffer.BlockCopy(BitConverter.GetBytes(totalSize), 0, header, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(offset), 0, header, 9, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(chunkSize), 0, header, 17, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(nameBytes.Length), 0, header, 25, 4);
            Buffer.BlockCopy(nameBytes, 0, header, 29, nameBytes.Length);
            await s.WriteExactAsync(header, 0, header.Length, ct).ConfigureAwait(false);

            cb.RaiseLog(string.Format("Chunk sending: {0} offset={1} size={2}",
                fileName, offset, Utils.FormatSize(chunkSize)));

            await SendFilePayload(s, filePath, chunkSize, fileName, bufferSize, limiter, cb, ct, offset).ConfigureAwait(false);

            cb.RaiseComplete();
        }

        /// <summary>Sends a file with resume support (type 0x03), negotiating the offset with the server.</summary>
        public static async Task SendResumableAsync(IWireStream s, string filePath, Guid sessionId, bool verifyHash,
            string serverIp, int port, bool isUdt, int bufferSize, SpeedLimiter limiter, WireCallbacks cb, CancellationToken ct)
        {
            ResumeState.EnsureDir();
            var fileInfo = new FileInfo(filePath);
            long fileSize = fileInfo.Length;
            string fileName = fileInfo.Name;
            long sentBytes = 0;
            long sourceMTime;
            try { sourceMTime = File.GetLastWriteTimeUtc(filePath).Ticks; }
            catch { sourceMTime = 0; }

            // Full-file hash for verification (computed up front; only when requested)
            byte[] fullHash = null;
            if (verifyHash)
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4194304, FileOptions.SequentialScan))
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    fullHash = sha.ComputeHash(fs);
                cb.RaiseLog(L.C_ComputingFullHash(fileName));
            }

            // Load existing state if resuming
            var existingState = ResumeState.Load(sessionId);
            if (existingState != null && existingState.SourceMTime != 0 && existingState.SourceMTime != sourceMTime)
            {
                // Source file changed since the interrupted transfer — restart from scratch
                cb.RaiseLog(L.C_ResumeSourceChanged(fileName));
                ResumeState.Delete(sessionId);
                existingState = null;
            }
            if (existingState != null)
            {
                sentBytes = existingState.SentBytes;
                cb.RaiseLog(L.C_Resuming(fileName, sentBytes, Utils.FormatSize(sentBytes)));
            }
            else
            {
                var newState = new ResumeState
                {
                    SessionId = sessionId,
                    TotalSize = fileSize,
                    FileName = fileName,
                    FilePath = filePath,
                    ServerIp = serverIp,
                    Port = port,
                    IsUdt = isUdt,
                    Created = DateTime.UtcNow,
                    SentBytes = 0,
                    SourceMTime = sourceMTime
                };
                newState.Save();
            }

            // Build 0x03 header: type(1) + sessionId(16) + totalSize(8) + resumeOffset(8) + nameLen(4) + name
            // + [verifyFlag(1)] + [fullHash(32)] when verifyHash is enabled. The flag byte is
            // always present so the server can parse the stream unambiguously.
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);
            int extra = 1 + (verifyHash ? 32 : 0);
            var header = new byte[1 + 16 + 8 + 8 + 4 + nameBytes.Length + extra];
            int p = 0;
            header[p++] = 0x03;
            Buffer.BlockCopy(sessionId.ToByteArray(), 0, header, p, 16); p += 16;
            Buffer.BlockCopy(BitConverter.GetBytes(fileSize), 0, header, p, 8); p += 8;
            Buffer.BlockCopy(BitConverter.GetBytes(sentBytes), 0, header, p, 8); p += 8;
            Buffer.BlockCopy(BitConverter.GetBytes(nameBytes.Length), 0, header, p, 4); p += 4;
            Buffer.BlockCopy(nameBytes, 0, header, p, nameBytes.Length); p += nameBytes.Length;
            header[p++] = verifyHash ? (byte)1 : (byte)0;
            if (verifyHash)
                Buffer.BlockCopy(fullHash, 0, header, p, 32);
            await s.WriteExactAsync(header, 0, header.Length, ct).ConfigureAwait(false);

            // Read 0x10 server response (10 bytes: 0x10 + offset8 + status1)
            var respBuf = new byte[10];
            await s.ReadExactAsync(respBuf, 0, 10, ct).ConfigureAwait(false);

            if (respBuf[0] != 0x10)
                throw new InvalidDataException(string.Format("Unexpected resume response type: {0}", respBuf[0]));

            byte respStatus = respBuf[9];
            long serverOffset = BitConverter.ToInt64(respBuf, 1);

            if (respStatus == 3)
            {
                // Full-file verification failed on the server — file was discarded.
                // Keep the local resume state so the transfer can be retried.
                cb.RaiseLog(L.C_VerifyFailed(fileName));
                cb.RaiseError(L.C_VerifyFailed(fileName));
                return;
            }

            if (respStatus == 2)
            {
                cb.RaiseLog(L.C_AlreadyReceived(fileName));
                ResumeState.Delete(sessionId);
                cb.RaiseComplete();
                return;
            }

            // The server is authoritative — resume from exactly the offset it reports.
            // Never take the max with the local checkpoint: if we (wrongly) believed we
            // had sent more than the server received, that would skip bytes the server
            // never got, leaving an unverified hole that no hash covers.
            long actualStart = serverOffset;
            cb.RaiseLog(L.C_ResumeNegotiated(actualStart, serverOffset, sentBytes));

            // Send file data from actualStart (hash covers only the re-sent increment)
            await SendFilePayload(s, filePath, fileSize - actualStart, fileName, bufferSize, limiter, cb, ct, actualStart).ConfigureAwait(false);

            // Read the final 0x10 response: status 2 = success, 3 = full-file
            // verification failed (server discarded the file, keep local state).
            var finalResp = new byte[10];
            await s.ReadExactAsync(finalResp, 0, 10, ct).ConfigureAwait(false);
            if (finalResp[0] != 0x10)
                throw new InvalidDataException(string.Format("Unexpected resume response type: {0}", finalResp[0]));
            if (finalResp[9] == 3)
            {
                cb.RaiseLog(L.C_VerifyFailed(fileName));
                cb.RaiseError(L.C_VerifyFailed(fileName));
                return;
            }

            // Success — delete resume state
            ResumeState.Delete(sessionId);
            cb.RaiseComplete();
        }

        /// <summary>Streams file bytes from fileOffset (sendSize bytes) followed by the SHA256 of what was sent.</summary>
        public static async Task SendFilePayload(IWireStream s, string filePath, long sendSize,
            string displayName, int bufferSize, SpeedLimiter limiter, WireCallbacks cb, CancellationToken ct, long fileOffset)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytesSent = 0;
            var bufA = new byte[bufferSize];
            var bufB = new byte[bufferSize];
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();

            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize, FileOptions.SequentialScan))
            {
                if (fileOffset > 0)
                    fileStream.Seek(fileOffset, SeekOrigin.Begin);
                long totalToSend = sendSize;
                int firstToRead = (int)Math.Min((long)bufA.Length, totalToSend);
                int read = await fileStream.ReadAsync(bufA, 0, firstToRead, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                    await s.WriteExactAsync(sha256.Hash, 0, 32, ct).ConfigureAwait(false);
                    return;
                }

                var cur = bufA;
                var nxt = bufB;

                while (read > 0 && !ct.IsCancellationRequested)
                {
                    long soFar = bytesSent + read;
                    int nextToRead = (int)Math.Min((long)nxt.Length, totalToSend - soFar);
                    Task<int> nextReadTask = null;
                    if (nextToRead > 0)
                        nextReadTask = fileStream.ReadAsync(nxt, 0, nextToRead, ct);

                    sha256.TransformBlock(cur, 0, read, null, 0);
                    await s.WriteExactAsync(cur, 0, read, ct).ConfigureAwait(false);
                    bytesSent += read;
                    await limiter.ThrottleAsync(read, ct).ConfigureAwait(false);

                    if (nextReadTask == null)
                    {
                        read = 0;
                        break;
                    }
                    read = await nextReadTask.ConfigureAwait(false);

                    var tmp = cur; cur = nxt; nxt = tmp;

                    if (progressTimer.ElapsedMilliseconds >= 100 || read == 0)
                    {
                        progressTimer.Restart();
                        cb.RaiseProgress(new TransferProgress
                        {
                            BytesTransferred = bytesSent,
                            TotalBytes = sendSize,
                            SpeedBytesPerSecond = bytesSent / sw.Elapsed.TotalSeconds,
                            Elapsed = sw.Elapsed,
                            FileName = displayName
                        });
                    }
                }
                sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                await s.WriteExactAsync(sha256.Hash, 0, 32, ct).ConfigureAwait(false);
            }
        }
    }

    #endregion
}
