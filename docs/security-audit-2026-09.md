# TrFileTransfer 代码审计 + 安全审计报告

日期：2026-09-10
审计对象：`TrFileTransfer` v2.11.0.0（审计基线 git 提交 `7751238`）
方法：5 路并行深度审计（加密/认证、HTTP 共享/发现/升级、文件系统/路径、核心协议、UI/CLI）+ 对严重/高危结论的逐行人工复核。

---

## 一、结论摘要

路径遍历防护、常量时间比较、加密-然后-MAC 顺序、压缩/加密分层、C# 5 语法约束这几块本来就做得扎实。真正的问题集中在**加密握手的密钥来源**、**续传偏移的信任模型**、**TCP 缺少正向确认**三处，其中两处不需要攻击者、正常使用即可触发。

共修复 1 个严重、6 个高危、以及约 14 个中危和若干低危问题。

| 级别 | 数量 | 代表 |
|---|---|---|
| 严重 | 1 | 加密会话密钥 = 明文传输的配对码哈希（已修复：认证 ECDH 0x09） |
| 高危 | 6 | 续传偏移被信任、明文降级可强制、TCP 无正向确认、6 位码无限制、续传会话可跨连接劫持、更新器同源投毒 |
| 中危 | ~14 | 预认证内存放大、磁盘预检溢出、UDT ACK 生命周期、路径 TOCTOU、配置竞态 fail-open、UI 取消/监控状态机等 |
| 低危/信息 | ~12 | 日志注入、非原子落盘、多实例竞态等 |

---

## 二、严重问题（已修复）

### S1. 认证 ECDH 取代「明文 PSK」握手
**原问题：** `0x07` 帧把 `SHA256(配对码)` 与 salt **明文**发给对端，两端直接用该哈希当 PSK 派生 AES/HMAC 密钥。任何抓到握手的被动窃听者（LAN 嗅探）都能重算密钥、解密全部内容——「加密传输」形同虚设。

**修复：** 新增 `0x09` 认证 ECDH 握手（`WireProtocol.HandleEcdhAuthAsync` / `ClientWire.TryEcdhHandshakeAsync`）：
- 双方临时 P-256 交换公钥（72 字节 BCRYPT blob），会话密钥来自 ECDH 共享秘密 `Z`——**网线上不出现任何可推出密钥的材料**。
- 配对码仅用于认证：`authKey = SHA256("tf-ecdh-auth" ‖ Z ‖ PBKDF2(配对码, salt=SHA256(clientPub‖serverPub), 50000))`，双方以 `confirmS`/`confirmC` 做 HMAC 确认——只有同时掌握配对码与 ECDH 私钥的一方能完成握手，主动中间人无法在不猜出配对码的前提下通过。
- 旧 `0x07` 仅保留供 ≤2.11 对端互通，并在服务器日志记录弱加密警告。

密钥派生见 `WireCrypto.cs`（`DeriveAuthKey`/`Confirm`）；协议见 `WIRE-PROTOCOL.md`。

---

## 三、高危问题（已修复）

### H1. 续传偏移服务端权威化
原 `long actualStart = Math.Max(clientOffset, resumeFrom)` 允许客户端声明偏移大于服务端实收字节，中间 `[resumeFrom, clientOffset)` 区间零填充且从不校验，可让全零文件被判「接收完成」。现改为服务端权威：`actualStart = resumeFrom`，客户端声明仅作日志参考；客户端侧同步改为 `actualStart = serverOffset`（不再取 max）。

### H2. 拒绝静默降级明文
原客户端对任何 `IOException` 或非 `0x17` 应答都静默回退明文 0x05，攻击者 RST 即可强制降级。现新增 `EncryptionDowngradeAllowed`：默认允许（保持与旧版互操作），Config `EncryptStrict=1` 时**失败关闭**；且新客户端不再使用弱 `0x07`，只走 `0x09` 或明文 0x05。

### H3. TCP 完成确认字节
`0x00`/`0x01`/`0x06` 原本无任何正向应答，TCP 上接收方拒绝（磁盘不足/接收确认被拒/IP 过滤）时发送方仍报「完成」——静默丢文件。现接收端穿过装饰器流写回 1 字节（`0x01` 接受 / `0x00` 拒绝），发送端读取后才报完成。仅当握手证明对端为新版（加密/压缩前导帧被接受）时才等待，保持与旧服务器互操作；UDT 沿用带外 ACK，线格式不变。

### H4. 配对码失败锁定
`ServerWireContext.MaxAuthFailures`（默认 10）按来源 IP 累计失败次数，超限后即使是正确配对码也拒绝（成功即清零）；在 `0x05`/`0x07`/`0x09` 三条认证路径均生效。

### H5. 续传会话绑定与并发锁
`ResumeStates` 改为同时校验对端 IP（`ResumeState.Peer`，落盘）与文件名；新增 `ResumeLocks`（每个 sessionId 一个 `SemaphoreSlim`，跨连接共享），第二条并发连接直接拒绝，杜绝连接间共享 FileStream 导致的偏移交错/数据损坏。

### H6. 更新器信任缺口（未改代码，记录风险）
`Updater` 的 SHA256 边车与清单同源获取、且接受 `http://` 源；恶意/被劫持的明文源可同时投递「恶意 exe + 匹配哈希」。默认 GitHub 源为 HTTPS，风险限于用户自配的 http 源。**建议后续**：强制 https + 用内嵌公钥对清单签名。

---

## 四、中危问题（已修复）

| # | 问题 | 修复 |
|---|---|---|
| M1 | 压缩前导帧在认证前被接受 → 预认证内存放大 | 服务器仅在 `authSeen` 或未配置配对码时接受 `0x08` |
| M2 | 磁盘预检 `fileSize + 64MB` 整数溢出绕过 | 新增 `Utils.HasFreeSpaceFor` / `MaxTransferSize`（1 PiB），所有声明大小上千处校验 |
| M3 | UDT ACK 发到已关闭 socket | `CompressedWireStream`/`EncryptedWireStream` 增 `ownsInner`，服务器侧传 false；socket 生命周期归传输层 |
| M4 | 0x04 续传完成后无全文件哈希校验 | 组装后对 manifest 哈希做 `VerifyFullHashFile` 二次校验 |
| M7 | 半成品直接写最终路径 | `ReceiveFilePayload` 改为 temp 文件 + 校验 + `File.Move` 原子改名 |
| M8 | `Config` 无锁；`GateAsync` 异常 fail-open | `Config` 全表加锁；接收确认门改为失败关闭并记日志 |
| M11 | 停止监控后 `_monitorCts` 未清空 → 队列/扫描/群发永久失效 | `StopMonitoring` 清空并 Dispose CTS、清队列 |
| M12 | 并发>1 时「取消」无效 | `ConcurrentTransfer` 增 `Cancel()`/`WasCancelled`，注册在途客户端；UI 保存 `_concurrent` 并在取消按钮调用 |
| M13 | CLI `--limit` 少乘 1024 | 按 KB→bytes 换算并钳制到 int 上限 |
| M14 | CLI `recv` 绑定失败永久挂起 | 检查 `IsRunning` + 订阅 `OnStopped`，失败返回退出码 1 |

---

## 五、第二批修复（本次续作）

审计报告第一版列为「未处理」的项，本轮已完成下列（其余见第六节）：

| 项 | 修复 |
|---|---|
| HTTP 访问码可无限爆破 | 按来源 IP 计失败次数，超 `MaxAuthFailures`(10) 锁定 10 分钟，锁定期正确码也回 429 |
| HTTP token 泄漏进 URL | `AppendToken` 不再回填 `t`，会话只靠 HttpOnly 同站 Cookie |
| HTTP 无并发/超时上限（slowloris） | `SemaphoreSlim` 限制并发连接(32) + 请求头 15s 空闲超时(408) |
| HTTP 上传「单文件上限」未生效 | 按 part 强制 `MaxUploadFileBytes`(2GB) + 整请求聚合上限 |
| HTTP 缺 CSP / 可被框架 | 响应统一 `no-store` + `X-Frame-Options: DENY` + CSP |
| 0x02 完成判据可被重叠块欺骗 | 改为已接收**区间覆盖**判定；同名异尺寸残留 tracker 驱逐 |
| 未知传输类型落入 0x00 | 显式拒绝 |
| 0x01 fileCount 无上限 | 统一 `MaxFolderFileCount`(1000000) |
| 加密段可重排/截断 | 0x09 会话每段带递增 `seq` 并被 MAC 覆盖（旧 0x07 保持兼容格式） |
| 更新源可明文投毒 | 强制 https（http 仅回环），`Parse`/`FromGitHubJson`/`CheckAnyAsync`/`DownloadAsync` 四处校验 |
| 更新暂存 TOCTOU | 换入前按 manifest 哈希重校验；`.part` 名带 GUID 防多实例互踩 |

## 六、仍未处理（低危 / 防御纵深）

- UDT DLL 解压重试未 rewind、UDT `_socket` 双重关闭（非 `Interlocked.Exchange`）、UDT accept 循环遇非取消错误即 break、`udt_send` 返回 0 忙循环。
- 恢复状态落盘非原子（`File.WriteAllText` 无 temp+rename）、`Flush()` 非 `Flush(true)`。
- `StatsStore` 多进程不安全、字段不转义 `|`/换行。
- HTTP 共享跟随 reparse point（junction/符号链接）。
- 上传/日志文件名未剥离控制字符（日志注入）。
- 设备发现响应可伪造（UDP 无鉴权）。
- 声明大小上限（`MaxTransferSize`）取 1 PiB，仍可驱动大额稀疏预分配。
- **更新清单仍是「同源哈希」而非签名**：https 防了 MITM，但若 GitHub 账号/发布流程被攻破，边车哈希与 exe 可一起被替换。彻底解决需内嵌公钥签名清单（建议后续）。
- **配对码仍是 6 位**：0x09 已使主动中间人必须离线爆破，但加长到 ≥12 位才能让该攻击不现实（比加大 PBKDF2 迭代更有效）。

## 七、测试框架改进

`TestRunner` 现在展开 `AggregateException` 显示真实原因，并支持 `TF_TEST_FILTER` 环境变量按名筛选测试（便于定位单项失败，无需跑完整慢套件）。

---

## 六、新增回归测试

`Tests/FeatureTests.cs`：

- `WireCrypto_EcdhSharedSecret_Agrees` / `_RejectsBadBlob` — ECDH 往返与畸形公钥拒绝
- `WireCrypto_AuthKey_BindsCodeAndTranscript` — 认证密钥确定性、错码改变密钥、方向标签域分离
- `DiskSpace_Overflow_Rejected` — 溢出/超限/负需求一律拒绝
- `Feature_TCP_ResumeOffsetGap_Rejected` — 声明偏移超前时服务端补齐空洞、内容逐字节一致（H1）
- `Feature_TCP_AuthLockout` — 连续错码后正确码亦被拒、无文件落盘（H4）
- `Feature_TCP_CompletionAck_SurvivesCompression` — 压缩会话下被拒绝传输在客户端报失败（H3+M3）
- `ChunkTracker_OverlappingChunks_NotComplete` / `_OutOfOrder_CoverageCompletes` — 覆盖式完成判据
- `WireCrypto_BoundSequence_ReorderedSegmentsDetected` — 段重排被 MAC 检出
- `Integration_HTTP_AuthLockout` — 访问码失败锁定后正确码回 429
- `Update_Parse_InsecureHttpRejected` — 明文 http（非回环）清单被拒

---

## 八、遗留建议

1. **加长配对码**：0x09 对主动中间人的唯一残余风险是离线爆破 6 位码；改用 ≥12 位或字母数字即可使该攻击不现实（比继续加大 PBKDF2 迭代更有效）。
2. **更新清单签名**：为 GitHub 发布流程加 Ed25519 签名，消除 H6 的信任缺口。
3. 按第五节的低危列表逐步清理，并为认证/路径/HTTP 鉴权路径补充针对性测试。
