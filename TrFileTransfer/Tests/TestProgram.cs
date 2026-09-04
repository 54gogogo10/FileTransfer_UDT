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

        public void Run(string name, Action test, int retries = 0)
        {
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
                        Console.WriteLine("  RETRY " + name + " (attempt " + (attempt + 1) + "): " + ex.Message);
                        continue;
                    }
                    _failed++;
                    var msg = string.Format("  FAIL  {0} — {1}", name, ex.Message);
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
            RunL10N(runner);
            RunServerResumeStore(runner);
            RunFolderResumeStore(runner);
            RunUpdater(runner);
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
                string text = "version=2.2.0.0\r\nurl=http://192.168.1.10/app.exe\nsha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\nnotes=Fixes\n";
                var m = UpdateManifest.Parse(text);
                Assert.True(m != null, "parsed");
                Assert.Equal(new Version(2, 2, 0, 0), m.Version, "version");
                Assert.Equal("http://192.168.1.10/app.exe", m.Url, "url");
                Assert.Equal("Fixes", m.Notes, "notes");
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
                string text = "# comment line\nfoo=bar\nversion=3.0.0.0\nurl=http://x/a.exe\nsha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
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
                string json = "{\"tag_name\":\"3.1.4.1\",\"assets\":[{\"browser_download_url\":\"http://x/a.exe\"}]}";
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

        private static void RunL10N(TestRunner runner)
        {
            runner.Run("L10N_English", () =>
            {
                L.IsChinese = false;
                Assert.Equal("File Transfer", L.AppTitle, "AppTitle EN");
                Assert.Equal("Ready", L.Ready, "Ready EN");
                Assert.Equal("Listening...", L.Listening, "Listening EN");
                Assert.Equal("Transfer complete!", L.TransferComplete, "Complete EN");
                Assert.Equal("Start Server", L.StartServer, "Start EN");
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
            Console.WriteLine("=== TrFileTransfer Test Suite ===");
            Console.WriteLine();

            var runner = new TestRunner();

            Console.WriteLine("--- Unit Tests ---");
            UnitTests.RunAll(runner);

            Console.WriteLine();
            Console.WriteLine("--- Integration Tests ---");
            IntegrationTests.RunAll(runner);

            runner.PrintSummary();
            return runner.Failed > 0 ? 1 : 0;
        }
    }
}
