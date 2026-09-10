using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TrFileTransfer
{
    /// <summary>Headless command-line mode: scriptable send/recv without the GUI.
    /// Invoked as "TrFileTransfer send ..." / "recv ..." / "--help"; App.OnStartup
    /// branches here before any window is created. Exit codes: 0 = success, 1 = error,
    /// 2 = usage. The exe is a WinExe (no console of its own), so output attaches to
    /// the parent console when launched from one — double-clicked runs still open
    /// the GUI as before.</summary>
    internal static class Cli
    {
        private sealed class CliUsageException : Exception
        {
            public CliUsageException(string msg) : base(msg) { }
        }

        public static bool IsCliInvocation(string[] args)
        {
            if (args == null || args.Length == 0) return false;
            string a = args[0].ToLowerInvariant();
            return a == "send" || a == "recv" || a == "help" || a == "--help" || a == "-h" || a == "/?";
        }

        public static int Run(string[] args)
        {
            EnsureConsole();
            try
            {
                string cmd = args[0].ToLowerInvariant();
                if (cmd == "help" || cmd == "--help" || cmd == "-h" || cmd == "/?")
                {
                    Console.Out.WriteLine(L.CliHelp);
                    return 0;
                }
                var opt = ParseOptions(args, 1);
                return cmd == "send" ? RunSend(opt) : RunRecv(opt);
            }
            catch (CliUsageException ex)
            {
                Console.Out.WriteLine(ex.Message);
                Console.Out.WriteLine(L.CliHelp);
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        // ---- send ----

        private static int RunSend(Dictionary<string, string> opt)
        {
            string ip = Require(opt, "ip");
            int port = ParsePort(Require(opt, "port"));
            string path = GetValue(opt, "file");
            if (path.Length == 0) path = GetValue(opt, "folder");
            if (path.Length == 0)
                throw new CliUsageException(L.CliMissingArg("--file / --folder"));
            bool isFolder = opt.ContainsKey("folder") || Directory.Exists(path);
            if (isFolder ? !Directory.Exists(path) : !File.Exists(path))
                throw new CliUsageException((isFolder ? L.DirNotExist : L.FileNotFound) + " " + path);

            bool isUdt = opt.ContainsKey("udt");
            long limitKb = ParseLong(opt, "limit", 0, 1024L * 1024 * 1024, 0);
            // --limit is KB/s; SpeedLimiter counts bytes/s. Clamp so a huge KB value
            // cannot wrap the int (which would silently mean "unlimited").
            long limitBps = limitKb * 1024L;
            if (limitBps > int.MaxValue) limitBps = int.MaxValue;
            int limit = (int)limitBps;
            int srcPort = (int)ParseLong(opt, "srcport", 0, 65535, 0);
            string code = GetValue(opt, "code");

            Console.Out.WriteLine("{0} -> {1}:{2} ({3})", path, ip, port, isUdt ? "UDT" : "TCP");
            var watch = Stopwatch.StartNew();
            long bytes;
            try
            {
                if (isUdt)
                {
                    var client = ClientFactory.CreateUdt(ip, port, path, srcPort, limit, code);
                    if (opt.ContainsKey("noencrypt")) client.EncryptionEnabled = false;
                    if (opt.ContainsKey("nocompress")) client.CompressionEnabled = false;
                    client.OnLog += msg => Console.Out.WriteLine(msg);
                    client.OnError += msg => Console.Error.WriteLine(msg);
                    bytes = Measure(path);
                    RunToCompletion(isFolder ? client.SendFolderAsync(path) : client.SendAsync());
                }
                else
                {
                    var client = ClientFactory.CreateTcp(ip, port, path, srcPort, limit, code);
                    if (opt.ContainsKey("noencrypt")) client.EncryptionEnabled = false;
                    if (opt.ContainsKey("nocompress")) client.CompressionEnabled = false;
                    client.OnLog += msg => Console.Out.WriteLine(msg);
                    client.OnError += msg => Console.Error.WriteLine(msg);
                    bytes = Measure(path);
                    RunToCompletion(isFolder ? client.SendFolderAsync(path) : client.SendAsync());
                }
            }
            catch (Exception)
            {
                // OnError already printed the reason on the console
                return 1;
            }
            watch.Stop();
            Console.Out.WriteLine(L.CliSendingDone(path, Utils.FormatSize(bytes), watch.Elapsed.TotalSeconds));
            return 0;
        }

        /// <summary>Blocks on the transfer task; failures propagate after the
        /// client had its chance to log (the logic layer rethrows in RunTransfer).
        /// The caller runs Cli.Run on a worker thread, so the continuations of the
        /// awaited work never capture the (blocked) WPF dispatcher context.</summary>
        private static void RunToCompletion(System.Threading.Tasks.Task task)
        {
            task.GetAwaiter().GetResult();
        }

        private static long Measure(string path)
        {
            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;
                long total = 0;
                foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                return total;
            }
            catch { return 0; }
        }

        // ---- recv ----

        private static int RunRecv(Dictionary<string, string> opt)
        {
            int port = ParsePort(Require(opt, "port"));
            string dir = GetValue(opt, "out");
            if (dir.Length == 0) dir = Directory.GetCurrentDirectory();
            if (!Directory.Exists(dir))
                throw new CliUsageException(L.DirNotExist + " " + dir);
            bool isUdt = opt.ContainsKey("udt");
            string code = GetValue(opt, "code");
            long countTarget = ParseLong(opt, "count", 0, long.MaxValue, 0);

            var stop = new ManualResetEvent(false);
            long received = 0;
            Action<string> log = msg => Console.Out.WriteLine(msg);
            Action<string> err = msg => Console.Error.WriteLine(msg);
            Action<string, long> fileRecv = (p, sz) =>
            {
                Console.Out.WriteLine(L.CliReceivedFile(p, Utils.FormatSize(sz)));
                if (countTarget > 0 && Interlocked.Increment(ref received) >= countTarget)
                    stop.Set();
            };

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true; // orderly shutdown instead of abrupt termination
                stop.Set();
            };

            // A failed Start() (port in use, bind error) reports through OnStopped
            // (and OnError); without handling it the wait below would block forever.
            // A *normal* Stop() also fires OnStopped, so the failure check must happen
            // before we call Stop() — hence the flag read right after WaitOne.
            bool asyncStopFailed = false;
            Action onStopped = () =>
            {
                // Only a stop that we did not ask for is a failure signal
                if (!stop.WaitOne(0)) { asyncStopFailed = true; stop.Set(); }
            };

            if (isUdt)
            {
                var server = new TransferUdtServer("0.0.0.0", port, dir);
                server.PairingCode = code;
                server.OnLog += log;
                server.OnError += err;
                server.OnFileReceived += fileRecv;
                server.OnStopped += onStopped;
                server.Start();
                if (!server.IsRunning) { Console.Error.WriteLine(L.CliRecvStartFailed(port)); return 1; }
                Console.Out.WriteLine(L.CliRecvWaiting("UDT", port, dir));
                stop.WaitOne();
                bool failed = asyncStopFailed;
                server.Stop();
                if (failed) return 1;
            }
            else
            {
                var server = new TransferServer("0.0.0.0", port, dir);
                server.PairingCode = code;
                server.OnLog += log;
                server.OnError += err;
                server.OnFileReceived += fileRecv;
                server.OnStopped += onStopped;
                server.Start();
                if (!server.IsRunning) { Console.Error.WriteLine(L.CliRecvStartFailed(port)); return 1; }
                Console.Out.WriteLine(L.CliRecvWaiting("TCP", port, dir));
                stop.WaitOne();
                bool failed = asyncStopFailed;
                server.Stop();
                if (failed) return 1;
            }

            if (countTarget > 0)
                Console.Out.WriteLine(L.CliRecvCountDone(Interlocked.Read(ref received)));
            return 0;
        }

        // ---- option parsing ----

        private static Dictionary<string, string> ParseOptions(string[] args, int from)
        {
            var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = from; i < args.Length; i++)
            {
                string a = args[i];
                if (a.Length < 2 || (a[0] != '-' && a[0] != '/'))
                    throw new CliUsageException(L.CliUnknownCmd(a));
                string key = a.TrimStart('-', '/').ToLowerInvariant();
                string value = null;
                int eq = key.IndexOf('=');
                if (eq >= 0)
                {
                    value = key.Substring(eq + 1);
                    key = key.Substring(0, eq);
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal)
                    && !args[i + 1].StartsWith("/", StringComparison.Ordinal))
                {
                    value = args[++i];
                }
                opt[key] = value ?? "";
            }
            return opt;
        }

        private static string Require(Dictionary<string, string> opt, string key)
        {
            string v;
            if (!opt.TryGetValue(key, out v) || string.IsNullOrEmpty(v))
                throw new CliUsageException(L.CliMissingArg("--" + key));
            return v;
        }

        private static string GetValue(Dictionary<string, string> opt, string key)
        {
            string v;
            return opt.TryGetValue(key, out v) ? (v ?? "") : "";
        }

        private static int ParsePort(string raw)
        {
            int port;
            if (!int.TryParse(raw, out port) || port < 1 || port > 65535)
                throw new CliUsageException("--port: 1-65535");
            return port;
        }

        private static long ParseLong(Dictionary<string, string> opt, string key, long min, long max, long dflt)
        {
            string raw;
            if (!opt.TryGetValue(key, out raw) || string.IsNullOrEmpty(raw)) return dflt;
            long v;
            if (!long.TryParse(raw, out v) || v < min || v > max)
                throw new CliUsageException("--" + key + ": " + min + "-" + max);
            return v;
        }

        // ---- console plumbing for a WinExe ----

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        private const int STD_OUTPUT_HANDLE = -11;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const int ATTACH_PARENT_PROCESS = -1;

        /// <summary>Attach to the launching console (cmd/powershell/CI) and repoint
        /// stdout/stderr at it — a WinExe's default streams go nowhere. An inherited
        /// or redirected stdout handle is left alone, so "app.exe --help &gt; out.txt"
        /// keeps working instead of being overridden by CONOUT$. Failure is silent:
        /// the command still runs, just without visible output.</summary>
        private static void EnsureConsole()
        {
            try
            {
                IntPtr existing = GetStdHandle(STD_OUTPUT_HANDLE);
                if (existing != IntPtr.Zero && existing != new IntPtr(-1))
                    return; // inherited console or a shell redirection — keep it

                if (!AttachConsole(ATTACH_PARENT_PROCESS)) return;
                IntPtr h = CreateFileW("CONOUT$", GENERIC_WRITE, FILE_SHARE_WRITE, IntPtr.Zero,
                    OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return;
                var fs = new Microsoft.Win32.SafeHandles.SafeFileHandle(h, true);
                var sw = new StreamWriter(new FileStream(fs, FileAccess.Write)) { AutoFlush = true };
                Console.SetOut(sw);
                Console.SetError(sw);
            }
            catch { }
        }
    }
}
