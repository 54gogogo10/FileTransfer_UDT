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

        public event Action<string> OnLog;
        public event Action<TransferProgress> OnProgress;
        public event Action<string> OnError;
        public event Action OnTransferComplete;

        /// <summary>Pairing code forwarded to every chunk connection (0x05 auth frame).</summary>
        public string PairingCode { get; set; }

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
                long offset = i * chunkSize;
                long size = Math.Min(chunkSize, totalSize - offset);
                if (size <= 0) break;
                int localPort = FindLocalPort(i);
                var task = SendChunkAsync(offset, size, totalSize, localPort);
                tasks.Add(task);
            }

            try
            {
                await Task.WhenAll(tasks);
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
            catch (Exception ex)
            {
                var errHandler = OnError;
                if (errHandler != null) errHandler("Concurrent transfer failed: " + ex.Message);
            }
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
                string file = files[i];
                long fileSize = 0;
                try { fileSize = new FileInfo(file).Length; } catch { }
                int localPort = FindLocalPort(i);
                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
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
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
            catch (Exception ex)
            {
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
            int localPort)
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
                            await client.SendChunkedAsync(offset, size, totalSize);
                        }
                        else
                        {
                            var client = new TransferClient(_serverIp, _port, _filePath, localPort, 4194304, perConn);
                            client.PairingCode = PairingCode;
                            await client.SendChunkedAsync(offset, size, totalSize);
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
                        await client.SendAsync();
                    }
                    else
                    {
                        var client = new TransferClient(_serverIp, _port, filePath, localPort, 4194304, perConn);
                        client.PairingCode = PairingCode;
                        await client.SendAsync();
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
