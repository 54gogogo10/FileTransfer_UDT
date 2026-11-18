# C# TrFileTransfer ↔ Rust trft 真机互通测试报告

- 日期：2026-11-18
- 被测：本仓库构建的 **C# TrFileTransfer 2.16.0.0**（互通测试时程序集版本为 2.14.0.0，代码相同，仅版本号差异）↔ `D:\zcode\TrFileTransfer_v2` 的 **Rust trft 0.2.0**（首轮）；2026-11-18 另用 **0.3.0** 全矩阵复测（见 §4）
- 拓扑：发送端/接收端分处两台 Windows 机器，同一 LAN
  - C# 侧本机：`10.143.208.78`
  - 对端 VM：`10.143.208.49`（gogo；.NET Framework 4.8.1；活动网络配置文件 Public，防火墙关闭）
- 载荷：2 MiB 随机数据，SHA-256 `26c81e35175d692666df28f233a5519ed1540b930845a18f53078f23b74d13bc`
- 二进制：
  - C# 发布构建（含 v2 单向 UDP）`release\TrFileTransfer-2.16.0.0.exe`（3,607,040 B，SHA-256 `ab921177cd5a76189b1902cbd6d71dfd0cb42cab7df2ba6fc9a4032a4044b1f5`；互通测试使用同一代码的 2.14.0.0 构建）
  - Rust `target\release\trft.exe`（9,401,856 B，SHA-256 `3c0d39b6cbbf72c5f5d18a1dea9252cf74cc0e86f93e3050650a5743728b6696`）
- 方法：接收端统一放在 VM（通过计划任务启动，日志与退出码落盘），发送端在本机；每个用例使用全新空目录，接收文件用 `Get-FileHash` 与源比对。VM 侧产物保留在 `C:\Users\gogo\trft-interop\`。
- 构建口径：A1/B1 与首轮 A2/B2/B3 使用同一工作树的较早构建（此后仅改了日志文案与收尾 linger，协议字节未变）；A2f/A4g/B2c/B3b 在最终构建上复测；C3–C6 为 2.16 新增的 v2 单向 UDP 实现（最终构建，上表 SHA）。

## 1. 结果矩阵

| # | 协议 | 方向 | 结果 | 接收 SHA-256 | 备注 |
|---|---|---|---|---|---|
| A1 | TCP 明文（压缩协商） | C# → Rust | **PASS** | 一致 | C# 日志：`Transfer compression enabled`；Rust 收满 |
| A2f | TCP 明文 | Rust → C# | **PASS** | 一致 | Rust 用 0x03；C# 日志 `Resume: ... status=0` |
| A3b | TCP 加密（配对码 424242） | C# → Rust | **PASS** | 一致 | C# 先 0x0C 被旧对端拒绝，提示后重连 0x09 并成功加密 |
| A4g | TCP 加密（配对码 424242） | Rust → C# | **PASS** | 一致 | C# 接受旧版 0x09 并记录弱握手警告，随后加密 + 压缩 |
| B1 | UDT 明文 | C# → Rust | **PASS** | 一致 | C# 使用内嵌 UDT4，Rust 为真 UDT4 |
| B2c | UDT 明文 | Rust → C# | **PASS** | 一致 | 见 §2.2：C# 接收端 `--count 1` 增加 400 ms 收尾等待后，Rust 发送端 rc=0 |
| B2（修复前） | UDT 明文 | Rust → C# | 数据 PASS / 发送端 rc=1 | 一致 | Rust 报 `early eof`（v2 已知竞态）；本轮已在 C# 侧消除 |
| B3b | UDT 加密 | C# → Rust | **PASS** | 一致 | Rust 接收端保持运行；C# 0x09 回退 + AES + 压缩 |
| B3（Rust `--count 1`） | UDT 加密 | C# → Rust | 数据 PASS / 发送端 rc=1 | 一致 | C# 报 `Connection closed before the receiver acknowledged`；Rust 收满即退，最后一字节 ACK 丢失（对端缺陷） |
| C1 | 单向 UDP v1 | C# → Rust | **FAIL（预期）** | 无文件 | C# 默认 `TU/v1` 帧，Rust 只认 v2（保留为回归基线） |
| C2 | 单向 UDP v1 | Rust → C# | **FAIL（预期）** | 无文件 | 同上 |
| C3 | 单向 UDP v2（2.16） | C# `--udp-v2` → Rust | **PASS** | 一致 | Rust `[接收-UDP] 收到 payload.bin` |
| C4 | 单向 UDP v2 | Rust → C# | **PASS** | 一致 | C# 日志 `Rust-compatible (UDP v2) session ... hash verified` |
| C5 | 单向 UDP v2 + FEC 丢包 | C# → Rust（每组丢 1 片，共 3 片） | **PASS** | 一致 | Rust 用 C# 奇偶单轮恢复 |
| C6 | 单向 UDP v2 + FEC 丢包 | Rust → C#（每组丢 1 片，共 3 片） | **PASS** | 一致 | C# 用 Rust 奇偶单轮恢复 |

> 「PASS」判据：接收端文件存在、长度 = 2,097,152、SHA-256 与源逐字节一致；发送端退出码单独标注，不与数据正确性混在一起。

## 2. 发现与处理

### 2.1 新版 0x0C 握手对旧对端的回退在真机上成立

C# 客户端配置配对码后先发 `0x0C`。Rust 0.2.0（C# 2.14 方言）不认识该帧，服务端记录：

```
[连接失败] 认证失败: 10.143.208.78:22208: 需要配对码
```

C# 客户端据此重连并改用 `0x09`，随后 `Encrypted session established (AES-256)` + 压缩，传输与哈希均正确。反向（Rust → C#）时 C# 服务端接受 `0x09` 并记录：

```
Peer used the legacy 0x09 handshake, whose pairing-code hash crosses the wire (offline-crackable) — upgrade both ends.
Connection encrypted (AES-256).
Connection using transfer compression (deflate).
```

这与设计一致：0x0C 失败只在「允许降级」时重连 0x09，且新服务端保留 0x09 处理器并记警告。

### 2.2 UDT 最后一个 ACK 与「收满即退」的竞态（双方都受影响）

现象：接收端用 `--count 1` 收满后立即退出，最后 1 字节应用层 ACK 可能在 UDT 关闭时丢失；发送端于是把「文件已完整、哈希一致」的传输报成失败：

- **Rust → C#（本轮已修）**：Rust 发送端 rc=1 `early eof`。在 C# CLI 接收端收满后增加 400 ms 收尾等待（`Cli.LingerAfterCount`）再关服务，复测 rc=0。这也是 v2 自己在 `TECH-STACK.md` 登记的已知缺陷（「C# `recv --count 1` 收满即退与 v2 终 ACK 读竞态」）。
- **C# → Rust（对端未修）**：Rust 接收端 `--count 1` 收满即退，C# 发送端读不到 ACK，报 `Connection closed before the receiver acknowledged`；文件与哈希正确，接收端保持运行时不复现（B3b rc=0）。建议 Rust 侧加同样的收尾等待，或在互通验收里按 v2 的既有判据「完整送达即 PASS + 误报标注」记录。

### 2.3 单向 UDP：v1 不互通，v2 已实现并验证

C# 的 v1 用 `TU` 魔数 + 版本 1 头；Rust 的 `--proto udp` 是 v2 帧（版本 2、u64 会话/序号），所以 C1/C2 按设计失败。

**2.16 新增 v2 收发**：接收端按首字节自动分流（`0x02` = v2，`'T'` = v1），发送端用 `--udp-v2`（GUI「Rust 兼容（UDP v2）」/ Config `UdpRustCompat`）显式选择，默认仍是 v1 以免旧 C# 接收端静默丢包。实现要点：会话 ID = SHA256(名‖size‖mtime 纳秒) 前 8 字节 LE；START/END 各 3 份 40ms；FEC 为 4 分片/组**系统化 Vandermonde**（GF(256)/0x11D，矩阵 V·inv(V_top)）；START 尾段 32B 哈希使“收齐即校验发布”，END 仅兜底。C3/C4 为双向无丢包基线；C5/C6 用本地 UDP 中继按组丢弃 3 个 DATA 分片，两端各自仅靠对端奇偶**单轮**恢复并 SHA-256 一致——证明奇偶矩阵/分片布局逐字节兼容。另有 `Unit_UdpFec_VandermondeRustVector` 用 Rust `reed-solomon-erasure 6.0` 生成的固定向量钉住奇偶字节（`cc8485f24adb3cd7` / `9ba1418882bb3fff`）。

注：v2 文档中「单向 UDP 恒为 Rust↔Rust」是**当时**的口径；本仓库 2.16 补齐了 C# 方言的 v2 收发（`WIRE-PROTOCOL.md` 新增「单向 UDP v2」）。

### 2.4 测试期间顺手修的两处 C# 侧问题

1. **诊断文案**：0x0C 失败改用 0x09 重试时，旧日志写「回退明文」，实际仍加密。新增 `C_EncryptLegacyRetry` 并改用之（A3b 已验证）。
2. **UDT 收尾竞态**：`Cli` 的 `--count` 退出增加 400 ms linger（见 §2.2），TCP/UDP 退出路径同样适用，成本仅一次 0.4 s。

## 3. 复现命令

VM（接收端，计划任务里执行）：

```bat
:: TCP 明文（Rust 收）
trft.exe recv --proto tcp --port 18080 --out C:\Users\gogo\trft-interop\a1\recv --count 1
:: TCP 明文（C# 收）
TrFileTransfer.exe recv --port 18081 --out C:\Users\gogo\trft-interop\a2f\recv --count 1
:: TCP 加密（两端同码）
trft.exe recv --proto tcp --port 18102 --out C:\Users\gogo\trft-interop\a3b\recv --count 1 --code 424242
TrFileTransfer.exe recv --port 18122 --out C:\Users\gogo\trft-interop\a4g\recv --count 1 --code 424242
:: UDT
trft.exe recv --proto udt --port 18090 --out ... --count 1
TrFileTransfer.exe recv --port 18112 --out ... --count 1 --udt
:: 单向 UDP v1（不互通的回归基线）
trft.exe recv --proto udp --port 18092 --out ...
TrFileTransfer.exe recv --port 18093 --out ... --udp
:: 单向 UDP v2（2.16 互通）
trft.exe recv --proto udp --port 18280 --out ...
TrFileTransfer.exe recv --port 18291 --out ... --udp   :: 接收端自动识别 v2
TrFileTransfer.exe send --ip 127.0.0.1 --port 18280 --file payload.bin --udp --udp-v2 --passes 1
```

本机（发送端）：

```powershell
& 'E:\zcode\TrFileTransfer\TrFileTransfer\release\TrFileTransfer-2.16.0.0.exe' send --ip 10.143.208.49 --port 18080 --file payload.bin
& "$env:TEMP\trft-interop-local\trft.exe" send --proto udt --ip 10.143.208.49 --port 18112 --file payload.bin
& '...\TrFileTransfer-2.16.0.0.exe' send --ip 10.143.208.49 --port 18110 --file payload.bin --udt --code 424242
```


## 4. 0.3.0 复测（2026-11-18 15:40–15:55）

Rust 侧已发布 **trft 0.3.0**（`release\TrFileTransfer-0.3.0.exe`，9,436,160 B，SHA-256 `cff95fcdea5c8c46f89c0d8382ae3cdec4f501323354f97e369b434024653c37`），其中包含 count 达标排水收尾与终态 ACK linger 修复。C# 侧仍为同一代码（测试构建 3,607,040 B；发布构建 2.16.0.0 仅版本号不同，SHA-256 `ab921177…`），**未为 0.3.0 改动任何代码**。

| # | 场景 | 方向 | 结果 | SHA-256 |
|---|---|---|---|---|
| T1 | TCP 明文 | C# → Rust | **PASS** | 一致 |
| T2 | TCP 明文 | Rust → C# | **PASS** | 一致 |
| T3 | TCP 加密（424242） | C# → Rust | **PASS**（0x0C 探测被拒 → 0x09 重连加密） | 一致 |
| T4 | TCP 加密（424242） | Rust → C# | **PASS**（C# 接受 0x09 并记弱握手警告） | 一致 |
| U1 | UDT | C# → Rust，Rust 端 `--count 1` | **PASS**，C# rc=0 | 一致 |
| U2 | UDT | Rust → C#，C# 端 `--count 1` | **PASS**，Rust rc=0（`完成`） | 一致 |
| D1 | 单向 UDP v2 | C# `--udp-v2` → Rust | **PASS** | 一致 |
| D2 | 单向 UDP v2 | Rust → C#（接收端自动识别） | **PASS** | 一致 |
| D3 | 单向 UDP v2 + FEC（丢 seq 100/500/1000） | C# → Rust | **PASS**（Rust 用 C# 奇偶单轮恢复） | 一致 |
| D4 | 单向 UDP v2 + FEC（同上丢包） | Rust → C# | **PASS**（C# 用 Rust 奇偶单轮恢复） | 一致 |

**关键结论**：0.3.0 的收尾修复消除了 §2.2 记录的 UDT「收满即退」误报——U1/U2 在各自对端 `--count 1` 时**双方退出码均为 0**，不再出现 `early eof` / `Connection closed before the receiver acknowledged`。单向 UDP v2 的线格式、START 哈希收齐发布语义与 4+2 Vandermonde 奇偶在 0.3.0 上保持兼容，无需 C# 侧变更。

## 5. 结论

- **TCP**：C# ↔ Rust 双向可用；明文与配对码加密（0x09 回退）均通过，压缩协商正常。
- **UDT**：C# ↔ Rust 双向数据正确；收满即退的 ACK 竞态在 C# 接收端已修，Rust 接收端仍会把成功传输误报为失败（文件与哈希正确），建议对端跟进。
- **单向 UDP**：v1 帧不互通（双方协议不同）；**2.16 起 v2 帧双向互通**，含 FEC 丢包单轮恢复（C3–C6）。
- 测试同时验证了本轮安全改动的兼容性：0x0C 首选、0x09 回退、0x0A bit2 未造成协商回归、连接/读取超时未影响正常传输。
