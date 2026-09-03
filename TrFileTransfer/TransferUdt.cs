using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>
    /// UDT transport. The 0x00-0x03 wire protocol lives in ServerWire / ClientWire
    /// (shared with TCP); this file holds only the UDT-specific pieces: native
    /// interop, DLL extraction, socket lifecycle, and the UDT accept/ACK semantics.
    /// </summary>
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

    /// <summary>UDT STREAM file/folder receiver. Protocol handling is shared (ServerWire).</summary>
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
        private readonly List<int> _clientSockets = new List<int>();
        private readonly ServerWireContext _wire = new ServerWireContext();

        /// <summary>Fired for every log message.</summary>
        public event Action<string> OnLog;
        /// <summary>Fired periodically during transfer with progress info.</summary>
        public event Action<TransferProgress> OnProgress;
        /// <summary>Fired when a non-fatal error occurs.</summary>
        public event Action<string> OnError;
        /// <summary>Fired when a single transfer completes. Server keeps listening.</summary>
        public event Action OnTransferComplete;
        /// <summary>Fired when a file has been fully received and saved (path, size).</summary>
        public event Action<string, long> OnFileReceived;
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
            _wire.SaveDirectory = saveDirectory;
            _wire.Cb.Log = Log;
            _wire.Cb.Progress = delegate(TransferProgress p)
            {
                var h = OnProgress; if (h != null) h(p);
            };
            _wire.Cb.Error = delegate(string msg)
            {
                var h = OnError; if (h != null) h(msg);
            };
            _wire.Cb.Complete = delegate
            {
                var h = OnTransferComplete; if (h != null) h();
            };
            _wire.Cb.FileReceived = delegate(string path, long size)
            {
                var h = OnFileReceived; if (h != null) h(path, size);
            };
        }

        /// <summary>Starts listening for incoming connections. Fires OnStarted on success.</summary>
        public void Start()
        {
            _cts = new CancellationTokenSource();
            ServerResumeStore.CleanupStale(7); // drop orphaned resume sessions from clients that never returned
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

            if (UdtNative.udt_listen(_socket, 32) == UdtNative.ERROR)
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
            // Persist incomplete resume sessions and dispose chunk trackers
            _wire.Shutdown();
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

            var ws = new UdtWireStream(clientSocket, true);
            try
            {
                WireOutcome outcome = await ServerWire.HandleClientAsync(ws, _wire, ct).ConfigureAwait(false);

                if (outcome.IsChunked)
                {
                    // Application-level ACK for this chunk. Chunk connections always clean
                    // up their progress card; assembled-file completion fires separately.
                    var ack2 = new byte[1] { 0x01 };
                    await Task.Run(() => UdtNative.udt_send(clientSocket, ack2, 1, 0), ct).ConfigureAwait(false);
                    var ccHandler = OnClientTransferComplete;
                    if (ccHandler != null) ccHandler(clientEp);
                }
                else if (outcome.Success)
                {
                    var ccHandler = OnClientTransferComplete;
                    if (ccHandler != null) ccHandler(clientEp);
                    // Application-level ACK only on verified success
                    var ack = new byte[1] { 0x01 };
                    await Task.Run(() => UdtNative.udt_send(clientSocket, ack, 1, 0), ct).ConfigureAwait(false);
                }
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
                ws.Dispose(); // closes the native socket, unblocking pending I/O
                System.Threading.Interlocked.Decrement(ref _activeClients);
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }

    #endregion

    #region TransferUdtClient

    /// <summary>UDT STREAM file/folder sender. Protocol handling is shared (ClientWire).</summary>
    public class TransferUdtClient
    {
        private int _socket;
        private CancellationTokenSource _cts;
        private readonly string _serverIp;
        private readonly int _port;
        private readonly string _filePath;
        private readonly int _bufferSize;
        private readonly int _localPort;
        private readonly SpeedLimiter _limiter;
        private volatile bool _isRunning;
        private readonly WireCallbacks _cb = new WireCallbacks();

        /// <summary>Fired for every log message.</summary>
        public event Action<string> OnLog;
        /// <summary>Fired periodically during transfer with progress info.</summary>
        public event Action<TransferProgress> OnProgress;
        /// <summary>Fired when a non-fatal error occurs.</summary>
        public event Action<string> OnError;
        /// <summary>Fired when the transfer completes successfully.</summary>
        public event Action OnTransferComplete;
        /// <summary>Fired when a file has been fully received and saved (path, size).</summary>
        public event Action<string, long> OnFileReceived;
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
        /// <param name="bufferSize">I/O buffer size in bytes (default 4 MB).</param>
        public TransferUdtClient(string serverIp, int port, string filePath, int bufferSize = 4194304)
            : this(serverIp, port, filePath, 0, bufferSize, 0)
        {
        }

        /// <summary>Creates a UDT client bound to a specific local port for concurrent transfers.</summary>
        public TransferUdtClient(string serverIp, int port, string filePath, int localPort, int bufferSize = 4194304)
            : this(serverIp, port, filePath, localPort, bufferSize, 0)
        {
        }

        /// <summary>Full constructor with rate limiting (maxBytesPerSec = 0 means unlimited).</summary>
        public TransferUdtClient(string serverIp, int port, string filePath, int localPort, int bufferSize, int maxBytesPerSec)
        {
            _serverIp = serverIp;
            _port = port;
            _filePath = filePath;
            _bufferSize = bufferSize;
            _localPort = localPort;
            _limiter = new SpeedLimiter(maxBytesPerSec);
            _cb.Log = Log;
            _cb.Progress = delegate(TransferProgress p)
            {
                var h = OnProgress; if (h != null) h(p);
            };
            _cb.Error = delegate(string msg)
            {
                var h = OnError; if (h != null) h(msg);
            };
            _cb.Complete = delegate
            {
                var h = OnTransferComplete; if (h != null) h();
            };
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

        public async Task<Guid> SendResumableAsync(Guid? existingSessionId = null, bool verifyHash = false)
        {
            var sessionId = existingSessionId ?? Guid.NewGuid();
            await RunUdtTransfer(ct => SendResumableUdtInternal(sessionId, ct, verifyHash));
            return sessionId;
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
                    throw new PortBindException(
                        "UDT bind to port " + _localPort + " failed: " + UdtNative.GetErrorDesc(), null, _localPort);
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

        private async Task SendFileInternal(CancellationToken ct)
        {
            await UdtConnect(ct);
            using (var ws = new UdtWireStream(_socket, false))
            {
                await ClientWire.SendSingleFileAsync(ws, _filePath, _bufferSize, _limiter, _cb, ct).ConfigureAwait(false);
            }
        }

        private async Task SendFolderInternal(string folderPath, CancellationToken ct)
        {
            await UdtConnect(ct);
            using (var ws = new UdtWireStream(_socket, false))
            {
                await ClientWire.SendFolderAsync(ws, folderPath, _bufferSize, _limiter, _cb, ct).ConfigureAwait(false);
            }
        }

        private async Task SendChunkedInternal(long offset, long chunkSize, long totalSize, CancellationToken ct)
        {
            await UdtConnect(ct);
            using (var ws = new UdtWireStream(_socket, false))
            {
                await ClientWire.SendChunkAsync(ws, _filePath, offset, chunkSize, totalSize,
                    _bufferSize, _limiter, _cb, ct).ConfigureAwait(false);
            }
        }

        private async Task SendResumableUdtInternal(Guid sessionId, CancellationToken ct, bool verifyHash)
        {
            await UdtConnect(ct);
            using (var ws = new UdtWireStream(_socket, false))
            {
                await ClientWire.SendResumableAsync(ws, _filePath, sessionId, verifyHash,
                    _serverIp, _port, true, _bufferSize, _limiter, _cb, ct).ConfigureAwait(false);
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
        /// <summary>Last native UDT error description (set on the thread that called udt_recv/udt_send).</summary>
        public static string LastError;

        public static async Task<int> UdtReadExactAsync(int socket, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await UdtReadAsync(socket, buffer, offset + totalRead, count - totalRead, ct);
                if (read <= 0)
                    throw new IOException(L.S_ConnClosedUnexpectedly + (LastError != null ? " [" + LastError + "]" : ""));
                totalRead += read;
            }
            return totalRead;
        }

        public static async Task<int> UdtReadAsync(int socket, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            // Short reads: if offset != 0, need a temp buffer or slice
            byte[] target = offset == 0 ? buffer : new byte[count];
            int result = await Task.Run(() =>
            {
                int r = UdtNative.udt_recv(socket, target, count, 0);
                LastError = r <= 0 ? UdtNative.GetErrorDesc() : null;
                return r;
            }, ct);
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
                int sent = await Task.Run(() =>
                {
                    int s = UdtNative.udt_send(socket, sendBuf, remaining, 0);
                    LastError = s < 0 ? UdtNative.GetErrorDesc() : null;
                    return s;
                }, ct);
                if (sent < 0)
                {
                    string err = LastError ?? "unknown";
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
