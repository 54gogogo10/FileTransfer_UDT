# AGENTS.md

This file provides guidance to AI coding agents (Codex / ZCode / Claude Code) when working with code in this repository.

本文件为 AI 编码代理（Codex / ZCode / Claude Code）在此仓库中工作时提供指引。

## 构建

```
cd TrFileTransfer && build.bat
```

使用 .NET SDK（`dotnet build`，需 SDK 8+）编译 SDK 风格 `TrFileTransfer.csproj`：目标 `net48`（WPF），输出 `bin\Release\net48\TrFileTransfer.exe`。csproj 经 `Microsoft.NETFramework.ReferenceAssemblies` 包（`PrivateAssets=all`，仅编译期、不进运行时）解决本机/CI 无 4.8 targeting pack 的问题；`udt.dll` 和 `libmcfgthread-2.dll` 经 `<EmbeddedResource LogicalName="TrFileTransfer.udt.dll">` 嵌入 exe（**LogicalName 必须与 `TransferUdt.cs` 运行时查找的资源名精确一致**，否则 UDT 静默降级为依赖 exe 旁 DLL）；托盘 `NotifyIcon`/`FolderBrowserDialog`/图标仍引用 System.Windows.Forms/System.Drawing 程序集（net48 WPF 无零依赖替代，仅此两处借用）。

**UDT DLL 编译**（如更新原生代码）：需 MinGW-w64，在 `udt-sdk\udt4\src\` 下执行：
```
g++ -DWIN32 -DNDEBUG -DUDT_EXPORTS -O2 -fno-strict-aliasing -Wall \
    -finline-functions -fvisibility=hidden \
    -c api.cpp buffer.cpp cache.cpp ccc.cpp channel.cpp common.cpp \
       core.cpp epoll.cpp list.cpp md5.cpp packet.cpp queue.cpp \
       window.cpp udt_c_wrapper.cpp
g++ -shared -o udt.dll *.o -lws2_32 -static-libgcc -static-libstdc++
```

**C# 版本边界**：主程序经 Roslyn 以 `LangVersion=latest` 编译，UI 层可用现代 C# 语法；但 `Tests\build.bat` 仍用 .NET 4 自带老 `csc.exe`（C# 5）编译 **14 个逻辑文件**（Config/Shared/L10N/WireProtocol/ConcurrentTransfer/TransferServer/TransferUdt/TransferClient/DeviceDiscovery/ResumeState/ServerResumeStore/FolderResumeState/Updater/HttpShare）——这 14 个文件必须保持 C# 5 语法（禁止字符串插值（`$`）、表达式体成员、空条件运算符（`?.`）、异常过滤器（`when`）、数字分隔符），且不得新增对 System.Windows.Forms/System.Drawing 的引用（测试构建未引用这两个程序集）。新 UI 代码（MainWindow/App/Dialogs/Themes 等）不受 C# 5 限制。生成的 exe 运行在 .NET Framework 4.8+。

## 测试

```
# 构建测试：
cd TrFileTransfer\Tests && build.bat

# 运行测试：
TrFileTransfer.Tests.exe
```

测试套件包含单元测试（L10N、Config、Shared 工具方法/AutoStart、ServerResumeStore、FolderResumeStore/同步会话派生、Updater 清单解析/版本比较/SHA256/Apply 回滚、WireAuth 配对码哈希与 Preview、发现协议配对位、端口探测）和 56 项集成测试（TCP：单文件/文件夹/空文件/中文名/多客户端/连接拒绝/文件不存在/大文件×2/续传×4/限速；UDT：单文件/文件夹/空文件/限速/大文件×2/续传×3；自动升级：检查+下载校验/哈希不匹配/清单404/清单无效/旧版本/GitHub 全流程/边车损坏/边车缺失——经 `MiniHttpServer`（TcpListener 手写 HTTP，无 HttpListener URL ACL 依赖）提供本地清单与文件；配对码：TCP 正确码/错码/无码/宽容路径 + UDT 正确码；文本：TCP 常规+大文本 + UDT；同步：TCP 三轮（全量/无变化零传输/仅新增文件）+ UDT 两轮；群发核心：双目标并行接收；端口探测：占用/释放/协议独立×4；HTTP 共享×4（上传多文件/中文/子目录/token/穿越文件名）；另有含配对标志的双协议发现测试）。使用自定义轻量级 `TestRunner` + `Assert` 类，无外部测试框架依赖。

- `TestRunner.Run(name, action, retries)` 支持重试参数——UDT 握手偶发抖动，所有 UDT 集成测试传 `retries: 1`。
- 集成测试每次经 `FindFreePort()` 获取空闲端口并绑定 127.0.0.1；该方法还会用 `UdpClient` 验证端口对 UDP 同样可用（UDT 在同端口绑 UDP，避开 Hyper-V/WSL 的 Windows 保留 UDP 端口段）。
- 集成测试临时目录经 `TempBase()` 选择：存在 `D:\cc` 时用 `D:\cc\tmp`，否则回退 `%TEMP%`（CI 兼容）。
- 超时：TCP 30 秒，UDT 60 秒。退出码 0 表示全部通过，1 表示有失败。
- CI：`.github/workflows/ci.yml` 在 windows-latest 上运行两个 build.bat + 测试套件（push 触发）。
- `Tests/QuickTest.cs` 是独立的 UDT 冒烟脚本，不在测试 build.bat 的编译列表中。

## 架构

逻辑层 14 个源文件 + WPF UI 层编译为单个 exe（GUI 已从 WinForms 迁移到 WPF，行为 1:1 移植，传输逻辑零改动）：
- **App.xaml / App.xaml.cs + AssemblyInfo.cs** — 入口。`OnStartup` 命名 `Mutex` 保证单实例（二次启动经命名 `EventWaitHandle` 唤醒已有窗口后退出）；`DispatcherUnhandledException` + `AppDomain.UnhandledException` 兜底写入 `%AppData%\TrFileTransfer\logs\crash.log` 并弹友好提示；`App.ExePath` 供 AutoStart/Updater/防火墙提示取 exe 路径；`ShutdownMode=OnMainWindowClose`。程序集属性（版本 2.7.0.0，`Updater.CurrentVersion` 读取）在 `AssemblyInfo.cs`（csproj `GenerateAssemblyInfo=false` 避免与源内 attribute 冲突）。
- **MainWindow.xaml / MainWindow.xaml.cs** — GUI（原 MainForm 的 WPF 1:1 移植；x:Name 自动生成字段，控件事件在 XAML 绑定/构造函数绑定）。窗口可缩放（Grid 根布局：表头/双卡片行固定，进度区左右分栏与日志区平分剩余空间）。**深色主题**：色板 token 集中在 `Themes/Tokens.xaml`（页面底 #17191F、卡片 #1F232C、Accent #2563EB 主按钮；未来加浅色主题只需换 token），控件模板在 `Themes/Controls.xaml`；Win11 启用 DWM 圆角+深色标题栏+Mica 背景（`UiChrome`，属性不支持时静默回退纯色，Win10/Win7 自动降级），分区标题带 Segoe 图标 glyph（`IconFont` 检测到无图标字体如 Win7 时整体隐藏）。**按钮布局**：服务器与客户端两张卡片并排（服务器：绑定地址/端口/协议、保存目录、启动/停止+打开目录/最近、HTTP 端口+共享│配对码；客户端：IP/端口/协议/发送、路径/浏览/取消、选项复选行、参数字段组（标签在输入框上方：源端口/并发/限速│配对码）、工具行：扫描/群发/队列/续传│发文本）；日志为深色控制台（`Font.Mono`：Cascadia Mono→Consolas）。服务器和客户端面板同时显示。协议选择（TCP/UDT）、服务器绑定地址下拉框。**并发控制**：`_numConcurrency`（1-8）。文件夹/监控模式复选框、**完整校验复选框**（续传时启用 0x03 全文件哈希）、**限速输入**（KB/s，0=不限，Config `SpeedLimit`）。动态进度卡片（左右两个 StackPanel，TCP/UDT 独立字典按 IPEndPoint 区分，完成 3 秒后经 DispatcherTimer 移除；卡片淡入 + 进度条 150ms 平滑动画，进度节流 100ms）。语言下拉框、日志上限 500 条、进度节流 100ms。`WireClientEvents`/`WireUdtClientEvents`/`WireConcurrentEvents` 辅助方法。**发送队列**（`QueueDialog` 串行批量执行，复用抽取的 `StartTransfer` 公共方法；失败自动重试 N 次——Config `QueueRetries`，0-5——列表项显示▶执行中/✓✗结果与耗时）。**续传列表**（`ResumeDialog` 同时列出单文件 `ResumeState` 与文件夹 `FolderResumeState` 会话，中括号 `[文件夹]` 前缀区分；选中后填充客户端面板并设 `_pendingResumeSession`，`StartTransfer` 按 isFolder 分派到 0x03/0x04）。**设备发现**（"扫描"按钮 → `DiscoveryDialog`，服务器启动时 `DiscoveryServer` 在 UDP 端口广播响应）。**完成通知**（托盘 `NotifyIcon` BalloonTip + 提示音，Config `NotifyEnabled`/`NotifySound`）。**接收文件管理**（"打开目录"/"最近接收"按钮 + `OnFileReceived` 事件 + 按日期归档 Config `AutoArchive`）。`StartTransfer` 内部 try/catch 返回 bool，失败不再冒泡崩溃。**拖放批量入队**（拖多个文件/文件夹时逐项入队并直接打开 `QueueDialog`，单个仍填充路径框；`CaptureQueuedTaskFor(path, isFolder)` 从 `CaptureQueuedTask` 抽取复用）。**日志持久化**（`AddLog` 的每条日志同时经 `AppendLogFile` 写入 `%AppData%\TrFileTransfer\logs\yyyy-MM-dd.txt`，失败静默；保留 30 天，每日首次写日志时清理过期文件）。**设备记忆**（传输成功后 `RememberDevice` 记入 Config `KnownDevices`——普通发送与监控模式均记录——格式 `Name|Ip|Port|flags`（分号分隔多条，flags 位1=TCP 位2=UDT），上限 10 条；`DiscoveryDialog` 将在线结果按 Ip+Port 合并进已知列表去重显示）。**托盘常驻**（最小化时 `Hide()`；用户关闭窗口=取消关闭+`SaveConfig`+隐藏+BalloonTip；仅托盘菜单"退出"设置 `_trayExit` 后真正退出，退出时释放 `NotifyIcon` 防止托盘图标残留）。**自动升级**（表头"检查更新"按钮 + 托盘菜单项 → `UpdateDialog`；启动后延迟 3 秒后台静默检查；`ApplyUpdateAndRestart` 经 `Updater.Apply` 换 exe 后重启，详见 Updater.cs 条目）。**配对码**（服务器面板"配对码"复选框 + 每次启动会话随机生成的 6 位码显示；客户端"配对码"输入框经 Config `PairingCode` 持久化，发送前先走 0x05 认证；TCP/UDT/并发分块/监控模式全部路径贯通）。**文本互传**（客户端"发文本"按钮 → `TextSendDialog` 多行输入 → 0x06 发送；服务器端 `OnTextReceived` → 日志 + BalloonTip + 非模态 `TextReceivedDialog`（复制按钮，用户关闭时仅隐藏以累积消息，随主窗口关闭）。**同步模式**（文件夹模式子选项"同步模式"复选框，Config `SyncMode`；勾选后文件夹发送走 0x04 + `DeriveSyncSession` 稳定会话 + keepState，重复发送只传与服务器差异的部分——增量备份语义，本地删除的文件服务器端保留）。**开机自启**（托盘菜单"开机自启"勾选项，`AutoStart` 写 HKCU Run 键，无需管理员）。**跳过此版本**（更新对话框按钮，Config `SkippedVersion`；启动静默检查对 ≤ 该版本的清单不再提示，手动检查不受影响）。**发现配对徽标**（扫描对话框对广播 bit2 显示"[需配对]"，点"使用"后日志提示并聚焦配对码框）。**HTTP 共享**（服务器面板"HTTP 共享"按钮开/停，共享保存目录；配对开启时复用配对码作网页访问码，URL 记入日志；随主窗口关闭停止）。**群发**（客户端"群发"按钮 → `FanOutDialog` 复选多选已知+在线设备 → 并行对每台目标起独立客户端（按面板 TCP/UDT 选择、限速、配对码；忽略源端口），每目标一张进度卡片，完成计数后汇总 `{ok}/{total}` 并通知，取消按钮可整体取消）。**端口占用换口**（启动服务器前预检端口（按所选协议探测 TCP/UDP，`Utils.IsPortFree/FindFreePortFrom`），被占时询问改用其后第一个可用端口）。**防火墙提示**（首次成功启动服务器展示一次（Config `FirewallHintShown`）netsh 放行命令说明，复用 `TextReceivedDialog` 的 title 重载）。
- **Themes/Tokens.xaml + Themes/Controls.xaml** — 深色主题 token（色板/字体族 Font.Main/Font.Mono/Font.Icon）与全套控件样式模板（BtnPrimary/BtnSecondary/BtnDanger、TxtInput、CmbInput、ChkBox、RdoBox、BarModern、ListDark/LogList、细深色 ScrollBar）。改配色只动 Tokens.xaml。
- **Dialogs/*.cs** — 8 个对话框（Resume/Queue/Discovery/RecentFiles/Update/TextSend/TextReceived/FanOut），代码构建（与原 WinForms 对话框结构对应）并复用 Themes 样式，`DlgUi` 提供窗口初始化（尺寸钳制/深色 chrome）与控件工厂。行为与原版一致：对话框间传数据对象（不再解析格式化字符串）、Discovery/FanOut 分组行用禁用容器而非 Ip=null 占位、TextReceivedDialog 用户关闭仅隐藏、QueueDialog 底部用 DockPanel 防窄窗重叠。
- **NumericBox.xaml / NumericBox.xaml.cs** — 数值输入控件（WPF 无内建 NumericUpDown 的替代）：Min/Max 钳制 + 上下 spinner，Value 与文本双向同步。
- **UiChrome.cs / IconFont.cs** — DWM 窗口效果（深色标题栏/圆角/Mica，逐属性静默降级）与 Segoe 图标字体检测（Fluent Icons/MDL2 缺失时隐藏 glyph）。
- **QueuedTask.cs** — 队列任务 POCO（从原 MainForm.cs 摘出，字段与 `DisplayName` 不变）。
- **WireProtocol.cs** — **共享线协议核心**（0x00–0x06 全部逻辑唯一实现）。`IWireStream` 统一精确读/写字节流（`TcpWireStream` 包 `NetworkStream`，`UdtWireStream` 包 UDT socket）；`ServerWire`（接收端：0x00/0x01/0x02/0x03/0x04 处理、0x05 配对码认证（`WireAuth.HashCode` 恒时比较，未认证/错码回 0x15 status=1 拒绝，未启用时宽容接受）、0x06 文本接收（≤1MB，无独立响应帧，送达确认与文件语义一致）、`ReceiveFilePayload`、全文件校验、续传协商、文件夹续传扫描 `GetFolderSessionDir`）与 `ClientWire`（发送端：单文件/文件夹/分块/续传/文件夹续传、`SendAuthFrameAsync`（空码即 no-op）、`SendTextAsync`、`SendFilePayload` 双缓冲+令牌桶限速）；`WireCallbacks` 把日志/进度/错误/完成/文本接收事件回传给传输类；`ServerWireContext` 持有分块与续传状态及 `PairingCode`（`Shutdown()` 统一落盘）。0x02 分块返回 `WireOutcome{Success, IsChunked}`，让 TCP（无条件触发 per-client 完成）与 UDT（成功才 ACK）各自保留 ACK 语义。
- **TransferServer.cs** — TCP 传输薄封装：监听/接受循环/生命周期 + `ServerWireContext` 装配；协议处理全部委托 `ServerWire.HandleClientAsync`。`NoDelay = true`，LongRunning 接受循环。
- **TransferClient.cs** — TCP 客户端薄封装：`CreateClient()`（源端口绑定，失败抛 `PortBindException`）+ `ConnectAsync()`；发送逻辑委托 `ClientWire`。公开 API：`SendAsync()`/`SendFolderAsync()`/`SendChunkedAsync()`/`SendResumableAsync(sessionId, verifyHash)`。
- **TransferUdt.cs** — UDT 传输薄封装：`UdtNative` 引用计数 + Cdecl P/Invoke；`UdtDll` 提取嵌入 DLL；`UdtIo` 封装异步 I/O（**错误描述按调用在闭包内捕获**，随异常消息抛出——并发传输间无共享状态）；`TransferUdtServer`/`TransferUdtClient` 仅保留 UDT 特有部分（accept 循环、连接握手 `WaitForConnectionReady`、成功后 1 字节应用层 ACK），协议处理委托 `ServerWire`/`ClientWire`。`udt_listen` backlog 32。
- **ConcurrentTransfer.cs** — 多连接并发传输编排器。单文件切分为 N 个等大分块并行发送（独立连接+端口）。文件夹用 `SemaphoreSlim` 控制并发度。
- **Config.cs** — 键值配置持久化。`Get`/`GetInt`/`GetBool`/`Set`/`SetInt`/`SetBool`。启动时加载，关闭时保存。
- **Shared.cs** — `TransferProgress`/`FileEntry` 结构体。`ChunkTracker`（分块重组）。`SpeedLimiter`（令牌桶限速，`ThrottleAsync` 异步等待不阻塞线程池线程）。`Utils` 静态辅助（`FormatSize`、`ConstantTimeEquals`、`LogTo`、`SanitizeRelativePath`、`GetUniqueSavePath`、`FindFreePort`、`EmptyBytes`）。
- **ResumeState.cs** — 客户端续传状态持久化（含 `SourceMTime` 源文件变化检测）。
- **FolderResumeState.cs** — 客户端**文件夹续传会话**持久化（`%AppData%\TrFileTransfer\folder-resume`，同一键值格式）：SessionId/源文件夹/目标地址/进度/`IsSync`。服务器侧进度不落盘——磁盘文件本身就是状态，续传时由服务器扫描推导。**同步模式**基于此：`DeriveSyncSession(folder, ip, port, isUdt)` 由 SHA1 派生稳定会话 ID（大小写/尾斜杠不敏感），`IsSync=1` 的会话完成后不删状态（下次同步只传差异），且被 `ListAll()` 过滤、不进入续传对话框。
- **ServerResumeStore.cs** — 服务器续传状态落盘（`%AppData%\TrFileTransfer\server-resume`），服务器重启后按磁盘偏移恢复；`CleanupStale(7)` 在 TCP/UDT 服务器 Start 时调用，清理 7 天以上客户端未返回的孤儿会话文件。
- **DeviceDiscovery.cs** — UDP 设备发现：探测 `0xD1` → 响应 `0xD2`（名称/端口/协议位图 bit0=TCP bit1=UDT bit2=需配对），默认端口 45000（Config `DiscoveryPort`）；`DiscoveryServer.Start` 第 5 参传入配对开关。
- **HttpShare.cs** — **HTTP 共享模式**（TcpListener 手写 HTTP，无 HttpListener URL ACL/管理员要求）：浏览器（手机即可）列目录并下载共享目录文件。路由：`?p=` 目录列表、`?f=` 文件下载（Content-Length + RFC5987 中文文件名）；访问码（配对开启时复用配对码）经 `?t=` 贯穿所有链接，缺失/错误只回输入表单；路径经 `SanitizeRelativePath` + `GetFullPath` 前缀校验防穿越（**注意 `SanitizeRelativePath("")` 返回 `"_"`，空路径须先短路**）；仅图片/视频/音频/pdf/txt 内联（MIME 白名单），HTML/SVG 一律 attachment 防脚本窃取 token。`LanUrl` 优先 RFC1918 私有网段 IPv4（虚拟网卡地址靠后）。`HandleClient` 的异常记入 `OnLog`（勿静默吞）。Config `HttpSharePort`（默认 8090，服务器面板"HTTP:"数字框可改）。**上传**：列表页底部表单 POST multipart/form-data（`p` 表单字段 = 当前目录，落到所浏览的子目录）；服务端以 `NetBufReader`（64KB 缓冲、模式匹配后剩余字节保留在缓冲内）流式解析——先读 head 再扫 `

`，正文按 `
--boundary` 分段直接写盘（单请求上限 4GB，超限 413）；文件名不做反斜杠反转义（避免破坏路径剥离），`Path.GetFileName` 剥离后 `GetUniqueSavePath` 落盘；token 拒绝 POST 时先排空请求体再响应（否则关连接会 RST 掐断客户端读响应）；完成后 303 重定向回列表。
- **Updater.cs** — 自动升级。更新源二选一（`CheckAnyAsync` 按 URL 是否含 `api.github.com` 分发）：① 普通 key=value 文本清单（version/url/sha256/notes；`UpdateManifest.Parse` 对缺字段、非法版本号、非 http(s) URL、非 64 位 hex 哈希一律返回 null）；② **GitHub Releases（默认源）**——`https://api.github.com/repos/54gogogo10/FileTransfer_UDT/releases/latest`，`UpdateManifest.FromGitHubJson` 从 `tag_name`（剥掉 v 前缀）取版本、第一个 `.exe` 资产取下载地址、release `body` 作更新说明，SHA256 取 `<exe url>.sha256` 边车资产（缺失/格式非法直接抛异常拒绝更新，发布时必须随 exe 上传边车文件）。`Updater`：`CheckAsync`/`CheckGitHubAsync` 拉取（HttpWebRequest，`Proxy=null` 绕过系统代理，User-Agent 必填否则 api.github.com 拒绝）；`DownloadAsync` 下载到 `%AppData%\TrFileTransfer\updates` 并 SHA256 校验（不匹配删临时文件抛 `InvalidDataException`，进度回调在后台线程；GitHub 资产下载经 302 重定向到 objects.githubusercontent.com，HttpWebRequest 默认跟随）；`Apply(staged, target)` 换文件——Windows 允许重命名运行中的 exe：旧 exe 改名 `<exe>.old`、新文件复制到原位、复制失败自动回滚；`DeleteStaleBackup` 于启动时清理残留备份；`CurrentVersion` 取 Assembly 版本。UI 入口：表头"检查更新"按钮 + 托盘菜单项 → `UpdateDialog`（Config `UpdateUrl`/`AutoUpdateCheck`，下载进度条，确认后交 `MainForm.ApplyUpdateAndRestart`：Apply→SaveConfig→启动新 exe→`_trayExit=true` 退出）。启动后延迟 3 秒后台静默检查（仅当 `AutoUpdateCheck` 开启；发现新版打开对话框，失败静默）。
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

新增日志消息时遵循以上命名约定添加到 `L10N.cs`。禁止使用内联 `L.IsChinese ? "..." : "..."` 三元表达式。语言切换时 `PopulateBindAddresses()` 重新读取网卡并更新第一个下拉项标签。传输过程中语言切换被禁用（下拉框不可用）。WPF 层沿用显式 `ApplyLanguage()` 刷新模式（`L` 为纯静态、无变更通知）；对话框文案在构造时一次性设置，语言切换不刷新已打开的对话框。

### 文件名冲突处理

TCP 和 UDT 服务器都使用 `Utils.GetUniqueSavePath`，在基础文件名（扩展名前）追加 `_1`、`_2` 等后缀，解决与保存目录中已有文件/目录的名称冲突。

### 线程安全模式

传输类使用 `volatile bool _isRunning` 标志和 `CancellationTokenSource`。UI 事件处理器经 `MainWindow` 的 `RunOnUi`/`RunOnUiSync`（Dispatcher 封送，窗口已关闭或 Dispatcher 停止时静默丢弃）。触发事件前使用局部变量快照模式：`var handler = OnXxx; if (handler != null) handler(...);`。

## TCP 线协议

所有传输以 1 字节类型开始：`0x00` = 单文件，`0x01` = 文件夹，`0x02` = 分块文件，`0x03` = 断点续传（单文件），`0x04` = 文件夹断点续传（清单 + 0x11 协商，完整字节布局见 `WIRE-PROTOCOL.md`），`0x05` = 配对码认证前导帧（可选，0x15 响应），`0x06` = 文本消息（≤1MB UTF-8，无响应帧）。

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

文件夹断点续传（0x04）的服务器保存目录由会话确定性推导（`ServerWire.GetFolderSessionDir`：`<saveDir>/<folderName>.<sessionId 前 8 位>`），同一会话跨重启映射到同一目录；续传扫描对"大小相符"的文件用清单中的全文件哈希二次校验后才跳过。

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

权威源码树是 `udt-sdk\udt4\src\`。根目录下未跟踪的 `udt-master\`（UDT4 原版副本）、`udt-build\`、`udt-build2\`（带外编译产物 .o/.dll）是工作副本/产物，勿在其内改动，也不要将其纳入提交。

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

`AcceptLoop` 中 IPEndPoint 构造：`sin_port` 需 `NetworkToHostOrder` 16 位字节交换（`(ushort)` 强转避免符号扩展，临时端口 ≥49152 不会变负数）；`sin_addr` **不做字节交换**——结构体字段小端读取网络字节序字节后恰好就是 `IPAddress(long)` 期望的布局（对照 `IPAddress.Loopback == 0x0100007F`），再交换会显示颠倒（如 1.0.0.127）。

### 客户端架构

`TransferUdtClient`：`RunUdtTransfer`（生命周期管理 + `.ConfigureAwait(false)`）→ `SendFileInternal`/`SendFolderInternal`。

连接流程：`udt_socket()` → `udt_connect()` → `SetTimeout(30s)` → **`WaitForConnectionReady()`**（UDT connect 握手为异步，轮询空 `udt_send()` 直到 socket 从 BOUND 进入 CONNECTED 状态，最多 30 次 × 200ms）→ 发送 TCP 线协议头部 + 数据 + SHA256。

`_socket` 声明为 `volatile`，防止 `Cancel()` 与 `finally` 块之间的重入竞态。连接/socket 失败抛出异常（触发 `OnError` + UI 重置），而非静默返回。

## 边界情况与异常处理

- `ObjectDisposedException` 和 `InvalidOperationException` 在所有传输循环和外部 try-catch 块中静默捕获——`Stop()`/`Cancel()` 关闭套接字期间 I/O 正在执行时会出现这些异常。
- `HandleResumableCore`（TCP 与 UDT）的 finally 块**只 Save 不 Delete**——`Stop()` 可能已落盘并清空字典，盲目 Delete 会丢失磁盘检查点；`ServerResumeStore.Delete` 仅在显式成功/失败路径（status 2、status 3、增量哈希失败）调用。落盘前先 `Flush()`，保证持久化的 ReceivedBytes 与磁盘实际数据一致。
- TCP 服务器对 0 字节文件特殊处理：不读数据块、直接读后续 32 字节哈希（否则空文件会被误判为连接关闭）。
- UDT 客户端 `SendFilePayload` 的所有分块（含最后一块）都必须经过 `_limiter.Throttle`——遗漏最后一块会导致 ≤4MB 的文件完全不限速。
- **构造函数重载陷阱**：`TransferClient`/`TransferUdtClient` 的 5 参数签名是 `(ip, port, path, localPort, bufferSize)`，不是 `(bufferSize, speedLimit)`。UI 一律通过 `ClientFactory.CreateTcp/CreateUdt(ip, port, path, srcPort, speedLimit)` 构造（内部走 6 参数全签名，localPort=0 表示随机，缓冲区恒为 4MB）——曾经把 4194304 误传成源端口、speedLimit(0) 误传成缓冲区大小，导致 FileStream 抛"要求正数"，UI 上所有普通发送失败。集成测试 `Integration_Factory_TCP/UDT` 以相同形状构造客户端做真实传输，防止该形状再次回归。
- **源端口绑定失败**：客户端绑定 `localPort` 失败时抛 `PortBindException`（Shared.cs，绑定发生在发送任何字节之前，可安全重试）；`ConcurrentTransfer` 捕获后经 `Utils.FindFreePort` 换口重试最多 3 次——并行分块绑定连续端口时可能与系统临时端口竞争（此前 TCP_LargeConcur 偶发 ADDRINUSE 即此原因）。注意 TCP 客户端的 0x00/0x01 路径（SendFileInternal/SendFolderInternal）此前用裸 `new TcpClient()` 未绑定 localPort（UI"源端口"选项对普通发送无效），已统一走 `CreateClient()` 修复；UDT 侧所有路径本就经 `UdtConnect` 绑定。
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

## 文档

- `WIRE-PROTOCOL.md` — 线协议规范（各类型详细字节布局）。改动 0x00–0x03 协议时先读它，并同步更新本文件的"TCP 线协议"章节。
- `docs/superpowers/plans/` 与 `docs/superpowers/specs/` — 主要功能的实施计划与设计文档（并发传输、断点续传、2026-08 增强批次）。改动对应功能前建议先读对应设计文档。

## 运行时要求

- Windows 7 SP1 或更高版本
- .NET Framework 4.8 或更高版本（Win10 1809+/Win11 内置；Win7 SP1 需手动安装 .NET Framework 4.8）
