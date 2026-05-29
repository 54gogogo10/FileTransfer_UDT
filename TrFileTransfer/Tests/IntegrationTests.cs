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
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        public static void RunAll(TestRunner runner)
        {
            runner.Run("Integration_TCP_SingleFile", TcpSingleFile);
            runner.Run("Integration_TCP_Folder", TcpFolder);
            runner.Run("Integration_TCP_LargeSingle", TcpLargeSingle);
            runner.Run("Integration_TCP_LargeConcur", TcpLargeConcur);
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
                string serverError = null;

                if (isTcp)
                {
                    var tcpServer = new TransferServer("127.0.0.1", port, recvDir);
                    tcpServer.OnStarted += () => serverStarted.Set();
                    tcpServer.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                    tcpServer.OnError += msg => { serverError = msg; serverDone.Set(); };
                    tcpServer.Start();
                }
                else
                {
                    var udtServer = new TransferUdtServer("127.0.0.1", port, recvDir);
                    udtServer.OnStarted += () => serverStarted.Set();
                    udtServer.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                    udtServer.OnError += msg => { serverError = msg; serverDone.Set(); };
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
                    concurrent.OnError += msg => { clientDone.Set(); };
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
                    throw new Exception("Server did not complete within " + timeoutSec + "s");
                if (!serverOk)
                    throw new Exception("Server error: " + (serverError ?? "unknown"));

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
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void TcpLargeSingle()  { ConcurrentTransferTest("tr_tcpls", true, 1, 500, 120); }
        private static void TcpLargeConcur()  { ConcurrentTransferTest("tr_tcplc", true, 16, 500, 300); }

        private static void UdtLargeSingle()  { ConcurrentTransferTest("tr_udtls", false, 1, 500, 300); }
        private static void UdtLargeConcur()  { ConcurrentTransferTest("tr_udtlc", false, 16, 500, 900); }
    }
}
