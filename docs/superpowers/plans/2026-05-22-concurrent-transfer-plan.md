# Concurrent Multi-Connection Transfer Implementation Plan

> **状态：已完成**（2026-05-29）。所有任务已实现并合并到 master 分支。CLAUDE.md 中的 @superpowers 元数据不准确：9 个源文件、UDT 替代 UDP、ConcurrentTransfer 已实现。无需重新实现。

> **For agentic workers (historical):** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add multi-connection concurrent file transfer — single files split into N chunks sent in parallel, folders send N files in parallel. TCP+UDP, concurrency 1-64.

**Architecture:** New `ChunkTracker` class on server aggregates chunks by filename. New `ConcurrentTransfer` class on client orchestrates N parallel `TransferClient`/`TransferUdpClient` instances, each bound to a different source port. New protocol type 0x02 carries chunk offset/size metadata. Server-side `_chunkTrackers` ConcurrentDictionary provides thread-safe chunk aggregation.

**Tech Stack:** C# 5, .NET Framework 4.5+, WinForms, csc.exe only (no NuGet)

---

### Task 0: Foundation — ChunkTracker + Source Port Helper

**Files:**
- Create: None (modifying existing)
- Modify: `TrFileTransfer/Shared.cs`
- Test: Existing 46 tests must pass

- [ ] **Step 1: Add ChunkTracker class and Utils.FindFreePort to Shared.cs**

Add after the `FileEntry` struct (before `Utils` class):

```csharp
    /// <summary>Tracks received chunks for concurrent file reassembly.</summary>
    public class ChunkTracker
    {
        public string FileName;
        public long TotalSize;
        public string SavePath;
        public FileStream WriteStream;
        public long BytesReceived;
        public int ChunksCompleted;
        public readonly object Lock = new object();
        public bool Complete;

        public void Dispose()
        {
            Complete = true;
            try { if (WriteStream != null) { WriteStream.Dispose(); WriteStream = null; } } catch { }
        }
    }
```

Add to `Utils` class (after `GetUniqueSavePath`):

```csharp
        /// <summary>Finds a free TCP port starting from basePort, scanning upward.</summary>
        public static int FindFreePort(int basePort)
        {
            for (int port = basePort; port < basePort + 128; port++)
            {
                try
                {
                    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch { }
            }
            return 0; // fallback: OS-assigned ephemeral port
        }
```

- [ ] **Step 2: Add `using System.Net.Sockets;` to Shared.cs**

Add at top with other usings:

```csharp
using System.Net.Sockets;
```

- [ ] **Step 3: Build main project, run tests**

```
cd TrFileTransfer && build.bat
cd Tests && build.bat && TrFileTransfer.Tests.exe
```

Expected: Build success, 46/46 pass.

- [ ] **Step 4: Commit**

```bash
git add TrFileTransfer/Shared.cs
git commit -m "添加 ChunkTracker 类 + Utils.FindFreePort 辅助方法"
```

---

### Task 1: TCP Server — HandleChunkedFile

**Files:**
- Modify: `TrFileTransfer/TransferServer.cs`
- Test: Existing integration tests must pass

- [ ] **Step 1: Add `_chunkTrackers` field and chunked handler dispatch**

Add field near other private fields (around line 14):

```csharp
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ChunkTracker> _chunkTrackers
            = new System.Collections.Concurrent.ConcurrentDictionary<string, ChunkTracker>();
```

Add `using System.Collections.Concurrent;` at top if not present.

- [ ] **Step 2: Add dispatch for type 0x02 in HandleClient**

In `HandleClient`, after reading `transferType`, add the 0x02 case:

```csharp
                    if (transferType == 0x01)
                    {
                        await HandleFolderTransfer(stream, ct);
                    }
                    else if (transferType == 0x02)
                    {
                        await HandleChunkedFile(stream, ct);
                    }
                    else
                    {
                        await HandleFileTransfer(stream, ct);
                    }
```

- [ ] **Step 3: Add HandleChunkedFile method**

Add new method after `HandleFileTransfer`:

```csharp
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

            // Get or create chunk tracker
            ChunkTracker tracker = _chunkTrackers.GetOrAdd(fileName, key =>
            {
                var t = new ChunkTracker
                {
                    FileName = key,
                    TotalSize = totalSize,
                    SavePath = Utils.GetUniqueSavePath(_saveDirectory, key)
                };
                t.WriteStream = new FileStream(t.SavePath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.RandomAccess);
                t.WriteStream.SetLength(totalSize);
                return t;
            });

            // Write chunk at correct offset
            byte[] chunkData = null;
            bool hashOk = false;
            try
            {
                chunkData = new byte[chunkSize];
                await ReadExactAsync(stream, chunkData, 0, (int)chunkSize, ct);

                var receivedHash = new byte[32];
                await ReadExactAsync(stream, receivedHash, 0, 32, ct);

                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    var computedHash = sha256.ComputeHash(chunkData);
                    hashOk = Utils.ConstantTimeEquals(receivedHash, computedHash);
                }

                if (hashOk)
                {
                    lock (tracker.Lock)
                    {
                        tracker.WriteStream.Seek(chunkOffset, SeekOrigin.Begin);
                        tracker.WriteStream.Write(chunkData, 0, (int)chunkSize);
                        tracker.BytesReceived += chunkSize;
                        tracker.ChunksCompleted++;
                    }

                    Log(string.Format("Chunk OK: {0} offset={1} size={2} [{3}/{4}]",
                        fileName, chunkOffset, Utils.FormatSize(chunkSize),
                        tracker.ChunksCompleted, (tracker.TotalSize + chunkSize - 1) / chunkSize));

                    // Check completion
                    bool isComplete = false;
                    lock (tracker.Lock)
                    {
                        if (tracker.BytesReceived >= tracker.TotalSize && !tracker.Complete)
                        {
                            tracker.Complete = true;
                            isComplete = true;
                        }
                    }

                    if (isComplete)
                    {
                        tracker.Dispose();
                        ChunkTracker removed;
                        _chunkTrackers.TryRemove(fileName, out removed);
                        Log(L.S_TransferDone(fileName, Utils.FormatSize(totalSize), 0, 0));
                        var completeHandler = OnTransferComplete;
                        if (completeHandler != null) completeHandler();
                    }
                }
                else
                {
                    Log(L.S_HashFailed(fileName));
                    var errHandler = OnError;
                    if (errHandler != null) errHandler(L.S_HashFailed(fileName));
                }
            }
            catch (Exception ex)
            {
                if (!(ex is OperationCanceledException || ex is ObjectDisposedException))
                {
                    Log(L.S_UnexpectedError(ex.Message));
                    var errHandler = OnError;
                    if (errHandler != null) errHandler(ex.Message);
                }
            }
        }
```

- [ ] **Step 4: Build and test**

```
cd TrFileTransfer && build.bat
cd Tests && build.bat && TrFileTransfer.Tests.exe
```

Expected: Build success, 46/46 pass.

- [ ] **Step 5: Commit**

```bash
git add TrFileTransfer/TransferServer.cs
git commit -m "TCP服务端新增 HandleChunkedFile 支持分块文件接收与重组"
```

---

### Task 2: TCP Client — SendChunkedAsync

**Files:**
- Modify: `TrFileTransfer/TransferClient.cs`
- Test: Existing integration tests must pass

- [ ] **Step 1: Add constructor overload with local port**

Add after the existing constructor:

```csharp
        /// <summary>Creates a TCP client bound to a specific local port.</summary>
        public TransferClient(string serverIp, int port, string filePath, int localPort, int bufferSize = 1024 * 1024)
            : this(serverIp, port, filePath, bufferSize)
        {
            _localPort = localPort;
        }
```

Add field:

```csharp
        private readonly int _localPort;
```

- [ ] **Step 2: Add SendChunkedAsync method**

Add after `SendFolderAsync`:

```csharp
        /// <summary>Sends a chunk of a file (type 0x02) for concurrent transfer.</summary>
        public async Task SendChunkedAsync(long offset, long chunkSize, long totalSize)
        {
            await RunTransfer(ct => SendChunkedInternal(offset, chunkSize, totalSize, ct));
        }
```

- [ ] **Step 3: Add SendChunkedInternal method**

Add after `SendFolderInternal`:

```csharp
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

                var fileInfo = new FileInfo(_filePath);
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
                await stream.WriteAsync(header, 0, header.Length, ct);

                Log(string.Format("Chunk sending: {0} offset={1} size={2}",
                    fileName, offset, Utils.FormatSize(chunkSize)));

                await SendFilePayload(stream, _filePath, chunkSize, fileName, ct, (int)offset);
            }
        }
```

- [ ] **Step 4: Modify SendFilePayload to support offset**

Change signature from `SendFilePayload(NetworkStream stream, string filePath, long fileSize, string displayName, CancellationToken ct)` to:

```csharp
        private async Task SendFilePayload(NetworkStream stream, string filePath, long fileSize,
            string displayName, CancellationToken ct, int fileOffset = 0)
```

In the method body, change `fileStream.ReadAsync(bufA, 0, bufA.Length, ct)` to seek first:

After `using (var fileStream = new FileStream(...))` add:

```csharp
                if (fileOffset > 0)
                    fileStream.Seek(fileOffset, SeekOrigin.Begin);
```

- [ ] **Step 5: Build and test**

```
cd TrFileTransfer && build.bat
cd Tests && build.bat && TrFileTransfer.Tests.exe
```

Expected: Build success, 46/46 pass.

- [ ] **Step 6: Commit**

```bash
git add TrFileTransfer/TransferClient.cs
git commit -m "TCP客户端新增 SendChunkedAsync + 源端口绑定 + SendFilePayload偏移支持"
```

---

### Task 3: UDP Client — SendChunkedAsync + Source Port

**Files:**
- Modify: `TrFileTransfer/TransferUdpClient.cs`
- Test: Existing integration tests must pass

- [ ] **Step 1: Add local port field and constructor**

Add field:

```csharp
        private readonly int _localPort;
```

Modify existing constructor to chain, add new overload:

```csharp
        public TransferUdpClient(string serverIp, int port, string filePath, int windowSize = UdpProtocol.DefaultWindowSize)
            : this(serverIp, port, filePath, 0, windowSize) { }

        public TransferUdpClient(string serverIp, int port, string filePath, int localPort, int windowSize = UdpProtocol.DefaultWindowSize)
        {
            _serverIp = serverIp;
            _port = port;
            _filePath = filePath;
            _localPort = localPort;
            _windowSize = windowSize;
        }
```

- [ ] **Step 2: Add SendChunkedAsync method**

```csharp
        public async Task SendChunkedAsync(long offset, long chunkSize, long totalSize)
        {
            await RunUdpTransfer(ct => SendChunkedUdpInternal(offset, chunkSize, totalSize, ct));
        }
```

- [ ] **Step 3: Add SendChunkedUdpInternal and modify CreateUdpClient**

Add method:

```csharp
        private async Task SendChunkedUdpInternal(long offset, long chunkSize, long totalSize, CancellationToken ct)
        {
            var udp = CreateUdpClient();
            var serverEp = new IPEndPoint(IPAddress.Parse(_serverIp), _port);

            var fileInfo = new FileInfo(_filePath);
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(fileInfo.Name);

            // Build HELLO body: transferType(1) + totalSize(8) + chunkOffset(8) + chunkSize(8) + nameLen(2) + name
            var body = new byte[1 + 8 + 8 + 8 + 2 + nameBytes.Length];
            body[0] = 0x02;
            Buffer.BlockCopy(BitConverter.GetBytes(totalSize), 0, body, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(offset), 0, body, 9, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(chunkSize), 0, body, 17, 8);
            Buffer.BlockCopy(BitConverter.GetBytes((short)nameBytes.Length), 0, body, 25, 2);
            Buffer.BlockCopy(nameBytes, 0, body, 27, nameBytes.Length);

            var hello = UdpProtocol.BuildPacket(UdpProtocol.TypeHello, 0, body);
            await udp.SendAsync(hello, hello.Length, serverEp);

            bool ok = await SendUdpFileDataAsync(udp, serverEp, _filePath, chunkSize,
                fileInfo.Name, ct, true, (int)offset);
            if (ok)
            {
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
        }
```

Modify `SendUdpFileDataAsync` signature to accept optional offset:

```csharp
        private async Task<bool> SendUdpFileDataAsync(UdpClient udp, IPEndPoint serverEp, string filePath,
            long fileSize, string displayName, CancellationToken ct, bool reportProgress, int fileOffset = 0)
```

In the method, after creating `fs = new FileStream(...)`, add:

```csharp
                if (fileOffset > 0)
                    fs.Seek(fileOffset, SeekOrigin.Begin);
```

Modify `CreateUdpClient` to use local port:

```csharp
        private UdpClient CreateUdpClient()
        {
            var udp = _localPort > 0
                ? new UdpClient(_localPort)
                : new UdpClient();
            udp.Client.SendBufferSize = 4 * 1024 * 1024;
            udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
            udp.EnableBroadcast = false;
            return udp;
        }
```

- [ ] **Step 4: Build and test**

```
cd TrFileTransfer && build.bat
cd Tests && build.bat && TrFileTransfer.Tests.exe
```

Expected: Build success, 46/46 pass.

- [ ] **Step 5: Commit**

```bash
git add TrFileTransfer/TransferUdpClient.cs
git commit -m "UDP客户端新增 SendChunkedAsync + 源端口绑定"
```

---

### Task 4: UDP Server — Chunked HELLO Handling

**Files:**
- Modify: `TrFileTransfer/TransferUdpSession.cs`
- Test: Existing integration tests must pass

- [ ] **Step 1: Add ChunkTracker support to TransferUdpSession**

Add using at top:

```csharp
using System.Collections.Concurrent;
```

Add static ChunkTracker dictionary (shared across all sessions):

```csharp
        private static readonly ConcurrentDictionary<string, ChunkTracker> _chunkTrackers
            = new ConcurrentDictionary<string, ChunkTracker>();
```

- [ ] **Step 2: Handle transferType 0x02 in RunAsync**

In `RunAsync`, after parsing the HELLO body for transferType 0x00 and 0x01, add 0x02 handling. Find the block that reads `byte transferType = helloPacket[UdpProtocol.HeaderSize];` and the subsequent parsing. After the existing 0x01 handling, add:

```csharp
                    else if (transferType == 0x02)
                    {
                        // Chunked file: totalSize(8) + chunkOffset(8) + chunkSize(8) + nameLen(2) + name
                        totalFileSize = BitConverter.ToInt64(helloPacket, UdpProtocol.HeaderSize + 1);
                        long chunkOffset = BitConverter.ToInt64(helloPacket, UdpProtocol.HeaderSize + 1 + 8);
                        long chunkSize = BitConverter.ToInt64(helloPacket, UdpProtocol.HeaderSize + 1 + 8 + 8);
                        int nameLen = BitConverter.ToInt16(helloPacket, UdpProtocol.HeaderSize + 1 + 8 + 8 + 8);
                        fileName = System.Text.Encoding.UTF8.GetString(helloPacket, UdpProtocol.HeaderSize + 1 + 8 + 8 + 8 + 2, nameLen);
                        fileName = Path.GetFileName(fileName);
                        isChunked = true;
                        _chunkOffset = chunkOffset;
                        _chunkSize = chunkSize;
                        _totalFileSize = totalFileSize;
                    }
```

- [ ] **Step 3: Add chunked fields and modify completion logic**

Add fields to TransferUdpSession:

```csharp
        private bool _isChunked;
        private long _chunkOffset;
        private long _chunkSize;
        private long _totalFileSize;
```

In the FIN handling area (where hash verification succeeds), add chunked file logic. After sending FIN_ACK, add:

```csharp
                            if (_isChunked)
                            {
                                var tracker = _chunkTrackers.GetOrAdd(fileName, key =>
                                {
                                    var t = new ChunkTracker
                                    {
                                        FileName = key,
                                        TotalSize = _totalFileSize,
                                        SavePath = Utils.GetUniqueSavePath(_saveDirectory, key)
                                    };
                                    t.WriteStream = new FileStream(t.SavePath, FileMode.Create, FileAccess.Write,
                                        FileShare.None, 4096, FileOptions.RandomAccess);
                                    t.WriteStream.SetLength(_totalFileSize);
                                    return t;
                                });

                                lock (tracker.Lock)
                                {
                                    tracker.WriteStream.Seek(_chunkOffset, SeekOrigin.Begin);
                                    tracker.WriteStream.Write(writeBuf, 0, (int)_chunkSize);
                                    tracker.BytesReceived += _chunkSize;
                                    tracker.ChunksCompleted++;
                                }

                                bool isComplete = false;
                                lock (tracker.Lock)
                                {
                                    if (tracker.BytesReceived >= tracker.TotalSize && !tracker.Complete)
                                    {
                                        tracker.Complete = true;
                                        isComplete = true;
                                    }
                                }

                                if (isComplete)
                                {
                                    tracker.Dispose();
                                    ChunkTracker removed;
                                    _chunkTrackers.TryRemove(fileName, out removed);
                                }

                                fireTransferComplete = isComplete || !_isChunked;
                            }
```

For the `fireTransferComplete` variable: declare `bool fireTransferComplete = false;` before FIN handling. For non-chunked transfers, `fireTransferComplete = true;` when hash passes. For chunked, only fire when the whole file is complete.

- [ ] **Step 4: Build and test**

```
cd TrFileTransfer && build.bat
cd Tests && build.bat && TrFileTransfer.Tests.exe
```

Expected: Build success, 46/46 pass.

- [ ] **Step 5: Commit**

```bash
git add TrFileTransfer/TransferUdpSession.cs
git commit -m "UDP Session 新增 transferType 0x02 分块文件支持 + ChunkTracker 聚合"
```

---

### Task 5: ConcurrentTransfer Orchestrator

**Files:**
- Create: `TrFileTransfer/ConcurrentTransfer.cs`
- Modify: `TrFileTransfer/Tests/build.bat`
- Test: New integration tests

- [ ] **Step 1: Create ConcurrentTransfer.cs**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    public class ConcurrentTransfer
    {
        private readonly string _serverIp;
        private readonly int _port;
        private readonly string _filePath;
        private readonly int _concurrency;
        private readonly bool _isUdp;

        private long _totalBytes;
        private long _transferredBytes;
        private int _completedCount;
        private int _totalCount;
        private readonly object _progressLock = new object();

        public event Action<string> OnLog;
        public event Action<TransferProgress> OnProgress;
        public event Action<string> OnError;
        public event Action OnTransferComplete;

        public ConcurrentTransfer(string serverIp, int port, string filePath,
            int concurrency, bool isUdp)
        {
            _serverIp = serverIp;
            _port = port;
            _filePath = filePath;
            _concurrency = Math.Max(1, Math.Min(64, concurrency));
            _isUdp = isUdp;
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

            int chunks = (int)Math.Min(_concurrency,
                (totalSize + UdpProtocol.MaxChunkSize - 1) / UdpProtocol.MaxChunkSize);
            chunks = Math.Max(1, chunks);
            long chunkSize = (totalSize + chunks - 1) / chunks;
            _totalBytes = totalSize;
            _totalCount = chunks;

            Log(string.Format("Concurrent send: {0} in {1} chunks of ~{2}",
                fileName, chunks, Utils.FormatSize(chunkSize)));

            var tasks = new List<Task>();
            var cts = new CancellationTokenSource();

            for (int i = 0; i < chunks; i++)
            {
                long offset = i * chunkSize;
                long size = Math.Min(chunkSize, totalSize - offset);
                if (size <= 0) break;

                int localPort = FindLocalPort(i);
                int index = i;
                var task = SendChunkAsync(offset, size, totalSize, localPort, index, cts.Token);
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
                cts.Cancel();
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

            _totalCount = files.Length;
            _totalBytes = files.Length;
            Log(string.Format("Concurrent folder send: {0} files, {1} parallel",
                files.Length, _concurrency));

            var semaphore = new SemaphoreSlim(_concurrency);
            var tasks = new List<Task>();
            var cts = new CancellationTokenSource();

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                int localPort = FindLocalPort(i);
                int index = i;
                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cts.Token);
                    try
                    {
                        await SendFileAsync(file, localPort, cts.Token);
                        lock (_progressLock)
                        {
                            _completedCount++;
                            _transferredBytes = _completedCount;
                            ReportProgress(file);
                        }
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
                cts.Cancel();
                var errHandler = OnError;
                if (errHandler != null) errHandler("Concurrent folder transfer failed: " + ex.Message);
            }
        }

        private async Task SendChunkAsync(long offset, long size, long totalSize,
            int localPort, int index, CancellationToken ct)
        {
            if (_isUdp)
            {
                var client = new TransferUdpClient(_serverIp, _port, _filePath, localPort);
                client.OnError += msg => Log(string.Format("Chunk {0} error: {1}", index, msg));
                await client.SendChunkedAsync(offset, size, totalSize);
            }
            else
            {
                var client = new TransferClient(_serverIp, _port, _filePath, localPort);
                client.OnError += msg => Log(string.Format("Chunk {0} error: {1}", index, msg));
                await client.SendChunkedAsync(offset, size, totalSize);
            }

            lock (_progressLock)
            {
                _transferredBytes += size;
                _completedCount++;
                ReportProgress(_filePath);
            }
        }

        private async Task SendFileAsync(string filePath, int localPort, CancellationToken ct)
        {
            string fileName = Path.GetFileName(filePath);
            if (_isUdp)
            {
                var client = new TransferUdpClient(_serverIp, _port, filePath, localPort);
                client.OnError += msg => Log(string.Format("{0}: {1}", fileName, msg));
                await client.SendAsync();
            }
            else
            {
                var client = new TransferClient(_serverIp, _port, filePath, localPort);
                client.OnError += msg => Log(string.Format("{0}: {1}", fileName, msg));
                await client.SendAsync();
            }
        }

        private int FindLocalPort(int index)
        {
            int basePort = _port + index + 1;
            return Utils.FindFreePort(basePort);
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
            var handler = OnLog;
            if (handler != null) handler(string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, msg));
        }
    }
}
```

- [ ] **Step 2: Add ConcurrentTransfer.cs to test build**

Edit `TrFileTransfer/Tests/build.bat`, add `..\ConcurrentTransfer.cs` to the file list (before `TestProgram.cs`).

- [ ] **Step 3: Build and run tests**

```
cd TrFileTransfer && build.bat
cd Tests && build.bat && TrFileTransfer.Tests.exe
```

Expected: Build success, 46/46 pass (new class compiles but no new tests yet).

- [ ] **Step 4: Commit**

```bash
git add TrFileTransfer/ConcurrentTransfer.cs TrFileTransfer/Tests/build.bat
git commit -m "新增 ConcurrentTransfer 并发传输编排类"
```

---

### Task 6: UI — NumericUpDown + Wire Up ConcurrentTransfer

**Files:**
- Modify: `TrFileTransfer/MainForm.cs`
- Modify: `TrFileTransfer/L10N.cs`
- Test: Existing 46 tests must pass

- [ ] **Step 1: Add NumericUpDown and label to client panel (InitializeComponent)**

Add field declarations near other client controls:

```csharp
        private NumericUpDown _numConcurrency;
```

In `InitializeComponent`, after creating `_rbClientUdp`, add:

```csharp
            _numConcurrency = new NumericUpDown
            {
                Location = new Point(455, 22), Width = 45,
                Minimum = 1, Maximum = 64, Value = 1
            };
```

Add to client panel controls:

```csharp
            _gbClient.Controls.Add(_numConcurrency);
```

Adjust BtnSend position since NumericUpDown now occupies its spot. Move BtnSend down or adjust layout:
- Keep BtnSend at its current position (455, 20) — NumericUpDown goes to its right or below.
- Actually, place NumericUpDown to the LEFT of BtnSend. Move BtnSend from X=455 to X=510, Width=65.
- Or place NumericUpDown above BtnSend on row 0 at X=455 Y=20.

Let's adjust: NumericUpDown at (450, 22, Width=45), BtnSend at (500, 20, Width=105, Height=30).

- [ ] **Step 2: Update BtnSend_Click to use ConcurrentTransfer**

Replace the `if (_rbClientTcp.Checked)` block:

```csharp
            int concurrency = (int)_numConcurrency.Value;
            if (concurrency > 1 && !isMonitor)
            {
                var ct = new ConcurrentTransfer(ip, port, path, concurrency, !_rbClientTcp.Checked);
                WireConcurrentEvents(ct);
                if (isFolder)
                    await ct.SendFolderAsync();
                else
                    await ct.SendAsync();
            }
            else if (_rbClientTcp.Checked)
            {
                // existing single-connection code...
            }
            else
            {
                // existing single-connection code...
            }
```

- [ ] **Step 3: Add WireConcurrentEvents helper**

```csharp
        private void WireConcurrentEvents(ConcurrentTransfer ct)
        {
            var card = CreateTransferCard(_progressPanelC);
            ct.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
            ct.OnProgress += p => this.Invoke((Action)(() => UpdateCardProgress(card, p)));
            ct.OnError += msg => this.Invoke((Action)(() =>
            {
                AddLog(L.ErrorPrefix + msg);
                ResetClientUI();
                UpdateCardComplete(card);
            }));
            ct.OnTransferComplete += () => this.Invoke((Action)(() =>
            {
                ResetClientUI();
                UpdateCardComplete(card);
            }));
        }
```

- [ ] **Step 4: Add L10N strings**

In `L10N.cs`, add:

```csharp
        public static string ConcurrencyLabel { get { return IsChinese ? "并发:" : "Concur:"; } }
```

- [ ] **Step 5: Update ApplyLanguage to set NumericUpDown label**

Not needed if using a separate label control. Add a Label `_lblConcurrency` next to NumericUpDown and set its text in `ApplyLanguage`.

Simpler: set a tooltip or leave it as bare control. For now, just set the label in InitializeComponent and ApplyLanguage.

Actually, let's keep it simple — just place the NumericUpDown without a separate label, or use a very short label.

- [ ] **Step 6: Update ApplyConfig and SaveConfig**

In `ApplyConfig`:
```csharp
            _numConcurrency.Value = Config.GetInt("Concurrency", 1);
```

In `SaveConfig`:
```csharp
            Config.SetInt("Concurrency", (int)_numConcurrency.Value);
```

- [ ] **Step 7: Update DisableClientInputs and ResetClientUI**

In `DisableClientInputs`, add:
```csharp
            _numConcurrency.Enabled = false;
```

In `ResetClientUI`, add (after `_rbClientUdp.Enabled = true;`):
```csharp
            _numConcurrency.Enabled = true;
```

- [ ] **Step 8: Build and test**

```
cd TrFileTransfer && build.bat
cd Tests && build.bat && TrFileTransfer.Tests.exe
```

Expected: Build success, 46/46 pass.

- [ ] **Step 9: Commit**

```bash
git add TrFileTransfer/MainForm.cs TrFileTransfer/L10N.cs
git commit -m "UI新增并发数选择器 + 接入ConcurrentTransfer + L10N"
```

---

### Task 7: Integration Tests for Concurrent Transfer

**Files:**
- Modify: `TrFileTransfer/Tests/IntegrationTests.cs`
- Test: New concurrent integration tests

- [ ] **Step 1: Add TCP chunked concurrent test**

Add to `IntegrationTests` class:

```csharp
        private static void TcpChunkedConcurrent()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(Path.GetTempPath(), "tr_it_csend_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(Path.GetTempPath(), "tr_it_crecv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server = null;
            try
            {
                // Create a ~200KB test file (big enough to split)
                var testFile = Path.Combine(sendDir, "bigfile.bin");
                var rng = new Random(42);
                var content = new byte[1024 * 200];
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;

                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                server.OnError += msg => serverDone.Set();
                server.Start();

                if (!serverStarted.WaitOne(5000))
                    throw new Exception("Server did not start");

                // Send with concurrency 3
                var ct = new ConcurrentTransfer("127.0.0.1", port, testFile, 3, false);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                ct.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                ct.OnError += msg => clientDone.Set();

                var sendTask = ct.SendAsync();

                if (!serverDone.WaitOne(60000))
                    throw new Exception("Server did not complete within 60s");
                if (!serverOk)
                    throw new Exception("Server transfer failed");

                sendTask.Wait(60000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("Client did not fire completion");
                if (!clientOk)
                    throw new Exception("Concurrent transfer failed");

                Thread.Sleep(500);

                var receivedFile = Path.Combine(recvDir, "bigfile.bin");
                Assert.True(File.Exists(receivedFile), "received file exists");
                var receivedContent = File.ReadAllBytes(receivedFile);
                Assert.Equal(content.Length, receivedContent.Length, "file size matches");
                Assert.True(Utils.ConstantTimeEquals(content, receivedContent), "content match");
            }
            finally
            {
                try { if (server != null) server.Stop(); } catch { }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }
```

- [ ] **Step 2: Register new test in RunAll**

```csharp
            runner.Run("Integration_TCP_ChunkedConcurrent", TcpChunkedConcurrent);
```

- [ ] **Step 3: Build and run tests**

```
cd Tests && build.bat && TrFileTransfer.Tests.exe
```

Expected: 47/47 pass.

- [ ] **Step 4: Commit**

```bash
git add TrFileTransfer/Tests/IntegrationTests.cs
git commit -m "新增 TCP 分块并发集成测试 (3路并发 200KB 文件)"
```

---

### Task 8: Final Verification

- [ ] **Step 1: Run full test suite**

```
cd TrFileTransfer && build.bat
cd Tests && build.bat && TrFileTransfer.Tests.exe
```

Expected: All tests pass (47/47 — 43 unit + 4 integration).

- [ ] **Step 2: Verify C# 5 compliance**

Check for forbidden syntax in all modified files:
- No `$` string interpolation
- No `?.` null-conditional
- No expression-bodied members (`=>` for properties)
- No `when` exception filters
- No `nameof`

- [ ] **Step 3: Commit any remaining changes**

```bash
git status
git add -A  # if any stragglers
git commit -m "并发传输最终验证：全测试通过 + C# 5合规"
```
