# TrFileTransfer

局域网文件传输工具，全程 SHA-256 完整性校验，支持断点续传、增量同步与传输加密。WPF GUI，单文件 exe，免安装。

## 功能

### 传输协议

- **TCP** — 面向连接，防火墙友好；支持 IPv4 / IPv6 / 双栈监听
- **UDT** — 基于 UDP 的可靠有序字节流（STREAM 模式），适合高丢包链路
- **裸 UDP 单向发送** — 面向无回传链路：接收方落盘即状态，重发即重试，跨重启续收（无配对码/加密/压缩）
- **单文件**或**文件夹**（递归，保留目录结构），**并发传输**（单文件切分多连接并行，1–8 路）
- **多客户端并发** — 服务端同时接受多个客户端，每个独立进度卡片
- **监控模式** — 持续监控目录，新文件自动发送；**群发** — 一次并行发送到多台设备
- **文本互传** — 双向发送短消息；**设备发现** — UDP 广播扫描局域网设备

### 可靠性

- **断点续传** — 单文件（0x03）与文件夹（0x04 清单协商）均支持，服务端偏移权威、重启后按磁盘恢复；发送失败自动重试续传
- **暂停 / 继续** — 单文件与并发分块发送均可暂停，继续时只补缺口（并发分块经覆盖率查询跳过已收区间）
- **同步模式（增量备份）** — 同一文件夹重复发送只传差异部分；CLI `sync` 适合任务计划程序定时备份
- **传输压缩** — Deflate 分段压缩，写前采样判定，不可压缩数据自动直传不膨胀（可关）
- **去重与摘要缓存** — 相同内容的文件自动跳过；磁盘摘要缓存让重复同步两侧都不重读文件
- **文件库校验（scrub）** — 重算保存目录全部文件哈希，检出事后损坏/被改动的文件

### 安全

- **传输加密** — 认证 ECDH（P-256）会话密钥 + AES-CTR + HMAC，配对码仅用于双向确认、不上网传输；旧对端自动降级明文并可配置严格模式拒绝降级
- **配对码认证** — 4–12 位数字码，恒时比较，按来源 IP 计数锁定防爆破
- **接收控制** — 接收前确认（未知设备/全部）、IP 白名单/黑名单（支持通配）、按设备分目录、重复文件改名或跳过
- **HTTP 共享访问码** — Cookie 会话、失败锁定、路径穿越防护，HTML/SVG 强制下载防脚本

### 便捷

- **HTTP 共享** — 保存目录变网盘：手机浏览器列目录、上传、下载（支持断点续传 Range），二维码扫码直达
- **发送队列** — 批量串行执行、失败重试，完成后可自动关机/休眠/执行命令
- **传输统计与历史** — 收发总量按日/设备聚合，历史记录可导出 CSV
- **自动升级** — GitHub Releases 检查更新、校验 SHA256 后原子换入重启
- **托盘常驻**、完成气泡通知、开机自启、日志持久化、深色/浅色/跟随系统主题（Win11 Mica 可选）
- **中英双语** UI，运行时可切换

## 快速开始

### 环境要求

- Windows 7 SP1 或更高版本
- .NET Framework 4.8（Win10 1809+/Win11 内置）

### 构建

```
cd TrFileTransfer
build.bat
```

生成带版本号的 `release\TrFileTransfer-<版本>.exe`（附 `.sha256`），另留一份不带版本号的 `TrFileTransfer.exe` 供 CI/发布工作流使用。需要 .NET SDK 8+。

UDT DLL 编译（如更新原生代码）需 MinGW-w64，详见 [AGENTS.md](AGENTS.md)。

### 使用

1. **接收方** — 服务器面板按协议选 TCP / UDT / UDP 标签页，勾选绑定地址、设置端口，点击*启动*
2. **发送方** — 客户端面板输入服务器 IP 与端口，选择协议，浏览文件或文件夹，设置并发数（1–8），点击*发送*
3. 两端开启*配对码*并填入相同的码即可启用加密与认证
4. 勾选*文件夹模式*发送整个目录，勾选*同步模式*则变为增量备份（重复发送只传差异）
5. 发送中可随时*暂停*，完成后*继续*只传剩余部分

### CLI（无头模式）

exe 内置命令行入口（退出码 0=成功 1=错误 2=用法）：

```
TrFileTransfer.exe send --ip 192.168.1.10 --port 12345 --file <路径> [--tcp|--udt] [--code <配对码>] [--limit <KB/s>]
TrFileTransfer.exe recv --port 12345 --out <目录> [--tcp|--udt] [--code <配对码>] [--count <N>]
TrFileTransfer.exe sync --folder <目录> --ip 192.168.1.10 --port 12345   # 一次性增量同步
TrFileTransfer.exe verify --dir <目录>                                    # 按接收时记录的摘要核对文件库
```

## 协议

完整 TCP / UDT / 裸 UDP 线协议规范（0x00–0x0B 全部帧布局）参见 [WIRE-PROTOCOL.md](WIRE-PROTOCOL.md)。

## 架构

逻辑层 + WPF UI 编译为单个 exe。主要源文件：

| 文件 | 职责 |
|------|------|
| `App.xaml.cs` | 入口：GUI / CLI 分流、全局异常兜底、主题初始化 |
| `MainWindow.xaml(.cs)` | WPF 主窗口：服务器三协议标签页、客户端面板、队列/续传/群发/同步 |
| `WireProtocol.cs` | 线协议唯一实现：收发两侧全部类型处理、加密/压缩握手、清单构建、摘要缓存 |
| `WireCrypto.cs` / `WireCompress.cs` | 会话加密（ECDH + AES-CTR + HMAC）与分段 Deflate 压缩装饰器 |
| `TransferClient.cs` / `TransferServer.cs` | TCP 收发端薄封装 |
| `TransferUdt.cs` | UDT 传输（原生库 P/Invoke、引用计数、DLL 提取） |
| `TransferUdp.cs` | 裸 UDP 单向传输（磁盘即状态的断点续收） |
| `ConcurrentTransfer.cs` | 多连接并发编排：分块并行、瞬断重试、覆盖率续传 |
| `DeviceDiscovery.cs` | UDP 广播设备发现与配对位 |
| `HttpShare.cs` | 手写 HTTP 服务器：列目录/上传/下载/Range/二维码 |
| `ResumeState.cs` / `FolderResumeState.cs` / `ServerResumeStore.cs` | 两侧续传会话持久化 |
| `LibraryVerifier.cs` | 文件库校验（scrub） |
| `StatsStore.cs` / `Updater.cs` / `QrCode.cs` | 统计存储、自动升级、零依赖二维码编码器 |
| `Config.cs` / `L10N.cs` / `Shared.cs` | 配置持久化、中英文本、公共工具（限速器/分块重组/端口探测等） |
| `Themes/` + `Dialogs/` | 深浅主题 token 与控件模板、12 个代码构建对话框 |

## 测试

```
cd TrFileTransfer\Tests && build.bat
TrFileTransfer.Tests.exe
```

自定义轻量 TestRunner（无外部框架依赖）：单元测试（L10N、配置、加密/压缩往返、摘要缓存、QR、限速等）+ 约 70 项 TCP/UDT/UDP 端到端集成测试（含续传、同步、加密回退、配对码、HTTP 共享、IPv6 双栈等）。退出码 0=全部通过。

## 许可证

MIT
