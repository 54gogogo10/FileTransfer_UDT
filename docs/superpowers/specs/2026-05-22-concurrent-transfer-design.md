# Concurrent Multi-Connection Transfer Design

> **状态：已实现**（2026-05-29）。此文档为实现前的设计方案，供历史参考。实际实现细节可能与此处描述不同——以 CLAUDE.md 和当前代码为准。

## Summary

Add multi-connection concurrent file transfer to TrFileTransfer client. Single large files are split into equal chunks sent over N parallel connections. Folder transfers send up to N files in parallel (one per connection). Both TCP and UDP supported. Concurrency configurable 1-64.

## Protocol Changes

### TCP — New Type 0x02 (Chunked File)

```
[1 byte:  0x02]                // Chunked file marker
[8 bytes: Int64 totalFileSize] // Total file size (all chunks combined)
[8 bytes: Int64 chunkOffset]   // Byte offset of this chunk in the file
[8 bytes: Int64 chunkSize]     // Size of this chunk's data
[4 bytes: Int32 nameLen]       // File name length
[N bytes: UTF-8 fileName]      // Same file name across all chunks (used as aggregation key)
[M bytes: chunkData]           // chunkSize bytes of data
[32 bytes: SHA256]             // SHA256 of this chunk's data only
```

Types 0x00 (single file) and 0x01 (folder) unchanged.

### UDP — New HELLO transferType 0x02

HELLO body for chunked transfers:

```
[1 byte:  0x02]                // transferType
[8 bytes: Int64 totalFileSize]
[8 bytes: Int64 chunkOffset]
[8 bytes: Int64 chunkSize]
[2 bytes: Int16 nameLen]
[N bytes: UTF-8 fileName]
```

Each chunk goes through full HELLO→DATA→FIN→FIN_ACK cycle independently. Server treats each chunk session as a piece of the aggregate file.

## Server Changes

### ChunkTracker (new, Shared.cs)

```
class ChunkTracker
  - string FileName          // Aggregation key
  - long TotalSize           // Total file size
  - string SavePath          // Resolved save path with collision avoidance
  - FileStream WriteStream   // Pre-allocated (SetLength) file stream
  - long BytesReceived       // Cumulative received bytes
  - int TotalChunks          // Computed from first chunk: ceil(totalSize / chunkSize)
  - bool IsComplete => BytesReceived >= TotalSize
```

### TCP Server — HandleChunkedFile (new method, TransferServer.cs)

1. Read totalFileSize, chunkOffset, chunkSize, fileName from header
2. Lookup or create ChunkTracker by fileName in `_chunkTrackers` dictionary
3. First chunk: call `Utils.GetUniqueSavePath`, create FileStream with `SetLength(totalSize)`
4. Seek to chunkOffset, write chunkData, verify SHA256
5. Update BytesReceived
6. If IsComplete and not yet fired: fire `OnTransferComplete`, remove tracker, close stream

### UDP Server — TransferUdpSession

When HELLO transferType == 0x02: same ChunkTracker logic. Each chunk session independently writes its data at the correct offset. Completion fires when BytesReceived reaches TotalSize.

### Thread Safety

`_chunkTrackers` is a `ConcurrentDictionary<string, ChunkTracker>`. Individual tracker field updates are guarded by `lock(tracker)` since multiple chunks may write concurrently.

## Client Changes

### ConcurrentTransfer (new class)

Manages N parallel connections. Entry point for both single-file and folder concurrent sends.

```
ConcurrentTransfer(serverIp, port, filePath, concurrency, isUdp, isFolder)

SendAsync():
  - Single file: split into concurrency equal chunks
  - Launch concurrency Tasks, each calls SendChunkedAsync on its own client instance
  - Aggregate progress from all sub-tasks
  - All complete → fire OnTransferComplete
  - Any fail → fire OnError, cancel remaining

SendFolderAsync():
  - Scan folder, collect all files
  - SemaphoreSlim(concurrency) controls parallelism
  - Each file: acquire → create client → send (type 0x00) → release
  - Aggregate progress: completedCount / totalCount
```

### TransferClient — SendChunkedAsync (new method)

```
SendChunkedAsync(offset, chunkSize, totalSize):
  - Construct type 0x02 header with totalSize, offset, chunkSize, fileName
  - Read chunkSize bytes from file starting at offset
  - Send header + data + SHA256
  - Bind to specific local port via new TcpClient(localEndPoint)
```

### TransferUdpClient — SendChunkedAsync (new method)

```
SendChunkedAsync(offset, chunkSize, totalSize):
  - HELLO with transferType=0x02 body
  - Send chunkSize bytes via sliding window (existing SendUdpFileDataAsync reuse)
  - Bind to specific local port via new UdpClient(localPort)
```

### Source Port Allocation

```
localPort = basePort + index + 1
e.g. server port 8080 → concurrent ports 8081, 8082, ..., 8081+N
If port in use, scan upward for next free port.
```

### Progress Aggregation

Single file: sum of all sub-transfer bytes / total file size.
Folder: completed file count / total file count. Each file has its own progress card.

Events:
- `OnProgress(AggregatedProgress)` — aggregated across all sub-transfers
- `OnTransferComplete` — all sub-transfers succeeded
- `OnError` — any sub-transfer failed

## UI Changes

### Client Panel — NumericUpDown Concurrency

- `_numConcurrency`: NumericUpDown, Min=1, Max=64, Value=1, Width=45
- Label: "并发:" / "Concurrent:" (L10N)
- Position: right of protocol radio buttons, left of Send button
- Disabled during transfer, re-enabled in ResetClientUI

### Progress Cards

- Single file chunked: one aggregated card showing total progress + "[N并发]" suffix
- Folder parallel: one card per file (existing behavior) + overall status label
- Folder status label: "已完成: X/Y 文件" / "Completed: X/Y files"

### Config Persistence

- `Config.SetInt("Concurrency", value)` / `Config.GetInt("Concurrency", 1)`

## Edge Cases

1. **Chunk size mismatch**: last chunk may be smaller than others. Server uses chunkSize from header, not computed size.
2. **Connection failure mid-chunk**: that chunk's OnError fires, ConcurrentTransfer cancels remaining, overall OnError fires.
3. **Server partial file on disk from failed transfer**: next transfer of same file overwrites (FileMode.Create on first chunk).
4. **File collision**: Utils.GetUniqueSavePath handles _1, _2 suffix. Applies on first chunk tracker creation.
5. **Source port conflict**: port allocator scans upward for free port. If none found in basePort..basePort+128, falls back to OS-assigned ephemeral port.
6. **UDP chunk sessions**: each chunk is a separate TransferUdpSession. HELLO from same clientEp (different source port) creates distinct sessions. ChunkTracker aggregates them by fileName.

## Testing

- Unit: ChunkTracker completion logic, port allocation
- Integration: TCP chunked single file (2- and 4-way), UDP chunked single file (2-way)
- Existing 46 tests must continue to pass
