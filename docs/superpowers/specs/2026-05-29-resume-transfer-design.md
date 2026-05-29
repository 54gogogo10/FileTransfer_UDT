# Resume Transfer (断点续传) Design

## Summary

断点续传功能——手动取消或网络断开后，可从断点继续传输。客户端持久化进度，服务端内存中维护状态，重连时双方协商起点。覆盖单文件和文件夹传输（不含并发分块）。

## Wire Protocol

### New Type `0x03` — Resumable File

```
[1 byte:  0x03]               // 可续传文件标记
[16 bytes: sessionId]         // Guid，客户端生成
[8 bytes: Int64 totalFileSize] // 文件总大小
[8 bytes: Int64 resumeOffset]  // 客户端请求从此偏移开始发送（首次=0）
[4 bytes: Int32 nameLen]
[N bytes: UTF-8 fileName]
[M bytes: fileContent]        // 从 resumeOffset 开始的文件数据
[32 bytes: SHA256]            // 整个文件内容的 SHA-256
```

### New Type `0x10` — Resume Response (Server → Client)

```
[1 byte:  0x10]               // 续传协商响应
[8 bytes: Int64 serverOffset] // 服务端已收字节数
[1 byte:  status]             // 0=新传输, 1=续传, 2=已完成
```

### Flow

```
客户端                                    服务端
  |                                         |
  |--- 0x03(sessionId, fileSize, 0, name) ->|  首次
  |                                         |  查表: sessionId 不存在
  |<-- 0x10(offset=0, status=0_new) --------|  从头收
  |--- DATA... (传输中断)                     |
  |                                         |
  |--- 0x03(sessionId, fileSize, N, name) ->|  续传
  |                                         |  查表: sessionId → 已收=K
  |<-- 0x10(offset=K, status=1_resume) -----|
  |--- DATA from K...                      |  客户端 Skip(K), 从 K 发
  |--- SHA256                              |
  |<-- 0x10(offset=fileSize, status=2) -----|  传输完成
```

## State Persistence

### Client — `%AppData%\TrFileTransfer\resume\<sessionId>.json`

```json
{
  "sessionId": "a1b2c3d4-...",
  "fileName": "bigfile.iso",
  "filePath": "D:\\videos\\bigfile.iso",
  "fileSize": 5242880000,
  "serverIp": "192.168.1.100",
  "port": 8080,
  "sentBytes": 1073741824,
  "isUdt": false,
  "created": "2026-05-29T10:30:00"
}
```

- 每完成一个 I/O 块（~100ms 节流）更新 `sentBytes`
- 传输完成后删除对应 `.json`
- 客户端启动时扫描 `resume\` 目录展示未完成列表
- 用户可选中继续传输或删除（放弃）

### Server — Memory Only

`ConcurrentDictionary<Guid, ResumeState>` 内存表，不落盘：

- sessionId → `{ receivedBytes, savePath, fileStream, totalSize, fileName }`
- 服务端重启后所有状态清空——客户端续传收到 status=0，回退为全新传输

## File Transfer Changes

### TransferClient

- `SendAsync()`: 先检查 `%AppData%\...\<sessionId>.json` 是否存在，存在则弹窗确认是否续传
- `SendResumableAsync(sessionId)`: 读取 `.json` 获取 `sentBytes`，构造 0x03 头部带 `resumeOffset`
- 发送数据从 `resumeOffset` 开始，用 `SendFilePayload` 的已有 `fileOffset` 参数
- 新增 `OnServerOffset(offset)` 解析 0x10 响应，若 `serverOffset > clientSentBytes` 则以服务端为准（服务端才是权威）
- 传输中每 100ms 节流更新 `.json` 的 `sentBytes`

### TransferServer

- `HandleClient`: 读取到 0x03 时调用 `HandleResumableFile`
- `HandleResumableFile`:
  1. 读取 sessionId、totalSize、offset、name
  2. 查 `_resumeStates` 字典
  3. 若 key 不存在 → 新建 `ResumeState`，创建文件流 + `SetLength`，写回 0x10(status=0)
  4. 若 key 存在 → 比对 `totalSize`，若不一致则报错；否则写回 0x10(status=1, serverOffset)
  5. 若已完成 → 写回 0x10(status=2)，跳过
  6. 接收剩余数据（从 serverOffset 开始写入），更新 `receivedBytes`
  7. 完成时计算 SHA-256，success → 删除状态 + 触发 `OnTransferComplete`

### TransferUdt (Server + Client)

复用相同线协议。UDT 客户端发送 0x03 头部，服务端解析并返回 0x10。传输层对应用层透明。

## Folder Transfer

文件夹模式中每个文件使用独立的 sessionId，各自携带 resume 元数据。服务端为每个文件维护独立 `ResumeState`。

续传文件夹时：
- 客户端扫描已完成文件（`.json` 中 `sentBytes == fileSize` 且找不到旧会话的），跳过
- 未完成文件从各自的 `sentBytes` 继续

## UI Changes

### Client Panel

- 传输中：进度卡片显示 `已发 X / Y`；新增"可续传"提示
- 新增"续传任务"按钮 → 弹出 `ResumeDialog` 窗口：
  - 列表：文件名、服务器 IP:Port、总大小、进度百分比、日期
  - 每行右侧：[继续] [删除] 按钮
  - 全部清空按钮

### Resume Prompt

- 用户点击"发送"时若检测到已有 `.json`（匹配 filePath + serverIp + port）：弹窗 "上次传输未完成，是否续传？" [续传] [重新发送]
- 选"续传" → 从 `sentBytes` 继续
- 选"重新发送" → 删除 `.json`，删除服务端旧状态（通过 0x03 + reserveOffset=0 且新 sessionId）

## L10N

- `L.ResumePrompt` — "上次传输未完成，是否续传？"
- `L.ResumeBtn` — "续传"
- `L.ReSendBtn` — "重新发送"
- `L.ResumeListTitle` — "续传任务"
- `L.ResumeContinue` — "继续"
- `L.ResumeDelete` — "删除"
- `L.ResumeClearAll` — "全部清空"
- `L.ResumeStatus` — "可续传"

## Edge Cases

1. **服务端重启后客户端来续传**: 收到 status=0，客户端回退为从头发送
2. **客户端 `.json` 与服务端状态不一致**（客户端崩溃后服务端仍在跑）: 以服务端 `receivedBytes` 为准（0x10 响应中的 serverOffset）
3. **SHA-256 覆盖全文件**: 客户端发送完整文件 SHA-256（非断点部分），服务端在文件完成时校验全文件
4. **同名文件+不同 sessionId**: 不同传输，不冲突
5. **同一 sessionId 多个连接**: 服务端发现已有活跃状态，写回 0x10(status=0, serverOffset=0) 并关闭该新连接（不覆盖已有状态）

## Testing

- Unit: `ResumeState` 序列化/反序列化，`sessionId` 匹配逻辑
- Integration: TCP 切断后重连续传、UDT 切断后重连续传、文件夹续传
- Edge: 服务端重启续传回退、进度不一致以服务端为准
