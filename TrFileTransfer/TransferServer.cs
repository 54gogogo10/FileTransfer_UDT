using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>TCP file/folder receiver with SHA256 integrity verification.</summary>
    public class TransferServer
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly string _bindAddress;
        private readonly int _port;
        private readonly string _saveDirectory;
        private readonly int _bufferSize;
        private volatile bool _isRunning;
        private readonly ConcurrentDictionary<string, ChunkTracker> _chunkTrackers
            = new ConcurrentDictionary<string, ChunkTracker>();
        private readonly ConcurrentDictionary<Guid, ResumeState> _resumeStates
            = new ConcurrentDictionary<Guid, ResumeState>();

        /// <summary>Fired for every log message.</summary>
        public event Action<string> OnLog;
        /// <summary>Fired periodically during transfer with progress info.</summary>
        public event Action<TransferProgress> OnProgress;
        /// <summary>Fired when a non-fatal error occurs.</summary>
        public event Action<string> OnError;
        /// <summary>Fired when a single transfer completes. Server keeps listening.</summary>
        public event Action OnTransferComplete;
        /// <summary>Fired when a file has been fully received and saved (path, size).</summary>
        public event Action<string, long> OnFileReceived;
        /// <summary>Fired when the server starts listening.</summary>
        public event Action OnStarted;
        /// <summary>Fired when the server stops.</summary>
        public event Action OnStopped;
        /// <summary>Fired when a new client connects (with endpoint for per-client tracking).</summary>
        public event Action<IPEndPoint> OnClientConnected;
        /// <summary>Fired periodically during a client's transfer with endpoint.</summary>
        public event Action<IPEndPoint, TransferProgress> OnClientProgress;
        /// <summary>Fired when a single client's transfer completes.</summary>
        public event Action<IPEndPoint> OnClientTransferComplete;

        /// <summary>Whether the server is currently listening.</summary>
        public bool IsRunning { get { return _isRunning; } }

        /// <summary>
        /// Creates a TCP server that listens for incoming file transfers.
        /// </summary>
        /// <param name="bindAddress">IPv4 address to bind to, or "0.0.0.0" for all interfaces.</param>
        /// <param name="port">Port to listen on.</param>
        /// <param name="saveDirectory">Directory where received files are saved.</param>
        /// <param name="bufferSize">I/O buffer size in bytes (default 1 MB).</param>
        public TransferServer(string bindAddress, int port, string saveDirectory, int bufferSize = 4194304)
        {
            _bindAddress = bindAddress;
            _port = port;
            _saveDirectory = saveDirectory;
            _bufferSize = bufferSize;
        }

        /// <summary>Starts listening for incoming connections. Fires OnStarted on success.</summary>
        public void Start()
        {
            _cts = new CancellationTokenSource();
            ServerResumeStore.CleanupStale(7); // drop orphaned resume sessions from clients that never returned
            IPAddress bindIp;
            if (!IPAddress.TryParse(_bindAddress, out bindIp))
                bindIp = IPAddress.Any;

            try
            {
                _listener = new TcpListener(bindIp, _port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();
            }
            catch (Exception ex)
            {
                Log(L.S_BindFailed(_bindAddress, _port.ToString(), ex.Message));
                var errHandler = OnError;
                if (errHandler != null) errHandler(ex.Message);
                _isRunning = false;
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
                return;
            }

            _isRunning = true;

            var handler = OnStarted;
            if (handler != null) handler();

            Log(L.S_Started(_port.ToString(), _saveDirectory));

            Task.Factory.StartNew(() => AcceptLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>Stops the server and closes the listening socket.</summary>
        public void Stop()
        {
            _isRunning = false;
            var cts = _cts;
            if (cts != null) cts.Cancel();
            try
            {
                var listener = _listener;
                if (listener != null) listener.Stop();
            }
            catch { }

            // Clean up any incomplete chunk trackers
            foreach (var kv in _chunkTrackers)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _chunkTrackers.Clear();

            // Persist incomplete resume states before releasing file handles
            foreach (var kv in _resumeStates)
            {
                try
                {
                    if (kv.Value.WriteStream != null) kv.Value.WriteStream.Flush();
                    ServerResumeStore.Save(kv.Value);
                    if (kv.Value.WriteStream != null) { kv.Value.WriteStream.Dispose(); kv.Value.WriteStream = null; }
                }
                catch { }
            }
            _resumeStates.Clear();

            var handler = OnStopped;
            if (handler != null) handler();

            Log(L.S_Stopped);
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    client.NoDelay = true;
                    client.SendBufferSize = _bufferSize;
                    client.ReceiveBufferSize = _bufferSize;
                    var clientEp = client.Client.RemoteEndPoint as IPEndPoint;
                    Log(L.S_ClientConnected(clientEp));
                    var _ = HandleClient(client, ct, clientEp);
                }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException)
                        break;
                    if (!ct.IsCancellationRequested)
                    {
                        Log(L.S_AcceptError(ex.Message));
                        var handler = OnError;
                        if (handler != null) handler(ex.Message);
                    }
                }
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken ct, IPEndPoint clientEp)
        {
            var connectedHandler = OnClientConnected;
            if (connectedHandler != null) connectedHandler(clientEp);

            Action<TransferProgress> clientProgress = p =>
            {
                var ch = OnClientProgress; if (ch != null) ch(clientEp, p);
            };
            OnProgress += clientProgress;

            using (client)
            {
                try
                {
                    var stream = client.GetStream();

                    // Read transfer type byte
                    var typeBuf = new byte[1];
                    await ReadExactAsync(stream, typeBuf, 0, 1, ct);
                    byte transferType = typeBuf[0];

                    if (transferType == 0x01)
                    {
                        await HandleFolderTransfer(stream, ct);
                    }
                    else if (transferType == 0x02)
                    {
                        await HandleChunkedFile(stream, ct);
                    }
                    else if (transferType == 0x03)
                    {
                        await HandleResumableFile(stream, ct);
                    }
                    else
                    {
                        await HandleFileTransfer(stream, ct);
                    }

                    var ccHandler = OnClientTransferComplete;
                    if (ccHandler != null) ccHandler(clientEp);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (IOException ex)
                {
                    Log(L.S_ConnectionError(ex.Message));
                    var handler = OnError;
                    if (handler != null) handler(ex.Message);
                }
                catch (Exception ex)
                {
                    Log(L.S_UnexpectedError(ex.Message));
                    var handler = OnError;
                    if (handler != null) handler(ex.Message);
                }
                finally
                {
                    OnProgress -= clientProgress;
                }
            }
        }

        private async Task HandleFileTransfer(NetworkStream stream, CancellationToken ct)
        {
            var headerBuf = new byte[12];
            await ReadExactAsync(stream, headerBuf, 0, 12, ct);

            long fileSize = BitConverter.ToInt64(headerBuf, 0);
            int nameLen = BitConverter.ToInt32(headerBuf, 8);

            if (fileSize < 0 || nameLen <= 0 || nameLen > 4096)
            {
                Log(L.S_InvalidHeader(fileSize, nameLen));
                return;
            }

            var nameBuf = new byte[nameLen];
            await ReadExactAsync(stream, nameBuf, 0, nameLen, ct);
            string fileName = System.Text.Encoding.UTF8.GetString(nameBuf);

            fileName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = L.S_ReceivedFile;

            string savePath = Utils.GetUniqueSavePath(_saveDirectory, fileName);

            Log(L.S_Receiving(fileName, Utils.FormatSize(fileSize)));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool hashOk = await ReceiveFilePayload(stream, savePath, fileSize, fileName, ct);
            sw.Stop();

            if (hashOk)
            {
                Log(L.S_TransferDone(fileName, Utils.FormatSize(fileSize),
                    sw.Elapsed.TotalSeconds,
                    Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
                RaiseFileReceived(savePath, fileSize);
            }
        }

        private async Task HandleChunkedFile(NetworkStream stream, CancellationToken ct)
        {
            var headerBuf = new byte[28]; // totalSize(8) + chunkOffset(8) + chunkSize(8) + nameLen(4)
            await ReadExactAsync(stream, headerBuf, 0, 28, ct);

            long totalSize = BitConverter.ToInt64(headerBuf, 0);
            long chunkOffset = BitConverter.ToInt64(headerBuf, 8);
            long chunkSize = BitConverter.ToInt64(headerBuf, 16);
            int nameLen = BitConverter.ToInt32(headerBuf, 24);

            if (totalSize <= 0 || chunkOffset < 0 || chunkSize <= 0 || nameLen <= 0 || nameLen > 4096)
            {
                Log(L.S_InvalidHeader(totalSize, nameLen));
                return;
            }

            var nameBuf = new byte[nameLen];
            await ReadExactAsync(stream, nameBuf, 0, nameLen, ct);
            string fileName = System.Text.Encoding.UTF8.GetString(nameBuf);
            fileName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = L.S_ReceivedFile;

            ChunkTracker tracker = ChunkTracker.GetOrCreate(
                _chunkTrackers, fileName, totalSize, _saveDirectory);

            // Stream chunk data through a fixed-size buffer — no giant array allocation
            const int BufSize = 4194304;
            var buf = new byte[BufSize];
            bool hashOk;
            bool isComplete = false;
            using (var sha256 = SHA256.Create())
            {
                long remaining = chunkSize;
                long writeOffset = chunkOffset;
                while (remaining > 0 && !ct.IsCancellationRequested)
                {
                    int toRead = (int)Math.Min(remaining, (long)BufSize);
                    await ReadExactAsync(stream, buf, 0, toRead, ct);
                    sha256.TransformBlock(buf, 0, toRead, null, 0);
                    isComplete = tracker.WriteChunk(writeOffset, buf, toRead);
                    writeOffset += toRead;
                    remaining -= toRead;
                }
                sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                var receivedHash = new byte[32];
                await ReadExactAsync(stream, receivedHash, 0, 32, ct);
                hashOk = Utils.ConstantTimeEquals(receivedHash, sha256.Hash);
            }

            if (hashOk)
            {

                // Report aggregate progress across all chunks
                var progressHandler = OnProgress;
                if (progressHandler != null)
                {
                    progressHandler(new TransferProgress
                    {
                        BytesTransferred = tracker.BytesReceived,
                        TotalBytes = totalSize,
                        SpeedBytesPerSecond = 0,
                        Elapsed = TimeSpan.Zero,
                        FileName = fileName
                    });
                }

                Log(L.S_ChunkOk(fileName, chunkOffset, Utils.FormatSize(chunkSize),
                    tracker.ChunksCompleted));

                if (isComplete)
                {
                    tracker.Dispose();
                    ChunkTracker removed;
                    _chunkTrackers.TryRemove(fileName, out removed);
                    Log(L.S_TransferDone(fileName, Utils.FormatSize(totalSize), 0.0, ""));
                    var completeHandler = OnTransferComplete;
                    if (completeHandler != null) completeHandler();
                    RaiseFileReceived(tracker.SavePath, totalSize);
                }
            }
            else
            {
                // Clean up tracker on hash failure
                ChunkTracker removed;
                if (_chunkTrackers.TryRemove(fileName, out removed))
                {
                    try { removed.Dispose(); } catch { }
                }
                Log(L.S_HashFailed(fileName));
                var errHandler = OnError;
                if (errHandler != null) errHandler(L.S_HashFailed(fileName));
            }
        }

        private async Task HandleResumableFile(NetworkStream stream, CancellationToken ct)
        {
            // Read 0x03 header: sessionId(16) + totalSize(8) + resumeOffset(8) + nameLen(4) = 36
            var headerBuf = new byte[36];
            await ReadExactAsync(stream, headerBuf, 0, 36, ct).ConfigureAwait(false);

            var sidBytes = new byte[16];
            Buffer.BlockCopy(headerBuf, 0, sidBytes, 0, 16);
            var sessionId = new Guid(sidBytes);
            long totalSize = BitConverter.ToInt64(headerBuf, 16);
            long clientOffset = BitConverter.ToInt64(headerBuf, 24);
            int nameLen = BitConverter.ToInt32(headerBuf, 32);

            if (totalSize <= 0 || clientOffset < 0 || clientOffset > totalSize || nameLen <= 0 || nameLen > 4096)
            {
                Log(L.S_InvalidHeader(totalSize, nameLen));
                return;
            }

            var nameBuf = new byte[nameLen];
            await ReadExactAsync(stream, nameBuf, 0, nameLen, ct).ConfigureAwait(false);
            string fileName = System.Text.Encoding.UTF8.GetString(nameBuf);
            fileName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = L.S_ReceivedFile;

            // Optional full-file hash verification extension: [verifyFlag(1) + fullHash(32)]
            var flagBuf = new byte[1];
            await ReadExactAsync(stream, flagBuf, 0, 1, ct).ConfigureAwait(false);
            byte[] expectedFullHash = null;
            if (flagBuf[0] == 1)
            {
                expectedFullHash = new byte[32];
                await ReadExactAsync(stream, expectedFullHash, 0, 32, ct).ConfigureAwait(false);
            }

            // From here on, the session is ours: persist any incomplete state to disk
            // on the way out (connection drop / server restart) and clear it once the
            // transfer completes or is discarded.
            try
            {
                await HandleResumableCore(stream, ct, sessionId, totalSize, clientOffset, fileName, expectedFullHash).ConfigureAwait(false);
            }
            finally
            {
                // Save only — never Delete here: Stop() may have already saved and
                // cleared the dict, and blindly deleting would lose the checkpoint.
                ResumeState st;
                if (_resumeStates.TryGetValue(sessionId, out st))
                {
                    // Flush so the persisted ReceivedBytes always matches what is on disk
                    try { if (st.WriteStream != null) st.WriteStream.Flush(); } catch { }
                    ServerResumeStore.Save(st);
                }
            }
        }

        private async Task HandleResumableCore(NetworkStream stream, CancellationToken ct,
            Guid sessionId, long totalSize, long clientOffset, string fileName, byte[] expectedFullHash)
        {
            ResumeState state = null;
            _resumeStates.TryGetValue(sessionId, out state);
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
                                FileShare.None, _bufferSize, FileOptions.RandomAccess);
                            disk.WriteStream.Seek(disk.ReceivedBytes, SeekOrigin.Begin);
                            if (_resumeStates.TryAdd(sessionId, disk))
                            {
                                state = disk;
                                isNew = false;
                                Log(string.Format("Resume: restored server state for session {0} at offset {1}",
                                    sessionId.ToString("N"), disk.ReceivedBytes));
                            }
                            else
                            {
                                disk.WriteStream.Dispose();
                                _resumeStates.TryGetValue(sessionId, out state);
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
                newState.SavePath = Utils.GetUniqueSavePath(_saveDirectory, fileName);
                newState.WriteStream = new FileStream(newState.SavePath, FileMode.Create, FileAccess.Write,
                    FileShare.None, _bufferSize, FileOptions.RandomAccess);
                newState.WriteStream.SetLength(totalSize);

                if (_resumeStates.TryAdd(sessionId, newState))
                {
                    state = newState;
                }
                else
                {
                    newState.WriteStream.Dispose();
                    try { File.Delete(newState.SavePath); } catch { }
                    _resumeStates.TryGetValue(sessionId, out state);
                    isNew = false;
                }
            }

            if (isNew)
            {
                status = 0;
                resumeFrom = 0;
                if (clientOffset > 0)
                {
                    Log(string.Format("Resume: no server state for session {0}, restarting from 0 (client claimed {1})",
                        sessionId.ToString("N"), clientOffset));
                }
            }
            else
            {
                if (state.TotalSize != totalSize)
                {
                    Log(string.Format("Resume size mismatch: session={0} expect={1} got={2}",
                        sessionId.ToString("N"), state.TotalSize, totalSize));
                    return;
                }
                if (state.ReceivedBytes >= totalSize)
                {
                    status = 2;
                    resumeFrom = totalSize;
                    await SendResumeResponse(stream, resumeFrom, status, ct).ConfigureAwait(false);
                    return;
                }
                status = 1;
                resumeFrom = state.ReceivedBytes;
            }

            // Negotiate: use max of client's claim and server's actual received.
            // When the server has no state for this session (e.g. server restarted),
            // the client's offset claim is NOT trusted — restart from 0 so a fresh
            // pre-allocated file is never zero-padded behind a stale client offset.
            long actualStart = isNew ? 0 : Math.Max(clientOffset, resumeFrom);
            await SendResumeResponse(stream, actualStart, status, ct).ConfigureAwait(false);

            // If client has less than server, it'll re-send from actualStart
            long remaining = totalSize - actualStart;
            Log(string.Format("Resume: {0} offset={1} remaining={2} status={3}",
                fileName, actualStart, Utils.FormatSize(remaining), status));

            // Receive remaining data + SHA256
            state.WriteStream.Seek(actualStart, SeekOrigin.Begin);
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                long bytesRead = 0;
                var buf = new byte[_bufferSize];
                var progressTimer = System.Diagnostics.Stopwatch.StartNew();
                var elapsedSw = System.Diagnostics.Stopwatch.StartNew();

                while (bytesRead < remaining && !ct.IsCancellationRequested)
                {
                    int toRead = (int)Math.Min(remaining - bytesRead, (long)buf.Length);
                    int read = await stream.ReadAsync(buf, 0, toRead, ct).ConfigureAwait(false);
                    if (read == 0)
                        throw new IOException(L.S_ConnClosedPrematurely);
                    sha256.TransformBlock(buf, 0, read, null, 0);
                    await state.WriteStream.WriteAsync(buf, 0, read, ct).ConfigureAwait(false);
                    bytesRead += read;
                    state.ReceivedBytes = actualStart + bytesRead;

                    if (progressTimer.ElapsedMilliseconds >= 100)
                    {
                        progressTimer.Restart();
                        var progressHandler = OnProgress;
                        if (progressHandler != null)
                        {
                            progressHandler(new TransferProgress
                            {
                                BytesTransferred = state.ReceivedBytes,
                                TotalBytes = totalSize,
                                SpeedBytesPerSecond = state.ReceivedBytes / Math.Max(elapsedSw.Elapsed.TotalSeconds, 0.001),
                                Elapsed = elapsedSw.Elapsed,
                                FileName = fileName
                            });
                        }
                    }
                }

                sha256.TransformFinalBlock(buf, 0, 0);
                var computedHash = sha256.Hash;
                var receivedHash = new byte[32];
                await ReadExactAsync(stream, receivedHash, 0, 32, ct).ConfigureAwait(false);

                if (Utils.ConstantTimeEquals(computedHash, receivedHash))
                {
                    // Close the write stream so the file can be re-read for full verification
                    state.WriteStream.Dispose();
                    state.WriteStream = null;

                    if (expectedFullHash == null || await VerifyFullHashFile(state.SavePath, expectedFullHash, ct).ConfigureAwait(false))
                    {
                        ResumeState removed;
                        _resumeStates.TryRemove(sessionId, out removed);
                        ServerResumeStore.Delete(sessionId);
                        Log(L.S_TransferDone(fileName, Utils.FormatSize(totalSize), 0.0, ""));
                        await SendResumeResponse(stream, totalSize, 2, ct).ConfigureAwait(false);
                        RaiseFileReceived(state.SavePath, totalSize);
                    }
                    else
                    {
                        // Full-file hash mismatch — the resumed file mixes old and new
                        // segments; discard it and tell the client to retry.
                        ResumeState removed;
                        _resumeStates.TryRemove(sessionId, out removed);
                        ServerResumeStore.Delete(sessionId);
                        try { File.Delete(state.SavePath); } catch { }
                        Log(L.S_FullHashFailed(fileName));
                        var errHandler = OnError;
                        if (errHandler != null) errHandler(L.S_FullHashFailed(fileName));
                        await SendResumeResponse(stream, totalSize, 3, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    // Clean up on hash failure
                    try { state.WriteStream.Dispose(); } catch { }
                    state.WriteStream = null;
                    ResumeState removed;
                    _resumeStates.TryRemove(sessionId, out removed);
                    ServerResumeStore.Delete(sessionId);
                    Log(L.S_HashFailed(fileName));
                    var errHandler = OnError;
                    if (errHandler != null) errHandler(L.S_HashFailed(fileName));
                }
            }
        }

        private static async Task SendResumeResponse(NetworkStream stream, long offset, byte status, CancellationToken ct)
        {
            var resp = new byte[10]; // type(1) + offset(8) + status(1)
            resp[0] = 0x10;
            Buffer.BlockCopy(BitConverter.GetBytes(offset), 0, resp, 1, 8);
            resp[9] = status;
            await stream.WriteAsync(resp, 0, 10, ct).ConfigureAwait(false);
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

        private void RaiseFileReceived(string path, long size)
        {
            var handler = OnFileReceived;
            if (handler != null) handler(path, size);
        }

        private async Task HandleFolderTransfer(NetworkStream stream, CancellationToken ct)
        {
            // Read folder header: folderNameLen(2) + folderName + fileCount(4)
            var folderHeaderBuf = new byte[2];
            await ReadExactAsync(stream, folderHeaderBuf, 0, 2, ct);
            int folderNameLen = BitConverter.ToInt16(folderHeaderBuf, 0);
            if (folderNameLen <= 0 || folderNameLen > 4096) return;

            var folderNameBuf = new byte[folderNameLen];
            await ReadExactAsync(stream, folderNameBuf, 0, folderNameLen, ct);
            string folderName = System.Text.Encoding.UTF8.GetString(folderNameBuf);
            folderName = Path.GetFileName(folderName);
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = "received_folder";

            var fileCountBuf = new byte[4];
            await ReadExactAsync(stream, fileCountBuf, 0, 4, ct);
            int fileCount = BitConverter.ToInt32(fileCountBuf, 0);
            if (fileCount <= 0) return;

            string folderSaveDir = Utils.GetUniqueSavePath(_saveDirectory, folderName);
            Directory.CreateDirectory(folderSaveDir);

            Log(L.S_ReceivingFolder(folderName, fileCount, "..."));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long totalSize = 0;
            int filesReceived = 0;

            for (int i = 0; i < fileCount && !ct.IsCancellationRequested; i++)
            {
                var fileHeaderBuf = new byte[10];
                await ReadExactAsync(stream, fileHeaderBuf, 0, 10, ct);
                long fileSize = BitConverter.ToInt64(fileHeaderBuf, 0);
                int pathLen = BitConverter.ToInt16(fileHeaderBuf, 8);
                if (fileSize < 0 || pathLen <= 0 || pathLen > 4096) return;

                var pathBuf = new byte[pathLen];
                await ReadExactAsync(stream, pathBuf, 0, pathLen, ct);
                string relativePath = System.Text.Encoding.UTF8.GetString(pathBuf);
                relativePath = Utils.SanitizeRelativePath(relativePath);

                string savePath = Path.Combine(folderSaveDir, relativePath);
                string dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                bool hashOk = await ReceiveFilePayload(stream, savePath, fileSize, relativePath, ct);
                if (!hashOk) return;
                RaiseFileReceived(savePath, fileSize);

                totalSize += fileSize;
                filesReceived++;

                var progressHandler = OnProgress;
                if (progressHandler != null)
                    progressHandler(new TransferProgress
                    {
                        BytesTransferred = filesReceived,
                        TotalBytes = fileCount,
                        SpeedBytesPerSecond = (totalSize > 0 ? totalSize : 0) / sw.Elapsed.TotalSeconds,
                        Elapsed = sw.Elapsed,
                        FileName = folderName
                    });
            }

            sw.Stop();
            Log(L.S_FolderTransferDone(folderName, fileCount, Utils.FormatSize(totalSize),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(totalSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

            var completeHandler = OnTransferComplete;
            if (completeHandler != null) completeHandler();
        }

        // Returns true if hash verification passed
        private async Task<bool> ReceiveFilePayload(NetworkStream stream, string savePath, long fileSize,
            string displayName, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytesRead = 0;
            var bufA = new byte[_bufferSize];
            var bufB = new byte[_bufferSize];
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();

            using (var sha256 = SHA256.Create())
            using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write,
                FileShare.None, _bufferSize, FileOptions.SequentialScan))
            {
                long remaining = fileSize;
                if (remaining == 0)
                {
                    // Empty file: only the 32-byte hash follows
                    var emptyHash = new byte[32];
                    await ReadExactAsync(stream, emptyHash, 0, 32, ct).ConfigureAwait(false);
                    return Utils.ConstantTimeEquals(emptyHash, sha256.ComputeHash(Utils.EmptyBytes));
                }
                int toRead = (int)Math.Min(remaining, (long)bufA.Length);
                int read = await stream.ReadAsync(bufA, 0, toRead, ct);
                if (read == 0)
                    throw new IOException(L.S_ConnClosedPrematurely);

                remaining -= read;
                var cur = bufA;
                var nxt = bufB;

                while (remaining > 0 && !ct.IsCancellationRequested)
                {
                    int nextToRead = (int)Math.Min(remaining, (long)nxt.Length);
                    var nextReadTask = stream.ReadAsync(nxt, 0, nextToRead, ct);

                    sha256.TransformBlock(cur, 0, read, null, 0);
                    await fileStream.WriteAsync(cur, 0, read, ct);
                    bytesRead += read;

                    read = await nextReadTask;
                    if (read == 0)
                        throw new IOException(L.S_ConnClosedPrematurely);
                    remaining -= read;

                    var tmp = cur; cur = nxt; nxt = tmp;

                    if (progressTimer.ElapsedMilliseconds >= 100 || remaining == 0)
                    {
                        progressTimer.Restart();
                        var progressHandler = OnProgress;
                        if (progressHandler != null)
                            progressHandler(new TransferProgress
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
                var finalProgressHandler = OnProgress;
                if (finalProgressHandler != null)
                    finalProgressHandler(new TransferProgress
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
                await ReadExactAsync(stream, receivedHash, 0, 32, ct);
                var computedHash = sha256.Hash;

                if (!Utils.ConstantTimeEquals(receivedHash, computedHash))
                {
                    Log(L.S_HashFailed(displayName));
                    var errHandler = OnError;
                    if (errHandler != null) errHandler(L.S_HashFailed(displayName));
                    return false;
                }
            }
            return true;
        }

        private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, ct);
                if (read == 0) throw new IOException(L.S_ConnClosedUnexpectedly);
                totalRead += read;
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }
}
