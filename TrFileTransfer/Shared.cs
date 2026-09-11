using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;

namespace TrFileTransfer
{
    /// <summary>Progress snapshot emitted periodically during a transfer.</summary>
    public struct TransferProgress
    {
        /// <summary>Bytes transferred so far.</summary>
        public long BytesTransferred { get; set; }
        /// <summary>Total bytes to transfer.</summary>
        public long TotalBytes { get; set; }
        /// <summary>Current transfer speed in bytes per second.</summary>
        public double SpeedBytesPerSecond { get; set; }
        /// <summary>Elapsed time since the transfer started.</summary>
        public TimeSpan Elapsed { get; set; }
        /// <summary>Name of the file or folder being transferred.</summary>
        public string FileName { get; set; }
    }

    /// <summary>Pre-computed file metadata for folder transfers.</summary>
    public struct FileEntry
    {
        /// <summary>Absolute path to the file on disk.</summary>
        public string Path;
        /// <summary>File size in bytes.</summary>
        public long Size;
        /// <summary>Relative path within the folder (used by receiver to recreate structure).</summary>
        public string RelativePath;
    }

    /// <summary>Tracks received chunks for concurrent file reassembly.</summary>
    #pragma warning disable 1591
    public class ChunkTracker
    {
        public string FileName;
        public long TotalSize;
        public string SavePath;
        public FileStream WriteStream;
        public long BytesReceived;
        public int ChunksCompleted;
        public readonly object Lock = new object();
        public bool Complete;
        // Received byte ranges, kept merged and sorted. Completion is decided by real
        // COVERAGE, not by summing chunk sizes: overlapping or duplicate chunks used to
        // inflate BytesReceived past TotalSize and report a file with holes as complete.
        private readonly List<long[]> _ranges = new List<long[]>();

        public void Dispose()
        {
            lock (Lock)
            {
                Complete = true;
                try { if (WriteStream != null) { WriteStream.Dispose(); WriteStream = null; } } catch { }
            }
        }

        /// <summary>Gets or creates a tracker, deferring FileStream creation.
        /// The dictionary key is separate from the display name so callers can
        /// namespace it (per-peer isolation for concurrent same-named files).</summary>
        public static ChunkTracker GetOrCreate(
            System.Collections.Concurrent.ConcurrentDictionary<string, ChunkTracker> dict,
            string key, string fileName, long totalSize, string saveDirectory)
        {
            ChunkTracker tracker;
            if (!dict.TryGetValue(key, out tracker))
            {
                var newTracker = new ChunkTracker
                {
                    FileName = fileName,
                    TotalSize = totalSize,
                    SavePath = Utils.GetUniqueSavePath(saveDirectory, fileName)
                };
                tracker = dict.GetOrAdd(key, newTracker);
            }
            return tracker;
        }

        /// <summary>Writes chunk data and checks completion. Returns true only when the
        /// received ranges actually COVER [0, TotalSize) — a sum of chunk sizes is not
        /// enough, because duplicate/overlapping chunks inflate the sum without filling
        /// the file, and a gap would be reported as a complete file.</summary>
        public bool WriteChunk(long chunkOffset, byte[] data, int bufferSize)
        {
            bool isComplete = false;
            lock (Lock)
            {
                if (Complete) return false;
                if (WriteStream == null)
                {
                    WriteStream = new FileStream(SavePath, FileMode.Create,
                        FileAccess.Write, FileShare.None, bufferSize, FileOptions.RandomAccess);
                    WriteStream.SetLength(TotalSize);
                }
                WriteStream.Seek(chunkOffset, SeekOrigin.Begin);
                WriteStream.Write(data, 0, bufferSize);
                BytesReceived += bufferSize;
                ChunksCompleted++;

                AddRange(chunkOffset, chunkOffset + bufferSize);
                if (!Complete && CoversAll())
                {
                    Complete = true;
                    isComplete = true;
                }
            }
            return isComplete;
        }

        /// <summary>True when the merged ranges cover exactly [0, TotalSize).</summary>
        private bool CoversAll()
        {
            if (TotalSize <= 0) return false;
            // Ranges are merged+sorted on insert, so full coverage is a single range
            return _ranges.Count == 1 && _ranges[0][0] <= 0 && _ranges[0][1] >= TotalSize;
        }

        /// <summary>Merges [start,end) into the sorted, non-overlapping range list —
        /// keeps the list tiny (a handful of entries for an out-of-order transfer).</summary>
        private void AddRange(long start, long end)
        {
            if (start < 0) start = 0;
            if (end <= start) return;

            // Ignore the part beyond the declared file size
            if (end > TotalSize) end = TotalSize;
            if (end <= start) return;

            int i = 0;
            while (i < _ranges.Count && _ranges[i][1] < start) i++;
            int lo = i;
            int hi = i;
            while (hi < _ranges.Count && _ranges[hi][0] <= end)
            {
                if (_ranges[hi][0] < start) start = _ranges[hi][0];
                if (_ranges[hi][1] > end) end = _ranges[hi][1];
                hi++;
            }
            if (hi > lo) _ranges.RemoveRange(lo, hi - lo);
            _ranges.Insert(lo, new long[] { start, end });
        }
    }
    #pragma warning restore 1591

        /// <summary>Token-bucket rate limiter for throttling transfer throughput.
        /// Thread-safe: a single instance may pace several concurrent connections
        /// (receive-side shaping shares one bucket across all clients).</summary>
        public class SpeedLimiter
        {
            private readonly long _maxBytesPerSec;
            private long _totalSent;
            private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

            /// <param name="maxBytesPerSec">0 = unlimited.</param>
            public SpeedLimiter(long maxBytesPerSec)
            {
                _maxBytesPerSec = maxBytesPerSec;
            }

            /// <summary>Records a chunk of bytes sent and delays as needed to stay within
            /// the limit. Asynchronous so throttled sends never block a thread-pool thread
            /// (a 4 MB chunk at a low limit can mean seconds of waiting).</summary>
            public async System.Threading.Tasks.Task ThrottleAsync(int bytes, System.Threading.CancellationToken ct)
            {
                if (_maxBytesPerSec <= 0) return;
                long total = System.Threading.Interlocked.Add(ref _totalSent, bytes);
                double expectedSeconds = (double)total / _maxBytesPerSec;
                double elapsed = _sw.Elapsed.TotalSeconds;
                double deficit = expectedSeconds - elapsed;
                if (deficit > 0.002)
                {
                    int ms = (int)(deficit * 1000.0);
                    if (ms > 0)
                        await System.Threading.Tasks.Task.Delay(ms, ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Thrown when a client cannot bind its requested local source port. Raised
        /// before any data is sent, so callers may safely retry with another port.
        /// </summary>
        public class PortBindException : IOException
        {
            public int Port { get; private set; }

            public PortBindException(string message, Exception inner, int port)
                : base(message, inner)
            {
                Port = port;
            }
        }

        /// <summary>
        /// Single construction point for transfer clients coming from the UI, so the
        /// constructor-argument shape is covered by the integration tests too.
        /// </summary>
        public static class ClientFactory
        {
            public const int ClientBufferSize = 4194304;

            /// <summary>Sends hash the source file/folder through the shared digest cache,
            /// so its Config policy (byte-for-byte mode, entry lifetime) is picked up here —
            /// every send goes through this factory.</summary>
            private static void ApplyHashCacheConfig()
            {
                FileHashCache.ForClient.ApplyConfig();
            }

            public static TransferClient CreateTcp(string serverIp, int port, string filePath, int srcPort, int speedLimit, string pairingCode = null)
            {
                ApplyHashCacheConfig();
                var client = new TransferClient(serverIp, port, filePath, srcPort, ClientBufferSize, speedLimit);
                client.PairingCode = pairingCode;
                return client;
            }

            public static TransferUdtClient CreateUdt(string serverIp, int port, string filePath, int srcPort, int speedLimit, string pairingCode = null)
            {
                ApplyHashCacheConfig();
                var client = new TransferUdtClient(serverIp, port, filePath, srcPort, ClientBufferSize, speedLimit);
                client.PairingCode = pairingCode;
                return client;
            }
        }

        /// <summary>
        /// HKCU Run-key auto start — survives reboots without admin rights. The exe path
        /// is passed explicitly so this class carries no WinForms dependency.
        /// </summary>
        public static class AutoStart
        {
            private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            private const string ValueName = "TrFileTransfer";

            /// <summary>Whether a TrFileTransfer value exists under HKCU ...\Run.</summary>
            public static bool IsEnabled()
            {
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                        return key != null && key.GetValue(ValueName) != null;
                }
                catch { return false; }
            }

            /// <summary>Registers (enable=true, value = quoted exePath) or removes the entry.
            /// Setting again with a new path overwrites; disabling when absent is a no-op.
            /// CreateSubKey so a missing Run key (fresh profile) is created, not skipped.</summary>
            public static void Set(bool enable, string exePath)
            {
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (enable)
                            key.SetValue(ValueName, "\"" + exePath + "\"");
                        else if (key.GetValue(ValueName) != null)
                            key.DeleteValue(ValueName);
                    }
                }
                catch { }
            }
        }

        /// <summary>General-purpose utility helpers.</summary>
    public static class Utils
    {
        /// <summary>Reusable empty byte array (avoids per-call allocations).</summary>
        public static readonly byte[] EmptyBytes = new byte[0];

        /// <summary>Formats a byte count into a human-readable string (e.g. "15.3 MB").</summary>
        public static string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int idx = 0;
            double size = bytes;
            while (size >= 1024 && idx < suffixes.Length - 1)
            {
                size /= 1024;
                idx++;
            }
            return string.Format("{0:F1} {1}", size, suffixes[idx]);
        }

        /// <summary>Timing-safe byte array comparison. Used for SHA256 hash verification.</summary>
        public static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>Fires a log event with a timestamp prefix, if the handler is non-null.</summary>
        public static void LogTo(Action<string> handler, string msg)
        {
            if (handler != null)
                handler(string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, msg));
        }

        /// <summary>Sanitizes a relative file path by replacing ".." and "." segments to prevent directory traversal.</summary>
        public static string SanitizeRelativePath(string path)
        {
            path = path.Replace('\\', '/').TrimStart('/');
            var parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == ".." || parts[i] == ".")
                    parts[i] = "_";
                if (string.IsNullOrWhiteSpace(parts[i]))
                    parts[i] = "_";
                // A colon would make "C:\evil" a rooted path (Path.Combine returns a
                // rooted second argument verbatim) or address an NTFS alternate
                // data stream ("file.txt:hidden") — neither may come from the wire.
                parts[i] = parts[i].Replace(':', '_');
                // Windows strips trailing dots/spaces on create, which desyncs the
                // collision check from the file that actually lands on disk
                parts[i] = parts[i].TrimEnd('.', ' ');
                if (parts[i].Length == 0)
                    parts[i] = "_";
                else if (IsReservedFileName(parts[i]))
                    parts[i] = "_" + parts[i];
            }
            return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        /// <summary>Whether the file name (any extension) collides with a Windows
        /// reserved device name (CON, NUL, COM1…). Those resolve to devices instead
        /// of disk files, so a crafted peer name must never reach FileStream.</summary>
        public static bool IsReservedFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string baseName = name;
            int dot = name.IndexOf('.');
            if (dot >= 0) baseName = name.Substring(0, dot);
            string upper = baseName.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL")
                return true;
            if (upper.Length == 4 && (upper.StartsWith("COM") || upper.StartsWith("LPT")))
            {
                char c = upper[3];
                return c >= '1' && c <= '9';
            }
            return false;
        }

        /// <summary>Finds a free port starting from basePort, scanning upward.</summary>
        public static int FindFreePort(int basePort, bool isUdp = false)
        {
            for (int port = basePort; port < basePort + 128; port++)
            {
                try
                {
                    if (isUdp)
                    {
                        var udp = new UdpClient(port);
                        udp.Close();
                        return port;
                    }
                    else
                    {
                        var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
                        listener.Start();
                        listener.Stop();
                        return port;
                    }
                }
                catch { }
            }
            return 0; // fallback: let OS assign ephemeral port
        }

        /// <summary>Whether the port can currently be bound, probing TCP and/or UDP as requested.</summary>
        public static bool IsPortFree(int port, bool tcp, bool udp)
        {
            if (tcp)
            {
                try
                {
                    var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                }
                catch { return false; }
            }
            if (udp)
            {
                try
                {
                    var probe = new UdpClient(port);
                    probe.Close();
                }
                catch { return false; }
            }
            return true;
        }

        /// <summary>First free port scanning upward from start (inclusive); 0 when none in range.</summary>
        public static int FindFreePortFrom(int start, bool tcp, bool udp)
        {
            for (int p = start; p < start + 128; p++)
            {
                if (IsPortFree(p, tcp, udp)) return p;
            }
            return 0;
        }

        /// <summary>Deletes a file, retrying briefly: on Windows an antivirus or the search
        /// indexer can hold a freshly written file open for a few milliseconds, and a plain
        /// File.Delete then fails with a sharing violation — which silently left files that
        /// the caller had already reported as discarded. Returns true when it is gone.</summary>
        public static bool DeleteWithRetry(string path, int attempts = 3)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    File.Delete(path);
                    return !File.Exists(path);
                }
                catch (IOException)
                {
                    if (i >= attempts) return !File.Exists(path);
                }
                catch (UnauthorizedAccessException)
                {
                    if (i >= attempts) return !File.Exists(path);
                }
                System.Threading.Thread.Sleep(150);
            }
        }

        /// <summary>Returns a unique file/directory path by appending _1, _2, etc. when collisions exist.</summary>
        public static string GetUniqueSavePath(string directory, string name)
        {
            string savePath = Path.Combine(directory, name);
            int counter = 1;
            string baseName = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            while (File.Exists(savePath) || Directory.Exists(savePath))
            {
                savePath = Path.Combine(directory,
                    string.Format("{0}_{1}{2}", baseName, counter, ext));
                counter++;
            }
            return savePath;
        }

        /// <summary>Headroom required on top of each incoming file (write cache, metadata, safety).</summary>
        public const long DiskSpaceMargin = 64 * 1024 * 1024;

        /// <summary>Largest file size a peer header may declare. Far beyond any real
        /// single file, and low enough that fileSize + DiskSpaceMargin cannot overflow
        /// a signed 64-bit value (which would silently defeat the free-space check).</summary>
        public const long MaxTransferSize = 1L << 50; // 1 PiB

        /// <summary>Whether the drive holding the directory has at least requiredBytes free.
        /// Returns true when availability cannot be determined — a broken probe must not
        /// block every transfer. A negative requirement (nonsense or overflowed) is
        /// rejected rather than treated as "no space needed".</summary>
        public static bool HasFreeSpace(string directory, long requiredBytes)
        {
            if (requiredBytes < 0) return false;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(directory));
                if (string.IsNullOrEmpty(root)) return true;
                return new DriveInfo(root).AvailableFreeSpace >= requiredBytes;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Disk-space precheck for an incoming file of the given declared size,
        /// adding the safety margin without overflowing. Implausible sizes fail closed.</summary>
        public static bool HasFreeSpaceFor(string directory, long fileSize)
        {
            if (fileSize < 0 || fileSize > MaxTransferSize) return false;
            return HasFreeSpace(directory, fileSize + DiskSpaceMargin);
        }
    }

    /// <summary>
    /// IP filter list matching for the server receive gate. Entries are separated by
    /// ';' or ','; each is an exact IPv4 address or a prefix ending in '*'
    /// (e.g. "192.168.1.*"). "*" alone matches everything.
    /// </summary>
    public static class IpFilter
    {
        public static bool Matches(string listDefinition, string ip)
        {
            if (string.IsNullOrWhiteSpace(listDefinition) || string.IsNullOrEmpty(ip)) return false;
            string[] parts = listDefinition.Split(new char[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string entry = parts[i].Trim();
                if (entry.Length == 0) continue;
                if (entry == "*") return true;
                if (entry[entry.Length - 1] == '*')
                {
                    string prefix = entry.Substring(0, entry.Length - 1);
                    if (ip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (string.Equals(entry, ip, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
