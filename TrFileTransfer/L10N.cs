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
        public static string StartServer { get { return IsChinese ? "启动服务器" : "Start Server"; } }
        public static string StopServer { get { return IsChinese ? "停止服务器" : "Stop Server"; } }

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
        public static string ConcurrencyLabel { get { return IsChinese ? "并发(1-16):" : "Concur(1-16):"; } }
        public static string SrcPortLabel { get { return IsChinese ? "源端口(0=随机):" : "SrcPort(0=Rnd):"; } }
        public static string AboutBtn { get { return IsChinese ? "关于" : "About"; } }
        public static string AboutTitle { get { return IsChinese ? "关于" : "About"; } }
        public static string AboutText { get { return IsChinese ? "TrFileTransfer v1.0\r\n局域网文件传输工具\r\nSHA-256 完整性校验" : "TrFileTransfer v1.0\r\nLAN file transfer tool\r\nSHA-256 integrity verification"; } }
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
        public static string S_ZeroFiles { get { return IsChinese ? "文件夹为空。" : "Folder is empty."; } }

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
        public static string MonitorStarted(object path)
        {
            return IsChinese
                ? string.Format("[监控] 开始监控: {0}", path)
                : string.Format("[Monitor] Started: {0}", path);
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

    }
}
