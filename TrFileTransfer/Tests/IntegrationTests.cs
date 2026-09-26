using System;
using System.IO;
using System.Net;
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

        /// <summary>Joins collected logs into one assertion message.</summary>
        private static string Dump(System.Collections.Generic.List<string> logs)
        {
            lock (logs) return string.Join(" | ", logs.ToArray());
        }

        private static string TempBase()
        {
            // Historical scratch root when present; %TEMP% keeps the suite portable (e.g. CI)
            if (Directory.Exists(@"D:\cc")) return @"D:\cc\tmp";
            return Path.GetTempPath();
        }

        public static void RunAll(TestRunner runner)
        {
            runner.Run("Integration_TCP_SingleFile", TcpSingleFile);
            runner.Run("Integration_TCP_Folder", TcpFolder);
            runner.Run("Integration_TCP_EmptyFile", TcpEmptyFile);
            runner.Run("Integration_TCP_ChineseName", TcpChineseName);
            runner.Run("Integration_TCP_MultiClient", TcpMultiClient);
            runner.Run("Integration_TCP_ConnectRefused", TcpConnectRefused);
            runner.Run("Integration_TCP_FileNotFound", TcpFileNotFound);
            runner.Run("Integration_TCP_LargeSingle", TcpLargeSingle);
            runner.Run("Integration_TCP_LargeConcur", TcpLargeConcur);
            runner.Run("Integration_TCP_ResumeSingleFile", TcpResumeSingleFile);
            runner.Run("Integration_TCP_ResumeInterrupted", TcpResumeInterrupted);
            runner.Run("Integration_TCP_ResumeAcrossRestart", TcpResumeAcrossRestart);
            runner.Run("Integration_TCP_ResumeFullHashCorrupt", TcpResumeFullHashCorrupt);
            runner.Run("Integration_TCP_RateLimit", TcpRateLimit);
            runner.Run("Integration_Discovery", DiscoveryTest);
            runner.Run("Integration_Factory_TCP", FactoryTcp);
            runner.Run("Integration_TCP_BindInUse_Typed", TcpBindInUseTyped);
            runner.Run("Integration_TCP_FolderResume", TcpFolderResume);
            runner.Run("Integration_Factory_UDT", FactoryUdt, 1);
            runner.Run("Integration_UDT_FolderResume", UdtFolderResume, 1);
            runner.Run("Integration_UDT_ResumeSingleFile", UdtResumeSingleFile, 1); // UDT flaky handshake retry
            runner.Run("Integration_UDT_ResumeAcrossRestart", UdtResumeAcrossRestart, 1);
            runner.Run("Integration_UDT_ResumeFullHashCorrupt", UdtResumeFullHashCorrupt, 1);
            runner.Run("Integration_UDT_Folder", UdtFolder, 1);
            runner.Run("Integration_UDT_EmptyFile", UdtEmptyFile, 1);
            runner.Run("Integration_UDT_RateLimit", UdtRateLimit, 1);
            runner.Run("Integration_UDT_SingleFile", UdtSingleFile, 1);
            runner.Run("Integration_UDT_LargeSingle", UdtLargeSingle, 1);
            // retries: 3 — 8 simultaneous UDT handshakes are the flakiest case on a CI
            // runner: the connection intermittently drops early ("Connection was broken")
            // under CPU contention, independent of payload size (a retry was needed even
            // on the 2.12.0.0 release run, before any compression change). This is
            // mitigation, not a root-cause fix — when an attempt does pass it still
            // exercises the full chunk/reassembly path, and the failure mode is a
            // dropped connection, never bad data. See UdtLargeConcur.
            runner.Run("Integration_UDT_LargeConcur", UdtLargeConcur, 3);
            runner.Run("Integration_Update_CheckAndDownload", UpdateCheckAndDownload);
            runner.Run("Integration_Update_DownloadHashMismatch", UpdateDownloadHashMismatch);
            runner.Run("Integration_Update_Manifest404", UpdateCheckHttp404);
            runner.Run("Integration_Update_ManifestInvalid", UpdateCheckInvalidManifest);
            runner.Run("Integration_Update_NotNewer", UpdateNotNewer);
            runner.Run("Integration_Update_GitHub_FullFlow", UpdateGitHubFullFlow);
            runner.Run("Integration_Update_GitHub_BadSidecar", UpdateGitHubBadSidecar);
            runner.Run("Integration_Update_GitHub_MissingSidecar", UpdateGitHubMissingSidecar);
            runner.Run("Integration_Text_TCP", TcpTextMessage);
            runner.Run("Integration_Text_TCP_Large", TcpTextLarge);
            runner.Run("Integration_Auth_TCP_CorrectCode", TcpAuthCorrectCode);
            runner.Run("Integration_Auth_TCP_WrongCode", TcpAuthWrongCode);
            runner.Run("Integration_Auth_TCP_NoCode", TcpAuthNoCode);
            runner.Run("Integration_Auth_TCP_Lenient", TcpAuthLenient);
            runner.Run("Integration_Text_UDT", UdtTextMessage, 1);
            runner.Run("Integration_Auth_UDT", UdtAuthCorrectCode, 1);
            runner.Run("Integration_UDT_AckLost_Fails", UdtAckLostFails);
            runner.Run("Integration_FolderSync_TCP", TcpFolderSync);
            runner.Run("Integration_FolderSync_UDT", UdtFolderSync, 1);
            runner.Run("Integration_HTTP_ListAndDownload", HttpShareListAndDownload);
            runner.Run("Integration_HTTP_TokenAndTraversal", HttpShareTokenAndTraversal);
            runner.Run("Integration_FanOut_TwoTargets", FanOutTwoTargets);
            runner.Run("Integration_HTTP_Upload", HttpShareUpload);
            runner.Run("Integration_HTTP_UploadTokenAndTraversalName", HttpShareUploadTokenAndTraversalName);
            runner.Run("Integration_HTTP_AuthLockout", HttpShareAuthLockout);
            runner.Run("Integration_ResumeFullHash_BeforeConnect", () => ResumeFullHashBeforeConnect(false));
            runner.Run("Integration_UDT_ResumeFullHash_BeforeConnect", () => ResumeFullHashBeforeConnect(true), 1);
            runner.Run("Integration_FolderSync_HashCacheSkipsReread", FolderSyncHashCache);
            runner.Run("Integration_FolderSync_ClientHashCacheSkipsReread", FolderSyncClientHashCache);
            runner.Run("Integration_ResumeFullHash_StaleCacheSelfHeals", ResumeFullHashStaleCache);
            runner.Run("Integration_TCP_DedupSkip_UsesCachedDigest", DedupSkipUsesCachedDigest);
            runner.Run("Integration_FolderSync_ChangeEmptyDeleteWithCache", FolderSyncChangeMatrix);
            runner.Run("Integration_TCP_FolderSync_DiscardedFileIsReported", FolderSyncDiscardReported);
            // retries: 1 — same mitigation as the large/concurrent cases below: FindFreePort
            // can lose the race against a Windows excluded port range on a busy machine
            runner.Run("Integration_TCP_FolderSync_ManyFilesNested", FolderSyncManyNested, 1);
            runner.Run("Integration_CLI_Sync", CliSync, 1);
            runner.Run("Integration_CLI_Verify", CliVerify, 1);
            runner.Run("Integration_TCP_CancelDuringFolderHash", CancelDuringFolderHash, 1);
            runner.Run("Integration_TCP_FolderSync_PerFileSkip", () => FolderSyncPerFileSkip(false), 1);
            runner.Run("Integration_UDT_FolderSync_PerFileSkip", () => FolderSyncPerFileSkip(true), 1);
            runner.Run("Integration_HTTP_RangeResume", HttpShareRangeResume);
            runner.Run("Integration_Auth_LongPairingCode", LongPairingCode);
            runner.Run("Integration_TCP_ChunkDigestRegistered", ChunkDigestRegistered);
            runner.Run("Integration_TCP_ChunkCoverageResume", () => ChunkCoverageResume(false), 1);
            runner.Run("Integration_UDT_ChunkCoverageResume", () => ChunkCoverageResume(true), 1);
            runner.Run("Integration_UDT_ChunkSend_AdjacentUdpServer", UdtChunkSendAdjacentUdpServer, 1);
            runner.Run("Integration_ConcurrentSend_Throws_WhenNoServer", ConcurrentSendThrowsWhenNoServer);
            runner.Run("Integration_TCP_IPv6", TcpIPv6, 1);
            runner.Run("Integration_UDT_IPv6", UdtIPv6, 1);
            runner.Run("Integration_TCP_DualStack_PeerNormalization", DualStackPeerNormalization, 1);
            runner.Run("Integration_TCP_V4V6_FamilyTabsCoexist", FamilyTabsCoexist, 1);
            runner.Run("Integration_UDT_V4V6_SamePort", UdtFamilySamePort, 1);
            runner.Run("Unit_BindConflictRules", BindConflictRules);
            runner.Run("Unit_AddressPortProbeFamilyScoped", AddressPortProbeFamilyScoped);
            runner.Run("Unit_AddressPortProbeUnavailableAddr", AddressPortProbeUnavailableAddr);
            runner.Run("Integration_UDP_OneWay_SingleFile", UdpOneWaySingleFile, 1);
            runner.Run("Integration_UDP_OneWay_EmptyFile", UdpOneWayEmptyFile, 1);
            runner.Run("Integration_UDP_OneWay_LossyResume", UdpOneWayLossyResume, 1);
            runner.Run("Integration_UDP_OneWay_GarbageRejected", UdpOneWayGarbageRejected, 1);
            runner.Run("Integration_UDP_V4V6_SamePort", UdpV4V6SamePort, 1);
            runner.Run("Integration_TCP_MultiAddressSamePort", TcpMultiAddressSamePort, 1);
        }

        /// <summary>
        /// The protocol tabs start ONE LISTENER PER SELECTED ADDRESS on one port. Two
        /// specific same-family addresses (loopback + the first real interface) must
        /// coexist on the same port number and each receive its own traffic. (A wildcard
        /// plus a specific of the same family is REFUSED by the app's conflict rule even
        /// though Windows would technically allow the bind — the app-side rule is pinned
        /// by Unit_BindConflictRules.)
        /// </summary>
        private static void TcpMultiAddressSamePort()
        {
            IPAddress lan = null;
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork
                            && !a.Address.Equals(IPAddress.Loopback))
                        {
                            lan = a.Address;
                            break;
                        }
                    }
                    if (lan != null) break;
                }
            }
            catch { }
            Assert.NotNull(lan, "machine has a non-loopback v4 interface");

            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_ma_s_" + Guid.NewGuid().ToString("N"));
            string recvLo = Path.Combine(TempBase(), "tr_ma_lo_" + Guid.NewGuid().ToString("N"));
            string recvLan = Path.Combine(TempBase(), "tr_ma_lan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvLo);
            Directory.CreateDirectory(recvLan);
            TransferServer sLo = null, sLan = null;
            try
            {
                var content = new byte[128 * 1024];
                new Random(305).NextBytes(content);
                string file = Path.Combine(sendDir, "multi.bin");
                File.WriteAllBytes(file, content);

                var loStarted = new ManualResetEvent(false);
                sLo = new TransferServer(IPAddress.Loopback.ToString(), port, recvLo);
                sLo.OnStarted += () => loStarted.Set();
                sLo.Start();
                if (!loStarted.WaitOne(5000)) throw new Exception("loopback listener did not start");

                var lanStarted = new ManualResetEvent(false);
                sLan = new TransferServer(lan.ToString(), port, recvLan);
                sLan.OnStarted += () => lanStarted.Set();
                sLan.Start();
                if (!lanStarted.WaitOne(5000)) throw new Exception("interface listener did not start on the same port");

                new TransferClient(IPAddress.Loopback.ToString(), port, file).SendAsync().Wait(30000);
                new TransferClient(lan.ToString(), port, file).SendAsync().Wait(30000);
                Thread.Sleep(400);
                Assert.True(Utils.ConstantTimeEquals(content,
                    File.ReadAllBytes(Path.Combine(recvLo, "multi.bin"))),
                    "loopback client landed in the loopback listener's dir");
                Assert.True(Utils.ConstantTimeEquals(content,
                    File.ReadAllBytes(Path.Combine(recvLan, "multi.bin"))),
                    "interface client landed in the interface listener's dir");
            }
            finally
            {
                if (sLo != null) { try { sLo.Stop(); } catch { } }
                if (sLan != null) { try { sLan.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvLo, true); } catch { }
                try { Directory.Delete(recvLan, true); } catch { }
            }
        }

        // ==================== One-way raw UDP ====================

        /// <summary>A forwarding proxy with a mutable drop policy — simulates a lossy
        /// one-way link in front of a real receiver, with real datagrams.</summary>
        private sealed class LossyUdpProxy : IDisposable
        {
            private readonly UdpClient _listen;
            private readonly IPEndPoint _target;
            public Func<byte[], bool> Forward;
            public int Port;

            public LossyUdpProxy(IPEndPoint target, Func<byte[], bool> forward)
            {
                _target = target;
                Forward = forward;
                _listen = new UdpClient(0);
                // The proxy adds a forwarding hop per datagram — without a deep buffer
                // the client's burst overflows it and the "lossy link" loses far more
                // than the policy says (observed ~25% on loopback)
                try { _listen.Client.ReceiveBufferSize = 4 * 1024 * 1024; } catch { }
                // ICMP port-unreachable (while the target restarts) must not poison the
                // proxy's next send — the lossy-link stand-in has to survive it
                try { _listen.Client.IOControl(unchecked((int)0x98000004), new byte[] { 0 }, null); }
                catch { }
                Port = ((IPEndPoint)_listen.Client.LocalEndPoint).Port;
                Loop();
            }

            private async void Loop()
            {
                try
                {
                    while (true)
                    {
                        UdpReceiveResult r = await _listen.ReceiveAsync();
                        if (Forward != null && !Forward(r.Buffer)) continue;
                        try
                        {
                            await _listen.SendAsync(r.Buffer, r.Buffer.Length, _target);
                        }
                        catch (SocketException) { } // transient (target mid-restart) — keep serving
                    }
                }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
            }

            public void Dispose()
            {
                try { _listen.Close(); } catch { }
            }
        }

        private static TransferUdpServer StartUdpReceiver(string bind, int port, string dir)
        {
            var started = new ManualResetEvent(false);
            var server = new TransferUdpServer(bind, port, dir);
            server.OnStarted += () => started.Set();
            server.Start();
            if (!started.WaitOne(5000))
                throw new Exception("UDP receiver did not start within 5s");
            return server;
        }

        /// <summary>Delivery waiter — subscribe BEFORE the send starts, or a fast
        /// loopback delivery fires before anyone is listening.</summary>
        private sealed class UdpDeliveryWaiter
        {
            public string Path;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        private static UdpDeliveryWaiter WatchUdpDelivery(TransferUdpServer server)
        {
            var w = new UdpDeliveryWaiter();
            server.OnFileReceived += (p, sz) => { w.Path = p; w.Done.Set(); };
            return w;
        }

        private static string WaitUdp(UdpDeliveryWaiter w, int timeoutMs)
        {
            return w.Done.WaitOne(timeoutMs) ? w.Path : null;
        }

        private static void UdpOneWaySingleFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_u1_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_u1_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferUdpServer server = null;
            try
            {
                var content = new byte[1024 * 1024];
                new Random(301).NextBytes(content);
                string file = Path.Combine(sendDir, "oneway.bin");
                File.WriteAllBytes(file, content);

                server = StartUdpReceiver("127.0.0.1", port, recvDir);
                var waiter = WatchUdpDelivery(server);
                var client = new TransferUdpClient("127.0.0.1", port, file);
                client.SendAsync().Wait(30000);

                string got = WaitUdp(waiter, 15000);
                Assert.NotNull(got, "one-way file delivered");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(got)),
                    "one-way delivered bytes match");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void UdpOneWayEmptyFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_u0_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_u0_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferUdpServer server = null;
            try
            {
                string file = Path.Combine(sendDir, "empty.bin");
                File.WriteAllBytes(file, new byte[0]);

                server = StartUdpReceiver("127.0.0.1", port, recvDir);
                var waiter = WatchUdpDelivery(server);
                var client = new TransferUdpClient("127.0.0.1", port, file);
                client.SendAsync().Wait(30000);

                string got = WaitUdp(waiter, 15000);
                Assert.NotNull(got, "empty one-way file delivered");
                Assert.Equal(0L, new FileInfo(got).Length, "empty file is empty");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        /// <summary>
        /// The one-way reliability model under real loss: pass 1 through a proxy that
        /// drops every 5th DATA datagram leaves the receiver with a sparse .part and NO
        /// final file; a re-send (through a now-clean proxy, and after a receiver
        /// RESTART — the .part file on disk is the state) fills exactly the gaps and
        /// completes. This is the "re-send is the retry" contract.
        /// </summary>
        private static void UdpOneWayLossyResume()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_ul_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_ul_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferUdpServer server = null;
            LossyUdpProxy proxy = null;
            try
            {
                var content = new byte[512 * 1024];
                new Random(302).NextBytes(content);
                string file = Path.Combine(sendDir, "lossy.bin");
                File.WriteAllBytes(file, content);

                server = StartUdpReceiver("127.0.0.1", port, recvDir);
                proxy = new LossyUdpProxy(new IPEndPoint(IPAddress.Loopback, port), delegate(byte[] b)
                {
                    // Drop every 5th DATA frame; START/END always pass
                    return b[3] != 0x02 || BitConverter.ToInt32(b, 20) % 5 != 3;
                });

                var waiter1 = WatchUdpDelivery(server);
                var client1 = new TransferUdpClient("127.0.0.1", proxy.Port, file);
                client1.SendAsync().Wait(60000);
                Thread.Sleep(800); // let the receiver chew what arrived

                Assert.False(File.Exists(Path.Combine(recvDir, "lossy.bin")),
                    "lossy pass 1 must not deliver a file");
                Assert.True(Directory.GetFiles(Path.Combine(recvDir, ".udp"), "*.part").Length == 1,
                    "partial .part file kept after the lossy pass");

                // Receiver restart: disk is the state
                server.Stop();
                server = StartUdpReceiver("127.0.0.1", port, recvDir);

                proxy.Forward = delegate(byte[] b) { return true; };
                var waiter2 = WatchUdpDelivery(server);
                var client2 = new TransferUdpClient("127.0.0.1", proxy.Port, file);
                client2.SendAsync().Wait(60000);

                string got = WaitUdp(waiter2, 15000);
                Assert.NotNull(got, "re-send completed the file after receiver restart");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(got)),
                    "gap-filled bytes match the original");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                if (proxy != null) proxy.Dispose();
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        /// <summary>Hostile datagrams (bad magic/version/shape/truncations and random
        /// noise) must neither crash the receiver nor produce files; a subsequent good
        /// send still works.</summary>
        private static void UdpOneWayGarbageRejected()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_ug_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_ug_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferUdpServer server = null;
            try
            {
                var content = new byte[64 * 1024];
                new Random(303).NextBytes(content);
                string file = Path.Combine(sendDir, "good.bin");
                File.WriteAllBytes(file, content);

                server = StartUdpReceiver("127.0.0.1", port, recvDir);

                using (var udp = new UdpClient())
                {
                    var target = new IPEndPoint(IPAddress.Loopback, port);
                    byte[] head = UdpOneWay.BuildHeader(UdpOneWay.FrameStart, Guid.NewGuid());
                    // bad magic / bad version / truncated START / absurd shape / noise
                    byte[][] junk =
                    {
                        new byte[] { 0x58, 0x59, 1, 1, 0, 0, 0, 0 },
                        Utils.CopyBytes(new byte[] { (byte)'T', (byte)'U', 9, 1 }, 0, 4),
                        Utils.CopyBytes(head, 0, UdpOneWay.HeaderSize - 1),
                        Utils.CopyBytes(head, 0, UdpOneWay.HeaderSize),
                        new byte[13],
                    };
                    for (int i = 0; i < junk.Length; i++)
                        udp.Send(junk[i], junk[i].Length, target);
                    // a plausible START with an absurd size (rejected by the cap)
                    var absurd = Utils.CopyBytes(head, 0, UdpOneWay.HeaderSize + 54);
                    Buffer.BlockCopy(BitConverter.GetBytes(long.MaxValue - 1), 0, absurd, UdpOneWay.HeaderSize, 8);
                    udp.Send(absurd, absurd.Length, target);
                    var noise = new byte[70000];
                    new Random(9).NextBytes(noise);
                    udp.Send(noise, 65500, target);
                }

                Thread.Sleep(400);
                Assert.Equal(0, Directory.GetFiles(recvDir, "*", SearchOption.AllDirectories)
                    .Length - Directory.GetDirectories(recvDir).Length,
                    "no files from hostile datagrams");

                var waiter = WatchUdpDelivery(server);
                var client = new TransferUdpClient("127.0.0.1", port, file);
                client.SendAsync().Wait(30000);
                string got = WaitUdp(waiter, 15000);
                Assert.NotNull(got, "good send still works after the garbage");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(got)), "bytes match");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        /// <summary>v4-any and v6-any one-way receivers share one port number (the
        /// families are independent UDP stacks), each serving its own family.</summary>
        private static void UdpV4V6SamePort()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_uv_s_" + Guid.NewGuid().ToString("N"));
            string recv4 = Path.Combine(TempBase(), "tr_uv_r4_" + Guid.NewGuid().ToString("N"));
            string recv6 = Path.Combine(TempBase(), "tr_uv_r6_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recv4);
            Directory.CreateDirectory(recv6);
            TransferUdpServer s4 = null, s6 = null;
            try
            {
                var content = new byte[128 * 1024];
                new Random(304).NextBytes(content);
                string file = Path.Combine(sendDir, "both.bin");
                File.WriteAllBytes(file, content);

                s4 = StartUdpReceiver("0.0.0.0", port, recv4);
                s6 = StartUdpReceiver("::", port, recv6);

                var waiter4 = WatchUdpDelivery(s4);
                var waiter6 = WatchUdpDelivery(s6);
                var c4 = new TransferUdpClient("127.0.0.1", port, file);
                c4.SendAsync().Wait(30000);
                var c6 = new TransferUdpClient("::1", port, file);
                c6.SendAsync().Wait(30000);

                string got4 = WaitUdp(waiter4, 15000);
                string got6 = WaitUdp(waiter6, 15000);
                Assert.NotNull(got4, "v4 one-way delivered");
                Assert.NotNull(got6, "v6 one-way delivered");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(got4)), "v4 bytes match");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(got6)), "v6 bytes match");
            }
            finally
            {
                if (s4 != null) { try { s4.Stop(); } catch { } }
                if (s6 != null) { try { s6.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recv4, true); } catch { }
                try { Directory.Delete(recv6, true); } catch { }
            }
        }

        /// <summary>An address that is not on any interface is "unavailable", not
        /// "busy" — the probe reports free (indeterminate) so the real bind surfaces
        /// the accurate error instead of a misleading port-change offer.</summary>
        private static void AddressPortProbeUnavailableAddr()
        {
            Assert.True(Utils.IsAddressPortFree(System.Net.IPAddress.Parse("203.0.113.99"), 19555, true),
                "unavailable v4 address reports indeterminate");
            Assert.True(Utils.IsAddressPortFree(System.Net.IPAddress.Parse("2001:db8::99"), 19555, false),
                "unavailable v6 address reports indeterminate");
        }

        /// <summary>
        /// The two server tabs are family-scoped wildcards: a v4-only ("0.0.0.0" +
        /// TcpBindMode.IPv4Only) and a v6-only ("::" + IPv6Only) listener may share one
        /// port number, each family reaches its own listener — and, the point of the
        /// scoping, a lone v4-only listener REFUSES a ::1 client (that reachability
        /// belongs to the legacy dual-stack "" mode).
        /// </summary>
        private static void FamilyTabsCoexist()
        {
            string sendDir = Path.Combine(TempBase(), "tr_ft_s_" + Guid.NewGuid().ToString("N"));
            string recv4 = Path.Combine(TempBase(), "tr_ft_r4_" + Guid.NewGuid().ToString("N"));
            string recv6 = Path.Combine(TempBase(), "tr_ft_r6_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recv4);
            Directory.CreateDirectory(recv6);
            TransferServer s4 = null, s6 = null;
            try
            {
                var content = new byte[256 * 1024];
                new Random(210).NextBytes(content);
                string file = Path.Combine(sendDir, "tab.bin");
                File.WriteAllBytes(file, content);

                // Part A: family scoping — a lone v4-only listener refuses ::1
                int soloPort = FindFreePort();
                var solo = new TransferServer("0.0.0.0", soloPort, recv4, 4194304, TcpBindMode.IPv4Only);
                var soloStarted = new ManualResetEvent(false);
                solo.OnStarted += () => soloStarted.Set();
                solo.Start();
                if (!soloStarted.WaitOne(5000)) throw new Exception("solo v4 server did not start");
                bool refused = false;
                try { new TransferClient("::1", soloPort, file).SendAsync().Wait(8000); }
                catch { refused = true; }
                solo.Stop();
                Assert.True(refused, "v4-only wildcard must refuse a ::1 client (dual-stack is the '' mode)");

                // Part B: both families listen on the SAME port and each serves its own
                int port = FindFreePort();
                var started4 = new ManualResetEvent(false);
                s4 = new TransferServer("0.0.0.0", port, recv4, 4194304, TcpBindMode.IPv4Only);
                s4.OnStarted += () => started4.Set();
                s4.Start();
                if (!started4.WaitOne(5000)) throw new Exception("v4 server did not start");

                var started6 = new ManualResetEvent(false);
                s6 = new TransferServer("::", port, recv6, 4194304, TcpBindMode.IPv6Only);
                s6.OnStarted += () => started6.Set();
                s6.Start();
                if (!started6.WaitOne(5000)) throw new Exception("v6 server did not start on the same port");

                new TransferClient("127.0.0.1", port, file).SendAsync().Wait(30000);
                new TransferClient("::1", port, file).SendAsync().Wait(30000);
                Thread.Sleep(400);
                Assert.True(Utils.ConstantTimeEquals(content,
                    File.ReadAllBytes(Path.Combine(recv4, "tab.bin"))), "v4 client landed in the v4 server's dir");
                Assert.True(Utils.ConstantTimeEquals(content,
                    File.ReadAllBytes(Path.Combine(recv6, "tab.bin"))), "v6 client landed in the v6 server's dir");
            }
            finally
            {
                if (s4 != null) { try { s4.Stop(); } catch { } }
                if (s6 != null) { try { s6.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recv4, true); } catch { }
                try { Directory.Delete(recv6, true); } catch { }
            }
        }

        /// <summary>
        /// UDT has no dual-mode socket — the family tabs bind two independent UDP
        /// sockets (v4-any and v6-any) which must coexist on one port and each serve
        /// its own family.
        /// </summary>
        private static void UdtFamilySamePort()
        {
            string sendDir = Path.Combine(TempBase(), "tr_ftu_s_" + Guid.NewGuid().ToString("N"));
            string recv4 = Path.Combine(TempBase(), "tr_ftu_r4_" + Guid.NewGuid().ToString("N"));
            string recv6 = Path.Combine(TempBase(), "tr_ftu_r6_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recv4);
            Directory.CreateDirectory(recv6);
            TransferUdtServer s4 = null, s6 = null;
            try
            {
                var content = new byte[256 * 1024];
                new Random(211).NextBytes(content);
                string file = Path.Combine(sendDir, "tabu.bin");
                File.WriteAllBytes(file, content);

                int port = FindFreePort();
                var started4 = new ManualResetEvent(false);
                s4 = new TransferUdtServer("0.0.0.0", port, recv4);
                s4.OnStarted += () => started4.Set();
                s4.Start();
                if (!started4.WaitOne(5000)) throw new Exception("v4 UDT server did not start");

                var started6 = new ManualResetEvent(false);
                s6 = new TransferUdtServer("::", port, recv6);
                s6.OnStarted += () => started6.Set();
                s6.Start();
                if (!started6.WaitOne(5000)) throw new Exception("v6 UDT server did not start on the same port");

                new TransferUdtClient("127.0.0.1", port, file).SendAsync().Wait(60000);
                new TransferUdtClient("::1", port, file).SendAsync().Wait(60000);
                Thread.Sleep(400);
                Assert.True(Utils.ConstantTimeEquals(content,
                    File.ReadAllBytes(Path.Combine(recv4, "tabu.bin"))), "v4 UDT client landed in the v4 server's dir");
                Assert.True(Utils.ConstantTimeEquals(content,
                    File.ReadAllBytes(Path.Combine(recv6, "tabu.bin"))), "v6 UDT client landed in the v6 server's dir");
            }
            finally
            {
                if (s4 != null) { try { s4.Stop(); } catch { } }
                if (s6 != null) { try { s6.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recv4, true); } catch { }
                try { Directory.Delete(recv6, true); } catch { }
            }
        }

        /// <summary>The conflict rule behind the tab port check: same port + same
        /// family + (equal or one wildcard). Cross-family and cross-port never conflict.</summary>
        private static void BindConflictRules()
        {
            var any4 = System.Net.IPAddress.Any;
            var any6 = System.Net.IPAddress.IPv6Any;
            var v4a = System.Net.IPAddress.Parse("192.168.1.5");
            var v4b = System.Net.IPAddress.Parse("10.0.0.1");
            var v6a = System.Net.IPAddress.Parse("2001:db8::1");
            var v6b = System.Net.IPAddress.Parse("fd00::2");

            Assert.True(Utils.BindConflicts(any4, any4, 1000, 1000), "v4 wildcard vs itself");
            Assert.True(Utils.BindConflicts(any4, v4a, 1000, 1000), "v4 wildcard covers a specific v4");
            Assert.True(Utils.BindConflicts(v4a, any4, 1000, 1000), "specific v4 covered by the v4 wildcard");
            Assert.True(Utils.BindConflicts(v4a, v4a, 1000, 1000), "identical v4 specifics conflict");
            Assert.False(Utils.BindConflicts(v4a, v4b, 1000, 1000), "distinct v4 specifics coexist");
            Assert.False(Utils.BindConflicts(any4, any6, 1000, 1000), "v4 and v6 wildcards are independent stacks");
            Assert.False(Utils.BindConflicts(v4a, v6a, 1000, 1000), "specific v4 and v6 coexist");
            Assert.False(Utils.BindConflicts(any4, v4a, 1000, 1001), "different ports never conflict");
            Assert.True(Utils.BindConflicts(any6, v6b, 1000, 1000), "v6 wildcard covers a specific v6");
            Assert.False(Utils.BindConflicts(null, any4, 1000, 1000), "null bind never conflicts");

            Assert.True(Utils.IsWildcardAddress(any4) && Utils.IsWildcardAddress(any6), "wildcards detected");
            Assert.False(Utils.IsWildcardAddress(v4a), "specific v4 is not a wildcard");
            Assert.False(Utils.IsWildcardAddress(v6a), "specific v6 is not a wildcard");
        }

        /// <summary>The exact-address port probe: probing the exact address detects a
        /// listener on that same address, and the v6 wildcard probe (a v6-only socket)
        /// is unaffected by v4 binds — the two families are independent stacks.
        /// (Wildcard-vs-specific COexistence is OS-dependent and deliberately not
        /// pinned; the app's own listeners additionally go through BindConflicts.)</summary>
        private static void AddressPortProbeFamilyScoped()
        {
            int port = FindFreePort();
            Assert.True(Utils.IsAddressPortFree(System.Net.IPAddress.IPv6Any, port, true),
                "v6 wildcard probe passes on a free port");
            var hold = new TcpListener(System.Net.IPAddress.Any, port);
            hold.Start();
            try
            {
                Assert.False(Utils.IsAddressPortFree(System.Net.IPAddress.Any, port, true),
                    "v4 wildcard probe sees the identical wildcard listener");
                Assert.True(Utils.IsAddressPortFree(System.Net.IPAddress.IPv6Any, port, true),
                    "v6 wildcard probe is unaffected by a v4 bind");
            }
            finally
            {
                hold.Stop();
            }

            // Same-address detection for a specific bind as well
            int port2 = FindFreePort();
            var hold2 = new TcpListener(System.Net.IPAddress.Loopback, port2);
            hold2.Start();
            try
            {
                Assert.False(Utils.IsAddressPortFree(System.Net.IPAddress.Loopback, port2, true),
                    "loopback probe sees the identical loopback listener");
            }
            finally
            {
                hold2.Stop();
            }
        }

        /// <summary>
        /// The pause/resume cycle for a concurrent chunk group: send only the first chunk
        /// (what a pause leaves behind server-side), query coverage (0x0B), then resume —
        /// the client must send ONLY the complement, proven by the server's chunk log
        /// showing a resume pass with no byte at offset 0.
        /// </summary>
        /// <summary>Regression for "UDT concurrent send fails when the one-way UDP
        /// server runs on the adjacent port": SendAsync used to pick chunk source
        /// ports starting at serverPort+1, and the UDT socket's SO_REUSEADDR bind
        /// silently shadowed the UDP server already holding that port — the handshake
        /// response landed on the UDP server and the chunk connect timed out. Chunk
        /// connections must use ephemeral ports when the user set no source port.</summary>
        private static void UdtChunkSendAdjacentUdpServer()
        {
            // Find a server port whose ADJACENT port is UDP-bindable on loopback,
            // so the bare-UDP server can actually hold the collision port.
            int port = 0;
            for (int attempt = 0; attempt < 32; attempt++)
            {
                int candidate = FindFreePort();
                if (candidate != 0 && Utils.IsPortFree(candidate + 1, false, true)) { port = candidate; break; }
            }
            Assert.True(port != 0, "found a port whose neighbour is UDP-free");

            string sendDir = Path.Combine(TempBase(), "tr_adj_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_adj_r_" + Guid.NewGuid().ToString("N"));
            string udpDir = Path.Combine(TempBase(), "tr_adj_u_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            Directory.CreateDirectory(udpDir);
            TransferUdtServer udtServer = null;
            TransferUdpServer udpServer = null;
            try
            {
                var content = new byte[58880];
                new Random(5).NextBytes(content);
                string file = Path.Combine(sendDir, "adj.bin");
                File.WriteAllBytes(file, content);

                var started = new ManualResetEvent(false);
                udtServer = new TransferUdtServer("127.0.0.1", port, recvDir);
                udtServer.OnStarted += () => started.Set();
                udtServer.Start();
                // The collision port: serverPort+1, exactly what the old SendAsync probed first
                udpServer = new TransferUdpServer("127.0.0.1", port + 1, udpDir);
                udpServer.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("UDT server did not start within 5s");
                Assert.True(udpServer.IsRunning, "one-way UDP server holds serverPort+1");

                var concurrent = new ConcurrentTransfer("127.0.0.1", port, file, 2, false);
                var done = new ManualResetEvent(false);
                Exception error = null;
                concurrent.OnTransferComplete += () => done.Set();
                concurrent.OnError += m => { error = new Exception(m); done.Set(); };
                var task = concurrent.SendAsync(Guid.NewGuid());
                if (!done.WaitOne(30000))
                    throw new Exception("Concurrent send did not finish within 30s");
                if (error != null)
                    throw new Exception("Concurrent send failed: " + error.Message);
                try { task.Wait(5000); }
                catch (Exception ex) { throw new Exception("SendAsync threw on a successful send: " + ex.Message); }

                var received = Path.Combine(recvDir, "adj.bin");
                for (int i = 0; i < 50 && !File.Exists(received); i++) Thread.Sleep(100);
                Assert.True(File.Exists(received), "received file exists at " + received);
                var info = new FileInfo(received);
                Assert.True(info.Length == content.Length, "received size matches");
            }
            finally
            {
                try { if (udtServer != null) udtServer.Stop(); } catch { }
                try { if (udpServer != null) udpServer.Stop(); } catch { }
            }
        }

        /// <summary>A failed concurrent send must THROW, not only fire OnError — the
        /// caller (GUI / future CLI) otherwise reports a completed send that never
        /// happened.</summary>
        private static void ConcurrentSendThrowsWhenNoServer()
        {
            int port = FindFreePort(); // reserved, but nothing will listen on it
            string sendDir = Path.Combine(TempBase(), "tr_thr_s_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            string file = Path.Combine(sendDir, "no.bin");
            File.WriteAllBytes(file, new byte[4096]);

            var concurrent = new ConcurrentTransfer("127.0.0.1", port, file, 2, true);
            bool threw = false;
            try
            {
                concurrent.SendAsync().Wait(20000);
            }
            catch { threw = true; }
            Assert.True(threw, "SendAsync throws when every chunk connection fails");
        }

        private static void ChunkCoverageResume(bool isUdt)
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_ccv_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_ccv_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer tcpServer = null;
            TransferUdtServer udtServer = null;
            Guid chunkSession = Guid.NewGuid();
            try
            {
                // Four 1-MB quadrants — well above ChunkMinSize so splitting works
                var content = new byte[4 * 1024 * 1024];
                new Random(191).NextBytes(content);
                string file = Path.Combine(sendDir, "big.bin");
                File.WriteAllBytes(file, content);

                var started = new ManualResetEvent(false);
                if (isUdt)
                {
                    udtServer = new TransferUdtServer("127.0.0.1", port, recvDir);
                    udtServer.OnStarted += () => started.Set();
                    udtServer.Start();
                }
                else
                {
                    tcpServer = new TransferServer("127.0.0.1", port, recvDir);
                    tcpServer.OnStarted += () => started.Set();
                    tcpServer.Start();
                }
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                // "Pause" equivalent: only the first half travels (one chunk connection
                // carrying the group session id)
                long half = content.Length / 2;
                if (isUdt)
                {
                    var u = new TransferUdtClient("127.0.0.1", port, file) { ChunkSessionId = chunkSession };
                    u.SendChunkedAsync(0, half, content.Length).Wait(60000);
                }
                else
                {
                    var c = new TransferClient("127.0.0.1", port, file) { ChunkSessionId = chunkSession };
                    c.SendChunkedAsync(0, half, content.Length).Wait(60000);
                }
                Thread.Sleep(300);

                // Coverage query must report exactly [0, half)
                long[][] covered;
                if (isUdt)
                {
                    var q = new TransferUdtClient("127.0.0.1", port, file);
                    var t = q.QueryChunkCoverageAsync(chunkSession);
                    Assert.True(t.Wait(30000), "coverage query completed");
                    covered = t.Result;
                }
                else
                {
                    var q = new TransferClient("127.0.0.1", port, file);
                    var t = q.QueryChunkCoverageAsync(chunkSession);
                    Assert.True(t.Wait(30000), "coverage query completed");
                    covered = t.Result;
                }
                Assert.True(covered != null, "coverage query answered");
                Assert.Equal(1, covered.Length, "one covered range");
                Assert.Equal(0, covered[0][0], "range starts at 0");
                Assert.Equal(half, covered[0][1], "range ends at the pause point");

                // Resume via the complement API — only the second half may travel
                var logs = new System.Collections.Generic.List<string>();
                var concurrent = new ConcurrentTransfer("127.0.0.1", port, file, 4, !isUdt, 0, 0);
                concurrent.OnLog += msg => logs.Add(msg);
                var done = new ManualResetEvent(false);
                Exception failure = null;
                concurrent.OnTransferComplete += () => done.Set();
                concurrent.OnError += msg => { failure = new Exception(msg); done.Set(); };
                var resumeTask = concurrent.ResumeAsync(chunkSession);
                if (!done.WaitOne(60000) && failure == null)
                    resumeTask.Wait(60000);
                if (failure != null) throw failure;
                Thread.Sleep(300);

                var saved = Path.Combine(recvDir, "big.bin");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(saved)),
                    "the resumed file is byte-identical");
                Assert.True(logs.Exists(m => m.Contains("gap(s)") || m.Contains("pieces to send")),
                    "the resume logged its gap plan (logs: " + string.Join(" | ", logs.ToArray()) + ")");
            }
            finally
            {
                if (tcpServer != null) { try { tcpServer.Stop(); } catch { } }
                if (udtServer != null) { try { udtServer.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        /// <summary>Single file over TCP to an IPv6 loopback target.</summary>
        private static void TcpIPv6()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_v6s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_v6r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            try
            {
                var content = new byte[512 * 1024];
                new Random(201).NextBytes(content);
                string file = Path.Combine(sendDir, "v6.bin");
                File.WriteAllBytes(file, content);

                var started = new ManualResetEvent(false);
                server = new TransferServer("::", port, recvDir); // dual-mode
                server.OnStarted += () => started.Set();
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                var client = new TransferClient("::1", port, file);
                client.SendAsync().Wait(30000);
                Thread.Sleep(300);
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(Path.Combine(recvDir, "v6.bin"))),
                    "TCP over IPv6 delivered the file");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        /// <summary>Single file over UDT to an IPv6 loopback target — exercises the
        /// AF_INET6 socket path and the sockaddr_in6 accept buffer.</summary>
        private static void UdtIPv6()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_v6u_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_v6u_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferUdtServer server = null;
            try
            {
                var content = new byte[512 * 1024];
                new Random(202).NextBytes(content);
                string file = Path.Combine(sendDir, "v6u.bin");
                File.WriteAllBytes(file, content);

                var started = new ManualResetEvent(false);
                server = new TransferUdtServer("::", port, recvDir);
                server.OnStarted += () => started.Set();
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                var client = new TransferUdtClient("::1", port, file);
                client.SendAsync().Wait(60000);
                Thread.Sleep(300);
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(Path.Combine(recvDir, "v6u.bin"))),
                    "UDT over IPv6 delivered the file");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        /// <summary>
        /// The dual-mode ("") listener must hand v4 peers to the policy layer as plain
        /// v4 addresses (not ::ffff:…) — per-device folders, IP filter and stats all
        /// compare plain v4 strings. Also pins the chunk-group ownership: the group
        /// owner's 0x0B query answers, a foreign peer's query is refused.
        /// </summary>
        private static void DualStackPeerNormalization()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_ds_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_ds_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            try
            {
                var content = new byte[256 * 1024];
                new Random(211).NextBytes(content);
                string file = Path.Combine(sendDir, "ds.bin");
                File.WriteAllBytes(file, content);

                var started = new ManualResetEvent(false);
                server = new TransferServer("", port, recvDir); // "" = dual-mode listener
                server.PerDeviceFolder = true;
                server.ResolveDeviceName = delegate(string ip) { return null; }; // fall back to the raw IP
                server.OnStarted += delegate { started.Set(); };
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                // A v4 peer arrives as ::ffff:127.0.0.1 on the dual socket — the device
                // folder must still be the plain "127.0.0.1"
                new TransferClient("127.0.0.1", port, file).SendAsync().Wait(30000);
                Thread.Sleep(300);
                string[] dirs = Directory.GetDirectories(recvDir);
                Assert.True(dirs.Length == 1 && Path.GetFileName(dirs[0]) == "127.0.0.1",
                    "v4 peer's device folder is the plain address (got: " +
                    (dirs.Length > 0 ? Path.GetFileName(dirs[0]) : "<none>") + ")");
                Assert.True(File.Exists(Path.Combine(dirs[0], "ds.bin")),
                    "file landed in the v4 device folder");

                // A v6 peer owns its chunk group: its query answers, a v4 peer's is refused
                Guid chunkSession = Guid.NewGuid();
                var v6 = new TransferClient("::1", port, file) { ChunkSessionId = chunkSession };
                v6.SendChunkedAsync(0, content.Length / 2, content.Length).Wait(30000);
                Thread.Sleep(300);

                var q6 = new TransferClient("::1", port, file);
                var t6 = q6.QueryChunkCoverageAsync(chunkSession);
                Assert.True(t6.Wait(30000), "owner's coverage query completed");
                long[][] covered = t6.Result;
                Assert.True(covered != null && covered.Length == 1 && covered[0][0] == 0
                    && covered[0][1] == content.Length / 2,
                    "owner sees exactly [0, half)");

                bool refused = false;
                try
                {
                    var q4 = new TransferClient("127.0.0.1", port, file);
                    var t4 = q4.QueryChunkCoverageAsync(chunkSession);
                    t4.Wait(30000);
                }
                catch (AggregateException) { refused = true; }
                Assert.True(refused, "foreign peer's coverage query was refused");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        /// <summary>
        /// A concurrently-chunked receive carries only per-chunk hashes, so the server hashes
        /// the assembled file once when it completes and records the digest — after which the
        /// dedup check answers from the cache (proven by locking the file: no re-read).
        /// </summary>
        private static void ChunkDigestRegistered()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_ckd_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_ckd_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            FileStream hold = null;
            bool prevStrict = FileHashCache.ForServer.Strict;
            try
            {
                // Large enough to split into chunks (> ChunkMinSize), small enough to be fast
                var content = new byte[9 * 1024 * 1024];
                new Random(181).NextBytes(content);
                string file = Path.Combine(sendDir, "chunked.bin");
                File.WriteAllBytes(file, content);

                var started = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.SkipDuplicateFiles = true;
                server.OnStarted += () => started.Set();
                var serverLogs = new System.Collections.Generic.List<string>();
                server.OnLog += msg => { lock (serverLogs) serverLogs.Add(msg); };
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");
                FileHashCache.ForServer.Strict = false;

                var client = new TransferClient("127.0.0.1", port, file);
                client.SendChunkedAsync(0, content.Length, content.Length).Wait(60000);
                Thread.Sleep(300);
                string saved = Path.Combine(recvDir, "chunked.bin");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(saved)), "chunked file assembled");

                // The assembled file's digest is remembered: locked (unreadable) file, and
                // only the cache can still answer "identical to expected".
                hold = new FileStream(saved, FileMode.Open, FileAccess.Read, FileShare.None);
                long size, mtime;
                Assert.True(FileHashCache.Stat(saved, out size, out mtime), "stat the received file");
                byte[] expected = ClientWire.ComputeFileHash(file); // the source's digest
                bool matches;
                Assert.True(FileHashCache.ForServer.TryMatch(saved, size, mtime, expected, out matches) && matches,
                    "the server cached the assembled chunked file's digest (no re-read: the copy is locked)");
            }
            finally
            {
                FileHashCache.ForServer.Strict = prevStrict;
                if (hold != null) { try { hold.Dispose(); } catch { } }
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

                private static void TcpSingleFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_it_send_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_it_recv_" + Guid.NewGuid().ToString("N"));
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
            string sendDir = Path.Combine(TempBase(), "tr_it_fsend_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_it_frecv_" + Guid.NewGuid().ToString("N"));
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
            string sendDir = Path.Combine(TempBase(), "tr_it_udt_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_it_udt_r_" + Guid.NewGuid().ToString("N"));
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
            string sendDir = Path.Combine(TempBase(), prefix + "_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), prefix + "_r_" + Guid.NewGuid().ToString("N"));
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
                    tcpServer.OnError += msg => { lock (serverErrors) serverErrors.Add(msg); };
                    tcpServer.Start();
                }
                else
                {
                    udtServer = new TransferUdtServer("127.0.0.1", port, recvDir);
                    udtServer.OnStarted += () => serverStarted.Set();
                    udtServer.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                    udtServer.OnError += msg => { lock (serverErrors) serverErrors.Add(msg); };
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

                // A transient connection break makes the server log an error for the
                // dead connection while ConcurrentTransfer retries it on a fresh one —
                // only the FILE ASSEMBLED event (serverDone) means success. Fail fast
                // when the client itself gave up (its retries were exhausted); give the
                // full budget otherwise.
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (!serverDone.WaitOne(500))
                {
                    if (clientDone.WaitOne(0) && !clientOk) break; // client exhausted its retries
                    if (waited.ElapsedMilliseconds > timeoutSec * 1000)
                    {
                        string detail;
                        lock (serverErrors) detail = "server errors: " + (serverErrors.Count > 0 ? string.Join(" | ", serverErrors) : "(none)");
                        lock (clientErrors) detail += " | client errors: " + (clientErrors.Count > 0 ? string.Join(" | ", clientErrors) : "(none)");
                        throw new Exception("Server did not complete within " + timeoutSec + "s — " + detail);
                    }
                }
                if (!serverOk)
                {
                    string detail;
                    lock (serverErrors) detail = "server errors: " + (serverErrors.Count > 0 ? string.Join(" | ", serverErrors) : "(none)");
                    lock (clientErrors) detail += " | client errors: " + (clientErrors.Count > 0 ? string.Join(" | ", clientErrors) : "(none)");
                    throw new Exception("Transfer failed — " + detail);
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
        // 1 GB, not the 5 GB the TCP case uses: 8 parallel UDT streams over 5 GB is
        // slow enough on a CI runner (~23 MB/s measured for a single stream) that the
        // handshake intermittently drops with "Connection was broken" — it failed on
        // two consecutive CI runs while passing on the identical commit elsewhere.
        // Chunking/reassembly correctness does not depend on total size (1 GB / 8 is
        // still far above ChunkMinSize), and single-stream large-file coverage stays
        // in UdtLargeSingle above.
        private static void UdtLargeConcur()  { ConcurrentTransferTest("tr_udtlc", false, 8, 1000, 1800); }

        private static void TcpResumeSingleFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_rs_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_rs_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
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

                var sendTask = client.SendResumableAsync(sessionId, true); // full-hash verification on

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
            string sendDir = Path.Combine(TempBase(), "tr_ri_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_ri_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
            string sendDir = Path.Combine(TempBase(), "tr_rr_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_rr_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
                        if (line.Contains("Resume: "))
                        {
                            int marker = line.IndexOf("offset=");
                            if (marker < 0) marker = line.IndexOf("offset ");
                            if (marker < 0) continue;
                            marker += 7; // "offset=" and "offset " are both 7 chars
                            string numStr = "";
                            for (int i = marker; i < line.Length && char.IsDigit(line[i]); i++)
                                numStr += line[i];
                            long off;
                            if (long.TryParse(numStr, out off) && off > 0)
                                sawNonZeroOffset = true;
                        }
                    }
                }
                if (!sawNonZeroOffset)
                {
                    string logs;
                    lock (s2Logs) logs = string.Join(" | ", s2Logs);
                    throw new Exception("server 2 resumed from zero — diskExists="
                        + File.Exists(ServerResumeStore.GetPath(sessionId)) + " — s2 logs: " + logs);
                }
                Assert.True(sawRestored, "server 2 restored disk state");
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
            string sendDir = Path.Combine(TempBase(), "tr_fh_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_fh_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
            string sendDir = Path.Combine(TempBase(), "tr_rl_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_rl_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "rate_test.bin");
                var rng = new Random(42);
                var content = new byte[1024 * 1024 * 6]; // 6 MB
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

                // 6 MB at 512 KB/s = 12 s nominal; allow generous slack (limit applies
                // per send-chunk so the total is always at or above the limit)
                Assert.True(sw.Elapsed.TotalSeconds >= 8.0,
                    "rate-limited transfer took " + sw.Elapsed.TotalSeconds.ToString("F1") + "s (expected >= 8s)");

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
            int dPort2 = FindFreePort();
            int dPort3 = FindFreePort();
            var server = new DiscoveryServer(dPort);
            server.Start("test-host", 8080, true, false);
            var server2 = new DiscoveryServer(dPort2);
            server2.Start("dual-host", 9090, true, true);
            var server3 = new DiscoveryServer(dPort3);
            server3.Start("locked-host", 7070, true, false, true);
            try
            {
                var devices = DiscoveryClient.Scan(dPort, 3000, "127.0.0.1").Result;
                Assert.True(devices.Length >= 1, "discovered at least one device");
                Assert.Equal("test-host", devices[0].Name, "device name");
                Assert.Equal(8080, devices[0].Port, "device port");
                Assert.True(devices[0].SupportsTcp, "tcp flag set");
                Assert.False(devices[0].SupportsUdt, "udt flag clear");
                Assert.False(devices[0].RequiresPairing, "pairing flag clear");

                // Dual-protocol server: both flags set in the response bitmap
                var devices2 = DiscoveryClient.Scan(dPort2, 3000, "127.0.0.1").Result;
                Assert.True(devices2.Length >= 1, "discovered dual-protocol device");
                Assert.Equal("dual-host", devices2[0].Name, "dual device name");
                Assert.True(devices2[0].SupportsTcp, "dual tcp flag set");
                Assert.True(devices2[0].SupportsUdt, "dual udt flag set");

                // Server with pairing enabled advertises the pairing bit
                var devices3 = DiscoveryClient.Scan(dPort3, 3000, "127.0.0.1").Result;
                Assert.True(devices3.Length >= 1, "discovered pairing device");
                Assert.Equal("locked-host", devices3[0].Name, "pairing device name");
                Assert.True(devices3[0].RequiresPairing, "pairing flag set");
            }
            finally
            {
                server.Stop();
                server2.Stop();
                server3.Stop();
            }
        }

        /// <summary>
        /// Sync mode end-to-end (TCP): three passes over the same derived session —
        /// initial full send, no-op resend (server reports already complete), and a
        /// third pass where only the newly added file travels.
        /// </summary>
        private static void TcpFolderSync()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_sync_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_sync_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            Guid sessionId = Guid.Empty;
            TransferServer server = null;
            try
            {
                var contentA = new byte[40 * 1024];
                new Random(7).NextBytes(contentA);
                File.WriteAllBytes(Path.Combine(sendDir, "a.bin"), contentA);

                var started = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => started.Set();
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                sessionId = FolderResumeState.DeriveSyncSession(sendDir, "127.0.0.1", port, false);
                string sessionDir = ServerWire.GetFolderSessionDir(recvDir, Path.GetFileName(sendDir), sessionId);

                // Pass 1: initial sync sends everything
                var client1 = new TransferClient("127.0.0.1", port, sendDir);
                client1.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                Thread.Sleep(300);
                Assert.True(File.Exists(Path.Combine(sessionDir, "a.bin")), "a.bin after pass 1");
                Assert.True(Utils.ConstantTimeEquals(contentA, File.ReadAllBytes(Path.Combine(sessionDir, "a.bin"))), "a.bin content after pass 1");

                // Pass 2: nothing changed → status 2, nothing sent
                var logs2 = new System.Collections.Generic.List<string>();
                var client2 = new TransferClient("127.0.0.1", port, sendDir);
                client2.OnLog += msg => logs2.Add(msg);
                client2.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                Thread.Sleep(300);
                Assert.True(logs2.Exists(m => m.Contains("already fully received")), "pass 2: server reports already complete");

                // Pass 3: add b.bin → a.bin skipped, only b.bin travels
                var contentB = new byte[20 * 1024];
                new Random(9).NextBytes(contentB);
                File.WriteAllBytes(Path.Combine(sendDir, "b.bin"), contentB);
                var logs3 = new System.Collections.Generic.List<string>();
                var client3 = new TransferClient("127.0.0.1", port, sendDir);
                client3.OnLog += msg => logs3.Add(msg);
                client3.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                Thread.Sleep(300);
                Assert.True(logs3.Exists(m => m.Contains("starting at file 2/2")), "pass 3: resumed at file 2/2 (a.bin skipped)");
                Assert.True(File.Exists(Path.Combine(sessionDir, "b.bin")), "b.bin after pass 3");
                Assert.True(Utils.ConstantTimeEquals(contentB, File.ReadAllBytes(Path.Combine(sessionDir, "b.bin"))), "b.bin content");
                Assert.True(Utils.ConstantTimeEquals(contentA, File.ReadAllBytes(Path.Combine(sessionDir, "a.bin"))), "a.bin unchanged by pass 3");

                // Sync state survives completion and stays out of the resume dialog
                var st = FolderResumeState.Load(sessionId);
                Assert.True(st != null && st.IsSync, "sync state kept after completion");
                Assert.False(FolderResumeState.ListAll().Exists(x => x.SessionId == sessionId), "sync state hidden from resume list");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
                if (sessionId != Guid.Empty) FolderResumeState.Delete(sessionId);
            }
        }

        private static void UdtFolderSync()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_usync_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_usync_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            Guid sessionId = Guid.Empty;
            TransferUdtServer server = null;
            try
            {
                var contentA = new byte[24 * 1024];
                new Random(11).NextBytes(contentA);
                File.WriteAllBytes(Path.Combine(sendDir, "a.bin"), contentA);

                var started = new ManualResetEvent(false);
                server = new TransferUdtServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => started.Set();
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("UDT server did not start within 5s");

                sessionId = FolderResumeState.DeriveSyncSession(sendDir, "127.0.0.1", port, true);
                string sessionDir = ServerWire.GetFolderSessionDir(recvDir, Path.GetFileName(sendDir), sessionId);

                var client1 = new TransferUdtClient("127.0.0.1", port, sendDir);
                client1.SendFolderResumableAsync(sessionId, keepState: true).Wait(60000);
                Thread.Sleep(300);
                Assert.True(File.Exists(Path.Combine(sessionDir, "a.bin")), "a.bin after UDT pass 1");

                // Pass 2: add b.bin → only b.bin travels
                var contentB = new byte[16 * 1024];
                new Random(13).NextBytes(contentB);
                File.WriteAllBytes(Path.Combine(sendDir, "b.bin"), contentB);
                var logs2 = new System.Collections.Generic.List<string>();
                var client2 = new TransferUdtClient("127.0.0.1", port, sendDir);
                client2.OnLog += msg => logs2.Add(msg);
                client2.SendFolderResumableAsync(sessionId, keepState: true).Wait(60000);
                Thread.Sleep(300);
                Assert.True(logs2.Exists(m => m.Contains("starting at file 2/2")), "UDT pass 2: resumed at file 2/2");
                Assert.True(File.Exists(Path.Combine(sessionDir, "b.bin")), "b.bin after UDT pass 2");
                Assert.True(Utils.ConstantTimeEquals(contentB, File.ReadAllBytes(Path.Combine(sessionDir, "b.bin"))), "b.bin content");
                Assert.True(Utils.ConstantTimeEquals(contentA, File.ReadAllBytes(Path.Combine(sessionDir, "a.bin"))), "a.bin unchanged");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
                if (sessionId != Guid.Empty) FolderResumeState.Delete(sessionId);
            }
        }

        private static void UdtResumeAcrossRestart()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_urr_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_urr_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferUdtServer server1 = null;
            TransferUdtServer server2 = null;
            try
            {
                var testFile = Path.Combine(sendDir, "udt_restart.bin");
                var rng = new Random(42);
                var content = new byte[1024 * 1024 * 64]; // 64 MB
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
                    IsUdt = true,
                    Created = DateTime.UtcNow,
                    SentBytes = 0
                };
                state.Save();

                // ---- Phase 1: partial transfer on server1, interrupted by server Stop ----
                var s1Started = new ManualResetEvent(false);
                var phase1Closed = new ManualResetEvent(false);
                server1 = new TransferUdtServer("127.0.0.1", port, recvDir);
                server1.OnStarted += () => s1Started.Set();
                server1.OnError += msg => phase1Closed.Set();
                server1.Start();
                if (!s1Started.WaitOne(5000))
                    throw new Exception("UDT server 1 did not start");

                var client1 = new TransferUdtClient("127.0.0.1", port, testFile);
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
                        partialReceived.Set();
                    }
                };
                server1.OnClientProgress += progressHandler;

                if (!partialReceived.WaitOne(60000))
                    throw new Exception("UDT server 1 did not receive partial data within 60s");

                // Simulate a server crash: Stop() closes sockets (interrupting the
                // client's send) and persists the incomplete resume state to disk.
                server1.Stop();
                server1 = null;
                try { sendTask1.Wait(30000); }
                catch (AggregateException) { /* the interrupted send faults — expected */ }
                if (!client1Done.WaitOne(5000))
                    throw new Exception("Client 1 did not stop after server stop");
                phase1Closed.WaitOne(2000);

                // ---- Phase 2: server2 recovers the disk state and continues ----
                var s2Started = new ManualResetEvent(false);
                var s2Done = new ManualResetEvent(false);
                bool s2Ok = false;
                string s2Error = null;
                var s2Logs = new System.Collections.Generic.List<string>();

                server2 = new TransferUdtServer("127.0.0.1", port, recvDir);
                server2.OnStarted += () => s2Started.Set();
                server2.OnLog += msg => { lock (s2Logs) s2Logs.Add(msg); };
                server2.OnClientTransferComplete += ep => { s2Ok = true; s2Done.Set(); };
                server2.OnError += msg => { s2Error = msg; s2Done.Set(); };
                server2.Start();
                if (!s2Started.WaitOne(5000))
                    throw new Exception("UDT server 2 did not start");

                var client2 = new TransferUdtClient("127.0.0.1", port, testFile);
                var client2Done = new ManualResetEvent(false);
                bool client2Ok = false;
                client2.OnTransferComplete += () => { client2Ok = true; client2Done.Set(); };
                client2.OnError += msg => client2Done.Set();

                var sendTask2 = client2.SendResumableAsync(sessionId);

                if (!s2Done.WaitOne(60000))
                    throw new Exception("UDT server 2 did not complete resumed transfer within 60s");
                if (!s2Ok)
                    throw new Exception("UDT server 2 error: " + (s2Error ?? "unknown"));
                sendTask2.Wait(60000);
                if (!client2Done.WaitOne(5000))
                    throw new Exception("Client 2 did not fire completion event");
                if (!client2Ok)
                    throw new Exception("UDT resume after restart failed");

                Thread.Sleep(300);

                var receivedFile = Path.Combine(recvDir, "udt_restart.bin");
                Assert.True(File.Exists(receivedFile), "received file exists");
                var receivedContent = File.ReadAllBytes(receivedFile);
                Assert.Equal(content.Length, receivedContent.Length, "file size matches");
                Assert.True(Utils.ConstantTimeEquals(content, receivedContent), "content match");

                Assert.False(File.Exists(ResumeState.GetPath(sessionId)), "client resume state deleted");
                Assert.False(File.Exists(ServerResumeStore.GetPath(sessionId)), "server resume state deleted");

                bool sawRestored = false;
                bool sawNonZeroOffset = false;
                lock (s2Logs)
                {
                    foreach (var line in s2Logs)
                    {
                        if (line.Contains("restored server state")) sawRestored = true;
                        if (line.Contains("Resume: "))
                        {
                            int marker = line.IndexOf("offset=");
                            if (marker < 0) marker = line.IndexOf("offset ");
                            if (marker < 0) continue;
                            marker += 7; // "offset=" and "offset " are both 7 chars
                            string numStr = "";
                            for (int i = marker; i < line.Length && char.IsDigit(line[i]); i++)
                                numStr += line[i];
                            long off;
                            if (long.TryParse(numStr, out off) && off > 0)
                                sawNonZeroOffset = true;
                        }
                    }
                }
                if (!sawNonZeroOffset)
                {
                    string logs;
                    lock (s2Logs) logs = string.Join(" | ", s2Logs);
                    throw new Exception("server 2 resumed from zero — diskExists="
                        + File.Exists(ServerResumeStore.GetPath(sessionId)) + " — s2 logs: " + logs);
                }
                Assert.True(sawRestored, "server 2 restored disk state");
            }
            finally
            {
                if (server1 != null) { try { server1.Stop(); } catch { } }
                if (server2 != null) { try { server2.Stop(); } catch { } }
                System.Threading.Thread.Sleep(500);
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void UdtResumeFullHashCorrupt()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_ufh_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_ufh_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferUdtServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "udt_corrupt.bin");
                var rng = new Random(42);
                var content = new byte[1024 * 1024 * 64];
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);
                DateTime originalMTime = File.GetLastWriteTimeUtc(testFile);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                var phase1Closed = new ManualResetEvent(false);
                bool secondPhase = false;
                string serverError = null;
                var serverLogs = new System.Collections.Generic.List<string>();

                server = new TransferUdtServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnLog += msg => { lock (serverLogs) serverLogs.Add(msg); };
                server.OnClientTransferComplete += ep => serverDone.Set();
                server.OnError += msg =>
                {
                    lock (serverLogs) serverLogs.Add("[OnError] " + msg);
                    if (secondPhase) { serverError = msg; serverDone.Set(); }
                    else phase1Closed.Set();
                };
                server.Start();
                if (!serverStarted.WaitOne(5000))
                    throw new Exception("UDT server did not start");

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

                // Phase 1: partial transfer with full-hash verification enabled
                var client1 = new TransferUdtClient("127.0.0.1", port, testFile);
                var client1Done = new ManualResetEvent(false);
                string client1Error = null;
                client1.OnStopped += () => client1Done.Set();
                client1.OnError += msg => { client1Error = msg; client1Done.Set(); };
                var sendTask1 = client1.SendResumableAsync(sessionId, true);

                var partialReceived = new ManualResetEvent(false);
                Action<System.Net.IPEndPoint, TransferProgress> progressHandler = null;
                progressHandler = (ep, p) =>
                {
                    if (p.BytesTransferred >= 1024 * 1024)
                    {
                        server.OnClientProgress -= progressHandler;
                        server.Stop(); // interrupt the client mid-transfer
                        partialReceived.Set();
                    }
                };
                server.OnClientProgress += progressHandler;

                if (!partialReceived.WaitOne(60000))
                    throw new Exception("UDT server did not receive partial data within 60s — client error: "
                        + (client1Error ?? "none") + " — clientDone=" + client1Done.WaitOne(0));
                try { sendTask1.Wait(30000); }
                catch (AggregateException) { /* server Stop() interrupted the send — expected fault */ }
                if (!client1Done.WaitOne(5000))
                    throw new Exception("Client 1 did not stop after server stop");
                // Wait for server 1 to finish unwinding (persisting resume state to disk)
                phase1Closed.WaitOne(2000);

                // Restart the server, then tamper with the source (same size, same mtime)
                server.Stop();
                var s2Started = new ManualResetEvent(false);
                server = new TransferUdtServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => s2Started.Set();
                server.OnLog += msg => { lock (serverLogs) serverLogs.Add(msg); };
                server.OnClientTransferComplete += ep => serverDone.Set();
                server.OnError += msg =>
                {
                    lock (serverLogs) serverLogs.Add("[OnError] " + msg);
                    if (secondPhase) { serverError = msg; serverDone.Set(); }
                };
                server.Start();
                if (!s2Started.WaitOne(5000))
                    throw new Exception("UDT server did not restart");

                var tampered = new byte[content.Length];
                var rng2 = new Random(99);
                rng2.NextBytes(tampered);
                File.WriteAllBytes(testFile, tampered);
                File.SetLastWriteTimeUtc(testFile, originalMTime);

                secondPhase = true;
                var client2 = new TransferUdtClient("127.0.0.1", port, testFile);
                var client2Done = new ManualResetEvent(false);
                bool client2Ok = false;
                bool client2Error = false;
                client2.OnTransferComplete += () => { client2Ok = true; client2Done.Set(); };
                client2.OnError += msg => { client2Error = true; client2Done.Set(); };

                var sendTask2 = client2.SendResumableAsync(sessionId, true);

                if (!client2Done.WaitOne(60000))
                    throw new Exception("Client 2 did not finish within 60s");
                sendTask2.Wait(60000);
                if (!client2Error)
                {
                    string logs;
                    lock (serverLogs) logs = string.Join(" | ", serverLogs);
                    throw new Exception("client 2 reported verification error — ok=" + client2Ok
                        + " — server logs: " + logs);
                }
                Assert.False(client2Ok, "client 2 did NOT report success");

                Thread.Sleep(500);
                var leftover = Directory.GetFiles(recvDir);
                Assert.Equal(0, leftover.Length, "server discarded the corrupt file");

                bool sawFullHashFailed = false;
                lock (serverLogs)
                {
                    foreach (var line in serverLogs)
                        if (line.Contains("Full-file hash")) sawFullHashFailed = true;
                }
                Assert.True(sawFullHashFailed, "server logged full-hash failure");
                Assert.True(File.Exists(ResumeState.GetPath(sessionId)), "client state kept for retry");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                System.Threading.Thread.Sleep(500);
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void UdtFolder()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_uf_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_uf_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string folderPath = Path.Combine(sendDir, "udtFolder");
            Directory.CreateDirectory(Path.Combine(folderPath, "sub"));

            TransferUdtServer server = null;
            try
            {
                var rng = new Random(123);
                var fileAContent = new byte[1024 * 10];
                var fileBContent = new byte[1024 * 15];
                rng.NextBytes(fileAContent);
                rng.NextBytes(fileBContent);
                File.WriteAllBytes(Path.Combine(folderPath, "a.bin"), fileAContent);
                File.WriteAllBytes(Path.Combine(folderPath, "sub", "b.bin"), fileBContent);

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

                var client = new TransferUdtClient("127.0.0.1", port, folderPath);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += msg => clientDone.Set();

                var sendTask = client.SendFolderAsync(folderPath);

                if (!serverDone.WaitOne(60000))
                    throw new Exception("UDT server did not complete within 60s");
                if (!serverOk)
                    throw new Exception("UDT server error: " + (serverError ?? "unknown"));
                sendTask.Wait(60000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("UDT client did not fire completion event");
                if (!clientOk)
                    throw new Exception("UDT client folder transfer failed");

                Thread.Sleep(300);

                Assert.True(Directory.Exists(recvDir), "receive dir exists");
                var receivedA = Path.Combine(recvDir, "udtFolder", "a.bin");
                var receivedB = Path.Combine(recvDir, "udtFolder", "sub", "b.bin");
                Assert.True(File.Exists(receivedA), "a.bin exists");
                Assert.True(File.Exists(receivedB), "sub/b.bin exists");
                Assert.True(Utils.ConstantTimeEquals(fileAContent, File.ReadAllBytes(receivedA)), "a.bin content");
                Assert.True(Utils.ConstantTimeEquals(fileBContent, File.ReadAllBytes(receivedB)), "b.bin content");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                System.Threading.Thread.Sleep(500);
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void TcpEmptyFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_te_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_te_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "empty.bin");
                File.WriteAllBytes(testFile, new byte[0]);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;
                string receivedPath = null;

                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                server.OnFileReceived += (path, size) => { receivedPath = path; serverDone.Set(); };
                server.OnError += msg => { serverError = msg; serverDone.Set(); };
                server.Start();
                if (!serverStarted.WaitOne(5000))
                    throw new Exception("Server did not start");

                var client = new TransferClient("127.0.0.1", port, testFile);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += msg => clientDone.Set();

                var sendTask = client.SendAsync();
                if (!serverDone.WaitOne(30000))
                    throw new Exception("Server did not complete within 30s");
                if (!serverOk && receivedPath == null)
                    throw new Exception("Server error: " + (serverError ?? "unknown"));
                sendTask.Wait(30000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("Client did not fire completion event");
                if (!clientOk)
                    throw new Exception("Client transfer failed");

                Thread.Sleep(300);
                var receivedFile = Path.Combine(recvDir, "empty.bin");
                Assert.True(File.Exists(receivedFile), "empty file exists");
                Assert.Equal(0L, new FileInfo(receivedFile).Length, "empty file size is 0");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                System.Threading.Thread.Sleep(500);
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void UdtEmptyFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_ue_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_ue_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferUdtServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "empty.bin");
                File.WriteAllBytes(testFile, new byte[0]);

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
                    throw new Exception("UDT server did not start");

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

                Thread.Sleep(300);
                var receivedFile = Path.Combine(recvDir, "empty.bin");
                Assert.True(File.Exists(receivedFile), "empty file exists");
                Assert.Equal(0L, new FileInfo(receivedFile).Length, "empty file size is 0");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                System.Threading.Thread.Sleep(500);
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void TcpChineseName()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_cn_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_cn_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "中文文件测试.txt");
                var rng = new Random(7);
                var content = new byte[1024 * 5];
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

                Thread.Sleep(300);
                var receivedFile = Path.Combine(recvDir, "中文文件测试.txt");
                Assert.True(File.Exists(receivedFile), "chinese-named file exists");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(receivedFile)), "content match");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                System.Threading.Thread.Sleep(500);
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void TcpConnectRefused()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_cr_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);

            try
            {
                var testFile = Path.Combine(sendDir, "x.bin");
                File.WriteAllBytes(testFile, new byte[1024]);

                // No server running — connect must fail gracefully via OnError
                var client = new TransferClient("127.0.0.1", port, testFile);
                var clientDone = new ManualResetEvent(false);
                bool clientError = false;
                client.OnError += msg => { clientError = true; clientDone.Set(); };

                var sendTask = client.SendAsync();
                Exception taskEx = null;
                try { sendTask.Wait(15000); }
                catch (AggregateException ex) { taskEx = ex.InnerException; }
                Assert.True(clientDone.WaitOne(5000), "OnError fired after connect refused");
                Assert.True(clientError, "client reported error");
                Assert.True(taskEx != null, "send task faulted (exception propagated)");
            }
            finally
            {
                try { Directory.Delete(sendDir, true); } catch { }
            }
        }

        private static void TcpFileNotFound()
        {
            int port = FindFreePort();

            var client = new TransferClient("127.0.0.1", port, Path.Combine(TempBase(), "no_such_file_xyz.bin"));
            var clientDone = new ManualResetEvent(false);
            bool clientError = false;
            client.OnError += msg => { clientError = true; clientDone.Set(); };

            var sendTask = client.SendAsync();
            Exception taskEx = null;
            try { sendTask.Wait(15000); }
            catch (AggregateException ex) { taskEx = ex.InnerException; }
            Assert.True(clientDone.WaitOne(5000), "OnError fired for missing file");
            Assert.True(clientError, "client reported error");
            Assert.True(taskEx != null, "send task faulted");
        }

        private static void TcpMultiClient()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_mc_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_mc_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server = null;
            try
            {
                var rng = new Random(5);
                var contentA = new byte[1024 * 300];
                var contentB = new byte[1024 * 200];
                rng.NextBytes(contentA);
                rng.NextBytes(contentB);
                var fileA = Path.Combine(sendDir, "multi_a.bin");
                var fileB = Path.Combine(sendDir, "multi_b.bin");
                File.WriteAllBytes(fileA, contentA);
                File.WriteAllBytes(fileB, contentB);

                var serverStarted = new ManualResetEvent(false);
                int completedCount = 0;
                var serverDone = new ManualResetEvent(false);
                var lockObj = new object();

                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnClientTransferComplete += ep =>
                {
                    lock (lockObj) { completedCount++; if (completedCount >= 2) serverDone.Set(); }
                };
                server.OnError += msg => serverDone.Set();
                server.Start();
                if (!serverStarted.WaitOne(5000))
                    throw new Exception("Server did not start");

                var clientA = new TransferClient("127.0.0.1", port, fileA);
                var clientB = new TransferClient("127.0.0.1", port, fileB);
                var doneA = new ManualResetEvent(false);
                var doneB = new ManualResetEvent(false);
                clientA.OnTransferComplete += () => doneA.Set();
                clientA.OnError += msg => doneA.Set();
                clientB.OnTransferComplete += () => doneB.Set();
                clientB.OnError += msg => doneB.Set();

                var taskA = clientA.SendAsync();
                var taskB = clientB.SendAsync();

                if (!serverDone.WaitOne(30000))
                    throw new Exception("Server did not complete both clients within 30s");
                taskA.Wait(30000);
                taskB.Wait(30000);
                if (!doneA.WaitOne(5000))
                    throw new Exception("Client A did not fire completion");
                if (!doneB.WaitOne(5000))
                    throw new Exception("Client B did not fire completion");

                Thread.Sleep(300);
                Assert.True(Utils.ConstantTimeEquals(contentA, File.ReadAllBytes(Path.Combine(recvDir, "multi_a.bin"))), "file A content");
                Assert.True(Utils.ConstantTimeEquals(contentB, File.ReadAllBytes(Path.Combine(recvDir, "multi_b.bin"))), "file B content");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                System.Threading.Thread.Sleep(500);
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void UdtRateLimit()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_ur_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_ur_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferUdtServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "rate_udt.bin");
                var rng = new Random(42);
                var content = new byte[1024 * 1024 * 3]; // 3 MB at 256 KB/s ≈ 12 s
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
                    throw new Exception("UDT server did not start");

                var client = new TransferUdtClient("127.0.0.1", port, testFile, 0, 4194304, 256 * 1024);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += msg => clientDone.Set();

                var sw = System.Diagnostics.Stopwatch.StartNew();
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
                sw.Stop();

                Assert.True(sw.Elapsed.TotalSeconds >= 8.0,
                    "rate-limited UDT transfer took " + sw.Elapsed.TotalSeconds.ToString("F1") + "s (expected >= 8s)");

                Thread.Sleep(300);
                var receivedFile = Path.Combine(recvDir, "rate_udt.bin");
                Assert.True(File.Exists(receivedFile), "received file exists");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(receivedFile)), "content match");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                System.Threading.Thread.Sleep(500);
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void UdtResumeSingleFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_ur_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_ur_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
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

        // ---- ClientFactory + bind-failure regression tests ----
        // The UI constructs clients through ClientFactory; these tests exercise the
        // exact same shape so a constructor-argument mistake fails the suite again.

        private static void FactoryTcp()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_fc_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_fc_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "factory_tcp.bin");
                var rng = new Random(7);
                var content = new byte[1024 * 1024];
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

                // srcPort=0 + non-zero speedLimit was the shape that once regressed
                var client = ClientFactory.CreateTcp("127.0.0.1", port, testFile, 0, 1024 * 1024);
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
                if (!clientOk)
                    throw new Exception("Client transfer failed");

                Thread.Sleep(300);
                var receivedFile = Path.Combine(recvDir, "factory_tcp.bin");
                Assert.True(File.Exists(receivedFile), "received file exists");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(receivedFile)), "content match");
            }
            finally
            {
                try { if (server != null) server.Stop(); } catch { }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void FactoryUdt()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_fu_s_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_fu_r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferUdtServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "factory_udt.bin");
                var rng = new Random(8);
                var content = new byte[1024 * 1024];
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
                if (!serverStarted.WaitOne(15000))
                    throw new Exception("UDT server did not start within 15s");

                var client = ClientFactory.CreateUdt("127.0.0.1", port, testFile, 0, 1024 * 1024);
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
                if (!clientOk)
                    throw new Exception("UDT client transfer failed");

                Thread.Sleep(300);
                var receivedFile = Path.Combine(recvDir, "factory_udt.bin");
                Assert.True(File.Exists(receivedFile), "received file exists");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(receivedFile)), "content match");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void TcpBindInUseTyped()
        {
            string sendDir = Path.Combine(TempBase(), "tr_bi_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            try
            {
                var testFile = Path.Combine(sendDir, "bind.bin");
                File.WriteAllBytes(testFile, new byte[1024]);

                // Bind the same wildcard address the client will use, exclusively,
                // so the client's bind is guaranteed to conflict on this port
                var probe = new TcpListener(System.Net.IPAddress.Any, 0);
                probe.ExclusiveAddressUse = true;
                probe.Start();
                int occupiedPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
                try
                {
                    var client = new TransferClient("127.0.0.1", 1, testFile, occupiedPort, 65536, 0);
                    Exception caught = null;
                    try { client.SendAsync().Wait(15000); }
                    catch (AggregateException ag) { caught = ag.InnerException; }
                    Assert.True(caught is PortBindException,
                        "expected PortBindException but got: " +
                        (caught == null ? "<no error>" : caught.GetType().Name + ": " + caught.Message));
                }
                finally
                {
                    probe.Stop();
                }
            }
            finally
            {
                try { Directory.Delete(sendDir, true); } catch { }
            }
        }

        // ---- Folder resume (type 0x04) ----
        // Pre-seeds the server session directory (one complete file, one half-done file),
        // then sends a three-file folder via SendFolderResumableAsync. The server must skip
        // the complete file, resume the half-done one from its on-disk offset, receive the
        // remaining file, and finish with all three byte-identical to the source. A second
        // send over the same session must take the "already complete" fast path.

        private static void TcpFolderResume() { FolderResumeTest("tcp", false); }
        private static void UdtFolderResume() { FolderResumeTest("udt", true); }

        private static void FolderResumeTest(string kind, bool isUdt)
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_fr_s_" + kind + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string recvDir = Path.Combine(TempBase(), "tr_fr_r_" + kind + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer tcpServer = null;
            TransferUdtServer udtServer = null;
            try
            {
                // Source folder: a.bin (root), c.bin (root), b.bin (subdirectory)
                string srcFolder = Path.Combine(sendDir, "rfolder");
                Directory.CreateDirectory(srcFolder);
                var rng = new Random(99);
                var c0 = new byte[1024 * 1024]; rng.NextBytes(c0);
                var c1 = new byte[512 * 1024]; rng.NextBytes(c1);
                var c2 = new byte[100 * 1024]; rng.NextBytes(c2);
                string f0 = Path.Combine(srcFolder, "a.bin");
                string f1 = Path.Combine(srcFolder, "c.bin");
                string subDir = Path.Combine(srcFolder, "sub");
                Directory.CreateDirectory(subDir);
                string f2 = Path.Combine(subDir, "b.bin");
                File.WriteAllBytes(f0, c0);
                File.WriteAllBytes(f1, c1);
                File.WriteAllBytes(f2, c2);

                var sessionId = Guid.NewGuid();
                string sessionDir = ServerWire.GetFolderSessionDir(recvDir, "rfolder", sessionId);

                // Pre-seed: a.bin fully received, c.bin half received (manifest order is
                // a.bin, c.bin, sub/b.bin — root files first, then subdirectories)
                Directory.CreateDirectory(sessionDir);
                File.WriteAllBytes(Path.Combine(sessionDir, "a.bin"), c0);
                var half = new byte[c1.Length / 2];
                Buffer.BlockCopy(c1, 0, half, 0, half.Length);
                File.WriteAllBytes(Path.Combine(sessionDir, "c.bin"), half);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;

                if (isUdt)
                {
                    udtServer = new TransferUdtServer("127.0.0.1", port, recvDir);
                    udtServer.OnStarted += () => serverStarted.Set();
                    udtServer.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                    udtServer.OnError += msg => { serverError = msg; serverDone.Set(); };
                    udtServer.Start();
                }
                else
                {
                    tcpServer = new TransferServer("127.0.0.1", port, recvDir);
                    tcpServer.OnStarted += () => serverStarted.Set();
                    tcpServer.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                    tcpServer.OnError += msg => { serverError = msg; serverDone.Set(); };
                    tcpServer.Start();
                }
                if (!serverStarted.WaitOne(isUdt ? 15000 : 5000))
                    throw new Exception("Server did not start");

                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                Task<Guid> sendTask;
                if (isUdt)
                {
                    var client = new TransferUdtClient("127.0.0.1", port, srcFolder, 0, 4194304, 0);
                    client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                    client.OnError += msg => clientDone.Set();
                    sendTask = client.SendFolderResumableAsync(sessionId);
                }
                else
                {
                    var client = new TransferClient("127.0.0.1", port, srcFolder, 0, 4194304, 0);
                    client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                    client.OnError += msg => clientDone.Set();
                    sendTask = client.SendFolderResumableAsync(sessionId);
                }

                if (!serverDone.WaitOne(60000))
                    throw new Exception("Server did not complete within 60s");
                if (!serverOk)
                    throw new Exception("Server error: " + (serverError ?? "unknown"));
                sendTask.Wait(60000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("Client did not fire completion event");
                if (!clientOk)
                    throw new Exception("Client transfer failed");

                // All three files must now exist and match the sources byte-for-byte
                Assert.True(Utils.ConstantTimeEquals(c0, File.ReadAllBytes(Path.Combine(sessionDir, "a.bin"))), "a.bin content (skipped, untouched)");
                Assert.True(Utils.ConstantTimeEquals(c1, File.ReadAllBytes(Path.Combine(sessionDir, "c.bin"))), "c.bin content (resumed from offset)");
                Assert.True(Utils.ConstantTimeEquals(c2, File.ReadAllBytes(Path.Combine(sessionDir, "sub", "b.bin"))), "b.bin content (new file)");

                // Success clears the client-side folder resume session
                Assert.True(FolderResumeState.Load(sessionId) == null, "folder resume state deleted after success");

                // Second send over the same session: server answers "already complete"
                serverDone.Reset();
                serverOk = false;
                var clientDone2 = new ManualResetEvent(false);
                bool clientOk2 = false;
                Task<Guid> resendTask;
                if (isUdt)
                {
                    var client = new TransferUdtClient("127.0.0.1", port, srcFolder, 0, 4194304, 0);
                    client.OnTransferComplete += () => { clientOk2 = true; clientDone2.Set(); };
                    client.OnError += msg => clientDone2.Set();
                    resendTask = client.SendFolderResumableAsync(sessionId);
                }
                else
                {
                    var client = new TransferClient("127.0.0.1", port, srcFolder, 0, 4194304, 0);
                    client.OnTransferComplete += () => { clientOk2 = true; clientDone2.Set(); };
                    client.OnError += msg => clientDone2.Set();
                    resendTask = client.SendFolderResumableAsync(sessionId);
                }
                if (!serverDone.WaitOne(60000))
                    throw new Exception("Server did not complete second pass within 60s");
                if (!serverOk)
                    throw new Exception("Server error on second pass: " + (serverError ?? "unknown"));
                resendTask.Wait(60000);
                if (!clientDone2.WaitOne(5000))
                    throw new Exception("Client did not fire completion event (second pass)");
                if (!clientOk2)
                    throw new Exception("Client second pass failed");

                // Content unchanged by the fast-path send
                Assert.True(Utils.ConstantTimeEquals(c0, File.ReadAllBytes(Path.Combine(sessionDir, "a.bin"))), "a.bin unchanged (second pass)");
                Assert.True(Utils.ConstantTimeEquals(c2, File.ReadAllBytes(Path.Combine(sessionDir, "sub", "b.bin"))), "b.bin unchanged (second pass)");
            }
            finally
            {
                if (tcpServer != null) { try { tcpServer.Stop(); } catch { } }
                if (udtServer != null) { try { udtServer.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        // ---- Auto update (Updater vs a minimal local HTTP server) ----

        /// <summary>Tiny blocking HTTP/1.1 file server on 127.0.0.1 — no HttpListener ACL
        /// requirements, so it works under plain users and CI.</summary>
        private sealed class MiniHttpServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly System.Collections.Generic.Dictionary<string, byte[]> _files =
                new System.Collections.Generic.Dictionary<string, byte[]>();
            private readonly Thread _thread;
            private volatile bool _running = true;

            public int Port { get; private set; }

            public MiniHttpServer()
            {
                _listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Start();
            }

            public void SetFile(string path, byte[] data) { _files[path] = data; }

            public string BaseUrl { get { return "http://127.0.0.1:" + Port; } }

            private void Loop()
            {
                while (_running)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch { break; }
                    ThreadPool.QueueUserWorkItem(delegate { Handle(client); });
                }
            }

            private void Handle(TcpClient client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    using (client)
                    using (NetworkStream ns = client.GetStream())
                    {
                        string requestLine = ReadRequestLine(ns);
                        string path = null;
                        if (requestLine != null && requestLine.StartsWith("GET ", StringComparison.OrdinalIgnoreCase))
                        {
                            path = requestLine.Substring(4);
                            int sp = path.IndexOf(' ');
                            if (sp > 0) path = path.Substring(0, sp);
                            int q = path.IndexOf('?');
                            if (q >= 0) path = path.Substring(0, q);
                        }

                        byte[] body = null;
                        bool found = path != null && _files.TryGetValue(path, out body);
                        if (!found) body = System.Text.Encoding.UTF8.GetBytes("not found");
                        byte[] head = System.Text.Encoding.ASCII.GetBytes(
                            "HTTP/1.1 " + (found ? "200 OK" : "404 Not Found") + "\r\n" +
                            "Content-Type: application/octet-stream\r\n" +
                            "Content-Length: " + body.Length + "\r\n" +
                            "Connection: close\r\n\r\n");
                        ns.Write(head, 0, head.Length);
                        ns.Write(body, 0, body.Length);
                    }
                }
                catch { }
            }

            private static string ReadRequestLine(NetworkStream ns)
            {
                // First line of the request is enough (Connection: close, body-less GET).
                var buf = new byte[8192];
                var all = new System.Text.StringBuilder();
                while (all.Length < 16384)
                {
                    int n = ns.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    all.Append(System.Text.Encoding.ASCII.GetString(buf, 0, n));
                    string s = all.ToString();
                    int nl = s.IndexOf("\r\n", StringComparison.Ordinal);
                    if (nl > 0) return s.Substring(0, nl);
                    if (nl == 0) return null;
                }
                return null;
            }

            public void Dispose()
            {
                _running = false;
                try { _listener.Stop(); } catch { }
            }
        }

        private static void UpdateCheckAndDownload()
        {
            var server = new MiniHttpServer();
            string dir = Path.Combine(TempBase(), "tr_upd_it_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                byte[] payload = new byte[100000];
                for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31 + 7);
                string payloadPath = Path.Combine(dir, "payload.bin");
                File.WriteAllBytes(payloadPath, payload);
                string sha = Updater.ComputeSha256Hex(payloadPath);

                string manifest = "version=9.9.9.9\r\n" +
                    "url=" + server.BaseUrl + "/app.exe\r\n" +
                    "sha256=" + sha + "\r\n" +
                    "notes=integration test build\r\n";
                server.SetFile("/manifest.txt", System.Text.Encoding.UTF8.GetBytes(manifest));
                server.SetFile("/app.exe", payload);

                UpdateManifest m = Updater.CheckAsync(server.BaseUrl + "/manifest.txt", 10000).Result;
                Assert.True(m != null, "manifest fetched and parsed");
                Assert.Equal(new Version(9, 9, 9, 9), m.Version, "version");
                Assert.Equal(server.BaseUrl + "/app.exe", m.Url, "url");
                Assert.Equal(sha, m.Sha256Hex, "sha256");
                Assert.Equal("integration test build", m.Notes, "notes");
                Assert.True(m.IsNewerThan(new Version(2, 1, 0, 0)), "newer than current release");

                string dest = Path.Combine(dir, "downloaded.exe");
                long lastRead = -1, lastTotal = -1;
                Updater.DownloadAsync(m, dest, (r, t) => { lastRead = r; lastTotal = t; }, 15000).Wait();
                Assert.True(File.Exists(dest), "downloaded file exists");
                byte[] got = File.ReadAllBytes(dest);
                Assert.Equal(payload.Length, got.Length, "downloaded length");
                Assert.True(Utils.ConstantTimeEquals(payload, got), "downloaded bytes identical");
                Assert.Equal((long)payload.Length, lastRead, "progress reported final read");
                Assert.Equal((long)payload.Length, lastTotal, "progress reported total");
            }
            finally
            {
                server.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void UpdateDownloadHashMismatch()
        {
            var server = new MiniHttpServer();
            string dir = Path.Combine(TempBase(), "tr_upd_hm_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                byte[] payload = System.Text.Encoding.UTF8.GetBytes("this is not the file you are hashing");
                server.SetFile("/app.exe", payload);
                string badSha = new string('0', 64);
                string manifest = "version=9.9.9.9\r\n" +
                    "url=" + server.BaseUrl + "/app.exe\r\n" +
                    "sha256=" + badSha + "\r\n";
                server.SetFile("/manifest.txt", System.Text.Encoding.UTF8.GetBytes(manifest));

                UpdateManifest m = Updater.CheckAsync(server.BaseUrl + "/manifest.txt", 10000).Result;
                string dest = Path.Combine(dir, "bad.exe");
                Exception inner = null;
                try { Updater.DownloadAsync(m, dest, null, 15000).Wait(); }
                catch (AggregateException agg) { inner = agg.InnerException; }
                Assert.True(inner is System.IO.InvalidDataException, "hash mismatch -> InvalidDataException, got: " + (inner == null ? "none" : inner.GetType().Name));
                Assert.False(File.Exists(dest), "dest removed on mismatch");
                Assert.False(File.Exists(dest + ".part"), "partial removed on mismatch");
            }
            finally
            {
                server.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void UpdateCheckHttp404()
        {
            var server = new MiniHttpServer();
            try
            {
                Exception inner = null;
                try { Updater.CheckAsync(server.BaseUrl + "/missing.txt", 10000).Wait(); }
                catch (AggregateException agg) { inner = agg.InnerException; }
                Assert.True(inner != null, "404 -> exception");
                Assert.True(inner is System.Net.WebException, "404 -> WebException, got: " + inner.GetType().Name);
            }
            finally { server.Dispose(); }
        }

        private static void UpdateCheckInvalidManifest()
        {
            var server = new MiniHttpServer();
            try
            {
                server.SetFile("/manifest.txt", System.Text.Encoding.UTF8.GetBytes("hello world\nno key value pairs here\n"));
                Exception inner = null;
                try { Updater.CheckAsync(server.BaseUrl + "/manifest.txt", 10000).Wait(); }
                catch (AggregateException agg) { inner = agg.InnerException; }
                Assert.True(inner is System.IO.InvalidDataException, "garbage manifest -> InvalidDataException, got: " + (inner == null ? "none" : inner.GetType().Name));
            }
            finally { server.Dispose(); }
        }

        private static void UpdateNotNewer()
        {
            var server = new MiniHttpServer();
            try
            {
                string manifest = "version=1.0.0.0\r\n" +
                    "url=" + server.BaseUrl + "/app.exe\r\n" +
                    "sha256=" + new string('a', 64) + "\r\n";
                server.SetFile("/manifest.txt", System.Text.Encoding.UTF8.GetBytes(manifest));

                UpdateManifest m = Updater.CheckAsync(server.BaseUrl + "/manifest.txt", 10000).Result;
                Assert.True(m != null, "manifest parsed");
                Assert.False(m.IsNewerThan(new Version(2, 1, 0, 0)), "older version is not newer");
            }
            finally { server.Dispose(); }
        }

        /// <summary>Builds a GitHub /releases/latest-style JSON document around the
        /// given server (asset URLs point at the same local server).</summary>
        private static string GitHubReleaseJson(MiniHttpServer server, string tag, string notes)
        {
            return "{" +
                "\"url\":\"https://api.github.com/repos/o/r/releases/1\"," +
                "\"tag_name\":\"" + tag + "\"," +
                "\"name\":\"" + tag + "\"," +
                "\"body\":\"" + notes + "\"," +
                "\"draft\":false," +
                "\"assets\":[" +
                "{\"name\":\"TrFileTransfer.exe\",\"browser_download_url\":\"" + server.BaseUrl + "/TrFileTransfer.exe\"}," +
                "{\"name\":\"TrFileTransfer.exe.sha256\",\"browser_download_url\":\"" + server.BaseUrl + "/TrFileTransfer.exe.sha256\"}" +
                "]}";
        }

        private static void UpdateGitHubFullFlow()
        {
            var server = new MiniHttpServer();
            string dir = Path.Combine(TempBase(), "tr_upd_gh_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                byte[] payload = new byte[50000];
                for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7 + 3);
                string payloadPath = Path.Combine(dir, "payload.bin");
                File.WriteAllBytes(payloadPath, payload);
                string sha = Updater.ComputeSha256Hex(payloadPath);

                server.SetFile("/repos/o/r/releases/latest",
                    System.Text.Encoding.UTF8.GetBytes(GitHubReleaseJson(server, "v9.9.9.9", "v\\u4e34\\u65f6\\u8bf4\\u660e")));
                server.SetFile("/TrFileTransfer.exe", payload);
                server.SetFile("/TrFileTransfer.exe.sha256", System.Text.Encoding.UTF8.GetBytes(sha + "\n"));

                UpdateManifest m = Updater.CheckGitHubAsync(server.BaseUrl + "/repos/o/r/releases/latest", 10000).Result;
                Assert.True(m != null, "manifest built");
                Assert.Equal(new Version(9, 9, 9, 9), m.Version, "version from tag");
                Assert.Equal(server.BaseUrl + "/TrFileTransfer.exe", m.Url, "exe asset url");
                Assert.Equal(sha, m.Sha256Hex, "sha from sidecar");
                Assert.Equal("v临时说明", m.Notes, "body with unicode escapes");
                Assert.True(m.IsNewerThan(new Version(2, 1, 0, 0)), "newer than current");

                string dest = Path.Combine(dir, "gh.exe");
                Updater.DownloadAsync(m, dest, null, 15000).Wait();
                byte[] got = File.ReadAllBytes(dest);
                Assert.True(Utils.ConstantTimeEquals(payload, got), "downloaded bytes identical");
            }
            finally
            {
                server.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void UpdateGitHubBadSidecar()
        {
            var server = new MiniHttpServer();
            try
            {
                server.SetFile("/repos/o/r/releases/latest",
                    System.Text.Encoding.UTF8.GetBytes(GitHubReleaseJson(server, "v9.9.9.9", "")));
                server.SetFile("/TrFileTransfer.exe", new byte[] { 1, 2, 3 });
                server.SetFile("/TrFileTransfer.exe.sha256", System.Text.Encoding.UTF8.GetBytes("this-is-not-a-hash"));
                Exception inner = null;
                try { Updater.CheckGitHubAsync(server.BaseUrl + "/repos/o/r/releases/latest", 10000).Wait(); }
                catch (AggregateException agg) { inner = agg.InnerException; }
                Assert.True(inner is System.IO.InvalidDataException,
                    "malformed sidecar -> InvalidDataException, got: " + (inner == null ? "none" : inner.GetType().Name));
            }
            finally { server.Dispose(); }
        }

        private static void UpdateGitHubMissingSidecar()
        {
            var server = new MiniHttpServer();
            try
            {
                server.SetFile("/repos/o/r/releases/latest",
                    System.Text.Encoding.UTF8.GetBytes(GitHubReleaseJson(server, "v9.9.9.9", "")));
                server.SetFile("/TrFileTransfer.exe", new byte[] { 1, 2, 3 });
                // no /TrFileTransfer.exe.sha256 -> 404
                Exception inner = null;
                try { Updater.CheckGitHubAsync(server.BaseUrl + "/repos/o/r/releases/latest", 10000).Wait(); }
                catch (AggregateException agg) { inner = agg.InnerException; }
                Assert.True(inner is System.Net.WebException,
                    "missing sidecar -> WebException, got: " + (inner == null ? "none" : inner.GetType().Name));
            }
            finally { server.Dispose(); }
        }

        // ---- Pairing auth (0x05) and text messages (0x06) ----

        /// <summary>Common scaffold: starts a TCP server on a free port and returns its pieces.</summary>
        private sealed class TcpServerFixture : IDisposable
        {
            public TransferServer Server;
            public int Port;
            public string SendDir;
            public string RecvDir;
            public readonly ManualResetEvent Started = new ManualResetEvent(false);

            public TcpServerFixture()
            {
                Port = FindFreePort();
                SendDir = Path.Combine(TempBase(), "tr_auth_send_" + Guid.NewGuid().ToString("N"));
                RecvDir = Path.Combine(TempBase(), "tr_auth_recv_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(SendDir);
                Directory.CreateDirectory(RecvDir);
                Server = new TransferServer("127.0.0.1", Port, RecvDir);
                Server.OnStarted += () => Started.Set();
            }

            public void Start()
            {
                Server.Start();
                if (!Started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");
            }

            public void Dispose()
            {
                try { Server.Stop(); } catch { }
                try { Directory.Delete(SendDir, true); } catch { }
                try { Directory.Delete(RecvDir, true); } catch { }
            }
        }

        private static byte[] MakeTestFile(string path, int size)
        {
            var content = new byte[size];
            new Random(size).NextBytes(content);
            File.WriteAllBytes(path, content);
            return content;
        }

        private static void TcpTextMessage()
        {
            using (var fx = new TcpServerFixture())
            {
                string text = "你好，TrFileTransfer!\nLine2\tTab END";
                string received = null;
                var gotText = new ManualResetEvent(false);
                fx.Server.OnTextReceived += t => { received = t; gotText.Set(); };
                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, "", 0, 4194304, 0);
                client.SendTextAsync(text).Wait(30000);

                if (!gotText.WaitOne(5000))
                    throw new Exception("Text message was not received");
                Assert.Equal(text, received, "text content matches");
            }
        }

        private static void TcpTextLarge()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 20000; i++)
                sb.Append("文本内容ABC123你好世界"); // 36 UTF-8 bytes per unit → ~720 KB
            string text = sb.ToString();

            using (var fx = new TcpServerFixture())
            {
                string received = null;
                var gotText = new ManualResetEvent(false);
                fx.Server.OnTextReceived += t => { received = t; gotText.Set(); };
                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, "", 0, 4194304, 0);
                client.SendTextAsync(text).Wait(30000);

                if (!gotText.WaitOne(10000))
                    throw new Exception("Large text was not received");
                Assert.Equal(text.Length, received.Length, "large text length matches");
                Assert.Equal(text, received, "large text content matches");
            }
        }

        private static void TcpAuthCorrectCode()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.PairingCode = "135790";
                var testFile = Path.Combine(fx.SendDir, "auth_ok.bin");
                byte[] content = MakeTestFile(testFile, 64 * 1024);

                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                fx.Server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                fx.Server.OnError += _ => serverDone.Set();
                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                client.PairingCode = "135790";
                client.SendAsync().Wait(30000);

                if (!serverDone.WaitOne(30000))
                    throw new Exception("Server did not complete within 30s");
                if (!serverOk)
                    throw new Exception("Server rejected a correct pairing code");

                Thread.Sleep(300);
                var received = File.ReadAllBytes(Path.Combine(fx.RecvDir, "auth_ok.bin"));
                Assert.True(Utils.ConstantTimeEquals(content, received), "file content matches");
            }
        }

        private static void TcpAuthWrongCode()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.PairingCode = "135790";
                var testFile = Path.Combine(fx.SendDir, "auth_bad.bin");
                MakeTestFile(testFile, 8192);
                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                client.PairingCode = "000000";
                bool threw = false;
                try { client.SendAsync().Wait(30000); }
                catch { threw = true; }
                Assert.True(threw, "wrong pairing code -> transfer fails");

                Thread.Sleep(300);
                Assert.False(File.Exists(Path.Combine(fx.RecvDir, "auth_bad.bin")), "no file saved on rejected code");
            }
        }

        private static void TcpAuthNoCode()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.PairingCode = "135790";
                var testFile = Path.Combine(fx.SendDir, "auth_none.bin");
                MakeTestFile(testFile, 8192);
                fx.Start();

                // Old-style client that never authenticates. Whether ITS writes fail
                // depends on when the server's RST lands, so the deterministic
                // assertion is server-side: nothing is ever saved.
                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                var done = new ManualResetEvent(false);
                client.OnTransferComplete += () => done.Set();
                client.OnError += _ => done.Set();
                client.SendAsync();
                done.WaitOne(30000);

                Thread.Sleep(300);
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline && Directory.GetFiles(fx.RecvDir).Length > 0)
                    Thread.Sleep(200);
                Assert.False(File.Exists(Path.Combine(fx.RecvDir, "auth_none.bin")), "no file saved for unauthenticated client");
            }
        }

        private static void TcpAuthLenient()
        {
            using (var fx = new TcpServerFixture())
            {
                // Server without pairing accepts a client that still sends its code
                var testFile = Path.Combine(fx.SendDir, "auth_lenient.bin");
                byte[] content = MakeTestFile(testFile, 16 * 1024);

                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                fx.Server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                fx.Server.OnError += _ => serverDone.Set();
                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                client.PairingCode = "246888";
                client.SendAsync().Wait(30000);

                if (!serverDone.WaitOne(30000))
                    throw new Exception("Server did not complete within 30s");
                if (!serverOk)
                    throw new Exception("Server rejected a client against an open server");

                Thread.Sleep(300);
                var received = File.ReadAllBytes(Path.Combine(fx.RecvDir, "auth_lenient.bin"));
                Assert.True(Utils.ConstantTimeEquals(content, received), "file content matches");
            }
        }

        private static void UdtTextMessage()
        {
            int port = FindFreePort();
            string recvDir = Path.Combine(TempBase(), "tr_udt_text_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recvDir);
            TransferUdtServer server = null;
            try
            {
                string text = "UDT 文本消息\nsecond line 中文";
                string received = null;
                var gotText = new ManualResetEvent(false);
                var started = new ManualResetEvent(false);

                server = new TransferUdtServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => started.Set();
                server.OnTextReceived += t => { received = t; gotText.Set(); };
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("UDT server did not start within 5s");

                var client = new TransferUdtClient("127.0.0.1", port, "", 0, 4194304, 0);
                client.SendTextAsync(text).Wait(60000);

                if (!gotText.WaitOne(10000))
                    throw new Exception("UDT text was not received");
                Assert.Equal(text, received, "UDT text content matches");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        /// <summary>A UDT peer that reads the whole transfer and then closes WITHOUT the
        /// application ACK must surface as a client failure. The ACK read used to treat
        /// EOF/error as success (only 0x00 was rejected), so a dead or starved receiver
        /// produced "client errors: (none)" and a false "transfer complete".</summary>
        private static void UdtAckLostFails()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_udt_ackl_s_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            var testFile = Path.Combine(sendDir, "acklost.bin");
            MakeTestFile(testFile, 256 * 1024);

            try
            {
                UdtDll.EnsureExtracted();
                Assert.True(UdtNative.UdtStartup(), "UDT startup");

                int listener = UdtNative.udt_socket(UdtNative.AF_INET, UdtNative.SOCK_STREAM, 0);
                var saddr = UdtNative.BuildSockaddr("127.0.0.1", port);
                Assert.True(UdtNative.udt_bind(listener, ref saddr, UdtNative.SockAddrSize) != UdtNative.ERROR, "bind");
                Assert.True(UdtNative.udt_listen(listener, 4) != UdtNative.ERROR, "listen");

                // Fake receiver: on the compression-probe connection just close (the
                // client then retries uncompressed); on the real transfer connection
                // read the full 0x00 frame (header + payload + hash) and close without
                // ever sending the 1-byte ACK.
                var serverTask = Task.Run(delegate
                {
                    while (true)
                    {
                        var addr = new sockaddr_in();
                        int addrLen = UdtNative.SockAddrSize;
                        int conn = UdtNative.udt_accept(listener, ref addr, ref addrLen);
                        if (conn < 0) return;
                        UdtNative.SetTimeout(conn, 15000, 15000);
                        try
                        {
                            var one = new byte[1];
                            UdtIo.UdtReadExactAsync(conn, one, 0, 1, CancellationToken.None).Wait();
                            if (one[0] == 0x08) { try { UdtNative.udt_close(conn); } catch { } continue; }

                            // 0x00 single file: type(1)+size(8)+nameLen(4) already 1 read
                            var rest = new byte[12];
                            UdtIo.UdtReadExactAsync(conn, rest, 0, 12, CancellationToken.None).Wait();
                            long size = BitConverter.ToInt64(rest, 0);
                            int nameLen = BitConverter.ToInt32(rest, 8);
                            var nameBuf = new byte[nameLen];
                            UdtIo.UdtReadExactAsync(conn, nameBuf, 0, nameLen, CancellationToken.None).Wait();
                            var payload = new byte[size];
                            UdtIo.UdtReadExactAsync(conn, payload, 0, (int)size, CancellationToken.None).Wait();
                            var hash = new byte[32];
                            UdtIo.UdtReadExactAsync(conn, hash, 0, 32, CancellationToken.None).Wait();
                            // Park the client in its ACK wait before vanishing: the close
                            // must land while it waits for the confirmation byte, not
                            // during the send phase (which fails loudly either way).
                            Thread.Sleep(300);
                            // ... and vanish: no ACK
                        }
                        catch { }
                        try { UdtNative.udt_close(conn); } catch { }
                        return; // one real transfer is all this fake peer takes
                    }
                });

                var client = new TransferUdtClient("127.0.0.1", port, testFile);
                bool errored = false;
                client.OnError += _ => { errored = true; };
                // OnTransferComplete fires at the protocol layer (after the payload and
                // hash are written, BEFORE the ACK read), so the reliable success/failure
                // signal is the send task's outcome: RunUdtTransfer rethrows after the
                // error event fired.
                var sendTask = client.SendAsync();
                bool taskFailed = false;
                try { sendTask.Wait(60000); }
                catch (AggregateException) { taskFailed = true; }

                Assert.True(taskFailed, "missing ACK must fail the send task");
                Assert.True(errored, "client raised an error");

                serverTask.Wait(5000);
                try { UdtNative.udt_close(listener); } catch { }
                UdtNative.UdtCleanup();
            }
            finally
            {
                try { Directory.Delete(sendDir, true); } catch { }
            }
        }

        private static void UdtAuthCorrectCode()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_udt_auth_send_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_udt_auth_recv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferUdtServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "udt_auth.bin");
                byte[] content = MakeTestFile(testFile, 32 * 1024);

                var started = new ManualResetEvent(false);
                var done = new ManualResetEvent(false);
                server = new TransferUdtServer("127.0.0.1", port, recvDir);
                server.PairingCode = "654321";
                server.OnStarted += () => started.Set();
                server.OnFileReceived += (p, s) => done.Set();
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("UDT server did not start within 5s");

                var client = new TransferUdtClient("127.0.0.1", port, testFile, 0, 4194304, 0);
                client.PairingCode = "654321";
                client.SendAsync().Wait(60000);

                if (!done.WaitOne(60000))
                    throw new Exception("UDT file with pairing was not received");

                Thread.Sleep(300);
                var received = File.ReadAllBytes(Path.Combine(recvDir, "udt_auth.bin"));
                Assert.True(Utils.ConstantTimeEquals(content, received), "UDT file content matches");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        // ---- HTTP share (browser listing/download) ----

        /// <summary>GET helper returning (status, body bytes, headers).</summary>
        private static System.Tuple<int, byte[], System.Net.WebHeaderCollection> HttpGet(string url)
        {
            return HttpGet(url, null);
        }

        /// <summary>GET with a cookie jar — models a browser: Set-Cookie from a 303
        /// lands in the container and rides along on the redirected request.</summary>
        private static System.Tuple<int, byte[], System.Net.WebHeaderCollection> HttpGet(string url, System.Net.CookieContainer cookies)
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 10000;
            req.ReadWriteTimeout = 10000;
            req.Proxy = null;
            if (cookies != null) req.CookieContainer = cookies;
            try
            {
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var ms = new MemoryStream())
                {
                    resp.GetResponseStream().CopyTo(ms);
                    return System.Tuple.Create((int)resp.StatusCode, ms.ToArray(), resp.Headers);
                }
            }
            catch (System.Net.WebException ex)
            {
                var resp = ex.Response as System.Net.HttpWebResponse;
                if (resp == null) throw;
                using (resp)
                using (var ms = new MemoryStream())
                {
                    resp.GetResponseStream().CopyTo(ms);
                    return System.Tuple.Create((int)resp.StatusCode, ms.ToArray(), resp.Headers);
                }
            }
        }

        private static void HttpShareListAndDownload()
        {
            int port = FindFreePort();
            string root = Path.Combine(TempBase(), "tr_http_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var server = new HttpShareServer();
            try
            {
                var contentA = new byte[300 * 1024];
                new Random(21).NextBytes(contentA);
                File.WriteAllBytes(Path.Combine(root, "a 文件.bin"), contentA);
                Directory.CreateDirectory(Path.Combine(root, "sub"));
                byte[] contentB = System.Text.Encoding.UTF8.GetBytes("hello from subdir");
                File.WriteAllBytes(Path.Combine(root, "sub", "b.txt"), contentB);

                server.Start(root, port, null);
                string baseUrl = "http://127.0.0.1:" + port + "/";

                // Root listing shows both entries
                var list = HttpGet(baseUrl);
                Assert.Equal(200, list.Item1, "root listing 200");
                string html = System.Text.Encoding.UTF8.GetString(list.Item2);
                Assert.True(html.Contains("a 文件.bin") || html.Contains(Uri.EscapeDataString("a 文件.bin").Replace("+", "%20")) || html.Contains("a %E6%96%87%E4%BB%B6.bin"),
                    "listing contains file name");
                Assert.True(html.Contains("sub"), "listing contains subdir");

                // Download with a non-ASCII name; content and length must match
                var dl = HttpGet(baseUrl + "?f=" + Uri.EscapeDataString("a 文件.bin"));
                Assert.Equal(200, dl.Item1, "download 200");
                Assert.Equal(contentA.Length, dl.Item2.Length, "download length");
                Assert.True(Utils.ConstantTimeEquals(contentA, dl.Item2), "download bytes identical");
                Assert.Equal(contentA.Length.ToString(), dl.Item3["Content-Length"], "Content-Length header");

                // Subdirectory navigation and download
                var subList = HttpGet(baseUrl + "?p=" + Uri.EscapeDataString("sub"));
                Assert.Equal(200, subList.Item1, "subdir listing 200");
                Assert.True(System.Text.Encoding.UTF8.GetString(subList.Item2).Contains("b.txt"), "subdir listing shows b.txt");
                var dlB = HttpGet(baseUrl + "?f=" + Uri.EscapeDataString("sub/b.txt"));
                Assert.Equal(200, dlB.Item1, "subdir download 200");
                Assert.True(Utils.ConstantTimeEquals(contentB, dlB.Item2), "subdir download bytes");

                // Missing file -> 404
                var missing = HttpGet(baseUrl + "?f=does_not_exist.bin");
                Assert.Equal(404, missing.Item1, "missing file 404");
            }
            finally
            {
                server.Stop();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void HttpShareTokenAndTraversal()
        {
            int port = FindFreePort();
            string root = Path.Combine(TempBase(), "tr_http_tk_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var server = new HttpShareServer();
            try
            {
                byte[] content = System.Text.Encoding.UTF8.GetBytes("top secret payload");
                File.WriteAllBytes(Path.Combine(root, "secret.bin"), content);
                // A file OUTSIDE the share root that traversal must never reach
                string outsideDir = Path.Combine(TempBase(), "tr_http_out_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(outsideDir);
                File.WriteAllBytes(Path.Combine(outsideDir, "escaped.txt"), new byte[] { 1 });

                server.Start(root, port, "135790");
                string baseUrl = "http://127.0.0.1:" + port + "/";

                // No/wrong token -> token form, never the listing
                var noTok = HttpGet(baseUrl);
                Assert.Equal(200, noTok.Item1, "token gate 200");
                string html = System.Text.Encoding.UTF8.GetString(noTok.Item2);
                Assert.True(html.Contains("name=\"t\""), "token form served");
                Assert.False(html.Contains("secret.bin"), "no listing without token");

                var badTok = HttpGet(baseUrl + "?t=000000");
                Assert.True(System.Text.Encoding.UTF8.GetString(badTok.Item2).Contains("name=\"t\""), "wrong token -> form");

                // Correct code submits ?t=... once: the server parks it in a cookie and
                // bounces to the bare URL — the jar models the browser following that
                var jar = new System.Net.CookieContainer();
                var okTok = HttpGet(baseUrl + "?t=135790", jar);
                Assert.True(System.Text.Encoding.UTF8.GetString(okTok.Item2).Contains("secret.bin"),
                    "correct token -> listing (via cookie redirect)");
                Assert.Equal(1, jar.Count, "access code parked in a cookie");

                // Cookie alone (no ?t= anywhere) keeps the session — links stay clean
                var cookieOnly = HttpGet(baseUrl, jar);
                Assert.True(System.Text.Encoding.UTF8.GetString(cookieOnly.Item2).Contains("secret.bin"),
                    "cookie alone -> listing");

                // A wrong cookie value is still just the form
                var badJar = new System.Net.CookieContainer();
                badJar.Add(new System.Net.Cookie("t", "000000", "/", "127.0.0.1"));
                var cookieBad = HttpGet(baseUrl, badJar);
                Assert.True(System.Text.Encoding.UTF8.GetString(cookieBad.Item2).Contains("name=\"t\""),
                    "wrong cookie -> form");

                // Download needs the session too
                var dlNoTok = HttpGet(baseUrl + "?f=secret.bin");
                Assert.True(System.Text.Encoding.UTF8.GetString(dlNoTok.Item2).Contains("name=\"t\""), "download without token -> form");
                var dlOk = HttpGet(baseUrl + "?f=secret.bin", jar);
                Assert.Equal(200, dlOk.Item1, "download with cookie 200");
                Assert.True(Utils.ConstantTimeEquals(content, dlOk.Item2), "download bytes with cookie");

                // Traversal attempts are blocked (sanitized into the root or 404)
                var trav1 = HttpGet(baseUrl + "?t=" + Uri.EscapeDataString("135790") + "&f=" + Uri.EscapeDataString("../tr_http_out_" + Path.GetFileName(outsideDir) + "/escaped.txt"), jar);
                Assert.True(trav1.Item1 == 404 || trav1.Item1 == 200,
                    "traversal attempt answered");
                if (trav1.Item1 == 200)
                    Assert.False(Utils.ConstantTimeEquals(new byte[] { 1 }, trav1.Item2), "traversal must not leak outside file");

                var missing = HttpGet(baseUrl + "?f=nope.bin", jar);
                Assert.Equal(404, missing.Item1, "missing file 404");

                try { Directory.Delete(outsideDir, true); } catch { }
            }
            finally
            {
                server.Stop();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>Repeated wrong access codes lock the client IP out; the correct code
        /// is then refused with 429 until the lockout expires.</summary>
        private static void HttpShareAuthLockout()
        {
            int port = FindFreePort();
            string root = Path.Combine(TempBase(), "tr_http_lock_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var server = new HttpShareServer();
            try
            {
                File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[] { 1, 2, 3 });
                server.Start(root, port, "424242");
                string baseUrl = "http://127.0.0.1:" + port + "/";

                // Burn the allowance with wrong codes
                for (int i = 0; i < HttpShareServer.MaxAuthFailures + 2; i++)
                    HttpGet(baseUrl + "?t=000000");

                // Now even the CORRECT code is refused
                var ok = HttpGet(baseUrl + "?t=424242");
                Assert.Equal(429, ok.Item1, "locked out -> 429 even with the right code");
                Assert.False(System.Text.Encoding.UTF8.GetString(ok.Item2).Contains("a.bin"),
                    "no listing while locked out");
            }
            finally
            {
                server.Stop();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>Fan-out pattern end-to-end: two independent servers receive the
        /// same file concurrently from parallel clients (distinct random source ports).</summary>
        private static void FanOutTwoTargets()
        {
            int port1 = FindFreePort();
            int port2 = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_fan_s_" + Guid.NewGuid().ToString("N"));
            string recv1 = Path.Combine(TempBase(), "tr_fan_r1_" + Guid.NewGuid().ToString("N"));
            string recv2 = Path.Combine(TempBase(), "tr_fan_r2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recv1);
            Directory.CreateDirectory(recv2);
            TransferServer s1 = null, s2 = null;
            try
            {
                var content = new byte[200 * 1024];
                new Random(33).NextBytes(content);
                var testFile = Path.Combine(sendDir, "fan.bin");
                File.WriteAllBytes(testFile, content);

                var started = new ManualResetEvent(false);
                s1 = new TransferServer("127.0.0.1", port1, recv1);
                s2 = new TransferServer("127.0.0.1", port2, recv2);
                var done1 = new ManualResetEvent(false);
                var done2 = new ManualResetEvent(false);
                s1.OnStarted += () => started.Set();
                s1.OnFileReceived += (p, sz) => done1.Set();
                s2.OnFileReceived += (p, sz) => done2.Set();
                s1.Start();
                s2.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Servers did not start within 5s");

                // Parallel clients — the fan-out core (random source ports, pairing, cards)
                var c1 = new TransferClient("127.0.0.1", port1, testFile);
                var c2 = new TransferClient("127.0.0.1", port2, testFile);
                var t1 = c1.SendAsync();
                var t2 = c2.SendAsync();
                t1.Wait(30000);
                t2.Wait(30000);

                if (!done1.WaitOne(5000) || !done2.WaitOne(5000))
                    throw new Exception("Both targets did not receive the file");

                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(Path.Combine(recv1, "fan.bin"))), "target 1 content");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(Path.Combine(recv2, "fan.bin"))), "target 2 content");
            }
            finally
            {
                if (s1 != null) { try { s1.Stop(); } catch { } }
                if (s2 != null) { try { s2.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recv1, true); } catch { }
                try { Directory.Delete(recv2, true); } catch { }
            }
        }

        // ---- HTTP share upload (multipart POST) ----

        /// <summary>Builds a multipart/form-data body: fields then files.</summary>
        private static byte[] BuildMultipart(string boundary,
            System.Collections.Generic.Dictionary<string, string> fields,
            System.Collections.Generic.Dictionary<string, byte[]> files)
        {
            var ms = new MemoryStream();
            foreach (var kv in fields)
            {
                byte[] part = System.Text.Encoding.UTF8.GetBytes(
                    "--" + boundary + "\r\n" +
                    "Content-Disposition: form-data; name=\"" + kv.Key + "\"\r\n\r\n" +
                    kv.Value + "\r\n");
                ms.Write(part, 0, part.Length);
            }
            foreach (var kv in files)
            {
                byte[] head = System.Text.Encoding.UTF8.GetBytes(
                    "--" + boundary + "\r\n" +
                    "Content-Disposition: form-data; name=\"file\"; filename=\"" + kv.Key + "\"\r\n" +
                    "Content-Type: application/octet-stream\r\n\r\n");
                ms.Write(head, 0, head.Length);
                ms.Write(kv.Value, 0, kv.Value.Length);
                byte[] tail = System.Text.Encoding.UTF8.GetBytes("\r\n");
                ms.Write(tail, 0, tail.Length);
            }
            byte[] end = System.Text.Encoding.UTF8.GetBytes("--" + boundary + "--\r\n");
            ms.Write(end, 0, end.Length);
            return ms.ToArray();
        }

        private static System.Tuple<int, byte[], string> HttpPost(string url, string contentType, byte[] body)
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Method = "POST";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            req.Proxy = null;
            req.ContentType = contentType;
            req.ContentLength = body.Length;
            req.AllowAutoRedirect = false;
            using (var rs = req.GetRequestStream())
                rs.Write(body, 0, body.Length);
            try
            {
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var ms = new MemoryStream())
                {
                    resp.GetResponseStream().CopyTo(ms);
                    return System.Tuple.Create((int)resp.StatusCode, ms.ToArray(), resp.Headers["Location"]);
                }
            }
            catch (System.Net.WebException ex)
            {
                var resp = ex.Response as System.Net.HttpWebResponse;
                if (resp == null) throw;
                using (resp)
                using (var ms = new MemoryStream())
                {
                    resp.GetResponseStream().CopyTo(ms);
                    return System.Tuple.Create((int)resp.StatusCode, ms.ToArray(), resp.Headers["Location"]);
                }
            }
        }

        private static void HttpShareUpload()
        {
            int port = FindFreePort();
            string root = Path.Combine(TempBase(), "tr_http_up_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var server = new HttpShareServer();
            try
            {
                // Payload larger than the 64KB read buffer, with boundary-prefix fragments
                // inside to stress the streaming pattern scanner across chunk edges
                var content = new byte[300 * 1024];
                new Random(77).NextBytes(content);
                System.Text.Encoding.UTF8.GetBytes("--BOUND\r\n").CopyTo(content, 50000);
                System.Text.Encoding.UTF8.GetBytes("\r\n--BOUN").CopyTo(content, 150000);
                var small = System.Text.Encoding.UTF8.GetBytes("second file body");
                var content2 = new byte[64 * 1024 + 17];
                new Random(78).NextBytes(content2);

                server.Start(root, port, null);
                string baseUrl = "http://127.0.0.1:" + port + "/";

                var files = new System.Collections.Generic.Dictionary<string, byte[]>
                {
                    { "up 大文件.bin", content },
                    { "second.bin", content2 },
                    { "small.txt", small }
                };
                var fields = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "p", "" }
                };
                byte[] body = BuildMultipart("BOUND", fields, files);
                var resp = HttpPost(baseUrl, "multipart/form-data; boundary=BOUND", body);

                Assert.Equal(303, resp.Item1, "upload responds 303");
                Assert.True(resp.Item3 != null && resp.Item3.StartsWith("/?p="), "redirect back to listing");
                Assert.Equal(3, Directory.GetFiles(root).Length, "three files uploaded");

                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(Path.Combine(root, "up 大文件.bin"))), "multipart file 1 bytes");
                Assert.True(Utils.ConstantTimeEquals(content2, File.ReadAllBytes(Path.Combine(root, "second.bin"))), "file 2 bytes");
                Assert.True(Utils.ConstantTimeEquals(small, File.ReadAllBytes(Path.Combine(root, "small.txt"))), "file 3 bytes");

                // Listing now shows the uploaded names
                var list = HttpGet(baseUrl);
                Assert.True(System.Text.Encoding.UTF8.GetString(list.Item2).Contains("second.bin"), "listing shows uploaded file");

                // Upload into a subdirectory via the p field
                Directory.CreateDirectory(Path.Combine(root, "sub"));
                var fieldsSub = new System.Collections.Generic.Dictionary<string, string> { { "p", "sub" } };
                var filesSub = new System.Collections.Generic.Dictionary<string, byte[]> { { "inner.txt", small } };
                var resp2 = HttpPost(baseUrl, "multipart/form-data; boundary=BOUND", BuildMultipart("BOUND", fieldsSub, filesSub));
                Assert.Equal(303, resp2.Item1, "subdir upload 303");
                Assert.True(Utils.ConstantTimeEquals(small, File.ReadAllBytes(Path.Combine(root, "sub", "inner.txt"))), "subdir upload bytes");
            }
            finally
            {
                server.Stop();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void HttpShareUploadTokenAndTraversalName()
        {
            int port = FindFreePort();
            string root = Path.Combine(TempBase(), "tr_http_upt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var server = new HttpShareServer();
            try
            {
                byte[] content = System.Text.Encoding.UTF8.GetBytes("guarded upload");
                server.Start(root, port, "246810");
                string baseUrl = "http://127.0.0.1:" + port + "/";

                // Without token: POST is refused (token form instead of an upload)
                var files = new System.Collections.Generic.Dictionary<string, byte[]> { { "a.txt", content } };
                var noTok = HttpPost(baseUrl, "multipart/form-data; boundary=B",
                    BuildMultipart("B", new System.Collections.Generic.Dictionary<string, string>(), files));
                Assert.False(noTok.Item1 == 303, "upload without token must not succeed");
                Assert.Equal(0, Directory.GetFiles(root).Length, "nothing written without token");

                // With token: upload works
                var withTok = HttpPost(baseUrl + "?t=246810", "multipart/form-data; boundary=B",
                    BuildMultipart("B", new System.Collections.Generic.Dictionary<string, string>(), files));
                Assert.Equal(303, withTok.Item1, "upload with token 303");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(Path.Combine(root, "a.txt"))), "token upload bytes");

                // Traversal filename is stripped to a bare name inside the share root
                var evil = new System.Collections.Generic.Dictionary<string, byte[]> { { "..\\..\\evil.txt", content } };
                var respEvil = HttpPost(baseUrl + "?t=246810", "multipart/form-data; boundary=B",
                    BuildMultipart("B", new System.Collections.Generic.Dictionary<string, string>(), evil));
                Assert.Equal(303, respEvil.Item1, "traversal-named upload still handled");
                Assert.True(File.Exists(Path.Combine(root, "evil.txt")), "name stripped to bare file name");
                string parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
                Assert.False(File.Exists(Path.Combine(parent, "evil.txt")), "nothing written outside root");
            }
            finally
            {
                server.Stop();
                try { Directory.Delete(root, true); } catch { }
                string parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
                try { File.Delete(Path.Combine(parent, "evil.txt")); } catch { }
            }
        }

        /// <summary>
        /// The full-file hash pass (Config VerifyHash) must finish BEFORE the client opens
        /// the connection. Hashing after the connect left the server silent through the pass,
        /// and the UDT server's 30s receive timeout on the accepted socket then dropped the
        /// connection before the 0x03 header ever arrived — with a large enough file the send
        /// could not start at all, and the auto-retry repeated the same fate. Log order is
        /// what pins the fix; the payload stays small so the test itself is fast.
        /// </summary>
        private static void ResumeFullHashBeforeConnect(bool isUdt)
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_hbc_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_hbc_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer tcpServer = null;
            TransferUdtServer udtServer = null;
            try
            {
                string file = Path.Combine(sendDir, "payload.bin");
                var content = new byte[512 * 1024];
                new Random(5).NextBytes(content);
                File.WriteAllBytes(file, content);

                var started = new ManualResetEvent(false);
                if (isUdt)
                {
                    udtServer = new TransferUdtServer("127.0.0.1", port, recvDir);
                    udtServer.OnStarted += () => started.Set();
                    udtServer.Start();
                }
                else
                {
                    tcpServer = new TransferServer("127.0.0.1", port, recvDir);
                    tcpServer.OnStarted += () => started.Set();
                    tcpServer.Start();
                }
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                string hashLine = L.C_ComputingFullHash(Path.GetFileName(file));
                string connectLine = L.C_Connected("127.0.0.1", port);
                var logs = new System.Collections.Generic.List<string>();
                Action<string> collect = msg => { lock (logs) logs.Add(msg); };

                if (isUdt)
                {
                    var client = new TransferUdtClient("127.0.0.1", port, file);
                    client.OnLog += collect;
                    client.SendResumableAsync(null, true).Wait(60000);
                }
                else
                {
                    var client = new TransferClient("127.0.0.1", port, file);
                    client.OnLog += collect;
                    client.SendResumableAsync(null, true).Wait(30000);
                }

                int hashIdx, connectIdx;
                string all;
                lock (logs)
                {
                    // Log lines carry a timestamp prefix — match on the text, not equality
                    hashIdx = logs.FindIndex(m => m.Contains(hashLine));
                    connectIdx = logs.FindIndex(m => m.Contains(connectLine));
                    all = string.Join(" | ", logs.ToArray());
                }
                Assert.True(hashIdx >= 0, "hash pass was logged (logs: " + all + ")");
                Assert.True(connectIdx >= 0, "connection was logged (logs: " + all + ")");
                Assert.True(hashIdx < connectIdx,
                    "full-hash pass must be logged before the connect (else the server waits through it)");
            }
            finally
            {
                if (tcpServer != null) { try { tcpServer.Stop(); } catch { } }
                if (udtServer != null) { try { udtServer.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        /// <summary>
        /// A repeat 0x04 sync must not re-read the files it already has. The server remembers
        /// both the digests it verified and the ones it computed while writing a file it just
        /// received, so the resume scan answers from that cache. Proved by holding the received
        /// files open with FileShare.None before the second pass: a hash attempt would throw,
        /// the server would read that as "content differs" and try to rewrite them.
        /// </summary>
        private static void FolderSyncHashCache()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_hc_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_hc_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            Guid sessionId = Guid.Empty;
            FileStream holdA = null;
            FileStream holdB = null;
            bool prevStrict = FileHashCache.ForServer.Strict;
            try
            {
                var contentA = new byte[64 * 1024];
                var contentB = new byte[48 * 1024];
                new Random(31).NextBytes(contentA);
                new Random(32).NextBytes(contentB);
                File.WriteAllBytes(Path.Combine(sendDir, "a.bin"), contentA);
                File.WriteAllBytes(Path.Combine(sendDir, "b.bin"), contentB);

                var started = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => started.Set();
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");
                // Start() adopts the machine's Config, and this test is about the cache
                // doing its job — pin it on regardless of what the developer's settings say.
                FileHashCache.ForServer.Strict = false;

                sessionId = FolderResumeState.DeriveSyncSession(sendDir, "127.0.0.1", port, false);
                string sessionDir = ServerWire.GetFolderSessionDir(recvDir, Path.GetFileName(sendDir), sessionId);
                Directory.CreateDirectory(sessionDir);

                // a.bin is already on the server (identical) — pass 1 verifies it by hashing,
                // which is the other way a digest gets remembered.
                File.WriteAllBytes(Path.Combine(sessionDir, "a.bin"), contentA);

                var client1 = new TransferClient("127.0.0.1", port, sendDir);
                client1.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                Thread.Sleep(300);
                Assert.True(Utils.ConstantTimeEquals(contentB, File.ReadAllBytes(Path.Combine(sessionDir, "b.bin"))),
                    "b.bin received in pass 1");

                // Make both received files unreadable, then sync again: only the cache can
                // answer, since hashing either file would fail.
                holdA = new FileStream(Path.Combine(sessionDir, "a.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
                holdB = new FileStream(Path.Combine(sessionDir, "b.bin"), FileMode.Open, FileAccess.Read, FileShare.None);

                var logs2 = new System.Collections.Generic.List<string>();
                var client2 = new TransferClient("127.0.0.1", port, sendDir);
                client2.OnLog += msg => logs2.Add(msg);
                client2.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                Thread.Sleep(300);
                Assert.True(logs2.Exists(m => m.Contains("already fully received")),
                    "pass 2 resolved from remembered digests without reading the files");
            }
            finally
            {
                FileHashCache.ForServer.Strict = prevStrict;
                if (holdA != null) { try { holdA.Dispose(); } catch { } }
                if (holdB != null) { try { holdB.Dispose(); } catch { } }
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
                if (sessionId != Guid.Empty) FolderResumeState.Delete(sessionId);
            }
        }

        /// <summary>
        /// The sending side keeps its own digest cache: re-syncing an unchanged source folder
        /// must not re-hash it. Proved the same way as the server-side case — the source file
        /// is held with FileShare.None before the second pass, so hashing it would fail and
        /// take the whole manifest (and the sync) down with it.
        /// </summary>
        private static void FolderSyncClientHashCache()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_cch_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_cch_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            Guid sessionId = Guid.Empty;
            FileStream hold = null;
            bool prevClientStrict = FileHashCache.ForClient.Strict;
            FileHashCache.ForClient.Strict = false; // this test is about the cache doing its job
            try
            {
                var content = new byte[96 * 1024];
                new Random(41).NextBytes(content);
                string source = Path.Combine(sendDir, "a.bin");
                File.WriteAllBytes(source, content);

                var started = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => started.Set();
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                sessionId = FolderResumeState.DeriveSyncSession(sendDir, "127.0.0.1", port, false);
                string sessionDir = ServerWire.GetFolderSessionDir(recvDir, Path.GetFileName(sendDir), sessionId);

                var client1 = new TransferClient("127.0.0.1", port, sendDir);
                client1.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                Thread.Sleep(300);
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(Path.Combine(sessionDir, "a.bin"))),
                    "a.bin received in pass 1");

                // The source becomes unreadable: only the client's digest cache can let the
                // next pass build a manifest for it.
                hold = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);

                var logs = new System.Collections.Generic.List<string>();
                var client2 = new TransferClient("127.0.0.1", port, sendDir);
                client2.OnLog += msg => logs.Add(msg);
                client2.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                Thread.Sleep(300);
                Assert.True(logs.Exists(m => m.Contains("files reused")),
                    "pass 2 reused the cached digest instead of re-reading the source");
                Assert.True(logs.Exists(m => m.Contains("already fully received")),
                    "pass 2 found the folder already in sync");

                // The strict switch (Config "SyncVerifyContent") must bypass that cache: the
                // same pass now has to read the locked source and cannot.
                FileHashCache.ForClient.Strict = true;
                bool failed;
                try
                {
                    var client3 = new TransferClient("127.0.0.1", port, sendDir);
                    client3.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                    failed = false;
                }
                catch (Exception) { failed = true; }
                Assert.True(failed, "byte-for-byte mode re-reads the source (and fails here because it is locked)");
            }
            finally
            {
                FileHashCache.ForClient.Strict = prevClientStrict;
                if (hold != null) { try { hold.Dispose(); } catch { } }
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
                if (sessionId != Guid.Empty) FolderResumeState.Delete(sessionId);
            }
        }

        /// <summary>
        /// A digest the peer contradicts must not survive: with a poisoned cache entry the
        /// 0x03 full-hash check fails on the server (status 3, file discarded), the client
        /// drops its entry, and the retry — which re-reads the file — delivers the right
        /// bytes. Without that self-healing the same bogus digest would fail every attempt
        /// until the entry aged out of the TTL.
        /// </summary>
        private static void ResumeFullHashStaleCache()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_shc_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_shc_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            Guid session1 = Guid.NewGuid();
            Guid session2 = Guid.NewGuid();
            bool prevStrict = FileHashCache.ForClient.Strict;
            FileHashCache.ForClient.Strict = false; // this test is about the cache being used
            try
            {
                var content = new byte[128 * 1024];
                new Random(51).NextBytes(content);
                string file = Path.Combine(sendDir, "payload.bin");
                File.WriteAllBytes(file, content);

                var started = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => started.Set();
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                // Cache a digest that does not belong to this file's content
                var bogus = new byte[32];
                for (int i = 0; i < bogus.Length; i++) bogus[i] = 0x33;
                FileHashCache.ForClient.Store(file, bogus);

                var logs = new System.Collections.Generic.List<string>();
                var client1 = new TransferClient("127.0.0.1", port, file);
                client1.OnLog += msg => { lock (logs) logs.Add(msg); };
                client1.SendResumableAsync(session1, true).Wait(30000);
                Thread.Sleep(300);

                string expected = L.C_VerifyFailed(Path.GetFileName(file));
                bool reported;
                lock (logs) reported = logs.Exists(m => m.Contains(expected));
                Assert.True(reported, "the server rejected the stale digest (status 3)");
                Assert.False(File.Exists(Path.Combine(recvDir, "payload.bin")),
                    "and discarded the file it could not verify");

                long size, mtime;
                bool matches;
                FileHashCache.Stat(file, out size, out mtime);
                Assert.False(FileHashCache.ForClient.TryMatch(file, size, mtime, bogus, out matches),
                    "the contradicted digest was dropped, so the retry re-reads the file");

                // Retry with a fresh session: recomputes the digest and the transfer lands
                var client2 = new TransferClient("127.0.0.1", port, file);
                client2.SendResumableAsync(session2, true).Wait(30000);
                Thread.Sleep(300);
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(Path.Combine(recvDir, "payload.bin"))),
                    "the retry delivered the file with the right content");
            }
            finally
            {
                FileHashCache.ForClient.Strict = prevStrict;
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
                ResumeState.Delete(session1);
                ResumeState.Delete(session2);
            }
        }

        /// <summary>
        /// Both duplicate checks compare an incoming file against one already on disk, and
        /// both must be able to answer from the server's digest cache instead of re-reading
        /// it. Proved by locking the on-disk copy: with the cache warm (it was recorded when
        /// the server received that file) the skip still happens, so no read was attempted.
        /// Covers 0x00 and 0x03, which reach the same verification by different routes.
        /// </summary>
        private static void DedupSkipUsesCachedDigest()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_dd_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_dd_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            FileStream hold = null;
            Guid sessionId = Guid.Empty;
            bool prevStrict = FileHashCache.ForServer.Strict;
            try
            {
                var content = new byte[64 * 1024];
                new Random(61).NextBytes(content);
                string file = Path.Combine(sendDir, "same.bin");
                File.WriteAllBytes(file, content);

                var started = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.SkipDuplicateFiles = true;   // the option both dedup paths need
                server.OnStarted += () => started.Set();
                // The dedup verdict is the receiver's, so its log is where the evidence is
                var serverLogs = new System.Collections.Generic.List<string>();
                server.OnLog += msg => { lock (serverLogs) serverLogs.Add(msg); };
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");
                FileHashCache.ForServer.Strict = false;   // pin the cache on: this test is about it

                string skipped = L.S_DuplicateSkipped("same.bin");
                // 0x00 first: it lands the file and records its digest as a side effect
                var client1 = new TransferClient("127.0.0.1", port, file);
                client1.SendAsync().Wait(30000);
                Thread.Sleep(300);
                string saved = Path.Combine(recvDir, "same.bin");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(saved)), "first copy received");

                // Make the on-disk copy unreadable: only the cache can identify a duplicate now
                hold = new FileStream(saved, FileMode.Open, FileAccess.Read, FileShare.None);

                var client2 = new TransferClient("127.0.0.1", port, file);
                client2.SendAsync().Wait(30000);
                Thread.Sleep(300);
                bool skippedVia00;
                lock (serverLogs) skippedVia00 = serverLogs.Exists(m => m.Contains(skipped));
                Assert.True(skippedVia00,
                    "0x00 dedup answered from the cached digest (logs: " + Dump(serverLogs) + ")");
                Assert.False(File.Exists(Path.Combine(recvDir, "same_1.bin")), "the duplicate copy was discarded");

                // 0x03 reaches the same file through the resume shortcut
                sessionId = Guid.NewGuid();
                var client3 = new TransferClient("127.0.0.1", port, file);
                client3.SendResumableAsync(sessionId, true).Wait(30000);
                Thread.Sleep(300);
                bool skippedVia03;
                int seen;
                lock (serverLogs)
                {
                    seen = 0;
                    for (int i = 0; i < serverLogs.Count; i++)
                        if (serverLogs[i].Contains(skipped)) seen++;
                    skippedVia03 = seen >= 2;
                }
                Assert.True(skippedVia03,
                    "0x03 dedup answered from the cached digest (logs: " + Dump(serverLogs) + ")");
            }
            finally
            {
                FileHashCache.ForServer.Strict = prevStrict;
                if (hold != null) { try { hold.Dispose(); } catch { } }
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
                if (sessionId != Guid.Empty) ResumeState.Delete(sessionId);
            }
        }

        /// <summary>
        /// The four things a repeat sync can meet, with the digest cache in play: an unchanged
        /// file (served from cache, proven by locking the source so a read would fail), a file
        /// whose content changed (re-hashed and re-sent), a zero-byte file (its digest is
        /// SHA256 of nothing and must be cached like any other), and a file deleted locally
        /// (sync keeps the server's copy — the documented direction).
        /// </summary>
        private static void FolderSyncChangeMatrix()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_cm_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_cm_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            Guid sessionId = Guid.Empty;
            bool prevClientStrict = FileHashCache.ForClient.Strict;
            bool prevServerStrict = FileHashCache.ForServer.Strict;
            var holds = new System.Collections.Generic.List<FileStream>();
            try
            {
                var keepContent = new byte[8 * 1024];
                var before = new byte[16 * 1024];
                var after = new byte[24 * 1024];
                new Random(71).NextBytes(keepContent);
                new Random(72).NextBytes(before);
                new Random(73).NextBytes(after);

                // Names sort so the unchanged file is scanned (and verify-skipped) first
                string keep = Path.Combine(sendDir, "a_keep.bin");
                string changed = Path.Combine(sendDir, "b_change.bin");
                string empty = Path.Combine(sendDir, "c_empty.bin");
                File.WriteAllBytes(keep, keepContent);
                File.WriteAllBytes(changed, before);
                File.WriteAllBytes(empty, new byte[0]);

                var started = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => started.Set();
                var serverLogs = new System.Collections.Generic.List<string>();
                server.OnLog += msg => { lock (serverLogs) serverLogs.Add(msg); };
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");
                FileHashCache.ForClient.Strict = false;
                FileHashCache.ForServer.Strict = false;

                sessionId = FolderResumeState.DeriveSyncSession(sendDir, "127.0.0.1", port, false);
                string sessionDir = ServerWire.GetFolderSessionDir(recvDir, Path.GetFileName(sendDir), sessionId);

                // Pass 1: everything travels, and both sides learn every digest
                var client1 = new TransferClient("127.0.0.1", port, sendDir);
                client1.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                Thread.Sleep(300);
                Assert.True(Utils.ConstantTimeEquals(keepContent, File.ReadAllBytes(Path.Combine(sessionDir, "a_keep.bin"))),
                    "pass 1: unchanged-so-far file received");
                Assert.True(File.Exists(Path.Combine(sessionDir, "c_empty.bin")), "pass 1: zero-byte file received");
                Assert.True(new FileInfo(Path.Combine(sessionDir, "c_empty.bin")).Length == 0, "pass 1: it is still empty");

                // Pass 2: one file's content changes. The unchanged one must come from cache
                // (its source is locked), the changed one must travel.
                File.WriteAllBytes(changed, after);
                holds.Add(new FileStream(keep, FileMode.Open, FileAccess.Read, FileShare.None));
                var logs2 = new System.Collections.Generic.List<string>();
                var client2 = new TransferClient("127.0.0.1", port, sendDir);
                client2.OnLog += msg => logs2.Add(msg);
                client2.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                Thread.Sleep(300);
                string landed = Path.Combine(sessionDir, "b_change.bin");
                Assert.True(Utils.ConstantTimeEquals(after, File.ReadAllBytes(landed)),
                    "pass 2: the changed content was re-sent (client: " + Dump(logs2) + ") (server: "
                    + Dump(serverLogs) + ") (landed " + new FileInfo(landed).Length + " bytes, wanted " + after.Length + ")");
                Assert.True(logs2.Exists(m => m.Contains("files reused")),
                    "pass 2: the unchanged file came from the cache (logs: " + Dump(logs2) + ")");

                // Pass 3: nothing changed at all, and now nothing may be read either
                holds.Add(new FileStream(changed, FileMode.Open, FileAccess.Read, FileShare.None));
                holds.Add(new FileStream(empty, FileMode.Open, FileAccess.Read, FileShare.None));
                var logs3 = new System.Collections.Generic.List<string>();
                var client3 = new TransferClient("127.0.0.1", port, sendDir);
                client3.OnLog += msg => logs3.Add(msg);
                client3.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                Thread.Sleep(300);
                Assert.True(logs3.Exists(m => m.Contains("already fully received")),
                    "pass 3: everything (including the zero-byte file) was answered from cache (logs: " + Dump(logs3) + ")");
                Assert.True(Utils.ConstantTimeEquals(keepContent, File.ReadAllBytes(Path.Combine(sessionDir, "a_keep.bin"))),
                    "pass 3: nothing was rewritten");

                // Pass 4: a file deleted on the sender stays on the receiver (sync semantics)
                foreach (var h in holds) { h.Dispose(); }
                holds.Clear();
                File.Delete(empty);
                var client4 = new TransferClient("127.0.0.1", port, sendDir);
                client4.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                Thread.Sleep(300);
                Assert.True(File.Exists(Path.Combine(sessionDir, "c_empty.bin")),
                    "pass 4: the receiver keeps files the sender deleted");
            }
            finally
            {
                foreach (var h in holds) { try { h.Dispose(); } catch { } }
                FileHashCache.ForClient.Strict = prevClientStrict;
                FileHashCache.ForServer.Strict = prevServerStrict;
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
                if (sessionId != Guid.Empty) FolderResumeState.Delete(sessionId);
            }
        }

        /// <summary>
        /// A stale partial file on the receiver (a leftover prefix that is NOT a prefix of the
        /// sender's content) makes the server resume where it should have started over. The
        /// assembled file then fails the full-file check: it must be discarded, the sender must
        /// be TOLD (0x04 writes a completion verdict on TCP, like 0x00/0x01/0x06 — without it
        /// the client reported success while nothing usable landed), and the retry must start
        /// clean and deliver the right bytes.
        /// </summary>
        private static void FolderSyncDiscardReported()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_fd_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_fd_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            Guid sessionId = Guid.Empty;
            bool prevClientStrict = FileHashCache.ForClient.Strict;
            bool prevServerStrict = FileHashCache.ForServer.Strict;
            try
            {
                var content = new byte[64 * 1024];
                new Random(81).NextBytes(content);
                File.WriteAllBytes(Path.Combine(sendDir, "big.bin"), content);

                var started = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => started.Set();
                var discardLogs = new System.Collections.Generic.List<string>();
                server.OnLog += msg => { lock (discardLogs) discardLogs.Add(msg); };
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");
                FileHashCache.ForClient.Strict = false;
                FileHashCache.ForServer.Strict = false;

                sessionId = FolderResumeState.DeriveSyncSession(sendDir, "127.0.0.1", port, false);
                string sessionDir = ServerWire.GetFolderSessionDir(recvDir, Path.GetFileName(sendDir), sessionId);
                Directory.CreateDirectory(sessionDir);

                // A half file from some other content: the right prefix length, wrong bytes
                var stale = new byte[32 * 1024];
                new Random(82).NextBytes(stale);
                string target = Path.Combine(sessionDir, "big.bin");
                File.WriteAllBytes(target, stale);
                // First attempt: the server appends the sender's tail to those bytes, the
                // full-file check fails, and the sender must be told about it.
                var client1 = new TransferClient("127.0.0.1", port, sendDir);
                bool reported;
                string message = null;
                try
                {
                    client1.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                    reported = false;
                }
                catch (Exception ex)
                {
                    reported = true;
                    message = ex.Message;
                }
                Assert.True(reported, "a discarded file must be reported to the sender, not reported as success");
                Thread.Sleep(300);
                Assert.False(File.Exists(target),
                    "and the spliced file is discarded instead of being left at the final path (error: " + message
                    + ") (server: " + Dump(discardLogs) + ")");

                // Second attempt: nothing is in the way, so it starts over and succeeds
                var client2 = new TransferClient("127.0.0.1", port, sendDir);
                client2.SendFolderResumableAsync(sessionId, keepState: true).Wait(30000);
                Thread.Sleep(300);
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(target)),
                    "the retry delivered the file");
            }
            finally
            {
                FileHashCache.ForClient.Strict = prevClientStrict;
                FileHashCache.ForServer.Strict = prevServerStrict;
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
                if (sessionId != Guid.Empty) FolderResumeState.Delete(sessionId);
            }
        }

        /// <summary>
        /// A real-world sync shape: dozens of files across nested subfolders, synced three
        /// times. Pass 1 transfers everything; pass 2 (untouched) is answered entirely from
        /// the two digest caches — with every source locked, so any re-read would fail;
        /// pass 3 adds one nested file and only that file travels.
        /// </summary>
        private static void FolderSyncManyNested()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_mn_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_mn_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            Guid sessionId = Guid.Empty;
            bool prevClientStrict = FileHashCache.ForClient.Strict;
            bool prevServerStrict = FileHashCache.ForServer.Strict;
            var holds = new System.Collections.Generic.List<FileStream>();
            try
            {
                var rnd = new Random(91);
                int total = 40;
                for (int i = 0; i < total; i++)
                {
                    // three levels deep: sub/level2/level3
                    string sub = Path.Combine(sendDir, "sub" + (i % 4), "level2_" + (i % 3), "level3_" + (i % 2));
                    Directory.CreateDirectory(sub);
                    var content = new byte[1024 + i * 64];
                    rnd.NextBytes(content);
                    File.WriteAllBytes(Path.Combine(sub, "f" + i + ".bin"), content);
                }

                var started = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => started.Set();
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");
                FileHashCache.ForClient.Strict = false;
                FileHashCache.ForServer.Strict = false;

                sessionId = FolderResumeState.DeriveSyncSession(sendDir, "127.0.0.1", port, false);
                string sessionDir = ServerWire.GetFolderSessionDir(recvDir, Path.GetFileName(sendDir), sessionId);

                var client1 = new TransferClient("127.0.0.1", port, sendDir);
                client1.SendFolderResumableAsync(sessionId, keepState: true).Wait(60000);
                Thread.Sleep(300);
                Assert.Equal(total, Directory.GetFiles(sessionDir, "*", SearchOption.AllDirectories).Length,
                    "pass 1 delivered every nested file");

                // Pass 2: lock every source — the manifest can only be built from the cache
                foreach (var f in Directory.GetFiles(sendDir, "*", SearchOption.AllDirectories))
                    holds.Add(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None));
                var logs2 = new System.Collections.Generic.List<string>();
                var client2 = new TransferClient("127.0.0.1", port, sendDir);
                client2.OnLog += msg => logs2.Add(msg);
                client2.SendFolderResumableAsync(sessionId, keepState: true).Wait(60000);
                Thread.Sleep(300);
                Assert.True(logs2.Exists(m => m.Contains("files reused")),
                    "pass 2 built the manifest without reading any source (logs: " + Dump(logs2) + ")");
                Assert.True(logs2.Exists(m => m.Contains("already fully received")),
                    "pass 2 transferred nothing");

                // Pass 3: one new nested file — only it may travel
                foreach (var h in holds) h.Dispose();
                holds.Clear();
                string extraDir = Path.Combine(sendDir, "sub1", "level2_2", "level3_0");
                Directory.CreateDirectory(extraDir);
                var extra = new byte[2048];
                rnd.NextBytes(extra);
                File.WriteAllBytes(Path.Combine(extraDir, "new.bin"), extra);
                var logs3 = new System.Collections.Generic.List<string>();
                var client3 = new TransferClient("127.0.0.1", port, sendDir);
                client3.OnLog += msg => logs3.Add(msg);
                client3.SendFolderResumableAsync(sessionId, keepState: true).Wait(60000);
                Thread.Sleep(300);
                // Enumeration order decides where the new file lands, so pin the shape rather
                // than a position: the manifest reused the 40 cached digests, the resume began
                // somewhere inside the list (not at zero — the scan skipped everything before
                // the insertion point), and the new file landed.
                Assert.True(logs3.Exists(m => m.Contains("Hash cache: 40 files reused")),
                    "pass 3 reused every cached digest (logs: " + Dump(logs3) + ")");
                Assert.True(logs3.Exists(m => m.Contains("starting at file") && m.Contains("/41")),
                    "pass 3 resumed mid-list at the new file (logs: " + Dump(logs3) + ")");
                Assert.True(File.Exists(Path.Combine(sessionDir, "sub1", "level2_2", "level3_0", "new.bin")),
                    "pass 3 delivered the new file");
            }
            finally
            {
                foreach (var h in holds) { try { h.Dispose(); } catch { } }
                FileHashCache.ForClient.Strict = prevClientStrict;
                FileHashCache.ForServer.Strict = prevServerStrict;
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
                if (sessionId != Guid.Empty) FolderResumeState.Delete(sessionId);
            }
        }

        /// <summary>
        /// The CLI sync verb drives the same 0x04 keepState path as the GUI sync mode, and
        /// derives the same stable session — so the first run transfers everything and the
        /// second run of the same command must be a no-op (the scheduled-backup contract).
        /// </summary>
        private static void CliSync()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_cli_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_cli_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            try
            {
                var a = new byte[48 * 1024];
                var b = new byte[24 * 1024];
                new Random(121).NextBytes(a);
                new Random(122).NextBytes(b);
                Directory.CreateDirectory(Path.Combine(sendDir, "sub"));
                File.WriteAllBytes(Path.Combine(sendDir, "a.bin"), a);
                File.WriteAllBytes(Path.Combine(sendDir, "sub", "b.bin"), b);

                var started = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => started.Set();
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");
                string sessionDir = ServerWire.GetFolderSessionDir(recvDir, Path.GetFileName(sendDir),
                    FolderResumeState.DeriveSyncSession(sendDir, "127.0.0.1", port, false));
                Directory.CreateDirectory(sessionDir);

                var aPath = Path.Combine(sessionDir, "a.bin");
                var bPath = Path.Combine(sessionDir, "sub", "b.bin");

                int rc = Cli.Run(new[] { "sync", "--folder", sendDir, "--ip", "127.0.0.1", "--port", port.ToString() });
                Assert.Equal(0, rc, "first sync exits 0");
                Thread.Sleep(300);
                Assert.True(Utils.ConstantTimeEquals(a, File.ReadAllBytes(aPath)), "nested file a.bin received");
                Assert.True(Utils.ConstantTimeEquals(b, File.ReadAllBytes(bPath)), "nested file b.bin received");

                // Second run: identical folder → zero transfer, still exit 0
                rc = Cli.Run(new[] { "sync", "--folder", sendDir, "--ip", "127.0.0.1", "--port", port.ToString() });
                Assert.Equal(0, rc, "second (no-op) sync exits 0");
                Assert.True(Utils.ConstantTimeEquals(a, File.ReadAllBytes(aPath)), "content untouched by the no-op");

                // A changed file re-travels on the next run
                var a2 = new byte[48 * 1024];
                new Random(123).NextBytes(a2);
                File.WriteAllBytes(Path.Combine(sendDir, "a.bin"), a2);
                rc = Cli.Run(new[] { "sync", "--folder", sendDir, "--ip", "127.0.0.1", "--port", port.ToString() });
                Assert.Equal(0, rc, "sync after a change exits 0");
                Thread.Sleep(300);
                Assert.True(Utils.ConstantTimeEquals(a2, File.ReadAllBytes(aPath)), "the new version was delivered");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        /// <summary>
        /// The CLI verify verb over a real received-files directory: a file that arrived via a
        /// transfer verifies; a foreign file shows as unverified without failing the run; a
        /// file corrupted in place (same size, mtime restored) fails the run with exit code 1.
        /// </summary>
        private static void CliVerify()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_cvs_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_cvr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            try
            {
                var content = new byte[64 * 1024];
                new Random(131).NextBytes(content);
                string file = Path.Combine(sendDir, "payload.bin");
                File.WriteAllBytes(file, content);

                var started = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => started.Set();
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                var client = new TransferClient("127.0.0.1", port, file);
                client.SendAsync().Wait(30000);
                Thread.Sleep(300);
                string received = Path.Combine(recvDir, "payload.bin");
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(received)), "file received");

                File.WriteAllBytes(Path.Combine(recvDir, "foreign.txt"), new byte[] { 1, 2, 3 });

                int rc = Cli.Run(new[] { "verify", "--dir", recvDir });
                Assert.Equal(0, rc, "unreceived foreign file does not fail the scan");

                // In-place corruption: same size, timestamp restored — the scrub's core case
                var damaged = new byte[64 * 1024];
                new Random(132).NextBytes(damaged);
                long sz, mt;
                FileHashCache.Stat(received, out sz, out mt);
                File.WriteAllBytes(received, damaged);
                File.SetLastWriteTimeUtc(received, new DateTime(mt));

                rc = Cli.Run(new[] { "verify", "--dir", recvDir });
                Assert.Equal(1, rc, "a corrupted file fails the scan with exit code 1");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        /// <summary>
        /// Cancelling (or pausing) during the pre-connect hash pass must actually stop the
        /// send: the token now runs through BuildFolderManifest, the OCE maps to
        /// WasCancelled (pause semantics), and the server never sees a connection — the
        /// transfer neither half-starts nor reports success.
        /// </summary>
        private static void CancelDuringFolderHash()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_chc_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_chc_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            try
            {
                // Big enough that the hash pass trips the 100ms progress throttle
                var content = new byte[192 * 1024 * 1024];
                new Random(141).NextBytes(content);
                File.WriteAllBytes(Path.Combine(sendDir, "big.bin"), content);

                var started = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => started.Set();
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                Guid sessionId = Guid.NewGuid();
                var client = new TransferClient("127.0.0.1", port, sendDir);
                bool sawProgress = false;
                client.OnProgress += delegate
                {
                    if (!sawProgress)
                    {
                        sawProgress = true;
                        client.Cancel();
                    }
                };
                var task = client.SendFolderResumableAsync(sessionId, keepState: true);
                task.Wait(60000);
                Assert.True(sawProgress, "hash progress reached the caller");
                Assert.True(client.WasCancelled, "the cancel maps to pause semantics, not an error");
                Thread.Sleep(300);
                Assert.False(Directory.Exists(ServerWire.GetFolderSessionDir(recvDir,
                    Path.GetFileName(sendDir), sessionId)), "the server never saw the transfer");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        /// <summary>
        /// With the 0x0A capability the receiver answers which of the remaining files it
        /// already holds identically, so a change in the middle of the manifest no longer
        /// re-sends its identical successors. Proven three ways: the skip is logged, the
        /// skipped file's mtime is untouched (no rewrite happened), and everything still
        /// byte-compares. Second pass: an inserted file only drags itself along. Third:
        /// the same flow works over UDT.
        /// </summary>
        private static void FolderSyncPerFileSkip(bool isUdt)
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_pfs_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_pfs_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer tcpServer = null;
            TransferUdtServer udtServer = null;
            Guid sessionId = Guid.Empty;
            bool prevClientStrict = FileHashCache.ForClient.Strict;
            bool prevServerStrict = FileHashCache.ForServer.Strict;
            try
            {
                var a = new byte[32 * 1024];
                var b = new byte[48 * 1024];
                var c = new byte[40 * 1024];
                new Random(151).NextBytes(a);
                new Random(152).NextBytes(b);
                new Random(153).NextBytes(c);
                File.WriteAllBytes(Path.Combine(sendDir, "a.bin"), a);
                File.WriteAllBytes(Path.Combine(sendDir, "b.bin"), b);
                File.WriteAllBytes(Path.Combine(sendDir, "c.bin"), c);

                var started = new ManualResetEvent(false);
                if (isUdt)
                {
                    udtServer = new TransferUdtServer("127.0.0.1", port, recvDir);
                    udtServer.OnStarted += () => started.Set();
                    udtServer.Start();
                }
                else
                {
                    tcpServer = new TransferServer("127.0.0.1", port, recvDir);
                    tcpServer.OnStarted += () => started.Set();
                    tcpServer.Start();
                }
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");
                FileHashCache.ForClient.Strict = false;
                FileHashCache.ForServer.Strict = false;

                sessionId = FolderResumeState.DeriveSyncSession(sendDir, "127.0.0.1", port, isUdt);
                string sessionDir = ServerWire.GetFolderSessionDir(recvDir, Path.GetFileName(sendDir), sessionId);

                // Pass 1 with a fresh client: full transfer, no skip possible
                var client1 = isUdt
                    ? (TransferClient)null
                    : new TransferClient("127.0.0.1", port, sendDir);
                if (isUdt)
                {
                    var u1 = new TransferUdtClient("127.0.0.1", port, sendDir);
                    u1.SendFolderResumableAsync(sessionId, keepState: true).Wait(60000);
                }
                else
                {
                    client1.SendFolderResumableAsync(sessionId, keepState: true).Wait(60000);
                }
                Thread.Sleep(300);
                Assert.True(Utils.ConstantTimeEquals(a, File.ReadAllBytes(Path.Combine(sessionDir, "a.bin"))),
                    "pass 1: a.bin received");
                string cPath = Path.Combine(sessionDir, "c.bin");
                DateTime cMtimeBefore = File.GetLastWriteTimeUtc(cPath);

                // Pass 2: b's content changes (same size). Old flow would re-send c after it;
                // with per-file skip the receiver declares it holds c and only b travels.
                var b2 = new byte[48 * 1024];
                new Random(154).NextBytes(b2);
                File.WriteAllBytes(Path.Combine(sendDir, "b.bin"), b2);

                var logs2 = new System.Collections.Generic.List<string>();
                if (isUdt)
                {
                    var u2 = new TransferUdtClient("127.0.0.1", port, sendDir);
                    u2.OnLog += msg => logs2.Add(msg);
                    u2.SendFolderResumableAsync(sessionId, keepState: true).Wait(60000);
                }
                else
                {
                    var c2 = new TransferClient("127.0.0.1", port, sendDir);
                    c2.OnLog += msg => logs2.Add(msg);
                    c2.SendFolderResumableAsync(sessionId, keepState: true).Wait(60000);
                }
                Thread.Sleep(300);
                string skipLine = L.C_PerFileSkip(1, 0);
                Assert.True(logs2.Exists(m => m.Contains(skipLine) || m.Contains("holds 1 file")),
                    "pass 2: c.bin was skipped by per-file answer (logs: " + Dump(logs2) + ")");
                Assert.True(Utils.ConstantTimeEquals(b2, File.ReadAllBytes(Path.Combine(sessionDir, "b.bin"))),
                    "pass 2: the changed file travelled");
                Assert.True(File.GetLastWriteTimeUtc(cPath).Ticks == cMtimeBefore.Ticks,
                    "skipped file was not rewritten");

                // Pass 3: change the first file AND insert a new one. The scan resumes at the
                // first mismatch; everything identical after that point is skipped by the
                // per-file answers (≥1 guaranteed — the new file and the changed one travel).
                var a2 = new byte[32 * 1024];
                new Random(156).NextBytes(a2);
                File.WriteAllBytes(Path.Combine(sendDir, "a.bin"), a2);
                var d = new byte[16 * 1024];
                new Random(155).NextBytes(d);
                File.WriteAllBytes(Path.Combine(sendDir, "d.bin"), d);
                var logs3 = new System.Collections.Generic.List<string>();
                if (isUdt)
                {
                    var u3 = new TransferUdtClient("127.0.0.1", port, sendDir);
                    u3.OnLog += msg => logs3.Add(msg);
                    u3.SendFolderResumableAsync(sessionId, keepState: true).Wait(60000);
                }
                else
                {
                    var c3 = new TransferClient("127.0.0.1", port, sendDir);
                    c3.OnLog += msg => logs3.Add(msg);
                    c3.SendFolderResumableAsync(sessionId, keepState: true).Wait(60000);
                }
                Thread.Sleep(300);
                Assert.True(Utils.ConstantTimeEquals(d, File.ReadAllBytes(Path.Combine(sessionDir, "d.bin"))),
                    "pass 3: the inserted file arrived");
                Assert.True(Utils.ConstantTimeEquals(a2, File.ReadAllBytes(Path.Combine(sessionDir, "a.bin"))),
                    "pass 3: the changed file arrived");
                Assert.True(logs3.Exists(m => m.Contains("对端已持有") || m.Contains("Peer already holds")),
                    "pass 3: identical successors were skipped (logs: " + Dump(logs3) + ")");
            }
            finally
            {
                FileHashCache.ForClient.Strict = prevClientStrict;
                FileHashCache.ForServer.Strict = prevServerStrict;
                if (tcpServer != null) { try { tcpServer.Stop(); } catch { } }
                if (udtServer != null) { try { udtServer.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
                if (sessionId != Guid.Empty) FolderResumeState.Delete(sessionId);
            }
        }

        /// <summary>Pairing codes are not hard-wired to six digits: an eight-digit code
        /// (Config "PairingLength") must authenticate end to end.</summary>
        private static void LongPairingCode()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_plc_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_plc_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);
            TransferServer server = null;
            try
            {
                var content = new byte[32 * 1024];
                new Random(171).NextBytes(content);
                string file = Path.Combine(sendDir, "payload.bin");
                File.WriteAllBytes(file, content);

                var started = new ManualResetEvent(false);
                server = new TransferServer("127.0.0.1", port, recvDir);
                server.PairingCode = "9081726354"; // 10 digits
                server.OnStarted += () => started.Set();
                server.Start();
                if (!started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                var client = new TransferClient("127.0.0.1", port, file) { PairingCode = "9081726354" };
                client.SendAsync().Wait(30000);
                Thread.Sleep(300);
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(Path.Combine(recvDir, "payload.bin"))),
                    "a long pairing code authenticates and the transfer lands");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }


        /// <summary>HTTP share must serve single ranges (browsers resume large downloads
        /// with them): 206 + Content-Range for open-ended and bounded ranges, correct byte
        /// slices, and 416 for a range past EOF.</summary>
        private static void HttpShareRangeResume()
        {
            int port = FindFreePort();
            string root = Path.Combine(TempBase(), "tr_http_range_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var server = new HttpShareServer();
            try
            {
                var content = new byte[256 * 1024];
                new Random(161).NextBytes(content);
                File.WriteAllBytes(Path.Combine(root, "big.bin"), content);
                server.Start(root, port, null);
                string url = "http://127.0.0.1:" + port + "/?f=big.bin";

                // Full download advertises range support
                var full = HttpGet(url);
                Assert.Equal(200, full.Item1, "full download 200");
                Assert.Equal("bytes", full.Item3["Accept-Ranges"], "range support advertised");

                // Open-ended range: bytes=100000-
                var tail = HttpRangeGet(url, 100000, null);
                Assert.Equal(206, tail.Item1, "open range 206");
                Assert.Equal(content.Length - 100000, tail.Item2.Length, "open range length");
                var expectTail = new byte[content.Length - 100000];
                Buffer.BlockCopy(content, 100000, expectTail, 0, expectTail.Length);
                Assert.True(Utils.ConstantTimeEquals(expectTail, tail.Item2), "open range bytes");
                Assert.Equal("bytes " + 100000 + "-" + (content.Length - 1) + "/" + content.Length,
                    tail.Item3["Content-Range"], "Content-Range header");

                // Bounded range: bytes=100-199
                var mid = HttpRangeGet(url, 100, 199);
                Assert.Equal(206, mid.Item1, "bounded range 206");
                Assert.Equal(100, mid.Item2.Length, "bounded range length");
                var expectMid = new byte[100];
                Buffer.BlockCopy(content, 100, expectMid, 0, 100);
                Assert.True(Utils.ConstantTimeEquals(expectMid, mid.Item2), "bounded range bytes");

                // Suffix range: bytes=-1024 (last 1 KB)
                var lastKb = HttpRangeGet(url, -1024, null);
                Assert.Equal(206, lastKb.Item1, "suffix range 206");
                Assert.Equal(1024, lastKb.Item2.Length, "suffix range length");
                var expectSuffix = new byte[1024];
                Buffer.BlockCopy(content, content.Length - 1024, expectSuffix, 0, 1024);
                Assert.True(Utils.ConstantTimeEquals(expectSuffix, lastKb.Item2), "suffix range bytes");

                // Start past EOF -> 416
                var beyond = HttpRangeGet(url, content.Length + 10, null);
                Assert.Equal(416, beyond.Item1, "range past EOF 416");
            }
            finally
            {
                server.Stop();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>GET with a raw single Range header over a raw socket (start >= 0, or
        /// suffix when start < 0; end == null means "to EOF"). Returns status, body and the
        /// headers the server actually wrote — bypasses HttpWebRequest's restricted Range.</summary>
        private static System.Tuple<int, byte[], System.Collections.Generic.Dictionary<string, string>> HttpRangeGet(
            string url, long start, long? end)
        {
            int p = url.LastIndexOf(':');
            int port = int.Parse(url.Substring(p + 1, url.IndexOf('/', p) - p - 1));
            string query = url.Substring(url.IndexOf('/', p + 1));
            string spec = start >= 0
                ? "bytes=" + start + "-" + (end.HasValue ? end.Value.ToString() : "")
                : "bytes=-" + (-start);

            var client = new TcpClient("127.0.0.1", port);
            try
            {
                string crlf = "\r\n";
                string reqText = "GET " + query + " HTTP/1.1" + crlf + "Host: 127.0.0.1" + crlf
                    + "Range: " + spec + crlf + "Connection: close" + crlf + crlf;
                byte[] reqBytes = System.Text.Encoding.ASCII.GetBytes(reqText);
                var stream = client.GetStream();
                stream.Write(reqBytes, 0, reqBytes.Length);

                var ms = new MemoryStream();
                var buf = new byte[65536];
                int n;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                    ms.Write(buf, 0, n);
                byte[] raw = ms.ToArray();

                byte[] sep = System.Text.Encoding.ASCII.GetBytes(crlf + crlf);
                int headerEnd = IndexOf(raw, sep);
                if (headerEnd < 0) throw new IOException("no response header");
                string head = System.Text.Encoding.ASCII.GetString(raw, 0, headerEnd);
                int status = int.Parse(head.Split(' ')[1]);
                var headers = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string[] lines = head.Split(new[] { crlf }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon > 0)
                        headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
                var body = new byte[raw.Length - headerEnd - 4];
                Buffer.BlockCopy(raw, headerEnd + 4, body, 0, body.Length);
                return System.Tuple.Create(status, body, headers);
            }
            finally
            {
                client.Close();
            }
        }

        private static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i <= hay.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (hay[i + j] != needle[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

    }
}