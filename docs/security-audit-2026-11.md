# TrFileTransfer 代码 + 安全全量审查报告

- 审查日期：2026-11-18
- 审查对象：`TrFileTransfer` v2.14.0.0（工作树提交 `a4df00c`，`git describe` = `v2.14.0.0-11-ga4df00c`，工作树干净）
- 规模：主程序 62 个 C#/XAML 源文件约 1.7 万行、测试 3 个文件约 1 万行，合计约 3.3 万行
- 方法：5 路并行只读审查（线协议/收发端、HTTP·发现·升级、UDP·FEC·UDT、UI·持久化·CLI、构建与测试），关键结论由主审逐行复核；**全程未改动任何源文件**
- 发布版本：**2.16.0.0**（审查基线 2.14.0.0；本轮修复与 UDP v2 批次均进入 2.16）
- 对照基线：`docs/security-audit-2026-09.md`（v2.11 审计）
- 验证结果：主工程 `dotnet build -c Release` **0 error / 56 warning**；测试套件 **296 passed / 0 failed**

---

## 一、结论摘要

本轮没有发现「无需任何交互即可远程读任意文件 / 执行任意代码 / 逃出共享目录」的缺陷。路径处理、恒时比较、帧长上限、认证前置、续传服务端权威化这些历史老大难问题仍然守得住，v2.12–2.15 的多数修复经复核是**真实有效**的。

真正的问题集中在四类：

1. **加密握手的认证材料本身在线上**：`0x09` 仍把 `SHA256(配对码)` 明文发出，6 位默认码可离线 1 秒还原 —— 拿到码即可冒充客户端，并在下一次会话做完整主动中间人。旧审计 S1 的「网线上不出现任何可推出密钥的材料」只对**会话密钥**成立，对**认证口令**不成立。
2. **安全修复存在未覆盖分支**：`EncryptStrict` 被 `0x09 status=2` 单包绕过；完成确认字节在「无配对码 + 关闭压缩」时被跳过（H3 静默丢文件回归）；`0x03` 续传的「已收完」分支不校验全文件哈希。
3. **HTTP 共享的暴露面与产品定位不一致**：无访问码时共享是**可写可删**的（文档仍写「只读」），上传接口**没有 CSRF 校验**，且 15 秒请求头超时在 net48 上**实测无效** → 32 个空连接即可让共享彻底不可用。
4. **资源耗尽面普遍缺闸门**：TCP 监听无连接上限、每连接 8 MB 缓冲、无读超时；分块 tracker 与 UDP `.part` 可被无成本地批量制造。

| 级别 | 数量 | 代表 |
|---|---|---|
| 高危 | 4 | 0x09 认证材料泄露、strict 降级绕过、HTTP slowloris、无码共享可写可删 + 无 CSRF |
| 中危 | 17 | 完成确认回归、续传跳哈希、TCP 连接 DoS、tracker 泄漏、同名文件截断、IPv6 绕锁定、DNS rebinding、UDP part 洪泛、日志注入、UI 线程卡死、CSV 注入、配置静默重置、UDT 引用计数被窃取、更新器剩余风险 |
| 低危 | 12 | 明文配对码、无 nosniff、扫描中止、设备列表竞态、确认框不关、清单累加溢出、0x04 无对端绑定、udt.dll 无校验、CI 未固定 SHA 等 |
| 信息 | 6 | 默认安全是「关」、发现协议泄露信息、构建告警、日志无大小上限、exe/清单未签名、文档与实现漂移 |

---

## 二、总体评价：这些地方做得对

审查中逐条核对、确认**没有问题**的点（也是后续改动不能破坏的契约）：

| # | 点 | 依据 |
|---|---|---|
| 1 | 路径穿越防护完整 | `..`/`.`/空段/冒号(ADS)/尾点空格/UNC/盘符/保留设备名全部中和；`ResolveSafe` 以「带尾分隔符的根」做前缀比较（`Shared.cs:343`、`HttpShare.cs:946`） |
| 2 | 帧长/数量/偏移上限齐备 | 0x00–0x0B 全部有上限，1 PiB 总量 + 64 MB 压缩段 + 16M 分片；`chunkOffset > totalSize - chunkSize` 无溢出（`WireProtocol.cs:1473`） |
| 3 | 分块完成判据正确 | 用「已收区间并集覆盖 `[0,TotalSize)`」而非字节求和，重叠/重复块不能伪造完成（`Shared.cs:99-132`） |
| 4 | 续传偏移服务端权威 | 客户端声明只记日志，实际从服务端已落盘偏移继续（`WireProtocol.cs:1920-1933`、`3255-3263`） |
| 5 | 续传会话绑定 + 单连接锁 | IP + 文件名双绑定、`SemaphoreSlim(1,1)` 拒绝并发同会话（`WireProtocol.cs:1687-1730`、`1856-1871`） |
| 6 | 认证失败计数覆盖三条路径 | 0x05/0x07/0x09 失败均计数，锁定后连正确码也拒绝（`WireProtocol.cs:840-858`） |
| 7 | 接收确认门 fail-closed | 委托抛异常即拒绝并记日志；IP 过滤异常同样不放过（`WireProtocol.cs:749-780`） |
| 8 | 加密/压缩分层与所有权正确 | 压缩在外、加密在内；`ownsInner=false` 保证传输层还能发 ACK（`WireCrypto.cs:242-264`、`WireCompress.cs:69-82`） |
| 9 | 加密段带序号 + 恒时校验 | 0x09 会话每段 MAC 覆盖递增 seq，重排/截断被检出（`WireCrypto.cs:333-371`） |
| 10 | ECDH 握手本体正确 | 每连接临时 P-256、salt 绑定双方公钥、双向 HMAC、错码回随机 confirmS 不泄露（`WireProtocol.cs:878-967`） |
| 11 | 半成品不落最终名 | 0x00 走 `.part-<guid>` → 校验 → `File.Move`；UDP 走 `.part` + 整文件 SHA256 后才改名（`WireProtocol.cs:2175-2319`、`TransferUdp.cs:605-710`） |
| 12 | HTTP 目录页无 XSS、路径无穿越、token 恒时比较 | 全部输出经 `HtmlEscape`；`?p/?f/?del` 走同一 `ResolveSafe`；`Utils.ConstantTimeEquals`（`HttpShare.cs:1000-1031`、`519-524`） |
| 13 | UDP 分片三维校验 | 索引范围 + 精确长度 + 尾片长度；写偏移由索引推导，不会超写声明大小（`TransferUdp.cs:932-957`） |
| 14 | 回收站式防御：明文凭据不进 URL | 列表链接过滤 `t=`，会话只靠 HttpOnly+SameSite=Strict Cookie（`HttpShare.cs:1038-1054`） |
| 15 | 配置/队列/历史解析健壮 | 字段数、类型、端口范围、空值全部校验，坏行跳过不崩（`QueueStore.cs:46-70`、`StatsStore.cs:40-66`） |

---

## 三、高危问题（建议下一版本必修）

### V-1｜`0x09` 把 `SHA256(配对码)` 明文放上线，6 位默认码可离线秒破

**位置**：`TrFileTransfer/WireProtocol.cs:2951-2957`（客户端发送）、`TrFileTransfer/WireProtocol.cs:896`（服务端比较）、`TrFileTransfer/WireProtocol.cs:147-151`（`WireAuth.HashCode`）

```csharp
// ClientWire.TryEcdhHandshakeAsync
frame[0] = 0x09;
Buffer.BlockCopy(codeHash, 0, frame, 1, 32);          // ← SHA256(配对码) 明文上网
Buffer.BlockCopy(clientPub, 0, frame, 33, clientPub.Length);
```
```csharp
// WireAuth.HashCode：无盐 SHA256
return sha.ComputeHash(Encoding.UTF8.GetBytes(code ?? ""));
```

**影响**：任何抓到一个 0x09（或 0x05/0x07）帧的被动嗅探者，只需 10⁶ 次单轮 SHA256（现代 CPU < 1 秒）即可还原配对码。拿到码后可以：①像合法客户端一样向服务器推送文件；②在下一次会话中同时冒充两端完成 ECDH 握手，从而**解密并篡改**「加密传输」。0x09 的设计目标是「网线上不出现任何可推出密钥的材料」，这条路径让 6 位码的强度退化为「一次性明文口令哈希」。

**触发条件**：局域网内能被动嗅探（共享 Wi-Fi、镜像端口、ARP 欺骗）即可，无需交互。

**修复建议**：
1. 0x09 帧**不再携带 codeHash**。服务端本来就能通过 `confirmC`（HMAC，密钥含 PBKDF2(code)）判定客户端是否知道码，错码时照常回随机 confirmS + status 1 即可，不需要提前比较哈希。0x05/0x07 同理可逐步废弃。
2. 默认 `PairingLength` 从 6 提到 ≥12（或字母数字混合），并在 UI 明示强度。
3. 若为兼容必须保留 codeHash，至少改为加盐慢哈希（PBKDF2 用户侧）——但这仍会在抓包后可离线验证，不建议。

**置信度**：确认（代码路径 + 算法可逆性）。

---

### V-2｜`0x09 status=2` 绕过 `EncryptStrict`，主动中间人单包强制明文

**位置**：`TrFileTransfer/WireProtocol.cs:2979-2985`（客户端）、`TrFileTransfer/WireProtocol.cs:2851-2861`（strict 判定只在 `Handled=false` 分支）

```csharp
if (status == 2)
{
    // Server has pairing disabled and will not encrypt. Continuing plaintext ...
    cb.RaiseLog(L.C_EncryptUnsupported);
    return new EcdhHandshakeResult { Handled = true };
}
```

`allowDowngrade`（对应 Config `EncryptStrict=1`）只在「对端根本没回 0x19」时生效；对端回了 `[0x19][0x02][任意 72B][任意 32B]` 时客户端**既不校验 confirmS，也不看 strict 开关**，直接在同一连接上以明文发送文件头与全部内容。

**影响**：配置了配对码并开启 `EncryptStrict=1`（明确要求「失败关闭」）的用户，仍可被单包降级到明文；攻击者读走文件内容，或在其上做透明篡改（0x00 场景只有尾部哈希，篡改会导致接收失败，但内容已泄露）。

**修复建议**：strict 模式把 `status=2` 视为拒绝；非 strict 模式也应在日志/UI 明确警告，并把「服务端无码」改成需要一次显式重连（与 `NeedPlainFallback` 同路径），而不是就地继续。

**置信度**：确认。

---

### V-3｜HTTP 共享的 15 秒头超时在 net48 上无效：32 个空连接即可打死共享

**位置**：`TrFileTransfer/HttpShare.cs:225-244`（`headCts.CancelAfter(15000)`）、`TrFileTransfer/HttpShare.cs:385-400`（`FillAsync` → `NetworkStream.ReadAsync(..., ct)`）、`TrFileTransfer/HttpShare.cs:46-53`（并发上限 32）

**证据**：`NetworkStream.ReadAsync` 在 .NET Framework 4.x 走 `Stream.ReadAsync` 的默认实现（`BeginRead/EndRead`），**不观察令牌**；取消只对「尚未发起」的读生效。子审查在本机 CLR 4.0.30319 上实测：读已挂起后 `CancelAfter(500)`，3 秒后任务仍未完成；同一探针在 .NET 8 上 0.5 秒即取消。`client.ReceiveTimeout = 10000`（`HttpShare.cs:207`）只约束同步 `Read`。

**影响**：攻击者连上 8090 后一个字节不发，`ReadHeadAsync` 永久阻塞，`_connLimit` 槽位永不释放；32 条后 `AcceptLoop` 卡在 `WaitAsync` 不再 accept，整个 HTTP 共享拒绝服务。**该逻辑在鉴权之前，无访问码也生效**；即使有访问码，也无需知道码。

**修复建议**：不要依赖 `ct` 打断 socket 读。任选其一：
- `Task.WhenAny(readTask, Task.Delay(timeout))`，超时后 `client.Close()` 强行中断；
- 把请求头读取改为「同步 Read + `ReceiveTimeout`」；
- 迁移到 `HttpListener`/Kestrel（自带超时与并发控制）。

**置信度**：确认（含运行时实测）。

---

### V-4｜HTTP 共享：无访问码时可**写**可**删**，且上传接口没有 CSRF 校验

**位置**：`TrFileTransfer/MainWindow.xaml.cs:1065`（token 快照）、`TrFileTransfer/HttpShare.cs:751-880`（上传，全文无 `SameOriginOrNoOrigin`）、`TrFileTransfer/HttpShare.cs:605-612`（删除有校验）

```csharp
// 只有勾选配对码时才给 token；默认未勾选 → token=null → 整个共享无鉴权
string token = _chkPairing.IsChecked == true ? _lblPairingCode.Text : null;
share.Start(dir, port, token);
```

**影响**：
1. **产品面**：`HttpShare.cs:15` 的类注释仍写「Read-only HTTP share」，但 2.15 起共享同时提供列表、下载、**上传**、**删除**。配对码未开启（默认）时，任何局域网主机都能删除共享目录中的任意文件、塞入任意文件（`.bat`/`.lnk`/`.exe`）或塞满磁盘。用户以为在「临时给手机看个文件」，实际开了个匿名可写可删的 SMB 替代品。
2. **攻击面放大**：删除有同源校验，**上传没有**。攻击者只需让受害者访问一个网页，页面即可 `fetch('http://<lan-ip>:8090/', {method:'POST', mode:'no-cors', body: fd})` —— `multipart/form-data` 属 CORS 安全列表类型，不触发预检、不需要 cookie，即可向共享目录写入攻击者指定的文件名与内容。Chrome 的 Local Network Access 会部分拦截，但这是浏览器策略而非服务端防线。

**修复建议**：
1. 把「上传/删除」从共享能力里拆成独立开关，默认**关闭**；只读共享时对写操作回 403。
2. 上传与删除统一走 `SameOriginOrNoOrigin` + 双重提交 CSRF token（表单隐藏字段 + Cookie）。
3. 未设置访问码时，UI 用红字提示「当前共享对局域网可写可删」，或在无码时直接禁用写操作。

**置信度**：确认（代码路径）/ 高度可能（浏览器可达性取决于版本）。

---

## 四、中危问题

### V-5｜完成确认字节在「无配对码 + 关闭压缩」时被整体跳过（H3 回归）

**位置**：`TrFileTransfer/TransferClient.cs:351-356`、`TrFileTransfer/WireProtocol.cs:2397-2402`

```csharp
_cb.CompletionAckOnStream = auth.Encrypted || auth.Compressed;   // ← 两者皆无 → false
...
if (cb != null && !cb.CompletionAckOnStream) return;             // ← 根本不读判决字节
```

**影响**：不设配对码、且 `Config Compress=0`（或 CLI `--nocompress`，或对端拒绝 0x08）时，服务端因磁盘不足 / 接收确认被拒 / IP 过滤回 `0x00`，发送端仍报「传输完成」。用户看到成功、对端没有文件。这正是旧审计 H3 要消灭的静默丢文件。

**修复建议**：把「对端是新版」的判定与「是否需要读判决」解耦。最稳的是在 0x0A 能力位里新增 `completion-ack` 位，服务端 ack 后客户端才等；或在未协商加密/压缩时也带 2 秒短超时尝试读 1 字节，EOF 即视为旧版。

**置信度**：确认。

---

### V-6｜`0x03` 续传的「已收完」分支跳过全文件哈希校验

**位置**：`TrFileTransfer/WireProtocol.cs:1899-1914`

```csharp
if (state.ReceivedBytes >= totalSize)
{
    ... status = 2; resumeFrom = totalSize;
    await SendResumeResponse(s, resumeFrom, status, ct);   // 直接报告"已完整"
    return true;                                           // expectedFullHash 完全没用
}
```

**影响**：连接在「收满 totalSize 字节」之后、客户端 32 字节尾哈希到达之前断开时，服务端把会话记为已收完；客户端重连 → 服务端回 status=2 → 客户端 `RaiseComplete()`。整个文件**从未与客户端声明的 `expectedFullHash` 比对**。对未加密传输，能改写 TCP 流的中间人可以在最后一个包后 RST 掉连接，让被篡改/截断的文件以「完成」状态落盘。非攻击场景下也会让「接收端保证哈希校验」这一承诺失效。

**修复建议**：该分支先判定 `state.ReceivedBytes == totalSize && expectedFullHash != null` 时调用 `VerifyFullHashFile`；不匹配则删文件、回 status=3；无 expectedFullHash（旧客户端）则回 status=0 重新传输尾段或至少记警告。

**置信度**：确认。

---

### V-7｜TCP 监听无连接上限、无读超时、每连接 8 MB 托管缓冲

**位置**：`TrFileTransfer/TransferServer.cs:267-314`（accept 循环无上限）、`TrFileTransfer/WireProtocol.cs:2167-2168`（`new byte[ctx.BufferSize]` ×2 = 8 MB）、`TrFileTransfer/WireProtocol.cs:43`（`SetReadTimeoutMs` 空实现）、`TransferServer.cs:273-276`（socket 收发缓冲 4 MB）

**影响**：攻击者并发开 N 条连接，每条只发一个合法的 ~13 字节 0x00 头部（`fileSize>0`、`nameLen>0`），即可触发 8 MB 托管分配 + 约 8 MB 内核缓冲，随后静默挂起（无读超时）。约 50 字节输入换约 16 MB 常驻，几百条连接即可 OOM / 句柄耗尽。无配对码时完全预认证。

**修复建议**：`MaxConnections` 信号量（参考 HTTP 的 32）、按需/池化缓冲（不要每连接 2×4 MB）、头部读取空闲超时（30 s 关连接）、按 IP 的连接数上限。

**置信度**：确认。

---

### V-8｜被放弃/被拒绝的分块 tracker 永不回收（句柄 + 稀疏文件泄漏）

**位置**：`TrFileTransfer/WireProtocol.cs:1559-1564`（超限 return false，不 Dispose）、`1536-1537`（`GetOrCreate` 立即建 tracker）、`Shared.cs:99-124`（首个分片才建 FileStream 并 `SetLength(TotalSize)`）

**影响**：对每个文件名发一个 0x02 头 + 少量数据后断链，`ChunkTrackers` 里留下一个持有 `FileShare.None` 句柄的 tracker 与一个逻辑大小为 `totalSize` 的稀疏文件，直到 `Stop()` 才释放。用户侧表现为「文件被其他程序占用」无法删除/覆盖，后续同名传输永远拿到 `_1`；句柄随连接数线性增长。范围上限检查命中时同样不清理。

**修复建议**：tracker 空闲超时回收（Dispose + TryRemove），所有失败/拒绝返回路径统一走清理；或在协议层把「tracker 建立」延后到收到首个分片。

**置信度**：确认。

---

### V-9｜分块/续传的「取唯一名」与「建文件」分离，可截断同名文件

**位置**：`TrFileTransfer/Shared.cs:88`（`SavePath = GetUniqueSavePath(...)`）、`TrFileTransfer/Shared.cs:105-109`（首个分片 `FileMode.Create`）、`WireProtocol.cs:1834-1836`（0x03 同样模式）

**影响**：唯一名在「文件尚不存在」时计算，真正 `Create` 发生在稍后。两个不同对端（tracker 键含 peer/groupId，互不排斥）可得到同一路径 X：攻击者先发同名 0x02 头拿到 X 后搁置（TCP 无读超时，可无限停住），受害者传完同名文件写入 X；攻击者再发首块 → `FileMode.Create` **截断** X 并覆盖。0x03 的窗口小得多，后果多为 IOException。

**修复建议**：分配路径与创建文件放在同一临界区，用 `FileMode.CreateNew`，冲突时重取唯一名；路径分配表加锁/预留。

**置信度**：高度可能（代码路径确认，利用需时序控制）。

---

### V-10｜配对码锁定按来源 IP 计数、字典无过期无上限，IPv6 换源即绕过

**位置**：`TrFileTransfer/WireProtocol.cs:274-279`、`840-858`

**影响**：单台主机可在自己的 /64 内任意绑定源地址，每个地址 10 次机会；6 位码（10⁶）在约 10⁵ 个地址内可穷举。每个失败 IP 永久留一条记录（IPv6 轮换 → 字典无界增长）。锁定不可自愈：正确码在锁定后同样被拒，只能等服务器重启（HTTP 侧有 10 分钟过期，TCP 侧没有）。

**修复建议**：全局滑动窗口（如每分钟 N 次失败）为主、每 IP 记录带 TTL 和容量上限；锁定改为指数退避而非永久。

**置信度**：确认。

---

### V-11｜HTTP 同源校验以请求自身 `Host` 为准，DNS rebinding 可绕过

**位置**：`TrFileTransfer/HttpShare.cs:661-693`

```csharp
headers.TryGetValue("Host", out ourHost);       // 请求自带，攻击者可控
return UrlHostEquals(origin, ourHost);          // Origin 与 Host 比
```

**影响**：`evil.com` 先把页面发给受害者（Origin=evil.com），TTL 归零后重绑到 `192.168.1.5:8090`；此后浏览器发出的 `Origin` 与 `Host` 都是 `evil.com:8090`，校验通过，`SameSite=Strict` 也满足，`t` cookie 照发。页面即与共享同源：可列目录、下载全部文件、`POST ?del=` 删除、上传。

**修复建议**：加 Host 白名单（由 `LanAddresses()` + 端口生成），Host 不在白名单直接 400；写操作再加 `Sec-Fetch-Site` 校验。

**置信度**：高度可能（取决于浏览器是否已部署 Local Network Access 缓解）。

---

### V-12｜HTTP 上传：同名并发竞态 + 失败留半成品

**位置**：`TrFileTransfer/HttpShare.cs:841-852`

**影响**：`GetUniqueSavePath` 的 `Exists` 检查与 `FileMode.Create` 之间存在竞态，两个同名上传可互相截断，写入交错却都报成功；上传中断或超限时，已写入的半个文件保留在最终文件名下（与传输接收端的 temp+rename 约定不一致），用户会看到一个「完整」的坏文件。

**修复建议**：`FileMode.CreateNew` + 冲突重试换名；先写 `.part`，成功后再 `File.Move`。

**置信度**：确认。

---

### V-13｜UDP：未鉴权的 START 可无限制造 `.part` 文件

**位置**：`TrFileTransfer/TransferUdp.cs:445-467`（建会话）、`862-881`（构造即 `OpenOrCreate`）、`447-467`（驱逐只 `CloseStream`，**保留 part**）、`787-802`（仅 7 天后清理）

**影响**：任意主机发 74 字节 START（size=1、chunkSize=60000、totalChunks=1）即落一个 `.part`；超过 64 个会话后每次驱逐旧会话但文件保留。10 万包（≈2.4 MB 流量）即可生成 10 万文件 → NTFS MFT/目录膨胀、目录扫描与删除变慢、最终写盘失败。

**修复建议**：驱逐时删除「零进度」会话的 part；按源 IP 限建会话速率；Sweep 高频清理 0 字节孤儿。

**置信度**：确认。

---

### V-14｜UDP：会话表全局无对端绑定，可驱逐/清空他人的在传 part

**位置**：`TrFileTransfer/TransferUdp.cs:431-467`、`484-540`

**影响**：会话键是 `DeriveSession(文件名,大小,mtime)`，创建与 `Discard()`（删在传 part）都不校验对端 IP。攻击者**无需猜 session id**：连发 65 条 START 即可把受害者会话挤出表；此后其 DATA/FEC 全部落入「unknown/finished」分支被丢弃，传输永不完成。若攻击者猜到同名同大小的会话，还能通过「shape 不同」分支 `Discard()` 直接删掉受害者的 part。

**修复建议**：会话表按 `PeerIp` 分表或加入会话键；驱逐优先淘汰零进度会话；`Discard` 前校验来源。

**置信度**：确认。

---

### V-15｜UDP：会话位图内存钉住（64 会话 × 2 MB + 64 个 fd，总流量 < 5 KB）

**位置**：`TrFileTransfer/TransferUdp.cs:862`、`406-408`、`770`（2 分钟空闲才回收）

**影响**：`totalChunks` 上限 16M → `_coveredBits` 2 MB/会话；64 个会话即 128 MB，再加 64 个句柄。每 2 分钟补一条 END 刷新 `LastHeard` 即可长期占用，放大 ~2.5 万倍。

**修复建议**：位图按已收字节阶梯扩容；对「长时间零进展」会话缩短超时。

**置信度**：确认。

---

### V-16｜对端文件名未剥离控制字符 → 日志行伪造 + 日志膨胀

**位置**：`TrFileTransfer/WireProtocol.cs:1081-1092`（`SafeName`）、`1369`（建文件**之前**就 `RaiseLog`）、`MainWindow.xaml.cs:3154-3183`（原样写 UI 与 `logs\<date>.txt`）

**影响**：任意能连上监听端口（默认无码）的对端在 0x00 头里发 `name = "x\r\n[10:00:00] 传输完成: 工资表.xlsx (2.1 MB)"`：`SafeName` 不剥离 CR/LF（Windows 无法创建此类文件，随后抛异常），但日志行、确认框文案已写入磁盘与 UI，可伪造审计证据；反复连接可无限追加（日志无大小上限，仅 30 天清理）。

**修复建议**：`SafeName`/`SanitizeRelativePath` 统一 `char.IsControl(c) → '_'` 并加长度上限；日志出口再做一次 `\r\n` 替换兜底。

**置信度**：确认。

---

### V-17｜文本历史在 UI 线程全量重写，大消息可把界面卡死

**位置**：`TrFileTransfer/TextHistoryStore.cs:87-113`、`MainWindow.xaml.cs:1424`（`RunOnUi` 内调用 `Add`）

**影响**：0x06 单条上限 1 MB，历史上限 200 条。对端连发 ~200 条 1 MB 文本后，每收到一条都在 **UI 线程** `File.ReadAllLines` + `WriteAllLines` 200 MB；同时 `TextReceivedDialog.AppendText` 把同样内容堆进 TextBox。无码时无需认证。

**修复建议**：修剪移出 UI 线程（`Task.Run`），按行计数而非每次全读；单条/累计大小设上限。

**置信度**：确认。

---

### V-18｜历史导出 CSV 公式注入

**位置**：`TrFileTransfer/Dialogs/HistoryDialog.cs:184-212`

**影响**：`Detail`/`Path` 来自对端文件名（`=`、`+`、`-`、`@` 在 Windows 文件名合法），`CsV()` 只处理逗号/引号/换行。对端发送名为 `=WEBSERVICE(...)` 的文件，用户导出后双击用 Excel 打开即触发公式（外带/钓鱼）。

**修复建议**：字段以 `= + - @ \t \r` 开头时前置 `'` 或强制加引号。

**置信度**：确认。

---

### V-19｜Config 保存回退会先删正式文件，而 Load 把「文件缺失」当用户重置

**位置**：`TrFileTransfer/Config.cs:106-121`、`55-64`

```csharp
try { File.Replace(tmp, _path, null); }
catch { File.Delete(_path); File.Move(tmp, _path); }   // 先删后搬
```

**影响**：`File.Replace` 在目标被占用/只读/杀软扫描时抛异常 → 删掉 `config.ini`；若随后的 `File.Move` 也失败（tmp 被索引器锁住），异常被外层 `catch {}` 吞掉，配置消失。下次启动走「文件缺失=重置」分支，**有意忽略 `.bak`**，安全相关设置静默回默认：`IpFilterMode=off`、`ConfirmReceive=off`、`PairingLength=6`、`EncryptStrict=false`。多实例共用同一 `config.ini.tmp` 会放大该竞态。

**修复建议**：①tmp 名带 PID/GUID；②Replace 失败时不要删原文件，改用 `File.Move(tmp, path)`（或放弃本次保存）；③Load 在文件缺失且 `.bak` 可读时优先用 `.bak`；④Save 整体加进程内锁。

**置信度**：确认（竞态窗口窄，故列中危）。

---

### V-20｜UDT 客户端在异常路径「窃取」一次引用计数，可在他人的传输中关闭 UDT 运行时

**位置**：`TrFileTransfer/TransferUdt.cs:889-898`（`OnStarted` → `EnsureExtracted` → `UdtStartup`）、`958-966`（finally 无条件 `UdtCleanup`）、`1149-1194`（0x0B 查询同样模式）、`53-79`（引用计数实现）

**影响**：`OnStarted` 处理器抛异常，或 `EnsureExtracted()` 三次解压失败时，本次流程**没有成功 Startup**，finally 却调用 `UdtCleanup()` —— 引用计数会减掉别人（如监听器或另一并发传输）持有的那一份。等真正持有者退出时计数归零，`udt_cleanup()` 在仍有活跃 socket 时执行，导致其他 UDT 传输失败甚至崩溃。服务端侧用 `_startupOk` 做对称保护（`TransferUdt.cs:564-571`），客户端没有。

**修复建议**：用 bool 标记「本流程 Startup 成功」，仅在该标记为真时 Cleanup（照抄服务端 `Uninit()`）。

**置信度**：确认（代码路径）/ 高度可能（现网触发率）。

---

### V-21｜更新器剩余风险：重定向不校验、资产按「第一个 .exe」、无签名、换入可能「变砖」

**位置**：`TrFileTransfer/Updater.cs:357-389`（`AllowAutoRedirect` 默认 true，从不校验 `ResponseUri`）、`113-126`（首个 `.exe` 资产）、`406-444`（Apply/DeleteStaleBackup）

**影响**：
1. https 清单/资产被 302 到 `http://` 时仍会跟随，scheme 校验只作用于初始 URL（需要可控的 https 端点，故列中危偏低）。
2. 发布页若同时存在安装器/调试版 exe，会选错资产；侧车也按错名取。
3. 更新清单仍是「同源 SHA256」而非签名：https 只挡 MITM，GitHub 账号/发布流程被攻破时边车与 exe 可一起替换 —— 旧审计 H6 的残余风险原样存在。
4. `Apply` 在 `File.Move(target, backup)` 与 `File.Copy(staged, target)` 之间被杀/断电，目录里只剩 `.old`；下次从 `.old` 启动的新实例会把 `.old` 删掉，主程序与备份可能双双消失。Copy 期间目标也是半成品（非原子）。

**修复建议**：手动跟随重定向并逐跳校验 scheme；优先精确匹配 `TrFileTransfer.exe`；内嵌公钥签名清单（Ed25519），exe 做代码签名；`Apply` 改为「先写 `target.new` → 校验 → `File.Replace`」，启动时若主程序缺失且 `.old` 存在则恢复。

**置信度**：确认。

---

## 五、低危与信息

| 编号 | 问题 | 位置 | 建议 |
|---|---|---|---|
| L-1 | 配对码明文保存于 `config.ini` | `MainWindow.xaml.cs:868` | 用 DPAPI（`ProtectedData`）加密；至少提示「配置文件含配对码」 |
| L-2 | 文件下载响应缺 `X-Content-Type-Options: nosniff` | `HttpShare.cs:1125-1145` | 所有响应统一加 `nosniff` |
| L-3 | `LibraryVerifier` 遇单个不可读子目录即整体中止并返回空报告，且 `AllDirectories` 会跟随 junction 扫到根外 | `LibraryVerifier.cs:48-51` | 改用 `EnumerateFiles` 逐目录 try/catch，区分「中止」与「完成」 |
| L-4 | `_knownDevices` 无同步：服务端线程读、UI 线程增删 | `MainWindow.xaml.cs:1983-1992`、`1386-1395` | lock 或快照 |
| L-5 | 接收确认框 30 秒超时后只 `TrySetResult(false)`，窗口不关 | `MainWindow.xaml.cs:2019-2034` | 超时分支 `RunOnUi(() => dlg.Close())` |
| L-6 | 0x04 `manifestTotal += size` 可溢出，`totalBytes` 造假通过校验（仅显示欺骗，随后逐文件预检会拒） | `WireProtocol.cs:1150-1152` | `checked` 累加或先判上界 |
| L-7 | 0x04 会话目录无对端绑定（0x03 有） | `WireProtocol.cs:1097-1099` | 目录名/状态加入 peer 校验（纵深防御） |
| L-8 | UDT 错误描述跨线程取值，可能读到空错误 | `TransferUdt.cs:1023-1033` | 在闭包内取描述（`UdtIo` 已这样做） |
| L-9 | UDP `Stop()` 先清表后关 socket 的竞态；`_cts` 不 Dispose | `TransferUdp.cs:323-342` | 先停收包再清表；Dispose CTS |
| L-10 | `udt.dll` 存在即信任、不校验；回退写 `%TEMP%` 后 `SetDllDirectory(%TEMP%)` 不复原 | `TransferUdt.cs:218-277` | 内嵌哈希白名单；唯一临时名；用完 `SetDllDirectory(null)` |
| L-11 | CI 使用 `actions/checkout@v5` 标签而非 commit SHA | `.github/workflows/*.yml:11/15` | 固定 SHA（供应链纵深） |
| L-12 | 日志文件无大小上限，只按 30 天清理；单条日志可被对端撑大 | `MainWindow.xaml.cs:3166-3199` | 单文件上限 + 轮转 |
| I-1 | 默认安全是「关」：配对码复选框默认未勾选，服务器对全网开放；HTTP 共享此时可写可删 | `MainWindow.xaml:226`、`MainWindow.xaml.cs:1065` | 首次启动引导用户选择；或默认生成配对码 |
| I-2 | 设备发现 UDP 应答泄露主机名/端口/配对标志，且 1 字节探针可换 ≤260 字节应答（~260× 放大，无速率限制） | `DeviceDiscovery.cs:56-68`、`115`、`127-138` | 仅回私网源、每源限速、名称定长 |
| I-3 | 构建有 56 个警告，几乎全是 XML 注释缺失（`CS1573/CS1587`） | 构建输出 | 清理或关闭该规则，避免真问题被淹没 |
| I-4 | 更新清单、exe 均未签名 | `Updater.cs`、`.github/workflows/release.yml` | 见 V-21 |
| I-5 | 文档与实现漂移：`HttpShare.cs:15` 仍称 "Read-only"；AGENTS.md 称 0x09「网线上不出现任何可推出密钥的材料」（对会话密钥成立、对配对码不成立） | `HttpShare.cs:15`、`AGENTS.md` WireCrypto 段 | 同步文档 |
| I-6 | CLI `send` 若在订阅 `OnError` 前抛异常，控制台无输出只有退出码 1 | `Cli.cs:136-140` | 顶层 catch 打印异常消息 |

---

## 六、对 2026-09 审计的复审

### 6.1 确认已修复且有代码证据

| 旧编号 | 项 | 现状 |
|---|---|---|
| S1 | 0x09 认证 ECDH | 握手本体正确（临时密钥、双向 HMAC、错码随机 confirmS）；但见 **V-1**（认证材料泄露） |
| H1 | 续传偏移服务端权威 | 已修：`WireProtocol.cs:1920-1933` + 客户端 `3255-3263` |
| H3 | 完成确认字节 | 主路径已修；见 **V-5**（未覆盖分支） |
| H4 | 配对码失败锁定 | 已实现；见 **V-10**（按 IP、无过期、IPv6 绕过） |
| H5 | 续传会话绑定与并发锁 | 已修（0x03）；0x04 见 L-7 |
| M1 | 预认证压缩放大 | 已修：`WireProtocol.cs:559-564` |
| M2 | 磁盘预检溢出 | 已修：`Shared.cs:618-622` |
| M3 | UDT ACK 生命周期 | 已修：`ownsInner` |
| M4 | 0x04 全文件哈希 | 已修：`WireProtocol.cs:1306-1316` |
| M7 | 半成品写最终路径 | 0x00 已修（temp+rename）；0x02/0x03 用预分配文件（设计如此） |
| M8 | Config 锁 / Gate fail-open | 已修；见 **V-19**（落盘回退） |
| M11/M12/M13/M14 | 监控/取消/CLI 边界 | 复核通过 |
| 第二批 | HTTP 锁定、token 不回填、并发上限、上传上限、CSP、0x02 覆盖判据、https 强制、暂存 TOCTOU | 复核通过；见 **V-3/V-4/V-11/V-12**（未覆盖面） |

### 6.2 修复不完整 / 新引入的回归

- **S1 → V-1**：0x09 不再让会话密钥可从线上推出，但仍发送 `SHA256(配对码)`；6 位码可离线破，破坏的是「认证」而非「密钥派生」。旧报告「主动中间人必须离线爆破且受 PBKDF2 拖慢」的评估不成立 —— 攻击者破的是无盐 SHA256，不经过 PBKDF2。
- **H2 → V-2**：`EncryptStrict` 只覆盖「对端不回 0x19」的情况，`status=2` 分支绕过。
- **H3 → V-5**：判决字节的读取与前导协商结果耦合，关闭压缩即失效。
- **M7 精神未贯彻到 HTTP 上传**：见 V-12。
- **旧报告 §6 遗留项仍在**：UDT accept 循环遇非取消错误即 break（`TransferUdt.cs:586`）、恢复状态落盘非原子、StatsStore 转义、HTTP 跟随 reparse point、日志控制字符、发现协议可伪造、更新清单无签名。

---

## 七、代码质量与工程化

1. **`WireProtocol.cs` 已 3800+ 行**，同时容纳服务端协议、客户端协议、目录清单、摘要缓存、文件哈希。建议按 `ServerWire` / `ClientWire` / `FileHashCache` / `FolderManifest` 拆文件（partial class 或独立类型），并把「读定长头部 + 上限校验」抽成统一的 `FrameReader`，减少 0x00–0x0B 之间重复的手写校验（这类重复正是 V-6/V-9 的来源）。
2. **异常口径不统一**：同一个 `HandleClientAsync` 里既有「吞掉继续」也有「抛穿到传输层」，调用方难以判断「失败但已清理」与「失败且状态残留」。建议为每个 handler 定义明确的返回值/清理契约。
3. **C# 5 双构建约束**（测试用老 `csc.exe`）是历史包袱：20 个逻辑文件不能用 `$""`/`?.`，迫使代码更啰嗦。若把 Tests 迁到 SDK 风格构建（`net48` + `LangVersion=latest`），可统一语言版本、顺手消除一类防御性写法。
4. **测试面广但深度集中在 happy path**：296 项全绿值得肯定，但缺少负向用例：
   - 0x09 `status=2` + `EncryptStrict`（V-2 回归测试）；
   - 「无码 + `--nocompress`」下的完成判决（V-5）；
   - 0x03 收满后断连再重连（V-6）；
   - HTTP 慢速/空连接（V-3）、畸形 multipart、并发同名上传（V-12）；
   - UDP 畸形 START/DATA/PARITY 与会话驱逐（V-13/V-14）；
   - 控制字符文件名进入日志（V-16）；
   - 配置损坏/半写（V-19）。
   建议为帧解析加 property-based/fuzz 测试（随机帧长/索引/计数），这类测试成本低、收益高。
5. **CI 只跑构建 + 测试**：没有静态分析（`EnableNETAnalyzers`、Security rules）、没有依赖/secret 扫描、没有固定 action SHA。对「自更新」类工具，发布链路本身就是攻击面，建议 release 工作流加签名步骤。
6. **文档与实现漂移**（I-5）会直接误导使用者与后续维护者，建议把「安全行为」写进 PRODUCT-SPEC 并在 CI 检查关键注释。

---

## 八、修复优先级建议

**P0（下一版本，安全承诺相关）**
1. V-1：0x09 不再发送 codeHash；默认配对码加长到 ≥12 位。
2. V-2：strict 模式拒绝 `status=2`。
3. V-3：HTTP 头读取改为可强制中断的超时。
4. V-4：上传/删除默认关闭 + 上传加 CSRF；无码时禁用写操作。

**P1（两到三个版本内）**
5. V-5 / V-6：完成判决与哈希校验的所有分支补齐。
6. V-7 / V-8 / V-9：连接上限、读超时、缓冲池化、tracker 回收、`CreateNew` 分配。
7. V-10：全局 + 带 TTL 的认证限速。
8. V-13 / V-14 / V-15：UDP 会话对端绑定、驱逐时删零进度 part、位图惰性分配。
9. V-19：Config 保存不回删 + `.bak` 自愈。
10. V-20：UDT Startup/Cleanup 成对。

**P2（按计划清理）**
11. V-16 / V-17 / V-18：控制字符剥离、历史落盘移出 UI 线程、CSV 前缀转义。
12. V-21：重定向逐跳校验、资产精确匹配、清单签名、Apply 原子化。
13. V-11：Host 白名单 + `Sec-Fetch-Site`。
14. 低危表逐项。

---

## 九、未覆盖 / 需动态验证

- UDT4 原生库（`udt-master` 完整实现）只审了 C# P/Invoke 面与 `udt_c_wrapper.cpp` 薄封装，未做原生内存安全审计。
- 未做网络级 fuzz 与渗透测试（本轮为只读代码审查 + 本机运行时语义验证）。
- 未审计 Windows 防火墙规则、安装器/卸载器行为（仓库不含安装器）。
- 多实例并发下的配置/队列/统计竞态只做了代码推理，未做压力复现。

---

## 十、修复实施记录（2026-11-18）

按 P0 → P1 顺序实施（两轮）：该批共改动 24 个已跟踪文件（+1973 / −291），另新增 `SecretProtector.cs` 与本报告；新增回归用例 12 项。其后单独的单向 UDP v2 互通批次又改动 10 个跟踪文件，当前工作树合计 **27 个跟踪文件（+3546 / −301）**、测试 313 项（详见 `docs/interop-csharp-rust-2026-11-18.md`）。所有改动保持测试构建要求的 C# 5 语法（20 个逻辑文件仍由老 `csc.exe` 编译）。

### 10.1 已修复并验证

| 编号 | 修复内容 | 关键改动 | 回归验证 |
|---|---|---|---|
| V-1 | 新增 `0x0C` 认证握手：帧内**只有客户端临时公钥**，配对码只作为 PBKDF2 输入；服务器响应 `0x1C` | `WireProtocol.cs`（`HandleEcdhAuthV2Async`、`TryEcdhHandshakeAsync`）、`WIRE-PROTOCOL.md` | `Feature_TCP_Encrypted` / `Feature_UDT_Encrypted` / `Feature_TCP_LegacyAuthFallback`（钉住首帧 0x0C 无哈希、旧对端才回退 0x09） |
| V-2 | `status=2` 在 `EncryptStrict` 下改为拒绝；非严格模式仍允许并记日志 | `TryEcdhHandshakeAsync` + `L.C_StrictPlainRefused` | `Feature_TCP_StrictRejectsPlainStatus2` |
| V-3 | HTTP 头/正文超时改为 `Task.WhenAny(读, Task.Delay)` + 关 socket（net48 的读不可取消）；超时上限可配（测试用） | `HttpShare.cs` | `Integration_HTTP_SlowlorisReleasesSlots`（填满全部槽位后仍能恢复服务） |
| V-4 | 无访问码 = 只读共享（POST 一律 403，列表不渲染上传/删除 UI）；上传增加同源校验；写操作要求访问码 | `HttpShare.cs` | `Integration_HTTP_ReadOnlyWithoutCode`、`Integration_HTTP_UploadRejectsCrossSite`、更新后的 `Integration_HTTP_Upload` |
| V-5 | 0x0A 新增 bit2（完成判决能力）；关闭压缩时客户端请求 bit2，据此读判决字节 | `WireProtocol.cs`、`TransferClient.cs` | `Feature_TCP_CompletionAck_NoCompression` |
| V-6 | `0x03` 的「已收满」分支先按 `expectedFullHash` 校验，不通过则删文件回 status=3 | `HandleResumableCore` | `Feature_TCP_ResumeCompleteVerifiesFullHash`（先传满正文再断连，重连声明错误全文件哈希 → 必须 status=3） |
| V-7 | TCP 同时服务连接上限 64；首字节 30 秒上限；**每次读 60 秒空闲上限**（`TcpWireStream` 超时关 socket；客户端 30 秒，长等待由 `LongReadWindow` 放宽）；准入槽位在所有路径释放 | `TransferServer.cs`、`WireProtocol.cs`、`TransferClient.cs` | `Integration_TCP_LargeSingle` + 既有连接/并发用例；全量套件 |
| V-8 | ChunkTracker 空闲 10 分钟自动回收；区间溢出时移除并 Dispose；唯一名与建文件合并为 `CreateNew` + 重试 | `Shared.cs`、`HandleChunkedFile` | `ChunkTracker_IdleSweep_ClosesAbandonedTrackers` + 既有分块/并发用例 |
| V-9 | 分块/续传的文件名分配与创建改为原子 `CreateNew`，冲突重取后缀，不再截断同名文件 | `Shared.cs`（`OpenUniqueStream`） | 分块/续传集成用例 |
| V-10 | 认证失败记录带时间戳：锁定 10 分钟后**自动解除**；记录表上限 4096，过期优先清理；并发握手闸门 16；**0x0C 握手在中途被丢弃也计为一次失败**（否则客户端在 confirmS 不符时本地中止，服务器永远不会计数，锁定形同虚设） | `WireProtocol.cs`（`AuthFailure`、`AuthLockedOut`、`HandleEcdhAuthV2Async`） | `Feature_TCP_AuthLockout`（含锁定后正确码被拒） |
| V-11 | Host 白名单（回环/机器名/本机 IPv4 + 端口），不匹配回 400；`HttpShareAllowAnyHost=1` 可关 | `HttpShare.cs` | `Integration_HTTP_RejectsForeignHost` |
| V-12 | 上传先写 `.part-<guid>`，收完再 `File.Move`；最终名冲突重试；失败删除临时文件 | `HttpShare.cs` | `Integration_HTTP_Upload` / `UploadTokenAndTraversalName` |
| V-13 | UDP 会话驱逐优先「零字节进度」并删除空 part；空闲回收同样删除零进度 part | `TransferUdp.cs` | 既有 UDP/FEC 用例 |
| V-14 | UDP 会话按来源 IP 绑定：DATA/FEC/END 与 START 都校验发送方，外来来源不能写或重置他人 part | `TransferUdp.cs` | 既有 UDP 用例（单向接收/重发/FEC） |
| V-16 | `SafeName`、`SanitizeRelativePath` 剥离控制字符与 Windows 非法文件名字符，并限长 | `Shared.cs`、`WireProtocol.cs` | `SanitizeRelativePath_StripsControlAndInvalidChars` |
| V-17 | 文本历史超过 1 MB 时把修剪放到线程池（小文件保持同步，测试确定性不变） | `TextHistoryStore.cs` | `TextHistory_*` 用例 |
| V-18 | CSV 导出对 `= + - @ \t \r` 开头的字段前置单引号 | `HistoryDialog.cs` | 代码复核（`CsV` 私有） |
| V-19 | Config 保存：唯一临时名 + 进程内锁；`File.Replace` 失败时**只 Copy 覆盖，绝不先删主文件** | `Config.cs` | `Config_*` 全部用例 |
| V-20 | UDT Startup/Cleanup 用 `libStarted` 配对，异常路径不再窃取他人引用 | `TransferUdt.cs` | `Feature_UDT_*` 全部用例 |
| V-21 | 重定向手动逐跳校验 scheme；资产优先精确匹配 `TrFileTransfer.exe`；`Apply` 经 `.new` 改名；启动时主程序缺失则从 `.old` 恢复；下载上限 512 MB | `Updater.cs` | `Update_Check_RejectsInsecureRedirect`、`Update_GitHubPrefersCanonicalAsset`、`Update_Apply_*` |
| L-2 | 文件下载与列表/表单响应统一加 `X-Content-Type-Options: nosniff` | `HttpShare.cs` | HTTP 用例 |
| L-3 | 文件库校验改为逐目录遍历：跳过 junction/符号链接、容忍不可读子目录、环检测 | `LibraryVerifier.cs` | 代码复核 + CLI verify 用例 |
| L-4 | 已知设备列表改为不可变快照供服务端线程读取，消除 `ArgumentOutOfRangeException` 竞态 | `MainWindow.xaml.cs` | 代码复核 |
| L-5 | 接收确认 30 秒超时后关闭对话框，不再残留窗口 | `MainWindow.xaml.cs` | 代码复核 |
| L-6 | 0x04 清单总量改为溢出安全累加 | `WireProtocol.cs` | 代码复核 |
| L-12 | 单日日志 8 MB 上限，超限轮转为 `.1`，30 天清理覆盖轮转文件 | `MainWindow.xaml.cs` | 代码复核 |
| I-2 | 发现协议应答加全局 200 次/秒预算（1 字节探针 → ≤260 字节应答的放大被限速） | `DeviceDiscovery.cs` | 代码复核 |
| I-6 | CLI `send`/`sync` 在 `OnError` 未触发时也把异常写到 stderr，退出码不再是唯一信号 | `Cli.cs` | 代码复核 |
| V-15 | 覆盖位图改为**首次写入时**分配：只有 START、没有数据的会话不再预占 2 MB | `TransferUdp.cs` | 既有 UDP 用例 |
| L-1 | 配对码落盘改为 DPAPI（CurrentUser）加密，前缀 `dpapi:`；旧明文值兼容读取、下次保存自动迁移 | `SecretProtector.cs`（新增）、`MainWindow.xaml.cs`、`TrFileTransfer.csproj`（+System.Security） | 构建 + 手工加载旧配置（代码复核） |
| L-7 | 0x04 文件夹续传会话 ID 绑定首个对端（进程级表 + 上限），外来对端重放 ID 被拒 | `WireProtocol.cs`（`FolderSessionPeers`） | 既有文件夹同步/续传用例 |
| L-8 | UDT `udt_connect` 的错误描述在**调用所在线程内**捕获（TLS 错误串不再跨线程读空） | `TransferUdt.cs` | 既有 UDT 用例 |
| L-9 | UDP `Stop()` 先停收包再清会话；`HandleStart` 在锁内复查运行标志，避免清表后新会话漏关；CTS 释放 | `TransferUdp.cs` | 既有 UDP 用例 |
| L-10 | 内嵌 `udt.dll`/`libmcfgthread-2.dll` 落盘/加载前按**内嵌副本的 SHA-256** 校验；不匹配且无法覆盖时**拒绝加载**（不再回退到仍会被优先加载的同目录 DLL）；测试构建无嵌入资源时保持原行为 | `TransferUdt.cs`（`UdtDll`） | `Integration_UDT_*` + 构建 |

### 10.2 兼容性说明（重要）

- **新客户端 ↔ 旧服务器（2.12–2.15）**：新客户端先发 `0x0C`；旧服务器当未知类型断开 → 客户端（允许降级时）重连改用 `0x09`。`EncryptStrict=1` 下不降级、直接报「对端过旧」。
- **旧客户端（≤2.15）↔ 新服务器**：服务器保留 `0x09` 处理器，并在日志记录弱握手警告（该帧仍携带配对码哈希）。
- **0x0A bit2**：旧客户端只请求 bit0/bit1，新服务器应答 `0x07` 不影响旧客户端（按位与校验）；旧服务器不应答 0x0A，新客户端回退时不再等待判决字节。
- **旧对端的协商成本**：关闭压缩时新客户端会先发 0x0A bit2 证明对端版本；因此对「连 0x0A 都不认识」的 ≤2.12 对端，一次普通发送会多一次重连（0x08 被拒 → 0x0A 被拒 → 明文 0x00），这是互操作代价，不是回归。
- **HTTP 共享行为变化**：未设置访问码时共享变为**只读**（上传/删除 403，页面不再显示入口）。这是有意的安全策略；需要写入请启用配对码（共享复用配对码作为访问码）。
- **HTTP 超时行为**：静默连接现在会被直接关闭（不再回 408），因为只有关闭 socket 才能真正中断 net48 上已挂起的读。
- **配对码落盘格式**：`config.ini` 中为 `dpapi:<base64>`（CurrentUser）。旧明文配置可直接读取并在下次保存时迁移；**降级到 ≤2.15 需重新输入配对码**，把配置复制到另一台机器/账户后同理。

### 10.3 验证

- 主工程 `dotnet build -c Release`：**0 error**（仅既有 XML 注释类告警）。
- 测试构建（老 `csc.exe`，C# 5）：**成功**（证明 20 个逻辑文件仍守语法边界）。
- 新增回归用例 12 项：`Feature_TCP_StrictRejectsPlainStatus2`、`Feature_TCP_LegacyAuthFallback`、`Feature_TCP_CompletionAck_NoCompression`、`Feature_TCP_ResumeCompleteVerifiesFullHash`、`Integration_HTTP_ReadOnlyWithoutCode`、`Integration_HTTP_UploadRejectsCrossSite`、`Integration_HTTP_SlowlorisReleasesSlots`、`Integration_HTTP_RejectsForeignHost`、`ChunkTracker_IdleSweep_ClosesAbandonedTrackers`、`SanitizeRelativePath_StripsControlAndInvalidChars`、`Update_Check_RejectsInsecureRedirect`、`Update_GitHubPrefersCanonicalAsset`（其中三项为工具函数单测）。
- 全量套件在修复过程中**发现并修掉两个回归**：①0x0C 客户端在 confirmS 不符时本地中止，服务器不再计数 → 认证锁定失效（改为中途丢弃也计数）；②关闭压缩后新增的 0x0A 探测改变了旧对端回退序列（测试与文档同步更新）。这正是"修复后必须跑全量"的价值。
- 全量测试套件（该批最终代码、最终构建）：**308 passed / 0 failed（308 项）**，退出码 0（后续单向 UDP v2 批次再增 5 项，当前全量 **313 项**）。定向运行（`TF_TEST_FILTER`）覆盖每条新回归用例；全量运行在修复过程中先发现 2 个回归（认证锁定计数、旧对端回退序列），修复后重跑全绿。
- **UDT DLL 完整性端到端验证**（真实 exe、无预置 DLL 的临时目录）：首次启动提取 `udt.dll`/`libmcfgthread-2.dll`，提取文件哈希与内嵌副本一致；UDT 传输收到文件；把 `udt.dll` 改成垃圾后重启 → 被自动恢复为内嵌副本且服务正常启动。
- 配对码 DPAPI 为 UI 层逻辑（测试构建不引用 `System.Security`），以代码复核 + 构建覆盖；旧明文配置读取路径保持兼容。

### 10.4 仍保留的已知风险（不在本轮范围）

- V-8 的兜底为 10 分钟空闲回收：被放弃的分块组在回收前仍占一个 `FileShare.None` 句柄。
- V-21 的**清单签名**与 exe 代码签名未做（需要发布密钥/签名流程）；https + 边车哈希 + 逐跳校验仍是同源信任模型。
- 0x04 会话绑定是**进程级**的：服务器重启后，第一个使用该会话 ID 的对端重新获得绑定（ID 是随机 GUID，实际风险低）。
- L-11 CI action 未固定 commit SHA；L-13 exe 未做代码签名。
- TCP 连接上限（64）、UDP 会话按来源绑定、0x04 对端绑定、UDT DLL 完整性目前主要靠代码复核与既有集成用例覆盖，尚未加针对时序/并发边界的自动化用例；`Feature_TCP_ResumeCompleteVerifiesFullHash` 已覆盖 0x03「已收满」分支。

