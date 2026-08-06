using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>TCP file/folder sender with SHA256 integrity verification.</summary>
    public class TransferClient
    {
        private CancellationTokenSource _cts;
        private readonly string _serverIp;
        private readonly int _port;
        private readonly string _filePath;
        private readonly int _bufferSize;
        private readonly int _localPort;
        private volatile bool _isRunning;

        /// <summary>Fired for every log message.</summary>
        public event Action<string> OnLog;
        /// <summary>Fired periodically during transfer with progress info.</summary>
        public event Action<TransferProgress> OnProgress;
        /// <summary>Fired when a non-fatal error occurs.</summary>
        public event Action<string> OnError;
        /// <summary>Fired when the transfer completes successfully.</summary>
        public event Action OnTransferComplete;
        /// <summary>Fired when the transfer starts.</summary>
        public event Action OnStarted;
        /// <summary>Fired when the transfer stops (completed, cancelled, or error).</summary>
        public event Action OnStopped;

        /// <summary>Whether a transfer is currently in progress.</summary>
        public bool IsRunning { get { return _isRunning; } }

        /// <summary>
        /// Creates a TCP client for sending files or folders.
        /// </summary>
        /// <param name="serverIp">Target server IPv4 address.</param>
        /// <param name="port">Target server port.</param>
        /// <param name="filePath">Path to the file or folder to send.</param>
        /// <param name="bufferSize">I/O buffer size in bytes (default 1 MB).</param>
        public TransferClient(string serverIp, int port, string filePath, int bufferSize = 4194304)
        {
            _serverIp = serverIp;
            _port = port;
            _filePath = filePath;
            _bufferSize = bufferSize;
        }

        /// <summary>Creates a TCP client bound to a specific local port for concurrent transfers.</summary>
        public TransferClient(string serverIp, int port, string filePath, int localPort, int bufferSize = 4194304)
            : this(serverIp, port, filePath, bufferSize)
        {
            _localPort = localPort;
        }

        /// <summary>Sends the file specified in the constructor over TCP.</summary>
        public async Task SendAsync()
        {
            await RunTransfer(SendFileInternal);
        }

        /// <summary>Sends a folder recursively over TCP.</summary>
        /// <param name="folderPath">Path to the folder to send.</param>
        public async Task SendFolderAsync(string folderPath)
        {
            await RunTransfer(ct => SendFolderInternal(folderPath, ct));
        }

        /// <summary>Sends a chunk of a file (type 0x02) for concurrent transfer.</summary>
        public async Task SendChunkedAsync(long offset, long chunkSize, long totalSize)
        {
            await RunTransfer(ct => SendChunkedInternal(offset, chunkSize, totalSize, ct));
        }

        /// <summary>Sends a file with resume support (type 0x03), negotiating with server via 0x10 response.</summary>
        public async Task<Guid> SendResumableAsync(Guid? existingSessionId = null)
        {
            var sessionId = existingSessionId ?? Guid.NewGuid();
            await RunTransfer(ct => SendResumableInternal(sessionId, ct));
            return sessionId;
        }

        private async Task RunTransfer(Func<CancellationToken, Task> transferAction)
        {
            _cts = new CancellationTokenSource();
            _isRunning = true;

            var startedHandler = OnStarted;
            if (startedHandler != null) startedHandler();

            try
            {
                await transferAction(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log(L.C_TransferCancelled);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log(L.C_Error(ex.Message));
                var handler = OnError;
                if (handler != null) handler(ex.Message);
                throw;
            }
            finally
            {
                _isRunning = false;
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
            }
        }

        /// <summary>Cancels the current transfer. Safe to call from any thread.</summary>
        public void Cancel()
        {
            var cts = _cts;
            if (cts != null) cts.Cancel();
        }

        private async Task SendFileInternal(CancellationToken ct)
        {
            using (var client = new TcpClient())
            {
                client.NoDelay = true;
                client.SendBufferSize = _bufferSize;
                client.ReceiveBufferSize = _bufferSize;

                Log(L.C_Connecting(_serverIp, _port));
                await client.ConnectAsync(_serverIp, _port);
                Log(L.C_Connected(_serverIp, _port));

                var stream = client.GetStream();
                var fileInfo = new FileInfo(_filePath);
                long fileSize = fileInfo.Length;
                string fileName = fileInfo.Name;
                byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);

                var header = new byte[1 + 12 + nameBytes.Length];
                header[0] = 0x00; // single file
                Buffer.BlockCopy(BitConverter.GetBytes(fileSize), 0, header, 1, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(nameBytes.Length), 0, header, 9, 4);
                Buffer.BlockCopy(nameBytes, 0, header, 13, nameBytes.Length);
                await stream.WriteAsync(header, 0, header.Length, ct);

                Log(L.C_Sending(fileName, Utils.FormatSize(fileSize)));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                await SendFilePayload(stream, _filePath, fileSize, fileName, ct);
                sw.Stop();

                Log(L.C_TransferDone(fileName, Utils.FormatSize(fileSize),
                    sw.Elapsed.TotalSeconds,
                    Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
        }

        private async Task SendFolderInternal(string folderPath, CancellationToken ct)
        {
            using (var client = new TcpClient())
            {
                client.NoDelay = true;
                client.SendBufferSize = _bufferSize;
                client.ReceiveBufferSize = _bufferSize;

                Log(L.C_Connecting(_serverIp, _port));
                await client.ConnectAsync(_serverIp, _port);
                Log(L.C_Connected(_serverIp, _port));

                var stream = client.GetStream();

                string folderName = Path.GetFileName(folderPath);
                if (string.IsNullOrWhiteSpace(folderName))
                    folderName = "folder";
                byte[] folderNameBytes = System.Text.Encoding.UTF8.GetBytes(folderName);

                var files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    Log(L.S_ZeroFiles);
                    var errHandler = OnError;
                    if (errHandler != null) errHandler(L.S_ZeroFiles);
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

                Log(L.C_SendingFolder(folderName, files.Length, Utils.FormatSize(totalSize)));

                // Folder header: type(1) + folderNameLen(2) + folderName + fileCount(4)
                var header = new byte[1 + 2 + folderNameBytes.Length + 4];
                int pos = 0;
                header[pos++] = 0x01; // folder
                Buffer.BlockCopy(BitConverter.GetBytes((short)folderNameBytes.Length), 0, header, pos, 2); pos += 2;
                Buffer.BlockCopy(folderNameBytes, 0, header, pos, folderNameBytes.Length); pos += folderNameBytes.Length;
                Buffer.BlockCopy(BitConverter.GetBytes(files.Length), 0, header, pos, 4);
                await stream.WriteAsync(header, 0, header.Length, ct);

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
                    await stream.WriteAsync(fileHeader, 0, fileHeader.Length, ct);

                    await SendFilePayload(stream, entry.Path, entry.Size, entry.RelativePath, ct);
                    totalSent += entry.Size;

                    var progressHandler = OnProgress;
                    if (progressHandler != null)
                        progressHandler(new TransferProgress
                        {
                            BytesTransferred = totalSent,
                            TotalBytes = totalSize,
                            SpeedBytesPerSecond = totalSent / sw.Elapsed.TotalSeconds,
                            Elapsed = sw.Elapsed,
                            FileName = folderName
                        });
                }

                sw.Stop();
                Log(L.C_FolderTransferDone(folderName, files.Length, Utils.FormatSize(totalSize),
                    sw.Elapsed.TotalSeconds,
                    Utils.FormatSize((long)(totalSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
        }

        private async Task SendChunkedInternal(long offset, long chunkSize, long totalSize, CancellationToken ct)
        {
            using (var client = _localPort > 0
                ? new TcpClient(new IPEndPoint(IPAddress.Any, _localPort))
                : new TcpClient())
            {
                client.NoDelay = true;
                client.SendBufferSize = _bufferSize;
                client.ReceiveBufferSize = _bufferSize;

                await client.ConnectAsync(_serverIp, _port);
                var stream = client.GetStream();

                string fileName = Path.GetFileName(_filePath);
                byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);

                // Header: type(1) + totalSize(8) + chunkOffset(8) + chunkSize(8) + nameLen(4) + name
                var header = new byte[1 + 28 + nameBytes.Length];
                header[0] = 0x02;
                Buffer.BlockCopy(BitConverter.GetBytes(totalSize), 0, header, 1, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(offset), 0, header, 9, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(chunkSize), 0, header, 17, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(nameBytes.Length), 0, header, 25, 4);
                Buffer.BlockCopy(nameBytes, 0, header, 29, nameBytes.Length);
                await stream.WriteAsync(header, 0, header.Length, ct);

                Log(string.Format("Chunk sending: {0} offset={1} size={2}",
                    fileName, offset, Utils.FormatSize(chunkSize)));

                await SendFilePayload(stream, _filePath, chunkSize, fileName, ct, offset);

                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
        }

        private async Task SendResumableInternal(Guid sessionId, CancellationToken ct)
        {
            ResumeState.EnsureDir();
            var fileInfo = new FileInfo(_filePath);
            long fileSize = fileInfo.Length;
            string fileName = fileInfo.Name;
            long sentBytes = 0;
            long sourceMTime;
            try { sourceMTime = File.GetLastWriteTimeUtc(_filePath).Ticks; }
            catch { sourceMTime = 0; }

            // Load existing state if resuming
            var existingState = ResumeState.Load(sessionId);
            if (existingState != null && existingState.SourceMTime != 0 && existingState.SourceMTime != sourceMTime)
            {
                // Source file changed since the interrupted transfer — restart from scratch
                Log(L.C_ResumeSourceChanged(fileName));
                ResumeState.Delete(sessionId);
                existingState = null;
            }
            if (existingState != null)
            {
                sentBytes = existingState.SentBytes;
                Log(L.C_Resuming(fileName, sentBytes, Utils.FormatSize(sentBytes)));
            }
            else
            {
                var newState = new ResumeState
                {
                    SessionId = sessionId,
                    TotalSize = fileSize,
                    FileName = fileName,
                    FilePath = _filePath,
                    ServerIp = _serverIp,
                    Port = _port,
                    IsUdt = false,
                    Created = DateTime.UtcNow,
                    SentBytes = 0,
                    SourceMTime = sourceMTime
                };
                newState.Save();
            }

            using (var client = _localPort > 0
                ? new TcpClient(new IPEndPoint(IPAddress.Any, _localPort))
                : new TcpClient())
            {
                client.NoDelay = true;
                client.SendBufferSize = _bufferSize;
                client.ReceiveBufferSize = _bufferSize;

                await client.ConnectAsync(_serverIp, _port).ConfigureAwait(false);
                var stream = client.GetStream();

                // Build 0x03 header: type(1) + sessionId(16) + totalSize(8) + resumeOffset(8) + nameLen(4) + name
                byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);
                var header = new byte[1 + 16 + 8 + 8 + 4 + nameBytes.Length];
                header[0] = 0x03;
                Buffer.BlockCopy(sessionId.ToByteArray(), 0, header, 1, 16);
                Buffer.BlockCopy(BitConverter.GetBytes(fileSize), 0, header, 17, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(sentBytes), 0, header, 25, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(nameBytes.Length), 0, header, 33, 4);
                Buffer.BlockCopy(nameBytes, 0, header, 37, nameBytes.Length);
                await stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);

                // Read 0x10 server response (10 bytes: 0x10 + offset8 + status1)
                var respBuf = new byte[10];
                await ReadExactResumeAsync(stream, respBuf, 0, 10, ct).ConfigureAwait(false);

                if (respBuf[0] != 0x10)
                    throw new InvalidDataException(string.Format("Unexpected resume response type: {0}", respBuf[0]));

                byte respStatus = respBuf[9];
                long serverOffset = BitConverter.ToInt64(respBuf, 1);

                if (respStatus == 2)
                {
                    Log(L.C_AlreadyReceived(fileName));
                    ResumeState.Delete(sessionId);
                    var completeHandler = OnTransferComplete;
                    if (completeHandler != null) completeHandler();
                    return;
                }

                // Server is authoritative: use its offset
                long actualStart = Math.Max(sentBytes, serverOffset);
                Log(L.C_ResumeNegotiated(actualStart, serverOffset, sentBytes));

                // Send file data from actualStart
                await SendFilePayload(stream, _filePath, fileSize, fileName, ct, actualStart).ConfigureAwait(false);

                // Success — delete resume state
                ResumeState.Delete(sessionId);

                var doneHandler = OnTransferComplete;
                if (doneHandler != null) doneHandler();
            }
        }

        private static async Task ReadExactResumeAsync(NetworkStream stream, byte[] buf, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buf, offset + total, count - total, ct).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException(L.C_ResumeConnClosed);
                total += read;
            }
        }

        private async Task SendFilePayload(NetworkStream stream, string filePath, long fileSize,
            string displayName, CancellationToken ct, long fileOffset = 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytesSent = 0;
            var bufA = new byte[_bufferSize];
            var bufB = new byte[_bufferSize];
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();

            using (var sha256 = SHA256.Create())
            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, _bufferSize, FileOptions.SequentialScan))
            {
                if (fileOffset > 0)
                    fileStream.Seek(fileOffset, SeekOrigin.Begin);
                long totalToSend = fileSize;
                int firstToRead = (int)Math.Min((long)bufA.Length, totalToSend);
                int read = await fileStream.ReadAsync(bufA, 0, firstToRead, ct);
                if (read == 0)
                {
                    sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                    await stream.WriteAsync(sha256.Hash, 0, 32, ct);
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
                    await stream.WriteAsync(cur, 0, read, ct);
                    bytesSent += read;

                    if (nextReadTask == null)
                    {
                        read = 0;
                        break;
                    }
                    read = await nextReadTask;

                    var tmp = cur; cur = nxt; nxt = tmp;

                    if (progressTimer.ElapsedMilliseconds >= 100 || read == 0)
                    {
                        progressTimer.Restart();
                        var handler = OnProgress;
                        if (handler != null)
                            handler(new TransferProgress
                            {
                                BytesTransferred = bytesSent,
                                TotalBytes = fileSize,
                                SpeedBytesPerSecond = bytesSent / sw.Elapsed.TotalSeconds,
                                Elapsed = sw.Elapsed,
                                FileName = displayName
                            });
                    }
                }
                sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);

                await stream.WriteAsync(sha256.Hash, 0, 32, ct);
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }

    }
}
