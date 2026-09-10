# TrFileTransfer

局域网文件传输工具，带 SHA-256 完整性校验。Windows GUI（WinForms）。

## 功能

- **TCP** — 面向连接，防火墙友好
- **UDT** — UDP-based Data Transfer Protocol（STREAM 模式，可靠有序字节流，类 TCP 语义）
- **单文件**或**文件夹**（递归，保留目录结构）
- **并发传输** — 单文件切分为多块并行发送（多连接）；文件夹最多 N 个文件并行发送（SemaphoreSlim 控制）
- **多客户端并发** — 服务端同时接受多个客户端传输，每个独立进度卡片（TCP 用 `_tcpCards`、UDT 用 `_udtCards` 字典）
- **监控模式** — 持续监控目录，自动发送新文件
- **配置持久化** — 地址、端口、并发数、语言等设置自动保存到 `%AppData%`
- **English / 中文** 可切换 UI
- 收发进度面板左右分列，每个传输独立进度卡片（进度条 + 速度），完成后自动移除

## 快速开始

### 环境要求

- Windows 7 SP1 或更高版本
- .NET Framework 4.5 或更高版本（推荐 4.6.1+）

### 构建

```
cd TrFileTransfer
build.bat
```

生成带版本号的 `release\TrFileTransfer-<版本>.exe`（附 `.sha256`），另留一份不带版本号的 `TrFileTransfer.exe` 供 CI/发布工作流使用。需要 .NET SDK 8+。

UDT DLL 编译（如更新原生代码）需 MinGW-w64，详见 CLAUDE.md。

### 使用

1. **接收方** — 左侧服务器面板：选择 TCP 或 UDT，选择保存目录，点击*启动服务器*
2. **发送方** — 右侧客户端面板：输入服务器 IP，浏览文件或文件夹，设置并发数（1-16），点击*发送*
3. 勾选*文件夹模式*可发送整个目录（文件夹内每个文件独立传输）
4. 勾选*监控模式*可持续监控目录——启动时先扫描已有文件并入队，然后检测新文件自动发送
5. 设置源端口（0 = 随机端口）

## 协议

完整 TCP 和 UDT 线协议规范参见 [WIRE-PROTOCOL.md](WIRE-PROTOCOL.md)。

## 架构

| 文件 | 职责 |
|------|------|
| `Program.cs` | 入口 |
| `MainForm.cs` | WinForms UI（代码构建，无设计器） |
| `TransferClient.cs` | TCP 发送端，支持分块（0x02）和源端口绑定 |
| `TransferServer.cs` | TCP 接收端，支持分块聚合（ChunkTracker） |
| `TransferUdt.cs` | UDT STREAM 传输（UdtNative P/Invoke + TransferUdtServer + TransferUdtClient），复用 TCP 线协议 |
| `ConcurrentTransfer.cs` | 多连接并发传输编排器（单文件分块 / 文件夹 SemaphoreSlim） |
| `Config.cs` | 键值配置持久化（`%AppData%\TrFileTransfer\`） |
| `Shared.cs` | `TransferProgress`、`FileEntry`、`ChunkTracker`、`Utils`（含 `FindFreePort`） |
| `L10N.cs` | 中英文字符串 |

## 许可证

MIT
