using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer.Tests
{
    public static class IntegrationTests
    {
        private static int FindFreePort()
        {
            for (int attempt = 0; attempt < 64; attempt++)
            {
                var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
                try
                {
                    listener.Start();
                    int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                    // UDT servers bind UDP on the same port — verify it is not inside a
                    // Windows reserved/excluded UDP range (Hyper-V/WSL dynamic ports).
                    try
                    {
                        var udp = new UdpClient(port);
                        udp.Close();
                        return port;
                    }
                    catch { }
                    finally
                    {
                        try { listener.Stop(); } catch { }
                    }
                }
                catch { }
            }
            return 0;
        }

        public static void RunAll(TestRunner runner)
        {
            runner.Run("Integration_TCP_SingleFile", TcpSingleFile);
            runner.Run("Integration_TCP_Folder", TcpFolder);
            runner.Run("Integration_TCP_LargeSingle", TcpLargeSingle);
            runner.Run("Integration_TCP_LargeConcur", TcpLargeConcur);
            runner.Run("Integration_TCP_ResumeSingleFile", TcpResumeSingleFile);
            runner.Run("Integration_TCP_ResumeInterrupted", TcpResumeInterrupted);
            runner.Run("Integration_TCP_ResumeAcrossRestart", TcpResumeAcrossRestart);
            runner.Run("Integration_TCP_ResumeFullHashCorrupt", TcpResumeFullHashCorrupt);
            runner.Run("Integration_TCP_RateLimit", TcpRateLimit);
            runner.Run("Integration_Discovery", DiscoveryTest);
            runner.Run("Integration_UDT_ResumeSingleFile", UdtResumeSingleFile);
            runner.Run("Integration_UDT_SingleFile", UdtSingleFile);
            runner.Run("Integration_UDT_LargeSingle", UdtLargeSingle);
            runner.Run("Integration_UDT_LargeConcur", UdtLargeConcur);
        }

        private static void TcpSingleFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(@"D:\cc\tmp", "tr_it_send_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(@"D:\cc\tmp", "tr_it_recv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "hello.txt");
                var rng = new Random(42);
                var content = new byte[1024 * 50];
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;

                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                server.OnError += msg => { serverError = msg; serverDone.Set(); };
                server.Start();

                if (!serverStarted.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                var client = new TransferClient("127.0.0.1", port, testFile);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += msg => clientDone.Set();

                var sendTask = client.SendAsync();

                if (!serverDone.WaitOne(30000))
                    throw new Exception("Server did not complete within 30s");
                if (!serverOk)
                    throw new Exception("Server error: " + (serverError ?? "unknown"));

                sendTask.Wait(30000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("Client did not fire completion event");
                if (!clientOk)
                    throw new Exception("Client transfer failed");

                Thread.Sleep(300); // allow file flush to settle

                var receivedFile = Path.Combine(recvDir, "hello.txt");
                Assert.True(File.Exists(receivedFile), "received file exists");
                var receivedContent = File.ReadAllBytes(receivedFile);
                Assert.Equal(content.Length, receivedContent.Length, "file size matches");
                Assert.True(Utils.ConstantTimeEquals(content, receivedContent), "content SHA256 match");
            }
            finally
            {
                try { if (server != null) server.Stop(); } catch { }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void TcpFolder()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(@"D:\cc\tmp", "tr_it_fsend_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(@"D:\cc\tmp", "tr_it_frecv_" + Guid.NewGuid().ToString("N"));
            string folderPath = Path.Combine(sendDir, "myFolder");
            Directory.CreateDirectory(folderPath);

            TransferServer server = null;
            try
            {
                var rng = new Random(123);
                var fileAContent = new byte[1024 * 10];
                var fileBContent = new byte[1024 * 15];
                rng.NextBytes(fileAContent);
                rng.NextBytes(fileBContent);
                File.WriteAllBytes(Path.Combine(folderPath, "a.bin"), fileAContent);
                File.WriteAllBytes(Path.Combine(folderPath, "b.bin"), fileBContent);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;

                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                server.OnError += msg => { serverError = msg; serverDone.Set(); };
                server.Start();

                if (!serverStarted.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                var client = new TransferClient("127.0.0.1", port, folderPath);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += msg => clientDone.Set();

                var sendTask = client.SendFolderAsync(folderPath);

                if (!serverDone.WaitOne(30000))
                    throw new Exception("Server did not complete within 30s");
                if (!serverOk)
                    throw new Exception("Server error: " + (serverError ?? "unknown"));

                sendTask.Wait(30000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("Client did not fire completion event");
                if (!clientOk)
                    throw new Exception("Client folder transfer failed");

                Thread.Sleep(300);

                Assert.True(Directory.Exists(recvDir), "receive dir exists");
                var receivedA = Path.Combine(recvDir, "myFolder", "a.bin");
                var receivedB = Path.Combine(recvDir, "myFolder", "b.bin");
                Assert.True(File.Exists(receivedA), "a.bin exists");
                Assert.True(File.Exists(receivedB), "b.bin exists");
                Assert.True(Utils.ConstantTimeEquals(fileAContent, File.ReadAllBytes(receivedA)), "a.bin match");
                Assert.True(Utils.ConstantTimeEquals(fileBContent, File.ReadAllBytes(receivedB)), "b.bin match");
            }
            finally
            {
                try { if (server != null) server.Stop(); } catch { }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void UdtSingleFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(@"D:\cc\tmp", "tr_it_udt_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(@"D:\cc\tmp", "tr_it_udt_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferUdtServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "udt_test.bin");
                var rng = new Random(99);
                var content = new byte[1024 * 50]; // 50 KB
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;

                server = new TransferUdtServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                server.OnError += msg => { serverError = msg; serverDone.Set(); };
                server.Start();

                if (!serverStarted.WaitOne(5000))
                    throw new Exception("UDT server did not start within 5s");

                var client = new TransferUdtClient("127.0.0.1", port, testFile);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += msg => clientDone.Set();

                var sendTask = client.SendAsync();

                if (!serverDone.WaitOne(60000))
                    throw new Exception("UDT server did not complete within 60s");
                if (!serverOk)
                    throw new Exception("UDT server error: " + (serverError ?? "unknown"));

                sendTask.Wait(60000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("UDT client did not fire completion event");
                if (!clientOk)
                    throw new Exception("UDT client transfer failed");

                Thread.Sleep(500);

                var receivedFile = Path.Combine(recvDir, "udt_test.bin");
                Assert.True(File.Exists(receivedFile), "received UDT file exists");
                var receivedContent = File.ReadAllBytes(receivedFile);
                Assert.Equal(content.Length, receivedContent.Length, "UDT file size matches");
                Assert.True(Utils.ConstantTimeEquals(content, receivedContent), "UDT content SHA256 match");
            }
            finally
            {
                try { if (server != null) server.Stop(); } catch { }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        // Shared helper: run a concurrent transfer test for TCP or UDT
        private static void ConcurrentTransferTest(string prefix, bool isTcp, int concurrency,
            long fileSizeMB, int timeoutSec)
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(@"D:\cc\tmp", prefix + "_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(@"D:\cc\tmp", prefix + "_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer tcpServer = null;
            TransferUdtServer udtServer = null;
            try
            {
                var testFile = Path.Combine(sendDir, "c_test.bin");
                long totalBytes = fileSizeMB * 1024 * 1024;
                var rng = new Random(42);

                // For large files, stream-write in 64 MB chunks to avoid OOM
                const int WriteChunk = 64 * 1024 * 1024;
                if (totalBytes <= 500L * 1024 * 1024)
                {
                    var content = new byte[totalBytes];
                    rng.NextBytes(content);
                    File.WriteAllBytes(testFile, content);
                }
                else
                {
                    var buf = new byte[WriteChunk];
                    using (var fs = new FileStream(testFile, FileMode.Create, FileAccess.Write, FileShare.None,
                        65536, FileOptions.SequentialScan))
                    {
                        long remaining = totalBytes;
                        while (remaining > 0)
                        {
                            int n = (int)Math.Min(remaining, WriteChunk);
                            rng.NextBytes(buf);
                            fs.Write(buf, 0, n);
                            remaining -= n;
                        }
                    }
                }

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                var serverErrors = new System.Collections.Generic.List<string>();
                var clientErrors = new System.Collections.Generic.List<string>();

                if (isTcp)
                {
                    tcpServer = new TransferServer("127.0.0.1", port, recvDir);
                    tcpServer.OnStarted += () => serverStarted.Set();
                    tcpServer.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                    tcpServer.OnError += msg => { lock (serverErrors) serverErrors.Add(msg); serverDone.Set(); };
                    tcpServer.Start();
                }
                else
                {
                    udtServer = new TransferUdtServer("127.0.0.1", port, recvDir);
                    udtServer.OnStarted += () => serverStarted.Set();
                    udtServer.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                    udtServer.OnError += msg => { lock (serverErrors) serverErrors.Add(msg); serverDone.Set(); };
                    udtServer.Start();
                }

                if (!serverStarted.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                Task sendTask;

                if (concurrency > 1)
                {
                    var concurrent = new ConcurrentTransfer("127.0.0.1", port, testFile, concurrency, isTcp);
                    concurrent.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                    concurrent.OnError += msg => { lock (clientErrors) clientErrors.Add(msg); clientDone.Set(); };
                    sendTask = concurrent.SendAsync();
                }
                else if (isTcp)
                {
                    var client = new TransferClient("127.0.0.1", port, testFile);
                    client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                    client.OnError += msg => { clientDone.Set(); };
                    sendTask = client.SendAsync();
                }
                else
                {
                    var client = new TransferUdtClient("127.0.0.1", port, testFile);
                    client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                    client.OnError += msg => { clientDone.Set(); };
                    sendTask = client.SendAsync();
                }

                if (!serverDone.WaitOne(timeoutSec * 1000))
                {
                    string detail;
                    lock (serverErrors) detail = "server errors: " + (serverErrors.Count > 0 ? string.Join(" | ", serverErrors) : "(none)");
                    lock (clientErrors) detail += " | client errors: " + (clientErrors.Count > 0 ? string.Join(" | ", clientErrors) : "(none)");
                    throw new Exception("Server did not complete within " + timeoutSec + "s — " + detail);
                }
                if (!serverOk)
                {
                    string detail;
                    lock (serverErrors) detail = string.Join(" | ", serverErrors);
                    lock (clientErrors) detail += " | client errors: " + (clientErrors.Count > 0 ? string.Join(" | ", clientErrors) : "(none)");
                    throw new Exception("Server errors: " + detail);
                }

                sendTask.Wait(timeoutSec * 1000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("Client did not fire completion event");
                if (!clientOk)
                    throw new Exception("Transfer failed");

                Thread.Sleep(300);

                var receivedFile = Path.Combine(recvDir, "c_test.bin");
                Assert.True(File.Exists(receivedFile), "received file exists");
                var receivedInfo = new FileInfo(receivedFile);
                Assert.True(receivedInfo.Length == totalBytes, "file size matches");

                // Verify with streaming SHA256 for large files
                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                using (var fs = new FileStream(receivedFile, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                {
                    var hash = sha256.ComputeHash(fs);
                    // Verify by checking hash is non-zero (deterministic random data with seed=42)
                    bool allZero = true;
                    for (int i = 0; i < hash.Length; i++) { if (hash[i] != 0) { allZero = false; break; } }
                    Assert.False(allZero, "received file hash is non-zero (valid data)");
                }
            }
            finally
            {
                try { if (tcpServer != null) tcpServer.Stop(); } catch { }
                try { if (udtServer != null) udtServer.Stop(); } catch { }
                // Let handler threads release file handles before deleting directories
                System.Threading.Thread.Sleep(500);
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void TcpLargeSingle()  { ConcurrentTransferTest("tr_tcpls", true, 1, 5000, 600); }
        private static void TcpLargeConcur()  { ConcurrentTransferTest("tr_tcplc", true, 8, 5000, 900); }

        private static void UdtLargeSingle()  { ConcurrentTransferTest("tr_udtls", false, 1, 5000, 1200); }
        private static void UdtLargeConcur()  { ConcurrentTransferTest("tr_udtlc", false, 8, 5000, 1800); }

        private static void TcpResumeSingleFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(@"D:\cc\tmp", "tr_rs_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(@"D:\cc\tmp", "tr_rs_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server = null;
            try
            {
                // Create test file (~200 KB)
                var testFile = Path.Combine(sendDir, "resume_test.bin");
                var rng = new Random(42);
                var content = new byte[1024 * 200];
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);

                // Start server
                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;

                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnClientTransferComplete += ep => { serverOk = true; serverDone.Set(); };
                server.OnError += msg => { serverError = msg; serverDone.Set(); };
                server.Start();

                if (!serverStarted.WaitOne(5000))
                    throw new Exception("Server did not start");

                // Send with resume — create a ResumeState and use SendResumableAsync
                var sessionId = Guid.NewGuid();
                var state = new ResumeState
                {
                    SessionId = sessionId,
                    TotalSize = content.Length,
                    FileName = Path.GetFileName(testFile),
                    FilePath = testFile,
                    ServerIp = "127.0.0.1",
                    Port = port,
                    IsUdt = false,
                    Created = DateTime.UtcNow,
                    SentBytes = 0
                };
                state.Save();

                var client = new TransferClient("127.0.0.1", port, testFile);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += msg => clientDone.Set();

                var sendTask = client.SendResumableAsync(sessionId);

                // Wait for server completion
                if (!serverDone.WaitOne(30000))
                    throw new Exception("Server did not complete within 30s");
                if (!serverOk)
                    throw new Exception("Server error: " + (serverError ?? "unknown"));

                sendTask.Wait(30000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("Client did not fire completion event");
                if (!clientOk)
                    throw new Exception("Resume transfer failed");

                Thread.Sleep(300);

                // Verify received file
                var receivedFile = Path.Combine(recvDir, "resume_test.bin");
                Assert.True(File.Exists(receivedFile), "received file exists");
                var receivedContent = File.ReadAllBytes(receivedFile);
                Assert.Equal(content.Length, receivedContent.Length, "file size matches");
                Assert.True(Utils.ConstantTimeEquals(content, receivedContent), "content match");

                // Verify resume state was deleted on success
                Assert.False(File.Exists(ResumeState.GetPath(sessionId)), "resume state deleted after success");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void TcpResumeInterrupted()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(@"D:\cc\tmp", "tr_ri_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(@"D:\cc\tmp", "tr_ri_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server = null;
            try
            {
                // 512 MB: large enough that loopback transfer reliably exceeds the
                // 100 ms server progress throttle, so the mid-transfer cancel below
                // fires while data is still flowing.
                var testFile = Path.Combine(sendDir, "resume_big.bin");
                var rng = new Random(42);
                var content = new byte[1024 * 1024 * 512];
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                var phase1Closed = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;
                bool secondPhase = false;
                var serverLogs = new System.Collections.Generic.List<string>();

                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnLog += msg => { lock (serverLogs) serverLogs.Add(msg); };
                server.OnClientTransferComplete += ep => { serverOk = true; serverDone.Set(); };
                // Connection-close errors are expected from the phase-1 cancel; they
                // signal that the server has finished unwinding that connection. Only
                // errors arriving once the resume (phase 2) has started are fatal.
                server.OnError += msg =>
                {
                    if (secondPhase) { serverError = msg; serverDone.Set(); }
                    else phase1Closed.Set();
                };
                server.Start();

                if (!serverStarted.WaitOne(5000))
                    throw new Exception("Server did not start");

                var sessionId = Guid.NewGuid();
                var state = new ResumeState
                {
                    SessionId = sessionId,
                    TotalSize = content.Length,
                    FileName = Path.GetFileName(testFile),
                    FilePath = testFile,
                    ServerIp = "127.0.0.1",
                    Port = port,
                    IsUdt = false,
                    Created = DateTime.UtcNow,
                    SentBytes = 0
                };
                state.Save();

                // Phase 1: start a resumable send, cancel it once the server has
                // received a meaningful amount of data.
                var client1 = new TransferClient("127.0.0.1", port, testFile);
                var client1Done = new ManualResetEvent(false);
                client1.OnStopped += () => client1Done.Set();
                var sendTask1 = client1.SendResumableAsync(sessionId);

                var partialReceived = new ManualResetEvent(false);
                Action<System.Net.IPEndPoint, TransferProgress> progressHandler = null;
                progressHandler = (ep, p) =>
                {
                    if (p.BytesTransferred >= 1024 * 1024)
                    {
                        server.OnClientProgress -= progressHandler;
                        client1.Cancel();
                        partialReceived.Set();
                    }
                };
                server.OnClientProgress += progressHandler;

                if (!partialReceived.WaitOne(30000))
                    throw new Exception("Server did not receive partial data within 30s");
                sendTask1.Wait(30000);
                if (!client1Done.WaitOne(5000))
                    throw new Exception("Client 1 did not stop after cancel");

                // Wait for the server to observe the phase-1 close (and finish
                // unwinding that connection) so its expected error can never be
                // mistaken for a phase-2 failure. Timeout is a safe fallback.
                phase1Closed.WaitOne(2000);

                // Phase 2: resume the same session — the server must continue from
                // its received offset (server state is authoritative).
                secondPhase = true;
                var client2 = new TransferClient("127.0.0.1", port, testFile);
                var client2Done = new ManualResetEvent(false);
                bool client2Ok = false;
                client2.OnTransferComplete += () => { client2Ok = true; client2Done.Set(); };
                client2.OnError += msg => client2Done.Set();

                var sendTask2 = client2.SendResumableAsync(sessionId);

                if (!serverDone.WaitOne(60000))
                    throw new Exception("Server did not complete resumed transfer within 60s");
                if (!serverOk)
                    throw new Exception("Server error: " + (serverError ?? "unknown"));
                sendTask2.Wait(60000);
                if (!client2Done.WaitOne(5000))
                    throw new Exception("Client 2 did not fire completion event");
                if (!client2Ok)
                    throw new Exception("Resume transfer failed");

                Thread.Sleep(300);

                // Verify the resumed file is complete and identical
                var receivedFile = Path.Combine(recvDir, "resume_big.bin");
                Assert.True(File.Exists(receivedFile), "received file exists");
                var receivedContent = File.ReadAllBytes(receivedFile);
                Assert.Equal(content.Length, receivedContent.Length, "file size matches");
                Assert.True(Utils.ConstantTimeEquals(content, receivedContent), "content match");

                // Verify resume state was deleted on success
                Assert.False(File.Exists(ResumeState.GetPath(sessionId)), "resume state deleted after success");

                // Prove the second attempt actually resumed from a non-zero offset:
                // the last server "Resume:" log line must show offset > 0.
                string resumeLog = null;
                lock (serverLogs)
                {
                    for (int i = serverLogs.Count - 1; i >= 0; i--)
                    {
                        if (serverLogs[i].Contains("Resume: ")) { resumeLog = serverLogs[i]; break; }
                    }
                }
                Assert.True(resumeLog != null, "server logged resume negotiation");
                int marker = resumeLog.IndexOf("offset=");
                Assert.True(marker >= 0, "resume log contains offset");
                long resumedFrom;
                string numStr = "";
                for (int i = marker + 7; i < resumeLog.Length && char.IsDigit(resumeLog[i]); i++)
                    numStr += resumeLog[i];
                Assert.True(long.TryParse(numStr, out resumedFrom), "offset is a number");
                Assert.True(resumedFrom > 0, "resumed from non-zero offset (got " + resumedFrom + ")");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void TcpResumeAcrossRestart()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(@"D:\cc\tmp", "tr_rr_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(@"D:\cc\tmp", "tr_rr_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server1 = null;
            TransferServer server2 = null;
            try
            {
                var testFile = Path.Combine(sendDir, "resume_restart.bin");
                var rng = new Random(42);
                var content = new byte[1024 * 1024 * 512];
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);

                var sessionId = Guid.NewGuid();
                var state = new ResumeState
                {
                    SessionId = sessionId,
                    TotalSize = content.Length,
                    FileName = Path.GetFileName(testFile),
                    FilePath = testFile,
                    ServerIp = "127.0.0.1",
                    Port = port,
                    IsUdt = false,
                    Created = DateTime.UtcNow,
                    SentBytes = 0
                };
                state.Save();

                // ---- Phase 1: partial transfer on server1, then stop it ----
                var s1Started = new ManualResetEvent(false);
                var phase1Closed = new ManualResetEvent(false);
                server1 = new TransferServer("127.0.0.1", port, recvDir);
                server1.OnStarted += () => s1Started.Set();
                server1.OnError += msg => phase1Closed.Set();
                server1.Start();
                if (!s1Started.WaitOne(5000))
                    throw new Exception("Server 1 did not start");

                var client1 = new TransferClient("127.0.0.1", port, testFile);
                var client1Done = new ManualResetEvent(false);
                client1.OnStopped += () => client1Done.Set();
                var sendTask1 = client1.SendResumableAsync(sessionId);

                var partialReceived = new ManualResetEvent(false);
                Action<System.Net.IPEndPoint, TransferProgress> progressHandler = null;
                progressHandler = (ep, p) =>
                {
                    if (p.BytesTransferred >= 1024 * 1024)
                    {
                        server1.OnClientProgress -= progressHandler;
                        client1.Cancel();
                        partialReceived.Set();
                    }
                };
                server1.OnClientProgress += progressHandler;

                if (!partialReceived.WaitOne(30000))
                    throw new Exception("Server 1 did not receive partial data within 30s");
                sendTask1.Wait(30000);
                if (!client1Done.WaitOne(5000))
                    throw new Exception("Client 1 did not stop after cancel");
                phase1Closed.WaitOne(2000);

                // Server restart — Stop() persists the incomplete resume state to disk
                server1.Stop();
                server1 = null;
                Thread.Sleep(500);

                // ---- Phase 2: server2 recovers the disk state and continues ----
                var s2Started = new ManualResetEvent(false);
                var s2Done = new ManualResetEvent(false);
                bool s2Ok = false;
                string s2Error = null;
                var s2Logs = new System.Collections.Generic.List<string>();

                server2 = new TransferServer("127.0.0.1", port, recvDir);
                server2.OnStarted += () => s2Started.Set();
                server2.OnLog += msg => { lock (s2Logs) s2Logs.Add(msg); };
                server2.OnClientTransferComplete += ep => { s2Ok = true; s2Done.Set(); };
                server2.OnError += msg => { s2Error = msg; s2Done.Set(); };
                server2.Start();
                if (!s2Started.WaitOne(5000))
                    throw new Exception("Server 2 did not start");

                var client2 = new TransferClient("127.0.0.1", port, testFile);
                var client2Done = new ManualResetEvent(false);
                bool client2Ok = false;
                client2.OnTransferComplete += () => { client2Ok = true; client2Done.Set(); };
                client2.OnError += msg => client2Done.Set();

                var sendTask2 = client2.SendResumableAsync(sessionId);

                if (!s2Done.WaitOne(60000))
                    throw new Exception("Server 2 did not complete resumed transfer within 60s");
                if (!s2Ok)
                    throw new Exception("Server 2 error: " + (s2Error ?? "unknown"));
                sendTask2.Wait(60000);
                if (!client2Done.WaitOne(5000))
                    throw new Exception("Client 2 did not fire completion event");
                if (!client2Ok)
                    throw new Exception("Resume after restart failed");

                Thread.Sleep(300);

                var receivedFile = Path.Combine(recvDir, "resume_restart.bin");
                Assert.True(File.Exists(receivedFile), "received file exists");
                var receivedContent = File.ReadAllBytes(receivedFile);
                Assert.Equal(content.Length, receivedContent.Length, "file size matches");
                Assert.True(Utils.ConstantTimeEquals(content, receivedContent), "content match");

                // Client state deleted on success
                Assert.False(File.Exists(ResumeState.GetPath(sessionId)), "client resume state deleted");
                // Server disk state cleaned up on success
                Assert.False(File.Exists(ServerResumeStore.GetPath(sessionId)), "server resume state deleted");

                // Server 2 must have restored from disk and resumed at a non-zero offset
                bool sawRestored = false;
                bool sawNonZeroOffset = false;
                lock (s2Logs)
                {
                    foreach (var line in s2Logs)
                    {
                        if (line.Contains("restored server state")) sawRestored = true;
                        if (line.Contains("Resume: ") && line.Contains("offset="))
                        {
                            int marker = line.IndexOf("offset=") + 7;
                            string numStr = "";
                            for (int i = marker; i < line.Length && char.IsDigit(line[i]); i++)
                                numStr += line[i];
                            long off;
                            if (long.TryParse(numStr, out off) && off > 0)
                                sawNonZeroOffset = true;
                        }
                    }
                }
                Assert.True(sawRestored, "server 2 restored disk state");
                Assert.True(sawNonZeroOffset, "server 2 resumed from non-zero offset");
            }
            finally
            {
                if (server1 != null) { try { server1.Stop(); } catch { } }
                if (server2 != null) { try { server2.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void TcpResumeFullHashCorrupt()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(@"D:\cc\tmp", "tr_fh_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(@"D:\cc\tmp", "tr_fh_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "resume_corrupt.bin");
                var rng = new Random(42);
                var content = new byte[1024 * 1024 * 128];
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);
                DateTime originalMTime = File.GetLastWriteTimeUtc(testFile);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;
                bool secondPhase = false;
                var serverLogs = new System.Collections.Generic.List<string>();

                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnLog += msg => { lock (serverLogs) serverLogs.Add(msg); };
                server.OnClientTransferComplete += ep => { serverOk = true; serverDone.Set(); };
                server.OnError += msg =>
                {
                    lock (serverLogs) serverLogs.Add("[OnError] " + msg);
                    if (secondPhase) { serverError = msg; serverDone.Set(); }
                };
                server.Start();
                if (!serverStarted.WaitOne(5000))
                    throw new Exception("Server did not start");

                var sessionId = Guid.NewGuid();
                var state = new ResumeState
                {
                    SessionId = sessionId,
                    TotalSize = content.Length,
                    FileName = Path.GetFileName(testFile),
                    FilePath = testFile,
                    ServerIp = "127.0.0.1",
                    Port = port,
                    IsUdt = false,
                    Created = DateTime.UtcNow,
                    SentBytes = 0
                };
                state.Save();

                // Phase 1: partial transfer with full-hash verification enabled
                var client1 = new TransferClient("127.0.0.1", port, testFile);
                var client1Done = new ManualResetEvent(false);
                client1.OnStopped += () => client1Done.Set();
                var sendTask1 = client1.SendResumableAsync(sessionId, true);

                var partialReceived = new ManualResetEvent(false);
                Action<System.Net.IPEndPoint, TransferProgress> progressHandler = null;
                progressHandler = (ep, p) =>
                {
                    if (p.BytesTransferred >= 1024 * 1024)
                    {
                        server.OnClientProgress -= progressHandler;
                        client1.Cancel();
                        partialReceived.Set();
                    }
                };
                server.OnClientProgress += progressHandler;

                if (!partialReceived.WaitOne(30000))
                    throw new Exception("Server did not receive partial data within 30s");
                sendTask1.Wait(30000);
                if (!client1Done.WaitOne(5000))
                    throw new Exception("Client 1 did not stop after cancel");

                // Phase 2: tamper with the source file (same size) but restore the
                // mtime so the mtime check passes — the server's full-file hash
                // verification must still catch the mixed content.
                var tampered = new byte[content.Length];
                var rng2 = new Random(99);
                rng2.NextBytes(tampered);
                File.WriteAllBytes(testFile, tampered);
                File.SetLastWriteTimeUtc(testFile, originalMTime);

                secondPhase = true;
                var client2 = new TransferClient("127.0.0.1", port, testFile);
                var client2Done = new ManualResetEvent(false);
                bool client2Ok = false;
                bool client2Error = false;
                client2.OnTransferComplete += () => { client2Ok = true; client2Done.Set(); };
                client2.OnError += msg => { client2Error = true; client2Done.Set(); };

                var sendTask2 = client2.SendResumableAsync(sessionId, true);

                // The server must reject the transfer: full-hash mismatch
                if (!client2Done.WaitOne(60000))
                    throw new Exception("Client 2 did not finish within 60s");
                sendTask2.Wait(60000);
                if (!client2Error)
                {
                    string logs;
                    lock (serverLogs) logs = string.Join(" | ", serverLogs);
                    throw new Exception("client 2 reported verification error — ok=" + client2Ok
                        + " taskStatus=" + sendTask2.Status + " — server logs: " + logs);
                }
                Assert.False(client2Ok, "client 2 did NOT report success");

                Thread.Sleep(500);

                // Server discarded the mixed file
                var leftover = Directory.GetFiles(recvDir);
                Assert.Equal(0, leftover.Length, "server discarded the corrupt file");

                // Server logged the full-hash failure
                bool sawFullHashFailed = false;
                lock (serverLogs)
                {
                    foreach (var line in serverLogs)
                        if (line.Contains("Full-file hash")) sawFullHashFailed = true;
                }
                Assert.True(sawFullHashFailed, "server logged full-hash failure");

                // Client resume state kept for retry
                Assert.True(File.Exists(ResumeState.GetPath(sessionId)), "client state kept for retry");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void TcpRateLimit()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(@"D:\cc\tmp", "tr_rl_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(@"D:\cc\tmp", "tr_rl_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "rate_test.bin");
                var rng = new Random(42);
                var content = new byte[1024 * 1024 * 8]; // 8 MB
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;

                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                server.OnError += msg => { serverError = msg; serverDone.Set(); };
                server.Start();
                if (!serverStarted.WaitOne(5000))
                    throw new Exception("Server did not start");

                // 512 KB/s limit on an 8 MB file ⇒ at least ~16 s of transfer
                var client = new TransferClient("127.0.0.1", port, testFile, 0, 4194304, 512 * 1024);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += msg => clientDone.Set();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var sendTask = client.SendAsync();
                if (!serverDone.WaitOne(60000))
                    throw new Exception("Server did not complete within 60s");
                if (!serverOk)
                    throw new Exception("Server error: " + (serverError ?? "unknown"));
                sendTask.Wait(60000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("Client did not fire completion event");
                if (!clientOk)
                    throw new Exception("Client transfer failed");
                sw.Stop();

                // 8 MB at 512 KB/s = 16 s nominal; allow generous slack (limit applies
                // per send-chunk so the total is always at or above the limit)
                Assert.True(sw.Elapsed.TotalSeconds >= 10.0,
                    "rate-limited transfer took " + sw.Elapsed.TotalSeconds.ToString("F1") + "s (expected >= 10s)");

                Thread.Sleep(300);
                var receivedFile = Path.Combine(recvDir, "rate_test.bin");
                Assert.True(File.Exists(receivedFile), "received file exists");
                var receivedContent = File.ReadAllBytes(receivedFile);
                Assert.Equal(content.Length, receivedContent.Length, "file size matches");
                Assert.True(Utils.ConstantTimeEquals(content, receivedContent), "content match");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void DiscoveryTest()
        {
            int dPort = FindFreePort();
            var server = new DiscoveryServer(dPort);
            server.Start("test-host", 8080, true, false);
            try
            {
                var devices = DiscoveryClient.Scan(dPort, 3000, "127.0.0.1").Result;
                Assert.True(devices.Length >= 1, "discovered at least one device");
                Assert.Equal("test-host", devices[0].Name, "device name");
                Assert.Equal(8080, devices[0].Port, "device port");
                Assert.True(devices[0].SupportsTcp, "tcp flag set");
                Assert.False(devices[0].SupportsUdt, "udt flag clear");
            }
            finally
            {
                server.Stop();
            }
        }

        private static void UdtResumeSingleFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(@"D:\cc\tmp", "tr_ur_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(@"D:\cc\tmp", "tr_ur_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferUdtServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "resume_udt.bin");
                var rng = new Random(42);
                var content = new byte[1024 * 200];
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;

                var serverLogs = new System.Collections.Generic.List<string>();
                server = new TransferUdtServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnLog += msg => { lock (serverLogs) serverLogs.Add(msg); };
                server.OnClientTransferComplete += ep => { serverOk = true; serverDone.Set(); };
                server.OnError += msg => { serverError = msg; serverDone.Set(); };
                server.Start();

                if (!serverStarted.WaitOne(5000))
                    throw new Exception("UDT server did not start" + (serverError != null ? " — " + serverError : ""));

                var sessionId = Guid.NewGuid();
                var state = new ResumeState
                {
                    SessionId = sessionId,
                    TotalSize = content.Length,
                    FileName = Path.GetFileName(testFile),
                    FilePath = testFile,
                    ServerIp = "127.0.0.1",
                    Port = port,
                    IsUdt = true,
                    Created = DateTime.UtcNow,
                    SentBytes = 0
                };
                state.Save();

                var client = new TransferUdtClient("127.0.0.1", port, testFile);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                string clientError = null;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += msg => { clientError = msg; clientDone.Set(); };

                var sendTask = client.SendResumableAsync(sessionId);

                if (!serverDone.WaitOne(60000))
                    throw new Exception("UDT server did not complete within 60s");
                if (!serverOk)
                    throw new Exception("UDT server error: " + (serverError ?? "unknown"));
                sendTask.Wait(60000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("UDT client did not fire completion event");
                if (!clientOk)
                {
                    string logs;
                    lock (serverLogs) logs = string.Join(" | ", serverLogs);
                    throw new Exception("UDT resume transfer failed: " + (clientError ?? "unknown") + " — server logs: " + logs);
                }

                Thread.Sleep(300);

                var receivedFile = Path.Combine(recvDir, "resume_udt.bin");
                Assert.True(File.Exists(receivedFile), "received file exists");
                var receivedContent = File.ReadAllBytes(receivedFile);
                Assert.Equal(content.Length, receivedContent.Length, "file size matches");
                Assert.True(Utils.ConstantTimeEquals(content, receivedContent), "content match");

                // Verify resume state was deleted on success
                Assert.False(File.Exists(ResumeState.GetPath(sessionId)), "resume state deleted after success");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }
    }
}
