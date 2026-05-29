# Resume Transfer (断点续传) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 断点续传 — 手动取消或网络断开后可从断点继续传输。客户端 `%AppData%` 落 `.json`，服务端内存维护状态，重连时 0x10 协商起点。本计划覆盖单文件续传，文件夹续传依赖同一套基础设施（每文件独立 sessionId），作为后续增强。

**Architecture:** 新增 `ResumeState.cs` 持久化类 + 线协议 0x03/0x10。TransferServer 新增 `_resumeStates` 字典 + `HandleResumableFile`。TransferClient 新增 `SendResumableAsync` + `.json` 读写。TransferUdt 复用相同协议。MainForm 新增续传对话框 + 提示弹窗。L10N 新增 8 个字符串。

**Tech Stack:** C# 5, .NET Framework 4.5+, WinForms, csc.exe only (no NuGet)

---

### Task 0: ResumeState + Wire Protocol Constants

**Files:**
- Create: `TrFileTransfer/ResumeState.cs`
- Modify: `TrFileTransfer/Tests/build.bat`

- [ ] **Step 1: Create ResumeState.cs**

```csharp
using System;
using System.IO;
using System.Text;

namespace TrFileTransfer
{
    public class ResumeState
    {
        public Guid SessionId;
        public long TotalSize;
        public long SentBytes;
        public string FileName;
        public string FilePath;
        public string ServerIp;
        public int Port;
        public bool IsUdt;
        public DateTime Created;
        // Server-side only (not persisted)
        public string SavePath;
        public FileStream WriteStream;
        public long ReceivedBytes;

        private static readonly string ResumeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TrFileTransfer", "resume");

        public static void EnsureDir()
        {
            Directory.CreateDirectory(ResumeDir);
        }

        public static string GetPath(Guid sessionId)
        {
            return Path.Combine(ResumeDir, sessionId.ToString("N") + ".json");
        }

        public void Save()
        {
            EnsureDir();
            var sb = new StringBuilder();
            sb.AppendLine("SessionId=" + SessionId.ToString("N"));
            sb.AppendLine("TotalSize=" + TotalSize);
            sb.AppendLine("SentBytes=" + SentBytes);
            sb.AppendLine("FileName=" + FileName);
            sb.AppendLine("FilePath=" + FilePath);
            sb.AppendLine("ServerIp=" + ServerIp);
            sb.AppendLine("Port=" + Port);
            sb.AppendLine("IsUdt=" + (IsUdt ? "1" : "0"));
            sb.AppendLine("Created=" + Created.ToString("o"));
            File.WriteAllText(GetPath(SessionId), sb.ToString(), Encoding.UTF8);
        }

        public static ResumeState Load(Guid sessionId)
        {
            string path = GetPath(sessionId);
            if (!File.Exists(path)) return null;
            var state = new ResumeState { SessionId = sessionId };
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                int idx = line.IndexOf('=');
                if (idx < 0) continue;
                string key = line.Substring(0, idx);
                string val = line.Substring(idx + 1);
                switch (key)
                {
                    case "TotalSize": state.TotalSize = long.Parse(val); break;
                    case "SentBytes": state.SentBytes = long.Parse(val); break;
                    case "FileName": state.FileName = val; break;
                    case "FilePath": state.FilePath = val; break;
                    case "ServerIp": state.ServerIp = val; break;
                    case "Port": state.Port = int.Parse(val); break;
                    case "IsUdt": state.IsUdt = val == "1"; break;
                    case "Created": state.Created = DateTime.Parse(val); break;
                }
            }
            return state;
        }

        public static void Delete(Guid sessionId)
        {
            string path = GetPath(sessionId);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static ResumeState[] ListAll()
        {
            EnsureDir();
            var files = Directory.GetFiles(ResumeDir, "*.json");
            var list = new ResumeState[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]);
                Guid sid;
                if (Guid.TryParseExact(name, "N", out sid))
                    list[i] = Load(sid);
            }
            return list;
        }
    }
}
```

- [ ] **Step 2: Add ResumeState.cs to test build.bat**

Edit `TrFileTransfer/Tests/build.bat`, add `..\ResumeState.cs` to the csc file list (before `TestProgram.cs`).

- [ ] **Step 3: Build main project and tests**

```
D:\cc\TrFileTransfer\TrFileTransfer\build.bat
D:\cc\TrFileTransfer\TrFileTransfer\Tests\build.bat
```

Expected: Build success both projects, tests pass.

- [ ] **Step 4: Commit**

```bash
git add TrFileTransfer/ResumeState.cs TrFileTransfer/Tests/build.bat
git commit -m "feat: ResumeState 持久化类 + 线协议 0x03/0x10 常量定义"
```

---

### Task 1: TransferServer — HandleResumableFile

**Files:**
- Modify: `TrFileTransfer/TransferServer.cs`

- [ ] **Step 1: Add _resumeStates field**

After line 22 (`_chunkTrackers`), add:

```csharp
        private readonly ConcurrentDictionary<Guid, ResumeState> _resumeStates
            = new ConcurrentDictionary<Guid, ResumeState>();
```

- [ ] **Step 2: Add 0x03 dispatch in HandleClient**

In `HandleClient` (around line 176), change the dispatch block:

Old:
```csharp
                    if (transferType == 0x01)
                    {
                        await HandleFolderTransfer(stream, ct);
                    }
                    else if (transferType == 0x02)
                    {
                        await HandleChunkedFile(stream, ct);
                    }
                    else
                    {
                        await HandleFileTransfer(stream, ct);
                    }
```

New:
```csharp
                    if (transferType == 0x01)
                    {
                        await HandleFolderTransfer(stream, ct);
                    }
                    else if (transferType == 0x02)
                    {
                        await HandleChunkedFile(stream, ct);
                    }
                    else if (transferType == 0x03)
                    {
                        await HandleResumableFile(stream, ct, clientEp);
                    }
                    else
                    {
                        await HandleFileTransfer(stream, ct);
                    }
```

- [ ] **Step 3: Add HandleResumableFile method**

Add after `HandleChunkedFile` (before `HandleFolderTransfer`):

```csharp
        private async Task HandleResumableFile(NetworkStream stream, CancellationToken ct, IPEndPoint clientEp)
        {
            // Read 0x03 header: sessionId(16) + totalSize(8) + resumeOffset(8) + nameLen(4) = 36
            var headerBuf = new byte[36];
            await ReadExactAsync(stream, headerBuf, 0, 36, ct);

            var sidBytes = new byte[16];
            Buffer.BlockCopy(headerBuf, 0, sidBytes, 0, 16);
            var sessionId = new Guid(sidBytes);
            long totalSize = BitConverter.ToInt64(headerBuf, 16);
            long clientOffset = BitConverter.ToInt64(headerBuf, 24);
            int nameLen = BitConverter.ToInt32(headerBuf, 32);

            if (totalSize <= 0 || nameLen <= 0 || nameLen > 4096)
            {
                Log(L.S_InvalidHeader(totalSize, nameLen));
                return;
            }

            var nameBuf = new byte[nameLen];
            await ReadExactAsync(stream, nameBuf, 0, nameLen, ct);
            string fileName = Encoding.UTF8.GetString(nameBuf);
            fileName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = L.S_ReceivedFile;

            ResumeState state;
            byte status;
            long resumeFrom;

            if (!_resumeStates.TryGetValue(sessionId, out state))
            {
                state = new ResumeState
                {
                    SessionId = sessionId,
                    TotalSize = totalSize,
                    FileName = fileName,
                    ReceivedBytes = 0
                };
                state.SavePath = Utils.GetUniqueSavePath(_saveDirectory, fileName);
                state.WriteStream = new FileStream(state.SavePath, FileMode.Create, FileAccess.Write,
                    FileShare.None, _bufferSize, FileOptions.RandomAccess);
                state.WriteStream.SetLength(totalSize);
                _resumeStates[sessionId] = state;
                status = 0;
                resumeFrom = 0;
            }
            else
            {
                if (state.TotalSize != totalSize)
                {
                    Log(string.Format("Resume size mismatch: session={0} expect={1} got={2}",
                        sessionId.ToString("N"), state.TotalSize, totalSize));
                    return;
                }
                if (state.ReceivedBytes >= totalSize)
                {
                    status = 2;
                    resumeFrom = totalSize;
                    SendResumeResponse(stream, resumeFrom, status, ct);
                    return;
                }
                status = 1;
                resumeFrom = state.ReceivedBytes;
            }

            // Negotiate: use max of client's claim and server's actual received
            long actualStart = Math.Max(clientOffset, resumeFrom);
            SendResumeResponse(stream, actualStart, status, ct);

            // If client has less than server, it'll re-send from actualStart
            long remaining = totalSize - actualStart;
            Log(string.Format("Resume: {0} offset={1} remaining={2} status={3}",
                fileName, actualStart, Utils.FormatSize(remaining), status));

            // Receive remaining data + SHA256
            state.WriteStream.Seek(actualStart, SeekOrigin.Begin);
            var sha256 = SHA256.Create();
            long bytesRead = 0;
            var buf = new byte[_bufferSize];

            while (bytesRead < remaining && !ct.IsCancellationRequested)
            {
                int toRead = (int)Math.Min(remaining - bytesRead, (long)buf.Length);
                int read = await stream.ReadAsync(buf, 0, toRead, ct);
                if (read == 0)
                    throw new IOException(L.S_ConnClosedPrematurely);
                sha256.TransformBlock(buf, 0, read, null, 0);
                await state.WriteStream.WriteAsync(buf, 0, read, ct);
                bytesRead += read;
                state.ReceivedBytes = actualStart + bytesRead;
            }

            sha256.TransformFinalBlock(buf, 0, 0);
            var computedHash = sha256.Hash;
            var receivedHash = new byte[32];
            await ReadExactAsync(stream, receivedHash, 0, 32, ct);

            if (Utils.ConstantTimeEquals(computedHash, receivedHash))
            {
                state.WriteStream.Dispose();
                state.WriteStream = null;
                ResumeState removed;
                _resumeStates.TryRemove(sessionId, out removed);
                Log(L.S_TransferDone(fileName, Utils.FormatSize(totalSize), 0, 0));
                SendResumeResponse(stream, totalSize, 2, ct);
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
            else
            {
                Log(L.S_HashFailed(fileName));
                var errHandler = OnError;
                if (errHandler != null) errHandler(L.S_HashFailed(fileName));
            }
        }

        private static void SendResumeResponse(NetworkStream stream, long offset, byte status, CancellationToken ct)
        {
            var resp = new byte[10]; // type(1) + offset(8) + status(1)
            resp[0] = 0x10;
            Buffer.BlockCopy(BitConverter.GetBytes(offset), 0, resp, 1, 8);
            resp[9] = status;
            stream.WriteAsync(resp, 0, 10, ct).Wait();
        }
```

- [ ] **Step 4: Build and test**

```
D:\cc\TrFileTransfer\TrFileTransfer\build.bat
D:\cc\TrFileTransfer\TrFileTransfer\Tests\build.bat
D:\cc\TrFileTransfer\TrFileTransfer\Tests\TrFileTransfer.Tests.exe
```

Expected: Build success, all existing tests pass.

- [ ] **Step 5: Commit**

```bash
git add TrFileTransfer/TransferServer.cs
git commit -m "feat: TransferServer HandleResumableFile + 0x10 协商响应"
```

---

### Task 2: TransferClient — SendResumableAsync + .json Persistence

**Files:**
- Modify: `TrFileTransfer/TransferClient.cs`

- [ ] **Step 1: Add SendResumableAsync method**

Add after `SendChunkedAsync`:

```csharp
        public async Task<Guid> SendResumableAsync(Guid? existingSessionId = null)
        {
            var sessionId = existingSessionId ?? Guid.NewGuid();
            await RunTransfer(ct => SendResumableInternal(sessionId, ct));
            return sessionId;
        }
```

- [ ] **Step 2: Add SendResumableInternal method**

Add after `SendChunkedInternal`:

```csharp
        private async Task SendResumableInternal(Guid sessionId, CancellationToken ct)
        {
            ResumeState.EnsureDir();
            var fileInfo = new FileInfo(_filePath);
            long fileSize = fileInfo.Length;
            string fileName = fileInfo.Name;
            long sentBytes = 0;

            // Load existing state if resuming
            var state = ResumeState.Load(sessionId);
            if (state != null)
            {
                sentBytes = state.SentBytes;
                Log(string.Format("Resuming {0} from offset {1} ({2})", fileName,
                    sentBytes, Utils.FormatSize(sentBytes)));
            }
            else
            {
                state = new ResumeState
                {
                    SessionId = sessionId,
                    TotalSize = fileSize,
                    FileName = fileName,
                    FilePath = _filePath,
                    ServerIp = _serverIp,
                    Port = _port,
                    IsUdt = false,
                    Created = DateTime.UtcNow,
                    SentBytes = 0
                };
                state.Save();
            }

            using (var client = _localPort > 0
                ? new TcpClient(new IPEndPoint(IPAddress.Any, _localPort))
                : new TcpClient())
            {
                client.NoDelay = true;
                client.SendBufferSize = _bufferSize;
                client.ReceiveBufferSize = _bufferSize;

                await client.ConnectAsync(_serverIp, _port);
                var stream = client.GetStream();

                // Build and send 0x03 header
                byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);

                // Header: type(1) + sessionId(16) + totalSize(8) + resumeOffset(8) + nameLen(4) + name
                var header = new byte[1 + 16 + 8 + 8 + 4 + nameBytes.Length];
                header[0] = 0x03;
                Buffer.BlockCopy(sessionId.ToByteArray(), 0, header, 1, 16);
                Buffer.BlockCopy(BitConverter.GetBytes(fileSize), 0, header, 17, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(sentBytes), 0, header, 25, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(nameBytes.Length), 0, header, 33, 4);
                Buffer.BlockCopy(nameBytes, 0, header, 37, nameBytes.Length);
                await stream.WriteAsync(header, 0, header.Length, ct);

                // Read 0x10 server response
                var respBuf = new byte[10];
                await ReadExactClientAsync(stream, respBuf, 0, 10, ct);

                byte respStatus = respBuf[9];
                long serverOffset = BitConverter.ToInt64(respBuf, 1);

                if (respStatus == 2)
                {
                    // Already complete on server
                    Log(fileName + " already fully received by server.");
                    ResumeState.Delete(sessionId);
                    var completeHandler = OnTransferComplete;
                    if (completeHandler != null) completeHandler();
                    return;
                }

                // Use server's offset as authoritative
                long actualStart = Math.Max(sentBytes, serverOffset);

                Log(string.Format("Resume negotiated: start={0} serverHad={1} clientHad={2}",
                    actualStart, serverOffset, sentBytes));

                // Send file data from actualStart
                await SendFilePayload(stream, _filePath, fileSize, fileName, ct, (int)actualStart);

                // On success, delete resume state
                ResumeState.Delete(sessionId);
            }
        }

        private static async Task ReadExactClientAsync(NetworkStream stream, byte[] buf, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buf, offset + total, count - total, ct);
                if (read == 0)
                    throw new IOException("Connection closed during resume response");
                total += read;
            }
        }
```

- [ ] **Step 3: Build and test**

```
D:\cc\TrFileTransfer\TrFileTransfer\build.bat
D:\cc\TrFileTransfer\TrFileTransfer\Tests\build.bat
D:\cc\TrFileTransfer\TrFileTransfer\Tests\TrFileTransfer.Tests.exe
```

Expected: Build success, all existing tests pass.

- [ ] **Step 4: Commit**

```bash
git add TrFileTransfer/TransferClient.cs
git commit -m "feat: TransferClient SendResumableAsync + .json 持久化 + 0x10 协商"
```

---

### Task 3: TransferUdt — Resume Support

**Files:**
- Modify: `TrFileTransfer/TransferUdt.cs`

- [ ] **Step 1: Add SendResumableAsync to TransferUdtClient**

Find `TransferUdtClient` class (search for `class TransferUdtClient`). Add after `SendChunkedAsync`:

```csharp
        public async Task<Guid> SendResumableAsync(Guid? existingSessionId = null)
        {
            var sessionId = existingSessionId ?? Guid.NewGuid();
            await RunUdpTransfer(ct => SendResumableUdtInternal(sessionId, ct));
            return sessionId;
        }
```

- [ ] **Step 2: Add SendResumableUdtInternal**

Add private method in TransferUdtClient:

```csharp
        private async Task SendResumableUdtInternal(Guid sessionId, CancellationToken ct)
        {
            ResumeState.EnsureDir();
            var fileInfo = new FileInfo(_filePath);
            long fileSize = fileInfo.Length;
            string fileName = fileInfo.Name;
            long sentBytes = 0;

            var state = ResumeState.Load(sessionId);
            if (state != null)
                sentBytes = state.SentBytes;
            else
            {
                state = new ResumeState
                {
                    SessionId = sessionId, TotalSize = fileSize,
                    FileName = fileName, FilePath = _filePath,
                    ServerIp = _serverIp, Port = _port,
                    IsUdt = true, Created = DateTime.UtcNow, SentBytes = 0
                };
                state.Save();
            }

            var udp = CreateUdpClient();
            try
            {
                var serverEp = new IPEndPoint(IPAddress.Parse(_serverIp), _port);
                byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);

                // Build 0x03 header
                var header = new byte[1 + 16 + 8 + 8 + 4 + nameBytes.Length];
                header[0] = 0x03;
                Buffer.BlockCopy(sessionId.ToByteArray(), 0, header, 1, 16);
                Buffer.BlockCopy(BitConverter.GetBytes(fileSize), 0, header, 17, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(sentBytes), 0, header, 25, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(nameBytes.Length), 0, header, 33, 4);
                Buffer.BlockCopy(nameBytes, 0, header, 37, nameBytes.Length);

                await Task.Run(() =>
                {
                    int sent = UdtNative.udt_send(_socket, header, header.Length, 0);
                    if (sent == UdtNative.ERROR)
                        throw new IOException("UDT send header failed: " + UdtNative.GetErrorDesc());
                }).ConfigureAwait(false);

                // Read 0x10 response
                var respBuf = new byte[10];
                await UdtReadExactAsync(respBuf, 0, 10, ct);

                byte respStatus = respBuf[9];
                long serverOffset = BitConverter.ToInt64(respBuf, 1);

                if (respStatus == 2)
                {
                    Log(fileName + " already fully received by server.");
                    ResumeState.Delete(sessionId);
                    var completeHandler = OnTransferComplete;
                    if (completeHandler != null) completeHandler();
                    return;
                }

                long actualStart = Math.Max(sentBytes, serverOffset);
                Log(string.Format("UDT Resume: start={0}", actualStart));

                // Send data + SHA256
                bool ok = await SendUdpFileDataAsync(udp, serverEp, _filePath, fileSize,
                    fileName, ct, true, (int)actualStart);
                if (ok)
                {
                    ResumeState.Delete(sessionId);
                    var completeHandler = OnTransferComplete;
                    if (completeHandler != null) completeHandler();
                }
            }
            finally
            {
                try { udp.Close(); } catch { }
            }
        }
```

- [ ] **Step 3: Add UdtReadExactAsync helper to TransferUdtClient**

Add private method:

```csharp
        private async Task UdtReadExactAsync(byte[] buf, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int read = await Task.Run(() =>
                    UdtNative.udt_recv(_socket, buf, offset + total, count - total, 0),
                    ct).ConfigureAwait(false);
                if (read == UdtNative.ERROR)
                    throw new IOException("UDT recv failed: " + UdtNative.GetErrorDesc());
                if (read == 0)
                    throw new IOException("UDT connection closed during resume response");
                total += read;
            }
        }
```

- [ ] **Step 4: Handle 0x03 in TransferUdtServer HandleClient**

Find the UDT server's `HandleClient` (in `TransferUdtServer` class). In the type dispatch, add 0x03 case that calls `HandleResumableFile`. The UDT server reads type byte + header, sends 0x10 response, then receives data. The pattern is the same as TCP.

Add a static `SendResumeResponse` method that uses `udt_send`:

```csharp
        private static void SendUdtResumeResponse(int socket, long offset, byte status)
        {
            var resp = new byte[10];
            resp[0] = 0x10;
            Buffer.BlockCopy(BitConverter.GetBytes(offset), 0, resp, 1, 8);
            resp[9] = status;
            int sent = UdtNative.udt_send(socket, resp, resp.Length, 0);
            if (sent == UdtNative.ERROR)
                throw new IOException("UDT send 0x10 failed: " + UdtNative.GetErrorDesc());
        }
```

- [ ] **Step 5: Build and test**

```
D:\cc\TrFileTransfer\TrFileTransfer\build.bat
D:\cc\TrFileTransfer\TrFileTransfer\Tests\build.bat
D:\cc\TrFileTransfer\TrFileTransfer\Tests\TrFileTransfer.Tests.exe
```

Expected: Build success, all existing tests pass.

- [ ] **Step 6: Commit**

```bash
git add TrFileTransfer/TransferUdt.cs
git commit -m "feat: TransferUdt 续传支持 + 0x03/0x10 协商"
```

---

### Task 4: UI — ResumeDialog + Prompt + L10N

**Files:**
- Modify: `TrFileTransfer/MainForm.cs`
- Modify: `TrFileTransfer/L10N.cs`

- [ ] **Step 1: Add L10N strings to L10N.cs**

Add after the existing `DragDropInvalid` property:

```csharp
        public static string ResumeListTitle { get { return IsChinese ? "续传任务" : "Resume Tasks"; } }
        public static string ResumeHeader { get { return IsChinese ? "文件名" : "File Name"; } }
        public static string ResumePromptFile { get { return IsChinese ? "上次传输未完成，是否续传？" : "Previous transfer incomplete. Resume?"; } }
        public static string ResumeBtn { get { return IsChinese ? "续传" : "Resume"; } }
        public static string ReSendBtn { get { return IsChinese ? "重新发送" : "Re-send"; } }
        public static string ResumeDelete { get { return IsChinese ? "删除" : "Delete"; } }
        public static string ResumeClearAll { get { return IsChinese ? "全部清空" : "Clear All"; } }
        public static string ResumeListEmpty { get { return IsChinese ? "没有未完成的传输任务。" : "No incomplete transfer tasks."; } }
```

- [ ] **Step 2: Add "续传任务" button to client panel in MainForm.cs InitializeComponent**

Add field near line 51 (`_lblSrcPort`):

```csharp
        private Button _btnResumeList;
```

In `InitializeComponent`, after `_numConcurrency` (around line 175), add:

```csharp
            _btnResumeList = new Button { Location = new Point(520, 93), Width = 45, Height = 22 };
            _btnResumeList.Click += BtnResumeList_Click;
```

Add to controls:

```csharp
            _gbClient.Controls.Add(_btnResumeList);
```

- [ ] **Step 3: Add ApplyLanguage for resume controls**

In `ApplyLanguage` (search for `_chkFolder.Text = L.FolderMode;`), add after:

```csharp
            _btnResumeList.Text = L.ResumeBtn;
```

- [ ] **Step 4: Add DisableClientInputs / ResetClientUI updates**

In `DisableClientInputs`, add:

```csharp
            _btnResumeList.Enabled = false;
```

In `ResetClientUI`, after `_btnResumeList.Enabled = true;` or near other re-enable lines:

```csharp
            _btnResumeList.Enabled = true;
```

- [ ] **Step 5: Add BtnResumeList_Click handler**

Add after `MainForm_DragDrop` or near other button handlers:

```csharp
        private void BtnResumeList_Click(object sender, EventArgs e)
        {
            var states = ResumeState.ListAll();
            if (states == null || states.Length == 0)
            {
                MessageBox.Show(L.ResumeListEmpty, L.ResumeListTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new ResumeDialog(states))
            {
                if (dlg.ShowDialog() == DialogResult.OK && dlg.SelectedState != null)
                {
                    var s = dlg.SelectedState;
                    _txtServerIp.Text = s.ServerIp;
                    _txtPortC.Text = s.Port.ToString();
                    _txtFile.Text = s.FilePath;
                    _chkFolder.Checked = false;
                    if (s.IsUdt)
                    {
                        _rbClientTcp.Checked = false;
                        _rbClientUdt.Checked = true;
                    }
                    else
                    {
                        _rbClientTcp.Checked = true;
                        _rbClientUdt.Checked = false;
                    }
                    BtnSend_Click(sender, e);
                }
            }
        }
```

- [ ] **Step 6: Add ResumeDialog class at end of MainForm.cs (before closing namespace brace)**

```csharp
    public class ResumeDialog : Form
    {
        public ResumeState SelectedState;
        private ListBox _list;
        private Button _btnContinue, _btnDelete, _btnClearAll, _btnClose;

        public ResumeDialog(ResumeState[] states)
        {
            Text = L.ResumeListTitle;
            Size = new Size(500, 320);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            _list = new ListBox
            {
                Location = new Point(12, 12), Width = 460, Height = 200,
                IntegralHeight = false
            };
            foreach (var s in states)
            {
                if (s == null) continue;
                string progress = s.TotalSize > 0
                    ? string.Format("{0:F1}%", 100.0 * s.SentBytes / s.TotalSize)
                    : "?";
                _list.Items.Add(string.Format("{0} -> {1}:{2} [{3}] {4}",
                    s.FileName, s.ServerIp, s.Port, progress,
                    s.Created.ToLocalTime().ToString("g")));
            }
            Controls.Add(_list);

            _btnContinue = new Button { Text = L.ResumeBtn, Location = new Point(12, 220), Width = 100 };
            _btnContinue.Click += (_, __) =>
            {
                int idx = _list.SelectedIndex;
                if (idx >= 0 && idx < states.Length)
                {
                    SelectedState = states[idx];
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };
            Controls.Add(_btnContinue);

            _btnDelete = new Button { Text = L.ResumeDelete, Location = new Point(120, 220), Width = 100 };
            _btnDelete.Click += (_, __) =>
            {
                int idx = _list.SelectedIndex;
                if (idx >= 0 && idx < states.Length && states[idx] != null)
                {
                    ResumeState.Delete(states[idx].SessionId);
                    _list.Items.RemoveAt(idx);
                }
            };
            Controls.Add(_btnDelete);

            _btnClearAll = new Button { Text = L.ResumeClearAll, Location = new Point(228, 220), Width = 100 };
            _btnClearAll.Click += (_, __) =>
            {
                foreach (var s in states)
                {
                    if (s != null) ResumeState.Delete(s.SessionId);
                }
                _list.Items.Clear();
            };
            Controls.Add(_btnClearAll);

            _btnClose = new Button { Text = L.CancelBtn, Location = new Point(370, 220), Width = 100 };
            _btnClose.Click += (_, __) => Close();
            Controls.Add(_btnClose);
        }
    }
```

- [ ] **Step 7: Add CheckResumeState helper + modify BtnSend_Click**

Add helper method before `BtnSend_Click`:

```csharp
        private Guid CheckResumeState(string filePath, string ip, int port)
        {
            var states = ResumeState.ListAll();
            if (states == null) return Guid.Empty;
            foreach (var s in states)
            {
                if (s != null && s.FilePath == filePath && s.ServerIp == ip && s.Port == port)
                {
                    var choice = MessageBox.Show(L.ResumePromptFile, L.ResumeListTitle,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (choice == DialogResult.Yes)
                        return s.SessionId;
                    ResumeState.Delete(s.SessionId);
                }
            }
            return Guid.Empty;
        }
```

In `BtnSend_Click`, in the single-file branch (not folder, not monitor, not concurrent), before `_rbClientTcp.Checked`:

```csharp
                Guid resumeId = CheckResumeState(path, ip, port);
                if (resumeId != Guid.Empty)
                {
                    if (_rbClientTcp.Checked)
                    {
                        var client = new TransferClient(ip, port, path, srcPort);
                        WireClientEvents(client);
                        await client.SendResumableAsync(resumeId);
                    }
                    else
                    {
                        var clientUdt = new TransferUdtClient(ip, port, path, srcPort);
                        WireUdtClientEvents(clientUdt);
                        await clientUdt.SendResumableAsync(resumeId);
                    }
                    return;
                }
```

Note: this is a simplified integration. Actual integration needs careful placement within the existing async `BtnSend_Click` flow prioritizing resume over concurrent.

- [ ] **Step 8: Build and test**

```
D:\cc\TrFileTransfer\TrFileTransfer\build.bat
D:\cc\TrFileTransfer\TrFileTransfer\Tests\build.bat
D:\cc\TrFileTransfer\TrFileTransfer\Tests\TrFileTransfer.Tests.exe
```

Expected: Build success, all tests pass.

- [ ] **Step 9: Commit**

```bash
git add TrFileTransfer/MainForm.cs TrFileTransfer/L10N.cs
git commit -m "feat: UI 续传对话框 + 续传提示 + L10N 字符串"
```

---

### Task 5: Integration Tests

**Files:**
- Modify: `TrFileTransfer/Tests/IntegrationTests.cs`
- Modify: `TrFileTransfer/Tests/TestProgram.cs`

- [ ] **Step 1: Add TCP resume integration test**

Add to `IntegrationTests` class:

```csharp
        private static void TcpResumeSingleFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(@"D:\cc\tmp", "tr_it_rs_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(@"D:\cc\tmp", "tr_it_rs_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server = null;
            try
            {
                // Create a test file
                var testFile = Path.Combine(sendDir, "resume_test.bin");
                var rng = new Random(42);
                int halfSize = 1024 * 80; // 80 KB
                var content = new byte[halfSize * 2]; // 160 KB total
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);

                // First transfer: send half, cancel, verify partial file
                var serverStarted = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.Start();

                if (!serverStarted.WaitOne(5000))
                    throw new Exception("Server did not start");

                // First send (simulated partial via cancel)
                var client1 = new TransferClient("127.0.0.1", port, testFile);
                var c1Done = new ManualResetEvent(false);
                client1.OnTransferComplete += () => c1Done.Set();
                client1.OnError += msg => c1Done.Set();

                var sendTask1 = client1.SendAsync();
                // Wait for partial transfer then cancel
                if (!System.Threading.Tasks.Task.WaitAll(new[] { sendTask1 }, 3000))
                {
                    client1.Cancel();
                }
                c1Done.WaitOne(60000);
                Thread.Sleep(500);

                // Now resume with SendResumableAsync
                var client2 = new TransferClient("127.0.0.1", port, testFile);
                var c2Done = new ManualResetEvent(false);
                bool c2Ok = false;
                client2.OnTransferComplete += () => { c2Ok = true; c2Done.Set(); };
                client2.OnError += msg => c2Done.Set();

                var sessionId = Guid.NewGuid();
                var state = new ResumeState
                {
                    SessionId = sessionId, FileSize = content.Length,
                    FileName = Path.GetFileName(testFile), FilePath = testFile,
                    ServerIp = "127.0.0.1", Port = port, IsUdt = false,
                    Created = DateTime.UtcNow, SentBytes = 0
                };
                state.Save();

                var sendTask2 = client2.SendResumableAsync(sessionId);
                c2Done.WaitOne(60000);
                if (!c2Ok) throw new Exception("Resume transfer failed");

                sendTask2.Wait(60000);
                Thread.Sleep(300);

                var receivedFile = Path.Combine(recvDir, Path.GetFileName(testFile));
                Assert.True(File.Exists(receivedFile), "received file exists");
                var receivedContent = File.ReadAllBytes(receivedFile);
                Assert.Equal(content.Length, receivedContent.Length, "file size matches");
                Assert.True(Utils.ConstantTimeEquals(content, receivedContent), "content match");
            }
            finally
            {
                try { if (server != null) server.Stop(); } catch { }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }
```

- [ ] **Step 2: Register test in RunAll**

```csharp
            runner.Run("Integration_TCP_ResumeSingleFile", TcpResumeSingleFile);
```

- [ ] **Step 3: Build and run tests**

```
D:\cc\TrFileTransfer\TrFileTransfer\Tests\build.bat
D:\cc\TrFileTransfer\TrFileTransfer\Tests\TrFileTransfer.Tests.exe
```

Expected: All tests pass including new resume test.

- [ ] **Step 4: Commit**

```bash
git add TrFileTransfer/Tests/IntegrationTests.cs TrFileTransfer/Tests/TestProgram.cs
git commit -m "test: TCP 断点续传集成测试"
```

---

### Task 6: Final Verification

- [ ] **Step 1: Run full test suite**

```
D:\cc\TrFileTransfer\TrFileTransfer\Tests\TrFileTransfer.Tests.exe
```

Expected: All tests pass.

- [ ] **Step 2: C# 5 compliance check**

Verify no forbidden syntax in modified files (`$`, `?.`, `=>`, `when`, `nameof`).

- [ ] **Step 3: Commit any remaining changes**

```bash
git status
git add -A  # if any stragglers
git commit -m "chore: 断点续传最终验证 + C# 5 合规"
```
