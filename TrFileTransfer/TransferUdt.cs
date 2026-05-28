using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    #region UDT Native Interop

    internal static class UdtNative
    {
        public const int AF_INET = 2;
        public const int SOCK_STREAM = 1;
        public const int ERROR = -1;
        public static readonly int SockAddrSize = Marshal.SizeOf(typeof(sockaddr_in));

        // getsockopt/setsockopt option names (must match udt.h UDTOpt enum)
        public const int UDT_MSS = 0;
        public const int UDT_SNDSYN = 1;
        public const int UDT_RCVSYN = 2;
        public const int UDT_FC = 4;
        public const int UDT_SNDBUF = 5;
        public const int UDT_RCVBUF = 6;
        public const int UDT_LINGER = 7;
        public const int UDT_RENDEZVOUS = 12;
        public const int UDT_SNDTIMEO = 13;
        public const int UDT_RCVTIMEO = 14;

        // Reference-counted startup/cleanup so concurrent server+client don't tear each other down
        private static int _refCount;
        private static readonly object _refLock = new object();

        [DllImport("udt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int udt_startup();

        [DllImport("udt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int udt_cleanup();

        /// <summary>Call once before using any UDT socket. Increments process-wide ref count.</summary>
        /// <returns>true if UDT library initialized (or already running).</returns>
        public static bool UdtStartup()
        {
            lock (_refLock)
            {
                if (_refCount == 0)
                {
                    if (udt_startup() == ERROR)
                        return false;
                }
                _refCount++;
                return true;
            }
        }

        /// <summary>Call once when done with UDT. Decrements ref count; cleans up only when zero.</summary>
        public static void UdtCleanup()
        {
            lock (_refLock)
            {
                if (_refCount > 0)
                {
                    _refCount--;
                    if (_refCount == 0)
                        udt_cleanup();
                }
            }
        }

        [DllImport("udt.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int udt_socket(int af, int type, int protocol);

        [DllImport("udt.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int udt_bind(int u, ref sockaddr_in name, int namelen);

        [DllImport("udt.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int udt_listen(int u, int backlog);

        [DllImport("udt.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int udt_accept(int u, ref sockaddr_in addr, ref int addrlen);

        [DllImport("udt.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int udt_connect(int u, ref sockaddr_in name, int namelen);

        [DllImport("udt.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int udt_close(int u);

        [DllImport("udt.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int udt_send(int u, byte[] buf, int len, int flags);

        [DllImport("udt.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int udt_recv(int u, byte[] buf, int len, int flags);

        [DllImport("udt.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr udt_getlasterror_desc();

        [DllImport("udt.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int udt_setsockopt(int u, int level, int optname, ref int optval, int optlen);

        [DllImport("udt.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int udt_getsockopt(int u, int level, int optname, ref int optval, ref int optlen);

        public static string GetErrorDesc()
        {
            IntPtr ptr = udt_getlasterror_desc();
            return ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) : "Unknown error";
        }

        public static bool SetTimeout(int u, int recvMs, int sendMs)
        {
            bool ok = true;
            if (recvMs > 0)
                ok &= (udt_setsockopt(u, 0, UDT_RCVTIMEO, ref recvMs, 4) != ERROR);
            if (sendMs > 0)
                ok &= (udt_setsockopt(u, 0, UDT_SNDTIMEO, ref sendMs, 4) != ERROR);
            return ok;
        }

        public static sockaddr_in BuildSockaddr(string ip, int port)
        {
            var addr = new sockaddr_in();
            addr.sin_family = AF_INET;
            addr.sin_port = (ushort)IPAddress.HostToNetworkOrder((short)port);
            byte[] ipBytes = IPAddress.Parse(ip).GetAddressBytes();
            // sin_addr is in network byte order. On little-endian, the uint bytes
            // must be stored in reverse so the network sees ipBytes[0] first.
            addr.sin_addr = (uint)(ipBytes[3] << 24 | ipBytes[2] << 16 | ipBytes[1] << 8 | ipBytes[0]);
            return addr;
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    internal struct sockaddr_in
    {
        public short sin_family;
        public ushort sin_port;
        public uint sin_addr;
    }

    #endregion

    #region UDT DLL Extraction

    internal static class UdtDll
    {
        private static bool _extracted;
        private static readonly object _lock = new object();

        public static void EnsureExtracted()
        {
            if (_extracted) return;
            lock (_lock)
            {
                if (_extracted) return;
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                // Extract UDT DLL and its runtime dependency (MCF thread library)
                ExtractResource("TrFileTransfer.udt.dll", Path.Combine(exeDir, "udt.dll"));
                ExtractResource("TrFileTransfer.libmcfgthread-2.dll", Path.Combine(exeDir, "libmcfgthread-2.dll"));
                _extracted = true;
            }
        }

        private static void ExtractResource(string resourceName, string primaryPath)
        {
            if (File.Exists(primaryPath)) return;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                // Resource not embedded (e.g., DLL placed manually in exe directory)
                if (stream == null) return;
                // Try primary path first; fall back to %TEMP% if read-only
                string targetPath = primaryPath;
                bool tryPrimary = true;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        if (tryPrimary)
                        {
                            using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                                stream.CopyTo(fs);
                        }
                        else
                        {
                            targetPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(primaryPath));
                            using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                            {
                                stream.Seek(0, SeekOrigin.Begin);
                                stream.CopyTo(fs);
                            }
                            SetDllDirectory(Path.GetTempPath());
                        }
                        return;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        tryPrimary = false;
                    }
                    catch (IOException)
                    {
                        System.Threading.Thread.Sleep(200);
                    }
                }
                throw new IOException("Failed to extract " + Path.GetFileName(primaryPath) + " after 3 attempts");
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
    }

    #endregion

    #region TransferUdtServer

    /// <summary>UDT STREAM file/folder receiver with SHA256 integrity verification.</summary>
    public class TransferUdtServer
    {
        private volatile int _socket;
        private CancellationTokenSource _cts;
        private readonly string _bindAddress;
        private readonly int _port;
        private readonly string _saveDirectory;
        private volatile bool _isRunning;
        private bool _startupOk;
        private int _activeClients;
        private readonly System.Collections.Generic.List<int> _clientSockets
            = new System.Collections.Generic.List<int>();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ChunkTracker> _chunkTrackers
            = new System.Collections.Concurrent.ConcurrentDictionary<string, ChunkTracker>();

        /// <summary>Fired for every log message.</summary>
        public event Action<string> OnLog;
        /// <summary>Fired periodically during transfer with progress info.</summary>
        public event Action<TransferProgress> OnProgress;
        /// <summary>Fired when a non-fatal error occurs.</summary>
        public event Action<string> OnError;
        /// <summary>Fired when a single transfer completes. Server keeps listening.</summary>
        public event Action OnTransferComplete;
        /// <summary>Fired when the server starts listening.</summary>
        public event Action OnStarted;
        /// <summary>Fired when the server stops.</summary>
        public event Action OnStopped;
        /// <summary>Fired when a new client connects (with endpoint for per-client tracking).</summary>
        public event Action<IPEndPoint> OnClientConnected;
        /// <summary>Fired periodically during a client's transfer with endpoint.</summary>
        public event Action<IPEndPoint, TransferProgress> OnClientProgress;
        /// <summary>Fired when a single client's transfer completes.</summary>
        public event Action<IPEndPoint> OnClientTransferComplete;

        /// <summary>Whether the server is currently listening.</summary>
        public bool IsRunning { get { return _isRunning; } }

        /// <summary>Creates a UDT server that listens for incoming file transfers.</summary>
        /// <param name="bindAddress">IPv4 address to bind to, or "0.0.0.0" for all interfaces.</param>
        /// <param name="port">Port to listen on.</param>
        /// <param name="saveDirectory">Directory where received files are saved.</param>
        public TransferUdtServer(string bindAddress, int port, string saveDirectory)
        {
            _bindAddress = bindAddress;
            _port = port;
            _saveDirectory = saveDirectory;
        }

        /// <summary>Starts listening for incoming connections. Fires OnStarted on success.</summary>
        public void Start()
        {
            _cts = new CancellationTokenSource();
            UdtDll.EnsureExtracted();
            if (!UdtNative.UdtStartup())
            {
                Log(L.S_BindFailed(_bindAddress, _port.ToString(), "UDT library init failed"));
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
                return;
            }
            _startupOk = true;

            _socket = UdtNative.udt_socket(UdtNative.AF_INET, UdtNative.SOCK_STREAM, 0);
            if (_socket < 0)
            {
                string err = "udt_socket failed";
                Log(L.S_BindFailed(_bindAddress, _port.ToString(), err));
                Uninit();
                var errHandler = OnError;
                if (errHandler != null) errHandler(err);
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
                return;
            }

            var addr = UdtNative.BuildSockaddr(_bindAddress, _port);
            if (UdtNative.udt_bind(_socket, ref addr, UdtNative.SockAddrSize) == UdtNative.ERROR)
            {
                string err = UdtNative.GetErrorDesc();
                Log(L.S_BindFailed(_bindAddress, _port.ToString(), err));
                UdtNative.udt_close(_socket);
                _socket = -1;
                Uninit();
                var errHandler = OnError;
                if (errHandler != null) errHandler(err);
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
                return;
            }

            if (UdtNative.udt_listen(_socket, 10) == UdtNative.ERROR)
            {
                string err = UdtNative.GetErrorDesc();
                Log(L.S_BindFailed(_bindAddress, _port.ToString(), err));
                UdtNative.udt_close(_socket);
                _socket = -1;
                Uninit();
                var errHandler = OnError;
                if (errHandler != null) errHandler(err);
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
                return;
            }

            _isRunning = true;

            var handler = OnStarted;
            if (handler != null) handler();

            Log(L.UdtS_Started(_port.ToString(), _saveDirectory));

            Task.Factory.StartNew(() => AcceptLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>Stops the server and closes all sockets.</summary>
        public void Stop()
        {
            _isRunning = false;
            var cts = _cts;
            if (cts != null) cts.Cancel();
            if (_socket >= 0)
            {
                try { UdtNative.udt_close(_socket); } catch { }
                _socket = -1;
            }
            // Clean up incomplete chunk trackers
            foreach (var kv in _chunkTrackers)
            {
                ChunkTracker removed;
                _chunkTrackers.TryRemove(kv.Key, out removed);
                try { kv.Value.Dispose(); } catch { }
            }
            // Close all active client sockets so HandleClient tasks unblock immediately
            lock (_clientSockets)
            {
                foreach (var cs in _clientSockets)
                    try { UdtNative.udt_close(cs); } catch { }
                _clientSockets.Clear();
            }
            Uninit();
            var handler = OnStopped;
            if (handler != null) handler();
            Log(L.UdtS_Stopped);
        }

        private void Uninit()
        {
            if (_startupOk)
            {
                _startupOk = false;
                UdtNative.UdtCleanup();
            }
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                int clientSocket = -1;
                try
                {
                    var addr = new sockaddr_in();
                    int addrLen = UdtNative.SockAddrSize;
                    clientSocket = await Task.Run(() =>
                        UdtNative.udt_accept(_socket, ref addr, ref addrLen), ct);
                    if (clientSocket < 0) break;

                    UdtNative.SetTimeout(clientSocket, 30000, 30000);
                    int bufSize = 8 * 1024 * 1024; // 8 MB
                    UdtNative.udt_setsockopt(clientSocket, 0, UdtNative.UDT_SNDBUF, ref bufSize, 4);
                    UdtNative.udt_setsockopt(clientSocket, 0, UdtNative.UDT_RCVBUF, ref bufSize, 4);
                    lock (_clientSockets) { _clientSockets.Add(clientSocket); }
                    // sin_addr/sin_port are uint/ushort in network byte order.
                    // Must cast to unsigned before NetworkToHostOrder to avoid sign extension.
                    var clientEp = new IPEndPoint(
                        new IPAddress((long)(uint)IPAddress.NetworkToHostOrder((int)addr.sin_addr)),
                        (int)(ushort)IPAddress.NetworkToHostOrder((short)addr.sin_port));
                    Log(L.S_ClientConnected(clientEp));
                    var _ = HandleClient(clientSocket, ct, clientEp);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Log(L.S_AcceptError(ex.Message));
                    if (clientSocket >= 0)
                        try { UdtNative.udt_close(clientSocket); } catch { }
                }
            }
        }

        private async Task HandleClient(int clientSocket, CancellationToken ct, IPEndPoint clientEp)
        {
            System.Threading.Interlocked.Increment(ref _activeClients);
            var connectedHandler = OnClientConnected;
            if (connectedHandler != null) connectedHandler(clientEp);

            Action<TransferProgress> clientProgress = p =>
            {
                var ch = OnClientProgress; if (ch != null) ch(clientEp, p);
            };
            OnProgress += clientProgress;
            bool completed = false;

            try
            {
                var typeBuf = new byte[1];
                if (await UdtIo.UdtReadExactAsync(clientSocket, typeBuf, 0, 1, ct) == 0) return;
                byte transferType = typeBuf[0];

                if (transferType == 0x01)
                    completed = await HandleFolderTransfer(clientSocket, ct);
                else if (transferType == 0x02)
                {
                    bool fileComplete = await HandleChunkedFile(clientSocket, ct);
                    // Application-level ACK for this chunk
                    var ack2 = new byte[1] { 0x01 };
                    await Task.Run(() => UdtNative.udt_send(clientSocket, ack2, 1, 0), ct);
                    // Chunk connection done — always clean up progress card for this endpoint.
                    // OnTransferComplete fires separately when file is fully assembled.
                    var ccHandler = OnClientTransferComplete;
                    if (ccHandler != null) ccHandler(clientEp);
                    completed = false; // don't fire again below
                }
                else
                    completed = await HandleFileTransfer(clientSocket, ct);

                if (completed)
                {
                    var ccHandler = OnClientTransferComplete;
                    if (ccHandler != null) ccHandler(clientEp);
                }

                // Application-level ACK: tell client we received data successfully
                var ack = new byte[1] { 0x01 };
                await Task.Run(() => UdtNative.udt_send(clientSocket, ack, 1, 0), ct);
            }
            catch (OperationCanceledException) { }
            catch (IOException ex)
            {
                Log(L.S_ConnectionError(ex.Message));
                var handler = OnError;
                if (handler != null) handler(ex.Message);
            }
            catch (Exception ex)
            {
                Log(L.S_UnexpectedError(ex.Message));
                var handler = OnError;
                if (handler != null) handler(ex.Message);
            }
            finally
            {
                OnProgress -= clientProgress;
                lock (_clientSockets) { _clientSockets.Remove(clientSocket); }
                try { UdtNative.udt_close(clientSocket); } catch { }
                System.Threading.Interlocked.Decrement(ref _activeClients);
            }
        }

        private async Task<bool> HandleFileTransfer(int clientSocket, CancellationToken ct)
        {
            var headerBuf = new byte[12];
            if (await UdtIo.UdtReadExactAsync(clientSocket, headerBuf, 0, 12, ct) == 0) return false;

            long fileSize = BitConverter.ToInt64(headerBuf, 0);
            int nameLen = BitConverter.ToInt32(headerBuf, 8);

            if (fileSize < 0 || nameLen <= 0 || nameLen > 4096)
            {
                Log(L.S_InvalidHeader(fileSize, nameLen));
                return false;
            }

            var nameBuf = new byte[nameLen];
            if (await UdtIo.UdtReadExactAsync(clientSocket, nameBuf, 0, nameLen, ct) == 0) return false;
            string fileName = System.Text.Encoding.UTF8.GetString(nameBuf);

            fileName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = L.S_ReceivedFile;

            string savePath = Utils.GetUniqueSavePath(_saveDirectory, fileName);

            Log(L.S_Receiving(fileName, Utils.FormatSize(fileSize)));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool hashOk = await ReceiveFilePayload(clientSocket, savePath, fileSize, fileName, ct);
            sw.Stop();

            if (hashOk)
            {
                Log(L.S_TransferDone(fileName, Utils.FormatSize(fileSize),
                    sw.Elapsed.TotalSeconds,
                    Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
                return true;
            }
            return false;
        }

        private async Task<bool> HandleFolderTransfer(int clientSocket, CancellationToken ct)
        {
            var folderHeaderBuf = new byte[2];
            if (await UdtIo.UdtReadExactAsync(clientSocket, folderHeaderBuf, 0, 2, ct) == 0) return false;
            int folderNameLen = BitConverter.ToInt16(folderHeaderBuf, 0);
            if (folderNameLen <= 0 || folderNameLen > 4096) return false;

            var folderNameBuf = new byte[folderNameLen];
            if (await UdtIo.UdtReadExactAsync(clientSocket, folderNameBuf, 0, folderNameLen, ct) == 0) return false;
            string folderName = System.Text.Encoding.UTF8.GetString(folderNameBuf);
            folderName = Path.GetFileName(folderName);
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = "received_folder";

            var fileCountBuf = new byte[4];
            if (await UdtIo.UdtReadExactAsync(clientSocket, fileCountBuf, 0, 4, ct) == 0) return false;
            int fileCount = BitConverter.ToInt32(fileCountBuf, 0);
            if (fileCount <= 0) return false;

            string folderSaveDir = Utils.GetUniqueSavePath(_saveDirectory, folderName);
            Directory.CreateDirectory(folderSaveDir);

            Log(L.S_ReceivingFolder(folderName, fileCount, "..."));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long totalSize = 0;
            int filesReceived = 0;

            var fileHeaderBuf = new byte[10];
            for (int i = 0; i < fileCount && !ct.IsCancellationRequested; i++)
            {
                if (await UdtIo.UdtReadExactAsync(clientSocket, fileHeaderBuf, 0, 10, ct) == 0) return false;
                long fileSize = BitConverter.ToInt64(fileHeaderBuf, 0);
                int pathLen = BitConverter.ToInt16(fileHeaderBuf, 8);
                if (fileSize < 0 || pathLen <= 0 || pathLen > 4096) return false;

                var pathBuf = new byte[pathLen];
                if (await UdtIo.UdtReadExactAsync(clientSocket, pathBuf, 0, pathLen, ct) == 0) return false;
                string relativePath = System.Text.Encoding.UTF8.GetString(pathBuf);
                relativePath = Utils.SanitizeRelativePath(relativePath);

                string savePath = Path.Combine(folderSaveDir, relativePath);
                string dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                bool hashOk = await ReceiveFilePayload(clientSocket, savePath, fileSize, relativePath, ct);
                if (!hashOk) return false;

                totalSize += fileSize;
                filesReceived++;

                var progressHandler = OnProgress;
                if (progressHandler != null)
                    progressHandler(new TransferProgress
                    {
                        BytesTransferred = filesReceived,
                        TotalBytes = fileCount,
                        SpeedBytesPerSecond = (totalSize > 0 ? totalSize : 0) / sw.Elapsed.TotalSeconds,
                        Elapsed = sw.Elapsed,
                        FileName = folderName
                    });
            }

            sw.Stop();
            Log(L.S_FolderTransferDone(folderName, fileCount, Utils.FormatSize(totalSize),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(totalSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

            var completeHandler = OnTransferComplete;
            if (completeHandler != null) completeHandler();
            return true;
        }

        private async Task<bool> HandleChunkedFile(int clientSocket, CancellationToken ct)
        {
            var chunkHeader = new byte[28];
            if (await UdtIo.UdtReadExactAsync(clientSocket, chunkHeader, 0, 28, ct) == 0) return false;
            long totalSize = BitConverter.ToInt64(chunkHeader, 0);
            long chunkOffset = BitConverter.ToInt64(chunkHeader, 8);
            long chunkSize = BitConverter.ToInt64(chunkHeader, 16);
            int nameLen = BitConverter.ToInt32(chunkHeader, 24);
            if (nameLen <= 0 || nameLen > 4096 || totalSize <= 0 || chunkOffset < 0 || chunkSize <= 0) return false;

            var nameBuf = new byte[nameLen];
            if (await UdtIo.UdtReadExactAsync(clientSocket, nameBuf, 0, nameLen, ct) == 0) return false;
            string fileName = System.Text.Encoding.UTF8.GetString(nameBuf);
            fileName = Path.GetFileName(fileName);

            // Stream chunk data through a fixed buffer — no giant array allocation
            const int BufSize = 4194304; // 4 MB
            var buf = new byte[BufSize];
            var tracker = ChunkTracker.GetOrCreate(_chunkTrackers, fileName, totalSize, _saveDirectory);

            bool isComplete = false;
            using (var sha256 = SHA256.Create())
            {
                long remaining = chunkSize;
                long writeOffset = chunkOffset;
                while (remaining > 0 && !ct.IsCancellationRequested)
                {
                    int toRead = (int)Math.Min(remaining, (long)BufSize);
                    if (await UdtIo.UdtReadExactAsync(clientSocket, buf, 0, toRead, ct) == 0) return false;
                    sha256.TransformBlock(buf, 0, toRead, null, 0);
                    isComplete = tracker.WriteChunk(writeOffset, buf, toRead);
                    writeOffset += toRead;
                    remaining -= toRead;
                }
                sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);

                var receivedHash = new byte[32];
                if (await UdtIo.UdtReadExactAsync(clientSocket, receivedHash, 0, 32, ct) == 0) return false;
                if (!Utils.ConstantTimeEquals(receivedHash, sha256.Hash))
                {
                    Log(L.S_HashFailed(fileName));
                    return false;
                }
            }
            Log("Chunk: " + fileName + " offset=" + chunkOffset + " size=" + chunkSize);

            // Report aggregate progress across all chunks
            var progressHandler = OnProgress;
            if (progressHandler != null)
            {
                long saved = tracker.BytesReceived;
                progressHandler(new TransferProgress
                {
                    BytesTransferred = saved,
                    TotalBytes = totalSize,
                    SpeedBytesPerSecond = 0,
                    Elapsed = TimeSpan.Zero,
                    FileName = fileName
                });
            }

            if (isComplete)
            {
                ChunkTracker removed;
                _chunkTrackers.TryRemove(fileName, out removed);
                if (tracker != null) tracker.Dispose();
                Log(L.S_TransferDone(fileName, Utils.FormatSize(totalSize), 0, ""));
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
                return true;
            }
            return false;
        }

        private async Task<bool> ReceiveFilePayload(int clientSocket, string savePath, long fileSize,
            string displayName, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytesRead = 0;
            const int BufferSize = 4194304; // 4 MB
            var bufA = new byte[BufferSize];
            var bufB = new byte[BufferSize];
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();

            using (var sha256 = SHA256.Create())
            using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write,
                FileShare.None, 65536, FileOptions.SequentialScan))
            {
                if (fileSize == 0)
                {
                    // Zero-byte file: no data to read, skip directly to hash verification
                    sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                }
                else
                {
                long remaining = fileSize;
                int toRead = (int)Math.Min(remaining, (long)bufA.Length);
                int read = await UdtIo.UdtReadAsync(clientSocket, bufA, 0, toRead, ct);
                if (read <= 0)
                    throw new IOException(L.S_ConnClosedPrematurely);

                remaining -= read;
                var cur = bufA;
                var nxt = bufB;

                while (remaining > 0 && !ct.IsCancellationRequested)
                {
                    int nextToRead = (int)Math.Min(remaining, (long)nxt.Length);
                    var nextReadTask = UdtIo.UdtReadAsync(clientSocket, nxt, 0, nextToRead, ct);

                    sha256.TransformBlock(cur, 0, read, null, 0);
                    await fileStream.WriteAsync(cur, 0, read, ct);
                    bytesRead += read;

                    read = await nextReadTask;
                    if (read <= 0)
                        throw new IOException(L.S_ConnClosedPrematurely);
                    remaining -= read;

                    var tmp = cur; cur = nxt; nxt = tmp;

                    if (progressTimer.ElapsedMilliseconds >= 100 || remaining == 0)
                    {
                        progressTimer.Restart();
                        var progressHandler = OnProgress;
                        if (progressHandler != null)
                            progressHandler(new TransferProgress
                            {
                                BytesTransferred = bytesRead,
                                TotalBytes = fileSize,
                                SpeedBytesPerSecond = bytesRead / sw.Elapsed.TotalSeconds,
                                Elapsed = sw.Elapsed,
                                FileName = displayName
                            });
                    }
                }

                sha256.TransformBlock(cur, 0, read, null, 0);
                await fileStream.WriteAsync(cur, 0, read, ct);
                bytesRead += read;

                if (progressTimer.ElapsedMilliseconds >= 100 || bytesRead == fileSize)
                {
                    progressTimer.Restart();
                    var progressHandler = OnProgress;
                    if (progressHandler != null)
                        progressHandler(new TransferProgress
                        {
                            BytesTransferred = bytesRead,
                            TotalBytes = fileSize,
                            SpeedBytesPerSecond = bytesRead / sw.Elapsed.TotalSeconds,
                            Elapsed = sw.Elapsed,
                            FileName = displayName
                        });
                }

                sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                }
                await fileStream.FlushAsync(ct);

                var receivedHash = new byte[32];
                if (await UdtIo.UdtReadExactAsync(clientSocket, receivedHash, 0, 32, ct) == 0)
                    return false;
                var computedHash = sha256.Hash;

                if (!Utils.ConstantTimeEquals(receivedHash, computedHash))
                {
                    Log(L.S_HashFailed(displayName));
                    var errHandler = OnError;
                    if (errHandler != null) errHandler(L.S_HashFailed(displayName));
                    return false;
                }
            }
            return true;
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }

    #endregion

    #region TransferUdtClient

    /// <summary>UDT STREAM file/folder sender with SHA256 integrity verification.</summary>
    public class TransferUdtClient
    {
        private int _socket;
        private CancellationTokenSource _cts;
        private readonly string _serverIp;
        private readonly int _port;
        private readonly string _filePath;
        private readonly int _bufferSize;
        private readonly int _localPort;
        private volatile bool _isRunning;

        /// <summary>Fired for every log message.</summary>
        public event Action<string> OnLog;
        /// <summary>Fired periodically during transfer with progress info.</summary>
        public event Action<TransferProgress> OnProgress;
        /// <summary>Fired when a non-fatal error occurs.</summary>
        public event Action<string> OnError;
        /// <summary>Fired when the transfer completes successfully.</summary>
        public event Action OnTransferComplete;
        /// <summary>Fired when the transfer starts.</summary>
        public event Action OnStarted;
        /// <summary>Fired when the transfer stops (completed, cancelled, or error).</summary>
        public event Action OnStopped;

        /// <summary>Whether a transfer is currently in progress.</summary>
        public bool IsRunning { get { return _isRunning; } }

        /// <summary>Creates a UDT client for sending files or folders.</summary>
        /// <param name="serverIp">Target server IPv4 address.</param>
        /// <param name="port">Target server port.</param>
        /// <param name="filePath">Path to the file or folder to send.</param>
        /// <param name="bufferSize">I/O buffer size in bytes (default 1 MB).</param>
        public TransferUdtClient(string serverIp, int port, string filePath, int bufferSize = 4194304)
        {
            _serverIp = serverIp;
            _port = port;
            _filePath = filePath;
            _bufferSize = bufferSize;
        }

        /// <summary>Creates a UDT client bound to a specific local port for concurrent transfers.</summary>
        public TransferUdtClient(string serverIp, int port, string filePath, int localPort, int bufferSize = 4194304)
        {
            _serverIp = serverIp;
            _port = port;
            _filePath = filePath;
            _bufferSize = bufferSize;
            _localPort = localPort;
        }

        /// <summary>Sends the file specified in the constructor over UDT.</summary>
        public async Task SendAsync()
        {
            await RunUdtTransfer(SendFileInternal);
        }

        /// <summary>Sends a folder recursively over UDT.</summary>
        /// <param name="folderPath">Path to the folder to send.</param>
        public async Task SendFolderAsync(string folderPath)
        {
            await RunUdtTransfer(ct => SendFolderInternal(folderPath, ct));
        }

        /// <summary>Sends a chunk of a file (type 0x02) for concurrent transfer.</summary>
        public async Task SendChunkedAsync(long offset, long chunkSize, long totalSize)
        {
            await RunUdtTransfer(ct => SendChunkedInternal(offset, chunkSize, totalSize, ct));
        }

        private async Task RunUdtTransfer(Func<CancellationToken, Task> transferAction)
        {
            _cts = new CancellationTokenSource();
            _isRunning = true;

            var startedHandler = OnStarted;
            if (startedHandler != null) startedHandler();

            try
            {
                UdtDll.EnsureExtracted();
                UdtNative.UdtStartup();
                await transferAction(_cts.Token).ConfigureAwait(false);
                // Wait for server ACK before closing — confirms data was received
                var ackBuf = new byte[1];
                int ackTimeout = 30000;
                UdtNative.udt_setsockopt(_socket, 0, UdtNative.UDT_RCVTIMEO, ref ackTimeout, 4);
                await Task.Run(() => UdtNative.udt_recv(_socket, ackBuf, 1, 0), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log(L.C_TransferCancelled);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log(L.C_Error(ex.Message));
                var handler = OnError;
                if (handler != null) handler(ex.Message);
            }
            finally
            {
                _isRunning = false;
                if (_socket >= 0)
                {
                    try { UdtNative.udt_close(_socket); } catch { }
                    _socket = -1;
                }
                UdtNative.UdtCleanup();
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
            }
        }

        /// <summary>Cancels the current transfer. Safe to call from any thread.</summary>
        public void Cancel()
        {
            var cts = _cts;
            if (cts != null) cts.Cancel();
            if (_socket >= 0)
            {
                try { UdtNative.udt_close(_socket); } catch { }
                _socket = -1;
            }
        }

        private async Task UdtConnect(CancellationToken ct)
        {
            _socket = UdtNative.udt_socket(UdtNative.AF_INET, UdtNative.SOCK_STREAM, 0);
            if (_socket < 0)
                throw new Exception("Failed to create UDT socket");

            if (_localPort > 0)
            {
                var localAddr = UdtNative.BuildSockaddr("0.0.0.0", _localPort);
                if (UdtNative.udt_bind(_socket, ref localAddr, UdtNative.SockAddrSize) == UdtNative.ERROR)
                    throw new Exception("UDT bind to port " + _localPort + " failed: " + UdtNative.GetErrorDesc());
            }

            Log(L.UdtC_Connecting(_serverIp, _port));
            var addr = UdtNative.BuildSockaddr(_serverIp, _port);
            int connectResult = await Task.Run(
                () => UdtNative.udt_connect(_socket, ref addr, UdtNative.SockAddrSize), ct);
            if (connectResult == UdtNative.ERROR)
                throw new Exception("UDT connect failed: " + UdtNative.GetErrorDesc());
            Log(L.C_Connected(_serverIp, _port));
            UdtNative.SetTimeout(_socket, 30000, 30000);
            // Set larger buffers for better throughput
            int bufSize = 8 * 1024 * 1024; // 8 MB
            UdtNative.udt_setsockopt(_socket, 0, UdtNative.UDT_SNDBUF, ref bufSize, 4);
            UdtNative.udt_setsockopt(_socket, 0, UdtNative.UDT_RCVBUF, ref bufSize, 4);
            await UdtIo.WaitForConnectionReady(_socket, ct);
        }

        private async Task SendChunkedInternal(long offset, long chunkSize, long totalSize, CancellationToken ct)
        {
            await UdtConnect(ct);
            var fileInfo = new FileInfo(_filePath);
            string fileName = fileInfo.Name;
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);

            // Chunk header: type(1) + totalSize(8) + offset(8) + chunkSize(8) + nameLen(4) + name
            var header = new byte[1 + 8 + 8 + 8 + 4 + nameBytes.Length];
            int pos = 0;
            header[pos++] = 0x02; // chunked file
            Buffer.BlockCopy(BitConverter.GetBytes(totalSize), 0, header, pos, 8); pos += 8;
            Buffer.BlockCopy(BitConverter.GetBytes(offset), 0, header, pos, 8); pos += 8;
            Buffer.BlockCopy(BitConverter.GetBytes(chunkSize), 0, header, pos, 8); pos += 8;
            Buffer.BlockCopy(BitConverter.GetBytes(nameBytes.Length), 0, header, pos, 4); pos += 4;
            Buffer.BlockCopy(nameBytes, 0, header, pos, nameBytes.Length);
            await UdtIo.UdtWriteExactAsync(_socket, header, 0, header.Length, ct);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await SendFilePayload(_socket, _filePath, chunkSize, fileName, ct, offset);
            sw.Stop();

            Log(L.C_TransferDone(fileName + " chunk", Utils.FormatSize(chunkSize),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(chunkSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));
        }

        private async Task SendFileInternal(CancellationToken ct)
        {
            await UdtConnect(ct);
            var fileInfo = new FileInfo(_filePath);
            long fileSize = fileInfo.Length;
            string fileName = fileInfo.Name;
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);

            var header = new byte[1 + 12 + nameBytes.Length];
            header[0] = 0x00;
            Buffer.BlockCopy(BitConverter.GetBytes(fileSize), 0, header, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(nameBytes.Length), 0, header, 9, 4);
            Buffer.BlockCopy(nameBytes, 0, header, 13, nameBytes.Length);
            await UdtIo.UdtWriteExactAsync(_socket, header, 0, header.Length, ct);

            Log(L.C_Sending(fileName, Utils.FormatSize(fileSize)));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await SendFilePayload(_socket, _filePath, fileSize, fileName, ct);
            sw.Stop();

            Log(L.C_TransferDone(fileName, Utils.FormatSize(fileSize),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

            var completeHandler = OnTransferComplete;
            if (completeHandler != null) completeHandler();
        }

        private async Task SendFolderInternal(string folderPath, CancellationToken ct)
        {
            await UdtConnect(ct);
            string folderName = Path.GetFileName(folderPath);
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = "folder";
            byte[] folderNameBytes = System.Text.Encoding.UTF8.GetBytes(folderName);

            var files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                Log(L.S_ZeroFiles);
                var errHandler = OnError;
                if (errHandler != null) errHandler(L.S_ZeroFiles);
                return;
            }

            var fileEntries = new FileEntry[files.Length];
            long totalSize = 0;
            for (int i = 0; i < files.Length; i++)
            {
                var fi = new FileInfo(files[i]);
                long size = fi.Length;
                fileEntries[i] = new FileEntry
                {
                    Path = files[i],
                    Size = size,
                    RelativePath = files[i].Substring(folderPath.Length).TrimStart('\\', '/')
                };
                totalSize += size;
            }

            Log(L.C_SendingFolder(folderName, files.Length, Utils.FormatSize(totalSize)));

            var header = new byte[1 + 2 + folderNameBytes.Length + 4];
            int pos = 0;
            header[pos++] = 0x01;
            Buffer.BlockCopy(BitConverter.GetBytes((short)folderNameBytes.Length), 0, header, pos, 2); pos += 2;
            Buffer.BlockCopy(folderNameBytes, 0, header, pos, folderNameBytes.Length); pos += folderNameBytes.Length;
            Buffer.BlockCopy(BitConverter.GetBytes(files.Length), 0, header, pos, 4);
            await UdtIo.UdtWriteExactAsync(_socket, header, 0, header.Length, ct);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long totalSent = 0;

            foreach (var entry in fileEntries)
            {
                if (ct.IsCancellationRequested) break;

                byte[] relPathBytes = System.Text.Encoding.UTF8.GetBytes(entry.RelativePath);
                var fileHeader = new byte[8 + 2 + relPathBytes.Length];
                Buffer.BlockCopy(BitConverter.GetBytes(entry.Size), 0, fileHeader, 0, 8);
                Buffer.BlockCopy(BitConverter.GetBytes((short)relPathBytes.Length), 0, fileHeader, 8, 2);
                Buffer.BlockCopy(relPathBytes, 0, fileHeader, 10, relPathBytes.Length);
                await UdtIo.UdtWriteExactAsync(_socket, fileHeader, 0, fileHeader.Length, ct);

                await SendFilePayload(_socket, entry.Path, entry.Size, entry.RelativePath, ct);
                totalSent += entry.Size;

                var progressHandler = OnProgress;
                if (progressHandler != null)
                    progressHandler(new TransferProgress
                    {
                        BytesTransferred = totalSent,
                        TotalBytes = totalSize,
                        SpeedBytesPerSecond = totalSent / sw.Elapsed.TotalSeconds,
                        Elapsed = sw.Elapsed,
                        FileName = folderName
                    });
            }

            sw.Stop();
            Log(L.C_FolderTransferDone(folderName, files.Length, Utils.FormatSize(totalSize),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(totalSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

            var completeHandler = OnTransferComplete;
            if (completeHandler != null) completeHandler();
        }

        private async Task SendFilePayload(int clientSocket, string filePath, long fileSize,
            string displayName, CancellationToken ct, long fileOffset = 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytesSent = 0;
            var bufA = new byte[_bufferSize];
            var bufB = new byte[_bufferSize];
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();

            using (var sha256 = SHA256.Create())
            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.SequentialScan))
            {
                if (fileOffset > 0)
                    fileStream.Seek(fileOffset, SeekOrigin.Begin);

                long remaining = fileSize;
                if (remaining == 0)
                {
                    sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                    await UdtIo.UdtWriteExactAsync(clientSocket, sha256.Hash, 0, 32, ct);
                    return;
                }

                int toRead = (int)Math.Min(remaining, (long)bufA.Length);
                int read = await fileStream.ReadAsync(bufA, 0, toRead, ct);
                if (read <= 0)
                {
                    sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                    await UdtIo.UdtWriteExactAsync(clientSocket, sha256.Hash, 0, 32, ct);
                    return;
                }
                remaining -= read;

                var cur = bufA;
                var nxt = bufB;

                while (remaining > 0 && !ct.IsCancellationRequested)
                {
                    int nextToRead = (int)Math.Min(remaining, (long)nxt.Length);
                    var nextReadTask = fileStream.ReadAsync(nxt, 0, nextToRead, ct);

                    sha256.TransformBlock(cur, 0, read, null, 0);
                    await UdtIo.UdtWriteExactAsync(clientSocket, cur, 0, read, ct);
                    bytesSent += read;

                    read = await nextReadTask;
                    if (read <= 0) break;
                    remaining -= read;

                    var tmp = cur; cur = nxt; nxt = tmp;

                    if (progressTimer.ElapsedMilliseconds >= 100 || remaining == 0)
                    {
                        progressTimer.Restart();
                        var handler = OnProgress;
                        if (handler != null)
                            handler(new TransferProgress
                            {
                                BytesTransferred = bytesSent,
                                TotalBytes = fileSize,
                                SpeedBytesPerSecond = bytesSent / sw.Elapsed.TotalSeconds,
                                Elapsed = sw.Elapsed,
                                FileName = displayName
                            });
                    }
                }

                if (read > 0)
                {
                    sha256.TransformBlock(cur, 0, read, null, 0);
                    await UdtIo.UdtWriteExactAsync(clientSocket, cur, 0, read, ct);
                    bytesSent += read;
                }
                sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                await UdtIo.UdtWriteExactAsync(clientSocket, sha256.Hash, 0, 32, ct);
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }

    #endregion

    #region UDT I/O Helpers

    internal static class UdtIo
    {
        public static async Task<int> UdtReadExactAsync(int socket, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await UdtReadAsync(socket, buffer, offset + totalRead, count - totalRead, ct);
                if (read <= 0) throw new IOException(L.S_ConnClosedUnexpectedly);
                totalRead += read;
            }
            return totalRead;
        }

        public static async Task<int> UdtReadAsync(int socket, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            // Short reads: if offset != 0, need a temp buffer or slice
            byte[] target = offset == 0 ? buffer : new byte[count];
            int result = await Task.Run(() => UdtNative.udt_recv(socket, target, count, 0), ct);
            if (result > 0 && offset != 0)
                Buffer.BlockCopy(target, 0, buffer, offset, result);
            return result;
        }

        public static async Task UdtWriteExactAsync(int socket, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalSent = 0;
            while (totalSent < count)
            {
                int remaining = count - totalSent;
                // udt_send(buf, len, flags) takes the buffer pointer + length; no offset param.
                // Use a temp buffer when we need a slice.
                byte[] sendBuf;
                if (offset == 0 && totalSent == 0)
                    sendBuf = buffer;
                else
                {
                    sendBuf = new byte[remaining];
                    Buffer.BlockCopy(buffer, offset + totalSent, sendBuf, 0, remaining);
                }
                int sent = await Task.Run(
                    () => UdtNative.udt_send(socket, sendBuf, remaining, 0), ct);
                if (sent < 0)
                {
                    string err = UdtNative.GetErrorDesc();
                    throw new IOException("UDT send failed: " + err);
                }
                totalSent += sent;
            }
        }

        /// <summary>Wait for UDT socket to transition from BOUND to CONNECTED state after async handshake.</summary>
        public static async Task WaitForConnectionReady(int socket, CancellationToken ct)
        {
            // Poll with empty send until socket leaves BOUND state (handshake complete).
            // High concurrency means later connections wait for earlier handshakes to finish.
            // Use short initial polls (50ms) then back off to 200ms after 100 attempts.
            for (int i = 0; i < 600; i++)
            {
                if (ct.IsCancellationRequested) break;
                int sent = await Task.Run(() => UdtNative.udt_send(socket, Utils.EmptyBytes, 0, 0), ct).ConfigureAwait(false);
                if (sent >= 0) return; // connected
                int delay = i < 100 ? 50 : 200; // fast poll first 5s, then 200ms
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    #endregion
}
