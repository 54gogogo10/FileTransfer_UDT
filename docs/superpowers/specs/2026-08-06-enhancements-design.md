# 功能增强设计：续传可靠性 + 队列/限速 + 设备发现 + 通知/接收管理

> 日期：2026-08-06 · 状态：已确认

## 背景

基于对断点续传功能的代码审查与测试验证，提出 7 项功能增强，分为 4 组：

- **组 A 可靠性**：A1 服务器续传状态落盘；A2 续传完整性加固（源文件变化检测 + 全文件哈希校验）
- **组 B 传输能力**：B1 发送队列；B2 传输限速
- **组 C 可用性**：C1 局域网设备发现
- **组 D 体验**：D1 完成通知；D2 接收文件管理

## 全局约束

- C# 5（内置 csc.exe 最高支持），无字符串插值/`?.`/表达式体；.NET Framework 4.5+
- 所有本地化字符串进 `L10N.cs`，按 `L.S_*`（TCP 服务器）/`L.C_*`（TCP 客户端）/`L.UdtS_*`/`L.UdtC_*`/`L.*`（UI）命名约定
- 所有异步方法 `await` 后 `.ConfigureAwait(false)`
- 构建：`TrFileTransfer\build.bat`（主程序）、`TrFileTransfer\Tests\build.bat`（测试）
- 集成测试使用 `D:\cc\tmp` 临时目录 + 回环地址 + 动态端口

---

## 组 A — 可靠性

### A1 服务器续传状态落盘

**现状**：`TransferServer._resumeStates` / `TransferUdtServer._udtResumeStates` 为内存字典，服务器重启后状态丢失，客户端续传从头重发。

**方案**：
- 持久化目录：`%AppData%\TrFileTransfer\server-resume\<sessionId>.json`（独立于客户端 resume 目录）。
- 触发时机：连接中断（HandleClient 结束且状态未完成）时保存；`Stop()` 时保存全部未完成状态。
- 加载：收到 0x03 请求时内存未命中 → 尝试 `ServerResumeStore.Load(sessionId)` → 命中则校验 `SavePath` 文件存在且大小 == TotalSize（`SetLength` 预分配保证）→ 恢复 `ReceivedBytes`/`WriteStream`（以 `FileMode.Open` 打开，seek 到 ReceivedBytes）。
- 清理：成功（哈希通过）或失败（哈希不通过/大小不匹配）后删除磁盘文件。
- 新文件 `ServerResumeStore.cs`：`Save(ResumeState)` / `Load(Guid)` / `Delete(Guid)`。字段：SessionId/TotalSize/FileName/SavePath/ReceivedBytes/Created（与客户端 `ResumeState` 同格式，SavePath 此时持久化）。

**测试**：集成测试 `Integration_TCP_ResumeAcrossServerRestart`：阶段 1 发送部分数据 → 服务器 `Stop()` → 新服务器实例（同目录同端口）→ 客户端同 session 续传 → 断言服务器日志 offset > 0 且最终文件完整。UDT 版本同理（可选，TCP 覆盖核心逻辑）。

### A2 续传完整性加固

**A2a 源文件变化检测**：
- `ResumeState` 增加 `SourceMTime`（long，UTC ticks）。
- 客户端 `SendResumableInternal`/`SendResumableUdtInternal` 加载状态后比对 `File.GetLastWriteTimeUtc(file).Ticks`：不一致 → 日志提示"源文件已变化，从头发送"→ 删除旧状态、重建状态、`sentBytes = 0`。

**A2b 全文件哈希校验（可选，UI 复选框默认关）**：
- 协议扩展（0x03，TCP 与 UDT 同步）：header(36) + name 之后追加 `[1 字节标志][32 字节完整 SHA256]`。标志 0 = 无完整哈希（兼容老服务器/客户端不开启）；1 = 有。
- 客户端：开启时发送前对**整个文件**流式计算 SHA256，随 0x03 头部发出。
- 服务器：标志为 1 时读取 32 字节完整哈希；接收增量数据并校验增量哈希后，重读已保存文件计算整体 SHA256 比对。不一致 → 删除文件 + 移除状态 + `OnError`，并在最终 `0x10` 响应中使用 **status=3**（完整校验失败）。
- 客户端：收到 status=3 → 报错（OnError + 日志），不删除本地 resume 状态（可重试）。
- 服务器接收循环中"重读整体文件"与"已接收数据写入"需先 `WriteStream.Flush()`（或关闭后重开），保证磁盘可见。
- UI：客户端面板加"完整校验"复选框（默认 false），传入所有发送路径（含队列）。

**测试**：集成测试 `Integration_TCP_ResumeFullHashCorrupt`：客户端开启完整校验但发送损坏数据（测试中直接改文件内容后重发同一 session）→ 服务器拒绝（status=3/OnError），文件被删除，客户端报错。

---

## 组 B — 队列 + 限速

### B1 发送队列

**方案**：独立对话框 `QueueDialog`（参照 `ResumeDialog` 模式）：
- 任务列表（ListBox）：`[状态] 文件名 -> IP:port (协议) 大小`
- 按钮：添加当前文件、删除选中、清空、开始、关闭
- 串行执行：`TransferQueue` 类维护 `Queue<QueuedTask>` + 当前任务；`QueuedTask` 字段：FilePath/IsFolder/ServerIp/Port/IsUdp/SrcPort/Concurrency/VerifyHash/SessionId
- 执行复用 MainForm 抽取的 `StartTransfer(...)` 公共方法（从 `BtnSend_Click` 抽出），任务完成/失败自动执行下一个
- 监控模式激活时禁用队列对话框入口；队列执行中禁用发送按钮（复用 `DisableClientInputs` 逻辑或独立状态）
- 状态更新：对话框内当前任务进度文本（复用客户端事件 → Invoke 更新 ListBox 项）

### B2 传输限速

**方案**：
- UI：客户端面板 `_lblSpeed` + `_numSpeed`（KB/s，0=不限，默认 0），Config 持久化（键 `SpeedLimit`）
- `TransferClient` / `TransferUdtClient` 构造增加可选参数 `int maxBytesPerSec = 0`；`ConcurrentTransfer` 同
- 实现：`Utils.SpeedLimiter`（令牌桶）：`Throttle(ref long sent, int chunk, Stopwatch sw)` —— 按累计已发送字节计算应耗时间，`Thread.Sleep` 差额；每连接独立实例，并发时总限速 = 设置值均分到各连接（`Math.Max(setting / concurrency, 1KB/s)`？当 setting=0 时不限速）
- 挂接点：`TransferClient.SendFilePayload` 与 UDT 版 `SendFilePayload` 发送循环内每次 `WriteAsync` 后调用
- 监控模式：`ProcessMonitoredFile` 创建的客户端同样传入

**测试**：集成测试 `Integration_TCP_RateLimit`：限速 512KB/s 传 8MB，断言耗时 ≥ 12s（宽松：≥10s）；不测 UDT（机制相同）。

---

## 组 C — 设备发现

**方案**：新文件 `DeviceDiscovery.cs`
- 常量：默认端口 45000（Config 键 `DiscoveryPort`），探测包 `0xD1`，响应 `[0xD2][nameLen:1][name UTF-8][port:2 网络序][protocols:1 bit0=TCP bit1=UDT]`
- `DiscoveryServer`：`UdpClient` 绑定端口 + `SO_BROADCAST`；`Start()` 后接收循环，收到 `0xD1` → 组响应（服务器名 = `Environment.MachineName`，端口 = 服务器监听端口，协议位图）→ 回复发送方地址；`Stop()` 关闭。MainForm 服务器启动时启停
- `DiscoveryClient`：`Scan(int timeoutMs = 1500)` → 向 `255.255.255.255:port` 发送 3 次探测（间隔 200ms）→ 收集响应 → `DeviceInfo[] { Name, Ip, Port, Protocols }`
- UI：客户端面板 IP 框旁"扫描"按钮 → 结果下拉（ComboBox 动态填充）→ 选择后自动填 IP/端口/协议单选
- 说明：广播可能被防火墙拦截，文档注明；响应仅暴露存在性与端口

**测试**：集成测试 `Integration_Discovery`：本机启动 `DiscoveryServer`，`DiscoveryClient.Scan()` 定向发送（广播在本机可用）→ 断言发现服务器且端口/协议正确。

---

## 组 D — 通知 + 接收管理

### D1 完成通知
- MainForm 创建 `NotifyIcon`（托盘图标，`Visible` 随服务器/传输状态），发送完成 / 服务器收到文件时 `ShowBalloonTip` + `SystemSounds.Asterisk.Play()`
- Config 键：`NotifyEnabled`（默认 true）、`NotifySound`（默认 true）
- L10N：`L.NotifySendDone` / `L.NotifyReceiveDone`

### D2 接收文件管理
- 服务器事件扩展：`TransferServer`/`TransferUdtServer` 增加 `public event Action<string, long> OnFileReceived;`（保存路径 + 字节数），在单文件/文件夹（每个文件）/续传/分块聚合完成时触发
- MainForm：`_recentFiles` 列表（上限 100），服务器面板加"打开目录"按钮（`Process.Start("explorer.exe", saveDir)`）与"最近接收"按钮（对话框：ListBox + 双击打开所在文件夹 + 清空）
- 按日期归档：Config 键 `AutoArchive`（默认 false）；开启时保存路径改为 `saveDir\yyyy-MM-dd\`（仅影响单文件/文件夹/续传的新文件创建点，用辅助函数 `GetArchiveDir(saveDir)`）

---

## 测试与验证计划

| 测试 | 类型 | 覆盖 |
|---|---|---|
| `Integration_TCP_ResumeAcrossServerRestart` | 集成 | A1 落盘跨重启续传 |
| `Integration_TCP_ResumeFullHashCorrupt` | 集成 | A2b 完整校验拒绝损坏 |
| `Integration_TCP_RateLimit` | 集成 | B2 限速耗时 |
| `Integration_Discovery` | 集成 | C1 发现协议 |
| `UnitTests` 扩展 | 单元 | `SpeedLimiter` 节流、`ServerResumeStore` 存取、ResumeState mtime 字段 |
| 现有全部测试 | 回归 | 41 项全绿 |

构建：每组合并前 `build.bat` 通过；最终全量测试 + 手动验证 UI（通知/队列/最近接收/扫描）。
