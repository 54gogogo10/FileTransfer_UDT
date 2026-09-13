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

        /// <summary>True when the last run ended via Cancel() — lets the UI distinguish
        /// a deliberate pause from a genuine failure (cancellation does not throw).</summary>
        public bool WasCancelled { get { return _wasCancelled; } }
        private bool _wasCancelled;

        /// <summary>Pairing code sent as a 0x05 auth frame before any transfer.
        /// Null/empty sends nothing (compatible with servers that don't require it).</summary>
        public string PairingCode { get; set; }

        /// <summary>When a pairing code is set, try the encrypted (0x09 ECDH) handshake
        /// first. Default from Config "Encrypt".</summary>
        public bool EncryptionEnabled { get; set; }

        /// <summary>Default true: if the peer cannot do the authenticated handshake
        /// (older build), reconnect in plaintext so version-skew still works. Set false
        /// (Config "EncryptStrict") to fail closed, so a downgrade cannot be forced.</summary>
        public bool EncryptionDowngradeAllowed { get; set; }

        /// <summary>Offer the 0x08 deflate transport before the transfer (falls back
        /// to uncompressed against older peers). Default from Config "Compress".</summary>
        public bool CompressionEnabled { get; set; }

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
            EncryptionEnabled = Config.GetBool("Encrypt", true);
            EncryptionDowngradeAllowed = !Config.GetBool("EncryptStrict", false);
            CompressionEnabled = Config.GetBool("Compress", true);
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
        /// <param name="keepState">Sync mode — keep the session state after completion so
        /// repeat runs send only differences.</param>
        public async Task<Guid> SendFolderResumableAsync(Guid? existingSessionId = null, bool keepState = false)
        {
            var sessionId = existingSessionId ?? Guid.NewGuid();
            await RunTransfer(ct => SendFolderResumableInternal(sessionId, ct, keepState));
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

        /// <summary>Sends a UTF-8 text message (type 0x06) over TCP.</summary>
        public async Task SendTextAsync(string text)
        {
            await RunTransfer(ct => SendTextInternal(text, ct));
        }

        private async Task RunTransfer(Func<CancellationToken, Task> transferAction)
        {
            _cts = new CancellationTokenSource();
            _isRunning = true;
            _wasCancelled = false;

            var startedHandler = OnStarted;
            if (startedHandler != null) startedHandler();

            try
            {
                await transferAction(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _wasCancelled = true;
                Log(L.C_TransferCancelled);
            }
            catch (ObjectDisposedException)
            {
                // Cancel() during the connect phase disposes the socket, which
                // surfaces as ODE rather than OCE — still a deliberate cancel
                if (_cts != null && _cts.IsCancellationRequested)
                {
                    _wasCancelled = true;
                    Log(L.C_TransferCancelled);
                }
            }
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

        /// <summary>Connects and returns the wire stream (disposing it closes the connection).
        /// The connect is cancellable: the token disposes the in-flight socket instead of
        /// letting it hang until the OS connect timeout (~21 s).</summary>
        private async Task<TcpWireStream> ConnectAsync(CancellationToken ct)
        {
            // A cancel that landed while the hash/manifest pass was still finishing would
            // otherwise walk into connect with a dead token — and ct.Register then fires
            // synchronously, disposing the client underneath its own pending EndConnect
            // (which surfaces as an opaque NullReferenceException instead of a cancel).
            ct.ThrowIfCancellationRequested();
            var client = CreateClient();
            try
            {
                client.NoDelay = true;
                client.SendBufferSize = _bufferSize;
                client.ReceiveBufferSize = _bufferSize;

                Log(L.C_Connecting(_serverIp, _port));
                var connectTask = client.ConnectAsync(_serverIp, _port);
                using (ct.Register(delegate
                {
                    try { client.Dispose(); } catch { }
                }))
                {
                    try
                    {
                        await connectTask.ConfigureAwait(false);
                    }
                    catch (Exception connectFailure)
                    {
                        // The cancellation that disposed the client mid-connect must read
                        // as a cancel, not as a connect error (C# 5: no exception filters)
                        ct.ThrowIfCancellationRequested();
                        throw connectFailure;
                    }
                }
                Log(L.C_Connected(_serverIp, _port));

                return new TcpWireStream(client.GetStream(), L.C_ResumeConnClosed);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Connects and runs the prelude negotiation: 0x08 compression offer, then the
        /// 0x07/0x05 auth. Against an older peer each offer can fail independently —
        /// the connection is re-established with that feature dropped (compression
        /// first, then encryption), at most twice.
        /// </summary>
        private async Task<IWireStream> OpenAndAuthenticateAsync(CancellationToken ct)
        {
            return (await OpenAndAuthenticateResultAsync(ct, false).ConfigureAwait(false)).Stream;
        }

        /// <summary>Same, returning the full negotiation result. wantPerFileSkip offers the
        /// 0x0A capability frame so a 0x04 body can skip files the receiver already holds;
        /// a peer that drops it costs one reconnect without the offer (budget 3 attempts —
        /// encryption, compression and skip can each be discovered independently).</summary>
        private async Task<AuthResult> OpenAndAuthenticateResultAsync(CancellationToken ct, bool wantPerFileSkip)
        {
            bool allowEncrypt = EncryptionEnabled;
            bool allowCompress = CompressionEnabled;
            int maxAttempts = wantPerFileSkip ? 3 : 2;
            for (int attempt = 0; ; attempt++)
            {
                TcpWireStream raw = await ConnectAsync(ct).ConfigureAwait(false);
                AuthResult auth;
                try
                {
                    auth = await ClientWire.AuthenticateAsync(raw, PairingCode, allowEncrypt, allowCompress, _cb, ct,
                        EncryptionDowngradeAllowed, wantPerFileSkip).ConfigureAwait(false);
                }
                catch
                {
                    raw.Dispose();
                    throw;
                }
                if (auth.NeedNoCompressionFallback && allowCompress && attempt < maxAttempts)
                {
                    raw.Dispose();
                    allowCompress = false;
                    _cb.RaiseLog(L.C_CompressFallback);
                    continue;
                }
                if (auth.NeedPlainFallback && allowEncrypt && attempt < maxAttempts)
                {
                    raw.Dispose();
                    // A peer too old for 0x17 is too old for 0x08 as well
                    allowEncrypt = false;
                    allowCompress = false;
                    _cb.RaiseLog(L.C_EncryptFallback);
                    continue;
                }
                if (auth.NeedNoPerFileSkipFallback && wantPerFileSkip && attempt < maxAttempts)
                {
                    raw.Dispose();
                    wantPerFileSkip = false;
                    continue;
                }
                // A successful prelude (encryption or compression accepted) proves the
                // peer is a current build, which means it sends the TCP completion
                // verdict byte. Without that proof (no pairing and compression off, or
                // an older peer) do not wait for a byte that may never come.
                _cb.CompletionAckOnStream = auth.Encrypted || auth.Compressed;
                return auth;
            }
        }

        private async Task SendFileInternal(CancellationToken ct)
        {
            using (var ws = await OpenAndAuthenticateAsync(ct).ConfigureAwait(false))
            {
                await ClientWire.SendSingleFileAsync(ws, _filePath, _bufferSize, _limiter, _cb, ct).ConfigureAwait(false);
            }
        }

        private async Task SendFolderInternal(string folderPath, CancellationToken ct)
        {
            using (var ws = await OpenAndAuthenticateAsync(ct).ConfigureAwait(false))
            {
                await ClientWire.SendFolderAsync(ws, folderPath, _bufferSize, _limiter, _cb, ct).ConfigureAwait(false);
            }
        }

        private async Task SendChunkedInternal(long offset, long chunkSize, long totalSize, CancellationToken ct)
        {
            using (var ws = await OpenAndAuthenticateAsync(ct).ConfigureAwait(false))
            {
                await ClientWire.SendChunkAsync(ws, _filePath, offset, chunkSize, totalSize,
                    _bufferSize, _limiter, _cb, ct).ConfigureAwait(false);
            }
        }

        private async Task SendFolderResumableInternal(Guid sessionId, CancellationToken ct, bool keepState)
        {
            // Hash the whole folder before connecting, not after: the connect-then-hash
            // order left the server idle through the hash pass, and a large enough folder
            // made it drop the connection before the transfer could start.
            var manifest = await Task.Run(
                delegate { return ClientWire.BuildFolderManifest(_filePath, _cb, ct); }, ct).ConfigureAwait(false);
            if (manifest == null) return;
            var auth = await OpenAndAuthenticateResultAsync(ct, wantPerFileSkip: true).ConfigureAwait(false);
            using (var ws = auth.Stream)
            {
                await ClientWire.SendFolderResumableAsync(ws, manifest, sessionId,
                    _serverIp, _port, false, _bufferSize, _limiter, _cb, ct, keepState, auth.PerFileSkip).ConfigureAwait(false);
            }
        }

        private async Task SendResumableInternal(Guid sessionId, CancellationToken ct, bool verifyHash)
        {
            // Same reason as SendFolderResumableInternal for hashing before the connect;
            // the cache keeps a retry from re-reading the file a second and third time.
            byte[] fullHash = null;
            if (verifyHash)
            {
                fullHash = await Task.Run(delegate
                {
                    bool computed;
                    return FileHashCache.ForClient.GetOrCompute(_filePath, _cb, true, out computed, ct);
                }, ct).ConfigureAwait(false);
            }
            using (var ws = await OpenAndAuthenticateAsync(ct).ConfigureAwait(false))
            {
                await ClientWire.SendResumableAsync(ws, _filePath, sessionId, fullHash,
                    _serverIp, _port, false, _bufferSize, _limiter, _cb, ct).ConfigureAwait(false);
            }
        }

        private async Task SendTextInternal(string text, CancellationToken ct)
        {
            using (var ws = await OpenAndAuthenticateAsync(ct).ConfigureAwait(false))
            {
                await ClientWire.SendTextAsync(ws, text, _cb, ct).ConfigureAwait(false);
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }
}
