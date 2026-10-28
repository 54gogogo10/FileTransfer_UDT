using System;
using System.IO;
using System.Collections.Generic;

namespace TrFileTransfer.Tests
{
    public static class Assert
    {
        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(string.Format("[{0}] Expected: {1}, Actual: {2}", message, expected, actual));
        }

        public static void True(bool condition, string message)
        {
            if (!condition)
                throw new Exception(string.Format("[{0}] Expected true, got false", message));
        }

        public static void False(bool condition, string message)
        {
            if (condition)
                throw new Exception(string.Format("[{0}] Expected false, got true", message));
        }

        public static void NotNull(object value, string message)
        {
            if (value == null)
                throw new Exception(string.Format("[{0}] Expected not null, got null", message));
        }

        public static void Throws(Action action, string message)
        {
            try { action(); }
            catch { return; }
            throw new Exception(string.Format("[{0}] Expected exception, none thrown", message));
        }
    }

    public class TestRunner
    {
        private int _passed;
        private int _failed;
        private readonly List<string> _failures = new List<string>();

        public int Passed { get { return _passed; } }
        public int Failed { get { return _failed; } }

        /// <summary>Optional name filter from the TF_TEST_FILTER environment variable:
        /// when set, only tests whose name contains it run. Lets a single failing
        /// integration test be iterated on without paying for the whole slow suite.</summary>
        private static readonly string Filter = Environment.GetEnvironmentVariable("TF_TEST_FILTER");

        private static bool Matches(string name)
        {
            return string.IsNullOrEmpty(Filter) || name.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Flattens an AggregateException so a FAIL line names the real cause
        /// instead of the useless "one or more errors occurred".</summary>
        private static string Describe(Exception ex)
        {
            var agg = ex as AggregateException;
            if (agg != null && agg.InnerExceptions.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var inner in agg.InnerExceptions)
                {
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append(inner.GetType().Name).Append(": ").Append(inner.Message);
                }
                return sb.ToString();
            }
            return ex.GetType().Name + ": " + ex.Message;
        }

        public void Run(string name, Action test, int retries = 0)
        {
            if (!Matches(name))
                return;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    test();
                    _passed++;
                    Console.WriteLine("  PASS  " + name);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt < retries)
                    {
                        Console.WriteLine("  RETRY " + name + " (attempt " + (attempt + 1) + "): " + Describe(ex));
                        continue;
                    }
                    _failed++;
                    var msg = string.Format("  FAIL  {0} — {1}", name, Describe(ex));
                    Console.WriteLine(msg);
                    _failures.Add(msg);
                    return;
                }
            }
        }

        public void PrintSummary()
        {
            Console.WriteLine();
            Console.WriteLine(string.Format("Results: {0} passed, {1} failed, {2} total",
                _passed, _failed, _passed + _failed));
            if (_failures.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Failures:");
                foreach (var f in _failures)
                    Console.WriteLine(f);
            }
        }
    }

    public static class UnitTests
    {
        public static void RunAll(TestRunner runner)
        {
            RunFormatSize(runner);
            RunSanitizeRelativePath(runner);
            RunConstantTimeEquals(runner);
            RunGetUniqueSavePath(runner);
            RunConfig(runner);
            RunConfigBackup(runner);
            RunDeviceDisplay(runner);
            RunQueueStore(runner);
            RunUdpFecOrder(runner);
            RunL10N(runner);
            RunKeepAwake(runner);
            RunUdpPartStore(runner);
            RunTextHistory(runner);
            RunIpFilterCidr(runner);
            RunServerResumeStore(runner);
            RunFolderResumeStore(runner);
            RunUpdater(runner);
            RunWire(runner);
            RunSyncBatch(runner);
            RunPortProbe(runner);
        }

        /// <summary>KeepAwake is a counter around a per-thread Windows call; the
        /// count itself is the testable contract (pairs nest, unpaired Ends clamp).</summary>
        private static void RunKeepAwake(TestRunner runner)
        {
            runner.Run("KeepAwake_RefCountsNest", () =>
            {
                int baseline = KeepAwake.RefCount;
                KeepAwake.Begin();
                KeepAwake.Begin();
                KeepAwake.Begin();
                Assert.Equal(baseline + 3, KeepAwake.RefCount, "three begins nest");
                KeepAwake.End();
                KeepAwake.End();
                Assert.Equal(baseline + 1, KeepAwake.RefCount, "two ends release two");
                KeepAwake.End();
                Assert.Equal(baseline, KeepAwake.RefCount, "last end restores the count");
            });
            runner.Run("KeepAwake_UnpairedEndClamps", () =>
            {
                int baseline = KeepAwake.RefCount; // 0 in practice; delta keeps this robust
                KeepAwake.End();
                KeepAwake.End();
                Assert.Equal(baseline, KeepAwake.RefCount,
                    "unpaired ends clamp at zero instead of going negative");
                KeepAwake.Begin();
                KeepAwake.End();
                Assert.Equal(baseline, KeepAwake.RefCount, "still pairable after a clamp");
            });
        }

        /// <summary>UdpPartStore is the single implementation behind both the UI's
        /// .part management and (indirectly) the orphan sweep contract.</summary>
        private static void RunUdpPartStore(TestRunner runner)
        {
            runner.Run("UdpPartStore_ListDeletePurge", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "tr_parts_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(UdpPartStore.PartDir(dir));
                try
                {
                    Assert.Equal(0, UdpPartStore.ListParts(dir).Count, "fresh dir lists nothing");
                    string a = Path.Combine(UdpPartStore.PartDir(dir), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.part");
                    string b = Path.Combine(UdpPartStore.PartDir(dir), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.part");
                    string other = Path.Combine(UdpPartStore.PartDir(dir), "notes.txt");
                    File.WriteAllBytes(a, new byte[100]);
                    File.WriteAllBytes(b, new byte[200]);
                    File.WriteAllText(other, "keep me");

                    var parts = UdpPartStore.ListParts(dir);
                    Assert.Equal(2, parts.Count, "only *.part files listed");
                    Assert.Equal(100L, parts[0].Bytes, "size recorded");
                    Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.part", Path.GetFileName(parts[0].Path),
                        "path-sorted, first is a...");

                    Assert.True(UdpPartStore.DeletePart(a), "delete one part");
                    Assert.False(UdpPartStore.DeletePart(a), "already-missing part reports false");
                    Assert.True(File.Exists(other), "non-part file untouched");
                    Assert.Equal(1, UdpPartStore.ListParts(dir).Count, "one part left");

                    Assert.Equal(1, UdpPartStore.DeleteAll(dir), "purge removes the rest");
                    Assert.Equal(0, UdpPartStore.ListParts(dir).Count, "nothing left after purge");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });
            runner.Run("UdpPartStore_MissingDirSafe", () =>
            {
                Assert.Equal(0, UdpPartStore.ListParts(null).Count, "null save dir lists nothing");
                Assert.Equal(0,
                    UdpPartStore.ListParts(Path.Combine(Path.GetTempPath(), "tr_nope_" + Guid.NewGuid().ToString("N"))).Count,
                    "missing dir lists nothing");
                Assert.Equal(0,
                    UdpPartStore.DeleteAll(Path.Combine(Path.GetTempPath(), "tr_nope2_" + Guid.NewGuid().ToString("N"))),
                    "missing dir purges nothing");
            });
        }

        /// <summary>Text history keeps received messages across restarts; the escape
        /// round-trip is the contract everything else hangs on.</summary>
        private static void RunTextHistory(TestRunner runner)
        {
            runner.Run("TextHistory_RoundTripAndEscape", () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "tr_th_" + Guid.NewGuid().ToString("N") + ".log");
                var store = new TextHistoryStore(path);
                try
                {
                    Assert.Equal(0, store.Load().Count, "fresh store is empty");
                    string tricky = "中文 line1\r\nline2 \\path\\to C:\\new\rfile";
                    store.Add(tricky);
                    store.Add("second");
                    var loaded = store.Load();
                    Assert.Equal(2, loaded.Count, "both messages persisted");
                    Assert.Equal(tricky, loaded[0].Text, "multi-line + backslash + Chinese round-trips");
                    Assert.Equal("second", loaded[1].Text, "plain message round-trips");
                    Assert.True(loaded[1].When > loaded[0].When.AddSeconds(-5), "timestamps recorded");
                }
                finally { try { File.Delete(path); } catch { } }
            });
            runner.Run("TextHistory_CapsAtMaxEntries", () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "tr_th_" + Guid.NewGuid().ToString("N") + ".log");
                var store = new TextHistoryStore(path);
                try
                {
                    int over = TextHistoryStore.MaxEntries + 50;
                    for (int i = 0; i < over; i++)
                        store.Add("msg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var loaded = store.Load();
                    Assert.Equal(TextHistoryStore.MaxEntries, loaded.Count, "bounded to MaxEntries");
                    Assert.Equal("msg" + (over - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        loaded[loaded.Count - 1].Text, "newest kept");
                    Assert.Equal(
                        "msg" + (over - TextHistoryStore.MaxEntries).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        loaded[0].Text, "oldest kept is exactly the window edge");
                }
                finally { try { File.Delete(path); } catch { } }
            });
            runner.Run("TextHistory_CorruptLinesSkipped", () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "tr_th_" + Guid.NewGuid().ToString("N") + ".log");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllLines(path, new[]
                    {
                        "garbage without a timestamp bar",
                        "9999-99-99 99:99:99|bad date",
                        "2026-01-02 03:04:05|real message"
                    });
                    var loaded = new TextHistoryStore(path).Load();
                    Assert.Equal(1, loaded.Count, "only the valid line survives");
                    Assert.Equal("real message", loaded[0].Text, "valid line parses");
                }
                finally { try { File.Delete(path); } catch { } }
            });
        }

        /// <summary>CIDR entries in the IP filter: prefix math for both families,
        /// host bits tolerated, broken entries never match (fail closed).</summary>
        private static void RunIpFilterCidr(TestRunner runner)
        {
            runner.Run("IpFilter_CidrV4", () =>
            {
                Assert.True(IpFilter.Matches("192.168.1.0/24", "192.168.1.77"), "/24 covers its network");
                Assert.False(IpFilter.Matches("192.168.1.0/24", "192.168.2.1"), "/24 stops at the boundary");
                Assert.True(IpFilter.Matches("10.0.0.0/8", "10.255.3.4"), "/8 covers the whole class-A");
                Assert.True(IpFilter.Matches("0.0.0.0/0", "203.0.113.9"), "/0 covers the family");
                Assert.True(IpFilter.Matches("192.168.1.77/24", "192.168.1.5"),
                    "host bits on the network side are masked away");
                Assert.False(IpFilter.Matches("192.168.1.0/24", "::1"), "v4 network never matches a v6 peer");
            });
            runner.Run("IpFilter_CidrV6AndMixed", () =>
            {
                Assert.True(IpFilter.Matches("fd00::/8", "fd12:3456::9"), "/8 v6 prefix");
                Assert.False(IpFilter.Matches("fd00::/8", "fe80::1"), "different v6 prefix");
                Assert.False(IpFilter.Matches("fd00::/8", "192.168.1.1"), "v6 network never matches a v4 peer");
                Assert.True(IpFilter.Matches("::1/128", "::1"), "/128 exact");
                Assert.False(IpFilter.Matches("::1/128", "::2"), "/128 rejects neighbors");
                Assert.True(IpFilter.Matches("192.168.0.0/16;10.0.0.5", "10.0.0.5"),
                    "CIDR and exact entries coexist (exact hit)");
                Assert.True(IpFilter.Matches("192.168.0.0/16;10.0.0.5", "192.168.44.3"),
                    "CIDR and exact entries coexist (CIDR hit)");
                Assert.False(IpFilter.Matches("192.168.0.0/16;10.0.0.5", "172.16.0.1"),
                    "mixed list still excludes the rest");
            });
            runner.Run("IpFilter_CidrBrokenEntriesNeverMatch", () =>
            {
                Assert.False(IpFilter.Matches("300.300.300.300/24", "192.168.1.1"), "invalid network");
                Assert.False(IpFilter.Matches("192.168.1.0/33", "192.168.1.1"), "prefix too long for v4");
                Assert.False(IpFilter.Matches("fd00::/129", "fd00::1"), "prefix too long for v6");
                Assert.False(IpFilter.Matches("192.168.1.0/", "192.168.1.1"), "missing length");
                Assert.False(IpFilter.Matches("/24", "192.168.1.1"), "missing network");
                Assert.False(IpFilter.Matches("192.168.1.0/abc", "192.168.1.1"), "non-numeric length");
            });
        }

        private static void RunFormatSize(TestRunner runner)
        {
            runner.Run("FormatSize_Zero", () =>
                Assert.Equal("0.0 B", Utils.FormatSize(0L), "0 bytes"));
            runner.Run("FormatSize_512B", () =>
                Assert.Equal("512.0 B", Utils.FormatSize(512L), "512 bytes"));
            runner.Run("FormatSize_1023B", () =>
                Assert.Equal("1023.0 B", Utils.FormatSize(1023L), "1023 bytes"));
            runner.Run("FormatSize_1KB", () =>
                Assert.Equal("1.0 KB", Utils.FormatSize(1024L), "1 KB"));
            runner.Run("FormatSize_1_5KB", () =>
                Assert.Equal("1.5 KB", Utils.FormatSize(1536L), "1.5 KB"));
            runner.Run("FormatSize_1MB", () =>
                Assert.Equal("1.0 MB", Utils.FormatSize(1048576L), "1 MB"));
            runner.Run("FormatSize_1GB", () =>
                Assert.Equal("1.0 GB", Utils.FormatSize(1073741824L), "1 GB"));
            runner.Run("FormatSize_1TB", () =>
                Assert.Equal("1.0 TB", Utils.FormatSize(1099511627776L), "1 TB"));
        }

        private static void RunSanitizeRelativePath(TestRunner runner)
        {
            var sep = Path.DirectorySeparatorChar.ToString();
            runner.Run("Sanitize_DotDot", () =>
                Assert.Equal("_", Utils.SanitizeRelativePath(".."), ".. -> _"));
            runner.Run("Sanitize_Dot", () =>
                Assert.Equal("_", Utils.SanitizeRelativePath("."), ". -> _"));
            runner.Run("Sanitize_PathTraversal", () =>
                Assert.Equal(string.Format("_{0}etc{0}passwd", sep),
                    Utils.SanitizeRelativePath("../etc/passwd"), "../etc/passwd"));
            runner.Run("Sanitize_NormalPath", () =>
                Assert.Equal(string.Format("subdir{0}file.txt", sep),
                    Utils.SanitizeRelativePath("subdir/file.txt"), "subdir/file.txt"));
            runner.Run("Sanitize_LeadingSlash", () =>
                Assert.Equal(string.Format("subdir{0}file.txt", sep),
                    Utils.SanitizeRelativePath("/subdir/file.txt"), "/subdir/file.txt"));
            runner.Run("Sanitize_EmptyPart", () =>
                Assert.Equal("_",
                    Utils.SanitizeRelativePath("/"), "empty parts -> _"));
            // A drive-letter path must never survive: Path.Combine returns a rooted
            // second argument verbatim, so "C:\evil" would escape the save directory
            runner.Run("Sanitize_DriveLetter", () =>
                Assert.Equal(string.Format("C_{0}evil", sep),
                    Utils.SanitizeRelativePath("C:\\evil"), "C:\\evil defused"));
            runner.Run("Sanitize_UncLikeDrive", () =>
                Assert.Equal(string.Format("server{0}share", sep),
                    Utils.SanitizeRelativePath("\\\\server\\share"), "UNC defused"));
            // Colons also address NTFS alternate data streams ("file.txt:hidden")
            runner.Run("Sanitize_AdsColon", () =>
                Assert.Equal(string.Format("sub{0}file.txt_ads", sep),
                    Utils.SanitizeRelativePath("sub/file.txt:ads"), "ADS colon defused"));
            // Reserved device names must not pass through a segment
            runner.Run("Sanitize_ReservedName", () =>
                Assert.Equal("_con", Utils.SanitizeRelativePath("con"), "con reserved"));
            // Trailing dots/spaces desync the unique-name collision check on Windows
            runner.Run("Sanitize_TrailingDots", () =>
                Assert.Equal("a.txt", Utils.SanitizeRelativePath("a.txt."), "trailing dot trimmed"));
            runner.Run("Sanitize_OnlyDots", () =>
                Assert.Equal("_", Utils.SanitizeRelativePath("..."), "only dots -> _"));
        }

        private static void RunConstantTimeEquals(TestRunner runner)
        {
            runner.Run("CTEquals_Same", () =>
                Assert.True(Utils.ConstantTimeEquals(
                    new byte[] { 1, 2, 3, 4 }, new byte[] { 1, 2, 3, 4 }), "same"));
            runner.Run("CTEquals_Different", () =>
                Assert.False(Utils.ConstantTimeEquals(
                    new byte[] { 1, 2, 3, 4 }, new byte[] { 1, 2, 3, 5 }), "different"));
            runner.Run("CTEquals_DiffLen", () =>
                Assert.False(Utils.ConstantTimeEquals(
                    new byte[] { 1, 2, 3 }, new byte[] { 1, 2 }), "diff length"));
            runner.Run("CTEquals_Empty", () =>
                Assert.True(Utils.ConstantTimeEquals(
                    new byte[0], new byte[0]), "empty arrays"));
            runner.Run("CTEquals_SingleByte", () =>
                Assert.True(Utils.ConstantTimeEquals(
                    new byte[] { 255 }, new byte[] { 255 }), "single byte"));
        }

        private static void RunGetUniqueSavePath(TestRunner runner)
        {
            var dir = Path.Combine(@"D:\cc\tmp", "tr_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                runner.Run("UniqueSavePath_NoCollision", () =>
                {
                    var result = Utils.GetUniqueSavePath(dir, "test.txt");
                    Assert.Equal(Path.Combine(dir, "test.txt"), result, "no collision");
                });

                runner.Run("UniqueSavePath_FileCollision", () =>
                {
                    File.WriteAllText(Path.Combine(dir, "test.txt"), "x");
                    var result = Utils.GetUniqueSavePath(dir, "test.txt");
                    Assert.Equal(Path.Combine(dir, "test_1.txt"), result, "file -> _1");
                });

                runner.Run("UniqueSavePath_MultipleCollision", () =>
                {
                    File.WriteAllText(Path.Combine(dir, "test_1.txt"), "x");
                    var result = Utils.GetUniqueSavePath(dir, "test.txt");
                    Assert.Equal(Path.Combine(dir, "test_2.txt"), result, "file -> _2");
                });

                runner.Run("UniqueSavePath_NoExt", () =>
                {
                    File.WriteAllText(Path.Combine(dir, "readme"), "x");
                    var result = Utils.GetUniqueSavePath(dir, "readme");
                    Assert.Equal(Path.Combine(dir, "readme_1"), result, "no ext -> _1");
                });
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void RunConfig(TestRunner runner)
        {
            runner.Run("Config_GetSetString", () =>
            {
                Config.Set("__ut_key", "hello");
                Assert.Equal("hello", Config.Get("__ut_key", ""), "get/set string");
            });

            runner.Run("Config_GetSetInt", () =>
            {
                Config.SetInt("__ut_int", 42);
                Assert.Equal(42, Config.GetInt("__ut_int", 0), "get/set int");
            });

            runner.Run("Config_GetSetBool", () =>
            {
                Config.SetBool("__ut_bool_t", true);
                Assert.True(Config.GetBool("__ut_bool_t", false), "get/set true");
                Config.SetBool("__ut_bool_f", false);
                Assert.False(Config.GetBool("__ut_bool_f", true), "get/set false");
            });

            runner.Run("Config_GetBool_Variants", () =>
            {
                Config.Set("__ut_bool1", "1");
                Assert.True(Config.GetBool("__ut_bool1", false), "1 = true");
                Config.Set("__ut_bool0", "0");
                Assert.False(Config.GetBool("__ut_bool0", true), "0 = false (if not true/1)");
            });

            runner.Run("Config_Fallback", () =>
            {
                Assert.Equal("default", Config.Get("__nonexistent_xyz", "default"), "string fallback");
                Assert.Equal(-1, Config.GetInt("__nonexistent_xyz", -1), "int fallback");
                Assert.True(Config.GetBool("__nonexistent_xyz", true), "bool fallback true");
                Assert.False(Config.GetBool("__nonexistent_xyz", false), "bool fallback false");
            });
        }

        /// <summary>Save keeps one good generation in .bak and swaps atomically; Load
        /// heals an unreadable or zero-length config from that backup. A missing
        /// config is a deliberate fresh start and must NOT resurrect from .bak.</summary>
        private static string NewTestConfigPath()
        {
            return Path.Combine(Path.GetTempPath(), "tr_cfg_" + Guid.NewGuid().ToString("N") + ".ini");
        }

        private static void CleanTestConfig(string p)
        {
            try { File.Delete(p); } catch { }
            try { File.Delete(p + ".bak"); } catch { }
            try { File.Delete(p + ".tmp"); } catch { }
        }

        private static void RunConfigBackup(TestRunner runner)
        {
            runner.Run("Config_BackupFallbackOnCorruption", () =>
            {
                string p = NewTestConfigPath();
                try
                {
                    Config.UseFile(p);
                    Config.Set("A", "1");
                    Config.Save();
                    Assert.True(File.Exists(p), "first save creates the config");
                    Assert.False(File.Exists(p + ".bak"), "no backup before a second generation");

                    Config.Set("B", "2");
                    Config.Save();
                    Assert.True(File.Exists(p + ".bak"), "second save keeps the previous generation");

                    // Corrupt the live file with content that parses to nothing
                    File.WriteAllText(p, "\0\0junk without any equals sign");
                    Config.UseFile(p); // clears the in-memory table like a restart
                    Config.Load();
                    Assert.Equal("1", Config.Get("A", ""), "A recovered from .bak");
                }
                finally { CleanTestConfig(p); }
            });

            runner.Run("Config_BackupFallbackOnTruncation", () =>
            {
                string p = NewTestConfigPath();
                try
                {
                    Config.UseFile(p);
                    Config.Set("K", "v");
                    Config.Set("K2", "v2");
                    Config.Save();
                    Config.Set("K3", "v3");
                    Config.Save(); // .bak now holds K/K2
                    File.WriteAllText(p, ""); // zero-length = crashed mid-write
                    Config.UseFile(p);
                    Config.Load();
                    Assert.Equal("v", Config.Get("K", ""), "K recovered from .bak");
                    Assert.Equal("v2", Config.Get("K2", ""), "K2 recovered from .bak");
                }
                finally { CleanTestConfig(p); }
            });

            runner.Run("Config_MissingFileIsFreshStart", () =>
            {
                string p = NewTestConfigPath();
                try
                {
                    Config.UseFile(p);
                    Config.Set("X", "gone");
                    Config.Save();
                    Config.Save(); // ensure .bak exists
                    Assert.True(File.Exists(p + ".bak"), "backup present");
                    File.Delete(p); // the user's reset gesture
                    Config.UseFile(p);
                    Config.Load();
                    Assert.Equal("", Config.Get("X", ""), "deleted config stays deleted (no .bak resurrection)");
                }
                finally { CleanTestConfig(p); }
            });

            runner.Run("Config_BakHoldsPreviousGeneration", () =>
            {
                string p = NewTestConfigPath();
                try
                {
                    Config.UseFile(p);
                    Config.Set("G", "1");
                    Config.Save();
                    Config.Set("G", "2");
                    Config.Save();
                    string bak = File.ReadAllText(p + ".bak");
                    Assert.True(bak.Contains("G=1") && !bak.Contains("G=2"),
                        ".bak froze the previous generation");
                    Assert.True(File.ReadAllText(p).Contains("G=2"), "live file has the newest value");
                }
                finally { CleanTestConfig(p); }
            });
        }

        /// <summary>Peer card titles: remembered name wins, junk names and unknown
        /// IPs fall back to the bare IP.</summary>
        private static void RunDeviceDisplay(TestRunner runner)
        {
            runner.Run("DeviceDisplay_NameFor", () =>
            {
                var known = new List<DeviceInfo>
                {
                    new DeviceInfo { Name = "办公室台式机", Ip = "192.168.1.20", Port = 8080 },
                    new DeviceInfo { Name = "?", Ip = "192.168.1.21", Port = 8080 },
                    new DeviceInfo { Name = "", Ip = "192.168.1.22", Port = 8080 }
                };
                Assert.Equal("办公室台式机 (192.168.1.20)", DeviceDisplay.NameFor(known, "192.168.1.20"),
                    "known device shows name + ip");
                Assert.Equal("192.168.1.21", DeviceDisplay.NameFor(known, "192.168.1.21"),
                    "'?' placeholder is not a name");
                Assert.Equal("192.168.1.22", DeviceDisplay.NameFor(known, "192.168.1.22"),
                    "empty name is not a name");
                Assert.Equal("10.0.0.9", DeviceDisplay.NameFor(known, "10.0.0.9"), "unknown ip falls through");
                Assert.Equal("10.0.0.9", DeviceDisplay.NameFor(null, "10.0.0.9"), "null list is safe");
                Assert.Equal("?", DeviceDisplay.NameFor(known, null), "null ip shows ?");
            });
        }

        /// <summary>The queue outlives restarts: every QueuedTask field round-trips,
        /// corrupt lines are dropped, the list stays bounded.</summary>
        private static void RunQueueStore(TestRunner runner)
        {
            runner.Run("QueueStore_RoundTripAllFields", () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "tr_qs_" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    var tasks = new List<QueuedTask>
                    {
                        new QueuedTask
                        {
                            FilePath = @"D:\资料\新建文件夹\报告 v2.pdf",
                            IsFolder = false,
                            ServerIp = "192.168.1.30",
                            Port = 8080,
                            IsUdp = false,
                            IsRawUdp = true,
                            SrcPort = 0,
                            Concurrency = 1,
                            VerifyHash = true,
                            SpeedLimit = 512000
                        },
                        new QueuedTask
                        {
                            FilePath = @"E:\备份\照片",
                            IsFolder = true,
                            ServerIp = "fd00::9",
                            Port = 9090,
                            IsUdp = true,
                            IsRawUdp = false,
                            SrcPort = 12345,
                            Concurrency = 4,
                            VerifyHash = false,
                            SpeedLimit = 0
                        }
                    };
                    QueueStore.Save(path, tasks);
                    var loaded = QueueStore.Load(path);
                    Assert.Equal(2, loaded.Count, "both tasks persisted");
                    Assert.Equal(tasks[0].FilePath, loaded[0].FilePath, "Chinese path round-trips");
                    Assert.True(loaded[0].IsRawUdp && !loaded[0].IsUdp, "raw-udp flag round-trips");
                    Assert.True(loaded[0].VerifyHash, "verify flag round-trips");
                    Assert.Equal(512000, loaded[0].SpeedLimit, "speed limit round-trips");
                    Assert.True(loaded[1].IsFolder, "folder flag round-trips");
                    Assert.Equal("fd00::9", loaded[1].ServerIp, "v6 target round-trips");
                    Assert.Equal(12345, loaded[1].SrcPort, "src port round-trips");
                    Assert.Equal(4, loaded[1].Concurrency, "concurrency round-trips");
                }
                finally { try { File.Delete(path); } catch { } }
            });
            runner.Run("QueueStore_BadLinesSkippedAndBounded", () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "tr_qs_" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    string good = QueueStore.Serialize(new QueuedTask
                    { FilePath = @"C:\ok.bin", ServerIp = "1.2.3.4", Port = 8080 });
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var lines = new List<string> { "garbage", "a|b|c", good };
                    for (int i = 0; i < QueueStore.MaxTasks; i++)
                        lines.Add(good);
                    File.WriteAllLines(path, lines.ToArray());
                    var loaded = QueueStore.Load(path);
                    Assert.Equal(QueueStore.MaxTasks, loaded.Count, "bounded to MaxTasks");
                    Assert.Equal(@"C:\ok.bin", loaded[0].FilePath, "garbage lines skipped, good ones kept");
                }
                finally { try { File.Delete(path); } catch { } }
            });
            runner.Run("QueueStore_MissingFileIsEmpty", () =>
            {
                Assert.Equal(0,
                    QueueStore.Load(Path.Combine(Path.GetTempPath(), "tr_qs_nope_" + Guid.NewGuid().ToString("N"))).Count,
                    "no file -> empty queue");
            });
        }

        /// <summary>FEC interleaving contract: the send order is a permutation, and
        /// same-group members ride at least GroupSize wire positions apart — the
        /// property that caps burst damage at one member per recoverable group.</summary>
        private static void RunUdpFecOrder(TestRunner runner)
        {
            runner.Run("UdpFec_InterleavedOrderProperties", () =>
            {
                int k = UdpOneWay.FecGroupSize;
                foreach (long total in new long[] { 1, 7, 8, 9, 63, 64, 65, 200, 1000 })
                {
                    var order = new System.Collections.Generic.List<long>(
                        UdpOneWay.InterleavedChunkOrder(total, k));
                    Assert.Equal((int)total, order.Count, "order covers every chunk exactly once (total=" + total + ")");

                    var seen = new bool[total];
                    foreach (long idx in order)
                    {
                        Assert.True(idx >= 0 && idx < total, "index in range");
                        Assert.False(seen[idx], "no duplicate index");
                        seen[idx] = true;
                    }

                    // Same-group members must be >= R positions apart, where R is the
                    // group count of that group's super-block (= k for every full
                    // block; the final partial block re-partitions into smaller groups
                    // so R = ceil(sbLen / ceil(sbLen/k))). Enumerate the same partition
                    // the implementation uses.
                    long super = (long)k * k;
                    for (long sbStart = 0; sbStart < total; sbStart += super)
                    {
                        long sbLen = Math.Min(super, total - sbStart);
                        long size = sbLen == super ? k : Math.Max(1, (sbLen + k - 1) / k);
                        long groups = (sbLen + size - 1) / size;
                        for (long g = 0; g < groups; g++)
                        {
                            long groupLo = sbStart + g * size;
                            long groupHi = Math.Min(sbStart + sbLen, groupLo + size);
                            var positions = new System.Collections.Generic.List<int>();
                            for (int pos = 0; pos < order.Count; pos++)
                                if (order[pos] >= groupLo && order[pos] < groupHi) positions.Add(pos);
                            Assert.Equal((int)(groupHi - groupLo), positions.Count, "group fully present");
                            for (int i = 1; i < positions.Count; i++)
                                Assert.True(positions[i] - positions[i - 1] >= groups,
                                    "group " + groupLo + " members spaced >= " + groups + " apart");
                        }
                    }
                }
            });
        }

        private static void RunServerResumeStore(TestRunner runner)
        {
            runner.Run("ServerResumeStore_RoundTrip", () =>
            {
                var sid = Guid.NewGuid();
                var state = new ResumeState
                {
                    SessionId = sid,
                    TotalSize = 123456789,
                    ReceivedBytes = 54321000,
                    FileName = "test.bin",
                    SavePath = @"D:\cc\tmp\server-resume-test\test.bin",
                    Created = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc)
                };
                ServerResumeStore.Save(state);
                try
                {
                    var loaded = ServerResumeStore.Load(sid);
                    Assert.True(loaded != null, "loaded not null");
                    Assert.Equal(state.TotalSize, loaded.TotalSize, "total size");
                    Assert.Equal(state.ReceivedBytes, loaded.ReceivedBytes, "received bytes");
                    Assert.Equal(state.FileName, loaded.FileName, "file name");
                    Assert.Equal(state.SavePath, loaded.SavePath, "save path");
                    Assert.Equal(state.Created, loaded.Created, "created");
                }
                finally
                {
                    ServerResumeStore.Delete(sid);
                }
            });

            runner.Run("ServerResumeStore_Missing", () =>
            {
                var loaded = ServerResumeStore.Load(Guid.NewGuid());
                Assert.True(loaded == null, "missing -> null");
            });

            runner.Run("ServerResumeStore_Delete", () =>
            {
                var sid = Guid.NewGuid();
                var state = new ResumeState { SessionId = sid, TotalSize = 1, SavePath = @"D:\x.bin" };
                ServerResumeStore.Save(state);
                ServerResumeStore.Delete(sid);
                Assert.True(ServerResumeStore.Load(sid) == null, "deleted -> null");
            });

            runner.Run("ServerResumeStore_CorruptFile", () =>
            {
                var sid = Guid.NewGuid();
                string path = ServerResumeStore.GetPath(sid);
                ServerResumeStore.EnsureDir();
                File.WriteAllText(path, "not a valid state file at all\nno equals sign\n");
                try
                {
                    var loaded = ServerResumeStore.Load(sid);
                    Assert.True(loaded != null, "corrupt file still returns empty state (no throw)");
                    Assert.Equal(0L, loaded.TotalSize, "corrupt fields default");
                }
                finally
                {
                    ServerResumeStore.Delete(sid);
                }
            });

            runner.Run("ServerResumeStore_CleanupStale", () =>
            {
                var oldSid = Guid.NewGuid();
                var freshSid = Guid.NewGuid();
                ServerResumeStore.Save(new ResumeState { SessionId = oldSid, TotalSize = 1, SavePath = @"D:\old.bin" });
                ServerResumeStore.Save(new ResumeState { SessionId = freshSid, TotalSize = 1, SavePath = @"D:\fresh.bin" });
                try
                {
                    File.SetLastWriteTime(ServerResumeStore.GetPath(oldSid), DateTime.Now.AddDays(-10));
                    File.SetLastWriteTime(ServerResumeStore.GetPath(freshSid), DateTime.Now.AddDays(-1));
                    ServerResumeStore.CleanupStale(7);
                    Assert.False(File.Exists(ServerResumeStore.GetPath(oldSid)), "stale session removed");
                    Assert.True(File.Exists(ServerResumeStore.GetPath(freshSid)), "fresh session kept");
                }
                finally
                {
                    ServerResumeStore.Delete(oldSid);
                    ServerResumeStore.Delete(freshSid);
                }
            });
        }

        private static void RunFolderResumeStore(TestRunner runner)
        {
            runner.Run("FolderResumeState_RoundTrip", () =>
            {
                var sid = Guid.NewGuid();
                var state = new FolderResumeState
                {
                    SessionId = sid,
                    FolderPath = @"D:\cc\tmp\fr-test\src",
                    FolderName = "docs",
                    ServerIp = "192.168.1.5",
                    Port = 9000,
                    IsUdt = true,
                    Created = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc),
                    FileCount = 7,
                    TotalBytes = 123456,
                    SentBytes = 1000
                };
                state.Save();
                try
                {
                    var loaded = FolderResumeState.Load(sid);
                    Assert.True(loaded != null, "loaded not null");
                    Assert.Equal(state.FolderPath, loaded.FolderPath, "folder path");
                    Assert.Equal(state.FolderName, loaded.FolderName, "folder name");
                    Assert.Equal(state.ServerIp, loaded.ServerIp, "server ip");
                    Assert.Equal(state.Port, loaded.Port, "port");
                    Assert.True(loaded.IsUdt, "isUdt");
                    Assert.Equal(state.Created, loaded.Created, "created");
                    Assert.Equal(state.FileCount, loaded.FileCount, "file count");
                    Assert.Equal(state.TotalBytes, loaded.TotalBytes, "total bytes");
                    Assert.Equal(state.SentBytes, loaded.SentBytes, "sent bytes");
                }
                finally
                {
                    FolderResumeState.Delete(sid);
                }
            });

            runner.Run("FolderResumeState_Delete", () =>
            {
                var sid = Guid.NewGuid();
                new FolderResumeState
                {
                    SessionId = sid, FolderPath = "x", FolderName = "y",
                    ServerIp = "1.2.3.4", Port = 1, Created = DateTime.UtcNow
                }.Save();
                FolderResumeState.Delete(sid);
                Assert.True(FolderResumeState.Load(sid) == null, "deleted -> null");
            });
        }

        private static void RunUpdater(TestRunner runner)
        {
            // ---- UpdateManifest.Parse ----
            runner.Run("Update_Parse_Valid", () =>
            {
                string text = "version=2.2.0.0\r\nurl=https://192.168.1.10/app.exe\nsha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\nnotes=Fixes\n";
                var m = UpdateManifest.Parse(text);
                Assert.True(m != null, "parsed");
                Assert.Equal(new Version(2, 2, 0, 0), m.Version, "version");
                Assert.Equal("https://192.168.1.10/app.exe", m.Url, "url");
                Assert.Equal("Fixes", m.Notes, "notes");
            });

            runner.Run("Update_Parse_InsecureHttpRejected", () =>
            {
                // Plain http is only accepted for loopback; a real host must be https,
                // otherwise a MITM serves both the exe and its matching hash.
                string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
                Assert.True(UpdateManifest.Parse("version=1.0.0.0\nurl=http://192.168.1.10/a.exe\nsha256=" + sha) == null,
                    "http on a real host rejected");
                Assert.True(UpdateManifest.Parse("version=1.0.0.0\nurl=http://localhost:8080/a.exe\nsha256=" + sha) != null,
                    "http on localhost accepted");
                Assert.True(UpdateManifest.Parse("version=1.0.0.0\nurl=http://127.0.0.1:8080/a.exe\nsha256=" + sha) != null,
                    "http on loopback IP accepted");
            });

            runner.Run("Update_Parse_UppercaseHashNormalized", () =>
            {
                string text = "version=1.0.0.0\nurl=https://example.com/app.exe\nsha256=0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
                var m = UpdateManifest.Parse(text);
                Assert.True(m != null, "parsed");
                Assert.True(m.Sha256Hex == m.Sha256Hex.ToLowerInvariant(), "hash lowercased");
            });

            runner.Run("Update_Parse_MissingVersion", () =>
                Assert.True(UpdateManifest.Parse("url=http://x/a.exe\nsha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef") == null, "null"));
            runner.Run("Update_Parse_MissingUrl", () =>
                Assert.True(UpdateManifest.Parse("version=1.0.0.0\nsha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef") == null, "null"));
            runner.Run("Update_Parse_MissingSha", () =>
                Assert.True(UpdateManifest.Parse("version=1.0.0.0\nurl=http://x/a.exe") == null, "null"));
            runner.Run("Update_Parse_BadVersion", () =>
                Assert.True(UpdateManifest.Parse("version=not.a.version\nurl=http://x/a.exe\nsha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef") == null, "null"));
            runner.Run("Update_Parse_HashTooShort", () =>
                Assert.True(UpdateManifest.Parse("version=1.0.0.0\nurl=http://x/a.exe\nsha256=abc123") == null, "null"));
            runner.Run("Update_Parse_HashNotHex", () =>
                Assert.True(UpdateManifest.Parse("version=1.0.0.0\nurl=http://x/a.exe\nsha256=zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef") == null, "null"));
            runner.Run("Update_Parse_BadScheme", () =>
                Assert.True(UpdateManifest.Parse("version=1.0.0.0\nurl=ftp://x/a.exe\nsha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef") == null, "null"));
            runner.Run("Update_Parse_Empty", () =>
                Assert.True(UpdateManifest.Parse("") == null, "null"));
            runner.Run("Update_Parse_UnknownKeysIgnored", () =>
            {
                string text = "# comment line\nfoo=bar\nversion=3.0.0.0\nurl=https://x/a.exe\nsha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
                var m = UpdateManifest.Parse(text);
                Assert.True(m != null, "unknown keys ignored");
                Assert.Equal(new Version(3, 0, 0, 0), m.Version, "version");
            });

            // ---- UpdateManifest.FromGitHubJson ----
            runner.Run("Update_GitHub_Parse_Valid", () =>
            {
                string json = "{\"url\":\"https://api.github.com/repos/o/r/releases/1\"," +
                    "\"tag_name\":\"v2.2.0.0\",\"name\":\"v2.2.0.0\"," +
                    "\"body\":\"Fixes\\nSpeedups\"," +
                    "\"draft\":false," +
                    "\"assets\":[" +
                    "{\"name\":\"sources.zip\",\"browser_download_url\":\"https://github.com/o/r/archive/refs.zip\"}," +
                    "{\"name\":\"TrFileTransfer.exe\",\"browser_download_url\":\"https://github.com/o/r/releases/download/v2.2.0.0/TrFileTransfer.exe\"}," +
                    "{\"name\":\"TrFileTransfer.exe.sha256\",\"browser_download_url\":\"https://github.com/o/r/releases/download/v2.2.0.0/TrFileTransfer.exe.sha256\"}" +
                    "]}";
                var m = UpdateManifest.FromGitHubJson(json);
                Assert.True(m != null, "parsed");
                Assert.Equal(new Version(2, 2, 0, 0), m.Version, "version from tag (v stripped)");
                Assert.Equal("https://github.com/o/r/releases/download/v2.2.0.0/TrFileTransfer.exe", m.Url, "first .exe asset");
                Assert.True(m.Url.IndexOf(".sha256", StringComparison.Ordinal) < 0, "sidecar not picked");
                Assert.Equal("Fixes\nSpeedups", m.Notes, "body as notes (escapes decoded)");
                Assert.True(m.Sha256Hex == null, "sha filled later by CheckGitHubAsync");
            });

            runner.Run("Update_GitHub_Parse_TagWithoutV", () =>
            {
                string json = "{\"tag_name\":\"3.1.4.1\",\"assets\":[{\"browser_download_url\":\"https://x/a.exe\"}]}";
                var m = UpdateManifest.FromGitHubJson(json);
                Assert.True(m != null, "parsed");
                Assert.Equal(new Version(3, 1, 4, 1), m.Version, "bare tag");
            });

            runner.Run("Update_GitHub_Parse_NoExeAsset", () =>
                Assert.True(UpdateManifest.FromGitHubJson(
                    "{\"tag_name\":\"v1.0.0.0\",\"assets\":[{\"browser_download_url\":\"http://x/src.zip\"}]}") == null,
                    "null without .exe asset"));

            runner.Run("Update_GitHub_Parse_MissingTag", () =>
                Assert.True(UpdateManifest.FromGitHubJson(
                    "{\"assets\":[{\"browser_download_url\":\"http://x/a.exe\"}]}") == null, "null without tag"));

            runner.Run("Update_GitHub_Parse_BadTag", () =>
                Assert.True(UpdateManifest.FromGitHubJson(
                    "{\"tag_name\":\"release-2026\",\"assets\":[{\"browser_download_url\":\"http://x/a.exe\"}]}") == null,
                    "null with non-version tag"));

            runner.Run("Update_GitHub_Parse_Empty", () =>
                Assert.True(UpdateManifest.FromGitHubJson("") == null, "null"));

            // ---- IsNewerThan ----
            runner.Run("Update_IsNewer_True", () =>
            {
                var m = new UpdateManifest { Version = new Version(2, 1, 0, 0) };
                Assert.True(m.IsNewerThan(new Version(2, 0, 9, 9)), "2.1.0.0 > 2.0.9.9");
            });
            runner.Run("Update_IsNewer_FalseOnEqual", () =>
            {
                var m = new UpdateManifest { Version = new Version(2, 1, 0, 0) };
                Assert.False(m.IsNewerThan(new Version(2, 1, 0, 0)), "equal not newer");
            });
            runner.Run("Update_IsNewer_FalseOnOlder", () =>
            {
                var m = new UpdateManifest { Version = new Version(1, 9, 9, 0) };
                Assert.False(m.IsNewerThan(new Version(2, 0, 0, 0)), "older not newer");
            });
            runner.Run("Update_IsNewer_RevisionCounts", () =>
            {
                var m = new UpdateManifest { Version = new Version(2, 0, 0, 1) };
                Assert.True(m.IsNewerThan(new Version(2, 0, 0, 0)), "revision bump counts");
            });

            // ---- ComputeSha256Hex ----
            runner.Run("Update_Sha256_KnownVector", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "tr_upd_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string path = Path.Combine(dir, "abc.txt");
                    File.WriteAllText(path, "abc");
                    // SHA256("abc")
                    Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                        Updater.ComputeSha256Hex(path), "sha256 of abc");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });

            // ---- Apply / rollback / backup cleanup ----
            var applyDir = Path.Combine(Path.GetTempPath(), "tr_upd_apply_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(applyDir);
            try
            {
                runner.Run("Update_Apply_SwapsAndBacksUp", () =>
                {
                    string target = Path.Combine(applyDir, "app.exe");
                    string staged = Path.Combine(applyDir, "staged.exe");
                    File.WriteAllText(target, "OLD");
                    File.WriteAllText(staged, "NEW");
                    Updater.Apply(staged, target);
                    Assert.Equal("NEW", File.ReadAllText(target), "target replaced");
                    Assert.Equal("OLD", File.ReadAllText(target + ".old"), "backup holds old content");
                });

                runner.Run("Update_Apply_RollbackOnFailure", () =>
                {
                    string target = Path.Combine(applyDir, "app2.exe");
                    File.WriteAllText(target, "OLD");
                    // staged file does not exist -> copy fails -> restore
                    Assert.Throws(delegate { Updater.Apply(Path.Combine(applyDir, "missing.exe"), target); },
                        "copy failure throws");
                    Assert.Equal("OLD", File.ReadAllText(target), "target restored");
                    Assert.False(File.Exists(target + ".old"), "no backup leftover after rollback");
                });

                runner.Run("Update_Apply_ReplacesStaleBackup", () =>
                {
                    string target = Path.Combine(applyDir, "app3.exe");
                    string staged = Path.Combine(applyDir, "staged3.exe");
                    File.WriteAllText(target, "OLD");
                    File.WriteAllText(staged, "NEW");
                    File.WriteAllText(target + ".old", "STALE");
                    Updater.Apply(staged, target);
                    Assert.Equal("OLD", File.ReadAllText(target + ".old"), "stale backup replaced by current old");
                });

                runner.Run("Update_DeleteStaleBackup", () =>
                {
                    string target = Path.Combine(applyDir, "app4.exe");
                    File.WriteAllText(target, "X");
                    File.WriteAllText(target + ".old", "OLD");
                    Updater.DeleteStaleBackup(target);
                    Assert.False(File.Exists(target + ".old"), "backup removed");
                    Assert.True(File.Exists(target), "target untouched");
                    Updater.DeleteStaleBackup(target); // no backup -> no throw
                });
            }
            finally
            {
                try { Directory.Delete(applyDir, true); } catch { }
            }
        }

        private static void RunWire(TestRunner runner)
        {
            runner.Run("WireAuth_HashCode_KnownVector", () =>
            {
                // SHA256("123456")
                string hex = BitConverter.ToString(WireAuth.HashCode("123456")).Replace("-", "").ToLowerInvariant();
                Assert.Equal("8d969eef6ecad3c29a3a629280e686cf0c3f5d5a86aff3ca12020c923adc6c92", hex, "sha256 of 123456");
            });

            runner.Run("WireAuth_HashCode_Deterministic", () =>
            {
                var a = WireAuth.HashCode("246810");
                var b = WireAuth.HashCode("246810");
                Assert.True(Utils.ConstantTimeEquals(a, b), "same code same hash");
                Assert.False(Utils.ConstantTimeEquals(a, WireAuth.HashCode("246811")), "different code different hash");
                Assert.True(Utils.ConstantTimeEquals(WireAuth.HashCode(null), WireAuth.HashCode("")), "null == empty");
            });

            runner.Run("WireAuth_GeneratePairingCode", () =>
            {
                for (int i = 0; i < 50; i++)
                {
                    string code = WireAuth.GeneratePairingCode();
                    Assert.Equal(6, code.Length, "6 digits");
                    for (int j = 0; j < code.Length; j++)
                        Assert.True(code[j] >= '0' && code[j] <= '9', "digit char");
                }
            });

            runner.Run("ServerWire_Preview", () =>
            {
                Assert.Equal("", ServerWire.Preview(""), "empty");
                Assert.Equal("a / b", ServerWire.Preview("a\nb"), "newline single-line");
                Assert.Equal("a / b", ServerWire.Preview("a\r\nb"), "crlf single-line");
                string longText = new string('x', 300);
                string p = ServerWire.Preview(longText);
                Assert.True(p.Length < 300 && p.EndsWith("…"), "long text truncated");
            });

            runner.Run("ServerWire_MaxTextBytes", () =>
            {
                Assert.Equal(1048576, ServerWire.MaxTextBytes, "1 MB cap");
            });
        }

        private static void RunSyncBatch(TestRunner runner)
        {
            // ---- FolderResumeState.DeriveSyncSession ----
            runner.Run("SyncSession_Deterministic", () =>
            {
                var a = FolderResumeState.DeriveSyncSession(@"D:\data\docs", "192.168.1.5", 8080, false);
                var b = FolderResumeState.DeriveSyncSession(@"D:\data\docs\", "192.168.1.5", 8080, false);
                var c = FolderResumeState.DeriveSyncSession(@"D:\DATA\DOCS", "192.168.1.5", 8080, false);
                Assert.Equal(a, b, "trailing slash ignored");
                Assert.Equal(a, c, "case-insensitive path");
            });

            runner.Run("SyncSession_DistinctInputs", () =>
            {
                var baseSession = FolderResumeState.DeriveSyncSession(@"D:\docs", "192.168.1.5", 8080, false);
                Assert.True(FolderResumeState.DeriveSyncSession(@"D:\other", "192.168.1.5", 8080, false) != baseSession, "path differs");
                Assert.True(FolderResumeState.DeriveSyncSession(@"D:\docs", "192.168.1.6", 8080, false) != baseSession, "ip differs");
                Assert.True(FolderResumeState.DeriveSyncSession(@"D:\docs", "192.168.1.5", 8081, false) != baseSession, "port differs");
                Assert.True(FolderResumeState.DeriveSyncSession(@"D:\docs", "192.168.1.5", 8080, true) != baseSession, "protocol differs");
            });

            // ---- IsSync persistence + resume-list filtering ----
            runner.Run("SyncState_RoundTripAndFilter", () =>
            {
                var sid = Guid.NewGuid();
                var plainSid = Guid.NewGuid();
                try
                {
                    new FolderResumeState
                    {
                        SessionId = sid, FolderPath = @"D:\x", FolderName = "x",
                        ServerIp = "1.2.3.4", Port = 1, Created = DateTime.UtcNow, IsSync = true
                    }.Save();
                    new FolderResumeState
                    {
                        SessionId = plainSid, FolderPath = @"D:\y", FolderName = "y",
                        ServerIp = "1.2.3.4", Port = 1, Created = DateTime.UtcNow, IsSync = false
                    }.Save();

                    var loaded = FolderResumeState.Load(sid);
                    Assert.True(loaded != null && loaded.IsSync, "IsSync persisted");

                    var list = FolderResumeState.ListAll();
                    Assert.False(list.Exists(s => s.SessionId == sid), "sync state hidden from resume list");
                    Assert.True(list.Exists(s => s.SessionId == plainSid), "normal state listed");
                }
                finally
                {
                    FolderResumeState.Delete(sid);
                    FolderResumeState.Delete(plainSid);
                }
            });

            // ---- AutoStart (HKCU Run key) ----
            runner.Run("AutoStart_SetAndRemove", () =>
            {
                try
                {
                    AutoStart.Set(false, null);
                    Assert.False(AutoStart.IsEnabled(), "disabled after clear");
                    AutoStart.Set(true, @"C:\Program Files\TrFileTransfer\TrFileTransfer.exe");
                    Assert.True(AutoStart.IsEnabled(), "enabled");
                    AutoStart.Set(true, @"C:\other\TrFileTransfer.exe");
                    Assert.True(AutoStart.IsEnabled(), "re-set keeps enabled");
                    AutoStart.Set(false, null);
                    Assert.False(AutoStart.IsEnabled(), "removed");
                    AutoStart.Set(false, null);
                    Assert.False(AutoStart.IsEnabled(), "remove is idempotent");
                }
                finally
                {
                    AutoStart.Set(false, null); // leave the machine clean
                }
            });

            // ---- Discovery pairing flag ----
            runner.Run("Discovery_PairingFlag", () =>
            {
                var from = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1234);
                byte[] withFlag = DiscoveryProtocol.BuildResponse("dev", 8080, true, true, true);
                var parsed = DiscoveryProtocol.ParseResponse(withFlag, withFlag.Length, from);
                Assert.True(parsed.HasValue, "parsed");
                Assert.True(parsed.Value.RequiresPairing, "pairing flag set");
                Assert.True(parsed.Value.SupportsTcp && parsed.Value.SupportsUdt, "protocol flags intact");

                byte[] withoutFlag = DiscoveryProtocol.BuildResponse("dev", 8080, true, false, false);
                var parsed2 = DiscoveryProtocol.ParseResponse(withoutFlag, withoutFlag.Length, from);
                Assert.True(parsed2.HasValue && !parsed2.Value.RequiresPairing, "pairing flag clear");
                Assert.True(parsed2.Value.SupportsTcp && !parsed2.Value.SupportsUdt, "protocols intact");
            });
        }

        private static void RunPortProbe(TestRunner runner)
        {
            runner.Run("PortProbe_FreePort", () =>
            {
                int port = Utils.FindFreePort(20000, false);
                Assert.True(port > 0, "found a free port");
                Assert.True(Utils.IsPortFree(port, true, true), "reported free is actually free");
            });

            runner.Run("PortProbe_OccupiedTcp", () =>
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                listener.Start();
                int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                try
                {
                    Assert.False(Utils.IsPortFree(port, true, false), "bound TCP port is not free");
                    Assert.True(Utils.FindFreePortFrom(port + 1, true, false) != port, "next free port differs");
                }
                finally { listener.Stop(); }
                Assert.True(Utils.IsPortFree(port, true, false), "free again after stop");
            });

            runner.Run("PortProbe_OccupiedUdp", () =>
            {
                var udp = new System.Net.Sockets.UdpClient(0);
                int port = ((System.Net.IPEndPoint)udp.Client.LocalEndPoint).Port;
                try
                {
                    Assert.False(Utils.IsPortFree(port, false, true), "bound UDP port is not free");
                }
                finally { udp.Close(); }
            });

            runner.Run("PortProbe_ProtocolIndependent", () =>
            {
                // Pick a port usable by BOTH stacks first: a raw TCP-ephemeral pick can
                // land inside a Windows UDP exclusion range (Hyper-V/WSL), which fails
                // the UDP probe for reasons unrelated to TCP/UDP independence.
                int basePort = Utils.FindFreePort(20000, false);
                int port = 0;
                for (int q = basePort; q < basePort + 256; q++)
                {
                    if (!Utils.IsPortFree(q, false, true)) continue;
                    var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, q);
                    try
                    {
                        probe.Start();
                        probe.Stop();
                        port = q;
                        break;
                    }
                    catch { }
                }
                Assert.True(port > 0, "found a port usable by both stacks");

                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                try
                {
                    // TCP occupied but UDP probe asked only — TCP result must not leak into UDP check
                    Assert.False(Utils.IsPortFree(port, true, false), "tcp busy");
                    Assert.True(Utils.IsPortFree(port, false, true), "udp independent of tcp");
                }
                finally { listener.Stop(); }
            });
        }

        private static void RunL10N(TestRunner runner)
        {
            runner.Run("L10N_English", () =>
            {
                L.IsChinese = false;
                Assert.Equal("File Transfer", L.AppTitle, "AppTitle EN");
                Assert.Equal("Ready", L.Ready, "Ready EN");
                Assert.Equal("Listening...", L.Listening, "Listening EN");
                Assert.Equal("Transfer complete!", L.TransferComplete, "Complete EN");
                Assert.Equal("Start", L.StartServer, "Start EN");
                Assert.Equal("Cancel", L.CancelBtn, "Cancel EN");
            });

            runner.Run("L10N_Chinese", () =>
            {
                L.IsChinese = true;
                Assert.Equal("文件传输", L.AppTitle, "AppTitle CN");
                Assert.Equal("就绪", L.Ready, "Ready CN");
                Assert.Equal("监听中...", L.Listening, "Listening CN");
                Assert.Equal("传输完成!", L.TransferComplete, "Complete CN");
            });

            runner.Run("L10N_Toggle", () =>
            {
                L.IsChinese = false;
                var en = L.AppTitle;
                L.IsChinese = true;
                var cn = L.AppTitle;
                Assert.False(en == cn, "EN != CN");
                L.IsChinese = false;
                Assert.Equal(en, L.AppTitle, "toggle back");
            });
        }
    }

    class TestProgram
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            // Isolate the whole suite from the developer's real config.ini — Config is a
            // process-global store, and tests exercising it used to stomp the live file
            // (observed as a GUI instance losing its settings mid-run).
            string testConfig = Path.Combine(Path.GetTempPath(), "TrFileTransfer-tests-config.ini");
            try { File.Delete(testConfig); } catch { }
            Config.UseFile(testConfig);
            Console.WriteLine("=== TrFileTransfer Test Suite ===");
            Console.WriteLine();

            var runner = new TestRunner();

            Console.WriteLine("--- Unit Tests ---");
            UnitTests.RunAll(runner);
            FeatureTests.RunUnit(runner);

            Console.WriteLine();
            Console.WriteLine("--- Integration Tests ---");
            IntegrationTests.RunAll(runner);
            FeatureTests.RunIntegration(runner);

            runner.PrintSummary();
            return runner.Failed > 0 ? 1 : 0;
        }
    }
}
