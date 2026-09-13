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
            RunWireCompress(runner);
            RunChunkTracker(runner);
            RunIpFilter(runner);
            RunDiskSpace(runner);
            RunQr(runner);
            RunStatsStore(runner);
            RunSpeedLimiter(runner);
            RunClientHashCache(runner);
            RunFileHashCache(runner);
            RunHashCancellation(runner);
            RunLibraryVerifier(runner);
            RunCli(runner);
        }

        // ==================== Unit: hash pass cancellation and progress ====================

        private static void RunHashCancellation(TestRunner runner)
        {
            runner.Run("ClientWire_ComputeFileHash_CancellationHonoured", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_hcanc_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string path = Path.Combine(dir, "a.bin");
                    var content = new byte[8 * 1024 * 1024]; // two buffer fills
                    new Random(101).NextBytes(content);
                    File.WriteAllBytes(path, content);

                    var cts = new CancellationTokenSource();
                    cts.Cancel();
                    try
                    {
                        ClientWire.ComputeFileHash(path, cts.Token);
                        Assert.True(false, "expected a cancelled hash to throw");
                    }
                    catch (OperationCanceledException) { }

                    // Same file, live token: the digest is still the real one
                    byte[] digest = ClientWire.ComputeFileHash(path, CancellationToken.None);
                    Assert.True(Utils.ConstantTimeEquals(digest, ClientWire.ComputeFileHash(path)),
                        "the overload computes the same digest as the plain one");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            runner.Run("ClientWire_BuildFolderManifest_CancellableMidPass", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_hmanc_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    // First file big enough that hashing it trips the 100ms progress throttle,
                    // so the cancel lands mid-pass and not before the loop starts
                    string big = Path.Combine(dir, "big.bin");
                    var content = new byte[192 * 1024 * 1024];
                    new Random(102).NextBytes(content);
                    File.WriteAllBytes(big, content);
                    File.WriteAllBytes(Path.Combine(dir, "small.bin"), new byte[128]);

                    var cts = new CancellationTokenSource();
                    var cb = new WireCallbacks();
                    cb.Progress = delegate { cts.Cancel(); };
                    try
                    {
                        ClientWire.BuildFolderManifest(dir, cb, cts.Token);
                        Assert.True(false, "expected the hash pass to be cancellable mid-way");
                    }
                    catch (OperationCanceledException) { }
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            runner.Run("ClientWire_BuildFolderManifest_HashProgress", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_hmprog_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    long total = 0;
                    var rnd = new Random(103);
                    for (int i = 0; i < 3; i++)
                    {
                        var content = new byte[96 * 1024 * 1024]; // slow enough to trip the throttle
                        rnd.NextBytes(content);
                        File.WriteAllBytes(Path.Combine(dir, "f" + i + ".bin"), content);
                        total += content.Length;
                    }

                    var events = new System.Collections.Generic.List<TransferProgress>();
                    var cb = new WireCallbacks();
                    cb.Progress = delegate(TransferProgress p)
                    {
                        lock (events) events.Add(p);
                    };
                    var manifest = ClientWire.BuildFolderManifest(dir, cb, CancellationToken.None);
                    Assert.True(manifest != null, "manifest built");
                    Assert.Equal(total, manifest.TotalBytes, "manifest total bytes");
                    lock (events)
                    {
                        Assert.True(events.Count > 0, "at least one hash progress event fired");
                        Assert.Equal(total, events[events.Count - 1].BytesTransferred, "last event reports the full pass");
                        Assert.Equal(total, events[events.Count - 1].TotalBytes, "event total matches the folder size");
                    }
                    bool computed;
                    byte[] h = manifest.Hashes[0];
                    Assert.True(Utils.ConstantTimeEquals(h, ClientWire.ComputeFileHash(manifest.Files[0])),
                        "manifest digest is the file's real SHA256");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });
        }

        // ==================== Unit: library verifier ====================

        private static void RunLibraryVerifier(TestRunner runner)
        {
            runner.Run("LibraryVerifier_Classification", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_lv_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string ok = Path.Combine(dir, "ok.bin");
                    string changed = Path.Combine(dir, "changed.bin");
                    string unknown = Path.Combine(dir, "unknown.bin");
                    string locked = Path.Combine(dir, "locked.bin");
                    File.WriteAllBytes(ok, new byte[4096]);
                    var original = new byte[8192];
                    new Random(111).NextBytes(original);
                    File.WriteAllBytes(changed, original);
                    File.WriteAllBytes(unknown, new byte[2048]);
                    File.WriteAllBytes(locked, new byte[4096]);

                    var cache = new FileHashCache(null);
                    cache.Store(ok, ClientWire.ComputeFileHash(ok));
                    cache.Store(changed, ClientWire.ComputeFileHash(changed));
                    cache.Store(locked, ClientWire.ComputeFileHash(locked));

                    // In-place corruption: same size, timestamp forced back to what the
                    // cache recorded — exactly the case a scrub exists to catch
                    var damaged = new byte[8192];
                    new Random(112).NextBytes(damaged);
                    long sz, mt;
                    FileHashCache.Stat(changed, out sz, out mt);
                    File.WriteAllBytes(changed, damaged);
                    File.SetLastWriteTimeUtc(changed, new DateTime(mt));

                    // Hold the lock while the scan runs: a FileShare.None read inside
                    // Verify fails, which must classify as Skipped, not Changed
                    VerifyReport report;
                    using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        report = LibraryVerifier.Verify(dir, cache, null, CancellationToken.None);
                    }

                    Assert.Equal(1, report.Verified.Count, "exactly the untouched file verifies");
                    Assert.True(report.Verified.Contains(ok), "verified file is the right one");
                    Assert.Equal(1, report.Changed.Count, "the in-place corruption is caught");
                    Assert.True(report.Changed.Contains(changed), "changed file is the right one");
                    Assert.Equal(1, report.Unverified.Count, "the never-received file has no record");
                    Assert.True(report.Unverified.Contains(unknown), "unverified file is the right one");
                    Assert.Equal(1, report.Skipped.Count, "the locked file is skipped, not judged");
                    Assert.True(report.Skipped.Contains(locked), "skipped file is the right one");
                    Assert.Equal(4, report.Total, "every file lands in exactly one bucket");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            runner.Run("LibraryVerifier_EmptyAndMissingDir", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_lv2_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    var empty = LibraryVerifier.Verify(dir, new FileHashCache(null), null, CancellationToken.None);
                    Assert.Equal(0, empty.Total, "empty directory scans to nothing");
                    var missing = LibraryVerifier.Verify(Path.Combine(dir, "nope"), new FileHashCache(null), null, CancellationToken.None);
                    Assert.Equal(0, missing.Total, "missing directory scans to nothing");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });
        }

        // ==================== Unit: CLI verbs ====================

        // ==================== Unit: pairing code length ====================

        private static void RunCli(TestRunner runner)
        {
            runner.Run("Cli_InvocationAndHelp", () =>
            {
                Assert.True(Cli.IsCliInvocation(new[] { "send" }), "send is a CLI verb");
                Assert.True(Cli.IsCliInvocation(new[] { "SYNC" }), "verbs are case-insensitive");
                Assert.True(Cli.IsCliInvocation(new[] { "verify" }), "verify is a CLI verb");
                Assert.False(Cli.IsCliInvocation(null), "no args is the GUI");
                Assert.False(Cli.IsCliInvocation(new[] { "whatever" }), "unknown verb is the GUI");
                Assert.Equal(0, Cli.Run(new[] { "--help" }), "help exits 0");
                Assert.Equal(2, Cli.Run(new[] { "sync" }), "sync without required args exits 2 (usage)");
            });

            runner.Run("WireAuth_GeneratePairingCode_Length", () =>
            {
                for (int len = 4; len <= 12; len += 2)
                {
                    for (int i = 0; i < 20; i++)
                    {
                        string code = WireAuth.GeneratePairingCode(len);
                        Assert.Equal(len, code.Length, "code honours the requested length");
                        bool allDigits = true;
                        foreach (char ch in code)
                            if (ch < '0' || ch > '9') allDigits = false;
                        Assert.True(allDigits, "code is digits only");
                    }
                }
                // Out-of-range requests clamp instead of producing garbage
                Assert.Equal(4, WireAuth.GeneratePairingCode(1).Length, "clamped low");
                Assert.Equal(12, WireAuth.GeneratePairingCode(50).Length, "clamped high");
                // Default overload keeps the historic 6-digit shape
                Assert.Equal(6, WireAuth.GeneratePairingCode().Length, "default stays 6");
            });
        }


        // ==================== Unit: server-side file digest cache ====================

        private static void RunFileHashCache(TestRunner runner)
        {
            runner.Run("FileHashCache_HitsMissesAndInvalidation", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_srvhash_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string path = Path.Combine(dir, "a.bin");
                    var content = new byte[32 * 1024];
                    new Random(21).NextBytes(content);
                    File.WriteAllBytes(path, content);
                    byte[] digest = ClientWire.ComputeFileHash(path);

                    var cache = new FileHashCache(null);
                    long size, mtime;
                    bool matches;
                    Assert.True(FileHashCache.Stat(path, out size, out mtime), "stat the file");
                    Assert.False(cache.TryMatch(path, size, mtime, digest, out matches), "cold cache misses");

                    cache.Store(path, digest);
                    Assert.True(cache.TryMatch(path, size, mtime, digest, out matches) && matches,
                        "remembered digest answers a match");
                    Assert.True(cache.TryMatch(path, size, mtime, new byte[32], out matches) && !matches,
                        "a remembered digest that differs is a verdict, not a miss");

                    // Rewritten content moves the key → the file must be hashed again
                    var other = new byte[48 * 1024];
                    new Random(22).NextBytes(other);
                    File.WriteAllBytes(path, other);
                    FileHashCache.Stat(path, out size, out mtime);
                    Assert.False(cache.TryMatch(path, size, mtime, digest, out matches), "changed file misses");

                    // Same size but a new timestamp is still a miss: content may differ
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
                    FileHashCache.Stat(path, out size, out mtime);
                    Assert.False(cache.TryMatch(path, size, mtime, digest, out matches), "touched file misses");

                    // A digest computed from a read the file has since outgrown is not
                    // remembered — a cache must never answer for bytes it did not see.
                    cache.StoreIfUnchanged(path, digest, size + 1, mtime);
                    Assert.False(cache.TryMatch(path, size, mtime, digest, out matches), "stale read is not remembered");
                    cache.StoreIfUnchanged(path, digest, size, mtime);
                    Assert.True(cache.TryMatch(path, size, mtime, digest, out matches) && matches, "unchanged read is remembered");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            runner.Run("FileHashCache_BoundedAcrossGenerations", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_srvbound_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    var cache = new FileHashCache(null);
                    cache.MaxEntriesPerGeneration = 2;

                    var paths = new string[5];
                    for (int i = 0; i < paths.Length; i++)
                    {
                        paths[i] = Path.Combine(dir, "f" + i + ".bin");
                        File.WriteAllBytes(paths[i], new byte[512 + i]);
                        cache.Store(paths[i], ClientWire.ComputeFileHash(paths[i]));
                    }

                    // Five stores with a generation of two: the oldest two were retired and
                    // are gone, the two before the newest are still reachable in the
                    // previous generation, and the total never exceeds two generations.
                    long size, mtime;
                    bool matches;
                    FileHashCache.Stat(paths[0], out size, out mtime);
                    Assert.False(cache.TryMatch(paths[0], size, mtime, new byte[32], out matches), "oldest entry retired");
                    FileHashCache.Stat(paths[2], out size, out mtime);
                    Assert.True(cache.TryMatch(paths[2], size, mtime, new byte[32], out matches), "entry from the previous generation still serves");
                    Assert.True(cache.Count <= 4, "entries stay within two generations, got " + cache.Count);
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });
        }

        // ==================== Unit: client-side digest reuse ====================

        private static void RunClientHashCache(TestRunner runner)
        {
            runner.Run("FileHashCache_GetOrComputeReusesUntilFileChanges", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_fhc_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "payload.bin");
                try
                {
                    var contentA = new byte[64 * 1024];
                    new Random(11).NextBytes(contentA);
                    File.WriteAllBytes(path, contentA);

                    int logCount = 0;
                    var cb = new WireCallbacks();
                    cb.Log = delegate(string m) { logCount++; };

                    bool computed;
                    var cache = new FileHashCache(null);
                    byte[] first = cache.GetOrCompute(path, cb, true, out computed);
                    Assert.True(computed, "first pass reads the file");
                    byte[] second = cache.GetOrCompute(path, cb, true, out computed);
                    Assert.True(!computed && ReferenceEquals(first, second),
                        "unchanged file: cached digest reused (a retry must not re-read the file)");
                    Assert.True(logCount == 1, "unchanged file: hash pass logged once, got " + logCount);
                    Assert.True(Utils.ConstantTimeEquals(first, ClientWire.ComputeFileHash(path)),
                        "cached digest is the file's real SHA256");

                    // A digest that no longer describes the file must never be reused
                    var contentB = new byte[96 * 1024];
                    new Random(12).NextBytes(contentB);
                    File.WriteAllBytes(path, contentB);
                    byte[] third = cache.GetOrCompute(path, cb, true, out computed);
                    Assert.True(computed, "changed file: digest recomputed");
                    Assert.True(logCount == 2, "changed file: hash pass logged again, got " + logCount);
                    Assert.True(Utils.ConstantTimeEquals(third, ClientWire.ComputeFileHash(path)),
                        "recomputed digest matches the new content");
                    Assert.True(!Utils.ConstantTimeEquals(first, third), "digests differ after the file changed");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            runner.Run("FileHashCache_SurvivesRestartAndExpires", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_fhcp_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                string journal = Path.Combine(dir, "journal.txt");
                string path = Path.Combine(dir, "payload.bin");
                try
                {
                    var content = new byte[16 * 1024];
                    new Random(13).NextBytes(content);
                    File.WriteAllBytes(path, content);
                    byte[] digest = ClientWire.ComputeFileHash(path);
                    string hex = BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();

                    long size, mtime;
                    bool matches;
                    FileHashCache.Stat(path, out size, out mtime);
                    new FileHashCache(journal).Store(path, digest);
                    Assert.True(File.Exists(journal), "journal written");

                    // A fresh instance (a restarted server/client) still knows the digest
                    var reopened = new FileHashCache(journal);
                    Assert.True(reopened.TryMatch(path, size, mtime, digest, out matches) && matches,
                        "digest survives a restart");
                    Assert.True(reopened.Count >= 1, "entry loaded from the journal, got " + reopened.Count);

                    // Byte-for-byte mode ignores the cache entirely
                    reopened.Strict = true;
                    Assert.False(reopened.TryMatch(path, size, mtime, digest, out matches), "strict mode ignores the cache");
                    reopened.Strict = false;

                    // Entry age drives the TTL: the same line is used when it is fresh and
                    // ignored once it is older than the limit (the periodic refresh).
                    string freshJournal = Path.Combine(dir, "fresh.txt");
                    string staleJournal = Path.Combine(dir, "stale.txt");
                    File.WriteAllText(freshJournal,
                        FileHashCache.FormatJournalLine(path, size, mtime, DateTime.UtcNow.Ticks, digest));
                    File.WriteAllText(staleJournal,
                        FileHashCache.FormatJournalLine(path, size, mtime, DateTime.UtcNow.AddDays(-30).Ticks, digest));
                    Assert.True(new FileHashCache(freshJournal).TryMatch(path, size, mtime, digest, out matches),
                        "entry within the TTL is used");
                    var expiring = new FileHashCache(staleJournal);
                    expiring.MaxAgeDays = 7;
                    Assert.False(expiring.TryMatch(path, size, mtime, digest, out matches),
                        "entry older than the TTL is ignored, so the file gets re-read");

                    // A journal bloated with superseded lines is rewritten on load, and the
                    // newest line per path is the one that survives
                    var bogus = new byte[32];
                    for (int i = 0; i < bogus.Length; i++) bogus[i] = 0xAB;
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < 5000; i++)
                        sb.Append(FileHashCache.FormatJournalLine(path, size, mtime, DateTime.UtcNow.Ticks, bogus));
                    sb.Append(FileHashCache.FormatJournalLine(path, size, mtime, DateTime.UtcNow.Ticks, digest));
                    string bloated = Path.Combine(dir, "bloated.txt");
                    File.WriteAllText(bloated, sb.ToString());
                    long before = new FileInfo(bloated).Length;
                    var compacted = new FileHashCache(bloated);
                    Assert.True(new FileInfo(bloated).Length * 10 < before,
                        "bloated journal rewritten on load (was " + before + " bytes)");
                    Assert.True(compacted.TryMatch(path, size, mtime, digest, out matches) && matches,
                        "the newest line per path survives compaction");
                    Assert.True(compacted.TryMatch(path, size, mtime, bogus, out matches) && !matches,
                        "superseded lines are gone: the remembered digest is the newest one");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            // Everything the journal can throw at the loader: a torn line, a tampered
            // digest, junk — must cost a re-hash at worst, and must never produce a digest
            // that answers for content the pass never saw.
            runner.Run("FileHashCache_RejectsCorruptJournalLines", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_fhcc_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string path = Path.Combine(dir, "a.bin");
                    var content = new byte[8 * 1024];
                    new Random(14).NextBytes(content);
                    File.WriteAllBytes(path, content);
                    byte[] digest = ClientWire.ComputeFileHash(path);
                    long size, mtime;
                    FileHashCache.Stat(path, out size, out mtime);

                    string good = FileHashCache.FormatJournalLine(path, size, mtime, DateTime.UtcNow.Ticks, digest);
                    var bogus = new byte[32];
                    for (int i = 0; i < bogus.Length; i++) bogus[i] = 0x5A;
                    string tampered = FileHashCache.FormatJournalLine(path, size, mtime, DateTime.UtcNow.Ticks, bogus);

                    // Journal with nothing but damaged lines: a torn tail, junk, the
                    // pre-checksum format, and a line whose checksum no longer matches its
                    // body (what a stray byte or a partial overwrite by a second instance
                    // looks like). None may load — the caller must re-hash instead.
                    string damaged = Path.Combine(dir, "damaged.txt");
                    var bad = new System.Text.StringBuilder();
                    bad.Append(tampered.TrimEnd('\r', '\n').Substring(0, tampered.TrimEnd('\r', '\n').Length - 8)).Append("\r\n");
                    bad.Append(good.Substring(0, good.Length / 2));                        // torn (crash mid-append)
                    bad.Append("not even a line\r\n");                                     // junk
                    bad.Append(path).Append("|1|2|3|").Append(new string('0', 64)).Append("\r\n"); // no checksum field
                    File.WriteAllText(damaged, bad.ToString());

                    long fileSize, fileMtime;
                    bool matches;
                    Assert.True(FileHashCache.Stat(path, out fileSize, out fileMtime), "stat the file");
                    var rejected = new FileHashCache(damaged);
                    Assert.True(rejected.Count == 0, "no damaged line loads, got " + rejected.Count);
                    Assert.False(rejected.TryMatch(path, fileSize, fileMtime, bogus, out matches),
                        "and none of them can answer a lookup");

                    // The same journal plus one intact line: that line loads, and asking about
                    // other content gets a definitive mismatch rather than a miss.
                    string mixed = Path.Combine(dir, "mixed.txt");
                    File.WriteAllText(mixed, bad.ToString() + good);
                    var cache = new FileHashCache(mixed);
                    Assert.True(cache.TryMatch(path, fileSize, fileMtime, digest, out matches) && matches,
                        "the intact line in the same journal still loads");
                    Assert.True(cache.TryMatch(path, fileSize, fileMtime, bogus, out matches) && !matches,
                        "the loaded digest is the intact one, so other content is a mismatch");

                    // Garbage, an empty file and a directory-as-journal must not throw
                    string emptyFile = Path.Combine(dir, "empty.txt");
                    File.WriteAllText(emptyFile, "");
                    Assert.True(new FileHashCache(emptyFile).Count == 0, "empty journal loads as empty");
                    string garbage = Path.Combine(dir, "garbage.txt");
                    File.WriteAllBytes(garbage, new byte[] { 0x00, 0xFF, 0x10, 0x0A, 0x0D });
                    Assert.True(new FileHashCache(garbage).Count == 0, "binary junk loads as empty");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            runner.Run("FileHashCache_TwoInstancesAppendToOneJournal", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_fhcm_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string journal = Path.Combine(dir, "shared.txt");
                    var a = new FileHashCache(journal);
                    var b = new FileHashCache(journal);   // second app instance, same machine

                    string pathA = Path.Combine(dir, "a.bin");
                    string pathB = Path.Combine(dir, "b.bin");
                    File.WriteAllBytes(pathA, new byte[1024]);
                    File.WriteAllBytes(pathB, new byte[2048]);
                    byte[] digestA = ClientWire.ComputeFileHash(pathA);
                    byte[] digestB = ClientWire.ComputeFileHash(pathB);

                    // Interleaved writes: neither instance saw the other's entry at load time
                    a.Store(pathA, digestA);
                    b.Store(pathB, digestB);
                    a.Store(pathA, digestA);

                    long size, mtime;
                    bool matches;
                    var reopened = new FileHashCache(journal);
                    FileHashCache.Stat(pathA, out size, out mtime);
                    Assert.True(reopened.TryMatch(pathA, size, mtime, digestA, out matches) && matches,
                        "first instance's entry survived");
                    FileHashCache.Stat(pathB, out size, out mtime);
                    Assert.True(reopened.TryMatch(pathB, size, mtime, digestB, out matches) && matches,
                        "second instance's entry survived");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            runner.Run("FileHashCache_UnwritableJournalStillServes", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_fhcu_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string path = Path.Combine(dir, "a.bin");
                    var content = new byte[4096];
                    new Random(15).NextBytes(content);
                    File.WriteAllBytes(path, content);
                    byte[] digest = ClientWire.ComputeFileHash(path);

                    // A directory where the journal should be: every append fails, and the
                    // cache must still answer from memory for this run.
                    string blocked = Path.Combine(dir, "blocked.txt");
                    Directory.CreateDirectory(blocked);
                    var cache = new FileHashCache(blocked);
                    cache.Store(path, digest);

                    long size, mtime;
                    bool matches;
                    FileHashCache.Stat(path, out size, out mtime);
                    Assert.True(cache.TryMatch(path, size, mtime, digest, out matches) && matches,
                        "an unwritable journal degrades to an in-memory cache");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            runner.Run("FileHashCache_TrimsAndForgets", () =>            {
                string dir = Path.Combine(TempBase(), "tr_fhct_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    // A journal with more entries than the cache is willing to hold: the trim
                    // keeps the newest ones and brings the total back within its bound.
                    string journal = Path.Combine(dir, "big.txt");
                    var sb = new System.Text.StringBuilder();
                    var digest = new byte[32];
                    for (int i = 0; i < 12; i++)
                    {
                        // Fresh timestamps (the TTL would otherwise reject them all) but
                        // ordered, so "newest" is unambiguous
                        sb.Append(FileHashCache.FormatJournalLine(
                            Path.Combine(dir, "f" + i), 10 + i, 100 + i, DateTime.UtcNow.Ticks + i, digest));
                    }
                    File.WriteAllText(journal, sb.ToString());
                    var cache = new FileHashCache(journal);
                    Assert.True(cache.Count == 12, "all twelve entries loaded, got " + cache.Count);
                    cache.MaxEntriesPerGeneration = 2;
                    cache.TrimToCapacity();
                    bool matches;
                    Assert.True(cache.Count <= 4, "trim stays within two generations, got " + cache.Count);
                    Assert.True(cache.TryMatch(Path.Combine(dir, "f11"), 21, 111, digest, out matches) && matches,
                        "the newest entry is kept");
                    Assert.False(cache.TryMatch(Path.Combine(dir, "f0"), 10, 100, digest, out matches),
                        "the oldest entry is dropped");

                    // Remove drops an entry so the next lookup re-reads (self-healing hook)
                    string path = Path.Combine(dir, "a.bin");
                    File.WriteAllBytes(path, new byte[512]);
                    byte[] real = ClientWire.ComputeFileHash(path);
                    var single = new FileHashCache(null);
                    single.Store(path, real);
                    long size, mtime;
                    FileHashCache.Stat(path, out size, out mtime);
                    Assert.True(single.TryMatch(path, size, mtime, real, out matches), "stored entry is usable");
                    single.Remove(path);
                    Assert.False(single.TryMatch(path, size, mtime, real, out matches), "removed entry must be re-read");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            // A peer that is hashing what it already holds is busy, not stalled: the wait
            // for its status response must not be held to the transfer's own socket timeout.
            runner.Run("ClientWire_NegotiationWaitGetsGenerousTimeout", () =>
            {
                var manifest = new FolderSendManifest
                {
                    FolderName = "f",
                    FolderPath = "x",
                    Files = new string[] { "x\\a.bin" },
                    RelativePaths = new string[] { "a.bin" },
                    Sizes = new long[] { 1 },
                    Hashes = new byte[][] { new byte[32] },
                    TotalBytes = 1
                };
                var resp = new byte[18];
                resp[0] = 0x11;                 // folder resume response, status 2 = complete
                resp[17] = 2;
                Guid sessionId = Guid.NewGuid();
                var wire = new ScriptedWire(resp);
                try
                {
                    ClientWire.SendFolderResumableAsync(wire, manifest, sessionId, "127.0.0.1", 1, false,
                        4096, null, new WireCallbacks(), CancellationToken.None).Wait(10000);
                }
                finally
                {
                    FolderResumeState.Delete(sessionId);
                }
                Assert.True(wire.ReadTimeouts.Count == 2,
                    "the wait is widened and then restored, got " + wire.ReadTimeouts.Count);
                Assert.True(wire.ReadTimeouts[0] == ClientWire.LongReadWindowMs,
                    "widened while waiting for the peer's verdict, got " + wire.ReadTimeouts[0]);
                Assert.True(wire.ReadTimeouts[1] == 30000,
                    "restored before the payload, got " + wire.ReadTimeouts[1]);
            });

            // The widen must not survive a failed read: whatever the wait throws, the next
            // read on this stream sees the normal window again.
            runner.Run("ClientWire_NegotiationTimeoutRestoredOnFailure", () =>
            {
                var manifest = new FolderSendManifest
                {
                    FolderName = "f",
                    FolderPath = "x",
                    Files = new string[] { "x\\a.bin" },
                    RelativePaths = new string[] { "a.bin" },
                    Sizes = new long[] { 1 },
                    Hashes = new byte[][] { new byte[32] },
                    TotalBytes = 1
                };
                Guid sessionId = Guid.NewGuid();
                var wire = new ScriptedWire(new byte[0]); // response read hits EOF and throws
                bool threw = false;
                try
                {
                    ClientWire.SendFolderResumableAsync(wire, manifest, sessionId, "127.0.0.1", 1, false,
                        4096, null, new WireCallbacks(), CancellationToken.None).Wait(10000);
                }
                catch
                {
                    threw = true;
                }
                finally
                {
                    FolderResumeState.Delete(sessionId);
                }
                Assert.True(threw, "a dead peer surfaces as an error");
                Assert.True(wire.ReadTimeouts.Count == 2,
                    "the window was restored despite the failed read, got " + wire.ReadTimeouts.Count);
                Assert.True(wire.ReadTimeouts[0] == ClientWire.LongReadWindowMs && wire.ReadTimeouts[1] == 30000,
                    "widen then restore, got " + wire.ReadTimeouts[0] + "/" + wire.ReadTimeouts[1]);
            });

            // The raw socket is the thing that owns a timeout, so both decorators must pass
            // the request through instead of swallowing it.
            runner.Run("WireStream_TimeoutForwardedThroughDecorators", () =>
            {
                var inner = new ScriptedWire(new byte[0]);
                byte[] k1 = MakeSecret(91, 32), k2 = MakeSecret(92, 32);
                byte[] k3 = MakeSecret(93, 32), k4 = MakeSecret(94, 32);
                var compressed = new CompressedWireStream(inner, false);
                var encrypted = new EncryptedWireStream(compressed, k1, k2, k3, k4, false);
                try
                {
                    encrypted.SetReadTimeoutMs(123456);
                }
                finally
                {
                    encrypted.Dispose();   // ownsInner=false: the fake stays open
                    compressed.Dispose();
                }
                Assert.True(inner.ReadTimeouts.Count == 1 && inner.ReadTimeouts[0] == 123456,
                    "the request reached the innermost stream, got " + inner.ReadTimeouts.Count + " entries");
            });

            // What the key is made of, and which mismatches must be refused even when only
            // one component moved.
            runner.Run("FileHashCache_KeyComposition", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_fhck_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string path = Path.Combine(dir, "a.bin");
                    var content = new byte[16 * 1024];
                    new Random(16).NextBytes(content);
                    File.WriteAllBytes(path, content);
                    byte[] digest = ClientWire.ComputeFileHash(path);
                    long size, mtime;
                    FileHashCache.Stat(path, out size, out mtime);
                    bool matches;
                    long stored = DateTime.UtcNow.Ticks;

                    // Size is part of the key on its own: a line that claims a different size
                    // for the same path and timestamp must not answer.
                    string wrongSize = Path.Combine(dir, "size.txt");
                    File.WriteAllText(wrongSize, FileHashCache.FormatJournalLine(path, size + 1, mtime, stored, digest));
                    Assert.False(new FileHashCache(wrongSize).TryMatch(path, size, mtime, digest, out matches),
                        "a line with the wrong size is not usable");

                    // Timestamp likewise
                    string wrongTime = Path.Combine(dir, "time.txt");
                    File.WriteAllText(wrongTime, FileHashCache.FormatJournalLine(path, size, mtime + 1, stored, digest));
                    Assert.False(new FileHashCache(wrongTime).TryMatch(path, size, mtime, digest, out matches),
                        "a line with the wrong timestamp is not usable");

                    // Path spelling: Windows is case-insensitive, so a differently-cased
                    // spelling of the same file finds the entry ...
                    var cache = new FileHashCache(null);
                    cache.Store(path, digest);
                    Assert.True(cache.TryMatch(path.ToUpperInvariant(), size, mtime, digest, out matches) && matches,
                        "path keys are case-insensitive");
                    // ... but a path the caller never recorded (a different spelling that is
                    // not just case) is a miss, never a wrong answer.
                    Assert.False(cache.TryMatch(Path.Combine(dir, "sub", "a.bin"), size, mtime, digest, out matches),
                        "an unrecorded path is a miss");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            runner.Run("FileHashCache_TtlZeroNeverExpiresAndRefreshesOnUse", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_fhcl_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string path = Path.Combine(dir, "a.bin");
                    var content = new byte[8192];
                    new Random(17).NextBytes(content);
                    File.WriteAllBytes(path, content);
                    byte[] digest = ClientWire.ComputeFileHash(path);
                    long size, mtime;
                    FileHashCache.Stat(path, out size, out mtime);
                    bool matches;

                    // MaxAgeDays = 0 means "trust forever": even an ancient entry is used.
                    string journal = Path.Combine(dir, "ancient.txt");
                    File.WriteAllText(journal, FileHashCache.FormatJournalLine(
                        path, size, mtime, DateTime.UtcNow.AddYears(-3).Ticks, digest));
                    var forever = new FileHashCache(journal);
                    forever.MaxAgeDays = 0;
                    Assert.True(forever.TryMatch(path, size, mtime, digest, out matches) && matches,
                        "TTL 0 keeps serving an old entry");

                    // With a TTL the same entry is ignored, and a fresh verification (what the
                    // caller does on a miss) makes it usable again — the refresh cycle.
                    var expiring = new FileHashCache(journal);
                    expiring.MaxAgeDays = 7;
                    Assert.False(expiring.TryMatch(path, size, mtime, digest, out matches),
                        "the same entry is ignored once a TTL applies");
                    expiring.StoreIfUnchanged(path, digest, size, mtime);
                    Assert.True(expiring.TryMatch(path, size, mtime, digest, out matches) && matches,
                        "re-verifying the file refreshes the entry");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            // Strict mode reads the file, but it still records what it learned so the cache
            // is warm the moment the switch goes back off.
            runner.Run("FileHashCache_StrictStillComputesAndStores", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_fhcs_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string path = Path.Combine(dir, "a.bin");
                    var content = new byte[4096];
                    new Random(18).NextBytes(content);
                    File.WriteAllBytes(path, content);

                    var cache = new FileHashCache(null);
                    cache.Strict = true;
                    bool computed;
                    byte[] first = cache.GetOrCompute(path, new WireCallbacks(), false, out computed);
                    Assert.True(computed, "strict mode always reads the file");
                    Assert.True(Utils.ConstantTimeEquals(first, ClientWire.ComputeFileHash(path)), "and returns the real digest");

                    long size, mtime;
                    bool matches;
                    FileHashCache.Stat(path, out size, out mtime);
                    Assert.False(cache.TryMatch(path, size, mtime, first, out matches), "still refuses to answer");

                    cache.Strict = false;
                    Assert.True(cache.TryMatch(path, size, mtime, first, out matches) && matches,
                        "what strict mode computed was recorded");
                    byte[] second = cache.GetOrCompute(path, new WireCallbacks(), false, out computed);
                    Assert.True(!computed, "and is reused once the switch is off");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            // Frequent lookups keep an entry alive through generation rotation; a rewrite
            // keeps what both generations hold.
            runner.Run("FileHashCache_PromotesHotEntriesAndKeepsBothGenerationsOnRewrite", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_fhcr_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    var cache = new FileHashCache(null);
                    cache.MaxEntriesPerGeneration = 1;
                    var digest = new byte[32];
                    string hot = Path.Combine(dir, "hot.bin");
                    string cold = Path.Combine(dir, "cold.bin");
                    string fresh = Path.Combine(dir, "fresh.bin");
                    File.WriteAllBytes(hot, new byte[1024]);
                    File.WriteAllBytes(cold, new byte[1024]);
                    File.WriteAllBytes(fresh, new byte[1024]);
                    long size, mtime;
                    bool matches;

                    cache.Store(hot, digest);
                    cache.Store(cold, digest);          // rotates: hot is only in the old generation
                    FileHashCache.Stat(hot, out size, out mtime);
                    Assert.True(cache.TryMatch(hot, size, mtime, digest, out matches), "hit in the retired generation");
                    cache.Store(fresh, digest);         // another rotation
                    Assert.True(cache.TryMatch(hot, size, mtime, digest, out matches),
                        "the entry that was just used survived the rotation");

                    // A rewrite keeps both generations, not just the live one
                    string journal = Path.Combine(dir, "small.txt");
                    var persisted = new FileHashCache(journal);
                    persisted.MaxEntriesPerGeneration = 1;
                    persisted.MaxJournalBytes = 1;      // force the journal to be rewritten
                    persisted.Store(hot, digest);
                    persisted.Store(cold, digest);
                    var reopened = new FileHashCache(journal);
                    // Each file's own (size, mtime): two files written back to back may or
                    // may not share a timestamp, and the key is per file.
                    long hotSize, hotMtime, coldSize, coldMtime;
                    FileHashCache.Stat(hot, out hotSize, out hotMtime);
                    FileHashCache.Stat(cold, out coldSize, out coldMtime);
                    Assert.True(reopened.TryMatch(hot, hotSize, hotMtime, digest, out matches),
                        "the rewrite kept the retired generation too");
                    Assert.True(reopened.TryMatch(cold, coldSize, coldMtime, digest, out matches),
                        "and the live one");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            // Non-ASCII names must survive the UTF-8 journal round-trip byte for byte.
            runner.Run("FileHashCache_UnicodePathRoundTrip", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_fhcn_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string path = Path.Combine(dir, "中文文件名 空格.bin");
                    var content = new byte[2048];
                    new Random(19).NextBytes(content);
                    File.WriteAllBytes(path, content);
                    byte[] digest = ClientWire.ComputeFileHash(path);

                    string journal = Path.Combine(dir, "unicode.txt");
                    new FileHashCache(journal).Store(path, digest);

                    long size, mtime;
                    bool matches;
                    FileHashCache.Stat(path, out size, out mtime);
                    Assert.True(new FileHashCache(journal).TryMatch(path, size, mtime, digest, out matches) && matches,
                        "a non-ASCII path round-trips through the journal");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            // The documented trust boundary: metadata that lies. Same size and a restored
            // mtime hide a content change from the cache — that is its designed trust level —
            // while byte-for-byte mode still catches it.
            runner.Run("FileHashCache_AdversarialMetadataAnsweredByStrict", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_fhca_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string path = Path.Combine(dir, "a.bin");
                    var first = new byte[16 * 1024];
                    new Random(21).NextBytes(first);
                    File.WriteAllBytes(path, first);
                    long size, mtime;
                    FileHashCache.Stat(path, out size, out mtime);
                    byte[] digest = ClientWire.ComputeFileHash(path);

                    var cache = new FileHashCache(null);
                    cache.Store(path, digest);

                    // Same size, different content, mtime forced back to the recorded value
                    var second = new byte[16 * 1024];
                    new Random(22).NextBytes(second);
                    File.WriteAllBytes(path, second);
                    File.SetLastWriteTimeUtc(path, new DateTime(mtime));

                    bool matches;
                    Assert.True(cache.TryMatch(path, size, mtime, digest, out matches) && matches,
                        "the cache answers from metadata — this is its documented trust level");
                    cache.Strict = true;
                    Assert.False(cache.TryMatch(path, size, mtime, digest, out matches),
                        "byte-for-byte mode ignores the lie and re-reads");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            // Parallel sends and lookups (multiple app instances, overlapping syncs): the
            // dictionary, the generation rotation and the journal append all under contention.
            runner.Run("FileHashCache_ConcurrentStress", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_fhcx_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    var paths = new string[32];
                    var digests = new byte[32][];
                    for (int i = 0; i < paths.Length; i++)
                    {
                        paths[i] = Path.Combine(dir, "f" + i + ".bin");
                        File.WriteAllBytes(paths[i], new byte[256 + i]);
                        digests[i] = ClientWire.ComputeFileHash(paths[i]);
                    }

                    var cache = new FileHashCache(Path.Combine(dir, "stress.txt"));
                    cache.MaxEntriesPerGeneration = 8; // force rotations during the storm

                    var errors = new System.Collections.Generic.List<string>();
                    var threads = new Thread[8];
                    for (int t = 0; t < threads.Length; t++)
                    {
                        int seed = t;
                        threads[t] = new Thread(delegate()
                        {
                            try
                            {
                                var rnd = new Random(seed);
                                for (int i = 0; i < 300; i++)
                                {
                                    int k = rnd.Next(paths.Length);
                                    long size, mtime;
                                    FileHashCache.Stat(paths[k], out size, out mtime);
                                    switch (rnd.Next(3))
                                    {
                                        case 0:
                                            cache.Store(paths[k], digests[k]);
                                            break;
                                        case 1:
                                            bool hit;
                                            cache.TryMatch(paths[k], size, mtime, digests[k], out hit);
                                            break;
                                        default:
                                            if (rnd.Next(20) == 0) cache.Remove(paths[k]);
                                            break;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                lock (errors) errors.Add(ex.Message);
                            }
                        });
                        threads[t].Start();
                    }
                    for (int t = 0; t < threads.Length; t++) threads[t].Join();
                    Assert.True(errors.Count == 0, "no thread saw an exception: " + string.Join(" | ", errors.ToArray()));
                    Assert.True(cache.Count <= 16, "entries stayed within two generations, got " + cache.Count);

                    // Whatever survived must reload and must never answer with a wrong digest.
                    // Reload materializes the previous generation into memory too (its entries
                    // are still valid), so the bound here is the path universe, not two
                    // generations — the runtime bound was already asserted above.
                    var reopened = new FileHashCache(Path.Combine(dir, "stress.txt"));
                    Assert.True(reopened.Count <= paths.Length,
                        "the journal holds at most one entry per path, got " + reopened.Count);
                    long s2, m2;
                    var zero = new byte[32];
                    bool matches;
                    for (int i = 0; i < paths.Length; i++)
                    {
                        FileHashCache.Stat(paths[i], out s2, out m2);
                        Assert.True(!reopened.TryMatch(paths[i], s2, m2, zero, out matches) || !matches,
                            "a stored digest never equals the all-zero digest");
                    }
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            // Compaction rewrites the journal with every live entry, not a sample of it.
            runner.Run("FileHashCache_CompactionKeepsAllLiveEntries", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_fhccomp_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string journal = Path.Combine(dir, "many.txt");
                    var sb = new System.Text.StringBuilder();
                    var digest = new byte[32];
                    int total = 300;
                    for (int i = 0; i < total; i++)
                        sb.Append(FileHashCache.FormatJournalLine(
                            Path.Combine(dir, "f" + i), i, 1000 + i, DateTime.UtcNow.Ticks + i, digest));
                    File.WriteAllText(journal, sb.ToString());

                    var cache = new FileHashCache(journal);
                    Assert.True(cache.Count == total, "all entries loaded, got " + cache.Count);
                    cache.MaxJournalBytes = 1024;   // force the rewrite on the next append
                    File.WriteAllBytes(Path.Combine(dir, "extra"), new byte[16]);
                    cache.Store(Path.Combine(dir, "extra"), digest);

                    var reopened = new FileHashCache(journal);
                    Assert.True(reopened.Count >= total,
                        "compaction lost no live entries (had " + total + ", reloaded " + reopened.Count + ")");
                    bool matches;
                    Assert.True(reopened.TryMatch(Path.Combine(dir, "f0"), 0, 1000, digest, out matches),
                        "oldest live entry survived the rewrite");
                    Assert.True(reopened.TryMatch(Path.Combine(dir, "f" + (total - 1)), total - 1, 999 + total, digest, out matches),
                        "newest live entry survived the rewrite");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            });

            // The receive-options plumbing end to end: Config keys flip the cache policy.
            runner.Run("HashCacheConfig_AppliedThroughApplyConfig", () =>
            {
                string prevVerify = Config.Get("SyncVerifyContent", "");
                string prevDays = Config.Get("HashCacheDays", "");
                try
                {
                    Config.SetBool("SyncVerifyContent", true);
                    Config.SetInt("HashCacheDays", 3);
                    var cache = new FileHashCache(null);
                    cache.ApplyConfig();
                    Assert.True(cache.Strict, "byte-for-byte mode reaches the cache");
                    Assert.True(cache.MaxAgeDays == 3, "the TTL reaches the cache, got " + cache.MaxAgeDays);

                    Config.SetBool("SyncVerifyContent", false);
                    Config.SetInt("HashCacheDays", 0);
                    cache.ApplyConfig();
                    Assert.False(cache.Strict, "turning the switch off clears strict");
                    Assert.True(cache.MaxAgeDays == 0, "TTL 0 = never expire");
                }
                finally
                {
                    Config.Set("SyncVerifyContent", prevVerify);
                    Config.Set("HashCacheDays", prevDays);
                    Config.Save();
                }
            });
        }

        // ==================== Unit: chunk reassembly coverage ====================

        private static void RunChunkTracker(TestRunner runner)
        {
            runner.Run("ChunkTracker_OverlappingChunks_NotComplete", () =>
            {
                // Duplicate/overlapping chunks inflate a naive byte SUM past TotalSize;
                // completion must instead require real coverage of [0, TotalSize).
                string dir = Path.Combine(TempBase(), "tr_ck_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    var t = new ChunkTracker { FileName = "f.bin", TotalSize = 100, SavePath = Path.Combine(dir, "f.bin") };
                    var data = new byte[50];
                    Assert.False(t.WriteChunk(0, data, 50), "first 50 bytes: not complete");
                    // Same range again — sum would now be 100 but bytes 50..99 are missing
                    Assert.False(t.WriteChunk(0, data, 50), "duplicate range must not complete");
                    Assert.True(t.WriteChunk(50, data, 50), "covering the tail completes it");
                    t.Dispose();
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });

            runner.Run("ChunkTracker_OutOfOrder_CoverageCompletes", () =>
            {
                string dir = Path.Combine(TempBase(), "tr_ck2_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    var t = new ChunkTracker { FileName = "g.bin", TotalSize = 90, SavePath = Path.Combine(dir, "g.bin") };
                    var data = new byte[30];
                    Assert.False(t.WriteChunk(60, data, 30), "tail first");
                    Assert.False(t.WriteChunk(0, data, 30), "head second");
                    Assert.True(t.WriteChunk(30, data, 30), "middle closes the gap");
                    t.Dispose();
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });
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
            runner.Run("Feature_TCP_RecvSpeedLimit", TcpRecvSpeedLimit);
            runner.Run("Feature_TCP_PauseResume", TcpPauseResume);
            runner.Run("Feature_TCP_Compressed", TcpCompressed);
            runner.Run("Feature_TCP_CompressEncrypted", TcpCompressEncrypted);
            runner.Run("Feature_TCP_CompressResume", TcpCompressResume);
            runner.Run("Feature_TCP_CompressFallbackOldPeer", TcpCompressFallbackOldPeer);
            runner.Run("Feature_UDT_Compressed", UdtCompressed, 1);
            runner.Run("Feature_TCP_ResumeOffsetGap_Rejected", TcpResumeOffsetGapRejected);
            runner.Run("Feature_TCP_AuthLockout", TcpAuthLockout);
            runner.Run("Feature_TCP_CompletionAck_SurvivesCompression", TcpCompletionAckCompressed);
        }

        /// <summary>A client that declares a resume offset above what the server has
        /// received must NOT be believed: the server resumes from its own byte count,
        /// so the skipped region is actually transferred (and a zero-filled "complete"
        /// file cannot be produced).</summary>
        private static void TcpResumeOffsetGapRejected()
        {
            using (var fx = new TcpServerFixture())
            {
                string testFile = Path.Combine(fx.SendDir, "gap.bin");
                var rng = new Random(7);
                var content = new byte[200 * 1024];
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);
                fx.Start();

                var sessionId = Guid.NewGuid();
                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                var done = new ManualResetEvent(false);
                bool ok = false;
                client.OnTransferComplete += () => { ok = true; done.Set(); };
                client.OnError += _ => done.Set();
                // The client declares its own offset from its saved state; seed the
                // state with a bogus 100 KB so it claims to be ahead of the server.
                var seed = new ResumeState
                {
                    SessionId = sessionId,
                    TotalSize = content.Length,
                    FileName = Path.GetFileName(testFile),
                    FilePath = testFile,
                    ServerIp = "127.0.0.1",
                    Port = fx.Port,
                    IsUdt = false,
                    Created = DateTime.UtcNow,
                    SentBytes = 100 * 1024
                };
                seed.Save();
                client.SendResumableAsync(sessionId).Wait(30000);
                Assert.True(done.WaitOne(30000), "client finished");

                WaitForFileCount(fx, 1);
                string[] files = RecvFiles(fx);
                Assert.Equal(1, files.Length, "one file received");
                byte[] saved = File.ReadAllBytes(files[0]);
                Assert.Equal(content.Length, saved.Length, "full size");
                // If the gap were skipped, the head would be zeros instead of content.
                Assert.True(Utils.ConstantTimeEquals(content, saved), "no zero-filled gap");
                Assert.True(ok, "transfer completed");
            }
        }

        /// <summary>Repeated wrong pairing codes lock the peer out; even a correct code
        /// is then refused until the server restarts (or the peer had succeeded first).</summary>
        private static void TcpAuthLockout()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.PairingCode = "424242";
                string testFile = Path.Combine(fx.SendDir, "lock.bin");
                MakeTestFile(testFile, 4096);
                fx.Start();

                // Exhaust the allowance with wrong codes.
                for (int i = 0; i < 12; i++)
                {
                    var bad = new TransferClient("127.0.0.1", fx.Port, testFile);
                    bad.PairingCode = "000000";
                    var d = new ManualResetEvent(false);
                    bad.OnTransferComplete += () => d.Set();
                    bad.OnError += _ => d.Set();
                    bad.SendAsync();
                    d.WaitOne(10000);
                }

                // Now a correct code must still be refused (locked out).
                var good = new TransferClient("127.0.0.1", fx.Port, testFile);
                good.PairingCode = "424242";
                var goodDone = new ManualResetEvent(false);
                bool goodOk = false;
                good.OnTransferComplete += () => { goodOk = true; goodDone.Set(); };
                good.OnError += _ => goodDone.Set();
                good.SendAsync();
                goodDone.WaitOne(15000);

                Assert.False(goodOk, "correct code refused while locked out");
                Assert.Equal(0, RecvFiles(fx).Length, "nothing saved under lockout");
            }
        }

        /// <summary>With compression accepted (so the client knows the peer is a current
        /// build), a refused transfer must surface as a client-side failure rather than
        /// silent success — this is the TCP completion-ack path through the decorator.</summary>
        private static void TcpCompletionAckCompressed()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.ConfirmRequest = (ip, name, size, files, isFolder) => Task.FromResult(false);
                string testFile = Path.Combine(fx.SendDir, "denied_c.bin");
                MakeTestFile(testFile, 64 * 1024);
                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                client.CompressionEnabled = true;
                var done = new ManualResetEvent(false);
                bool failed = false;
                client.OnTransferComplete += () => done.Set();
                client.OnError += _ => { failed = true; done.Set(); };
                client.SendAsync();
                Assert.True(done.WaitOne(20000), "client finished");
                Assert.True(failed, "refused transfer reported as failure on the client");
                Assert.Equal(0, RecvFiles(fx).Length, "denied file not saved");
            }
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

            public void SetReadTimeoutMs(int milliseconds) { }

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

            runner.Run("WireCrypto_EcdhSharedSecret_Agrees", () =>
            {
                using (var a = SessionCrypto.CreateEcdhKey())
                using (var b = SessionCrypto.CreateEcdhKey())
                {
                    var apub = SessionCrypto.EcdhPublicBlob(a);
                    var bpub = SessionCrypto.EcdhPublicBlob(b);
                    Assert.Equal(SessionCrypto.EcdhPublicBytes, apub.Length, "P-256 blob size");
                    var sab = SessionCrypto.EcdhDeriveShared(a, bpub);
                    var sba = SessionCrypto.EcdhDeriveShared(b, apub);
                    Assert.True(Utils.ConstantTimeEquals(sab, sba), "both sides derive the same secret");
                }
            });

            runner.Run("WireCrypto_EcdhSharedSecret_RejectsBadBlob", () =>
            {
                using (var a = SessionCrypto.CreateEcdhKey())
                {
                    Assert.Throws(() => SessionCrypto.EcdhDeriveShared(a, new byte[SessionCrypto.EcdhPublicBytes]),
                        "all-zero blob rejected");
                    Assert.Throws(() => SessionCrypto.EcdhDeriveShared(a, new byte[10]),
                        "short blob rejected");
                }
            });

            runner.Run("WireCrypto_AuthKey_BindsCodeAndTranscript", () =>
            {
                using (var a = SessionCrypto.CreateEcdhKey())
                using (var b = SessionCrypto.CreateEcdhKey())
                {
                    var apub = SessionCrypto.EcdhPublicBlob(a);
                    var bpub = SessionCrypto.EcdhPublicBlob(b);
                    var shared = SessionCrypto.EcdhDeriveShared(a, bpub);
                    var k1 = SessionCrypto.DeriveAuthKey("123456", apub, bpub, shared);
                    var k2 = SessionCrypto.DeriveAuthKey("123456", apub, bpub, shared);
                    var k3 = SessionCrypto.DeriveAuthKey("654321", apub, bpub, shared);
                    Assert.True(Utils.ConstantTimeEquals(k1, k2), "auth key deterministic");
                    Assert.False(Utils.ConstantTimeEquals(k1, k3), "wrong code changes the auth key");
                    // Confirms are label- and transcript-bound
                    var c1 = SessionCrypto.Confirm(k1, "c2s-confirm", apub, bpub);
                    var c2 = SessionCrypto.Confirm(k1, "s2c-confirm", apub, bpub);
                    Assert.False(Utils.ConstantTimeEquals(c1, c2), "direction labels are domain-separated");
                    Assert.True(Utils.ConstantTimeEquals(c1, SessionCrypto.Confirm(k1, "c2s-confirm", apub, bpub)),
                        "confirm deterministic");
                }
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

            runner.Run("WireCrypto_BoundSequence_ReorderedSegmentsDetected", () =>
            {
                // With the sequence-bound format (0x09), reordering two equal-length
                // records on the wire must fail authentication instead of decrypting.
                byte[] c2sEnc, c2sMac, s2cEnc, s2cMac;
                SessionCrypto.DeriveSessionKeys(MakeSecret(21, 32), MakeSecret(22, 16),
                    out c2sEnc, out c2sMac, out s2cEnc, out s2cMac);

                const int SegLen = 1000;
                const int RecSize = 8 + 4 + SessionCrypto.WireMacBytes + SegLen;
                byte[] raw;
                {
                    var wire = new MemoryWireStream();
                    using (var enc = new EncryptedWireStream(wire, c2sEnc, c2sMac, s2cEnc, s2cMac, true, true))
                    {
                        var a = MakeSecret(23, SegLen);
                        var b = MakeSecret(24, SegLen);
                        enc.WriteExactAsync(a, 0, a.Length, CancellationToken.None).Wait();
                        enc.WriteExactAsync(b, 0, b.Length, CancellationToken.None).Wait();
                    }
                    raw = wire.Buffer.ToArray();
                }
                Assert.Equal((long)(RecSize * 2), (long)raw.Length, "two records on the wire");

                // Swap the two whole records: the reader sees seq 0/1 swapped against
                // ciphertext, and the MAC (which covers seq) no longer matches.
                var swapped = new byte[raw.Length];
                Buffer.BlockCopy(raw, RecSize, swapped, 0, RecSize);
                Buffer.BlockCopy(raw, 0, swapped, RecSize, RecSize);

                var reader = new MemoryWireStream();
                reader.Buffer.Write(swapped, 0, swapped.Length);
                reader.Buffer.Position = 0;
                bool threw = false;
                try
                {
                    using (var dec = new EncryptedWireStream(reader, s2cEnc, s2cMac, c2sEnc, c2sMac, true, true))
                        dec.ReadExactAsync(new byte[SegLen * 2], 0, SegLen * 2, CancellationToken.None).Wait();
                }
                catch (AggregateException) { threw = true; }

                Assert.True(threw, "reordered records must fail authentication");
            });
        }

        // ==================== Unit: transport compression ====================

        private static void RunWireCompress(TestRunner runner)
        {
            runner.Run("WireCompress_Roundtrip_VariedSizes", () =>
            {
                // Writes shaped like the real pipeline: tiny header, big payload,
                // random (stored) payload, odd sizes — read back across segment
                // boundaries with a mix of ReadExact and ReadSome
                var wire = new MemoryWireStream();
                var parts = new System.Collections.Generic.List<byte[]>();
                parts.Add(MakeSecret(101, 13));                    // tiny — stored
                byte[] flat = new byte[1024 * 1024];                // compressible — deflated
                for (int i = 0; i < flat.Length; i++) flat[i] = (byte)(i % 251);
                parts.Add(flat);
                parts.Add(MakeSecret(102, 300 * 1024));            // random — stored
                parts.Add(MakeSecret(103, 1));
                parts.Add(MakeSecret(104, 65537));

                using (var comp = new CompressedWireStream(wire))
                {
                    foreach (var p in parts)
                        comp.WriteExactAsync(p, 0, p.Length, CancellationToken.None).Wait();
                }

                wire.Buffer.Position = 0;
                using (var dec = new CompressedWireStream(wire))
                {
                    // Read part 0 exactly, part 1 via ReadSome chunks, rest exactly
                    var buf0 = new byte[parts[0].Length];
                    dec.ReadExactAsync(buf0, 0, buf0.Length, CancellationToken.None).Wait();
                    Assert.True(Utils.ConstantTimeEquals(parts[0], buf0), "tiny header segment roundtrips");

                    var buf1 = new byte[parts[1].Length];
                    int got1 = 0;
                    while (got1 < buf1.Length)
                    {
                        int n = dec.ReadSomeAsync(buf1, got1, buf1.Length - got1, CancellationToken.None).Result;
                        if (n <= 0) break;
                        got1 += n;
                    }
                    Assert.Equal(buf1.Length, got1, "compressible payload fully read");
                    Assert.True(Utils.ConstantTimeEquals(parts[1], buf1), "compressible payload roundtrips");

                    for (int i = 2; i < parts.Count; i++)
                    {
                        var buf = new byte[parts[i].Length];
                        dec.ReadExactAsync(buf, 0, buf.Length, CancellationToken.None).Wait();
                        Assert.True(Utils.ConstantTimeEquals(parts[i], buf), "part " + i + " roundtrips");
                    }
                }
            });

            runner.Run("WireCompress_ShrinksCompressible_StoresRandom", () =>
            {
                var flat = new byte[1024 * 1024];
                for (int i = 0; i < flat.Length; i++) flat[i] = (byte)'A';
                var wire = new MemoryWireStream();
                using (var comp = new CompressedWireStream(wire))
                    comp.WriteExactAsync(flat, 0, flat.Length, CancellationToken.None).Wait();
                Assert.True(wire.Buffer.Length < flat.Length / 8, "repetitive data compresses on the wire");

                var rnd = MakeSecret(105, 200 * 1024);
                var wire2 = new MemoryWireStream();
                using (var comp2 = new CompressedWireStream(wire2))
                    comp2.WriteExactAsync(rnd, 0, rnd.Length, CancellationToken.None).Wait();
                // Stored fallback: raw bytes + a 4-byte header, never a blow-up
                Assert.True(wire2.Buffer.Length <= rnd.Length + 4 + 64, "random data goes stored (no expansion)");

                wire2.Buffer.Position = 0;
                var back = new byte[rnd.Length];
                using (var dec2 = new CompressedWireStream(wire2))
                    dec2.ReadExactAsync(back, 0, back.Length, CancellationToken.None).Wait();
                Assert.True(Utils.ConstantTimeEquals(rnd, back), "stored random data roundtrips");
            });

            runner.Run("WireCompress_ProbeGate_SkipsMarginalData", () =>
            {
                // The conservative gate must decline data that only shrinks a little:
                // such data would cost more deflate CPU than the bytes it saves. The
                // segment must still arrive intact (stored), just not compressed.
                // 50% zeros deflated to R~0.65 — above the 0.30 gate.
                var marginal = MakeSecret(106, 512 * 1024);
                for (int i = 0; i + 1 < marginal.Length; i += 2) marginal[i] = 0;

                var wire = new MemoryWireStream();
                using (var comp = new CompressedWireStream(wire))
                    comp.WriteExactAsync(marginal, 0, marginal.Length, CancellationToken.None).Wait();

                var back = new byte[marginal.Length];
                wire.Buffer.Position = 0;
                using (var dec = new CompressedWireStream(wire))
                    dec.ReadExactAsync(back, 0, back.Length, CancellationToken.None).Wait();
                Assert.True(Utils.ConstantTimeEquals(marginal, back), "gated-out segment roundtrips");

                // And it really was sent stored: the wire is the payload plus framing,
                // never a deflate of it (which would be ~0.65x and look "smaller")
                Assert.True(wire.Buffer.Length >= marginal.Length,
                    "marginal data sails through stored (no wasted deflate), got " + wire.Buffer.Length);
            });

            runner.Run("WireCompress_ProbeGate_StillsCompressesWell", () =>
            {
                // The gate must not be so strict that genuinely compressible data loses
                // its benefit — a segment that clears MaxCompressedRatio is deflated.
                var text = new byte[512 * 1024];
                var line = System.Text.Encoding.ASCII.GetBytes(
                    "2026-09-10 INFO transfer ok src=10.0.0.5 dst=10.0.0.9 bytes=1048576\n");
                for (int i = 0; i < text.Length; i++) text[i] = line[i % line.Length];

                var wire = new MemoryWireStream();
                using (var comp = new CompressedWireStream(wire))
                    comp.WriteExactAsync(text, 0, text.Length, CancellationToken.None).Wait();
                Assert.True(wire.Buffer.Length < text.Length / 4,
                    "well-compressible data still shrinks past the gate (got " + wire.Buffer.Length + ")");

                var back = new byte[text.Length];
                wire.Buffer.Position = 0;
                using (var dec = new CompressedWireStream(wire))
                    dec.ReadExactAsync(back, 0, back.Length, CancellationToken.None).Wait();
                Assert.True(Utils.ConstantTimeEquals(text, back), "compressed segment roundtrips");
            });

            runner.Run("WireCompress_CorruptSegment_Throws", () =>
            {
                // A segment length beyond the decompression cap must be refused
                // deterministically (raw-deflate bit flips may or may not produce an
                // invalid Huffman code, so corrupting the LENGTH is the stable probe;
                // end-to-end payload integrity is covered by the protocol SHA256).
                var raw = new byte[4 + 8];
                Buffer.BlockCopy(BitConverter.GetBytes(0x04000001u), 0, raw, 0, 4); // > MaxSegment
                bool threw = false;
                using (var dec = new CompressedWireStream(new MemoryStreamWire(raw)))
                {
                    try
                    {
                        var buf = new byte[4096];
                        dec.ReadSomeAsync(buf, 0, buf.Length, CancellationToken.None).Wait();
                    }
                    catch (AggregateException ex)
                    {
                        threw = ex.InnerException is IOException;
                    }
                }
                Assert.True(threw, "oversized segment length must throw IOException");

                // Zero length is equally invalid
                var raw0 = new byte[4];
                Buffer.BlockCopy(BitConverter.GetBytes(0u), 0, raw0, 0, 4);
                bool threw0 = false;
                using (var dec0 = new CompressedWireStream(new MemoryStreamWire(raw0)))
                {
                    try
                    {
                        var buf = new byte[16];
                        dec0.ReadSomeAsync(buf, 0, buf.Length, CancellationToken.None).Wait();
                    }
                    catch (AggregateException ex)
                    {
                        threw0 = ex.InnerException is IOException;
                    }
                }
                Assert.True(threw0, "zero-length segment must throw IOException");
            });

            runner.Run("WireCompress_LayeredUnderEncryption", () =>
            {
                // The real session stacking: compression wraps encryption, so the
                // wire bytes are encrypt(compress(plain)) — deflate sees plaintext
                var psk = MakeSecret(107, 32);
                var salt = MakeSecret(108, 16);
                byte[] c2sEnc, c2sMac, s2cEnc, s2cMac;
                SessionCrypto.DeriveSessionKeys(psk, salt, out c2sEnc, out c2sMac, out s2cEnc, out s2cMac);

                var wire = new MemoryWireStream();
                byte[] flat = new byte[512 * 1024];
                for (int i = 0; i < flat.Length; i++) flat[i] = (byte)(i % 7);
                using (var enc = new EncryptedWireStream(wire, c2sEnc, c2sMac, s2cEnc, s2cMac))
                using (var comp = new CompressedWireStream(enc))
                    comp.WriteExactAsync(flat, 0, flat.Length, CancellationToken.None).Wait();
                Assert.True(wire.Buffer.Length < flat.Length / 4, "compress-then-encrypt shrinks on the wire");

                wire.Buffer.Position = 0;
                using (var enc = new EncryptedWireStream(wire, s2cEnc, s2cMac, c2sEnc, c2sMac))
                using (var dec = new CompressedWireStream(enc))
                {
                    var back = new byte[flat.Length];
                    dec.ReadExactAsync(back, 0, back.Length, CancellationToken.None).Wait();
                    Assert.True(Utils.ConstantTimeEquals(flat, back), "compress+encrypt roundtrip intact");
                }
            });
        }

        /// <summary>Read-only in-memory stream seeded from a byte array. Records the read
        /// timeouts the protocol asks for, so a test can see the widen/restore around a wait.</summary>
        private class MemoryStreamWire : IWireStream
        {
            private readonly MemoryStream _ms;
            public readonly List<int> ReadTimeouts = new List<int>();
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
            public void SetReadTimeoutMs(int milliseconds) { ReadTimeouts.Add(milliseconds); }
            public void Dispose() { _ms.Dispose(); }
        }

        /// <summary>Accepts and discards writes, serves a canned response, and records the
        /// read timeouts the protocol asks for.</summary>
        private class ScriptedWire : IWireStream
        {
            private readonly MemoryStream _in;
            public readonly List<int> ReadTimeouts = new List<int>();
            public ScriptedWire(byte[] response) { _in = new MemoryStream(response, false); }
            public Task WriteExactAsync(byte[] b, int o, int c, CancellationToken ct) { return Task.FromResult(0); }
            public Task ReadExactAsync(byte[] b, int o, int c, CancellationToken ct)
            {
                if (c == 0) return Task.FromResult(0);
                int read = _in.Read(b, o, c);
                if (read < c) throw new IOException("exhausted");
                return Task.FromResult(0);
            }
            public Task<int> ReadSomeAsync(byte[] b, int o, int c, CancellationToken ct)
            {
                return Task.FromResult(_in.Read(b, o, c));
            }
            public void SetReadTimeoutMs(int milliseconds) { ReadTimeouts.Add(milliseconds); }
            public void Dispose() { _in.Dispose(); }
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
            runner.Run("DiskSpace_Overflow_Rejected", () =>
            {
                // A declared size near long.MaxValue must not wrap size+margin into a
                // negative requirement that trivially passes the check.
                Assert.False(Utils.HasFreeSpaceFor(TempBase(), long.MaxValue), "max declared size rejected");
                Assert.False(Utils.HasFreeSpaceFor(TempBase(), Utils.MaxTransferSize + 1), "over the cap rejected");
                Assert.False(Utils.HasFreeSpace(TempBase(), -1), "negative requirement rejected");
                Assert.True(Utils.HasFreeSpaceFor(TempBase(), 1024), "sane size still passes");
            });
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

                runner.Run("StatsStore_DetailFieldsRoundtrip", () =>
                {
                    string path = Path.Combine(dir, "stats_detail.log");
                    var store = new StatsStore(path);
                    store.Append(new StatsEntry
                    {
                        When = DateTime.Now,
                        Direction = 'R',
                        Peer = "192.168.1.9",
                        Bytes = 4242,
                        Files = 2,
                        Seconds = 3.0,
                        Detail = "photo.zip",
                        Path = @"C:\recv\photo.zip"
                    });

                    var entries = new StatsStore(path).LoadAll();
                    Assert.Equal(1, entries.Count, "one line");
                    Assert.Equal("photo.zip", entries[0].Detail, "detail survives the log");
                    Assert.Equal(@"C:\recv\photo.zip", entries[0].Path, "path survives the log");
                });

                runner.Run("StatsStore_LegacyAndModernLines", () =>
                {
                    var legacy = StatsEntry.Parse("2026-09-07 10:00:00|S|10.0.0.1|123|1|2.5");
                    Assert.NotNull(legacy, "legacy 6-field line still parses");
                    Assert.True(string.IsNullOrEmpty(legacy.Detail), "legacy detail empty");
                    Assert.True(string.IsNullOrEmpty(legacy.Path), "legacy path empty");

                    var modern = StatsEntry.Parse("2026-09-07 10:00:00|S|10.0.0.1|123|1|2.5|report.pdf|C:\\docs\\report.pdf");
                    Assert.NotNull(modern, "8-field line parses");
                    Assert.Equal("report.pdf", modern.Detail, "modern detail");
                    Assert.Equal("C:\\docs\\report.pdf", modern.Path, "modern path");

                    Assert.True(StatsEntry.Parse("2026-09-07 10:00:00|S|10.0.0.1|1|1|1|only-detail") != null,
                        "7-field line (empty path) parses");
                });

                runner.Run("StatsStore_ClearAll", () =>
                {
                    string path = Path.Combine(dir, "stats_clear.log");
                    var store = new StatsStore(path);
                    DateTime now = DateTime.Now;
                    store.Append(new StatsEntry { When = now, Direction = 'S', Peer = "10.0.0.1", Bytes = 1000, Files = 1, Seconds = 1 });
                    store.Append(new StatsEntry { When = now, Direction = 'R', Peer = "10.0.0.2", Bytes = 500, Files = 1, Seconds = 1 });
                    Assert.Equal(2, store.LoadAll().Count, "two entries before clear");

                    store.ClearAll();
                    Assert.Equal(0, store.LoadAll().Count, "log empty after clear");
                    Assert.True(File.Exists(path), "log file kept in place");

                    // The same instance keeps accepting new records after a clear
                    store.Append(new StatsEntry { When = now, Direction = 'S', Peer = "10.0.0.3", Bytes = 7, Files = 1, Seconds = 0.1 });
                    var after = store.LoadAll();
                    Assert.Equal(1, after.Count, "append after clear works");
                    Assert.Equal("10.0.0.3", after[0].Peer, "new entry intact");

                    // A fresh instance (dialog pattern) sees the cleared state too
                    Assert.Equal(1, new StatsStore(path).LoadAll().Count, "fresh instance agrees");
                });
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>The receive-side limiter shares one bucket across concurrent callers
        /// (thread-safe accounting), unlike per-connection buckets.</summary>
        private static void RunSpeedLimiter(TestRunner runner)
        {
            runner.Run("SpeedLimiter_ZeroIsUnlimited", () =>
            {
                var limiter = new SpeedLimiter(0);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                limiter.ThrottleAsync(10 * 1024 * 1024, CancellationToken.None).Wait();
                Assert.True(sw.ElapsedMilliseconds < 500, "zero limit never delays");
            });

            runner.Run("SpeedLimiter_SharedBucketPacesTotal", () =>
            {
                var limiter = new SpeedLimiter(100 * 1024); // 100 KB/s
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // Two concurrent senders, 100 KB each: 200 KB through one 100 KB/s
                // bucket must take ~2 s regardless of interleaving
                var t1 = limiter.ThrottleAsync(50 * 1024, CancellationToken.None);
                var t2 = limiter.ThrottleAsync(50 * 1024, CancellationToken.None);
                var t3 = limiter.ThrottleAsync(50 * 1024, CancellationToken.None);
                var t4 = limiter.ThrottleAsync(50 * 1024, CancellationToken.None);
                Task.WaitAll(new[] { t1, t2, t3, t4 });
                sw.Stop();
                Assert.True(sw.ElapsedMilliseconds >= 1500,
                    "shared bucket paces the TOTAL (took " + sw.ElapsedMilliseconds + "ms)");
                Assert.True(sw.ElapsedMilliseconds < 10000,
                    "throttling is not excessive (took " + sw.ElapsedMilliseconds + "ms)");
            });
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
                Assert.Equal("stats.bin", seen.Detail, "transfer name recorded for the history");
                Assert.True(seen.Path != null && seen.Path.EndsWith("stats.bin"),
                    "first save path recorded for the history");
            }
        }

        /// <summary>Receive-side shaping: one server-wide bucket slows the incoming
        /// transfer to roughly the configured rate (600 KB at 200 KB/s ≈ 3 s).</summary>
        private static void TcpRecvSpeedLimit()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.ReceiveSpeedLimit = 200 * 1024; // 200 KB/s
                fx.Start();

                string testFile = Path.Combine(fx.SendDir, "limited.bin");
                MakeTestFile(testFile, 600 * 1024);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                SendAndWait(fx, testFile); // SendAndWait verifies the SHA256 as usual
                sw.Stop();

                Assert.True(sw.ElapsedMilliseconds >= 2200,
                    "receive limit throttled the transfer (took " + sw.ElapsedMilliseconds + "ms)");
                Assert.True(sw.ElapsedMilliseconds < 30000,
                    "throttling is not excessive (took " + sw.ElapsedMilliseconds + "ms)");

                var files = WaitForFileCount(fx, 1);
                Assert.Equal(1, files.Length, "exactly one file saved");
            }
        }

        /// <summary>The pause/resume mechanic behind the UI button: cancel a throttled
        /// send mid-flight, then re-run the SAME session — the server checkpoint makes
        /// the second pass finish the file byte-for-byte.</summary>
        private static void TcpPauseResume()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Start();

                string testFile = Path.Combine(fx.SendDir, "pause.bin");
                byte[] content = MakeTestFile(testFile, 8 * 1024 * 1024); // 8 MB at 2 MB/s ≈ 4 s
                var session = Guid.NewGuid();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile, 0, 4194304, 2 * 1024 * 1024);
                var stopped = new ManualResetEvent(false);
                client.OnStopped += () => stopped.Set();

                var sendTask = client.SendResumableAsync(session, false);
                if (!stopped.WaitOne(1500))
                {
                    // Mid-transfer: this is the "pause button"
                    client.Cancel();
                    if (!stopped.WaitOne(10000))
                        throw new Exception("client did not stop after cancel");
                }
                Assert.True(client.WasCancelled, "cancel registered as a deliberate pause");
                try { sendTask.Wait(15000); } catch { }
                Thread.Sleep(600); // let the server persist the interrupted checkpoint

                // Resume on the same session, unthrottled
                var client2 = new TransferClient("127.0.0.1", fx.Port, testFile);
                var done2 = new ManualResetEvent(false);
                bool ok2 = false;
                string error2 = null;
                client2.OnTransferComplete += () => { ok2 = true; done2.Set(); };
                client2.OnError += msg => { error2 = msg; done2.Set(); };
                var resumeTask = client2.SendResumableAsync(session, false);
                if (!done2.WaitOne(30000))
                    throw new Exception("resume did not finish within 30s");
                if (!ok2)
                    throw new Exception("resume failed: " + (error2 ?? "unknown"));
                try
                {
                    if (resumeTask.Exception != null)
                        throw new Exception("resume task faulted: " + resumeTask.Exception.InnerException.Message);
                }
                catch (AggregateException) { }

                Thread.Sleep(300);
                var files = Directory.GetFiles(fx.RecvDir, "*", SearchOption.AllDirectories);
                Assert.Equal(1, files.Length, "exactly one file after resume");
                byte[] saved = File.ReadAllBytes(files[0]);
                Assert.Equal(content.Length, saved.Length, "resumed file is complete");
                Assert.True(Utils.ConstantTimeEquals(content, saved), "resumed content matches the source");
            }
        }

        // ==================== 2.11: transport compression (0x08) ====================

        private static void TcpCompressed()
        {
            using (var fx = new TcpServerFixture())
            {
                string testFile = Path.Combine(fx.SendDir, "comp.bin");
                // Compressible payload so the deflate path is genuinely exercised
                var content = new byte[1200 * 1024];
                var rng = new Random(77);
                for (int i = 0; i < content.Length; i += 4096)
                {
                    byte b = (byte)rng.Next(256);
                    int end = Math.Min(i + 4096, content.Length);
                    for (int j = i; j < end; j++) content[j] = b;
                }
                File.WriteAllBytes(testFile, content);

                var logs = new List<string>();
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                fx.Server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                fx.Server.OnError += _ => serverDone.Set();
                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                client.CompressionEnabled = true;
                client.EncryptionEnabled = false;
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

                bool sawOn = false;
                foreach (var m in logs)
                    if (m.Contains("compression enabled")) sawOn = true;
                Assert.True(sawOn, "client log reports compression");

                string[] received = WaitForFileCount(fx, 1);
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(received[0])),
                    "compressed payload intact");
            }
        }

        private static void TcpCompressEncrypted()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Server.PairingCode = "246810";
                string testFile = Path.Combine(fx.SendDir, "ce.bin");
                var content = new byte[900 * 1024];
                for (int i = 0; i < content.Length; i++) content[i] = (byte)(i % 13);
                File.WriteAllBytes(testFile, content);

                var logs = new List<string>();
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                fx.Server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                fx.Server.OnError += _ => serverDone.Set();
                fx.Start();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile);
                client.PairingCode = "246810";
                client.EncryptionEnabled = true;
                client.CompressionEnabled = true;
                client.OnLog += msg => logs.Add(msg);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += _ => clientDone.Set();

                var sendTask = client.SendAsync();
                if (!serverDone.WaitOne(30000)) throw new Exception("server timeout");
                if (!clientDone.WaitOne(5000)) throw new Exception("client timeout");
                Assert.True(serverOk && clientOk, "both sides completed");
                if (sendTask.Exception != null)
                    throw sendTask.Exception.InnerException ?? sendTask.Exception;

                bool sawEnc = false, sawComp = false;
                foreach (var m in logs)
                {
                    if (m.Contains("Encrypted session")) sawEnc = true;
                    if (m.Contains("compression enabled")) sawComp = true;
                }
                Assert.True(sawEnc, "encryption active");
                Assert.True(sawComp, "compression active");

                string[] received = WaitForFileCount(fx, 1);
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(received[0])),
                    "encrypted+compressed payload intact");
            }
        }

        /// <summary>0x03 resume riding the compression decorator: offsets count
        /// uncompressed logical bytes, so a cancelled-then-resumed session must
        /// still finish byte-for-byte.</summary>
        private static void TcpCompressResume()
        {
            using (var fx = new TcpServerFixture())
            {
                fx.Start();

                string testFile = Path.Combine(fx.SendDir, "cres.bin");
                byte[] content = new byte[6 * 1024 * 1024];
                for (int i = 0; i < content.Length; i++) content[i] = (byte)(i % 11);
                File.WriteAllBytes(testFile, content);
                var session = Guid.NewGuid();

                var client = new TransferClient("127.0.0.1", fx.Port, testFile, 0, 4194304, 2 * 1024 * 1024);
                client.CompressionEnabled = true;
                client.EncryptionEnabled = false;
                var stopped = new ManualResetEvent(false);
                client.OnStopped += () => stopped.Set();
                var sendTask = client.SendResumableAsync(session, false);
                if (!stopped.WaitOne(1500))
                {
                    client.Cancel();
                    if (!stopped.WaitOne(10000))
                        throw new Exception("client did not stop after cancel");
                }
                try { sendTask.Wait(15000); } catch { }
                Thread.Sleep(600);

                var client2 = new TransferClient("127.0.0.1", fx.Port, testFile);
                client2.CompressionEnabled = true;
                client2.EncryptionEnabled = false;
                var done2 = new ManualResetEvent(false);
                bool ok2 = false;
                client2.OnTransferComplete += () => { ok2 = true; done2.Set(); };
                client2.OnError += _ => done2.Set();
                var resumeTask = client2.SendResumableAsync(session, false);
                if (!done2.WaitOne(30000))
                    throw new Exception("resume did not finish within 30s");
                if (!ok2)
                    throw new Exception("resume failed");
                if (resumeTask.Exception != null)
                    throw new Exception("resume task faulted: " + resumeTask.Exception.InnerException.Message);

                Thread.Sleep(300);
                var files = Directory.GetFiles(fx.RecvDir, "*", SearchOption.AllDirectories);
                Assert.Equal(1, files.Length, "exactly one file after compressed resume");
                byte[] saved = File.ReadAllBytes(files[0]);
                Assert.True(Utils.ConstantTimeEquals(content, saved), "resumed compressed content matches");
            }
        }

        /// <summary>Old-peer fallback: a server that never answers the 0x08 offer
        /// drops the connection; the client must reconnect uncompressed and still
        /// deliver the file. The scripted peer reads the offer on connection #1 and
        /// drops; connection #2 carries NO 0x08 frame at all (the client turned the
        /// offer off) — it just drains the plain 0x00 transfer.</summary>
        private static void TcpCompressFallbackOldPeer()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_cf_s_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            try
            {
                string testFile = Path.Combine(sendDir, "fb.bin");
                byte[] content = MakeTestFile(testFile, 200 * 1024);

                var transferDrained = new ManualResetEvent(false);
                var offerSeen = new ManualResetEvent(false);
                System.Threading.Tasks.Task.Run(delegate
                {
                    // Connection #1: the 0x08 offer arrives first; an old peer would
                    // read it as a corrupt header and drop without answering
                    var s1 = listener.AcceptTcpClient();
                    using (s1)
                    using (var ns1 = s1.GetStream())
                    {
                        var offer = new byte[17]; // 0x08 + 16 pad
                        ReadFully(ns1, offer);
                        if (offer[0] == 0x08) offerSeen.Set();
                        // no 0x18 answer — close instead
                    }

                    // Connection #2: fallback path — no offer, straight to the
                    // plain 0x00 transfer; drain it to completion
                    var s2 = listener.AcceptTcpClient();
                    using (s2)
                    using (var ns2 = s2.GetStream())
                    {
                        var first = new byte[1];
                        ReadFully(ns2, first);
                        if (first[0] == 0x00) // plain single-file transfer header
                        {
                            var buf = new byte[65536];
                            try
                            {
                                int n;
                                while ((n = ns2.Read(buf, 0, buf.Length)) > 0) { }
                            }
                            catch (System.Net.Sockets.SocketException) { }
                            catch (IOException) { }
                            transferDrained.Set();
                        }
                    }
                });

                var client = new TransferClient("127.0.0.1", port, testFile);
                client.CompressionEnabled = true;
                client.EncryptionEnabled = false;
                var logs = new List<string>();
                client.OnLog += msg => logs.Add(msg);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += _ => clientDone.Set();

                var sendTask = client.SendAsync();
                if (!clientDone.WaitOne(30000))
                    throw new Exception("client did not finish within 30s");
                Assert.True(clientOk, "client completed after fallback");
                if (sendTask.Exception != null)
                    throw sendTask.Exception.InnerException ?? sendTask.Exception;

                Assert.True(offerSeen.WaitOne(5000), "peer saw the 0x08 offer on the first attempt");
                bool sawFallback = false;
                foreach (var m in logs)
                    if (m.Contains("falling back to uncompressed")) sawFallback = true;
                Assert.True(sawFallback, "client logged the compression fallback");
                Assert.True(transferDrained.WaitOne(10000), "second connection carried the plain transfer");
            }
            finally
            {
                listener.Stop();
                try { Directory.Delete(sendDir, true); } catch { }
            }
        }

        private static void ReadFully(System.Net.Sockets.NetworkStream ns, byte[] buf)
        {
            int got = 0;
            while (got < buf.Length)
            {
                int n = ns.Read(buf, got, buf.Length - got);
                if (n <= 0) throw new IOException("fake peer connection closed early");
                got += n;
            }
        }

        private static void UdtCompressed()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(TempBase(), "tr_f_uc_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(TempBase(), "tr_f_uc_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferUdtServer server = null;
            try
            {
                string testFile = Path.Combine(sendDir, "udt_comp.bin");
                var content = new byte[600 * 1024];
                for (int i = 0; i < content.Length; i++) content[i] = (byte)(i % 17);
                File.WriteAllBytes(testFile, content);

                server = new TransferUdtServer("127.0.0.1", port, recvDir);
                var started = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                server.OnStarted += () => started.Set();
                server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                server.OnError += _ => serverDone.Set();
                server.Start();
                if (!started.WaitOne(5000)) throw new Exception("UDT server did not start");

                var client = new TransferUdtClient("127.0.0.1", port, testFile);
                client.CompressionEnabled = true;
                client.EncryptionEnabled = false;
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
                Assert.True(Utils.ConstantTimeEquals(content, File.ReadAllBytes(received[0])),
                    "UDT compressed payload intact");
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
