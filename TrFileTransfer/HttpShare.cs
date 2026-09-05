using System;
using System.Collections.Generic;
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
    /// code) rides in every link as "?t=..." — without it only the token entry form
    /// is served. Navigation and download paths are sanitized and pinned under the
    /// shared root; files are always served as attachments except a small safe-inline
    /// allowlist (images/video/pdf/text — never HTML/SVG, which could script the page).
    /// </summary>
    public class HttpShareServer
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private string _rootDir;
        private string _rootFull;
        private string _token;
        private volatile bool _isRunning;

        /// <summary>Fired for lifecycle events and downloads (log-ready lines).</summary>
        public event Action<string> OnLog;

        public const int DefaultPort = 8090;

        public bool IsRunning { get { return _isRunning; } }
        public int Port { get; private set; }

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

        /// <summary>The best LAN-facing URL for browsers — prefers RFC1918 private
        /// addresses (what phones actually reach), falls back to the first non-loopback
        /// IPv4, then loopback for local testing.</summary>
        public string LanUrl
        {
            get
            {
                string firstAny = null;
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
                            if (firstAny == null) firstAny = ip;
                            if (ip.StartsWith("192.168.", StringComparison.Ordinal) ||
                                ip.StartsWith("10.", StringComparison.Ordinal) ||
                                IsRfc1918_172(ip))
                                return "http://" + ip + ":" + Port + "/";
                        }
                    }
                }
                catch { }
                if (firstAny != null) return "http://" + firstAny + ":" + Port + "/";
                return "http://127.0.0.1:" + Port + "/";
            }
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
                var _ = Task.Run(() => HandleClient(client, ct), ct);
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
                    string requestLine = await ReadRequestHeadAsync(ns, ct).ConfigureAwait(false);
                    if (requestLine == null || !requestLine.StartsWith("GET ", StringComparison.Ordinal))
                    {
                        await WriteSimpleAsync(ns, 405, "Method Not Allowed", ct).ConfigureAwait(false);
                        return;
                    }
                    // "/?t=x&p=y HTTP/1.1" -> "/?t=x&p=y"
                    string rawPath = requestLine.Substring(4).TrimEnd(' ');
                    int sp = rawPath.IndexOf(' ');
                    if (sp >= 0) rawPath = rawPath.Substring(0, sp);
                    if (rawPath.Length == 0 || rawPath[0] != '/') rawPath = "/" + rawPath;

                    string path = rawPath, query = "";
                    int q = rawPath.IndexOf('?');
                    if (q >= 0) { path = rawPath.Substring(0, q); query = rawPath.Substring(q + 1); }
                    var args = ParseQuery(query);
                    string tArg;
                    args.TryGetValue("t", out tArg);

                    // Token gate: every real response requires ?t=<token> when set
                    if (_token.Length > 0 && tArg != _token)
                    {
                        await WriteBytesAsync(ns, "200 OK", "text/html; charset=utf-8",
                            BuildTokenForm(tArg == ""), ct).ConfigureAwait(false);
                        return;
                    }

                    string fArg;
                    if (args.TryGetValue("f", out fArg))
                    {
                        await ServeFileAsync(ns, fArg, peer, ct).ConfigureAwait(false);
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

        /// <summary>Reads up to the blank line that ends the request head; returns the
        /// request line (first line). Null on premature close.</summary>
        private static async Task<string> ReadRequestHeadAsync(NetworkStream ns, CancellationToken ct)
        {
            var head = new byte[16384];
            int total = 0;
            while (total < head.Length)
            {
                int n = await ns.ReadAsync(head, total, head.Length - total, ct).ConfigureAwait(false);
                if (n <= 0) return null;
                total += n;
                string s = Encoding.ASCII.GetString(head, 0, total);
                int idx = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    int nl = s.IndexOf("\r\n", StringComparison.Ordinal);
                    return nl > 0 ? s.Substring(0, nl) : null;
                }
            }
            return null;
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
            sb.Append("</ul><p style=\"text-align:center;color:#aaa;font-size:.75rem\">TrFileTransfer</p></body></html>");

            await WriteBytesAsync(ns, "200 OK", "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
        }

        /// <summary>Re-attaches the token (plus any unrelated params) to listing links.</summary>
        private static string AppendToken(string rawQuery)
        {
            var kept = new StringBuilder();
            string[] pairs = rawQuery.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                if (pairs[i].Length == 0) continue;
                if (pairs[i].StartsWith("p=", StringComparison.Ordinal) ||
                    pairs[i].StartsWith("f=", StringComparison.Ordinal)) continue;
                kept.Append("&").Append(pairs[i]);
            }
            return kept.ToString();
        }

        private async Task ServeFileAsync(NetworkStream ns, string rawRel, string peer, CancellationToken ct)
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

            byte[] head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: " + mime + "\r\n" +
                "Content-Length: " + new FileInfo(full).Length + "\r\n" +
                "Content-Disposition: " + (inline ? "inline" : "attachment") +
                "; filename=\"" + SanitizeAsciiFallback(name) + "\"; filename*=UTF-8''" + Uri.EscapeDataString(name) + "\r\n" +
                "Connection: close\r\n\r\n");
            await ns.WriteAsync(head, 0, head.Length, ct).ConfigureAwait(false);

            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.SequentialScan))
            {
                var buf = new byte[65536];
                int n;
                while ((n = await fs.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                    await ns.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
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
                "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
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
