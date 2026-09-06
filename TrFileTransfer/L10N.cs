namespace TrFileTransfer
{
    /// <summary>Localization strings for English and Chinese. Set IsChinese to switch language.</summary>
    #pragma warning disable 1591
    public static class L
    {
        /// <summary>When true, all property getters return Chinese text; otherwise English.</summary>
        public static bool IsChinese { get; set; }

        // ---- Window title ----
        public static string AppTitle { get { return IsChinese ? "文件传输" : "File Transfer"; } }

        // ---- Server ----
        public static string ServerSettings { get { return IsChinese ? "服务器设置" : "Server Settings"; } }
        public static string BindAddress { get { return IsChinese ? "绑定地址:" : "Bind:"; } }
        public static string Port { get { return IsChinese ? "端口:" : "Port:"; } }
        public static string SaveTo { get { return IsChinese ? "保存到:" : "Save to:"; } }
        public static string Browse { get { return IsChinese ? "浏览..." : "Browse..."; } }
        public static string StartServer { get { return IsChinese ? "启动" : "Start"; } }
        public static string StopServer { get { return IsChinese ? "停止" : "Stop"; } }

        // ---- Client ----
        public static string ClientSettings { get { return IsChinese ? "客户端设置" : "Client Settings"; } }
        public static string ServerIP { get { return IsChinese ? "服务器IP:" : "Server IP:"; } }
        public static string FileLabel { get { return IsChinese ? "文件:" : "File:"; } }
        public static string SendFile { get { return IsChinese ? "发送文件" : "Send File"; } }
        public static string CancelBtn { get { return IsChinese ? "取消" : "Cancel"; } }

        // ---- Progress ----
        public static string ProgressGroup { get { return IsChinese ? "传输进度" : "Progress"; } }
        public static string ServerProgress { get { return IsChinese ? "服务器接收进度" : "Server Receive"; } }
        public static string ClientProgress { get { return IsChinese ? "客户端发送进度" : "Client Send"; } }
        public static string SpeedLabel { get { return IsChinese ? "速度: --" : "Speed: --"; } }
        public static string Ready { get { return IsChinese ? "就绪" : "Ready"; } }
        public static string Listening { get { return IsChinese ? "监听中..." : "Listening..."; } }
        public static string ServerStopped { get { return IsChinese ? "服务器已停止。" : "Server stopped."; } }
        public static string TransferComplete { get { return IsChinese ? "传输完成!" : "Transfer complete!"; } }
        public static string Cancelling { get { return IsChinese ? "正在取消..." : "Cancelling..."; } }
        public static string ConcurrencyLabel { get { return IsChinese ? "并发(1-8):" : "Concur(1-8):"; } }
        public static string SrcPortLabel { get { return IsChinese ? "源端口(0=随机):" : "SrcPort(0=Rnd):"; } }
        public static string ExportLog { get { return IsChinese ? "导出日志" : "Export Log"; } }
        public static string ExportLogTitle { get { return IsChinese ? "导出日志" : "Export Log"; } }
        public static string ExportLogFailed { get { return IsChinese ? "导出失败: " : "Export failed: "; } }

        public static string SpeedPrefix { get { return IsChinese ? "速度: " : "Speed: "; } }
        public static string Transferring(object fileName, string eta)
        {
            return IsChinese
                ? string.Format("传输中... {0}  剩余时间: {1}", fileName, eta)
                : string.Format("Transferring... {0}  ETA: {1}", fileName, eta);
        }

        // ---- Log ----
        public static string LogGroup { get { return IsChinese ? "日志" : "Log"; } }

        // ---- Dialog ----
        public static string DlgError { get { return IsChinese ? "错误" : "Error"; } }
        public static string InvalidPort { get { return IsChinese ? "无效的端口号。" : "Invalid port number."; } }
        public static string DirNotExist { get { return IsChinese ? "保存目录不存在。" : "Save directory does not exist."; } }
        public static string FileNotFound { get { return IsChinese ? "文件未找到。" : "File not found."; } }
        public static string EnterServerIP { get { return IsChinese ? "请输入服务器IP地址。" : "Enter server IP address."; } }
        public static string BrowseDirDesc { get { return IsChinese ? "选择接收文件的保存目录" : "Select directory to save received files"; } }
        public static string BrowseFileTitle { get { return IsChinese ? "选择要发送的文件" : "Select file to send"; } }
        public static string ErrorPrefix { get { return IsChinese ? "错误: " : "Error: "; } }
        public static string NoProtocolSelected { get { return IsChinese ? "请至少选择一个服务器协议（TCP/UDT）。" : "Select at least one server protocol (TCP/UDT)."; } }
        public static string ServerStartFailed { get { return IsChinese ? "服务器启动失败。请检查端口是否被占用。" : "Server start failed. Check if port is in use."; } }

        // ---- Folder mode ----
        public static string FolderMode { get { return IsChinese ? "文件夹模式" : "Folder mode"; } }
        public static string FileMode { get { return IsChinese ? "文件模式" : "File mode"; } }
        public static string FolderLabel { get { return IsChinese ? "文件夹:" : "Folder:"; } }
        public static string BrowseFolderTitle { get { return IsChinese ? "选择要发送的文件夹" : "Select folder to send"; } }
        public static string BrowseFolderDesc { get { return IsChinese ? "选择要发送的文件夹" : "Select folder to send"; } }
        public static string SendFolder { get { return IsChinese ? "发送文件夹" : "Send Folder"; } }
        public static string TransferTypeGroup { get { return IsChinese ? "传输类型" : "Transfer Type"; } }
        public static string BindAll { get { return IsChinese ? "0.0.0.0 (所有接口)" : "0.0.0.0 (All interfaces)"; } }

        // ---- Folder transfer log ----
        public static string S_ReceivingFolder(string name, int count, string sizeStr)
        {
            return IsChinese
                ? string.Format("正在接收文件夹: {0} ({1} 个文件, {2})", name, count, sizeStr)
                : string.Format("Receiving folder: {0} ({1} files, {2})", name, count, sizeStr);
        }
        public static string S_FolderTransferDone(string name, int count, string sizeStr, double secs, string speedStr)
        {
            return IsChinese
                ? string.Format("文件夹传输完成: {0} ({1} 个文件, {2}) 用时 {3:F1}秒 ({4}/s)", name, count, sizeStr, secs, speedStr)
                : string.Format("Folder transfer complete: {0} ({1} files, {2}) in {3:F1}s ({4}/s)", name, count, sizeStr, secs, speedStr);
        }
        public static string C_SendingFolder(string name, int count, string sizeStr)
        {
            return IsChinese
                ? string.Format("正在发送文件夹: {0} ({1} 个文件, {2})", name, count, sizeStr)
                : string.Format("Sending folder: {0} ({1} files, {2})", name, count, sizeStr);
        }
        public static string C_FolderTransferDone(string name, int count, string sizeStr, double secs, string speedStr)
        {
            return IsChinese
                ? string.Format("文件夹传输完成: {0} ({1} 个文件, {2}) 用时 {3:F1}秒 ({4}/s)", name, count, sizeStr, secs, speedStr)
                : string.Format("Folder transfer complete: {0} ({1} files, {2}) in {3:F1}s ({4}/s)", name, count, sizeStr, secs, speedStr);
        }
        public static string DirCreateError(string path)
        {
            return IsChinese
                ? string.Format("无法创建目录: {0}", path)
                : string.Format("Cannot create directory: {0}", path);
        }
        public static string C_ZeroFiles { get { return IsChinese ? "文件夹为空。" : "Folder is empty."; } }

        // ---- Server log messages ----
        public static string S_BindFailed(string addr, string port, string err)
        {
            return IsChinese
                ? string.Format("绑定失败 {0}:{1} — {2}", addr, port, err)
                : string.Format("Bind failed {0}:{1} — {2}", addr, port, err);
        }
        public static string S_Started(string port, string dir)
        {
            return IsChinese
                ? string.Format("服务器已启动，端口 {0}。保存目录: {1}", port, dir)
                : string.Format("Server started on port {0}. Save directory: {1}", port, dir);
        }
        public static string S_Stopped { get { return IsChinese ? "服务器已停止。" : "Server stopped."; } }
        public static string S_ClientConnected(object ep)
        {
            return IsChinese
                ? string.Format("客户端已连接: {0}", ep)
                : string.Format("Client connected: {0}", ep);
        }
        public static string S_AcceptError(string msg)
        {
            return IsChinese
                ? string.Format("接受连接错误: {0}", msg)
                : string.Format("Accept error: {0}", msg);
        }
        public static string S_InvalidHeader(long size, int nameLen)
        {
            return IsChinese
                ? string.Format("无效的头部: 大小={0}, 名称长度={1}", size, nameLen)
                : string.Format("Invalid header: size={0}, nameLen={1}", size, nameLen);
        }
        public static string S_Receiving(string fileName, string sizeStr)
        {
            return IsChinese
                ? string.Format("正在接收: {0} ({1})", fileName, sizeStr)
                : string.Format("Receiving: {0} ({1})", fileName, sizeStr);
        }
        public static string S_HashFailed(string fileName)
        {
            return IsChinese
                ? string.Format("哈希验证失败: {0}", fileName)
                : string.Format("Hash verification FAILED for {0}", fileName);
        }
        public static string S_ChunkOk(string fileName, long offset, string sizeStr, int chunksDone)
        {
            return IsChinese
                ? string.Format("分块 OK: {0} offset={1} size={2} [{3}]", fileName, offset, sizeStr, chunksDone)
                : string.Format("Chunk OK: {0} offset={1} size={2} [{3}]", fileName, offset, sizeStr, chunksDone);
        }
        public static string S_TransferDone(string fileName, string sizeStr, double secs, string speedStr)
        {
            return IsChinese
                ? string.Format("传输完成: {0} ({1}) 用时 {2:F1}秒 ({3}/s)", fileName, sizeStr, secs, speedStr)
                : string.Format("Transfer complete: {0} ({1}) in {2:F1}s ({3}/s)", fileName, sizeStr, secs, speedStr);
        }
        public static string S_ConnectionError(string msg)
        {
            return IsChinese
                ? string.Format("连接错误: {0}", msg)
                : string.Format("Connection error: {0}", msg);
        }
        public static string S_UnexpectedError(string msg)
        {
            return IsChinese
                ? string.Format("意外错误: {0}", msg)
                : string.Format("Unexpected error: {0}", msg);
        }
        public static string S_ConnClosedPrematurely { get { return IsChinese ? "连接过早关闭" : "Connection closed prematurely"; } }
        public static string S_ConnClosedUnexpectedly { get { return IsChinese ? "连接意外关闭" : "Connection closed unexpectedly"; } }
        public static string S_ReceivedFile { get { return IsChinese ? "received_file" : "received_file"; } }

        // ---- Client log messages ----
        public static string C_TransferCancelled { get { return IsChinese ? "传输已取消。" : "Transfer cancelled."; } }
        public static string C_Error(string msg)
        {
            return IsChinese
                ? string.Format("错误: {0}", msg)
                : string.Format("Error: {0}", msg);
        }
        public static string C_Connecting(string ip, int port)
        {
            return IsChinese
                ? string.Format("正在连接 {0}:{1}...", ip, port)
                : string.Format("Connecting to {0}:{1}...", ip, port);
        }
        public static string C_Connected(string ip, int port)
        {
            return IsChinese
                ? string.Format("已连接到 {0}:{1}", ip, port)
                : string.Format("Connected to {0}:{1}", ip, port);
        }
        public static string C_Sending(string fileName, string sizeStr)
        {
            return IsChinese
                ? string.Format("正在发送: {0} ({1})", fileName, sizeStr)
                : string.Format("Sending: {0} ({1})", fileName, sizeStr);
        }
        public static string C_TransferDone(string fileName, string sizeStr, double secs, string speedStr)
        {
            return IsChinese
                ? string.Format("传输完成: {0} ({1}) 用时 {2:F1}秒 ({3}/s)", fileName, sizeStr, secs, speedStr)
                : string.Format("Transfer complete: {0} ({1}) in {2:F1}s ({3}/s)", fileName, sizeStr, secs, speedStr);
        }

        // ---- UDT server log messages ----
        public static string UdtS_Started(string port, string dir)
        {
            return IsChinese
                ? string.Format("UDT服务器已启动，端口 {0}。保存目录: {1}", port, dir)
                : string.Format("UDT server started on port {0}. Save directory: {1}", port, dir);
        }
        public static string UdtS_Stopped { get { return IsChinese ? "UDT服务器已停止。" : "UDT server stopped."; } }

        // ---- UDT client log messages ----
        public static string UdtC_Connecting(string ip, int port)
        {
            return IsChinese
                ? string.Format("UDT 正在连接 {0}:{1}...", ip, port)
                : string.Format("UDT connecting to {0}:{1}...", ip, port);
        }
        // ---- Monitor mode ----
        public static string MonitorMode { get { return IsChinese ? "监控模式" : "Monitor Mode"; } }
        public static string MonitorLabel { get { return IsChinese ? "监控目录:" : "Monitor Dir:"; } }
        public static string StartMonitor { get { return IsChinese ? "开始监控" : "Start Monitor"; } }
        public static string StopMonitor { get { return IsChinese ? "停止监控" : "Stop Monitor"; } }
        public static string MonitorWaiting { get { return IsChinese ? "等待新文件..." : "Waiting for new files..."; } }
        public static string MonitorStopped { get { return IsChinese ? "监控已停止。" : "Monitor stopped."; } }
        public static string MonitorFileWaiting(object fileName, int secs)
        {
            return IsChinese
                ? string.Format("等待文件写入完成: {0} ({1}秒)", fileName, secs)
                : string.Format("Waiting for file: {0} ({1}s)", fileName, secs);
        }
        public static string MonitorFileSent(object fileName)
        {
            return IsChinese
                ? string.Format("已发送: {0}", fileName)
                : string.Format("Sent: {0}", fileName);
        }
        public static string MonitorFileSendFailed(object fileName, string err)
        {
            return IsChinese
                ? string.Format("发送失败: {0} - {1}", fileName, err)
                : string.Format("Send failed: {0} - {1}", fileName, err);
        }
        public static string MonitorFileNotReady(object fileName)
        {
            return IsChinese
                ? string.Format("文件未就绪，移至队列末尾: {0}", fileName)
                : string.Format("File not ready, moved to back of queue: {0}", fileName);
        }
        public static string MonitorDirNotExist { get { return IsChinese ? "监控目录不存在。" : "Monitor directory does not exist."; } }
        public static string DragDropOnlyFirst(object fileName, int total)
        {
            return IsChinese
                ? string.Format("[拖放] 仅加载了第1个文件: {0}，其余 {1} 个已忽略", fileName, total - 1)
                : string.Format("[Drop] Loaded first file: {0}, {1} others ignored", fileName, total - 1);
        }
        public static string DragDropInvalid(object path)
        {
            return IsChinese
                ? string.Format("[拖放] 无法识别的拖放内容: {0}", path)
                : string.Format("[Drop] Unrecognized drop content: {0}", path);
        }
        public static string ResumeListTitle { get { return IsChinese ? "续传任务" : "Resume Tasks"; } }
        public static string ResumePromptFile { get { return IsChinese ? "上次传输未完成，是否续传？" : "Previous transfer incomplete. Resume?"; } }
        public static string ResumeBtn { get { return IsChinese ? "续传" : "Resume"; } }
        public static string ReSendBtn { get { return IsChinese ? "重新发送" : "Re-send"; } }
        public static string ResumeDelete { get { return IsChinese ? "删除" : "Delete"; } }
        public static string ResumeClearAll { get { return IsChinese ? "全部清空" : "Clear All"; } }
        public static string ResumeListEmpty { get { return IsChinese ? "没有未完成的传输任务。" : "No incomplete transfer tasks."; } }
        public static string ResumeQueued { get { return IsChinese ? "已选择续传任务，点击“发送”开始续传。" : "Resume task selected. Click Send to resume."; } }
        public static string VerifyHashLabel { get { return IsChinese ? "完整校验（续传）" : "Full hash verify (resume)"; } }
        public static string SpeedLimitLabel { get { return IsChinese ? "限速(KB/s)" : "Limit(KB/s)"; } }
        public static string QueueBtn { get { return IsChinese ? "发送队列" : "Send Queue"; } }
        public static string QueueTitle { get { return IsChinese ? "发送队列" : "Send Queue"; } }
        public static string QueueAdd { get { return IsChinese ? "添加当前文件" : "Add Current"; } }
        public static string QueueDelete { get { return IsChinese ? "删除" : "Delete"; } }
        public static string QueueClear { get { return IsChinese ? "全部清空" : "Clear All"; } }
        public static string QueueStart { get { return IsChinese ? "开始" : "Start"; } }
        public static string QueueInvalidTask { get { return IsChinese ? "请先填写有效的文件/文件夹、IP 和端口。" : "Fill in a valid file/folder, IP and port first."; } }
        public static string ScanBtn { get { return IsChinese ? "扫描" : "Scan"; } }
        public static string ScanTitle { get { return IsChinese ? "局域网设备" : "LAN Devices"; } }
        public static string ScanRescan { get { return IsChinese ? "重新扫描" : "Rescan"; } }
        public static string ScanUse { get { return IsChinese ? "使用" : "Use"; } }
        public static string ScanEmpty { get { return IsChinese ? "未发现设备（请确认对方已启动服务器且防火墙允许 UDP 广播）。" : "No devices found (make sure the remote server is running and UDP broadcast is allowed)."; } }
        public static string Scanning { get { return IsChinese ? "正在扫描..." : "Scanning..."; } }
        public static string ScanKnownTitle { get { return IsChinese ? "— 已保存设备 —" : "— Saved devices —"; } }
        public static string ScanOnlineTitle { get { return IsChinese ? "— 在线设备 —" : "— Online devices —"; } }
        public static string ScanOffline { get { return IsChinese ? "离线" : "offline"; } }
        public static string TrayShow { get { return IsChinese ? "显示主窗口" : "Show Main Window"; } }
        public static string TrayExit { get { return IsChinese ? "退出" : "Exit"; } }
        public static string TrayMinimized { get { return IsChinese ? "程序已最小化到托盘。" : "Minimized to tray."; } }
        public static string DragDropQueued(object count)
        {
            return IsChinese
                ? string.Format("[拖放] 已将 {0} 个项目加入发送队列。", count)
                : string.Format("[Drop] Enqueued {0} items to the send queue.", count);
        }
        public static string DragDropSkipped(object count)
        {
            return IsChinese
                ? string.Format("[拖放] 已跳过 {0} 个项目：服务器地址或端口未配置。", count)
                : string.Format("[Drop] Skipped {0} item(s): server address or port not configured.", count);
        }
        public static string EtaShort { get { return IsChinese ? "剩余" : "ETA"; } }
        public static string About { get { return IsChinese ? "关于" : "About"; } }
        public static string AboutText(object version)
        {
            return IsChinese
                ? string.Format("TrFileTransfer v{0}\n局域网文件传输工具（TCP / UDT）", version)
                : string.Format("TrFileTransfer v{0}\nLAN file transfer tool (TCP / UDT)", version);
        }
        public static string StartedVersion(object version)
        {
            return IsChinese
                ? string.Format("TrFileTransfer v{0} 已启动。", version)
                : string.Format("TrFileTransfer v{0} started.", version);
        }
        public static string FolderTag { get { return IsChinese ? "[文件夹] " : "[Folder] "; } }
        public static string QueueRetriesLabel { get { return IsChinese ? "失败重试:" : "Retries:"; } }
        public static string RetryWord { get { return IsChinese ? "重试" : "retry"; } }
        public static string ComputingFolderHashes(object count)
        {
            return IsChinese
                ? string.Format("正在计算 {0} 个文件的校验值...", count)
                : string.Format("Computing hashes for {0} files...", count);
        }
        public static string FolderResumeStart(object index, object count, object offset)
        {
            return IsChinese
                ? string.Format("文件夹续传：从第 {0}/{1} 个文件恢复（偏移 {2}）。", index, count, offset)
                : string.Format("Folder resume: starting at file {0}/{1} (offset {2}).", index, count, offset);
        }
        public static string NotifySendDone { get { return IsChinese ? "发送完成" : "Send complete"; } }
        public static string NotifyReceiveDone { get { return IsChinese ? "收到文件" : "File received"; } }
        public static string OpenSaveDir { get { return IsChinese ? "目录" : "Folder"; } }
        public static string RecentFiles { get { return IsChinese ? "最近" : "Recent"; } }
        public static string RecentFilesEmpty { get { return IsChinese ? "还没有收到文件。" : "No files received yet."; } }
        public static string RecentOpen { get { return IsChinese ? "打开位置" : "Open Location"; } }
        public static string MonitorStarted(object path)
        {
            return IsChinese
                ? string.Format("[监控] 开始监控: {0}", path)
                : string.Format("[Monitor] Started: {0}", path);
        }
        // ---- Resume / client log messages ----
        public static string C_Resuming(object fileName, object offset, string sizeStr)
        {
            return IsChinese
                ? string.Format("续传 {0} 从偏移 {1} ({2})", fileName, offset, sizeStr)
                : string.Format("Resuming {0} from offset {1} ({2})", fileName, offset, sizeStr);
        }
        public static string C_AlreadyReceived(object fileName)
        {
            return IsChinese
                ? string.Format("{0} 已被服务器完整接收。", fileName)
                : string.Format("{0} already fully received by server.", fileName);
        }
        public static string C_ResumeNegotiated(object start, object serverHad, object clientHad)
        {
            return IsChinese
                ? string.Format("续传协商: 起点={0} 服务端={1} 客户端={2}", start, serverHad, clientHad)
                : string.Format("Resume negotiated: start={0} server={1} client={2}", start, serverHad, clientHad);
        }
        public static string C_ResumeConnClosed { get { return IsChinese ? "续传连接意外关闭" : "Resume connection closed unexpectedly"; } }
        public static string C_ResumeSourceChanged(object fileName)
        {
            return IsChinese
                ? string.Format("源文件已修改（{0}），放弃旧断点，从头发送。", fileName)
                : string.Format("Source file changed ({0}) — discarding old checkpoint, sending from scratch.", fileName);
        }
        public static string C_ComputingFullHash(object fileName)
        {
            return IsChinese
                ? string.Format("正在计算 {0} 的完整校验值...", fileName)
                : string.Format("Computing full hash of {0}...", fileName);
        }
        public static string C_VerifyFailed(object fileName)
        {
            return IsChinese
                ? string.Format("{0} 完整校验失败，服务器已丢弃文件，请重试。", fileName)
                : string.Format("{0} failed full-file verification; server discarded the file. Retry.", fileName);
        }
        public static string S_FullHashFailed(object fileName)
        {
            return IsChinese
                ? string.Format("完整校验失败，文件已删除: {0}", fileName)
                : string.Format("Full-file hash mismatch, file discarded: {0}", fileName);
        }

        public static string MonitorLogStopped
        {
            get
            {
                return IsChinese
                    ? "[监控] 监控已停止。"
                    : "[Monitor] Monitor stopped.";
            }
        }

        // ---- Auto update ----
        public static string UpdBtn { get { return IsChinese ? "检查更新" : "Check Update"; } }
        public static string UpdTitle { get { return IsChinese ? "检查更新" : "Software Update"; } }
        public static string UpdCurrentVersion(object version)
        {
            return IsChinese
                ? string.Format("当前版本: v{0}", version)
                : string.Format("Current version: v{0}", version);
        }
        public static string UpdUrlLabel { get { return IsChinese ? "更新源 URL:" : "Update source URL:"; } }
        public static string UpdUrlHint { get { return IsChinese ? "GitHub Releases API（默认）或 version/url/sha256 清单地址" : "GitHub Releases API (default) or a version/url/sha256 manifest"; } }
        public static string UpdAutoCheck { get { return IsChinese ? "启动时自动检查更新" : "Check for updates at startup"; } }
        public static string UpdCheckNow { get { return IsChinese ? "立即检查" : "Check Now"; } }
        public static string UpdLatest(object version)
        {
            return IsChinese
                ? string.Format("已是最新版本 (v{0})。", version)
                : string.Format("You are on the latest version (v{0}).", version);
        }
        public static string UpdAvailable(object current, object next)
        {
            return IsChinese
                ? string.Format("发现新版本: v{0} → v{1}", current, next)
                : string.Format("New version available: v{0} → v{1}", current, next);
        }
        public static string UpdNotesLabel { get { return IsChinese ? "更新说明:" : "Release notes:"; } }
        public static string UpdDownloadBtn { get { return IsChinese ? "下载并安装" : "Download & Install"; } }
        public static string UpdDownloading(object percent)
        {
            return IsChinese
                ? string.Format("正在下载... {0}%", percent)
                : string.Format("Downloading... {0}%", percent);
        }
        public static string UpdDownloadDone { get { return IsChinese ? "下载完成，校验通过。" : "Download complete, hash verified."; } }
        public static string UpdRestartPrompt { get { return IsChinese ? "更新已就绪。现在重启应用以完成升级？" : "Update ready. Restart now to apply?"; } }
        public static string UpdRestarting { get { return IsChinese ? "升级已应用，正在重启..." : "Update applied, restarting..."; } }
        public static string UpdCheckFailed(string msg)
        {
            return IsChinese
                ? string.Format("检查更新失败: {0}", msg)
                : string.Format("Update check failed: {0}", msg);
        }
        public static string UpdDownloadFailed(string msg)
        {
            return IsChinese
                ? string.Format("下载失败: {0}", msg)
                : string.Format("Download failed: {0}", msg);
        }
        public static string UpdApplyFailed(string msg)
        {
            return IsChinese
                ? string.Format("应用更新失败（已恢复原版本）: {0}", msg)
                : string.Format("Failed to apply update (previous version restored): {0}", msg);
        }
        public static string UpdNoUrl { get { return IsChinese ? "请先填写更新源 URL。" : "Enter the manifest URL first."; } }
        public static string UpdChecking { get { return IsChinese ? "正在检查更新..." : "Checking for updates..."; } }

        // ---- Pairing (0x05 auth) ----
        public static string PairingLabel { get { return IsChinese ? "配对码" : "Pairing"; } }
        public static string PairingClientLabel { get { return IsChinese ? "配对码:" : "Pairing:"; } }
        public static string S_AuthOk { get { return IsChinese ? "配对码验证通过。" : "Pairing code verified."; } }
        public static string S_AuthFailed { get { return IsChinese ? "配对码验证失败，已拒绝连接。" : "Pairing code verification FAILED — connection rejected."; } }
        public static string S_AuthRequired { get { return IsChinese ? "服务器已启用配对码，客户端未认证，连接被拒绝。" : "Server requires a pairing code; unauthenticated client rejected."; } }
        public static string C_Authing { get { return IsChinese ? "正在验证配对码..." : "Verifying pairing code..."; } }
        public static string C_AuthFailed { get { return IsChinese ? "配对码错误，服务器拒绝连接。" : "Pairing code rejected by the server."; } }

        // ---- Text messages (0x06) ----
        public static string SendTextBtn { get { return IsChinese ? "发文本" : "Text"; } }
        public static string SendTextTitle { get { return IsChinese ? "发送文本" : "Send Text"; } }
        public static string SendTextSend { get { return IsChinese ? "发送" : "Send"; } }
        public static string SendTextEmpty { get { return IsChinese ? "请输入要发送的文本。" : "Enter some text to send."; } }
        public static string SendTextTooLarge { get { return IsChinese ? "文本过长（上限 1 MB）。" : "Text too long (max 1 MB)."; } }
        public static string SendTextDone { get { return IsChinese ? "文本已发送。" : "Text sent."; } }
        public static string S_TextReceived(object preview)
        {
            return IsChinese
                ? string.Format("收到文本: {0}", preview)
                : string.Format("Text received: {0}", preview);
        }
        public static string S_TextRejected { get { return IsChinese ? "文本消息无效（过大或长度不符）。" : "Invalid text message (too large or length mismatch)."; } }
        public static string TextReceivedTitle { get { return IsChinese ? "收到文本" : "Text Received"; } }
        public static string CopyBtn { get { return IsChinese ? "复制" : "Copy"; } }
        public static string Copied { get { return IsChinese ? "已复制到剪贴板。" : "Copied to clipboard."; } }
        public static string NotifyTextTitle { get { return IsChinese ? "收到文本消息" : "Text message received"; } }
        public static string C_SendingText(object preview, string sizeStr)
        {
            return IsChinese
                ? string.Format("正在发送文本: {0} ({1})", preview, sizeStr)
                : string.Format("Sending text: {0} ({1})", preview, sizeStr);
        }

        // ---- Crash handling ----
        public static string CrashPrompt(string msg)
        {
            return IsChinese
                ? string.Format("发生未处理的错误，程序可能不稳定：\n{0}\n\n详细信息已写入日志目录的 crash.log。", msg)
                : string.Format("An unhandled error occurred. The program may be unstable:\n{0}\n\nDetails were written to crash.log in the log folder.", msg);
        }

        // ---- Folder sync mode ----
        public static string SyncModeLabel { get { return IsChinese ? "同步模式" : "Sync Mode"; } }
        public static string C_SyncStart(object folder)
        {
            return IsChinese
                ? string.Format("同步模式：仅传输与服务器差异的部分 ({0})", folder)
                : string.Format("Sync mode: only differences will be sent ({0})", folder);
        }

        // ---- Auto start ----
        public static string TrayAutoStart { get { return IsChinese ? "开机自启" : "Start with Windows"; } }
        public static string AutoStartOn { get { return IsChinese ? "已开启开机自启。" : "Auto start with Windows enabled."; } }
        public static string AutoStartOff { get { return IsChinese ? "已关闭开机自启。" : "Auto start with Windows disabled."; } }

        // ---- Skip this version ----
        public static string UpdSkip { get { return IsChinese ? "跳过此版本" : "Skip This Version"; } }

        // ---- Discovery pairing flag ----
        public static string ScanNeedsPairing { get { return IsChinese ? "[需配对]" : "[Pairing]"; } }
        public static string UsePairingHint { get { return IsChinese ? "该设备已启用配对码，请先在客户端填写配对码再发送。" : "This device requires a pairing code — fill it in on the client before sending."; } }

        // ---- HTTP share ----
        public static string HttpShareBtn { get { return IsChinese ? "共享" : "Share"; } }
        public static string HttpPortLabel { get { return IsChinese ? "HTTP:" : "HTTP:"; } }
        public static string HttpShareUploadBtn { get { return IsChinese ? "上传" : "Upload"; } }
        public static string HttpShareStop { get { return IsChinese ? "停止" : "Stop"; } }
        public static string HttpShareOn(object url)
        {
            return IsChinese
                ? string.Format("HTTP 共享已开启: {0} （浏览器打开即可下载）", url)
                : string.Format("HTTP share on: {0} (open in a browser to download)", url);
        }
        public static string HttpShareOff { get { return IsChinese ? "HTTP 共享已关闭。" : "HTTP share stopped."; } }
        public static string HttpShareDirMissing { get { return IsChinese ? "共享目录不存在。" : "Share directory does not exist."; } }
        public static string HttpShareStartFailed(string msg)
        {
            return IsChinese
                ? string.Format("HTTP 共享启动失败: {0}", msg)
                : string.Format("HTTP share failed to start: {0}", msg);
        }
        public static string HttpShareEmpty { get { return IsChinese ? "（空目录）" : "(empty)"; } }
        public static string HttpShareTokenPrompt { get { return IsChinese ? "此共享需要访问码，请输入对方显示的配对码。" : "This share requires an access code — enter the pairing code shown on the host."; } }
        public static string HttpShareTokenSubmit { get { return IsChinese ? "打开" : "Open"; } }
        public static string HttpShareWrongToken { get { return IsChinese ? "访问码不正确，请重试。" : "Wrong access code, try again."; } }

        // ---- Fan-out (one -> many) ----
        public static string FanOutBtn { get { return IsChinese ? "群发" : "Fan Out"; } }
        public static string FanOutTitle { get { return IsChinese ? "群发到多台设备" : "Fan Out to Devices"; } }
        public static string FanOutSend { get { return IsChinese ? "开始群发" : "Start"; } }
        public static string FanOutNoSelection { get { return IsChinese ? "请先勾选至少一台设备。" : "Check at least one device first."; } }
        public static string FanOutIgnoreSrcPort { get { return IsChinese ? "群发模式忽略源端口设置（并行客户端使用随机端口）。" : "Fan-out ignores the source port setting (random ports are used)."; } }
        public static string FanOutStart(object count)
        {
            return IsChinese
                ? string.Format("正在群发到 {0} 台设备...", count)
                : string.Format("Fanning out to {0} device(s)...", count);
        }
        public static string FanOutDone(object ok, object total)
        {
            return IsChinese
                ? string.Format("群发完成: {0}/{1} 成功。", ok, total)
                : string.Format("Fan out finished: {0}/{1} succeeded.", ok, total);
        }

        // ---- Port busy / firewall ----
        public static string PortBusyOffer(object busy, object alt)
        {
            return IsChinese
                ? string.Format("端口 {0} 已被占用。改用可用端口 {1} 启动？", busy, alt)
                : string.Format("Port {0} is in use. Start on free port {1} instead?", busy, alt);
        }
        public static string PortBusyNoAlt(object busy)
        {
            return IsChinese
                ? string.Format("端口 {0} 已被占用，且未在其后找到可用端口。", busy)
                : string.Format("Port {0} is in use and no free port was found after it.", busy);
        }
        public static string FwHintTitle { get { return IsChinese ? "防火墙提示" : "Firewall Note"; } }
        public static string FwHintText(object cmd)
        {
            return IsChinese
                ? string.Format("若其他设备无法连接本机，请允许 TrFileTransfer 通过 Windows 防火墙：\n" +
                    "首次监听时 Windows 会弹出允许提示，请勾选\"专用网络\"和\"公用网络\"。\n\n" +
                    "也可以用管理员命令直接放行（选中下方命令右键复制）：\n{0}", cmd)
                : string.Format("If other devices cannot connect, allow TrFileTransfer through Windows Firewall:\n" +
                    "Windows shows an allow prompt on the first listen — check BOTH \"Private\" and \"Public\".\n\n" +
                    "Or run this command as administrator (select & copy below):\n{0}", cmd);
        }

        public static string HelpBtn { get { return IsChinese ? "使用说明" : "Guide"; } }
        public static string FanOutAddBtn { get { return IsChinese ? "添加" : "Add"; } }
        public static string FanOutManualTag { get { return "[手动] " ; } }
        public static string QueueBatchAdd { get { return IsChinese ? "批量添加" : "Add files"; } }
        public static string ThemeToggleTip { get { return IsChinese ? "切换深色/浅色主题" : "Toggle dark/light theme"; } }
        public static string ThemeBtn(bool dark) { return dark ? (IsChinese ? "浅色" : "Light") : (IsChinese ? "深色" : "Dark"); }
        public static string FieldIpInvalid { get { return IsChinese ? "IP 格式不正确（需要 IPv4 地址）" : "Invalid IP format (IPv4 expected)"; } }
        public static string FieldPathInvalid { get { return IsChinese ? "路径包含非法字符" : "Path contains invalid characters"; } }
        public static string FieldDigitsOnly { get { return IsChinese ? "只能输入数字" : "Digits only"; } }
        public static string FieldUrlInvalid { get { return IsChinese ? "地址必须以 http:// 或 https:// 开头" : "Address must start with http:// or https://"; } }

        public static string HelpText()
        {
            return IsChinese
                ? "【接收（服务器）】\n" +
                  "1. 选择\"保存到\"目录，勾选 TCP 和/或 UDT，点击\"启动\"。\n" +
                  "2. 勾选\"配对码\"后，发送方必须输入相同的 6 位码才能连接。\n" +
                  "3. \"HTTP 共享\"把保存目录变成网页：手机浏览器可直接浏览、下载与上传。\n\n" +
                  "【发送（客户端）】\n" +
                  "1. 填写对方 IP 与端口；同一台电脑自测填 127.0.0.1。\n" +
                  "2. 选好文件/文件夹点\"发送文件\"，或直接把文件拖进窗口。\n" +
                  "3. 大文件可把\"并发\"调到 4-8；\"文件夹模式\"支持整目录发送，\n" +
                  "   勾选\"同步模式\"则只传输与对方差异的部分。\n\n" +
                  "【实用功能】\n" +
                  "· 扫描 —— 自动发现同一局域网内运行本程序的设备\n" +
                  "· 群发 —— 同一文件同时发给多台设备\n" +
                  "· 发送队列 —— 批量任务串行发送，失败自动重试\n" +
                  "· 续传 —— 中断后从断点继续；\"完整校验\"逐字节核对\n" +
                  "· 监控模式 —— 监视目录，出现新文件自动发送\n" +
                  "· 发文本 —— 向对方发送一条即时消息\n\n" +
                  "提示：所有设置自动保存；关闭窗口仅最小化到托盘，\n托盘右键菜单可开机自启、检查更新或真正退出。"
                : "[Receive (server)]\n" +
                  "1. Pick the \"Save to\" folder, check TCP and/or UDT, press \"Start\".\n" +
                  "2. With \"Pairing\" enabled the sender must enter the same 6-digit code.\n" +
                  "3. \"HTTP Share\" turns the folder into a web page — browse, download and upload from a phone.\n\n" +
                  "[Send (client)]\n" +
                  "1. Enter the peer IP and port; use 127.0.0.1 to test on one machine.\n" +
                  "2. Pick a file/folder and press \"Send\", or just drop files onto the window.\n" +
                  "3. Raise \"Concurrency\" to 4-8 for large files; \"Folder mode\" sends whole folders and\n" +
                  "   \"Sync mode\" transfers only the differences.\n\n" +
                  "[Tools]\n" +
                  "- Scan: discover devices running this app on the LAN\n" +
                  "- Fan-out: send one file to many devices at once\n" +
                  "- Queue: batch tasks sent serially with automatic retries\n" +
                  "- Resume: continue broken transfers; \"Verify hash\" double-checks bytes\n" +
                  "- Monitor: watch a folder and auto-send new files\n" +
                  "- Send text: quick instant message to the peer\n\n" +
                  "Tips: settings save automatically; closing the window hides to tray —\nuse the tray menu for auto-start, updates or a real exit.";
        }

    }
}
