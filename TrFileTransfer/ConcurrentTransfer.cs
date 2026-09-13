using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    #pragma warning disable 1591
    public class ConcurrentTransfer
    {
        private readonly string _serverIp;
        private readonly int _port;
        private readonly string _filePath;
        private readonly int _concurrency;
        private readonly bool _isUdt;
        private readonly int _srcPort;
        private readonly int _maxBytesPerSec;
        private const int ChunkMinSize = 1048576; // 1 MB minimum chunk size

        private long _totalBytes;
        private long _transferredBytes;

        // Cancellation: each in-flight client is registered so Cancel() can reach it.
        // A parallel transfer otherwise has no way to be stopped from the UI.
        private readonly object _cancelLock = new object();
        private readonly List<Action> _activeCancels = new List<Action>();
        private volatile bool _cancelled;

        public event Action<string> OnLog;
        public event Action<TransferProgress> OnProgress;
        public event Action<string> OnError;
        public event Action OnTransferComplete;

        /// <summary>Pairing code forwarded to every chunk connection (0x05 auth frame).</summary>
        public string PairingCode { get; set; }

        /// <summary>True once Cancel() was requested — callers use it to report "cancelled"
        /// rather than a failure when SendAsync/SendFolderAsync unwinds.</summary>
        public bool WasCancelled { get { return _cancelled; } }

        /// <summary>Cancels every in-flight chunk/file connection and stops scheduling
        /// further ones. Safe to call from the UI thread.</summary>
        public void Cancel()
        {
            _cancelled = true;
            Action[] pending;
            lock (_cancelLock)
            {
                pending = _activeCancels.ToArray();
                _activeCancels.Clear();
            }
            foreach (var cancel in pending)
            {
                try { cancel(); } catch { }
            }
        }

        /// <summary>Runs <paramref name="cancel"/> when Cancel() is (or already was) called.</summary>
        private void RegisterCancel(Action cancel)
        {
            bool runNow = false;
            lock (_cancelLock)
            {
                if (_cancelled) runNow = true;
                else _activeCancels.Add(cancel);
            }
            if (runNow)
            {
                try { cancel(); } catch { }
            }
        }

        /// <summary>Drops a finished client's cancel hook so long transfers do not
        /// accumulate dead delegates.</summary>
        private void UnregisterCancel(Action cancel)
        {
            lock (_cancelLock) { _activeCancels.Remove(cancel); }
        }

        public ConcurrentTransfer(string serverIp, int port, string filePath,
            int concurrency, bool isTcp, int srcPort = 0, int maxBytesPerSec = 0)
        {
            _serverIp = serverIp;
            _port = port;
            _filePath = filePath;
            _concurrency = Math.Max(1, Math.Min(8, concurrency));
            _isUdt = !isTcp;
            _srcPort = srcPort;
            _maxBytesPerSec = maxBytesPerSec;
        }

        public async Task SendAsync()
        {
            await SendAsync(null).ConfigureAwait(false);
        }

        /// <summary>Sends the file in parallel chunks. When chunkSession is provided, every
        /// chunk header carries it (against a 0x0A-capable peer), so a paused group can be
        /// resumed into exactly the gaps the server is missing (see <see cref="ResumeAsync"/>).
        /// Against an older peer the id is simply not sent — chunks flow as before.</summary>
        public async Task SendAsync(Guid? chunkSession)
        {
            var fileInfo = new FileInfo(_filePath);
            long totalSize = fileInfo.Length;
            string fileName = fileInfo.Name;

            if (totalSize == 0)
            {
                var errHandler = OnError;
                if (errHandler != null) errHandler("File is empty");
                return;
            }

            int chunks = Math.Min(_concurrency,
                (int)((totalSize + ChunkMinSize - 1) / ChunkMinSize));
            chunks = Math.Max(1, chunks);
            long chunkSize = (totalSize + chunks - 1) / chunks;
            _totalBytes = totalSize;

            Log(string.Format("Concurrent send: {0} in {1} chunks", fileName, chunks));

            var tasks = new List<Task>();

            for (int i = 0; i < chunks; i++)
            {
                if (_cancelled) break;
                long offset = i * chunkSize;
                long size = Math.Min(chunkSize, totalSize - offset);
                if (size <= 0) break;
                int localPort = FindLocalPort(i);
                var task = SendChunkAsync(offset, size, totalSize, localPort, chunkSession);
                tasks.Add(task);
            }

            try
            {
                await Task.WhenAll(tasks);
                if (_cancelled) return; // Cancel() reported the outcome; not a success
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
            catch (Exception ex)
            {
                if (_cancelled) return;
                var errHandler = OnError;
                if (errHandler != null) errHandler("Concurrent transfer failed: " + ex.Message);
            }
        }

        /// <summary>Continues a paused chunk group: asks the server (0x0B) which ranges it
        /// already holds and sends only the complement. A peer without chunk-group support
        /// (null coverage) falls back to re-sending everything — identical to the historic
        /// behaviour of a restarted concurrent send.</summary>
        public async Task ResumeAsync(Guid chunkSession)
        {
            long totalSize = new FileInfo(_filePath).Length;
            _totalBytes = totalSize;

            long[][] covered = null;
            try
            {
                covered = await QueryCoverage(chunkSession).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("Coverage query failed (" + ex.Message + ") — re-sending everything");
            }

            // Build the gaps: complement of the covered ranges within [0, totalSize)
            var holes = new List<long[]>();
            long cursor = 0;
            if (covered != null)
            {
                foreach (var r in covered)
                {
                    if (r[0] > cursor) holes.Add(new long[] { cursor, Math.Min(r[0], totalSize) });
                    if (r[1] > cursor) cursor = r[1];
                    if (cursor >= totalSize) break;
                }
            }
            if (cursor < totalSize) holes.Add(new long[] { cursor, totalSize });

            // Split big holes so the send stays parallel
            long maxPiece = Math.Max(ChunkMinSize, totalSize / _concurrency);
            var pieces = new List<long[]>();
            foreach (var hole in holes)
            {
                long start = hole[0];
                while (start < hole[1])
                {
                    long size = Math.Min(maxPiece, hole[1] - start);
                    pieces.Add(new long[] { start, size });
                    start += size;
                }
            }

            Log(string.Format("Chunk resume: {0} gap(s), {1} piece(s) to send",
                holes.Count, pieces.Count));

            var semaphore = new SemaphoreSlim(_concurrency);
            var tasks = new List<Task>();
            int pieceIndex = 0;
            foreach (var piece in pieces)
            {
                if (_cancelled) break;
                var p = piece;
                // With no user source port, let the OS hand out distinct ephemeral ports:
                // concurrent FindFreePort probes can converge on one port and the losing
                // piece dies on a bind conflict (TOCTOU between probe and bind).
                int localPort = _srcPort > 0 ? FindLocalPort(pieceIndex++) : 0;
                tasks.Add(Task.Run(async delegate
                {
                    await semaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (_cancelled) return;
                        await SendChunkAsync(p[0], p[1], totalSize, localPort, chunkSession).ConfigureAwait(false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            try
            {
                await Task.WhenAll(tasks);
                if (_cancelled) return;
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
            catch (Exception ex)
            {
                if (_cancelled) return;
                var errHandler = OnError;
                if (errHandler != null) errHandler("Concurrent resume failed: " + ex.Message);
            }
        }

        private async Task<long[][]> QueryCoverage(Guid chunkSession)
        {
            if (_isUdt)
            {
                var client = new TransferUdtClient(_serverIp, _port, _filePath, 0, 4194304, 0);
                client.PairingCode = PairingCode;
                return await client.QueryChunkCoverageAsync(chunkSession).ConfigureAwait(false);
            }
            var tcp = new TransferClient(_serverIp, _port, _filePath, 0, 4194304, 0);
            tcp.PairingCode = PairingCode;
            return await tcp.QueryChunkCoverageAsync(chunkSession).ConfigureAwait(false);
        }

        public async Task SendFolderAsync()
        {
            if (!Directory.Exists(_filePath))
            {
                var errHandler = OnError;
                if (errHandler != null) errHandler("Folder not found: " + _filePath);
                return;
            }

            var files = Directory.GetFiles(_filePath, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                var errHandler = OnError;
                if (errHandler != null) errHandler("No files in folder");
                return;
            }

            // Pre-compute total size for accurate progress
            long folderTotal = 0;
            foreach (var f in files)
            {
                try { folderTotal += new FileInfo(f).Length; } catch { }
            }
            _totalBytes = folderTotal;
            Log(string.Format("Concurrent folder: {0} files ({1}), {2} parallel",
                files.Length, Utils.FormatSize(folderTotal), _concurrency));

            var semaphore = new SemaphoreSlim(_concurrency);
            var tasks = new List<Task>();

            for (int i = 0; i < files.Length; i++)
            {
                if (_cancelled) break;
                string file = files[i];
                long fileSize = 0;
                try { fileSize = new FileInfo(file).Length; } catch { }
                int localPort = FindLocalPort(i);
                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        if (_cancelled) return;
                        await SendFileAsync(file, localPort);
                        long p, n;
                        do {
                            p = _transferredBytes;
                            n = Math.Min(_totalBytes, p + fileSize);
                        } while (Interlocked.CompareExchange(ref _transferredBytes, n, p) != p);
                        ReportProgress(Path.GetFileName(file));
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                tasks.Add(task);
            }

            try
            {
                await Task.WhenAll(tasks);
                if (_cancelled) return;
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
            catch (Exception ex)
            {
                if (_cancelled) return;
                var errHandler = OnError;
                if (errHandler != null) errHandler("Concurrent folder transfer failed: " + ex.Message);
            }
        }

        private int PerConnectionLimit()
        {
            // Total limit is split evenly across connections (floor 1 KB/s when enabled)
            if (_maxBytesPerSec <= 0) return 0;
            return Math.Max(_maxBytesPerSec / _concurrency, 1024);
        }

        private async Task SendChunkAsync(long offset, long size, long totalSize,
            int localPort, Guid? chunkSession)
        {
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        int perConn = PerConnectionLimit();
                        if (_isUdt)
                        {
                            var client = new TransferUdtClient(_serverIp, _port, _filePath, localPort, 4194304, perConn);
                            client.PairingCode = PairingCode;
                            client.ChunkSessionId = chunkSession;
                            RegisterCancel(client.Cancel);
                            try { await client.SendChunkedAsync(offset, size, totalSize); }
                            finally { UnregisterCancel(client.Cancel); }
                        }
                        else
                        {
                            var client = new TransferClient(_serverIp, _port, _filePath, localPort, 4194304, perConn);
                            client.PairingCode = PairingCode;
                            client.ChunkSessionId = chunkSession;
                            RegisterCancel(client.Cancel);
                            try { await client.SendChunkedAsync(offset, size, totalSize); }
                            finally { UnregisterCancel(client.Cancel); }
                        }
                        break;
                    }
                    catch (PortBindException ex)
                    {
                        // A bind failure happens before any byte is sent — safe to retry on another port
                        if (attempt >= 3) throw;
                        localPort = NextBindRetryPort(ex);
                        if (localPort == 0) throw;
                        Log(string.Format("Bind port {0} busy (attempt {1}/3), retrying chunk offset={2} on port {3}",
                            ex.Port, attempt, offset, localPort));
                    }
                    catch (IOException ex)
                    {
                        // A dropped connection (broken/reset mid-chunk) is often
                        // transient — e.g. 8 simultaneous UDT streams on a loaded
                        // machine can starve the ACK path long enough for one
                        // connection to die. The retry re-sends the SAME chunk over a
                        // fresh connection, which is idempotent server-side: chunks
                        // are written at their declared offsets into the shared
                        // tracker, so the re-send overwrites the same region and
                        // completion is decided by byte coverage. A user cancellation
                        // is not a failure.
                        if (_cancelled || attempt >= 3) throw;
                        Log(string.Format("Chunk offset={0} lost its connection ({1}) — retry {2}/3",
                            offset, ex.Message, attempt + 1));
                    }
                }

                // Lock-free accumulation: add chunk size atomically, cap at _totalBytes
                long prev, next;
                do {
                    prev = _transferredBytes;
                    next = Math.Min(_totalBytes, prev + size);
                } while (Interlocked.CompareExchange(ref _transferredBytes, next, prev) != prev);
                ReportProgress(_filePath);
            }
            catch (Exception ex)
            {
                Log(string.Format("Chunk offset={0} failed: {1}", offset, ex.Message));
                throw;
            }
        }

        private async Task SendFileAsync(string filePath, int localPort)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    int perConn = PerConnectionLimit();
                    if (_isUdt)
                    {
                        var client = new TransferUdtClient(_serverIp, _port, filePath, localPort, 4194304, perConn);
                        client.PairingCode = PairingCode;
                        RegisterCancel(client.Cancel);
                        try { await client.SendAsync(); }
                        finally { UnregisterCancel(client.Cancel); }
                    }
                    else
                    {
                        var client = new TransferClient(_serverIp, _port, filePath, localPort, 4194304, perConn);
                        client.PairingCode = PairingCode;
                        RegisterCancel(client.Cancel);
                        try { await client.SendAsync(); }
                        finally { UnregisterCancel(client.Cancel); }
                    }
                    return;
                }
                catch (PortBindException ex)
                {
                    if (attempt >= 3) throw;
                    localPort = NextBindRetryPort(ex);
                    if (localPort == 0) throw;
                    Log(string.Format("Bind port {0} busy (attempt {1}/3), retrying {2} on port {3}",
                        ex.Port, attempt, Path.GetFileName(filePath), localPort));
                }
                catch (IOException)
                {
                    // Same transient-connection retry as chunks: a per-file connection
                    // that died mid-send gets one more try before failing the folder.
                    if (_cancelled || attempt >= 3) throw;
                    Log(string.Format("Connection lost sending {0} — retry {1}/3",
                        Path.GetFileName(filePath), attempt + 1));
                }
            }
        }

        /// <summary>Picks a replacement source port after a bind race (scan upward from the failed one).</summary>
        private int NextBindRetryPort(PortBindException ex)
        {
            return Utils.FindFreePort(ex.Port + 1, _isUdt);
        }

        private int FindLocalPort(int index)
        {
            int basePort = _srcPort > 0 ? _srcPort + index : _port + index + 1;
            if (basePort > 65535) basePort = 49152 + (basePort % 1024); // wrap to ephemeral range
            return Utils.FindFreePort(basePort, _isUdt);
        }

        private void ReportProgress(string displayName)
        {
            var progressHandler = OnProgress;
            if (progressHandler != null)
            {
                progressHandler(new TransferProgress
                {
                    BytesTransferred = _transferredBytes,
                    TotalBytes = _totalBytes,
                    SpeedBytesPerSecond = 0,
                    Elapsed = TimeSpan.Zero,
                    FileName = displayName
                });
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }
}
