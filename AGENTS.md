# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

本文件为 Codex 在此仓库中工作时提供指引。

## 构建

```
cd TrFileTransfer && build.bat
```

使用 .NET Framework 4.x 自带的 `csc.exe` 编译。引用：`System.dll`、`System.Core.dll`、`System.Windows.Forms.dll`、`System.Drawing.dll`。`udt.dll` 和 `libmcfgthread-2.dll` 通过 `/resource` 嵌入 exe。

**UDT DLL 编译**（如更新原生代码）：需 MinGW-w64，在 `udt-sdk\udt4\src\` 下执行：
```
g++ -DWIN32 -DNDEBUG -DUDT_EXPORTS -O2 -fno-strict-aliasing -Wall \
    -finline-functions -fvisibility=hidden \
    -c api.cpp buffer.cpp cache.cpp ccc.cpp channel.cpp common.cpp \
       core.cpp epoll.cpp list.cpp md5.cpp packet.cpp queue.cpp \
       window.cpp udt_c_wrapper.cpp
g++ -shared -o udt.dll *.o -lws2_32 -static-libgcc -static-libstdc++
```

编译器为 C# 5（内置 `csc.exe` 支持的最高语言版本），生成的 exe 运行在 .NET Framework 4.5+（推荐 4.6.1）。避免 C# 6+ 语法：禁止字符串插值（`$`）、禁止表达式体成员、禁止空条件运算符（`?.`）、禁止异常过滤器（`when`）、禁止数字分隔符。

## 测试

```
# 构建测试：
cd TrFileTransfer\Tests && build.bat

# 运行测试：
TrFileTransfer.Tests.exe
```

测试套件包含单元测试（L10N、Config、Shared 工具方法）和集成测试（TCP 单文件、TCP 文件夹、UDT 单文件）。使用自定义轻量级 `TestRunner` + `Assert` 类，无外部测试框架依赖。

集成测试每次从 `TcpListener` 获取空闲端口并绑定本地回环（127.0.0.1），使用临时目录。超时：TCP 30 秒，UDT 60 秒。退出码 0 表示全部通过，1 表示有失败。

## 架构

十二个源文件编译为单个 WinForms exe：

- **Program.cs** — 入口。`[STAThread]` Main 启动 `MainForm`。
- **MainForm.cs** — GUI。服务器和客户端面板同时显示。协议选择（TCP/UDT）、服务器绑定地址下拉框。**并发控制**：`_numConcurrency`（1-8）。文件夹/监控模式复选框、**完整校验复选框**（续传时启用 0x03 全文件哈希）、**限速输入**（KB/s，0=不限，Config `SpeedLimit`）。动态进度卡片（左右两个 FlowLayoutPanel，TCP/UDT 独立字典按 IPEndPoint 区分，完成 3 秒后移除）。语言下拉框、日志上限 500 条、进度节流 100ms。`WireClientEvents`/`WireUdtClientEvents`/`WireConcurrentEvents` 辅助方法。**发送队列**（`QueueDialog` 串行批量执行，复用抽取的 `StartTransfer` 公共方法）。**设备发现**（"扫描"按钮 → `DiscoveryDialog`，服务器启动时 `DiscoveryServer` 在 UDP 端口广播响应）。**完成通知**（托盘 `NotifyIcon` BalloonTip + 提示音，Config `NotifyEnabled`/`NotifySound`）。**接收文件管理**（"打开目录"/"最近接收"按钮 + `OnFileReceived` 事件 + 按日期归档 Config `AutoArchive`）。`StartTransfer` 内部 try/catch 返回 bool，失败不再冒泡崩溃。
- **TransferServer.cs** — 异步 TCP 服务器。`HandleClient` 读取 1 字节类型前缀，分发：0x00 单文件 / 0x01 文件夹 / 0x02 分块（`ChunkTracker` 聚合）/ **0x03 续传**（`HandleResumableCore`，支持**磁盘状态恢复**与**全文件哈希校验**）。`OnFileReceived(path, size)` 事件在四种完成路径触发。`NoDelay = true`，LongRunning 接受循环。
- **TransferClient.cs** — 异步 TCP 客户端。`SendAsync()`/`SendFolderAsync()`/`SendChunkedAsync()`/`SendResumableAsync(sessionId, verifyHash)`（0x03，发送后读取最终 0x10 响应确认）。`SendFilePayload` 支持 `fileOffset` 与**限速**（`SpeedLimiter` 令牌桶）。支持绑定源端口。
- **TransferUdt.cs** — UDT STREAM 传输。`UdtNative` 引用计数 + Cdecl P/Invoke；`UdtDll` 提取嵌入 DLL；`UdtIo` 封装异步 I/O（**错误描述在线程内捕获**，`LastError` 供诊断）。`TransferUdtServer`/`TransferUdtClient` 复用 TCP 线协议（含 0x03 续传、全文件校验、限速、`OnFileReceived`）。`udt_listen` backlog 32。
- **ConcurrentTransfer.cs** — 多连接并发传输编排器。单文件切分为 N 个等大分块并行发送（独立连接+端口）。文件夹用 `SemaphoreSlim` 控制并发度。
- **Config.cs** — 键值配置持久化。`Get`/`GetInt`/`GetBool`/`Set`/`SetInt`/`SetBool`。启动时加载，关闭时保存。
- **Shared.cs** — `TransferProgress`/`FileEntry` 结构体。`ChunkTracker`（分块重组）。`SpeedLimiter`（令牌桶限速）。`Utils` 静态辅助（`FormatSize`、`ConstantTimeEquals`、`LogTo`、`SanitizeRelativePath`、`GetUniqueSavePath`、`FindFreePort`、`EmptyBytes`）。
- **ResumeState.cs** — 客户端续传状态持久化（含 `SourceMTime` 源文件变化检测）。
- **ServerResumeStore.cs** — 服务器续传状态落盘（`%AppData%\TrFileTransfer\server-resume`），服务器重启后按磁盘偏移恢复。
- **DeviceDiscovery.cs** — UDP 设备发现：探测 `0xD1` → 响应 `0xD2`（名称/端口/协议位图），默认端口 45000（Config `DiscoveryPort`）。
- **L10N.cs** — 本地化字符串。静态 `L` 类根据 `L.IsChinese` 返回英文或中文文本。命名规则见下方"本地化约定"。
- **Tests/TestProgram.cs** — 测试入口。`TestProgram.Main` 先运行单元测试再运行集成测试，退出码反映结果。同时包含 `UnitTests` 类、`Assert` 辅助类（`Equal/True/False/NotNull/Throws`）、`TestRunner.Run(name, action)` 追踪通过/失败计数。
- **Tests/IntegrationTests.cs** — TCP/UDT 端到端集成测试。`FindFreePort()` 动态获取可用端口。

### 本地化约定

GUI 有语言下拉框（English / 中文），设置 `L.IsChinese`。所有本地化字符串在 `L10N.cs` 中定义。按消费者命名：
- `L.S_*` — TCP 服务器日志消息
- `L.C_*` — TCP 客户端日志消息
- `L.UdtS_*` — UDT 服务器日志消息
- `L.UdtC_*` — UDT 客户端日志消息
- 其他 `L.*` 属性 — MainForm UI 字符串

新增日志消息时遵循以上命名约定添加到 `L10N.cs`。禁止使用内联 `L.IsChinese ? "..." : "..."` 三元表达式。语言切换时 `PopulateBindAddresses()` 重新读取网卡并更新第一个下拉项标签。传输过程中语言切换被禁用（下拉框不可用）。

### 文件名冲突处理

TCP 和 UDT 服务器都使用 `Utils.GetUniqueSavePath`，在基础文件名（扩展名前）追加 `_1`、`_2` 等后缀，解决与保存目录中已有文件/目录的名称冲突。

### 线程安全模式

传输类使用 `volatile bool _isRunning` 标志和 `CancellationTokenSource`。UI 事件处理器通过 `MainForm.cs` 中的 `this.Invoke()` 封送。触发事件前使用局部变量快照模式：`var handler = OnXxx; if (handler != null) handler(...);`。

## TCP 线协议

所有传输以 1 字节类型开始：`0x00` = 单文件，`0x01` = 文件夹，`0x02` = 分块文件，`0x03` = 断点续传。

### 断点续传（类型 0x03）

```
[1 byte:  0x03]
[16 bytes: sessionId (Guid)]
[8 bytes: Int64 文件总大小]
[8 bytes: Int64 客户端声明偏移]
[4 bytes: Int32 文件名长度]
[N bytes: UTF-8 文件名]
[1 byte:  verifyFlag（恒存在，0=无完整哈希 1=有）]
[32 bytes: 完整文件 SHA256]（仅 verifyFlag=1 时）
[M bytes: 文件内容（从协商偏移开始）]
[32 bytes: 增量内容 SHA256]
```

协商响应（0x10，10 字节）：`[1 byte: 0x10][8 bytes: Int64 服务器偏移（权威）][1 byte: status]`。status：0=新会话，1=续传中，2=已完成，3=完整校验失败（文件已删除）。服务器偏移取 `max(客户端声明, 服务器实际)`；服务器无状态时（重启后从磁盘恢复或全新）不信任客户端偏移、从 0 开始。客户端在发送完成后**读取最终 0x10 响应**确认结果（status 2/3）。完整校验开启时（verifyFlag=1）服务器接收完成后重读文件比对整体哈希，不一致则删除文件并回 status=3。

服务器侧状态（`ServerResumeStore`）在连接中断/`Stop()` 时落盘，重启后按磁盘偏移恢复；客户端状态含 `SourceMTime`，源文件修改后自动从头发送。


### 单文件（类型 0x00）

```
[1 byte:  0x00]
[8 bytes: Int64 文件大小，小端序]
[4 bytes: Int32 文件名长度]
[N bytes: UTF-8 文件名]
[M bytes: 文件内容（fileSize 字节）]
[32 bytes: 文件内容 SHA256 哈希]
```

### 文件夹（类型 0x01）

```
[1 byte:  0x01]
[2 bytes: Int16 文件夹名长度]
[N bytes: UTF-8 文件夹名]
[4 bytes: Int32 文件数量]
每个文件：
    [8 bytes: Int64 文件大小]
    [2 bytes: Int16 相对路径长度]
    [N bytes: UTF-8 相对路径（相对于文件夹根目录，如 "subdir/file.txt"）]
    [M bytes: 文件内容（fileSize 字节）]
    [32 bytes: 此文件内容 SHA256 哈希]
```

服务器根据需要从每个文件的相对路径在保存路径中创建子目录。如果某个文件的 SHA256 失败，整个文件夹传输中止。文件名冲突处理在文件夹保存目录名追加 `_1`、`_2`。

## UDT 传输

UDT 传输基于 UDT4/libudt v4.11，使用 STREAM 模式。UDT 在 UDP 之上提供可靠、有序的字节流（类 TCP 语义），由库内部处理拥塞控制、丢包恢复和重排序。应用层无需实现 ARQ、ACK/NAK、滑动窗口或 RTT 测量。

**应用层协议复用了相同的 TCP 线协议**（见上方"TCP 线协议"章节）。TransferUdtServer 和 TransferUdtClient 发送/接收与 TCP 对等类相同格式的数据，仅传输层不同。

### DLL 编译

UDT4 源码不默认导出 `udt_` 前缀的 C 函数——所有 API（`startup`、`socket`、`bind` 等）位于 `UDT::` 命名空间内，以 C++ 名称修饰编译。`udt_c_wrapper.cpp` 用 `extern "C"` + `__declspec(dllexport)` 包装所有 14 个 API，导出纯 C 名称供 P/Invoke 调用。

```
# 编译 udt.dll（需要 MinGW-w64）：
cd udt-sdk\udt4\src
g++ -DWIN32 -DNDEBUG -DUDT_EXPORTS -O2 -fno-strict-aliasing -Wall \
    -finline-functions -fvisibility=hidden \
    -c api.cpp buffer.cpp cache.cpp ccc.cpp channel.cpp common.cpp \
       core.cpp epoll.cpp list.cpp md5.cpp packet.cpp queue.cpp \
       window.cpp udt_c_wrapper.cpp
g++ -shared -o udt.dll *.o -lws2_32 -static-libgcc -static-libstdc++

# 复制产物：
copy udt.dll libmcfgthread-2.dll TrFileTransfer\
```

`udt.dll` 依赖 `libmcfgthread-2.dll`（MinGW MCF 线程运行时，约 42 KB）。两个 DLL 均通过 csc.exe `/resource` 嵌入 exe，首次运行时由 `UdtDll.EnsureExtracted()` 提取到 exe 目录。提取逻辑支持只读目录回退到 `%TEMP%` + `SetDllDirectory`，以及杀软锁重试（最多 3 次，间隔 200ms）。

### P/Invoke

`UdtNative` 静态类声明了核心 UDT API（CallingConvention.Cdecl）：

- 生命周期：`UdtStartup()` / `UdtCleanup()` —— **引用计数包装**，private `udt_startup()`/`udt_cleanup()` 仅在引用计数归零时调用原生函数。防止客户端传输完成时 `udt_cleanup()` 关闭活跃的服务器连接。
- 套接字：`udt_socket()` / `udt_close()`
- 连接：`udt_bind()` / `udt_listen()` / `udt_accept()` / `udt_connect()`
- I/O：`udt_send()` / `udt_recv()`（通过 `Task.Run` 包装实现异步，均有 `.ConfigureAwait(false)`）
- 选项：`udt_setsockopt()` / `udt_getsockopt()`（`UDT_RCVTIMEO` / `UDT_SNDTIMEO` 设为 30 秒）
- 错误：`udt_getlasterror_desc()` 返回错误描述字符串（`GetErrorDesc()` 包装）
- `SockAddrSize` 缓存 `Marshal.SizeOf(typeof(sockaddr_in))`，避免每次 accept/connect 反射调用

### 服务器架构

`TransferUdtServer.Start()`：`UdtDll.EnsureExtracted()` → `UdtStartup()`（检查返回值，失败则报告 "UDT library init failed"）→ `udt_socket()` → `udt_bind()` → `udt_listen(10)` → LongRunning accept 循环。

Accept 循环：`udt_accept()` 返回客户端 socket → 添加到 `_clientSockets` 列表 → fire-and-forget `HandleClient` → 添加/移除 client-specific progress handler（模式与 TCP TransferServer 相同），支持 `OnClientProgress`/`OnClientTransferComplete`（按 IPEndPoint 区分）。

`HandleClient` 仅在传输真正成功完成（`HandleFileTransfer`/`HandleFolderTransfer` 返回 true）时触发 `OnClientTransferComplete`。协议错误（无效头部、零文件计数、哈希失败）返回 false，不触发完成事件。

`Stop()` 关闭监听 socket → 遍历 `_clientSockets` 关闭所有活跃客户端 socket（使 `HandleClient` 中的阻塞 I/O 立即中断）→ `Uninit()`（仅在 `_startupOk` 为 true 时调用 `UdtCleanup()`，防止 `Start()` 中途失败后的双重清理）。

`AcceptLoop` 中 IPEndPoint 构造使用 `(uint)`/`(ushort)` 强制转换避免 `sin_addr`/`sin_port` 的符号扩展——客户端临时端口（≥49152）在从网络字节序转换时不会变为负数。

### 客户端架构

`TransferUdtClient`：`RunUdtTransfer`（生命周期管理 + `.ConfigureAwait(false)`）→ `SendFileInternal`/`SendFolderInternal`。

连接流程：`udt_socket()` → `udt_connect()` → `SetTimeout(30s)` → **`WaitForConnectionReady()`**（UDT connect 握手为异步，轮询空 `udt_send()` 直到 socket 从 BOUND 进入 CONNECTED 状态，最多 30 次 × 200ms）→ 发送 TCP 线协议头部 + 数据 + SHA256。

`_socket` 声明为 `volatile`，防止 `Cancel()` 与 `finally` 块之间的重入竞态。连接/socket 失败抛出异常（触发 `OnError` + UI 重置），而非静默返回。

## 边界情况与异常处理

- `ObjectDisposedException` 和 `InvalidOperationException` 在所有传输循环和外部 try-catch 块中静默捕获——`Stop()`/`Cancel()` 关闭套接字期间 I/O 正在执行时会出现这些异常。
- 绑定失败（地址不在任何网卡上）在 `TransferServer.Start()` 和 `TransferUdtServer.Start()` 中捕获——触发 `OnError` + `OnStopped`，UI 重新启用控件。
- 传输过程中语言切换被禁用。
- `TransferUdtServer.Stop()` 调用 `udt_close()` 以中断阻塞中的 `udt_accept()`。
- C# 5 不允许在 `catch` 块中使用 `await`（CS1985）。使用在 `catch` 中设置的标志变量，在 try-catch 之后检查。

### 异步/UI 线程安全

所有异步传输方法在每个 `await` 上使用 `.ConfigureAwait(false)`。否则 WinForms 的 `SynchronizationContext` 会导致每个异步续延在 UI 线程上恢复——使消息泵饥饿并在传输期间冻结 UI。服务器在 `Task.Factory.StartNew(LongRunning)` 上运行（无同步上下文），其 await 不会捕获 UI 线程，但为保持一致性仍应用 `ConfigureAwait(false)`。

### UDT 超时与错误处理

UDT STREAM 模式通过 `udt_setsockopt` 设置 `UDT_RCVTIMEO` 和 `UDT_SNDTIMEO` 为 30 秒。超时后 `udt_recv`/`udt_send` 返回 -1 (ERROR)，调用方通过 `UdtNative.GetErrorDesc()` 获取错误描述。`udt_close()` 用于中断阻塞中的 I/O 操作（类似 TCP 的 socket close）。

`UdtWriteExactAsync` 处理 `udt_send` 部分发送（阻塞模式下极少发生），通过循环 + 临时缓冲区补齐剩余字节。`UdtReadExactAsync` 在短读取或 EOF 时抛出 `IOException`，调用方不需要检查返回值（也不检查——返回值始终等于 count）。

`sockaddr_in` 为 blittable 结构体（`LayoutKind.Sequential, Size=16`），通过引用传递给原生代码。`BuildSockaddr` 手动构造网络字节序的 `sin_addr`/`sin_port`。

## 监控模式

客户端 UI 在文件路径字段下方有一个"监控模式"复选框。启用后：

- **`StartMonitoring(folderPath, ip, port)`**：创建"已发送文件"子目录，禁用 UI 输入控件，在目标文件夹上启动 `FileSystemWatcher`，在线程池线程上启动 `MonitorLoop`。
- **`MonitorLoop`**：处理循环——每次处理一个文件出队，调用 `ProcessMonitoredFile`，队列为空时睡眠 500ms。通过 `CancellationToken` 退出。
- **`ProcessMonitoredFile`**：(a) 调用 `WaitForFileReady` 确保文件写入完成；(b) 创建新的 `TransferClient` 或 `TransferUdtClient`，绑定最小事件集（日志、进度、`TaskCompletionSource<bool>` 用于完成跟踪）；(c) 成功后移入"已发送文件/"；(d) 失败时记录错误并保留文件。
- **`WaitForFileReady`**：每 500ms 轮询 `FileInfo.Length`。连续 2 次大小不变时返回 `true`。每 30 秒记录一条日志。2 分钟未稳定则返回 `false`——文件移至队列末尾（防止卡住的文件阻塞其他文件）。
- **事件绑定分离**：监控模式使用自己的事件处理器更新日志和进度条，但不调用 `ResetClientUI`。`TaskCompletionSource<bool>` 跟踪每个文件的成功/失败——`OnTransferComplete` 设为 true，`OnStopped` 设为 false（仅首次调用生效）。
- **停止**：监控中点击取消按钮调用 `StopMonitoring()`，取消令牌、释放 FileSystemWatcher、重置 UI。

## 运行时要求

- Windows 7 SP1 或更高版本
- .NET Framework 4.5 或更高版本（Win7 推荐 4.6.1+）
