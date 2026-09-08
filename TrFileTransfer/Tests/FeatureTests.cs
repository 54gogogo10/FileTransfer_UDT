using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TrFileTransfer;

namespace TrFileTransfer.Tests
{
    /// <summary>Unit + integration tests for the 2.9 feature batch: session encryption,
    /// receive gates (IP filter / confirmation), disk pre-check, duplicate skipping,
    /// per-device folders, QR encoding, and the stats store.</summary>
    public static class FeatureTests
    {
        public static void RunUnit(TestRunner runner)
        {
            RunWireCrypto(runner);
            RunIpFilter(runner);
            RunDiskSpace(runner);
            RunQr(runner);
            RunStatsStore(runner);
        }

        public static void RunIntegration(TestRunner runner)
        {
            runner.Run("Feature_TCP_Encrypted", TcpEncrypted, 1);
            runner.Run("Feature_TCP_EncryptDisabled", TcpEncryptDisabled);
            runner.Run("Feature_TCP_EncryptWrongCode", TcpEncryptWrongCode);
            runner.Run("Feature_TCP_EncryptLenient", TcpEncryptLenient);
            runner.Run("Feature_UDT_Encrypted", UdtEncrypted, 1);
            runner.Run("Feature_UDT_BindAny", UdtBindAny, 1);
            runner.Run("Feature_TCP_DuplicateSkip", TcpDuplicateSkip);
            runner.Run("Feature_TCP_DuplicateRename", TcpDuplicateRename);
            runner.Run("Feature_TCP_PerDeviceFolder", TcpPerDeviceFolder);
            runner.Run("Feature_TCP_IpFilter", TcpIpFilter);
            runner.Run("Feature_TCP_ConfirmDeny", TcpConfirmDeny);
            runner.Run("Feature_TCP_ConfirmAccept", TcpConfirmAccept);
            runner.Run("Feature_TCP_SessionStats", TcpSessionStats);
        }

        // ==================== Unit: session crypto ====================

        /// <summary>In-memory IWireStream for crypto round-trips.</summary>
        private class MemoryWireStream : IWireStream
        {
            public readonly MemoryStream Buffer = new MemoryStream();

            public Task WriteExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                Buffer.Write(buffer, offset, count);
                return Task.FromResult(0);
            }

            public Task ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (count == 0) return Task.FromResult(0);
                int read = Buffer.Read(buffer, offset, count);
                if (read < count)
                    throw new IOException("Memory stream exhausted");
                return Task.FromResult(0);
            }

            public Task<int> ReadSomeAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                return Task.FromResult(Buffer.Read(buffer, offset, count));
            }

            public void Dispose() { }
        }

        private static byte[] MakeSecret(int seed, int length)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static void RunWireCrypto(TestRunner runner)
        {
            runner.Run("WireCrypto_KeyDerivation_Deterministic", () =>
            {
                var psk = MakeSecret(1, 32);
                var salt = MakeSecret(2, 16);
                byte[] a1, a2, a3, a4, b1, b2, b3, b4;
                SessionCrypto.DeriveSessionKeys(psk, salt, out a1, out a2, out a3, out a4);
                SessionCrypto.DeriveSessionKeys(psk, salt, out b1, out b2, out b3, out b4);
                Assert.True(Utils.ConstantTimeEquals(a1, b1), "c2sEnc stable");
                Assert.True(Utils.ConstantTimeEquals(a2, b2), "c2sMac stable");
                // All four keys must be pairwise distinct
                Assert.False(Utils.ConstantTimeEquals(a1, a3), "c2sEnc != s2cEnc");
                Assert.False(Utils.ConstantTimeEquals(a1, a2), "enc != mac");
            });

            runner.Run("WireCrypto_KeyDerivation_SaltMatters", () =>
            {
                var psk = MakeSecret(3, 32);
                byte[] a1, a2, a3, a4, b1, b2, b3, b4;
                SessionCrypto.DeriveSessionKeys(psk, MakeSecret(4, 16), out a1, out a2, out a3, out a4);
                SessionCrypto.DeriveSessionKeys(psk, MakeSecret(5, 16), out b1, out b2, out b3, out b4);
                Assert.False(Utils.ConstantTimeEquals(a1, b1), "different salt -> different key");
            });

            runner.Run("WireCrypto_Roundtrip_LargeMultiSegment", () =>
            {
                var psk = MakeSecret(6, 32);
                var salt = MakeSecret(7, 16);
                byte[] c2sEnc, c2sMac, s2cEnc, s2cMac;
                SessionCrypto.DeriveSessionKeys(psk, salt, out c2sEnc, out c2sMac, out s2cEnc, out s2cMac);

                var wire = new MemoryWireStream();
                var payload = MakeSecret(8, 1024 * 1024 + 123); // crosses several 256 KB segments
                byte[] expected = (byte[])payload.Clone(); // encryption destroys the caller's buffer

                using (var enc = new EncryptedWireStream(wire, c2sEnc, c2sMac, s2cEnc, s2cMac))
                {
                    // Odd chunk sizes on purpose (segment edges + tiny frames)
                    int pos = 0;
                    int[] chunkSizes = { 7, 1000, EncryptedWireStream.MaxSegment, 1, 65536, 300000, 11 };
                    int ci = 0;
                    while (pos < payload.Length)
                    {
                        int n = Math.Min(chunkSizes[ci % chunkSizes.Length], payload.Length - pos);
                        enc.WriteExactAsync(payload, pos, n, CancellationToken.None).Wait();
                        pos += n;
                        ci++;
                    }
                }

                var plain = new byte[payload.Length];
                wire.Buffer.Position = 0;
                // The reader never sends, so only its incoming keys (keyIn/macIn = the
                // writer's c2s keys) matter here
                using (var dec = new EncryptedWireStream(wire, s2cEnc, s2cMac, c2sEnc, c2sMac))
                {
                    dec.ReadExactAsync(plain, 0, plain.Length, CancellationToken.None).Wait();
                }
                Assert.True(Utils.ConstantTimeEquals(expected, plain), "1 MB roundtrip");
            });

            runner.Run("WireCrypto_ReadSome_Partial", () =>
            {
                var psk = MakeSecret(9, 32);
                var salt = MakeSecret(10, 16);
                byte[] c2sEnc, c2sMac, s2cEnc, s2cMac;
                SessionCrypto.DeriveSessionKeys(psk, salt, out c2sEnc, out c2sMac, out s2cEnc, out s2cMac);

                var wire = new MemoryWireStream();
                var payload = MakeSecret(11, 5000);
                byte[] expected = (byte[])payload.Clone(); // encryption destroys the caller's buffer
                using (var enc = new EncryptedWireStream(wire, c2sEnc, c2sMac, s2cEnc, s2cMac))
                    enc.WriteExactAsync(payload, 0, payload.Length, CancellationToken.None).Wait();

                wire.Buffer.Position = 0;
                using (var dec = new EncryptedWireStream(wire, s2cEnc, s2cMac, c2sEnc, c2sMac))
                {
                    var got = new byte[payload.Length];
                    int total = 0;
                    while (total < got.Length)
                    {
                        int n = dec.ReadSomeAsync(got, total, 777, CancellationToken.None).Result;
                        Assert.True(n > 0, "progress on every ReadSome");
                        total += n;
                    }
                    Assert.True(Utils.ConstantTimeEquals(expected, got), "ReadSome assembly");
                }
            });

            runner.Run("WireCrypto_TamperCiphertext_Detected", () =>
            {
                var psk = MakeSecret(12, 32);
                var salt = MakeSecret(13, 16);
                byte[] c2sEnc, c2sMac, s2cEnc, s2cMac;
                SessionCrypto.DeriveSessionKeys(psk, salt, out c2sEnc, out c2sMac, out s2cEnc, out s2cMac);

                var wire = new MemoryWireStream();
                using (var enc = new EncryptedWireStream(wire, c2sEnc, c2sMac, s2cEnc, s2cMac))
                    enc.WriteExactAsync(MakeSecret(14, 1000), 0, 1000, CancellationToken.None).Wait();

                byte[] raw = wire.Buffer.ToArray();
                raw[4 + 16 + 50] ^= 0xFF; // flip one ciphertext byte

                bool threw = false;
                using (var dec = new EncryptedWireStream(
                    new MemoryStreamWire(raw), s2cEnc, s2cMac, c2sEnc, c2sMac))
                {
                    try
                    {
                        var buf = new byte[1000];
                        dec.ReadExactAsync(buf, 0, buf.Length, CancellationToken.None).Wait();
                    }
                    catch (AggregateException ex)
                    {
                        threw = ex.InnerException is IOException;
                    }
                }
                Assert.True(threw, "tampered ciphertext must throw IOException");
            });

            runner.Run("WireCrypto_WrongKey_Detected", () =>
            {
                var psk = MakeSecret(15, 32);
                var salt = MakeSecret(16, 16);
                byte[] c2sEnc, c2sMac, s2cEnc, s2cMac;
                SessionCrypto.DeriveSessionKeys(psk, salt, out c2sEnc, out c2sMac, out s2cEnc, out s2cMac);

                var wire = new MemoryWireStream();
                using (var enc = new EncryptedWireStream(wire, c2sEnc, c2sMac, s2cEnc, s2cMac))
                    enc.WriteExactAsync(MakeSecret(17, 500), 0, 500, CancellationToken.None).Wait();

                // Wrong key material everywhere
                byte[] w1, w2, w3, w4;
                SessionCrypto.DeriveSessionKeys(MakeSecret(18, 32), salt, out w1, out w2, out w3, out w4);

                bool threw = false;
                using (var dec = new EncryptedWireStream(new MemoryStreamWire(wire.Buffer.ToArray()), w1, w2, w3, w4))
                {
                    try
                    {
                        var buf = new byte[500];
                        dec.ReadExactAsync(buf, 0, buf.Length, CancellationToken.None).Wait();
                    }
                    catch (AggregateException ex)
                    {
                        threw = ex.InnerException is IOException;
                    }
                }
                Assert.True(threw, "wrong keys must fail MAC verification");
            });

            runner.Run("WireCrypto_EmptyWrite_NoOp", () =>
            {
                var psk = MakeSecret(19, 32);
                var salt = MakeSecret(20, 16);
                byte[] c2sEnc, c2sMac, s2cEnc, s2cMac;
                SessionCrypto.DeriveSessionKeys(psk, salt, out c2sEnc, out c2sMac, out s2cEnc, out s2cMac);
                var wire = new MemoryWireStream();
                using (var enc = new EncryptedWireStream(wire, c2sEnc, c2sMac, s2cEnc, s2cMac))
                    enc.WriteExactAsync(new byte[10], 0, 0, CancellationToken.None).Wait();
                Assert.Equal(0L, wire.Buffer.Length, "zero-length write writes nothing");
            });
        }

        /// <summary>Read-only in-memory stream seeded from a byte array.</summary>
        private class MemoryStreamWire : IWireStream
        {
            private readonly MemoryStream _ms;
            public MemoryStreamWire(byte[] data) { _ms = new MemoryStream(data, false); }
            public Task WriteExactAsync(byte[] b, int o, int c, CancellationToken ct) { throw new IOException("read-only"); }
            public Task ReadExactAsync(byte[] b, int o, int c, CancellationToken ct)
            {
                if (c == 0) return Task.FromResult(0);
                int read = _ms.Read(b, o, c);
                if (read < c) throw new IOException("exhausted");
                return Task.FromResult(0);
            }
            public Task<int> ReadSomeAsync(byte[] b, int o, int c, CancellationToken ct)
            {
                return Task.FromResult(_ms.Read(b, o, c));
            }
            public void Dispose() { _ms.Dispose(); }
        }

        // ==================== Unit: IP filter / disk / stats ====================

        private static void RunIpFilter(TestRunner runner)
        {
            runner.Run("IpFilter_ExactMatch", () =>
                Assert.True(IpFilter.Matches("192.168.1.5;10.0.0.1", "192.168.1.5"), "exact"));
            runner.Run("IpFilter_Wildcard", () =>
            {
                Assert.True(IpFilter.Matches("192.168.1.*", "192.168.1.77"), "prefix hit");
                Assert.False(IpFilter.Matches("192.168.1.*", "192.168.2.77"), "prefix miss");
                Assert.False(IpFilter.Matches("192.168.1.*", "192.168.11.7"), "no partial-octet match");
            });
            runner.Run("IpFilter_StarAll", () =>
                Assert.True(IpFilter.Matches("*", "8.8.8.8"), "* matches all"));
            runner.Run("IpFilter_Empty", () =>
            {
                Assert.False(IpFilter.Matches("", "1.2.3.4"), "empty list");
                Assert.False(IpFilter.Matches(null, "1.2.3.4"), "null list");
            });
            runner.Run("IpFilter_CommasAndSpaces", () =>
                Assert.True(IpFilter.Matches(" 10.0.0.1 , 10.0.0.2 ", "10.0.0.2"), "comma list trimmed"));
        }

        private static void RunDiskSpace(TestRunner runner)
        {
            runner.Run("DiskSpace_PlentyAvailable", () =>
                Assert.True(Utils.HasFreeSpace(TempBase(), 1024), "1 KB is available"));
            runner.Run("DiskSpace_AbsurdRequest_False", () =>
                Assert.False(Utils.HasFreeSpace(TempBase(), long.MaxValue - 1), "absurd request rejected"));
            runner.Run("DiskSpace_MissingDir_True", () =>
                Assert.True(Utils.HasFreeSpace(Path.Combine(TempBase(), "does_not_exist_xyz"), 1024),
                    "falls back to the drive root"));
        }

        private static string TempBase()
        {
            if (Directory.Exists(@"D:\cc")) return @"D:\cc\tmp";
            return Path.GetTempPath();
        }

        // ==================== Unit: QR ====================

        private static void RunQr(TestRunner runner)
        {
            runner.Run("Qr_Structure", () =>
            {
                var qr = QrEncoder.Encode("http://192.168.1.5:8090/?t=123456");
                int size = qr.Size;
                Assert.Equal(4 * qr.Version + 17, size, "size matches version");
                Assert.True(qr.Modules[0, 0], "finder TL dark");
                Assert.True(qr.Modules[0, size - 1], "finder TR dark");
                Assert.True(qr.Modules[size - 1, 0], "finder BL dark");
                Assert.True(qr.Modules[size - 8, 8], "dark module");
                // Timing pattern row 6 must alternate
                for (int x = 8; x < size - 8; x++)
                    Assert.True(qr.Modules[6, x] != qr.Modules[6, x + 1], "timing row alternates");
                for (int y = 8; y < size - 8; y++)
                    Assert.True(qr.Modules[y, 6] != qr.Modules[y + 1, 6], "timing column alternates");
            });

            runner.Run("Qr_Deterministic", () =>
            {
                var a = QrEncoder.Encode("HELLO WORLD");
                var b = QrEncoder.Encode("HELLO WORLD");
                Assert.Equal(a.Size, b.Size, "same size");
                for (int y = 0; y < a.Size; y++)
                    for (int x = 0; x < a.Size; x++)
                        Assert.Equal(a.Modules[y, x], b.Modules[y, x], "same modules");
            });

            runner.Run("Qr_EmptyPayload_Throws", () =>
                Assert.Throws(() => QrEncoder.Encode(""), "empty rejected"));

            runner.Run("Qr_TooLarge_Throws", () =>
                Assert.Throws(() => QrEncoder.Encode(new string('A', 5000)), "huge payload rejected"));

            runner.Run("Qr_MaxPayload_Fits", () =>
            {
                // Version 10 M holds 213 bytes; the encoder must find a version for any
                // payload up to that size without throwing
                var qr = QrEncoder.Encode(new string('B', 213));
                Assert.True(qr.Version >= 8, "large payload needs a high version");
            });
        }

        // ==================== Unit: stats store ====================

        private static void RunStatsStore(TestRunner runner)
        {
            string dir = Path.Combine(TempBase(), "tr_stats_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                runner.Run("StatsStore_RoundtripAndAggregate", () =>
                {
                    string path = Path.Combine(dir, "stats.log");
                    var store = new StatsStore(path);
                    DateTime now = DateTime.Now;
                    store.Append(new StatsEntry { When = now, Direction = 'S', Peer = "10.0.0.1", Bytes = 1000, Files = 1, Seconds = 1.5 });
                    store.Append(new StatsEntry { When = now, Direction = 'R', Peer = "10.0.0.1", Bytes = 500, Files = 1, Seconds = 0.5 });
                    store.Append(new StatsEntry { When = now.AddDays(-30), Direction = 'R', Peer = "10.0.0.2", Bytes = 7000, Files = 3, Seconds = 9 });

                    var entries = new StatsStore(path).LoadAll();
                    Assert.Equal(3, entries.Count, "all lines parsed");

                    var summary = StatsStore.Aggregate(entries, now);
                    Assert.Equal(2, summary.TodayCount, "today count");
                    Assert.Equal(1500L, summary.TodayBytes, "today bytes");
                    Assert.Equal(3, summary.AllCount, "all count");
                    Assert.Equal(8500L, summary.AllBytes, "all bytes");
                    Assert.Equal(2, summary.Peers.Count, "two peers");
                    Assert.Equal("10.0.0.2", summary.Peers[0].Peer, "biggest peer first");
                    Assert.Equal(500L, summary.Peers[1].Received, "peer received split");
                    Assert.Equal(1000L, summary.Peers[1].Sent, "peer sent split");
                });

                runner.Run("StatsStore_BadLinesIgnored", () =>
                {
                    Assert.True(StatsEntry.Parse("garbage") == null, "junk line");
                    Assert.True(StatsEntry.Parse("2026-09-07 10:00:00|X|1.2.3.4|1|1|1") == null, "bad direction");
                    Assert.True(StatsEntry.Parse("2026-13-99 10:00:00|S|1.2.3.4|1|1|1") == null, "bad date");
                    var ok = StatsEntry.Parse("2026-09-07 10:00:00|R|1.2.3.4|100|2|3.5");
                    Assert.NotNull(ok, "valid line parses");
                    Assert.Equal(100L, ok.Bytes, "bytes");
                    Assert.Equal('R', ok.Direction, "direction");
                });
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ==================== Integration: TCP/UDT features ====================

        private static int FindFreePort()
        {
            for (int port = 45000; port < 45128; port++)
            {
                try
                {
                    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                    // Verify the port is equally free for UDP (UDT binds both)
                    var udp = new System.Net.Sockets.UdpClient(port);
                    udp.Close();
                    return port;
                }
                catch { }
            }
            throw new Exception("No free port found");
        }

        private static byte[] MakeTestFile(string path, int size)
        {
            var content = new byte[size];
            new Random(size).NextBytes(content);
            File.WriteAllBytes(path, content);
            return content;
        }

        private sealed class TcpServerFixture : IDisposable
        {
            public TransferServer Server;
            public int Port;
            public string SendDir;
            public string RecvDir;
            private readonly ManualResetEvent _started = new ManualResetEvent(false);

            public TcpServerFixture()
            {
                Port = FindFreePort();
                SendDir = Path.Combine(TempBase(), "tr_feat_send_" + Guid.NewGuid().ToString("N"));
                RecvDir = Path.Combine(TempBase(), "tr_feat_recv_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(SendDir);
                Directory.CreateDirectory(RecvDir);
                Server = new TransferServer("127.0.0.1", Port, RecvDir);
                Server.OnStarted += () => _started.Set();
            }

            public void Start()
            {
                Server.Start();
                if (!_started.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");
            }

            public void Dispose()
            {
                try { Server.Stop(); } catch { }
                try { Directory.Delete(SendDir, true); } catch { }
                try { Directory.Delete(RecvDir, true); } catch { }
            }
        }

        /// <summary>Sends and waits for BOTH sides: the client's completion and the
        /// server's per-connection completion, then lets the OS settle so directory
        /// listings and file reads see the finished write (antivirus can hold a new
        /// file briefly).</summary>
        private static void SendAndWait(TcpServerFixture fx, string file, string pairing = null)
        {
            var client = new TransferClient("127.0.0.1", fx.Port, file);
            if (pairing != null) client.PairingCode = pairing;
            var clientDone = new ManualResetEvent(false);
            var serverDone = new ManualResetEvent(false);
            bool ok = false;
            string error = null;
            client.OnTransferComplete += () => { ok = true; clientDone.Set(); };
            client.OnError += msg => { error = msg; clientDone.Set(); };
            fx.Server.OnTransferComplete += () => serverDone.Set();
            var sendTask = client.SendAsync();
            if (!clientDone.WaitOne(30000))
                throw new Exception("client did not finish within 30s");
            if (!serverDone.WaitOne(30000))
                throw new Exception("server did not finish within 30s");
            if (sendTask.Exception != null && error == null)
                error = sendTask.Exception.InnerException != null
                    ? sendTask.Exception.InnerException.Message : sendTask.Exception.Message;
            if (!ok)
                throw new Exception("send failed: " + (error ?? "unknown"));
            Thread.Sleep(300);
        }

        /// <summary>Waits until the directory holds exactly the expected file count
        /// (server writes may lag the client's completion by a beat).</summary>
        private static string[] WaitForFileCount(TcpServerFixture fx, int expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            string[] files = new string[0];
            while (DateTime.UtcNow < deadline)
            {
                files = Directory.GetFiles(fx.RecvDir, "*", SearchOption.AllDirectories);
                if (files.Length == expected) return files;
                Thread.Sleep(200);
            }
            return files; // caller asserts and reports the mismatch
        }

        private static string[] RecvFiles(TcpServerFixture fx)
        {
            return Directory.GetFiles(fx.RecvDir, "*", SearchOption.AllDirectories);
        }

        private static void TcpEncrypted()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.PairingCode = "246810";
                string testFile = Path.Combine(fx.SendDir, "enc.bin");
                byte[] content = MakeTestFile(testFile, 1500 * 1024); // crosses several crypto segments

                var logs = new List<string>();
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                fx.Server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                fx.Server.OnError += _ => serverDone.Set();
                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                client.PairingCode = "246810"; // EncryptionEnabled defaults to true
                client.OnLog += msg => logs.Add(msg);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += _ => clientDone.Set();

                var sendTask = client.SendAsync();
                if (!serverDone.WaitOne(30000)) throw new Exception("server timeout");
                if (!clientDone.WaitOne(5000)) throw new Exception("client timeout");
                Assert.True(serverOk, "server completed");
                Assert.True(clientOk, "client completed");
                if (sendTask.Exception != null)
                    throw sendTask.Exception.InnerException ?? sendTask.Exception;

                bool clientEncrypted = false;
                foreach (var m in logs)
                {
                    if (m.Contains("ncrypted")) clientEncrypted = true;
                }
                Assert.True(clientEncrypted, "client log reports encryption");

                string[] received = WaitForFileCount(fx, 1);
                Assert.Equal(1, received.Length, "one file received");
                byte[] saved = File.ReadAllBytes(received[0]);
                Assert.True(Utils.ConstantTimeEquals(content, saved), "encrypted payload intact");
            }
        }

        private static void TcpEncryptDisabled()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.PairingCode = "112233";
                string testFile = Path.Combine(fx.SendDir, "plain.bin");
                MakeTestFile(testFile, 300 * 1024);

                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                fx.Server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                fx.Server.OnError += _ => serverDone.Set();
                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                client.PairingCode = "112233";
                client.EncryptionEnabled = false; // legacy plain 0x05 path
                var done = new ManualResetEvent(false);
                bool ok = false;
                client.OnTransferComplete += () => { ok = true; done.Set(); };
                client.OnError += _ => done.Set();
                var sendTask = client.SendAsync();

                Assert.True(serverDone.WaitOne(30000), "server timeout");
                Assert.True(done.WaitOne(5000), "client timeout");
                Assert.True(serverOk && ok, "plain auth transfer succeeds");
                if (sendTask.Exception != null)
                    throw sendTask.Exception.InnerException ?? sendTask.Exception;
                WaitForFileCount(fx, 1);
                Assert.Equal(1, RecvFiles(fx).Length, "file saved");
            }
        }

        private static void TcpEncryptWrongCode()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.PairingCode = "999999";
                string testFile = Path.Combine(fx.SendDir, "bad.bin");
                MakeTestFile(testFile, 8192);

                var serverDone = new ManualResetEvent(false);
                fx.Server.OnTransferComplete += () => serverDone.Set();
                fx.Server.OnError += _ => serverDone.Set();
                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                client.PairingCode = "111111"; // wrong — 0x17 status 1, no fallback
                var done = new ManualResetEvent(false);
                bool failed = false;
                client.OnTransferComplete += () => done.Set();
                client.OnError += _ => { failed = true; done.Set(); };
                client.SendAsync();

                Assert.True(serverDone.WaitOne(15000) || done.WaitOne(15000), "handshake ends");
                Assert.True(done.WaitOne(1000), "client fails fast");
                Assert.True(failed, "wrong code must fail");
                Assert.Equal(0, RecvFiles(fx).Length, "nothing saved");
            }
        }

        private static void TcpEncryptLenient()
        {
            // Client asks for encryption (0x07); server has no pairing code — answers
            // status 2 and both sides continue in plaintext on the same connection
            using (var fx = new TcpServerFixture())
            {
                string testFile = Path.Combine(fx.SendDir, "lenient.bin");
                MakeTestFile(testFile, 64 * 1024);

                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                fx.Server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                fx.Server.OnError += _ => serverDone.Set();
                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                client.PairingCode = "777777"; // server ignores (pairing off)
                var done = new ManualResetEvent(false);
                bool ok = false;
                client.OnTransferComplete += () => { ok = true; done.Set(); };
                client.OnError += _ => done.Set();
                var sendTask = client.SendAsync();

                Assert.True(serverDone.WaitOne(30000), "server timeout");
                Assert.True(done.WaitOne(5000), "client timeout");
                Assert.True(serverOk && ok, "lenient path succeeds");
                if (sendTask.Exception != null)
                    throw sendTask.Exception.InnerException ?? sendTask.Exception;
            }
        }

        private static void UdtEncrypted()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_f_u_send_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_f_u_recv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferUdtServer server = null;
            try
            {
                string testFile = Path.Combine(sendDir, "udt_enc.bin");
                byte[] content = MakeTestFile(testFile, 700 * 1024);

                server = new TransferUdtServer("127.0.0.1", port, recvDir);
                server.PairingCode = "135135";
                var started = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                server.OnStarted += () => started.Set();
                server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                server.OnError += _ => serverDone.Set();
                server.Start();
                if (!started.WaitOne(5000)) throw new Exception("UDT server did not start");

                var client = new TransferUdtClient("127.0.0.1", port, testFile);
                client.PairingCode = "135135";
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += _ => clientDone.Set();
                var sendTask = client.SendAsync();

                if (!serverDone.WaitOne(60000)) throw new Exception("UDT server timeout");
                if (!clientDone.WaitOne(5000)) throw new Exception("UDT client timeout");
                Assert.True(serverOk, "UDT server completed");
                Assert.True(clientOk, "UDT client completed");
                if (sendTask.Exception != null)
                    throw sendTask.Exception.InnerException ?? sendTask.Exception;

                string[] received = Directory.GetFiles(recvDir);
                Assert.Equal(1, received.Length, "one file");
                byte[] saved = File.ReadAllBytes(received[0]);
                Assert.True(Utils.ConstantTimeEquals(content, saved), "UDT encrypted payload intact");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void UdtBindAny()
        {
            // Regression: binding the UDT server to INADDR_ANY ("0.0.0.0") must work —
            // the UI combo passes this as a display string and IPAddress.Parse used to
            // throw FormatException out of Start()
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_f_ba_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_f_ba_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferUdtServer server = null;
            try
            {
                string testFile = Path.Combine(sendDir, "bindany.bin");
                byte[] content = MakeTestFile(testFile, 256 * 1024);

                server = new TransferUdtServer("0.0.0.0", port, recvDir);
                var started = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                server.OnStarted += () => started.Set();
                server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                server.OnError += _ => serverDone.Set();
                server.Start();
                if (!started.WaitOne(5000)) throw new Exception("UDT server did not start on 0.0.0.0");

                var client = new TransferUdtClient("127.0.0.1", port, testFile);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += _ => clientDone.Set();
                var sendTask = client.SendAsync();

                if (!serverDone.WaitOne(60000)) throw new Exception("UDT server timeout");
                if (!clientDone.WaitOne(5000)) throw new Exception("UDT client timeout");
                Assert.True(serverOk, "server completed");
                Assert.True(clientOk, "client completed");
                if (sendTask.Exception != null)
                    throw sendTask.Exception.InnerException ?? sendTask.Exception;

                string[] received = Directory.GetFiles(recvDir);
                Assert.Equal(1, received.Length, "one file");
                byte[] saved = File.ReadAllBytes(received[0]);
                Assert.True(Utils.ConstantTimeEquals(content, saved), "content intact");
            }
            finally
            {
                if (server != null) { try { server.Stop(); } catch { } }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void TcpDuplicateSkip()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.SkipDuplicateFiles = true;
                string testFile = Path.Combine(fx.SendDir, "dup.txt");
                byte[] content = MakeTestFile(testFile, 64 * 1024);

                var started = new ManualResetEvent(false);
                fx.Server.OnStarted += () => started.Set();
                fx.Start();
                if (!started.WaitOne(5000)) throw new Exception("server did not start");

                // First send: file lands normally
                SendAndWait(fx, testFile);
                string[] afterFirst = WaitForFileCount(fx, 1);
                Assert.Equal(1, afterFirst.Length, "first copy saved");

                // Second send of identical content: discarded, still one file
                SendAndWait(fx, testFile);
                string[] afterSecond = WaitForFileCount(fx, 1);
                Assert.Equal(1, afterSecond.Length, "identical duplicate skipped");
                byte[] saved = File.ReadAllBytes(afterSecond[0]);
                Assert.True(Utils.ConstantTimeEquals(content, saved), "original copy intact");
            }
        }

        private static void TcpDuplicateRename()
        {
            using (var fx = new TcpServerFixture())
            {
                // Default mode: rename (the historical behavior)
                string testFile = Path.Combine(fx.SendDir, "dup2.txt");
                MakeTestFile(testFile, 32 * 1024);

                fx.Start();
                SendAndWait(fx, testFile);
                SendAndWait(fx, testFile);

                string[] files = WaitForFileCount(fx, 2);
                Assert.Equal(2, files.Length, "both copies kept with suffixes");
            }
        }

        private static void TcpPerDeviceFolder()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.PerDeviceFolder = true;
                fx.Server.ResolveDeviceName = ip => ip == "127.0.0.1" ? "devA" : ip;
                string testFile = Path.Combine(fx.SendDir, "perdev.bin");
                byte[] content = MakeTestFile(testFile, 48 * 1024);

                fx.Start();
                SendAndWait(fx, testFile);

                string expectedDir = Path.Combine(fx.RecvDir, "devA");
                var deadline = DateTime.UtcNow.AddSeconds(15);
                string[] files = new string[0];
                while (DateTime.UtcNow < deadline)
                {
                    if (Directory.Exists(expectedDir))
                        files = Directory.GetFiles(expectedDir);
                    if (files.Length == 1) break;
                    Thread.Sleep(200);
                }
                Assert.True(Directory.Exists(expectedDir), "device folder created");
                Assert.Equal(1, files.Length, "file inside device folder");
                Assert.Equal("perdev.bin", Path.GetFileName(files[0]), "file name kept");
                byte[] saved = File.ReadAllBytes(files[0]);
                Assert.True(Utils.ConstantTimeEquals(content, saved), "content intact");
            }
        }

        private static void TcpIpFilter()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.IpAllowed = ip => false;
                string testFile = Path.Combine(fx.SendDir, "filtered.bin");
                MakeTestFile(testFile, 16 * 1024);

                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                var done = new ManualResetEvent(false);
                client.OnTransferComplete += () => done.Set();
                client.OnError += _ => done.Set();
                client.SendAsync();
                done.WaitOne(15000);

                // The deterministic guarantee is on the server side: the rejected
                // connection never saves anything (the client may or may not observe
                // the refusal, depending on when the RST lands)
                var noFileDeadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < noFileDeadline && RecvFiles(fx).Length > 0)
                    Thread.Sleep(200);
                Assert.Equal(0, RecvFiles(fx).Length, "nothing saved");

                // Allow and re-send
                fx.Server.IpAllowed = ip => true;
                SendAndWait(fx, testFile);
                WaitForFileCount(fx, 1);
                Assert.Equal(1, RecvFiles(fx).Length, "allowed after unblock");
            }
        }

        private static void TcpConfirmDeny()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.ConfirmRequest = (ip, name, size, files, isFolder) => Task.FromResult(false);
                string testFile = Path.Combine(fx.SendDir, "denied.bin");
                MakeTestFile(testFile, 16 * 1024);

                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                var done = new ManualResetEvent(false);
                client.OnTransferComplete += () => done.Set();
                client.OnError += _ => done.Set();
                client.SendAsync();
                done.WaitOne(15000);

                // Denied transfer must never land on disk (client-side failure timing
                // depends on the RST race and is not asserted)
                var noFileDeadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < noFileDeadline && RecvFiles(fx).Length > 0)
                    Thread.Sleep(200);
                Assert.Equal(0, RecvFiles(fx).Length, "denied file not saved");
            }
        }

        private static void TcpConfirmAccept()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.ConfirmRequest = (ip, name, size, files, isFolder) => Task.FromResult(true);
                string testFile = Path.Combine(fx.SendDir, "accepted.bin");
                MakeTestFile(testFile, 24 * 1024);

                fx.Start();
                SendAndWait(fx, testFile);
                WaitForFileCount(fx, 1);
                Assert.Equal(1, RecvFiles(fx).Length, "accepted file saved");
            }
        }

        private static void TcpSessionStats()
        {
            using (var fx = new TcpServerFixture())
            {
                string testFile = Path.Combine(fx.SendDir, "stats.bin");
                byte[] content = MakeTestFile(testFile, 96 * 1024);

                WireSessionStats seen = null;
                var statsSeen = new ManualResetEvent(false);
                fx.Server.OnSessionStats += s => { seen = s; statsSeen.Set(); };

                fx.Start();
                SendAndWait(fx, testFile);

                if (!statsSeen.WaitOne(5000))
                    throw new Exception("session stats were not raised");
                Assert.Equal("127.0.0.1", seen.Peer, "peer recorded");
                Assert.Equal((long)content.Length, seen.Bytes, "bytes recorded");
                Assert.Equal(1, seen.Files, "file count recorded");
                Assert.False(seen.Encrypted, "no pairing -> plaintext session");
            }
        }
    }
}
