# 功能增强实施计划（组 A-D）

> **For agentic workers:** 本计划由主会话 inline 执行（项目约束微妙：C# 5、无外部依赖、csc 构建；上下文连贯性要求高）。步骤使用 `- [ ]` 跟踪。

**Goal:** 实施 7 项功能增强：服务器续传落盘、完整性加固、发送队列、传输限速、设备发现、完成通知、接收文件管理。

**Architecture:** 可靠性组改传输层（ResumeState/TransferServer/TransferUdt + 新 ServerResumeStore）；能力组新增 UI 组件（QueueDialog）与参数贯穿；发现组新增独立 UDP 组件（DeviceDiscovery）；体验组挂接现有事件。

**Tech Stack:** C# 5 / .NET Framework 4.5+ / WinForms / csc.bat / UDT4（P/Invoke）

## Global Constraints

- C# 5：禁止字符串插值、`?.`、表达式体成员、异常过滤器、数字分隔符
- 异步方法所有 `await` 后 `.ConfigureAwait(false)`；UI 事件用 `this.Invoke`
- 本地化字符串全部进 `L10N.cs`（`L.S_*`/`L.C_*`/`L.UdtS_*`/`L.UdtC_*`/`L.*` 命名）
- 构建：`cd TrFileTransfer && build.bat`；测试：`cd Tests && build.bat && TrFileTransfer.Tests.exe`
- 集成测试临时目录 `D:\cc\tmp`、回环地址、动态端口；不引入任何外部包

---

## 文件结构

| 文件 | 职责 |
|---|---|
| `TrFileTransfer/ResumeState.cs` | 修改：`SourceMTime` 字段持久化；`Save`/`Load` 兼容 |
| `TrFileTransfer/ServerResumeStore.cs` | 新建：服务器续传状态磁盘存取 |
| `TrFileTransfer/TransferServer.cs` | 修改：A1 落盘接入、A2b 完整哈希、D2 `OnFileReceived`、限速参数传递 |
| `TrFileTransfer/TransferUdt.cs` | 修改：同上（UDT 版） |
| `TrFileTransfer/TransferClient.cs` | 修改：A2a mtime 检测、A2b 完整哈希发送、B2 限速 |
| `TrFileTransfer/ConcurrentTransfer.cs` | 修改：B2 限速参数、A2b verify 参数 |
| `TrFileTransfer/DeviceDiscovery.cs` | 新建：UDP 探测/响应 |
| `TrFileTransfer/Shared.cs` | 修改：`SpeedLimiter` 令牌桶 |
| `TrFileTransfer/MainForm.cs` | 修改：队列对话框、扫描按钮、通知、最近接收、完整校验复选框、限速输入 |
| `TrFileTransfer/L10N.cs` | 修改：全部新字符串 |
| `TrFileTransfer/Config.cs` | 无修改（键值已通用） |
| `Tests/IntegrationTests.cs` | 修改：4 个新集成测试 |
| `Tests/TestProgram.cs` | 修改：单元测试扩展（SpeedLimiter/ServerResumeStore） |
| `docs/AGENTS.md`（工作区根） | 修改：协议与功能描述同步 |

## 任务分解

### T1: ResumeState.mtime + ServerResumeStore（单元测试）
- `ResumeState.cs`：加 `public long SourceMTime;`，`Save()` 写 `SourceMTime=` 行，`Load()` 读
- 新建 `ServerResumeStore.cs`：`static string Dir`（`%AppData%\TrFileTransfer\server-resume`）、`Save(ResumeState)`（含 SavePath/ReceivedBytes）、`Load(Guid)`、`Delete(Guid)`、`ListAll()`
- 单元测试：store 存取往返、目录隔离
- 验证：Tests build + 单测通过 → commit

### T2: 服务器落盘接入（A1）
- `TransferServer.HandleResumableFile`：状态创建/更新后注册"未完成即保存"；成功/失败清理 `ServerResumeStore.Delete`；0x03 到达时内存未命中 → `ServerResumeStore.Load` → 校验 `File.Exists(SavePath)` 且 `Length == TotalSize` → 以 `FileMode.Open` 恢复 `WriteStream`、`ReceivedBytes`；大小不匹配 → 删除磁盘文件按新 session 处理
- `TransferUdtServer` 同逻辑
- `Stop()`：未完成状态全部 `ServerResumeStore.Save`
- 集成测试 `Integration_TCP_ResumeAcrossServerRestart`：传 512MB 中断 → 服务器 Stop（触发保存）→ 新服务器实例同目录同端口 → 续传 → 断言 offset > 0 + 文件完整
- 验证：build + 新测试 + 回归 → commit

### T3: 源文件变化检测（A2a）
- 客户端 TCP/UDT `SendResumable*Internal`：`existingState.SourceMTime` 与当前 `File.GetLastWriteTimeUtc` 不一致 → 日志提示 + `ResumeState.Delete` + 重建状态（sentBytes=0）
- 验证：build + 回归 → commit

### T4: 完整哈希校验（A2b）
- 协议：0x03 头部 + name 后追加 `[flag:1][hash:32?]`；服务器读 name 后读 1 字节 flag，flag=1 读 32 字节
- 客户端：`verifyHash` 参数（构造或方法参数）→ 发送前流式算完整 SHA256 → 拼入头部
- 服务器：flag=1 时校验：接收完成后 `WriteStream.Flush()` → 重开读文件算整体哈希 → 不一致 → 删文件、清状态、`OnError`、最终 0x10 status=3
- 客户端：status=3 → 抛错/OnError，**不删除**本地状态
- 已有 `SendResumeResponse` 支持 status 3；UDT 版响应同样扩展
- UI：`_chkVerifyHash`（客户端面板，默认 false），传入发送/队列/并发/监控路径
- 集成测试 `Integration_TCP_ResumeFullHashCorrupt`：开启校验，阶段 2 前篡改源文件内容（同大小）→ 服务器拒绝（serverError 含 HashFailed/校验失败）、文件被删、客户端 OnError
- 验证：build + 新测试 + 回归 → commit

### T5: 传输限速（B2）
- `Shared.cs`：`SpeedLimiter` 类（`MaxBytesPerSec`、`Throttle(int sent)` 内部 Stopwatch + Sleep 差额）
- `TransferClient`/`TransferUdtClient`：构造加 `int maxBytesPerSec = 0`；`SendFilePayload` 循环内 `_limiter.Throttle(read)`
- `ConcurrentTransfer`：构造加 `maxBytesPerSec`，每连接 `max(总/并发, 1024)`，传入子客户端
- MainForm：`_numSpeed`（KB/s，0=不限，Config 键 `SpeedLimit`）；传入全部路径
- 集成测试 `Integration_TCP_RateLimit`：512KB/s 传 8MB，耗时 ≥ 10s
- 验证：build + 新测试 → commit

### T6: 发送队列（B1）
- MainForm 抽 `StartTransfer(path, isFolder, ip, port, isTcp, srcPort, concurrency, verifyHash)` 公共方法（从 BtnSend_Click 提取）
- 新 `QueuedTask` 类 + `TransferQueue`（串行执行、事件 OnTaskStateChanged）
- `QueueDialog`：ListBox + 添加当前文件/删除/清空/开始/关闭；执行中更新任务状态
- 客户端面板加"队列"按钮（`_btnQueue`），监控/传输中禁用
- 验证：build + 手动流程（无法自动化 UI）→ commit

### T7: 设备发现（C1）
- `DeviceDiscovery.cs`：`DeviceInfo` 结构、`DiscoveryServer`（UdpClient 接收 0xD1 → 回复 0xD2 包）、`DiscoveryClient`（Scan）
- MainForm：服务器启动/停止时启停 DiscoveryServer（端口 Config 键 `DiscoveryPort` 默认 45000）；客户端"扫描"按钮 → Scan → ComboBox 填充 → 选择填充 IP/端口/协议
- 集成测试 `Integration_Discovery`：本机 DiscoveryServer + Scan → 断言发现
- 验证：build + 新测试 → commit

### T8: 完成通知（D1）
- MainForm：`NotifyIcon`（托盘，图标用 SystemIcons 生成或嵌入式资源占位）、发送完成/服务器接收完成时 BalloonTip + `SystemSounds.Asterisk`
- Config 键 `NotifyEnabled`/`NotifySound`（默认 true）；L10N 字符串
- 验证：build → commit

### T9: 接收文件管理（D2）
- `TransferServer`/`TransferUdtServer`：`OnFileReceived(string savePath, long size)`，在单文件/文件夹每文件/续传成功/分块聚合完成时触发
- MainForm：`_recentFiles`（上限 100）+ "打开目录"按钮 + "最近接收"对话框（双击 `Process.Start` explorer 定位）
- Config 键 `AutoArchive`（默认 false）：开启时新文件保存到 `saveDir\yyyy-MM-dd\`（封装 `GetArchiveSaveDir(saveDir)` 替换 GetUniqueSavePath 调用点）
- 验证：build → commit

### T10: 回归与文档
- 全量测试（41 + 4 新集成 + 单测扩展）全绿；残留目录清理
- AGENTS.md 同步（协议 0x03 扩展、新功能、并发上限 8）
- 最终 commit

## 提交策略

每个 T1-T9 完成后独立 commit（`feat:` / `test:` 前缀），T10 收尾 commit。所有 commit 前跑对应构建与测试。
