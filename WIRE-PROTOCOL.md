# TrFileTransfer — 线协议规范

## TCP 协议

单文件和文件夹传输都以单个类型字节开始。

### 单文件（类型 `0x00`）

```
偏移  大小  字段
----  ----  -----
0     1     类型 = 0x00
1     8     文件大小，Int64 小端序
9     4     文件名长度（字节），Int32 小端序
13    N     文件名，UTF-8
13+N  M     文件内容（fileSize 字节）
13+N+M 32  文件内容 SHA-256 哈希
```

### 文件夹（类型 `0x01`）

```
偏移  大小  字段
----  ----  -----
0     1     类型 = 0x01
1     2     文件夹名长度（字节），Int16 小端序
3     N     文件夹名，UTF-8
3+N   4     文件数量，Int32 小端序
```

紧接着是每个文件的条目：

```
偏移  大小  字段
----  ----  -----
0     8     文件大小，Int64 小端序
8     2     相对路径长度（字节），Int16 小端序
10    N     相对路径，UTF-8（如 "subdir/file.txt"）
10+N  M     文件内容（fileSize 字节）
10+N+M 32  此文件内容 SHA-256 哈希
```

所有多字节整数均为小端序。

### 分块文件（类型 `0x02`）

用于并发传输——将单个大文件拆分为多个等大分块，每个分块通过独立连接并行发送。服务端通过 `ChunkTracker` 按文件名聚合，写入预分配文件（`SetLength`）的对应偏移位置。

```
[1 byte:  0x02]                // 分块文件标记
[8 bytes: Int64 totalFileSize] // 文件总大小（所有分块合计）
[8 bytes: Int64 chunkOffset]   // 此分块在文件中的字节偏移
[8 bytes: Int64 chunkSize]     // 此分块数据大小
[4 bytes: Int32 nameLen]       // 文件名长度
[N bytes: UTF-8 fileName]      // 文件名（所有分块使用相同文件名，作为聚合键）
[M bytes: chunkData]           // chunkSize 字节的数据
[32 bytes: SHA256]             // 此分块数据的 SHA-256
```

每个分块独立发送头部+数据+哈希。服务端收到首个分块时创建 `ChunkTracker`：
- 调用 `Utils.GetUniqueSavePath` 解析保存路径（处理名称冲突）
- 创建 `FileStream`，调用 `SetLength(totalSize)` 预分配
- 后续分块 Seek 到对应偏移写入，`BytesReceived` 累加
- `BytesReceived >= TotalSize` 时文件完成，触发 `OnTransferComplete`，释放 tracker

末块可能小于其他块——服务端以头部 `chunkSize` 为准，不自行计算。

### 断点续传（类型 `0x03` 与 `0x04`）

单文件续传使用 `0x03`（头部 + 0x10 协商响应的完整字节布局见 AGENTS.md "TCP 线协议" 章节）；文件夹续传使用 `0x04`，两者共享同一偏移协商思想：**服务器偏移权威**，服务器无状态时客户端声明偏移不被信任、从头发送。

#### 文件夹续传（类型 `0x04`）

客户端发送清单（manifest），服务器扫描磁盘上已收到的文件后回答续传点：

```
[1 byte:  0x04]                 // 文件夹续传标记
[16 bytes: sessionId]           // 会话 GUID
[2 bytes: Int16 folderNameLen]
[N bytes: UTF-8 folderName]
[4 bytes: Int32 fileCount]
[8 bytes: Int64 totalBytes]     // 所有文件大小之和（必须等于清单合计，否则拒绝）
每个文件（清单，按顺序）：
    [8 bytes: Int64 fileSize]
    [2 bytes: Int16 pathLen]
    [N bytes: UTF-8 relativePath]
    [32 bytes: SHA256]          // 该文件完整内容的哈希（供服务器校验已收文件）
```

服务器协商响应（`0x11`，26 字节）：

```
[1 byte:  0x11]
[8 bytes: Int64 resumeFileIndex]  // 首个未完成文件的清单序号（全部完成 = fileCount）
[8 bytes: Int64 resumeOffset]     // 该文件内已收到的字节偏移
[1 byte:  status]                 // 0=新会话 1=续传中 2=已全部完成
```

随后客户端从 `(resumeFileIndex, resumeOffset)` 开始，按清单顺序发送剩余内容，每个文件沿用 0x01 的文件体格式：

```
[M bytes: fileData]             // 该文件本次实际发送的字节（续传文件从 resumeOffset 起）
[32 bytes: SHA256]              // 本次发送字节的 SHA-256（增量哈希，同 0x03）
```

服务器行为：
- 保存目录由会话确定性推导：`<saveDir>/<folderName>.<sessionId 前 8 位>`——同一会话跨重启映射到同一目录，无需服务器落盘状态（磁盘文件本身就是状态）。
- 续传扫描：文件存在且大小相符 → 用清单哈希全文件校验，通过则跳过，不符则从 0 重写；文件存在且偏小 → 从其大小偏移续传；其余（缺失/为空/超长）→ 从 0 重写。
- 假定清单顺序发送（与 0x01 一致的顺序遍历）；任一文件增量哈希不符即中止，保留部分文件供下次续传。

### 服务器行为

- 文件名通过 `Path.GetFileName()` 清理；文件夹传输的相对路径通过 `SanitizeRelativePath` 处理（替换 `..` / `.`）。
- 名称冲突时在扩展名前追加 `_1`、`_2` 等。
- 任何文件 SHA-256 不匹配，整个文件夹传输中止。
- 分块传输的 `ChunkTracker` 在首块到达时预分配文件，即使后续块乱序到达也能正确写入对应偏移位置。

---

## UDT 协议

UDT（UDP-based Data Transfer）在 UDP 之上提供可靠、有序字节流（STREAM 模式）。拥塞控制、丢包恢复和重排序由 UDT4/libudt v4.11 库内部处理。

**应用层协议与 TCP 完全相同**——UDT 的 `TransferUdtServer` 和 `TransferUdtClient` 发送/接收与 TCP 章节所述相同格式的数据（1 字节类型 + 头部 + 载荷 + SHA-256），仅传输层不同。

### UDT 特性

| 特性 | 说明 |
|------|------|
| 模式 | STREAM（类 TCP 语义） |
| 拥塞控制 | UDT 内置（类似于 TCP BIC） |
| 超时 | `UDT_RCVTIMEO` / `UDT_SNDTIMEO` = 30 秒 |
| 缓冲区 | 4 MB（发送 & 接收） |
| DLL 大小 | `udt.dll` + `libmcfgthread-2.dll` ≈ 实体内嵌 |

### 连接建立

UDT `udt_connect()` 为异步握手（返回时可能仍处于 BOUND 状态）。客户端通过 `WaitForConnectionReady()` 轮询（30 次 × 200ms），执行一次小 `udt_send()` 等待套接字进入 CONNECTED 状态后再发送实际数据。

### 类型支持

UDT 传输支持所有 TCP 线协议类型：
- `0x00` — 单文件
- `0x01` — 文件夹
- `0x02` — 分块文件（配合 `ConcurrentTransfer`）

### 服务端

`TransferUdtServer` 使用 `udt_bind()` → `udt_listen(10)` → accept 循环（类似 TCP 服务器模式）。每个客户端连接通过 `udt_accept()` 获取独立套接字，fire-and-forget `HandleClient`。Stop 时关闭所有活跃客户端套接字以中断阻塞中的 I/O。

### 客户端

`TransferUdtClient` 使用 `udt_connect()` → `WaitForConnectionReady()` → 发送 TCP 线协议数据 → drain 接收 → `udt_close()`。

### DLL 嵌入

`udt.dll` 和 `libmcfgthread-2.dll` 通过 csc.exe `/resource` 嵌入 exe。首次运行时由 `UdtDll.EnsureExtracted()` 提取到 exe 目录（只读目录回退到 `%TEMP%` + `SetDllDirectory`）。杀软文件锁采用重试机制（最多 3 次，间隔 200ms）。P/Invoke 通过 `udt_c_wrapper.cpp` 导出的纯 C 函数（14 个 API）调用，使用 `CallingConvention.Cdecl`。

### 生命周期

`UdtStartup()` / `UdtCleanup()` 通过引用计数管理——仅当计数归零时才调用原生 `udt_cleanup()`，防止客户端传输完成时关闭活跃的服务器连接。

---

## SHA-256 完整性

两种协议都在数据载荷后追加 32 字节文件内容 SHA-256 哈希。
哈希在传输过程中增量计算（流式）。比较使用恒定时间 XOR 循环以防止时序侧信道攻击。
