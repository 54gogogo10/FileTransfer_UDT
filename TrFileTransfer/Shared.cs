using System;
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

        public void Dispose()
        {
            lock (Lock)
            {
                Complete = true;
                try { if (WriteStream != null) { WriteStream.Dispose(); WriteStream = null; } } catch { }
            }
        }

        /// <summary>Gets or creates a tracker, deferring FileStream creation.</summary>
        public static ChunkTracker GetOrCreate(
            System.Collections.Concurrent.ConcurrentDictionary<string, ChunkTracker> dict,
            string fileName, long totalSize, string saveDirectory)
        {
            ChunkTracker tracker;
            if (!dict.TryGetValue(fileName, out tracker))
            {
                var newTracker = new ChunkTracker
                {
                    FileName = fileName,
                    TotalSize = totalSize,
                    SavePath = Utils.GetUniqueSavePath(saveDirectory, fileName)
                };
                tracker = dict.GetOrAdd(fileName, newTracker);
            }
            return tracker;
        }

        /// <summary>Writes chunk data and checks completion. Returns true if all chunks received.</summary>
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

                if (BytesReceived >= TotalSize && !Complete)
                {
                    Complete = true;
                    isComplete = true;
                }
            }
            return isComplete;
        }
    }
    #pragma warning restore 1591

        /// <summary>Token-bucket rate limiter for throttling transfer throughput.</summary>
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
                _totalSent += bytes;
                double expectedSeconds = (double)_totalSent / _maxBytesPerSec;
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

            public static TransferClient CreateTcp(string serverIp, int port, string filePath, int srcPort, int speedLimit, string pairingCode = null)
            {
                var client = new TransferClient(serverIp, port, filePath, srcPort, ClientBufferSize, speedLimit);
                client.PairingCode = pairingCode;
                return client;
            }

            public static TransferUdtClient CreateUdt(string serverIp, int port, string filePath, int srcPort, int speedLimit, string pairingCode = null)
            {
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
            /// Setting again with a new path overwrites; disabling when absent is a no-op.</summary>
            public static void Set(bool enable, string exePath)
            {
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (key == null) return;
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
            }
            return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
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
    }
}
