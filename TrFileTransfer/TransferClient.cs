using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>TCP file/folder sender with SHA256 integrity verification.
    /// The 0x00-0x03 wire protocol lives in ClientWire, shared with the UDT client.</summary>
    public class TransferClient
    {
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
        /// <summary>Fired when the transfer starts.</summary>
        public event Action OnStarted;
        /// <summary>Fired when the transfer stops (completed, cancelled, or error).</summary>
        public event Action OnStopped;

        /// <summary>Whether a transfer is currently in progress.</summary>
        public bool IsRunning { get { return _isRunning; } }

        /// <summary>
        /// Creates a TCP client for sending files or folders.
        /// </summary>
        /// <param name="serverIp">Target server IPv4 address.</param>
        /// <param name="port">Target server port.</param>
        /// <param name="filePath">Path to the file or folder to send.</param>
        /// <param name="bufferSize">I/O buffer size in bytes (default 4 MB).</param>
        public TransferClient(string serverIp, int port, string filePath, int bufferSize = 4194304)
            : this(serverIp, port, filePath, 0, bufferSize, 0)
        {
        }

        /// <summary>Creates a TCP client bound to a specific local port for concurrent transfers.</summary>
        public TransferClient(string serverIp, int port, string filePath, int localPort, int bufferSize = 4194304)
            : this(serverIp, port, filePath, localPort, bufferSize, 0)
        {
        }

        /// <summary>Full constructor with rate limiting (maxBytesPerSec = 0 means unlimited).</summary>
        public TransferClient(string serverIp, int port, string filePath, int localPort, int bufferSize, int maxBytesPerSec)
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

        /// <summary>Sends the file specified in the constructor over TCP.</summary>
        public async Task SendAsync()
        {
            await RunTransfer(SendFileInternal);
        }

        /// <summary>Sends a folder recursively over TCP.</summary>
        /// <param name="folderPath">Path to the folder to send.</param>
        public async Task SendFolderAsync(string folderPath)
        {
            await RunTransfer(ct => SendFolderInternal(folderPath, ct));
        }

        /// <summary>Sends a chunk of a file (type 0x02) for concurrent transfer.</summary>
        public async Task SendChunkedAsync(long offset, long chunkSize, long totalSize)
        {
            await RunTransfer(ct => SendChunkedInternal(offset, chunkSize, totalSize, ct));
        }

        /// <summary>Sends a folder with resume support (type 0x04). Interrupted sessions
        /// continue from where the server's files on disk left off.</summary>
        /// <param name="existingSessionId">Session to resume, or null for a new session.</param>
        public async Task<Guid> SendFolderResumableAsync(Guid? existingSessionId = null)
        {
            var sessionId = existingSessionId ?? Guid.NewGuid();
            await RunTransfer(ct => SendFolderResumableInternal(sessionId, ct));
            return sessionId;
        }

        /// <summary>Sends a file with resume support (type 0x03), negotiating with server via 0x10 response.</summary>
        /// <param name="existingSessionId">Session to resume, or null for a new session.</param>
        /// <param name="verifyHash">When true, sends a full-file SHA256 so the server can
        /// reject a resumed file whose earlier segments no longer match the source.</param>
        public async Task<Guid> SendResumableAsync(Guid? existingSessionId = null, bool verifyHash = false)
        {
            var sessionId = existingSessionId ?? Guid.NewGuid();
            await RunTransfer(ct => SendResumableInternal(sessionId, ct, verifyHash));
            return sessionId;
        }

        private async Task RunTransfer(Func<CancellationToken, Task> transferAction)
        {
            _cts = new CancellationTokenSource();
            _isRunning = true;

            var startedHandler = OnStarted;
            if (startedHandler != null) startedHandler();

            try
            {
                await transferAction(_cts.Token).ConfigureAwait(false);
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
                throw;
            }
            finally
            {
                _isRunning = false;
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
            }
        }

        /// <summary>Cancels the current transfer. Safe to call from any thread.</summary>
        public void Cancel()
        {
            var cts = _cts;
            if (cts != null) cts.Cancel();
        }

        /// <summary>Creates the client socket; a busy source port raises PortBindException (no bytes sent yet).</summary>
        private TcpClient CreateClient()
        {
            if (_localPort <= 0) return new TcpClient();
            try
            {
                return new TcpClient(new IPEndPoint(IPAddress.Any, _localPort));
            }
            catch (SocketException ex)
            {
                throw new PortBindException(
                    "Bind local port " + _localPort + " failed: " + ex.Message, ex, _localPort);
            }
        }

        /// <summary>Connects and returns the wire stream (disposing it closes the connection).</summary>
        private async Task<TcpWireStream> ConnectAsync(CancellationToken ct)
        {
            var client = CreateClient();
            try
            {
                client.NoDelay = true;
                client.SendBufferSize = _bufferSize;
                client.ReceiveBufferSize = _bufferSize;

                Log(L.C_Connecting(_serverIp, _port));
                await client.ConnectAsync(_serverIp, _port).ConfigureAwait(false);
                Log(L.C_Connected(_serverIp, _port));

                return new TcpWireStream(client.GetStream(), L.C_ResumeConnClosed);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private async Task SendFileInternal(CancellationToken ct)
        {
            using (var ws = await ConnectAsync(ct).ConfigureAwait(false))
            {
                await ClientWire.SendSingleFileAsync(ws, _filePath, _bufferSize, _limiter, _cb, ct).ConfigureAwait(false);
            }
        }

        private async Task SendFolderInternal(string folderPath, CancellationToken ct)
        {
            using (var ws = await ConnectAsync(ct).ConfigureAwait(false))
            {
                await ClientWire.SendFolderAsync(ws, folderPath, _bufferSize, _limiter, _cb, ct).ConfigureAwait(false);
            }
        }

        private async Task SendChunkedInternal(long offset, long chunkSize, long totalSize, CancellationToken ct)
        {
            using (var ws = await ConnectAsync(ct).ConfigureAwait(false))
            {
                await ClientWire.SendChunkAsync(ws, _filePath, offset, chunkSize, totalSize,
                    _bufferSize, _limiter, _cb, ct).ConfigureAwait(false);
            }
        }

        private async Task SendFolderResumableInternal(Guid sessionId, CancellationToken ct)
        {
            using (var ws = await ConnectAsync(ct).ConfigureAwait(false))
            {
                await ClientWire.SendFolderResumableAsync(ws, _filePath, sessionId,
                    _serverIp, _port, false, _bufferSize, _limiter, _cb, ct).ConfigureAwait(false);
            }
        }

        private async Task SendResumableInternal(Guid sessionId, CancellationToken ct, bool verifyHash)
        {
            using (var ws = await ConnectAsync(ct).ConfigureAwait(false))
            {
                await ClientWire.SendResumableAsync(ws, _filePath, sessionId, verifyHash,
                    _serverIp, _port, false, _bufferSize, _limiter, _cb, ct).ConfigureAwait(false);
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }
}
