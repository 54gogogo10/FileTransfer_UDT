using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>Parsed update manifest — a simple key=value text document served over HTTP:</summary>
    /// <remarks>
    /// version=2.2.0.0
    /// url=http://192.168.1.10/releases/TrFileTransfer.exe
    /// sha256=64 lowercase hex chars
    /// notes=Fixes and speedups (optional)
    /// </remarks>
    #pragma warning disable 1591
    public class UpdateManifest
    {
        public Version Version;
        public string Url;
        public string Sha256Hex;
        public string Notes;

        /// <summary>Parses manifest text. Returns null when any required field is missing
        /// or malformed (bad version, non-http(s) URL, hash not 64 hex chars).</summary>
        public static UpdateManifest Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string version = null, url = null, sha = null, notes = null;
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim('\r', ' ', '\t');
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;
                string key = line.Substring(0, idx).Trim().ToLowerInvariant();
                string val = line.Substring(idx + 1).Trim();
                if (key == "version") version = val;
                else if (key == "url") url = val;
                else if (key == "sha256") sha = val.ToLowerInvariant();
                else if (key == "notes") notes = val;
            }
            if (version == null || url == null || sha == null) return null;

            Version v;
            try { v = new Version(version); }
            catch { return null; }
            if (!IsSecureUpdateUrl(url)) return null;
            if (!IsHex64(sha)) return null;

            return new UpdateManifest { Version = v, Url = url, Sha256Hex = sha, Notes = notes ?? "" };
        }

        /// <summary>True when the update URL is safe to trust: https anywhere, or plain
        /// http only for a loopback host (local testing). The SHA256 is fetched from the
        /// same origin as the binary, so over plain http a LAN attacker could serve both
        /// a malicious exe and its matching hash; requiring https for real hosts removes
        /// that whole class of attack.</summary>
        public static bool IsSecureUpdateUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            if (uri.Scheme == Uri.UriSchemeHttps) return true;
            if (uri.Scheme != Uri.UriSchemeHttp) return false;
            return IsLoopbackHost(uri.Host);
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            UriHostNameType t;
            System.Net.IPAddress addr;
            if (System.Net.IPAddress.TryParse(host, out addr))
                return System.Net.IPAddress.IsLoopback(addr);
            t = Uri.CheckHostName(host);
            return t == UriHostNameType.IPv6 && host == "::1";
        }

        private static bool IsHex64(string s)
        {
            if (s == null || s.Length != 64) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        public bool IsNewerThan(Version current)
        {
            return Version > current;
        }

        // ---- GitHub Releases support ----

        /// <summary>
        /// Builds a manifest from a GitHub API release JSON document (as returned by
        /// /releases/latest). The version comes from tag_name (a leading "v" is stripped),
        /// the download URL from the first asset whose browser_download_url ends in ".exe",
        /// and the release body becomes the notes. The SHA256 is NOT taken from the JSON —
        /// CheckGitHubAsync fetches it from the sidecar asset "&lt;exe url&gt;.sha256".
        /// Returns null when the document lacks tag_name or an .exe asset, or the tag is
        /// not a valid version.
        /// </summary>
        public static UpdateManifest FromGitHubJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string tag = ExtractJsonString(json, "tag_name");
            string exeUrl = null;
            int idx = 0;
            while (exeUrl == null)
            {
                idx = json.IndexOf("\"browser_download_url\"", idx, StringComparison.Ordinal);
                if (idx < 0) break;
                string candidate = ExtractJsonStringAt(json, idx);
                if (candidate != null && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    exeUrl = candidate;
                idx++;
            }
            if (tag == null || exeUrl == null) return null;
            // GitHub asset URLs are https; reject a downgraded one defensively
            if (!IsSecureUpdateUrl(exeUrl)) return null;

            tag = tag.TrimStart('v', 'V');
            Version v;
            try { v = new Version(tag); }
            catch { return null; }

            return new UpdateManifest
            {
                Version = v,
                Url = exeUrl,
                Sha256Hex = null, // filled by CheckGitHubAsync from the .sha256 sidecar
                Notes = ExtractJsonString(json, "body") ?? ""
            };
        }

        /// <summary>Extracts the JSON string value for the first occurrence of "key":
        /// key : "value". Understands the standard JSON escapes. Returns null if absent.</summary>
        private static string ExtractJsonString(string json, string key)
        {
            int idx = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (idx < 0) return null;
            return ExtractJsonStringAt(json, idx);
        }

        /// <summary>Reads the string value of the "key": "value" pair whose key starts
        /// at keyIdx. Returns null when the syntax does not match.</summary>
        private static string ExtractJsonStringAt(string json, int keyIdx)
        {
            int i = json.IndexOf(':', keyIdx);
            if (i < 0) return null;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\\')
                {
                    i++;
                    if (i >= json.Length) return null;
                    char e = json[i];
                    if (e == '"' || e == '\\' || e == '/') sb.Append(e);
                    else if (e == 'n') sb.Append('\n');
                    else if (e == 't') sb.Append('\t');
                    else if (e == 'r') sb.Append('\r');
                    else if (e == 'b') sb.Append('\b');
                    else if (e == 'f') sb.Append('\f');
                    else if (e == 'u')
                    {
                        if (i + 4 >= json.Length) return null;
                        int code;
                        if (!int.TryParse(json.Substring(i + 1, 4),
                            System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out code)) return null;
                        sb.Append((char)code);
                        i += 4;
                    }
                    else return null;
                    i++;
                }
                else if (c == '"')
                {
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return null;
        }
    }
    #pragma warning restore 1591

    /// <summary>
    /// Auto-update: check an HTTP manifest, download + SHA256-verify the new build,
    /// and swap it in for the running exe. The running exe is renamed to
    /// "&lt;exe&gt;.old" (Windows allows renaming a running image), the staged copy
    /// takes its place, and the new instance deletes the leftover backup on startup.
    /// </summary>
    public static class Updater
    {
        /// <summary>Staging directory under %AppData% (exe folder may be read-only).</summary>
        public static readonly string StagingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TrFileTransfer", "updates");

        /// <summary>Version of the currently running assembly.</summary>
        public static Version CurrentVersion
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version; }
        }

        /// <summary>
        /// Checks for updates from either a plain key=value manifest or a GitHub
        /// "/releases/latest" API URL (detected by host). GitHub releases get their
        /// SHA256 from the "&lt;exe url&gt;.sha256" sidecar asset published next to the exe.
        /// Throws on network errors or invalid content.
        /// </summary>
        public static Task<UpdateManifest> CheckAnyAsync(string url, int timeoutMs)
        {
            // Refuse to even talk to an insecure update source (plain http on a real
            // host): a MITM there controls both the manifest and the hash it names.
            if (!UpdateManifest.IsSecureUpdateUrl(url))
                throw new InvalidDataException(
                    "insecure update URL (use https, or http only for localhost): " + url);
            if (url.IndexOf("api.github.com", StringComparison.OrdinalIgnoreCase) >= 0)
                return CheckGitHubAsync(url, timeoutMs);
            return CheckAsync(url, timeoutMs);
        }

        /// <summary>
        /// Fetches a GitHub releases/latest document, builds the manifest from it and
        /// downloads the .sha256 sidecar for the exe asset. Throws (WebException /
        /// InvalidDataException) on network errors or when the sidecar hash is missing
        /// or malformed — a release without a valid sidecar can never be applied.
        /// </summary>
        public static Task<UpdateManifest> CheckGitHubAsync(string apiUrl, int timeoutMs)
        {
            return Task.Run(delegate
            {
                string json;
                using (var ms = new MemoryStream())
                {
                    HttpGetToStream(apiUrl, timeoutMs, null, ms);
                    json = Encoding.UTF8.GetString(ms.ToArray());
                }
                UpdateManifest m = UpdateManifest.FromGitHubJson(json);
                if (m == null)
                    throw new InvalidDataException("unsupported GitHub release at " + apiUrl +
                        " (need tag_name + .exe asset)");
                string sidecarText;
                using (var ms2 = new MemoryStream())
                {
                    HttpGetToStream(m.Url + ".sha256", timeoutMs, null, ms2);
                    sidecarText = Encoding.UTF8.GetString(ms2.ToArray());
                }
                string sha = FirstHex64Token(sidecarText);
                if (sha == null)
                    throw new InvalidDataException("missing or malformed .sha256 sidecar for " + m.Url);
                m.Sha256Hex = sha;
                return m;
            });
        }

        /// <summary>First whitespace-delimited token that is exactly 64 hex chars.</summary>
        private static string FirstHex64Token(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] tokens = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i].Trim().ToLowerInvariant();
                if (t.Length == 64)
                {
                    bool ok = true;
                    for (int j = 0; j < t.Length; j++)
                    {
                        char c = t[j];
                        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) { ok = false; break; }
                    }
                    if (ok) return t;
                }
            }
            return null;
        }

        /// <summary>Downloads and parses the manifest at manifestUrl.
        /// Throws (WebException / InvalidDataException) on network errors or invalid content.</summary>
        public static Task<UpdateManifest> CheckAsync(string manifestUrl, int timeoutMs)
        {
            return Task.Run(delegate
            {
                string text;
                using (var ms = new MemoryStream())
                {
                    HttpGetToStream(manifestUrl, timeoutMs, null, ms);
                    text = Encoding.UTF8.GetString(ms.ToArray());
                }
                UpdateManifest m = UpdateManifest.Parse(text);
                if (m == null)
                    throw new InvalidDataException("invalid update manifest: " + manifestUrl);
                return m;
            });
        }

        /// <summary>
        /// Downloads the update to destPath and verifies its SHA256 against the manifest.
        /// progress(bytesRead, totalBytes) fires on a background thread. Throws on network
        /// errors or hash mismatch; a partial or mismatching file is deleted before throwing.
        /// </summary>
        public static Task DownloadAsync(UpdateManifest manifest, string destPath,
            Action<long, long> progress, int timeoutMs)
        {
            if (!UpdateManifest.IsSecureUpdateUrl(manifest.Url))
                throw new InvalidDataException("insecure update asset URL: " + manifest.Url);
            return Task.Run(delegate
            {
                string dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // Unique per download: two app instances updating at once must not
                // fight over one staging file (truncation / wrong binary applied).
                string tmp = destPath + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        HttpGetToStream(manifest.Url, timeoutMs, progress, fs);
                    }
                    string actual = ComputeSha256Hex(tmp);
                    if (!string.Equals(actual, manifest.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("sha256 mismatch for " + manifest.Url);
                    if (File.Exists(destPath)) File.Delete(destPath);
                    File.Move(tmp, destPath);
                }
                catch
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    throw;
                }
            });
        }

        /// <summary>Hard cap for anything buffered in memory (manifests, GitHub API
        /// documents) — a hostile update URL must not be able to OOM the process.</summary>
        private const long MaxMetaBytes = 8L * 1024 * 1024;

        private static void HttpGetToStream(string url, int timeoutMs, Action<long, long> progress, Stream dest)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            // api.github.com rejects requests without a User-Agent
            req.UserAgent = "TrFileTransfer-Updater/" + CurrentVersion;
            // LAN tool — bypass system/IE proxy settings, which can break direct intranet URLs
            req.Proxy = null;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                if (resp.StatusCode != HttpStatusCode.OK)
                    throw new WebException("HTTP " + (int)resp.StatusCode + " for " + url);
                long total = resp.ContentLength;
                using (Stream src = resp.GetResponseStream())
                {
                    byte[] buf = new byte[81920];
                    long read = 0;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dest.Write(buf, 0, n);
                        read += n;
                        // Only metadata callers pass a null progress callback; the exe
                        // download (streamed to disk, hash-verified) is unbounded
                        if (progress == null && read > MaxMetaBytes)
                            throw new InvalidDataException("update document exceeds " + MaxMetaBytes + " bytes: " + url);
                        if (progress != null) progress(read, total);
                    }
                }
            }
        }

        /// <summary>
        /// Swaps targetPath for the staged file: target is renamed to "&lt;path&gt;.old"
        /// (a running exe may be renamed), the staged copy takes its place, and on copy
        /// failure the backup is restored. The caller then restarts the app; the new
        /// instance removes the leftover backup via DeleteStaleBackup.
        /// </summary>
        public static void Apply(string stagedPath, string targetPath)
        {
            Apply(stagedPath, targetPath, null);
        }

        /// <summary>Same as Apply, but re-verifies the staged file's SHA256 immediately
        /// before swapping it in. Closes the window between download-time verification
        /// and apply: another local process could otherwise replace the staged exe in
        /// between. Pass null to skip (kept for callers that already verified).</summary>
        public static void Apply(string stagedPath, string targetPath, string expectedSha256Hex)
        {
            if (!string.IsNullOrEmpty(expectedSha256Hex))
            {
                string actual = ComputeSha256Hex(stagedPath);
                if (!string.Equals(actual, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("staged update no longer matches its hash");
            }

            string backup = targetPath + ".old";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(targetPath, backup);
            try
            {
                File.Copy(stagedPath, targetPath, true);
            }
            catch
            {
                // Roll back so the app keeps running on the old binary
                try
                {
                    if (File.Exists(targetPath)) File.Delete(targetPath);
                    File.Move(backup, targetPath);
                }
                catch { }
                throw;
            }
        }

        /// <summary>Removes the leftover exe backup from a previous update; safe at every startup.</summary>
        public static void DeleteStaleBackup(string exePath)
        {
            try
            {
                string backup = exePath + ".old";
                if (File.Exists(backup)) File.Delete(backup);
            }
            catch { }
        }

        /// <summary>SHA256 of a file as lowercase hex.</summary>
        public static string ComputeSha256Hex(string path)
        {
            using (var sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
