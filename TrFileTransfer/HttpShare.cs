using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>
    /// Read-only HTTP share over a directory: any LAN browser (phones included) can
    /// list and download files. Built on TcpListener so it needs no HttpListener URL
    /// ACLs or admin rights. An optional access token (pairing mode reuses the pairing
    /// code) is exchanged once via the entry form for an HttpOnly cookie — listing and
    /// download links stay clean of credentials. The legacy "?t=..." query still
    /// authenticates (bookmarks, scripted clients). Navigation and download paths are
    /// sanitized and pinned under the shared root; files are always served as
    /// attachments except a small safe-inline allowlist (images/video/pdf/text — never
    /// HTML/SVG, which could script the page).
    /// </summary>
    public class HttpShareServer
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private string _rootDir;
        private string _rootFull;
        private string _token;
        private volatile bool _isRunning;

        // Access-code brute-force defence. The code may be short (a pairing code),
        // so a LAN peer must not be able to try unlimited guesses against the cheap
        // HTTP endpoint. Failures are counted per client IP; on success the count
        // resets. Locked-out attempts are answered 429 without touching the token.
        private readonly object _authLock = new object();
        private readonly Dictionary<string, int> _authFailures = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _authLockedUntil = new Dictionary<string, DateTime>();
        /// <summary>Wrong access codes allowed per client IP before a temporary lockout.</summary>
        public const int MaxAuthFailures = 10;
        /// <summary>How long a locked-out IP stays refused.</summary>
        public static readonly TimeSpan AuthLockoutDuration = TimeSpan.FromMinutes(10);

        // Connection admission: without a cap, an unauthenticated peer can open
        // thousands of idle connections and exhaust memory/handles (slowloris).
        private SemaphoreSlim _connLimit;
        /// <summary>Maximum requests being served at once.</summary>
        public const int MaxConcurrentConnections = 32;
        /// <summary>Per-connection idle deadline while reading the request head — a
        /// client that never finishes its headers is dropped instead of held open.</summary>
        public const int HeadReadTimeoutMs = 15000;

        /// <summary>Fired for lifecycle events and downloads (log-ready lines).</summary>
        public event Action<string> OnLog;

        public const int DefaultPort = 8090;

        public bool IsRunning { get { return _isRunning; } }
        public int Port { get; private set; }

        /// <summary>Hard cap for a single upload request body (4 GB).</summary>
        public const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;

        /// <summary>Hard cap for one uploaded FILE, enforced while writing rather than
        /// only against the whole-request Content-Length (a request may carry several
        /// parts, so the request cap alone does not bound a single file).</summary>
        public const long MaxUploadFileBytes = 2L * 1024 * 1024 * 1024;

        /// <summary>MIME types served inline (browser plays/preview); everything else is
        /// sent as an attachment. HTML/SVG are deliberately absent — inline markup could
        /// script the listing page and read the access token from its links.</summary>
        private static readonly Dictionary<string, string> InlineTypes = new Dictionary<string, string>
        {
            { ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" },
            { ".gif", "image/gif" }, { ".webp", "image/webp" }, { ".bmp", "image/bmp" },
            { ".mp4", "video/mp4" }, { ".mp3", "audio/mpeg" }, { ".wav", "audio/wav" },
            { ".pdf", "application/pdf" }, { ".txt", "text/plain; charset=utf-8" }
        };

        /// <summary>The best LAN-facing URL for browsers — first entry of
        /// LanAddresses() (RFC1918 private first — what phones actually reach).</summary>
        public string LanUrl
        {
            get { return "http://" + LanAddresses()[0] + ":" + Port + "/"; }
        }

        /// <summary>All non-loopback IPv4 addresses worth offering for the share URL,
        /// RFC1918 private ranges first (interface enumeration order kept within each
        /// group), loopback alone as the local-testing fallback when nothing else exists.</summary>
        public static List<string> LanAddresses()
        {
            var privateIps = new List<string>();
            var otherIps = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork ||
                            IPAddress.IsLoopback(addr.Address)) continue;
                        string ip = addr.Address.ToString();
                        if (ip.StartsWith("192.168.", StringComparison.Ordinal) ||
                            ip.StartsWith("10.", StringComparison.Ordinal) ||
                            IsRfc1918_172(ip))
                            privateIps.Add(ip);
                        else
                            otherIps.Add(ip);
                    }
                }
            }
            catch { }
            var all = new List<string>();
            all.AddRange(privateIps);
            all.AddRange(otherIps);
            if (all.Count == 0) all.Add("127.0.0.1");
            return all;
        }

        /// <summary>Builds a share URL for one of the LanAddresses() entries.</summary>
        public static string BuildLanUrl(string ip, int port)
        {
            return "http://" + ip + ":" + port.ToString(CultureInfo.InvariantCulture) + "/";
        }

        private static bool IsRfc1918_172(string ip)
        {
            // 172.16.0.0 – 172.31.255.255
            int second;
            string[] parts = ip.Split('.');
            return parts.Length == 4 && parts[0] == "172" &&
                int.TryParse(parts[1], out second) && second >= 16 && second <= 31;
        }

        /// <summary>Starts sharing rootDir. token = required access code (empty/null = open).</summary>
        public void Start(string rootDir, int port, string token)
        {
            Stop();
            _rootDir = rootDir;
            _rootFull = Path.GetFullPath(rootDir);
            if (!_rootFull.EndsWith(Path.DirectorySeparatorChar.ToString()))
                _rootFull += Path.DirectorySeparatorChar;
            _token = token ?? "";
            lock (_authLock)
            {
                _authFailures.Clear();
                _authLockedUntil.Clear();
            }
            _connLimit = new SemaphoreSlim(MaxConcurrentConnections, MaxConcurrentConnections);
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Port = port;
            _isRunning = true;
            var ct = _cts.Token;
            Task.Factory.StartNew(() => AcceptLoop(ct), ct,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Stop()
        {
            _isRunning = false;
            var cts = _cts;
            if (cts != null) cts.Cancel();
            var listener = _listener;
            if (listener != null)
            {
                try { listener.Stop(); } catch { }
            }
            _listener = null;
            _cts = null;
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                catch (SocketException) { if (ct.IsCancellationRequested) break; continue; }

                // Admission control: hold a slot for the whole request. When all slots
                // are busy, stop accepting (back-pressure) instead of piling up
                // unbounded fire-and-forget handlers.
                var limit = _connLimit;
                if (limit == null) { try { client.Close(); } catch { } continue; }
                try { await limit.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { try { client.Close(); } catch { } break; }
                var _ = Task.Run(async () =>
                {
                    try { await HandleClient(client, ct).ConfigureAwait(false); }
                    finally { try { limit.Release(); } catch { } }
                }, ct);
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken ct)
        {
            string peer = "";
            try
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 30000;
                peer = (client.Client.RemoteEndPoint as IPEndPoint != null)
                    ? ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString() : "?";

                using (client)
                using (NetworkStream ns = client.GetStream())
                {
                    // One buffered reader for the whole request: bytes read past the head
                    // (start of a POST body) must survive into the body parsing
                    var reader = new NetBufReader(ns);

                    // Idle deadline for the head only: a client that opens a connection
                    // and dribbles bytes must not hold the slot open indefinitely.
                    // (C# 5 forbids awaiting inside a catch, so the timeout is handled
                    // after the try with a flag.)
                    string head;
                    bool headTimedOut = false;
                    using (var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        headCts.CancelAfter(HeadReadTimeoutMs);
                        try
                        {
                            head = await reader.ReadHeadAsync(headCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            if (ct.IsCancellationRequested) return;
                            headTimedOut = true;
                            head = null;
                        }
                    }
                    if (headTimedOut)
                    {
                        try { await WriteSimpleAsync(ns, 408, "Request Timeout", ct).ConfigureAwait(false); }
                        catch { }
                        return;
                    }
                    if (head == null) return;
                    string requestLine = head.Length > 0 ? head : "";
                    int nl = requestLine.IndexOf("\r\n", StringComparison.Ordinal);
                    if (nl >= 0) requestLine = requestLine.Substring(0, nl);
                    var headers = ParseHeaders(head);

                    string method = requestLine.Length > 0 ? requestLine.Split(' ')[0] : "";
                    string rawPath = method.Length > 0 ? requestLine.Substring(method.Length).TrimStart(' ') : "";
                    int sp = rawPath.IndexOf(' ');
                    if (sp >= 0) rawPath = rawPath.Substring(0, sp);
                    if (rawPath.Length == 0 || rawPath[0] != '/') rawPath = "/" + rawPath;

                    string path = rawPath, query = "";
                    int q = rawPath.IndexOf('?');
                    if (q >= 0) { path = rawPath.Substring(0, q); query = rawPath.Substring(q + 1); }
                    var args = ParseQuery(query);
                    string tArg;
                    args.TryGetValue("t", out tArg);

                    // Token gate: a valid session = cookie t=<token> (set on the code
                    // form's first success) or the legacy ?t=<token> query (kept for
                    // bookmarks and scripted clients). Everything else gets the form.
                    if (_token.Length > 0)
                    {
                        // A locked-out IP is refused before any token comparison, so a
                        // brute-force loop cannot keep guessing (it gets 429, not a hint).
                        if (IsAuthLockedOut(peer))
                        {
                            await WriteBytesAsync(ns, "429 Too Many Requests", "text/plain; charset=utf-8",
                                Encoding.UTF8.GetBytes("Too many wrong codes. Try again later.\r\n"), ct).ConfigureAwait(false);
                            return;
                        }

                        bool cookieOk = TokenEquals(TokenFromCookie(headers));
                        bool queryOk = TokenEquals(tArg);
                        if (!cookieOk && !queryOk)
                        {
                            // Drain the request body first — closing mid-upload resets
                            // the connection under the client before it reads the reply
                            string clenGate;
                            if (method == "POST" && headers.TryGetValue("Content-Length", out clenGate))
                            {
                                long bodyLen;
                                if (long.TryParse(clenGate, out bodyLen) && bodyLen > 0 && bodyLen <= MaxUploadBytes)
                                    await reader.DiscardAsync(bodyLen, ct).ConfigureAwait(false);
                            }
                            // Only a real attempt (a token was actually presented) counts
                            // as a failure; a plain first visit stays clean.
                            if (!string.IsNullOrEmpty(tArg) || !string.IsNullOrEmpty(TokenFromCookie(headers)))
                                NoteAuthFailure(peer);
                            // Only a real attempt (a t= parameter was present) gets the
                            // red "wrong code" hint — first visits stay clean
                            await WriteBytesAsync(ns, "200 OK", "text/html; charset=utf-8",
                                BuildTokenForm(tArg != null), ct).ConfigureAwait(false);
                            return;
                        }
                        NoteAuthSuccess(peer);
                        if (queryOk && !cookieOk && method == "GET")
                        {
                            // Valid code just submitted (the form is a GET) — park it in
                            // a cookie and bounce to the same page without the token
                            // riding the URL. POSTs fall through: a redirect would eat
                            // the multipart body, and scripted uploads keep ?t= working.
                            await WriteRedirectAsync(ns, BuildTokenlessPath(path, query), _token, ct).ConfigureAwait(false);
                            return;
                        }
                    }

                    if (method == "POST")
                    {
                        await HandleUploadAsync(ns, reader, headers, query, peer, ct).ConfigureAwait(false);
                        return;
                    }

                    if (method != "GET")
                    {
                        await WriteSimpleAsync(ns, 405, "Method Not Allowed", ct).ConfigureAwait(false);
                        return;
                    }

                    string fArg;
                    if (args.TryGetValue("f", out fArg))
                    {
                        await ServeFileAsync(ns, fArg, peer, headers, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        string pArg;
                        args.TryGetValue("p", out pArg);
                        await ServeListingAsync(ns, pArg, query, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                // Surface handler bugs instead of silently dropping the connection
                var handler = OnLog;
                if (handler != null && !ct.IsCancellationRequested)
                    handler("[HTTP] handler error: " + ex);
            }
        }

        /// <summary>Buffers the network stream so pattern scans never lose bytes that
        /// arrive past a match — essential for multipart bodies.</summary>
        private sealed class NetBufReader
        {
            private readonly NetworkStream _ns;
            internal readonly byte[] _buf = new byte[65536];
            internal int _start;
            internal int _end;

            public NetBufReader(NetworkStream ns) { _ns = ns; }

            /// <summary>Reads the request head (all header lines, no trailing blank
            /// line). Null on premature close.</summary>
            public async Task<string> ReadHeadAsync(CancellationToken ct)
            {
                var head = new MemoryStream();
                byte[] crlfcrlf = { 13, 10, 13, 10 };
                if (!await ReadUntilAsync(this, crlfcrlf, head, 65536, ct).ConfigureAwait(false))
                    return null;
                string all = Encoding.UTF8.GetString(head.ToArray());
                int idx = all.LastIndexOf("\r\n\r\n", StringComparison.Ordinal);
                return idx >= 0 ? all.Substring(0, idx) : all;
            }

            /// <summary>Fills the buffer (compacting first); returns bytes available.</summary>
            public async Task<int> FillAsync(CancellationToken ct)
            {
                if (_start > 0)
                {
                    Array.Copy(_buf, _start, _buf, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }
                if (_end == _buf.Length) return _end - _start;
                int n = await _ns.ReadAsync(_buf, _end, _buf.Length - _end, ct).ConfigureAwait(false);
                _end += n;
                return _end - _start;
            }

            /// <summary>Reads exactly count bytes (from buffer or network).</summary>
            public async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
            {
                var outBuf = new byte[count];
                int got = 0;
                while (got < count)
                {
                    int avail = _end - _start;
                    if (avail == 0)
                    {
                        if (await FillAsync(ct).ConfigureAwait(false) == 0)
                            throw new IOException("connection closed mid-body");
                        continue;
                    }
                    int take = Math.Min(count - got, avail);
                    Buffer.BlockCopy(_buf, _start, outBuf, got, take);
                    _start += take;
                    got += take;
                }
                return outBuf;
            }

            /// <summary>Consumes and discards count bytes (EOF tolerated).</summary>
            public async Task DiscardAsync(long count, CancellationToken ct)
            {
                while (count > 0)
                {
                    int avail = _end - _start;
                    if (avail == 0)
                    {
                        if (await FillAsync(ct).ConfigureAwait(false) == 0)
                            return;
                        continue;
                    }
                    int take = (int)Math.Min(count, avail);
                    _start += take;
                    count -= take;
                }
            }
        }

        private static int IndexOf(byte[] haystack, int start, int count, byte[] needle)
        {
            int last = start + count - needle.Length;
            for (int i = start; i <= last; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        /// <summary>Streams bytes until the byte pattern is found (pattern consumed, not
        /// written). Bytes before the match go to sink (null to discard). Leftover bytes
        /// after the pattern stay buffered in the reader. False on EOF before a match.</summary>
        private static async Task<bool> ReadUntilAsync(NetBufReader reader, byte[] pattern,
            Stream sink, long maxBytes, CancellationToken ct)
        {
            long total = 0;
            while (true)
            {
                int avail = reader._end - reader._start;
                int idx = IndexOf(reader._buf, reader._start, avail, pattern);
                if (idx >= 0)
                {
                    int before = idx - reader._start;
                    if (sink != null && before > 0)
                        await sink.WriteAsync(reader._buf, reader._start, before, ct).ConfigureAwait(false);
                    reader._start = idx + pattern.Length;
                    return true;
                }
                // Flush only the prefix that cannot contain a partial pattern
                int safe = avail - pattern.Length + 1;
                if (safe > 0)
                {
                    if (sink != null)
                        await sink.WriteAsync(reader._buf, reader._start, safe, ct).ConfigureAwait(false);
                    total += safe;
                    reader._start += safe;
                }
                if (maxBytes >= 0 && total > maxBytes)
                    throw new IOException("upload section exceeds size cap");
                if (await reader.FillAsync(ct).ConfigureAwait(false) == 0)
                    return false;
            }
        }

        private static Dictionary<string, string> ParseHeaders(string head)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = head.Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                result[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
            }
            return result;
        }

        /// <summary>Value of the "t" cookie from a Cookie header, or null.</summary>
        private static string TokenFromCookie(Dictionary<string, string> headers)
        {
            string cookies;
            if (!headers.TryGetValue("Cookie", out cookies)) return null;
            foreach (string part in cookies.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (part.Substring(0, eq).Trim() == "t")
                    return part.Substring(eq + 1).Trim();
            }
            return null;
        }

        /// <summary>Constant-time code comparison (null never matches).</summary>
        private bool TokenEquals(string candidate)
        {
            if (string.IsNullOrEmpty(candidate) || _token.Length == 0) return false;
            return Utils.ConstantTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(_token));
        }

        /// <summary>True when this peer IP has exhausted its wrong-code allowance.</summary>
        private bool IsAuthLockedOut(string peer)
        {
            if (string.IsNullOrEmpty(peer)) return false;
            lock (_authLock)
            {
                DateTime until;
                if (!_authLockedUntil.TryGetValue(peer, out until)) return false;
                if (DateTime.UtcNow >= until)
                {
                    _authLockedUntil.Remove(peer);
                    _authFailures.Remove(peer);
                    return false;
                }
                return true;
            }
        }

        private void NoteAuthFailure(string peer)
        {
            if (string.IsNullOrEmpty(peer)) return;
            lock (_authLock)
            {
                int n;
                _authFailures.TryGetValue(peer, out n);
                n++;
                _authFailures[peer] = n;
                if (n >= MaxAuthFailures)
                {
                    _authLockedUntil[peer] = DateTime.UtcNow + AuthLockoutDuration;
                    var handler = OnLog;
                    if (handler != null)
                        handler("[HTTP] " + peer + " locked out after " + n + " wrong access codes");
                }
            }
        }

        private void NoteAuthSuccess(string peer)
        {
            if (string.IsNullOrEmpty(peer)) return;
            lock (_authLock)
            {
                _authFailures.Remove(peer);
                _authLockedUntil.Remove(peer);
            }
        }

        /// <summary>The same path/query minus the t= parameter.</summary>
        private static string BuildTokenlessPath(string path, string query)
        {
            if (query.Length == 0) return path;
            var kept = new StringBuilder();
            string[] pairs = query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                if (pairs[i].Length == 0) continue;
                if (pairs[i] == "t" || pairs[i].StartsWith("t=", StringComparison.Ordinal)) continue;
                kept.Append(kept.Length > 0 ? "&" : "").Append(pairs[i]);
            }
            return kept.Length == 0 ? path : path + "?" + kept;
        }

        /// <summary>303 that also plants the access-code cookie (HttpOnly so page
        /// scripts can never read it; SameSite=Strict pins it to this share).</summary>
        private static async Task WriteRedirectAsync(NetworkStream ns, string location, string token, CancellationToken ct)
        {
            byte[] resp = Encoding.ASCII.GetBytes(
                "HTTP/1.1 303 See Other\r\nLocation: " + location +
                "\r\nSet-Cookie: t=" + token + "; Path=/; HttpOnly; SameSite=Strict" +
                "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await ns.WriteAsync(resp, 0, resp.Length, ct).ConfigureAwait(false);
        }

        // ---- Upload (multipart/form-data POST) ----

        /// <summary>
        /// Streams a multipart/form-data body to disk. Files land in the directory
        /// named by the "p" form field (share root when empty); names are reduced to
        /// a bare file name and uniquified. Responds 303 back to the listing.
        /// </summary>
        private async Task HandleUploadAsync(NetworkStream ns, NetBufReader reader,
            Dictionary<string, string> headers, string query, string peer, CancellationToken ct)
        {
            long contentLength;
            string clen;
            if (!headers.TryGetValue("Content-Length", out clen) ||
                !long.TryParse(clen, out contentLength) || contentLength <= 0)
            {
                await WriteSimpleAsync(ns, 400, "Bad Request", ct).ConfigureAwait(false);
                return;
            }
            if (contentLength > MaxUploadBytes)
            {
                await WriteSimpleAsync(ns, 413, "File Too Large", ct).ConfigureAwait(false);
                return;
            }
            string contentType;
            headers.TryGetValue("Content-Type", out contentType);
            string boundary = ExtractBoundary(contentType);
            if (boundary == null)
            {
                await WriteSimpleAsync(ns, 400, "Bad Request", ct).ConfigureAwait(false);
                return;
            }

            byte[] startDelim = Encoding.ASCII.GetBytes("--" + boundary);
            byte[] midDelim = Encoding.ASCII.GetBytes("\r\n--" + boundary);
            byte[] headEnd = { 13, 10, 13, 10 };

            string uploadRel = "";
            string tokenPart = "";
            var args = ParseQuery(query);
            string tq;
            if (args.TryGetValue("t", out tq)) tokenPart = "&t=" + Uri.EscapeDataString(tq);
            int files = 0;
            long filesBytes = 0;

            // Skip the preamble up to the first boundary
            if (!await ReadUntilAsync(reader, startDelim, null, 65536, ct).ConfigureAwait(false))
                throw new IOException("upload body ended before first boundary");

            while (true)
            {
                // After the boundary: "--" closes, otherwise CRLF starts a part
                byte[] two = await reader.ReadExactAsync(2, ct).ConfigureAwait(false);
                if (two[0] == '-' && two[1] == '-') break;

                var partHead = new MemoryStream();
                if (!await ReadUntilAsync(reader, headEnd, partHead, 32768, ct).ConfigureAwait(false))
                    throw new IOException("upload part headers truncated");
                string disposition = Encoding.UTF8.GetString(partHead.ToArray());

                string name = ExtractDispositionValue(disposition, "name");
                string filename = ExtractDispositionValue(disposition, "filename");

                if (string.IsNullOrEmpty(filename))
                {
                    // Form field (e.g. current directory "p")
                    var field = new MemoryStream();
                    if (!await ReadUntilAsync(reader, midDelim, field, 65536, ct).ConfigureAwait(false))
                        throw new IOException("upload field truncated");
                    if (name == "p")
                        uploadRel = Encoding.UTF8.GetString(field.ToArray()).Trim();
                    continue;
                }

                // Reduce to a bare file name and neutralize reserved device names
                // (CON/NUL/…) plus trailing dots/spaces — same rules the transfer
                // receivers apply; a crafted upload must never open a device.
                string bareName = Path.GetFileName(filename.Replace('/', '\\'));
                bareName = bareName.TrimEnd('.', ' ');
                if (Utils.IsReservedFileName(bareName))
                    bareName = "_" + bareName;
                if (string.IsNullOrWhiteSpace(bareName) || bareName == "_")
                {
                    // No usable name — stream the content to null and continue
                    if (!await ReadUntilAsync(reader, midDelim, null, contentLength, ct).ConfigureAwait(false))
                        throw new IOException("upload part truncated");
                    continue;
                }

                string dir = ResolveSafe(uploadRel);
                if (dir == null) dir = _rootFull;
                // Same pre-flight the file receivers use — fail with 507 instead of
                // dying mid-upload when the disk cannot hold the announced body
                if (!Utils.HasFreeSpaceFor(dir, contentLength))
                {
                    await WriteSimpleAsync(ns, 507, "Insufficient Storage", ct).ConfigureAwait(false);
                    return;
                }
                string savePath = Utils.GetUniqueSavePath(dir, bareName);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.SequentialScan))
                {
                    // Cap THIS file, not just the whole request: one part must not be
                    // able to stream up to the 4 GB request cap into a single file.
                    long perFile = Math.Min(MaxUploadFileBytes, contentLength);
                    if (!await ReadUntilAsync(reader, midDelim, fs, perFile, ct).ConfigureAwait(false))
                        throw new IOException("upload part truncated");
                }
                sw.Stop();
                if (filesBytes + new FileInfo(savePath).Length > MaxUploadBytes)
                {
                    // Aggregate request cap exceeded — undo this file and refuse.
                    try { File.Delete(savePath); } catch { }
                    await WriteSimpleAsync(ns, 413, "File Too Large", ct).ConfigureAwait(false);
                    return;
                }
                files++;
                long size = new FileInfo(savePath).Length;
                filesBytes += size;
                var handler = OnLog;
                if (handler != null)
                    handler("[HTTP] " + peer + " upload: " + bareName + " (" +
                        Utils.FormatSize(size) + ", " + sw.Elapsed.TotalSeconds.ToString("F1") + "s)");
            }

            var log = OnLog;
            if (log != null && files > 1)
                log("[HTTP] " + peer + " upload done: " + files + " file(s), " + Utils.FormatSize(filesBytes));

            // Back to the listing the uploader came from
            string loc = "/?p=" + Uri.EscapeDataString(uploadRel) + tokenPart;
            byte[] resp = Encoding.ASCII.GetBytes(
                "HTTP/1.1 303 See Other\r\nLocation: " + loc +
                "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await ns.WriteAsync(resp, 0, resp.Length, ct).ConfigureAwait(false);
        }

        /// <summary>Extracts boundary=... from a multipart Content-Type header; strips quotes.</summary>
        private static string ExtractBoundary(string contentType)
        {
            if (contentType == null) return null;
            int idx = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            string b = contentType.Substring(idx + 9).Trim();
            int end = b.IndexOf(';');
            if (end >= 0) b = b.Substring(0, end);
            b = b.Trim().Trim('"');
            return b.Length == 0 ? null : b;
        }

        /// <summary>Extracts name=/filename= from a Content-Disposition header line.
        /// No backslash unescaping: browsers send raw names (old IE sends full paths,
        /// where "\" is a separator to strip), and escaping would corrupt path stripping
        /// ("..\..\x" would become "....x"). The key must start at a token boundary —
        /// otherwise the "name=" search would match the tail of "filename=". Callers
        /// must still strip path segments.</summary>
        private static string ExtractDispositionValue(string partHead, string key)
        {
            int idx = 0;
            while (true)
            {
                idx = partHead.IndexOf(key + "=\"", idx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return "";
                if (idx > 0)
                {
                    char prev = partHead[idx - 1];
                    if (prev != ';' && prev != ',' && prev != ' ' && prev != '\t' && prev != '\r' && prev != '\n')
                    {
                        idx++; // matched inside another attribute name — keep looking
                        continue;
                    }
                }
                int start = idx + key.Length + 2;
                int end = partHead.IndexOf('"', start);
                if (end < 0) end = partHead.Length;
                return partHead.Substring(start, end - start);
            }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (query.Length == 0) return result;
            string[] pairs = query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                int eq = pairs[i].IndexOf('=');
                string key, val;
                if (eq < 0) { key = pairs[i]; val = ""; }
                else { key = pairs[i].Substring(0, eq); val = pairs[i].Substring(eq + 1); }
                try { key = Uri.UnescapeDataString(key); } catch { }
                try { val = Uri.UnescapeDataString(val.Replace("+", "%20")); } catch { }
                result[key] = val;
            }
            return result;
        }

        /// <summary>Resolves a client-supplied relative path inside the shared root.
        /// Returns null when the path escapes the root (traversal) or resolves nowhere.
        /// Empty resolves to the root itself — SanitizeRelativePath maps "" to "_",
        /// so it must be handled before sanitizing.</summary>
        private string ResolveSafe(string rawRel)
        {
            string raw = (rawRel ?? "").Trim();
            if (raw.Length == 0) return _rootFull;
            string rel = Utils.SanitizeRelativePath(raw);
            string full = Path.GetFullPath(Path.Combine(_rootFull, rel));
            if (!full.StartsWith(_rootFull, StringComparison.OrdinalIgnoreCase)) return null;
            return full;
        }

        private async Task ServeListingAsync(NetworkStream ns, string rawRel, string rawQuery, CancellationToken ct)
        {
            rawRel = rawRel ?? ""; // missing ?p= -> root listing
            string dir = ResolveSafe(rawRel);
            if (dir == null || !Directory.Exists(dir))
            {
                await WriteSimpleAsync(ns, 404, "Not Found", ct).ConfigureAwait(false);
                return;
            }

            var sb = new StringBuilder();
            string title = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (dir.TrimEnd(Path.DirectorySeparatorChar).Equals(_rootFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                title = Path.GetFileName(_rootFull.TrimEnd(Path.DirectorySeparatorChar));
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>").Append(HtmlEscape(title)).Append("</title><style>");
            sb.Append("body{font-family:system-ui,sans-serif;margin:0;background:#f3f4f6}");
            sb.Append("h2{padding:12px 16px;margin:0;background:#fff;border-bottom:1px solid #ddd;font-size:1.05rem}");
            sb.Append("ul{list-style:none;margin:8px;padding:0}");
            sb.Append("li{background:#fff;margin:6px 8px;padding:10px 12px;border-radius:8px;display:flex;justify-content:space-between;gap:8px}");
            sb.Append("a{color:#0078d7;text-decoration:none;word-break:break-all}span{color:#888;white-space:nowrap;font-size:.85rem;align-self:center}");
            sb.Append("</style></head><body><h2>").Append(HtmlEscape(title)).Append("</h2><ul>");

            string linkQuery = AppendToken(rawQuery);
            string relPrefix = rawRel.Length > 0 ? rawRel.TrimEnd('/') + "/" : "";

            var entries = new List<FileSystemInfo>();
            try { entries.AddRange(new DirectoryInfo(dir).GetFileSystemInfos()); } catch { }
            entries.Sort((a, b) =>
            {
                bool da = a is DirectoryInfo, db = b is DirectoryInfo;
                if (da != db) return da ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            foreach (var e in entries)
            {
                bool isDir = e is DirectoryInfo;
                string rel = relPrefix + e.Name;
                string href = isDir
                    ? "?p=" + Uri.EscapeDataString(rel) + linkQuery
                    : "?f=" + Uri.EscapeDataString(rel) + linkQuery;
                string size = isDir ? "&lt;dir&gt;" : Utils.FormatSize(((FileInfo)e).Length);
                sb.Append("<li><a href=\"").Append(HtmlEscape(href)).Append("\">")
                  .Append(isDir ? "&#128193; " : "&#128196; ").Append(HtmlEscape(e.Name))
                  .Append(isDir ? "/" : "").Append("</a><span>").Append(size).Append("</span></li>");
            }
            if (entries.Count == 0)
                sb.Append("<li><span>").Append(L.HttpShareEmpty).Append("</span></li>");
            sb.Append("</ul>");

            // Upload form — posts into the directory being viewed
            string formAction = linkQuery.Length > 0 ? "/?" + linkQuery.Substring(1) : "/";
            sb.Append("<form method=\"post\" action=\"" + HtmlEscape(formAction) +
                "\" enctype=\"multipart/form-data\" style=\"margin:10px 8px 24px;background:#fff;padding:12px;border-radius:8px;display:flex;gap:8px;flex-wrap:wrap\">");
            sb.Append("<input type=\"hidden\" name=\"p\" value=\"").Append(HtmlEscape(rawRel)).Append("\">");
            sb.Append("<input type=\"file\" name=\"file\" multiple required style=\"flex:1;min-width:200px;font-size:.95rem\">");
            sb.Append("<input type=\"submit\" value=\"").Append(L.HttpShareUploadBtn)
              .Append("\" style=\"padding:8px 16px;border:0;border-radius:8px;background:#0078d7;color:#fff;font-size:.95rem\">");
            sb.Append("</form>");

            sb.Append("<p style=\"text-align:center;color:#aaa;font-size:.75rem\">TrFileTransfer</p></body></html>");

            await WriteBytesAsync(ns, "200 OK", "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
        }

        /// <summary>Carries unrelated params over to listing links. The access token is
        /// deliberately NOT re-attached: the authenticated session lives in the HttpOnly
        /// cookie, and re-emitting t= in every href would leak the credential into
        /// browser history, Referer headers and shared links — defeating the cookie.</summary>
        private static string AppendToken(string rawQuery)
        {
            var kept = new StringBuilder();
            string[] pairs = rawQuery.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                if (pairs[i].Length == 0) continue;
                if (pairs[i].StartsWith("p=", StringComparison.Ordinal) ||
                    pairs[i].StartsWith("f=", StringComparison.Ordinal) ||
                    pairs[i] == "t" ||
                    pairs[i].StartsWith("t=", StringComparison.Ordinal)) continue;
                kept.Append("&").Append(pairs[i]);
            }
            return kept.ToString();
        }

        private async Task ServeFileAsync(NetworkStream ns, string rawRel, string peer,
            Dictionary<string, string> headers, CancellationToken ct)
        {
            string full = ResolveSafe(rawRel);
            if (full == null || !File.Exists(full))
            {
                await WriteSimpleAsync(ns, 404, "Not Found", ct).ConfigureAwait(false);
                return;
            }

            string ext = Path.GetExtension(full).ToLowerInvariant();
            string mime;
            bool inline = InlineTypes.TryGetValue(ext, out mime);
            if (!inline) mime = "application/octet-stream";

            var handler = OnLog;
            string name = Path.GetFileName(full);
            if (handler != null)
                handler("[HTTP] " + peer + " " + (inline ? "preview" : "download") + ": " + name);

            long total = new FileInfo(full).Length;
            long rangeStart = 0, rangeEnd = total - 1;
            bool isRange = false, rangeInvalid = false;
            string rangeHeader;
            if (headers.TryGetValue("Range", out rangeHeader) && total > 0)
            {
                // Single range only (browsers resume with one); "bytes=start-end",
                // "bytes=start-" (to EOF) or "bytes=-suffix" (last N bytes)
                string spec = rangeHeader.Trim();
                if (spec.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) spec = spec.Substring(6);
                int dash = spec.IndexOf('-');
                string first = dash >= 0 ? spec.Substring(0, dash).Trim() : spec.Trim();
                string second = dash >= 0 ? spec.Substring(dash + 1).Trim() : "";
                long start, end;
                if (first.Length == 0 && long.TryParse(second, out end) && end > 0)
                {
                    // suffix form: last end bytes
                    rangeStart = total > end ? total - end : 0;
                    rangeEnd = total - 1;
                    isRange = rangeStart <= rangeEnd;
                }
                else if (long.TryParse(first, out start) && start >= 0 && start < total)
                {
                    rangeStart = start;
                    if (second.Length == 0 || !long.TryParse(second, out end) || end >= total)
                        rangeEnd = total - 1;
                    else
                        rangeEnd = end;
                    isRange = rangeStart <= rangeEnd;
                }
                else
                {
                    rangeInvalid = true;
                }
            }

            if (rangeInvalid)
            {
                byte[] bad = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 416 Range Not Satisfiable\r\n" +
                    "Content-Range: bytes */" + total + "\r\n" +
                    "Content-Length: 0\r\nConnection: close\r\n\r\n");
                await ns.WriteAsync(bad, 0, bad.Length, ct).ConfigureAwait(false);
                return;
            }

            byte[] head;
            if (isRange)
            {
                head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 206 Partial Content\r\n" +
                    "Content-Type: " + mime + "\r\n" +
                    "Content-Length: " + (rangeEnd - rangeStart + 1) + "\r\n" +
                    "Content-Range: bytes " + rangeStart + "-" + rangeEnd + "/" + total + "\r\n" +
                    "Accept-Ranges: bytes\r\n" +
                    "Content-Disposition: " + (inline ? "inline" : "attachment") +
                    "; filename=\"" + SanitizeAsciiFallback(name) + "\"; filename*=UTF-8''" + Uri.EscapeDataString(name) + "\r\n" +
                    "Connection: close\r\n\r\n");
            }
            else
            {
                head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: " + mime + "\r\n" +
                    "Content-Length: " + total + "\r\n" +
                    "Accept-Ranges: bytes\r\n" +
                    "Content-Disposition: " + (inline ? "inline" : "attachment") +
                    "; filename=\"" + SanitizeAsciiFallback(name) + "\"; filename*=UTF-8''" + Uri.EscapeDataString(name) + "\r\n" +
                    "Connection: close\r\n\r\n");
            }
            await ns.WriteAsync(head, 0, head.Length, ct).ConfigureAwait(false);

            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.SequentialScan))
            {
                if (isRange) fs.Seek(rangeStart, SeekOrigin.Begin);
                long left = isRange ? rangeEnd - rangeStart + 1 : total;
                var buf = new byte[65536];
                int n;
                while (left > 0 && (n = await fs.ReadAsync(buf, 0, (int)Math.Min(buf.Length, left), ct).ConfigureAwait(false)) > 0)
                {
                    await ns.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                    left -= n;
                }
            }
        }

        /// <summary>ASCII fallback for the legacy filename parameter (quoted-string must
        /// not contain non-ASCII); browsers prefer the UTF-8 filename* form anyway.</summary>
        private static string SanitizeAsciiFallback(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name)
                sb.Append(c < 32 || c > 126 || c == '"' || c == '\\' ? '_' : c);
            return sb.Length == 0 ? "file" : sb.ToString();
        }

        private byte[] BuildTokenForm(bool wrongAttempt)
        {
            string hint = wrongAttempt ? "<p style=\"color:#c00\">" + L.HttpShareWrongToken + "</p>" : "";
            string html = "<!doctype html><html><head><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                "<title>TrFileTransfer</title></head><body style=\"font-family:system-ui,sans-serif;margin:0\">" +
                "<div style=\"max-width:340px;margin:12vh auto;background:#fff;padding:24px;border-radius:12px\">" +
                "<h2>TrFileTransfer</h2><p>" + L.HttpShareTokenPrompt + "</p>" + hint +
                "<form method=\"get\" action=\"/\"><input name=\"t\" style=\"width:100%;box-sizing:border-box;padding:10px;font-size:1rem\"" +
                " autofocus autocomplete=\"off\"><input type=\"submit\" value=\"" + L.HttpShareTokenSubmit +
                "\" style=\"margin-top:10px;width:100%;padding:10px;border:0;border-radius:8px;background:#0078d7;color:#fff;font-size:1rem\"></form>" +
                "</div></body></html>";
            return Encoding.UTF8.GetBytes(html);
        }

        private static async Task WriteBytesAsync(NetworkStream ns, string status, string contentType,
            byte[] body, CancellationToken ct)
        {
            byte[] head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + status + "\r\nContent-Type: " + contentType +
                "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n" +
                // This share renders attacker-influenced names; do not cache and do not
                // let another origin frame it (clickjacking on the upload form).
                "Cache-Control: no-store\r\nX-Frame-Options: DENY\r\n" +
                "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n\r\n");
            await ns.WriteAsync(head, 0, head.Length, ct).ConfigureAwait(false);
            await ns.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
        }

        private static Task WriteSimpleAsync(NetworkStream ns, int status, string text, CancellationToken ct)
        {
            return WriteBytesAsync(ns, status + " " + text, "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes(text), ct);
        }

        private static string HtmlEscape(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
