using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>TCP file/folder receiver with SHA256 integrity verification.
    /// The 0x00-0x03 wire protocol lives in ServerWire, shared with the UDT server.</summary>
    public class TransferServer
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly string _bindAddress;
        private readonly int _port;
        private readonly string _saveDirectory;
        private readonly int _bufferSize;
        private volatile bool _isRunning;
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
        /// <summary>Fired when a 0x06 text message has been received.</summary>
        public event Action<string> OnTextReceived;
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

        /// <summary>
        /// Creates a TCP server that listens for incoming file transfers.
        /// </summary>
        /// <param name="bindAddress">IPv4 address to bind to, or "0.0.0.0" for all interfaces.</param>
        /// <param name="port">Port to listen on.</param>
        /// <param name="saveDirectory">Directory where received files are saved.</param>
        /// <param name="bufferSize">I/O buffer size in bytes (default 4 MB).</param>
        public TransferServer(string bindAddress, int port, string saveDirectory, int bufferSize = 4194304)
        {
            _bindAddress = bindAddress;
            _port = port;
            _saveDirectory = saveDirectory;
            _bufferSize = bufferSize;
            _wire.SaveDirectory = saveDirectory;
            _wire.BufferSize = bufferSize;
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
            _wire.Cb.TextReceived = delegate(string text)
            {
                var h = OnTextReceived; if (h != null) h(text);
            };
        }

        /// <summary>When non-empty, clients must present this pairing code (0x05) before
        /// any transfer is accepted. Set before Start().</summary>
        public string PairingCode
        {
            get { return _wire.PairingCode; }
            set { _wire.PairingCode = value; }
        }

        /// <summary>Starts listening for incoming connections. Fires OnStarted on success.</summary>
        public void Start()
        {
            _cts = new CancellationTokenSource();
            ServerResumeStore.CleanupStale(7); // drop orphaned resume sessions from clients that never returned
            IPAddress bindIp;
            if (!IPAddress.TryParse(_bindAddress, out bindIp))
                bindIp = IPAddress.Any;

            try
            {
                _listener = new TcpListener(bindIp, _port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();
            }
            catch (Exception ex)
            {
                Log(L.S_BindFailed(_bindAddress, _port.ToString(), ex.Message));
                var errHandler = OnError;
                if (errHandler != null) errHandler(ex.Message);
                _isRunning = false;
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
                return;
            }

            _isRunning = true;

            var handler = OnStarted;
            if (handler != null) handler();

            Log(L.S_Started(_port.ToString(), _saveDirectory));

            Task.Factory.StartNew(() => AcceptLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>Stops the server and closes the listening socket.</summary>
        public void Stop()
        {
            _isRunning = false;
            var cts = _cts;
            if (cts != null) cts.Cancel();
            try
            {
                var listener = _listener;
                if (listener != null) listener.Stop();
            }
            catch { }

            // Persist incomplete resume sessions and dispose chunk trackers
            _wire.Shutdown();

            var handler = OnStopped;
            if (handler != null) handler();

            Log(L.S_Stopped);
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    client.NoDelay = true;
                    client.SendBufferSize = _bufferSize;
                    client.ReceiveBufferSize = _bufferSize;
                    var clientEp = client.Client.RemoteEndPoint as IPEndPoint;
                    Log(L.S_ClientConnected(clientEp));
                    var _ = HandleClient(client, ct, clientEp);
                }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException)
                        break;
                    if (!ct.IsCancellationRequested)
                    {
                        Log(L.S_AcceptError(ex.Message));
                        var handler = OnError;
                        if (handler != null) handler(ex.Message);
                    }
                }
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken ct, IPEndPoint clientEp)
        {
            var connectedHandler = OnClientConnected;
            if (connectedHandler != null) connectedHandler(clientEp);

            Action<TransferProgress> clientProgress = p =>
            {
                var ch = OnClientProgress; if (ch != null) ch(clientEp, p);
            };
            OnProgress += clientProgress;

            using (client)
            {
                try
                {
                    using (var ws = new TcpWireStream(client.GetStream(), L.S_ConnClosedUnexpectedly))
                    {
                        await ServerWire.HandleClientAsync(ws, _wire, ct).ConfigureAwait(false);
                    }

                    var ccHandler = OnClientTransferComplete;
                    if (ccHandler != null) ccHandler(clientEp);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
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
                }
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }
}
