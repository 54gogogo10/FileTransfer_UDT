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
    }

    /// <summary>State a server keeps across client connections: save location plus chunk/resume tracking.</summary>
    public class ServerWireContext
    {
        public string SaveDirectory;
        public int BufferSize = 4194304;
        /// <summary>When non-empty, clients must present this pairing code (0x05 frame)
        /// before any transfer type is accepted. Empty = open to the LAN.</summary>
        public string PairingCode;
        public readonly WireCallbacks Cb = new WireCallbacks();
        public readonly ConcurrentDictionary<string, ChunkTracker> ChunkTrackers
            = new ConcurrentDictionary<string, ChunkTracker>();
        public readonly ConcurrentDictionary<Guid, ResumeState> ResumeStates
            = new ConcurrentDictionary<Guid, ResumeState>();

        /// <summary>Persists incomplete resume sessions and disposes trackers (called from server Stop).</summary>
        public void Shutdown()
        {
            foreach (var kv in ChunkTrackers)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            ChunkTrackers.Clear();

            // Persist incomplete resume states before releasing file handles
            foreach (var kv in ResumeStates)
            {
                try
                {
                    if (kv.Value.WriteStream != null) kv.Value.WriteStream.Flush();
                    ServerResumeStore.Save(kv.Value);
                    if (kv.Value.WriteStream != null) { kv.Value.WriteStream.Dispose(); kv.Value.WriteStream = null; }
                }
                catch { }
            }
            ResumeStates.Clear();
        }
    }

    #endregion

    #region Server-side protocol

    /// <summary>Receiving side of the 0x00-0x03 protocol, shared by TCP and UDT servers.</summary>
    public static class ServerWire
    {
        /// <summary>Maximum payload size for a 0x06 text message (1 MB of UTF-8 bytes).</summary>
        public const int MaxTextBytes = 1048576;

        /// <summary>Single-line preview of a text message for logs and balloon tips.</summary>
        public static string Preview(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string line = text.Replace("\r", "").Replace("\n", " / ");
            if (line.Length > 160) line = line.Substring(0, 160) + "…";
            return line;
        }

        /// <summary>Reads the transfer type and dispatches. Throws on connection errors.</summary>
        public static async Task<WireOutcome> HandleClientAsync(IWireStream s, ServerWireContext ctx, CancellationToken ct)
        {
            var typeBuf = new byte[1];
            await s.ReadExactAsync(typeBuf, 0, 1, ct).ConfigureAwait(false);
            byte transferType = typeBuf[0];

            // Optional 0x05 pairing frame before any transfer type
            if (transferType == 0x05)
            {
                if (!await HandleAuthAsync(s, ctx, ct).ConfigureAwait(false))
                    return new WireOutcome { Success = false };
                await s.ReadExactAsync(typeBuf, 0, 1, ct).ConfigureAwait(false);
                transferType = typeBuf[0];
            }
            else if (!string.IsNullOrEmpty(ctx.PairingCode))
            {
                // Server requires pairing — reject clients that skip authentication
                await SendAuthResponse(s, 1, ct).ConfigureAwait(false);
                ctx.Cb.RaiseLog(L.S_AuthRequired);
                ctx.Cb.RaiseError(L.S_AuthRequired);
                return new WireOutcome { Success = false };
            }

            if (transferType == 0x01)
                return new WireOutcome { Success = await HandleFolderTransfer(s, ctx, ct).ConfigureAwait(false) };
            if (transferType == 0x02)
                return new WireOutcome { IsChunked = true, Success = await HandleChunkedFile(s, ctx, ct).ConfigureAwait(false) };
            if (transferType == 0x03)
                return new WireOutcome { Success = await HandleResumableFile(s, ctx, ct).ConfigureAwait(false) };
            if (transferType == 0x04)
                return new WireOutcome { Success = await HandleFolderResumableAsync(s, ctx, ct).ConfigureAwait(false) };
            if (transferType == 0x06)
                return new WireOutcome { Success = await HandleTextMessage(s, ctx, ct).ConfigureAwait(false) };
            return new WireOutcome { Success = await HandleFileTransfer(s, ctx, ct).ConfigureAwait(false) };
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
                ctx.Cb.RaiseLog(L.S_AuthFailed);
                ctx.Cb.RaiseError(L.S_AuthFailed);
                return false;
            }
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

        /// <summary>Normalizes a received name to a bare file name with a fallback.</summary>
        private static string SafeName(string rawName, string fallback)
        {
            string name = Path.GetFileName(rawName);
            if (string.IsNullOrWhiteSpace(name))
                name = fallback;
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
            if (fileCount <= 0 || fileCount > 1000000 || totalBytes < 0) return false;

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
                if (size < 0 || pathLen <= 0 || pathLen > 4096) return false;

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

            if (fileSize < 0 || nameLen <= 0 || nameLen > 4096)
            {
                ctx.Cb.RaiseLog(L.S_InvalidHeader(fileSize, nameLen));
                return false;
            }

            var nameBuf = new byte[nameLen];
            await s.ReadExactAsync(nameBuf, 0, nameLen, ct).ConfigureAwait(false);
            string fileName = SafeName(System.Text.Encoding.UTF8.GetString(nameBuf), L.S_ReceivedFile);

            string savePath = Utils.GetUniqueSavePath(ctx.SaveDirectory, fileName);

            ctx.Cb.RaiseLog(L.S_Receiving(fileName, Utils.FormatSize(fileSize)));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool hashOk = await ReceiveFilePayload(s, ctx, savePath, fileSize, fileName, ct).ConfigureAwait(false);
            sw.Stop();

            if (hashOk)
            {
                ctx.Cb.RaiseLog(L.S_TransferDone(fileName, Utils.FormatSize(fileSize),
                    sw.Elapsed.TotalSeconds,
                    Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));
                ctx.Cb.RaiseComplete();
                ctx.Cb.RaiseFileReceived(savePath, fileSize);
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
            if (fileCount <= 0) return false;

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
                if (fileSize < 0 || pathLen <= 0 || pathLen > 4096) return false;

                var pathBuf = new byte[pathLen];
                await s.ReadExactAsync(pathBuf, 0, pathLen, ct).ConfigureAwait(false);
                string relativePath = Utils.SanitizeRelativePath(System.Text.Encoding.UTF8.GetString(pathBuf));

                string savePath = Path.Combine(folderSaveDir, relativePath);
                string dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                bool hashOk = await ReceiveFilePayload(s, ctx, savePath, fileSize, relativePath, ct).ConfigureAwait(false);
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

            if (totalSize <= 0 || chunkOffset < 0 || chunkSize <= 0 || chunkOffset > totalSize - chunkSize
                || nameLen <= 0 || nameLen > 4096)
            {
                ctx.Cb.RaiseLog(L.S_InvalidHeader(totalSize, nameLen));
                return false;
            }

            var nameBuf = new byte[nameLen];
            await s.ReadExactAsync(nameBuf, 0, nameLen, ct).ConfigureAwait(false);
            string fileName = SafeName(System.Text.Encoding.UTF8.GetString(nameBuf), L.S_ReceivedFile);

            ChunkTracker tracker = ChunkTracker.GetOrCreate(
                ctx.ChunkTrackers, fileName, totalSize, ctx.SaveDirectory);

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
                    ctx.ChunkTrackers.TryRemove(fileName, out removed);
                    ctx.Cb.RaiseLog(L.S_TransferDone(fileName, Utils.FormatSize(totalSize), 0.0, ""));
                    ctx.Cb.RaiseComplete();
                    ctx.Cb.RaiseFileReceived(tracker.SavePath, totalSize);
                }
            }
            else
            {
                // Clean up tracker on hash failure
                ChunkTracker removed;
                if (ctx.ChunkTrackers.TryRemove(fileName, out removed))
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

            if (totalSize <= 0 || clientOffset < 0 || clientOffset > totalSize || nameLen <= 0 || nameLen > 4096)
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
                    ServerResumeStore.Save(st);
                }
            }
        }

        private static async Task<bool> HandleResumableCore(IWireStream s, ServerWireContext ctx, CancellationToken ct,
            Guid sessionId, long totalSize, long clientOffset, string fileName, byte[] expectedFullHash)
        {
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
                    status = 2;
                    resumeFrom = totalSize;
                    await SendResumeResponse(s, resumeFrom, status, ct).ConfigureAwait(false);
                    return true;
                }
                status = 1;
                resumeFrom = state.ReceivedBytes;
            }

            // Negotiate: use max of client's claim and server's actual received.
            // When the server has no state for this session (e.g. server restarted),
            // the client's offset claim is NOT trusted — restart from 0 so a fresh
            // pre-allocated file is never zero-padded behind a stale client offset.
            long actualStart = isNew ? 0 : Math.Max(clientOffset, resumeFrom);
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

        /// <summary>Receives file bytes into savePath and verifies the trailing SHA256. Returns true on match.</summary>
        private static async Task<bool> ReceiveFilePayload(IWireStream s, ServerWireContext ctx, string savePath, long fileSize,
            string displayName, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytesRead = 0;
            var bufA = new byte[ctx.BufferSize];
            var bufB = new byte[ctx.BufferSize];
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();

            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write,
                FileShare.None, ctx.BufferSize, FileOptions.SequentialScan))
            {
                long remaining = fileSize;
                if (remaining == 0)
                {
                    // Empty file: only the 32-byte hash follows
                    var emptyHash = new byte[32];
                    await s.ReadExactAsync(emptyHash, 0, 32, ct).ConfigureAwait(false);
                    return Utils.ConstantTimeEquals(emptyHash, sha256.ComputeHash(Utils.EmptyBytes));
                }
                int toRead = (int)Math.Min(remaining, (long)bufA.Length);
                int read = await s.ReadSomeAsync(bufA, 0, toRead, ct).ConfigureAwait(false);
                if (read <= 0)
                    throw new IOException(L.S_ConnClosedPrematurely);

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
                var computedHash = sha256.Hash;

                if (!Utils.ConstantTimeEquals(receivedHash, computedHash))
                {
                    ctx.Cb.RaiseLog(L.S_HashFailed(displayName));
                    ctx.Cb.RaiseError(L.S_HashFailed(displayName));
                    return false;
                }
            }
            return true;
        }
    }

    #endregion

    #region Client-side protocol

    /// <summary>Sending side of the 0x00-0x03 protocol, shared by TCP and UDT clients.
    /// Connection setup (and its transport-specific logging) stays in the transport classes.</summary>
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

            cb.RaiseComplete();
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

            cb.RaiseComplete();
        }

        /// <summary>
        /// Sends a folder with resume support (type 0x04). Sends a manifest with per-file
        /// full-file SHA256, then only the bytes the server reports as missing. The client
        /// persists a FolderResumeState so an interrupted session can be retried later.
        /// </summary>
        public static async Task SendFolderResumableAsync(IWireStream s, string folderPath, Guid sessionId,
            string serverIp, int port, bool isUdt, int bufferSize, SpeedLimiter limiter, WireCallbacks cb,
            CancellationToken ct)
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
                SentBytes = 0
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
                FolderResumeState.Delete(sessionId);
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

            FolderResumeState.Delete(sessionId);
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

            // Server is authoritative: use its offset
            long actualStart = Math.Max(sentBytes, serverOffset);
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
